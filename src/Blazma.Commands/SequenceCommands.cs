using System.Text.Json.Nodes;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Physics;
using Blazma.Rigging;

namespace Blazma.Commands;

/// <summary>Phase 8: clip sequences (blending/transitions) on a character track, their evaluation and baking.</summary>
public static class SequenceCommands
{
    const string CurveHelp = "smooth (default; no velocity jump) | linear | ease_in | ease_out.";

    public static void Register(CommandRegistry r)
    {
        r.Register(new("sequence.create", "Create an empty clip sequence on a character's track (clips played one after another with blended transitions). Returns sequenceId.",
            CommandEffect.Mutating,
            S.Of(S.Str("characterId", "Character whose rig the clips animate.", true), S.Str("name", "Sequence name.", true),
                 S.Int("fps", "Timeline frame rate (default: the project frame rate).", false, 1),
                 S.Bool("alignRoot", "Each clip continues from where the previous one is (default true).", false),
                 S.Bool("alignYaw", "Also align the facing direction (default true).", false)),
            a => new CreateSequence(S.Id(a, "characterId"), S.Name(a, "name"), Args.OptLong(a, "fps"), (bool?)a["alignRoot"] ?? true, (bool?)a["alignYaw"] ?? true)));

        r.Register(new("sequence.add_clip",
            "Add a clip to a sequence. By default it starts where the last item ends minus its blend, so it fades in over the end of the previous clip. " +
            "matchFootPhase picks the inFrame whose foot phase best matches the sequence at the start (less foot sliding in the transition). Returns itemId, startFrame, inFrame.",
            CommandEffect.Mutating,
            S.Of(S.Str("sequenceId", "Sequence id.", true), S.Str("clipId", "Clip id (animates the sequence's character rig).", true),
                 S.Int("startFrame", "Sequence frame where the item starts.", false), S.Int("inFrame", "First clip frame played (default 0).", false),
                 S.Int("outFrame", "Clip frame where a pass ends (exclusive; default: clip length).", false, 1),
                 S.Num("speed", "Playback speed (default 1).", false), S.Int("loops", "Times the in–out range repeats (default 1).", false, 1),
                 S.Int("blendFrames", "Frames to fade in over the previous items (default 0 for the first item, 0.3 s otherwise).", false),
                 S.Str("blendCurve", CurveHelp, false), S.Bool("matchFootPhase", "Choose inFrame to match the foot phase (default false).", false)),
            a => new AddSequenceItem(S.Id(a, "sequenceId"), S.Id(a, "clipId"), Long(a, "startFrame"), Long(a, "inFrame"), Long(a, "outFrame"),
                Speed(a), Loops(a), Long(a, "blendFrames"), Curve(a), (bool?)a["matchFootPhase"] ?? false)));

        r.Register(new("sequence.set_blend", "Set an item's fade-in length (frames) and/or curve.", CommandEffect.Mutating,
            S.Of(S.Str("sequenceId", "Sequence id.", true), S.Str("itemId", "Item id.", true), S.Int("blendFrames", "Fade-in frames.", false), S.Str("blendCurve", CurveHelp, false)),
            a =>
            {
                if (a["blendFrames"] is null && a["blendCurve"] is null) throw new CommandException("invalid_args", "Give 'blendFrames' and/or 'blendCurve'.");
                return new UpdateSequenceItem(S.Id(a, "sequenceId"), S.Id(a, "itemId"), "sequence.set_blend", "Set blend",
                    null, null, null, null, null, Long(a, "blendFrames"), Curve(a));
            }));

        r.Register(new("sequence.update_item", "Change an item's timing: startFrame, inFrame, outFrame, speed and/or loops (at least one).", CommandEffect.Mutating,
            S.Of(S.Str("sequenceId", "Sequence id.", true), S.Str("itemId", "Item id.", true), S.Int("startFrame", "Sequence start frame.", false),
                 S.Int("inFrame", "First clip frame.", false), S.Int("outFrame", "Clip frame where a pass ends (exclusive).", false, 1),
                 S.Num("speed", "Playback speed.", false), S.Int("loops", "Repeats.", false, 1)),
            a =>
            {
                if (new[] { "startFrame", "inFrame", "outFrame", "speed", "loops" }.All(k => a[k] is null))
                    throw new CommandException("invalid_args", "Give at least one of startFrame, inFrame, outFrame, speed, loops.");
                return new UpdateSequenceItem(S.Id(a, "sequenceId"), S.Id(a, "itemId"), "sequence.update_item", "Edit sequence item",
                    Long(a, "startFrame"), Long(a, "inFrame"), Long(a, "outFrame"), Speed(a), Loops(a), null, null);
            }));

        r.Register(new("sequence.update", "Rename a sequence and/or change its root alignment options.", CommandEffect.Mutating,
            S.Of(S.Str("sequenceId", "Sequence id.", true), S.Str("name", "New name.", false), S.Bool("alignRoot", "Root alignment.", false), S.Bool("alignYaw", "Facing alignment.", false)),
            a => new UpdateSequence(S.Id(a, "sequenceId"), S.OptName(a, "name"), (bool?)a["alignRoot"], (bool?)a["alignYaw"])));

        r.Register(new("sequence.remove", "Remove an item from a sequence, or the whole sequence when itemId is omitted (undoable). Clips are not deleted.", CommandEffect.Destructive,
            S.Of(S.Str("sequenceId", "Sequence id.", true), S.Str("itemId", "Item id; omit to remove the sequence.", false)),
            a => new RemoveSequence(S.Id(a, "sequenceId"), S.OptId(a, "itemId"))));

        r.Register(new("sequence.inspect", "Items of a sequence with their timing (start/end frames, blend, alignment) and the sequence length. Does not change anything.", CommandEffect.ReadOnly,
            S.Of(S.Str("sequenceId", "Sequence id.", true)),
            a => new InspectSequence(S.Id(a, "sequenceId"))));

        r.Register(new("sequence.evaluate", "The blended pose of a sequence at a frame and which items contribute with what weight. Does not change anything.", CommandEffect.ReadOnly,
            S.Of(S.Str("sequenceId", "Sequence id.", true), S.Num("frame", "Sequence frame (may be fractional).", true)),
            a => new EvaluateSequence(S.Id(a, "sequenceId"), S.Number(a, "frame"))));

        r.Register(new("sequence.bake", "Bake a sequence into a NEW normal clip (undoable) that plays exactly like the sequence; export that clip to export the sequence. Returns clipId.",
            CommandEffect.Mutating,
            S.Of(S.Str("sequenceId", "Sequence id.", true), S.Str("name", "Clip name (default: the sequence name).", false),
                 S.Bool("reduceKeys", "Remove keys linear interpolation reproduces within 0.05° / 0.2 mm (default true).", false),
                 S.Bool("footLock", "Lock foot contacts of the baked clip (default false).", false)),
            a => new BakeSequence(S.Id(a, "sequenceId"), S.OptName(a, "name"), (bool?)a["reduceKeys"] ?? true, (bool?)a["footLock"] ?? false)));
    }

