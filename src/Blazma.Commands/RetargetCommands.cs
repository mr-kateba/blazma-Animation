using System.Text.Json.Nodes;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Physics;
using Blazma.Rigging;
using Blazma.Rigging.Retargeting;

namespace Blazma.Commands;

/// <summary>Phase 8: clip retargeting between characters, foot contacts and foot locking.</summary>
public static class RetargetCommands
{
    public static void Register(CommandRegistry r)
    {
        r.Register(new("clip.retarget",
            "Retarget a clip from one character to another (rig → rig through the humanoid maps; differently proportioned or imported characters). " +
            "Creates a NEW normal clip (undoable): mapped bones point the same way in the world, hip/root travel is scaled by the leg-length ratio, animated IK targets/poles are mapped, " +
            "unmapped bones stay at rest (warning). With footLock (default true) the source's foot contacts are kept planted on the target. Returns clipId and a report.",
            CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Source clip id.", true), S.Str("fromCharacterId", "Character the clip animates now.", true),
                 S.Str("toCharacterId", "Character to retarget onto (its rig must be humanoid-mapped).", true), S.Str("name", "Name of the new clip.", false),
                 S.Bool("footLock", "Keep the source's foot contacts planted (pinned IK, default true).", false),
                 S.Bool("reduceKeys", "Remove keys linear interpolation reproduces within 0.05° / 0.2 mm (default true).", false)),
            a => new RetargetClip(S.Id(a, "clipId"), S.Id(a, "fromCharacterId"), S.Id(a, "toCharacterId"), S.OptName(a, "name"),
                (bool?)a["footLock"] ?? true, (bool?)a["reduceKeys"] ?? true)));

        r.Register(new("clip.contacts",
            "Detect foot-ground contacts in a clip (same rules as physics.analyze: a sole point within 'threshold' of the ground and slower than 'maxSpeed'). Returns spans per foot. Does not change anything.",
            CommandEffect.ReadOnly,
            ContactSchema(),
            a => new ClipContacts(S.Id(a, "clipId"), S.OptId(a, "characterId"), Options(a), Frame(a, "startFrame"), Frame(a, "endFrame"))));

        r.Register(new("clip.foot_lock",
            "Lock feet during ground contacts: detects contacts, then keys the leg IK targets pinned on one anchor per contact (with knee poles from the motion) and eases on/off over blendFrames. " +
            "Frames outside contact ± blendFrames keep exactly their original pose. Undoable. Returns spans, maxAnchorError (m) and maxUntouchedChange.",
            CommandEffect.Mutating,
            ContactSchema(S.Int("blendFrames", "Frames to ease onto/off each anchor (default 3).", false)),
            a => new FootLockClip(S.Id(a, "clipId"), S.OptId(a, "characterId"), Options(a), Frame(a, "startFrame"), Frame(a, "endFrame"))));
    }

    static JsonObject ContactSchema(params (string, JsonObject, bool)[] extra) =>
        S.Of(new[]
        {
            S.Str("clipId", "Clip id.", true), S.Str("characterId", "Character (default: the first one using the clip's rig).", false),
            S.Num("threshold", "Contact height above the ground in metres (default 0.03).", false),
            S.Num("maxSpeed", "Maximum sole speed in m/s for a contact (default 0.35).", false),
            S.Int("minFrames", "Shortest contact in frames (default 3).", false, 1),
            S.Int("startFrame", "First frame to search (default 0).", false), S.Int("endFrame", "Last frame to search (default: clip end).", false),
        }.Concat(extra).ToArray());

    static long? Frame(JsonObject a, string key)
    {
        var v = Args.OptLong(a, key);
        return v is < 0 or > 10_000_000 ? throw new CommandException("invalid_args", $"'{key}' must be between 0 and 10,000,000.") : v;
    }

    static FootLockOptions Options(JsonObject a)
    {
        var o = new FootLockOptions();
        if (S.OptNumber(a, "threshold") is { } h)
        {
            if (h is <= 0 or > 1) throw new CommandException("invalid_args", "'threshold' must be between 0 and 1 m.");
            o = o with { ContactHeight = h };
        }
        if (S.OptNumber(a, "maxSpeed") is { } v)
        {
            if (v is <= 0 or > 100) throw new CommandException("invalid_args", "'maxSpeed' must be between 0 and 100 m/s.");
            o = o with { ContactSpeed = v };
        }
        if (Args.OptLong(a, "minFrames") is { } m) o = o with { MinFrames = (int)Math.Clamp(m, 1, 10_000) };
        if (a.ContainsKey("blendFrames") && Args.OptLong(a, "blendFrames") is { } b)
        {
            if (b > 1000) throw new CommandException("invalid_args", "'blendFrames' must be at most 1000.");
            o = o with { BlendFrames = (int)b };
        }
        return o;
    }

    internal static JsonArray SpansJson(IEnumerable<FootContactSpan> spans) =>
        new(spans.Select(s => (JsonNode?)new JsonObject
        {
            ["side"] = s.Side == Side.Left ? "left" : "right",
            ["startFrame"] = s.Start,
            ["endFrame"] = s.End,
            ["frames"] = s.End - s.Start + 1,
        }).ToArray());

    internal static CommandException Map(RetargetException e) => new(e.Code, e.Message);
}

