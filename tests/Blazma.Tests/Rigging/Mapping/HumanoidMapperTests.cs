using Blazma.Core.Model;
using Blazma.Rigging;
using Blazma.Rigging.Import;
using Blazma.Rigging.Mapping;
using Blazma.Tests.Rigging.Import;

namespace Blazma.Tests.Rigging.Mapping;

public class HumanoidMapperTests
{
    static readonly string[] Roles =
    {
        "root", "pelvis", "spine_01", "spine_02", "spine_03", "neck", "head",
        "clavicle_l", "upper_arm_l", "lower_arm_l", "hand_l", "thigh_l", "calf_l", "foot_l", "toe_l",
        "clavicle_r", "upper_arm_r", "lower_arm_r", "hand_r", "thigh_r", "calf_r", "foot_r", "toe_r",
    };

    static RigImportResult ImportTemplate(GltfTestBuilder b)
    {
        using var d = new TempDir();
        return RigImporter.Import(b.WriteGlb(d.File("t.glb")));
    }

    public static IEnumerable<object[]> Conventions()
    {
        yield return new object[] { "Mixamo", HumanoidTemplate.Mixamo() };
        yield return new object[] { "Unreal", HumanoidTemplate.Unreal() };
        yield return new object[] { "Unity", HumanoidTemplate.Unity() };
        yield return new object[] { "Blender", HumanoidTemplate.Blender() };
        yield return new object[] { "BlenderDEF", HumanoidTemplate.Blender("DEF-") };
        yield return new object[] { "Biped", HumanoidTemplate.Biped() };
        yield return new object[] { "VRM", HumanoidTemplate.Vrm() };
        yield return new object[] { "Blazma", HumanoidTemplate.Blazma() };
    }

    [Theory]
    [MemberData(nameof(Conventions))]
    public void Each_convention_maps_every_required_bone_to_the_expected_bone(string label, Dictionary<string, string> names)
    {
        var extras = label == "Mixamo"
            ? new Dictionary<string, string> { ["hand_l"] = "mixamorig:LeftHandThumb1", ["toe_l"] = "mixamorig:LeftToe_End" }
            : null;
        var res = ImportTemplate(HumanoidTemplate.Build(names, extras: extras));
        var rep = res.Mapping;

        Assert.True(rep.IsUsable, label + ": missing " + string.Join(",", rep.MissingRequired));
        Assert.DoesNotContain(rep.Issues, i => i.Code is "faces_negative_z" or "hierarchy_violation" or "side_mismatch");
        foreach (string role in Roles.Where(names.ContainsKey))
            Assert.Equal(names[role], res.Rig.FindByStandardName(role)?.Name);
        Assert.Empty(rep.MissingRequired);
        Assert.Equal(res.Rig.HumanoidMap.Count, rep.Assignments.Count);
        Assert.Equal(rep.Assignments.Count, rep.Assignments.Select(a => a.Standard).Distinct().Count());
        Assert.Equal(rep.Assignments.Count, rep.Assignments.Select(a => a.BoneId).Distinct().Count());
        Assert.All(rep.Assignments, a => Assert.True(HumanoidStandard.IsStandard(a.Standard)));
        Assert.InRange(rep.Coverage, 0.8, 1.0);
    }

    [Fact]
    public void Blazma_names_are_exact_and_mixamo_names_are_synonyms()
    {
        var blazma = ImportTemplate(HumanoidTemplate.Build(HumanoidTemplate.Blazma())).Mapping;
        Assert.All(blazma.Assignments, a => Assert.Equal(MappingConfidence.Exact, a.Confidence));
        Assert.Equal(1.0, blazma.Coverage, 9);
        Assert.Empty(blazma.MissingOptional);

        var mixamo = ImportTemplate(HumanoidTemplate.Build(HumanoidTemplate.Mixamo())).Mapping;
        Assert.Equal(MappingConfidence.Synonym, mixamo.Assignments.Single(a => a.Standard == "upper_arm_l").Confidence);
        Assert.Equal(MappingConfidence.Synonym, mixamo.Assignments.Single(a => a.Standard == "spine_02").Confidence);
        Assert.Equal(new[] { "root" }, mixamo.MissingOptional);
    }

    [Fact]
    public void Rigify_pelvis_is_found_through_the_thighs_parent()
    {
        var res = ImportTemplate(HumanoidTemplate.Build(HumanoidTemplate.Blender()));
        var pelvis = res.Mapping.Assignments.Single(a => a.Standard == "pelvis");
        Assert.Equal("spine", pelvis.BoneName);
        Assert.Equal(MappingConfidence.Hierarchy, pelvis.Confidence);
        Assert.Equal("spine.004", res.Rig.FindByStandardName("neck")!.Name); // shallowest neck candidate
    }

    [Fact]
    public void Single_spine_bone_maps_to_spine_01_only()
    {
        var n = HumanoidTemplate.Unity();
        n.Remove("spine_02"); n.Remove("spine_03");
        var rep = ImportTemplate(HumanoidTemplate.Build(n)).Mapping;
        Assert.True(rep.IsUsable);
        Assert.Equal(new[] { "spine_02", "spine_03" }, rep.MissingOptional.Where(m => m.StartsWith("spine")));
    }

