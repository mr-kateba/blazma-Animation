using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Blazma.Core.Mathematics;
using Blazma.Rigging.Import;

namespace Blazma.Export;

public sealed record GltfNodeInfo(int Index, string? Name, int? Parent, int[] Children, Transform Local, bool HasMatrix, int? Mesh, int? Skin);

/// <summary>One animation channel with its sampler data decoded to floats.</summary>
public sealed record GltfAnimationChannel(int Node, string Path, string Interpolation, float[] Times, float[] Values, int Components)
{
    /// <summary>Value at <paramref name="t"/> seconds (STEP, LINEAR with slerp for rotation, CUBICSPLINE); held outside the key range.</summary>
    public double[] Sample(double t)
    {
        var n = Times.Length;
        var cubic = Interpolation == "CUBICSPLINE";
        double[] Key(int k, int part) // part: 0 in-tangent, 1 value, 2 out-tangent (cubic layout)
        {
            var baseIndex = cubic ? (k * 3 + part) * Components : k * Components;
            var v = new double[Components];
            for (var c = 0; c < Components; c++) v[c] = Values[baseIndex + c];
            return v;
        }
        if (n == 0) return new double[Components];
        if (t <= Times[0] || n == 1) return Norm(Key(0, 1));
        if (t >= Times[n - 1]) return Norm(Key(n - 1, 1));
        // Segment k0 → k0+1 with Times[k0] <= t (times are float, t is double: compare as doubles).
        var k0 = Array.BinarySearch(Times, (float)t);
        if (k0 < 0) k0 = ~k0 - 1;
        k0 = Math.Clamp(k0, 0, n - 2);
        while (k0 > 0 && Times[k0] > t) k0--;
        while (k0 < n - 2 && Times[k0 + 1] <= t) k0++;
        var t0 = Times[k0];
        var t1 = Times[k0 + 1];
        var td = t1 - t0;
        var s = Math.Clamp((t - t0) / td, 0, 1);
        var a = Key(k0, 1);
        var b = Key(k0 + 1, 1);
        switch (Interpolation)
        {
            case "STEP":
                return Norm(a);
            case "CUBICSPLINE":
            {
                var m0 = Key(k0, 2);
                var m1 = Key(k0 + 1, 0);
                double s2 = s * s, s3 = s2 * s;
                var r = new double[Components];
                for (var c = 0; c < Components; c++)
                    r[c] = (2 * s3 - 3 * s2 + 1) * a[c] + (s3 - 2 * s2 + s) * td * m0[c] + (-2 * s3 + 3 * s2) * b[c] + (s3 - s2) * td * m1[c];
                return Norm(r);
            }
            default:
                if (Path == "rotation")
                {
                    var q = Quat.Slerp(new Quat(a[0], a[1], a[2], a[3]), new Quat(b[0], b[1], b[2], b[3]), s);
                    return new[] { q.X, q.Y, q.Z, q.W };
                }
                var l = new double[Components];
                for (var c = 0; c < Components; c++) l[c] = a[c] + (b[c] - a[c]) * s;
                return l;
        }
    }

    double[] Norm(double[] v)
    {
        if (Path != "rotation") return v;
        var q = new Quat(v[0], v[1], v[2], v[3]).Normalized();
        return new[] { q.X, q.Y, q.Z, q.W };
    }

    public double Duration => Times.Length == 0 ? 0 : Times[^1];
}

public sealed record GltfAnimation(int Index, string? Name, JsonNode? Extras, IReadOnlyList<GltfAnimationChannel> Channels)
{
    public double Duration => Channels.Count == 0 ? 0 : Channels.Max(c => c.Duration);
    public string? BlazmaClipId => (string?)Extras?["blazma"]?["clipId"];
}

