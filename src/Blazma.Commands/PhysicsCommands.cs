using System.Text.Json.Nodes;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Physics;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Commands;

/// <summary>
/// Phase 6 physics assistance: centre of mass, support polygon and balance analysis, auto-balance,
/// trajectories and baked secondary motion. Formulas: docs/ARCHITECTURE.md §7.
/// </summary>
public static class PhysicsCommands
{
    /// <summary>Largest frame range one command evaluates.</summary>
    public const long MaxRange = 20_000;

    const string RangeHelp = "First frame (default 0).";
    const string EndHelp = "Last frame, inclusive (default: the clip's last frame).";
    const string CharacterHelp = "Character to evaluate the clip on (default: the first character using the clip's rig).";

    public static void Register(CommandRegistry r)
    {
        r.Register(new("physics.analyze",
            "Balance analysis of a clip, frame by frame: centre of mass (COM), foot contacts, support polygon (convex hull of the touching sole points, XZ), " +
            "signed margin of the tested point to the polygon (inside positive, metres) and balanced/unbalanced/airborne. Returns {summary:{unbalancedRanges…}, frames:[…]}. Does not change anything.",
            CommandEffect.ReadOnly,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("characterId", CharacterHelp, false),
                 S.Int("startFrame", RangeHelp, false), S.Int("endFrame", EndHelp, false),
                 S.Bool("summaryOnly", "Only the summary, no per-frame list.", false),
                 S.Int("maxFrames", "Most per-frame entries to return (default 120, max 2000); the rest is summarized.", false, 1),
                 S.Str("metric", "com (static, default) | zmp (zero-moment point, uses COM acceleration) | capture (capture point, uses COM velocity).", false),
                 S.Num("minMargin", "Margin (m) a frame needs to count as balanced (default 0).", false),
                 S.Num("contactHeight", "Sole points at most this high above the ground (m) can touch (default 0.03).", false)),
            a => new AnalyzeBalance(S.Id(a, "clipId"), S.OptId(a, "characterId"), Frame(a, "startFrame"), Frame(a, "endFrame"),
                a["summaryOnly"] is not null && (bool)a["summaryOnly"]!, (int)Math.Min(2000, Args.OptLong(a, "maxFrames") ?? 120), Options(a))));

        r.Register(new("physics.com",
            "Centre of mass and static balance of a character: its working pose, or a clip frame when 'clipId' is given (with velocity, ZMP and capture point). " +
            "Returns {com, totalMass, massSource, contacts, polygon, margin, state}. Does not change anything.",
            CommandEffect.ReadOnly,
            S.Of(S.Str("characterId", "Character id.", true), S.Str("clipId", "Evaluate this clip instead of the working pose.", false),
                 S.Int("frame", "Clip frame (default 0).", false), S.Bool("segments", "Also list each segment's mass and centre.", false)),
            a => new CenterOfMassQuery(S.Id(a, "characterId"), S.OptId(a, "clipId"), Frame(a, "frame") ?? 0, a["segments"] is not null && (bool)a["segments"]!)));