/// <summary>
/// Keys a foot lock into a clip (docs/ARCHITECTURE.md §8.2): per locked foot the leg IK target's translation, rotation and pinned
/// flag, and the knee pole, on the planned frames only; every other frame keeps exactly its value (<see cref="KeyBake"/>).
/// </summary>
internal static class FootLockWriter
{
    /// <param name="rangeStart">First frame contacts were searched in (default 0): nothing before it is written, and a contact
    /// starting there may have started earlier (partial: no ease at that edge).</param>
    /// <param name="rangeEnd">Last frame contacts were searched in (default: the clip's last analysed frame): nothing after it is
    /// written (except the end key when it is the clip's last analysed frame), and a contact reaching it is partial.</param>
    public static JsonObject Apply(Clip clip, Rig rig, IReadOnlyList<FootContactSpan> spans, FootLockOptions options, List<Diagnostic> warnings,
        long? rangeStart = null, long? rangeEnd = null)
    {
        var original = ClipEdit.CloneClip(clip);
        var clipLast = Math.Max(1, clip.DurationFrames);
        var analyzedLast = BalanceAnalyzer.LastFrame(clip);
        var first = Math.Max(0, rangeStart ?? 0);
        var cutEnd = rangeEnd is long re && re < analyzedLast;
        var lastFrame = cutEnd ? rangeEnd!.Value : clipLast;
        // Contact detection stops one frame before the clip end; a contact that reaches it continues to the end key.
        if (!cutEnd) spans = spans.Select(s => s.End >= analyzedLast ? s with { End = clipLast } : s).ToList();
        var notes = new List<string>();
        var plan = FootLocker.Plan(original, rig, spans, options, lastFrame, notes, first, cutStart: first > 0, cutEnd: cutEnd);
        warnings.AddRange(notes.Select(n => new Diagnostic("no_leg_ik", n)));
        if (plan.Count == 0) throw new CommandException("no_leg_ik", "No contact can be locked: the feet have no leg IK chains (rig IK setup).");

        var ev = new RigEvaluator(rig);
        var feet = FootModel.For(rig, ev);
        var edits = new List<TrackEdit>();
        foreach (var group in plan.GroupBy(x => x.Foot))
        {
            var chain = feet.Feet[group.Key].Chain!;
            var target = rig.GetControl(chain.Target);
            TrackEdit Edit(RigControl control, string channel)
            {
                var existing = clip.Tracks.Find(t => t.Target.Id == control.Id && t.Channel == channel);
                if (existing is { Locked: true }) throw new CommandException("track_locked", $"The {channel} track of '{control.Name}' is locked; unlock it to lock the foot.");
                if (existing is { Muted: true }) throw new CommandException("track_muted", $"The {channel} track of '{control.Name}' is muted; unmute it to lock the foot.");
                var e = new TrackEdit(existing, TargetKind.Control, control.Id, channel, KeyCommands.RestValue(rig, TargetKind.Control, control.Id, channel)!);
                edits.Add(e);
                return e;
            }
            var tr = Edit(target, TrackChannels.Translation);
            var rot = Edit(target, TrackChannels.Rotation);
            var pin = Edit(target, TrackChannels.Pinned);
            var pole = !chain.Pole.IsEmpty && rig.FindControl(chain.Pole) is { } pc && group.Any(x => x.PoleStored is not null) ? Edit(pc, TrackChannels.Translation) : null;
            foreach (var x in group)
            {
                var p = x.Target.Translation;
                var q = x.Target.Rotation;
                tr.Values[x.Frame] = new[] { p.X, p.Y, p.Z };
                rot.Values[x.Frame] = new[] { q.X, q.Y, q.Z, q.W };
                pin.Values[x.Frame] = new[] { 1.0 };
                if (pole is not null && x.PoleStored is { } ps) pole.Values[x.Frame] = new[] { ps.X, ps.Y, ps.Z };
            }
        }
        foreach (var e in edits)
        {
            foreach (var bad in e.Values.Where(kv => !TrackEdit.Finite(kv.Value)).Select(kv => kv.Key).ToList()) e.Values.Remove(bad);
            e.Plan(clip, clipLast);
        }
        var total = edits.Sum(e => KeyBake.Count(e.Regions));
        if (total > PhysicsCommands.MaxKeys)
            throw new CommandException("too_large", $"This foot lock would write {total} keys (at most {PhysicsCommands.MaxKeys}); lock a shorter range.");
        if (clip.Skeleton.IsEmpty) clip.Skeleton = rig.Id;
        var keys = edits.Sum(e => e.Write(clip));

        // Verify what playback/export will give: anchors held, every frame outside the plan unchanged.
        var planned = plan.Select(x => x.Frame).ToHashSet();
        var regionFrames = edits.SelectMany(e => e.Regions).ToList();
        var from = Math.Max(0, regionFrames.Min(r => r.From) - 1);
        var to = Math.Min(clipLast, regionFrames.Max(r => r.To) + 1);
        double maxAnchor = 0, maxUntouched = 0;
        var anchors = plan.Where(x => x.InContact).GroupBy(x => x.Frame).ToDictionary(g => g.Key, g => g.ToList());
        for (var f = from; f <= to; f++)
        {
            var t = Flicks.FromFrame(f, clip.Fps);
            var after = ev.Model(ClipEvaluator.Evaluate(clip, rig, t));
            if (anchors.TryGetValue(f, out var list))
                foreach (var x in list)
                    maxAnchor = Math.Max(maxAnchor, (after[feet.Feet[x.Foot].FootBone].Translation - x.Anchor).Length());
            if (planned.Contains(f)) continue;
            var before = ev.Model(ClipEvaluator.Evaluate(original, rig, t));
            for (var b = 0; b < after.Length; b++) maxUntouched = Math.Max(maxUntouched, (after[b].Translation - before[b].Translation).Length());
        }
        var anchorBySpan = plan.Where(x => x.InContact).GroupBy(x => (x.Foot, x.Anchor)).Select(g => (g.Key.Foot, g.Key.Anchor, Start: g.Min(x => x.Frame), End: g.Max(x => x.Frame))).ToList();
        return new JsonObject
        {
            ["spans"] = new JsonArray(anchorBySpan.OrderBy(s => s.Start).Select(s => (JsonNode?)new JsonObject
            {
                ["side"] = feet.Feet[s.Foot].Side == Side.Left ? "left" : "right",
                ["startFrame"] = s.Start,
                ["endFrame"] = s.End,
                ["anchor"] = PhysicsCommands.V(s.Anchor),
            }).ToArray()),
            ["lockedFrames"] = planned.Count,
            ["changedStartFrame"] = planned.Min(),
            ["changedEndFrame"] = planned.Max(),
            ["keys"] = keys,
            ["maxAnchorError"] = PhysicsCommands.M(maxAnchor),
            ["maxUntouchedChange"] = PhysicsCommands.M(maxUntouched),
        };
    }
}

