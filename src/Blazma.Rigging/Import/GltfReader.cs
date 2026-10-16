using System.Buffers.Binary;
using System.Text.Json;
using Blazma.Core.Mathematics;

namespace Blazma.Rigging.Import;

/// <summary>
/// Minimal glTF 2.0 reader (.gltf and .glb) that extracts what skeleton import needs: the node
/// hierarchy with local transforms, skins and their inverse bind matrices, scenes and mesh count.
/// Meshes, materials, animations and textures are not parsed.
/// </summary>
public static class GltfReader
{
    const uint GlbMagic = 0x46546C67; // "glTF"
    const uint ChunkJson = 0x4E4F534A;
    const uint ChunkBin = 0x004E4942;

    static readonly string[] MeshOnlyExtensions =
    {
        "KHR_mesh_quantization", "EXT_meshopt_compression", "KHR_draco_mesh_compression",
    };

    public static GltfDocument Read(string path)
    {
        if (!File.Exists(path))
            throw new GltfImportException("not_found", $"File not found: '{path}'.");

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GltfImportException("not_found", $"Cannot read '{path}': {ex.Message}");
        }

        byte[]? glbBin = null;
        ReadOnlyMemory<byte> json;
        if (string.Equals(Path.GetExtension(path), ".glb", StringComparison.OrdinalIgnoreCase))
            json = ParseGlb(bytes, out glbBin);
        else
            json = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes.AsMemory(3) : bytes; // skip UTF-8 BOM

        JsonDocument jd;
        try
        {
            jd = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new GltfImportException("invalid_format", $"Invalid glTF JSON: {ex.Message}");
        }

        using (jd)
        {
            return Build(jd.RootElement, Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", glbBin);
        }
    }

    // ---- GLB container -------------------------------------------------------------------------

    static ReadOnlyMemory<byte> ParseGlb(byte[] b, out byte[]? bin)
    {
        bin = null;
        if (b.Length < 12) throw new GltfImportException("invalid_format", "GLB file is shorter than its 12-byte header.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(b) != GlbMagic)
            throw new GltfImportException("invalid_format", "Not a GLB file (bad magic, expected 'glTF').");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
        if (version != 2)
            throw new GltfImportException("unsupported_version", $"Unsupported GLB container version {version}; only version 2 is supported.");
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8));
        if (total > b.Length || total < 12)
            throw new GltfImportException("invalid_format", "GLB header length does not match the file size (truncated file?).");