        r.Register(new("physics.auto_balance",
            "Move the COM inside the support polygon (by 'margin') on unbalanced frames by shifting the hips horizontally; supporting feet stay planted (legs re-solved with IK). " +
            "Keys the pelvis translation (and leg rotations of unpinned supporting legs) on corrected frames, plus the original values on neighbouring frames. " +
            "One undo step. Returns before/after margins per frame.",
            CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("characterId", CharacterHelp, false),
                 S.Str("frames", "Comma-separated frames to correct; omit to use startFrame/endFrame.", false),
                 S.Int("startFrame", RangeHelp, false), S.Int("endFrame", EndHelp, false),
                 S.Num("margin", "Distance (m) to keep the COM inside the polygon (default 0.02, max 0.5).", false),
                 S.Num("maxShift", "Largest hip shift (m, default 0.3, max 1).", false)),
            a => new AutoBalanceClip(S.Id(a, "clipId"), S.OptId(a, "characterId"),
                a["frames"] is null ? null : AnimationEditCommands.ParseFrames(S.Text(a, "frames")),
                Frame(a, "startFrame"), Frame(a, "endFrame"),
                new AutoBalanceOptions
                {
                    Margin = Bounded(a, "margin", 0.02, 0, 0.5),
                    MaxShift = Bounded(a, "maxShift", 0.3, 1e-4, 1),
                })));

        r.Register(new("physics.secondary_motion",
            "Bake follow-through / overlap: the chosen bones lag behind the clip's motion like damped springs (stiffness k 1/s², damping c 1/s; mass-independent, " +
            "stable at any fps). Writes rotation keys on every frame of the range (one undo step). Give 'bones' and/or 'preset'.",
            CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("characterId", CharacterHelp, false),
                 S.Str("bones", "Comma-separated bone names or ids, parent before child for a chain.", false),
                 S.Str("preset", "spine | head | arms (humanoid-mapped bones).", false),
                 S.Num("stiffness", "Spring stiffness k (1/s², default 150 ≈ 1.9 Hz; max 10000).", false),
                 S.Num("damping", "Damping c (1/s, default 12; damping ratio = c / (2√k)).", false),
                 S.Num("maxAngle", "Largest lag in degrees (default 30).", false),
                 S.Int("startFrame", RangeHelp, false), S.Int("endFrame", EndHelp, false)),
            a =>
            {
                var names = ((string?)a["bones"])?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
                var preset = ((string?)a["preset"])?.Trim().ToLowerInvariant();
                if (names.Length == 0 && preset is null) throw new CommandException("invalid_args", "Give 'bones' and/or 'preset'.");
                if (preset is not null && Preset(preset) is null) throw new CommandException("invalid_args", "'preset' must be spine, head or arms.");
                var spring = new SpringSettings
                {
                    Stiffness = Bounded(a, "stiffness", 150, 0.01, SpringSettings.MaxStiffness),
                    Damping = Bounded(a, "damping", 12, 0, SpringSettings.MaxDamping),
                    MaxAngleDegrees = Bounded(a, "maxAngle", 30, 0.1, 180),
                };
                return new BakeSecondaryMotion(S.Id(a, "clipId"), S.OptId(a, "characterId"), names, preset, spring, Frame(a, "startFrame"), Frame(a, "endFrame"));
            }));

        r.Register(new("physics.trajectories",
            "Per-frame model-space paths of a clip for drawing: centre of mass, hips (pelvis) and/or feet (ankles). Returns arrays of [x,y,z]. Does not change anything.",
            CommandEffect.ReadOnly,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("characterId", CharacterHelp, false),
                 S.Str("what", "com | feet | hips | all (default all).", false),
                 S.Int("startFrame", RangeHelp, false), S.Int("endFrame", EndHelp, false)),
            a =>
            {
                var what = ((string?)a["what"])?.Trim().ToLowerInvariant() ?? "all";
                if (what is not ("com" or "feet" or "hips" or "all")) throw new CommandException("invalid_args", "'what' must be com, feet, hips or all.");
                return new TrajectoryQuery(S.Id(a, "clipId"), S.OptId(a, "characterId"), what, Frame(a, "startFrame"), Frame(a, "endFrame"));
            }));
    }

    // ---- helpers ---------------------------------------------------------------------------

    static long? Frame(JsonObject a, string key)
    {
        var v = Args.OptLong(a, key);
        return v is < 0 or > 10_000_000 ? throw new CommandException("invalid_args", $"'{key}' must be between 0 and 10,000,000.") : v;
    }

    static double Bounded(JsonObject a, string key, double fallback, double min, double max)
    {
        var v = S.OptNumber(a, key) ?? fallback;
        if (v < min || v > max) throw new CommandException("invalid_args", $"'{key}' must be between {min} and {max}.");
        return v;
    }

    static BalanceOptions Options(JsonObject a)
    {
        var metric = ((string?)a["metric"])?.Trim().ToLowerInvariant() switch
        {
            null or "com" => BalanceMetric.Com,
            "zmp" => BalanceMetric.Zmp,
            "capture" or "capture_point" => BalanceMetric.CapturePoint,
            _ => throw new CommandException("invalid_args", "'metric' must be com, zmp or capture."),
        };
        return new BalanceOptions
        {
            Metric = metric,
            MinMargin = Bounded(a, "minMargin", 0, -1, 1),
            ContactHeight = Bounded(a, "contactHeight", 0.03, 0, 0.5),
        };
    }

    internal static IReadOnlyList<string>? Preset(string name) => name switch
    {
        "spine" => new[] { H.Spine1, H.Spine2, H.Chest, H.Neck, H.Head },
        "head" => new[] { H.Neck, H.Head },
        "arms" => new[] { Side.Left, Side.Right }.SelectMany(s => new[] { H.Clavicle(s), H.UpperArm(s), H.LowerArm(s), H.Hand(s) }).ToArray(),
        _ => null,
    };

    /// <summary>[start, end] within the clip; <c>range_too_large</c> above <see cref="MaxRange"/> frames.</summary>
    internal static (long, long) Range(Clip clip, long? start, long? end)
    {
        var last = BalanceAnalyzer.LastFrame(clip);
        var s = start ?? 0;
        var e = end ?? last;
        if (s > last) throw new CommandException("invalid_args", $"'startFrame' is after the clip's last frame ({last}).");
        if (e < s) throw new CommandException("invalid_args", "'endFrame' must not be before 'startFrame'.");
        e = Math.Min(e, last);
        if (e - s + 1 > MaxRange) throw new CommandException("range_too_large", $"At most {MaxRange} frames per call; use startFrame/endFrame.");
        return (s, e);
    }

    /// <summary>Rounded to 0.01 mm, without "-0".</summary>
    internal static double R(double d) => Math.Round(d, 5) + 0.0;
    internal static JsonArray V(Vec3 v) => new(R(v.X), R(v.Y), R(v.Z));
    internal static JsonNode? V(Vec3? v) => v is { } x ? V(x) : null;
    internal static JsonNode? M(double d) => double.IsFinite(d) ? JsonValue.Create(R(d)) : null;

    internal static JsonArray Polygon(IReadOnlyList<Vec2> hull)
    {
        var a = new JsonArray();
        foreach (var p in hull) a.Add(new JsonArray(R(p.X), R(p.Z)));
        return a;
    }

    internal static string StateName(BalanceState s) => s switch
    {
        BalanceState.Balanced => "balanced",
        BalanceState.Unbalanced => "unbalanced",
        _ => "airborne",
    };

    internal static JsonObject Frame(PoseBalance b, bool detailedContacts = false)
    {
        var o = new JsonObject
        {
            ["frame"] = b.Frame,
            ["state"] = StateName(b.State),
            ["margin"] = M(b.Margin),
            ["comMargin"] = M(b.ComMargin),
            ["com"] = V(b.Com),
            ["zmp"] = V(b.Zmp),
            ["capturePoint"] = V(b.CapturePoint),
            ["feet"] = new JsonArray(b.Contacts.Select(c => c.Side).Distinct().Select(s => (JsonNode)JsonValue.Create(s == Side.Left ? "left" : "right")!).ToArray()),
            ["contacts"] = b.Contacts.Count,
            ["polygon"] = Polygon(b.Polygon),
        };
        if (detailedContacts)
            o["contactPoints"] = new JsonArray(b.Contacts.Select(c => (JsonNode)new JsonObject
            {
                ["foot"] = c.Side == Side.Left ? "left" : "right", ["point"] = c.Point, ["position"] = V(c.Position),
            }).ToArray());
        return o;
    }

    internal static JsonObject Summary(ClipBalanceReport r)
    {
        var worst = r.Worst;
        return new JsonObject
        {
            ["frames"] = r.Frames.Count,
            ["balanced"] = r.BalancedFrames,
            ["unbalanced"] = r.UnbalancedFrames,
            ["airborne"] = r.AirborneFrames,
            ["minMargin"] = worst is null ? null : M(worst.Margin),
            ["worstFrame"] = worst?.Frame,
            ["unbalancedRanges"] = new JsonArray(r.UnbalancedRanges.Select(x => (JsonNode)new JsonObject
            {
                ["start"] = x.Start, ["end"] = x.End, ["minMargin"] = M(x.MinMargin), ["worstFrame"] = x.WorstFrame,
            }).ToArray()),
        };
    }

    internal static void RequireFeet(BalanceAnalyzer an)
    {
        if (an.Feet.Feet.Count == 0)
            throw new CommandException("no_feet", $"Rig '{an.Rig.Name}' has no humanoid-mapped feet (foot_l/foot_r); map them with rig.map_bone.");
    }
}

