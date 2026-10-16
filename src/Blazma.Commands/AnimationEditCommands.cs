using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;

namespace Blazma.Commands;

/// <summary>
/// Phase 3 animation-editing commands: markers, loop range, key values and tangents,
/// copy/paste of keys and track flags. All mutating commands are whole-clip snapshots (<see cref="ClipEdit"/>).
/// </summary>
public static class AnimationEditCommands
{
    const string PhaseHelp = "none | initial | anticipation | main_action | contact | follow_through | recovery | final.";

    public static void Register(CommandRegistry r)
    {
        r.Register(new("marker.add", "Add a named marker (optionally a key-pose phase) at a frame. Returns {markerId}.", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Num("frame", "Frame (whole number, 0 ≤ frame < clip length).", true),
                 S.Str("name", "Marker name.", true), S.Str("phase", PhaseHelp, false)),
            a => new AddMarker(S.Id(a, "clipId"), Frame(a, "frame"), S.Name(a, "name"), Phase(a) ?? KeyPhase.None)));

        r.Register(new("marker.update", "Change a marker's frame, name and/or phase (at least one).", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("markerId", "Marker id.", true), S.Num("frame", "New frame.", false),
                 S.Str("name", "New name.", false), S.Str("phase", PhaseHelp, false)),
            a =>
            {
                if (a["frame"] is null && a["name"] is null && a["phase"] is null)
                    throw new CommandException("invalid_args", "Give at least one of 'frame', 'name' or 'phase'.");
                return new UpdateMarker(S.Id(a, "clipId"), S.Id(a, "markerId"), a["frame"] is null ? null : Frame(a, "frame"), S.OptName(a, "name"), Phase(a));
            }));

        r.Register(new("marker.delete", "Delete a marker.", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("markerId", "Marker id.", true)),
            a => new DeleteMarker(S.Id(a, "clipId"), S.Id(a, "markerId"))));

        r.Register(new("clip.add_phase_markers", "Add the 7 key-pose phase markers (Initial … Final) spread over the clip; phases that already have a marker are skipped. Returns {added, markers:[{markerId,name,frame,phase}]}.", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true)),
            a => new AddPhaseMarkers(S.Id(a, "clipId"))));

        r.Register(new("clip.set_loop", "Enable/disable looping and optionally set the loop range (startFrame < endFrame ≤ clip length).", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Bool("enabled", "Loop on or off.", true),
                 S.Num("startFrame", "Loop start frame.", false), S.Num("endFrame", "Loop end frame (exclusive); the clip length means clip end.", false)),
            a => new SetLoop(S.Id(a, "clipId"), (bool)a["enabled"]!, a["startFrame"] is null ? null : Frame(a, "startFrame"), a["endFrame"] is null ? null : Frame(a, "endFrame"))));

        r.Register(new("key.set_value", "Set one component of the key at a frame. Rotation keys are renormalized afterwards. Pinned channel accepts 0 or 1 (component 0).", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("trackId", "Track id.", true), S.Num("frame", "Key frame.", true),
                 Int("component", "Component index (0-based).", true), S.Num("value", "New value.", true)),
            a => new SetKeyValue(S.Id(a, "clipId"), S.Id(a, "trackId"), Frame(a, "frame"), Component(a), S.Number(a, "value"))));

        r.Register(new("key.set_tangents", "Make a key's tangents explicit (value per second) and set one component's in/out slope. Other components keep their current auto values, so the curve does not jump.", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("trackId", "Track id.", true), S.Num("frame", "Key frame.", true),
                 Int("component", "Component index (0-based).", true), S.Num("inSlope", "Incoming slope (value/second).", false), S.Num("outSlope", "Outgoing slope (value/second).", false)),
            a =>
            {
                var inS = S.OptNumber(a, "inSlope");
                var outS = S.OptNumber(a, "outSlope");
                if (inS is null && outS is null) throw new CommandException("invalid_args", "Give 'inSlope' and/or 'outSlope'.");
                return new SetKeyTangents(S.Id(a, "clipId"), S.Id(a, "trackId"), Frame(a, "frame"), Component(a), inS, outS);
            }));

        r.Register(new("key.auto_tangents", "Reset keys' tangents to automatic. Filter by track and/or frames; omit both for every key.", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("trackId", "Track id; omit for all tracks.", false), S.Str("frames", "Comma-separated frames; omit for all.", false)),
            a => new AutoTangents(S.Id(a, "clipId"), S.OptId(a, "trackId"), a["frames"] is null ? null : ParseFrames(S.Text(a, "frames")))));

        r.Register(new("key.copy", "Copy keys (optionally only some frames/bones) as JSON in 'keysJson', frames relative to the earliest copied frame. Does not change anything.", CommandEffect.ReadOnly,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("frames", "Comma-separated frames; omit for all.", false), S.Str("bones", "Comma-separated bone/control names or ids; omit for all.", false)),
            a => new CopyKeys(S.Id(a, "clipId"), a["frames"] is null ? null : ParseFrames(S.Text(a, "frames")), Names(a))));

        r.Register(new("key.paste", "Paste keys from key.copy at a frame (replaces keys at the same times, creates tracks, extends the clip if needed). Targets unknown to the clip's rig are ignored with a warning.", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("keysJson", "JSON produced by key.copy.", true), S.Num("frame", "Frame where the copied block starts.", true)),
            a => new PasteKeys(S.Id(a, "clipId"), S.Text(a, "keysJson"), Frame(a, "frame"))));

        r.Register(new("track.set_flags", "Lock/unlock and/or mute/unmute a track (at least one flag).", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("trackId", "Track id.", true), S.Bool("locked", "Locked tracks cannot be edited.", false), S.Bool("muted", "Muted tracks are not evaluated.", false)),
            a =>
            {
                var locked = a["locked"] is null ? (bool?)null : (bool)a["locked"]!;
                var muted = a["muted"] is null ? (bool?)null : (bool)a["muted"]!;
                if (locked is null && muted is null) throw new CommandException("invalid_args", "Give 'locked' and/or 'muted'.");
                return new SetTrackFlags(S.Id(a, "clipId"), S.Id(a, "trackId"), locked, muted);
            }));
    }

    // ---- argument helpers ------------------------------------------------------------------

    static (string, JsonObject, bool) Int(string n, string d, bool req) => (n, new JsonObject { ["type"] = "integer", ["description"] = d }, req);

    internal static long Frame(JsonObject a, string key)
    {
        var f = S.Number(a, key);
        if (f < 0 || f != Math.Floor(f) || f > 10_000_000) throw new CommandException("invalid_args", $"'{key}' must be a whole number ≥ 0.");
        return (long)f;
    }

    static int Component(JsonObject a)
    {
        var c = S.Number(a, "component");
        if (c < 0 || c != Math.Floor(c) || c > 1000) throw new CommandException("invalid_args", "'component' must be a whole number ≥ 0.");
        return (int)c;
    }

    internal static IReadOnlyList<long> ParseFrames(string text)
    {
        var list = new List<long>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) || f < 0)
                throw new CommandException("invalid_args", $"'{part}' is not a valid frame number.");
            list.Add(f);
        }
        if (list.Count == 0) throw new CommandException("invalid_args", "'frames' is empty.");
        return list.Distinct().ToList();
    }

    static IReadOnlyList<string>? Names(JsonObject a) =>
        ((string?)a["bones"])?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } n ? n : null;

    static KeyPhase? Phase(JsonObject a)
    {
        var s = ((string?)a["phase"])?.Trim().ToLowerInvariant();
        if (s is null) return null;
        return s switch
        {
            "none" => KeyPhase.None,
            "initial" => KeyPhase.Initial,
            "anticipation" => KeyPhase.Anticipation,
            "main_action" => KeyPhase.MainAction,
            "contact" => KeyPhase.Contact,
            "follow_through" => KeyPhase.FollowThrough,
            "recovery" => KeyPhase.Recovery,
            "final" => KeyPhase.Final,
            _ => throw new CommandException("invalid_args", $"'phase' must be one of: {PhaseHelp}"),
        };
    }

    internal static Clip RequireClip(Project p, ObjectId id) =>
        p.FindClip(id) ?? throw new CommandException("not_found", $"Clip '{id}' does not exist.");

    internal static Track FindTrack(Clip clip, ObjectId id) =>
        clip.Tracks.Find(t => t.Id == id) ?? throw new CommandException("not_found", $"Track '{id}' does not exist in this clip.");

    internal static void CheckFrameInClip(Clip clip, long frame, string key = "frame")
    {
        if (frame >= clip.DurationFrames)
            throw new CommandException("invalid_args", $"'{key}' {frame} is outside the clip (length {clip.DurationFrames} frames).");
    }

    /// <summary>KeyPhase → the snake_case name used in command arguments.</summary>
    public static string PhaseName(KeyPhase phase) => phase switch
    {
        KeyPhase.Initial => "initial",
        KeyPhase.Anticipation => "anticipation",
        KeyPhase.MainAction => "main_action",
        KeyPhase.Contact => "contact",
        KeyPhase.FollowThrough => "follow_through",
        KeyPhase.Recovery => "recovery",
        KeyPhase.Final => "final",
        _ => "none",
    };

    internal static void SortMarkers(Clip clip) => clip.Markers = clip.Markers.OrderBy(m => m.Time.Value).ToList();

    internal static Keyframe KeyAt(Clip clip, Track track, long frame) =>
        track.Keys.Find(k => k.Time == Flicks.FromFrame(frame, clip.Fps))
            ?? throw new CommandException("not_found", $"Track has no key at frame {frame}.");

    internal static string InterpName(Interpolation i) => i switch { Interpolation.Step => "step", Interpolation.Linear => "linear", _ => "cubic" };
}

