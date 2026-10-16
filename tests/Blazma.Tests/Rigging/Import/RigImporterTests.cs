using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Rigging.Import;

namespace Blazma.Tests.Rigging.Import;

public class RigImporterTests
{
    [Fact]
    public void Simple_chain_becomes_bones_with_rest_hierarchy_and_source()
    {
        var b = new GltfTestBuilder();
        int a = b.Add("Hips", -1, new Vec3(0, 1, 0));
        int s = b.Add("Spine", a, new Vec3(0, 0.2, 0), r: Quat.FromAxisAngle(Vec3.UnitX, 0.3));
        b.Add("Head", s, new Vec3(0, 0.3, 0), s: new Vec3(1, 2, 1));
        using var d = new TempDir();
        var res = RigImporter.Import(b.WriteGltf(d.File("char.gltf")));

        var rig = res.Rig;
        Assert.Equal("char", rig.Name);
        Assert.Equal(RigSourceKind.Imported, rig.Source.Kind);
        Assert.Equal("char.gltf", rig.Source.AssetPath);
        Assert.Equal(new[] { "Hips", "Spine", "Head" }, rig.Bones.Select(x => x.Name));
        Assert.True(rig.Bones[0].Parent.IsEmpty);
        Assert.Equal(rig.Bones[0].Id, rig.Bones[1].Parent);
        Assert.Equal(rig.Bones[1].Id, rig.Bones[2].Parent);
        Assert.True(rig.Bones[1].Rest.Rotation.ApproxEquals(Quat.FromAxisAngle(Vec3.UnitX, 0.3), 1e-9));
        Assert.Equal(new Vec3(1, 2, 1), rig.Bones[2].Rest.Scale);
        Assert.Empty(rig.Controls);
        Assert.Empty(rig.IkChains);
        Assert.Equal(new[] { 0, 1, 2 }, rig.Bones.Select(x => res.NodeIndexOf[x.Id]));
    }

    [Fact]
    public void Relative_asset_path_is_used()
    {
        var b = new GltfTestBuilder();
        b.Add("A", -1, Vec3.Zero);
        using var d = new TempDir();
        var res = RigImporter.Import(b.WriteGlb(d.File("x.glb")), "assets/x.glb");
        Assert.Equal("assets/x.glb", res.Rig.Source.AssetPath);
    }

    [Fact]
    public void Intermediate_non_joint_node_between_joints_becomes_a_bone()
    {
        var b = new GltfTestBuilder();
        int a = b.Add("Hips", -1, new Vec3(0, 1, 0));
        int mid = b.Add("Helper", a, new Vec3(0, 0.1, 0), joint: false);
        b.Add("Spine", mid, new Vec3(0, 0.1, 0));
        using var d = new TempDir();
        var res = RigImporter.Import(b.WriteGltf(d.File("i.gltf")));
        Assert.Equal(new[] { "Hips", "Helper", "Spine" }, res.Rig.Bones.Select(x => x.Name));
        Assert.Equal(res.Rig.Bones[1].Id, res.Rig.Bones[2].Parent);
        Assert.Contains(res.Warnings, w => w.Contains("non-joint"));
    }

    [Fact]
    public void Non_joint_ancestors_of_the_root_are_folded_into_its_rest()
    {
        var b = new GltfTestBuilder();
        int scene = b.Add("Scene", -1, new Vec3(10, 0, 0), joint: false, r: Quat.FromAxisAngle(Vec3.UnitY, Math.PI / 2));
        int arm = b.Add("Armature", scene, new Vec3(0, 2, 0), joint: false, s: new Vec3(2, 2, 2));
        int root = b.Add("Hips", arm, new Vec3(0, 1, 0));
        b.Add("Child", root, new Vec3(0, 0, 1));
        using var d = new TempDir();
        var res = RigImporter.Import(b.WriteGltf(d.File("f.gltf")));

        Assert.Equal(2, res.Rig.Bones.Count);
        var rest = res.Rig.Bones[0].Rest;
        // model-space position of the root joint: Scene * Armature * (0,1,0)
        var expected = new Transform(new Vec3(10, 0, 0), Quat.FromAxisAngle(Vec3.UnitY, Math.PI / 2), Vec3.One)
            .TransformPoint(new Transform(new Vec3(0, 2, 0), Quat.Identity, new Vec3(2, 2, 2)).TransformPoint(new Vec3(0, 1, 0)));
        Assert.True(rest.Translation.ApproxEquals(expected, 1e-9));
        Assert.Equal(new Vec3(2, 2, 2), rest.Scale);
        // Child local stays local.
        Assert.Equal(new Vec3(0, 0, 1), res.Rig.Bones[1].Rest.Translation);
    }

