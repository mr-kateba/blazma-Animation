using System.Text.Json.Nodes;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Rigging;

namespace Blazma.Export;

public sealed class ExportValidationReport
{
    public bool Ok => Issues.Count == 0;
    public List<string> Issues { get; } = new();
    public string? AnimationName { get; set; }
    public int FramesChecked { get; set; }
    public int ChannelsChecked { get; set; }
    public double DurationSeconds { get; set; }
    public double MaxPositionError { get; set; }
    public double MaxRotationErrorDegrees { get; set; }
    public double MaxScaleError { get; set; }

    public JsonObject ToJson(int maxIssues = 20) => new()
    {
        ["ok"] = Ok,
        ["animation"] = AnimationName,
        ["framesChecked"] = FramesChecked,
        ["channelsChecked"] = ChannelsChecked,
        ["durationSeconds"] = DurationSeconds,
        ["maxPositionError"] = MaxPositionError,
        ["maxRotationErrorDegrees"] = MaxRotationErrorDegrees,
        ["maxScaleError"] = MaxScaleError,
        ["issueCount"] = Issues.Count,
        ["issues"] = new JsonArray(Issues.Take(maxIssues).Select(i => (JsonNode?)i).ToArray()),
    };
}

/// <summary>
/// Re-reads an exported GLB and checks it against a fresh bake of the clip: container and glTF
/// structure, node hierarchy, animation duration, and every bone's local TRS on every frame
/// (tolerances: <see cref="PositionTolerance"/> m, <see cref="RotationToleranceDegrees"/>°, <see cref="ScaleTolerance"/>).
/// </summary>
public static class ExportValidator
{
    public const double PositionTolerance = 1e-4;
    public const double RotationToleranceDegrees = 0.01;
    public const double ScaleTolerance = 1e-4;

