using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;

namespace Blazma.Motion;

/// <summary>
/// Turns per-frame local bone transforms into editable tracks: linear keys on every frame, optionally reduced to the
/// frames linear interpolation cannot reproduce within tolerance (retargeting, templates, sequence bake).
/// </summary>
public static class BakedTracks
{
    /// <summary>
    /// Adds rotation tracks for bones where <paramref name="rotation"/> is true and translation tracks where
    /// <paramref name="translation"/> is true. Returns the number of keys written.
    /// </summary>
    /// <param name="interpolation">Linear (default) or Cubic. Cubic keys use auto-clamped tangents; their reduction is verified
    /// against the dense cubic curve (on and half-way between frames) and refined until it is within tolerance.</param>
    /// <param name="cyclic">The last frame repeats the first (translations may add a constant travel): cubic end keys get the
    /// periodic tangent instead of a flat one, so the velocity is continuous across the loop seam.</param>
    public static int AddBoneTracks(Clip clip, Rig rig, Transform[][] locals, Func<int, bool> rotation, Func<int, bool> translation,
        bool reduce, double rotationToleranceDegrees = 0.1, double translationToleranceMetres = 0.0005,
        Interpolation interpolation = Interpolation.Linear, bool cyclic = false)
    {
        var frames = locals.Length;
        var keyCount = 0;
        var rotTol = rotationToleranceDegrees * Math.PI / 180;
        for (var b = 0; b < rig.Bones.Count; b++)
        {
            var bone = rig.Bones[b];
            if (rotation(b))
            {
                var values = new Quat[frames];
                for (var f = 0; f < frames; f++)
                {
                    var q = locals[f][b].Rotation;
                    if (f > 0 && Quat.Dot(q, values[f - 1]) < 0) q = new Quat(-q.X, -q.Y, -q.Z, -q.W); // continuous sign
                    values[f] = q;
                }
                if (interpolation == Interpolation.Cubic)
                {
                    var t = CubicTrack(TargetKind.Bone, bone.Id, GroupOf(rig, bone.Id), TrackChannels.Rotation,
                        values.Select(q => new[] { q.X, q.Y, q.Z, q.W }).ToArray(), clip.Fps, reduce, rotTol, cyclic);
                    clip.Tracks.Add(t);
                    keyCount += t.Keys.Count;
                }
                else
                {
                    var keep = reduce ? Reduce(frames, (i, j, k) => Quat.AngleBetween(Quat.Slerp(values[i], values[j], (double)(k - i) / (j - i)), values[k]) <= rotTol)
                        : Enumerable.Range(0, frames).ToList();
                    clip.Tracks.Add(Track(TargetKind.Bone, bone.Id, GroupOf(rig, bone.Id), TrackChannels.Rotation,
                        keep.Select(f => (f, new[] { values[f].X, values[f].Y, values[f].Z, values[f].W })), clip.Fps));
                    keyCount += keep.Count;
                }
            }
            if (translation(b))
            {
                var pos = locals.Select(l => l[b].Translation).ToArray();
                if (interpolation == Interpolation.Cubic)
                {
                    var t = CubicTrack(TargetKind.Bone, bone.Id, GroupOf(rig, bone.Id), TrackChannels.Translation,
                        pos.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(), clip.Fps, reduce, translationToleranceMetres, cyclic);
                    clip.Tracks.Add(t);
                    keyCount += t.Keys.Count;
                    continue;
                }
                var keep = reduce ? Reduce(frames, (i, j, k) => (Vec3.Lerp(pos[i], pos[j], (double)(k - i) / (j - i)) - pos[k]).Length() <= translationToleranceMetres)
                    : Enumerable.Range(0, frames).ToList();
                clip.Tracks.Add(Track(TargetKind.Bone, bone.Id, GroupOf(rig, bone.Id), TrackChannels.Translation,
                    keep.Select(f => (f, new[] { pos[f].X, pos[f].Y, pos[f].Z })), clip.Fps));
                keyCount += keep.Count;
            }
        }
        return keyCount;
    }