/// <summary>Reads nodes, accessors and animations from a GLB document (buffers: the BIN chunk or base64 data URIs).</summary>
public static class GltfAnimationReader
{
    public static IReadOnlyList<GltfNodeInfo> ReadNodes(JsonObject json)
    {
        var list = new List<GltfNodeInfo>();
        var arr = json["nodes"] as JsonArray ?? new JsonArray();
        var parents = new int?[arr.Count];
        for (var i = 0; i < arr.Count; i++)
            foreach (var c in Ints(arr[i]?["children"]))
                if (c >= 0 && c < arr.Count) parents[c] = i;
        for (var i = 0; i < arr.Count; i++)
        {
            var n = arr[i] as JsonObject ?? new JsonObject();
            Transform local;
            var hasMatrix = n["matrix"] is JsonArray;
            if (n["matrix"] is JsonArray m && m.Count == 16)
                local = GltfReader.DecomposeMatrix(m.Select(x => (double)x!).ToArray(), out _);
            else
            {
                var t = Doubles(n["translation"], 3) is { } tv ? new Vec3(tv[0], tv[1], tv[2]) : Vec3.Zero;
                var r = Doubles(n["rotation"], 4) is { } rv ? new Quat(rv[0], rv[1], rv[2], rv[3]).Normalized() : Quat.Identity;
                var s = Doubles(n["scale"], 3) is { } sv ? new Vec3(sv[0], sv[1], sv[2]) : Vec3.One;
                local = new Transform(t, r, s);
            }
            list.Add(new GltfNodeInfo(i, (string?)n["name"], parents[i], Ints(n["children"]), local, hasMatrix, Int(n["mesh"]), Int(n["skin"])));
        }
        return list;
    }

    /// <summary>Scene-space transform of a node (product of local transforms from its scene root).</summary>
    public static Transform WorldOf(IReadOnlyList<GltfNodeInfo> nodes, int index)
    {
        var t = Transform.Identity;
        var chain = new List<int>();
        for (int? p = index; p is int i && chain.Count <= nodes.Count; p = nodes[i].Parent) chain.Add(i);
        for (var i = chain.Count - 1; i >= 0; i--) t = t * nodes[chain[i]].Local;
        return t;
    }

    public static IReadOnlyList<GltfAnimation> ReadAnimations(GlbContainer glb)
    {
        var result = new List<GltfAnimation>();
        var anims = glb.Json["animations"] as JsonArray ?? new JsonArray();
        for (var a = 0; a < anims.Count; a++)
        {
            var anim = anims[a] as JsonObject ?? throw Bad($"animations[{a}] is not an object.");
            var samplers = anim["samplers"] as JsonArray ?? throw Bad($"animations[{a}] has no samplers.");
            var channels = new List<GltfAnimationChannel>();
            foreach (var chNode in anim["channels"] as JsonArray ?? new JsonArray())
            {
                var ch = chNode as JsonObject ?? throw Bad($"animations[{a}] has a channel that is not an object.");
                var si = Int(ch["sampler"]) ?? throw Bad($"animations[{a}] has a channel without sampler.");
                if (si < 0 || si >= samplers.Count) throw Bad($"animations[{a}] channel references missing sampler {si}.");
                var target = ch["target"] as JsonObject ?? throw Bad($"animations[{a}] has a channel without target.");
                var path = (string?)target["path"] ?? throw Bad($"animations[{a}] has a channel without target.path.");
                if (target["node"] is null) continue; // e.g. KHR_animation_pointer targets
                var node = Int(target["node"]) ?? throw Bad($"animations[{a}] has a channel with an invalid node.");
                var sampler = samplers[si] as JsonObject ?? throw Bad($"animations[{a}].samplers[{si}] is not an object.");
                var interp = (string?)sampler["interpolation"] ?? "LINEAR";
                var times = ReadFloats(glb, Int(sampler["input"]) ?? throw Bad("Sampler without input."), out var inComps);
                var values = ReadFloats(glb, Int(sampler["output"]) ?? throw Bad("Sampler without output."), out var outComps);
                if (inComps != 1) throw Bad($"animations[{a}].samplers[{si}] input is not SCALAR.");
                if (path == "weights") outComps = times.Length == 0 ? 1 : values.Length / times.Length / (interp == "CUBICSPLINE" ? 3 : 1);
                channels.Add(new GltfAnimationChannel(node, path, interp, times, values, outComps));
            }
            result.Add(new GltfAnimation(a, (string?)anim["name"], anim["extras"]?.DeepClone(), channels));
        }
        return result;
    }

