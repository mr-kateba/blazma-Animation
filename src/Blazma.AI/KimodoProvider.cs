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
    bool _disposed;

    public KimodoProvider(KimodoSettings settings)
    {
        _settings = settings;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopRunner("host exit");
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
                "Runs locally on CPU or Vulkan; the first request loads the models and can take a while. Memory needs depend on the text encoder quantisation (not measured by Blazma yet, R4).",
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
            ["diffusionSteps"] = s.DiffusionSteps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["runnerRunning"] = RunnerRunning.ToString().ToLowerInvariant(),
            ["settingsFrom"] = $"{KimodoSettings.RunnerEnv}, {KimodoSettings.ModelsEnv} or the app settings",
        };
        var warnings = new List<string>();
        if (info.UnavailableReason is { } why) warnings.Add(why);
        if (probe && runner is not null)
        {
            try
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var r = await EnsureRunnerAsync(s, ct).ConfigureAwait(false);
                    var d = await r.CallAsync("diagnose", new JsonObject
                    {
                        ["motionModel"] = motion, ["textModel"] = text, ["device"] = s.Device,
                    }, null, s.DiagnoseTimeout, ct).ConfigureAwait(false);
                    foreach (var (k, v) in _hello ?? new JsonObject()) details["runner." + k] = v?.ToJsonString().Trim('"');
                    foreach (var (k, v) in d)
                        if (k == "warnings" && v is JsonArray wa) warnings.AddRange(wa.Select(x => (string?)x ?? ""));
                        else details["runner." + k] = v?.ToJsonString().Trim('"');
                }
                finally
                {
                    _gate.Release();
                    ScheduleIdleStop();
                }
            }
            catch (MotionGenerationException e)
            {
                warnings.Add($"Runner probe failed [{e.Code}]: {e.Message}");
                StopRunner("probe failed");
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
        if (frames > 10_000) throw new MotionGenerationException("invalid_request", "Kimodo accepts at most 10000 frames.");

        progress?.Report(new GenerationProgress("queued", 0));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CancelIdleStop();
            progress?.Report(new GenerationProgress("start", 0.02, "starting kimodo-runner"));
            var runner = await EnsureRunnerAsync(s, ct).ConfigureAwait(false);
            var p = new JsonObject
            {
                ["prompt"] = request.Prompt,
                ["frames"] = frames,
                ["seed"] = request.Seed,
                ["steps"] = s.DiffusionSteps,
                ["motionModel"] = s.ResolveMotionModel(),
                ["textModel"] = s.ResolveTextModel(),
                ["device"] = s.Device,
                ["threads"] = s.Threads,
            };
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
                StopRunner(e.Code);
                throw;
            }
            catch (OperationCanceledException)
            {
                StopRunner("cancelled");
                throw;
            }
            var motion = Parse(result, request);
            progress?.Report(new GenerationProgress("done", 1));
            return motion;
        }
        finally
        {
            _gate.Release();
            ScheduleIdleStop();
        }
    }

    async Task<RunnerProcess> EnsureRunnerAsync(KimodoSettings s, CancellationToken ct)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runner is { HasExited: false } live) return live;
            _runner?.Dispose();
            _runner = null;
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
                _hello = hello;
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

    static MotionResult Parse(JsonObject r, MotionRequest request)
    {
        try
        {
            var skel = r["skeleton"] as JsonObject ?? throw new MotionGenerationException("invalid_motion", "The result has no skeleton.");
            var names = (skel["names"] as JsonArray ?? new JsonArray()).Select(x => (string?)x ?? "").ToList();
            var parents = (skel["parents"] as JsonArray ?? new JsonArray()).Select(x => (int)x!).ToList();
            var offsets = (skel["offsets"] as JsonArray ?? new JsonArray()).Select(o => o is JsonArray a && a.Count == 3
                ? new Vec3((double)a[0]!, (double)a[1]!, (double)a[2]!) : throw new MotionGenerationException("invalid_motion", "Bad offset.")).ToList();
            if ((string?)skel["up"] is { } up && up != "+Y" || (string?)skel["forward"] is { } fwd && fwd != "+Z")
                throw new MotionGenerationException("invalid_motion", "The runner's skeleton is not +Y up / +Z forward.");
            var definition = SomaSkeleton.FromRunner((string?)skel["key"] ?? "", names, parents, offsets);

            var fps = (double?)r["fps"] ?? SomaSkeleton.NativeFps;
            var frames = (int?)r["frames"] ?? 0;
            var joints = (int?)r["joints"] ?? 0;
            if (joints != definition.Count) throw new MotionGenerationException("invalid_motion", $"The result has {joints} joints, the skeleton {definition.Count}.");
            var rot = r["rotations"] as JsonArray ?? throw new MotionGenerationException("invalid_motion", "The result has no rotations.");
            var pos = r["rootPositions"] as JsonArray ?? throw new MotionGenerationException("invalid_motion", "The result has no root positions.");
            if (frames <= 0 || rot.Count != frames * joints * 4 || pos.Count != frames * 3)
                throw new MotionGenerationException("invalid_motion", "Rotation/position arrays do not match frames × joints.");
            var q = new Quat[frames * joints];
            for (var i = 0; i < q.Length; i++) q[i] = new Quat((double)rot[i * 4]!, (double)rot[i * 4 + 1]!, (double)rot[i * 4 + 2]!, (double)rot[i * 4 + 3]!);
            var p = new Vec3[frames];
            for (var i = 0; i < frames; i++) p[i] = new Vec3((double)pos[i * 3]!, (double)pos[i * 3 + 1]!, (double)pos[i * 3 + 2]!);

            var warnings = new List<string> { "Kimodo generates the 30 SOMA body joints only: fingers and face stay at their rest pose (R5)." };
            if (request.Constraints is { IsEmpty: false }) warnings.Add("Constraints were ignored: kimodo.cpp does not support constraint input yet.");
            warnings.AddRange((r["warnings"] as JsonArray ?? new JsonArray()).Select(x => (string?)x ?? "").Where(x => x.Length > 0));
            var meta = new Dictionary<string, string>();
            if ((string?)r["model"] is { } model) meta["kimodo.model"] = Path.GetFileName(model);
            if ((string?)r["textModel"] is { } tm) meta["kimodo.textModel"] = Path.GetFileName(tm);
            if (r["steps"] is JsonValue st) meta["kimodo.steps"] = st.ToJsonString();
            if ((string?)r["runner"] is { } rv) meta["kimodo.runner"] = rv;
            var result = new MotionResult(ProviderId, definition, fps, frames, q, p, warnings) { Metadata = meta };
            result.Validate();
            return result;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or InvalidCastException or NullReferenceException or System.Text.Json.JsonException)
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
            _idleTimer = new Timer(_ =>
            {
                if (!_gate.Wait(0)) return; // busy again
                try { StopRunner("idle"); }
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

    /// <summary>Stops the runner process (the next request starts a new one).</summary>
    public void StopRunner(string reason = "stopped")
    {
        RunnerProcess? r;
        lock (_sync)
        {
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
        lock (_sync) _disposed = true;
        StopRunner("disposed");
    }
}