    /// <summary>
    /// A cubic (auto-clamped) track through per-frame values. With <paramref name="reduce"/>, starts from the keys linear reduction
    /// would keep at twice the tolerance and inserts the worst frame of every segment whose cubic curve leaves the dense cubic curve
    /// by more than <paramref name="tolerance"/> (radians for rotations, metres otherwise), checked on and half-way between frames.
    /// </summary>
    public static Track CubicTrack(TargetKind kind, ObjectId id, string? group, string channel, double[][] values, FrameRate fps, bool reduce,
        double tolerance, bool cyclic)
    {
        var n = values.Length;
        var isRotation = channel == TrackChannels.Rotation;
        double[]? endTangent = null;
        var endSign = 1.0;
        if (cyclic && n > 3)
        {
            // Periodic tangent at the seam: central difference across it (frame n−1 repeats frame 0, translations + travel).
            var dt = 1 / fps.FramesPerSecond;
            var prev = (double[])values[n - 2].Clone();
            if (isRotation)
            {
                endSign = Dot(values[n - 1], values[0]) < 0 ? -1 : 1;
                for (var i = 0; i < prev.Length; i++) prev[i] *= endSign;
            }
            else for (var i = 0; i < prev.Length; i++) prev[i] -= values[n - 1][i] - values[0][i];
            endTangent = new double[prev.Length];
            for (var i = 0; i < prev.Length; i++) endTangent[i] = (values[1][i] - prev[i]) / (2 * dt);
        }
        Track Make(IEnumerable<int> keep)
        {
            var t = Track(kind, id, group, channel, keep.Select(f => (f, values[f])), fps);
            foreach (var k in t.Keys) k.Interp = Interpolation.Cubic;
            if (endTangent is not null && t.Keys.Count >= 2)
            {
                t.Keys[0].InTangent = t.Keys[0].OutTangent = endTangent;
                t.Keys[^1].InTangent = t.Keys[^1].OutTangent = endTangent.Select(x => x * endSign).ToArray();
            }
            return t;
        }
        var dense = Make(Enumerable.Range(0, n));
        if (!reduce || n <= 2) return dense;

        double Error(double[] a, double[] b)
        {
            if (isRotation) return Quat.AngleBetween(new Quat(a[0], a[1], a[2], a[3]).Normalized(), new Quat(b[0], b[1], b[2], b[3]).Normalized());
            double s = 0;
            for (var i = 0; i < a.Length; i++) s += (a[i] - b[i]) * (a[i] - b[i]);
            return Math.Sqrt(s);
        }
        var samples = new double[2 * n - 1][];
        for (var h = 0; h < samples.Length; h++) samples[h] = TrackSampler.Sample(dense, Half(h, fps))!;
        var keepSet = new SortedSet<int>(Reduce(n, (i, j, k) => Error(Lerp(values[i], values[j], (double)(k - i) / (j - i)), values[k]) <= 2 * tolerance));
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var track = Make(keepSet);
            var keys = keepSet.ToList();
            var added = false;
            for (var s = 0; s + 1 < keys.Count; s++)
            {
                int worst = -1;
                var worstErr = tolerance;
                for (var h = 2 * keys[s] + 1; h < 2 * keys[s + 1]; h++)
                {
                    var e = Error(TrackSampler.Sample(track, Half(h, fps))!, samples[h]);
                    if (e <= worstErr) continue;
                    worstErr = e;
                    worst = h % 2 == 0 ? h / 2 : (h / 2 - keys[s] < keys[s + 1] - h / 2 - 1 ? h / 2 + 1 : h / 2);
                }
                if (worst > keys[s] && worst < keys[s + 1] && keepSet.Add(worst)) added = true;
            }
            if (!added) break;
        }
        return Make(keepSet);
    }

    static Flicks Half(int halfFrame, FrameRate fps) => Flicks.FromSeconds(halfFrame / (2 * fps.FramesPerSecond));

    static double Dot(double[] a, double[] b)
    {
        double d = 0;
        for (var i = 0; i < a.Length; i++) d += a[i] * b[i];
        return d;
    }

    static double[] Lerp(double[] a, double[] b, double t)
    {
        var r = new double[a.Length];
        for (var i = 0; i < a.Length; i++) r[i] = a[i] + (b[i] - a[i]) * t;
        return r;
    }

    /// <summary>
    /// Translation and rotation tracks (and scale when it is not 1 somewhere) of a prop from its value on every frame, group
    /// "Props" as key.set writes them. Returns the number of keys written.
    /// </summary>
    public static int AddPropTracks(Clip clip, ObjectId prop, Transform[] values, bool reduce, double rotationToleranceDegrees = 0.1, double translationToleranceMetres = 0.0005)
    {
        var frames = values.Length;
        var rotTol = rotationToleranceDegrees * Math.PI / 180;
        var rot = new Quat[frames];
        for (var f = 0; f < frames; f++)
        {
            var q = values[f].Rotation;
            if (f > 0 && Quat.Dot(q, rot[f - 1]) < 0) q = new Quat(-q.X, -q.Y, -q.Z, -q.W);
            rot[f] = q;
        }
        var count = 0;
        void Vector(string channel, Func<Transform, Vec3> get)
        {
            var v = values.Select(get).ToArray();
            var keep = reduce ? Reduce(frames, (i, j, k) => (Vec3.Lerp(v[i], v[j], (double)(k - i) / (j - i)) - v[k]).Length() <= translationToleranceMetres)
                : Enumerable.Range(0, frames).ToList();
            clip.Tracks.Add(Track(TargetKind.Prop, prop, "Props", channel, keep.Select(f => (f, new[] { v[f].X, v[f].Y, v[f].Z })), clip.Fps));
            count += keep.Count;
        }
        Vector(TrackChannels.Translation, t => t.Translation);
        var keepR = reduce ? Reduce(frames, (i, j, k) => Quat.AngleBetween(Quat.Slerp(rot[i], rot[j], (double)(k - i) / (j - i)), rot[k]) <= rotTol)
            : Enumerable.Range(0, frames).ToList();
        clip.Tracks.Add(Track(TargetKind.Prop, prop, "Props", TrackChannels.Rotation, keepR.Select(f => (f, new[] { rot[f].X, rot[f].Y, rot[f].Z, rot[f].W })), clip.Fps));
        count += keepR.Count;
        if (values.Any(t => !t.Scale.ApproxEquals(Vec3.One, 1e-12))) Vector(TrackChannels.Scale, t => t.Scale);
        return count;
    }

    /// <summary>True when some frame's value of bone <paramref name="b"/> differs from its rest value.</summary>
    public static bool RotationMoves(Rig rig, Transform[][] locals, int b) =>
        locals.Any(l => Quat.AngleBetween(l[b].Rotation, rig.Bones[b].Rest.Rotation) > 1e-9);

    public static bool TranslationMoves(Rig rig, Transform[][] locals, int b) =>
        locals.Any(l => !l[b].Translation.ApproxEquals(rig.Bones[b].Rest.Translation, 1e-9));

    /// <summary>Greedy key reduction for linear interpolation: keeps a frame only when dropping it would move some
    /// in-between sample beyond tolerance. Segments are capped at 120 frames to bound the cost.</summary>
    public static List<int> Reduce(int frames, Func<int, int, int, bool> withinTolerance)
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

    public static Track Track(TargetKind kind, ObjectId id, string? group, string channel, IEnumerable<(int Frame, double[] Value)> keys, FrameRate fps) => new()
    {
        Target = new TrackTarget { Kind = kind, Id = id },
        Channel = channel,
        Group = group,
        Keys = keys.Select(k => new Keyframe
        {
            Time = Flicks.FromFrame(k.Frame, fps), Value = k.Value,
            Interp = channel == TrackChannels.Pinned ? Interpolation.Step : Interpolation.Linear,
        }).ToList(),
    };

    /// <summary>Timeline group of a bone (Legs, Arms, Body, Other) from its humanoid name.</summary>
    public static string GroupOf(Rig rig, ObjectId bone) => rig.StandardNameOf(bone) switch
    {
        null => "Other",
        var s when s.StartsWith("thigh") || s.StartsWith("calf") || s.StartsWith("foot") || s.StartsWith("toe") => "Legs",
        var s when s.StartsWith("clavicle") || s.Contains("arm") || s.StartsWith("hand") => "Arms",
        _ => "Body",
    };
}
