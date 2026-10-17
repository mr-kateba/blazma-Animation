using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;
using Blazma.Rigging.Retargeting;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.AI;

public sealed record RetargetOptions
{
    /// <summary>Frame rate of the clip to create (the motion is resampled).</summary>
    public FrameRate Fps { get; init; } = FrameRate.Fps30;

    public string Name { get; init; } = "Generated";

    /// <summary>Id of the clip to create (fixed by the caller so redo recreates the same clip).</summary>
    public ObjectId ClipId { get; init; } = ObjectId.New();

    /// <summary>Pin feet while they are in ground contact (low and slow), with two-bone IK on the legs.</summary>
    public bool FootCleanup { get; init; } = true;

    /// <summary>Drop keys that linear interpolation reproduces within tolerance (keeps the motion editable).</summary>
    public bool ReduceKeys { get; init; } = true;

    /// <summary>The motion is a seamless cycle (last frame = first frame + travel): root travel smoothing wraps around.</summary>
    public bool Cyclic { get; init; }

    public double RotationToleranceDegrees { get; init; } = 0.1;
    public double TranslationToleranceMetres { get; init; } = 0.0005;

    public Dictionary<string, string> Metadata { get; init; } = new();
}

public sealed record RetargetResult(Clip Clip, IReadOnlyList<string> Warnings, IReadOnlyList<string> MappedBones,
    IReadOnlyList<string> UnmappedBones, double Scale, int PinnedFrames, int KeyCount);

/// <summary>
/// Converts a <see cref="MotionResult"/> (e.g. 30-joint SOMA) into an editable draft clip for any humanoid-mapped
/// rig (the mannequin or an imported character).
/// <list type="bullet">
/// <item><b>Rotations:</b> rest-relative world-space delta transfer per mapped bone. For source joint s and target
/// bone b: <c>G_b(t) = Δ_s(t) · C_b · Rest_b</c> with <c>Δ_s(t) = G_s(t) · Rest_s⁻¹</c> (world rotations) and
/// <c>C_b</c> the shortest rotation taking b's rest bone direction (towards its standard child) onto s's. So each
/// bone points the same way in the world as its source bone whatever the two rest poses (T/A-pose) and local
/// bone axes are. Bones between mapped ones and unmapped bones (fingers, face, twist bones) keep their rest
/// rotation.</item>
/// <item><b>Root:</b> the pelvis moves by the source hips' displacement from rest, scaled by the leg-length
/// ratio (hip-height ratio when legs are not mapped). When a bone mapped to "root" sits above the pelvis, the
/// smoothed horizontal travel is moved onto it (the pelvis keeps height and sway relative to it; model-space
/// result unchanged), so export rootMotion=in_place works and the root does not stay at the origin.</item>
/// <item><b>Resampling</b> to the clip's frame rate (slerp/lerp between source frames).</item>
/// <item><b>Foot cleanup</b> (optional): a foot that is low and nearly still is pinned to the mean position of
/// its contact span with analytic two-bone IK (foot orientation kept).</item>
/// </list>
/// Output: a draft clip (IsDraft) with linear keys on every frame, reduced within tolerance.
/// </summary>
public static class MotionRetargeter
{
    public static RetargetResult Retarget(MotionResult motion, Rig rig, RetargetOptions options)
    {
        motion.Validate();
        var src = motion.Skeleton;
        var warnings = new List<string>(motion.Warnings);

        // ---- source rest (model space) and the shared humanoid map (Blazma.Rigging.Retargeting)
        var srcParent = src.Parents.ToArray();
        var srcRest = new Transform[src.Count];
        for (var j = 0; j < src.Count; j++) srcRest[j] = srcParent[j] < 0 ? src.Rest[j] : srcRest[srcParent[j]] * src.Rest[j];
        var srcByStd = new Dictionary<string, int>();
        foreach (var (name, std) in src.StandardNames)
            if (src.IndexOf(name) is var i and >= 0) srcByStd[std] = i;
        HumanoidRetargetMap map;
        try { map = new HumanoidRetargetMap(new RetargetSkeleton(src.Names, srcParent, srcRest, srcByStd), rig); }
        catch (RetargetException e) { throw new MotionGenerationException(e.Code, e.Message); }
        var ev = map.Evaluator;
        var tRest = map.TargetRest;
        var tByStd = map.TargetByStandard;
        var tPelvis = map.TargetPelvis;
        warnings.AddRange(map.Warnings);
        var scale = map.Scale;

        // ---- resample and transfer
        var fps = options.Fps.FramesPerSecond;
        var frames = Math.Max(1, (int)Math.Round(motion.DurationSeconds * fps) + 1);
        var locals = new Transform[frames][];
        var srcLocal = new Quat[src.Count];
        var srcWorldRot = new Quat[src.Count];
        var srcWorldPos = new Vec3[src.Count];
        for (var f = 0; f < frames; f++)
        {
            var s = Math.Min(f / fps * motion.Fps, motion.FrameCount - 1);
            var i0 = (int)Math.Floor(s);
            var i1 = Math.Min(i0 + 1, motion.FrameCount - 1);
            var a = s - i0;
            var root = Vec3.Lerp(motion.RootTranslations[i0], motion.RootTranslations[i1], a);
            for (var j = 0; j < src.Count; j++)
            {
                srcLocal[j] = Quat.Slerp(motion.Rotation(i0, j), motion.Rotation(i1, j), a);
                var lt = srcParent[j] < 0 ? root : src.Rest[j].Translation;
                if (srcParent[j] < 0) { srcWorldRot[j] = srcLocal[j]; srcWorldPos[j] = lt; }
                else
                {
                    srcWorldRot[j] = (srcWorldRot[srcParent[j]] * srcLocal[j]).Normalized();
                    srcWorldPos[j] = srcWorldPos[srcParent[j]] + srcWorldRot[srcParent[j]].Rotate(lt);
                }
            }
            locals[f] = map.Transfer(srcWorldRot, srcWorldPos);
        }

        var pinned = options.FootCleanup ? PinFeet(rig, ev, tByStd, locals, fps, scale, warnings) : 0;
        var travelBone = HumanoidRetargetMap.MoveTravelToRoot(ev, tByStd, tPelvis, tRest, locals, fps, options.Cyclic);
        var clip = BuildClip(rig, options, locals, map.PairOf, tPelvis, travelBone, frames, motion, out var keyCount);
        return new RetargetResult(clip, warnings, map.Mapped, map.Unmapped, scale, pinned, keyCount);
    }

