using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Ids;

namespace Blazma.Agent;

/// <summary>Assisted: every task waits for the user's Approve/Reject. Autonomous: tasks are accepted when finished.</summary>
public enum AgentMode { Assisted, Autonomous }

public enum AgentTaskState { Running, Review, Approved, Rejected }

public sealed record AgentCall(DateTimeOffset Time, string Tool, string ArgsSummary, bool Ok, string? ErrorCode, string? Message,
    IReadOnlyList<string> Warnings);

/// <summary>One unit of agent work. Its changes are applied immediately (so they can be previewed and
/// played back) but stay revertible as a whole until approved.</summary>
public sealed class AgentTask
{
    internal AgentTask(string id, string sessionId, string title)
    {
        Id = id;
        SessionId = sessionId;
        Title = title;
    }

    public string Id { get; }
    public string SessionId { get; }
    public string Title { get; }
    public string? Summary { get; internal set; }
    public AgentTaskState State { get; internal set; } = AgentTaskState.Running;
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

    /// <summary>Undo-stack transactions created by this task, oldest first.</summary>
    public List<string> Transactions { get; } = new();
    public List<AgentCall> Calls { get; } = new();
    public bool HasChanges => Transactions.Count > 0;
}

public sealed record AgentSessionInfo(string Id, string ClientName, DateTimeOffset Connected);

public sealed record AgentPendingApproval(string Id, string SessionId, string Tool, JsonObject Args, string Reason);

/// <summary>
/// The one place where agents (MCP or CLI) edit a project. Wraps <see cref="ProjectSession"/> with:
/// <list type="bullet">
/// <item>sessions (one per connected client) and tasks with Approve/Reject,</item>
/// <item>permissions: Destructive commands need the user's approval when a user is present;
/// commands that read or write files outside the project (External, or a declared <see cref="CommandDescriptor.FileArg"/>)
/// may only use allowed folders, and agents only ever create new files,</item>
/// <item>an activity log for the AI Workspace panel.</item>
/// </list>
/// Not thread-safe: call it on the thread that owns the session (the app marshals IPC calls to the
/// Godot main thread).
/// </summary>
public sealed class AgentHost
{
    readonly CommandRegistry _registry;
    readonly Func<ProjectSession> _session;
    readonly Dictionary<string, AgentSessionInfo> _sessions = new();
    readonly List<AgentTask> _tasks = new();
    readonly List<AgentPendingApproval> _pending = new();

    /// <param name="session">Current session (the app may replace the project, so it is a function).</param>
    /// <param name="userPresent">True inside the app: risky calls wait for the user. False for headless MCP/CLI,
    /// where the person running the agent is the one who asked for the work.</param>
    public AgentHost(CommandRegistry registry, Func<ProjectSession> session, bool userPresent)
    {
        _registry = registry;
        _session = session;
        UserPresent = userPresent;
    }

    public bool UserPresent { get; }
    public AgentMode Mode { get; set; } = AgentMode.Assisted;

    /// <summary>Folders agents may import files from and export new files to (besides the project's own folder).</summary>
    public List<string> AllowedReadRoots { get; } = new();

    /// <summary>Folder of the open project file, if any (always readable).</summary>
    public Func<string?> ProjectFolder { get; set; } = () => null;

    public IReadOnlyCollection<AgentSessionInfo> Sessions => _sessions.Values;
    public IReadOnlyList<AgentTask> Tasks => _tasks;
    public IReadOnlyList<AgentPendingApproval> PendingApprovals => _pending;

    /// <summary>Raised after anything the AI Workspace shows has changed.</summary>
    public event Action? Changed;

    public AgentSessionInfo Connect(string clientName)
    {
        var info = new AgentSessionInfo(ObjectId.New().ToString(), string.IsNullOrWhiteSpace(clientName) ? "agent" : clientName.Trim(), DateTimeOffset.UtcNow);
        _sessions[info.Id] = info;
        Changed?.Invoke();
        return info;
    }

