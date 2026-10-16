using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;

namespace Blazma.Commands;

/// <summary>Phase 3 commands: keyframes, interpolation and evaluating clips.</summary>
public static class KeyCommands
{
    public static void Register(CommandRegistry r)
    {
        r.Register(new("key.set", "Record the character's current pose as keys at a frame (replaces existing keys there). By default every bone and IK control; pass 'bones' to key only some. Binds the clip to the character's rig on first use.", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("characterId", "Character id.", true), S.Num("frame", "Frame number (≥ 0).", true),
                 S.Str("bones", "Comma-separated bone/control names or ids; omit for all.", false),
                 S.Str("interpolation", "step | linear | cubic (default cubic for new keys).", false)),
            a => new SetKeys(S.Id(a, "clipId"), S.Id(a, "characterId"), Frame(a, "frame"), Names(a), Interp(a, false))));

        r.Register(new("key.delete", "Delete keys at the given frames (optionally only for some bones/controls).", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("frames", "Comma-separated frame numbers, e.g. \"0,12\".", true),
                 S.Str("bones", "Comma-separated bone/control names or ids; omit for all.", false)),
            a => new DeleteKeys(S.Id(a, "clipId"), Frames(a), Names(a))));

        r.Register(new("key.move", "Shift keys at the given frames by 'delta' frames (keys they land on are replaced).", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("frames", "Comma-separated frame numbers.", true),
                 S.Num("delta", "Frames to move (may be negative).", true), S.Str("bones", "Comma-separated names or ids; omit for all.", false)),
            a => new MoveKeys(S.Id(a, "clipId"), Frames(a), (long)Math.Round(S.Number(a, "delta")), Names(a))));

        r.Register(new("key.set_interpolation", "Set how motion leaves the given keys: step, linear or cubic.", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("interpolation", "step | linear | cubic.", true),
                 S.Str("frames", "Comma-separated frames; omit for all keys.", false), S.Str("bones", "Names or ids; omit for all.", false)),
            a => new SetKeyInterpolation(S.Id(a, "clipId"), Interp(a, true)!.Value, a["frames"] is null ? null : Frames(a), Names(a))));

        r.Register(new("anim.evaluate", "The pose a clip produces at a frame (does not change anything). Returns per-bone rotation/position and the pose JSON.", CommandEffect.ReadOnly,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("characterId", "Character id.", true), S.Num("frame", "Frame (may be fractional).", true)),
            a => new EvaluateClip(S.Id(a, "clipId"), S.Id(a, "characterId"), S.Number(a, "frame"))));

        r.Register(new("anim.pose_from_frame", "Set the character's working pose to what the clip gives at a frame (undoable).", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("characterId", "Character id.", true), S.Num("frame", "Frame.", true)),
            a => new PoseFromFrame(S.Id(a, "characterId"), S.Id(a, "clipId"), S.Number(a, "frame"))));
    }

    static long Frame(JsonObject a, string key)
    {
        var f = S.Number(a, key);
        if (f < 0 || f != Math.Floor(f) || f > 10_000_000) throw new CommandException("invalid_args", $"'{key}' must be a whole number ≥ 0.");
        return (long)f;
    }

    static IReadOnlyList<long> Frames(JsonObject a)
    {
        var text = S.Text(a, "frames");
        var list = new List<long>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var f) || f < 0)
                throw new CommandException("invalid_args", $"'{part}' is not a valid frame number.");
            list.Add(f);
        }
        if (list.Count == 0) throw new CommandException("invalid_args", "'frames' is empty.");
        return list.Distinct().ToList();
    }

    static IReadOnlyList<string>? Names(JsonObject a) =>
        ((string?)a["bones"])?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } n ? n : null;

    static Interpolation? Interp(JsonObject a, bool required)
    {
        var s = ((string?)a["interpolation"])?.Trim().ToLowerInvariant();
        if (s is null)
            return required ? throw new CommandException("invalid_args", "'interpolation' is required.") : null;
        return s switch
        {
            "step" => Interpolation.Step,
            "linear" => Interpolation.Linear,
            "cubic" => Interpolation.Cubic,
            _ => throw new CommandException("invalid_args", "'interpolation' must be step, linear or cubic."),
        };
    }

    /// <summary>Resolve a name/id filter to track targets of a rig; null filter = everything.</summary>
    internal static HashSet<ObjectId>? ResolveTargets(Rig? rig, IReadOnlyList<string>? names)
    {
        if (names is null) return null;
        var set = new HashSet<ObjectId>();
        foreach (var n in names)
        {
            if (ObjectId.TryParse(n, out var id) && (rig is null || rig.FindBone(id) is not null || rig.FindControl(id) is not null)) { set.Add(id); continue; }
            if (rig?.FindBoneByName(n) is { } b) { set.Add(b.Id); continue; }
            if (rig?.Controls.Find(c => c.Name == n) is { } c) { set.Add(c.Id); continue; }
            throw new CommandException("not_found", $"'{n}' is not a bone or control of this clip's rig.");
        }
        return set;
    }

    internal static void InsertKey(Track track, Keyframe key)
    {
        var i = track.Keys.FindIndex(k => k.Time >= key.Time);
        if (i < 0) track.Keys.Add(key);
        else if (track.Keys[i].Time == key.Time) track.Keys[i] = key;
        else track.Keys.Insert(i, key);
    }

    internal static Change ClipChanged(ObjectId id) => new(Change.ClipChanged, id.ToString());

    /// <summary>
    /// Finds or creates the track for (target, channel). A newly created track is back-filled
    /// with the channel's rest value at <paramref name="existingTimes"/> (the clip's key times
    /// before this edit), because that is what the channel was at those keys before it existed.
    /// Without this, the first key of a new track would be held back to frame 0.
    /// </summary>
    internal static Track GetOrCreateTrack(Clip clip, Rig? rig, TargetKind kind, ObjectId id, string channel,
        string? group, IReadOnlyCollection<Flicks> existingTimes)
    {
        var track = clip.Tracks.Find(t => t.Target.Id == id && t.Channel == channel);
        if (track is not null) return track;
        track = new Track { Target = new TrackTarget { Kind = kind, Id = id }, Channel = channel, Group = group };
        clip.Tracks.Add(track);
        if (RestValue(rig, kind, id, channel) is not { } rest) return track;
        foreach (var time in existingTimes.OrderBy(t => t.Value))
        {
            var neighbour = clip.Tracks.Where(t => t.Target.Id == id && t != track)
                .SelectMany(t => t.Keys).FirstOrDefault(k => k.Time == time);
            track.Keys.Add(new Keyframe
            {
                Time = time,
                Value = (double[])rest.Clone(),
                Interp = channel == TrackChannels.Pinned ? Interpolation.Step : neighbour?.Interp ?? Interpolation.Cubic,
            });
        }
        return track;
    }

    static double[]? RestValue(Rig? rig, TargetKind kind, ObjectId id, string channel)
    {
        Transform? rest = kind switch
        {
            TargetKind.Bone => rig?.FindBone(id)?.Rest,
            TargetKind.Control => rig?.FindControl(id)?.Rest,
            _ => null,
        };
        if (rest is not { } t) return null;
        return channel switch
        {
            TrackChannels.Translation => new[] { t.Translation.X, t.Translation.Y, t.Translation.Z },
            TrackChannels.Rotation => new[] { t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W },
            TrackChannels.Scale => kind == TargetKind.Control ? new[] { 1.0, 1, 1 } : new[] { t.Scale.X, t.Scale.Y, t.Scale.Z },
            TrackChannels.Pinned => new[] { 0.0 },
            _ => null,
        };
    }

    /// <summary>All distinct key times in the clip.</summary>
    internal static HashSet<Flicks> KeyTimes(Clip clip) => clip.Tracks.SelectMany(t => t.Keys).Select(k => k.Time).ToHashSet();
}

