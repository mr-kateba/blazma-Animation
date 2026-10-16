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

    /// <summary>Raised after every change to the project, history or save state.</summary>
    public event Action<SessionChangedArgs>? SessionChanged;

    /// <summary>Raised when a user-facing error should be shown (already translated where possible).</summary>
    public event Action<string>? ErrorRaised;

    public event Action? SettingsChanged;

    RecoverySession? _recovery;
    SessionLock? _lock;
    double _sinceAutosave;
    ulong _lastAutosaveVersion;
    ulong _version;
    readonly Queue<Action> _posted = new();

    public string? FilePath => Session.FilePath;
    public bool IsDirty => Session.IsDirty;
    public DateTimeOffset? LastAutosaveUtc => _recovery?.LastAutosaveUtc;

    public override void _EnterTree()
    {
        Instance = this;
        Settings.Load();
        Recovery = new RecoveryStore(Godot.ProjectSettings.GlobalizePath("user://recovery"));
        var log = new JsonlOpLog(Godot.ProjectSettings.GlobalizePath("user://logs/oplog.jsonl"));
        Session = new ProjectSession(NewDefaultProject("Untitled"), null, log);
        Session.Changed += OnSessionChanged;
        _recovery = Recovery.BeginSession(Session.Project, null);
        GetTree().AutoAcceptQuit = false; // MainWindow asks about unsaved changes first
        ApplyLocale();
    }

    public override void _Process(double delta)
    {
        while (_posted.Count > 0) _posted.Dequeue()();
        AdvancePlayback(delta);

        _sinceAutosave += delta;
        if (Settings.AutosaveMinutes > 0 && _sinceAutosave >= Settings.AutosaveMinutes * 60)
            AutosaveNow();
    }

    /// <summary>Queue work for the main thread (used by background jobs and IPC).</summary>
    public void Post(Action action)
    {
        lock (_posted) _posted.Enqueue(action);
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
        SessionChanged?.Invoke(args);
        AfterSessionNotify(args);
    }

    // ---- Files -------------------------------------------------------------------------------

    /// <summary>Starts a fresh project. Callers must have dealt with unsaved changes first.</summary>
    public void NewProject(string name = "Untitled")
    {
        CloseCurrent();
        // The mannequin is added before the session sees the project: a new project is ready to pose and not dirty.
        Session.ReplaceProject(NewDefaultProject(string.IsNullOrWhiteSpace(name) ? "Untitled" : name), null);
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
        var newLock = SessionLock.TryAcquire(path);
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
        catch (ProjectFormatException e)
        {
            newLock.Dispose();
            ErrorRaised?.Invoke(DescribeLoadError(e));
            return false;
        }

        CloseCurrent();
        _lock = newLock;
        Session.ReplaceProject(project, path);
        _recovery = Recovery.BeginSession(project, path);
        Settings.AddRecent(path);
        return true;
    }

    /// <summary>Save to the current path. Returns false if there is no path yet (caller shows Save As).</summary>
    public bool Save() => FilePath is not null && SaveAs(FilePath);

    public bool SaveAs(string path)
    {
        if (!path.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
            path += ProjectFile.Extension;
        path = System.IO.Path.GetFullPath(path);

        var samePath = FilePath is not null && SamePath(FilePath, path);
        SessionLock? newLock = null;
        if (!samePath)
        {
            newLock = SessionLock.TryAcquire(path);
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
            ErrorRaised?.Invoke(string.Format(Tr("Could not save the project: {0}"), e.Message));
            return false;
        }

        if (newLock is not null)
        {
            _lock?.Dispose();
            _lock = newLock;
        }
        Session.MarkSaved(path);
        _recovery?.MarkSaved(path, Session.Project);
        _lastAutosaveVersion = _version;
        Settings.AddRecent(path);
        return true;
    }

    public void AutosaveNow()
    {
        _sinceAutosave = 0;
        if (_recovery is null || !Session.IsDirty || _version == _lastAutosaveVersion) return;
        try
        {
            _recovery.Autosave(Session.Project);
            _lastAutosaveVersion = _version;
            SessionChanged?.Invoke(new SessionChangedArgs(new[] { new Change(Change.SaveStateChanged) }, CommandOrigin.Ui));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"Autosave failed: {e.Message}");
        }
    }

    /// <summary>Loads a recovered project; it starts dirty so the user is asked to save it.</summary>
    public bool RestoreFrom(RecoveryCandidate candidate)
    {
        Project project;
        try { project = Recovery.Recover(candidate); }
        catch (ProjectFormatException e)
        {
            ErrorRaised?.Invoke(DescribeLoadError(e));
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
        _recovery.Autosave(project); // keep it safe until the user saves
        return true;
    }

    /// <summary>Clean shutdown: removes recovery data and releases the lock.</summary>
    public void ShutdownCleanly()
    {
        CloseCurrent();
        Settings.Save();
    }

    void CloseCurrent()
    {
        _recovery?.EndCleanly();
        _recovery = null;
        _lock?.Dispose();
        _lock = null;
        _sinceAutosave = 0;
        _lastAutosaveVersion = _version;
    }

    static bool SamePath(string a, string b) => string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    string DescribeLoadError(ProjectFormatException e) => e.Code switch
    {
        "not_found" => Tr("The file could not be found."),
        "not_a_project" => Tr("This file is not a Blazma Animation project."),
        "integrity" => Tr("The project file is damaged (integrity check failed)."),
        "schema_too_new" => Tr("This project was saved by a newer version of Blazma Animation."),
        _ => string.Format(Tr("The project could not be opened: {0}"), e.Message),
    };

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

    public void SetOnionFrames(int frames)
    {
        Settings.OnionFrames = Math.Clamp(frames, 1, 30);
        Settings.Save();
        SettingsChanged?.Invoke();
    }

    void ApplyLocale() => TranslationServer.SetLocale(Settings.Language);

    public bool IsRtl => Settings.Language == "ar";
}
