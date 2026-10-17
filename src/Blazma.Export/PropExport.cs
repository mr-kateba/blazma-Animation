using System.Text.Json.Nodes;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;

namespace Blazma.Export;

/// <summary>A prop written to an exported file: its node and the node-local transform on every baked frame.</summary>
/// <param name="Bone">Index of the bone the node is parented under, or −1 for a scene root.</param>
public sealed record ExportedProp(Prop Prop, int Node, int Bone, Transform[] Locals);

/// <summary>
/// Props in GLB export (docs/DATA_FORMAT.md §7.8). Every prop that <see cref="PropEvaluator.PropsFor"/> selects for the clip and
/// rig becomes a node: attached props are children of their bone's node (node-local = the animated offset), others are scene
/// roots (node-local = world transform). The node carries the animation channels; its geometry hangs under it as a child node:
/// a generated box/sphere/cylinder mesh scaled to the prop size, or the embedded .glb's default scene (meshes, materials and
/// textures copied; skins, cameras and animations are dropped).
/// </summary>
public static class PropExport
{
    public static List<ExportedProp> Add(GltfBuilder b, Project project, BakedClip baked, Rig rig, NodeBinding[] bindings, List<string> warnings,
        ObjectId? character = null)
    {
        var who = CharacterFor(project, rig, character);
        var props = PropEvaluator.PropsFor(project, baked.Clip, rig, who);
        var result = new List<ExportedProp>();
        if (props.Count == 0) return result;
        var nodes = b.Arr("nodes");
        var clip = baked.Clip;
        var meshes = new Dictionary<PropShape, int>();
        int? material = null;
        foreach (var prop in props)
        {
            var bone = PropEvaluator.AttachedBone(project, prop, rig, who);
            var locals = new Transform[baked.FrameCount];
            for (var i = 0; i < locals.Length; i++)
            {
                var elapsed = Flicks.FromFrame(baked.StartFrame + i, clip.Fps);
                var time = clip.Loop.Enabled ? ClipEvaluator.PlaybackTime(clip, elapsed) : elapsed;
                var local = PropEvaluator.Local(prop, clip, time);
                locals[i] = bone >= 0 ? local : PropEvaluator.World(project, prop, local, rig, null, who);
            }
            if (bone < 0 && prop.Attachment is not null)
                warnings.Add($"Prop '{prop.Name}' is attached to another character; it is exported at its free transform.");

            var first = locals[0];
            var node = GltfBuilder.Add(nodes, new JsonObject
            {
                ["name"] = prop.Name,
                ["translation"] = AnimationExporter.Vec(first.Translation),
                ["rotation"] = AnimationExporter.Q(first.Rotation),
                ["scale"] = AnimationExporter.Vec(first.Scale),
                ["extras"] = new JsonObject { ["blazma"] = new JsonObject { ["kind"] = "prop", ["propId"] = prop.Id.ToString() } },
            });
            var children = new JsonArray();
            if (prop.Shape == PropShape.Mesh)
            {
                try
                {
                    var asset = project.FindAsset(prop.Asset) ?? throw new ExportException("missing_asset", $"The model file of prop '{prop.Name}' is not in the project.");
                    foreach (var root in AppendGlbScene(b, GlbContainer.Parse(asset.Data), warnings, prop.Name)) children.Add(root);
                }
                catch (ExportException e)
                {
                    warnings.Add($"Prop '{prop.Name}': its model could not be copied ({e.Message}); a box is exported instead.");
                    children.Add(ShapeNode(PropShape.Box));
                }
            }
            else children.Add(ShapeNode(prop.Shape));
            nodes[node]!["children"] = children;

            if (bone >= 0)
            {
                var parent = (JsonObject)nodes[bindings[bone].Node]!;
                if (parent["children"] is not JsonArray pc) parent["children"] = pc = new JsonArray();
                pc.Add(node);
            }
            else SceneRoots(b).Add(node);
            result.Add(new ExportedProp(prop, node, bone, locals));

            int ShapeNode(PropShape shape)
            {
                if (!meshes.TryGetValue(shape, out var mesh))
                {
                    material ??= GltfBuilder.Add(b.Arr("materials"), new JsonObject
                    {
                        ["name"] = "BlazmaProp",
                        ["pbrMetallicRoughness"] = new JsonObject
                        {
                            ["baseColorFactor"] = new JsonArray(0.85, 0.62, 0.25, 1.0), ["metallicFactor"] = 0.2, ["roughnessFactor"] = 0.5,
                        },
                    });
                    mesh = meshes[shape] = AddShapeMesh(b, shape, material.Value);
                }
                return GltfBuilder.Add(nodes, new JsonObject
                {
                    ["name"] = $"{prop.Name}_mesh",
                    ["mesh"] = mesh,
                    ["scale"] = AnimationExporter.Vec(prop.Size),
                });
            }
        }
        return result;
    }