// ---- markers ---------------------------------------------------------------------------------

public sealed class AddMarker(ObjectId clipId, long frame, string name, KeyPhase phase) : ClipEdit(clipId)
{
    public override string Id => "marker.add";
    public override string Label => "Add marker";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        AnimationEditCommands.CheckFrameInClip(clip, frame);
        var marker = new Marker { Time = Flicks.FromFrame(frame, clip.Fps), Name = name, Phase = phase };
        clip.Markers.Add(marker);
        AnimationEditCommands.SortMarkers(clip);
        return new JsonObject { ["markerId"] = marker.Id.ToString() };
    }
}

public sealed class UpdateMarker(ObjectId clipId, ObjectId markerId, long? frame, string? name, KeyPhase? phase) : ClipEdit(clipId)
{
    public override string Id => "marker.update";
    public override string Label => "Edit marker";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        var marker = clip.Markers.Find(m => m.Id == markerId) ?? throw new CommandException("not_found", $"Marker '{markerId}' does not exist.");
        if (frame is { } f)
        {
            AnimationEditCommands.CheckFrameInClip(clip, f);
            marker.Time = Flicks.FromFrame(f, clip.Fps);
        }
        if (name is not null) marker.Name = name;
        if (phase is { } ph) marker.Phase = ph;
        AnimationEditCommands.SortMarkers(clip);
        return new JsonObject { ["markerId"] = marker.Id.ToString() };
    }
}