public sealed class AnalyzeBalance(ObjectId clipId, ObjectId? characterId, long? start, long? end, bool summaryOnly, int maxFrames, BalanceOptions options) : ICommand
{
    public string Id => "physics.analyze";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Analyze balance";

    public CommandOutcome Execute(Project p)
    {
        var (clip, rig) = ExportCommands.Resolve(p, clipId, characterId);
        var (s, e) = PhysicsCommands.Range(clip, start, end);
        var an = new BalanceAnalyzer(rig, options);
        PhysicsCommands.RequireFeet(an);
        var report = an.AnalyzeClip(clip, s, e);
        var data = new JsonObject
        {
            ["clipId"] = clip.Id.ToString(),
            ["startFrame"] = s,
            ["endFrame"] = e,
            ["fps"] = clip.Fps.ToString(),
            ["metric"] = options.Metric switch { BalanceMetric.Zmp => "zmp", BalanceMetric.CapturePoint => "capture", _ => "com" },
            ["ground"] = an.Ground,
            ["totalMass"] = Math.Round(an.Mass.TotalMass, 3),
            ["massSource"] = an.Mass.Source == MassSource.BoneMasses ? "bone_masses" : "length_distributed",
            ["summary"] = PhysicsCommands.Summary(report),
        };
        var warnings = new List<Diagnostic>();
        if (an.Mass.Source == MassSource.LengthDistributed)
            warnings.Add(new Diagnostic("estimated_mass", $"The rig has no bone masses; {an.Mass.TotalMass:0.#} kg was distributed by bone length."));
        if (!summaryOnly)
        {
            var frames = new JsonArray();
            foreach (var f in report.Frames.Take(maxFrames)) frames.Add(PhysicsCommands.Frame(f));
            data["frames"] = frames;
            data["truncated"] = report.Frames.Count > maxFrames;
        }
        return new(Array.Empty<Change>(), data, warnings);
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}

public sealed class CenterOfMassQuery(ObjectId characterId, ObjectId? clipId, long frame, bool segments) : ICommand
{
    public string Id => "physics.com";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Centre of mass";

