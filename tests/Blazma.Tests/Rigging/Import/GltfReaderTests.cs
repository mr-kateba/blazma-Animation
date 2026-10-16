using System.Text;
using Blazma.Core.Mathematics;
using Blazma.Rigging.Import;

namespace Blazma.Tests.Rigging.Import;

public class GltfReaderTests
{
    static GltfTestBuilder Simple()
    {
        var b = new GltfTestBuilder();
        int a = b.Add("A", -1, new Vec3(0, 1, 0));
        b.Add("B", a, new Vec3(0, 0.5, 0));
        return b;
    }

    static void AssertCode(string code, Action act) =>
        Assert.Equal(code, Assert.Throws<GltfImportException>(act).Code);

    [Fact]
    public void Gltf_with_data_uri_buffer_is_read()
    {
        using var d = new TempDir();
        var doc = GltfReader.Read(Simple().WriteGltf(d.File("a.gltf")));
        Assert.Equal("Blazma test builder", doc.Generator);
        Assert.Equal(3, doc.Nodes.Count); // A, B, Body
        Assert.Equal(1, doc.MeshCount);
        Assert.Equal(0, doc.DefaultScene);
        Assert.Equal(new[] { 0, 2 }, doc.SceneRoots[0]);
        Assert.Equal(0, doc.Nodes[1].Parent);
        Assert.Null(doc.Nodes[0].Parent);
        Assert.Equal(0, doc.Nodes[2].Skin);
        var skin = Assert.Single(doc.Skins);
        Assert.Equal(new[] { 0, 1 }, skin.Joints);
        Assert.NotNull(skin.InverseBindMatrices);
        Assert.Equal(2, skin.InverseBindMatrices!.Length);
        // IBM of B = translate(-(0,1.5,0))
        Assert.Equal(-1.5, skin.InverseBindMatrices[1][13], 5);
        Assert.Empty(doc.Warnings);
    }

    [Fact]
    public void Gltf_with_external_bin_is_read()
    {
        using var d = new TempDir();
        var doc = GltfReader.Read(Simple().WriteGltf(d.File("ext.gltf"), externalBin: true));
        Assert.NotNull(doc.Skins[0].InverseBindMatrices);
        Assert.Empty(doc.Warnings);
    }

    [Fact]
    public void Missing_external_bin_only_warns_about_inverse_bind_matrices()
    {
        using var d = new TempDir();
        string p = Simple().WriteGltf(d.File("ext.gltf"), externalBin: true);
        File.Delete(d.File("ext.bin"));
        var doc = GltfReader.Read(p);
        Assert.Null(doc.Skins[0].InverseBindMatrices);
        Assert.Contains(doc.Warnings, w => w.Contains("inverse bind matrices"));
    }

    [Fact]
    public void Glb_is_read()
    {
        using var d = new TempDir();
        var doc = GltfReader.Read(Simple().WriteGlb(d.File("a.glb")));
        Assert.Equal(3, doc.Nodes.Count);
        Assert.Equal(-1.5, doc.Skins[0].InverseBindMatrices![1][13], 5);
    }

    [Fact]
    public void Matrix_node_is_decomposed_into_trs()
    {
        // 90 degrees about Z, scale (2,3,4), translation (1,2,3). Column-major.
        double c = 0, s = 1;
        double[] m =
        {
            2 * c, 2 * s, 0, 0,
            -3 * s, 3 * c, 0, 0,
            0, 0, 4, 0,
            1, 2, 3, 1,
        };
        var b = new GltfTestBuilder();
        b.Add("M", -1, Vec3.Zero, matrix: m);
        using var d = new TempDir();
        var local = GltfReader.Read(b.WriteGltf(d.File("m.gltf"))).Nodes[0].Local;
        Assert.True(local.Translation.ApproxEquals(new Vec3(1, 2, 3), 1e-9));
        Assert.True(local.Scale.ApproxEquals(new Vec3(2, 3, 4), 1e-9));
        Assert.True(local.Rotation.ApproxEquals(Quat.FromAxisAngle(Vec3.UnitZ, Math.PI / 2), 1e-9));
    }

