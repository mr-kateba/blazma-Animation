using Blazma.Core.Model;
using Blazma.Core.Timing;

namespace Blazma.Motion;

/// <summary>
/// Samples one track at a time. Interpolation of the segment k → k+1 is decided by key k:
/// <list type="bullet">
/// <item>Step: hold key k's value.</item>
/// <item>Linear: lerp; rotations use shortest-path slerp.</item>
/// <item>Cubic: Hermite per component with glTF CUBICSPLINE semantics (tangents are value per
/// second; p = h00·p0 + h10·Δt·m0 + h01·p1 + h11·Δt·m1); rotations are renormalized.</item>
/// </list>
/// Keys without explicit tangents get "auto-clamped" tangents: Catmull-Rom slopes, flattened at
/// local extremes and limited so the curve does not overshoot between keys (Fritsch–Carlson).
/// End keys get flat tangents. Before the first key / after the last, the end value is held.
/// </summary>
public static class TrackSampler
{
    public static double[]? Sample(Track track, Flicks time)
    {
        var keys = track.Keys;
        if (keys.Count == 0) return null;
        var arity = TrackChannels.Arity(track.Channel);
        if (arity == 0) return null;
        var isRotation = track.Channel == TrackChannels.Rotation;

        if (time <= keys[0].Time || keys.Count == 1) return Copy(keys[0].Value, arity);
        if (time >= keys[^1].Time) return Copy(keys[^1].Value, arity);

        var k = SegmentIndex(keys, time);
        var k0 = keys[k];
        var k1 = keys[k + 1];
        var span = (k1.Time - k0.Time).Value;
        var s = (time - k0.Time).Value / (double)span;
        var interp = track.Channel == TrackChannels.Pinned ? Interpolation.Step : k0.Interp;

        var p0 = Copy(k0.Value, arity);
        var p1 = Copy(k1.Value, arity);
        double sign = 1;
        if (isRotation && Dot(p0, p1) < 0)
        {
            sign = -1;
            for (var i = 0; i < arity; i++) p1[i] = -p1[i];
        }

        double[] result;
        switch (interp)
        {
            case Interpolation.Step:
                return p0;
            case Interpolation.Linear:
                result = isRotation ? Slerp(p0, p1, s) : Lerp(p0, p1, s);
                break;
            default:
                var dt = span / (double)Flicks.PerSecond;
                var m0 = OutTangent(track, k, arity, isRotation);
                var m1 = InTangent(track, k + 1, arity, isRotation);
                double s2 = s * s, s3 = s2 * s;
                double h00 = 2 * s3 - 3 * s2 + 1, h10 = s3 - 2 * s2 + s, h01 = -2 * s3 + 3 * s2, h11 = s3 - s2;
                result = new double[arity];
                for (var i = 0; i < arity; i++)
                    result[i] = h00 * p0[i] + h10 * dt * m0[i] + h01 * p1[i] + h11 * dt * m1[i] * sign;
                if (isRotation) Normalize(result);
                break;
        }
        return result;
    }

    /// <summary>Index k such that keys[k].Time ≤ time &lt; keys[k+1].Time (keys sorted, time inside).</summary>
    public static int SegmentIndex(IReadOnlyList<Keyframe> keys, Flicks time)
    {
        int lo = 0, hi = keys.Count - 2;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (keys[mid].Time <= time) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Outgoing tangent of key k (explicit, or auto-clamped).</summary>
    public static double[] OutTangent(Track track, int k, int arity, bool isRotation) =>
        track.Keys[k].OutTangent is { } t && t.Length == arity ? t : AutoTangent(track.Keys, k, arity, isRotation);

    /// <summary>Incoming tangent of key k (explicit, or auto-clamped).</summary>
    public static double[] InTangent(Track track, int k, int arity, bool isRotation) =>
        track.Keys[k].InTangent is { } t && t.Length == arity ? t : AutoTangent(track.Keys, k, arity, isRotation);

    public static double[] AutoTangent(IReadOnlyList<Keyframe> keys, int k, int arity, bool isRotation)
    {
        var m = new double[arity];
        if (k <= 0 || k >= keys.Count - 1) return m;

        var cur = keys[k].Value;
        var prev = Copy(keys[k - 1].Value, arity);
        var next = Copy(keys[k + 1].Value, arity);
        if (isRotation)
        {
            if (Dot(prev, cur) < 0) for (var i = 0; i < arity; i++) prev[i] = -prev[i];
            if (Dot(next, cur) < 0) for (var i = 0; i < arity; i++) next[i] = -next[i];
        }

        var dtPrev = (keys[k].Time - keys[k - 1].Time).Value / (double)Flicks.PerSecond;
        var dtNext = (keys[k + 1].Time - keys[k].Time).Value / (double)Flicks.PerSecond;
        for (var i = 0; i < arity; i++)
        {
            var dPrev = (cur[i] - prev[i]) / dtPrev;
            var dNext = (next[i] - cur[i]) / dtNext;
            if (dPrev * dNext <= 0) { m[i] = 0; continue; } // extreme or flat: no overshoot
            var slope = (next[i] - prev[i]) / (dtPrev + dtNext);
            var limit = 3 * Math.Min(Math.Abs(dPrev), Math.Abs(dNext));
            m[i] = Math.Clamp(slope, -limit, limit);
        }
        return m;
    }

    static double[] Copy(double[] v, int arity)
    {
        var r = new double[arity];
        Array.Copy(v, r, Math.Min(arity, v.Length));
        return r;
    }

    static double Dot(double[] a, double[] b)
    {
        double d = 0;
        for (var i = 0; i < a.Length; i++) d += a[i] * b[i];
        return d;
    }

    static double[] Lerp(double[] a, double[] b, double s)
    {
        var r = new double[a.Length];
        for (var i = 0; i < a.Length; i++) r[i] = a[i] + (b[i] - a[i]) * s;
        return r;
    }

    static double[] Slerp(double[] a, double[] b, double s)
    {
        var q = Core.Mathematics.Quat.Slerp(new(a[0], a[1], a[2], a[3]), new(b[0], b[1], b[2], b[3]), s);
        return new[] { q.X, q.Y, q.Z, q.W };
    }

    static void Normalize(double[] q)
    {
        var len = Math.Sqrt(Dot(q, q));
        if (len < 1e-12) { q[0] = q[1] = q[2] = 0; q[3] = 1; return; }
        for (var i = 0; i < q.Length; i++) q[i] /= len;
    }
}