public sealed class DeleteMarker(ObjectId clipId, ObjectId markerId) : ClipEdit(clipId)
{
    public override string Id => "marker.delete";
    public override string Label => "Delete marker";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        if (clip.Markers.RemoveAll(m => m.Id == markerId) == 0) throw new CommandException("not_found", $"Marker '{markerId}' does not exist.");
        return null;
    }
}

public sealed class AddPhaseMarkers(ObjectId clipId) : ClipEdit(clipId)
{
    static readonly (KeyPhase Phase, double Fraction, string Name)[] Phases =
    {
        (KeyPhase.Initial, 0, "Initial Pose"),
        (KeyPhase.Anticipation, 0.15, "Anticipation"),
        (KeyPhase.MainAction, 0.35, "Main Action"),
        (KeyPhase.Contact, 0.45, "Contact"),
        (KeyPhase.FollowThrough, 0.6, "Follow-through"),
        (KeyPhase.Recovery, 0.8, "Recovery"),
        (KeyPhase.Final, 1.0, "Final Pose"),
    };

    public override string Id => "clip.add_phase_markers";
    public override string Label => "Add phase markers";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        var last = Math.Max(0, clip.DurationFrames - 1);
        var added = 0;
        var created = new JsonArray();
        foreach (var (phase, fraction, name) in Phases)
        {
            if (clip.Markers.Exists(m => m.Phase == phase))
            {
                warnings.Add(new Diagnostic("phase_exists", $"A '{name}' marker already exists; skipped."));
                continue;
            }
            var frame = (long)Math.Round(fraction * last, MidpointRounding.AwayFromZero);
            var marker = new Marker { Time = Flicks.FromFrame(frame, clip.Fps), Name = name, Phase = phase };
            clip.Markers.Add(marker);
            created.Add(new JsonObject { ["markerId"] = marker.Id.ToString(), ["name"] = name, ["frame"] = frame, ["phase"] = AnimationEditCommands.PhaseName(phase) });
            added++;
        }
        AnimationEditCommands.SortMarkers(clip);
        return new JsonObject { ["added"] = added, ["markers"] = created };
    }
}

