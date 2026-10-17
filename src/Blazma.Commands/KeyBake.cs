using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;

namespace Blazma.Commands;

/// <summary>
/// Writes values on whole frames of a track while every other whole frame keeps exactly the value it had
/// (docs/ARCHITECTURE.md §7.6). Inserting a key into a cubic track changes the auto tangents of its neighbours
/// and the shape of the segment it splits, so baked edits would leak into frames that were never changed.
/// Instead, every frame from the original key before a changed frame to the original key after it is keyed
/// (with the original curve's value where it is not changed, and the interpolation of the segment it splits),
/// and the two bounding original keys get their original auto tangents written explicitly on the outside, so the
/// curve beyond them is untouched (bit for bit). With no original key on a side, one extra key on the
/// neighbouring frame holds the original value. Keys are merged in one sorted pass.
/// </summary>
internal static class KeyBake
{
    /// <summary>Inclusive frame intervals to key, sorted and disjoint.</summary>
    public static List<(long From, long To)> Regions(IReadOnlyList<Keyframe> keys, FrameRate fps, IReadOnlyList<long> changedSorted, long lastFrame)
    {
        var result = new List<(long, long)>();
        foreach (var f in changedSorted)
        {
            var t = Flicks.FromFrame(f, fps);
            var li = LastBefore(keys, t);
            var ri = FirstAfter(keys, t);
            var from = li >= 0 ? FirstFrameAfter(keys[li].Time, fps) : Math.Max(0, f - 1);
            var to = ri >= 0 ? LastFrameBefore(keys[ri].Time, fps) : Math.Min(lastFrame, f + 1);
            from = Math.Min(from, f);
            to = Math.Min(Math.Max(to, f), Math.Max(lastFrame, f));
            if (result.Count > 0 && from <= result[^1].Item2 + 1)
                result[^1] = (result[^1].Item1, Math.Max(result[^1].Item2, to));
            else result.Add((from, to));
        }
        return result;
    }

    public static long Count(IEnumerable<(long From, long To)> regions) => regions.Sum(r => r.To - r.From + 1);

    /// <summary>
    /// Keys every frame of <paramref name="regions"/>: <paramref name="changed"/>(frame) where it returns a value,
    /// otherwise the track's original value (<paramref name="baseValue"/> when the track has no keys).
    /// Returns the frames written.
    /// </summary>
    public static int Write(Track track, FrameRate fps, double[] baseValue, IReadOnlyList<(long From, long To)> regions, Func<long, double[]?> changed)
    {
        var old = track.Keys;
        var snapshot = new Track { Channel = track.Channel, Keys = old };
        var arity = TrackChannels.Arity(track.Channel);
        var isRotation = track.Channel == TrackChannels.Rotation;

        // Original auto tangents of the keys bounding each region, computed before anything changes.
        var pinIn = new Dictionary<int, double[]>();
        var pinOut = new Dictionary<int, double[]>();
        foreach (var (from, to) in regions)
        {
            var li = LastBefore(old, Flicks.FromFrame(from, fps));
            var ri = FirstAfter(old, Flicks.FromFrame(to, fps));
            if (li > 0 && old[li].InTangent is null) pinIn[li] = TrackSampler.AutoTangent(old, li, arity, isRotation);
            if (ri >= 0 && ri < old.Count - 1 && old[ri].OutTangent is null) pinOut[ri] = TrackSampler.AutoTangent(old, ri, arity, isRotation);
        }

        var baked = new List<Keyframe>();
        foreach (var (from, to) in regions)
            for (var f = from; f <= to; f++)
            {
                var time = Flicks.FromFrame(f, fps);
                var value = changed(f);
                var at = old.Count == 0 ? -1 : IndexOf(old, time);
                if (at >= 0)
                {
                    var k = old[at];
                    baked.Add(value is null ? k : new Keyframe
                    {
                        Time = time, Value = (double[])value.Clone(), Interp = k.Interp,
                        InTangent = (double[]?)k.InTangent?.Clone(), OutTangent = (double[]?)k.OutTangent?.Clone(),
                    });
                    continue;
                }
                baked.Add(new Keyframe
                {
                    Time = time,
                    Value = value is not null ? (double[])value.Clone() : old.Count == 0 ? (double[])baseValue.Clone() : TrackSampler.Sample(snapshot, time)!,
                    Interp = SegmentInterp(old, time),
                });
            }

        // One sorted merge: baked keys replace original keys at the same time.
        var merged = new List<Keyframe>(old.Count + baked.Count);
        int i = 0, j = 0;
        while (i < old.Count || j < baked.Count)
        {
            if (j >= baked.Count || (i < old.Count && old[i].Time < baked[j].Time)) merged.Add(Pinned(i, old[i++]));
            else
            {
                if (i < old.Count && old[i].Time == baked[j].Time) i++;
                merged.Add(baked[j++]);
            }
        }
        track.Keys = merged;
        return baked.Count;

        Keyframe Pinned(int index, Keyframe k)
        {
            var hasIn = pinIn.TryGetValue(index, out var tin);
            var hasOut = pinOut.TryGetValue(index, out var tout);
            if (!hasIn && !hasOut) return k;
            return new Keyframe
            {
                Time = k.Time, Value = k.Value, Interp = k.Interp,
                InTangent = hasIn ? tin : k.InTangent, OutTangent = hasOut ? tout : k.OutTangent,
            };
        }
    }

    /// <summary>Interpolation of the original segment containing <paramref name="time"/> (the nearest end key outside).</summary>
    static Interpolation SegmentInterp(IReadOnlyList<Keyframe> keys, Flicks time)
    {
        if (keys.Count == 0) return Interpolation.Cubic;
        if (time <= keys[0].Time) return keys[0].Interp;
        if (time >= keys[^1].Time) return keys[^1].Interp;
        return keys[TrackSampler.SegmentIndex(keys, time)].Interp;
    }

    static int IndexOf(IReadOnlyList<Keyframe> keys, Flicks time)
    {
        int lo = 0, hi = keys.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (keys[mid].Time == time) return mid;
            if (keys[mid].Time < time) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }

    /// <summary>Index of the last key strictly before <paramref name="t"/>, or −1.</summary>
    static int LastBefore(IReadOnlyList<Keyframe> keys, Flicks t)
    {
        int lo = 0, hi = keys.Count - 1, best = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (keys[mid].Time < t) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return best;
    }

    /// <summary>Index of the first key strictly after <paramref name="t"/>, or −1.</summary>
    static int FirstAfter(IReadOnlyList<Keyframe> keys, Flicks t)
    {
        int lo = 0, hi = keys.Count - 1, best = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (keys[mid].Time > t) { best = mid; hi = mid - 1; } else lo = mid + 1;
        }
        return best;
    }

    static long FirstFrameAfter(Flicks t, FrameRate fps)
    {
        var f = Math.Max(0, t.ToFrame(fps));
        while (Flicks.FromFrame(f, fps) <= t) f++;
        while (f > 0 && Flicks.FromFrame(f - 1, fps) > t) f--;
        return f;
    }

    static long LastFrameBefore(Flicks t, FrameRate fps)
    {
        var f = t.ToFrame(fps);
        while (f > 0 && Flicks.FromFrame(f, fps) >= t) f--;
        while (Flicks.FromFrame(f + 1, fps) < t) f++;
        return f;
    }
}