    public static ExportValidationReport Validate(string glbPath, Project project, Clip clip, Rig rig, ExportOptions? options = null)
    {
        var report = new ExportValidationReport();
        try { Check(File.ReadAllBytes(glbPath), project, clip, rig, options, report); }
        catch (ExportException e) { report.Issues.Add($"{e.Code}: {e.Message}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { report.Issues.Add("io_error: " + e.Message); }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or InvalidCastException)
        {
            report.Issues.Add("invalid_gltf: " + e.Message); // a JSON value of the wrong kind
        }
        return report;
    }

    public static ExportValidationReport Validate(byte[] glbBytes, Project project, Clip clip, Rig rig, ExportOptions? options = null)
    {
        var report = new ExportValidationReport();
        try { Check(glbBytes, project, clip, rig, options, report); }
        catch (ExportException e) { report.Issues.Add($"{e.Code}: {e.Message}"); }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or InvalidCastException)
        {
            report.Issues.Add("invalid_gltf: " + e.Message);
        }
        return report;
    }

    /// <param name="options">The options the file was exported with; null reads them from the animation's
    /// "extras.blazma" (frame range and root motion), as written by <see cref="AnimationExporter"/>.</param>
    static void Check(byte[] bytes, Project project, Clip clip, Rig rig, ExportOptions? options, ExportValidationReport report)
    {
        var glb = GlbContainer.Parse(bytes);
        var issues = report.Issues;
        issues.AddRange(glb.Issues);
        CheckStructure(glb, issues);
        var nodes = GltfAnimationReader.ReadNodes(glb.Json);

        // Hierarchy and bone → node binding.
        NodeBinding[] bindings;
        if (rig.Source.Kind == RigSourceKind.Imported)
        {
            var original = AnimationExporter.OriginalGlb(project, rig);
            var originalNodes = GltfAnimationReader.ReadNodes(original.Json);
            if (originalNodes.Count != nodes.Count)
                issues.Add($"Node count {nodes.Count} differs from the original file ({originalNodes.Count}).");
            else
                for (var i = 0; i < nodes.Count; i++)
                    if (nodes[i].Parent != originalNodes[i].Parent || nodes[i].Name != originalNodes[i].Name || nodes[i].Mesh != originalNodes[i].Mesh || nodes[i].Skin != originalNodes[i].Skin)
                        issues.Add($"Node {i} ('{nodes[i].Name}') differs from the original file (parent, name, mesh or skin).");
            foreach (var key in new[] { "meshes", "skins", "materials" })
                if (Count(glb.Json, key) != Count(original.Json, key))
                    issues.Add($"The number of {key} differs from the original file.");
            bindings = AnimationExporter.BindImported(rig, originalNodes);
        }
        else
        {
            bindings = Enumerable.Range(0, rig.Bones.Count).Select(i => new NodeBinding(i, null)).ToArray();
            var ev = new RigEvaluator(rig);
            for (var i = 0; i < rig.Bones.Count; i++)
            {
                if (i >= nodes.Count) { issues.Add($"Bone '{rig.Bones[i].Name}' has no node."); continue; }
                if (nodes[i].Name != rig.Bones[i].Name) issues.Add($"Node {i} is '{nodes[i].Name}', expected bone '{rig.Bones[i].Name}'.");
                var parent = ev.ParentOf(i);
                if (nodes[i].Parent != (parent < 0 ? null : parent)) issues.Add($"Node {i} ('{rig.Bones[i].Name}') has the wrong parent.");
                if (!Close(nodes[i].Local, rig.Bones[i].Rest, out _, out _, out _)) issues.Add($"Node {i} ('{rig.Bones[i].Name}') rest transform differs from the rig.");
            }
        }

        // Animation.
        var animations = GltfAnimationReader.ReadAnimations(glb);
        var anim = animations.LastOrDefault(a => a.BlazmaClipId == clip.Id.ToString())
            ?? animations.LastOrDefault(a => a.Name == (options?.AnimationName ?? clip.Name));
        if (anim is null) { issues.Add($"No animation for clip '{clip.Name}' in the file."); return; }
        options ??= OptionsFrom(anim);
        report.AnimationName = anim.Name;
        report.DurationSeconds = anim.Duration;

        var baked = AnimationBaker.Bake(clip, rig, options);
        var expected = AnimationExporter.NodeLocals(baked, bindings);
        var halfFrame = 0.5 * baked.Fps.Denominator / baked.Fps.Numerator;
        if (Math.Abs(anim.Duration - baked.DurationSeconds) > halfFrame)
            issues.Add($"Animation lasts {anim.Duration:0.####} s; expected {baked.DurationSeconds:0.####} s.");

        var byTarget = anim.Channels.GroupBy(c => (c.Node, c.Path)).ToDictionary(g => g.Key, g => g.Last());
        foreach (var g in anim.Channels.GroupBy(c => (c.Node, c.Path)).Where(g => g.Count() > 1))
            issues.Add($"Node {g.Key.Node} has {g.Count()} '{g.Key.Path}' channels in one animation.");

        double maxP = 0, maxR = 0, maxS = 0;
        for (var bone = 0; bone < bindings.Length; bone++)
        {
            var node = bindings[bone].Node;
            var name = rig.Bones[bone].Name;
            foreach (var path in new[] { "translation", "rotation", "scale" })
            {
                if (!byTarget.TryGetValue((node, path), out var ch)) { issues.Add($"Bone '{name}' has no {path} channel."); continue; }
                report.ChannelsChecked++;
                if (Math.Abs(ch.Duration - baked.DurationSeconds) > halfFrame)
                    issues.Add($"Bone '{name}' {path} ends at {ch.Duration:0.####} s; expected {baked.DurationSeconds:0.####} s.");
                double worst = 0;
                var worstFrame = 0;
                for (var f = 0; f < baked.FrameCount; f++)
                {
                    var v = ch.Sample(baked.SecondsAt(f));
                    var e = expected[f][bone];
                    var err = path switch
                    {
                        "translation" => (new Vec3(v[0], v[1], v[2]) - e.Translation).Length(),
                        "scale" => (new Vec3(v[0], v[1], v[2]) - e.Scale).Length(),
                        _ => Quat.AngleBetween(new Quat(v[0], v[1], v[2], v[3]).Normalized(), e.Rotation.Normalized()) * 180 / Math.PI,
                    };
                    if (err > worst) { worst = err; worstFrame = f; }
                }
                var tol = path switch { "translation" => PositionTolerance, "scale" => ScaleTolerance, _ => RotationToleranceDegrees };
                if (worst > tol) issues.Add($"Bone '{name}' {path} differs by {worst:G3} at frame {baked.StartFrame + worstFrame} (tolerance {tol:G3}).");
                switch (path)
                {
                    case "translation": maxP = Math.Max(maxP, worst); break;
                    case "scale": maxS = Math.Max(maxS, worst); break;
                    default: maxR = Math.Max(maxR, worst); break;
                }
            }
        }
        report.FramesChecked = baked.FrameCount;
        report.MaxPositionError = maxP;
        report.MaxRotationErrorDegrees = maxR;
        report.MaxScaleError = maxS;
    }

    /// <summary>Export options recorded in an animation's extras by this exporter (defaults when absent).</summary>
    public static ExportOptions OptionsFrom(GltfAnimation anim)
    {
        var x = anim.Extras?["blazma"];
        RootMotionModes.TryParse((string?)x?["rootMotion"], out var mode);
        return new ExportOptions { StartFrame = GltfAnimationReader.Long(x?["startFrame"]), EndFrame = GltfAnimationReader.Long(x?["endFrame"]), RootMotion = mode, AnimationName = anim.Name };
    }

    static int Count(JsonObject json, string key) => json[key] is JsonArray a ? a.Count : 0;

    static bool Close(Transform a, Transform b, out double p, out double rDeg, out double s)
    {
        p = (a.Translation - b.Translation).Length();
        s = (a.Scale - b.Scale).Length();
        rDeg = Quat.AngleBetween(a.Rotation.Normalized(), b.Rotation.Normalized()) * 180 / Math.PI;
        return p <= PositionTolerance && s <= ScaleTolerance && rDeg <= RotationToleranceDegrees;
    }

    /// <summary>glTF 2.0 structural rules this exporter relies on (not a full Khronos validator).</summary>
    public static void CheckStructure(GlbContainer glb, List<string> issues)
    {
        var json = glb.Json;
        if ((string?)json["asset"]?["version"] is not { } version || !version.StartsWith("2.", StringComparison.Ordinal))
            issues.Add("asset.version must be 2.x.");

        var buffers = json["buffers"] as JsonArray ?? new JsonArray();
        var bufferLengths = new long[buffers.Count];
        for (var i = 0; i < buffers.Count; i++)
        {
            var declared = GltfAnimationReader.Long(buffers[i]?["byteLength"]);
            if (declared is null or < 1) { issues.Add($"buffers[{i}] has no valid byteLength."); continue; }
            bufferLengths[i] = declared.Value;
            if (i == 0 && buffers[i]?["uri"] is null)
            {
                if (!glb.HasBin) issues.Add("buffers[0] has no uri but the GLB has no BIN chunk.");
                else if (glb.Bin.Length < declared || glb.Bin.Length - declared > 3)
                    issues.Add($"BIN chunk is {glb.Bin.Length} bytes but buffers[0].byteLength is {declared}.");
            }
        }

        var views = json["bufferViews"] as JsonArray ?? new JsonArray();
        for (var i = 0; i < views.Count; i++)
        {
            var v = views[i] as JsonObject;
            var buffer = GltfAnimationReader.Int(v?["buffer"]) ?? -1;
            var off = GltfAnimationReader.Long(v?["byteOffset"]) ?? 0;
            var len = GltfAnimationReader.Long(v?["byteLength"]) ?? 0;
            if (buffer < 0 || buffer >= buffers.Count) { issues.Add($"bufferViews[{i}] references missing buffer {buffer}."); continue; }
            if (off < 0 || len < 1 || off + len > bufferLengths[buffer]) issues.Add($"bufferViews[{i}] (offset {off}, length {len}) is outside buffer {buffer}.");
            if (GltfAnimationReader.Int(v?["byteStride"]) is int stride && (stride < 4 || stride > 252 || stride % 4 != 0)) issues.Add($"bufferViews[{i}] has invalid byteStride {stride}.");
        }

        var accessors = json["accessors"] as JsonArray ?? new JsonArray();
        for (var i = 0; i < accessors.Count; i++)
        {
            var a = accessors[i] as JsonObject;
            if (a is null) { issues.Add($"accessors[{i}] is not an object."); continue; }
            if (a["bufferView"] is null) continue;
            var view = GltfAnimationReader.Int(a["bufferView"]) ?? -1;
            if (view < 0 || view >= views.Count) { issues.Add($"accessors[{i}] references missing bufferView {view}."); continue; }
            int size, comps;
            try { size = GltfAnimationReader.ComponentSize(GltfAnimationReader.Int(a["componentType"]) ?? -1); comps = GltfBuilder.Components((string)a["type"]!); }
            catch (Exception e) when (e is ExportException or ArgumentException or NullReferenceException) { issues.Add($"accessors[{i}] has an invalid type."); continue; }
            var count = GltfAnimationReader.Long(a["count"]) ?? 0;
            var off = GltfAnimationReader.Long(a["byteOffset"]) ?? 0;
            var vlen = GltfAnimationReader.Long(views[view]?["byteLength"]) ?? 0;
            var stride = GltfAnimationReader.Long(views[view]?["byteStride"]) ?? size * comps;
            if (count < 1) issues.Add($"accessors[{i}] has count {count}.");
            else if (off + stride * (count - 1) + size * comps > vlen) issues.Add($"accessors[{i}] reads past the end of bufferView {view}.");
            var vOff = GltfAnimationReader.Long(views[view]?["byteOffset"]) ?? 0;
            if ((off + vOff) % size != 0) issues.Add($"accessors[{i}] is not aligned to its component size.");
        }

        var meshes = json["meshes"] as JsonArray ?? new JsonArray();
        for (var m = 0; m < meshes.Count; m++)
            foreach (var prim in meshes[m]?["primitives"] as JsonArray ?? new JsonArray())
                if (GltfAnimationReader.Int(prim?["attributes"]?["POSITION"]) is int pos && pos >= 0 && pos < accessors.Count
                    && (accessors[pos]?["min"] is not JsonArray || accessors[pos]?["max"] is not JsonArray))
                    issues.Add($"meshes[{m}] POSITION accessor {pos} has no min/max.");

        var nodes = json["nodes"] as JsonArray ?? new JsonArray();
        var parent = new int?[nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i] as JsonObject;
            foreach (var c in n?["children"] as JsonArray ?? new JsonArray())
            {
                var ci = GltfAnimationReader.Int(c) ?? -1;
                if (ci < 0 || ci >= nodes.Count) issues.Add($"nodes[{i}] has missing child {ci}.");
                else if (parent[ci] is not null) issues.Add($"Node {ci} has two parents.");
                else parent[ci] = i;
            }
            if (GltfAnimationReader.Int(n?["mesh"]) is int mesh && (mesh < 0 || mesh >= meshes.Count)) issues.Add($"nodes[{i}] references missing mesh {mesh}.");
        }
        for (var i = 0; i < nodes.Count; i++)
        {
            var steps = 0;
            for (var p = parent[i]; p is int pi; p = parent[pi])
                if (++steps > nodes.Count) { issues.Add($"Node hierarchy has a cycle through node {i}."); break; }
        }

        var animations = json["animations"] as JsonArray ?? new JsonArray();
        for (var a = 0; a < animations.Count; a++)
        {
            var samplers = animations[a]?["samplers"] as JsonArray ?? new JsonArray();
            for (var s = 0; s < samplers.Count; s++)
            {
                var input = GltfAnimationReader.Int(samplers[s]?["input"]) ?? -1;
                var output = GltfAnimationReader.Int(samplers[s]?["output"]) ?? -1;
                var interp = (string?)samplers[s]?["interpolation"] ?? "LINEAR";
                if (interp is not ("LINEAR" or "STEP" or "CUBICSPLINE")) issues.Add($"animations[{a}].samplers[{s}] has unknown interpolation '{interp}'.");
                if (input < 0 || input >= accessors.Count || output < 0 || output >= accessors.Count)
                {
                    issues.Add($"animations[{a}].samplers[{s}] references a missing accessor.");
                    continue;
                }
                var inAcc = accessors[input]!;
                if (inAcc["min"] is not JsonArray || inAcc["max"] is not JsonArray) issues.Add($"animations[{a}].samplers[{s}] input accessor {input} has no min/max.");
                float[] times;
                try { times = GltfAnimationReader.ReadFloats(glb, input, out _); }
                catch (ExportException e) { issues.Add($"animations[{a}].samplers[{s}] input: {e.Message}"); continue; }
                for (var k = 1; k < times.Length; k++)
                    if (!(times[k] > times[k - 1])) { issues.Add($"animations[{a}].samplers[{s}] times are not strictly increasing."); break; }
                if (times.Length > 0 && times[0] < 0) issues.Add($"animations[{a}].samplers[{s}] has negative times.");
                if (inAcc["min"] is JsonArray min && inAcc["max"] is JsonArray max && times.Length > 0
                    && ((float)min[0]! != times.Min() || (float)max[0]! != times.Max()))
                    issues.Add($"animations[{a}].samplers[{s}] input min/max do not match the data.");
                var outCount = GltfAnimationReader.Long(accessors[output]?["count"]) ?? 0;
                var keys = Math.Max(times.Length, 1) * (interp == "CUBICSPLINE" ? 3L : 1L);
                if (outCount < keys || outCount % keys != 0)
                    issues.Add($"animations[{a}].samplers[{s}] output count {outCount} does not match {times.Length} keys.");
            }
            foreach (var ch in animations[a]?["channels"] as JsonArray ?? new JsonArray())
            {
                var si = GltfAnimationReader.Int(ch?["sampler"]) ?? -1;
                if (si < 0 || si >= samplers.Count) issues.Add($"animations[{a}] has a channel with missing sampler {si}.");
                if (GltfAnimationReader.Int(ch?["target"]?["node"]) is int node)
                {
                    if (node < 0 || node >= nodes.Count) issues.Add($"animations[{a}] targets missing node {node}.");
                    else if (nodes[node]?["matrix"] is not null) issues.Add($"animations[{a}] animates node {node}, which uses a matrix.");
                }
                if ((string?)ch?["target"]?["path"] is not ("translation" or "rotation" or "scale" or "weights" or "pointer"))
                    issues.Add($"animations[{a}] has a channel with an invalid path.");
            }
        }
    }
}
