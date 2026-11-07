using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Rigging;
using Godot;

namespace Blazma.UI.App;

/// <summary>
/// Autoload ("App") that owns the open project and everything around it: the command session,
/// file lock, autosave/recovery and user settings. Views never touch Blazma.Core directly for
/// edits; they call <see cref="Execute"/> (or Undo/Redo) and listen to <see cref="SessionChanged"/>.
/// Everything here runs on the Godot main thread. Future IPC (MCP attach) enqueues work with
/// <see cref="Post"/>, which is drained in _Process so the scene tree is only touched here.
/// </summary>
public partial class AppController : Node
{
    public static AppController Instance { get; private set; } = null!;

    public CommandRegistry Registry { get; } = CommandRegistry.CreateDefault();
    public ProjectSession Session { get; private set; } = null!;
    public AppSettings Settings { get; } = new();
    public RecoveryStore Recovery { get; private set; } = null!;

    /// <summary>Phase 6: cached balance analysis of the active clip and the working pose (viewport overlay, timeline strip, panel).</summary>
    public PhysicsAnalysis Physics { get; private set; } = null!;

    /// <summary>Raised after every change to the project, history or save state.</summary>
    public event Action<SessionChangedArgs>? SessionChanged;

    /// <summary>Raised when a user-facing error should be shown (already translated where possible).</summary>
    public event Action<string>? ErrorRaised;

    /// <summary>Raised after opening a project that had to be repaired by a migration (messages from the core).</summary>
    public event Action<IReadOnlyList<string>>? LoadWarningsRaised;

    public event Action? SettingsChanged;

    RecoverySession? _recovery;
    SessionLock? _lock;
    double _sinceAutosave;
    ulong _lastAutosaveVersion;
    ulong _version;
    /// <summary>Counts every session change (caches of derived data key on it).</summary>
    public ulong SessionVersion => _version;
    readonly Queue<Action> _posted = new();

    public string? FilePath => Session.FilePath;
    public bool IsDirty => Session.IsDirty;
    public DateTimeOffset? LastAutosaveUtc => _recovery?.LastAutosaveUtc;

    /// <summary>First existing *.blazanim path passed on the command line (file association: <c>BlazmaAnimation.exe "%1"</c>), or null.</summary>
    public string? StartupProject { get; private set; }

    /// <summary>Picks the first argument that names an existing project file (options starting with "-" are skipped).</summary>
    public static string? FindStartupProject(IEnumerable<string> args)
    {
        foreach (var a in args)
        {
            if (string.IsNullOrWhiteSpace(a) || a.StartsWith('-')) continue;
            if (!a.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var full = System.IO.Path.GetFullPath(a);
                if (System.IO.File.Exists(full)) return full;
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { }
        }
        return null;
    }

    /// <summary>Named mutex the installer checks to know whether the app is running. Only held, never waited on: a second window is allowed.</summary>
    System.Threading.Mutex? _runningMutex;

    void HoldRunningMutex()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { _runningMutex = new System.Threading.Mutex(false, "BlazmaAnimation-Running"); }
        catch (Exception e) { GD.PushWarning($"Could not create the running-instance mutex: {e.Message}"); }
    }

    public override void _EnterTree()
    {
        Instance = this;
        StartupProject = FindStartupProject(OS.GetCmdlineArgs().Concat(OS.GetCmdlineUserArgs()));
        HoldRunningMutex();
        Settings.Load();
        ApplyLocale(); // before the first project is named
        Recovery = new RecoveryStore(AppPaths.Combine("recovery"));
        var log = new JsonlOpLog(AppPaths.Combine(System.IO.Path.Combine("logs", "oplog.jsonl")));
        Session = new ProjectSession(NewDefaultProject(Tr("Untitled")), null, log);
        Session.Changed += OnSessionChanged;
        Physics = new PhysicsAnalysis(this);
        _recovery = Recovery.BeginSession(Session.Project, null);
        GetTree().AutoAcceptQuit = false; // MainWindow asks about unsaved changes first
        ApplyLocale();
        InitAgents();
        InitMotion();
    }

    public override void _ExitTree()
    {
        try { _runningMutex?.Dispose(); } catch (Exception) { }
        _runningMutex = null;
    }

    public override void _Process(double delta)
    {
        DrainPosted();
        AdvancePlayback(delta);

        _sinceAutosave += delta;
        if (Settings.AutosaveMinutes > 0 && _sinceAutosave >= Settings.AutosaveMinutes * 60)
            AutosaveNow();
    }

    /// <summary>Queue work for the main thread (used by background jobs and IPC). Safe to call from any thread.</summary>
    public void Post(Action action)
    {
        lock (_posted) _posted.Enqueue(action);
    }