    static long? Long(JsonObject a, string key)
    {
        var v = Args.OptLong(a, key);
        return v is < 0 or > ClipSequence.MaxFrame ? throw new CommandException("invalid_args", $"'{key}' must be between 0 and {ClipSequence.MaxFrame:N0}.") : v;
    }

    static double? Speed(JsonObject a)
    {
        var s = S.OptNumber(a, "speed");
        return s is null ? null : !(s >= ClipSequence.MinSpeed && s <= ClipSequence.MaxSpeed)
            ? throw new CommandException("invalid_args", $"'speed' must be between {ClipSequence.MinSpeed} and {ClipSequence.MaxSpeed}.") : s;
    }

    static int? Loops(JsonObject a)
    {
        var l = Args.OptLong(a, "loops");
        return l is null ? null : l is < 1 or > ClipSequence.MaxLoops ? throw new CommandException("invalid_args", $"'loops' must be between 1 and {ClipSequence.MaxLoops}.") : (int)l;
    }

    static BlendCurve? Curve(JsonObject a) => ((string?)a["blendCurve"])?.Trim().ToLowerInvariant() switch
    {
        null => null,
        "smooth" => BlendCurve.Smooth,
        "linear" => BlendCurve.Linear,
        "ease_in" => BlendCurve.EaseIn,
        "ease_out" => BlendCurve.EaseOut,
        _ => throw new CommandException("invalid_args", "'blendCurve' must be " + CurveHelp),
    };

