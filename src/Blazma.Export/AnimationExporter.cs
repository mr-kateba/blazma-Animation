using System.Text.Json.Nodes;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Rigging;

namespace Blazma.Export;

/// <summary>Where one bone's animation goes in the file.</summary>
/// <param name="Node">glTF node index.</param>
/// <param name="ParentWorld">For a root bone whose node has non-bone ancestors (folded into the bone's rest on import):
/// their scene-space transform, used to convert the bone's model-space local back into the node's real parent space.</param>
public readonly record struct NodeBinding(int Node, Transform? ParentWorld)
{
    public Transform ToNodeLocal(Transform boneLocal) =>
        ParentWorld is { } pw ? RigEvaluator.LocalFromModel(pw, boneLocal) : boneLocal;
}

public sealed class ExportResult
{
    public required byte[] Glb { get; init; }
    public required JsonObject Sidecar { get; init; }
    public required BakedClip Baked { get; init; }
    public required string AnimationName { get; init; }
    public int AnimationIndex { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Props written as extra nodes (empty when the clip shows none).</summary>
    public IReadOnlyList<ExportedProp> Props { get; init; } = Array.Empty<ExportedProp>();
}

/// <summary>
/// Clip → GLB. Built-in rigs get a new file: one node per bone (rest TRS) plus rigid box meshes as
/// child nodes. Imported rigs re-use the embedded original GLB unchanged and only gain an animation
/// whose channels target the original joint nodes.
/// </summary>
public static class AnimationExporter
{
    public const string Generator = "Blazma Animation";

    public static ExportResult Export(Project project, Clip clip, Rig rig, ExportOptions? options = null, string glbFileName = "animation.glb")
    {
        options ??= new ExportOptions();
        var baked = AnimationBaker.Bake(clip, rig, options);
        var warnings = new List<string>();
        GltfBuilder builder;
        NodeBinding[] bindings;
        if (rig.Source.Kind == RigSourceKind.Imported)
            (builder, bindings) = PrepareImported(project, rig, warnings);
        else
            (builder, bindings) = BuildMannequin(rig);

        var name = UniqueAnimationName(builder.Json, string.IsNullOrWhiteSpace(options.AnimationName) ? clip.Name : options.AnimationName.Trim());
        var props = options.IncludeProps ? PropExport.Add(builder, project, baked, rig, bindings, warnings, options.Character) : new List<ExportedProp>();
        var index = AddAnimation(builder, baked, bindings, name, options.CompactConstantChannels, options.IncludeProps ? props : null);
        var glb = builder.ToGlb();
        var sidecar = Sidecar.Build(project, baked, rig, bindings, name, glbFileName);
        if (props.Count > 0)
            sidecar["props"] = new JsonArray(props.Select(x => (JsonNode?)new JsonObject
            {
                ["id"] = x.Prop.Id.ToString(),
                ["name"] = x.Prop.Name,
                ["shape"] = x.Prop.Shape.ToString().ToLowerInvariant(),
                ["node"] = x.Node,
                ["attachedTo"] = x.Bone >= 0 ? rig.Bones[x.Bone].Name : null,
            }).ToArray());
        if (Sidecar.Combat(project, baked, rig, bindings, props, PropExport.CharacterFor(project, rig, options.Character), warnings) is { } combat)
            sidecar["combat"] = combat;
        return new ExportResult
        {
            Glb = glb, Baked = baked, AnimationName = name, AnimationIndex = index, Warnings = warnings,
            Sidecar = sidecar, Props = props,
        };
    }

    /// <summary>"walk.glb" → "walk.blazma.json" in the same folder.</summary>
    public static string SidecarPathFor(string glbPath) =>
        Path.Combine(Path.GetDirectoryName(glbPath) ?? "", Path.GetFileNameWithoutExtension(glbPath) + ".blazma.json");

    // ---- imported characters ---------------------------------------------------------------------

    static (GltfBuilder, NodeBinding[]) PrepareImported(Project project, Rig rig, List<string> warnings)
    {
        var glb = OriginalGlb(project, rig);
        var builder = new GltfBuilder(glb.Json, glb.Bin);
        var nodes = GltfAnimationReader.ReadNodes(glb.Json);
        var bindings = BindImported(rig, nodes);

        // glTF forbids "matrix" on animated nodes: rewrite those as the equivalent TRS.
        var nodeArray = (JsonArray)glb.Json["nodes"]!;
        foreach (var b in bindings)
        {
            var n = (JsonObject)nodeArray[b.Node]!;
            if (n["matrix"] is null) continue;
            n.Remove("matrix");
            var l = nodes[b.Node].Local;
            n["translation"] = Vec(l.Translation);
            n["rotation"] = Q(l.Rotation);
            n["scale"] = Vec(l.Scale);
            warnings.Add($"Node {b.Node} ('{nodes[b.Node].Name}') used a matrix; it was rewritten as translation/rotation/scale so it can be animated.");
        }
        return (builder, bindings);
    }