    [Fact]
    public void Two_spine_bones_map_to_spine_01_and_spine_03()
    {
        var n = HumanoidTemplate.Unity();
        n.Remove("spine_03");
        var res = ImportTemplate(HumanoidTemplate.Build(n));
        Assert.Equal("Spine", res.Rig.FindByStandardName("spine_01")!.Name);
        Assert.Equal("Chest", res.Rig.FindByStandardName("spine_03")!.Name);
        Assert.Null(res.Rig.FindByStandardName("spine_02"));
    }

    [Fact]
    public void Four_spine_bones_map_first_middle_last_and_report_the_rest()
    {
        var n = HumanoidTemplate.Unreal();
        n["spine_04"] = "spine_04";
        var res = ImportTemplate(HumanoidTemplate.Build(n));
        Assert.Equal("spine_01", res.Rig.FindByStandardName("spine_01")!.Name);
        Assert.Equal("spine_03", res.Rig.FindByStandardName("spine_02")!.Name); // index N/2 = 2
        Assert.Equal("spine_04", res.Rig.FindByStandardName("spine_03")!.Name);
        Assert.Contains(res.Mapping.Issues, i => i.Code == "spine_extra_bones_unmapped");
    }

    [Fact]
    public void Spine_hanging_off_the_root_beside_the_pelvis_is_still_found()
    {
        var res = ImportTemplate(HumanoidTemplate.Build(HumanoidTemplate.Biped(), spineSiblingOfPelvis: true));
        Assert.True(res.Mapping.IsUsable, string.Join(",", res.Mapping.MissingRequired));
        Assert.Equal("Bip01 Spine", res.Rig.FindByStandardName("spine_01")!.Name);
        Assert.Equal("Bip01 Spine2", res.Rig.FindByStandardName("spine_03")!.Name);
    }

    [Fact]
    public void Rig_facing_negative_z_is_warned_about_but_not_flipped()
    {
        var res = ImportTemplate(HumanoidTemplate.Build(HumanoidTemplate.Unreal(), flipX: true));
        Assert.Contains(res.Mapping.Issues, i => i.Code == "faces_negative_z");
        Assert.DoesNotContain(res.Mapping.Issues, i => i.Code == "side_mismatch");
        Assert.True(res.Mapping.IsUsable);
        Assert.Equal("thigh_l", res.Rig.FindByStandardName("thigh_l")!.Name);
    }

    [Fact]
    public void A_single_mislocated_side_bone_gets_a_side_mismatch_issue()
    {
        var res = ImportTemplate(HumanoidTemplate.Build(HumanoidTemplate.Unreal()));
        var hand = res.Rig.FindBoneByName("hand_l")!;
        hand.Rest = hand.Rest with { Translation = new(-0.5, 0, 0) };
        var rep = HumanoidMapper.AutoMap(res.Rig);
        var issue = Assert.Single(rep.Issues, i => i.Code == "side_mismatch");
        Assert.Equal("hand_l", issue.BoneName);
        Assert.DoesNotContain(rep.Issues, i => i.Code == "faces_negative_z");
    }

    [Fact]
    public void Name_match_violating_the_hierarchy_is_rejected_with_an_issue()
    {
        var res = ImportTemplate(HumanoidTemplate.Build(HumanoidTemplate.Unreal()));
        var rig = res.Rig;
        rig.FindBoneByName("calf_l")!.Parent = rig.FindBoneByName("pelvis")!.Id; // no longer below thigh_l
        var rep = HumanoidMapper.AutoMap(rig);
        Assert.Contains(rep.Issues, i => i.Code == "hierarchy_violation" && i.BoneName == "calf_l");
        Assert.Contains("calf_l", rep.MissingRequired);
        Assert.False(rep.IsUsable);
        Assert.Null(rep.Assignments.FirstOrDefault(a => a.Standard == "calf_l"));
    }

    [Fact]
    public void Hand_must_be_below_the_lower_arm()
    {
        var res = ImportTemplate(HumanoidTemplate.Build(HumanoidTemplate.Mixamo()));
        var rig = res.Rig;
        rig.FindBoneByName("mixamorig:RightHand")!.Parent = rig.FindBoneByName("mixamorig:Hips")!.Id;
        var rep = HumanoidMapper.AutoMap(rig);
        Assert.Contains(rep.Issues, i => i.Code == "hierarchy_violation" && i.BoneName == "mixamorig:RightHand");
        Assert.Contains("hand_r", rep.MissingRequired);
    }