/// <summary>
/// Base for edits to one clip. The first Execute computes the result; undo/redo swap whole-clip
/// snapshots, so redo is exact even if the pose it was recorded from has changed since.
/// Note: the Clip object in Project.Clips is replaced on every edit, undo and redo — UI code must
/// look clips up by id after a clip.changed notification instead of keeping references.
/// </summary>
public abstract class ClipEdit(ObjectId clipId) : ICommand
{
    Clip? _before;
    Clip? _after;
    protected ObjectId ClipId { get; } = clipId;

    public abstract string Id { get; }
    public CommandEffect Effect => CommandEffect.Mutating;
    public abstract string Label { get; }

    public CommandOutcome Execute(Project p)
    {
        var index = p.Clips.FindIndex(c => c.Id == ClipId);
        if (index < 0) throw new CommandException("not_found", $"Clip '{ClipId}' does not exist.");
        if (_after is not null)
        {
            p.Clips[index] = CloneClip(_after);
            return new(new[] { KeyCommands.ClipChanged(ClipId) });
        }
        var before = CloneClip(p.Clips[index]);
        var work = CloneClip(before);
        var warnings = new List<Diagnostic>();
        var data = Apply(p, work, warnings);
        work.Tracks.RemoveAll(t => t.Keys.Count == 0);
        _before = before;
        _after = CloneClip(work);
        p.Clips[index] = work;
        return new(new[] { KeyCommands.ClipChanged(ClipId) }, data, warnings);
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        var index = p.Clips.FindIndex(c => c.Id == ClipId);
        if (index < 0) throw new InvalidOperationException($"Clip '{ClipId}' disappeared before undo.");
        p.Clips[index] = CloneClip(_before!);
        return new[] { KeyCommands.ClipChanged(ClipId) };
    }

