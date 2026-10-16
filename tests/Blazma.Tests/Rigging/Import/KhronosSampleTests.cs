using Blazma.Core.Mathematics;
using Blazma.Rigging.Import;
using Blazma.Rigging.Mapping;

namespace Blazma.Tests.Rigging.Import;

/// <summary>Imports real Khronos sample assets (CC BY 4.0, see samples/models/khronos/README.md).</summary>
public class KhronosSampleTests
{
    static RigImportResult Import(string name) =>
        RigImporter.Import(RepoPaths.Sample($"models/khronos/{name}.glb"), $"models/khronos/{name}.glb");

    [Fact]
    public void RiggedFigure_has_one_bone_per_skin_joint_and_the_expected_hierarchy()
    {
        var doc = GltfReader.Read(RepoPaths.Sample("models/khronos/RiggedFigure.glb"));
        Assert.Equal("COLLADA2GLTF", doc.Generator);
        Assert.Single(doc.Skins);
        Assert.Equal(19, doc.Skins[0].Joints.Length);
        Assert.Equal(19, doc.Skins[0].InverseBindMatrices!.Length);

        var res = Import("RiggedFigure");
        var rig = res.Rig;
        Assert.Equal("RiggedFigure", rig.Name);
        Assert.Equal("models/khronos/RiggedFigure.glb", rig.Source.AssetPath);
        Assert.Equal(19, rig.Bones.Count); // all joints are already connected: no intermediates
        Assert.DoesNotContain(rig.Bones, b => b.Name.StartsWith("node_"));

        var torso1 = rig.FindBoneByName("torso_joint_1")!;
        Assert.True(torso1.Parent.IsEmpty);
        Assert.Equal(torso1.Id, rig.FindBoneByName("leg_joint_L_1")!.Parent);
        Assert.Equal(torso1.Id, rig.FindBoneByName("leg_joint_R_1")!.Parent);
        Assert.Equal(rig.FindBoneByName("torso_joint_3")!.Id, rig.FindBoneByName("neck_joint_1")!.Parent);
        Assert.Equal(rig.FindBoneByName("arm_joint_L_2")!.Id, rig.FindBoneByName("arm_joint_L_3")!.Parent);
        Assert.Equal(2, res.NodeIndexOf[torso1.Id]);
        Assert.All(rig.Bones, b => Assert.Equal(b.Name, res.OriginalNodeNames[b.Id]));
        // Parent-before-child ordering.
        foreach (var b in rig.Bones.Where(b => !b.Parent.IsEmpty))
            Assert.True(rig.IndexOf(b.Parent) < rig.IndexOf(b.Id));
    }

    [Fact]
    public void RiggedFigure_joint_rest_transforms_match_the_json_values()
    {
        var rig = Import("RiggedFigure").Rig;

        // leg_joint_L_2 is node 8 in the file; its local TRS is used unchanged.
        var rest = rig.FindBoneByName("leg_joint_L_2")!.Rest;
        Assert.True(rest.Translation.ApproxEquals(new Vec3(0, 0.26611220836639404, 0), 1e-9));
        var q = new Quat(0.21115437150001526, -0.29843246936798096, -0.04688597097992897, -0.9295986294746399).Normalized();
        Assert.True(rest.Rotation.ApproxEquals(q, 1e-9));
        Assert.True(rest.Scale.ApproxEquals(new Vec3(1.0000001192092896, 1.0000001192092896, 0.9999999403953552), 1e-9));

        // The root joint (node 2) sits below the "Z_UP" matrix node (-90 deg about X: (x,y,z) -> (x,z,-y)),
        // whose transform is folded into the root bone's rest.
        var root = rig.FindBoneByName("torso_joint_1")!.Rest;
        Assert.True(root.Translation.ApproxEquals(new Vec3(2.793969944292485e-09, 0.6860002279281616, 1.4156600514070306e-07), 1e-9));
        var zUp = Quat.FromAxisAngle(Vec3.UnitX, -Math.PI / 2);
        var local = new Quat(0.03792940452694893, 0.002913428470492363, -0.00011058452219003811, -0.9992762207984924).Normalized();
        Assert.True(root.Rotation.ApproxEquals(zUp * local, 1e-6));
    }

