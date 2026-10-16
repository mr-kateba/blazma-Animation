using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.Rigging;

/// <summary>Regression tests for the Phase 2 core review findings (each one reproduced a real bug).</summary>
public class ReviewRegressionTests
{
    static readonly CommandRegistry Registry = CommandRegistry.CreateDefault();

    static CommandResult Run(ProjectSession s, string id, object args) =>
        s.Execute(Registry.Create(id, JsonNode.Parse(JsonSerializer.Serialize(args))!.AsObject()), CommandOrigin.Test);

    static (ProjectSession s, string id) Mannequin()
    {
        var s = new ProjectSession(Project.CreateNew("Review"));
        return (s, (string)Run(s, "scene.add_mannequin", new { }).Data!["characterId"]!);
    }

    static Transform Local(ProjectSession s, string bone)
    {
        var c = s.Project.Characters[0];
        return c.Pose.LocalOf(s.Project.GetRig(c.Rig).FindBoneByName(bone)!);
    }

    static Transform[] Model(ProjectSession s)
    {
        var c = s.Project.Characters[0];
        return new RigEvaluator(s.Project.GetRig(c.Rig)).Model(c.Pose);
    }

    static int Index(ProjectSession s, string bone)
    {
        var rig = s.Project.Rigs[0];
        return new RigEvaluator(rig).IndexOf(rig.FindBoneByName(bone)!.Id);
    }

    [Fact]
    public void Ik_DoesNotDriftWithHistory_WhenTargetCirclesBack()
    {
        var (s, id) = Mannequin();
        var start = new { characterId = id, control = "ik_foot_l", x = 0.09, y = 0.25, z = 0.15, pinned = true };
        Run(s, "pose.set_control", start);
        var thigh = Local(s, H.Thigh(Side.Left));
        var calf = Local(s, H.Calf(Side.Left));

        for (var lap = 0; lap < 5; lap++)
            for (var k = 0; k < 36; k++)
            {
                var a = k * Math.PI * 2 / 36;
                Run(s, "pose.set_control", new { characterId = id, control = "ik_foot_l", x = 0.09 + 0.3 * Math.Cos(a), y = 0.3 + 0.1 * Math.Sin(a), z = 0.15 + 0.3 * Math.Sin(a) });
            }
        Run(s, "pose.set_control", start);

        Assert.True(Quat.AngleBetween(thigh.Rotation, Local(s, H.Thigh(Side.Left)).Rotation) < 1e-9);
        Assert.True(Quat.AngleBetween(calf.Rotation, Local(s, H.Calf(Side.Left)).Rotation) < 1e-9);
    }

    [Fact]
    public void Ik_DoesNotDrift_WhenPelvisCyclesWithPinnedFeet()
    {
        var (s, id) = Mannequin();
        Run(s, "pose.set_pinned", new { characterId = id, control = "ik_foot_l", pinned = true });
        Run(s, "pose.set_bone", new { characterId = id, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, ty = 0.85 });
        var thigh = Local(s, H.Thigh(Side.Left));
        for (var i = 0; i < 20; i++)
            foreach (var (rx, ry, rz) in new[] { (10, 0, 0), (0, 25, 0), (0, 0, -8), (-12, 15, 6) })
                Run(s, "pose.set_bone", new { characterId = id, bone = H.Pelvis, rx, ry, rz, ty = 0.85 });
        Run(s, "pose.set_bone", new { characterId = id, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, ty = 0.85 });
        Assert.True(Quat.AngleBetween(thigh.Rotation, Local(s, H.Thigh(Side.Left)).Rotation) < 1e-9);
    }

    [Fact]
    public void Ik_Knee_StaysAPureHinge()
    {
        var (s, id) = Mannequin();
        Run(s, "pose.set_control", new { characterId = id, control = "ik_foot_r", x = -0.2, y = 0.35, z = 0.25, pinned = true });
        var e = Local(s, H.Calf(Side.Right)).Rotation.ToEulerXYZ() * (180 / Math.PI);
        Assert.True(e.X > 10, $"knee bent {e}");
        Assert.True(Math.Abs(e.Y) < 1e-6 && Math.Abs(e.Z) < 1e-6, $"knee twist {e}");
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(-120)]
    [InlineData(-145)]
    public void Elbow_BendBeyond90_IsKept(double deg)
    {
        var (s, id) = Mannequin();
        var r = Run(s, "pose.set_bone", new { characterId = id, bone = H.LowerArm(Side.Left), rx = 0, ry = deg, rz = 0 });
        Assert.False((bool)r.Data!["clamped"]!);
        var want = Quat.FromAxisAngle(Vec3.UnitY, deg * Math.PI / 180);
        Assert.True(Quat.AngleBetween(want, Local(s, H.LowerArm(Side.Left)).Rotation) < 1e-9);
    }