    internal static string CurveName(BlendCurve c) => c switch
    {
        BlendCurve.Linear => "linear", BlendCurve.EaseIn => "ease_in", BlendCurve.EaseOut => "ease_out", _ => "smooth",
    };

    internal static ClipSequence Require(Project p, ObjectId id) =>
        p.FindSequence(id) ?? throw new CommandException("not_found", $"Sequence '{id}' does not exist.");

    internal static SequenceItem RequireItem(ClipSequence s, ObjectId id) =>
        s.Items.Find(i => i.Id == id) ?? throw new CommandException("not_found", $"Item '{id}' does not exist in sequence '{s.Name}'.");

    internal static SequenceEvaluator Evaluator(Project p, ClipSequence s)
    {
        try { return new SequenceEvaluator(p, s); }
        catch (SequenceException e) { throw new CommandException(e.Code, e.Message); }
    }

    /// <summary>Checks an item against its clip and the sequence's character.</summary>
    internal static void Validate(Project p, ClipSequence s, SequenceItem item)
    {
        var clip = AnimationEditCommands.RequireClip(p, item.Clip);
        var (character, rig) = PoseCommands.Resolve(p, s.Character);
        if (!clip.Skeleton.IsEmpty && clip.Skeleton != rig.Id)
            throw new CommandException("rig_mismatch", $"Clip '{clip.Name}' animates another rig than '{character.Name}'.");
        var length = clip.DurationFrames;
        if (item.InFrame >= length) throw new CommandException("invalid_args", $"'inFrame' {item.InFrame} is outside clip '{clip.Name}' (length {length}).");
        if (item.OutFrame is long o && (o <= item.InFrame || o > length))
            throw new CommandException("invalid_args", $"'outFrame' must be after 'inFrame' and at most the clip length ({length}).");
        if (!ClipSequence.ItemInLimits(item))
            throw new CommandException("too_large", $"The item's timing is outside the limits (start/in/out/blend at most {ClipSequence.MaxFrame:N0} frames).");
    }

    /// <summary>
    /// After an edit: the sequence must still be evaluable within the limits (no item skipped because more than
    /// <see cref="SequenceEvaluator.MaxBlending"/> items would blend at once, blends within the preparation budget). Throws
    /// too_many_blends / too_large; SceneEdit then restores the sequence. Builds only the evaluator's timing (no poses).
    /// </summary>
    internal static void CheckPlayable(Project p, ClipSequence s)
    {
        var ev = Evaluator(p, s);
        if (ev.SkippedForBlending.Count > 0)
            throw new CommandException("too_many_blends",
                $"More than {SequenceEvaluator.MaxBlending} items would blend at the same time; start the item later or shorten the blends.");
    }

    internal static SequenceItem Copy(SequenceItem i) => new()
    {
        Id = i.Id, Clip = i.Clip, StartFrame = i.StartFrame, InFrame = i.InFrame, OutFrame = i.OutFrame, Speed = i.Speed, Loops = i.Loops,
        BlendInFrames = i.BlendInFrames, BlendCurve = i.BlendCurve,
    };

    internal static void Sort(ClipSequence s) => s.Items = s.Items.Select((it, i) => (it, i)).OrderBy(x => x.it.StartFrame).ThenBy(x => x.i).Select(x => x.it).ToList();

    internal static Change Changed(ObjectId id) => new(Change.SequenceChanged, id.ToString());
}

