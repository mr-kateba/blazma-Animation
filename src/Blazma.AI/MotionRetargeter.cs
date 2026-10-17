using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Rigging;
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
/// ratio (hip-height ratio when legs are not mapped).</item>
/// <item><b>Resampling</b> to the clip's frame rate (slerp/lerp between source frames).</item>
/// <item><b>Foot cleanup</b> (optional): a foot that is low and nearly still is pinned to the mean position of
/// its contact span with analytic two-bone IK (foot orientation kept).</item>
/// </list>
/// Output: a draft clip (IsDraft) with linear keys on every frame, reduced within tolerance.
/// </summary>
public static class MotionRetargeter
{
    /// <summary>Standard child used to define a bone's direction (first one mapped on both sides wins).</summary>
    static readonly Dictionary<string, string[]> AimChildren = BuildAim();

    static Dictionary<string, string[]> BuildAim()
    {
        var d = new Dictionary<string, string[]>
        {
            [H.Pelvis] = new[] { H.Spine1, H.Spine2, H.Chest, H.Neck },
            [H.Spine1] = new[] { H.Spine2, H.Chest, H.Neck },
            [H.Spine2] = new[] { H.Chest, H.Neck },
            [H.Chest] = new[] { H.Neck, H.Head },
            [H.Neck] = new[] { H.Head },
        };
        foreach (var s in new[] { Side.Left, Side.Right })
        {
            d[H.Clavicle(s)] = new[] { H.UpperArm(s) };
            d[H.UpperArm(s)] = new[] { H.LowerArm(s), H.Hand(s) };
            d[H.LowerArm(s)] = new[] { H.Hand(s) };
            d[H.Thigh(s)] = new[] { H.Calf(s), H.Foot(s) };
            d[H.Calf(s)] = new[] { H.Foot(s) };
            d[H.Foot(s)] = new[] { H.Toe(s) };
        }
        return d;
    }

