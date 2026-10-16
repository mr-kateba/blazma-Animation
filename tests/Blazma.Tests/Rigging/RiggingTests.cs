using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.Rigging;

public class MannequinTests
{
    [Fact]
    public void Mannequin_HasEveryStandardBone_ParentBeforeChild_AndAllRequiredMapped()
    {
        var rig = MannequinFactory.Create();
        Assert.Equal(H.Bones.Count, rig.Bones.Count);
        foreach (var (name, parent) in H.Bones)
        {
            var bone = rig.FindBoneByName(name)!;
            Assert.Equal(name, rig.StandardNameOf(bone.Id));
            if (parent is null) Assert.True(bone.Parent.IsEmpty);
            else Assert.Equal(rig.FindBoneByName(parent)!.Id, bone.Parent);
        }
        Assert.All(H.Required, r => Assert.NotNull(rig.FindByStandardName(r)));
        _ = new RigEvaluator(rig); // throws if out of order
    }

    [Fact]
    public void Mannequin_Proportions_And_Mass_ArePlausible()
    {
        var rig = MannequinFactory.Create();
        var ev = new RigEvaluator(rig);
        var rest = ev.RestModel();
        Vec3 P(string n) => rest[ev.IndexOf(rig.FindBoneByName(n)!.Id)].Translation;

        Assert.InRange(P(H.Head).Y, 1.5, 1.7);           // head joint; head top is ~1.80 m
        Assert.True(P(H.Hand(Side.Left)).X > 0.6);         // +X is the character's left
        Assert.True(P(H.Hand(Side.Right)).X < -0.6);
        Assert.InRange(P(H.Foot(Side.Left)).Y, 0.03, 0.12);
        Assert.True(P(H.Toe(Side.Left)).Z > P(H.Foot(Side.Left)).Z); // toes point forward (+Z)
        Assert.InRange(rig.Bones.Sum(b => b.Mass), 74.0, 76.0);
    }

    [Fact]
    public void Mannequin_HasFourIkChains_WithTargetsAtTheirEndBones()
    {
        var rig = MannequinFactory.Create();
        Assert.Equal(new[] { "leg_l", "arm_l", "leg_r", "arm_r" }, rig.IkChains.Select(c => c.Name));
        var ev = new RigEvaluator(rig);
        var rest = ev.RestModel();
        foreach (var chain in rig.IkChains)
        {
            var target = rig.GetControl(chain.Target);
            Assert.True(target.Rest.Translation.ApproxEquals(rest[ev.IndexOf(chain.End)].Translation, 1e-12));
        }
    }
}

public class TwoBoneIkTests
{
    static readonly Vec3 A = new(0, 1, 0), B = new(0, 0.5, 0.01), C = new(0, 0, 0);

    [Theory]
    [InlineData(0.3, 0.2, 0.2)]
    [InlineData(-0.2, 0.4, 0.1)]
    [InlineData(0.0, 0.1, 0.0)]
    [InlineData(0.5, 0.9, -0.3)]
    public void ReachableTarget_IsHitExactly_AndLengthsArePreserved(double x, double y, double z)
    {
        var target = new Vec3(x, y, z);
        var r = TwoBoneIk.Solve(A, B, C, target, pole: null, Vec3.UnitZ);
        Assert.True(r.Reached);
        Assert.True(r.End.ApproxEquals(target, 1e-9), $"end {r.End} target {target}");
        Assert.Equal((B - A).Length(), (r.Mid - A).Length(), 9);
        Assert.Equal((C - B).Length(), (r.End - r.Mid).Length(), 9);
        // Deltas really move the bones where the positions say.
        Assert.True((A + r.UpperDelta.Rotate(B - A)).ApproxEquals(r.Mid, 1e-9));
        Assert.True((r.Mid + (r.LowerDelta * r.UpperDelta).Rotate(C - B)).ApproxEquals(r.End, 1e-9));
    }

    [Fact]
    public void UnreachableTarget_ExtendsTowardsIt()
    {
        var target = new Vec3(0, -5, 0.5);
        var r = TwoBoneIk.Solve(A, B, C, target, null, Vec3.UnitZ);
        Assert.False(r.Reached);
        var reach = (B - A).Length() + (C - B).Length();
        Assert.Equal(reach, (r.End - A).Length(), 6);
        Assert.True(Vec3.Dot((r.End - A).Normalized(), (target - A).Normalized()) > 0.999999);
    }

    [Fact]
    public void Pole_DecidesBendPlane()
    {
        var target = new Vec3(0, 0.3, 0);
        var forward = TwoBoneIk.Solve(A, B, C, target, new Vec3(0, 0.5, 2), Vec3.UnitZ);
        var sideways = TwoBoneIk.Solve(A, B, C, target, new Vec3(2, 0.5, 0), Vec3.UnitZ);
        Assert.True(forward.Mid.Z > 0.1 && Math.Abs(forward.Mid.X) < 1e-9);
        Assert.True(sideways.Mid.X > 0.1 && Math.Abs(sideways.Mid.Z) < 1e-9);
    }

