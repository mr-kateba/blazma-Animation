using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Rigging;
using Blazma.Tests.Persistence;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.Commands;

public class PoseCommandTests
{
    static readonly CommandRegistry Registry = CommandRegistry.CreateDefault();

    static CommandResult Run(ProjectSession s, string id, object args) =>
        s.Execute(Registry.Create(id, JsonNode.Parse(JsonSerializer.Serialize(args))!.AsObject()), CommandOrigin.Test);

    static (ProjectSession s, string characterId) WithMannequin()
    {
        var s = new ProjectSession(Project.CreateNew("Pose test"));
        var r = Run(s, "scene.add_mannequin", new { });
        Assert.True(r.Ok, r.Error?.Message);
        return (s, (string)r.Data!["characterId"]!);
    }

    static Vec3 BonePos(ProjectSession s, string bone)
    {
        var c = s.Project.Characters[0];
        var rig = s.Project.GetRig(c.Rig);
        var ev = new RigEvaluator(rig);
        return ev.Model(c.Pose)[ev.IndexOf(rig.FindBoneByName(bone)!.Id)].Translation;
    }

    [Fact]
    public void AddMannequin_UndoRedo_ReusesSameObjects()
    {
        var (s, id) = WithMannequin();
        Assert.Single(s.Project.Rigs);
        s.Undo(CommandOrigin.Ui);
        Assert.Empty(s.Project.Characters);
        Assert.Empty(s.Project.Rigs);
        s.Redo(CommandOrigin.Ui);
        Assert.Equal(id, s.Project.Characters.Single().Id.ToString());

        // A second mannequin shares the rig, so library poses work on both.
        Run(s, "scene.add_mannequin", new { name = "Second" });
        Assert.Single(s.Project.Rigs);
        Assert.Equal(2, s.Project.Characters.Count);
    }

    [Fact]
    public void SetBone_RotatesAndIsUndoable_AndDragMergesIntoOneStep()
    {
        var (s, id) = WithMannequin();
        var handBefore = BonePos(s, H.Hand(Side.Left));
        using (s.BeginTransaction("Drag", CommandOrigin.Test))
            for (var deg = 5; deg <= 60; deg += 5)
                Assert.True(Run(s, "pose.set_bone", new { characterId = id, bone = H.UpperArm(Side.Left), rx = 0, ry = 0, rz = -deg }).Ok);

        Assert.Equal(1, s.UndoHistory[^1].CommandCount);
        var hand = BonePos(s, H.Hand(Side.Left));
        Assert.True(hand.Y < handBefore.Y - 0.3, "arm swung down");

        s.Undo(CommandOrigin.Ui);
        Assert.True(BonePos(s, H.Hand(Side.Left)).ApproxEquals(handBefore, 1e-12));
        s.Redo(CommandOrigin.Ui);
        Assert.True(BonePos(s, H.Hand(Side.Left)).ApproxEquals(hand, 1e-12));
    }

    [Fact]
    public void SetBone_ByNameOrId_AndJointLimitsWarn()
    {
        var (s, id) = WithMannequin();
        var calfId = s.Project.Rigs[0].FindBoneByName(H.Calf(Side.Left))!.Id.ToString();
        var r = Run(s, "pose.set_bone", new { characterId = id, bone = calfId, rx = -40, ry = 0, rz = 0 });
        Assert.True(r.Ok);
        Assert.Contains(r.Warnings, w => w.Code == "clamped_by_limits");
        Assert.True((bool)r.Data!["clamped"]!);
    }

    [Fact]
    public void SetBone_OnPinnedChain_IsRejectedWithClearCode()
    {
        var (s, id) = WithMannequin();
        Assert.True(Run(s, "pose.set_pinned", new { characterId = id, control = "ik_foot_l", pinned = true }).Ok);
        var r = Run(s, "pose.set_bone", new { characterId = id, bone = H.Calf(Side.Left), rx = 30, ry = 0, rz = 0 });
        Assert.Equal("driven_by_ik", r.Error!.Code);
    }

