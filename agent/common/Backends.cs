using System.Text.Json.Nodes;
using Blazma.Agent;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Rigging;

namespace Blazma.AgentTools;

/// <summary>Where agent RPC calls go: a running app (attach) or an in-process project (headless).</summary>
public interface IAgentBackend : IAsyncDisposable
{
    bool IsHeadless { get; }

    /// <summary>Runs one agent RPC method and returns its result; throws <see cref="AgentRpcException"/> on error.</summary>
    Task<JsonNode?> CallAsync(string method, JsonObject? parameters, CancellationToken ct);
}

public sealed class AttachBackend(AgentIpcClient client) : IAgentBackend
{
    public bool IsHeadless => false;

    public static async Task<AttachBackend?> TryConnectAsync(TimeSpan timeout, string? pipeName, CancellationToken ct = default)
    {
        var c = await AgentIpcClient.TryConnectAsync(timeout, pipeName, ct).ConfigureAwait(false);
        return c is null ? null : new AttachBackend(c);
    }

    public Task<JsonNode?> CallAsync(string method, JsonObject? parameters, CancellationToken ct) =>
        client.CallAsync(method, parameters, ct);

    public ValueTask DisposeAsync() => client.DisposeAsync();
}

/// <summary>
/// No app: an in-process project driven through the same <see cref="AgentRpcHandler"/> the app uses.
/// All calls run under one lock, so the (non thread-safe) session sees one caller at a time.
/// Adds the project lifecycle methods project.new / project.open / project.close.
/// </summary>
public sealed class HeadlessBackend : IAgentBackend, IAgentHostServices
{
    readonly CommandRegistry _registry = CommandRegistry.CreateDefault();
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly ProjectSession _session = new(Project.CreateNew("(no project)"));
    readonly AgentHost _host;
    readonly AgentRpcHandler _rpc;
    SessionLock? _lock;
    bool _hasProject;
    long _nextId;

    public HeadlessBackend()
    {
        _host = new AgentHost(_registry, () => _session, userPresent: false);
        _host.ProjectFolder = () => _session.FilePath is { } p ? Path.GetDirectoryName(Path.GetFullPath(p)) : null;
        // Headless agents may read: the project folder, the folder the tool was started in, the write
        // folders, and folders the user listed (--allow-read DIR, or BLAZMA_ALLOW_READ separated by the OS
        // path separator). Read folders never grant write.
        // They may create NEW files (exports) only in: the project folder, the folder the tool was started
        // in (the person who launched the headless tool chose it, and headless project_save/project_new
        // already create new .blazanim files there), and folders listed with --allow-write DIR or
        // BLAZMA_ALLOW_WRITE. A start folder that is a filesystem root (e.g. "/" when an MCP client starts
        // the server there) is not made writable.
        var cwd = Directory.GetCurrentDirectory();
        _host.AllowedReadRoots.Add(cwd);
        if (!IsFilesystemRoot(cwd)) _host.AllowedWriteRoots.Add(cwd);
        foreach (var dir in EnvList("BLAZMA_ALLOW_READ")) AllowRead(dir);
        foreach (var dir in EnvList("BLAZMA_ALLOW_WRITE")) AllowWrite(dir);
        _rpc = new AgentRpcHandler(_host, _registry, this);
        // Headless calls run one at a time anyway, so motion.job_status may wait for a generation job to finish.
        if (_registry.Motion is { } motion) motion.AllowBlockingWait = true;
    }

