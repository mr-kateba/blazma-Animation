using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Agent;
using Blazma.Commands;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Export;
using Blazma.Motion;
using Blazma.Rigging;
using Blazma.Tests.Persistence;
using Blazma.Tests.Rigging.Import;
using TempDir = Blazma.Tests.Persistence.TempDir;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.Export;

/// <summary>Shared fixtures: a walk-like mannequin clip (root travel, pinned feet re-solved by IK) and an imported character.</summary>
static class ExportFixture
{
    public static readonly CommandRegistry Registry = CommandRegistry.CreateDefault();

    public static CommandResult Run(ProjectSession s, string id, object args, CommandOrigin? origin = null) =>
        s.Execute(Registry.Create(id, A(args)), origin ?? CommandOrigin.Test);

    public static JsonObject A(object o) => JsonNode.Parse(JsonSerializer.Serialize(o))!.AsObject();

    static void Ok(CommandResult r) => Assert.True(r.Ok, r.Error?.Message);

    /// <summary>A step (keys 0, 12, 24): the root travels 0.6 m forward and bobs, the pelvis sways, both feet are
    /// pinned IK targets; the left foot stays planted until frame 12.</summary>
    public static (ProjectSession s, string ch, string clip) Walk()
    {
        var s = new ProjectSession(Project.CreateNew("Export"));
        var ch = (string)Run(s, "scene.add_mannequin", new { }).Data!["characterId"]!;
        var clip = (string)Run(s, "clip.create", new { name = "Walk", durationFrames = 24 }).Data!["clipId"]!;
        void Pose(double rootY, double rootZ, double pelvisRy, (double y, double z) footL, (double y, double z) footR)
        {
            Ok(Run(s, "pose.set_bone", new { characterId = ch, bone = H.Root, rx = 0, ry = 0, rz = 0, tx = 0, ty = rootY, tz = rootZ }));
            Ok(Run(s, "pose.set_bone", new { characterId = ch, bone = H.Pelvis, rx = 0, ry = pelvisRy, rz = 0, ty = 0.9 }));
            Ok(Run(s, "pose.set_control", new { characterId = ch, control = "ik_foot_l", x = 0.09, y = footL.y, z = footL.z, pinned = true }));
            Ok(Run(s, "pose.set_control", new { characterId = ch, control = "ik_foot_r", x = -0.09, y = footR.y, z = footR.z, pinned = true }));
        }
        Pose(0, 0, -8, (0.06, 0.15), (0.06, -0.15));
        Ok(Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 0 }));
        Pose(0.01, 0.3, 0, (0.06, 0.15), (0.16, 0.3));
        Ok(Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 12, interpolation = "linear" }));
        Pose(0, 0.6, 8, (0.06, 0.75), (0.06, 0.45));
        Ok(Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 24 }));
        Ok(Run(s, "marker.add", new { clipId = clip, frame = 0, name = "Start", phase = "initial" }));
        Ok(Run(s, "marker.add", new { clipId = clip, frame = 12, name = "Pass" }));
        var c = s.Project.Clips[0];
        c.Events.Add(new AnimationEvent { Time = Flicks.FromFrame(6, c.Fps), Name = "foot_contact_r", Data = { ["surface"] = "stone" } });
        return (s, ch, clip);
    }

    public static (ProjectSession s, string ch, string clip) Imported(string model)
    {
        var s = new ProjectSession(Project.CreateNew("Imported"));
        var r = Run(s, "rig.import", new { path = RepoPaths.Sample($"models/khronos/{model}.glb") });
        Ok(r);
        var ch = (string)r.Data!["characterId"]!;
        var clip = (string)Run(s, "clip.create", new { name = "Wave", durationFrames = 20 }).Data!["clipId"]!;
        var rig = s.Project.Rigs.Single(x => x.Source.Kind == RigSourceKind.Imported);
        Ok(Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 0 }));
        // Rotate the root bone (whose rest folds the file's Z-up ancestors) and move it, plus a child bone.
        var root = rig.Bones[0].Name;
        var rest = rig.Bones[0].Rest.Translation;
        Ok(Run(s, "pose.set_bone", new { characterId = ch, bone = root, rx = 20, ry = 35, rz = -10, tx = rest.X + 0.1, ty = rest.Y + 0.05, tz = rest.Z + 0.2 }));
        if (rig.Bones.Count > 2)
            Ok(Run(s, "pose.set_bone", new { characterId = ch, bone = rig.Bones[2].Name, rx = 25, ry = 0, rz = 40 }));
        Ok(Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 20 }));
        return (s, ch, clip);
    }

    public static Rig RigOf(ProjectSession s, string ch) => s.Project.GetRig(s.Project.GetCharacter(Blazma.Core.Ids.ObjectId.Parse(ch)).Rig);
    public static Clip ClipOf(ProjectSession s, string clip) => s.Project.GetClip(Blazma.Core.Ids.ObjectId.Parse(clip));
}