    [Fact]
    public void Elbow_OverBend_IsClampedToRange()
    {
        var (s, id) = Mannequin();
        var r = Run(s, "pose.set_bone", new { characterId = id, bone = H.LowerArm(Side.Left), rx = 0, ry = -170, rz = 0 });
        Assert.True((bool)r.Data!["clamped"]!);
        Assert.True(Quat.AngleBetween(Quat.FromAxisAngle(Vec3.UnitY, -150 * Math.PI / 180), Local(s, H.LowerArm(Side.Left)).Rotation) < 1e-9);
    }

    [Fact]
    public void Pole_InModelSpace_WorksWhenCharacterIsMovedAndTurned()
    {
        var (s, id) = Mannequin();
        Run(s, "pose.set_bone", new { characterId = id, bone = H.Root, rx = 0, ry = 90, rz = 0, tx = 1.0 });
        // Character now faces +X. Put the knee pole in front of it (model space +X).
        var model = Model(s);
        var knee = model[Index(s, H.Calf(Side.Left))].Translation;
        var pole = knee + new Vec3(0.6, 0, 0);
        Run(s, "pose.set_control", new { characterId = id, control = "pole_knee_l", x = pole.X, y = pole.Y, z = pole.Z });
        var foot = model[Index(s, H.Foot(Side.Left))].Translation + new Vec3(0, 0.25, 0);
        Run(s, "pose.set_control", new { characterId = id, control = "ik_foot_l", x = foot.X, y = foot.Y, z = foot.Z, pinned = true });

        var newKnee = Model(s)[Index(s, H.Calf(Side.Left))].Translation;
        var hip = Model(s)[Index(s, H.Thigh(Side.Left))].Translation;
        Assert.True(newKnee.X > hip.X + 0.05, $"knee {newKnee} should bend towards +X (forward)");

        // pose.get reports the pole where it was put.
        var reported = Run(s, "pose.get", new { characterId = id }).Data!["controls"]!.AsArray()
            .Single(c => (string)c!["name"]! == "pole_knee_l")!["position"]!.AsArray().Select(v => (double)v!).ToArray();
        Assert.True(new Vec3(reported[0], reported[1], reported[2]).ApproxEquals(pole, 1e-6));
    }