    /// <summary>Edit <paramref name="clip"/> (a private copy). Throw CommandException to abort.</summary>
    protected abstract JsonNode? Apply(Project project, Clip clip, List<Diagnostic> warnings);

    public static Clip CloneClip(Clip c) =>
        JsonSerializer.Deserialize<Clip>(JsonSerializer.SerializeToUtf8Bytes(c, ProjectArchive.JsonOptions), ProjectArchive.JsonOptions)!;
}

public sealed class SetKeys(ObjectId clipId, ObjectId characterId, long frame, IReadOnlyList<string>? names, Interpolation? interp) : ClipEdit(clipId)
{
    public override string Id => "key.set";
    public override string Label => "Set key";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        var (character, rig) = PoseCommands.Resolve(p, characterId);
        if (clip.Skeleton.IsEmpty) clip.Skeleton = rig.Id;
        else if (clip.Skeleton != rig.Id)
            throw new CommandException("rig_mismatch", $"Clip '{clip.Name}' animates a different rig than '{character.Name}'.");

        var only = KeyCommands.ResolveTargets(rig, names);
        var time = Flicks.FromFrame(frame, clip.Fps);
        var pose = character.Pose;
        var count = 0;
        var existingTimes = KeyCommands.KeyTimes(clip);
        existingTimes.Remove(time);

        foreach (var bone in rig.Bones)
        {
            if (only is not null && !only.Contains(bone.Id)) continue;
            var t = pose.LocalOf(bone);
            Key(TargetKind.Bone, bone.Id, TrackChannels.Rotation, Q(t.Rotation), always: true);
            Key(TargetKind.Bone, bone.Id, TrackChannels.Translation, V(t.Translation), always: !t.Translation.ApproxEquals(bone.Rest.Translation, 1e-12));
            Key(TargetKind.Bone, bone.Id, TrackChannels.Scale, V(t.Scale), always: !t.Scale.ApproxEquals(Vec3.One, 1e-12));
        }
        foreach (var control in rig.Controls)
        {
            if (only is not null && !only.Contains(control.Id)) continue;
            var state = PoseSolver.StateOf(rig, pose, control.Id);
            Key(TargetKind.Control, control.Id, TrackChannels.Translation, V(state.Transform.Translation), always: true);
            if (control.Kind == ControlKind.IkTarget)
            {
                Key(TargetKind.Control, control.Id, TrackChannels.Rotation, Q(state.Transform.Rotation), always: true);
                Key(TargetKind.Control, control.Id, TrackChannels.Pinned, new[] { state.Pinned ? 1.0 : 0.0 }, always: true);
            }
        }

