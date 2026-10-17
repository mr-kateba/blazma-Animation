using System.Text.Json.Nodes;
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
    public static List<ExportedProp> Add(GltfBuilder b, Project project, BakedClip baked, Rig rig, NodeBinding[] bindings, List<string> warnings)
    {
        var props = PropEvaluator.PropsFor(project, baked.Clip, rig);
        var result = new List<ExportedProp>();
        if (props.Count == 0) return result;
        var nodes = b.Arr("nodes");
        var clip = baked.Clip;
        var meshes = new Dictionary<PropShape, int>();
        int? material = null;
        foreach (var prop in props)
        {
            var bone = PropEvaluator.AttachedBone(project, prop, rig);
            var locals = new Transform[baked.FrameCount];
            for (var i = 0; i < locals.Length; i++)
            {
                var elapsed = Flicks.FromFrame(baked.StartFrame + i, clip.Fps);
                var time = clip.Loop.Enabled ? ClipEvaluator.PlaybackTime(clip, elapsed) : elapsed;
                var local = PropEvaluator.Local(prop, clip, time);
                locals[i] = bone >= 0 ? local : PropEvaluator.World(project, prop, local, rig, null);
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

    /// <summary>
    /// Appends the default scene of <paramref name="src"/> (nodes, meshes, accessors, buffer views, materials, textures, images,
    /// samplers) to <paramref name="b"/>. Skins, cameras, morph weights and animations are dropped. Returns the new root nodes.
    /// Files that require extensions are refused (unsupported_source).
    /// </summary>
    public static List<int> AppendGlbScene(GltfBuilder b, GlbContainer src, List<string> warnings, string name)
    {
        var json = src.Json;
        if (json["extensionsRequired"] is JsonArray { Count: > 0 } req)
            throw new ExportException("unsupported_source", $"The model needs glTF extensions ({string.Join(", ", req.Select(x => (string?)x))}).");
        var views = json["bufferViews"] as JsonArray ?? new JsonArray();
        var viewMap = new int[views.Count];
        var buffers = new Dictionary<int, byte[]>();
        for (var i = 0; i < views.Count; i++)
        {
            var v = (JsonObject)views[i]!;
            var buffer = GltfAnimationReader.Int(v["buffer"]) ?? 0;
            if (!buffers.TryGetValue(buffer, out var data)) buffers[buffer] = data = GltfAnimationReader.BufferData(src, buffer);
            var off = GltfAnimationReader.Int(v["byteOffset"]) ?? 0;
            var len = GltfAnimationReader.Int(v["byteLength"]) ?? 0;
            if (off < 0 || len < 0 || off + len > data.Length) throw new ExportException("invalid_gltf", $"bufferViews[{i}] is outside its buffer.");
            viewMap[i] = b.AddBufferView(data.AsSpan(off, len), GltfAnimationReader.Int(v["target"]), GltfAnimationReader.Int(v["byteStride"]));
        }
        int MapView(JsonNode? n) => GltfAnimationReader.Int(n) is int i && i >= 0 && i < viewMap.Length ? viewMap[i] : throw new ExportException("invalid_gltf", "A bufferView reference is out of range.");

        var accMap = Copy("accessors", a =>
        {
            if (a["bufferView"] is not null) a["bufferView"] = MapView(a["bufferView"]);
            if (a["sparse"] is JsonObject sp)
            {
                if (sp["indices"] is JsonObject si) si["bufferView"] = MapView(si["bufferView"]);
                if (sp["values"] is JsonObject sv) sv["bufferView"] = MapView(sv["bufferView"]);
            }
        });
        var samplerMap = Copy("samplers", _ => { });
        var imageMap = Copy("images", img =>
        {
            if (img["bufferView"] is not null) img["bufferView"] = MapView(img["bufferView"]);
            else if ((string?)img["uri"] is { } uri && !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Prop '{name}': image '{uri}' is an external file and was not copied.");
                img.Remove("uri");
            }
        });
        var textureMap = Copy("textures", t =>
        {
            t.Remove("extensions");
            if (GltfAnimationReader.Int(t["sampler"]) is int s) t["sampler"] = Remap(samplerMap, s);
            if (GltfAnimationReader.Int(t["source"]) is int im) t["source"] = Remap(imageMap, im);
        });
        var materialMap = Copy("materials", m =>
        {
            m.Remove("extensions");
            foreach (var info in TextureInfos(m)) info["index"] = Remap(textureMap, GltfAnimationReader.Int(info["index"]) ?? -1);
        });
        var meshMap = Copy("meshes", mesh =>
        {
            mesh.Remove("weights");
            foreach (var prim in mesh["primitives"] as JsonArray ?? new JsonArray())
            {
                if (prim is not JsonObject p) continue;
                p.Remove("extensions");
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
        });

        // Nodes of the default scene, depth first.
        var srcNodes = json["nodes"] as JsonArray ?? new JsonArray();
        var sceneIndex = GltfAnimationReader.Int(json["scene"]) ?? 0;
        var scenes = json["scenes"] as JsonArray;
        var roots = scenes is { Count: > 0 } && sceneIndex >= 0 && sceneIndex < scenes.Count
            ? (scenes[sceneIndex]?["nodes"] as JsonArray ?? new JsonArray()).Select(n => GltfAnimationReader.Int(n) ?? -1).ToList()
            : Enumerable.Range(0, srcNodes.Count).Where(i => !srcNodes.Any(n => (n?["children"] as JsonArray)?.Any(c => GltfAnimationReader.Int(c) == i) == true)).ToList();
        var dest = b.Arr("nodes");
        var visited = new HashSet<int>();
        var skipped = false;
        int CopyNode(int i)
        {
            if (i < 0 || i >= srcNodes.Count || !visited.Add(i)) throw new ExportException("invalid_gltf", "The model's node hierarchy is invalid.");
            var n = (JsonObject)srcNodes[i]!.DeepClone();
            if (n.Remove("skin") | n.Remove("camera") | n.Remove("weights")) skipped = true;
            n.Remove("extensions");
            if (GltfAnimationReader.Int(n["mesh"]) is int mesh) n["mesh"] = Remap(meshMap, mesh);
            var kids = (n["children"] as JsonArray ?? new JsonArray()).Select(c => GltfAnimationReader.Int(c) ?? -1).ToList();
            n.Remove("children");
            var index = GltfBuilder.Add(dest, n);
            if (kids.Count > 0) n["children"] = new JsonArray(kids.Select(k => (JsonNode?)CopyNode(k)).ToArray());
            return index;
        }
        var result = roots.Select(CopyNode).ToList();
        if (skipped) warnings.Add($"Prop '{name}': skins, cameras and morph weights of its model are not exported (static mesh only).");
        return result;

        int[] Copy(string array, Action<JsonObject> fix)
        {
            var source = json[array] as JsonArray ?? new JsonArray();
            var map = new int[source.Count];
            for (var i = 0; i < source.Count; i++)
            {
                var item = (JsonObject)source[i]!.DeepClone();
                fix(item);
                map[i] = GltfBuilder.Add(b.Arr(array), item);
            }
            return map;
        }

        static int Remap(int[] map, int i) => i >= 0 && i < map.Length ? map[i] : throw new ExportException("invalid_gltf", "A reference in the model is out of range.");
    }

    static IEnumerable<JsonObject> TextureInfos(JsonObject material)
    {
        if (material["pbrMetallicRoughness"] is JsonObject pbr)
            foreach (var k in new[] { "baseColorTexture", "metallicRoughnessTexture" })
                if (pbr[k] is JsonObject t) yield return t;
        foreach (var k in new[] { "normalTexture", "occlusionTexture", "emissiveTexture" })
            if (material[k] is JsonObject t) yield return t;
    }
}