    [Fact]
    public void ImportedZUpRig_KneeBendsForward()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "models", "khronos", "RiggedFigure.glb");
        var s = new ProjectSession(Project.CreateNew("Import"));
        var imp = Run(s, "rig.import", new { path = Path.GetFullPath(path) });
        Assert.True(imp.Ok, imp.Error?.Message);
        var rigId = (string)imp.Data!["rigId"]!;
        var id = (string)imp.Data["characterId"]!;
        Assert.False((bool)imp.Data["mapping"]!["usable"]!);

        foreach (var (bone, std) in new[]
        {
            ("torso_joint_1", "pelvis"), ("torso_joint_2", "spine_01"), ("torso_joint_3", "spine_03"), ("neck_joint_2", "head"),
            ("arm_joint_L_1", "upper_arm_l"), ("arm_joint_L_2", "lower_arm_l"), ("arm_joint_L_3", "hand_l"),
            ("arm_joint_R_1", "upper_arm_r"), ("arm_joint_R_2", "lower_arm_r"), ("arm_joint_R_3", "hand_r"),
            ("leg_joint_L_1", "thigh_l"), ("leg_joint_L_2", "calf_l"), ("leg_joint_L_3", "foot_l"),
            ("leg_joint_R_1", "thigh_r"), ("leg_joint_R_2", "calf_r"), ("leg_joint_R_3", "foot_r"),
        })
            Assert.True(Run(s, "rig.map_bone", new { rigId, bone, standard = std }).Ok);

        var report = Run(s, "rig.mapping_report", new { rigId }).Data!;
        Assert.True((bool)report["usable"]!, report.ToJsonString());
        var rig = s.Project.Rigs.Single();
        Assert.Contains(rig.IkChains, c => c.Name == "leg_l");

        var ev = new RigEvaluator(rig);
        var c0 = s.Project.Characters[0];
        var rest = ev.Model(c0.Pose);
        int Idx(string n) => ev.IndexOf(rig.FindBoneByName(n)!.Id);
        var foot = rest[Idx("leg_joint_L_3")].Translation;
        var legLen = (rest[Idx("leg_joint_L_1")].Translation - foot).Length();
        var raised = foot + new Vec3(0, legLen * 0.35, 0);
        Assert.True(Run(s, "pose.set_control", new { characterId = id, control = "ik_foot_l", x = raised.X, y = raised.Y, z = raised.Z, pinned = true }).Ok);

        var posed = ev.Model(c0.Pose);
        Assert.True(posed[Idx("leg_joint_L_3")].Translation.ApproxEquals(raised, 1e-9));
        var kneeOffset = posed[Idx("leg_joint_L_2")].Translation - (posed[Idx("leg_joint_L_1")].Translation + raised) * 0.5;
        Assert.True(kneeOffset.Z > 0, $"knee should bend forward (+Z), offset {kneeOffset}");

        // Undo of the whole import removes rig, character and embedded asset.
        while (s.CanUndo) s.Undo(CommandOrigin.Ui);
        Assert.Empty(s.Project.Rigs);
        Assert.Empty(s.Project.Assets);
    }

    [Fact]
    public void ImportedProject_EmbedsTheModel_AndReloadsExactly()
    {
        using var dir = new Blazma.Tests.Persistence.TempDir();
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "models", "khronos", "CesiumMan.glb"));
        var s = new ProjectSession(Project.CreateNew("Embed"));
        Assert.True(Run(s, "rig.import", new { path }).Ok);
        var file = dir.File("embed.blazanim");
        ProjectFile.Save(s.Project, file);
        var loaded = ProjectFile.Load(file);
        Assert.Equal(File.ReadAllBytes(path), loaded.Assets.Single().Data);
        Assert.Equal(loaded.Assets[0].Id, loaded.Rigs[0].Source.Asset);
        Assert.Equal(s.Project.Rigs[0].Source.NodeIndex, loaded.Rigs[0].Source.NodeIndex);
    }

    [Fact]
    public void MergedPartialTranslations_RedoIdentically()
    {
        var (s, id) = Mannequin();
        using (s.BeginTransaction("Drag", CommandOrigin.Test))
        {
            Run(s, "pose.set_bone", new { characterId = id, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, tx = 0.3 });
            Run(s, "pose.set_bone", new { characterId = id, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, ty = 0.5 });
        }
        var after = Local(s, H.Pelvis).Translation;
        Assert.True(after.ApproxEquals(new Vec3(0.3, 0.5, 0), 1e-12));
        s.Undo(CommandOrigin.Ui);
        s.Redo(CommandOrigin.Ui);
        Assert.True(Local(s, H.Pelvis).Translation.ApproxEquals(after, 1e-12));
    }

    [Fact]
    public void PoseJson_MissingScale_DefaultsToOne_AndRotationsAreNormalized()
    {
        var (s, id) = Mannequin();
        var spine = s.Project.Rigs[0].FindBoneByName(H.Spine1)!.Id.ToString();
        var json = $"{{\"bones\":{{\"{spine}\":{{\"translation\":[0,0.1,0],\"rotation\":[0.5,0,0,1]}}}},\"controls\":{{}}}}";
        Assert.True(Run(s, "pose.apply", new { characterId = id, poseJson = json }).Ok);
        var t = Local(s, H.Spine1);
        Assert.Equal(Vec3.One, t.Scale);
        Assert.True(t.Rotation.IsNormalized(1e-12));
        var head = Model(s)[Index(s, H.Head)].Translation;
        Assert.True(head.Y > 1.2, "skeleton did not collapse");

        Assert.Equal("invalid_args", Assert.Throws<CommandException>(() => Registry.Create("pose.apply",
            new JsonObject { ["characterId"] = id, ["poseJson"] = $"{{\"bones\":{{\"{spine}\":{{\"scale\":[0,1,1]}}}}}}" })).Code);
    }

    [Fact]
    public void Euler_RoundTrip_IsTightEverywhere()
    {
        var rnd = new Random(7);
        var worst = 0.0;
        for (var i = 0; i < 100_000; i++)
        {
            var q = new Quat(rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1, rnd.NextDouble() * 2 - 1).Normalized();
            worst = Math.Max(worst, Quat.AngleBetween(q, Quat.FromEulerXYZ(q.ToEulerXYZ())));
        }
        Assert.True(worst < 1e-7, $"worst {worst}");
    }
}