        if (frame >= clip.DurationFrames)
        {
            clip.Duration = Flicks.FromFrame(frame + 1, clip.Fps);
            warnings.Add(new Diagnostic("clip_extended", $"The clip was extended to {frame + 1} frames."));
        }
        return new JsonObject { ["frame"] = frame, ["keys"] = count };

        // Only create a track when the value is not the default, but always update existing tracks.
        void Key(TargetKind kind, ObjectId target, string channel, double[] value, bool always)
        {
            var exists = clip.Tracks.Exists(t => t.Target.Id == target && t.Channel == channel);
            if (!exists && !always) return;
            var track = KeyCommands.GetOrCreateTrack(clip, rig, kind, target, channel, GroupOf(rig, kind, target), existingTimes);
            if (track.Locked) { warnings.Add(new Diagnostic("track_locked", $"A locked track was skipped.")); return; }
            var existing = track.Keys.Find(k => k.Time == time);
            KeyCommands.InsertKey(track, new Keyframe
            {
                Time = time,
                Value = value,
                Interp = channel == TrackChannels.Pinned ? Interpolation.Step : interp ?? existing?.Interp ?? Interpolation.Cubic,
            });
            count++;
        }
    }

    static string GroupOf(Rig rig, TargetKind kind, ObjectId id) =>
        kind == TargetKind.Control ? "IK" : rig.StandardNameOf(id) switch
        {
            null => "Other",
            var s when s.StartsWith("thigh") || s.StartsWith("calf") || s.StartsWith("foot") || s.StartsWith("toe") => "Legs",
            var s when s.StartsWith("clavicle") || s.Contains("arm") || s.StartsWith("hand") => "Arms",
            _ => "Body",
        };

    static double[] Q(Quat q) => new[] { q.X, q.Y, q.Z, q.W };
    static double[] V(Vec3 v) => new[] { v.X, v.Y, v.Z };
}

public sealed class DeleteKeys(ObjectId clipId, IReadOnlyList<long> frames, IReadOnlyList<string>? names) : ClipEdit(clipId)
{
    public override string Id => "key.delete";
    public override string Label => "Delete keys";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        var only = KeyCommands.ResolveTargets(p.FindRig(clip.Skeleton), names);
        var times = frames.Select(f => Flicks.FromFrame(f, clip.Fps)).ToHashSet();
        var removed = 0;
        foreach (var track in clip.Tracks)
        {
            if (only is not null && !only.Contains(track.Target.Id)) continue;
            if (track.Locked)
            {
                if (track.Keys.Exists(k => times.Contains(k.Time))) warnings.Add(new Diagnostic("track_locked", "Keys on locked tracks were not deleted."));
                continue;
            }
            removed += track.Keys.RemoveAll(k => times.Contains(k.Time));
        }
        if (removed == 0) throw new CommandException("not_found", "No keys at those frames.");
        return new JsonObject { ["removed"] = removed };
    }
}

public sealed class MoveKeys(ObjectId clipId, IReadOnlyList<long> frames, long delta, IReadOnlyList<string>? names) : ClipEdit(clipId)
{
    public override string Id => "key.move";
    public override string Label => "Move keys";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        if (delta == 0) throw new CommandException("invalid_args", "'delta' must not be 0.");
        if (frames.Min() + delta < 0) throw new CommandException("invalid_args", "Keys cannot move before frame 0.");
        var only = KeyCommands.ResolveTargets(p.FindRig(clip.Skeleton), names);
        var times = frames.Select(f => Flicks.FromFrame(f, clip.Fps)).ToHashSet();
        var moved = 0;
        var replaced = 0;
        foreach (var track in clip.Tracks)
        {
            if (only is not null && !only.Contains(track.Target.Id)) continue;
            if (track.Locked)
            {
                if (track.Keys.Exists(k => times.Contains(k.Time))) warnings.Add(new Diagnostic("track_locked", "Keys on locked tracks were not moved."));
                continue;
            }
            var moving = track.Keys.Where(k => times.Contains(k.Time)).ToList();
            if (moving.Count == 0) continue;
            track.Keys.RemoveAll(k => times.Contains(k.Time));
            foreach (var k in moving)
            {
                // Re-derive from frame numbers so keys stay on the frame grid at any frame rate.
                var target = Flicks.FromFrame(k.Time.ToFrame(clip.Fps) + delta, clip.Fps);
                if (track.Keys.Exists(x => x.Time == target)) replaced++;
                k.Time = target;
                KeyCommands.InsertKey(track, k);
                moved++;
            }
        }
        if (moved == 0) throw new CommandException("not_found", "No keys at those frames.");
        if (replaced > 0) warnings.Add(new Diagnostic("keys_replaced", $"{replaced} existing keys were replaced."));
        var last = clip.Tracks.SelectMany(t => t.Keys).Select(k => k.Time.ToFrame(clip.Fps)).DefaultIfEmpty(0).Max();
        if (last >= clip.DurationFrames) clip.Duration = Flicks.FromFrame(last + 1, clip.Fps);
        return new JsonObject { ["moved"] = moved };
    }
}