    [Fact]
    public void Matrix_with_shear_warns()
    {
        double[] m = { 1, 0, 0, 0, 1, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        var b = new GltfTestBuilder();
        b.Add("Sheared", -1, Vec3.Zero, matrix: m);
        using var d = new TempDir();
        var doc = GltfReader.Read(b.WriteGltf(d.File("m.gltf")));
        Assert.Contains(doc.Warnings, w => w.Contains("shear"));
    }

    [Fact]
    public void Mirror_matrix_decomposes_with_negative_x_scale_and_reproduces_points()
    {
        double[] m = { -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 5, 0, 0, 1 };
        var t = GltfReader.DecomposeMatrix(m, out bool shear);
        Assert.False(shear);
        Assert.True(t.TransformPoint(new Vec3(1, 2, 3)).ApproxEquals(new Vec3(4, 2, 3), 1e-9));
    }

    [Fact]
    public void Missing_file_is_not_found() =>
        AssertCode("not_found", () => GltfReader.Read("/definitely/not/here.glb"));

    [Fact]
    public void Glb_with_wrong_magic_is_invalid_format()
    {
        using var d = new TempDir();
        string p = Simple().WriteGlb(d.File("a.glb"));
        var bytes = File.ReadAllBytes(p);
        bytes[0] = (byte)'X';
        File.WriteAllBytes(p, bytes);
        AssertCode("invalid_format", () => GltfReader.Read(p));
    }

    [Fact]
    public void Glb_container_version_1_is_unsupported()
    {
        using var d = new TempDir();
        string p = Simple().WriteGlb(d.File("a.glb"));
        var bytes = File.ReadAllBytes(p);
        bytes[4] = 1;
        File.WriteAllBytes(p, bytes);
        AssertCode("unsupported_version", () => GltfReader.Read(p));
    }

    [Fact]
    public void Truncated_glb_is_invalid_format()
    {
        using var d = new TempDir();
        string p = Simple().WriteGlb(d.File("a.glb"));
        var bytes = File.ReadAllBytes(p);
        File.WriteAllBytes(p, bytes[..(bytes.Length / 2)]);
        AssertCode("invalid_format", () => GltfReader.Read(p));
    }

    [Fact]
    public void Gltf_asset_version_1_is_unsupported()
    {
        using var d = new TempDir();
        var b = Simple();
        b.AssetVersion = "1.0";
        AssertCode("unsupported_version", () => GltfReader.Read(b.WriteGltf(d.File("v1.gltf"))));
    }

    [Fact]
    public void Garbage_json_is_invalid_format()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("x.gltf"), "{ not json");
        AssertCode("invalid_format", () => GltfReader.Read(d.File("x.gltf")));
    }

    [Fact]
    public void Missing_asset_is_invalid_format()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("x.gltf"), "{\"nodes\":[]}");
        AssertCode("invalid_format", () => GltfReader.Read(d.File("x.gltf")));
    }

    [Fact]
    public void Out_of_range_child_is_invalid_reference()
    {
        using var d = new TempDir();
        var b = Simple();
        b.OutOfRangeChild = 99;
        AssertCode("invalid_reference", () => GltfReader.Read(b.WriteGltf(d.File("x.gltf"))));
    }

    [Fact]
    public void Out_of_range_joint_is_invalid_reference()
    {
        using var d = new TempDir();
        var b = Simple();
        b.ExtraSkins.Add(new[] { 0, 42 });
        AssertCode("invalid_reference", () => GltfReader.Read(b.WriteGltf(d.File("x.gltf"))));
    }

    [Fact]
    public void Node_cycle_is_invalid_format()
    {
        using var d = new TempDir();
        string json = "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"children\":[1]},{\"children\":[0]}]}";
        File.WriteAllText(d.File("c.gltf"), json);
        AssertCode("invalid_format", () => GltfReader.Read(d.File("c.gltf")));
    }

    [Fact]
    public void Node_with_two_parents_is_invalid_format()
    {
        using var d = new TempDir();
        string json = "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"children\":[2]},{\"children\":[2]},{}]}";
        File.WriteAllText(d.File("c.gltf"), json);
        AssertCode("invalid_format", () => GltfReader.Read(d.File("c.gltf")));
    }

    [Fact]
    public void Unsupported_required_extension_is_listed()
    {
        using var d = new TempDir();
        var b = Simple();
        b.ExtensionsRequired = new[] { "KHR_materials_unlit", "KHR_lights_punctual", "VENDOR_weird" };
        var ex = Assert.Throws<GltfImportException>(() => GltfReader.Read(b.WriteGltf(d.File("x.gltf"))));
        Assert.Equal("unsupported_extension", ex.Code);
        Assert.Contains("KHR_lights_punctual", ex.Message);
        Assert.Contains("VENDOR_weird", ex.Message);
        Assert.DoesNotContain("KHR_materials_unlit", ex.Message);
    }

    [Fact]
    public void Mesh_only_and_appearance_extensions_are_ignored_with_a_warning()
    {
        using var d = new TempDir();
        var b = Simple();
        b.ExtensionsRequired = new[] { "KHR_mesh_quantization", "KHR_texture_transform", "KHR_materials_emissive_strength" };
        var doc = GltfReader.Read(b.WriteGltf(d.File("x.gltf")));
        var w = Assert.Single(doc.Warnings);
        Assert.Contains("KHR_mesh_quantization", w);
    }

    [Fact]
    public void Gltf_with_utf8_bom_is_read()
    {
        using var d = new TempDir();
        string p = Simple().WriteGltf(d.File("bom.gltf"));
        File.WriteAllBytes(p, Encoding.UTF8.GetPreamble().Concat(File.ReadAllBytes(p)).ToArray());
        Assert.Equal(3, GltfReader.Read(p).Nodes.Count);
    }
}
