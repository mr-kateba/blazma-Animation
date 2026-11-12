using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.AI;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Core.Timing;
using Blazma.Motion;

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

    /// <summary>The steps of a planned (multi-step) job, in order; empty for a single-prompt job.</summary>
    public IReadOnlyList<MotionJobSegment> Segments { get; init; } = Array.Empty<MotionJobSegment>();

    /// <summary>0-based step being generated (planned jobs while running), else null.</summary>
    public int? CurrentSegment { get; init; }

    /// <summary>Who started the job ("ui", "mcp:&lt;session&gt;", "test"); null when it was started without an origin.</summary>
    public string? Owner { get; init; }

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
        ["owner"] = Owner,
        ["segments"] = new JsonArray(Segments.Select(x => (JsonNode?)x.ToJson()).ToArray()),
        ["currentSegment"] = CurrentSegment,
    };
}

/// <summary>One step of a planned generation: the user's words, what was sent to the provider and how long it is (DECISIONS D45).</summary>
/// <param name="Prompt">Text sent to the provider (English caption for Kimodo, a keyword for the procedural generator).</param>
public sealed record MotionJobPart(string Text, string Prompt, double DurationSeconds, string? Action = null, bool Understood = true);

/// <summary>State of one step of a planned job. <paramref name="Device"/>: where Kimodo generated it ("generatedOn"), when known.</summary>
public sealed record MotionJobSegment(int Index, string Text, string Caption, double DurationSeconds, string Provider, string? Action, bool Understood,
    string State, string? Device)
{
    public JsonObject ToJson() => new()
    {
        ["index"] = Index,
        ["text"] = Text,
        ["caption"] = Caption,
        ["durationSeconds"] = Math.Round(DurationSeconds, 3),
        ["provider"] = Provider,
        ["action"] = Action,
        ["understood"] = Understood,
        ["state"] = State,
        ["device"] = Device,
    };
}