    static IEnumerable<string> EnvList(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
            ? v.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

    static bool IsFilesystemRoot(string dir)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        return Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? "") == full;
    }

    /// <summary>Folders agents may create new files in (read-only access is <see cref="AllowedReadRoots"/>).</summary>
    public IReadOnlyList<string> AllowedWriteRoots => _host.AllowedWriteRoots;
    public IReadOnlyList<string> AllowedReadRoots => _host.AllowedReadRoots;

    public bool IsHeadless => true;
    public bool HasProject => _hasProject;

    /// <summary>Lets agents import files from <paramref name="dir"/> (validated: no network/device paths).</summary>
    public void AllowRead(string dir) => _host.AllowedReadRoots.Add(AgentPaths.Normalize(dir));

    /// <summary>Lets agents create new files (exports) in <paramref name="dir"/> (validated: no network/device paths).</summary>
    public void AllowWrite(string dir) => _host.AllowedWriteRoots.Add(AgentPaths.Normalize(dir));
    public CommandRegistry Registry => _registry;

    // IAgentHostServices
    public string ProjectName => _hasProject ? _session.Project.Name : "";
    public string? ProjectPath => _hasProject ? _session.FilePath : null;
    public bool IsDirty => _hasProject && _session.IsDirty;

    public string Save(string? path)
    {
        if (!_hasProject) throw NoProject();
        var target = path ?? _session.FilePath ?? throw new AgentRpcException("no_path", "The project has not been saved yet; pass 'path'.");
        // Only the open project's file or a NEW .blazanim file: never overwrite anything else.
        target = AgentPaths.ValidateSaveTarget(target, _session.FilePath);
        if (_session.FilePath is null || Path.GetFullPath(_session.FilePath) != target)
        {
            // Save as: move the lock to the new file.
            var newLock = SessionLock.TryAcquire(target)
                ?? throw new AgentRpcException("project_locked", $"'{target}' is open in another Blazma process.");
            _lock?.Dispose();
            _lock = newLock;
        }
        ProjectFile.Save(_session.Project, target);
        _session.MarkSaved(target);
        return target;
    }

    static AgentRpcException NoProject() =>
        new("no_project", "No project is open. Call project_new or project_open first.");

    public async Task<JsonNode?> CallAsync(string method, JsonObject? parameters, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            parameters ??= new JsonObject();
            switch (method)
            {
                case "project.new": return NewProject(parameters);
                case "project.open": return OpenProject(parameters);
                case "project.close": return CloseProject(parameters);
                case "tools.call" or "project.save" or "edit.undo" when !_hasProject:
                    throw NoProject();
            }
            JsonObject response;
            try
            {
                response = _rpc.Handle(new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = ++_nextId, ["method"] = method, ["params"] = parameters,
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new AgentRpcException("io_error", e.Message);
            }
            catch (ProjectFormatException e) { throw new AgentRpcException(e.Code, e.Message); }
            if (response["error"] is JsonObject err)
                throw new AgentRpcException((string?)err["data"]?["code"] ?? "error", (string?)err["message"] ?? "Error");
            return response["result"]?.DeepClone();
        }
        finally { _gate.Release(); }
    }

    JsonObject NewProject(JsonObject p)
    {
        var path = AgentPaths.Normalize(Req(p, "path"));
        if (!path.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)) path += ProjectFile.Extension;
        var name = (string?)p["name"] is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(path);
        if (File.Exists(path)) throw new AgentRpcException("file_exists", $"'{path}' already exists. Use project_open, or pick a new path.");
        EnsureCanReplace(p);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var @lock = SessionLock.TryAcquire(path)
            ?? throw new AgentRpcException("project_locked", $"'{path}' is open in another Blazma process.");
        try
        {
            var project = Project.CreateNew(name);
            MannequinFactory.AddTo(project);
            ProjectFile.Save(project, path);
            Swap(project, path, @lock);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            @lock.Dispose();
            throw new AgentRpcException("io_error", e.Message);
        }
        catch { @lock.Dispose(); throw; }
        return Describe();
    }

    JsonObject OpenProject(JsonObject p)
    {
        var path = AgentPaths.Normalize(Req(p, "path"));
        if (!File.Exists(path)) throw new AgentRpcException("not_found", $"File not found: {path}");
        EnsureCanReplace(p);
        if (_lock is not null && _session.FilePath is { } cur && Path.GetFullPath(cur) == path)
            throw new AgentRpcException("already_open", "That project is already open.");
        var @lock = SessionLock.TryAcquire(path)
            ?? throw new AgentRpcException("project_locked", $"'{path}' is open in another Blazma process (the app or another agent).");
        try
        {
            Project project;
            try { project = ProjectFile.Load(path); }
            catch (ProjectFormatException e) { throw new AgentRpcException(e.Code, e.Message); }
            Swap(project, path, @lock);
        }
        catch { @lock.Dispose(); throw; }
        return Describe();
    }

    JsonObject CloseProject(JsonObject p)
    {
        if (!_hasProject) return new JsonObject { ["closed"] = false };
        EnsureCanReplace(p);
        _lock?.Dispose();
        _lock = null;
        _hasProject = false;
        _session.ReplaceProject(Project.CreateNew("(no project)"), null);
        return new JsonObject { ["closed"] = true };
    }

    void EnsureCanReplace(JsonObject p)
    {
        if (_hasProject && _session.IsDirty && p["discard"]?.GetValue<bool>() != true)
            throw new AgentRpcException("unsaved_changes", "The open project has unsaved changes. Call project_save first, or pass discard=true.");
    }

    void Swap(Project project, string path, SessionLock newLock)
    {
        var old = _lock;
        _session.ReplaceProject(project, path);
        _lock = newLock;
        _hasProject = true;
        old?.Dispose();
    }

    JsonObject Describe() => new()
    {
        ["name"] = _session.Project.Name,
        ["path"] = _session.FilePath,
        ["characters"] = new JsonArray(_session.Project.Characters.Select(c => (JsonNode?)new JsonObject
        {
            ["characterId"] = c.Id.ToString(), ["name"] = c.Name,
        }).ToArray()),
    };

    static string Req(JsonObject p, string key) =>
        (string?)p[key] is { Length: > 0 } s ? s : throw new AgentRpcException("invalid_args", $"Missing '{key}'.");

    public ValueTask DisposeAsync()
    {
        _lock?.Dispose();
        _lock = null;
        _registry.Motion?.Dispose(); // cancels running generation jobs and stops kimodo-runner
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