        ReadOnlyMemory<byte>? json = null;
        long off = 12;
        bool first = true;
        while (off + 8 <= total)
        {
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)off));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)off + 4));
            long start = off + 8;
            if (start + len > total) throw new GltfImportException("invalid_format", "GLB chunk extends past the end of the file.");
            if (first && type != ChunkJson) throw new GltfImportException("invalid_format", "The first GLB chunk must be JSON.");
            first = false;
            if (type == ChunkJson && json is null) json = new ReadOnlyMemory<byte>(b, (int)start, (int)len);
            else if (type == ChunkBin && bin is null) bin = b.AsSpan((int)start, (int)len).ToArray();
            off = start + len;
        }
        return json ?? throw new GltfImportException("invalid_format", "GLB has no JSON chunk.");
    }

    // ---- document ------------------------------------------------------------------------------

    static GltfDocument Build(JsonElement root, string baseDir, byte[]? glbBin)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new GltfImportException("invalid_format", "glTF root must be a JSON object.");

        var warnings = new List<string>();

        if (!root.TryGetProperty("asset", out var asset) || asset.ValueKind != JsonValueKind.Object ||
            !asset.TryGetProperty("version", out var ver) || ver.ValueKind != JsonValueKind.String)
            throw new GltfImportException("invalid_format", "Missing required 'asset.version'.");
        string version = ver.GetString()!;
        if (!version.StartsWith("2.", StringComparison.Ordinal))
            throw new GltfImportException("unsupported_version", $"Unsupported glTF version '{version}'; only 2.x is supported.");
        string generator = asset.TryGetProperty("generator", out var gen) && gen.ValueKind == JsonValueKind.String ? gen.GetString()! : "";

        CheckExtensions(root, warnings);

        int meshCount = ArrayLength(root, "meshes");

        // nodes
        var nodes = new List<GltfNode>();
        if (root.TryGetProperty("nodes", out var nodesEl))
        {
            if (nodesEl.ValueKind != JsonValueKind.Array) throw Bad("'nodes' must be an array.");
            int i = 0;
            foreach (var n in nodesEl.EnumerateArray())
                nodes.Add(ReadNode(n, i++, warnings));
        }
        int nodeCount = nodes.Count;
        int skinCount = ArrayLength(root, "skins");

        foreach (var n in nodes)
        {
            foreach (int c in n.Children)
            {
                if (c < 0 || c >= nodeCount)
                    throw new GltfImportException("invalid_reference", $"Node {n.Index} references child node {c}, but the file has {nodeCount} nodes.");
                if (nodes[c].Parent is not null)
                    throw new GltfImportException("invalid_format", $"Node {c} has more than one parent (nodes {nodes[c].Parent} and {n.Index}).");
                nodes[c].Parent = n.Index;
            }
            if (n.Mesh is int m && (m < 0 || m >= meshCount))
                throw new GltfImportException("invalid_reference", $"Node {n.Index} references mesh {m}, but the file has {meshCount} meshes.");
            if (n.Skin is int s && (s < 0 || s >= skinCount))
                throw new GltfImportException("invalid_reference", $"Node {n.Index} references skin {s}, but the file has {skinCount} skins.");
        }
        foreach (var n in nodes)
        {
            int steps = 0;
            for (int? p = n.Parent; p is not null; p = nodes[p.Value].Parent)
                if (++steps > nodeCount) throw Bad($"Node hierarchy contains a cycle through node {n.Index}.");
        }

        // scenes
        var sceneRoots = new List<int[]>();
        if (root.TryGetProperty("scenes", out var scenesEl))
        {
            if (scenesEl.ValueKind != JsonValueKind.Array) throw Bad("'scenes' must be an array.");
            int si = 0;
            foreach (var sc in scenesEl.EnumerateArray())
            {
                int[] roots = sc.ValueKind == JsonValueKind.Object && sc.TryGetProperty("nodes", out var sn)
                    ? IntArray(sn, $"scenes[{si}].nodes") : Array.Empty<int>();
                foreach (int r in roots)
                    if (r < 0 || r >= nodeCount)
                        throw new GltfImportException("invalid_reference", $"Scene {si} references node {r}, but the file has {nodeCount} nodes.");
                sceneRoots.Add(roots);
                si++;
            }
        }
        int? defaultScene = null;
        if (root.TryGetProperty("scene", out var sceneEl))
        {
            if (sceneEl.ValueKind != JsonValueKind.Number || !sceneEl.TryGetInt32(out int sv)) throw Bad("'scene' must be an integer.");
            if (sv < 0 || sv >= sceneRoots.Count)
                throw new GltfImportException("invalid_reference", $"Default scene {sv} does not exist (file has {sceneRoots.Count} scenes).");
            defaultScene = sv;
        }

        // skins
        var skins = new List<GltfSkin>();
        if (root.TryGetProperty("skins", out var skinsEl))
        {
            if (skinsEl.ValueKind != JsonValueKind.Array) throw Bad("'skins' must be an array.");
            var buffers = new BufferCache(root, baseDir, glbBin);
            int k = 0;
            foreach (var sk in skinsEl.EnumerateArray())
                skins.Add(ReadSkin(sk, k++, root, nodeCount, buffers, warnings));
        }

        return new GltfDocument
        {
            Generator = generator,
            Nodes = nodes,
            Skins = skins,
            MeshCount = meshCount,
            DefaultScene = defaultScene,
            SceneRoots = sceneRoots,
            Warnings = warnings,
        };
    }

    static void CheckExtensions(JsonElement root, List<string> warnings)
    {
        if (!root.TryGetProperty("extensionsRequired", out var req)) return;
        if (req.ValueKind != JsonValueKind.Array) throw Bad("'extensionsRequired' must be an array.");
        var unsupported = new List<string>();
        foreach (var e in req.EnumerateArray())
        {
            string? name = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (name is null) throw Bad("'extensionsRequired' entries must be strings.");
            if (name.StartsWith("KHR_materials_", StringComparison.Ordinal) || name.StartsWith("KHR_texture_", StringComparison.Ordinal))
                continue; // appearance only
            if (Array.IndexOf(MeshOnlyExtensions, name) >= 0)
            {
                warnings.Add($"Required extension {name} only affects mesh data; it is ignored for skeleton import.");
                continue;
            }
            unsupported.Add(name);
        }
        if (unsupported.Count > 0)
            throw new GltfImportException("unsupported_extension", "Required glTF extensions are not supported: " + string.Join(", ", unsupported) + ".");
    }

    // ---- nodes ---------------------------------------------------------------------------------

    static GltfNode ReadNode(JsonElement n, int index, List<string> warnings)
    {
        if (n.ValueKind != JsonValueKind.Object) throw Bad($"nodes[{index}] must be an object.");
        string? name = n.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null;
        int[] children = n.TryGetProperty("children", out var ch) ? IntArray(ch, $"nodes[{index}].children") : Array.Empty<int>();

        Transform local;
        if (n.TryGetProperty("matrix", out var mx))
        {
            double[] m = DoubleArray(mx, 16, $"nodes[{index}].matrix");
            local = DecomposeMatrix(m, out bool sheared);
            if (sheared) warnings.Add($"Node {index} ('{name ?? "unnamed"}') has a matrix with shear; the shear was dropped.");
        }
        else
        {
            Vec3 t = n.TryGetProperty("translation", out var te) ? ToVec(DoubleArray(te, 3, $"nodes[{index}].translation")) : Vec3.Zero;
            Quat r = Quat.Identity;
            if (n.TryGetProperty("rotation", out var re))
            {
                double[] q = DoubleArray(re, 4, $"nodes[{index}].rotation");
                r = new Quat(q[0], q[1], q[2], q[3]).Normalized();
            }
            Vec3 s = n.TryGetProperty("scale", out var se) ? ToVec(DoubleArray(se, 3, $"nodes[{index}].scale")) : Vec3.One;
            local = new Transform(t, r, s);
        }

        return new GltfNode
        {
            Index = index,
            Name = name,
            Children = children,
            Local = local,
            Mesh = OptInt(n, "mesh"),
            Skin = OptInt(n, "skin"),
        };
    }

    static Vec3 ToVec(double[] a) => new(a[0], a[1], a[2]);

    /// <summary>
    /// Decomposes a column-major 4x4 matrix into translation, rotation and scale. Scale is the length of
    /// each basis column (negated on X when the determinant is negative); shear is dropped.
    /// </summary>
    public static Transform DecomposeMatrix(double[] m, out bool sheared)
    {
        sheared = false;
        var t = new Vec3(m[12], m[13], m[14]);
        double sx = Math.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2]);
        double sy = Math.Sqrt(m[4] * m[4] + m[5] * m[5] + m[6] * m[6]);
        double sz = Math.Sqrt(m[8] * m[8] + m[9] * m[9] + m[10] * m[10]);
        double det = m[0] * (m[5] * m[10] - m[6] * m[9]) + m[1] * (m[6] * m[8] - m[4] * m[10]) + m[2] * (m[4] * m[9] - m[5] * m[8]);
        if (det < 0) sx = -sx;
        if (Math.Abs(sx) < 1e-12 || sy < 1e-12 || sz < 1e-12)
            return new Transform(t, Quat.Identity, new Vec3(sx, sy, sz));

        double r00 = m[0] / sx, r10 = m[1] / sx, r20 = m[2] / sx;
        double r01 = m[4] / sy, r11 = m[5] / sy, r21 = m[6] / sy;
        double r02 = m[8] / sz, r12 = m[9] / sz, r22 = m[10] / sz;

        double d01 = r00 * r01 + r10 * r11 + r20 * r21;
        double d02 = r00 * r02 + r10 * r12 + r20 * r22;
        double d12 = r01 * r02 + r11 * r12 + r21 * r22;
        sheared = Math.Abs(d01) > 1e-4 || Math.Abs(d02) > 1e-4 || Math.Abs(d12) > 1e-4;

        double w, x, y, z;
        double tr = r00 + r11 + r22;
        if (tr > 0)
        {
            double s = Math.Sqrt(tr + 1.0) * 2;
            w = s / 4; x = (r21 - r12) / s; y = (r02 - r20) / s; z = (r10 - r01) / s;
        }
        else if (r00 > r11 && r00 > r22)
        {
            double s = Math.Sqrt(1.0 + r00 - r11 - r22) * 2;
            w = (r21 - r12) / s; x = s / 4; y = (r01 + r10) / s; z = (r02 + r20) / s;
        }
        else if (r11 > r22)
        {
            double s = Math.Sqrt(1.0 + r11 - r00 - r22) * 2;
            w = (r02 - r20) / s; x = (r01 + r10) / s; y = s / 4; z = (r12 + r21) / s;
        }
        else
        {
            double s = Math.Sqrt(1.0 + r22 - r00 - r11) * 2;
            w = (r10 - r01) / s; x = (r02 + r20) / s; y = (r12 + r21) / s; z = s / 4;
        }
        return new Transform(t, new Quat(x, y, z, w).Normalized(), new Vec3(sx, sy, sz));
    }

    // ---- skins & accessors ---------------------------------------------------------------------

    static GltfSkin ReadSkin(JsonElement sk, int index, JsonElement root, int nodeCount, BufferCache buffers, List<string> warnings)
    {
        if (sk.ValueKind != JsonValueKind.Object) throw Bad($"skins[{index}] must be an object.");
        string? name = sk.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() : null;
        if (!sk.TryGetProperty("joints", out var jEl)) throw Bad($"skins[{index}] has no 'joints'.");
        int[] joints = IntArray(jEl, $"skins[{index}].joints");
        foreach (int j in joints)
            if (j < 0 || j >= nodeCount)
                throw new GltfImportException("invalid_reference", $"Skin {index} references joint node {j}, but the file has {nodeCount} nodes.");
        int? skeleton = OptInt(sk, "skeleton");
        if (skeleton is int sn && (sn < 0 || sn >= nodeCount))
            throw new GltfImportException("invalid_reference", $"Skin {index} references skeleton node {sn}, but the file has {nodeCount} nodes.");

        double[][]? ibm = null;
        if (OptInt(sk, "inverseBindMatrices") is int acc)
        {
            try
            {
                ibm = ReadMat4Accessor(root, acc, buffers);
                if (ibm.Length != joints.Length)
                    warnings.Add($"Skin {index}: inverseBindMatrices has {ibm.Length} entries but the skin has {joints.Length} joints.");
            }
            catch (GltfImportException ex)
            {
                warnings.Add($"Skin {index}: inverse bind matrices could not be read ({ex.Code}: {ex.Message}).");
            }
        }
        return new GltfSkin { Name = name, Joints = joints, Skeleton = skeleton, InverseBindMatrices = ibm };
    }

    static double[][] ReadMat4Accessor(JsonElement root, int accIndex, BufferCache buffers)
    {
        var acc = ArrayItem(root, "accessors", accIndex);
        if (acc.TryGetProperty("sparse", out _)) throw Bad("Sparse accessors are not supported.");
        if (OptInt(acc, "componentType") != 5126 || !(acc.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String && ty.GetString() == "MAT4"))
            throw Bad($"Accessor {accIndex} must be FLOAT MAT4.");
        int count = OptInt(acc, "count") ?? throw Bad($"Accessor {accIndex} has no count.");
        if (count < 0) throw Bad($"Accessor {accIndex} has a negative count.");
        int bvIndex = OptInt(acc, "bufferView") ?? throw Bad($"Accessor {accIndex} has no bufferView.");
        int accOffset = OptInt(acc, "byteOffset") ?? 0;

        var bv = ArrayItem(root, "bufferViews", bvIndex);
        int bufIndex = OptInt(bv, "buffer") ?? throw Bad($"bufferView {bvIndex} has no buffer.");
        int bvOffset = OptInt(bv, "byteOffset") ?? 0;
        int bvLength = OptInt(bv, "byteLength") ?? throw Bad($"bufferView {bvIndex} has no byteLength.");
        int stride = OptInt(bv, "byteStride") ?? 64;
        if (stride < 64) throw Bad($"bufferView {bvIndex} has byteStride {stride} < 64 for MAT4.");

        byte[] data = buffers.Get(bufIndex);
        long start = (long)bvOffset + accOffset;
        long end = (long)bvOffset + bvLength;
        var result = new double[count][];
        for (int i = 0; i < count; i++)
        {
            long p = start + (long)i * stride;
            if (p < 0 || p + 64 > end || p + 64 > data.Length)
                throw Bad($"Accessor {accIndex} reads past the end of its buffer view.");
            var m = new double[16];
            for (int c = 0; c < 16; c++)
                m[c] = BitConverter.ToSingle(data, (int)p + c * 4);
            result[i] = m;
        }
        return result;
    }

    sealed class BufferCache
    {
        readonly JsonElement _root;
        readonly string _dir;
        readonly byte[]? _glbBin;
        readonly Dictionary<int, byte[]> _cache = new();

        public BufferCache(JsonElement root, string dir, byte[]? glbBin)
        {
            _root = root; _dir = dir; _glbBin = glbBin;
        }

        public byte[] Get(int index)
        {
            if (_cache.TryGetValue(index, out var cached)) return cached;
            var buf = ArrayItem(_root, "buffers", index);
            int declared = OptInt(buf, "byteLength") ?? throw Bad($"buffer {index} has no byteLength.");
            byte[] data;
            if (buf.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String)
                data = LoadUri(u.GetString()!);
            else if (index == 0 && _glbBin is not null)
                data = _glbBin;
            else
                throw new GltfImportException("invalid_reference", $"buffer {index} has no uri and no GLB binary chunk is available.");
            if (data.Length < declared) throw Bad($"buffer {index} is {data.Length} bytes but declares {declared}.");
            _cache[index] = data;
            return data;
        }

        byte[] LoadUri(string uri)
        {
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = uri.IndexOf(',');
                if (comma < 0 || !uri.AsSpan(0, comma).Contains(";base64", StringComparison.OrdinalIgnoreCase))
                    throw Bad("Only base64 data: URIs are supported.");
                try { return Convert.FromBase64String(uri[(comma + 1)..]); }
                catch (FormatException) { throw Bad("Invalid base64 in data: URI."); }
            }
            if (uri.Contains("://", StringComparison.Ordinal))
                throw Bad($"Remote buffer URIs are not supported: '{uri}'.");
            string file = Path.GetFullPath(Path.Combine(_dir, Uri.UnescapeDataString(uri)));
            if (!File.Exists(file)) throw new GltfImportException("not_found", $"External buffer file not found: '{uri}'.");
            try { return File.ReadAllBytes(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new GltfImportException("not_found", $"Cannot read buffer '{uri}': {ex.Message}");
            }
        }
    }

    // ---- JSON helpers --------------------------------------------------------------------------

    static GltfImportException Bad(string msg) => new("invalid_format", msg);

    static int ArrayLength(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 0;

    static JsonElement ArrayItem(JsonElement root, string prop, int index)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new GltfImportException("invalid_reference", $"'{prop}' is referenced but missing.");
        if (index < 0 || index >= arr.GetArrayLength())
            throw new GltfImportException("invalid_reference", $"{prop}[{index}] does not exist (count {arr.GetArrayLength()}).");
        var item = arr[index];
        if (item.ValueKind != JsonValueKind.Object) throw Bad($"{prop}[{index}] must be an object.");
        return item;
    }

    static int? OptInt(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var e)) return null;
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out int v)) throw Bad($"'{prop}' must be an integer.");
        return v;
    }

    static int[] IntArray(JsonElement e, string what)
    {
        if (e.ValueKind != JsonValueKind.Array) throw Bad($"{what} must be an array.");
        var r = new int[e.GetArrayLength()];
        int i = 0;
        foreach (var x in e.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out r[i])) throw Bad($"{what} must contain integers.");
            i++;
        }
        return r;
    }

    static double[] DoubleArray(JsonElement e, int length, string what)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != length) throw Bad($"{what} must be an array of {length} numbers.");
        var r = new double[length];
        int i = 0;
        foreach (var x in e.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Number) throw Bad($"{what} must contain numbers.");
            r[i++] = x.GetDouble();
        }
        return r;
    }
}