    /// <summary>The embedded original file of an imported rig, parsed. Only GLB with embedded buffers is supported.</summary>
    public static GlbContainer OriginalGlb(Project project, Rig rig)
    {
        var asset = project.FindAsset(rig.Source.Asset)
            ?? throw new ExportException("missing_asset", $"The model file of rig '{rig.Name}' is not in the project.");
        if (!asset.FileName.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            throw new ExportException("unsupported_source", $"'{asset.FileName}' is not a .glb file; only characters imported from .glb can be exported.");
        var glb = GlbContainer.Parse(asset.Data);
        var buffers = glb.Json["buffers"] as JsonArray ?? new JsonArray();
        for (var i = 0; i < buffers.Count; i++)
        {
            var uri = (string?)buffers[i]?["uri"];
            if (i == 0 && uri is null) continue;
            if (uri is null || !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                throw new ExportException("unsupported_source", $"'{asset.FileName}' references external buffer files; re-export it as a self-contained .glb.");
        }
        if (buffers.Count > 0 && (string?)buffers[0]?["uri"] is not null)
            throw new ExportException("unsupported_source", $"'{asset.FileName}' keeps its first buffer outside the GLB binary chunk.");
        return glb;
    }

    /// <summary>Bone → node binding of an imported rig; root bones under non-bone nodes get their ancestors' world transform.</summary>
    public static NodeBinding[] BindImported(Rig rig, IReadOnlyList<GltfNodeInfo> nodes)
    {
        var result = new NodeBinding[rig.Bones.Count];
        for (var i = 0; i < rig.Bones.Count; i++)
        {
            var bone = rig.Bones[i];
            if (!rig.Source.NodeIndex.TryGetValue(bone.Id.ToString(), out var node) || node < 0 || node >= nodes.Count)
                throw new ExportException("missing_node", $"Bone '{bone.Name}' has no node in the original file.");
            Transform? parentWorld = null;
            if (bone.Parent.IsEmpty && nodes[node].Parent is int p)
                parentWorld = GltfAnimationReader.WorldOf(nodes, p);
            result[i] = new NodeBinding(node, parentWorld);
        }
        return result;
    }

    // ---- built-in mannequin ----------------------------------------------------------------------

    static (GltfBuilder, NodeBinding[]) BuildMannequin(Rig rig)
    {
        var json = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = Generator },
            ["scene"] = 0,
        };
        var b = new GltfBuilder(json);
        var nodes = b.Arr("nodes");
        var ev = new RigEvaluator(rig);
        var children = new List<int>[rig.Bones.Count];
        for (var i = 0; i < rig.Bones.Count; i++)
        {
            children[i] = new List<int>();
            var rest = rig.Bones[i].Rest;
            nodes.Add(new JsonObject
            {
                ["name"] = rig.Bones[i].Name,
                ["translation"] = Vec(rest.Translation),
                ["rotation"] = Q(rest.Rotation),
                ["scale"] = Vec(rest.Scale),
            });
        }
        var roots = new JsonArray();
        for (var i = 0; i < rig.Bones.Count; i++)
        {
            if (ev.ParentOf(i) is var p and >= 0) children[p].Add(i);
            else roots.Add(i);
        }

        b.Arr("materials").Add(new JsonObject
        {
            ["name"] = "Mannequin",
            ["pbrMetallicRoughness"] = new JsonObject
            {
                ["baseColorFactor"] = new JsonArray(0.72, 0.74, 0.78, 1.0), ["metallicFactor"] = 0.0, ["roughnessFactor"] = 0.6,
            },
        });
        var mesh = AddUnitBox(b);

        // One rigid box per bone → child segment, and a small block for leaf bones (head, hands, toes).
        for (var i = 0; i < rig.Bones.Count; i++)
        {
            var bone = rig.Bones[i];
            var segments = children[i].Select(c => rig.Bones[c].Rest.Translation).Where(t => t.Length() > 1e-4).ToList();
            var isRoot = ev.ParentOf(i) < 0;
            if (isRoot) segments.Clear(); // the root sits on the floor; its offset to the pelvis is not a limb
            var std = rig.StandardNameOf(bone.Id) ?? bone.Name;
            if (segments.Count == 0 && !isRoot)
                segments.Add(LeafExtent(std, rig, i));
            for (var k = 0; k < segments.Count; k++)
            {
                var seg = segments[k];
                var len = seg.Length();
                var width = Width(std, len);
                var mi = GltfBuilder.Add(nodes, new JsonObject
                {
                    ["name"] = $"{bone.Name}_mesh{(segments.Count > 1 ? "_" + k : "")}",
                    ["mesh"] = mesh,
                    ["translation"] = Vec(seg * 0.5),
                    ["rotation"] = Q(Quat.FromTo(Vec3.UnitY, seg)),
                    ["scale"] = Vec(new Vec3(width, len, width)),
                });
                children[i].Add(mi);
            }
        }
        for (var i = 0; i < rig.Bones.Count; i++)
            if (children[i].Count > 0)
                nodes[i]!["children"] = new JsonArray(children[i].Select(c => (JsonNode?)c).ToArray());
        json["scenes"] = new JsonArray(new JsonObject { ["name"] = rig.Name, ["nodes"] = roots });

        var bindings = Enumerable.Range(0, rig.Bones.Count).Select(i => new NodeBinding(i, null)).ToArray();
        return (b, bindings);
    }

