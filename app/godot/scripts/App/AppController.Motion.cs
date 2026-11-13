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
        if (Settings.KimodoTextModel.Length > 0) s = s with { TextModel = Settings.KimodoTextModel };
        if (Settings.KimodoDevice != "auto") s = s with { Device = Settings.KimodoDevice };
        if (Settings.KimodoGpu.Length > 0) s = s with { GpuDevice = Settings.KimodoGpu };
        if (Settings.KimodoTimeoutSeconds > 0) s = s with { GenerateTimeout = TimeSpan.FromSeconds(Settings.KimodoTimeoutSeconds) };
        // The optional CUDA backend the user downloaded (DECISIONS D47), when it matches this runner build.
        if (s.CudaBackendDirectory is null && CudaState().Status == CudaBackendStatus.Installed) s = s with { CudaBackendDirectory = CudaDirectory() };
        return s;
    }

    // ---- optional CUDA backend (DECISIONS D47) ------------------------------------------------------------------------

    /// <summary>Tests only: another folder for the CUDA download.</summary>
    public string? CudaDirectoryOverride { get; set; }

    /// <summary>Where the CUDA backend is downloaded to: next to the models (%LOCALAPPDATA%\BlazmaAnimation\runtime\cuda), or
    /// userdata/runtime/cuda in portable mode. A folder set in BLAZMA_KIMODO_CUDA_DIR wins.</summary>
    public string CudaDirectory() =>
        CudaDirectoryOverride
        ?? (System.Environment.GetEnvironmentVariable(KimodoSettings.CudaEnv) is { Length: > 0 } env && System.IO.Path.IsPathFullyQualified(env) ? env
            : AppPaths.Portable ? AppPaths.Combine(System.IO.Path.Combine("runtime", "cuda")) : CudaBackend.DefaultDirectory());

    /// <summary>The runner the CUDA package belongs to: the configured one, else the one bundled with the app.</summary>
    public string? CudaRunner() =>
        EffectiveKimodoSettingsWithoutCuda().ResolveRunner() ?? ModelCatalog.Default.FindBundledRunner(System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? "");

    KimodoSettings EffectiveKimodoSettingsWithoutCuda()
    {
        var s = KimodoSettings.FromEnvironment();
        return Settings.KimodoRunner.Length > 0 ? s with { RunnerPath = Settings.KimodoRunner } : s;
    }

    /// <summary>Tests only: the package description to use instead of cuda-backend.json next to the runner.</summary>
    public CudaBackendPackage? CudaPackageOverride { get; set; }

    /// <summary>This build's CUDA package (cuda-backend.json next to the runner), or null (Linux, a self-built runner, no CUDA asset).</summary>
    public CudaBackendPackage? CudaPackage() => CudaPackageOverride ?? CudaBackendPackage.Load(CudaRunner());

    public CudaBackendState CudaState() => CudaBackend.State(CudaPackage(), CudaDirectory(), CudaRunner());

    /// <summary>True when the download is offered (package available, not installed): the "generated on" hint can point to it.</summary>
    public bool CudaDownloadOffered() => CudaState().Status is CudaBackendStatus.NotInstalled or CudaBackendStatus.Outdated;

    /// <summary>After installing or removing the CUDA backend: the runner restarts with (or without) it.</summary>
    public void CudaBackendChanged()
    {
        ApplyKimodoSettings();
        SettingsChanged?.Invoke();
        AgentChanged?.Invoke();
    }

    void ApplyKimodoSettings()
    {
        if (Kimodo is { } k) k.Settings = EffectiveKimodoSettings(); // restarts the runner
    }

    public void SetKimodoSettings(string runner, string models, string device, int timeoutSeconds, string? gpu = null)
    {
        if (gpu is not null) Settings.KimodoGpu = gpu.Trim();
        Settings.KimodoRunner = runner.Trim();
        // A text encoder chosen by the installer belongs to its folder: another folder means "find the best file there".
        if (!string.Equals(Settings.KimodoModels, models.Trim(), StringComparison.Ordinal)) Settings.KimodoTextModel = "";
        Settings.KimodoModels = models.Trim();
        Settings.KimodoDevice = KimodoSettings.NormalizeDevice(device) is var d && KimodoSettings.DeviceModes.Contains(d) ? d : "auto";
        Settings.KimodoTimeoutSeconds = Math.Clamp(timeoutSeconds, 0, 24 * 3600);
        Settings.Save();
        ApplyKimodoSettings();
        SettingsChanged?.Invoke();
        AgentChanged?.Invoke();
    }

    /// <summary>Points Kimodo at a freshly downloaded package (folder, text encoder, device), saves, and restarts the runner.
    /// Without a runner chosen yet, a runner bundled next to the app is used.</summary>
    public void ApplyModelInstall(ModelInstallResult r, string? bundledRunner)
    {
        var s = r.ApplyTo(new KimodoSettings());
        Settings.KimodoModels = s.ModelDirectory ?? "";
        Settings.KimodoTextModel = s.TextModel ?? "";
        Settings.KimodoDevice = KimodoSettings.DeviceModes.Contains(s.Device) ? s.Device : "auto";
        if (Settings.KimodoRunner.Length == 0 && bundledRunner is not null) Settings.KimodoRunner = bundledRunner;
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