    /// <summary>Runs everything queued by <see cref="Post"/>. The queue is only touched under its lock (producers run on pool threads).</summary>
    void DrainPosted()
    {
        while (true)
        {
            Action next;
            lock (_posted)
            {
                if (_posted.Count == 0) return;
                next = _posted.Dequeue();
            }
            try { next(); }
            catch (Exception e) { GD.PushError($"Posted action failed: {e}"); }
        }
    }

    // ---- Editing -----------------------------------------------------------------------------

    public CommandResult Execute(string commandId, JsonObject? args = null)
    {
        ICommand command;
        try { command = Registry.Create(commandId, args); }
        catch (CommandException e) { return Report(CommandResult.Fail(e.Code, e.Message)); }
        if (PoseEditIds.Contains(commandId)) return ExecutePoseEdit(command, commandId, args);
        return Report(Session.Execute(command, CommandOrigin.Ui));
    }

    public CommandResult Execute(ICommand command) => Report(Session.Execute(command, CommandOrigin.Ui));

    public TransactionScope BeginTransaction(string label) => Session.BeginTransaction(label, CommandOrigin.Ui);

    public CommandResult Undo() => Report(Session.Undo(CommandOrigin.Ui), quietCodes: "nothing_to_undo");
    public CommandResult Redo() => Report(Session.Redo(CommandOrigin.Ui), quietCodes: "nothing_to_redo");

    /// <summary>Error code of the most recent failure that was reported through <see cref="ErrorRaised"/>.</summary>
    public string? LastErrorCode { get; private set; }

    CommandResult Report(CommandResult r, string? quietCodes = null)
    {
        if (!r.Ok && r.Error is { } e && e.Code != quietCodes)
        {
            LastErrorCode = e.Code;
            ErrorRaised?.Invoke(e.Message);
        }
        return r;
    }

    void OnSessionChanged(SessionChangedArgs args)
    {
        _version++;
        BeforeSessionNotify(args);
        Phase8Before(args);
        SessionChanged?.Invoke(args);
        AfterSessionNotify(args);
        Phase8After(args);
        Phase9After(args);
    }

    // ---- Files -------------------------------------------------------------------------------

    /// <summary>Starts a fresh project. Callers must have dealt with unsaved changes first.</summary>
    public void NewProject(string? name = null)
    {
        CloseCurrent();
        // The mannequin is added before the session sees the project: a new project is ready to pose and not dirty.
        Session.ReplaceProject(NewDefaultProject(string.IsNullOrWhiteSpace(name) ? Tr("Untitled") : name), null);
        _recovery = Recovery.BeginSession(Session.Project, null);
    }

    /// <summary>A new project that already holds the default mannequin (outside the undo history).</summary>
    static Project NewDefaultProject(string name)
    {
        var project = Project.CreateNew(name);
        MannequinFactory.AddTo(project);
        return project;
    }