public sealed class CreateSequence(ObjectId characterId, string name, long? fps, bool alignRoot, bool alignYaw) : SceneEdit
{
    readonly ObjectId _id = ObjectId.New();
    public override string Id => "sequence.create";
    public override string Label => "Create sequence";
    protected override SceneParts Touches => SceneParts.Sequences;
    protected override IReadOnlyList<Change> Changes => new[] { SequenceCommands.Changed(_id) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        PoseCommands.Resolve(p, characterId);
        if (fps is > ClipSequence.MaxFps) throw new CommandException("invalid_args", $"'fps' must be between 1 and {ClipSequence.MaxFps}.");
        p.Sequences.Add(new ClipSequence
        {
            Id = _id, Name = name, Character = characterId, AlignRoot = alignRoot, AlignYaw = alignYaw,
            Fps = fps is long f ? new FrameRate((int)f) : p.Settings.DefaultFrameRate,
        });
        return new JsonObject { ["sequenceId"] = _id.ToString() };
    }
}

public sealed class AddSequenceItem(ObjectId sequenceId, ObjectId clipId, long? start, long? inFrame, long? outFrame, double? speed, int? loops,
    long? blend, BlendCurve? curve, bool matchFootPhase) : SceneEdit
{
    readonly ObjectId _itemId = ObjectId.New();
    public override string Id => "sequence.add_clip";
    public override string Label => "Add clip to sequence";
    protected override SceneParts Touches => SceneParts.Sequences;
    protected override IReadOnlyList<Change> Changes => new[] { SequenceCommands.Changed(sequenceId) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        var s = SequenceCommands.Require(p, sequenceId);
        var clip = AnimationEditCommands.RequireClip(p, clipId);
        if (s.Items.Count >= ClipSequence.MaxItems)
            throw new CommandException("too_large", $"A sequence holds at most {ClipSequence.MaxItems} items.");
        var fps = s.Fps.FramesPerSecond;
        SequenceEvaluator? current = s.Items.Count > 0 ? SequenceCommands.Evaluator(p, s) : null;
        var blendFrames = blend ?? (current is null ? 0 : (long)Math.Round(0.3 * fps));
        var startFrame = start ?? (current is null ? 0 : Math.Max(0, (long)Math.Round(current.EndSeconds * fps) - blendFrames));
        var item = new SequenceItem
        {
            Id = _itemId, Clip = clipId, StartFrame = startFrame, InFrame = inFrame ?? 0, OutFrame = outFrame, Speed = speed ?? 1, Loops = loops ?? 1,
            BlendInFrames = blendFrames, BlendCurve = curve ?? BlendCurve.Smooth,
        };
        SequenceCommands.Validate(p, s, item);
        if (matchFootPhase)
        {
            if (current is null) warnings.Add(new Diagnostic("nothing_to_match", "matchFootPhase: the sequence was empty; inFrame kept."));
            else
            {
                var outF = item.OutFrame ?? SequenceEvaluator.DefaultOut(clip).ToFrame(clip.Fps);
                // Search one pass of the clip (or its first second for long clips).
                var to = Math.Min(outF - 1, item.InFrame + Math.Max(1, (long)Math.Round(clip.Fps.FramesPerSecond * 1.5)));
                // The item's speed only: MatchFootPhase compares rates per second, so differing frame rates need no factor.
                if (current.MatchFootPhase(startFrame, clip, item.InFrame, to, item.Speed) is long best)
                {
                    item.InFrame = best;
                    if (item.OutFrame is long o && o <= best) item.OutFrame = null;
                }
                else warnings.Add(new Diagnostic("no_feet", "matchFootPhase: the rig has no humanoid-mapped feet; inFrame kept."));
            }
        }
        s.Items.Add(item);
        SequenceCommands.Sort(s);
        SequenceCommands.CheckPlayable(p, s);
        return new JsonObject { ["itemId"] = _itemId.ToString(), ["startFrame"] = item.StartFrame, ["inFrame"] = item.InFrame, ["blendFrames"] = item.BlendInFrames };
    }
}