    [Fact]
    public void StraightChain_WithoutPole_UsesDefaultBend()
    {
        var straightB = new Vec3(0, 0.5, 0);
        var r = TwoBoneIk.Solve(A, straightB, C, new Vec3(0, 0.4, 0), null, Vec3.UnitZ);
        Assert.True(r.Mid.Z > 0.1);
    }
}

public class PoseSolverTests
{
    static (Rig rig, Character c, RigEvaluator ev) Setup()
    {
        var rig = MannequinFactory.Create();
        return (rig, MannequinFactory.CreateCharacter(rig), new RigEvaluator(rig));
    }

    static Vec3 ModelPos(RigEvaluator ev, Pose pose, string bone) =>
        ev.Model(pose)[ev.IndexOf(ev.Rig.FindBoneByName(bone)!.Id)].Translation;

    [Fact]
    public void PinnedFeet_StayPlanted_WhenPelvisDrops()
    {
        var (rig, c, ev) = Setup();
        var footBefore = ModelPos(ev, c.Pose, H.Foot(Side.Left));
        foreach (var chain in rig.IkChains.Where(ch => ch.Name.StartsWith("leg")))
            c.Pose.Controls[chain.Target.ToString()].Pinned = true;

        var pelvis = rig.FindBoneByName(H.Pelvis)!;
        c.Pose.Bones[pelvis.Id.ToString()] = pelvis.Rest with { Translation = pelvis.Rest.Translation + new Vec3(0, -0.2, 0.05) };
        PoseSolver.Update(rig, c.Pose);

        Assert.True(ModelPos(ev, c.Pose, H.Foot(Side.Left)).ApproxEquals(footBefore, 1e-9));
        Assert.True(ModelPos(ev, c.Pose, H.Calf(Side.Left)).Z > 0.05, "knee bends forward");
        // Foot keeps its orientation (target rotation).
        var footModel = ev.Model(c.Pose)[ev.IndexOf(rig.FindBoneByName(H.Foot(Side.Left))!.Id)];
        Assert.True(footModel.Rotation.ApproxEquals(Quat.Identity, 1e-9));
    }

    [Fact]
    public void UnpinnedControls_FollowTheirEndBones()
    {
        var (rig, c, ev) = Setup();
        var thigh = rig.FindBoneByName(H.Thigh(Side.Left))!;
        c.Pose.Bones[thigh.Id.ToString()] = thigh.Rest with { Rotation = Quat.FromAxisAngle(Vec3.UnitX, -0.6) };
        PoseSolver.Update(rig, c.Pose);
        var leg = rig.IkChains.Single(ch => ch.Name == "leg_l");
        Assert.True(c.Pose.Controls[leg.Target.ToString()].Transform.Translation
            .ApproxEquals(ModelPos(ev, c.Pose, H.Foot(Side.Left)), 1e-9));
    }

    [Fact]
    public void MovingTarget_DrivesChain()
    {
        var (rig, c, ev) = Setup();
        var arm = rig.IkChains.Single(ch => ch.Name == "arm_r");
        var target = new Vec3(-0.45, 1.25, 0.35);
        c.Pose.Controls[arm.Target.ToString()] = new ControlState { Transform = new Transform(target, Quat.Identity, Vec3.One), Pinned = true };
        PoseSolver.Update(rig, c.Pose);
        Assert.True(ModelPos(ev, c.Pose, H.Hand(Side.Right)).ApproxEquals(target, 1e-9));
    }

    [Fact]
    public void Solve_IsIdempotent()
    {
        var (rig, c, _) = Setup();
        var leg = rig.IkChains.Single(ch => ch.Name == "leg_r");
        c.Pose.Controls[leg.Target.ToString()] = new ControlState { Transform = new Transform(new Vec3(-0.15, 0.3, 0.3), Quat.Identity, Vec3.One), Pinned = true };
        PoseSolver.Update(rig, c.Pose);
        var once = c.Pose.Clone();
        PoseSolver.Update(rig, c.Pose);
        Assert.True(once.ContentEquals(c.Pose, 1e-9));
    }

    [Fact]
    public void DrivingChain_ReportsPinnedBones()
    {
        var (rig, c, _) = Setup();
        var calf = rig.FindBoneByName(H.Calf(Side.Left))!;
        Assert.Null(PoseSolver.DrivingChain(rig, c.Pose, calf.Id));
        var leg = rig.IkChains.Single(ch => ch.Name == "leg_l");
        c.Pose.Controls[leg.Target.ToString()].Pinned = true;
        Assert.Equal(leg, PoseSolver.DrivingChain(rig, c.Pose, calf.Id));
    }
}

