using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Agent;
using Blazma.Combat;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Rigging;
using Blazma.Rigging.Import;
using Blazma.Rigging.Mapping;
using Blazma.Tests.Advanced;
using Blazma.Tests.Combat;
using TempDir = Blazma.Tests.Persistence.TempDir;
using Blazma.Tests.Rigging.Import;
using Blazma.Tests.Rigging.Mapping;

namespace Blazma.Tests.Agent;

/// <summary>Audit fixes for CLI/agent use: overwrite as the user, automatic humanoid mapping, standard bone names, messages.</summary>
public class AuditUsabilityTests
{
    static JsonObject J(object o) => JsonNode.Parse(JsonSerializer.Serialize(o))!.AsObject();

    static (AgentHost Host, string Session, string Clip, string Character, TempDir Dir, ProjectSession S) ExportScene(bool callerIsUser)
    {
        var dir = new TempDir();
        var (s, man, _) = P8.Scene(cesium: false);
        var clip = P8.ApplyTemplate(s, "builtin.wave", man);
        s.MarkSaved(dir.File("p.blazanim"));
        var host = new AgentHost(P8.Registry, () => s, userPresent: false) { CallerIsUser = callerIsUser };
        host.ProjectFolder = () => dir.Path;
        return (host, host.Connect(callerIsUser ? "cli" : "mcp").Id, clip, man, dir, s);
    }

    [Fact]
    public void CliUser_MayOverwriteAnExport_McpAgentsNever()
    {
        var (cli, cs, clip, ch, dir, _) = ExportScene(callerIsUser: true);
        using (dir)
        {
            var path = dir.File("wave.glb");
            Assert.True(cli.Call(cs, "animation.export", J(new { clipId = clip, characterId = ch, path })).Ok);
            var again = cli.Call(cs, "animation.export", J(new { clipId = clip, characterId = ch, path }));
            Assert.Equal("file_exists", again.Error!.Code);
            Assert.Contains("--arg overwrite=true", again.Error.Message);
            var r = cli.Call(cs, "animation.export", J(new { clipId = clip, characterId = ch, path, overwrite = true }));
            Assert.True(r.Ok, r.Error?.Message);
        }

        var (mcp, ms, clip2, ch2, dir2, _) = ExportScene(callerIsUser: false);
        using (dir2)
        {
            var path = dir2.File("wave.glb");
            Assert.True(mcp.Call(ms, "animation.export", J(new { clipId = clip2, characterId = ch2, path })).Ok);
            var denied = mcp.Call(ms, "animation.export", J(new { clipId = clip2, characterId = ch2, path, overwrite = true }));
            Assert.Equal("file_exists", denied.Error!.Code);
            Assert.Contains("Agents only create new files", denied.Error.Message);
        }
    }

    [Fact]
    public void PathNotAllowed_TellsHowToAllowTheFolder()
    {
        var (host, sid, clip, ch, dir, _) = ExportScene(callerIsUser: true);
        using (dir)
        using (var other = new TempDir())
        {
            var r = host.Call(sid, "animation.export", J(new { clipId = clip, characterId = ch, path = other.File("x.glb") }));
            Assert.Equal("path_not_allowed", r.Error!.Code);
            Assert.Contains("--allow-write", r.Error.Message);
            var read = host.Call(sid, "rig.import", J(new { path = other.File("x.glb") }));
            Assert.Equal("path_not_allowed", read.Error!.Code);
            Assert.Contains("--allow-read", read.Error.Message);
        }
    }

    [Fact]
    public void CesiumMan_IsMappedOnImport_WithConfidence_AndRigAutoMapRestoresIt()
    {
        var s = new ProjectSession(Project.CreateNew("Auto"));
        var imp = P8.Ok(s, "rig.import", new { path = RepoPaths.Sample("models/khronos/CesiumMan.glb") }).Data!;
        var mapping = imp["mapping"]!;
        Assert.True((bool)mapping["usable"]!);
        Assert.Equal("medium", (string)mapping["confidence"]!); // inferred from the shape: worth a glance
        var rigId = (string)imp["rigId"]!;
        var rig = s.Project.GetRig(Blazma.Core.Ids.ObjectId.Parse(rigId));
        Assert.True(rig.HumanoidMap.Count >= 16);
        foreach (var (bone, std) in P8.CesiumMap) Assert.Equal(bone, rig.FindByStandardName(std)!.Name);
        Assert.Equal(new[] { "arm_l", "arm_r", "leg_l", "leg_r" }, rig.IkChains.Select(c => c.Name).Order());

        P8.Ok(s, "rig.map_bone", new { rigId, bone = "Skeleton_arm_joint_R__3_" }); // unmap the right hand
        Assert.Null(rig.FindByStandardName("hand_r"));
        var auto = P8.Ok(s, "rig.auto_map", new { rigId }).Data!;
        Assert.True((bool)auto["usable"]!);
        Assert.Equal("Skeleton_arm_joint_R__3_", rig.FindByStandardName("hand_r")!.Name);
        Assert.True(s.Undo(CommandOrigin.Ui).Ok);
        Assert.Null(rig.FindByStandardName("hand_r"));
    }