    // ------------------------------------------------------------------ foot contact cleanup

    static int PinFeet(Rig rig, RigEvaluator ev, IReadOnlyDictionary<string, int> byStd, Transform[][] locals, double fps, double scale, List<string> warnings)
    {
        var frames = locals.Length;
        if (frames < 3) return 0;
        var model = locals.Select(ev.Model).ToArray();
        var pinnedFrames = 0;
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            if (!byStd.TryGetValue(H.Thigh(side), out var iu) || !byStd.TryGetValue(H.Calf(side), out var il) || !byStd.TryGetValue(H.Foot(side), out var ie))
                continue;
            if (ev.ParentOf(il) != iu || ev.ParentOf(ie) != il)
            {
                warnings.Add($"Foot cleanup skipped for the {side.ToString().ToLowerInvariant()} leg: thigh, calf and foot are not direct parent/child bones.");
                continue;
            }
            var pos = model.Select(m => m[ie].Translation).ToArray();
            var ground = pos.Min(p => p.Y);
            var heightTol = 0.035 * scale;
            var speedTol = 0.25 * scale;
            var contact = new bool[frames];
            for (var f = 0; f < frames; f++)
            {
                var a = pos[Math.Max(0, f - 1)];
                var b = pos[Math.Min(frames - 1, f + 1)];
                var dt = (Math.Min(frames - 1, f + 1) - Math.Max(0, f - 1)) / fps;
                var speed = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Z - a.Z, 2)) / dt;
                contact[f] = pos[f].Y - ground < heightTol && speed < speedTol;
            }
            for (var f = 0; f < frames;)
            {
                if (!contact[f]) { f++; continue; }
                var end = f;
                while (end + 1 < frames && contact[end + 1]) end++;
                if (end - f + 1 >= 3)
                {
                    var anchor = Vec3.Zero;
                    for (var k = f; k <= end; k++) anchor += pos[k];
                    anchor /= end - f + 1;
                    for (var k = f; k <= end; k++) { PinFrame(rig, ev, locals[k], iu, il, ie, anchor); pinnedFrames++; }
                }
                f = end + 1;
            }
        }
        return pinnedFrames;
    }

    static void PinFrame(Rig rig, RigEvaluator ev, Transform[] loc, int iu, int il, int ie, Vec3 target)
    {
        var m = ev.Model(loc);
        var r = TwoBoneIk.Solve(m[iu].Translation, m[il].Translation, m[ie].Translation, target, null, Vec3.UnitZ);
        var parent = ev.ParentOf(iu);
        var parentRot = parent < 0 ? Quat.Identity : m[parent].Rotation;
        var upper = (r.UpperDelta * m[iu].Rotation).Normalized();
        var lower = (r.LowerDelta * r.UpperDelta * m[il].Rotation).Normalized();
        loc[iu] = loc[iu] with { Rotation = (parentRot.Inverse() * upper).Normalized() };
        loc[il] = loc[il] with { Rotation = (upper.Inverse() * lower).Normalized() };
        loc[ie] = loc[ie] with { Rotation = (lower.Inverse() * m[ie].Rotation).Normalized() };
    }

    // ------------------------------------------------------------------ clip

    static Clip BuildClip(Rig rig, RetargetOptions o, Transform[][] locals, int[] pairOf, int pelvis, int travelBone, int frames, MotionResult motion, out int keyCount)
    {
        var clip = new Clip
        {
            Id = o.ClipId,
            Name = o.Name,
            Fps = o.Fps,
            Duration = Flicks.FromFrame(frames, o.Fps),
            Skeleton = rig.Id,
            IsDraft = true,
            Metadata = new Dictionary<string, string>(o.Metadata),
        };
        clip.Metadata["source.provider"] = motion.Provider;
        clip.Metadata["source.skeleton"] = motion.Skeleton.Key;
        clip.Metadata["source.fps"] = motion.Fps.ToString(System.Globalization.CultureInfo.InvariantCulture);
        clip.Metadata["source.frames"] = motion.FrameCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (k, v) in motion.Metadata) clip.Metadata[k] = v;

        keyCount = BakedTracks.AddBoneTracks(clip, rig, locals,
            b => pairOf[b] >= 0 || BakedTracks.RotationMoves(rig, locals, b),
            b => b == pelvis || b == travelBone,
            o.ReduceKeys, o.RotationToleranceDegrees, o.TranslationToleranceMetres);
        return clip;
    }
}
