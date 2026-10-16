using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Rigging;

/// <summary>
/// The built-in humanoid mannequin (1.80 m, 75 kg, T-pose, identity rest rotations so every
/// bone's local axes match model axes). Masses follow Winter's segment fractions
/// (Biomechanics and Motor Control of Human Movement) and are approximations.
/// Joint limits: only the clearly anatomical ones (knees, elbows) for now.
/// </summary>
public static class MannequinFactory
{
    public const string RigName = "Blazma Mannequin";
    public const double TotalMassKg = 75;

    public static Rig Create()
    {
        var rig = new Rig { Name = RigName, Source = new RigSource { Kind = RigSourceKind.Builtin } };
        var byName = new Dictionary<string, Bone>();

        Add(H.Root, null, 0, 0, 0, 0);
        Add(H.Pelvis, H.Root, 0, 0.95, 0, 0.142);
        Add(H.Spine1, H.Pelvis, 0, 0.10, 0, 0.100);
        Add(H.Spine2, H.Spine1, 0, 0.12, 0, 0.100);
        Add(H.Chest, H.Spine2, 0, 0.14, 0, 0.147);
        Add(H.Neck, H.Chest, 0, 0.17, 0, 0.013);
        Add(H.Head, H.Neck, 0, 0.10, 0, 0.068);
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            var x = side == Side.Left ? 1.0 : -1.0; // +X is the character's left
            Add(H.Clavicle(side), H.Chest, 0.03 * x, 0.13, 0, 0.004);
            Add(H.UpperArm(side), H.Clavicle(side), 0.15 * x, 0, 0, 0.028);
            Add(H.LowerArm(side), H.UpperArm(side), 0.28 * x, 0, 0, 0.016);
            Add(H.Hand(side), H.LowerArm(side), 0.26 * x, 0, 0, 0.006);
        }
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            var x = side == Side.Left ? 1.0 : -1.0;
            Add(H.Thigh(side), H.Pelvis, 0.09 * x, -0.05, 0, 0.100);
            Add(H.Calf(side), H.Thigh(side), 0, -0.43, 0, 0.0465);
            Add(H.Foot(side), H.Calf(side), 0, -0.41, 0, 0.0120);
            Add(H.Toe(side), H.Foot(side), 0, -0.04, 0.13, 0.0025);
        }

        // Knees hinge forward only (positive X rotation swings the shin backwards).
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            byName[H.Calf(side)].Limits = new JointLimits { MinDegrees = new Vec3(0, -5, -5), MaxDegrees = new Vec3(150, 5, 5) };
            // Elbows: the forearm folds towards the front (+Z). Left arm points +X, so that is −Y; right is +Y.
            byName[H.LowerArm(side)].Limits = side == Side.Left
                ? new JointLimits { MinDegrees = new Vec3(-90, -150, -5), MaxDegrees = new Vec3(90, 0, 5) }
                : new JointLimits { MinDegrees = new Vec3(-90, 0, -5), MaxDegrees = new Vec3(90, 150, 5) };
        }

        RigSetup.AddStandardIk(rig);
        return rig;

        void Add(string name, string? parent, double x, double y, double z, double massFraction)
        {
            var bone = new Bone
            {
                Name = name,
                Parent = parent is null ? default : byName[parent].Id,
                Rest = new Transform(new Vec3(x, y, z), Quat.Identity, Vec3.One),
                Mass = Math.Round(massFraction * TotalMassKg, 3),
            };
            byName[name] = bone;
            rig.Bones.Add(bone);
            rig.HumanoidMap.Add(new BoneMapping { Bone = bone.Id, Standard = name });
        }
    }

    /// <summary>A character instance of a rig, at rest, with controls synced.</summary>
    public static Character CreateCharacter(Rig rig, string name = "Mannequin")
    {
        var c = new Character { Name = name, Rig = rig.Id };
        PoseSolver.Update(rig, c.Pose);
        return c;
    }
}
