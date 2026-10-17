using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blazma.Export;

/// <summary>A binary glTF (GLB v2) split into its JSON document and BIN chunk.</summary>
public sealed class GlbContainer
{
    public const uint Magic = 0x46546C67; // "glTF"
    public const uint ChunkJson = 0x4E4F534A;
    public const uint ChunkBin = 0x004E4942;

    public required JsonObject Json { get; init; }

    /// <summary>BIN chunk contents (including trailing padding); empty when the file has none.</summary>
    public byte[] Bin { get; init; } = Array.Empty<byte>();
    public bool HasBin { get; init; }

    /// <summary>Container-level problems that did not stop parsing (alignment, length, chunk order).</summary>
    public List<string> Issues { get; } = new();

    /// <summary>Parses a GLB. Throws <see cref="ExportException"/> ("invalid_glb") when it cannot be read at all.</summary>
    public static GlbContainer Parse(byte[] b)
    {
        static ExportException Bad(string m) => new("invalid_glb", m);
        if (b.Length < 20) throw Bad("File is too short to be a GLB.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(b) != Magic) throw Bad("Bad magic: not a GLB file.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
        if (version != 2) throw Bad($"GLB container version {version}; expected 2.");
        var total = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8));
        var issues = new List<string>();
        if (total != b.Length) issues.Add($"Header length {total} differs from the file size {b.Length}.");
        if (total > b.Length) throw Bad("GLB is truncated.");

        JsonObject? json = null;
        byte[]? bin = null;
        var off = 12L;
        var index = 0;
        while (off + 8 <= total)
        {
            var len = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)off));
            var type = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)off + 4));
            var start = off + 8;
            if (start + len > total) throw Bad($"Chunk {index} extends past the end of the file.");
            if (len % 4 != 0) issues.Add($"Chunk {index} length {len} is not a multiple of 4.");
            if (index == 0 && type != ChunkJson) throw Bad("The first chunk must be JSON.");
            if (index == 1 && type != ChunkBin) issues.Add("The second chunk is not BIN.");
            if (type == ChunkJson && json is null)
            {
                try { json = JsonNode.Parse(b.AsSpan((int)start, (int)len)) as JsonObject; }
                catch (JsonException e) { throw Bad("Invalid JSON chunk: " + e.Message); }
                if (json is null) throw Bad("The JSON chunk is not an object.");
            }
            else if (type == ChunkBin && bin is null) bin = b.AsSpan((int)start, (int)len).ToArray();
            off = start + len;
            index++;
        }
        if (off != total) issues.Add("Trailing bytes after the last chunk.");
        var c = new GlbContainer { Json = json ?? throw Bad("No JSON chunk."), Bin = bin ?? Array.Empty<byte>(), HasBin = bin is not null };
        c.Issues.AddRange(issues);
        return c;
    }

    /// <summary>Writes a GLB: JSON chunk padded with spaces, BIN chunk (if any) padded with zeros.</summary>
    public static byte[] Write(JsonObject json, ReadOnlySpan<byte> bin)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        var jsonPad = Pad4(jsonBytes.Length);
        var binPad = Pad4(bin.Length);
        var total = 12 + 8 + jsonBytes.Length + jsonPad + (bin.Length > 0 ? 8 + bin.Length + binPad : 0);
        var o = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(o, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(8), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(12), (uint)(jsonBytes.Length + jsonPad));
        BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(16), ChunkJson);
        jsonBytes.CopyTo(o, 20);
        o.AsSpan(20 + jsonBytes.Length, jsonPad).Fill((byte)' ');
        if (bin.Length > 0)
        {
            var p = 20 + jsonBytes.Length + jsonPad;
            BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(p), (uint)(bin.Length + binPad));
            BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(p + 4), ChunkBin);
            bin.CopyTo(o.AsSpan(p + 8));
        }
        return o;
    }

    public static int Pad4(int n) => (4 - n % 4) % 4;
}

/// <summary>
/// Appends binary data and accessors to a glTF document whose buffer 0 is the GLB BIN chunk.
/// Every buffer view starts on a 4-byte boundary, so float and uint accessors are always aligned.
/// </summary>
public sealed class GltfBuilder
{
    public const int ArrayBuffer = 34962;
    public const int ElementArrayBuffer = 34963;
    public const int Float = 5126;
    public const int UnsignedShort = 5123;
    public const int UnsignedInt = 5125;