    static Vec3 LeafExtent(string standard, Rig rig, int bone)
    {
        // Continue in the direction of the bone (parent → bone), or up for a bone with no direction.
        var dir = rig.Bones[bone].Rest.Translation.Normalized();
        if (dir == Vec3.Zero) dir = Vec3.UnitY;
        return standard switch
        {
            HumanoidStandard.Head => Vec3.UnitY * 0.22,
            _ when standard.StartsWith("hand", StringComparison.Ordinal) => dir * 0.16,
            _ when standard.StartsWith("toe", StringComparison.Ordinal) => Vec3.UnitZ * 0.06,
            _ => dir * 0.08,
        };
    }

    static double Width(string standard, double length) => standard switch
    {
        HumanoidStandard.Pelvis or HumanoidStandard.Spine1 or HumanoidStandard.Spine2 => 0.22,
        HumanoidStandard.Chest => 0.12,
        HumanoidStandard.Head => 0.18,
        HumanoidStandard.Neck => 0.08,
        _ => Math.Clamp(length * 0.28, 0.05, 0.13),
    };

    /// <summary>Box from (-0.5, 0, -0.5) to (0.5, 1, 0.5) with flat normals (24 vertices).</summary>
    static int AddUnitBox(GltfBuilder b)
    {
        var pos = new List<float>();
        var nrm = new List<float>();
        var idx = new List<int>();
        void Face(Vec3 n, Vec3 u, Vec3 v)
        {
            var c = new Vec3(0, 0.5, 0) + n * 0.5;
            var start = pos.Count / 3;
            foreach (var (su, sv) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
            {
                var p = c + u * (0.5 * su) + v * (0.5 * sv);
                pos.AddRange(new[] { (float)p.X, (float)p.Y, (float)p.Z });
                nrm.AddRange(new[] { (float)n.X, (float)n.Y, (float)n.Z });
            }
            // u × v = n, so (0,1,2) is counter-clockwise seen from outside.
            idx.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
        }
        Face(Vec3.UnitX, -Vec3.UnitZ, Vec3.UnitY);
        Face(-Vec3.UnitX, Vec3.UnitZ, Vec3.UnitY);
        Face(Vec3.UnitY, Vec3.UnitX, -Vec3.UnitZ);
        Face(-Vec3.UnitY, Vec3.UnitX, Vec3.UnitZ);
        Face(Vec3.UnitZ, Vec3.UnitX, Vec3.UnitY);
        Face(-Vec3.UnitZ, -Vec3.UnitX, Vec3.UnitY);
        var position = b.AddFloats(pos.ToArray(), "VEC3", minMax: true, GltfBuilder.ArrayBuffer);
        var normal = b.AddFloats(nrm.ToArray(), "VEC3", minMax: false, GltfBuilder.ArrayBuffer);
        var indices = b.AddIndices(idx.ToArray(), pos.Count / 3);
        return GltfBuilder.Add(b.Arr("meshes"), new JsonObject
        {
            ["name"] = "MannequinSegment",
            ["primitives"] = new JsonArray(new JsonObject
            {
                ["attributes"] = new JsonObject { ["POSITION"] = position, ["NORMAL"] = normal },
                ["indices"] = indices,
                ["material"] = 0,
                ["mode"] = 4,
            }),
        });
    }

    // ---- animation -------------------------------------------------------------------------------

    static string UniqueAnimationName(JsonObject json, string name)
    {
        var used = (json["animations"] as JsonArray ?? new JsonArray()).Select(a => (string?)a?["name"]).Where(n => n is not null).ToHashSet();
        var unique = name;
        for (var k = 2; used.Contains(unique); k++) unique = $"{name}_{k}";
        return unique;
    }

    /// <summary>Expected node-local transform of every bone on every baked frame.</summary>
    /// <remarks>When no bone needs converting (built-in rigs, imported roots at scene level) the baked frames are returned as they are, not copied.</remarks>
    public static Transform[][] NodeLocals(BakedClip baked, NodeBinding[] bindings)
    {
        if (bindings.All(b => b.ParentWorld is null)) return baked.Locals;
        return baked.Locals.Select(frame =>
        {
            var copy = (Transform[])frame.Clone();
            for (var i = 0; i < bindings.Length; i++)
                if (bindings[i].ParentWorld is not null) copy[i] = bindings[i].ToNodeLocal(frame[i]);
            return copy;
        }).ToArray();
    }

    static int AddAnimation(GltfBuilder b, BakedClip baked, NodeBinding[] bindings, string name, bool compact, IReadOnlyList<ExportedProp>? props)
    {
        var locals = NodeLocals(baked, bindings);
        var n = baked.FrameCount;
        var times = new float[n];
        for (var i = 0; i < n; i++) times[i] = (float)baked.SecondsAt(i);
        int? fullInput = null, shortInput = null;
        int Input(bool constant)
        {
            if (constant && n > 1) return shortInput ??= b.AddFloats(new[] { times[0], times[^1] }, "SCALAR", minMax: true);
            return fullInput ??= b.AddFloats(times, "SCALAR", minMax: true);
        }

        var samplers = new JsonArray();
        var channels = new JsonArray();
        for (var bone = 0; bone < bindings.Length; bone++)
        {
            var bi = bone;
            Channels(bindings[bone].Node, f => locals[f][bi]);
        }
        foreach (var prop in props ?? Array.Empty<ExportedProp>()) Channels(prop.Node, f => prop.Locals[f]);

        void Channels(int node, Func<int, Transform> at)
        {
            foreach (var path in new[] { "translation", "rotation", "scale" })
            {
                var comps = path == "rotation" ? 4 : 3;
                var values = new float[n * comps];
                Quat previous = default;
                for (var f = 0; f < n; f++)
                {
                    var t = at(f);
                    double[] v;
                    if (path == "rotation")
                    {
                        var q = t.Rotation.Normalized();
                        if (f > 0 && Quat.Dot(q, previous) < 0) q = new Quat(-q.X, -q.Y, -q.Z, -q.W); // keep the sign continuous for LINEAR
                        previous = q;
                        v = new[] { q.X, q.Y, q.Z, q.W };
                    }
                    else
                    {
                        var x = path == "translation" ? t.Translation : t.Scale;
                        v = new[] { x.X, x.Y, x.Z };
                    }
                    for (var c = 0; c < comps; c++) values[f * comps + c] = (float)v[c];
                }
                var constant = compact && IsConstant(values, comps);
                if (constant && n > 1) values = values[..comps].Concat(values[..comps]).ToArray();
                var sampler = GltfBuilder.Add(samplers, new JsonObject
                {
                    ["input"] = Input(constant),
                    ["output"] = b.AddFloats(values, comps == 4 ? "VEC4" : "VEC3", minMax: false),
                    ["interpolation"] = "LINEAR",
                });
                channels.Add(new JsonObject
                {
                    ["sampler"] = sampler,
                    ["target"] = new JsonObject { ["node"] = node, ["path"] = path },
                });
            }
        }
        var anim = new JsonObject
        {
            ["name"] = name,
            ["channels"] = channels,
            ["samplers"] = samplers,
            ["extras"] = new JsonObject
            {
                ["blazma"] = new JsonObject
                {
                    ["clipId"] = baked.Clip.Id.ToString(),
                    ["fps"] = baked.Fps.ToString(),
                    ["startFrame"] = baked.StartFrame,
                    ["endFrame"] = baked.EndFrame,
                    ["rootMotion"] = RootMotionModes.Name(baked.RootMotion.Mode),
                },
            },
        };
        if (props is { Count: > 0 }) ((JsonObject)anim["extras"]!["blazma"]!)["props"] = props.Count;
        return GltfBuilder.Add(b.Arr("animations"), anim);
    }

    static bool IsConstant(float[] values, int comps)
    {
        for (var i = comps; i < values.Length; i++)
            if (Math.Abs(values[i] - values[i % comps]) > 1e-7f) return false;
        return true;
    }

    internal static JsonArray Vec(Vec3 v) => new(v.X, v.Y, v.Z);
    internal static JsonArray Q(Quat q) { q = q.Normalized(); return new JsonArray(q.X, q.Y, q.Z, q.W); }
}