public class GlbStructureTests
{
    [Fact]
    public void Glb_HasHeader_AlignedChunks_AndMinMaxWhereRequired()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        var result = AnimationExporter.Export(s.Project, ExportFixture.ClipOf(s, clip), ExportFixture.RigOf(s, ch));
        var b = result.Glb;

        Assert.Equal(GlbContainer.Magic, BinaryPrimitives.ReadUInt32LittleEndian(b));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)));
        Assert.Equal((uint)b.Length, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)));
        Assert.Equal(0, b.Length % 4);
        var jsonLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12));
        Assert.Equal(GlbContainer.ChunkJson, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16)));
        Assert.Equal(0, jsonLen % 4);
        Assert.Equal((byte)' ', b[20 + jsonLen - 1]); // JSON padding is spaces
        var binLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(20 + jsonLen));
        Assert.Equal(GlbContainer.ChunkBin, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(24 + jsonLen)));
        Assert.Equal(0, binLen % 4);
        Assert.Equal(b.Length, 28 + jsonLen + binLen);

        var glb = GlbContainer.Parse(b);
        Assert.Empty(glb.Issues);
        var json = glb.Json;
        Assert.Equal("Blazma Animation", (string?)json["asset"]!["generator"]);
        Assert.Equal("2.0", (string?)json["asset"]!["version"]);
        Assert.Equal(binLen, (int)json["buffers"]![0]!["byteLength"]!);
        foreach (var v in json["bufferViews"]!.AsArray()) Assert.Equal(0, (int)v!["byteOffset"]! % 4);

        var accessors = json["accessors"]!.AsArray();
        var pos = (int)json["meshes"]![0]!["primitives"]![0]!["attributes"]!["POSITION"]!;
        Assert.Equal(new[] { -0.5f, 0f, -0.5f }, accessors[pos]!["min"]!.AsArray().Select(x => (float)x!));
        Assert.Equal(new[] { 0.5f, 1f, 0.5f }, accessors[pos]!["max"]!.AsArray().Select(x => (float)x!));
        var anim = json["animations"]![0]!;
        foreach (var sampler in anim["samplers"]!.AsArray())
        {
            var input = accessors[(int)sampler!["input"]!]!;
            Assert.Equal(0f, (float)input["min"]![0]!);
            Assert.Equal(25 / 30f, (float)input["max"]![0]!); // key at frame 24 made the clip 25 frames; frames 0..25 are baked
            Assert.Equal("LINEAR", (string?)sampler["interpolation"]);
        }
        var issues = new List<string>();
        ExportValidator.CheckStructure(glb, issues);
        Assert.Empty(issues);
    }

    [Fact]
    public void Structure_Check_Catches_BadReferences_And_TruncatedFiles()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        var good = AnimationExporter.Export(s.Project, ExportFixture.ClipOf(s, clip), ExportFixture.RigOf(s, ch)).Glb;
        Assert.Equal("invalid_glb", Assert.Throws<ExportException>(() => GlbContainer.Parse(good[..^8])).Code);
        var bad = good.ToArray();
        bad[0] = (byte)'X';
        Assert.Equal("invalid_glb", Assert.Throws<ExportException>(() => GlbContainer.Parse(bad)).Code);

        var glb = GlbContainer.Parse(good);
        glb.Json["bufferViews"]![0]!["byteLength"] = 1 << 30;
        glb.Json["animations"]![0]!["channels"]![0]!["target"]!["node"] = 9999;
        glb.Json["accessors"]![(int)glb.Json["animations"]![0]!["samplers"]![0]!["input"]!]!.AsObject().Remove("max");
        var issues = new List<string>();
        ExportValidator.CheckStructure(glb, issues);
        Assert.Contains(issues, i => i.Contains("bufferViews[0]"));
        Assert.Contains(issues, i => i.Contains("missing node 9999"));
        Assert.Contains(issues, i => i.Contains("no min/max"));
    }
}

public class MannequinExportTests
{
    [Fact]
    public void Walk_RoundTrips_WithinTolerance_IncludingPinnedFootIk()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        var rig = ExportFixture.RigOf(s, ch);
        var c = ExportFixture.ClipOf(s, clip);
        Assert.Contains(c.Tracks, t => t.Channel == TrackChannels.Pinned);
        var result = AnimationExporter.Export(s.Project, c, rig);

        var report = ExportValidator.Validate(result.Glb, s.Project, c, rig);
        Assert.True(report.Ok, string.Join("\n", report.Issues));
        Assert.Equal(26, report.FramesChecked);
        Assert.Equal(rig.Bones.Count * 3, report.ChannelsChecked);
        Assert.InRange(report.MaxPositionError, 0, 1e-5);
        Assert.InRange(report.MaxRotationErrorDegrees, 0, 1e-3);
        Assert.Equal(25 / 30.0, report.DurationSeconds, 5);