public sealed class RetargetClip(ObjectId clipId, ObjectId fromCharacterId, ObjectId toCharacterId, string? name, bool footLock, bool reduceKeys) : AddClipCommand
{
    public override string Id => "clip.retarget";
    public override string Label => "Retarget clip";

    protected override Clip Build(Project p, List<Diagnostic> warnings, out JsonObject data)
    {
        var source = AnimationEditCommands.RequireClip(p, clipId);
        var (from, fromRig) = PoseCommands.Resolve(p, fromCharacterId);
        var (to, toRig) = PoseCommands.Resolve(p, toCharacterId);
        if (!source.Skeleton.IsEmpty && source.Skeleton != fromRig.Id)
            throw new CommandException("rig_mismatch", $"Clip '{source.Name}' animates another rig than '{from.Name}'.");
        if (source.DurationFrames * Math.Max(1, toRig.Bones.Count) > ClipRetargeter.MaxSamples)
            throw new CommandException("too_large", "The clip is too long to retarget at once.");
        ClipRetargetResult result;
        try
        {
            result = ClipRetargeter.Retarget(source, fromRig, toRig, new ClipRetargetOptions
            {
                Name = name ?? $"{source.Name} ({to.Name})", ReduceKeys = reduceKeys,
            });
        }
        catch (RetargetException e) { throw RetargetCommands.Map(e); }
        var clip = result.Clip;
        warnings.AddRange(result.Warnings.Select(w => new Diagnostic("retarget", w)));
        PropTrackWarnings(p, clip, to.Id, warnings);
        data = new JsonObject
        {
            ["sourceClipId"] = source.Id.ToString(),
            ["scale"] = PhysicsCommands.R(result.Scale),
            ["mappedBones"] = result.MappedBones.Count,
            ["unmappedBones"] = new JsonArray(result.UnmappedBones.Select(b => (JsonNode?)b).ToArray()),
            ["mappedControls"] = result.MappedControls,
            ["keyCount"] = result.KeyCount,
        };
        if (footLock)
            data["footLock"] = LockSourceContacts(source, fromRig, clip, toRig, 1, warnings);
        return clip;
    }

