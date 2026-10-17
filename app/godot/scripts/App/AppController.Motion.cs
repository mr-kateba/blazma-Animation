using System.Text.Json.Nodes;
using Blazma.AI;
using Blazma.Commands;
using Godot;

namespace Blazma.UI.App;

/// <summary>Result of the settings "Check" button: runner diagnostics (or why they failed) plus what the machine has.</summary>
public sealed record KimodoCheckResult(ProviderDiagnostics? Diagnostics, string? Error);

/// <summary>
/// Phase 7: the app's side of text-to-motion generation. The job service lives in <see cref="CommandRegistry.Motion"/> and reports changes
/// on worker threads; everything here marshals them to the main thread with <see cref="Post"/>. Jobs started from the UI are applied
/// automatically when they are ready; jobs started by agents (MCP) are only listed, the agent applies them with motion.job_apply.
/// Kimodo is configured from the app settings (falling back to the BLAZMA_KIMODO_* environment variables). Nothing is downloaded.
/// </summary>
public partial class AppController
{
    readonly HashSet<string> _uiJobs = new();
    readonly HashSet<string> _autoApplied = new();
    int _kimodoLogPending;
    bool _motionDisposed;

    public MotionJobService MotionJobs => Registry.Motion!;
    public KimodoProvider? Kimodo => MotionJobs.Kimodo;

    /// <summary>Raised on the main thread whenever a generation job changed (progress, ready, failed, applied...).</summary>
    public event Action<MotionJobSnapshot>? MotionJobChanged;

    /// <summary>Raised on the main thread (coalesced) after the Kimodo runner wrote to its log.</summary>
    public event Action? KimodoLogChanged;

    void InitMotion()
    {
        ApplyKimodoSettings();
        MotionJobs.JobChanged += snap => Post(() => OnMotionJob(snap));
        if (Kimodo is { } k)
            k.Log += _ =>
            {
                if (Interlocked.Exchange(ref _kimodoLogPending, 1) == 0) Post(() => { Interlocked.Exchange(ref _kimodoLogPending, 0); KimodoLogChanged?.Invoke(); });
            };
    }

    void DisposeMotion()
    {
        if (_motionDisposed) return;
        _motionDisposed = true;
        try { MotionJobs.Dispose(); } catch (Exception e) { GD.PushWarning($"Motion service shutdown: {e.Message}"); }
    }

    void OnMotionJob(MotionJobSnapshot snap)
    {
        if (_motionDisposed) return;
        MotionJobChanged?.Invoke(snap);
        AgentChanged?.Invoke(); // the AI panel lists the jobs
    }

    /// <summary>True for jobs started from this window (the user is waiting for them); false for agent jobs.</summary>
    public bool IsUiJob(string jobId) => _uiJobs.Contains(jobId);

    /// <summary>True once per UI job: the caller should apply it now.</summary>
    public bool TakeAutoApply(string jobId) => _uiJobs.Contains(jobId) && _autoApplied.Add(jobId);

    /// <summary>Starts a job through motion.generate (the same command agents use). Returns the job id, or null with the error in <paramref name="error"/>.</summary>
    public string? StartMotionJob(JsonObject args, out string error, Func<string, JsonObject, CommandResult>? execute = null)
    {
        var r = execute is null ? Execute("motion.generate", args) : execute("motion.generate", args);
        if (!r.Ok) { error = r.Error?.Message ?? ""; return null; }
        error = "";
        var id = (string?)r.Data?["jobId"];
        if (id is not null) _uiJobs.Add(id);
        return id;
    }

    public void CancelMotionJob(string jobId) => Execute("motion.job_cancel", new JsonObject { ["jobId"] = jobId });

    /// <summary>Applies a ready job as an undoable draft clip (main thread). The caller selects and plays it.</summary>
    public CommandResult ApplyMotionJob(string jobId) =>
        Execute("motion.job_apply", new JsonObject { ["jobId"] = jobId });

    // ---- Kimodo settings -----------------------------------------------------------------------------------------

    /// <summary>The effective settings: environment variables first, the user's choices in the app on top.</summary>
    public KimodoSettings EffectiveKimodoSettings()
    {
        var s = KimodoSettings.FromEnvironment();
        if (Settings.KimodoRunner.Length > 0) s = s with { RunnerPath = Settings.KimodoRunner };
        if (Settings.KimodoModels.Length > 0) s = s with { ModelDirectory = Settings.KimodoModels };
        if (Settings.KimodoDevice != "auto") s = s with { Device = Settings.KimodoDevice };
        if (Settings.KimodoTimeoutSeconds > 0) s = s with { GenerateTimeout = TimeSpan.FromSeconds(Settings.KimodoTimeoutSeconds) };
        return s;
    }

    void ApplyKimodoSettings()
    {
        if (Kimodo is { } k) k.Settings = EffectiveKimodoSettings(); // restarts the runner
    }

    public void SetKimodoSettings(string runner, string models, string device, int timeoutSeconds)
    {
        Settings.KimodoRunner = runner.Trim();
        Settings.KimodoModels = models.Trim();
        Settings.KimodoDevice = device is "cpu" or "vulkan" ? device : "auto";
        Settings.KimodoTimeoutSeconds = Math.Clamp(timeoutSeconds, 0, 24 * 3600);
        Settings.Save();
        ApplyKimodoSettings();
        SettingsChanged?.Invoke();
        AgentChanged?.Invoke();
    }

    /// <summary>
    /// The settings "Check" button: runs the provider diagnostics (starting the runner briefly when it is configured) on a worker thread and
    /// calls <paramref name="done"/> on the main thread. The main thread never waits.
    /// </summary>
    public void RunKimodoCheck(Action<KimodoCheckResult> done)
    {
        var kimodo = Kimodo;
        if (kimodo is null) { done(new KimodoCheckResult(null, "Kimodo is not part of this build.")); return; }
        _ = Task.Run(async () =>
        {
            KimodoCheckResult result;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                result = new KimodoCheckResult(await kimodo.DiagnoseAsync(true, cts.Token).ConfigureAwait(false), null);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { result = new KimodoCheckResult(null, e.Message); }
            Post(() => done(result));
        });
    }
}