        // Reading back and running FK over the file's node hierarchy gives the app's pinned-foot positions.
        var glb = GlbContainer.Parse(result.Glb);
        var anim = GltfAnimationReader.ReadAnimations(glb).Single();
        var nodes = GltfAnimationReader.ReadNodes(glb.Json);
        Assert.Equal("Walk", anim.Name);
        var ev = new RigEvaluator(rig);
        var foot = ev.IndexOf(rig.FindBoneByName(H.Foot(Side.Left))!.Id);
        for (var f = 0; f <= 25; f++)
        {
            var world = ImportedExportTests.World(nodes, anim, f / 30.0);
            var app = ev.Model(ClipEvaluator.Evaluate(c, rig, Flicks.FromFrame(f, c.Fps)));
            for (var i = 0; i < rig.Bones.Count; i++)
                Assert.True(world[i].Translation.ApproxEquals(app[i].Translation, 1e-4), $"bone {rig.Bones[i].Name} frame {f}");
            if (f <= 12) Assert.True(world[foot].Translation.ApproxEquals(new Vec3(0.09, 0.06, 0.15), 1e-4), $"left foot slid at frame {f}");
        }
    }

    [Fact]
    public void Mannequin_File_Has_BoneNodes_Meshes_WithNormals_AndOneMaterial()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        var rig = ExportFixture.RigOf(s, ch);
        var glb = GlbContainer.Parse(AnimationExporter.Export(s.Project, ExportFixture.ClipOf(s, clip), rig).Glb);
        var nodes = GltfAnimationReader.ReadNodes(glb.Json);
        Assert.Single(glb.Json["materials"]!.AsArray());
        Assert.NotNull(glb.Json["meshes"]![0]!["primitives"]![0]!["attributes"]!["NORMAL"]);
        var ev = new RigEvaluator(rig);
        for (var i = 0; i < rig.Bones.Count; i++)
        {
            Assert.Equal(rig.Bones[i].Name, nodes[i].Name);
            Assert.Equal(ev.ParentOf(i) < 0 ? null : ev.ParentOf(i), nodes[i].Parent);
        }
        var meshNodes = nodes.Where(n => n.Mesh is not null).ToList();
        Assert.True(meshNodes.Count >= rig.Bones.Count - 1);
        Assert.All(meshNodes, n => Assert.True(n.Parent < rig.Bones.Count));
        Assert.Equal(new[] { 0 }, glb.Json["scenes"]![0]!["nodes"]!.AsArray().Select(x => (int)x!));
    }

    [Fact]
    public void ConstantChannels_AreCompactedToTwoKeys_SpanningTheClip()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        var rig = ExportFixture.RigOf(s, ch);
        var glb = GlbContainer.Parse(AnimationExporter.Export(s.Project, ExportFixture.ClipOf(s, clip), rig).Glb);
        var anim = GltfAnimationReader.ReadAnimations(glb).Single();
        var head = rig.IndexOf(rig.FindBoneByName(H.Head)!.Id);
        var headRot = anim.Channels.Single(c => c.Node == head && c.Path == "rotation");
        Assert.Equal(new[] { 0f, 25 / 30f }, headRot.Times);
        var rootT = anim.Channels.Single(c => c.Node == 0 && c.Path == "translation");
        Assert.Equal(26, rootT.Times.Length);
        Assert.All(anim.Channels, c => Assert.Equal(25 / 30f, c.Times[^1]));

        var full = GlbContainer.Parse(AnimationExporter.Export(s.Project, ExportFixture.ClipOf(s, clip), rig,
            new ExportOptions { CompactConstantChannels = false }).Glb);
        Assert.All(GltfAnimationReader.ReadAnimations(full).Single().Channels, c => Assert.Equal(26, c.Times.Length));
    }

    [Fact]
    public void InPlace_RemovesRootXZ_KeepsY_AndReportsTheDisplacement()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        var rig = ExportFixture.RigOf(s, ch);
        var c = ExportFixture.ClipOf(s, clip);
        var keep = AnimationBaker.Bake(c, rig);
        var inPlace = AnimationBaker.Bake(c, rig, new ExportOptions { RootMotion = RootMotionMode.InPlace });
        var all = AnimationBaker.Bake(c, rig, new ExportOptions { RootMotion = RootMotionMode.InPlaceAll });
        var root = inPlace.RootMotion.BoneIndex;
        Assert.Equal(H.Root, inPlace.RootMotion.BoneName);
        Assert.Contains(keep.Locals, f => f[root].Translation.Y > 0.005);
        for (var f = 0; f < inPlace.FrameCount; f++)
        {
            var t = inPlace.Locals[f][root].Translation;
            Assert.Equal(0, t.X, 9);
            Assert.Equal(0, t.Z, 9);
            Assert.Equal(keep.Locals[f][root].Translation.Y, t.Y, 9);
            Assert.True(all.Locals[f][root].Translation.ApproxEquals(Vec3.Zero, 1e-9));
            // Other bones are untouched.
            Assert.Equal(keep.Locals[f][1], inPlace.Locals[f][1]);
        }
        Assert.True(inPlace.RootMotion.Total.ApproxEquals(new Vec3(0, 0, 0.6), 1e-9));
        Assert.True(inPlace.RootMotion.Deltas.Aggregate(Vec3.Zero, (a, d) => a + d).ApproxEquals(inPlace.RootMotion.Total, 1e-9));
        Assert.True(keep.RootMotion.Total.ApproxEquals(new Vec3(0, 0, 0.6), 1e-9));
        Assert.False(keep.RootMotion.Removed);
        Assert.Equal(0, all.RootMotion.Total.Y, 9); // last frame is back at the start height

        // The exported in-place file validates against an in-place bake.
        var options = new ExportOptions { RootMotion = RootMotionMode.InPlace };
        var result = AnimationExporter.Export(s.Project, c, rig, options);
        var report = ExportValidator.Validate(result.Glb, s.Project, c, rig, options);
        Assert.True(report.Ok, string.Join("\n", report.Issues));
        // Without options the validator reads them from the animation extras.
        Assert.True(ExportValidator.Validate(result.Glb, s.Project, c, rig).Ok);
        // Validated against the travelling bake instead, the root is off by up to 0.6 m.
        Assert.False(ExportValidator.Validate(result.Glb, s.Project, c, rig, new ExportOptions()).Ok);
    }

    [Fact]
    public void FrameRange_StartsAtTimeZero_AndIsChecked()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        var rig = ExportFixture.RigOf(s, ch);
        var c = ExportFixture.ClipOf(s, clip);
        var options = new ExportOptions { StartFrame = 6, EndFrame = 18 };
        var result = AnimationExporter.Export(s.Project, c, rig, options);
        Assert.Equal(13, result.Baked.FrameCount);
        Assert.Equal(12 / 30.0, result.Baked.DurationSeconds, 9);
        Assert.True(ExportValidator.Validate(result.Glb, s.Project, c, rig).Ok);
        Assert.Equal(0, (long)result.Sidecar["events"]![0]!["frame"]!); // event at frame 6
        Assert.Equal(6, (long)result.Sidecar["markers"]![0]!["frame"]!); // "Pass" at 12; "Start" at 0 is outside
        Assert.Equal("invalid_range", Assert.Throws<ExportException>(() => AnimationBaker.Bake(c, rig, new ExportOptions { EndFrame = 26 })).Code);
        Assert.Equal("invalid_range", Assert.Throws<ExportException>(() => AnimationBaker.Bake(c, rig, new ExportOptions { StartFrame = 10, EndFrame = 9 })).Code);
    }

    [Fact]
    public void LoopingClip_EndFrameRepeatsTheLoopStart()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        Assert.True(ExportFixture.Run(s, "clip.set_loop", new { clipId = clip, enabled = true }).Ok);
        var baked = AnimationBaker.Bake(ExportFixture.ClipOf(s, clip), ExportFixture.RigOf(s, ch));
        for (var i = 0; i < baked.Locals[0].Length; i++)
            Assert.Equal(baked.Locals[0][i], baked.Locals[^1][i]);
    }

    [Fact]
    public void Sidecar_Describes_Timing_Loop_Markers_Events_RootMotion_AndBones()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        ExportFixture.Run(s, "clip.set_loop", new { clipId = clip, enabled = true, startFrame = 0, endFrame = 24 });
        var rig = ExportFixture.RigOf(s, ch);
        var result = AnimationExporter.Export(s.Project, ExportFixture.ClipOf(s, clip), rig,
            new ExportOptions { RootMotion = RootMotionMode.InPlace }, "walk.glb");
        var j = result.Sidecar;
        Assert.Equal(Sidecar.Schema, (string?)j["schema"]);
        Assert.Equal(1, (int)j["schemaVersion"]!);
        Assert.Equal("walk.glb", (string?)j["glb"]);
        Assert.Equal("Walk", (string?)j["clip"]!["name"]);
        Assert.Equal("Walk", (string?)j["animation"]);
        Assert.Equal(30, (int)j["fps"]!["numerator"]!);
        Assert.Equal(1, (int)j["fps"]!["denominator"]!);
        Assert.Equal(26, (int)j["frameCount"]!);
        Assert.Equal(25 / 30.0, (double)j["durationSeconds"]!, 9);
        Assert.True((bool)j["loop"]!["enabled"]!);
        Assert.Equal(24, (long)j["loop"]!["endFrame"]!);
        Assert.Equal(new[] { "Start:initial:0", "Pass:marker:12" },
            j["markers"]!.AsArray().Select(m => $"{m!["name"]}:{m["kind"]}:{m["frame"]}"));
        var ev = j["events"]![0]!;
        Assert.Equal("foot_contact_r", (string?)ev["name"]);
        Assert.Equal(6, (long)ev["frame"]!);
        Assert.Equal(0.2, (double)ev["time"]!, 9);
        Assert.Equal("stone", (string?)ev["payload"]!["surface"]);
        Assert.Equal("in_place", (string?)j["rootMotion"]!["mode"]);
        Assert.True((bool)j["rootMotion"]!["removed"]!);
        // Looping: frames 24 and 25 wrap to the loop start (frames 0 and 1), as in playback.
        Assert.Equal(result.Baked.RootMotion.Total.Z, (double)j["rootMotion"]!["totalDisplacement"]![2]!, 6);
        Assert.InRange((double)j["rootMotion"]!["totalDisplacement"]![2]!, 0, 0.01);
        Assert.Equal(26, j["rootMotion"]!["perFrameDelta"]!.AsArray().Count);
        var bones = j["bones"]!.AsArray();
        Assert.Equal(rig.Bones.Count, bones.Count);
        var thigh = bones.Single(b => (string?)b!["name"] == H.Thigh(Side.Left))!;
        Assert.Equal(H.Thigh(Side.Left), (string?)thigh["standard"]);
        Assert.Equal(H.Pelvis, (string?)thigh["parent"]);
        Assert.Equal("builtin", (string?)j["source"]!["kind"]);
        Assert.Equal("walk.blazma.json", Path.GetFileName(AnimationExporter.SidecarPathFor("/x/walk.glb")));
    }
}

