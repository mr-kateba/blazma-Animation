using System.Text;
using System.Text.Json;
using Blazma.Core.Mathematics;

namespace Blazma.Tests.Rigging.Import;

/// <summary>Writes small glTF/GLB files with a skin whose inverseBindMatrices live in a real buffer.</summary>
public sealed class GltfTestBuilder
{
    public sealed class N
    {
        public string? Name;
        public int Parent = -1;
        public Vec3 T = Vec3.Zero;
        public Quat R = Quat.Identity;
        public Vec3 S = Vec3.One;
        public double[]? Matrix;
        public bool Joint = true;
    }

    public List<N> Nodes { get; } = new();

    /// <summary>Extra skins (joint node lists) besides the main one built from <see cref="N.Joint"/> nodes.</summary>
    public List<int[]> ExtraSkins { get; } = new();

    public string AssetVersion = "2.0";
    public string[]? ExtensionsRequired;
    public bool AddSkinnedMeshNode = true;
    public bool OmitSkin;
    public int? OutOfRangeChild;

    public int Add(string? name, int parent, Vec3 t, bool joint = true, Quat? r = null, Vec3? s = null, double[]? matrix = null)
    {
        Nodes.Add(new N { Name = name, Parent = parent, T = t, R = r ?? Quat.Identity, S = s ?? Vec3.One, Joint = joint, Matrix = matrix });
        return Nodes.Count - 1;
    }

    public void Joint(int node, bool isJoint) => Nodes[node].Joint = isJoint;

    Vec3 WorldPos(int i)
    {
        var p = Vec3.Zero;
        for (int k = i; k >= 0 && k < Nodes.Count; k = Nodes[k].Parent) p += Nodes[k].T;
        return p;
    }

