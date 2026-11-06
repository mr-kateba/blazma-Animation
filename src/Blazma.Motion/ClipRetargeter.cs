using System.Globalization;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Rigging;
using Blazma.Rigging.Retargeting;

namespace Blazma.Motion;

public sealed record ClipRetargetOptions
{
    /// <summary>Id of the clip to create (fixed by the caller so redo recreates the same clip).</summary>
    public ObjectId ClipId { get; init; } = ObjectId.New();
    public string Name { get; init; } = "Retargeted";

    /// <summary>Output length / source length (1 = same timing; 2 = twice as slow). Used by templates with a requested duration.</summary>
    public double TimeScale { get; init; } = 1;

    /// <summary>Drop keys that linear interpolation reproduces within tolerance.</summary>
    public bool ReduceKeys { get; init; } = true;
    public double RotationToleranceDegrees { get; init; } = 0.05;
    public double TranslationToleranceMetres { get; init; } = 0.0002;

    /// <summary>Map animated IK targets/poles of the source onto the target's matching chains (pinned flags kept).</summary>
    public bool MapControls { get; init; } = true;
}

public sealed record ClipRetargetResult(Clip Clip, IReadOnlyList<string> Warnings, IReadOnlyList<string> MappedBones, IReadOnlyList<string> UnmappedBones,
    double Scale, int KeyCount, int MappedControls);

/// <summary>
/// Rig → rig clip retargeting (clip.retarget, templates). The source clip is evaluated on its rig every output frame exactly
/// like playback (rest base, pinned IK solved), then transferred with <see cref="HumanoidRetargetMap"/>: world-space rotation
/// deltas with rest-pose/axis compensation for every humanoid-mapped bone, pelvis (and root) travel scaled by the leg-length
/// ratio; unmapped target bones stay at rest (warning). When the source rig has no root bone but the target has one, the
/// smoothed horizontal travel is moved onto it (D30). Animated IK targets and poles of the source are mapped onto the
/// target's chains with the same end bone (positions relative to the paired rest joint, scaled; rotations like bones;
/// pinned flags copied as stepped keys), so pinned feet stay pinned. Markers, events, the loop range and prop tracks are copied
/// (scaled in time). Output: a normal clip with linear keys, reduced within tolerance.
/// </summary>
public static class ClipRetargeter
{
    /// <summary>Upper bound of output frames × target bones per call.</summary>
    public const long MaxSamples = 2_000_000;

    /// <summary>Largest <see cref="ClipRetargetOptions.TimeScale"/> (output 100× longer than the source).</summary>
    public const double MaxTimeScale = 100;

    public static ClipRetargetResult Retarget(Clip source, Rig sourceRig, Rig targetRig, ClipRetargetOptions options)
    {
        if (!(options.TimeScale > 0) || !double.IsFinite(options.TimeScale) || options.TimeScale > MaxTimeScale)
            throw new RetargetException("invalid_args", $"The time scale must be greater than 0 and at most {MaxTimeScale} (got {options.TimeScale}).");
        var map = new HumanoidRetargetMap(RetargetSkeleton.FromRig(sourceRig), targetRig);
        var warnings = new List<string>(map.Warnings);
        var srcEv = new RigEvaluator(sourceRig);
        var ev = map.Evaluator;
        var fps = source.Fps;
        var srcLast = source.DurationFrames;
        var lastFrame = Math.Max(1, (long)Math.Round(srcLast * options.TimeScale));
        var frames = (int)(lastFrame + 1);
        if ((long)frames * Math.Max(1, targetRig.Bones.Count) > MaxSamples)
            throw new RetargetException("too_large", $"{frames} frames × {targetRig.Bones.Count} bones is more than {MaxSamples:N0} samples.");

        var locals = new Transform[frames][];
        var srcPoses = new Pose[frames];
        var srcModels = new Transform[frames][];
        for (var f = 0; f < frames; f++)
        {
            var t = SourceTime(f, fps, options.TimeScale, source.Duration);
            var pose = ClipEvaluator.Evaluate(source, sourceRig, t);
            var model = srcEv.Model(pose);
            srcPoses[f] = pose;
            srcModels[f] = model;
            locals[f] = map.Transfer(model.Select(m => m.Rotation).ToArray(), model.Select(m => m.Translation).ToArray());
        }
        var travelBone = map.SourceRoot < 0
            ? HumanoidRetargetMap.MoveTravelToRoot(ev, map.TargetByStandard, map.TargetPelvis, map.TargetRest, locals, fps.FramesPerSecond)
            : map.TargetRoot;

        var clip = new Clip
        {
            Id = options.ClipId,
            Name = options.Name,
            Fps = fps,
            Duration = Flicks.FromFrame(lastFrame, fps),
            Skeleton = targetRig.Id,
            IsDraft = false,
            Metadata = new Dictionary<string, string>(source.Metadata),
        };
        clip.Metadata["retarget.sourceClip"] = source.Id.ToString();
        clip.Metadata["retarget.sourceRig"] = sourceRig.Name;
        clip.Metadata["retarget.scale"] = map.Scale.ToString("0.#####", CultureInfo.InvariantCulture);
        if (options.TimeScale != 1) clip.Metadata["retarget.timeScale"] = options.TimeScale.ToString(CultureInfo.InvariantCulture);
        CopyTiming(source, clip, options.TimeScale, warnings);
        CopyCombat(source, sourceRig, targetRig, clip, options.TimeScale, map.Scale, warnings);

        var keyCount = BakedTracks.AddBoneTracks(clip, targetRig, locals,
            b => map.PairOf[b] >= 0 || BakedTracks.RotationMoves(targetRig, locals, b),
            b => b == map.TargetPelvis || b == travelBone || BakedTracks.TranslationMoves(targetRig, locals, b),
            options.ReduceKeys, options.RotationToleranceDegrees, options.TranslationToleranceMetres);

        var mappedControls = 0;
        if (options.MapControls)
            mappedControls = MapControls(source, sourceRig, targetRig, map, srcPoses, srcModels, locals, clip, options, warnings, ref keyCount);
        return new ClipRetargetResult(clip, warnings, map.Mapped, map.Unmapped, map.Scale, keyCount, mappedControls);
    }

