using System.IO.Compression;
using System.Text.Json.Nodes;
using Blazma.AI;
using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;
using Blazma.Tests.Rigging.Import;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.Advanced;

public class TemplateAndSchemaTests
{
    static readonly string[] Builtins =
        { "builtin.idle_breathe", "builtin.walk_cycle", "builtin.run_cycle", "builtin.jump", "builtin.wave", "builtin.attack_swing" };

    /// <summary>Phase 8 acceptance 5: every built-in template on the mannequin and on CesiumMan; user templates saved and re-applied.</summary>
    [Fact]
    public void Acceptance_Phase8_Templates_BuiltinAndUser_OnMannequinAndCesiumMan()
    {
        var (s, man, ces) = P8.Scene();
        var list = P8.Ok(s, "template.list", new { }).Data!["templates"]!.AsArray();
        Assert.Equal(Builtins, list.Where(t => (string)t!["kind"]! == "builtin").Select(t => (string)t!["templateId"]!));

        foreach (var ch in new[] { man, ces })
        {
            var rig = P8.RigOf(s, ch);
            foreach (var id in Builtins)
            {
                var clipId = P8.ApplyTemplate(s, id, ch);
                var clip = P8.Clip(s, clipId);
                Assert.False(clip.IsDraft, id);
                Assert.Equal(rig.Id, clip.Skeleton);
                Assert.Equal(id, clip.Metadata["template"]);
                Assert.True(clip.Tracks.Count >= 10, $"{id}: {clip.Tracks.Count} tracks");
                Assert.All(clip.Tracks, t => Assert.All(t.Keys, k => Assert.All(k.Value, v => Assert.True(double.IsFinite(v)))));
                var cycle = BuiltinTemplates.Find(id)!.Loop;
                Assert.Equal(cycle, clip.Loop.Enabled);
                if (cycle)
                {
                    // The loop end repeats the first pose (apart from travel): seamless.
                    var a = P8.Model(rig, clip, 0);
                    var b = P8.Model(rig, clip, clip.Loop.End.ToFrame(clip.Fps));
                    var pelvis = P8.Bone(rig, H.Pelvis);
                    var shift = b[pelvis].Translation - a[pelvis].Translation;
                    shift = new Vec3(shift.X, 0, shift.Z);
                    for (var i = 0; i < a.Length; i++)
                        Assert.True((b[i].Translation - shift - a[i].Translation).Length() < 0.02, $"{id} on {rig.Name}: bone {rig.Bones[i].Name} seam");
                }
            }
            // Locomotion moves forward; the attack swings the right hand above the shoulders.
            var walk = P8.Clip(s, P8.ApplyTemplate(s, "builtin.walk_cycle", ch, 3));
            Assert.InRange(walk.DurationFrames, 85, 95);
            var pz = P8.Model(rig, walk, walk.DurationFrames - 1)[P8.Bone(rig, H.Pelvis)].Translation.Z - P8.Model(rig, walk, 0)[P8.Bone(rig, H.Pelvis)].Translation.Z;
            Assert.True(pz > 1.0 * (ch == man ? 1 : 0.5), $"{rig.Name} walks {pz:0.00} m");
            var attack = P8.Clip(s, P8.ApplyTemplate(s, "builtin.attack_swing", ch));
            var hand = P8.Bone(rig, H.Hand(Side.Right));
            var head = P8.Bone(rig, H.Head);
            Assert.Contains(Enumerable.Range(0, (int)attack.DurationFrames), f => P8.Model(rig, attack, f)[hand].Translation.Y > P8.Model(rig, attack, f)[head].Translation.Y);
        }

        // Undo removes the applied clip; redo brings the same clip back.
        var count = s.Project.Clips.Count;
        var lastId = s.Project.Clips[^1].Id;
        var json = P8.Json(s.Project.Clips[^1]);
        s.Undo(CommandOrigin.Test);
        Assert.Equal(count - 1, s.Project.Clips.Count);
        s.Redo(CommandOrigin.Test);
        Assert.Equal(json, P8.Json(s.Project.FindClip(lastId)));

        // User template: a mannequin clip saved, applied to CesiumMan (scaled, feet locked) at another length.
        var source = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
        var t = (string)P8.Ok(s, "template.save_clip", new { clipId = source, name = "My walk", description = "test" }).Data!["templateId"]!;
        Assert.Contains(P8.Ok(s, "template.list", new { }).Data!["templates"]!.AsArray(), x => (string)x!["templateId"]! == t && (string)x["kind"]! == "project");
        var applied = P8.Ok(s, "template.apply", new { templateId = t, characterId = ces, durationSeconds = 4.4 });
        var clipOnCes = P8.Clip(s, (string)applied.Data!["clipId"]!);
        Assert.False(clipOnCes.IsDraft);
        Assert.Equal(P8.RigOf(s, ces).Id, clipOnCes.Skeleton);
        Assert.InRange(clipOnCes.DurationFrames, 128, 134); // twice as long as the 2.2 s source
        Assert.NotNull(applied.Data["footLock"]);
        // Deleting the source clip does not break the template (it holds a copy and a rig snapshot).
        P8.Ok(s, "clip.delete", new { clipId = source });
        P8.Ok(s, "template.apply", new { templateId = t, characterId = man });

        Assert.Equal("not_found", P8.Err(P8.Run(s, "template.apply", new { templateId = "builtin.moonwalk", characterId = man })));
        Assert.Equal("not_found", P8.Err(P8.Run(s, "template.delete", new { templateId = "builtin.walk_cycle" })));
        Assert.Equal("invalid_args", P8.Err(P8.Run(s, "template.apply", new { templateId = "builtin.wave", characterId = man, durationSeconds = 100 })));
        var templates = P8.Json(s.Project.Templates);
        P8.Ok(s, "template.delete", new { templateId = t });
        Assert.Empty(s.Project.Templates);
        s.Undo(CommandOrigin.Test);
        Assert.Equal(templates, P8.Json(s.Project.Templates));
    }

