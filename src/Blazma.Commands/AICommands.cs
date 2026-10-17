using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.AI;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Core.Timing;

namespace Blazma.Commands;

public enum MotionJobState { Running, Ready, Failed, Cancelled, Applied }

/// <summary>Immutable view of a generation job (safe to hand to any thread).</summary>
public sealed record MotionJobSnapshot(
    string JobId,
    string Provider,
    string Prompt,
    ulong Seed,
    string CharacterId,
    string ClipName,
    MotionJobState State,
    string Stage,
    double Progress,
    string? ErrorCode,
    string? Message,
    IReadOnlyList<string> Warnings,
    string? ClipId,
    int? Frames,
    DateTimeOffset Created,
    DateTimeOffset? Finished)
{
    public bool IsFinished => State is not MotionJobState.Running;

    public JsonObject ToJson() => new()
    {
        ["jobId"] = JobId,
        ["provider"] = Provider,
        ["prompt"] = Prompt,
        ["seed"] = Seed,
        ["characterId"] = CharacterId,
        ["clipName"] = ClipName,
        ["state"] = State.ToString().ToLowerInvariant(),
        ["stage"] = Stage,
        ["progress"] = Math.Round(Progress, 3),
        ["errorCode"] = ErrorCode,
        ["message"] = Message,
        ["warnings"] = new JsonArray(Warnings.Select(w => (JsonNode?)w).ToArray()),
        ["clipId"] = ClipId,
        ["frames"] = Frames,
        ["created"] = Created.ToString("O", CultureInfo.InvariantCulture),
        ["finished"] = Finished?.ToString("O", CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// Long-running motion generation (DECISIONS D12, D24). The synchronous command layer only <i>starts</i> a job
/// (<c>motion.generate</c>, which does not change the project). The job runs on a worker thread on an immutable
/// snapshot of the target rig: provider → MotionResult → retarget → a finished draft <see cref="Clip"/> held in
/// memory. Nothing touches the (single-threaded) <see cref="ProjectSession"/> from the worker. The clip enters the
/// project only through the undoable <c>motion.job_apply</c> command, which the host runs on its session thread
/// (the app: on the main thread when <see cref="JobChanged"/> reports Ready, or when the user clicks; agents: by
/// calling the tool). Thread-safe.
/// </summary>
public sealed class MotionJobService : IDisposable
{
    sealed class Job
    {
        public required string Id;
        public required string Provider;
        public required string Prompt;
        public required ulong Seed;
        public required string CharacterId;
        public required string ClipName;
        public readonly CancellationTokenSource Cts = new();
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MotionJobState State = MotionJobState.Running;
        public string Stage = "queued";
        public double Progress;
        public string? ErrorCode;
        public string? Message;
        public List<string> Warnings = new();
        public Clip? Result;
        public string? ClipId;
        public int? Frames;
        public DateTimeOffset Created = DateTimeOffset.UtcNow;
        public DateTimeOffset? Finished;
    }

    public const int MaxRunningJobs = 4;
    public const int MaxKeptJobs = 50;

    readonly object _lock = new();
    readonly List<Job> _jobs = new();
    readonly List<IMotionGenerationProvider> _providers;

    public MotionJobService(IEnumerable<IMotionGenerationProvider> providers)
    {
        _providers = providers.ToList();
        if (_providers.Select(p => p.Id).Distinct().Count() != _providers.Count) throw new ArgumentException("Provider ids must be unique.");
    }

    /// <summary>Kimodo (settings from BLAZMA_KIMODO_* environment variables) + the procedural provider.</summary>
    public static MotionJobService CreateDefault() =>
        new(new IMotionGenerationProvider[] { new KimodoProvider(KimodoSettings.FromEnvironment()), new ProceduralMotionProvider() });

    public IReadOnlyList<IMotionGenerationProvider> Providers => _providers;
    public KimodoProvider? Kimodo => _providers.OfType<KimodoProvider>().FirstOrDefault();

    /// <summary>
    /// Allows <c>motion.job_status</c> to block for up to waitSeconds. Only for headless hosts, where commands run
    /// one at a time anyway (CLI/MCP without the app). The app leaves it off so its main thread never waits.
    /// </summary>
    public bool AllowBlockingWait { get; set; }

    /// <summary>Raised on worker threads (and on the caller's thread for cancel/apply) whenever a job changes.
    /// UI: marshal to the main thread before touching the session.</summary>
    public event Action<MotionJobSnapshot>? JobChanged;

    public IMotionGenerationProvider? FindProvider(string id) => _providers.Find(p => p.Id == id);

    /// <summary>The provider <c>motion.generate</c> uses: the requested one, or Kimodo when it is available.</summary>
    public IMotionGenerationProvider Resolve(string? id)
    {
        if (id is not null)
        {
            var p = FindProvider(id) ?? throw new CommandException("invalid_args", $"Unknown provider '{id}'. Available: {string.Join(", ", _providers.Select(x => x.Id))}.");
            var info = p.Describe();
            if (!info.Available) throw new CommandException("provider_unavailable", $"{info.Name} is not available: {info.UnavailableReason}");
            return p;
        }
        var kimodo = FindProvider(KimodoProvider.ProviderId);
        if (kimodo?.Describe() is { Available: true }) return kimodo;
        var why = kimodo?.Describe().UnavailableReason ?? "no Kimodo provider is configured.";
        throw new CommandException("provider_unavailable",
            $"Kimodo text-to-motion is not available: {why} Nothing is downloaded automatically; see docs/AI_INTEGRATION.md for installing kimodo-runner and the model files. " +
            $"For an offline demo pass provider=\"procedural\" (keyword cycles: {string.Join(", ", ProceduralMotionProvider.SupportedKeywords)}; not AI).");
    }

    /// <summary>Starts a job. <paramref name="rigSnapshot"/> must be a private copy (it is used on a worker thread).</summary>
    public MotionJobSnapshot Start(IMotionGenerationProvider provider, MotionRequest request, Rig rigSnapshot, ObjectId characterId, RetargetOptions options)
    {
        Job job;
        lock (_lock)
        {
            if (_jobs.Count(j => j.State == MotionJobState.Running) >= MaxRunningJobs)
                throw new CommandException("busy", $"{MaxRunningJobs} generation jobs are already running; wait or cancel one (motion.job_cancel).");
            job = new Job
            {
                Id = ObjectId.New().ToString(), Provider = provider.Id, Prompt = request.Prompt, Seed = request.Seed,
                CharacterId = characterId.ToString(), ClipName = options.Name,
            };
            _jobs.Add(job);
            Prune();
        }
        _ = Task.Run(() => RunAsync(job, provider, request, rigSnapshot, options));
        return Snapshot(job);
    }

    async Task RunAsync(Job job, IMotionGenerationProvider provider, MotionRequest request, Rig rig, RetargetOptions options)
    {
        var lastReported = -1.0;
        var lastStage = "";
        var progress = new SyncProgress(p =>
        {
            lock (_lock)
            {
                if (job.State != MotionJobState.Running) return;
                job.Stage = p.Stage;
                job.Progress = Math.Clamp(p.Fraction, 0, 1) * 0.9; // last 10 % = retargeting
                job.Message = p.Message ?? job.Message;
                if (p.Stage == lastStage && job.Progress - lastReported < 0.01) return;
                lastStage = p.Stage;
                lastReported = job.Progress;
            }
            Raise(job);
        });
        try
        {
            var motion = await provider.GenerateAsync(request, progress, job.Cts.Token).ConfigureAwait(false);
            job.Cts.Token.ThrowIfCancellationRequested();
            progress.Report(new GenerationProgress("retarget", 1, "converting to the character"));
            var r = MotionRetargeter.Retarget(motion, rig, options);
            lock (_lock)
            {
                if (job.State != MotionJobState.Running) return;
                job.Result = r.Clip;
                job.Frames = (int)r.Clip.DurationFrames;
                job.Warnings = r.Warnings.ToList();
                job.State = MotionJobState.Ready;
                job.Stage = "ready";
                job.Progress = 1;
                job.Message = $"{r.Clip.DurationFrames} frames, {r.MappedBones.Count} bones driven, {r.KeyCount} keys" + (r.PinnedFrames > 0 ? $", feet pinned on {r.PinnedFrames} frames" : "") + ".";
            }
        }
        catch (OperationCanceledException)
        {
            lock (_lock) { job.State = MotionJobState.Cancelled; job.Stage = "cancelled"; job.Message = "Cancelled."; }
        }
        catch (MotionGenerationException e)
        {
            lock (_lock) { job.State = MotionJobState.Failed; job.Stage = "failed"; job.ErrorCode = e.Code; job.Message = e.Message; }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            lock (_lock) { job.State = MotionJobState.Failed; job.Stage = "failed"; job.ErrorCode = "internal_error"; job.Message = e.Message; }
        }
        finally
        {
            lock (_lock) job.Finished ??= DateTimeOffset.UtcNow;
            job.Done.TrySetResult();
            Raise(job);
        }
    }

    public MotionJobSnapshot? Get(string jobId)
    {
        lock (_lock) return _jobs.Find(j => j.Id == jobId) is { } j ? Snapshot(j) : null;
    }

    public IReadOnlyList<MotionJobSnapshot> List()
    {
        lock (_lock) return _jobs.Select(Snapshot).ToList();
    }

    /// <summary>Cancels a running job (Kimodo: kills the runner process). False if it had already finished.</summary>
    public bool Cancel(string jobId)
    {
        Job? job;
        lock (_lock)
        {
            job = _jobs.Find(j => j.Id == jobId) ?? throw new CommandException("not_found", $"No generation job '{jobId}'.");
            if (job.State != MotionJobState.Running) return false;
        }
        job.Cts.Cancel();
        return true;
    }

    /// <summary>Waits until the job finishes or the timeout passes. Returns the latest snapshot.</summary>
    public MotionJobSnapshot Wait(string jobId, TimeSpan timeout)
    {
        Job job;
        lock (_lock) job = _jobs.Find(j => j.Id == jobId) ?? throw new CommandException("not_found", $"No generation job '{jobId}'.");
        try { job.Done.Task.Wait(timeout); }
        catch (AggregateException) { }
        return Get(jobId)!;
    }

    /// <summary>A private copy of a Ready (or already applied) job's clip, for <see cref="ApplyMotionJob"/>.</summary>
    internal Clip TakeResult(string jobId)
    {
        lock (_lock)
        {
            var job = _jobs.Find(j => j.Id == jobId) ?? throw new CommandException("not_found", $"No generation job '{jobId}'.");
            return job.State switch
            {
                MotionJobState.Ready or MotionJobState.Applied => ClipEdit.CloneClip(job.Result!),
                MotionJobState.Running => throw new CommandException("job_running", "The job is still running; check motion.job_status and apply when it is ready."),
                MotionJobState.Cancelled => throw new CommandException("job_cancelled", "The job was cancelled."),
                _ => throw new CommandException("job_failed", $"The job failed [{job.ErrorCode}]: {job.Message}"),
            };
        }
    }

    internal void MarkApplied(string jobId, ObjectId clipId)
    {
        Job? job;
        lock (_lock)
        {
            job = _jobs.Find(j => j.Id == jobId);
            if (job is null) return;
            job.State = MotionJobState.Applied;
            job.Stage = "applied";
            job.ClipId = clipId.ToString();
        }
        Raise(job);
    }

    void Prune()
    {
        while (_jobs.Count > MaxKeptJobs && _jobs.FindIndex(j => j.State != MotionJobState.Running) is var i and >= 0)
            _jobs.RemoveAt(i);
    }

    MotionJobSnapshot Snapshot(Job j)
    {
        lock (_lock)
            return new MotionJobSnapshot(j.Id, j.Provider, j.Prompt, j.Seed, j.CharacterId, j.ClipName, j.State, j.Stage, j.Progress,
                j.ErrorCode, j.Message, j.Warnings.ToList(), j.ClipId, j.Frames, j.Created, j.Finished);
    }

    void Raise(Job job)
    {
        var handler = JobChanged;
        if (handler is null) return;
        try { handler(Snapshot(job)); }
        catch (Exception) { /* a UI handler must not break the job */ }
    }

    public void Dispose()
    {
        lock (_lock) foreach (var j in _jobs) if (j.State == MotionJobState.Running) j.Cts.Cancel();
        foreach (var p in _providers.OfType<IDisposable>()) p.Dispose();
    }

    /// <summary>IProgress that calls back synchronously (Progress&lt;T&gt; would post to a captured context).</summary>
    sealed class SyncProgress(Action<GenerationProgress> report) : IProgress<GenerationProgress>
    {
        public void Report(GenerationProgress value) => report(value);
    }
}

/// <summary>Phase 7 commands: text-to-motion generation (as background jobs), provider diagnostics and drafts.</summary>
public static class AICommands
{
    public static void Register(CommandRegistry r, MotionJobService jobs)
    {
        r.Register(new("motion.providers",
            "List the motion generation providers and diagnostics: is kimodo-runner configured and found, are the model files there, CPU/RAM/GPU (best effort, offline). Never downloads anything.",
            CommandEffect.ReadOnly,
            S.Of(S.Bool("probe", "Also start kimodo-runner briefly and ask it for its own diagnostics (only in headless mode; can take seconds).", false)),
            a => new ListMotionProviders(jobs, (bool?)a["probe"] ?? false)));

        r.Register(new("motion.generate",
            "Start generating a motion from a text prompt for a character. Runs in the background and returns a jobId; it does not change the project. " +
            "Poll motion.job_status, then call motion.job_apply to add the result as a DRAFT clip (undoable), review it, and keep it with clip.accept_draft (or remove it with clip.delete). " +
            "Provider: kimodo (local AI, must be installed by the user) by default; 'procedural' is a built-in keyword demo (walk, run, jump, wave, squat, idle), not AI. Body only: fingers and face are not generated.",
            CommandEffect.ReadOnly,
            S.Of(S.Str("characterId", "Character to animate (its rig must be humanoid-mapped).", true),
                 S.Str("prompt", "What the character should do, e.g. \"a person walks forward\".", true),
                 S.Num("durationSeconds", $"Length in seconds ({MotionRequest.MinDurationSeconds}–{MotionRequest.MaxDurationSeconds}).", true),
                 S.Int("fps", "Frame rate of the clip (default: the project frame rate).", false, 1),
                 S.Int("seed", "Random seed (default: a random one, returned and stored in the clip).", false),
                 S.Str("provider", "kimodo | procedural. Default: kimodo, or the error provider_unavailable when it is not installed.", false),
                 S.Str("name", "Clip name (default from the prompt).", false),
                 S.Bool("footCleanup", "Pin feet during ground contact (default true).", false),
                 S.Bool("reduceKeys", "Remove keys that linear interpolation reproduces within 0.1° / 0.5 mm (default true).", false),
                 S.Bool("loop", "Ask for a loopable motion (procedural only; Kimodo ignores constraints).", false),
                 S.Bool("inPlace", "Remove travel (procedural only).", false)),
            a => new GenerateMotion(jobs, S.Id(a, "characterId"), S.Text(a, "prompt"), S.Number(a, "durationSeconds"),
                Args.OptLong(a, "fps"), a["seed"] is null ? null : (ulong)Math.Max(0, Args.OptLong(a, "seed") ?? 0),
                ((string?)a["provider"])?.Trim().ToLowerInvariant(), S.OptName(a, "name"),
                (bool?)a["footCleanup"] ?? true, (bool?)a["reduceKeys"] ?? true, (bool?)a["loop"] ?? false, (bool?)a["inPlace"] ?? false)));

        r.Register(new("motion.job_status",
            "State of a generation job (running, ready, failed, cancelled, applied) with progress, warnings and, once applied, its clipId. Without jobId: all jobs. " +
            "Headless only: waitSeconds blocks until the job finishes (max 600).",
            CommandEffect.ReadOnly,
            S.Of(S.Str("jobId", "Job id from motion.generate (omit to list all jobs).", false),
                 S.Num("waitSeconds", "Wait up to this long for the job to finish (headless mode only; ignored in the app).", false)),
            a => new MotionJobStatus(jobs, (string?)a["jobId"], S.OptNumber(a, "waitSeconds"))));

        r.Register(new("motion.job_cancel", "Cancel a running generation job (stops the Kimodo runner). Does not change the project.", CommandEffect.ReadOnly,
            S.Of(S.Str("jobId", "Job id.", true)),
            a => new CancelMotionJob(jobs, S.Text(a, "jobId"))));

        r.Register(new("motion.job_apply",
            "Add a finished generation job's motion to the project as a new DRAFT clip (undoable). Returns clipId. Review it, edit keys, then clip.accept_draft.",
            CommandEffect.Mutating,
            S.Of(S.Str("jobId", "Job id (state ready).", true), S.Str("name", "Clip name (default: the job's name).", false)),
            a => new ApplyMotionJob(jobs, S.Text(a, "jobId"), S.OptName(a, "name"))));

        r.Register(new("clip.accept_draft", "Accept a draft clip (generated motion): it becomes a normal clip (undoable). To discard a draft use clip.delete.",
            CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Draft clip id.", true)),
            a => new AcceptDraft(S.Id(a, "clipId"))));
    }

    internal static JsonObject DescribeProvider(ProviderDiagnostics d)
    {
        var i = d.Info;
        return new JsonObject
        {
            ["id"] = i.Id,
            ["name"] = i.Name,
            ["kind"] = i.Kind,
            ["available"] = i.Available,
            ["unavailableReason"] = i.UnavailableReason,
            ["licence"] = i.Licence,
            ["skeletons"] = new JsonArray(i.Skeletons.Select(s => (JsonNode?)s).ToArray()),
            ["nativeFps"] = i.NativeFps,
            ["notes"] = new JsonArray(i.Notes.Select(s => (JsonNode?)s).ToArray()),
            ["details"] = new JsonObject(d.Details.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["warnings"] = new JsonArray(d.Warnings.Select(s => (JsonNode?)s).ToArray()),
        };
    }

    internal static JsonObject DescribeHardware(HardwareReport h) => new()
    {
        ["os"] = h.Os,
        ["architecture"] = h.Architecture,
        ["logicalCpus"] = h.LogicalCpus,
        ["totalRamBytes"] = h.TotalRamBytes,
        ["gpus"] = new JsonArray(h.Gpus.Select(g => (JsonNode?)new JsonObject
        {
            ["name"] = g.Name, ["vendor"] = g.Vendor, ["vramBytes"] = g.VramBytes, ["source"] = g.Source,
        }).ToArray()),
        ["notes"] = new JsonArray(h.Notes.Select(s => (JsonNode?)s).ToArray()),
    };
}

public sealed class ListMotionProviders(MotionJobService jobs, bool probe) : ICommand
{
    public string Id => "motion.providers";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Motion providers";

    public CommandOutcome Execute(Project project)
    {
        var warnings = new List<Diagnostic>();
        var doProbe = probe && jobs.AllowBlockingWait;
        if (probe && !doProbe) warnings.Add(new Diagnostic("probe_skipped", "probe is only done in headless mode (it would block the app); the app shows runner diagnostics in its settings."));
        var list = new JsonArray();
        HardwareReport? hw = null;
        foreach (var p in jobs.Providers)
        {
            var d = p.DiagnoseAsync(doProbe, CancellationToken.None).GetAwaiter().GetResult();
            hw = d.Hardware;
            list.Add(AICommands.DescribeProvider(d));
        }
        string? def = jobs.FindProvider(KimodoProvider.ProviderId)?.Describe() is { Available: true } ? KimodoProvider.ProviderId : null;
        return new CommandOutcome(Array.Empty<Change>(), new JsonObject
        {
            ["default"] = def,
            ["providers"] = list,
            ["hardware"] = hw is null ? null : AICommands.DescribeHardware(hw),
        }, warnings);
    }

    public IReadOnlyList<Change> Undo(Project project) => Array.Empty<Change>();
}

public sealed class GenerateMotion(MotionJobService jobs, ObjectId characterId, string prompt, double durationSeconds, long? fps, ulong? seed,
    string? provider, string? name, bool footCleanup, bool reduceKeys, bool loop, bool inPlace) : ICommand
{
    public string Id => "motion.generate";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Generate motion";

    public CommandOutcome Execute(Project project)
    {
        var (character, rig) = PoseCommands.Resolve(project, characterId);
        if (rig.FindByStandardName(Blazma.Rigging.HumanoidStandard.Pelvis) is null)
            throw new CommandException("not_humanoid", $"'{character.Name}' has no bone mapped to 'pelvis'. Map the humanoid bones first (rig.map_bone).");
        if (fps is > 240) throw new CommandException("invalid_args", "'fps' must be between 1 and 240.");
        var rate = fps is long f ? new FrameRate((int)f) : project.Settings.DefaultFrameRate;
        var actualSeed = seed ?? (ulong)Random.Shared.NextInt64(0, int.MaxValue);
        var request = new MotionRequest(prompt, durationSeconds, rate.FramesPerSecond, actualSeed,
            loop || inPlace ? new MotionConstraints { Loop = loop, InPlace = inPlace } : null);
        try { request.Validate(); }
        catch (MotionGenerationException e) { throw new CommandException("invalid_args", e.Message); }
        var p = jobs.Resolve(provider);

        var clipName = name ?? (p.Id == ProceduralMotionProvider.ProviderId ? "Procedural: " : "AI: ") + Shorten(prompt, 40);
        // The worker thread gets its own copy of the rig: the project is only touched on the session thread.
        var rigCopy = JsonSerializer.Deserialize<Rig>(JsonSerializer.SerializeToUtf8Bytes(rig, ProjectArchive.JsonOptions), ProjectArchive.JsonOptions)!;
        var options = new RetargetOptions
        {
            Fps = rate,
            Name = clipName,
            FootCleanup = footCleanup,
            ReduceKeys = reduceKeys,
            Metadata = new Dictionary<string, string>
            {
                ["provider"] = p.Id,
                ["prompt"] = prompt,
                ["seed"] = actualSeed.ToString(CultureInfo.InvariantCulture),
                ["durationSeconds"] = durationSeconds.ToString(CultureInfo.InvariantCulture),
                ["character"] = character.Name,
            },
        };
        var job = jobs.Start(p, request, rigCopy, characterId, options);
        var data = job.ToJson();
        data["next"] = "Poll motion.job_status {jobId}; when state is 'ready' call motion.job_apply {jobId}.";
        var warnings = new List<Diagnostic>();
        if (p.Id == ProceduralMotionProvider.ProviderId)
            warnings.Add(new Diagnostic("procedural", "Procedural provider: a keyword-based demo cycle, not AI generation."));
        return new CommandOutcome(Array.Empty<Change>(), data, warnings);
    }

    static string Shorten(string s, int max)
    {
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    public IReadOnlyList<Change> Undo(Project project) => Array.Empty<Change>();
}

public sealed class MotionJobStatus(MotionJobService jobs, string? jobId, double? waitSeconds) : ICommand
{
    public string Id => "motion.job_status";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Generation job status";

    public CommandOutcome Execute(Project project)
    {
        var warnings = new List<Diagnostic>();
        if (jobId is null)
            return new CommandOutcome(Array.Empty<Change>(), new JsonObject
            {
                ["jobs"] = new JsonArray(jobs.List().Select(j => (JsonNode?)j.ToJson()).ToArray()),
            });
        var snap = jobs.Get(jobId) ?? throw new CommandException("not_found", $"No generation job '{jobId}'.");
        if (waitSeconds is > 0 && !snap.IsFinished)
        {
            if (jobs.AllowBlockingWait) snap = jobs.Wait(jobId, TimeSpan.FromSeconds(Math.Min(waitSeconds.Value, 600)));
            else warnings.Add(new Diagnostic("wait_ignored", "waitSeconds is ignored while the app is open; poll again later."));
        }
        return new CommandOutcome(Array.Empty<Change>(), snap.ToJson(), warnings);
    }

    public IReadOnlyList<Change> Undo(Project project) => Array.Empty<Change>();
}

public sealed class CancelMotionJob(MotionJobService jobs, string jobId) : ICommand
{
    public string Id => "motion.job_cancel";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Cancel generation";

    public CommandOutcome Execute(Project project)
    {
        var cancelled = jobs.Cancel(jobId);
        var snap = jobs.Get(jobId)!.ToJson();
        snap["cancelRequested"] = cancelled;
        return new CommandOutcome(Array.Empty<Change>(), snap);
    }

    public IReadOnlyList<Change> Undo(Project project) => Array.Empty<Change>();
}

/// <summary>Adds a finished job's clip as a draft. The clip id is fixed per command so redo re-adds the same clip;
/// applying the same job twice gives two clips.</summary>
public sealed class ApplyMotionJob(MotionJobService jobs, string jobId, string? name) : ICommand
{
    readonly ObjectId _clipId = ObjectId.New();
    Clip? _clip;

    public string Id => "motion.job_apply";
    public CommandEffect Effect => CommandEffect.Mutating;
    public string Label => "Add generated motion";

    public CommandOutcome Execute(Project project)
    {
        var warnings = new List<Diagnostic>();
        if (_clip is null)
        {
            var clip = jobs.TakeResult(jobId);
            if (project.FindRig(clip.Skeleton) is null)
                throw new CommandException("rig_changed", "The character's rig no longer exists in the project; generate again.");
            clip.Id = _clipId;
            if (name is not null) clip.Name = name;
            clip.IsDraft = true;
            clip.Metadata["jobId"] = jobId;
            _clip = clip;
            foreach (var w in jobs.Get(jobId)?.Warnings ?? Array.Empty<string>()) warnings.Add(new Diagnostic("generation", w));
        }
        project.Clips.Add(ClipEdit.CloneClip(_clip));
        jobs.MarkApplied(jobId, _clipId);
        return new CommandOutcome(new[] { new Change(Change.ClipAdded, _clipId.ToString()) }, new JsonObject
        {
            ["clipId"] = _clipId.ToString(),
            ["name"] = _clip.Name,
            ["isDraft"] = true,
            ["durationFrames"] = _clip.DurationFrames,
            ["fps"] = _clip.Fps.ToString(),
            ["trackCount"] = _clip.Tracks.Count,
        }, warnings);
    }

    public IReadOnlyList<Change> Undo(Project project)
    {
        project.Clips.RemoveAll(c => c.Id == _clipId);
        return new[] { new Change(Change.ClipRemoved, _clipId.ToString()) };
    }
}

public sealed class AcceptDraft(ObjectId clipId) : ClipEdit(clipId)
{
    public override string Id => "clip.accept_draft";
    public override string Label => "Accept draft";

    protected override JsonNode? Apply(Project project, Clip clip, List<Diagnostic> warnings)
    {
        if (!clip.IsDraft) throw new CommandException("not_draft", $"Clip '{clip.Name}' is not a draft.");
        clip.IsDraft = false;
        clip.Metadata["acceptedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return new JsonObject { ["clipId"] = clip.Id.ToString(), ["isDraft"] = false };
    }
}