    public static RetargetResult Retarget(MotionResult motion, Rig rig, RetargetOptions options)
    {
        motion.Validate();
        var src = motion.Skeleton;
        var warnings = new List<string>(motion.Warnings);

        // ---- source rest (model space)
        var srcParent = src.Parents.ToArray();
        var srcRest = new Transform[src.Count];
        for (var j = 0; j < src.Count; j++) srcRest[j] = srcParent[j] < 0 ? src.Rest[j] : srcRest[srcParent[j]] * src.Rest[j];
        var srcByStd = new Dictionary<string, int>();
        foreach (var (name, std) in src.StandardNames)
            if (src.IndexOf(name) is var i and >= 0) srcByStd[std] = i;

        // ---- target rest
        var ev = new RigEvaluator(rig);
        var tRest = ev.RestModel();
        var tByStd = new Dictionary<string, int>();
        foreach (var m in rig.HumanoidMap)
            if (ev.IndexOf(m.Bone) is var i and >= 0) tByStd[m.Standard] = i;
        if (!tByStd.TryGetValue(H.Pelvis, out var tPelvis) || !srcByStd.TryGetValue(H.Pelvis, out var sPelvis))
            throw new MotionGenerationException("not_humanoid", $"Rig '{rig.Name}' has no bone mapped to '{H.Pelvis}'; map the humanoid bones first (rig.map_bone).");

        // ---- pairs and rest corrections
        var pairOf = new int[rig.Bones.Count];               // target bone → source joint (−1 = not driven)
        Array.Fill(pairOf, -1);
        foreach (var (std, ti) in tByStd)
            if (srcByStd.TryGetValue(std, out var si)) pairOf[ti] = si;
        var correction = new Quat[rig.Bones.Count];
        for (var b = 0; b < rig.Bones.Count; b++)
        {
            correction[b] = Quat.Identity;
            if (pairOf[b] < 0) continue;
            var std = rig.StandardNameOf(rig.Bones[b].Id)!;
            Quat? c = null;
            if (AimChildren.TryGetValue(std, out var kids))
                foreach (var kid in kids)
                    if (tByStd.TryGetValue(kid, out var tk) && srcByStd.TryGetValue(kid, out var sk))
                    {
                        var dt = tRest[tk].Translation - tRest[b].Translation;
                        var ds = srcRest[sk].Translation - srcRest[pairOf[b]].Translation;
                        if (dt.Length() > 1e-6 && ds.Length() > 1e-6) { c = Quat.FromTo(dt, ds); break; }
                    }
            if (c is null)
            {
                // Leaf (hand, head, toe): reuse the nearest driven ancestor's correction.
                c = Quat.Identity;
                for (var p = ev.ParentOf(b); p >= 0; p = ev.ParentOf(p))
                    if (pairOf[p] >= 0) { c = correction[p]; break; }
            }
            correction[b] = c.Value;
        }

        var mapped = new List<string>();
        var unmapped = new List<string>();
        for (var b = 0; b < rig.Bones.Count; b++) (pairOf[b] >= 0 ? mapped : unmapped).Add(rig.Bones[b].Name);
        if (unmapped.Count > 0)
            warnings.Add($"{unmapped.Count} bone(s) are not driven by the generated motion and stay at rest: {string.Join(", ", unmapped.Take(12))}{(unmapped.Count > 12 ? ", …" : "")}.");
        foreach (var std in H.Required)
            if (!tByStd.ContainsKey(std)) warnings.Add($"The rig has no bone mapped to '{std}'; that part of the motion is lost.");

        // ---- root scale
        var scale = LegLength(tRest, tByStd) is { } tl && LegLength(srcRest, srcByStd) is { } sl && sl > 1e-6 ? tl / sl
            : srcRest[sPelvis].Translation.Y > 1e-6 && tRest[tPelvis].Translation.Y > 1e-6 ? tRest[tPelvis].Translation.Y / srcRest[sPelvis].Translation.Y : 1.0;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;

        // ---- resample and transfer
        var fps = options.Fps.FramesPerSecond;
        var frames = Math.Max(1, (int)Math.Round(motion.DurationSeconds * fps) + 1);
        var locals = new Transform[frames][];
        var srcLocal = new Quat[src.Count];
        var srcWorldRot = new Quat[src.Count];
        var srcWorldPos = new Vec3[src.Count];
        var srcRestInv = srcRest.Select(t => t.Rotation.Inverse()).ToArray();
        var restLocals = rig.Bones.Select(b => b.Rest).ToArray();
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

            var loc = new Transform[rig.Bones.Count];
            var world = new Transform[rig.Bones.Count];
            for (var b = 0; b < rig.Bones.Count; b++)
            {
                var p = ev.ParentOf(b);
                var parentWorld = p < 0 ? Transform.Identity : world[p];
                var l = restLocals[b];
                if (pairOf[b] is var sj and >= 0)
                {
                    var g = (srcWorldRot[sj] * srcRestInv[sj] * correction[b] * tRest[b].Rotation).Normalized();
                    l = l with { Rotation = (parentWorld.Rotation.Inverse() * g).Normalized() };
                }
                if (b == tPelvis)
                {
                    var d = (srcWorldPos[sPelvis] - srcRest[sPelvis].Translation) * scale;
                    var desired = tRest[b].Translation + d;
                    var w = new Transform(desired, (parentWorld.Rotation * l.Rotation).Normalized(), parentWorld.Scale);
                    l = l with { Translation = RigEvaluator.LocalFromModel(parentWorld, w).Translation };
                }
                loc[b] = l;
                world[b] = p < 0 ? l : parentWorld * l;
            }
            locals[f] = loc;
        }