// ---- loop ------------------------------------------------------------------------------------

public sealed class SetLoop(ObjectId clipId, bool enabled, long? startFrame, long? endFrame) : ClipEdit(clipId)
{
    public override string Id => "clip.set_loop";
    public override string Label => "Set loop";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        var length = clip.DurationFrames;
        var start = startFrame ?? clip.Loop.Start.ToFrame(clip.Fps);
        var end = endFrame ?? (clip.Loop.End == Flicks.Zero ? length : clip.Loop.End.ToFrame(clip.Fps));
        if (start < 0 || start >= end || end > length)
            throw new CommandException("invalid_args", $"Loop range must satisfy 0 ≤ startFrame < endFrame ≤ {length} (got {start}..{end}).");
        clip.Loop.Enabled = enabled;
        clip.Loop.Start = Flicks.FromFrame(start, clip.Fps);
        // Always explicit, so lengthening the clip later does not silently move the loop end.
        clip.Loop.End = Flicks.FromFrame(end, clip.Fps);
        return new JsonObject { ["startFrame"] = start, ["endFrame"] = end };
    }
}

// ---- key values and tangents -----------------------------------------------------------------

public sealed class SetKeyValue(ObjectId clipId, ObjectId trackId, long frame, int component, double value) : ClipEdit(clipId)
{
    public override string Id => "key.set_value";
    public override string Label => "Set key value";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        var track = AnimationEditCommands.FindTrack(clip, trackId);
        var arity = TrackChannels.Arity(track.Channel);
        if (component >= arity) throw new CommandException("invalid_args", $"Channel '{track.Channel}' has {arity} component(s); 'component' {component} is out of range.");
        if (track.Channel == TrackChannels.Pinned && value != 0 && value != 1)
            throw new CommandException("invalid_args", "The pinned channel only accepts 0 or 1.");
        var key = AnimationEditCommands.KeyAt(clip, track, frame);
        if (track.Locked) throw new CommandException("track_locked", "The track is locked.");

        var v = new double[arity];
        Array.Copy(key.Value, v, Math.Min(arity, key.Value.Length));
        v[component] = value;
        if (track.Channel == TrackChannels.Rotation)
        {
            var len = Math.Sqrt(v.Sum(x => x * x));
            if (len < 1e-9) throw new CommandException("invalid_args", "A rotation key cannot be all zeros.");
            for (var i = 0; i < arity; i++) v[i] /= len;
        }
        key.Value = v;
        return new JsonObject { ["value"] = new JsonArray(v.Select(x => (JsonNode?)x).ToArray()) };
    }
}