    /// <summary>Source time sampled for output frame <paramref name="f"/> (clamped to the source clip).</summary>
    public static Flicks SourceTime(long f, FrameRate fps, double timeScale, Flicks sourceDuration)
    {
        if (timeScale == 1) return Flicks.FromFrame(f, fps);
        var t = Flicks.FromSeconds(f / fps.FramesPerSecond / timeScale);
        return t > sourceDuration ? sourceDuration : t;
    }

    static Flicks Scaled(Flicks t, Clip target, double timeScale) =>
        timeScale == 1 ? t : Flicks.FromFrame((long)Math.Round(t.ToFrameExact(target.Fps) * timeScale), target.Fps);

    static void CopyTiming(Clip source, Clip clip, double timeScale, List<string> warnings)
    {
        clip.Loop = new LoopSettings
        {
            Enabled = source.Loop.Enabled,
            Start = Scaled(source.Loop.Start, clip, timeScale),
            End = source.Loop.End.Value == 0 ? Flicks.Zero : Scaled(source.Loop.End, clip, timeScale),
        };
        foreach (var m in source.Markers)
            clip.Markers.Add(new Marker { Name = m.Name, Phase = m.Phase, Time = Scaled(m.Time, clip, timeScale) });
        var late = 0;
        foreach (var e in source.Events)
        {
            var t = Scaled(e.Time, clip, timeScale);
            if (!CombatLimits.InTime(t)) { late++; continue; }
            clip.Events.Add(new AnimationEvent { Name = e.Name, Kind = e.Kind, Data = new Dictionary<string, string>(e.Data), Time = t });
        }
        if (late > 0) warnings.Add($"{late} event(s) would land after {CombatLimits.MaxTime.ToSeconds():N0} s once re-timed; dropped.");
        // Prop tracks do not depend on the skeleton (a world transform, or an offset from the attachment bone): copied as they
        // are, re-timed. Keys that land on the same frame after re-timing keep the last one.
        foreach (var t in source.Tracks.Where(t => t.Target.Kind == TargetKind.Prop))
        {
            var copy = new Track
            {
                Target = new TrackTarget { Kind = t.Target.Kind, Id = t.Target.Id }, Channel = t.Channel, Group = t.Group, Muted = t.Muted, Locked = t.Locked,
            };
            foreach (var k in t.Keys)
            {
                var key = new Keyframe
                {
                    Time = Scaled(k.Time, clip, timeScale), Value = (double[])k.Value.Clone(), Interp = k.Interp,
                    // Tangents are per second: a slower clip changes the value more slowly.
                    InTangent = k.InTangent?.Select(v => v / timeScale).ToArray(), OutTangent = k.OutTangent?.Select(v => v / timeScale).ToArray(),
                };
                if (copy.Keys.Count > 0 && copy.Keys[^1].Time >= key.Time) copy.Keys[^1] = key;
                else copy.Keys.Add(key);
            }
            clip.Tracks.Add(copy);
        }
    }