public class ImportedExportTests
{
    /// <summary>Scene-space transforms of the given nodes with animated nodes sampled at <paramref name="t"/>.</summary>
    internal static Transform[] World(IReadOnlyList<GltfNodeInfo> nodes, GltfAnimation anim, double t)
    {
        var local = nodes.Select(n => n.Local).ToArray();
        foreach (var ch in anim.Channels)
        {
            var v = ch.Sample(t);
            local[ch.Node] = ch.Path switch
            {
                "translation" => local[ch.Node] with { Translation = new Vec3(v[0], v[1], v[2]) },
                "rotation" => local[ch.Node] with { Rotation = new Quat(v[0], v[1], v[2], v[3]) },
                "scale" => local[ch.Node] with { Scale = new Vec3(v[0], v[1], v[2]) },
                _ => local[ch.Node],
            };
        }
        var world = new Transform[nodes.Count];
        Transform W(int i) => nodes[i].Parent is int p ? W(p) * local[i] : local[i];
        for (var i = 0; i < nodes.Count; i++) world[i] = W(i);
        return world;
    }

    [Theory]
    [InlineData("CesiumMan")]
    [InlineData("RiggedFigure")]
    [InlineData("RiggedSimple")]
    public void Imported_KeepsTheOriginal_AppendsAnimation_OnOriginalNodes_AndMatchesTheApp(string model)
    {
        var (s, ch, clip) = ExportFixture.Imported(model);
        var rig = ExportFixture.RigOf(s, ch);
        var c = ExportFixture.ClipOf(s, clip);
        var original = AnimationExporter.OriginalGlb(s.Project, rig);
        var result = AnimationExporter.Export(s.Project, c, rig);
        var glb = GlbContainer.Parse(result.Glb);

        // Everything of the original is still there.
        foreach (var key in new[] { "nodes", "meshes", "skins", "materials", "accessors", "bufferViews" })
            if (original.Json[key] is JsonArray a)
                Assert.True(glb.Json[key]!.AsArray().Count >= a.Count, key);
        Assert.Equal(original.Json["nodes"]!.AsArray().Count, glb.Json["nodes"]!.AsArray().Count);
        Assert.Equal(original.Json["skins"]!.ToJsonString(), glb.Json["skins"]!.ToJsonString());
        Assert.Equal(original.Json["meshes"]!.ToJsonString(), glb.Json["meshes"]!.ToJsonString());
        var originalBufferLength = (int)original.Json["buffers"]![0]!["byteLength"]!;
        Assert.Equal(original.Bin.AsSpan(0, originalBufferLength).ToArray(), glb.Bin.AsSpan(0, originalBufferLength).ToArray());

        var animations = GltfAnimationReader.ReadAnimations(glb);
        var originalCount = (original.Json["animations"] as JsonArray)?.Count ?? 0;
        Assert.Equal(originalCount + 1, animations.Count);
        var anim = animations[^1];
        Assert.Equal("Wave", anim.Name);
        Assert.Equal(c.Id.ToString(), anim.BlazmaClipId);
        Assert.Equal(rig.Source.NodeIndex.Values.OrderBy(x => x), anim.Channels.Select(x => x.Node).Distinct().OrderBy(x => x));

        // Animated nodes carry TRS (a matrix cannot be animated).
        foreach (var node in rig.Source.NodeIndex.Values)
            Assert.Null(glb.Json["nodes"]![node]!["matrix"]);

        // World-space joint positions through the ORIGINAL hierarchy equal the app's evaluation.
        var nodes = GltfAnimationReader.ReadNodes(glb.Json);
        var ev = new RigEvaluator(rig);
        for (var f = 0; f <= 20; f++)
        {
            var world = World(nodes, anim, f / 30.0);
            var app = ev.Model(ClipEvaluator.Evaluate(c, rig, Flicks.FromFrame(f, c.Fps)));
            for (var i = 0; i < rig.Bones.Count; i++)
            {
                var node = rig.Source.NodeIndex[rig.Bones[i].Id.ToString()];
                Assert.True(world[node].Translation.ApproxEquals(app[i].Translation, 1e-4),
                    $"{model} {rig.Bones[i].Name} frame {f}: {world[node].Translation} vs {app[i].Translation}");
                Assert.True(Quat.AngleBetween(world[node].Rotation.Normalized(), app[i].Rotation.Normalized()) < 1e-3);
            }
        }

        var report = ExportValidator.Validate(result.Glb, s.Project, c, rig);
        Assert.True(report.Ok, string.Join("\n", report.Issues));
        if (model == "RiggedSimple") Assert.Contains(result.Warnings, w => w.Contains("matrix"));
    }