        var pinned = options.FootCleanup ? PinFeet(rig, ev, tByStd, locals, fps, scale, warnings) : 0;
        var clip = BuildClip(rig, options, locals, pairOf, tPelvis, frames, motion, out var keyCount);
        return new RetargetResult(clip, warnings, mapped, unmapped, scale, pinned, keyCount);
    }

    static double? LegLength(Transform[] rest, Dictionary<string, int> byStd)
    {
        double? total = null;
        foreach (var s in new[] { Side.Left, Side.Right })
            if (byStd.TryGetValue(H.Thigh(s), out var t) && byStd.TryGetValue(H.Calf(s), out var c) && byStd.TryGetValue(H.Foot(s), out var f))
            {
                var len = (rest[c].Translation - rest[t].Translation).Length() + (rest[f].Translation - rest[c].Translation).Length();
                total = total is null ? len : (total + len) / 2;
            }
        return total;
    }

    // ------------------------------------------------------------------ foot contact cleanup

    static int PinFeet(Rig rig, RigEvaluator ev, Dictionary<string, int> byStd, Transform[][] locals, double fps, double scale, List<string> warnings)
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

    static Clip BuildClip(Rig rig, RetargetOptions o, Transform[][] locals, int[] pairOf, int pelvis, int frames, MotionResult motion, out int keyCount)
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

        keyCount = 0;
        var rotTol = o.RotationToleranceDegrees * Math.PI / 180;
        for (var b = 0; b < rig.Bones.Count; b++)
        {
            var bone = rig.Bones[b];
            var driven = pairOf[b] >= 0 || locals.Any(l => Quat.AngleBetween(l[b].Rotation, bone.Rest.Rotation) > 1e-9);
            if (driven)
            {
                var values = new Quat[frames];
                for (var f = 0; f < frames; f++)
                {
                    var q = locals[f][b].Rotation;
                    if (f > 0 && Quat.Dot(q, values[f - 1]) < 0) q = new Quat(-q.X, -q.Y, -q.Z, -q.W); // continuous sign
                    values[f] = q;
                }
                var keep = o.ReduceKeys ? Reduce(frames, (i, j, k) => Quat.AngleBetween(Quat.Slerp(values[i], values[j], (double)(k - i) / (j - i)), values[k]) <= rotTol)
                    : Enumerable.Range(0, frames).ToList();
                clip.Tracks.Add(Track(bone, TrackChannels.Rotation, keep.Select(f => (f, new[] { values[f].X, values[f].Y, values[f].Z, values[f].W })), o.Fps, rig));
                keyCount += keep.Count;
            }
            if (b == pelvis)
            {
                var pos = locals.Select(l => l[b].Translation).ToArray();
                var keep = o.ReduceKeys ? Reduce(frames, (i, j, k) => (Vec3.Lerp(pos[i], pos[j], (double)(k - i) / (j - i)) - pos[k]).Length() <= o.TranslationToleranceMetres)
                    : Enumerable.Range(0, frames).ToList();
                clip.Tracks.Add(Track(bone, TrackChannels.Translation, keep.Select(f => (f, new[] { pos[f].X, pos[f].Y, pos[f].Z })), o.Fps, rig));
                keyCount += keep.Count;
            }
        }
        return clip;
    }

    /// <summary>Greedy key reduction for linear interpolation: keeps a frame only when dropping it would move some
    /// in-between sample beyond tolerance. Segments are capped at 120 frames to bound the cost.</summary>
    static List<int> Reduce(int frames, Func<int, int, int, bool> withinTolerance)
    {
        var keep = new List<int> { 0 };
        if (frames == 1) return keep;
        var anchor = 0;
        for (var end = 2; end < frames; end++)
        {
            var ok = end - anchor <= 120;
            for (var k = anchor + 1; ok && k < end; k++) ok = withinTolerance(anchor, end, k);
            if (!ok)
            {
                keep.Add(end - 1);
                anchor = end - 1;
            }
        }
        keep.Add(frames - 1);
        return keep;
    }

    static Track Track(Bone bone, string channel, IEnumerable<(int Frame, double[] Value)> keys, FrameRate fps, Rig rig) => new()
    {
        Target = new TrackTarget { Kind = TargetKind.Bone, Id = bone.Id },
        Channel = channel,
        Group = rig.StandardNameOf(bone.Id) switch
        {
            null => "Other",
            var s when s.StartsWith("thigh") || s.StartsWith("calf") || s.StartsWith("foot") || s.StartsWith("toe") => "Legs",
            var s when s.StartsWith("clavicle") || s.Contains("arm") || s.StartsWith("hand") => "Arms",
            _ => "Body",
        },
        Keys = keys.Select(k => new Keyframe { Time = Flicks.FromFrame(k.Frame, fps), Value = k.Value, Interp = Interpolation.Linear }).ToList(),
    };
}
