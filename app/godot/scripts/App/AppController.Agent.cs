using System.Text.Json.Nodes;
using Blazma.Agent;
using Blazma.Commands;
using Godot;

namespace Blazma.UI.App;

/// <summary>
/// Phase 5: the app hosts the agent API. An <see cref="AgentIpcServer"/> accepts local connections (blazma-mcp,
/// blazma-anim --attach); each request is marshalled to the Godot main thread with <see cref="Post"/> and run by the
/// shared <see cref="AgentRpcHandler"/>, so agent edits go through <see cref="Session"/> like any other edit.
/// The AI Workspace panel (UI/AgentPanel.cs) shows and controls <see cref="Agents"/>.
/// </summary>
public partial class AppController
{
    /// <summary>Sessions, tasks, approvals and the activity log of connected agents.</summary>
    public AgentHost Agents { get; private set; } = null!;

    AgentRpcHandler _rpc = null!;
    AgentIpcServer? _ipc;
    readonly HashSet<string> _staleTasks = new();

    /// <summary>Raised (main thread) after anything the AI Workspace shows has changed.</summary>
    public event Action? AgentChanged;

    /// <summary>Name of the pipe the server listens on (<c>--agent-pipe=</c> / BLAZMA_AGENT_PIPE override it; self-tests use a private one).</summary>
    public string AgentPipeName { get; private set; } = AgentIpc.PipeName;

    public bool AgentServerRunning => _ipc is not null;