    /// <summary>The character a clip is exported with: the given one, else the first character using <paramref name="rig"/>.</summary>
    public static ObjectId CharacterFor(Project project, Rig rig, ObjectId? character) =>
        character ?? project.Characters.Find(c => c.Rig == rig.Id)?.Id ?? default;

    static JsonArray SceneRoots(GltfBuilder b)
    {
        var scenes = b.Arr("scenes");
        var index = GltfAnimationReader.Int(b.Json["scene"]) ?? 0;
        if (scenes.Count == 0) { scenes.Add(new JsonObject { ["nodes"] = new JsonArray() }); index = 0; b.Json["scene"] = 0; }
        index = Math.Clamp(index, 0, scenes.Count - 1);
        var scene = (JsonObject)scenes[index]!;
        if (scene["nodes"] is not JsonArray roots) scene["nodes"] = roots = new JsonArray();
        return roots;
    }

    // ---- generated shapes (unit size, centred) -----------------------------------------------------

    static int AddShapeMesh(GltfBuilder b, PropShape shape, int material)
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var idx = new List<int>();
        void V(Vec3 p, Vec3 n)
        {
            pos.AddRange(new[] { (float)p.X, (float)p.Y, (float)p.Z });
            nrm.AddRange(new[] { (float)n.X, (float)n.Y, (float)n.Z });
        }
        switch (shape)
        {
            case PropShape.Sphere:
            {
                const int rings = 12, segs = 16;
                for (var r = 0; r <= rings; r++)
                {
                    var phi = Math.PI * r / rings;
                    for (var s = 0; s <= segs; s++)
                    {
                        var th = 2 * Math.PI * s / segs;
                        var n = new Vec3(Math.Sin(phi) * Math.Sin(th), Math.Cos(phi), Math.Sin(phi) * Math.Cos(th));
                        V(n * 0.5, n);
                    }
                }
                for (var r = 0; r < rings; r++)
                    for (var s = 0; s < segs; s++)
                    {
                        int a = r * (segs + 1) + s, c = a + segs + 1;
                        idx.AddRange(new[] { a, c, a + 1, a + 1, c, c + 1 });
                    }
                break;
            }
            case PropShape.Cylinder:
            {
                const int segs = 16;
                for (var s = 0; s <= segs; s++)
                {
                    var th = 2 * Math.PI * s / segs;
                    var n = new Vec3(Math.Sin(th), 0, Math.Cos(th));
                    V(n * 0.5 + new Vec3(0, -0.5, 0), n);
                    V(n * 0.5 + new Vec3(0, 0.5, 0), n);
                }
                for (var s = 0; s < segs; s++)
                {
                    int a = 2 * s;
                    idx.AddRange(new[] { a, a + 2, a + 1, a + 1, a + 2, a + 3 });
                }
                foreach (var y in new[] { -0.5, 0.5 })
                {
                    var n = new Vec3(0, Math.Sign(y), 0);
                    var centre = pos.Count / 3;
                    V(new Vec3(0, y, 0), n);
                    for (var s = 0; s <= segs; s++)
                    {
                        var th = 2 * Math.PI * s / segs;
                        V(new Vec3(0.5 * Math.Sin(th), y, 0.5 * Math.Cos(th)), n);
                    }
                    for (var s = 0; s < segs; s++)
                        idx.AddRange(y > 0 ? new[] { centre, centre + 1 + s, centre + 2 + s } : new[] { centre, centre + 2 + s, centre + 1 + s });
                }
                break;
            }
            default:
            {
                void Face(Vec3 n, Vec3 u, Vec3 v)
                {
                    var start = pos.Count / 3;
                    foreach (var (su, sv) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                        V(n * 0.5 + u * (0.5 * su) + v * (0.5 * sv), n);
                    idx.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
                }
                Face(Vec3.UnitX, -Vec3.UnitZ, Vec3.UnitY);
                Face(-Vec3.UnitX, Vec3.UnitZ, Vec3.UnitY);
                Face(Vec3.UnitY, Vec3.UnitX, -Vec3.UnitZ);
                Face(-Vec3.UnitY, Vec3.UnitX, Vec3.UnitZ);
                Face(Vec3.UnitZ, Vec3.UnitX, Vec3.UnitY);
                Face(-Vec3.UnitZ, -Vec3.UnitX, Vec3.UnitY);
                break;
            }
        }
        var position = b.AddFloats(pos.ToArray(), "VEC3", minMax: true, GltfBuilder.ArrayBuffer);
        var normal = b.AddFloats(nrm.ToArray(), "VEC3", minMax: false, GltfBuilder.ArrayBuffer);
        var indices = b.AddIndices(idx.ToArray(), pos.Count / 3);
        return GltfBuilder.Add(b.Arr("meshes"), new JsonObject
        {
            ["name"] = "Prop" + shape,
            ["primitives"] = new JsonArray(new JsonObject
            {
                ["attributes"] = new JsonObject { ["POSITION"] = position, ["NORMAL"] = normal },
                ["indices"] = indices,
                ["material"] = material,
                ["mode"] = 4,
            }),
        });
    }