    /// <summary>
    /// Combat data (schema v5): phases re-timed; hitboxes on bones move to the target bone with the same humanoid name, their
    /// offset carried through the rest pose (model-space placement relative to the joint, scaled by the leg-length ratio) and their
    /// size scaled; hitboxes on props are copied as they are. A hitbox whose bone has no humanoid counterpart is dropped (warning).
    /// </summary>
    static void CopyCombat(Clip source, Rig sourceRig, Rig targetRig, Clip clip, double timeScale, double scale, List<string> warnings)
    {
        if (source.Combat is not { } c) return;
        var srcRest = new RigEvaluator(sourceRig).RestModel();
        var dstRest = new RigEvaluator(targetRig).RestModel();
        var s = double.IsFinite(scale) && scale > 0 ? scale : 1;
        var combat = new ClipCombat();
        // Re-timed times stay inside the new clip and the archive's limit (CombatLimits.MaxTime).
        var limit = clip.Duration < CombatLimits.MaxTime ? clip.Duration : CombatLimits.MaxTime;
        var clamped = false;
        Flicks Fit(Flicks t)
        {
            var r = Scaled(t, clip, timeScale);
            if (r <= limit) return r;
            clamped = true;
            return limit;
        }
        if (c.Phases is { } p)
            combat.Phases = new CombatPhases
            {
                StartupStart = Fit(p.StartupStart), ActiveStart = Fit(p.ActiveStart), RecoveryStart = Fit(p.RecoveryStart), End = Fit(p.End),
            };
        if (combat.Phases is { } q && q.RecoveryStart <= q.ActiveStart)
        {
            combat.Phases = null;
            warnings.Add("The attack phases vanish after re-timing; dropped.");
        }
        else if (clamped) warnings.Add("The attack phases ran past the end of the re-timed clip; they were cut at its end.");
        foreach (var h in c.Hitboxes)
        {
            var copy = new Hitbox
            {
                Id = h.Id, Name = h.Name, Shape = h.Shape.Clone(), Damage = h.Damage, Knockback = h.Knockback, HitstopFrames = h.HitstopFrames,
                Tags = new List<string>(h.Tags),
                Attachment = new HitboxAttachment { Kind = h.Attachment.Kind, Target = h.Attachment.Target, Offset = h.Attachment.Offset },
            };
            clamped = false;
            copy.Active = h.Active.Select(r => new ClipRange { Start = Fit(r.Start), End = Fit(r.End) }).Where(r => r.End > r.Start).ToList();
            if (copy.Active.Count == 0) { warnings.Add($"Hitbox '{h.Name}': its active frames vanish after re-timing (or lie past the clip's end); dropped."); continue; }
            if (clamped) warnings.Add($"Hitbox '{h.Name}': active frames past the end of the re-timed clip were cut.");
            if (h.Attachment.Kind == HitboxAttachKind.Bone)
            {
                var si = sourceRig.IndexOf(h.Attachment.Target);
                var std = si < 0 ? null : sourceRig.StandardNameOf(h.Attachment.Target);
                var target = std is null ? null : targetRig.FindByStandardName(std);
                if (target is null) { warnings.Add($"Hitbox '{h.Name}': its bone has no humanoid counterpart on '{targetRig.Name}'; dropped."); continue; }
                var ti = targetRig.IndexOf(target.Id);
                var m = srcRest[si] * h.Attachment.Offset;
                var placed = new Transform(dstRest[ti].Translation + (m.Translation - srcRest[si].Translation) * s, m.Rotation, Vec3.One);
                copy.Attachment.Target = target.Id;
                copy.Attachment.Offset = RigEvaluator.LocalFromModel(dstRest[ti], placed) with { Scale = Vec3.One };
                copy.Shape.Radius = Math.Min(CombatLimits.MaxShapeSize, copy.Shape.Radius * s);
                copy.Shape.Length = Math.Min(CombatLimits.MaxShapeSize, copy.Shape.Length * s);
                copy.Shape.Size = new Vec3(Math.Min(CombatLimits.MaxShapeSize, copy.Shape.Size.X * s), Math.Min(CombatLimits.MaxShapeSize, copy.Shape.Size.Y * s),
                    Math.Min(CombatLimits.MaxShapeSize, copy.Shape.Size.Z * s));
                if (CombatLimits.CheckOffset(copy.Attachment.Offset) is not null) { warnings.Add($"Hitbox '{h.Name}': its offset is out of range on the target; dropped."); continue; }
            }
            combat.Hitboxes.Add(copy);
        }
        if (combat.Phases is not null || combat.Hitboxes.Count > 0) clip.Combat = combat;
    }