    public CommandOutcome Execute(Project p)
    {
        var (character, rig) = PoseCommands.Resolve(p, characterId);
        var an = new BalanceAnalyzer(rig);
        PoseBalance b;
        Transform[] model;
        if (clipId is { } cid)
        {
            var (clip, _) = ExportCommands.Resolve(p, cid, characterId);
            if (frame > BalanceAnalyzer.LastFrame(clip)) throw new CommandException("invalid_args", $"'frame' is after the clip's last frame ({BalanceAnalyzer.LastFrame(clip)}).");
            var report = an.AnalyzeClip(clip, frame, frame);
            b = report.Frames[0];
            model = report.Samples!.Model[(int)(frame - report.Samples.StartFrame)];
        }
        else
        {
            b = an.AnalyzePose(character.Pose);
            model = an.Evaluator.Model(character.Pose);
        }
        var data = PhysicsCommands.Frame(b, detailedContacts: true);
        data["source"] = clipId is null ? "pose" : "clip";
        data["comVelocity"] = PhysicsCommands.V(b.ComVelocity);
        data["totalMass"] = Math.Round(an.Mass.TotalMass, 3);
        data["massSource"] = an.Mass.Source == MassSource.BoneMasses ? "bone_masses" : "length_distributed";
        data["ground"] = an.Ground;
        if (segments)
        {
            var centres = an.Mass.SegmentCentres(model);
            data["segments"] = new JsonArray(an.Mass.Segments.Select((sg, i) => (JsonNode)new JsonObject
            {
                ["bone"] = rig.Bones[sg.Bone].Name, ["mass"] = Math.Round(sg.Mass, 4), ["com"] = PhysicsCommands.V(centres[i]),
            }).ToArray());
        }
        return new(Array.Empty<Change>(), data);
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}

public sealed class TrajectoryQuery(ObjectId clipId, ObjectId? characterId, string what, long? start, long? end) : ICommand
{
    public string Id => "physics.trajectories";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Trajectories";