public sealed class UpdateSequenceItem(ObjectId sequenceId, ObjectId itemId, string id, string label, long? start, long? inFrame, long? outFrame,
    double? speed, int? loops, long? blend, BlendCurve? curve) : SceneEdit
{
    public override string Id => id;
    public override string Label => label;
    protected override SceneParts Touches => SceneParts.Sequences;
    protected override IReadOnlyList<Change> Changes => new[] { SequenceCommands.Changed(sequenceId) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        var s = SequenceCommands.Require(p, sequenceId);
        var item = SequenceCommands.RequireItem(s, itemId);
        var copy = SequenceCommands.Copy(item);
        if (start is long st) copy.StartFrame = st;
        if (inFrame is long i) copy.InFrame = i;
        if (outFrame is long o) copy.OutFrame = o;
        if (speed is double sp) copy.Speed = sp;
        if (loops is int l) copy.Loops = l;
        if (blend is long b) copy.BlendInFrames = b;
        if (curve is BlendCurve c) copy.BlendCurve = c;
        SequenceCommands.Validate(p, s, copy);
        s.Items[s.Items.IndexOf(item)] = copy;
        SequenceCommands.Sort(s);
        SequenceCommands.CheckPlayable(p, s);
        return new JsonObject { ["itemId"] = itemId.ToString() };
    }

    public override bool TryMerge(ICommand next) =>
        next is UpdateSequenceItem u && u.Id == Id && u.SequenceId == sequenceId && u.ItemId == itemId && AbsorbAfterState(u);

    internal ObjectId SequenceId => sequenceId;
    internal ObjectId ItemId => itemId;
}

public sealed class UpdateSequence(ObjectId sequenceId, string? name, bool? alignRoot, bool? alignYaw) : SceneEdit
{
    public override string Id => "sequence.update";
    public override string Label => "Edit sequence";
    protected override SceneParts Touches => SceneParts.Sequences;
    protected override IReadOnlyList<Change> Changes => new[] { SequenceCommands.Changed(sequenceId) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        if (name is null && alignRoot is null && alignYaw is null) throw new CommandException("invalid_args", "Give at least one of name, alignRoot, alignYaw.");
        var s = SequenceCommands.Require(p, sequenceId);
        if (name is not null) s.Name = name;
        if (alignRoot is bool r) s.AlignRoot = r;
        if (alignYaw is bool y) s.AlignYaw = y;
        return new JsonObject { ["sequenceId"] = sequenceId.ToString() };
    }
}

public sealed class RemoveSequence(ObjectId sequenceId, ObjectId? itemId) : SceneEdit
{
    public override string Id => "sequence.remove";
    public override CommandEffect Effect => CommandEffect.Destructive;
    public override string Label => itemId is null ? "Delete sequence" : "Remove sequence item";
    protected override SceneParts Touches => SceneParts.Sequences;
    protected override IReadOnlyList<Change> Changes => new[] { SequenceCommands.Changed(sequenceId) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        var s = SequenceCommands.Require(p, sequenceId);
        if (itemId is { } id) s.Items.Remove(SequenceCommands.RequireItem(s, id));
        else p.Sequences.Remove(s);
        return null;
    }
}

public sealed class InspectSequence(ObjectId sequenceId) : ICommand
{
    public string Id => "sequence.inspect";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Inspect sequence";