public sealed class SetKeyTangents(ObjectId clipId, ObjectId trackId, long frame, int component, double? inSlope, double? outSlope) : ClipEdit(clipId)
{
    public override string Id => "key.set_tangents";
    public override string Label => "Set tangents";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        var track = AnimationEditCommands.FindTrack(clip, trackId);
        if (track.Channel == TrackChannels.Pinned) throw new CommandException("invalid_args", "The pinned channel is stepped and has no tangents.");
        var arity = TrackChannels.Arity(track.Channel);
        if (component >= arity) throw new CommandException("invalid_args", $"Channel '{track.Channel}' has {arity} component(s); 'component' {component} is out of range.");
        var key = AnimationEditCommands.KeyAt(clip, track, frame);
        if (track.Locked) throw new CommandException("track_locked", "The track is locked.");

        var index = track.Keys.IndexOf(key);
        var isRotation = track.Channel == TrackChannels.Rotation;
        // Freeze whatever the curve uses right now, so converting to explicit tangents is seamless.
        var tin = (double[])TrackSampler.InTangent(track, index, arity, isRotation).Clone();
        var tout = (double[])TrackSampler.OutTangent(track, index, arity, isRotation).Clone();
        if (inSlope is { } i) tin[component] = i;
        if (outSlope is { } o) tout[component] = o;
        key.InTangent = tin;
        key.OutTangent = tout;
        return new JsonObject { ["inSlope"] = tin[component], ["outSlope"] = tout[component] };
    }
}

public sealed class AutoTangents(ObjectId clipId, ObjectId? trackId, IReadOnlyList<long>? frames) : ClipEdit(clipId)
{
    public override string Id => "key.auto_tangents";
    public override string Label => "Auto tangents";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        if (trackId is { } tid) AnimationEditCommands.FindTrack(clip, tid);
        var times = frames?.Select(f => Flicks.FromFrame(f, clip.Fps)).ToHashSet();
        var matched = 0;
        var changed = 0;
        var skippedLocked = 0;
        foreach (var track in clip.Tracks)
        {
            if (trackId is { } id && track.Id != id) continue;
            if (track.Channel == TrackChannels.Pinned) continue;
            foreach (var k in track.Keys)
            {
                if (times is not null && !times.Contains(k.Time)) continue;
                matched++;
                if (track.Locked) { skippedLocked++; continue; }
                k.InTangent = null;
                k.OutTangent = null;
                changed++;
            }
        }
        if (matched == 0) throw new CommandException("not_found", "No matching keys.");
        if (skippedLocked > 0) warnings.Add(new Diagnostic("track_locked", $"{skippedLocked} keys on locked tracks were skipped."));
        return new JsonObject { ["changed"] = changed };
    }
}

// ---- copy / paste ----------------------------------------------------------------------------

public sealed class CopyKeys(ObjectId clipId, IReadOnlyList<long>? frames, IReadOnlyList<string>? names) : ICommand
{
    public string Id => "key.copy";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Copy keys";

    public CommandOutcome Execute(Project p)
    {
        var clip = AnimationEditCommands.RequireClip(p, clipId);
        var only = KeyCommands.ResolveTargets(p.FindRig(clip.Skeleton), names);
        var times = frames?.Select(f => Flicks.FromFrame(f, clip.Fps)).ToHashSet();

        var picked = new List<(Track Track, Keyframe Key, long Frame)>();
        foreach (var track in clip.Tracks)
        {
            if (only is not null && !only.Contains(track.Target.Id)) continue;
            foreach (var k in track.Keys)
                if (times is null || times.Contains(k.Time)) picked.Add((track, k, k.Time.ToFrame(clip.Fps)));
        }
        if (picked.Count == 0) throw new CommandException("not_found", "No keys to copy.");

        var origin = picked.Min(x => x.Frame);
        var keys = new JsonArray();
        foreach (var (track, k, frame) in picked.OrderBy(x => x.Frame))
            keys.Add(new JsonObject
            {
                ["target"] = new JsonObject { ["kind"] = track.Target.Kind.ToString().ToLowerInvariant(), ["id"] = track.Target.Id.ToString() },
                ["channel"] = track.Channel,
                ["frame"] = frame - origin,
                ["value"] = Arr(k.Value),
                ["interp"] = AnimationEditCommands.InterpName(k.Interp),
                ["inTangent"] = k.InTangent is null ? null : Arr(k.InTangent),
                ["outTangent"] = k.OutTangent is null ? null : Arr(k.OutTangent),
            });
        var doc = new JsonObject { ["fps"] = clip.Fps.ToString(), ["keys"] = keys };
        return new(Array.Empty<Change>(), new JsonObject { ["keysJson"] = doc.ToJsonString(), ["count"] = picked.Count });
    }