public class PoseMirrorTests
{
    static Pose Posed(Rig rig)
    {
        var pose = new Pose();
        void Rot(string bone, Vec3 euler)
        {
            var b = rig.FindBoneByName(bone)!;
            pose.Bones[b.Id.ToString()] = b.Rest with { Rotation = Quat.FromEulerXYZ(euler) };
        }
        Rot(H.UpperArm(Side.Left), new Vec3(0.3, -0.4, -1.1));
        Rot(H.LowerArm(Side.Left), new Vec3(0.1, -1.2, 0));
        Rot(H.Thigh(Side.Right), new Vec3(-0.7, 0.2, 0.1));
        Rot(H.Spine2, new Vec3(0.1, 0.3, 0.2));
        var pelvis = rig.FindBoneByName(H.Pelvis)!;
        pose.Bones[pelvis.Id.ToString()] = new Transform(pelvis.Rest.Translation + new Vec3(0.1, -0.05, 0.02), Quat.FromAxisAngle(Vec3.UnitY, 0.4), Vec3.One);
        return pose;
    }

    [Fact]
    public void Mirror_MapsJointPositionsAcrossYZ()
    {
        var rig = MannequinFactory.Create();
        var ev = new RigEvaluator(rig);
        var pose = Posed(rig);
        var mirrored = PoseMirror.Mirror(rig, pose);
        var a = ev.Model(pose);
        var b = ev.Model(mirrored);
        var cp = PoseMirror.Counterparts(rig, ev);
        for (var i = 0; i < rig.Bones.Count; i++)
            Assert.True(b[cp[i]].Translation.ApproxEquals(PoseMirror.MirrorVector(a[i].Translation), 1e-9),
                $"{rig.Bones[i].Name}: {b[cp[i]].Translation} vs {PoseMirror.MirrorVector(a[i].Translation)}");
    }

    [Fact]
    public void MirrorTwice_IsIdentity()
    {
        var rig = MannequinFactory.Create();
        var pose = Posed(rig);
        var twice = PoseMirror.Mirror(rig, PoseMirror.Mirror(rig, pose));
        var ev = new RigEvaluator(rig);
        var a = ev.Model(pose);
        var b = ev.Model(twice);
        for (var i = 0; i < a.Length; i++)
        {
            Assert.True(a[i].Translation.ApproxEquals(b[i].Translation, 1e-9));
            Assert.True(a[i].Rotation.ApproxEquals(b[i].Rotation, 1e-9));
        }
    }

    [Fact]
    public void Mirror_WorksWithAsymmetricRestOrientations()
    {
        // Imported rigs often have left/right bones with different rest rotations.
        // Same joint positions, but right-side bones get a different rest orientation.
        var rig = MannequinFactory.Create();
        var restModel = new RigEvaluator(rig).RestModel();
        var twist = Quat.FromAxisAngle(new Vec3(1, 0.3, 0.2), 2.5);
        var wanted = restModel.Select((t, i) => rig.Bones[i].Name.EndsWith("_r") ? t with { Rotation = twist } : t).ToArray();
        var ev0 = new RigEvaluator(rig);
        for (var i = 0; i < rig.Bones.Count; i++)
        {
            var parent = ev0.ParentOf(i);
            rig.Bones[i].Rest = parent < 0 ? wanted[i] : RigEvaluator.LocalFromModel(wanted[parent], wanted[i]);
        }
        var ev = new RigEvaluator(rig);
        var check = ev.RestModel();
        for (var i = 0; i < check.Length; i++) Assert.True(check[i].Translation.ApproxEquals(restModel[i].Translation, 1e-9));
        var pose = Posed(rig);
        var mirrored = PoseMirror.Mirror(rig, pose);
        var a = ev.Model(pose);
        var b = ev.Model(mirrored);
        var cp = PoseMirror.Counterparts(rig, ev);
        for (var i = 0; i < rig.Bones.Count; i++)
            Assert.True(b[cp[i]].Translation.ApproxEquals(PoseMirror.MirrorVector(a[i].Translation), 1e-9), rig.Bones[i].Name);
    }

    [Theory]
    [InlineData("thigh_l", "thigh_r")]
    [InlineData("upper_arm.L", "upper_arm.R")]
    [InlineData("mixamorig:LeftHand", "mixamorig:RightHand")]
    [InlineData("L_Elbow", "R_Elbow")]
    [InlineData("Bip01 L Thigh", "Bip01 R Thigh")]
    [InlineData("ik_foot_r", "ik_foot_l")]
    public void SideNaming_FindsCounterparts(string a, string b)
    {
        Assert.Equal(b, SideNaming.Counterpart(a));
        Assert.Equal(a, SideNaming.Counterpart(b));
    }

    [Theory]
    [InlineData("spine_01")]
    [InlineData("head")]
    [InlineData("pelvis")]
    public void SideNaming_CentreBonesHaveNone(string name) => Assert.Null(SideNaming.Counterpart(name));
}

public class JointLimitTests
{
    [Fact]
    public void Knee_CannotBendBackwards()
    {
        var calf = MannequinFactory.Create().FindBoneByName(H.Calf(Side.Left))!;
        var q = JointLimiter.Clamp(calf, Quat.FromAxisAngle(Vec3.UnitX, -0.5), out var clamped);
        Assert.True(clamped);
        Assert.True(q.ApproxEquals(Quat.Identity, 1e-9));
        var ok = Quat.FromAxisAngle(Vec3.UnitX, 1.0);
        Assert.True(JointLimiter.Clamp(calf, ok, out clamped).ApproxEquals(ok, 1e-12));
        Assert.False(clamped);
    }
}