/// <summary>
/// Long-running motion generation (DECISIONS D12, D24, D29). The synchronous command layer only <i>starts</i> a job
/// (<c>motion.generate</c>, which does not change the project). The job runs on a worker thread on an immutable
/// snapshot of the target rig: provider → MotionResult → retarget → a finished draft <see cref="Clip"/> held in
/// memory. Nothing touches the (single-threaded) <see cref="ProjectSession"/> from the worker. The clip enters the
/// project only through the undoable <c>motion.job_apply</c> command, which the host runs on its session thread
/// (the app: on the main thread when <see cref="JobChanged"/> reports Ready, or when the user clicks; agents: by
/// calling the tool). Thread-safe.
/// <para>Ownership: a job belongs to the origin that started it. Through the commands, agents see, wait for, cancel and
/// apply only their own jobs (others: <c>not_owner</c>); the user (UI origin) sees all. The C# API without an origin
/// (<see cref="List()"/>, <see cref="Get(string)"/>, <see cref="Cancel(string)"/>) is for the app itself and is unrestricted.</para>
/// <para>Staleness: a job remembers the project, character, rig and a fingerprint of the rig (bones, parents, rest pose,
/// humanoid map). Applying it into a different project, or after the character was removed or re-rigged or the rig was
/// edited, fails with <c>job_stale</c>.</para>
/// <para>Memory: the clip is held while the job is Ready; applying hands it to the undo history (undoing the apply gives it
/// back and the job is Ready again). Finished jobs are kept for <see cref="FinishedRetention"/>, at most
/// <see cref="MaxFinishedJobs"/>.</para>
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
        public required ObjectId RigId;
        public required string RigFingerprint;
        public CommandOrigin? Owner;
        public ObjectId? ProjectId;
        public readonly CancellationTokenSource Cts = new();
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MotionJobState State = MotionJobState.Running;
        public bool CancelRequested;
        public string Stage = "queued";
        public double Progress;
        public string? ErrorCode;
        public string? Message;
        public List<string> Warnings = new();
        public Clip? Result;
        public string? ClipId;
        public int? Frames;
        public DateTimeOffset Created;
        public DateTimeOffset? Finished;
        public List<MotionJobSegment> Segments = new();
        public int? CurrentSegment;
    }

    /// <summary>Cross-fade between the steps of a planned job (seconds; shorter for very short steps).</summary>
    public const double StepBlendSeconds = 0.3;

    public const int MaxRunningJobs = 4;

    /// <summary>Finished jobs (ready, applied, failed, cancelled) kept; the oldest are dropped first.</summary>
    public const int MaxFinishedJobs = 20;

    /// <summary>Most jobs held at once (running + finished).</summary>
    public const int MaxKeptJobs = MaxRunningJobs + MaxFinishedJobs;

    /// <summary>Finished jobs older than this are dropped (with their unapplied clip).</summary>
    public static readonly TimeSpan FinishedRetention = TimeSpan.FromMinutes(30);

    /// <summary>Default cap for a blocking <c>motion.job_status</c> wait (a headless host answers one call at a time).</summary>
    public const int MaxWaitSeconds = 25;

    readonly object _lock = new();
    readonly List<Job> _jobs = new();
    readonly List<IMotionGenerationProvider> _providers;
    readonly CancellationTokenSource _disposed = new();

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
    /// Allows <c>motion.job_status</c> to block for up to waitSeconds (capped at <see cref="MaxBlockingWait"/>). Only for headless
    /// hosts, where commands run one at a time anyway (CLI/MCP without the app). The app leaves it off so its main thread never waits.
    /// </summary>
    public bool AllowBlockingWait { get; set; }

    /// <summary>Longest blocking wait of <c>motion.job_status</c> (default <see cref="MaxWaitSeconds"/> s).</summary>
    public TimeSpan MaxBlockingWait { get; set; } = TimeSpan.FromSeconds(MaxWaitSeconds);

    /// <summary>Clock for job times and pruning (tests replace it).</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

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

    /// <summary>
    /// Fingerprint of everything the retargeted clip depends on: bone ids, parents and rest transforms, and the humanoid map.
    /// A job whose rig fingerprint changed is stale.
    /// </summary>
    public static string RigFingerprint(Rig rig)
    {
        var sb = new System.Text.StringBuilder();
        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        foreach (var b in rig.Bones)
        {
            var r = b.Rest;
            sb.Append(b.Id).Append('|').Append(b.Parent).Append('|')
              .Append(F(r.Translation.X)).Append(',').Append(F(r.Translation.Y)).Append(',').Append(F(r.Translation.Z)).Append('|')
              .Append(F(r.Rotation.X)).Append(',').Append(F(r.Rotation.Y)).Append(',').Append(F(r.Rotation.Z)).Append(',').Append(F(r.Rotation.W)).Append('|')
              .Append(F(r.Scale.X)).Append(',').Append(F(r.Scale.Y)).Append(',').Append(F(r.Scale.Z)).Append('\n');
        }
        sb.Append("map\n");
        foreach (var m in rig.HumanoidMap.OrderBy(m => m.Standard, StringComparer.Ordinal).ThenBy(m => m.Bone.ToString(), StringComparer.Ordinal))
            sb.Append(m.Standard).Append('=').Append(m.Bone).Append('\n');
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Starts a job without an owner or project (visible to every caller). <paramref name="rigSnapshot"/> must be a private copy.</summary>
    public MotionJobSnapshot Start(IMotionGenerationProvider provider, MotionRequest request, Rig rigSnapshot, ObjectId characterId, RetargetOptions options) =>
        Start(provider, request, rigSnapshot, characterId, options, null, null);

    /// <summary>Starts a job owned by <paramref name="owner"/> for <paramref name="projectId"/>. <paramref name="rigSnapshot"/> must be a
    /// private copy (it is used on a worker thread).</summary>
    public MotionJobSnapshot Start(IMotionGenerationProvider provider, MotionRequest request, Rig rigSnapshot, ObjectId characterId, RetargetOptions options,
        CommandOrigin? owner, ObjectId? projectId) => StartCore(provider, request, null, rigSnapshot, characterId, options, owner, projectId);

    /// <summary>
    /// Starts a planned job (DECISIONS D45): every part is generated as its own provider request (in order, seed + index), retargeted, then
    /// the parts are assembled with the clip-sequence machinery (root continuity and facing, <see cref="StepBlendSeconds"/> cross-fades,
    /// foot-phase matching between two gait steps) and baked into ONE draft clip with a marker per step. One part = <see cref="Start(IMotionGenerationProvider, MotionRequest, Rig, ObjectId, RetargetOptions, CommandOrigin?, ObjectId?)"/>
    /// with that part's prompt. <paramref name="request"/> gives fps, seed and constraints; its prompt and length are the whole job's.
    /// </summary>
    public MotionJobSnapshot StartPlan(IMotionGenerationProvider provider, MotionRequest request, IReadOnlyList<MotionJobPart> parts, Rig rigSnapshot,
        ObjectId characterId, RetargetOptions options, CommandOrigin? owner = null, ObjectId? projectId = null)
    {
        if (parts.Count == 0) throw new ArgumentException("A plan needs at least one part.", nameof(parts));
        if (parts.Count == 1) return StartCore(provider, request with { Prompt = parts[0].Prompt }, null, rigSnapshot, characterId, options, owner, projectId);
        return StartCore(provider, request, parts, rigSnapshot, characterId, options, owner, projectId);
    }

    MotionJobSnapshot StartCore(IMotionGenerationProvider provider, MotionRequest request, IReadOnlyList<MotionJobPart>? parts, Rig rigSnapshot, ObjectId characterId,
        RetargetOptions options, CommandOrigin? owner, ObjectId? projectId)
    {
        Job job;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
            Prune();
            if (_jobs.Count(j => j.State == MotionJobState.Running) >= MaxRunningJobs)
                throw new CommandException("busy", $"{MaxRunningJobs} generation jobs are already running; wait or cancel one (motion.job_cancel).");
            job = new Job
            {
                Id = ObjectId.New().ToString(), Provider = provider.Id, Prompt = request.Prompt, Seed = request.Seed,
                CharacterId = characterId.ToString(), ClipName = options.Name, RigId = rigSnapshot.Id, RigFingerprint = RigFingerprint(rigSnapshot),
                Owner = owner, ProjectId = projectId, Created = Clock.GetUtcNow(),
            };
            if (parts is not null)
                job.Segments = parts.Select((x, i) => new MotionJobSegment(i, x.Text, x.Prompt, x.DurationSeconds, provider.Id, x.Action, x.Understood, "pending", null)).ToList();
            _jobs.Add(job);
        }
        _ = Task.Run(() => RunAsync(job, provider, request, parts, rigSnapshot, options));
        return Snapshot(job);
    }

    async Task RunAsync(Job job, IMotionGenerationProvider provider, MotionRequest request, IReadOnlyList<MotionJobPart>? parts, Rig rig, RetargetOptions options)
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
            if (parts is not null)
            {
                var (clip, warnings, message) = await RunPartsAsync(job, provider, request, parts, rig, options, progress).ConfigureAwait(false);
                lock (_lock)
                {
                    if (job.State != MotionJobState.Running) return;
                    if (job.CancelRequested || job.Cts.IsCancellationRequested) { Finish(job, MotionJobState.Cancelled, null, "Cancelled."); return; }
                    job.Result = clip;
                    job.Frames = (int)clip.DurationFrames;
                    job.Warnings = warnings;
                    job.State = MotionJobState.Ready;
                    job.Stage = "ready";
                    job.Progress = 1;
                    job.CurrentSegment = null;
                    job.Message = message;
                }
                return;
            }
            var motion = await provider.GenerateAsync(request, progress, job.Cts.Token).ConfigureAwait(false);
            job.Cts.Token.ThrowIfCancellationRequested();
            progress.Report(new GenerationProgress("retarget", 1, "converting to the character"));
            var r = MotionRetargeter.Retarget(motion, rig, options.PhysicsPolish ? options with { ReduceKeys = false } : options);
            var polishWarnings = r.Warnings.ToList();
            var polished = options.PhysicsPolish ? PhysicsPolishCommands.PolishNew(r.Clip, rig, options.Cyclic, options.ReduceKeys, polishWarnings) : null;
            lock (_lock)
            {
                if (job.State != MotionJobState.Running) return;
                // A cancel that arrived while retargeting wins: the user asked for no result.
                if (job.CancelRequested || job.Cts.IsCancellationRequested) { Finish(job, MotionJobState.Cancelled, null, "Cancelled."); return; }
                job.Result = r.Clip;
                job.Frames = (int)r.Clip.DurationFrames;
                job.Warnings = polishWarnings;
                job.State = MotionJobState.Ready;
                job.Stage = "ready";
                job.Progress = 1;
                job.Message = $"{r.Clip.DurationFrames} frames, {r.MappedBones.Count} bones driven, {r.Clip.Tracks.Sum(t => t.Keys.Count)} keys" + (r.PinnedFrames > 0 ? $", feet pinned on {r.PinnedFrames} frames" : "") +
                    (polished is { Stages.Count: > 0 } ? $", physics polish: {string.Join(", ", polished.Stages)}" : "") + ".";
            }
        }
        catch (OperationCanceledException) when (job.CancelRequested || job.Cts.IsCancellationRequested)
        {
            lock (_lock) Finish(job, MotionJobState.Cancelled, null, "Cancelled.");
        }
        catch (MotionGenerationException e)
        {
            lock (_lock) Finish(job, MotionJobState.Failed, e.Code, e.Message);
        }
        catch (OutOfMemoryException)
        {
            lock (_lock) Finish(job, MotionJobState.Failed, "out_of_memory", "Blazma ran out of memory while preparing the motion.");
        }
        catch (Exception e)
        {
            lock (_lock) Finish(job, MotionJobState.Failed, "internal_error", e.Message);
        }
        finally
        {
            lock (_lock)
            {
                // Whatever happened, a job never stays Running after its worker ended.
                if (job.State == MotionJobState.Running) Finish(job, MotionJobState.Failed, "internal_error", "The generation ended without a result.");
                job.Finished ??= Clock.GetUtcNow();
            }
            Raise(job); // before waiters are released: whoever waited for the job has also seen its final event
            job.Done.TrySetResult();
        }
    }

    /// <summary>Generates every part in order, retargets each, then assembles and bakes them into one draft clip.</summary>
    async Task<(Clip Clip, List<string> Warnings, string Message)> RunPartsAsync(Job job, IMotionGenerationProvider provider, MotionRequest request,
        IReadOnlyList<MotionJobPart> parts, Rig rig, RetargetOptions options, IProgress<GenerationProgress> progress)
    {
        var ct = job.Cts.Token;
        var n = parts.Count;
        var blend = Math.Min(StepBlendSeconds, 0.5 * parts.Min(x => x.DurationSeconds));
        var clips = new List<Clip>();
        var warnings = new List<string>();
        if (request.Constraints is { Loop: true }) warnings.Add("Loop was ignored: a motion made of several steps does not loop.");
        string? lastDevice = null;
        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var part = parts[i];
            var index = i;
            SetSegment(job, i, "generating", null);
            var length = Math.Clamp(part.DurationSeconds + (i < n - 1 ? blend : 0), MotionRequest.MinDurationSeconds, MotionRequest.MaxDurationSeconds);
            var req = request with
            {
                Prompt = part.Prompt, DurationSeconds = length, Seed = unchecked(request.Seed + (ulong)i),
                Constraints = request.Constraints is { } c ? c with { Loop = false } : null,
            };
            var step = new SyncProgress(p => progress.Report(new GenerationProgress(p.Stage, (index + 0.9 * Math.Clamp(p.Fraction, 0, 1)) / n,
                $"{index + 1}/{n}: {part.Prompt}" + (p.Message is { Length: > 0 } m ? " · " + m : ""))));
            MotionResult motion;
            try { motion = await provider.GenerateAsync(req, step, ct).ConfigureAwait(false); }
            catch (MotionGenerationException e) { throw new MotionGenerationException(e.Code, $"Step {i + 1} (\"{part.Text}\"): {e.Message}", e); }
            ct.ThrowIfCancellationRequested();
            progress.Report(new GenerationProgress("retarget", (i + 0.95) / n, $"{i + 1}/{n}: converting to the character"));
            var r = MotionRetargeter.Retarget(motion, rig, options with
            {
                ClipId = ObjectId.New(), Name = StepName(i, part.Text), ReduceKeys = false, Cyclic = false, Metadata = new Dictionary<string, string>(),
            });
            clips.Add(r.Clip);
            foreach (var w in r.Warnings) if (!warnings.Contains(w)) warnings.Add(w);
            var device = motion.Metadata.GetValueOrDefault("kimodo.generatedOn");
            lastDevice = device ?? lastDevice;
            SetSegment(job, i, "done", device);
        }
        ct.ThrowIfCancellationRequested();
        progress.Report(new GenerationProgress("assemble", 1, $"joining {n} steps"));
        var clip = Assemble(rig, clips, parts, options.PhysicsPolish ? options with { ReduceKeys = false } : options, blend, warnings);
        var polished = options.PhysicsPolish ? PhysicsPolishCommands.PolishNew(clip, rig, false, options.ReduceKeys, warnings) : null;
        clip.Metadata["source.provider"] = provider.Id;
        clip.Metadata["segments"] = n.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < n; i++)
            clip.Metadata[$"segment.{i + 1}"] = $"{parts[i].Text} → {parts[i].Prompt} ({parts[i].DurationSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s)";
        if (lastDevice is not null) clip.Metadata["kimodo.generatedOn"] = lastDevice;
        var message = $"{clip.DurationFrames} frames from {n} steps: {string.Join(" → ", parts.Select(x => x.Action ?? x.Prompt))}" +
            (polished is { Stages.Count: > 0 } ? $"; physics polish: {string.Join(", ", polished.Stages)}" : "") + ".";
        return (clip, warnings, message);
    }

    internal static string StepName(int index, string text)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > 40) text = text[..39] + "…";
        return $"{index + 1}. {text}";
    }

    static bool IsGait(string? action) => action is "walk" or "run" or "sprint";

    /// <summary>The step clips placed one after another as a clip sequence (root and facing continue, cross-fades, foot phase matched
    /// between two gait steps), baked into one draft clip with the job's id, name and metadata.</summary>
    static Clip Assemble(Rig rig, List<Clip> clips, IReadOnlyList<MotionJobPart> parts, RetargetOptions options, double blend, List<string> warnings)
    {
        var p = new Project();
        p.Rigs.Add(rig);
        var ch = new Character { Name = "Generated", Rig = rig.Id };
        p.Characters.Add(ch);
        p.Clips.AddRange(clips);
        var fps = options.Fps.FramesPerSecond;
        var seq = new ClipSequence { Name = options.Name, Character = ch.Id, Fps = options.Fps, AlignRoot = true, AlignYaw = true };
        var blendFrames = (long)Math.Round(blend * fps);
        for (var i = 0; i < clips.Count; i++)
        {
            var item = new SequenceItem { Clip = clips[i].Id, BlendInFrames = i == 0 ? 0 : blendFrames, BlendCurve = BlendCurve.Smooth };
            if (i > 0)
            {
                var ev = new SequenceEvaluator(p, seq);
                item.StartFrame = Math.Max(0, (long)Math.Round(ev.EndSeconds * fps) - blendFrames);
                if (IsGait(parts[i - 1].Action) && IsGait(parts[i].Action))
                {
                    var to = Math.Min(clips[i].DurationFrames - 2, (long)Math.Round(clips[i].Fps.FramesPerSecond * 0.6));
                    if (to > 0 && ev.MatchFootPhase(item.StartFrame, clips[i], 0, to, 1) is long best) item.InFrame = best;
                }
            }
            seq.Items.Add(item);
        }
        var diags = new List<Diagnostic>();
        var clip = BakeSequence.Bake(p, seq, new SequenceEvaluator(p, seq), options.Name, options.ReduceKeys, false, diags, out _);
        foreach (var d in diags) if (!warnings.Contains(d.Message)) warnings.Add(d.Message);
        clip.Id = options.ClipId;
        clip.IsDraft = true;
        clip.Metadata = new Dictionary<string, string>(options.Metadata);
        return clip;
    }

    void SetSegment(Job job, int index, string state, string? device)
    {
        lock (_lock)
        {
            if (index >= job.Segments.Count) return;
            job.Segments[index] = job.Segments[index] with { State = state, Device = device ?? job.Segments[index].Device };
            job.CurrentSegment = state == "generating" ? index : job.CurrentSegment;
        }
        Raise(job);
    }

    /// <summary>Sets a final state if the job is still running (caller holds the lock).</summary>
    void Finish(Job job, MotionJobState state, string? code, string message)
    {
        if (job.State != MotionJobState.Running) return;
        job.State = state;
        job.Stage = state == MotionJobState.Cancelled ? "cancelled" : "failed";
        job.ErrorCode = code;
        job.Message = message;
        job.Finished = Clock.GetUtcNow();
    }

    static bool IsUser(CommandOrigin? caller) => caller is null || caller.Source == CommandSource.Ui;

    static bool CanAccess(Job j, CommandOrigin? caller) => IsUser(caller) || j.Owner is null || j.Owner == caller;

    /// <summary>Finds a job the caller may use (caller holds the lock).</summary>
    Job Find(string jobId, CommandOrigin? caller)
    {
        var job = _jobs.Find(j => j.Id == jobId) ?? throw new CommandException("not_found", $"No generation job '{jobId}'.");
        if (!CanAccess(job, caller)) throw new CommandException("not_owner", "This generation job was started by someone else; agents can only use their own jobs.");
        return job;
    }

    /// <summary>Any job (the app's own view).</summary>
    public MotionJobSnapshot? Get(string jobId)
    {
        lock (_lock) return _jobs.Find(j => j.Id == jobId) is { } j ? Snapshot(j) : null;
    }

    /// <summary>A job <paramref name="caller"/> may see; throws not_found / not_owner.</summary>
    public MotionJobSnapshot Get(string jobId, CommandOrigin? caller)
    {
        lock (_lock) return Snapshot(Find(jobId, caller));
    }

    /// <summary>All jobs (the app's own view).</summary>
    public IReadOnlyList<MotionJobSnapshot> List() => List(null);

    /// <summary>The jobs <paramref name="caller"/> may see (agents: their own).</summary>
    public IReadOnlyList<MotionJobSnapshot> List(CommandOrigin? caller)
    {
        lock (_lock)
        {
            Prune();
            return _jobs.Where(j => CanAccess(j, caller)).Select(Snapshot).ToList();
        }
    }

    /// <summary>Cancels a running job (Kimodo: kills the runner process). False if it had already finished.</summary>
    public bool Cancel(string jobId) => Cancel(jobId, null);

    /// <summary>Cancels a running job <paramref name="caller"/> may use; throws not_found / not_owner.</summary>
    public bool Cancel(string jobId, CommandOrigin? caller)
    {
        Job job;
        lock (_lock)
        {
            job = Find(jobId, caller);
            if (job.State != MotionJobState.Running) return false;
            job.CancelRequested = true; // read under the same lock when the job finishes: no Ready after a successful cancel
        }
        try { job.Cts.Cancel(); }
        catch (AggregateException) { /* a provider's cancel callback failed; the job still ends as cancelled */ }
        return true;
    }

    /// <summary>Waits until the job finishes or the timeout passes. Returns the latest snapshot.</summary>
    public MotionJobSnapshot Wait(string jobId, TimeSpan timeout) => Wait(jobId, timeout, null, CancellationToken.None);

    /// <summary>Waits until the job finishes, the timeout passes, <paramref name="ct"/> is cancelled or the service is disposed.</summary>
    public MotionJobSnapshot Wait(string jobId, TimeSpan timeout, CommandOrigin? caller, CancellationToken ct)
    {
        Job job;
        lock (_lock) job = Find(jobId, caller);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
        try { job.Done.Task.Wait(timeout, linked.Token); }
        catch (OperationCanceledException) { }
        catch (AggregateException) { }
        lock (_lock) return Snapshot(job);
    }

    /// <summary>
    /// A private copy of a Ready job's clip, for <see cref="ApplyMotionJob"/>, after checking that the caller owns the job and
    /// that it still fits <paramref name="project"/> (same project, character still there with the same, unchanged rig).
    /// </summary>
    internal Clip TakeResult(string jobId, CommandOrigin? caller, Project project)
    {
        lock (_lock)
        {
            var job = Find(jobId, caller);
            switch (job.State)
            {
                case MotionJobState.Ready: break;
                case MotionJobState.Applied:
                    throw new CommandException("job_applied", $"The job was already applied as clip {job.ClipId}. Undo that to apply it again, or duplicate the clip.");
                case MotionJobState.Running:
                    throw new CommandException("job_running", "The job is still running; check motion.job_status and apply when it is ready.");
                case MotionJobState.Cancelled:
                    throw new CommandException("job_cancelled", "The job was cancelled.");
                default:
                    throw new CommandException("job_failed", $"The job failed [{job.ErrorCode}]: {job.Message}");
            }
            if (job.ProjectId is { } pid && project.Id != pid)
                throw new CommandException("job_stale", "The job was started for another project (the project was closed or replaced); generate again.");
            var character = project.FindCharacter(ObjectId.Parse(job.CharacterId))
                ?? throw new CommandException("job_stale", "The character the motion was generated for no longer exists; generate again.");
            if (character.Rig != job.RigId)
                throw new CommandException("job_stale", $"'{character.Name}' now uses a different rig than when the motion was generated; generate again.");
            var rig = project.FindRig(job.RigId)
                ?? throw new CommandException("job_stale", "The character's rig no longer exists in the project; generate again.");
            if (RigFingerprint(rig) != job.RigFingerprint)
                throw new CommandException("job_stale", $"The rig of '{character.Name}' changed (bones, rest pose or humanoid mapping) since the motion was generated; generate again.");
            return ClipEdit.CloneClip(job.Result!);
        }
    }

    /// <summary>The clip went into the project: the job no longer holds it (the undo history does).</summary>
    internal void MarkApplied(string jobId, ObjectId clipId)
    {
        Job? job;
        lock (_lock)
        {
            job = _jobs.Find(j => j.Id == jobId);
            if (job is null || job.State is not (MotionJobState.Ready or MotionJobState.Applied)) return;
            job.State = MotionJobState.Applied;
            job.Stage = "applied";
            job.ClipId = clipId.ToString();
            job.Result = null;
            job.Finished = Clock.GetUtcNow();
        }
        Raise(job);
    }

    /// <summary>The apply was undone: the job is Ready again with the clip, so it can be applied again.</summary>
    internal void MarkUnapplied(string jobId, ObjectId clipId, Clip clip)
    {
        Job? job;
        lock (_lock)
        {
            job = _jobs.Find(j => j.Id == jobId);
            if (job is null || job.State != MotionJobState.Applied || job.ClipId != clipId.ToString()) return;
            job.State = MotionJobState.Ready;
            job.Stage = "ready";
            job.ClipId = null;
            job.Result = clip;
            job.Finished = Clock.GetUtcNow();
        }
        Raise(job);
    }

    /// <summary>Drops finished jobs older than <see cref="FinishedRetention"/> and keeps at most <see cref="MaxFinishedJobs"/> (caller holds the lock).</summary>
    void Prune()
    {
        var now = Clock.GetUtcNow();
        _jobs.RemoveAll(j => j.State != MotionJobState.Running && j.Finished is { } f && now - f > FinishedRetention);
        var finished = _jobs.Where(j => j.State != MotionJobState.Running).OrderBy(j => j.Finished ?? j.Created).ToList();
        for (var i = 0; i < finished.Count - MaxFinishedJobs; i++) _jobs.Remove(finished[i]);
    }

    MotionJobSnapshot Snapshot(Job j)
    {
        lock (_lock)
            return new MotionJobSnapshot(j.Id, j.Provider, j.Prompt, j.Seed, j.CharacterId, j.ClipName, j.State, j.Stage, j.Progress,
                j.ErrorCode, j.Message, j.Warnings.ToList(), j.ClipId, j.Frames, j.Created, j.Finished)
            {
                Owner = j.Owner?.ToString(), Segments = j.Segments.ToList(), CurrentSegment = j.State == MotionJobState.Running ? j.CurrentSegment : null,
            };
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
        List<Job> running;
        lock (_lock)
        {
            if (_disposed.IsCancellationRequested) return;
            _disposed.Cancel(); // releases blocking waits
            running = _jobs.Where(j => j.State == MotionJobState.Running).ToList();
            foreach (var j in running) j.CancelRequested = true;
        }
        foreach (var j in running)
        {
            try { j.Cts.Cancel(); }
            catch (AggregateException) { }
        }
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
            "List the motion generation providers and diagnostics: is kimodo-runner configured and found, are the model files there, CPU/RAM/GPU (best effort, offline). Never downloads anything. Agents see file names and found flags, not absolute paths.",
            CommandEffect.ReadOnly,
            S.Of(S.Bool("probe", "Also start kimodo-runner briefly and ask it for its own diagnostics (only in headless mode; can take seconds).", false)),
            a => new ListMotionProviders(jobs, (bool?)a["probe"] ?? false)));

        r.Register(new("ai.hardware",
            "This PC's hardware for local AI (offline, read-only): OS, CPU threads, RAM, graphics cards (name, vendor, VRAM, integrated or discrete; Windows from the registry, " +
            "Linux from sysfs, NVIDIA VRAM from nvidia-smi when installed), the best GPU, and which Kimodo package (light / standard / reference) and device fit it. " +
            "kimodoDevices: the devices kimodo-runner itself listed when it last started (CUDA / Vulkan graphics cards and the CPU; null until it has run), and where the last generation ran.",
            CommandEffect.ReadOnly, S.Of(),
            _ => new AiHardware(jobs)));

        r.Register(new("ai.models",
            "The downloadable Kimodo model packages (tiers) with download size, licences, RAM/VRAM needs (estimates), whether each fits this PC, the recommended one, " +
            "and whether it is installed in the configured models folder. Read-only. Downloading is NOT available to agents: only the user can start it, " +
            "from Settings → AI after seeing the size and licences (DECISIONS D43).",
            CommandEffect.ReadOnly, S.Of(),
            _ => new AiModels(jobs)));

        r.Register(new("motion.generate",
            "Start generating a motion from a text prompt for a character. Runs in the background and returns a jobId; it does not change the project. " +
            "Poll motion.job_status, then call motion.job_apply to add the result as a DRAFT clip (undoable), review it, and keep it with clip.accept_draft (or remove it with clip.delete). " +
            "Provider: kimodo (local AI, must be installed by the user) by default; 'procedural' is a built-in keyword demo (walk, run, jump, wave, squat, idle, turn around, look back, fall, die, stop, punch), not AI. Body only: fingers and face are not generated. " +
            "Multi-step prompts (\"someone is behind you, run, then die\"; Arabic or English) are split into steps by the offline planner (see motion.plan_prompt); each step is generated " +
            "separately (Kimodo gets an English caption per step) and the steps are joined into ONE draft clip with a marker per step. Pass 'segments' for an explicit plan. The result's 'plan' is the plan used.",
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
                 S.Bool("inPlace", "Remove travel (procedural only).", false),
                 S.Bool("physicsPolish", "Run motion.physics_polish on the result (default true): planted feet, nothing below the ground, ballistic jumps, smooth step transitions, falls.", false),
                 ("segments", new JsonObject
                 {
                     ["type"] = "array",
                     ["description"] = $"Optional explicit plan (at most {MotionPromptPlanner.MaxSegments} steps, in order): [{{text, durationSeconds?, caption?}}]. text may be Arabic or English " +
                         "(translated offline); caption overrides the English caption sent to Kimodo; steps without durationSeconds share the rest of durationSeconds.",
                     ["items"] = new JsonObject
                     {
                         ["type"] = "object",
                         ["properties"] = new JsonObject
                         {
                             ["text"] = new JsonObject { ["type"] = "string" }, ["durationSeconds"] = new JsonObject { ["type"] = "number" }, ["caption"] = new JsonObject { ["type"] = "string" },
                         },
                         ["required"] = new JsonArray("text"),
                     },
                 }, false)),
            a => new GenerateMotion(jobs, S.Id(a, "characterId"), S.Text(a, "prompt"), S.Number(a, "durationSeconds"),
                Args.OptLong(a, "fps"), a["seed"] is null ? null : (ulong)Math.Max(0, Args.OptLong(a, "seed") ?? 0),
                ((string?)a["provider"])?.Trim().ToLowerInvariant(), S.OptName(a, "name"),
                (bool?)a["footCleanup"] ?? true, (bool?)a["reduceKeys"] ?? true, (bool?)a["loop"] ?? false, (bool?)a["inPlace"] ?? false)
            {
                Segments = ParseSegments(a["segments"]),
                PhysicsPolish = (bool?)a["physicsPolish"] ?? true,
            }));

        r.Register(new("motion.plan_prompt",
            "Read-only: how motion.generate would split a prompt into steps, offline (no AI, no network): Arabic and English sequencing words (ثم، بعدين، وبعدها، then, after that…), " +
            "punctuation and «و»/'and' between two different actions; per-step English caption, action, length (from 'for 3 seconds' / «لمدة 3 ثواني» or per-action defaults scaled to " +
            "durationSeconds), and whether each step was understood (Arabic that is not understood should be rephrased).",
            CommandEffect.ReadOnly,
            S.Of(S.Str("prompt", "The prompt (Arabic or English).", true),
                 S.Num("durationSeconds", $"Total length to share among the steps ({MotionRequest.MinDurationSeconds}–{MotionRequest.MaxDurationSeconds}).", true)),
            a => new PlanMotionPrompt(S.Text(a, "prompt"), S.Number(a, "durationSeconds"))));

        r.Register(new("motion.job_status",
            "State of a generation job (running, ready, failed, cancelled, applied) with progress, warnings and, once applied, its clipId. Without jobId: all jobs. " +
            $"Agents see only the jobs they started. Headless only: waitSeconds blocks until the job finishes (at most {MotionJobService.MaxWaitSeconds} s per call; call again to keep waiting).",
            CommandEffect.ReadOnly,
            S.Of(S.Str("jobId", "Job id from motion.generate (omit to list all jobs).", false),
                 S.Num("waitSeconds", $"Wait up to this long for the job to finish (headless mode only, capped at {MotionJobService.MaxWaitSeconds} s; ignored in the app).", false)),
            a => new MotionJobStatus(jobs, (string?)a["jobId"], S.OptNumber(a, "waitSeconds"))));

        r.Register(new("motion.job_cancel", "Cancel a running generation job you started (stops the Kimodo runner). Does not change the project.", CommandEffect.ReadOnly,
            S.Of(S.Str("jobId", "Job id.", true)),
            a => new CancelMotionJob(jobs, S.Text(a, "jobId"))));

        r.Register(new("motion.job_apply",
            "Add a finished generation job's motion to the project as a new DRAFT clip (undoable; undo makes the job ready again). Returns clipId. Review it, edit keys, then clip.accept_draft. " +
            "Fails with job_stale when the project was replaced, or the character was removed, re-rigged or its rig/humanoid mapping changed since the job started; with job_applied when it was already applied.",
            CommandEffect.Mutating,
            S.Of(S.Str("jobId", "Job id (state ready).", true), S.Str("name", "Clip name (default: the job's name).", false)),
            a => new ApplyMotionJob(jobs, S.Text(a, "jobId"), S.OptName(a, "name"))));

        r.Register(new("clip.accept_draft", "Accept a draft clip (generated motion): it becomes a normal clip (undoable). To discard a draft use clip.delete.",
            CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Draft clip id.", true)),
            a => new AcceptDraft(S.Id(a, "clipId"))));
    }

    /// <summary>Parses motion.generate's 'segments' argument (null when absent).</summary>
    internal static List<SegmentInput>? ParseSegments(JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonArray arr || arr.Count == 0) throw new CommandException("invalid_args", "'segments' must be a non-empty array of {text, durationSeconds?, caption?}.");
        if (arr.Count > MotionPromptPlanner.MaxSegments) throw new CommandException("invalid_args", $"'segments' has more than {MotionPromptPlanner.MaxSegments} steps.");
        var list = new List<SegmentInput>();
        foreach (var item in arr)
        {
            if (item is not JsonObject o) throw new CommandException("invalid_args", "Each segment must be an object {text, durationSeconds?, caption?}.");
            foreach (var (k, _) in o)
                if (k is not ("text" or "durationSeconds" or "caption")) throw new CommandException("invalid_args", $"Unknown segment field '{k}'.");
            static string? Str(JsonNode? n, string key) =>
                n is null ? null : n is JsonValue v && v.TryGetValue<string>(out var t) ? t.Trim() : throw new CommandException("invalid_args", $"Segment '{key}' must be a string.");
            var text = Str(o["text"], "text");
            if (string.IsNullOrEmpty(text)) throw new CommandException("invalid_args", "Each segment needs a non-empty 'text'.");
            var caption = Str(o["caption"], "caption");
            if (text.Length > MotionRequest.MaxPromptLength || caption?.Length > MotionRequest.MaxPromptLength)
                throw new CommandException("invalid_args", $"A segment text is too long (max {MotionRequest.MaxPromptLength} characters).");
            double? seconds = null;
            if (o["durationSeconds"] is { } dn)
            {
                if (dn is not JsonValue dv || !SchemaValidator.TryGetNumber(dv, out var d) || !double.IsFinite(d)
                    || d < MotionRequest.MinDurationSeconds || d > MotionRequest.MaxDurationSeconds)
                    throw new CommandException("invalid_args", $"Segment durationSeconds must be between {MotionRequest.MinDurationSeconds} and {MotionRequest.MaxDurationSeconds}.");
                seconds = d;
            }
            list.Add(new SegmentInput(text, seconds, string.IsNullOrEmpty(caption) ? null : caption));
        }
        return list;
    }

    /// <summary>A plan as JSON (motion.plan_prompt, and motion.generate's 'plan').</summary>
    public static JsonObject PlanJson(MotionPlan plan) => new()
    {
        ["prompt"] = plan.Prompt,
        ["totalSeconds"] = plan.TotalSeconds,
        ["segmentCount"] = plan.Segments.Count,
        ["allUnderstood"] = plan.AllUnderstood,
        ["confidence"] = plan.Confidence,
        ["segments"] = new JsonArray(plan.Segments.Select(x => (JsonNode?)new JsonObject
        {
            ["index"] = x.Index,
            ["text"] = x.Text,
            ["caption"] = x.Caption,
            ["action"] = x.Action,
            ["durationSeconds"] = x.DurationSeconds,
            ["durationExplicit"] = x.DurationExplicit,
            ["understood"] = x.Understood,
            ["language"] = x.IsArabic ? "ar" : "en",
            ["procedural"] = x.ProceduralPrompt,
            ["unrecognized"] = new JsonArray(x.Unrecognized.Select(w => (JsonNode?)w).ToArray()),
            ["confidence"] = x.Confidence,
            ["layers"] = new JsonArray(x.Layers.Select(w => (JsonNode?)w).ToArray()),
            ["suggestions"] = new JsonArray(x.Suggestions.Select(g => (JsonNode?)new JsonObject
            {
                ["word"] = g.Word,
                ["candidates"] = new JsonArray(g.Candidates.Select(c => (JsonNode?)c).ToArray()),
                ["applied"] = g.Applied,
                ["confidence"] = g.Confidence,
                ["message"] = g.Message,
            }).ToArray()),
        }).ToArray()),
        ["warnings"] = new JsonArray(plan.Warnings.Select(w => (JsonNode?)w).ToArray()),
    };

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

    /// <summary>Path-valued detail keys: reduced to a file (or folder) name for agents.</summary>
    static readonly string[] PathDetails = { "runnerPath", "modelDirectory", "motionModel", "textModel" };

    /// <summary>
    /// Diagnostics for an agent: no absolute paths (they reveal the user's account name and folder layout). Path details become file
    /// names; every known path inside messages and runner details is replaced by its file name.
    /// </summary>
    internal static ProviderDiagnostics HidePaths(ProviderDiagnostics d, IReadOnlyList<string> knownPaths)
    {
        string? R(string? text)
        {
            if (text is null) return null;
            foreach (var p in knownPaths) text = text.Replace(p, Name(p), StringComparison.Ordinal);
            return text;
        }
        static string Name(string p) => Path.GetFileName(Path.TrimEndingDirectorySeparator(p)) is { Length: > 0 } n ? n : "(set)";
        var details = d.Details.ToDictionary(kv => kv.Key, kv => PathDetails.Contains(kv.Key) && kv.Value is { Length: > 0 } v ? Name(v) : R(kv.Value));
        var info = d.Info with { UnavailableReason = R(d.Info.UnavailableReason) };
        return new ProviderDiagnostics(info, details, d.Hardware, d.Warnings.Select(w => R(w) ?? "").ToList());
    }

    /// <summary>The runner's device list (null until it has started) and the last generation's devices, for motion.providers / ai.hardware.</summary>
    internal static void AddKimodoDevices(JsonObject o, KimodoProvider? k)
    {
        var s = k?.Settings;
        o["kimodoDevices"] = k?.RunnerDevices is { } list ? new JsonArray(list.Select(d => (JsonNode?)d.ToJson()).ToArray()) : null;
        o["kimodoDeviceMode"] = s?.Device;
        o["kimodoGpuDevice"] = s?.GpuDevice;
        o["kimodoLastDevice"] = k?.LastDeviceUse?.ToJson();
    }

    internal static JsonObject DescribeGpu(GpuInfo g) => new()
    {
        ["name"] = g.Name, ["vendor"] = g.Vendor, ["vramBytes"] = g.VramBytes, ["source"] = g.Source, ["integrated"] = g.Integrated,
    };

    internal static JsonObject DescribeRecommendation(ModelRecommendation r) => new()
    {
        ["tier"] = r.TierId, ["device"] = r.Device, ["gpu"] = r.Gpu?.Name, ["reasonEn"] = r.ReasonEn, ["reasonAr"] = r.ReasonAr,
        ["tiers"] = new JsonArray(r.Tiers.Select(t => (JsonNode?)new JsonObject
        {
            ["id"] = t.TierId, ["fits"] = t.Fits, ["gpuFits"] = t.GpuFits, ["device"] = t.Device, ["reasonEn"] = t.ReasonEn, ["reasonAr"] = t.ReasonAr,
        }).ToArray()),
    };

    internal static JsonObject DescribeHardware(HardwareReport h) => new()
    {
        ["os"] = h.Os,
        ["architecture"] = h.Architecture,
        ["logicalCpus"] = h.LogicalCpus,
        ["totalRamBytes"] = h.TotalRamBytes,
        ["gpus"] = new JsonArray(h.Gpus.Select(g => (JsonNode?)DescribeGpu(g)).ToArray()),
        ["bestGpu"] = h.BestGpu is { } b ? DescribeGpu(b) : null,
        ["notes"] = new JsonArray(h.Notes.Select(s => (JsonNode?)s).ToArray()),
    };
}