    public CommandOutcome Execute(Project p)
    {
        var (clip, rig) = ExportCommands.Resolve(p, clipId, characterId);
        var (s, e) = PhysicsCommands.Range(clip, start, end);
        var an = new BalanceAnalyzer(rig);
        var t = Trajectories.Compute(an, clip, s, e);
        static JsonArray Path(Vec3[] pts) => new(pts.Select(v => (JsonNode)PhysicsCommands.V(v)).ToArray());
        var data = new JsonObject { ["startFrame"] = s, ["endFrame"] = e, ["fps"] = clip.Fps.ToString() };
        if (what is "com" or "all") data["com"] = Path(t.Com);
        if (what is "hips" or "all") data["hips"] = Path(t.Hips);
        if (what is "feet" or "all")
        {
            data["leftFoot"] = Path(t.LeftFoot);
            data["rightFoot"] = Path(t.RightFoot);
        }
        return new(Array.Empty<Change>(), data);
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}

public sealed class AutoBalanceClip(ObjectId clipId, ObjectId? characterId, IReadOnlyList<long>? frames, long? start, long? end, AutoBalanceOptions options) : ClipEdit(clipId)
{
    public override string Id => "physics.auto_balance";
    public override string Label => "Auto-balance";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        var (_, rig) = ExportCommands.Resolve(p, ClipId, characterId);
        var last = BalanceAnalyzer.LastFrame(clip);
        IReadOnlyList<long> selected;
        if (frames is not null)
        {
            if (frames.Any(f => f > last)) throw new CommandException("invalid_args", $"Frames must be ≤ the clip's last frame ({last}).");
            selected = frames.OrderBy(f => f).ToList();
        }
        else
        {
            var (s, e) = PhysicsCommands.Range(clip, start, end);
            selected = Enumerable.Range(0, (int)(e - s + 1)).Select(i => s + i).ToList();
        }
        if (selected[^1] - selected[0] + 1 > PhysicsCommands.MaxRange)
            throw new CommandException("range_too_large", $"At most {PhysicsCommands.MaxRange} frames per call.");

        var an = new BalanceAnalyzer(rig);
        PhysicsCommands.RequireFeet(an);
        if (an.PelvisBone < 0) throw new CommandException("no_pelvis", $"Rig '{rig.Name}' has no humanoid-mapped pelvis; map it with rig.map_bone.");
        var pelvis = rig.Bones[an.PelvisBone];
        var pelvisTrack = clip.Tracks.Find(t => t.Target.Id == pelvis.Id && t.Channel == TrackChannels.Translation);
        if (pelvisTrack is { Locked: true })
            throw new CommandException("track_locked", $"The translation track of '{pelvis.Name}' is locked; unlock it to auto-balance.");

        var before = an.AnalyzeClip(clip, selected[0], selected[^1]);
        var samples = before.Samples!;
        Pose OriginalPose(long f) => samples.Poses[(int)(f - samples.StartFrame)];
        PoseBalance BalanceAt(ClipBalanceReport r, long f) => r.Frames[(int)(f - r.StartFrame)];

        var corrections = new SortedDictionary<long, BalanceCorrection>();
        foreach (var f in selected)
        {
            var c = AutoBalance.CorrectPose(an, OriginalPose(f), BalanceAt(before, f), options);
            if (c.Changed) corrections[f] = c;
        }
        if (corrections.Count == 0)
            throw new CommandException("already_balanced", "Every selected frame is already balanced (with the margin) or airborne; nothing to correct.");

        // Bones to key: pelvis translation; rotations of legs that IK had to re-solve.
        var rotBones = corrections.Values.SelectMany(c => c.ChangedBones.Skip(1)).Distinct().OrderBy(i => i).ToList();
        var keyFrames = new SortedSet<long>(corrections.Keys);
        foreach (var f in corrections.Keys)
            foreach (var n in new[] { f - 1, f + 1 })
                if (n >= 0 && n <= last && !corrections.ContainsKey(n) && n >= samples.StartFrame && n < samples.StartFrame + samples.Count)
                    keyFrames.Add(n); // hold the original motion on both sides of a corrected run