    [Fact]
    public void RiggedFigure_mapping_only_reports_what_the_names_support()
    {
        var res = Import("RiggedFigure");
        // The names (torso_joint_1, leg_joint_L_1, ...) are not part of any known convention, so no
        // required bone is guessed and the report asks for manual assignment.
        Assert.False(res.Mapping.IsUsable);
        Assert.Contains("pelvis", res.Mapping.MissingRequired);
        Assert.Contains("hand_l", res.Mapping.MissingRequired);
        Assert.Equal(res.Mapping.Assignments.Count, res.Rig.HumanoidMap.Count);
        Assert.Equal(res.Mapping.Assignments.Count, res.Mapping.Assignments.Select(a => a.Standard).Distinct().Count());
    }

    [Fact]
    public void RiggedFigure_manual_reassign_makes_the_mapping_usable()
    {
        var res = Import("RiggedFigure");
        var rig = res.Rig;
        var report = res.Mapping;
        var manual = new (string Bone, string Std)[]
        {
            ("torso_joint_1", "pelvis"), ("torso_joint_2", "spine_01"), ("torso_joint_3", "spine_03"), ("neck_joint_2", "head"),
            ("arm_joint_L_1", "upper_arm_l"), ("arm_joint_L_2", "lower_arm_l"), ("arm_joint_L_3", "hand_l"),
            ("arm_joint_R_1", "upper_arm_r"), ("arm_joint_R_2", "lower_arm_r"), ("arm_joint_R_3", "hand_r"),
            ("leg_joint_L_1", "thigh_l"), ("leg_joint_L_2", "calf_l"), ("leg_joint_L_3", "foot_l"),
            ("leg_joint_R_1", "thigh_r"), ("leg_joint_R_2", "calf_r"), ("leg_joint_R_3", "foot_r"),
        };
        foreach (var (bone, std) in manual)
            report = HumanoidMapper.Reassign(rig, report, rig.FindBoneByName(bone)!.Id, std);
        Assert.True(report.IsUsable, string.Join(",", report.MissingRequired));
        // The figure's left limbs are at +X in model space, so there is no facing warning.
        Assert.DoesNotContain(report.Issues, i => i.Code is "faces_negative_z" or "side_mismatch" or "hierarchy_violation");
    }

    [Fact]
    public void CesiumMan_imports_19_joints_below_two_matrix_nodes()
    {
        var res = Import("CesiumMan");
        var rig = res.Rig;
        Assert.Equal(19, rig.Bones.Count);
        Assert.Equal(19, GltfReader.Read(RepoPaths.Sample("models/khronos/CesiumMan.glb")).Skins[0].Joints.Length);
        var root = rig.Bones[0];
        Assert.Equal("Skeleton_torso_joint_1", root.Name);
        Assert.True(root.Parent.IsEmpty);
        Assert.Equal(1, rig.Bones.Count(b => b.Parent.IsEmpty));
        // Model-space position of the root joint = Z_UP * Armature * local, computed independently from the file's JSON.
        Assert.True(root.Rest.Translation.ApproxEquals(new Vec3(0.004999836906790045, 0.6789999008178711, 1.597395040546626e-08), 1e-6));
        Assert.Contains("Skeleton_neck_joint_2", rig.Bones.Select(b => b.Name));
    }

    [Fact]
    public void RiggedSimple_has_two_bones()
    {
        var res = Import("RiggedSimple");
        var rig = res.Rig;
        Assert.Equal(new[] { "Bone", "Bone.001" }, rig.Bones.Select(b => b.Name));
        Assert.Equal(rig.Bones[0].Id, rig.Bones[1].Parent);
        Assert.True(rig.Bones[0].Parent.IsEmpty);
        Assert.True(rig.Bones[1].Rest.Translation.ApproxEquals(new Vec3(1.2150299863455949e-11, 0.02797747030854225, 4.187077045440674), 1e-9));
        // "Bone" is a matrix node below Z_UP * Armature (also matrices); their product is folded into its rest.
        Assert.True(rig.Bones[0].Rest.Translation.ApproxEquals(new Vec3(-1.3597299641787686e-07, -4.1803297996521, 0), 1e-6));
    }
}