public sealed class AiHardware(MotionJobService jobs) : ICommand
{
    public string Id => "ai.hardware";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "AI hardware";

    public CommandOutcome Execute(Project project)
    {
        var hw = HardwareProbe.Probe();
        var rec = HardwareAdvisor.Recommend(hw, ModelCatalog.Default);
        var data = AICommands.DescribeHardware(hw);
        data["recommendation"] = AICommands.DescribeRecommendation(rec);
        AICommands.AddKimodoDevices(data, jobs.Kimodo);
        return new CommandOutcome(Array.Empty<Change>(), data, Array.Empty<Diagnostic>());
    }

    public IReadOnlyList<Change> Undo(Project project) => Array.Empty<Change>();
}

public sealed class AiModels(MotionJobService jobs) : IOriginAwareCommand
{
    public string Id => "ai.models";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "AI models";
    public CommandOrigin? Origin { get; set; }

    public CommandOutcome Execute(Project project)
    {
        var catalog = ModelCatalog.Default;
        var rec = HardwareAdvisor.Recommend(HardwareProbe.Probe(), catalog);
        var configured = jobs.Kimodo?.Settings.ModelDirectory;
        var dir = configured ?? ModelCatalog.DefaultModelsDirectory();
        var agent = Origin is { Source: not CommandSource.Ui };
        var tiers = new JsonArray();
        foreach (var t in catalog.Tiers)
        {
            var st = ModelCatalog.State(t, dir);
            var fit = rec.Tiers.First(f => f.TierId == t.Id);
            tiers.Add(new JsonObject
            {
                ["id"] = t.Id, ["nameEn"] = t.NameEn, ["nameAr"] = t.NameAr,
                ["motionModel"] = t.MotionModel, ["textModel"] = t.TextFile.FileName,
                ["downloadBytes"] = t.TotalBytes,
                ["minRamGiB"] = t.MinRamGiB, ["recommendedRamGiB"] = t.RecommendedRamGiB,
                ["minVramGiB"] = t.MinVramGiB, ["recommendedVramGiB"] = t.RecommendedVramGiB,
                ["qualityEn"] = t.QualityEn, ["qualityAr"] = t.QualityAr,
                ["licences"] = new JsonArray(t.LicenceIds.Select(id => (JsonNode?)DescribeLicence(catalog.Licences[id])).ToArray()),
                ["files"] = new JsonArray(t.Files.Select(f => (JsonNode?)new JsonObject
                {
                    ["name"] = f.FileName, ["bytes"] = f.Size, ["sha256"] = f.Sha256, ["url"] = f.Url.ToString(),
                }).ToArray()),
                ["fits"] = fit.Fits, ["gpuFits"] = fit.GpuFits, ["device"] = fit.Device, ["fitEn"] = fit.ReasonEn, ["fitAr"] = fit.ReasonAr,
                ["recommended"] = t.Id == rec.TierId,
                ["installed"] = st.Installed, ["bytesMissing"] = st.BytesMissing,
            });
        }
        return new CommandOutcome(Array.Empty<Change>(), new JsonObject
        {
            ["tiers"] = tiers,
            ["recommendation"] = AICommands.DescribeRecommendation(rec),
            // Agents do not see absolute paths (they reveal the account name).
            ["modelsDirectory"] = agent ? null : dir,
            ["modelsDirectoryConfigured"] = configured is not null,
            ["runner"] = new JsonObject
            {
                ["bundledFolder"] = catalog.Runner.BundledFolder, ["downloadable"] = catalog.Runner.Downloadable,
                ["noteEn"] = catalog.Runner.NoteEn, ["noteAr"] = catalog.Runner.NoteAr,
            },
            ["downloadAvailableToAgents"] = false,
            ["memoryNote"] = catalog.MemoryNote,
            ["verified"] = catalog.Verified,
        }, Array.Empty<Diagnostic>());
    }

