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

    /// <summary>Drop keys that the key interpolation reproduces within tolerance (keeps the motion editable).</summary>
    public bool ReduceKeys { get; init; } = true;

    /// <summary>Interpolation of the baked keys. Cubic (auto-clamped tangents, the default) keeps velocities continuous between
    /// keys; linear keys make reduced motion look mechanical (velocity jumps at every key).</summary>
    public Interpolation KeyInterpolation { get; init; } = Interpolation.Cubic;

    /// <summary>The motion is a seamless cycle (last frame = first frame + travel): root travel smoothing wraps around.</summary>
    public bool Cyclic { get; init; }

    /// <summary>Run the physics polish (Blazma.Physics, applied by the command layer after retargeting / assembling, before key
    /// reduction): planted feet, nothing below the ground, ballistic flights, smooth step transitions, falls and follow-through.</summary>
    public bool PhysicsPolish { get; init; }

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
/// <item><b>Foot cleanup</b> (optional): heel and ball pivots that are low and nearly still are pinned to the mean position
/// of their contact span (on the ground) with analytic two-bone IK, foot orientation kept, easing in and out around each contact
/// (see <see cref="PinFeet"/>).</item>
/// </list>
/// Output: a draft clip (IsDraft) with cubic (auto-clamped) keys on every frame, reduced within tolerance (verified against the dense curve).
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

        var pinned = options.FootCleanup ? PinFeet(rig, ev, tByStd, locals, fps, scale, options.Cyclic, warnings) : 0;
        var travelBone = HumanoidRetargetMap.MoveTravelToRoot(ev, tByStd, tPelvis, tRest, locals, fps, options.Cyclic);
        var clip = BuildClip(rig, options, locals, map.PairOf, tPelvis, travelBone, frames, motion, out var keyCount);
        return new RetargetResult(clip, warnings, map.Mapped, map.Unmapped, scale, pinned, keyCount);
    }

    // ------------------------------------------------------------------ foot contact cleanup

    /// <summary>
    /// Foot planting with pinned IK targets. Each foot has two sole pivots: under the heel (on the foot bone) and under the ball
    /// (toe joint, on the toe bone when there is one). A pivot is in contact while it is near the ground and slow; each contact span gets one anchor
    /// (its mean horizontal position). Inside contact the ankle is shifted by the pivot's horizontal offset from its anchor (and lifted
    /// when the pivot is below the ground)
    /// (heel and ball weighted, so the heel → ball roll hands over smoothly), so planted feet neither slide nor sink while the
    /// foot still rolls heel → toe. Over <c>blend</c> frames before and after a contact the shift eases (smoothstep) to zero, so
    /// the leg never pops. Cyclic motions are treated as periodic (spans and blends wrap around the loop seam).
    /// </summary>
    static int PinFeet(Rig rig, RigEvaluator ev, IReadOnlyDictionary<string, int> byStd, Transform[][] locals, double fps, double scale,
        bool cyclic, List<string> warnings)
    {
        var frames = locals.Length;
        if (frames < 3) return 0;
        var period = cyclic && frames > 3 ? frames - 1 : frames; // cyclic: frame period repeats frame 0 (+ travel)
        var model = locals.Select(ev.Model).ToArray();
        var rest = ev.RestModel();
        var pelvis = byStd.TryGetValue(H.Pelvis, out var pv) ? pv : 0;
        var travel = cyclic ? model[frames - 1][pelvis].Translation - model[0][pelvis].Translation : Vec3.Zero;
        travel = new Vec3(travel.X, 0, travel.Z);
        var lowest = double.MaxValue;
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            if (byStd.TryGetValue(H.Foot(side), out var fb)) lowest = Math.Min(lowest, rest[fb].Translation.Y);
            if (byStd.TryGetValue(H.Toe(side), out var tb)) lowest = Math.Min(lowest, rest[tb].Translation.Y);
        }
        var ground = lowest is >= 0 and <= 0.2 ? 0 : lowest;
        var blend = Math.Max(2, (int)Math.Round(0.12 * fps));
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

            // Sole pivots (foot-bone local): under the ball (toe joint, or 0.13·scale ahead) and 0.35 of the foot length behind the ankle.
            var ankleRest = rest[ie].Translation;
            var ballRest = byStd.TryGetValue(H.Toe(side), out var it) && ev.IsAncestorOrSelf(ie, it) ? rest[it].Translation : ankleRest + new Vec3(0, 0, 0.13 * scale);
            var footLen = Math.Max(0.05 * scale, Math.Sqrt(Math.Pow(ballRest.X - ankleRest.X, 2) + Math.Pow(ballRest.Z - ankleRest.Z, 2)));
            var fwd = new Vec3(ballRest.X - ankleRest.X, 0, ballRest.Z - ankleRest.Z);
            fwd = fwd.Length() > 1e-9 ? fwd.Normalized() : Vec3.UnitZ;
            // The ball pivot rides on the toe bone when there is one: the toes stay flat on the ground while the heel rises.
            var ballBone = byStd.TryGetValue(H.Toe(side), out var tb2) && ev.IsAncestorOrSelf(ie, tb2) ? tb2 : ie;
            var pivots = new[]
            {
                (Bone: ie, Local: rest[ie].Inverse().TransformPoint(new Vec3(ankleRest.X, ground, ankleRest.Z) - fwd * (0.35 * footLen))),
                (Bone: ballBone, Local: rest[ballBone].Inverse().TransformPoint(new Vec3(ballRest.X, ground, ballRest.Z))),
            };

            var corr = new Vec3[period];
            var weight = new double[period];
            var inContact = new bool[period];
            foreach (var (pivotBone, local) in pivots)
            {
                var pos = model.Select(m => m[pivotBone].TransformPoint(local)).ToArray();
                Vec3 At(int k) // periodic unwrap for cyclic motions
                {
                    if (!cyclic) return pos[Math.Clamp(k, 0, frames - 1)];
                    var w = (int)Math.Floor((double)k / period);
                    return pos[k - w * period] + travel * w;
                }
                var contact = new bool[period];
                for (var f = 0; f < period; f++)
                {
                    int f0 = cyclic ? f - 1 : Math.Max(0, f - 1), f1 = cyclic ? f + 1 : Math.Min(frames - 1, f + 1);
                    var d = At(f1) - At(f0);
                    var speed = Math.Sqrt(d.X * d.X + d.Z * d.Z) / ((f1 - f0) / fps);
                    contact[f] = pos[f].Y - ground < 0.02 * scale && speed < 0.3 * scale;
                }
                foreach (var (start, len) in Spans(contact, cyclic))
                {
                    if (len < 3) continue;
                    var anchor = Vec3.Zero;
                    for (var k = start; k < start + len; k++) anchor += At(k);
                    anchor /= len;
                    for (var k = start; k < start + len; k++)
                    {
                        var p = At(k);
                        var edge = Math.Min(k - start, start + len - 1 - k);
                        var w = Math.Min(1, (edge + 1) / 3.0);
                        var fi = ((k % period) + period) % period;
                        corr[fi] += new Vec3(anchor.X - p.X, Math.Max(0, ground - p.Y), anchor.Z - p.Z) * w; // lift out of the ground, never pull down
                        weight[fi] += w;
                        inContact[fi] = true;
                    }
                }
            }
            for (var f = 0; f < period; f++) if (inContact[f]) corr[f] /= weight[f];

            // Ease the shift in and out around each contact.
            var final = new Vec3[frames];
            for (var f = 0; f < period; f++)
            {
                if (inContact[f]) { final[f] = corr[f]; continue; }
                var sum = Vec3.Zero;
                var wsum = 0.0;
                foreach (var dir in new[] { -1, 1 })
                    for (var d = 1; d <= blend; d++)
                    {
                        var k = f + dir * d;
                        if (cyclic) k = ((k % period) + period) % period;
                        else if (k < 0 || k >= period) break;
                        if (!inContact[k]) continue;
                        var x = 1 - (double)d / (blend + 1);
                        var w = x * x * (3 - 2 * x);
                        sum += corr[k] * w;
                        wsum += w;
                        break;
                    }
                final[f] = wsum > 1 ? sum / wsum : sum;
            }
            if (cyclic) final[frames - 1] = final[0];
            for (var f = 0; f < frames; f++)
            {
                if (final[f].LengthSquared() < 1e-14) continue;
                PinFrame(rig, ev, locals[f], iu, il, ie, ev.Model(locals[f])[ie].Translation + final[f]);
                if (f < period && inContact[f]) pinnedFrames++;
            }
        }
        return pinnedFrames;
    }

    /// <summary>Runs of true values as (start, length); with <paramref name="cyclic"/> a run may wrap past the end (start + length &gt; count).</summary>
    static IEnumerable<(int Start, int Length)> Spans(bool[] on, bool cyclic)
    {
        var n = on.Length;
        if (on.All(x => x)) { yield return (0, n); yield break; }
        var first = 0;
        if (cyclic) while (on[first]) first++; // start scanning at an "off" frame so wrapped runs stay whole
        for (var i = 0; i < n;)
        {
            var f = (first + i) % n;
            if (!on[f]) { i++; continue; }
            var len = 0;
            while (i < n && on[(first + i) % n]) { len++; i++; }
            yield return (cyclic ? first + i - len : f, len);
        }
    }

    static void PinFrame(Rig rig, RigEvaluator ev, Transform[] loc, int iu, int il, int ie, Vec3 target)
    {
        var m = ev.Model(loc);
        // Soft reach limit: a target pushed towards full extension saturates smoothly instead of snapping the knee straight
        // (never below the leg's own extension in this frame).
        var hip = m[iu].Translation;
        var reach = (m[il].Translation - hip).Length() + (m[ie].Translation - m[il].Translation).Length();
        var toTarget = target - hip;
        var dist = toTarget.Length();
        var own = (m[ie].Translation - hip).Length();
        double limit = 0.9995 * reach, knee = 0.015 * reach;
        if (dist > own && dist > limit - knee)
            target = hip + toTarget * (Math.Max(own, limit - knee * Math.Exp(-(dist - (limit - knee)) / knee)) / dist);
        var r = TwoBoneIk.Solve(hip, m[il].Translation, m[ie].Translation, target, null, Vec3.UnitZ);
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
            o.ReduceKeys, o.RotationToleranceDegrees, o.TranslationToleranceMetres, o.KeyInterpolation, o.Cyclic);
        return clip;
    }
}
