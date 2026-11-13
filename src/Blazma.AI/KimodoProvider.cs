using System.Text.Json.Nodes;
using Blazma.Core.Mathematics;

namespace Blazma.AI;

/// <summary>
/// Kimodo text-to-motion through the external <c>kimodo-runner</c> process (DECISIONS D6, D24). The runner is
/// started on the first request that needs it, keeps the loaded models between requests, and is stopped after
/// <see cref="KimodoSettings.IdleTimeout"/> without requests. One generation runs at a time. Cancelling or timing
/// out kills the process; a crash becomes a clean <see cref="MotionGenerationException"/> and the next request
/// starts a fresh runner. Nothing is downloaded: missing files make the provider unavailable with a message.
/// </summary>
public sealed class KimodoProvider : IMotionGenerationProvider, IDisposable
{
    public const string ProviderId = "kimodo";
    public const int ProtocolVersion = 1;

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly object _sync = new();
    readonly Queue<string> _log = new();
    KimodoSettings _settings;
    RunnerProcess? _runner;
    Timer? _idleTimer;
    JsonObject? _hello;
    IReadOnlyList<KimodoDevice>? _devices;
    KimodoDeviceUse? _lastUse;
    bool _disposed;
    long _epoch; // bumped whenever the runner is stopped on purpose: a runner started before that is never kept
    readonly EventHandler _onProcessExit;

    public KimodoProvider(KimodoSettings settings)
    {
        _settings = settings;
        _onProcessExit = (_, _) => StopRunner("host exit");
        AppDomain.CurrentDomain.ProcessExit += _onProcessExit;
    }

    public string Id => ProviderId;

    /// <summary>Raised (on a worker thread) for every runner log line and lifecycle event.</summary>
    public event Action<string>? Log;

    /// <summary>The settings in use. Setting new ones stops a running runner (it holds the old models).</summary>
    public KimodoSettings Settings
    {
        get { lock (_sync) return _settings; }
        set
        {
            lock (_sync) _settings = value;
            StopRunner("settings changed");
        }
    }

    /// <summary>True while a runner process exists (for diagnostics and tests).</summary>
    public bool RunnerRunning { get { lock (_sync) return _runner is { HasExited: false }; } }

    public int? RunnerProcessId { get { lock (_sync) return _runner?.ProcessId; } }

    public IReadOnlyList<string> RecentLog() { lock (_log) return _log.ToArray(); }

    /// <summary>The devices the runner listed when it last started (null: not started yet, or a runner older than 0.3).</summary>
    public IReadOnlyList<KimodoDevice>? RunnerDevices { get { lock (_sync) return _devices; } }

    /// <summary>Where the last successful generation ran (null before the first one).</summary>
    public KimodoDeviceUse? LastDeviceUse { get { lock (_sync) return _lastUse; } }

    /// <summary>Starts the runner if needed and returns its devices (CUDA / Vulkan graphics cards and the processor). When a
    /// generation is running the runner is not disturbed: the list from its start is returned. Null for an old runner.</summary>
    public async Task<IReadOnlyList<KimodoDevice>?> ProbeDevicesAsync(CancellationToken ct)
    {
        var s = Settings;
        if (s.ResolveRunner() is null) throw new MotionGenerationException("provider_unavailable", s.MissingReason() ?? "kimodo-runner not found.");
        if (!_gate.Wait(0)) return RunnerDevices;
        try
        {
            await EnsureRunnerAsync(s, ct).ConfigureAwait(false);
            return RunnerDevices;
        }
        finally
        {
            _gate.Release();
            ScheduleIdleStop();
        }
    }

    public ProviderInfo Describe()
    {
        var s = Settings;
        var missing = s.MissingReason();
        return new ProviderInfo(Id, "Kimodo (kimodo.cpp, local)", "ai", missing is null, missing,
            "Runner: Apache-2.0 (kimodo.cpp). Weights are not distributed with Blazma: SOMA RP/SEED v1.1 under the NVIDIA Open Model License; " +
            "the text encoder contains Meta Llama 3 material under its own licence. Review the model cards before use (docs/RISKS.md R3).",
            new[] { SomaSkeleton.Key }, SomaSkeleton.NativeFps,
            new[]
            {
                "Generates the 30 SOMA body joints only: fingers and face are not animated (R5).",
                "Runs locally on the graphics card (CUDA or Vulkan) or the processor; Automatic tries the graphics card first and falls back to graphics card + processor, then the processor. The first request loads the models and can take a while. Memory needs depend on the text encoder quantisation (not measured by Blazma yet, R4).",
                "Constraints are not supported by kimodo.cpp yet.",
            });
    }