    static JsonArray Arr(double[] v) => new(v.Select(x => (JsonNode?)x).ToArray());

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}

/// <summary>
/// Pastes keys. Decisions: targets the clip's rig does not know are skipped with an "ignored_targets"
/// warning (rig_mismatch only if nothing at all can be pasted); locked tracks are skipped with "track_locked";
/// tangents are rescaled when the source frame rate differs so the shape in frames is preserved.
/// </summary>
public sealed class PasteKeys(ObjectId clipId, string keysJson, long frame) : ClipEdit(clipId)
{
    public override string Id => "key.paste";
    public override string Label => "Paste keys";

    sealed record Item(TrackTarget Target, string Channel, long Frame, double[] Value, Interpolation Interp, double[]? In, double[]? Out);

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        var (items, srcFps) = Parse(clip);

        // Resolve which targets are valid for the clip's rig (binding it first if needed).
        var rigTargets = items.Where(i => i.Target.Kind != TargetKind.Object).Select(i => i.Target.Id).Distinct().ToList();
        Rig? rig = null;
        if (!clip.Skeleton.IsEmpty) rig = p.FindRig(clip.Skeleton);
        else if (rigTargets.Count > 0)
        {
            rig = p.Rigs.Select(r => (Rig: r, Hits: rigTargets.Count(id => r.FindBone(id) is not null || r.FindControl(id) is not null)))
                .Where(x => x.Hits > 0).OrderByDescending(x => x.Hits).Select(x => x.Rig).FirstOrDefault();
            if (rig is null) throw new CommandException("rig_mismatch", "None of the copied bones/controls belong to a rig in this project.");
            clip.Skeleton = rig.Id;
        }

        var ignored = new HashSet<ObjectId>();
        var usable = new List<Item>();
        foreach (var item in items)
        {
            var known = item.Target.Kind switch
            {
                TargetKind.Bone => rig is null || rig.FindBone(item.Target.Id) is not null,
                TargetKind.Control => rig is null || rig.FindControl(item.Target.Id) is not null,
                _ => true,
            };
            if (known) usable.Add(item); else ignored.Add(item.Target.Id);
        }
        if (usable.Count == 0) throw new CommandException("rig_mismatch", "None of the copied bones/controls belong to this clip's rig.");
        if (ignored.Count > 0)
            warnings.Add(new Diagnostic("ignored_targets", $"{ignored.Count} target(s) are not part of the clip's rig and were ignored."));