    [Fact]
    public void AutoMap_is_pure_and_Apply_writes_the_humanoid_map()
    {
        var res = ImportTemplate(HumanoidTemplate.Build(HumanoidTemplate.Mixamo()));
        var rig = res.Rig;
        rig.HumanoidMap.Clear();
        var rep = HumanoidMapper.AutoMap(rig);
        Assert.Empty(rig.HumanoidMap);
        HumanoidMapper.Apply(rig, rep);
        Assert.Equal(rep.Assignments.Count, rig.HumanoidMap.Count);
        Assert.Equal("thigh_r", rig.StandardNameOf(rig.FindBoneByName("mixamorig:RightUpLeg")!.Id));

        // Apply replaces, it does not append.
        HumanoidMapper.Apply(rig, rep);
        Assert.Equal(rep.Assignments.Count, rig.HumanoidMap.Count);
    }

    [Fact]
    public void Reassign_moves_a_standard_name_to_another_bone()
    {
        var res = ImportTemplate(HumanoidTemplate.Build(HumanoidTemplate.Unreal()));
        var rig = res.Rig;
        var rep = res.Mapping;
        var neck = rig.FindBoneByName("neck_01")!;

        var next = HumanoidMapper.Reassign(rig, rep, neck.Id, "head");
        Assert.Equal(neck.Id, next.Assignments.Single(a => a.Standard == "head").BoneId);
        Assert.Equal(MappingConfidence.Manual, next.Assignments.Single(a => a.Standard == "head").Confidence);
        Assert.DoesNotContain(next.Assignments, a => a.Standard == "neck"); // neck_01 lost its old name
        Assert.DoesNotContain(next.Assignments, a => a.BoneName == "head");  // old head holder lost 'head'
        Assert.Equal(next.Assignments.Count, next.Assignments.Select(a => a.BoneId).Distinct().Count());
        Assert.Contains("head", rep.Assignments.Select(a => a.Standard)); // original report untouched
    }

    [Fact]
    public void Reassign_to_null_unmaps_and_recomputes_missing_lists()
    {
        var res = ImportTemplate(HumanoidTemplate.Build(HumanoidTemplate.Unreal()));
        var rig = res.Rig;
        Assert.True(res.Mapping.IsUsable);

        var next = HumanoidMapper.Reassign(rig, res.Mapping, rig.FindBoneByName("hand_r")!.Id, null);
        Assert.False(next.IsUsable);
        Assert.Equal(new[] { "hand_r" }, next.MissingRequired);

        var back = HumanoidMapper.Reassign(rig, next, rig.FindBoneByName("hand_r")!.Id, "hand_r");
        Assert.True(back.IsUsable);
        Assert.Empty(back.MissingRequired);

        var unmappedToy = HumanoidMapper.Reassign(rig, res.Mapping, rig.FindBoneByName("ball_l")!.Id, null);
        Assert.Contains("toe_l", unmappedToy.MissingOptional);
        Assert.True(unmappedToy.IsUsable);
    }

    [Fact]
    public void Reassign_validates_the_standard_name_and_the_bone()
    {
        var res = ImportTemplate(HumanoidTemplate.Build(HumanoidTemplate.Unreal()));
        var bone = res.Rig.Bones[0];
        Assert.Throws<ArgumentException>(() => HumanoidMapper.Reassign(res.Rig, res.Mapping, bone.Id, "not_a_bone"));
        Assert.Throws<ArgumentException>(() => HumanoidMapper.Reassign(res.Rig, res.Mapping, Blazma.Core.Ids.ObjectId.New(), "head"));
    }

    [Fact]
    public void Reassign_reruns_hierarchy_checks()
    {
        var res = ImportTemplate(HumanoidTemplate.Build(HumanoidTemplate.Unreal()));
        var rig = res.Rig;
        // Claim the right hand bone is the left calf: not below thigh_l.
        var next = HumanoidMapper.Reassign(rig, res.Mapping, rig.FindBoneByName("hand_r")!.Id, "calf_l");
        Assert.Contains(next.Issues, i => i.Code == "hierarchy_violation" && i.BoneName == "hand_r");
    }

    [Fact]
    public void Unnamed_limb_bones_get_their_side_from_the_rest_position()
    {
        var b = new GltfTestBuilder();
        int pelvis = b.Add("Hips", -1, new Blazma.Core.Mathematics.Vec3(0, 1, 0));
        int spine = b.Add("Spine", pelvis, new Blazma.Core.Mathematics.Vec3(0, 0.1, 0));
        int head = b.Add("Head", spine, new Blazma.Core.Mathematics.Vec3(0, 0.4, 0));
        int tl = b.Add("Thigh", pelvis, new Blazma.Core.Mathematics.Vec3(0.1, -0.05, 0));
        int cl = b.Add("Calf", tl, new Blazma.Core.Mathematics.Vec3(0, -0.4, 0));
        int fl = b.Add("Foot", cl, new Blazma.Core.Mathematics.Vec3(0, -0.4, 0));
        _ = head; _ = fl;
        var rep = ImportTemplate(b).Mapping;
        var a = rep.Assignments.Single(x => x.Standard == "thigh_l");
        Assert.Equal("Thigh", a.BoneName);
        Assert.Equal(MappingConfidence.Hierarchy, a.Confidence);
        Assert.Contains(rep.Assignments, x => x.Standard == "foot_l");
    }
}