    public bool Open(string path)
    {
        if (FilePath is not null && SamePath(FilePath, path)) return true; // already open here
        SessionLock? newLock;
        try { newLock = SessionLock.TryAcquire(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorRaised?.Invoke(DescribeFileError(e, opening: true));
            return false;
        }
        if (newLock is null)
        {
            ErrorRaised?.Invoke(Tr("This project is open in another Blazma window or tool."));
            return false;
        }
        Project project;
        try
        {
            project = ProjectFile.Load(path);
        }
        catch (Exception e) when (e is ProjectFormatException or IOException or UnauthorizedAccessException)
        {
            newLock.Dispose();
            ErrorRaised?.Invoke(DescribeFileError(e, opening: true));
            return false;
        }

        CloseCurrent();
        _lock = newLock;
        Session.ReplaceProject(project, path);
        _recovery = Recovery.BeginSession(project, path);
        Settings.AddRecent(path);
        if (project.LoadWarnings.Count > 0) LoadWarningsRaised?.Invoke(project.LoadWarnings.ToList());
        return true;
    }

    /// <summary>Save to the current path. Returns false if there is no path yet (caller shows Save As).</summary>
    public bool Save() => FilePath is not null && SaveAs(FilePath);

    public bool SaveAs(string path)
    {
        if (!path.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
            path += ProjectFile.Extension;
        try { path = System.IO.Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            ErrorRaised?.Invoke(DescribeFileError(e, opening: false));
            return false;
        }

        var samePath = FilePath is not null && SamePath(FilePath, path);
        SessionLock? newLock = null;
        if (!samePath)
        {
            try { newLock = SessionLock.TryAcquire(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ErrorRaised?.Invoke(DescribeFileError(e, opening: false));
                return false;
            }
            if (newLock is null)
            {
                ErrorRaised?.Invoke(Tr("That file is open in another Blazma window or tool."));
                return false;
            }
        }

        try
        {
            ProjectFile.Save(Session.Project, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ProjectFormatException)
        {
            newLock?.Dispose();
            ErrorRaised?.Invoke(DescribeFileError(e, opening: false));
            return false;
        }

        if (newLock is not null)
        {
            _lock?.Dispose();
            _lock = newLock;
        }
        Session.MarkSaved(path);
        WaitForAutosave();
        _recovery?.MarkSaved(path, Session.Project);
        _lastAutosaveVersion = _version;
        Settings.AddRecent(path);
        return true;
    }

    // ---- Autosave (off the main thread) ---------------------------------------------------------

    Task? _autosaveTask;
    /// <summary>True while an autosave is being written on the worker thread (the status bar shows "Saving…").</summary>
    public bool IsAutosaving => _autosaveTask is { IsCompleted: false };

    /// <summary>Main thread: a private copy of the project (a few tens of ms). A worker writes it while the user keeps editing.</summary>
    ProjectSnapshot SnapshotForAutosave() => ProjectArchive.Snapshot(Session.Project);

    /// <summary>Worker thread: writes the snapshot into the recovery folder (thread-safe against MarkSaved / EndCleanly).</summary>
    static bool WriteAutosave(RecoverySession recovery, ProjectSnapshot snapshot) => recovery.AutosaveSnapshot(snapshot);

    /// <summary>Starts an autosave without blocking. Never runs two writes at once (a pending request is simply skipped; the next tick retries).</summary>
    public void AutosaveNow()
    {
        _sinceAutosave = 0;
        if (_recovery is null || !Session.IsDirty || _version == _lastAutosaveVersion || IsAutosaving) return;
        ProjectSnapshot snapshot;
        try { snapshot = SnapshotForAutosave(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ProjectFormatException)
        {
            GD.PushWarning($"Autosave failed: {e.Message}");
            return;
        }
        var recovery = _recovery;
        var version = _version;
        Task? self = null;
        self = _autosaveTask = Task.Run(() =>
        {
            Exception? error = null; var written = false;
            try { written = WriteAutosave(recovery, snapshot); }
            catch (Exception e) { error = e; }
            Post(() => AutosaveFinished(self!, recovery, version, written, error));
        });
        NotifySaveState(); // "Saving…"
    }

    /// <summary>Autosave and wait for it (shutdown paths and tests only).</summary>
    public void AutosaveNowAndWait()
    {
        WaitForAutosave();
        AutosaveNow();
        WaitForAutosave();
        DrainPosted();
    }

    /// <summary>Blocks until a running autosave has finished; the recovery folder must not change underneath it.</summary>
    void WaitForAutosave()
    {
        try { _autosaveTask?.Wait(); } catch (AggregateException) { }
    }

    void AutosaveFinished(Task task, RecoverySession recovery, ulong version, bool written, Exception? error)
    {
        if (ReferenceEquals(_autosaveTask, task)) _autosaveTask = null;
        if (error is null && written && ReferenceEquals(recovery, _recovery)) _lastAutosaveVersion = Math.Max(_lastAutosaveVersion, version);
        else if (error is not null) GD.PushWarning($"Autosave failed: {error.Message}");
        NotifySaveState();
    }

    void NotifySaveState() =>
        SessionChanged?.Invoke(new SessionChangedArgs(new[] { new Change(Change.SaveStateChanged) }, CommandOrigin.Ui));

    /// <summary>Loads a recovered project; it starts dirty so the user is asked to save it.</summary>
    public bool RestoreFrom(RecoveryCandidate candidate)
    {
        Project project;
        try { project = Recovery.Recover(candidate); }
        catch (Exception e) when (e is ProjectFormatException or IOException or UnauthorizedAccessException)
        {
            ErrorRaised?.Invoke(DescribeFileError(e, opening: true));
            return false;
        }

        SessionLock? newLock = null;
        if (candidate.ProjectPath is not null)
        {
            newLock = SessionLock.TryAcquire(candidate.ProjectPath);
            if (newLock is null)
            {
                ErrorRaised?.Invoke(Tr("This project is open in another Blazma window or tool."));
                return false;
            }
        }

        CloseCurrent();
        Recovery.Discard(candidate);
        _lock = newLock;
        Session.ReplaceProject(project, candidate.ProjectPath, startDirty: true);
        _recovery = Recovery.BeginSession(project, candidate.ProjectPath);
        try { _recovery.Autosave(project); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { GD.PushWarning($"Autosave failed: {e.Message}"); } // keep it safe until the user saves
        return true;
    }

    /// <summary>Clean shutdown: removes recovery data and releases the lock.</summary>
    public void ShutdownCleanly()
    {
        StopAgentServer();
        DisposeMotion();
        CloseCurrent();
        Settings.Save();
    }

    void CloseCurrent()
    {
        WaitForAutosave();
        _recovery?.EndCleanly();
        _recovery = null;
        _lock?.Dispose();
        _lock = null;
        _sinceAutosave = 0;
        _lastAutosaveVersion = _version;
    }

    static bool SamePath(string a, string b) => string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Friendly text for a failed open/save. Core codes map to fixed messages; plain I/O exceptions are classified here.</summary>
    string DescribeFileError(Exception e, bool opening)
    {
        var code = FileErrors.Classify(e);
        switch (code)
        {
            case "not_found": return Tr("The file could not be found.");
            case "not_a_project": return Tr("This file is not a Blazma Animation project.");
            case "integrity": return Tr("The project file is damaged (integrity check failed).");
            case "schema_too_new": return Tr("This project was saved by a newer version of Blazma Animation.");
            case "read_only_folder": return Tr("Blazma cannot write to this folder. Choose another folder or check its permissions.");
            case "path_too_long": return Tr("The file path is too long. Choose a shorter path.");
            case "sharing_violation": return Tr("The file is being used by another program. Close it there and try again.");
            case "disk_full": return Tr("The disk is full. Free some space or choose another drive.");
            case "locked_by_other": return Tr("This project is open in another Blazma window or tool.");
        }
        return string.Format(opening ? Tr("The project could not be opened: {0}") : Tr("Could not save the project: {0}"), e.Message);
    }

    // ---- Settings ------------------------------------------------------------------------------

    public void SetLanguage(string locale)
    {
        Settings.Language = locale;
        ApplyLocale();
        Settings.Save();
        SettingsChanged?.Invoke();
    }

    public void SetTheme(string theme)
    {
        Settings.Theme = theme;
        Settings.Save();
        SettingsChanged?.Invoke();
    }

    public void SetAdvancedMode(bool advanced)
    {
        Settings.AdvancedMode = advanced;
        Settings.Save();
        SettingsChanged?.Invoke();
    }

    public void SetShowSkeleton(bool show)
    {
        Settings.ShowSkeleton = show;
        Settings.Save();
        SettingsChanged?.Invoke();
    }

    public void SetOverlayAll(bool all)
    {
        Settings.OverlayAll = all;
        Settings.Save();
        SettingsChanged?.Invoke();
    }

    public void SetAutosaveMinutes(int minutes)
    {
        Settings.AutosaveMinutes = Math.Clamp(minutes, 0, 60);
        Settings.Save();
        SettingsChanged?.Invoke();
    }

    /// <summary>Viewport camera preferences (clamped): fly speed 0.5-40 m/s, FOV 30-100 degrees.</summary>
    public void SetCamera(float flySpeed, bool invertY, bool orbitSelection, float fov)
    {
        Settings.CameraFlySpeed = Math.Clamp(flySpeed, 0.5f, 40f);
        Settings.CameraInvertY = invertY;
        Settings.CameraOrbitSelection = orbitSelection;
        Settings.CameraFov = Math.Clamp(fov, 30f, 100f);
        Settings.Save();
        SettingsChanged?.Invoke();
    }

    public void SetShowTrail(bool show)
    {
        Settings.ShowTrail = show;
        Settings.Save();
        SettingsChanged?.Invoke();
    }

    public void SetShowOnion(bool show)
    {
        Settings.ShowOnion = show;
        Settings.Save();
        SettingsChanged?.Invoke();
    }

    public void SetShowPhysics(bool show)
    {
        Settings.ShowPhysics = show;
        Settings.Save();
        SettingsChanged?.Invoke();
    }

    public void SetShowPhysicsPaths(bool show)
    {
        Settings.ShowPhysicsPaths = show;
        Settings.Save();
        SettingsChanged?.Invoke();
    }

    public void SetPhysicsPanel(bool open)
    {
        Settings.PhysicsPanel = open;
        Settings.Save();
        SettingsChanged?.Invoke();
    }

    public void SetOnionFrames(int frames)
    {
        Settings.OnionFrames = Math.Clamp(frames, 1, 30);
        Settings.Save();
        SettingsChanged?.Invoke();
    }

    void ApplyLocale() => TranslationServer.SetLocale(Settings.Language);

    public bool IsRtl => Settings.Language == "ar";
}