        var scale = clip.Fps.FramesPerSecond / srcFps.FramesPerSecond;
        var existingTimes = KeyCommands.KeyTimes(clip);
        var pasted = 0;
        var lockedSkipped = 0;
        var lastFrame = 0L;
        foreach (var item in usable)
        {
            var track = KeyCommands.GetOrCreateTrack(clip, rig, item.Target.Kind, item.Target.Id, item.Channel,
                clip.Tracks.Find(t => t.Target.Id == item.Target.Id)?.Group, existingTimes);
            if (track.Locked) { lockedSkipped++; continue; }
            var f = frame + item.Frame;
            KeyCommands.InsertKey(track, new Keyframe
            {
                Time = Flicks.FromFrame(f, clip.Fps),
                Value = item.Value,
                Interp = item.Interp,
                InTangent = item.In?.Select(x => x * scale).ToArray(),
                OutTangent = item.Out?.Select(x => x * scale).ToArray(),
            });
            lastFrame = Math.Max(lastFrame, f);
            pasted++;
        }
        if (lockedSkipped > 0) warnings.Add(new Diagnostic("track_locked", $"{lockedSkipped} keys on locked tracks were skipped."));
        if (pasted > 0 && lastFrame >= clip.DurationFrames)
        {
            clip.Duration = Flicks.FromFrame(lastFrame + 1, clip.Fps);
            warnings.Add(new Diagnostic("clip_extended", $"The clip was extended to {lastFrame + 1} frames."));
        }
        return new JsonObject { ["pasted"] = pasted, ["ignored"] = ignored.Count };
    }

    (List<Item>, FrameRate) Parse(Clip clip)
    {
        try
        {
            var root = JsonNode.Parse(keysJson) as JsonObject ?? throw new CommandException("invalid_args", "'keysJson' must be a JSON object.");
            var fps = root["fps"] is JsonNode f ? FrameRate.Parse((string)f!) : clip.Fps;
            var array = root["keys"] as JsonArray ?? throw new CommandException("invalid_args", "'keysJson.keys' must be an array.");
            if (array.Count == 0) throw new CommandException("invalid_args", "'keysJson' contains no keys.");
            var items = new List<Item>();
            foreach (var n in array)
            {
                var o = n as JsonObject ?? throw new CommandException("invalid_args", "Each key must be an object.");
                var tgt = o["target"] as JsonObject ?? throw new CommandException("invalid_args", "A key has no target.");
                var kind = ((string)tgt["kind"]!).ToLowerInvariant() switch
                {
                    "bone" => TargetKind.Bone,
                    "control" => TargetKind.Control,
                    "object" => TargetKind.Object,
                    var other => throw new CommandException("invalid_args", $"Unknown target kind '{other}'."),
                };
                if (!ObjectId.TryParse((string?)tgt["id"], out var id)) throw new CommandException("invalid_args", "A key has an invalid target id.");
                var channel = (string)o["channel"]!;
                var arity = TrackChannels.Arity(channel);
                if (arity == 0) throw new CommandException("invalid_args", $"Unknown channel '{channel}'.");
                var rel = (long)o["frame"]!;
                if (rel < 0) throw new CommandException("invalid_args", "A key has a negative frame.");
                var value = Doubles(o["value"], arity) ?? throw new CommandException("invalid_args", "A key has no value.");
                var interp = ((string)o["interp"]!).ToLowerInvariant() switch
                {
                    "step" => Interpolation.Step,
                    "linear" => Interpolation.Linear,
                    "cubic" => Interpolation.Cubic,
                    var other => throw new CommandException("invalid_args", $"Unknown interpolation '{other}'."),
                };
                items.Add(new Item(new TrackTarget { Kind = kind, Id = id }, channel, rel, value, interp, Doubles(o["inTangent"], arity), Doubles(o["outTangent"], arity)));
            }
            return (items, fps);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or NullReferenceException or ArgumentException)
        {
            throw new CommandException("invalid_args", $"'keysJson' is not valid key data: {e.Message}");
        }
    }

    static double[]? Doubles(JsonNode? n, int arity)
    {
        if (n is null) return null;
        var a = n as JsonArray ?? throw new CommandException("invalid_args", "Expected an array of numbers.");
        if (a.Count != arity) throw new CommandException("invalid_args", $"Expected {arity} numbers, got {a.Count}.");
        var r = a.Select(x => (double)x!).ToArray();
        if (r.Any(x => !double.IsFinite(x))) throw new CommandException("invalid_args", "Values must be finite.");
        return r;
    }
}

// ---- track flags -----------------------------------------------------------------------------

public sealed class SetTrackFlags(ObjectId clipId, ObjectId trackId, bool? locked, bool? muted) : ClipEdit(clipId)
{
    public override string Id => "track.set_flags";
    public override string Label => "Track flags";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        var track = AnimationEditCommands.FindTrack(clip, trackId);
        if (locked is { } l) track.Locked = l;
        if (muted is { } m) track.Muted = m;
        return new JsonObject { ["locked"] = track.Locked, ["muted"] = track.Muted };
    }
}
