using System.Diagnostics;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Core.Timing;
using Blazma.Export;
using Blazma.Motion;
using Blazma.Rigging;
using Blazma.Tests.Rigging.Import;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.Advanced;

/// <summary>Regression tests for the Phase 8 review (findings 1–16).</summary>
public class Phase8ReviewTests
{
    // ------------------------------------------------------------------ helpers

    static JsonObject Asset() => new() { ["version"] = "2.0" };

    /// <summary>A node array: a chain (each node the child of the previous) or flat nodes, optionally with a scene.</summary>
    static JsonObject Nodes(int count, bool chain, bool scene)
    {
        var arr = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            var n = new JsonObject { ["name"] = "n" + i };
            if (chain && i + 1 < count) n["children"] = new JsonArray(i + 1);
            arr.Add(n);
        }
        var j = new JsonObject { ["asset"] = Asset(), ["nodes"] = arr };
        if (scene) { j["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }); j["scene"] = 0; }
        return j;
    }

    /// <summary>A one-triangle mesh with the given POSITION bounds.</summary>
    static JsonObject Triangle(double min, double max) => new()
    {
        ["asset"] = Asset(),
        ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = 36 }),
        ["bufferViews"] = new JsonArray(new JsonObject { ["buffer"] = 0, ["byteLength"] = 36 }),
        ["accessors"] = new JsonArray(new JsonObject
        {
            ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = 3, ["type"] = "VEC3",
            ["min"] = new JsonArray(min, min, min), ["max"] = new JsonArray(max, max, max),
        }),
        ["meshes"] = new JsonArray(new JsonObject { ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = new JsonObject { ["POSITION"] = 0 } }) }),
        ["nodes"] = new JsonArray(new JsonObject { ["mesh"] = 0 }),
        ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
        ["scene"] = 0,
    };

    static string Write(TempDir dir, string name, JsonObject json, byte[] bin)
    {
        var p = dir.File(name);
        File.WriteAllBytes(p, GlbContainer.Write(json, bin));
        return p;
    }

    /// <summary>Runs a command the way the UI does (no try/catch around Execute): any escaping exception fails the test.</summary>
    static CommandResult Ui(ProjectSession s, string id, object args) =>
        s.Execute(P8.Registry.Create(id, JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(args))!.AsObject()), CommandOrigin.Ui);

    static Project RoundTrip(Project p)
    {
        using var ms = new MemoryStream();
        ProjectArchive.Write(p, ms);
        ms.Position = 0;
        return ProjectArchive.Read(ms);
    }

    // ------------------------------------------------------------------ #1 deep/cyclic node hierarchies

    [Fact]
    public void Review1_DeepNodeChain_IsRefused_WithoutRecursion()
    {
        using var dir = new TempDir();
        var (s, _, _) = P8.Scene(cesium: false);
        // 100k nodes in one chain: used to overflow the stack (process crash); now refused by the node cap.
        var r = Ui(s, "prop.import", new { path = Write(dir, "chain100k.glb", Nodes(100_000, chain: true, scene: true), Array.Empty<byte>()) });
        Assert.Equal("invalid_gltf", P8.Err(r));
        // Within the node cap but deeper than 256 levels: refused by the depth cap.
        r = Ui(s, "prop.import", new { path = Write(dir, "chain300.glb", Nodes(300, chain: true, scene: true), Array.Empty<byte>()) });
        Assert.Equal("invalid_gltf", P8.Err(r));
        Assert.Contains("deeper", r.Error!.Message);
        // A cycle (0 → 1 → 0).
        var cyc = Nodes(2, chain: true, scene: true);
        cyc["nodes"]![1]!["children"] = new JsonArray(0);
        Assert.Equal("invalid_gltf", P8.Err(Ui(s, "prop.import", new { path = Write(dir, "cycle.glb", cyc, Array.Empty<byte>()) })));
        // 200 levels are fine, in document order.
        r = P8.Ok(s, "prop.import", new { path = Write(dir, "chain200.glb", Nodes(200, chain: true, scene: true), Array.Empty<byte>()) });
        Assert.Single(s.Project.Props);
    }

    [Fact]
    public void Review1_CraftedStoredMeshProp_ExportFallsBackToABox()
    {
        // A .blazanim can carry any asset bytes: the export path validates them again (no recursion, no crash).
        var (s, man, _) = P8.Scene(cesium: false);
        var clip = P8.ApplyTemplate(s, "builtin.idle_breathe", man);
        var asset = new ProjectAsset { FileName = "evil.glb", MediaType = "model/gltf-binary", Data = GlbContainer.Write(Nodes(100_000, chain: true, scene: true), Array.Empty<byte>()) };
        s.Project.Assets.Add(asset);
        var prop = new Prop { Name = "Evil", Shape = PropShape.Mesh, Asset = asset.Id };
        s.Project.Props.Add(prop);
        P8.Ok(s, "key.set", new { clipId = clip, frame = 0, props = "Evil" });
        using var dir = new TempDir();
        var r = Ui(s, "animation.export", new { clipId = clip, characterId = man, path = dir.File("evil.glb") });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Contains(r.Warnings, w => w.Message.Contains("a box is exported instead"));
        Assert.True((bool)r.Data!["validation"]!["ok"]!, r.Data["validation"]!.ToJsonString());
    }

    // ------------------------------------------------------------------ #2 overlapping buffer views

    [Fact]
    public void Review2_OverlappingBufferViews_AreCopiedOnce()
    {
        var bin = new byte[4 << 20];
        var views = new JsonArray();
        for (var i = 0; i < 200; i++) views.Add(new JsonObject { ["buffer"] = 0, ["byteLength"] = bin.Length - i * 4, ["byteOffset"] = i * 4 });
        var json = new JsonObject { ["asset"] = Asset(), ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length }), ["bufferViews"] = views };
        var glb = GlbContainer.Parse(GlbContainer.Write(json, bin));
        var b = new GltfBuilder(new JsonObject { ["asset"] = Asset() });
        var before = GC.GetTotalAllocatedBytes(true);
        PropExport.AppendGlbScene(b, glb, new List<string>(), "alias");
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        Assert.True(allocated < 64L << 20, $"allocated {allocated / 1e6:0} MB (used to be ~1.6 GB)");
        var output = GlbContainer.Parse(b.ToGlb());
        Assert.InRange(output.Bin.Length, bin.Length, bin.Length + 8); // the source buffer once
        var outViews = (JsonArray)output.Json["bufferViews"]!;
        Assert.Equal(200, outViews.Count);
        Assert.Equal(4 * 7, (int)outViews[7]!["byteOffset"]! - (int)outViews[0]!["byteOffset"]!); // rebased, same layout
    }

    // ------------------------------------------------------------------ #3 quadratic root detection

    [Fact]
    public void Review3_ManyNodesWithoutScene_AreFast_AndCapped()
    {
        var glb = GlbContainer.Parse(GlbContainer.Write(Nodes(PropExport.MaxNodes, chain: false, scene: false), Array.Empty<byte>()));
        var sw = Stopwatch.StartNew();
        var roots = PropExport.AppendGlbScene(new GltfBuilder(new JsonObject { ["asset"] = Asset() }), glb, new List<string>(), "flat");
        Assert.Equal(PropExport.MaxNodes, roots.Count);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"{sw.ElapsedMilliseconds} ms");
        var tooMany = GlbContainer.Parse(GlbContainer.Write(Nodes(PropExport.MaxNodes + 1, chain: false, scene: false), Array.Empty<byte>()));
        var e = Assert.Throws<ExportException>(() => PropExport.AppendGlbScene(new GltfBuilder(new JsonObject { ["asset"] = Asset() }), tooMany, new List<string>(), "flat"));
        Assert.Equal("invalid_gltf", e.Code);
    }

    // ------------------------------------------------------------------ #4 unexpected exceptions

    [Fact]
    public void Review4_MalformedGlbs_NeverThrowThroughTheUiPath()
    {
        using var dir = new TempDir();
        var (s, _, _) = P8.Scene(cesium: false);
        var cases = new List<(string, JsonObject, byte[])>
        {
            ("view-number", new JsonObject { ["asset"] = Asset(), ["bufferViews"] = new JsonArray(5) }, Array.Empty<byte>()),
            ("min-strings", new JsonObject
            {
                ["asset"] = Asset(),
                ["accessors"] = new JsonArray(new JsonObject { ["componentType"] = 5126, ["count"] = 3, ["type"] = "VEC3", ["min"] = new JsonArray("a", "b", "c"), ["max"] = new JsonArray(1, 1, 1) }),
                ["meshes"] = new JsonArray(new JsonObject { ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = new JsonObject { ["POSITION"] = 0 } }) }),
                ["nodes"] = new JsonArray(new JsonObject { ["mesh"] = 0 }),
            }, Array.Empty<byte>()),
            ("offset-overflow", new JsonObject
            {
                ["asset"] = Asset(), ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = 4 }),
                ["bufferViews"] = new JsonArray(new JsonObject { ["buffer"] = 0, ["byteOffset"] = int.MaxValue, ["byteLength"] = int.MaxValue }),
            }, new byte[4]),
            ("accessor-number", new JsonObject { ["asset"] = Asset(), ["accessors"] = new JsonArray(7) }, Array.Empty<byte>()),
            ("node-string", new JsonObject { ["asset"] = Asset(), ["nodes"] = new JsonArray("x") }, Array.Empty<byte>()),
            ("nodes-object", new JsonObject { ["asset"] = Asset(), ["nodes"] = new JsonObject() }, Array.Empty<byte>()),
            ("primitives-missing", new JsonObject { ["asset"] = Asset(), ["meshes"] = new JsonArray(new JsonObject()) }, Array.Empty<byte>()),
            ("child-string", new JsonObject { ["asset"] = Asset(), ["nodes"] = new JsonArray(new JsonObject { ["children"] = new JsonArray("a") }) }, Array.Empty<byte>()),
        };
        foreach (var (name, json, bin) in cases)
        {
            var r = Ui(s, "prop.import", new { path = Write(dir, name + ".glb", json, bin) });
            Assert.False(r.Ok, name);
            Assert.Equal("invalid_gltf", P8.Err(r));
        }
        Assert.Empty(s.Project.Props);
        Assert.DoesNotContain(s.Project.Assets, a => s.Project.Rigs.All(r => r.Source.Asset != a.Id));
    }

    sealed class Exploding(bool mutate) : ICommand
    {
        public string Id => "test.explode";
        public CommandEffect Effect => CommandEffect.Mutating;
        public string Label => "Explode";

        public CommandOutcome Execute(Project p)
        {
            if (mutate) throw new InvalidOperationException("boom");
            throw new IndexOutOfRangeException("bang");
        }

        public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
    }

    [Fact]
    public void Review4_SafetyNet_TurnsUnexpectedExceptionsIntoInternalError()
    {
        var (s, _, _) = P8.Scene(cesium: false);
        var undo = s.UndoHistory.Count;
        foreach (var mutate in new[] { true, false })
        {
            var r = s.Execute(new Exploding(mutate), CommandOrigin.Ui);
            Assert.Equal("internal_error", P8.Err(r));
            Assert.Contains("test.explode", r.Error!.Message);
        }
        Assert.Equal(undo, s.UndoHistory.Count); // nothing recorded

        // A SceneEdit whose Apply throws something unexpected restores what it touched.
        var seqId = (string)P8.Ok(s, "sequence.create", new { characterId = s.Project.Characters[0].Id.ToString(), name = "S" }).Data!["sequenceId"]!;
        var json = P8.Json(s.Project.Sequences);
        Assert.Equal("internal_error", P8.Err(s.Execute(new ThrowingSceneEdit(), CommandOrigin.Ui)));
        Assert.Equal(json, P8.Json(s.Project.Sequences));
        Assert.NotNull(s.Project.FindSequence(ObjectId.Parse(seqId)));
    }

    sealed class ThrowingSceneEdit : SceneEdit
    {
        public override string Id => "test.scene_throw";
        public override string Label => "Throw";
        protected override IReadOnlyList<Change> Changes => Array.Empty<Change>();
        protected override SceneParts Touches => SceneParts.Sequences;

        protected override JsonNode? Apply(Project project, List<Diagnostic> warnings)
        {
            project.Sequences.Clear();
            throw new NullReferenceException("half-done");
        }
    }

    [Fact]
    public void Review4_TemplateTimeScaleAbove100_IsInvalidArgs()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var clip = P8.ApplyTemplate(s, "builtin.jump", man, 0.2);
        var t = (string)P8.Ok(s, "template.save_clip", new { clipId = clip, name = "short" }).Data!["templateId"]!;
        var r = Ui(s, "template.apply", new { templateId = t, characterId = man, durationSeconds = 30 });
        Assert.Equal("invalid_args", P8.Err(r));
        Assert.Contains("100", r.Error!.Message);
        var e = Assert.Throws<Blazma.Rigging.Retargeting.RetargetException>(() =>
            ClipRetargeter.Retarget(P8.Clip(s, clip), P8.RigOf(s, man), P8.RigOf(s, man), new ClipRetargetOptions { TimeScale = 150 }));
        Assert.Equal("invalid_args", e.Code);
    }

    [Fact]
    public void Review4_SequenceWithHugeStartFrame_DoesNotOverflow()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var walk = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
        var seq = (string)P8.Ok(s, "sequence.create", new { characterId = man, name = "S" }).Data!["sequenceId"]!;
        P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = walk });
        s.Project.FindSequence(ObjectId.Parse(seq))!.Items[0].StartFrame = long.MaxValue / 2; // as from a crafted file
        var r = Ui(s, "sequence.bake", new { sequenceId = seq });
        Assert.Equal("empty_sequence", P8.Err(r)); // the out-of-limit item is skipped, not overflowed
        Assert.Equal("empty_sequence", P8.Err(Ui(s, "sequence.evaluate", new { sequenceId = seq, frame = 0 })));
    }

    // ------------------------------------------------------------------ #5 prop bounds

    [Fact]
    public void Review5_HugeOrNonFiniteBounds_AreRefusedAtImport_AndProjectsStaySaveable()
    {
        using var dir = new TempDir();
        var (s, _, _) = P8.Scene(cesium: false);
        var r = Ui(s, "prop.import", new { path = Write(dir, "huge.glb", Triangle(-1e200, 1e200), new byte[36]) });
        Assert.Equal("invalid_gltf", P8.Err(r));
        Assert.Contains("10,000", r.Error!.Message);
        Assert.Equal("invalid_gltf", P8.Err(Ui(s, "prop.import", new { path = Write(dir, "big.glb", Triangle(-6000, 6000), new byte[36]) })));
        Assert.Empty(s.Project.Props);

        var ok = P8.Ok(s, "prop.import", new { path = Write(dir, "fine.glb", Triangle(-0.5, 0.5), new byte[36]) });
        Assert.Equal(1.0, (double)ok.Data!["size"]![0]!, 9);
        var file = dir.File("p.blazanim");
        ProjectFile.Save(s.Project, file); // never unsaveable through commands
        Assert.Single(ProjectFile.Load(file).Props);

        // The archive check does not overflow (each component ≤ 10 km) and names the prop.
        s.Project.Props[0].Size = new Vec3(1e200, 1, 1);
        var e = Assert.Throws<ProjectFormatException>(() => RoundTrip(s.Project));
        Assert.Contains("'fine'", e.Message);
        Assert.Contains(s.Project.Props[0].Id.ToString(), e.Message);
    }

    // ------------------------------------------------------------------ #6 props of characters sharing a rig

    [Fact]
    public void Review6_PropFollowsOnlyItsOwnCharacter_WhenTwoShareARig()
    {
        var (s, a, _) = P8.Scene(cesium: false);
        var b = (string)P8.Ok(s, "scene.add_mannequin", new { }).Data!["characterId"]!;
        Assert.Equal(P8.Character(s, a).Rig, P8.Character(s, b).Rig);
        var sword = (string)P8.Ok(s, "prop.add", new { name = "Sword", characterId = a, bone = H.Hand(Side.Right), y = 0.3 }).Data!["propId"]!;
        var prop = s.Project.FindProp(ObjectId.Parse(sword))!;
        var clipId = P8.ApplyTemplate(s, "builtin.attack_swing", a);
        var clip = P8.Clip(s, clipId);
        var rig = P8.RigOf(s, a);

        Assert.Single(PropEvaluator.PropsFor(s.Project, clip, rig, ObjectId.Parse(a)));
        Assert.Empty(PropEvaluator.PropsFor(s.Project, clip, rig, ObjectId.Parse(b)));
        Assert.True(PropEvaluator.AttachedBone(s.Project, prop, rig, ObjectId.Parse(b)) < 0);
        var t = Flicks.FromFrame(10, clip.Fps);
        Assert.Equal(prop.Transform, PropEvaluator.WorldAt(s.Project, prop, clip, rig, t, ObjectId.Parse(b)));
        Assert.NotEqual(prop.Transform, PropEvaluator.WorldAt(s.Project, prop, clip, rig, t, ObjectId.Parse(a)));

        using var dir = new TempDir();
        var rb = P8.Ok(s, "animation.export", new { clipId, characterId = b, path = dir.File("b.glb") }).Data!;
        Assert.True((bool)rb["validation"]!["ok"]!, rb["validation"]!.ToJsonString());
        Assert.Equal(0, (int)rb["validation"]!["propsChecked"]!);
        Assert.DoesNotContain(GlbContainer.Parse(File.ReadAllBytes(dir.File("b.glb"))).Json["nodes"]!.AsArray(), n => (string?)n!["name"] == "Sword");
        var ra = P8.Ok(s, "animation.export", new { clipId, characterId = a, path = dir.File("a.glb") }).Data!;
        Assert.Equal(1, (int)ra["validation"]!["propsChecked"]!);
        // Validating a's file as b's (other character) finds the prop node unexpected.
        var vb = P8.Ok(s, "animation.validate", new { path = dir.File("a.glb"), clipId, characterId = b }).Data!;
        Assert.False((bool)vb["ok"]!);
        var va = P8.Ok(s, "animation.validate", new { path = dir.File("a.glb"), clipId, characterId = a }).Data!;
        Assert.True((bool)va["ok"]!, va.ToJsonString());
        // prop.evaluate on b: the free transform.
        var ev = P8.Ok(s, "prop.evaluate", new { propId = sword, clipId, frame = 10, characterId = b }).Data!;
        Assert.Equal(prop.Transform.Translation.Y, (double)ev["world"]!["position"]![1]!, 6);
    }

    // ------------------------------------------------------------------ #7 sequence limits

    [Fact]
    public void Review7_SequenceLimits_ItemsBlendsAndWork()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var walk = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
        var seq = (string)P8.Ok(s, "sequence.create", new { characterId = man, name = "S" }).Data!["sequenceId"]!;
        P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = walk });

        // The reviewer's DoS: overlapping items with a 10M-frame blend are refused before any heavy work.
        var sw = Stopwatch.StartNew();
        Assert.Equal("too_large", P8.Err(P8.Run(s, "sequence.add_clip", new { sequenceId = seq, clipId = walk, startFrame = 0, blendFrames = 10_000_000, loops = 1000 })));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms");

        // At most MaxBlending items blending at once.
        for (var i = 1; i < SequenceEvaluator.MaxBlending; i++)
            P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = walk, startFrame = 0, blendFrames = 20 });
        var r = P8.Run(s, "sequence.add_clip", new { sequenceId = seq, clipId = walk, startFrame = 0, blendFrames = 20 });
        Assert.Equal("too_many_blends", P8.Err(r));
        Assert.Equal(SequenceEvaluator.MaxBlending, s.Project.FindSequence(ObjectId.Parse(seq))!.Items.Count);
        var sample = P8.Ok(s, "sequence.evaluate", new { sequenceId = seq, frame = 10 }).Data!;
        Assert.Equal(SequenceEvaluator.MaxBlending, sample["active"]!.AsArray().Count);

        // An archive with more overlapping items: extras are skipped with a warning (never more than MaxBlending evaluated).
        var sq = s.Project.FindSequence(ObjectId.Parse(seq))!;
        sq.Items.Add(new SequenceItem { Clip = ObjectId.Parse(walk), StartFrame = 0, BlendInFrames = 20 });
        var ev = new SequenceEvaluator(s.Project, sq);
        Assert.Single(ev.SkippedForBlending);
        Assert.True(ev.Evaluate(10).Active.Count <= SequenceEvaluator.MaxBlending);

        // Item cap; adding hundreds of items one after another stays fast (no evaluator rebuild with poses per add).
        var seq2 = (string)P8.Ok(s, "sequence.create", new { characterId = man, name = "Long" }).Data!["sequenceId"]!;
        sw.Restart();
        for (var i = 0; i < ClipSequence.MaxItems; i++) P8.Ok(s, "sequence.add_clip", new { sequenceId = seq2, clipId = walk, blendFrames = 5 });
        Assert.True(sw.ElapsedMilliseconds < 20_000, $"{ClipSequence.MaxItems} add_clip took {sw.ElapsedMilliseconds} ms");
        Assert.Equal("too_large", P8.Err(P8.Run(s, "sequence.add_clip", new { sequenceId = seq2, clipId = walk })));
        // Speed below the minimum is refused (it made sequences astronomically long).
        Assert.Equal("invalid_args", P8.Err(P8.Run(s, "sequence.add_clip", new { sequenceId = seq, clipId = walk, speed = 0.001 })));
    }

    // ------------------------------------------------------------------ #8 snapshots share templates

    [Fact]
    public void Review8_SceneEditSnapshots_ShareImmutableTemplates()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var walk = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
        P8.Ok(s, "template.save_clip", new { clipId = walk, name = "T" });
        var template = s.Project.Templates.Single();
        var seq = (string)P8.Ok(s, "sequence.create", new { characterId = man, name = "S" }).Data!["sequenceId"]!;
        P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = walk });
        P8.Ok(s, "prop.add", new { name = "Box" });
        s.Undo(CommandOrigin.Ui);
        s.Undo(CommandOrigin.Ui);
        s.Redo(CommandOrigin.Ui);
        // Sequence/prop edits never copy the template library: still the very same object.
        Assert.Same(template, s.Project.Templates.Single());
        // Template edits keep working with shared references.
        s.Undo(CommandOrigin.Ui); // add_clip
        s.Undo(CommandOrigin.Ui); // create
        s.Undo(CommandOrigin.Ui); // save_clip
        Assert.Empty(s.Project.Templates);
        s.Redo(CommandOrigin.Ui);
        Assert.Same(template, s.Project.Templates.Single());
    }

    // ------------------------------------------------------------------ #9 heading carried on the pelvis

    [Fact]
    public void Review9_AlignYaw_UsesAHeadingCarriedOnThePelvis()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var rig = P8.RigOf(s, man);
        var idleId = P8.ApplyTemplate(s, "builtin.idle_breathe", man);
        var idle = P8.Clip(s, idleId);
        // A copy of the idle turned 90° to the left on the pelvis (the root does not rotate).
        var turned = ClipEdit.CloneClip(idle);
        turned.Id = ObjectId.New();
        turned.Name = "Turned";
        var pelvis = rig.FindByStandardName(H.Pelvis)!;
        var pi = rig.IndexOf(pelvis.Id);
        var parent = new RigEvaluator(rig).ParentOf(pi);
        var pm = parent < 0 ? Quat.Identity : new RigEvaluator(rig).RestModel()[parent].Rotation;
        var adj = pm.Inverse() * Quat.FromAxisAngle(Vec3.UnitY, Math.PI / 2) * pm;
        var track = turned.Tracks.Find(t => t.Target.Kind == TargetKind.Bone && t.Target.Id == pelvis.Id && t.Channel == TrackChannels.Rotation);
        if (track is null)
        {
            var q0 = pelvis.Rest.Rotation;
            turned.Tracks.Add(track = new Track { Target = new TrackTarget { Kind = TargetKind.Bone, Id = pelvis.Id }, Channel = TrackChannels.Rotation,
                Keys = { new Keyframe { Time = Flicks.Zero, Value = new[] { q0.X, q0.Y, q0.Z, q0.W } } } });
        }
        foreach (var k in track.Keys)
        {
            var q = (adj * new Quat(k.Value[0], k.Value[1], k.Value[2], k.Value[3])).Normalized();
            k.Value = new[] { q.X, q.Y, q.Z, q.W };
            k.InTangent = k.OutTangent = null;
        }
        s.Project.Clips.Add(turned);
        var seq = (string)P8.Ok(s, "sequence.create", new { characterId = man, name = "Turn" }).Data!["sequenceId"]!;
        P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = turned.Id.ToString() });
        var item = (string)P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = idleId, blendFrames = 0 }).Data!["itemId"]!;
        var align = new SequenceEvaluator(s.Project, s.Project.FindSequence(ObjectId.Parse(seq))!).AlignmentOf(ObjectId.Parse(item));
        var yaw = 2 * Math.Atan2(align.Rotation.Y, align.Rotation.W) * 180 / Math.PI;
        Assert.InRange(yaw, 85, 95); // was 0: only the root's yaw was read
    }

    // ------------------------------------------------------------------ #10 prop tracks carried through

    [Fact]
    public void Review10_PropTracks_SurviveBakeRetargetAndTemplates()
    {
        var (s, man, cesium) = P8.Scene();
        var walk = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
        var idle = P8.ApplyTemplate(s, "builtin.idle_breathe", man);
        var ball = (string)P8.Ok(s, "prop.add", new { name = "Ball", shape = "sphere", x = 1, y = 0.1, z = 0 }).Data!["propId"]!;
        P8.Ok(s, "key.set", new { clipId = walk, frame = 0, props = "Ball" });
        P8.Ok(s, "prop.set_transform", new { propId = ball, x = 1, y = 1.5, z = 2 });
        P8.Ok(s, "key.set", new { clipId = walk, frame = 20, props = "Ball" });
        var prop = s.Project.FindProp(ObjectId.Parse(ball))!;

        // Bake: the blended prop values become prop tracks.
        var seq = (string)P8.Ok(s, "sequence.create", new { characterId = man, name = "S" }).Data!["sequenceId"]!;
        P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = walk });
        P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = idle, blendFrames = 10 });
        var bake = P8.Ok(s, "sequence.bake", new { sequenceId = seq, reduceKeys = false }).Data!;
        Assert.Equal(1, (int)bake["props"]!);
        var baked = P8.Clip(s, (string)bake["clipId"]!);
        Assert.Equal(2, baked.Tracks.Count(t => t.Target.Kind == TargetKind.Prop && t.Target.Id == prop.Id));
        var ev = new SequenceEvaluator(s.Project, s.Project.FindSequence(ObjectId.Parse(seq))!);
        foreach (var f in new[] { 0, 10, 20, 35 })
        {
            var expected = ev.PropLocal(prop, f)!.Value;
            var got = PropEvaluator.Local(prop, baked, Flicks.FromFrame(f, baked.Fps));
            Assert.True((got.Translation - expected.Translation).Length() < 1e-6, $"frame {f}");
        }
        Assert.Equal(1.5, PropEvaluator.Local(prop, baked, Flicks.FromFrame(20, baked.Fps)).Translation.Y, 6);

        // Retarget: prop tracks are copied.
        var rr = P8.Ok(s, "clip.retarget", new { clipId = walk, fromCharacterId = man, toCharacterId = cesium, footLock = false });
        var retargeted = P8.Clip(s, (string)rr.Data!["clipId"]!);
        Assert.Equal(2, retargeted.Tracks.Count(t => t.Target.Kind == TargetKind.Prop));
        Assert.Contains(rr.Warnings, w => w.Code == "prop_tracks");
        Assert.Equal(1.5, PropEvaluator.Local(prop, retargeted, Flicks.FromFrame(20, retargeted.Fps)).Translation.Y, 6);

        // Template (with a different duration): copied and re-timed.
        var t = (string)P8.Ok(s, "template.save_clip", new { clipId = walk, name = "Ball walk" }).Data!["templateId"]!;
        var length = P8.Clip(s, walk).Duration.ToSeconds();
        var applied = P8.Clip(s, P8.ApplyTemplate(s, t, man, 2 * length));
        var tr = applied.Tracks.Single(x => x.Target.Kind == TargetKind.Prop && x.Channel == TrackChannels.Translation);
        Assert.Equal(40, tr.Keys[^1].Time.ToFrame(applied.Fps));
        Assert.Equal(1.5, PropEvaluator.Local(prop, applied, Flicks.FromFrame(40, applied.Fps)).Translation.Y, 6);

        // An attached prop of another character is reported.
        var sword = (string)P8.Ok(s, "prop.add", new { name = "Sword", characterId = man, bone = H.Hand(Side.Right) }).Data!["propId"]!;
        P8.Ok(s, "key.set", new { clipId = idle, frame = 0, props = "Sword" });
        var r2 = P8.Ok(s, "clip.retarget", new { clipId = idle, fromCharacterId = man, toCharacterId = cesium, footLock = false });
        Assert.Contains(r2.Warnings, w => w.Code == "prop_attached_elsewhere");
        Assert.NotNull(sword);
    }

    // ------------------------------------------------------------------ #11 foot phase with mixed frame rates

    [Fact]
    public void Review11_MatchFootPhase_PassesTheItemSpeedOnly()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var walk = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
        var run = (string)P8.Ok(s, "template.apply", new { templateId = "builtin.run_cycle", characterId = man, fps = 60 }).Data!["clipId"]!;
        var runClip = P8.Clip(s, run);
        Assert.Equal(60, runClip.Fps.FramesPerSecond);
        var seq = (string)P8.Ok(s, "sequence.create", new { characterId = man, name = "Mixed", fps = 30 }).Data!["sequenceId"]!;
        P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = walk, loops = 2 });
        var ev = new SequenceEvaluator(s.Project, s.Project.FindSequence(ObjectId.Parse(seq))!);
        var start = Math.Max(0, (long)Math.Round(ev.EndSeconds * 30) - 10);
        var to = Math.Min(SequenceEvaluator.DefaultOut(runClip).ToFrame(runClip.Fps) - 1, (long)Math.Round(60 * 1.5));
        var expected = ev.MatchFootPhase(start, runClip, 0, to, 1.0);
        var wrong = ev.MatchFootPhase(start, runClip, 0, to, 2.0); // the old factor speed × clipFps / seqFps
        var r = P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = run, blendFrames = 10, matchFootPhase = true }).Data!;
        Assert.Equal(expected, (long)r["inFrame"]!);
        Assert.NotNull(wrong);
    }

    // ------------------------------------------------------------------ #12 archive validation

    [Fact]
    public void Review12_HandCraftedArchives_AreRefusedWithTheObjectNamed()
    {
        void Refused(string what, Action<Project> damage, string expect)
        {
            var (s, man, _) = P8.Scene(cesium: false);
            var walk = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
            var seq = (string)P8.Ok(s, "sequence.create", new { characterId = man, name = "Seq" }).Data!["sequenceId"]!;
            P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = walk });
            P8.Ok(s, "template.save_clip", new { clipId = walk, name = "Tpl" });
            P8.Ok(s, "prop.add", new { name = "Box" });
            RoundTrip(s.Project); // valid before the damage
            damage(s.Project);
            var e = Assert.Throws<ProjectFormatException>(() => RoundTrip(s.Project));
            Assert.Contains(expect, e.Message);
            Assert.Equal("invalid_json", e.Code);
            _ = what;
        }
        Refused("duplicate prop ids", p => p.Props.Add(p.Props[0]), "duplicate prop id");
        Refused("loops", p => p.Sequences[0].Items[0].Loops = int.MaxValue, "Sequence 'Seq'");
        Refused("start frame", p => p.Sequences[0].Items[0].StartFrame = long.MaxValue / 2, "Sequence 'Seq'");
        Refused("blend", p => p.Sequences[0].Items[0].BlendInFrames = 20_000_000, "Sequence 'Seq'");
        Refused("speed", p => p.Sequences[0].Items[0].Speed = 1e-9, "Sequence 'Seq'");
        Refused("too many items", p => p.Sequences[0].Items.AddRange(Enumerable.Range(0, ClipSequence.MaxItems).Select(_ => new SequenceItem { Clip = p.Clips[0].Id })), "items (at most");
        Refused("template duplicate bones", p => p.Templates[0].Rig.Bones.Add(p.Templates[0].Rig.Bones[^1]), "Template 'Tpl'");
        Refused("template missing parent", p => p.Templates[0].Rig.Bones[^1].Parent = ObjectId.New(), "Template 'Tpl'");
        Refused("template cycle", p =>
        {
            var bones = p.Templates[0].Rig.Bones;
            bones[0].Parent = bones[^1].Id; // root → last bone: a parent cycle
        }, "Template 'Tpl'");
        Refused("template keys unsorted", p =>
        {
            var k = p.Templates[0].Clip.Tracks.First(t => t.Keys.Count > 1).Keys;
            (k[0], k[1]) = (k[1], k[0]);
        }, "Template 'Tpl'");
        Refused("template null rig", p => p.Templates[0].Rig = null!, "Template 'Tpl'");
        Refused("sequence null item", p => p.Sequences[0].Items.Add(null!), "Sequence 'Seq'");
    }

    // ------------------------------------------------------------------ #13 root speed in blends

    [Fact]
    public void Review13_BlendRootSpeed_StaysWithinTheTwoClips()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var walk = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
        var run = P8.ApplyTemplate(s, "builtin.run_cycle", man);
        var rig = P8.RigOf(s, man);
        var rm = SequenceEvaluator.RootMotionBoneOf(rig);
        double MaxStep(Clip c)
        {
            var max = 0.0;
            var loopEnd = SequenceEvaluator.DefaultOut(c).ToFrame(c.Fps);
            for (var f = 1; f <= loopEnd; f++)
            {
                var d = P8.Model(rig, c, f)[rm].Translation - P8.Model(rig, c, f - 1)[rm].Translation;
                max = Math.Max(max, Math.Sqrt(d.X * d.X + d.Z * d.Z));
            }
            return max;
        }
        var limit = Math.Max(MaxStep(P8.Clip(s, walk)), MaxStep(P8.Clip(s, run)));
        foreach (var inFrame in new[] { 0, 7 })
        {
            var seq = (string)P8.Ok(s, "sequence.create", new { characterId = man, name = "S" + inFrame }).Data!["sequenceId"]!;
            P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = walk, loops = 3, inFrame });
            P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = run, loops = 2, blendFrames = 10 });
            var ev = new SequenceEvaluator(s.Project, s.Project.FindSequence(ObjectId.Parse(seq))!);
            var e = new RigEvaluator(rig);
            Vec3? prev = null;
            var worst = 0.0;
            for (var f = 0; f <= ev.LastFrame; f++)
            {
                var p = e.Model(ev.Evaluate(f).Pose)[rm].Translation;
                if (prev is { } q) worst = Math.Max(worst, Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Z - q.Z) * (p.Z - q.Z)));
                prev = p;
            }
            Assert.True(worst <= limit + 2e-3, $"inFrame {inFrame}: root step {worst:0.0000} m/frame > clips' max {limit:0.0000}");
        }
    }

    // ------------------------------------------------------------------ #14 external images and extensions

    [Fact]
    public void Review14_ExternalImages_AndExtensions_AreDroppedFromPropModels()
    {
        var json = Triangle(-0.5, 0.5);
        json["images"] = new JsonArray(new JsonObject { ["uri"] = "texture.png" });
        json["samplers"] = new JsonArray(new JsonObject());
        json["textures"] = new JsonArray(new JsonObject { ["source"] = 0, ["sampler"] = 0 });
        json["materials"] = new JsonArray(new JsonObject
        {
            ["pbrMetallicRoughness"] = new JsonObject { ["baseColorTexture"] = new JsonObject { ["index"] = 0, ["extensions"] = new JsonObject { ["KHR_texture_transform"] = new JsonObject() } } },
            ["extensions"] = new JsonObject { ["KHR_materials_emissive_strength"] = new JsonObject { ["emissiveStrength"] = 2 } },
        });
        json["meshes"]![0]!["primitives"]![0]!["material"] = 0;
        json["extensionsUsed"] = new JsonArray("KHR_texture_transform", "KHR_materials_emissive_strength");
        var glb = GlbContainer.Parse(GlbContainer.Write(json, new byte[36]));
        var b = new GltfBuilder(new JsonObject { ["asset"] = Asset() });
        var warnings = new List<string>();
        PropExport.AppendGlbScene(b, glb, warnings, "Tex");
        Assert.Contains(warnings, w => w.Contains("texture.png"));
        Assert.Contains(warnings, w => w.Contains("KHR_materials_emissive_strength"));
        var outJson = GlbContainer.Parse(b.ToGlb()).Json;
        Assert.Null(outJson["images"]?.AsArray().FirstOrDefault());
        Assert.Null(outJson["textures"]?.AsArray().FirstOrDefault());
        var mat = outJson["materials"]![0]!.AsObject();
        Assert.Null(mat["extensions"]);
        Assert.Null(mat["pbrMetallicRoughness"]!["baseColorTexture"]);
        var issues = new List<string>();
        ExportValidator.CheckStructure(GlbContainer.Parse(b.ToGlb()), issues);
        Assert.DoesNotContain(issues, i => i.Contains("image") || i.Contains("Extension") || i.Contains("texture"));

        // The validator's new checks catch what the old copy produced.
        var bad = GlbContainer.Parse(GlbContainer.Write(new JsonObject
        {
            ["asset"] = Asset(), ["images"] = new JsonArray(new JsonObject()), ["textures"] = new JsonArray(new JsonObject { ["source"] = 3 }),
            ["materials"] = new JsonArray(new JsonObject { ["extensions"] = new JsonObject { ["KHR_x"] = new JsonObject() } }),
        }, Array.Empty<byte>()));
        issues.Clear();
        ExportValidator.CheckStructure(bad, issues);
        Assert.Contains(issues, i => i.Contains("images[0]"));
        Assert.Contains(issues, i => i.Contains("textures[0]"));
        Assert.Contains(issues, i => i.Contains("KHR_x"));
    }

    // ------------------------------------------------------------------ #15 events of looping items

    [Fact]
    public void Review15_BakedEvents_UseTheWrappedTiming()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var walk = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
        var clip = P8.Clip(s, walk);
        Assert.True(clip.Loop.Enabled);
        clip.Events.Add(new AnimationEvent { Name = "step", Time = Flicks.FromFrame(5, clip.Fps) });
        var seq = (string)P8.Ok(s, "sequence.create", new { characterId = man, name = "S" }).Data!["sequenceId"]!;
        var item = (string)P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = walk, inFrame = 12, loops = 2 }).Data!["itemId"]!;
        var ev = new SequenceEvaluator(s.Project, s.Project.FindSequence(ObjectId.Parse(seq))!);
        var baked = P8.Clip(s, (string)P8.Ok(s, "sequence.bake", new { sequenceId = seq }).Data!["clipId"]!);
        var events = baked.Events.Where(e => e.Name == "step").ToList();
        Assert.Equal(2, events.Count);
        foreach (var e in events)
        {
            var frame = e.Time.ToFrame(baked.Fps);
            var played = ev.Evaluate(frame).Active.Single(a => a.ItemId == ObjectId.Parse(item)).ClipFrame;
            Assert.True(Math.Abs(played - 5) <= 0.5, $"event at sequence frame {frame} plays clip frame {played:0.##}, not 5");
        }
    }
}