    public async Task<ProviderDiagnostics> DiagnoseAsync(bool probe, CancellationToken ct)
    {
        var s = Settings;
        var info = Describe();
        var runner = s.ResolveRunner();
        var motion = s.ResolveMotionModel();
        var text = s.ResolveTextModel();
        var details = new Dictionary<string, string?>
        {
            ["runnerPath"] = s.RunnerPath,
            ["runnerFound"] = (runner is not null).ToString().ToLowerInvariant(),
            ["modelDirectory"] = s.ModelDirectory,
            ["model"] = s.Model,
            ["motionModel"] = motion,
            ["motionModelFound"] = (motion is not null).ToString().ToLowerInvariant(),
            ["textModel"] = text,
            ["textModelFound"] = (text is not null).ToString().ToLowerInvariant(),
            ["tokenizerFound"] = (text is not null && KimodoSettings.HasTokenizer(text)).ToString().ToLowerInvariant(),
            ["device"] = s.Device,
            ["gpuDevice"] = s.GpuDevice,
            ["lastDevice"] = LastDeviceUse?.Summary,
            ["diffusionSteps"] = s.DiffusionSteps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["runnerRunning"] = RunnerRunning.ToString().ToLowerInvariant(),
            ["settingsFrom"] = $"{KimodoSettings.RunnerEnv}, {KimodoSettings.ModelsEnv} or the app settings",
        };
        var warnings = new List<string>();
        if (info.UnavailableReason is { } why) warnings.Add(why);
        if (probe && runner is not null)
        {
            // Never wait behind a generation (it can run for minutes, and a headless host answers one call at a time):
            // when the runner is busy, report what it said when it started.
            if (!_gate.Wait(0))
            {
                details["runnerBusy"] = "true";
                JsonObject? hello;
                lock (_sync) hello = _hello?.DeepClone().AsObject();
                foreach (var (k, v) in hello ?? new JsonObject()) details["runner." + k] = v?.ToJsonString().Trim('"');
                warnings.Add("kimodo-runner is busy with a generation, so it was not probed again; the runner details are from when it started.");
                return new ProviderDiagnostics(info, details, HardwareProbe.Probe(), warnings);
            }
            RunnerProcess? probed = null;
            try
            {
                probed = await EnsureRunnerAsync(s, ct).ConfigureAwait(false);
                var d = await probed.CallAsync("diagnose", new JsonObject
                {
                    ["motionModel"] = motion, ["textModel"] = text,
                }, null, s.DiagnoseTimeout, ct).ConfigureAwait(false);
                JsonObject? hello;
                lock (_sync) hello = _hello?.DeepClone().AsObject();
                foreach (var (k, v) in hello ?? new JsonObject()) details["runner." + k] = v?.ToJsonString().Trim('"');
                foreach (var (k, v) in d)
                    if (k == "warnings" && v is JsonArray wa) warnings.AddRange(wa.Select(x => (string?)x ?? ""));
                    else details["runner." + k] = v?.ToJsonString().Trim('"');
            }
            catch (MotionGenerationException e)
            {
                warnings.Add($"Runner probe failed [{e.Code}]: {e.Message}");
                // Only the runner this probe used: still inside the gate, so no generation can be using it.
                if (probed is not null) StopRunner(probed, "probe failed");
            }
            finally
            {
                _gate.Release();
                ScheduleIdleStop();
            }
        }
        return new ProviderDiagnostics(info, details, HardwareProbe.Probe(), warnings);
    }