        if (clip.Skeleton.IsEmpty) clip.Skeleton = rig.Id;
        var existingTimes = KeyCommands.KeyTimes(clip);
        var keys = 0;
        var locked = false;
        foreach (var f in keyFrames)
        {
            var pose = corrections.TryGetValue(f, out var c) ? c.Pose! : OriginalPose(f);
            var time = Flicks.FromFrame(f, clip.Fps);
            var t = pose.LocalOf(pelvis).Translation;
            Key(TargetKind.Bone, pelvis.Id, TrackChannels.Translation, new[] { t.X, t.Y, t.Z }, time);
            foreach (var b in rotBones)
            {
                var q = pose.LocalOf(rig.Bones[b]).Rotation;
                Key(TargetKind.Bone, rig.Bones[b].Id, TrackChannels.Rotation, new[] { q.X, q.Y, q.Z, q.W }, time);
            }
        }
        if (locked) warnings.Add(new Diagnostic("track_locked", "Locked leg tracks were not keyed; feet may slide on corrected frames."));

        // Verify on the edited clip: what playback and export will give.
        var after = an.AnalyzeClip(clip, selected[0], selected[^1]);
        var afterSamples = after.Samples!;
        var list = new JsonArray();
        var stillUnbalanced = new List<long>();
        double maxDrift = 0;
        foreach (var (f, c) in corrections)
        {
            var b0 = BalanceAt(before, f);
            var b1 = BalanceAt(after, f);
            var m0 = samples.Model[(int)(f - samples.StartFrame)];
            var m1 = afterSamples.Model[(int)(f - afterSamples.StartFrame)];
            var drift = 0.0;
            foreach (var fi in b0.ContactFeet)
            {
                var foot = an.Feet.Feet[fi];
                drift = Math.Max(drift, (m1[foot.FootBone].Translation - m0[foot.FootBone].Translation).Length());
                if (foot.ToeBone >= 0) drift = Math.Max(drift, (m1[foot.ToeBone].Translation - m0[foot.ToeBone].Translation).Length());
            }
            maxDrift = Math.Max(maxDrift, drift);
            var afterMargin = Geometry2D.SignedDistance(b0.Polygon, Vec2.Of(b1.Com));
            if (afterMargin < 0) stillUnbalanced.Add(f);
            var entry = new JsonObject
            {
                ["frame"] = f,
                ["marginBefore"] = PhysicsCommands.M(c.MarginBefore),
                ["marginAfter"] = PhysicsCommands.M(afterMargin),
                ["hipShift"] = new JsonArray(PhysicsCommands.R(c.HipShift.X), PhysicsCommands.R(c.HipShift.Z)),
                ["comBefore"] = PhysicsCommands.V(c.ComBefore),
                ["comAfter"] = PhysicsCommands.V(b1.Com),
                ["footDrift"] = Math.Round(drift, 6),
            };
            if (c.Note is not null) entry["note"] = c.Note;
            list.Add(entry);
        }
        if (maxDrift > 1e-3) warnings.Add(new Diagnostic("feet_moved", $"Supporting feet moved up to {maxDrift:0.###} m (legs could not reach); consider a smaller hip shift or bending the knees."));
        if (stillUnbalanced.Count > 0) warnings.Add(new Diagnostic("still_unbalanced", $"{stillUnbalanced.Count} frame(s) could not be fully balanced (limited by maxShift or leg reach)."));