    readonly MemoryStream _bin = new();

    public GltfBuilder(JsonObject json, byte[]? existingBin = null)
    {
        Json = json;
        if (existingBin is { Length: > 0 })
        {
            // Only the declared length is data; anything after it is chunk padding.
            var declared = GltfAnimationReader.Int(Arr("buffers").FirstOrDefault()?["byteLength"]) ?? existingBin.Length;
            _bin.Write(existingBin, 0, Math.Min(declared, existingBin.Length));
        }
    }

    public JsonObject Json { get; }

    public JsonArray Arr(string name)
    {
        if (Json[name] is JsonArray a) return a;
        a = new JsonArray();
        Json[name] = a;
        return a;
    }

    public static int Add(JsonArray array, JsonNode item)
    {
        array.Add(item);
        return array.Count - 1;
    }

    public int AddBufferView(ReadOnlySpan<byte> data, int? target = null, int? byteStride = null)
    {
        while (_bin.Length % 4 != 0) _bin.WriteByte(0);
        var offset = (int)_bin.Length;
        _bin.Write(data);
        var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = data.Length };
        if (byteStride is int s) view["byteStride"] = s;
        if (target is int t) view["target"] = t;
        return Add(Arr("bufferViews"), view);
    }

    /// <summary>Appends raw bytes to the BIN chunk (4-byte aligned start) without creating a buffer view; returns their offset.</summary>
    public int AppendBytes(ReadOnlySpan<byte> data)
    {
        while (_bin.Length % 4 != 0) _bin.WriteByte(0);
        var offset = (int)_bin.Length;
        _bin.Write(data);
        return offset;
    }

    /// <summary>A buffer view over bytes already in the BIN chunk (see <see cref="AppendBytes"/>).</summary>
    public int AddBufferViewAt(int offset, int length, int? target = null, int? byteStride = null)
    {
        var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = length };
        if (byteStride is int s) view["byteStride"] = s;
        if (target is int t) view["target"] = t;
        return Add(Arr("bufferViews"), view);
    }

    /// <summary>Float accessor of <paramref name="type"/> ("SCALAR", "VEC3", "VEC4"...). min/max are written when asked.</summary>
    public int AddFloats(float[] data, string type, bool minMax, int? target = null)
    {
        var comps = Components(type);
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        var view = AddBufferView(bytes, target);
        var acc = new JsonObject
        {
            ["bufferView"] = view, ["componentType"] = Float, ["count"] = data.Length / comps, ["type"] = type,
        };
        if (minMax)
        {
            var min = new float[comps];
            var max = new float[comps];
            Array.Fill(min, float.PositiveInfinity);
            Array.Fill(max, float.NegativeInfinity);
            for (var i = 0; i < data.Length; i++)
            {
                min[i % comps] = Math.Min(min[i % comps], data[i]);
                max[i % comps] = Math.Max(max[i % comps], data[i]);
            }
            acc["min"] = new JsonArray(min.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            acc["max"] = new JsonArray(max.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }
        return Add(Arr("accessors"), acc);
    }

    public int AddIndices(int[] indices, int vertexCount)
    {
        var small = vertexCount <= ushort.MaxValue;
        var bytes = new byte[indices.Length * (small ? 2 : 4)];
        for (var i = 0; i < indices.Length; i++)
        {
            if (small) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)indices[i]);
            else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), (uint)indices[i]);
        }
        var view = AddBufferView(bytes, ElementArrayBuffer);
        return Add(Arr("accessors"), new JsonObject
        {
            ["bufferView"] = view, ["componentType"] = small ? UnsignedShort : UnsignedInt, ["count"] = indices.Length, ["type"] = "SCALAR",
        });
    }

    public static int Components(string type) => type switch
    {
        "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT2" => 4, "MAT3" => 9, "MAT4" => 16,
        _ => throw new ArgumentException($"Unknown accessor type '{type}'."),
    };

    /// <summary>Finalizes buffer 0's length and returns the GLB bytes.</summary>
    public byte[] ToGlb()
    {
        while (_bin.Length % 4 != 0) _bin.WriteByte(0);
        var buffers = Arr("buffers");
        if (buffers.Count == 0) buffers.Add(new JsonObject());
        buffers[0]!["byteLength"] = (int)_bin.Length;
        return GlbContainer.Write(Json, _bin.GetBuffer().AsSpan(0, (int)_bin.Length));
    }
}