    [Fact]
    public void NameCollision_GetsASuffix()
    {
        var (s, ch, clip) = ExportFixture.Imported("CesiumMan");
        var rig = ExportFixture.RigOf(s, ch);
        var asset = s.Project.FindAsset(rig.Source.Asset)!;
        // Name the original's animation like our clip, as a previous export would have.
        var glb = GlbContainer.Parse(asset.Data);
        glb.Json["animations"]![0]!["name"] = "Wave";
        asset.Data = GlbContainer.Write(glb.Json, glb.Bin);
        var result = AnimationExporter.Export(s.Project, ExportFixture.ClipOf(s, clip), rig);
        Assert.Equal("Wave_2", result.AnimationName);
        var names = GltfAnimationReader.ReadAnimations(GlbContainer.Parse(result.Glb)).Select(a => a.Name).ToList();
        Assert.Equal(new[] { "Wave", "Wave_2" }, names);
        Assert.True(ExportValidator.Validate(result.Glb, s.Project, ExportFixture.ClipOf(s, clip), rig).Ok);
    }

    [Fact]
    public void NonGlbSource_And_ExternalBuffers_AreUnsupported()
    {
        var (s, ch, clip) = ExportFixture.Imported("RiggedSimple");
        var rig = ExportFixture.RigOf(s, ch);
        var asset = s.Project.FindAsset(rig.Source.Asset)!;
        var glb = GlbContainer.Parse(asset.Data);
        glb.Json["buffers"]![0]!["uri"] = "outside.bin";
        asset.Data = GlbContainer.Write(glb.Json, glb.Bin);
        Assert.Equal("unsupported_source", Assert.Throws<ExportException>(() =>
            AnimationExporter.Export(s.Project, ExportFixture.ClipOf(s, clip), rig)).Code);
        asset.FileName = "model.gltf";
        Assert.Equal("unsupported_source", Assert.Throws<ExportException>(() =>
            AnimationExporter.Export(s.Project, ExportFixture.ClipOf(s, clip), rig)).Code);
        s.Project.Assets.Clear();
        Assert.Equal("missing_asset", Assert.Throws<ExportException>(() =>
            AnimationExporter.Export(s.Project, ExportFixture.ClipOf(s, clip), rig)).Code);
    }
}