    public void Disconnect(string sessionId)
    {
        _sessions.Remove(sessionId);
        _pending.RemoveAll(p => p.SessionId == sessionId);
        if (RunningTask(sessionId) is { } t) Finish(t, "Agent disconnected.");
        Changed?.Invoke();
    }

    public AgentTask? RunningTask(string sessionId) =>
        _tasks.LastOrDefault(t => t.SessionId == sessionId && t.State == AgentTaskState.Running);

    public AgentTask BeginTask(string sessionId, string title)
    {
        RequireSession(sessionId);
        if (RunningTask(sessionId) is { } running) Finish(running, null);
        var task = new AgentTask(ObjectId.New().ToString(), sessionId, string.IsNullOrWhiteSpace(title) ? "Agent task" : title.Trim());
        _tasks.Add(task);
        Changed?.Invoke();
        return task;
    }

    /// <summary>Ends the running task: Review in Assisted mode (if it changed anything), else Approved.</summary>
    public AgentTask? FinishTask(string sessionId, string? summary)
    {
        RequireSession(sessionId);
        var task = RunningTask(sessionId);
        if (task is null) return null;
        Finish(task, summary);
        Changed?.Invoke();
        return task;
    }

    void Finish(AgentTask task, string? summary)
    {
        task.Summary = summary ?? task.Summary;
        task.State = Mode == AgentMode.Assisted && UserPresent && task.HasChanges ? AgentTaskState.Review : AgentTaskState.Approved;
    }

    /// <summary>Execute a registry command for an agent.</summary>
    public CommandResult Call(string sessionId, string commandId, JsonObject? args)
    {
        RequireSession(sessionId);
        args ??= new JsonObject();
        if (!_registry.TryGet(commandId, out var descriptor))
            return Record(sessionId, commandId, args, CommandResult.Fail("unknown_command", $"Unknown command '{commandId}'."), track: false);

        // Agents never replace existing files, whatever they ask for.
        if (descriptor.FileArg?.OverwriteArgument is { } ow && args[ow] is not null)
        {
            args = (JsonObject)args.DeepClone();
            args[ow] = false;
        }
        if (CheckPermission(descriptor, args) is { } denial)
        {
            if (denial.Code == "approval_required" && UserPresent)
            {
                var request = new AgentPendingApproval(ObjectId.New().ToString(), sessionId, commandId, (JsonObject)args.DeepClone(), denial.Message);
                _pending.Add(request);
                var r = new CommandResult(false, new JsonObject { ["approvalId"] = request.Id }, Array.Empty<Change>(),
                    Array.Empty<Diagnostic>(), denial, null);
                return Record(sessionId, commandId, args, r, track: false);
            }
            if (denial.Code != "approval_required")
                return Record(sessionId, commandId, args, CommandResult.Fail(denial.Code, denial.Message), track: false);
        }
        return Execute(sessionId, descriptor, args);
    }

    CommandResult Execute(string sessionId, CommandDescriptor descriptor, JsonObject args)
    {
        ICommand command;
        try { command = _registry.Create(descriptor.Id, args); }
        catch (CommandException e) { return Record(sessionId, descriptor.Id, args, CommandResult.Fail(e.Code, e.Message), track: false); }

        var origin = new CommandOrigin(CommandSource.Mcp, sessionId);
        if (descriptor.Effect == CommandEffect.ReadOnly)
            return Record(sessionId, descriptor.Id, args, _session().Execute(command, origin), track: false);

        var task = RunningTask(sessionId) ?? BeginTask(sessionId, "Agent task");
        var result = _session().Execute(command, origin);
        if (result.Ok && result.TransactionId is { } tx) task.Transactions.Add(tx);
        return Record(sessionId, descriptor.Id, args, result);
    }