        return new JsonObject
        {
            ["corrected"] = corrections.Count,
            ["keys"] = keys,
            ["keyedFrames"] = new JsonArray(keyFrames.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            ["minMarginBefore"] = PhysicsCommands.M(corrections.Values.Min(c => c.MarginBefore)),
            ["minMarginAfter"] = PhysicsCommands.M(corrections.Keys.Min(f => Geometry2D.SignedDistance(BalanceAt(before, f).Polygon, Vec2.Of(BalanceAt(after, f).Com)))),
            ["maxFootDrift"] = Math.Round(maxDrift, 6),
            ["stillUnbalanced"] = new JsonArray(stillUnbalanced.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
            ["frames"] = list,
        };

        void Key(TargetKind kind, ObjectId target, string channel, double[] value, Flicks time)
        {
            var track = KeyCommands.GetOrCreateTrack(clip, rig, kind, target, channel, null, existingTimes);
            if (track.Locked) { locked = true; return; }
            var existing = track.Keys.Find(k => k.Time == time);
            KeyCommands.InsertKey(track, new Keyframe { Time = time, Value = value, Interp = existing?.Interp ?? Interpolation.Cubic });
            keys++;
        }
    }
}

public sealed class BakeSecondaryMotion(ObjectId clipId, ObjectId? characterId, IReadOnlyList<string> names, string? preset,
    SpringSettings spring, long? start, long? end) : ClipEdit(clipId)
{
    public override string Id => "physics.secondary_motion";
    public override string Label => "Secondary motion";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        var (_, rig) = ExportCommands.Resolve(p, ClipId, characterId);
        var (s, e) = PhysicsCommands.Range(clip, start, end);
        if (e - s < 1) throw new CommandException("invalid_args", "Secondary motion needs at least 2 frames.");

        var bones = new List<int>();
        foreach (var n in names) bones.Add(rig.IndexOf(PoseCommands.ResolveBone(rig, n).Id));
        if (preset is not null)
        {
            var missing = new List<string>();
            foreach (var std in PhysicsCommands.Preset(preset)!)
            {
                if (rig.FindByStandardName(std) is { } b) bones.Add(rig.IndexOf(b.Id));
                else missing.Add(std);
            }
            if (missing.Count > 0) warnings.Add(new Diagnostic("bones_missing", $"Preset bones not mapped in this rig: {string.Join(", ", missing)}."));
        }
        bones = bones.Distinct().ToList();
        if (bones.Count == 0) throw new CommandException("not_found", "None of the preset's bones are mapped in this rig.");

        var ikBones = bones.Where(i => rig.IkChains.Exists(c => c.Upper == rig.Bones[i].Id || c.Lower == rig.Bones[i].Id || c.End == rig.Bones[i].Id)).ToList();
        if (ikBones.Count > 0)
            warnings.Add(new Diagnostic("ik_driven", $"{string.Join(", ", ikBones.Select(i => rig.Bones[i].Name))} belong to IK chains; their keys are overridden while the chain is pinned."));

        var lockedBones = bones.Where(i => clip.Tracks.Exists(t => t.Target.Id == rig.Bones[i].Id && t.Channel == TrackChannels.Rotation && t.Locked)).ToList();
        if (lockedBones.Count > 0)
        {
            warnings.Add(new Diagnostic("track_locked", $"Locked rotation tracks were skipped: {string.Join(", ", lockedBones.Select(i => rig.Bones[i].Name))}."));
            if (lockedBones.Count == bones.Count) throw new CommandException("track_locked", "Every chosen bone's rotation track is locked.");
        }

        // Simulate all chosen bones (locked ones still drive their children), key the unlocked ones.
        var result = SecondaryMotion.Simulate(rig, clip, bones, spring, s, e);
        if (clip.Skeleton.IsEmpty) clip.Skeleton = rig.Id;
        var existingTimes = KeyCommands.KeyTimes(clip);
        var keys = 0;
        for (var b = 0; b < result.Bones.Count; b++)
        {
            var bone = rig.Bones[result.Bones[b]];
            if (lockedBones.Contains(result.Bones[b])) continue;
            var track = KeyCommands.GetOrCreateTrack(clip, rig, TargetKind.Bone, bone.Id, TrackChannels.Rotation, null, existingTimes);
            for (var i = 0; i < result.Rotations[b].Length; i++)
            {
                var time = Flicks.FromFrame(s + i, clip.Fps);
                var q = result.Rotations[b][i];
                var existing = track.Keys.Find(k => k.Time == time);
                KeyCommands.InsertKey(track, new Keyframe { Time = time, Value = new[] { q.X, q.Y, q.Z, q.W }, Interp = existing?.Interp ?? Interpolation.Cubic });
                keys++;
            }
        }
        return new JsonObject
        {
            ["bones"] = new JsonArray(result.Bones.Select(i => (JsonNode)JsonValue.Create(rig.Bones[i].Name)!).ToArray()),
            ["startFrame"] = s,
            ["endFrame"] = e,
            ["keys"] = keys,
            ["maxLagDegrees"] = Math.Round(result.MaxLagDegrees, 3),
            ["naturalFrequencyHz"] = Math.Round(Math.Sqrt(spring.Stiffness) / (2 * Math.PI), 4),
            ["dampingRatio"] = Math.Round(spring.DampingRatio, 4),
        };
    }
}