    [Fact]
    public void Matrix_based_node_gets_trs_rest()
    {
        var b = new GltfTestBuilder();
        double[] m = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0.5, 1.5, -2, 1 };
        int a = b.Add("Hips", -1, Vec3.Zero, matrix: m);
        b.Add("Child", a, new Vec3(0, 1, 0));
        using var d = new TempDir();
        var res = RigImporter.Import(b.WriteGltf(d.File("m.gltf")));
        Assert.Equal(new Vec3(0.5, 1.5, -2), res.Rig.Bones[0].Rest.Translation);
    }

    [Fact]
    public void Missing_names_get_node_index_names_and_duplicates_are_uniquified()
    {
        var b = new GltfTestBuilder();
        int a = b.Add(null, -1, Vec3.Zero);
        int x = b.Add("Bone", a, new Vec3(0, 1, 0));
        int y = b.Add("Bone", x, new Vec3(0, 1, 0));
        b.Add("Bone", y, new Vec3(0, 1, 0));
        b.Add("Bone_2", a, new Vec3(1, 0, 0)); // collides with a generated name
        using var d = new TempDir();
        var res = RigImporter.Import(b.WriteGltf(d.File("n.gltf")));
        var names = res.Rig.Bones.Select(q => q.Name).ToList();
        Assert.Equal("node_0", names[0]);
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(new[] { "node_0", "Bone", "Bone_2", "Bone_3" }, names.Take(4));
        Assert.Equal("Bone_2_2", names[4]);
        var ids = res.Rig.Bones.Select(q => q.Id).ToList();
        Assert.Equal("node_0", res.OriginalNodeNames[ids[0]]);
        Assert.Equal("Bone", res.OriginalNodeNames[ids[2]]);
        Assert.Equal("Bone", res.OriginalNodeNames[ids[3]]);
        Assert.Equal(2, res.NodeIndexOf[ids[2]]);
    }

    [Fact]
    public void Bones_are_ordered_parent_before_child_even_when_node_indices_are_not()
    {
        // child has a lower node index than its parent
        string json = """
        {"asset":{"version":"2.0"},
         "nodes":[{"name":"Child"},{"name":"Parent","children":[0],"translation":[0,1,0]}],
         "skins":[{"joints":[0,1]}], "scenes":[{"nodes":[1]}], "scene":0}
        """;
        using var d = new TempDir();
        File.WriteAllText(d.File("o.gltf"), json);
        var res = RigImporter.Import(d.File("o.gltf"));
        Assert.Equal(new[] { "Parent", "Child" }, res.Rig.Bones.Select(x => x.Name));
        Assert.Equal(res.Rig.Bones[0].Id, res.Rig.Bones[1].Parent);
    }

    [Fact]
    public void Two_skins_import_the_one_with_most_joints_and_warn()
    {
        var b = new GltfTestBuilder();
        int a = b.Add("A", -1, Vec3.Zero);
        int c = b.Add("B", a, new Vec3(0, 1, 0));
        b.Add("C", c, new Vec3(0, 1, 0));
        b.Joint(0, false); // main skin: B, C
        b.ExtraSkins.Add(new[] { 0, 1, 2 }); // extra skin has 3 joints and wins
        using var d = new TempDir();
        var res = RigImporter.Import(b.WriteGltf(d.File("two.gltf")));
        Assert.Equal(1, res.SkinIndex);
        Assert.Equal(3, res.Rig.Bones.Count);
        Assert.Contains(res.Warnings, w => w.Contains("2 skins") && w.Contains("skin 0"));
    }

    [Fact]
    public void File_without_skin_throws_no_skin()
    {
        var b = new GltfTestBuilder { OmitSkin = true };
        b.Add("A", -1, Vec3.Zero);
        using var d = new TempDir();
        var ex = Assert.Throws<GltfImportException>(() => RigImporter.Import(b.WriteGltf(d.File("n.gltf"))));
        Assert.Equal("no_skin", ex.Code);
    }

    [Fact]
    public void Missing_file_throws_not_found()
    {
        var ex = Assert.Throws<GltfImportException>(() => RigImporter.Import("/no/such/file.glb"));
        Assert.Equal("not_found", ex.Code);
    }

    [Fact]
    public void Import_fills_the_humanoid_map_from_the_mapping_report()
    {
        var b = Mapping.HumanoidTemplate.Build(Mapping.HumanoidTemplate.Unreal());
        using var d = new TempDir();
        var res = RigImporter.Import(b.WriteGlb(d.File("u.glb")));
        Assert.True(res.Mapping.IsUsable);
        Assert.Equal(res.Mapping.Assignments.Count, res.Rig.HumanoidMap.Count);
        Assert.Equal("thigh_l", res.Rig.StandardNameOf(res.Rig.FindBoneByName("thigh_l")!.Id));
    }
}