    /// <summary>User approved a held Destructive/External call: run it now for the agent.</summary>
    public CommandResult ApprovePending(string approvalId)
    {
        var p = _pending.Find(x => x.Id == approvalId) ?? throw new KeyNotFoundException($"No pending approval '{approvalId}'.");
        _pending.Remove(p);
        if (!_registry.TryGet(p.Tool, out var d) || !_sessions.ContainsKey(p.SessionId))
            return CommandResult.Fail("expired", "The agent or command is no longer available.");
        var r = Execute(p.SessionId, d, p.Args);
        Changed?.Invoke();
        return r;
    }

    public void DenyPending(string approvalId)
    {
        var p = _pending.Find(x => x.Id == approvalId);
        if (p is null) return;
        _pending.Remove(p);
        RecordRaw(p.SessionId, p.Tool, Summarize(p.Args), false, "denied", "The user denied this action.", Array.Empty<string>());
    }

    /// <summary>Accept a task's changes (they are already applied).</summary>
    public void Approve(string taskId)
    {
        var t = GetTask(taskId);
        if (t.State is AgentTaskState.Running or AgentTaskState.Review) t.State = AgentTaskState.Approved;
        Changed?.Invoke();
    }

    /// <summary>
    /// Revert all of a task's changes. Only possible while the task's transactions are the most
    /// recent ones on the undo stack: the user's own later edits are never undone silently.
    /// </summary>
    public CommandResult Reject(string taskId)
    {
        var t = GetTask(taskId);
        if (t.State == AgentTaskState.Rejected) return CommandResult.Fail("already_rejected", "This task was already rejected.");
        var session = _session();
        var origin = new CommandOrigin(CommandSource.Mcp, t.SessionId);
        // Only transactions still on the undo stack can be reverted (the user may have undone some already).
        var onStack = session.UndoHistory.Select(h => h.Id).ToHashSet();
        var mine = t.Transactions.Where(onStack.Contains).ToList();
        var top = session.UndoHistory.TakeLast(mine.Count).Select(h => h.Id).ToList();
        if (mine.Count > 0 && !top.SequenceEqual(mine))
            return CommandResult.Fail("edited_after", "There are newer changes after this task. Undo them first, then reject.");
        for (var i = mine.Count - 1; i >= 0; i--)
        {
            var r = session.Undo(origin, mine[i]);
            if (!r.Ok) return r;
        }
        t.State = AgentTaskState.Rejected;
        Changed?.Invoke();
        return new CommandResult(true, new JsonObject { ["reverted"] = mine.Count }, Array.Empty<Change>(), Array.Empty<Diagnostic>(), null, null);
    }

    /// <summary>Agent undo of its own latest change.</summary>
    public CommandResult Undo(string sessionId, string transactionId)
    {
        RequireSession(sessionId);
        var r = _session().Undo(new CommandOrigin(CommandSource.Mcp, sessionId), transactionId);
        if (r.Ok) foreach (var t in _tasks.Where(t => t.SessionId == sessionId)) t.Transactions.Remove(transactionId);
        RecordRaw(sessionId, "edit.undo", transactionId, r.Ok, r.Error?.Code, r.Error?.Message, Array.Empty<string>());
        return r;
    }

    /// <summary>Permission rules (docs/AI_INTEGRATION.md §3). Null = allowed.</summary>
    ErrorInfo? CheckPermission(CommandDescriptor d, JsonObject args)
    {
        if (d.Effect == CommandEffect.Destructive)
            return new ErrorInfo("approval_required", $"'{d.Id}' deletes something; the user must approve it.");
        // Files outside the project: declared by the descriptor, or "path" of an External command (read).
        var file = d.FileArg ?? (d.Effect == CommandEffect.External ? new FileArgument("path") : null);
        if (file is null || args[file.Argument] is null) return null;
        string full;
        try
        {
            full = file.Write
                ? AgentPaths.ValidateNewFileTarget((string?)args[file.Argument], file.Extension, _session().FilePath)
                : AgentPaths.ResolveLinks(AgentPaths.Normalize((string?)args[file.Argument]));
        }
        catch (AgentRpcException e) { return new ErrorInfo(e.Code, e.Message); }
        if (IsUnderAllowedRoot(full)) return null;
        var verb = file.Write ? "writes" : "reads";
        // Only an interactive user in Assisted mode can widen access case by case; headless and
        // Autonomous agents are limited to the allowed folders (docs/AI_INTEGRATION.md §3).
        return UserPresent && Mode == AgentMode.Assisted
            ? new ErrorInfo("approval_required", $"'{d.Id}' {verb} '{full}', outside the allowed folders.")
            : new ErrorInfo("path_not_allowed", $"'{full}' is outside the folders agents may use (the project folder and the allowed folders).");
    }

