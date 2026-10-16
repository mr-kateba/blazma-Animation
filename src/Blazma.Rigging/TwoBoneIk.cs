using Blazma.Core.Mathematics;

namespace Blazma.Rigging;

/// <summary>
/// Analytic two-bone IK (law of cosines) on model-space joint positions:
/// A = upper joint (hip/shoulder), B = mid joint (knee/elbow), C = end joint (ankle/wrist).
/// <para>
/// The result depends only on the start configuration, target and bend hint — never on
/// previous solves — so the same inputs always give the same pose (no twist drift).
/// The upper bone is rotated so its start hinge axis lines up with the solved hinge axis,
/// which makes the lower bone's rotation a pure hinge.
/// </para>
/// Bone lengths are preserved exactly; out-of-reach targets give a fully extended chain
/// pointing at the target.
/// </summary>
public static class TwoBoneIk
{
    public readonly record struct Result(Vec3 Mid, Vec3 End, Quat UpperDelta, Quat LowerDelta, bool Reached, Vec3 Bend);

    /// <param name="pole">Optional pole position: the chain bends towards it.</param>
    /// <param name="defaultBend">Bend direction when there is no pole and the start chain is straight.</param>
    /// <returns>Model-space deltas: new upper = UpperDelta·upper, new lower = LowerDelta·UpperDelta·lower.</returns>
    public static Result Solve(Vec3 a, Vec3 b, Vec3 c, Vec3 target, Vec3? pole, Vec3 defaultBend)
    {
        var l1 = (b - a).Length();
        var l2 = (c - b).Length();
        if (l1 < 1e-9 || l2 < 1e-9) return new Result(b, c, Quat.Identity, Quat.Identity, false, defaultBend);

        var toTarget = target - a;
        var dist = toTarget.Length();
        var dir = dist > 1e-12 ? toTarget / dist : (c - a).Normalized();
        if (dir.LengthSquared() < 1e-12) dir = -Vec3.UnitY;

        // Keep a sliver of bend so the plane stays defined and the solve stays stable.
        var minReach = Math.Abs(l1 - l2) + 1e-9;
        var maxReach = l1 + l2 - 1e-9;
        var reached = dist >= minReach && dist <= maxReach;
        var d = Math.Clamp(dist, minReach, maxReach);

        // Bend side of the start chain (zero if straight); used as hint and as the start hinge reference.
        var startSide = Side(a, b, c);
        var hint = pole is { } p ? p - a
            : startSide.LengthSquared() > 1e-12 * l1 * l1 ? startSide
            : defaultBend;
        var bend = Perpendicular(hint, dir, defaultBend);

        var cosA = Math.Clamp((l1 * l1 + d * d - l2 * l2) / (2 * l1 * d), -1, 1);
        var sinA = Math.Sqrt(1 - cosA * cosA);
        var newB = a + dir * (l1 * cosA) + bend * (l1 * sinA);
        var newC = a + dir * d;

        // Frames: (bone direction, hinge axis). Hinge axis = bone × bend side.
        var u0 = (b - a).Normalized();
        // A straight start chain has no geometric hinge: the joint's natural bend (defaultBend)
        // defines it, so the pole only twists the upper bone and the lower stays a pure hinge.
        var s0 = startSide.LengthSquared() > 1e-12 * l1 * l1 ? startSide : Perpendicular(defaultBend, u0, bend);
        var n0 = Vec3.Cross(u0, s0).Normalized();
        var u1 = (newB - a).Normalized();
        var n1 = Vec3.Cross(u1, bend).Normalized();
        if (n0.LengthSquared() < 0.5 || n1.LengthSquared() < 0.5)
            return new Result(newB, newC, Quat.FromTo(b - a, newB - a), Quat.Identity, reached, bend);

        var upperDelta = (FromBasis(u1, n1) * FromBasis(u0, n0).Inverse()).Normalized();

        // Lower: pure rotation about the solved hinge axis.
        var v1 = upperDelta.Rotate(c - b);
        var v2 = newC - newB;
        var angle = Math.Atan2(Vec3.Dot(Vec3.Cross(v1, v2), n1), Vec3.Dot(v1, v2));
        var lowerDelta = Quat.FromAxisAngle(n1, angle);
        return new Result(newB, newC, upperDelta, lowerDelta, reached, bend);
    }

    /// <summary>Offset of the mid joint from the line A→C (zero for a straight chain).</summary>
    static Vec3 Side(Vec3 a, Vec3 b, Vec3 c)
    {
        var ac = (c - a).Normalized();
        var ab = b - a;
        return ac.LengthSquared() < 1e-12 ? Vec3.Zero : ab - ac * Vec3.Dot(ab, ac);
    }

    /// <summary>Unit vector of <paramref name="v"/> perpendicular to <paramref name="axis"/>, with fallbacks.</summary>
    static Vec3 Perpendicular(Vec3 v, Vec3 axis, Vec3 fallback)
    {
        foreach (var candidate in new[] { v, fallback })
        {
            var p = candidate - axis * Vec3.Dot(candidate, axis);
            if (p.LengthSquared() > 1e-10) return p.Normalized();
        }
        var any = Vec3.Cross(axis, Math.Abs(axis.Y) < 0.9 ? Vec3.UnitY : Vec3.UnitX);
        return any.Normalized();
    }

    /// <summary>Rotation whose X axis maps to <paramref name="x"/> and Y axis to <paramref name="y"/> (orthonormal).</summary>
    internal static Quat FromBasis(Vec3 x, Vec3 y)
    {
        var z = Vec3.Cross(x, y);
        double m00 = x.X, m10 = x.Y, m20 = x.Z;
        double m01 = y.X, m11 = y.Y, m21 = y.Z;
        double m02 = z.X, m12 = z.Y, m22 = z.Z;
        var trace = m00 + m11 + m22;
        Quat q;
        if (trace > 0)
        {
            var s = Math.Sqrt(trace + 1) * 2;
            q = new Quat((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25 * s);
        }
        else if (m00 > m11 && m00 > m22)
        {
            var s = Math.Sqrt(1 + m00 - m11 - m22) * 2;
            q = new Quat(0.25 * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
        }
        else if (m11 > m22)
        {
            var s = Math.Sqrt(1 + m11 - m00 - m22) * 2;
            q = new Quat((m01 + m10) / s, 0.25 * s, (m12 + m21) / s, (m02 - m20) / s);
        }
        else
        {
            var s = Math.Sqrt(1 + m22 - m00 - m11) * 2;
            q = new Quat((m02 + m20) / s, (m12 + m21) / s, 0.25 * s, (m10 - m01) / s);
        }
        return q.Normalized();
    }
}