    [Fact]
    public void Template_NotHumanoid_IsRefused()
    {
        var s = new ProjectSession(Project.CreateNew("x"));
        var r = P8.Ok(s, "rig.import", new { path = RepoPaths.Sample("models/khronos/RiggedSimple.glb") });
        var ch = (string)r.Data!["characterId"]!;
        Assert.Equal("not_humanoid", P8.Err(P8.Run(s, "template.apply", new { templateId = "builtin.walk_cycle", characterId = ch })));
        var clip = (string)P8.Ok(s, "clip.create", new { name = "c" }).Data!["clipId"]!;
        Assert.Equal("invalid_args", P8.Err(P8.Run(s, "template.save_clip", new { clipId = clip, name = "t" })));
        Assert.Equal("not_humanoid", P8.Err(P8.Run(s, "template.save_clip", new { clipId = clip, name = "t", characterId = ch })));
    }

    [Fact]
    public void WalkTemplate_ExportsInPlace_WithAnAttachedProp()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var walk = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
        P8.Ok(s, "prop.add", new { name = "Lantern", characterId = man, bone = H.Hand(Side.Left), y = -0.2 });
        using var dir = new TempDir();
        foreach (var mode in new[] { "keep", "in_place" })
        {
            var v = P8.Ok(s, "animation.export", new { clipId = walk, characterId = man, path = dir.File($"walk_{mode}.glb"), rootMotion = mode }).Data!["validation"]!;
            Assert.True((bool)v["ok"]!, v.ToJsonString());
            Assert.Equal(1, (int)v["propsChecked"]!);
        }
    }

    // ------------------------------------------------------------------ schema v4

    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", name);

    [Fact]
    public void Acceptance_Phase8_V3FixtureOpensAndUpgradesToV4_WithoutLosingData()
    {
        var p = ProjectFile.Load(Fixture("schema-v3/project-v3.blazanim"));
        Assert.Equal("Schema v3 fixture", p.Name);
        var clip = Assert.Single(p.Clips);
        Assert.Equal("Walk", clip.Name);
        Assert.Equal(24, clip.DurationFrames);
        Assert.True(clip.Loop.Enabled);
        var marker = Assert.Single(clip.Markers);
        Assert.Equal(KeyPhase.Contact, marker.Phase);
        Assert.Contains(clip.Tracks, t => t.Channel == TrackChannels.Pinned);
        Assert.Single(p.Characters);
        Assert.Single(p.PoseLibrary);
        Assert.Empty(p.Sequences);
        Assert.Empty(p.Props);
        Assert.Empty(p.Templates);

        // Phase 8 features work on the upgraded project, and it saves as schema 4.
        var s = new ProjectSession(p);
        var ch = p.Characters[0].Id.ToString();
        P8.Ok(s, "sequence.create", new { characterId = ch, name = "S" });
        P8.Ok(s, "sequence.add_clip", new { sequenceId = p.Sequences[0].Id.ToString(), clipId = clip.Id.ToString() });
        P8.Ok(s, "prop.add", new { name = "Ball", shape = "sphere" });
        using var dir = new TempDir();
        var path = dir.File("v4.blazanim");
        ProjectFile.Save(p, path);
        using (var zip = ZipFile.OpenRead(path))
        using (var r = new StreamReader(zip.GetEntry("project.json")!.Open()))
            Assert.Equal(4, (int)JsonNode.Parse(r.ReadToEnd())!["schemaVersion"]!);
        var again = ProjectFile.Load(path);
        Assert.Equal(P8.Json(p.Clips), P8.Json(again.Clips));
        Assert.Equal(P8.Json(p.Sequences), P8.Json(again.Sequences));
        Assert.Equal(P8.Json(p.Props), P8.Json(again.Props));
    }

    [Fact]
    public void V4_RoundTrip_SequencesPropsTemplatesAndPropTracks()
    {
        var (s, man, ces) = P8.Scene();
        var walk = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
        var seq = (string)P8.Ok(s, "sequence.create", new { characterId = man, name = "Seq", alignYaw = false }).Data!["sequenceId"]!;
        P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = walk, loops = 2, blendCurve = "ease_out", speed = 1.25 });
        var sword = (string)P8.Ok(s, "prop.add", new { name = "Sword", characterId = man, bone = "hand_r", x = 0.1, rz = 15 }).Data!["propId"]!;
        P8.Ok(s, "prop.import", new { path = RepoPaths.Sample("models/khronos/RiggedSimple.glb"), name = "Mesh" });
        P8.Ok(s, "key.set", new { clipId = walk, frame = 3, props = sword });
        P8.Ok(s, "template.save_clip", new { clipId = walk, name = "Walk T" });
        using var dir = new TempDir();
        var path = dir.File("p.blazanim");
        ProjectFile.Save(s.Project, path);
        var again = ProjectFile.Load(path);
        Assert.Equal(P8.Json(s.Project.Sequences), P8.Json(again.Sequences));
        Assert.Equal(P8.Json(s.Project.Props), P8.Json(again.Props));
        Assert.Equal(P8.Json(s.Project.Templates), P8.Json(again.Templates));
        Assert.Equal(P8.Json(s.Project.Clips), P8.Json(again.Clips));
        Assert.Equal(s.Project.Assets.Select(a => a.Data.Length), again.Assets.Select(a => a.Data.Length));
        Assert.Contains(again.Clips.SelectMany(c => c.Tracks), t => t.Target.Kind == TargetKind.Prop);
        Assert.Equal(BlendCurve.EaseOut, again.Sequences[0].Items[0].BlendCurve);
    }

    [Fact]
    public void V4_InvalidSequenceItemOrPropAsset_IsRefused()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var walk = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
        var seq = (string)P8.Ok(s, "sequence.create", new { characterId = man, name = "Seq" }).Data!["sequenceId"]!;
        P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = walk });
        P8.Ok(s, "prop.add", new { name = "Box" });
        using var dir = new TempDir();
        string Saved(Action<Project> damage, string name)
        {
            var copy = ProjectFile.Load(Save());
            damage(copy);
            var path = dir.File(name);
            ProjectFile.Save(copy, path);
            return path;
        }
        string Save() { var p = dir.File("ok.blazanim"); ProjectFile.Save(s.Project, p); return p; }
        Assert.Equal("invalid_json", Assert.Throws<ProjectFormatException>(() => ProjectFile.Load(Saved(p => p.Sequences[0].Items[0].Speed = 0, "a.blazanim"))).Code);
        Assert.Equal("invalid_json", Assert.Throws<ProjectFormatException>(() => ProjectFile.Load(Saved(p => { p.Props[0].Shape = PropShape.Mesh; p.Props[0].Asset = ObjectId.New(); }, "b.blazanim"))).Code);
        Assert.Equal("invalid_json", Assert.Throws<ProjectFormatException>(() => ProjectFile.Load(Saved(p => p.Props[0].Size = Vec3.Zero, "c.blazanim"))).Code);
    }

    [Fact]
    public void Phase8Commands_AreRegistered_WithTheRightEffects()
    {
        foreach (var (id, effect) in new[]
        {
            ("clip.retarget", CommandEffect.Mutating), ("clip.foot_lock", CommandEffect.Mutating), ("clip.contacts", CommandEffect.ReadOnly),
            ("sequence.create", CommandEffect.Mutating), ("sequence.add_clip", CommandEffect.Mutating), ("sequence.set_blend", CommandEffect.Mutating),
            ("sequence.update_item", CommandEffect.Mutating), ("sequence.update", CommandEffect.Mutating), ("sequence.remove", CommandEffect.Destructive),
            ("sequence.inspect", CommandEffect.ReadOnly), ("sequence.evaluate", CommandEffect.ReadOnly), ("sequence.bake", CommandEffect.Mutating),
            ("prop.add", CommandEffect.Mutating), ("prop.import", CommandEffect.External), ("prop.remove", CommandEffect.Destructive),
            ("prop.attach", CommandEffect.Mutating), ("prop.detach", CommandEffect.Mutating), ("prop.set_transform", CommandEffect.Mutating),
            ("prop.evaluate", CommandEffect.ReadOnly), ("template.list", CommandEffect.ReadOnly), ("template.apply", CommandEffect.Mutating),
            ("template.save_clip", CommandEffect.Mutating), ("template.delete", CommandEffect.Destructive),
        })
        {
            Assert.True(P8.Registry.TryGet(id, out var d), id);
            Assert.Equal(effect, d.Effect);
            Assert.Equal(effect, P8.Registry.All.Single(x => x.Id == id).Effect);
        }
        // project.inspect lists the new collections.
        var (s, man, _) = P8.Scene(cesium: false);
        P8.Ok(s, "prop.add", new { name = "Box" });
        var data = P8.Ok(s, "project.inspect", new { }).Data!;
        Assert.Equal(4, (int)data["schemaVersion"]!);
        Assert.Single(data["props"]!.AsArray());
        Assert.Empty(data["sequences"]!.AsArray());
        Assert.Empty(data["templates"]!.AsArray());
    }
}