    [Fact]
    public void PinnedFeet_StayPut_WhenPelvisMovesDown()
    {
        var (s, id) = WithMannequin();
        var footL = BonePos(s, H.Foot(Side.Left));
        var footR = BonePos(s, H.Foot(Side.Right));
        Run(s, "pose.set_pinned", new { characterId = id, control = "ik_foot_l", pinned = true });
        Run(s, "pose.set_pinned", new { characterId = id, control = "ik_foot_r", pinned = true });
        Assert.True(Run(s, "pose.set_bone", new { characterId = id, bone = H.Pelvis, rx = 10, ry = 0, rz = 0, ty = 0.75 }).Ok);

        Assert.True(BonePos(s, H.Pelvis).Y < 0.8);
        Assert.True(BonePos(s, H.Foot(Side.Left)).ApproxEquals(footL, 1e-9));
        Assert.True(BonePos(s, H.Foot(Side.Right)).ApproxEquals(footR, 1e-9));
    }

    [Fact]
    public void SetControl_MovesHandToTarget()
    {
        var (s, id) = WithMannequin();
        var r = Run(s, "pose.set_control", new { characterId = id, control = "ik_hand_r", x = -0.35, y = 1.3, z = 0.4, pinned = true });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Equal("arm_r", (string)r.Data!["chain"]!);
        Assert.True(BonePos(s, H.Hand(Side.Right)).ApproxEquals(new Vec3(-0.35, 1.3, 0.4), 1e-9));
    }

    [Fact]
    public void Mirror_SwapsSides()
    {
        var (s, id) = WithMannequin();
        Run(s, "pose.set_bone", new { characterId = id, bone = H.UpperArm(Side.Left), rx = 0, ry = 30, rz = -50 });
        var left = BonePos(s, H.Hand(Side.Left));
        Assert.True(Run(s, "pose.mirror", new { characterId = id }).Ok);
        Assert.True(BonePos(s, H.Hand(Side.Right)).ApproxEquals(PoseMirror.MirrorVector(left), 1e-9));
    }

    [Fact]
    public void Reset_And_CopyPaste_Work()
    {
        var (s, id) = WithMannequin();
        Run(s, "pose.set_bone", new { characterId = id, bone = H.Head, rx = 20, ry = 10, rz = 0 });
        var copied = (string)Run(s, "pose.get", new { characterId = id }).Data!["pose"]!;
        Run(s, "pose.reset", new { characterId = id });
        Assert.Empty(s.Project.Characters[0].Pose.Bones);
        var headRest = BonePos(s, H.Head);

        Assert.True(Run(s, "pose.apply", new { characterId = id, poseJson = copied }).Ok);
        Assert.True(s.Project.Characters[0].Pose.Bones.Count > 0);
        Assert.True(BonePos(s, H.Head).ApproxEquals(headRest, 1e-12)); // head joint itself does not move when only rotated
        Assert.Equal("invalid_args", Assert.Throws<CommandException>(() =>
            Registry.Create("pose.apply", new JsonObject { ["characterId"] = id, ["poseJson"] = "{not json" })).Code);
    }

    [Fact]
    public void PoseLibrary_SaveApplyRenameDelete()
    {
        var (s, id) = WithMannequin();
        Run(s, "pose.set_bone", new { characterId = id, bone = H.Spine2, rx = 15, ry = 25, rz = 0 });
        var poseId = (string)Run(s, "poselib.save", new { characterId = id, name = "Guard" }).Data!["poseId"]!;
        Run(s, "pose.reset", new { characterId = id });
        Assert.True(Run(s, "poselib.apply", new { characterId = id, poseId }).Ok);
        Assert.Single(s.Project.Characters[0].Pose.Bones, kv => kv.Key == s.Project.Rigs[0].FindBoneByName(H.Spine2)!.Id.ToString());
        Run(s, "poselib.rename", new { poseId, name = "Guard high" });
        Assert.Equal("Guard high", s.Project.PoseLibrary[0].Name);
        Run(s, "poselib.delete", new { poseId });
        Assert.Empty(s.Project.PoseLibrary);
        s.Undo(CommandOrigin.Ui);
        Assert.Equal("Guard high", s.Project.PoseLibrary[0].Name);
    }