    static JsonObject DescribeLicence(ModelLicence l) => new() { ["id"] = l.Id, ["name"] = l.Name, ["url"] = l.Url, ["summaryEn"] = l.SummaryEn, ["summaryAr"] = l.SummaryAr };

    public IReadOnlyList<Change> Undo(Project project) => Array.Empty<Change>();
}

public sealed class ListMotionProviders(MotionJobService jobs, bool probe) : IOriginAwareCommand
{
    public string Id => "motion.providers";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Motion providers";
    public CommandOrigin? Origin { get; set; }

    public CommandOutcome Execute(Project project)
    {
        var warnings = new List<Diagnostic>();
        var doProbe = probe && jobs.AllowBlockingWait;
        if (probe && !doProbe) warnings.Add(new Diagnostic("probe_skipped", "probe is only done in headless mode (it would block the app); the app shows runner diagnostics in its settings."));
        var agent = Origin is { Source: not CommandSource.Ui };
        var list = new JsonArray();
        HardwareReport? hw = null;
        foreach (var p in jobs.Providers)
        {
            // Bounded: the probe never waits behind a generation, and starting + asking the runner has its own timeouts.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var d = p.DiagnoseAsync(doProbe, cts.Token).GetAwaiter().GetResult();
            if (agent && p is KimodoProvider k) d = AICommands.HidePaths(d, k.Settings.KnownPaths());
            hw = d.Hardware;
            var entry = AICommands.DescribeProvider(d);
            if (p is KimodoProvider kp) AICommands.AddKimodoDevices(entry, kp);
            list.Add(entry);
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
    string? provider, string? name, bool footCleanup, bool reduceKeys, bool loop, bool inPlace) : IOriginAwareCommand
{
    public string Id => "motion.generate";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Generate motion";
    public CommandOrigin? Origin { get; set; }

    /// <summary>Explicit plan (agents' 'segments', the dialog's edited plan); null = plan the prompt.</summary>
    public IReadOnlyList<SegmentInput>? Segments { get; init; }

    /// <summary>Run the physics polish on the result (motion.physics_polish).</summary>
    public bool PhysicsPolish { get; init; } = true;

    public CommandOutcome Execute(Project project)
    {
        var (character, rig) = PoseCommands.Resolve(project, characterId);
        if (rig.FindByStandardName(Blazma.Rigging.HumanoidStandard.Pelvis) is null)
            throw new CommandException("not_humanoid", $"'{character.Name}' has no bone mapped to 'pelvis'. Map the humanoid bones first (rig.map_bone).");
        if (fps is > 240) throw new CommandException("invalid_args", "'fps' must be between 1 and 240.");
        var rate = fps is long f ? new FrameRate((int)f) : project.Settings.DefaultFrameRate;
        var actualSeed = seed ?? (ulong)Random.Shared.NextInt64(0, int.MaxValue);
        try { new MotionRequest(prompt, durationSeconds, rate.FramesPerSecond, actualSeed).Validate(); }
        catch (MotionGenerationException e) { throw new CommandException("invalid_args", e.Message); }
        var plan = Segments is null ? MotionPromptPlanner.Plan(prompt, durationSeconds) : MotionPromptPlanner.FromSegments(Segments, durationSeconds);
        var multi = plan.Segments.Count > 1;
        var total = multi ? plan.TotalSeconds : durationSeconds;
        var request = new MotionRequest(prompt, total, rate.FramesPerSecond, actualSeed,
            loop || inPlace ? new MotionConstraints { Loop = loop, InPlace = inPlace } : null);
        try { request.Validate(); }
        catch (MotionGenerationException e) { throw new CommandException("invalid_args", e.Message); }
        var p = jobs.Resolve(provider);
        var procedural = p.Id == ProceduralMotionProvider.ProviderId;
        string Sent(MotionSegmentPlan x) => procedural ? x.ProceduralPrompt ?? x.Caption : x.Caption;
        var parts = new List<MotionJobPart>();
        if (multi)
            parts.AddRange(plan.Segments.Select(x => new MotionJobPart(x.Text, Sent(x), x.DurationSeconds, x.Action, x.Understood)));
        else if (plan.Segments.Count == 1 && (Segments is not null || plan.Segments[0] is { IsArabic: true, Understood: true }))
            parts.Add(new MotionJobPart(plan.Segments[0].Text, Sent(plan.Segments[0]), total, plan.Segments[0].Action));
        else
            parts.Add(new MotionJobPart(prompt, prompt, total)); // a single English step: the prompt as written, as before the planner

        var clipName = name ?? (p.Id == ProceduralMotionProvider.ProviderId ? "Procedural: " : "AI: ") + Shorten(prompt, 40);
        // The worker thread gets its own copy of the rig: the project is only touched on the session thread.
        var rigCopy = JsonSerializer.Deserialize<Rig>(JsonSerializer.SerializeToUtf8Bytes(rig, ProjectArchive.JsonOptions), ProjectArchive.JsonOptions)!;
        var options = new RetargetOptions
        {
            Fps = rate,
            Name = clipName,
            FootCleanup = footCleanup,
            ReduceKeys = reduceKeys,
            Cyclic = loop,
            PhysicsPolish = PhysicsPolish,
            Metadata = new Dictionary<string, string>
            {
                ["provider"] = p.Id,
                ["prompt"] = prompt,
                ["seed"] = actualSeed.ToString(CultureInfo.InvariantCulture),
                ["durationSeconds"] = total.ToString(CultureInfo.InvariantCulture),
                ["character"] = character.Name,
            },
        };
        if (parts[0].Prompt != prompt && !multi) options.Metadata["caption"] = parts[0].Prompt;
        var job = jobs.StartPlan(p, request, parts, rigCopy, characterId, options, Origin ?? CommandOrigin.Ui, project.Id);
        var data = job.ToJson();
        data["plan"] = AICommands.PlanJson(plan);
        data["next"] = "Poll motion.job_status {jobId}; when state is 'ready' call motion.job_apply {jobId}.";
        var warnings = plan.Warnings.Select(w => new Diagnostic("plan", w)).ToList();
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

public sealed class MotionJobStatus(MotionJobService jobs, string? jobId, double? waitSeconds) : IOriginAwareCommand
{
    public string Id => "motion.job_status";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Generation job status";
    public CommandOrigin? Origin { get; set; }

    public CommandOutcome Execute(Project project)
    {
        var warnings = new List<Diagnostic>();
        if (jobId is null)
            return new CommandOutcome(Array.Empty<Change>(), new JsonObject
            {
                ["jobs"] = new JsonArray(jobs.List(Origin).Select(j => (JsonNode?)j.ToJson()).ToArray()),
            });
        var snap = jobs.Get(jobId, Origin);
        if (waitSeconds is > 0 && !snap.IsFinished)
        {
            if (jobs.AllowBlockingWait)
            {
                var cap = jobs.MaxBlockingWait.TotalSeconds;
                if (waitSeconds.Value > cap)
                    warnings.Add(new Diagnostic("wait_capped", $"waitSeconds is capped at {cap:0.#} s per call; call motion.job_status again to keep waiting."));
                snap = jobs.Wait(jobId, TimeSpan.FromSeconds(Math.Min(waitSeconds.Value, cap)), Origin, CancellationToken.None);
            }
            else warnings.Add(new Diagnostic("wait_ignored", "waitSeconds is ignored while the app is open; poll again later."));
        }
        return new CommandOutcome(Array.Empty<Change>(), snap.ToJson(), warnings);
    }

    public IReadOnlyList<Change> Undo(Project project) => Array.Empty<Change>();
}

public sealed class CancelMotionJob(MotionJobService jobs, string jobId) : IOriginAwareCommand
{
    public string Id => "motion.job_cancel";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Cancel generation";
    public CommandOrigin? Origin { get; set; }

    public CommandOutcome Execute(Project project)
    {
        var cancelled = jobs.Cancel(jobId, Origin);
        var snap = jobs.Get(jobId, Origin).ToJson();
        snap["cancelRequested"] = cancelled;
        return new CommandOutcome(Array.Empty<Change>(), snap);
    }

    public IReadOnlyList<Change> Undo(Project project) => Array.Empty<Change>();
}

/// <summary>
/// Adds a finished job's clip as a draft. The clip id is fixed per command so redo re-adds the same clip. The clip moves from the
/// job to this command (the undo history): undo removes it from the project and gives it back to the job, which is Ready again
/// and can be applied again; redo applies it again.
/// </summary>
public sealed class ApplyMotionJob(MotionJobService jobs, string jobId, string? name) : IOriginAwareCommand
{
    readonly ObjectId _clipId = ObjectId.New();
    public long HistoryBytes => _clip is null ? 256 : ClipDelta.EstimateBytes(_clip);
    Clip? _clip;
    string _jobClipName = "";
    bool _jobClipDraft;

    public string Id => "motion.job_apply";
    public CommandEffect Effect => CommandEffect.Mutating;
    public string Label => "Add generated motion";
    public CommandOrigin? Origin { get; set; }

    public CommandOutcome Execute(Project project)
    {
        var warnings = new List<Diagnostic>();
        if (_clip is null)
        {
            var clip = jobs.TakeResult(jobId, Origin, project);
            _jobClipName = clip.Name; // to give the job its own clip back on undo (without a second copy in the history)
            _jobClipDraft = clip.IsDraft;
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
            // Where Kimodo ran (null for other providers): mode gpu | hybrid | cpu and a readable summary.
            ["device"] = _clip.Metadata.GetValueOrDefault("kimodo.device"),
            ["motionDevice"] = _clip.Metadata.GetValueOrDefault("kimodo.motionDevice"),
            ["generatedOn"] = _clip.Metadata.GetValueOrDefault("kimodo.generatedOn"),
            ["durationFrames"] = _clip.DurationFrames,
            ["fps"] = _clip.Fps.ToString(),
            ["trackCount"] = _clip.Tracks.Count,
        }, warnings);
    }

    public IReadOnlyList<Change> Undo(Project project)
    {
        project.Clips.RemoveAll(c => c.Id == _clipId);
        if (_clip is not null)
        {
            var back = ClipEdit.CloneClip(_clip);
            back.Name = _jobClipName;
            back.IsDraft = _jobClipDraft;
            back.Metadata.Remove("jobId");
            jobs.MarkUnapplied(jobId, _clipId, back);
        }
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

public sealed class PlanMotionPrompt(string prompt, double durationSeconds) : ICommand
{
    public string Id => "motion.plan_prompt";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Plan a motion prompt";

    public CommandOutcome Execute(Project project)
    {
        if (prompt.Length > MotionRequest.MaxPromptLength) throw new CommandException("invalid_args", $"The prompt is too long (max {MotionRequest.MaxPromptLength} characters).");
        if (!double.IsFinite(durationSeconds) || durationSeconds < MotionRequest.MinDurationSeconds || durationSeconds > MotionRequest.MaxDurationSeconds)
            throw new CommandException("invalid_args", $"durationSeconds must be between {MotionRequest.MinDurationSeconds} and {MotionRequest.MaxDurationSeconds}.");
        var plan = MotionPromptPlanner.Plan(prompt, durationSeconds);
        return new CommandOutcome(Array.Empty<Change>(), AICommands.PlanJson(plan), plan.Warnings.Select(w => new Diagnostic("plan", w)).ToList());
    }

    public IReadOnlyList<Change> Undo(Project project) => Array.Empty<Change>();
}
