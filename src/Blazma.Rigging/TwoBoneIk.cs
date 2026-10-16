using Blazma.Core.Mathematics;

namespace Blazma.Rigging;

/// <summary>
/// Analytic two-bone IK (law of cosines). Works on model-space joint positions:
/// A = upper joint (hip/shoulder), B = mid joint (knee/elbow), C = end joint (ankle/wrist).
/// Bone lengths are preserved exactly; out-of-reach targets give a fully extended chain
/// pointing at the target. The bend plane comes from the pole (if any), else the current
/// bend, else <c>defaultBend</c>.
/// </summary>
public static class TwoBoneIk
{
    public readonly record struct Result(Vec3 Mid, Vec3 End, Quat UpperDelta, Quat LowerDelta, bool Reached);

    /// <param name="upperDelta">Out: model-space rotation to pre-multiply onto the upper bone.</param>
    public static Result Solve(Vec3 a, Vec3 b, Vec3 c, Vec3 target, Vec3? pole, Vec3 defaultBend)
    {
        var l1 = (b - a).Length();
        var l2 = (c - b).Length();
        if (l1 < 1e-9 || l2 < 1e-9) return new Result(b, c, Quat.Identity, Quat.Identity, false);

        var toTarget = target - a;
        var dist = toTarget.Length();
        var dir = dist > 1e-12 ? toTarget / dist : (c - a).Normalized();
        if (dir.LengthSquared() < 1e-12) dir = -Vec3.UnitY;

        // Keep a sliver of bend so the plane stays defined and the solve stays stable.
        var minReach = Math.Abs(l1 - l2) + 1e-9;
        var maxReach = l1 + l2 - 1e-9;
        var reached = dist >= minReach && dist <= maxReach;
        var d = Math.Clamp(dist, minReach, maxReach);

        var bend = BendDirection(a, b, dir, pole, defaultBend);

        var cosA = Math.Clamp((l1 * l1 + d * d - l2 * l2) / (2 * l1 * d), -1, 1);
        var sinA = Math.Sqrt(1 - cosA * cosA);
        var newB = a + dir * (l1 * cosA) + bend * (l1 * sinA);
        var newC = a + dir * d;

        var upperDelta = Quat.FromTo(b - a, newB - a);
        var lowerAfterUpper = upperDelta.Rotate(c - b);
        var lowerDelta = Quat.FromTo(lowerAfterUpper, newC - newB);
        return new Result(newB, newC, upperDelta, lowerDelta, reached);
    }

    /// <summary>Unit vector perpendicular to <paramref name="dir"/> in the bend plane.</summary>
    static Vec3 BendDirection(Vec3 a, Vec3 b, Vec3 dir, Vec3? pole, Vec3 defaultBend)
    {
        foreach (var candidate in Candidates())
        {
            var v = candidate - dir * Vec3.Dot(candidate, dir);
            if (v.LengthSquared() > 1e-10) return v.Normalized();
        }
        // dir parallel to everything we tried: pick any perpendicular.
        var any = Vec3.Cross(dir, Math.Abs(dir.Y) < 0.9 ? Vec3.UnitY : Vec3.UnitX);
        return any.Normalized();

        IEnumerable<Vec3> Candidates()
        {
            if (pole is { } p) yield return p - a;
            yield return b - a;
            yield return defaultBend;
        }
    }
}