public class GltfAnimationReaderTests
{
    static GltfAnimationChannel Channel(string interp, string path, float[] times, float[] values, int comps) =>
        new(0, path, interp, times, values, comps);

    [Fact]
    public void Sampling_Step_Linear_Cubic()
    {
        var step = Channel("STEP", "translation", new[] { 0f, 1f }, new[] { 0f, 0, 0, 2, 4, 6 }, 3);
        Assert.Equal(new double[] { 0, 0, 0 }, step.Sample(0.9));
        Assert.Equal(new double[] { 2, 4, 6 }, step.Sample(1));
        var lin = Channel("LINEAR", "translation", new[] { 0f, 1f }, new[] { 0f, 0, 0, 2, 4, 6 }, 3);
        Assert.Equal(new double[] { 1, 2, 3 }, lin.Sample(0.5));
        Assert.Equal(new double[] { 2, 4, 6 }, lin.Sample(5)); // held after the end
        // CUBICSPLINE with zero tangents: smoothstep.
        var cub = Channel("CUBICSPLINE", "translation", new[] { 0f, 2f }, new[] { 0f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 8, 0, 0, 0, 0, 0 }, 3);
        Assert.Equal(4, cub.Sample(1)[0], 6);
        Assert.Equal(8 * (3 * 0.25 * 0.25 - 2 * 0.25 * 0.25 * 0.25), cub.Sample(0.5)[0], 6);
        var q = Quat.FromAxisAngle(Vec3.UnitY, 1);
        var rot = Channel("LINEAR", "rotation", new[] { 0f, 1f }, new[] { 0f, 0, 0, 1, (float)q.X, (float)q.Y, (float)q.Z, (float)q.W }, 4);
        var mid = rot.Sample(0.5);
        Assert.True(Quat.AngleBetween(new Quat(mid[0], mid[1], mid[2], mid[3]), Quat.FromAxisAngle(Vec3.UnitY, 0.5)) < 1e-6);
    }

