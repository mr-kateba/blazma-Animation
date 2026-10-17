using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Physics;

/// <summary>A point of the sole, rigidly attached to a bone.</summary>
/// <param name="Name">heel_in, heel_out, ball_in, ball_out or tip.</param>
/// <param name="Bone">Bone index the point moves with.</param>
/// <param name="Local">Position in that bone's local space.</param>
public readonly record struct SolePoint(string Name, int Bone, Vec3 Local);

/// <param name="FootBone">Index of the foot (ankle) bone.</param>
/// <param name="ToeBone">Index of the toe bone, or −1.</param>
/// <param name="Chain">The leg IK chain ending at the foot, if any.</param>
public sealed record Foot(Side Side, int FootBone, int ToeBone, IkChain? Chain, IReadOnlyList<SolePoint> Points);

/// <summary>
/// Sole approximation of each humanoid-mapped foot: two heel points (attached to the foot bone),
/// two ball points either side of the toe joint (foot bone) and the toe tip (toe bone).
/// Built in the rest pose, which is assumed to stand on the ground plane: the sole lies at y = 0 when the
/// lowest foot joint is between 0 and 0.2 m, otherwise at the lowest foot joint.
/// Foot length L = horizontal ankle→toe-joint distance; heel 0.35·L behind the ankle, tip 0.45·L ahead of the
/// toe joint, half-width 0.38·L at the ball and 0.23·L at the heel.
/// </summary>
public sealed class FootModel
{
    FootModel(IReadOnlyList<Foot> feet, double soleHeight)
    {
        Feet = feet;
        RestSoleHeight = soleHeight;
    }

    public IReadOnlyList<Foot> Feet { get; }

    /// <summary>Model-space height of the sole in the rest pose.</summary>
    public double RestSoleHeight { get; }

    public static FootModel For(Rig rig, RigEvaluator ev)
    {
        var rest = ev.RestModel();
        var found = new List<(Side, int, int)>();
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            var foot = rig.FindByStandardName(H.Foot(side));
            if (foot is null) continue;
            var toe = rig.FindByStandardName(H.Toe(side));
            var fi = rig.IndexOf(foot.Id);
            var ti = toe is null ? -1 : rig.IndexOf(toe.Id);
            if (ti >= 0 && !ev.IsAncestorOrSelf(fi, ti)) ti = -1;
            found.Add((side, fi, ti));
        }
        if (found.Count == 0) return new FootModel(Array.Empty<Foot>(), 0);

        var lowest = found.Min(f => Math.Min(rest[f.Item2].Translation.Y, f.Item3 >= 0 ? rest[f.Item3].Translation.Y : double.MaxValue));
        var sole = lowest is >= 0 and <= 0.2 ? 0 : lowest;

        var feet = new List<Foot>();
        foreach (var (side, fi, ti) in found)
        {
            var ankle = rest[fi].Translation;
            Vec3 ball;
            if (ti >= 0) ball = rest[ti].Translation;
            else
            {
                // No toe bone: assume a forward-pointing foot about 3× as long as the ankle is high.
                var forward = rest[fi].Rotation.Rotate(Vec3.UnitZ);
                forward = new Vec3(forward.X, 0, forward.Z).LengthSquared() < 1e-9 ? Vec3.UnitZ : new Vec3(forward.X, 0, forward.Z).Normalized();
                ball = ankle + forward * Math.Max(0.08, 2.2 * (ankle.Y - sole));
            }
            var flat = new Vec3(ball.X - ankle.X, 0, ball.Z - ankle.Z);
            var length = flat.Length();
            if (length < 1e-6) { flat = Vec3.UnitZ; length = 0.13; }
            var d = flat.Normalized();
            var lateral = Vec3.Cross(Vec3.UnitY, d); // +X for a forward-facing foot
            var outward = side == Side.Left ? 1.0 : -1.0; // +X is the character's left

            Vec3 Ground(Vec3 p) => new(p.X, sole, p.Z);
            var heel = Ground(ankle) - d * (0.35 * length);
            var ballC = Ground(ball);
            var tip = ballC + d * (0.45 * length);
            var halfBall = 0.38 * length;
            var halfHeel = 0.23 * length;
            var pts = new List<SolePoint>
            {
                Point("heel_in", fi, heel - lateral * (outward * halfHeel)),
                Point("heel_out", fi, heel + lateral * (outward * halfHeel)),
                Point("ball_in", fi, ballC - lateral * (outward * halfBall)),
                Point("ball_out", fi, ballC + lateral * (outward * halfBall)),
                Point("tip", ti >= 0 ? ti : fi, tip),
            };
            var chain = rig.IkChains.Find(c => c.End == rig.Bones[fi].Id);
            feet.Add(new Foot(side, fi, ti, chain, pts));
        }
        return new FootModel(feet, sole);

        SolePoint Point(string name, int bone, Vec3 model) => new(name, bone, rest[bone].Inverse().TransformPoint(model));
    }

    /// <summary>Model-space positions of every sole point, foot by foot.</summary>
    public Vec3[][] SolePositions(Transform[] model) =>
        Feet.Select(f => f.Points.Select(p => model[p.Bone].TransformPoint(p.Local)).ToArray()).ToArray();
}
