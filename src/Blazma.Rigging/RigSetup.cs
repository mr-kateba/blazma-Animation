using Blazma.Core.Mathematics;
using Blazma.Core.Model;

namespace Blazma.Rigging;

/// <summary>Adds the standard IK controls and chains (legs and arms) to any humanoid-mapped rig.</summary>
public static class RigSetup
{
    /// <summary>
    /// Creates leg_l/r and arm_l/r chains with targets and poles where the mapped bones form a
    /// direct Upper → Lower → End hierarchy. Returns warnings for limbs that could not be set up.
    /// Existing chains are kept.
    /// </summary>
    public static IReadOnlyList<string> AddStandardIk(Rig rig)
    {
        var warnings = new List<string>();
        var ev = new RigEvaluator(rig);
        var rest = ev.RestModel();

        foreach (var side in new[] { Side.Left, Side.Right })
        {
            var s = side == Side.Left ? "l" : "r";
            Add($"leg_{s}", HumanoidStandard.Thigh(side), HumanoidStandard.Calf(side), HumanoidStandard.Foot(side),
                $"ik_foot_{s}", $"pole_knee_{s}", Vec3.UnitZ);
            Add($"arm_{s}", HumanoidStandard.UpperArm(side), HumanoidStandard.LowerArm(side), HumanoidStandard.Hand(side),
                $"ik_hand_{s}", $"pole_elbow_{s}", -Vec3.UnitZ);
        }
        return warnings;

        void Add(string name, string upperStd, string lowerStd, string endStd, string targetName, string poleName, Vec3 bend)
        {
            if (rig.IkChains.Exists(c => c.Name == name)) return;
            var upper = rig.FindByStandardName(upperStd);
            var lower = rig.FindByStandardName(lowerStd);
            var end = rig.FindByStandardName(endStd);
            if (upper is null || lower is null || end is null)
            {
                warnings.Add($"{name}: limb bones are not mapped, IK skipped.");
                return;
            }
            if (lower.Parent != upper.Id || end.Parent != lower.Id)
            {
                warnings.Add($"{name}: '{upper.Name}' → '{lower.Name}' → '{end.Name}' are not direct parent/child bones (twist bones?), IK skipped.");
                return;
            }

            var iu = ev.IndexOf(upper.Id);
            var il = ev.IndexOf(lower.Id);
            var ie = ev.IndexOf(end.Id);
            var reach = (rest[il].Translation - rest[iu].Translation).Length() + (rest[ie].Translation - rest[il].Translation).Length();

            var target = new RigControl { Name = targetName, Kind = ControlKind.IkTarget, Rest = rest[ie] with { Scale = Vec3.One } };
            var pole = new RigControl
            {
                Name = poleName,
                Kind = ControlKind.Pole,
                Rest = new Transform(rest[il].Translation + bend * (0.6 * reach), Quat.Identity, Vec3.One),
            };
            rig.Controls.Add(target);
            rig.Controls.Add(pole);
            rig.IkChains.Add(new IkChain
            {
                Name = name, Upper = upper.Id, Lower = lower.Id, End = end.Id,
                Target = target.Id, Pole = pole.Id, DefaultBend = bend,
            });
        }
    }
}

/// <summary>Applies <see cref="JointLimits"/> to a local rotation.</summary>
public static class JointLimiter
{
    /// <summary>Clamps <paramref name="local"/> (relative to the bone's rest) to the bone's limits.</summary>
    public static Quat Clamp(Bone bone, Quat local, out bool clamped)
    {
        clamped = false;
        if (bone.Limits is not { } lim) return local;
        var rel = (bone.Rest.Rotation.Inverse() * local).Normalized();
        var e = rel.ToEulerXYZ();
        const double d = Math.PI / 180;
        var c = new Vec3(
            Math.Clamp(e.X, lim.MinDegrees.X * d, lim.MaxDegrees.X * d),
            Math.Clamp(e.Y, lim.MinDegrees.Y * d, lim.MaxDegrees.Y * d),
            Math.Clamp(e.Z, lim.MinDegrees.Z * d, lim.MaxDegrees.Z * d));
        if (c.ApproxEquals(e, 1e-12)) return local;
        clamped = true;
        return (bone.Rest.Rotation * Quat.FromEulerXYZ(c)).Normalized();
    }
}