    [Fact]
    public void Reads_TheKhronosSampleAnimations()
    {
        var glb = GlbContainer.Parse(File.ReadAllBytes(RepoPaths.Sample("models/khronos/CesiumMan.glb")));
        var anim = GltfAnimationReader.ReadAnimations(glb).Single();
        Assert.Equal(57, anim.Channels.Count);
        Assert.InRange(anim.Duration, 1.9, 2.1);
        var issues = new List<string>();
        ExportValidator.CheckStructure(glb, issues);
        Assert.Empty(issues);
    }
}

public class ExportCommandTests
{
    [Fact]
    public void Export_WritesGlbAndSidecar_Validates_AndDoesNotTouchTheUndoStack()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        var before = s.UndoHistory.Count;
        var wasDirty = s.IsDirty;
        using var dir = new TempDir();
        var path = dir.File("walk.glb");
        var r = ExportFixture.Run(s, "animation.export", new { clipId = clip, path, rootMotion = "in_place" });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Null(r.TransactionId);
        Assert.Equal(before, s.UndoHistory.Count);
        Assert.Equal(wasDirty, s.IsDirty);
        Assert.Equal(path, (string?)r.Data!["path"]);
        Assert.Equal(new FileInfo(path).Length, (int)r.Data["bytes"]!);
        Assert.Equal(26, (int)r.Data["frames"]!);
        Assert.True((bool)r.Data["validation"]!["ok"]!);
        Assert.Equal("in_place", (string?)r.Data["rootMotion"]!["mode"]);
        var sidecar = dir.File("walk.blazma.json");
        Assert.Equal(sidecar, (string?)r.Data["sidecarPath"]);
        Assert.Equal("walk.glb", (string?)JsonNode.Parse(File.ReadAllText(sidecar))!["glb"]);
        Assert.False(File.Exists(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));

        var v = ExportFixture.Run(s, "animation.validate", new { path, clipId = clip });
        Assert.True(v.Ok, v.Error?.Message);
        Assert.True((bool)v.Data!["ok"]!, v.Data.ToJsonString());
    }

    [Fact]
    public void Export_Refuses_ExistingFiles_WrongExtension_AndMissingFolders()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        using var dir = new TempDir();
        var path = dir.File("walk.glb");
        Assert.True(ExportFixture.Run(s, "animation.export", new { clipId = clip, path }).Ok);
        Assert.Equal("file_exists", ExportFixture.Run(s, "animation.export", new { clipId = clip, path }).Error!.Code);
        // The UI may overwrite on request.
        Assert.True(ExportFixture.Run(s, "animation.export", new { clipId = clip, path, overwrite = true }, CommandOrigin.Ui).Ok);
        // A sidecar that already exists is not replaced either.
        var other = dir.File("other.glb");
        File.WriteAllText(dir.File("other.blazma.json"), "{}");
        Assert.Equal("file_exists", ExportFixture.Run(s, "animation.export", new { clipId = clip, path = other }).Error!.Code);
        Assert.True(ExportFixture.Run(s, "animation.export", new { clipId = clip, path = other, sidecar = false }).Ok);
        Assert.Equal("{}", File.ReadAllText(dir.File("other.blazma.json")));

        Assert.Equal("unsupported_format", ExportFixture.Run(s, "animation.export", new { clipId = clip, path = dir.File("walk.fbx") }).Error!.Code);
        Assert.Equal("not_found", ExportFixture.Run(s, "animation.export", new { clipId = clip, path = Path.Combine(dir.Path, "missing", "a.glb") }).Error!.Code);
        Assert.Equal("invalid_args", Assert.Throws<CommandException>(() =>
            ExportFixture.Registry.Create("animation.export", ExportFixture.A(new { clipId = clip, path, rootMotion = "sideways" }))).Code);
        Assert.Equal("invalid_range", ExportFixture.Run(s, "animation.export", new { clipId = clip, path = dir.File("r.glb"), startFrame = 30 }).Error!.Code);
        Assert.False(File.Exists(dir.File("r.glb")));
    }

    [Fact]
    public void Export_ImportedCharacter_ThroughTheCommand()
    {
        var (s, ch, clip) = ExportFixture.Imported("CesiumMan");
        using var dir = new TempDir();
        var r = ExportFixture.Run(s, "animation.export", new { clipId = clip, characterId = ch, path = dir.File("cesium.glb") });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Equal("imported", (string?)r.Data!["source"]);
        Assert.True((bool)r.Data["validation"]!["ok"]!, r.Data["validation"]!.ToJsonString());
    }

    [Fact]
    public void Validate_ReportsDifferences()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        using var dir = new TempDir();
        var path = dir.File("walk.glb");
        Assert.True(ExportFixture.Run(s, "animation.export", new { clipId = clip, path }).Ok);
        // Edit the clip after exporting: the file no longer matches.
        ExportFixture.Run(s, "pose.set_bone", new { characterId = ch, bone = H.Head, rx = 40, ry = 0, rz = 0 });
        ExportFixture.Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 12, bones = H.Head });
        var v = ExportFixture.Run(s, "animation.validate", new { path, clipId = clip });
        Assert.True(v.Ok);
        Assert.False((bool)v.Data!["ok"]!);
        Assert.Contains(v.Data["issues"]!.AsArray(), i => ((string)i!).Contains(H.Head));
        Assert.Equal("not_found", ExportFixture.Run(s, "animation.validate", new { path = dir.File("none.glb"), clipId = clip }).Error!.Code);
    }
}