    [Fact]
    public void UnnamedSyntheticHumanoid_IsMappedFromItsShape()
    {
        var roles = HumanoidTemplate.Blazma();
        var i = 0;
        var anonymous = roles.ToDictionary(kv => kv.Key, _ => $"jnt_{i++}"); // names that say nothing (no side, no part)
        using var d = new TempDir();
        var res = RigImporter.Import(HumanoidTemplate.Build(anonymous).WriteGlb(d.File("anon.glb")));
        Assert.True(res.Mapping.IsUsable, "missing " + string.Join(",", res.Mapping.MissingRequired));
        foreach (var std in HumanoidStandard.Required) Assert.Equal(anonymous[std], res.Rig.FindByStandardName(std)!.Name);
        Assert.Equal(anonymous["clavicle_l"], res.Rig.FindByStandardName("clavicle_l")!.Name);
        Assert.DoesNotContain(res.Mapping.Issues, x => x.Code is "side_mismatch" or "hierarchy_violation");
        Assert.Equal("medium", res.Mapping.ConfidenceLevel);

        // Named conventions keep high confidence.
        var named = RigImporter.Import(HumanoidTemplate.Build(HumanoidTemplate.Mixamo()).WriteGlb(d.File("mixamo.glb")));
        Assert.Equal("high", named.Mapping.ConfidenceLevel);
    }

    [Fact]
    public void StandardBoneNames_ResolveThroughTheHumanoidMap()
    {
        var (s, _, ces) = P8.Scene();
        var prop = P8.Ok(s, "prop.add", new { name = "Sword", sizeX = 0.04, sizeY = 0.9, sizeZ = 0.04, characterId = ces, bone = "hand_r" }).Data!;
        var p = s.Project.Props.Single(x => x.Id.ToString() == (string)prop["propId"]!);
        Assert.Equal("Skeleton_arm_joint_R__3_", P8.RigOf(s, ces).GetBone(p.Attachment!.Bone).Name);
        var clip = P8.ApplyTemplate(s, "builtin.attack_swing", ces);
        P8.Ok(s, "hitbox.add", new { clipId = clip, name = "Fist", bone = "hand_l", radius = 0.08, activeFrames = "10-12" });
        P8.Ok(s, "key.set", new { clipId = clip, characterId = ces, bones = "thigh_l", frame = 2 });
        var bad = P8.Run(s, "prop.add", new { name = "X", characterId = ces, bone = "toe_l" }); // a standard name the rig does not map
        Assert.Equal("not_found", bad.Error!.Code);
        Assert.Contains("rig.map_bone", bad.Error.Message);
    }

    [Fact]
    public void HitTest_ByDefault_ChoosesEnoughSubstepsForAFastSwing()
    {
        var c = C9.Build();
        var data = P8.Ok(c.S, "combat.hit_test", new { attackerCharacterId = c.Attacker, attackClipId = c.Attack, defenderCharacterId = c.Defender, defenderZ = 0.75 });
        Assert.DoesNotContain(data.Warnings, w => w.Code == HitDetector.MissedWarningCode);
        Assert.False((bool)data.Data!["mayHaveMissedContacts"]!);
        Assert.InRange((int)data.Data!["maxSubsteps"]!, 9, HitDetector.MaxSubstepsLimit);

        var capped = P8.Ok(c.S, "combat.hit_test", new { attackerCharacterId = c.Attacker, attackClipId = c.Attack, defenderCharacterId = c.Defender, defenderZ = 0.75, maxSubsteps = 1 });
        Assert.Equal(1, (int)capped.Data!["maxSubsteps"]!);
    }
}
