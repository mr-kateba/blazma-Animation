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
            // Poles are stored in the space of the first bone (see PoseSolver.ControlModel).
            var poleModel = new Transform(rest[il].Translation + bend * (0.6 * reach), Quat.Identity, Vec3.One);
            var pole = new RigControl
            {
                Name = poleName,
                Kind = ControlKind.Pole,
                Rest = RigEvaluator.LocalFromModel(rest[0], poleModel),
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
    const double Deg = Math.PI / 180;

    /// <summary>Clamps <paramref name="local"/> (relative to the bone's rest) to the bone's limits.</summary>
    public static Quat Clamp(Bone bone, Quat local, out bool clamped)
    {
        clamped = false;
        if (bone.Limits is not { } lim) return local;
        var rel = (bone.Rest.Rotation.Inverse() * local).Normalized();
        var limited = lim.Hinge is { } h ? ClampHinge(rel, h) : ClampEuler(rel, lim);
        if (Quat.AngleBetween(limited, rel) < 1e-12) return local;
        clamped = true;
        return (bone.Rest.Rotation * limited).Normalized();
    }

    static Quat ClampEuler(Quat rel, JointLimits lim)
    {
        var e = rel.ToEulerXYZ();
        var c = new Vec3(
            Math.Clamp(e.X, lim.MinDegrees.X * Deg, lim.MaxDegrees.X * Deg),
            Math.Clamp(e.Y, lim.MinDegrees.Y * Deg, lim.MaxDegrees.Y * Deg),
            Math.Clamp(e.Z, lim.MinDegrees.Z * Deg, lim.MaxDegrees.Z * Deg));
        return c.ApproxEquals(e, 1e-12) ? rel : Quat.FromEulerXYZ(c);
    }

    /// <summary>Swing-twist: rel = swing · twist, twist about the hinge axis.</summary>
    static Quat ClampHinge(Quat rel, HingeLimit h)
    {
        var axis = h.Axis.Normalized();
        if (rel.W < 0) rel = new Quat(-rel.X, -rel.Y, -rel.Z, -rel.W);
        var v = new Vec3(rel.X, rel.Y, rel.Z);
        var along = Vec3.Dot(v, axis);
        var twistAngle = 2 * Math.Atan2(along, rel.W);
        var twist = Quat.FromAxisAngle(axis, twistAngle);
        var swing = (rel * twist.Inverse()).Normalized();

        var clampedTwist = Quat.FromAxisAngle(axis, Math.Clamp(twistAngle, h.MinDegrees * Deg, h.MaxDegrees * Deg));
        var swingAngle = 2 * Math.Acos(Math.Clamp(Math.Abs(swing.W), 0, 1));
        var maxSwing = h.MaxSwingDegrees * Deg;
        if (swingAngle > maxSwing)
        {
            var sAxis = new Vec3(swing.X, swing.Y, swing.Z) * Math.Sign(swing.W == 0 ? 1 : swing.W);
            swing = sAxis.LengthSquared() < 1e-24 ? Quat.Identity : Quat.FromAxisAngle(sAxis, maxSwing);
        }
        return (swing * clampedTwist).Normalized();
    }
}