public sealed class SetKeyInterpolation(ObjectId clipId, Interpolation interp, IReadOnlyList<long>? frames, IReadOnlyList<string>? names) : ClipEdit(clipId)
{
    public override string Id => "key.set_interpolation";
    public override string Label => "Set interpolation";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        var only = KeyCommands.ResolveTargets(p.FindRig(clip.Skeleton), names);
        var times = frames?.Select(f => Flicks.FromFrame(f, clip.Fps)).ToHashSet();
        var changed = 0;
        foreach (var track in clip.Tracks)
        {
            if (track.Channel == TrackChannels.Pinned || track.Locked) continue;
            if (only is not null && !only.Contains(track.Target.Id)) continue;
            foreach (var k in track.Keys)
                if (times is null || times.Contains(k.Time)) { k.Interp = interp; changed++; }
        }
        if (changed == 0) throw new CommandException("not_found", "No matching keys.");
        return new JsonObject { ["changed"] = changed };
    }
}

public sealed class EvaluateClip(ObjectId clipId, ObjectId characterId, double frame) : ICommand
{
    public string Id => "anim.evaluate";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Evaluate clip";

    public CommandOutcome Execute(Project p)
    {
        var clip = p.FindClip(clipId) ?? throw new CommandException("not_found", $"Clip '{clipId}' does not exist.");
        var (character, rig) = PoseCommands.Resolve(p, characterId);
        // Rest base: the same result playback and export give, independent of the working pose.
        var pose = ClipEvaluator.Evaluate(clip, rig, Flicks.FromSeconds(frame / clip.Fps.FramesPerSecond));
        var model = new RigEvaluator(rig).Model(pose);
        var bones = new JsonArray();
        for (var i = 0; i < rig.Bones.Count; i++)
            bones.Add(new JsonObject
            {
                ["name"] = rig.Bones[i].Name,
                ["rotation"] = InspectRig.Vec(pose.LocalOf(rig.Bones[i]).Rotation.ToEulerXYZ() * (180 / Math.PI)),
                ["position"] = InspectRig.Vec(model[i].Translation),
            });
        return new(Array.Empty<Change>(), new JsonObject { ["frame"] = frame, ["bones"] = bones, ["pose"] = PoseCommands.SerializePose(pose) });
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}

public sealed class PoseFromFrame(ObjectId characterId, ObjectId clipId, double frame) : PoseEdit(characterId)
{
    public override string Id => "anim.pose_from_frame";
    public override string Label => "Pose from frame";

    protected override JsonNode? Apply(Project project, Rig rig, Pose pose, List<Diagnostic> warnings)
    {
        var clip = project.FindClip(clipId) ?? throw new CommandException("not_found", $"Clip '{clipId}' does not exist.");
        var evaluated = ClipEvaluator.Evaluate(clip, rig, Flicks.FromSeconds(frame / clip.Fps.FramesPerSecond));
        pose.Bones = evaluated.Bones;
        pose.Controls = evaluated.Controls;
        return null;
    }
}