    byte[] BuildBinary(List<int[]> skins, out List<int> offsets)
    {
        var ms = new MemoryStream();
        offsets = new List<int>();
        foreach (var joints in skins)
        {
            offsets.Add((int)ms.Length);
            foreach (int j in joints)
            {
                var w = WorldPos(j);
                float[] m = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, (float)-w.X, (float)-w.Y, (float)-w.Z, 1 };
                foreach (float f in m) ms.Write(BitConverter.GetBytes(f));
            }
        }
        return ms.ToArray();
    }

    string BuildJson(byte[] bin, List<int> offsets, List<int[]> skins, string? bufferUri)
    {
        var children = new Dictionary<int, List<int>>();
        for (int i = 0; i < Nodes.Count; i++)
            if (Nodes[i].Parent >= 0)
            {
                if (!children.TryGetValue(Nodes[i].Parent, out var l)) children[Nodes[i].Parent] = l = new();
                l.Add(i);
            }

        var nodes = new List<Dictionary<string, object>>();
        for (int i = 0; i < Nodes.Count; i++)
        {
            var n = Nodes[i];
            var d = new Dictionary<string, object>();
            if (n.Name is not null) d["name"] = n.Name;
            if (children.TryGetValue(i, out var ch))
            {
                var list = ch.ToList();
                if (OutOfRangeChild is int oor && i == 0) list.Add(oor);
                d["children"] = list;
            }
            else if (OutOfRangeChild is int oor2 && i == 0) d["children"] = new[] { oor2 };
            if (n.Matrix is not null) d["matrix"] = n.Matrix;
            else
            {
                if (n.T != Vec3.Zero) d["translation"] = new[] { n.T.X, n.T.Y, n.T.Z };
                if (n.R != Quat.Identity) d["rotation"] = new[] { n.R.X, n.R.Y, n.R.Z, n.R.W };
                if (n.S != Vec3.One) d["scale"] = new[] { n.S.X, n.S.Y, n.S.Z };
            }
            nodes.Add(d);
        }
        var sceneRoots = Enumerable.Range(0, Nodes.Count).Where(i => Nodes[i].Parent < 0).ToList();
        if (AddSkinnedMeshNode && !OmitSkin)
        {
            nodes.Add(new Dictionary<string, object> { ["name"] = "Body", ["mesh"] = 0, ["skin"] = 0 });
            sceneRoots.Add(nodes.Count - 1);
        }

        var root = new Dictionary<string, object>
        {
            ["asset"] = new Dictionary<string, object> { ["version"] = AssetVersion, ["generator"] = "Blazma test builder" },
            ["scene"] = 0,
            ["scenes"] = new[] { new Dictionary<string, object> { ["nodes"] = sceneRoots } },
            ["nodes"] = nodes,
        };
        if (ExtensionsRequired is not null) root["extensionsRequired"] = ExtensionsRequired;
        if (!OmitSkin)
        {
            root["meshes"] = new[] { new Dictionary<string, object> { ["primitives"] = Array.Empty<object>() } };
            var skinList = new List<Dictionary<string, object>>();
            var views = new List<Dictionary<string, object>>();
            var accs = new List<Dictionary<string, object>>();
            for (int s = 0; s < skins.Count; s++)
            {
                views.Add(new() { ["buffer"] = 0, ["byteOffset"] = offsets[s], ["byteLength"] = skins[s].Length * 64 });
                accs.Add(new() { ["bufferView"] = s, ["componentType"] = 5126, ["count"] = skins[s].Length, ["type"] = "MAT4" });
                skinList.Add(new() { ["name"] = s == 0 ? "MainSkin" : $"Extra{s}", ["joints"] = skins[s], ["inverseBindMatrices"] = s });
            }
            var buffer = new Dictionary<string, object> { ["byteLength"] = bin.Length };
            if (bufferUri is not null) buffer["uri"] = bufferUri;
            root["buffers"] = new[] { buffer };
            root["bufferViews"] = views;
            root["accessors"] = accs;
            root["skins"] = skinList;
        }
        return JsonSerializer.Serialize(root);
    }

    List<int[]> AllSkins()
    {
        var skins = new List<int[]>();
        if (OmitSkin) return skins;
        skins.Add(Enumerable.Range(0, Nodes.Count).Where(i => Nodes[i].Joint).ToArray());
        skins.AddRange(ExtraSkins);
        return skins;
    }

    public string WriteGltf(string path, bool externalBin = false)
    {
        var skins = AllSkins();
        var bin = BuildBinary(skins, out var offsets);
        string? uri = null;
        if (skins.Count > 0)
        {
            if (externalBin)
            {
                string binName = Path.GetFileNameWithoutExtension(path) + ".bin";
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, binName), bin);
                uri = binName;
            }
            else uri = "data:application/octet-stream;base64," + Convert.ToBase64String(bin);
        }
        File.WriteAllText(path, BuildJson(bin, offsets, skins, uri));
        return path;
    }

    public string WriteGlb(string path)
    {
        var skins = AllSkins();
        var bin = BuildBinary(skins, out var offsets);
        byte[] json = Encoding.UTF8.GetBytes(BuildJson(bin, offsets, skins, null));
        json = Pad(json, 0x20);
        bin = Pad(bin, 0);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        uint total = (uint)(12 + 8 + json.Length + (skins.Count > 0 ? 8 + bin.Length : 0));
        w.Write(0x46546C67u); w.Write(2u); w.Write(total);
        w.Write((uint)json.Length); w.Write(0x4E4F534Au); w.Write(json);
        if (skins.Count > 0) { w.Write((uint)bin.Length); w.Write(0x004E4942u); w.Write(bin); }
        return path;
    }

    static byte[] Pad(byte[] data, byte fill)
    {
        int padded = (data.Length + 3) & ~3;
        if (padded == data.Length) return data;
        var r = new byte[padded];
        Array.Fill(r, fill);
        data.CopyTo(r, 0);
        return r;
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "blazma-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

public static class RepoPaths
{
    public static string Sample(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string candidate = System.IO.Path.Combine(d.FullName, "samples", relative);
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Sample asset not found: " + relative);
    }
}