    /// <summary>
    /// Prop tracks are copied unchanged (props do not depend on the skeleton). A prop attached to another character than
    /// <paramref name="target"/> keeps its attachment: its keys stay offsets from that character's bone, so on the target it shows at
    /// its free transform until it is attached there (prop.attach) — say so.
    /// </summary>
    internal static void PropTrackWarnings(Project p, Clip clip, ObjectId target, List<Diagnostic> warnings)
    {
        var props = clip.Tracks.Where(t => t.Target.Kind == TargetKind.Prop).Select(t => t.Target.Id).Distinct().ToList();
        if (props.Count > 0) warnings.Add(new Diagnostic("prop_tracks", $"{props.Count} prop(s) keep their keys in the new clip."));
        foreach (var id in props)
            if (p.FindProp(id) is { Attachment: { } a } prop && a.Character != target)
                warnings.Add(new Diagnostic("prop_attached_elsewhere",
                    $"Prop '{prop.Name}' is attached to another character; its keys are offsets from that character's bone. Attach it to this character (prop.attach) to carry it."));
    }

    /// <summary>Contacts of the source clip (on its rig), locked on the new clip (frames scaled by <paramref name="timeScale"/>).</summary>
    internal static JsonNode? LockSourceContacts(Clip source, Rig fromRig, Clip clip, Rig toRig, double timeScale, List<Diagnostic> warnings)
    {
        var srcFeet = FootModel.For(fromRig, new RigEvaluator(fromRig));
        var dstFeet = FootModel.For(toRig, new RigEvaluator(toRig));
        if (srcFeet.Feet.Count == 0 || dstFeet.Feet.Count == 0)
        {
            warnings.Add(new Diagnostic("no_feet", "Foot locking skipped: one of the characters has no humanoid-mapped feet."));
            return null;
        }
        var options = new FootLockOptions();
        var spans = new List<FootContactSpan>();
        foreach (var s in FootLocker.DetectContacts(source, fromRig, options))
        {
            var target = dstFeet.Feet.Select((f, i) => (f, i)).FirstOrDefault(x => x.f.Side == s.Side);
            if (target.f is null) continue;
            spans.Add(new FootContactSpan(target.i, s.Side, (long)Math.Round(s.Start * timeScale), (long)Math.Round(s.End * timeScale)));
        }
        if (spans.Count == 0)
        {
            warnings.Add(new Diagnostic("no_contacts", "Foot locking: the source clip has no foot contacts."));
            return null;
        }
        try { return FootLockWriter.Apply(clip, toRig, spans, options, warnings); }
        catch (CommandException e) when (e.Code is "no_leg_ik")
        {
            warnings.Add(new Diagnostic(e.Code, "Foot locking skipped: " + e.Message));
            return null;
        }
    }
}

public sealed class ClipContacts(ObjectId clipId, ObjectId? characterId, FootLockOptions options, long? start, long? end) : ICommand
{
    public string Id => "clip.contacts";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Foot contacts";

    public CommandOutcome Execute(Project p)
    {
        var (clip, rig) = ExportCommands.Resolve(p, clipId, characterId);
        var (s, e) = PhysicsCommands.Range(clip, start, end);
        var an = new BalanceAnalyzer(rig);
        PhysicsCommands.RequireFeet(an);
        var spans = FootLocker.DetectContacts(clip, rig, options, s, e);
        return new CommandOutcome(Array.Empty<Change>(), new JsonObject
        {
            ["startFrame"] = s, ["endFrame"] = e,
            ["spans"] = RetargetCommands.SpansJson(spans),
        });
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}

public sealed class FootLockClip(ObjectId clipId, ObjectId? characterId, FootLockOptions options, long? start, long? end) : ClipEdit(clipId)
{
    public override string Id => "clip.foot_lock";
    public override string Label => "Foot lock";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        var (_, rig) = ExportCommands.Resolve(p, ClipId, characterId);
        var (s, e) = PhysicsCommands.Range(clip, start, end);
        if (e - s + 1 > PhysicsCommands.MaxEditRange)
            throw new CommandException("too_large", $"Foot locking handles at most {PhysicsCommands.MaxEditRange} frames per call; use startFrame/endFrame.");
        var an = new BalanceAnalyzer(rig);
        PhysicsCommands.RequireFeet(an);
        var spans = FootLocker.DetectContacts(clip, rig, options, s, e);
        if (spans.Count == 0) throw new CommandException("no_contacts", "No foot contact found in that range (try a larger 'threshold' or 'maxSpeed').");
        var data = FootLockWriter.Apply(clip, rig, spans, options, warnings, s, e);
        data["clipId"] = ClipId.ToString();
        return data;
    }
}