    [Fact]
    public void Inspect_ReportsRigAndPose()
    {
        var (s, id) = WithMannequin();
        var rig = Run(s, "rig.inspect", new { characterId = id }).Data!;
        Assert.Equal(H.Bones.Count, (int)rig["boneCount"]!);
        Assert.Empty(rig["missingRequired"]!.AsArray());
        Assert.Equal(4, rig["ikChains"]!.AsArray().Count);
        var pose = Run(s, "pose.get", new { characterId = id }).Data!;
        Assert.Equal(H.Bones.Count, pose["bones"]!.AsArray().Count);
        var proj = Run(s, "project.inspect", new { }).Data!;
        Assert.Equal(id, (string)proj["characters"]![0]!["characterId"]!);
    }

    [Theory]
    [InlineData("{\"characterId\":\"01M52R40DS5ZSQ9YQBD439QZJP\",\"bone\":\"head\",\"rx\":1,\"ry\":2,\"rz\":3}", "not_found")]
    [InlineData("{\"bone\":\"head\",\"rx\":1,\"ry\":2,\"rz\":3}", "invalid_args")]
    public void BadReferences_FailCleanly(string json, string code)
    {
        var (s, _) = WithMannequin();
        CommandResult r;
        try { r = s.Execute(Registry.Create("pose.set_bone", JsonNode.Parse(json)!.AsObject()), CommandOrigin.Test); }
        catch (CommandException e) { Assert.Equal(code, e.Code); return; }
        Assert.Equal(code, r.Error!.Code);
    }

    /// <summary>Phase 2 acceptance (core part): pose a character, save poses, reload, identical values.</summary>
    [Fact]
    public void Acceptance_PoseSaveReload_IsExact()
    {
        using var dir = new TempDir();
        var (s, id) = WithMannequin();
        Run(s, "pose.set_pinned", new { characterId = id, control = "ik_foot_l", pinned = true });
        Run(s, "pose.set_bone", new { characterId = id, bone = H.Pelvis, rx = 5, ry = 20, rz = -3, ty = 0.88 });
        Run(s, "pose.set_control", new { characterId = id, control = "ik_hand_r", x = -0.3, y = 1.4, z = 0.35, pinned = true });
        Run(s, "pose.set_bone", new { characterId = id, bone = H.UpperArm(Side.Left), rx = 10, ry = -20, rz = -70 });
        Run(s, "poselib.save", new { characterId = id, name = "Throw" });

        var path = dir.File("posed.blazanim");
        ProjectFile.Save(s.Project, path);
        var loaded = ProjectFile.Load(path);

        var a = s.Project.Characters[0];
        var b = loaded.Characters.Single();
        Assert.True(a.Pose.ContentEquals(b.Pose, 0)); // bit-exact
        Assert.True(s.Project.PoseLibrary[0].Pose.ContentEquals(loaded.PoseLibrary[0].Pose, 0));
        var rigA = s.Project.Rigs[0];
        var rigB = loaded.Rigs[0];
        Assert.Equal(rigA.Bones.Select(x => (x.Id, x.Name, x.Parent, x.Rest)), rigB.Bones.Select(x => (x.Id, x.Name, x.Parent, x.Rest)));
        Assert.Equal(rigA.IkChains.Select(x => x.Name), rigB.IkChains.Select(x => x.Name));

        var evA = new RigEvaluator(rigA);
        var evB = new RigEvaluator(rigB);
        var ma = evA.Model(a.Pose);
        var mb = evB.Model(b.Pose);
        for (var i = 0; i < ma.Length; i++) Assert.Equal(ma[i], mb[i]);
    }
}