    static int MapControls(Clip source, Rig sourceRig, Rig targetRig, HumanoidRetargetMap map, Pose[] srcPoses, Transform[][] srcModels,
        Transform[][] locals, Clip clip, ClipRetargetOptions o, List<string> warnings, ref int keyCount)
    {
        var animated = source.Tracks.Where(t => t.Target.Kind == TargetKind.Control && !t.Muted).Select(t => t.Target.Id).ToHashSet();
        if (animated.Count == 0) return 0;
        var frames = locals.Length;
        var tModels = locals.Select(map.Evaluator.Model).ToArray();
        var srcRestModel = new RigEvaluator(sourceRig).RestModel();
        var mapped = 0;
        foreach (var sChain in sourceRig.IkChains)
        {
            var targetAnimated = animated.Contains(sChain.Target);
            var poleAnimated = !sChain.Pole.IsEmpty && animated.Contains(sChain.Pole);
            if (!targetAnimated && !poleAnimated) continue;
            var endStd = sourceRig.StandardNameOf(sChain.End);
            var midStd = sourceRig.StandardNameOf(sChain.Lower);
            var tEnd = endStd is null ? null : targetRig.FindByStandardName(endStd);
            var tChain = tEnd is null ? null : targetRig.IkChains.Find(c => c.End == tEnd.Id);
            if (tChain is null || endStd is null)
            {
                warnings.Add($"IK chain '{sChain.Name}' has no matching chain on '{targetRig.Name}'; its animated controls were not mapped (the bones still follow the motion).");
                continue;
            }
            var tEndIndex = targetRig.IndexOf(tChain.End);
            var sEndIndex = sourceRig.IndexOf(sChain.End);
            var group = "IK";
            if (targetAnimated)
            {
                var pos = new double[frames][];
                var rot = new Quat[frames];
                var pin = new double[frames][];
                for (var f = 0; f < frames; f++)
                {
                    var st = PoseSolver.StateOf(sourceRig, srcPoses[f], sChain.Target);
                    var p = map.MapPoint(endStd, st.Transform.Translation);
                    pos[f] = new[] { p.X, p.Y, p.Z };
                    var q = (st.Transform.Rotation * srcRestModel[sEndIndex].Rotation.Inverse() * map.Correction[tEndIndex] * map.TargetRest[tEndIndex].Rotation).Normalized();
                    if (f > 0 && Quat.Dot(q, rot[f - 1]) < 0) q = new Quat(-q.X, -q.Y, -q.Z, -q.W);
                    rot[f] = q;
                    pin[f] = new[] { st.Pinned ? 1.0 : 0.0 };
                }
                keyCount += AddVectorTrack(clip, tChain.Target, group, TrackChannels.Translation, pos, o);
                var keepR = o.ReduceKeys ? BakedTracks.Reduce(frames, (i, j, k) => Quat.AngleBetween(Quat.Slerp(rot[i], rot[j], (double)(k - i) / (j - i)), rot[k]) <= o.RotationToleranceDegrees * Math.PI / 180)
                    : Enumerable.Range(0, frames).ToList();
                clip.Tracks.Add(BakedTracks.Track(TargetKind.Control, tChain.Target, group, TrackChannels.Rotation, keepR.Select(f => (f, new[] { rot[f].X, rot[f].Y, rot[f].Z, rot[f].W })), clip.Fps));
                keyCount += keepR.Count;
                // Pinned: a stepped key wherever the value changes (plus the first frame).
                var pinKeys = Enumerable.Range(0, frames).Where(f => f == 0 || pin[f][0] != pin[f - 1][0]).Select(f => (f, pin[f])).ToList();
                clip.Tracks.Add(BakedTracks.Track(TargetKind.Control, tChain.Target, group, TrackChannels.Pinned, pinKeys, clip.Fps));
                keyCount += pinKeys.Count;
                mapped++;
            }
            if (poleAnimated && !tChain.Pole.IsEmpty && targetRig.FindControl(tChain.Pole) is { } tPole && sourceRig.FindControl(sChain.Pole) is { } sPole)
            {
                var pos = new double[frames][];
                for (var f = 0; f < frames; f++)
                {
                    var model = PoseSolver.ControlModel(sourceRig, srcPoses[f], sPole, srcModels[f]);
                    var p = map.MapPoint(midStd ?? endStd, model.Translation);
                    var stored = PoseSolver.ToControlSpace(tPole, new Transform(p, Quat.Identity, Vec3.One), tModels[f]).Translation;
                    pos[f] = new[] { stored.X, stored.Y, stored.Z };
                }
                keyCount += AddVectorTrack(clip, tPole.Id, group, TrackChannels.Translation, pos, o);
                mapped++;
            }
        }
        return mapped;
    }

    static int AddVectorTrack(Clip clip, ObjectId control, string group, string channel, double[][] v, ClipRetargetOptions o)
    {
        var frames = v.Length;
        Vec3 P(int i) => new(v[i][0], v[i][1], v[i][2]);
        var keep = o.ReduceKeys ? BakedTracks.Reduce(frames, (i, j, k) => (Vec3.Lerp(P(i), P(j), (double)(k - i) / (j - i)) - P(k)).Length() <= o.TranslationToleranceMetres)
            : Enumerable.Range(0, frames).ToList();
        clip.Tracks.Add(BakedTracks.Track(TargetKind.Control, control, group, channel, keep.Select(f => (f, v[f])), clip.Fps));
        return keep.Count;
    }
}