    /// <summary>Accessor contents as floats (FLOAT, or normalized integer types per the glTF spec).</summary>
    public static float[] ReadFloats(GlbContainer glb, int accessor, out int components)
    {
        var acc = Item(glb.Json, "accessors", accessor);
        if (acc["sparse"] is not null) throw Bad($"Accessor {accessor} is sparse (not supported).");
        components = GltfBuilder.Components((string?)acc["type"] ?? throw Bad($"Accessor {accessor} has no type."));
        var count = Int(acc["count"]) ?? throw Bad($"Accessor {accessor} has no count.");
        var ct = Int(acc["componentType"]) ?? throw Bad($"Accessor {accessor} has no componentType.");
        var normalized = (bool?)acc["normalized"] ?? false;
        var size = ComponentSize(ct);
        var result = new float[count * components];
        if (acc["bufferView"] is null) return result; // all zeros per spec
        var viewIndex = Int(acc["bufferView"]) ?? throw Bad($"Accessor {accessor} has an invalid bufferView.");
        var view = Item(glb.Json, "bufferViews", viewIndex);
        var data = BufferData(glb, Int(view["buffer"]) ?? 0);
        var viewOffset = Long(view["byteOffset"]) ?? 0;
        var viewLength = Long(view["byteLength"]) ?? 0;
        var stride = Int(view["byteStride"]) ?? size * components;
        var accOffset = Long(acc["byteOffset"]) ?? 0;
        for (var i = 0; i < count; i++)
        {
            var p = viewOffset + accOffset + (long)i * stride;
            if (p + size * components > viewOffset + viewLength || p + size * components > data.Length)
                throw Bad($"Accessor {accessor} reads past the end of buffer view {viewIndex}.");
            for (var c = 0; c < components; c++)
            {
                var s = data.AsSpan((int)p + c * size);
                result[i * components + c] = ct switch
                {
                    GltfBuilder.Float => BinaryPrimitives.ReadSingleLittleEndian(s),
                    5120 => normalized ? Math.Max((sbyte)s[0] / 127f, -1f) : (sbyte)s[0],
                    5121 => normalized ? s[0] / 255f : s[0],
                    5122 => normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(s) / 32767f, -1f) : BinaryPrimitives.ReadInt16LittleEndian(s),
                    5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(s) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(s),
                    5125 => BinaryPrimitives.ReadUInt32LittleEndian(s),
                    _ => throw Bad($"Accessor {accessor} has unknown componentType {ct}."),
                };
            }
        }
        return result;
    }

    public static int ComponentSize(int componentType) => componentType switch
    {
        5120 or 5121 => 1,
        5122 or 5123 => 2,
        5125 or 5126 => 4,
        _ => throw Bad($"Unknown componentType {componentType}."),
    };

    /// <summary>Bytes of buffer <paramref name="index"/>: the BIN chunk (buffer 0 without uri) or a base64 data URI.</summary>
    public static byte[] BufferData(GlbContainer glb, int index)
    {
        var buf = Item(glb.Json, "buffers", index);
        if ((string?)buf["uri"] is { } uri)
        {
            var comma = uri.IndexOf(',');
            if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || comma < 0 || !uri[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ExportException("unsupported_source", $"Buffer {index} refers to an external file ('{uri}'); only GLB-embedded data is supported.");
            try { return Convert.FromBase64String(uri[(comma + 1)..]); }
            catch (FormatException) { throw Bad($"Buffer {index} has invalid base64 data."); }
        }
        if (index != 0 || !glb.HasBin) throw Bad($"Buffer {index} has no uri and the GLB has no BIN chunk for it.");
        return glb.Bin;
    }

    public static JsonObject Item(JsonObject json, string array, int index)
    {
        var arr = json[array] as JsonArray ?? throw Bad($"'{array}' is referenced but missing.");
        if (index < 0 || index >= arr.Count) throw Bad($"{array}[{index}] does not exist (count {arr.Count}).");
        return arr[index] as JsonObject ?? throw Bad($"{array}[{index}] is not an object.");
    }

    /// <summary>Integer value of a JSON node whether it was parsed or created in memory (any numeric type); null otherwise.</summary>
    public static long? Long(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < 9e15) return (long)d;
        if (v.TryGetValue<float>(out var f) && f == MathF.Floor(f)) return (long)f;
        return null;
    }

    public static int? Int(JsonNode? n) => Long(n) is long l && l is >= int.MinValue and <= int.MaxValue ? (int)l : null;

    static int[] Ints(JsonNode? n) => n is JsonArray a ? a.Select(x => Int(x) ?? throw Bad("Expected an integer.")).ToArray() : Array.Empty<int>();
    static double[]? Doubles(JsonNode? n, int count) => n is JsonArray a && a.Count == count ? a.Select(x => (double)x!).ToArray() : null;
    static ExportException Bad(string m) => new("invalid_gltf", m);
}
