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
    public static int AddBoneTracks(Clip clip, Rig rig, Transform[][] locals, Func<int, bool> rotation, Func<int, bool> translation,
        bool reduce, double rotationToleranceDegrees = 0.1, double translationToleranceMetres = 0.0005)
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
                var keep = reduce ? Reduce(frames, (i, j, k) => Quat.AngleBetween(Quat.Slerp(values[i], values[j], (double)(k - i) / (j - i)), values[k]) <= rotTol)
                    : Enumerable.Range(0, frames).ToList();
                clip.Tracks.Add(Track(TargetKind.Bone, bone.Id, GroupOf(rig, bone.Id), TrackChannels.Rotation,
                    keep.Select(f => (f, new[] { values[f].X, values[f].Y, values[f].Z, values[f].W })), clip.Fps));
                keyCount += keep.Count;
            }
            if (translation(b))
            {
                var pos = locals.Select(l => l[b].Translation).ToArray();
                var keep = reduce ? Reduce(frames, (i, j, k) => (Vec3.Lerp(pos[i], pos[j], (double)(k - i) / (j - i)) - pos[k]).Length() <= translationToleranceMetres)
                    : Enumerable.Range(0, frames).ToList();
                clip.Tracks.Add(Track(TargetKind.Bone, bone.Id, GroupOf(rig, bone.Id), TrackChannels.Translation,
                    keep.Select(f => (f, new[] { pos[f].X, pos[f].Y, pos[f].Z })), clip.Fps));
                keyCount += keep.Count;
            }
        }
        return keyCount;
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