    public bool IsUnderAllowedRoot(string fullPath)
    {
        var roots = AllowedReadRoots.Concat(ProjectFolder() is { } pf ? new[] { pf } : Array.Empty<string>())
            .Select(r => { try { return AgentPaths.ResolveLinks(Path.GetFullPath(r)); } catch (Exception) { return r; } });
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var root in roots)
        {
            var r = Path.GetFullPath(root);
            if (!r.EndsWith(Path.DirectorySeparatorChar)) r += Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(r, cmp)) return true;
        }
        return false;
    }

    AgentTask GetTask(string id) => _tasks.Find(t => t.Id == id) ?? throw new KeyNotFoundException($"No task '{id}'.");

    public bool HasSession(string id) => _sessions.ContainsKey(id);

    void RequireSession(string id)
    {
        if (!_sessions.ContainsKey(id)) throw new InvalidOperationException($"Unknown agent session '{id}'.");
    }

    CommandResult Record(string sessionId, string tool, JsonObject args, CommandResult r, bool track = true)
    {
        RecordRaw(sessionId, tool, Summarize(args), r.Ok, r.Error?.Code, r.Error?.Message, r.Warnings.Select(w => w.Message).ToList(), track);
        return r;
    }

    void RecordRaw(string sessionId, string tool, string args, bool ok, string? code, string? message, IReadOnlyList<string> warnings, bool track = true)
    {
        var call = new AgentCall(DateTimeOffset.UtcNow, tool, args, ok, code, message, warnings);
        var task = RunningTask(sessionId);
        if (task is null && track) task = BeginTask(sessionId, "Agent task");
        task?.Calls.Add(call);
        if (task is null) ReadOnlyLog.Add(call);
        Changed?.Invoke();
    }

    /// <summary>Record a host-level action (e.g. saving) in the activity log.</summary>
    public void Log(string sessionId, string tool, string args, bool ok, string? code = null, string? message = null)
    {
        RequireSession(sessionId);
        RecordRaw(sessionId, tool, args, ok, code, message, Array.Empty<string>(), track: false);
    }

    /// <summary>
    /// Agent gives up its running (or just finished, not yet approved by the user) task and reverts it,
    /// e.g. a script that failed half way. Same rules as <see cref="Reject"/>.
    /// </summary>
    public CommandResult AbortTask(string sessionId)
    {
        RequireSession(sessionId);
        var task = _tasks.LastOrDefault(t => t.SessionId == sessionId && t.State != AgentTaskState.Rejected);
        if (task is null) return CommandResult.Fail("no_task", "There is no task to abort.");
        if (task.State == AgentTaskState.Running) task.State = AgentTaskState.Review;
        return Reject(task.Id);
    }

    /// <summary>Calls made outside any task (read-only inspection).</summary>
    public List<AgentCall> ReadOnlyLog { get; } = new();

    static string Summarize(JsonObject args)
    {
        var parts = args.Select(kv => $"{kv.Key}={Short(kv.Value)}");
        var s = string.Join(", ", parts);
        return s.Length > 200 ? s[..200] + "…" : s;

        static string Short(JsonNode? v)
        {
            var t = v?.ToJsonString() ?? "null";
            return t.Length > 40 ? t[..40] + "…" : t;
        }
    }
}