    public CommandOutcome Execute(Project p)
    {
        var s = SequenceCommands.Require(p, sequenceId);
        SequenceEvaluator? ev = null;
        var warnings = new List<Diagnostic>();
        try { ev = new SequenceEvaluator(p, s); warnings.AddRange(ev.Warnings.Select(w => new Diagnostic("sequence", w))); }
        catch (SequenceException e) { warnings.Add(new Diagnostic(e.Code, e.Message)); }
        return new CommandOutcome(Array.Empty<Change>(), new JsonObject
        {
            ["sequenceId"] = s.Id.ToString(),
            ["name"] = s.Name,
            ["characterId"] = s.Character.ToString(),
            ["fps"] = s.Fps.ToString(),
            ["alignRoot"] = s.AlignRoot,
            ["alignYaw"] = s.AlignYaw,
            ["lastFrame"] = ev?.LastFrame,
            ["items"] = new JsonArray(s.Items.Select(i =>
            {
                var range = ev?.RangeOf(i.Id);
                var align = ev?.AlignmentOf(i.Id);
                return (JsonNode?)new JsonObject
                {
                    ["itemId"] = i.Id.ToString(),
                    ["clipId"] = i.Clip.ToString(),
                    ["clip"] = p.FindClip(i.Clip)?.Name,
                    ["startFrame"] = i.StartFrame,
                    ["endFrame"] = range is { } r ? Math.Round(r.End, 4) : null,
                    ["inFrame"] = i.InFrame,
                    ["outFrame"] = i.OutFrame,
                    ["speed"] = i.Speed,
                    ["loops"] = i.Loops,
                    ["blendFrames"] = i.BlendInFrames,
                    ["blendCurve"] = SequenceCommands.CurveName(i.BlendCurve),
                    ["alignOffset"] = align is { } a ? PhysicsCommands.V(a.Translation) : null,
                    ["alignYawDegrees"] = align is { } b ? Math.Round(2 * Math.Atan2(b.Rotation.Y, b.Rotation.W) * 180 / Math.PI, 4) : null,
                };
            }).ToArray()),
        }, warnings);
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}

public sealed class EvaluateSequence(ObjectId sequenceId, double frame) : ICommand
{
    public string Id => "sequence.evaluate";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Evaluate sequence";

    public CommandOutcome Execute(Project p)
    {
        if (frame is < 0 or > 10_000_000) throw new CommandException("invalid_args", "'frame' must be between 0 and 10,000,000.");
        var s = SequenceCommands.Require(p, sequenceId);
        var ev = SequenceCommands.Evaluator(p, s);
        var sample = ev.Evaluate(frame);
        var rig = ev.Rig;
        var model = new RigEvaluator(rig).Model(sample.Pose);
        var bones = new JsonArray();
        for (var i = 0; i < rig.Bones.Count; i++)
            bones.Add(new JsonObject
            {
                ["name"] = rig.Bones[i].Name,
                ["rotation"] = InspectRig.Vec(sample.Pose.LocalOf(rig.Bones[i]).Rotation.ToEulerXYZ() * (180 / Math.PI)),
                ["position"] = InspectRig.Vec(model[i].Translation),
            });
        return new CommandOutcome(Array.Empty<Change>(), new JsonObject
        {
            ["frame"] = frame,
            ["lastFrame"] = ev.LastFrame,
            ["active"] = new JsonArray(sample.Active.Select(c => (JsonNode?)new JsonObject
            {
                ["itemId"] = c.ItemId.ToString(), ["clipId"] = c.ClipId.ToString(), ["weight"] = Math.Round(c.Weight, 6),
                ["clipFrame"] = Math.Round(c.ClipFrame, 4), ["cycle"] = c.Cycle,
            }).ToArray()),
            ["bones"] = bones,
            ["pose"] = PoseCommands.SerializePose(sample.Pose),
        }, ev.Warnings.Select(w => new Diagnostic("sequence", w)).ToList());
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}

/// <summary>
/// Samples the sequence on every timeline frame (0 … last, inclusive) and writes the local bone transforms as linear keys
/// (reduced within tolerance) into a new clip on the character's rig. Pinned IK is already solved into the bone rotations, so
/// the clip reproduces the sequence with plain FK. Item starts become markers; item events are copied for every pass.
/// </summary>
public sealed class BakeSequence(ObjectId sequenceId, string? name, bool reduceKeys, bool footLock) : AddClipCommand
{
    public const long MaxSamples = 2_000_000;
    public override string Id => "sequence.bake";
    public override string Label => "Bake sequence";