public class ExportAgentSecurityTests
{
    static (ProjectSession s, AgentHost host, string sid, string clip) Host(bool userPresent, AgentMode mode = AgentMode.Assisted)
    {
        var (s, _, clip) = ExportFixture.Walk();
        var host = new AgentHost(ExportFixture.Registry, () => s, userPresent) { Mode = mode };
        return (s, host, host.Connect("t").Id, clip);
    }

    [Fact]
    public void Agent_ExportsInsideAllowedFolder_ButNeverOverwrites()
    {
        var (s, host, sid, clip) = Host(userPresent: false);
        using var dir = new TempDir();
        host.AllowedReadRoots.Add(dir.Path);
        var path = dir.File("walk.glb");
        var r = host.Call(sid, "animation.export", ExportFixture.A(new { clipId = clip, path }));
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Null(r.TransactionId);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("file_exists", host.Call(sid, "animation.export", ExportFixture.A(new { clipId = clip, path, overwrite = true })).Error!.Code);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        // Overwrite is forced off for agents, so an existing sidecar is protected too.
        File.WriteAllText(dir.File("b.blazma.json"), "mine");
        Assert.Equal("file_exists", host.Call(sid, "animation.export", ExportFixture.A(new { clipId = clip, path = dir.File("b.glb"), overwrite = true })).Error!.Code);
        Assert.Equal("mine", File.ReadAllText(dir.File("b.blazma.json")));
        Assert.Equal("unsupported_format", host.Call(sid, "animation.export", ExportFixture.A(new { clipId = clip, path = dir.File("x.blazanim") })).Error!.Code);
        Assert.Equal("not_found", host.Call(sid, "animation.export", ExportFixture.A(new { clipId = clip, path = Path.Combine(dir.Path, "no", "x.glb") })).Error!.Code);
        Assert.True(host.Call(sid, "animation.validate", ExportFixture.A(new { clipId = clip, path })).Ok);
        Assert.Empty(host.Tasks); // read-only for the project: no task, nothing to approve
    }

    [Theory]
    [InlineData(false, AgentMode.Assisted)]
    [InlineData(true, AgentMode.Autonomous)]
    public void Agent_CannotWriteOrReadOutsideAllowedFolders(bool userPresent, AgentMode mode)
    {
        var (_, host, sid, clip) = Host(userPresent, mode);
        using var dir = new TempDir();
        var path = dir.File("walk.glb");
        Assert.Equal("path_not_allowed", host.Call(sid, "animation.export", ExportFixture.A(new { clipId = clip, path })).Error!.Code);
        Assert.False(File.Exists(path));
        File.WriteAllBytes(path, new byte[] { 1 });
        Assert.Equal("path_not_allowed", host.Call(sid, "animation.validate", ExportFixture.A(new { clipId = clip, path })).Error!.Code);
        Assert.Empty(host.PendingApprovals);
    }

    [Fact]
    public void AssistedUser_ApprovesAnExportOutsideTheAllowedFolders()
    {
        var (_, host, sid, clip) = Host(userPresent: true);
        using var dir = new TempDir();
        var path = dir.File("walk.glb");
        var r = host.Call(sid, "animation.export", ExportFixture.A(new { clipId = clip, path, overwrite = true }));
        Assert.Equal("approval_required", r.Error!.Code);
        Assert.False(File.Exists(path));
        var pending = Assert.Single(host.PendingApprovals);
        Assert.False((bool)pending.Args["overwrite"]!);
        Assert.True(host.ApprovePending(pending.Id).Ok);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SymlinkedFolder_InsideAllowedRoot_CannotRedirectTheExport()
    {
        if (OperatingSystem.IsWindows()) return;
        var (_, host, sid, clip) = Host(userPresent: false);
        using var allowed = new TempDir();
        using var outside = new TempDir();
        var link = Path.Combine(allowed.Path, "out");
        Directory.CreateSymbolicLink(link, outside.Path);
        host.AllowedReadRoots.Add(allowed.Path);
        Assert.Equal("path_not_allowed", host.Call(sid, "animation.export", ExportFixture.A(new { clipId = clip, path = Path.Combine(link, "walk.glb") })).Error!.Code);
        Assert.Empty(Directory.GetFiles(outside.Path));
    }
}