    // ---- copying an embedded .glb ---------------------------------------------------------------

    /// <summary>Most nodes a prop model may have (the whole file, not only the default scene).</summary>
    public const int MaxNodes = 10_000;

    /// <summary>Deepest node nesting a prop model may have (roots are depth 1).</summary>
    public const int MaxDepth = 256;

    /// <summary>
    /// Appends the default scene of <paramref name="src"/> (nodes, meshes, accessors, buffer views, materials, textures, images,
    /// samplers) to <paramref name="b"/>. Skins, cameras, morph weights and animations are dropped; every glTF extension object is
    /// stripped (so the output never uses an extension it does not declare); images that are external files are dropped together
    /// with the textures and material slots that use them (warning). Returns the new root nodes.
    /// <para>Hostile input is bounded: each source buffer's used range is copied once (overlapping buffer views share it, so the
    /// copied bytes never exceed the source buffers), at most <see cref="MaxNodes"/> nodes and <see cref="MaxDepth"/> levels are
    /// accepted, the hierarchy is walked without recursion and a node reached twice (cycle or shared child) is refused. Every
    /// malformed input throws <see cref="ExportException"/> (invalid_gltf / unsupported_source), never another exception.</para>
    /// </summary>
    public static List<int> AppendGlbScene(GltfBuilder b, GlbContainer src, List<string> warnings, string name)
    {
        var json = src.Json;
        if (json["extensionsRequired"] is JsonArray { Count: > 0 } req)
            throw new ExportException("unsupported_source", $"The model needs glTF extensions ({string.Join(", ", req.Select(x => x is JsonValue v && v.TryGetValue<string>(out var t) ? t : "?"))}).");
        var srcNodes = ArrayOf(json, "nodes");
        if (srcNodes.Count > MaxNodes) throw Bad($"The model has {srcNodes.Count} nodes (at most {MaxNodes:N0}).");
        var stripped = new SortedSet<string>(StringComparer.Ordinal);

        // Buffer views: the used range of every source buffer is copied once; views are rebased into it.
        var views = ArrayOf(json, "bufferViews");
        var parsed = new (int Buffer, long Offset, long Length, int? Target, int? Stride)[views.Count];
        var buffers = new Dictionary<int, byte[]>();
        var used = new Dictionary<int, (long From, long To)>();
        for (var i = 0; i < views.Count; i++)
        {
            var v = Obj(views, i, "bufferViews");
            var buffer = GltfAnimationReader.Int(v["buffer"]) ?? throw Bad($"bufferViews[{i}] has no valid buffer.");
            if (!buffers.TryGetValue(buffer, out var data)) buffers[buffer] = data = GltfAnimationReader.BufferData(src, buffer);
            var off = v["byteOffset"] is null ? 0 : GltfAnimationReader.Long(v["byteOffset"]) ?? -1;
            var len = GltfAnimationReader.Long(v["byteLength"]) ?? -1;
            if (off < 0 || len < 0 || off > data.Length || len > data.Length - off) throw Bad($"bufferViews[{i}] is outside its buffer.");
            parsed[i] = (buffer, off, len, GltfAnimationReader.Int(v["target"]), GltfAnimationReader.Int(v["byteStride"]));
            used[buffer] = used.TryGetValue(buffer, out var r) ? (Math.Min(r.From, off), Math.Max(r.To, off + len)) : (off, off + len);
        }
        var bases = new Dictionary<int, (long Base, long From)>();
        foreach (var (buffer, (from, to)) in used.OrderBy(kv => kv.Key))
        {
            var aligned = from - from % 4; // keeps every view's offset modulo 4, so accessor alignment is preserved
            bases[buffer] = (b.AppendBytes(buffers[buffer].AsSpan((int)aligned, (int)(to - aligned))), aligned);
        }
        var viewMap = new int[views.Count];
        for (var i = 0; i < views.Count; i++)
        {
            var (buffer, off, len, target, stride) = parsed[i];
            var (bas, from) = bases[buffer];
            viewMap[i] = b.AddBufferViewAt(checked((int)(bas + off - from)), (int)len, target, stride);
        }
        int MapView(JsonNode? n) => GltfAnimationReader.Int(n) is int i && i >= 0 && i < viewMap.Length ? viewMap[i] : throw Bad("A bufferView reference is out of range.");

        var accMap = Copy("accessors", a =>
        {
            if (a["bufferView"] is not null) a["bufferView"] = MapView(a["bufferView"]);
            if (a["sparse"] is JsonObject sp)
            {
                if (sp["indices"] is JsonObject si) si["bufferView"] = MapView(si["bufferView"]);
                if (sp["values"] is JsonObject sv) sv["bufferView"] = MapView(sv["bufferView"]);
            }
            return true;
        });
        var samplerMap = Copy("samplers", _ => true);
        var imageMap = Copy("images", img =>
        {
            if (img["bufferView"] is not null)
            {
                img["bufferView"] = MapView(img["bufferView"]);
                img.Remove("uri");
                return true;
            }
            if (img["uri"] is JsonValue u && u.TryGetValue<string>(out var uri) && uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;
            var what = img["uri"] is JsonValue u2 && u2.TryGetValue<string>(out var ext) ? $"image '{ext}' is an external file" : "an image has no data";
            warnings.Add($"Prop '{name}': {what}; it was not copied (textures using it are dropped).");
            return false;
        });
        var textureMap = Copy("textures", t =>
        {
            if (GltfAnimationReader.Int(t["sampler"]) is int s) t["sampler"] = Remap(samplerMap, s);
            else t.Remove("sampler");
            // A texture whose only image was dropped (or that names its image through an extension) has nothing to show.
            if (GltfAnimationReader.Int(t["source"]) is not int im || Remap(imageMap, im) is var mapped && mapped < 0) return false;
            t["source"] = mapped;
            return true;
        });
        var materialMap = Copy("materials", m =>
        {
            foreach (var (owner, key) in TextureSlots(m))
            {
                var index = Remap(textureMap, GltfAnimationReader.Int(owner[key]?["index"]) ?? -1);
                if (index < 0) owner.Remove(key);
                else owner[key]!["index"] = index;
            }
            return true;
        });
        var meshMap = Copy("meshes", mesh =>
        {
            mesh.Remove("weights");
            if (mesh["primitives"] is not JsonArray prims) throw Bad("A mesh has no primitives.");
            foreach (var prim in prims)
            {
                if (prim is not JsonObject p) throw Bad("A mesh primitive is not an object.");
                if (p["attributes"] is JsonObject attrs)
                    foreach (var key in attrs.Select(kv => kv.Key).ToList())
                    {
                        if (key.StartsWith("JOINTS_", StringComparison.Ordinal) || key.StartsWith("WEIGHTS_", StringComparison.Ordinal)) { attrs.Remove(key); continue; }
                        attrs[key] = Remap(accMap, GltfAnimationReader.Int(attrs[key]) ?? -1);
                    }
                if (GltfAnimationReader.Int(p["indices"]) is int ind) p["indices"] = Remap(accMap, ind);
                if (GltfAnimationReader.Int(p["material"]) is int mat) p["material"] = Remap(materialMap, mat);
                if (p["targets"] is JsonArray targets)
                    foreach (var tg in targets.OfType<JsonObject>())
                        foreach (var key in tg.Select(kv => kv.Key).ToList()) tg[key] = Remap(accMap, GltfAnimationReader.Int(tg[key]) ?? -1);
            }
            return true;
        });

        // Roots of the default scene (or every parentless node when there is no scene).
        var sceneIndex = GltfAnimationReader.Int(json["scene"]) ?? 0;
        var scenes = json["scenes"] as JsonArray;
        List<int> roots;
        if (scenes is { Count: > 0 } && sceneIndex >= 0 && sceneIndex < scenes.Count)
            roots = ((scenes[sceneIndex] as JsonObject)?["nodes"] as JsonArray ?? new JsonArray()).Select(n => GltfAnimationReader.Int(n) ?? -1).ToList();
        else
        {
            var children = new HashSet<int>();
            for (var i = 0; i < srcNodes.Count; i++)
                foreach (var c in (srcNodes[i] as JsonObject)?["children"] as JsonArray ?? new JsonArray())
                    if (GltfAnimationReader.Int(c) is int ci) children.Add(ci);
            roots = Enumerable.Range(0, srcNodes.Count).Where(i => !children.Contains(i)).ToList();
        }

        // Nodes, depth first in document order, with an explicit stack (no recursion on file data).
        var dest = b.Arr("nodes");
        var visited = new HashSet<int>();
        var skipped = false;
        var result = new List<int>();
        var stack = new Stack<(int Src, JsonArray? Parent, int Depth)>();
        for (var r = roots.Count - 1; r >= 0; r--) stack.Push((roots[r], null, 1));
        while (stack.Count > 0)
        {
            var (i, parentKids, depth) = stack.Pop();
            if (i < 0 || i >= srcNodes.Count || !visited.Add(i)) throw Bad("The model's node hierarchy is invalid (a missing node, a cycle or a node with two parents).");
            if (depth > MaxDepth) throw Bad($"The model's node hierarchy is deeper than {MaxDepth} levels.");
            var n = (JsonObject)Obj(srcNodes, i, "nodes").DeepClone();
            if (n.Remove("skin") | n.Remove("camera") | n.Remove("weights")) skipped = true;
            Strip(n, stripped);
            if (GltfAnimationReader.Int(n["mesh"]) is int mesh) n["mesh"] = Remap(meshMap, mesh);
            else n.Remove("mesh");
            var kids = (n["children"] as JsonArray ?? new JsonArray()).Select(c => GltfAnimationReader.Int(c) ?? -1).ToList();
            n.Remove("children");
            var index = GltfBuilder.Add(dest, n);
            if (parentKids is null) result.Add(index); else parentKids.Add(index);
            if (kids.Count == 0) continue;
            var own = new JsonArray();
            n["children"] = own;
            for (var k = kids.Count - 1; k >= 0; k--) stack.Push((kids[k], own, depth + 1));
        }
        if (skipped) warnings.Add($"Prop '{name}': skins, cameras and morph weights of its model are not exported (static mesh only).");
        if (stripped.Count > 0) warnings.Add($"Prop '{name}': glTF extensions of its model are not exported ({string.Join(", ", stripped)}).");
        return result;

        // Copies the array's items (fix returns false to drop one: its map entry is −1).
        int[] Copy(string array, Func<JsonObject, bool> fix)
        {
            var source = ArrayOf(json, array);
            var map = new int[source.Count];
            for (var i = 0; i < source.Count; i++)
            {
                var item = (JsonObject)Obj(source, i, array).DeepClone();
                Strip(item, stripped);
                map[i] = fix(item) ? GltfBuilder.Add(b.Arr(array), item) : -1;
            }
            return map;
        }
    }

    static ExportException Bad(string message) => new("invalid_gltf", message);

    static JsonArray ArrayOf(JsonObject json, string key) => json[key] switch
    {
        null => new JsonArray(),
        JsonArray a => a,
        _ => throw Bad($"'{key}' is not an array."),
    };

    static JsonObject Obj(JsonArray array, int i, string name) => array[i] as JsonObject ?? throw Bad($"{name}[{i}] is not an object.");

    /// <summary>Index in the copy, or −1 when the item was dropped; invalid_gltf when out of range.</summary>
    static int Remap(int[] map, int i) => i >= 0 && i < map.Length ? map[i] : throw Bad("A reference in the model is out of range.");

    /// <summary>Removes every "extensions" object (outside "extras") and records the extension names.</summary>
    static void Strip(JsonObject o, SortedSet<string> names)
    {
        var stack = new Stack<JsonNode>();
        stack.Push(o);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case JsonObject obj:
                    if (obj["extensions"] is JsonObject ext) foreach (var kv in ext) names.Add(kv.Key);
                    if (obj.ContainsKey("extensions")) obj.Remove("extensions");
                    foreach (var kv in obj)
                        if (kv.Key != "extras" && kv.Value is not null) stack.Push(kv.Value);
                    break;
                case JsonArray arr:
                    foreach (var x in arr) if (x is not null) stack.Push(x);
                    break;
            }
        }
    }

    /// <summary>The texture slots (owner object, key) a material uses.</summary>
    static List<(JsonObject Owner, string Key)> TextureSlots(JsonObject material)
    {
        var list = new List<(JsonObject, string)>();
        if (material["pbrMetallicRoughness"] is JsonObject pbr)
            foreach (var k in new[] { "baseColorTexture", "metallicRoughnessTexture" })
                if (pbr[k] is JsonObject) list.Add((pbr, k));
        foreach (var k in new[] { "normalTexture", "occlusionTexture", "emissiveTexture" })
            if (material[k] is JsonObject) list.Add((material, k));
        return list;
    }
}