    protected override Clip Build(Project p, List<Diagnostic> warnings, out JsonObject data)
    {
        var s = SequenceCommands.Require(p, sequenceId);
        var ev = SequenceCommands.Evaluator(p, s);
        warnings.AddRange(ev.Warnings.Select(w => new Diagnostic("sequence", w)));
        var rig = ev.Rig;
        var last = Math.Max(1, ev.LastFrame);
        if ((last + 1.0) * Math.Max(1, rig.Bones.Count) > MaxSamples)
            throw new CommandException("too_large", $"{last + 1} frames × {rig.Bones.Count} bones is more than {MaxSamples:N0} samples.");
        if (ev.BakeWork > SequenceEvaluator.MaxBakeWork)
            throw new CommandException("too_large", $"Baking would evaluate {ev.BakeWork:0.#e0} bones (frames × blending items × bones; at most {SequenceEvaluator.MaxBakeWork:0.#e0}).");
        var rev = new RigEvaluator(rig);
        var locals = new Transform[last + 1][];
        for (var f = 0; f <= last; f++) locals[f] = rev.Locals(ev.Evaluate(f).Pose);
        var clip = new Clip
        {
            Name = name ?? s.Name, Fps = s.Fps, Duration = Flicks.FromFrame(last, s.Fps), Skeleton = rig.Id,
            Metadata = new Dictionary<string, string> { ["source"] = "sequence", ["sequenceId"] = s.Id.ToString() },
        };
        var keys = BakedTracks.AddBoneTracks(clip, rig, locals, b => BakedTracks.RotationMoves(rig, locals, b), b => BakedTracks.TranslationMoves(rig, locals, b),
            reduceKeys, 0.05, 0.0002);

        // Props the items' clips animate: their blended values (moved with the items when free), as prop tracks.
        var propCount = 0;
        foreach (var prop in p.Props)
        {
            if (!s.Items.Any(i => p.FindClip(i.Clip) is { } c && PropEvaluator.Animates(c, prop))) continue;
            var values = new Transform[last + 1];
            for (var f = 0; f <= last; f++) values[f] = ev.PropLocal(prop, f) ?? prop.Local;
            keys += BakedTracks.AddPropTracks(clip, prop.Id, values, reduceKeys, 0.05, 0.0002);
            propCount++;
        }

        foreach (var item in s.Items)
        {
            if (ev.RangeOf(item.Id) is not { } range || p.FindClip(item.Clip) is not { } c) continue;
            var startFrame = (long)Math.Round(range.Start);
            if (startFrame <= last) clip.Markers.Add(new Marker { Name = c.Name, Time = Flicks.FromFrame(startFrame, s.Fps) });
            // Events where the evaluator actually plays them (speed, loops, phase and wrap of looping clips).
            foreach (var e in c.Events)
                foreach (var at in ev.FramesPlaying(item.Id, e.Time))
                {
                    var frame = (long)Math.Round(at);
                    if (frame <= last) clip.Events.Add(new AnimationEvent { Name = e.Name, Data = new Dictionary<string, string>(e.Data), Time = Flicks.FromFrame(frame, s.Fps) });
                }
        }
        clip.Markers = clip.Markers.OrderBy(m => m.Time.Value).ToList();
        clip.Events = clip.Events.OrderBy(e => e.Time.Value).ToList();
        data = new JsonObject { ["sequenceId"] = s.Id.ToString(), ["frames"] = last + 1, ["keyCount"] = keys, ["props"] = propCount };
        if (footLock)
        {
            var spans = FootLocker.DetectContacts(clip, rig, new FootLockOptions());
            if (spans.Count == 0) warnings.Add(new Diagnostic("no_contacts", "footLock: no foot contact found in the baked clip."));
            else
            {
                try { data["footLock"] = FootLockWriter.Apply(clip, rig, spans, new FootLockOptions(), warnings); }
                catch (CommandException e) when (e.Code == "no_leg_ik") { warnings.Add(new Diagnostic(e.Code, e.Message)); }
            }
        }
        return clip;
    }
}