    void InitAgents()
    {
        AgentPipeName = ResolvePipeName();
        Agents = new AgentHost(Registry, () => Session, userPresent: true)
        {
            Mode = Settings.AgentAutonomous ? AgentMode.Autonomous : AgentMode.Assisted,
            ProjectFolder = () => FilePath is null ? null : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(FilePath)),
        };
        SyncAgentRoots();
        Agents.Changed += () => AgentChanged?.Invoke();
        _rpc = new AgentRpcHandler(Agents, Registry, new AppAgentServices(this));
        Session.Changed += OnAgentSessionChanged;
        if (Settings.AllowAgents) StartAgentServer();
    }

    static string ResolvePipeName()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--agent-pipe=") && a.Length > 13) return a[13..];
        if (System.Environment.GetEnvironmentVariable("BLAZMA_AGENT_PIPE") is { Length: > 0 } env) return env;
        // Tests must never meet (or steal the pipe of) a real window.
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--selftest=") || a.StartsWith("--crashtest-"))
                return "blazma-selftest-" + System.Environment.ProcessId;
        return AgentIpc.PipeName;
    }

    public void StartAgentServer()
    {
        if (_ipc is not null) return;
        _ipc = new AgentIpcServer(HandleAgentRequest, sid => Post(() => Agents.Disconnect(sid)), AgentPipeName);
        _ipc.Start();
    }

    public void StopAgentServer()
    {
        var server = _ipc;
        _ipc = null;
        if (server is null) return;
        // The server disposes its listening pipe on shutdown, so "off" really refuses new connections.
        _ = server.DisposeAsync().AsTask().ContinueWith(t => _ = t.Exception);
        foreach (var s in Agents.Sessions.ToList()) Agents.Disconnect(s.Id);
    }

    /// <summary>Called on a pool thread by the IPC server: hand the request to the main thread and wait for its answer.</summary>
    Task<JsonObject> HandleAgentRequest(JsonObject request, AgentRpcContext context)
    {
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.SetResult(_rpc.Handle(request, context)); }
            catch (Exception e) { tcs.SetResult(AgentRpcHandler.Error(request["id"]?.DeepClone(), "internal_error", e.Message)); }
        });
        return tcs.Task;
    }

    // ---- settings -----------------------------------------------------------------------------------

    public void SetAllowAgents(bool allow)
    {
        Settings.AllowAgents = allow;
        Settings.Save();
        if (allow) StartAgentServer(); else StopAgentServer();
        SettingsChanged?.Invoke();
        AgentChanged?.Invoke();
    }

    public void SetAgentAutonomous(bool autonomous)
    {
        Settings.AgentAutonomous = autonomous;
        Agents.Mode = autonomous ? AgentMode.Autonomous : AgentMode.Assisted;
        Settings.Save();
        SettingsChanged?.Invoke();
        AgentChanged?.Invoke();
    }

    public void SetAgentReadRoots(IEnumerable<string> roots)
    {
        Settings.AgentReadRoots.Clear();
        Settings.AgentReadRoots.AddRange(roots.Where(r => r.Length > 0).Distinct());
        SyncAgentRoots();
        Settings.Save();
        AgentChanged?.Invoke();
    }

    void SyncAgentRoots()
    {
        Agents.AllowedReadRoots.Clear();
        Agents.AllowedReadRoots.AddRange(Settings.AgentReadRoots);
    }

    // ---- user decisions on agent work ----------------------------------------------------------------

    /// <summary>True when the task's changes belong to a project that is no longer open (they cannot be reverted any more).</summary>
    public bool IsAgentTaskStale(string taskId) => _staleTasks.Contains(taskId);

    public void AgentApprove(string taskId)
    {
        try { Agents.Approve(taskId); } catch (KeyNotFoundException) { }
    }

    public CommandResult AgentReject(string taskId)
    {
        if (_staleTasks.Contains(taskId))
            return CommandResult.Fail("project_replaced", "The project was replaced; this task can no longer be reverted.");
        try { return Agents.Reject(taskId); }
        catch (KeyNotFoundException e) { return CommandResult.Fail("not_found", e.Message); }
    }

    public CommandResult AgentApprovePending(string approvalId)
    {
        try { return Agents.ApprovePending(approvalId); }
        catch (KeyNotFoundException e) { return CommandResult.Fail("expired", e.Message); }
    }

    public void AgentDenyPending(string approvalId) => Agents.DenyPending(approvalId);

    /// <summary>
    /// New/Open replaces the project and clears the undo history: agent sessions stay connected, but tasks created so far can no
    /// longer be rejected, and approvals that were waiting referred to the old project.
    /// </summary>
    void OnAgentSessionChanged(SessionChangedArgs args)
    {
        if (!args.Changes.Any(c => c.Kind == Change.ProjectReplaced)) return;
        foreach (var s in Agents.Sessions.ToList()) Agents.FinishTask(s.Id, null);
        foreach (var p in Agents.PendingApprovals.ToList()) Agents.DenyPending(p.Id);
        foreach (var t in Agents.Tasks) _staleTasks.Add(t.Id);
        AgentChanged?.Invoke();
    }

    sealed class AppAgentServices(AppController app) : IAgentHostServices
    {
        public string ProjectName => app.Session.Project.Name;
        public string? ProjectPath => app.FilePath;
        public bool IsDirty => app.IsDirty;

        public string Save(string? path)
        {
            if (path is null)
            {
                if (app.FilePath is null)
                    throw new AgentRpcException("no_path", "The project has not been saved yet; pass 'path' or ask the user to save it first.");
                path = app.FilePath;
            }
            else
            {
                // Agents never write outside the project folder or the folders the user allowed, never to
                // network/device paths, and never over an existing file other than the open project.
                var full = AgentPaths.ValidateSaveTarget(path, app.FilePath);
                var same = app.FilePath is not null && AgentPaths.SamePath(full, System.IO.Path.GetFullPath(app.FilePath));
                if (!same && !app.Agents.IsUnderAllowedRoot(full))
                    throw new AgentRpcException("path_not_allowed", "Agents can only save inside the project folder or the folders the user allowed.");
                path = full;
            }
            string? error = null;
            void Capture(string m) => error = m;
            app.ErrorRaised += Capture;
            bool ok;
            try { ok = app.SaveAs(path); }
            finally { app.ErrorRaised -= Capture; }
            if (!ok) throw new AgentRpcException("save_failed", error ?? "The project could not be saved.");
            return app.FilePath!;
        }
    }
}