    public async Task<MotionResult> GenerateAsync(MotionRequest request, IProgress<GenerationProgress>? progress, CancellationToken ct)
    {
        request.Validate();
        var s = Settings;
        if (s.MissingReason() is { } missing) throw new MotionGenerationException("provider_unavailable", missing);
        var frames = request.FramesAt(SomaSkeleton.NativeFps);
        if (frames > MaxFrames) throw new MotionGenerationException("invalid_request", $"Kimodo accepts at most {MaxFrames} frames.");

        progress?.Report(new GenerationProgress("queued", 0));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CancelIdleStop();
            progress?.Report(new GenerationProgress("start", 0.02, "starting kimodo-runner"));
            var runner = await EnsureRunnerAsync(s, ct).ConfigureAwait(false);
            var attempts = KimodoDevicePlanner.Plan(s.Device, s.GpuDevice, RunnerDevices);
            var fallbackWarnings = new List<string>();
            JsonObject result;
            KimodoAttempt attempt;
            for (var i = 0; ; i++)
            {
                attempt = attempts[i];
                try
                {
                    if (i > 0) runner = await EnsureRunnerAsync(s, ct).ConfigureAwait(false); // a crash on the previous device ended it
                    result = await GenerateOnceAsync(runner, s, request, frames, attempt, progress, ct).ConfigureAwait(false);
                    break;
                }
                catch (MotionGenerationException e) when (i + 1 < attempts.Count && KimodoDevicePlanner.ShouldFallBack(e.Code))
                {
                    var what = DescribeAttempt(attempt);
                    var next = DescribeAttempt(attempts[i + 1]);
                    var w = $"Kimodo could not run on {what} [{e.Code}]: {e.Message} — trying {next} instead.";
                    fallbackWarnings.Add(w);
                    Write(w);
                    progress?.Report(new GenerationProgress("fallback", 0.05, $"{what} failed; trying {next}"));
                }
            }
            var motion = Parse(result, request);
            var use = KimodoDeviceUse.FromResult(result) ?? new KimodoDeviceUse(attempt.Device, null, null);
            lock (_sync) _lastUse = use;
            var meta = new Dictionary<string, string>(motion.Metadata)
            {
                ["kimodo.device"] = use.Mode,
                ["kimodo.generatedOn"] = use.Summary,
            };
            if (use.Motion is { } md) meta["kimodo.motionDevice"] = md.Label;
            if (use.Text is { } td) meta["kimodo.textDevice"] = td.Label;
            var cudaHint = use.CudaWouldHelp(RunnerDevices);
            if (cudaHint) meta["kimodo.cudaHint"] = "true";
            var warnings = motion.Warnings.ToList();
            if (fallbackWarnings.Count > 0)
                warnings.Insert(0, $"Generated on {use.Summary} after {fallbackWarnings.Count} failed attempt(s): {string.Join(" ", fallbackWarnings)}");
            motion = motion with { Metadata = meta, Warnings = warnings };
            Write($"generated on {use.Summary}" + (cudaHint ? " (CUDA is not installed: the optional CUDA download in Settings → AI is usually faster on this card)" : ""));
            progress?.Report(new GenerationProgress("done", 1));
            return motion;
        }
        finally
        {
            _gate.Release();
            ScheduleIdleStop();
        }
    }

    string DescribeAttempt(KimodoAttempt a)
    {
        var gpu = a.Gpu is { } name ? RunnerDevices?.FirstOrDefault(d => d.Name == name)?.Label ?? name : null;
        return a.Device switch
        {
            "gpu" => $"the graphics card {gpu ?? "(best available)"}",
            "hybrid" => $"graphics card + processor ({gpu ?? "best graphics card"}, text encoder on the processor)",
            "cpu" => "the processor",
            "vulkan" => "the graphics card (Vulkan)",
            _ => "the automatically chosen device",
        };
    }

    async Task<JsonObject> GenerateOnceAsync(RunnerProcess runner, KimodoSettings s, MotionRequest request, int frames, KimodoAttempt attempt,
        IProgress<GenerationProgress>? progress, CancellationToken ct)
    {
        var p = new JsonObject
        {
            ["prompt"] = request.Prompt,
            ["frames"] = frames,
            ["seed"] = request.Seed,
            ["steps"] = s.DiffusionSteps,
            ["motionModel"] = s.ResolveMotionModel(),
            ["textModel"] = s.ResolveTextModel(),
            ["device"] = attempt.Device,
            ["threads"] = s.Threads,
        };
        if (attempt.Gpu is { } gpu) p["gpu"] = gpu;
        JsonObject result;
        try
        {
            result = await runner.CallAsync("generate", p, n => progress?.Report(new GenerationProgress(
                (string?)n["stage"] ?? "generate",
                Math.Clamp(n["fraction"] is JsonValue f && f.TryGetValue<double>(out var x) ? x : 0, 0, 1),
                (string?)n["message"])), s.GenerateTimeout, ct).ConfigureAwait(false);
        }
        catch (MotionGenerationException e) when (e.Code is "runner_crashed" or "runner_timeout" or "message_too_large" or "protocol_error" or "runner_stopped")
        {
            StopRunner(runner, e.Code);
            throw;
        }
        catch (OperationCanceledException)
        {
            StopRunner(runner, "cancelled");
            throw;
        }
        return result;
    }

    async Task<RunnerProcess> EnsureRunnerAsync(KimodoSettings s, CancellationToken ct)
    {
        long epoch;
        lock (_sync)
        {
            if (_disposed) throw new MotionGenerationException("runner_stopped", "The Kimodo provider was shut down.");
            if (_runner is { HasExited: false } live) return live;
            _runner?.Dispose();
            _runner = null;
            epoch = _epoch;
        }
        var path = s.ResolveRunner() ?? throw new MotionGenerationException("provider_unavailable", s.MissingReason() ?? "kimodo-runner not found.");
        var runner = RunnerProcess.Start(path, s, Write);
        Write($"kimodo-runner started (pid {runner.ProcessId}).");
        try
        {
            var hello = await runner.CallAsync("hello", new JsonObject { ["protocol"] = ProtocolVersion, ["client"] = "blazma" }, null, s.StartTimeout, ct).ConfigureAwait(false);
            if ((int?)hello["protocol"] != ProtocolVersion)
                throw new MotionGenerationException("protocol_mismatch", $"kimodo-runner speaks protocol {hello["protocol"]}, Blazma needs {ProtocolVersion}. Update kimodo-runner.");
            lock (_sync)
            {
                // Disposed, or stopped on purpose (new settings) while this one was starting: never keep it.
                if (_disposed || _epoch != epoch)
                    throw new MotionGenerationException("runner_stopped", _disposed ? "The Kimodo provider was shut down." : "kimodo-runner was stopped while it was starting (the settings changed).");
                _hello = hello;
                _devices = KimodoDevice.ListFrom(hello);
                _runner = runner;
            }
            return runner;
        }
        catch
        {
            runner.Dispose();
            throw;
        }
    }

    /// <summary>Largest motion the runner may return (the same limit the runner and <see cref="GenerateAsync"/> apply).</summary>
    public const int MaxFrames = 10_000;

    /// <summary>An integer field within [min, max] (integral JSON numbers only; checked before anything is allocated).</summary>
    static int ReadInt(JsonNode? node, string name, int min, int max, int? fallback = null)
    {
        if (node is null && fallback is { } f) return f;
        long value;
        if (node is JsonValue v && v.TryGetValue<long>(out var l)) value = l;
        else if (node is JsonValue iv && iv.TryGetValue<int>(out var i)) value = i;
        else if (node is JsonValue d && d.TryGetValue<double>(out var x) && double.IsFinite(x) && Math.Floor(x) == x && Math.Abs(x) < 1e15) value = (long)x;
        else throw new MotionGenerationException("invalid_motion", $"The result's '{name}' is not an integer.");
        if (value < min || value > max) throw new MotionGenerationException("invalid_motion", $"The result's '{name}' ({value}) is outside {min}..{max}.");
        return (int)value;
    }

    internal static MotionResult Parse(JsonObject r, MotionRequest request)
    {
        try
        {
            var skel = r["skeleton"] as JsonObject ?? throw new MotionGenerationException("invalid_motion", "The result has no skeleton.");
            var namesNode = skel["names"] as JsonArray ?? new JsonArray();
            if (namesNode.Count > SomaSkeleton.Names.Count) throw new MotionGenerationException("invalid_motion", "The result's skeleton has too many joints.");
            var names = namesNode.Select(x => (string?)x ?? "").ToList();
            var parents = (skel["parents"] as JsonArray ?? new JsonArray()).Select(x => ReadInt(x, "parents", -1, SomaSkeleton.Names.Count)).ToList();
            var offsets = (skel["offsets"] as JsonArray ?? new JsonArray()).Select(o => o is JsonArray a && a.Count == 3
                ? new Vec3((double)a[0]!, (double)a[1]!, (double)a[2]!) : throw new MotionGenerationException("invalid_motion", "Bad offset.")).ToList();
            if ((string?)skel["up"] is { } up && up != "+Y" || (string?)skel["forward"] is { } fwd && fwd != "+Z")
                throw new MotionGenerationException("invalid_motion", "The runner's skeleton is not +Y up / +Z forward.");
            var definition = SomaSkeleton.FromRunner((string?)skel["key"] ?? "", names, parents, offsets);

            var fps = (double?)r["fps"] ?? SomaSkeleton.NativeFps;
            // Validate the sizes before allocating anything: a hostile or broken runner must not make Blazma allocate
            // frames × joints arrays of its choosing (or overflow the multiplication).
            var frames = ReadInt(r["frames"], "frames", 1, MaxFrames);
            var joints = ReadInt(r["joints"], "joints", 1, SomaSkeleton.Names.Count);
            if (joints != definition.Count) throw new MotionGenerationException("invalid_motion", $"The result has {joints} joints, the skeleton {definition.Count}.");
            var rot = r["rotations"] as JsonArray ?? throw new MotionGenerationException("invalid_motion", "The result has no rotations.");
            var pos = r["rootPositions"] as JsonArray ?? throw new MotionGenerationException("invalid_motion", "The result has no root positions.");
            var quatCount = checked(frames * joints);
            if (rot.Count != checked(quatCount * 4) || pos.Count != checked(frames * 3))
                throw new MotionGenerationException("invalid_motion", "Rotation/position arrays do not match frames × joints.");
            var q = new Quat[quatCount];
            for (var i = 0; i < q.Length; i++) q[i] = new Quat((double)rot[i * 4]!, (double)rot[i * 4 + 1]!, (double)rot[i * 4 + 2]!, (double)rot[i * 4 + 3]!);
            var p = new Vec3[frames];
            for (var i = 0; i < frames; i++) p[i] = new Vec3((double)pos[i * 3]!, (double)pos[i * 3 + 1]!, (double)pos[i * 3 + 2]!);

            var warnings = new List<string> { "Kimodo generates the 30 SOMA body joints only: fingers and face stay at their rest pose (R5)." };
            if (request.Constraints is { IsEmpty: false }) warnings.Add("Constraints were ignored: kimodo.cpp does not support constraint input yet.");
            warnings.AddRange((r["warnings"] as JsonArray ?? new JsonArray()).Take(20).Select(x => (string?)x ?? "").Where(x => x.Length > 0));
            var meta = new Dictionary<string, string>();
            if ((string?)r["model"] is { } model) meta["kimodo.model"] = Path.GetFileName(model);
            if ((string?)r["textModel"] is { } tm) meta["kimodo.textModel"] = Path.GetFileName(tm);
            if (r["steps"] is JsonValue st) meta["kimodo.steps"] = st.ToJsonString();
            if ((string?)r["runner"] is { } rv) meta["kimodo.runner"] = rv;
            var result = new MotionResult(ProviderId, definition, fps, frames, q, p, warnings) { Metadata = meta };
            result.Validate();
            return result;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or InvalidCastException or NullReferenceException
                                       or System.Text.Json.JsonException or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            throw new MotionGenerationException("invalid_motion", $"kimodo-runner returned a malformed result: {e.Message}", e);
        }
    }

    void ScheduleIdleStop()
    {
        lock (_sync)
        {
            if (_runner is null || _disposed) return;
            _idleTimer?.Dispose();
            var runner = _runner;
            _idleTimer = new Timer(_ =>
            {
                if (!_gate.Wait(0)) return; // busy again
                try
                {
                    lock (_sync) if (!ReferenceEquals(_runner, runner)) return;
                    StopRunner("idle");
                }
                finally { _gate.Release(); }
            }, null, _settings.IdleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    void CancelIdleStop()
    {
        lock (_sync)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }

    /// <summary>Stops the runner process (the next request starts a new one). A runner that is still starting is not kept.</summary>
    public void StopRunner(string reason = "stopped")
    {
        RunnerProcess? r;
        lock (_sync)
        {
            _epoch++;
            r = _runner;
            _runner = null;
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
        if (r is null) return;
        if (reason == "idle") r.Shutdown(TimeSpan.FromSeconds(2));
        else r.Kill(reason);
        r.Dispose();
    }

    /// <summary>Stops <paramref name="instance"/> only: a runner started in the meantime (by another request) is left alone.</summary>
    void StopRunner(RunnerProcess instance, string reason)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_runner, instance))
            {
                _runner = null;
                _idleTimer?.Dispose();
                _idleTimer = null;
            }
        }
        instance.Kill(reason);
        instance.Dispose();
    }

    void Write(string line)
    {
        lock (_log)
        {
            _log.Enqueue($"{DateTimeOffset.UtcNow:HH:mm:ss} {line}");
            while (_log.Count > 200) _log.Dequeue();
        }
        Log?.Invoke(line);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        AppDomain.CurrentDomain.ProcessExit -= _onProcessExit;
        StopRunner("disposed");
    }
}
