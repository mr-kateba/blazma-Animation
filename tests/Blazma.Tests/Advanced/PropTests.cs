using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Export;
using Blazma.Motion;
using Blazma.Rigging;
using Blazma.Tests.Rigging.Import;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.Advanced;

public class PropTests
{
    /// <summary>A sword (box 4×4×90 cm) in the mannequin's right hand.</summary>
    static string Sword(ProjectSession s, string ch) =>
        (string)P8.Ok(s, "prop.add", new { name = "Sword", sizeX = 0.04, sizeY = 0.9, sizeZ = 0.04, characterId = ch, bone = H.Hand(Side.Right), x = -0.08, y = 0.35, z = 0, rz = 0 })
            .Data!["propId"]!;

    /// <summary>Phase 8 acceptance 4: a sword attached to the hand, animated with key.set, exported and validated.</summary>
    [Fact]
    public void Acceptance_Phase8_SwordInHand_ExportRoundTrip()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var attack = P8.ApplyTemplate(s, "builtin.attack_swing", man);
        var sword = Sword(s, man);
        // Key the grip: frame 0 as placed, frame 18 rolled 40° in the hand.
        P8.Ok(s, "key.set", new { clipId = attack, frame = 0, props = "Sword" });
        P8.Ok(s, "prop.set_transform", new { propId = sword, rx = 40 });
        P8.Ok(s, "key.set", new { clipId = attack, frame = 18, props = sword });
        var clip = P8.Clip(s, attack);
        Assert.Equal(2, clip.Tracks.Count(t => t.Target.Kind == TargetKind.Prop));
        var prop = s.Project.FindProp(ObjectId.Parse(sword))!;
        var rig = P8.RigOf(s, man);

        // World = hand · offset; the offset follows the keys.
        var hand = P8.Bone(rig, H.Hand(Side.Right));
        var w9 = PropEvaluator.WorldAt(s.Project, prop, clip, rig, Flicks.FromFrame(9, clip.Fps));
        var expected = P8.Model(rig, clip, 9)[hand] * PropEvaluator.Local(prop, clip, Flicks.FromFrame(9, clip.Fps));
        Assert.True((w9.Translation - expected.Translation).Length() < 1e-12);
        Assert.InRange(Quat.AngleBetween(PropEvaluator.Local(prop, clip, Flicks.FromFrame(9, clip.Fps)).Rotation, Quat.Identity) * 180 / Math.PI, 5, 35);

        // A free ball thrown across the scene (world keys) is exported too.
        var ball = (string)P8.Ok(s, "prop.add", new { name = "Ball", shape = "sphere", sizeX = 0.22, sizeY = 0.22, sizeZ = 0.22, x = 1, y = 0.11, z = 0 }).Data!["propId"]!;
        P8.Ok(s, "key.set", new { clipId = attack, frame = 0, props = "Ball" });
        P8.Ok(s, "prop.set_transform", new { propId = ball, x = -1, y = 1.5 });
        P8.Ok(s, "key.set", new { clipId = attack, frame = 30, props = "Ball" });

        using var dir = new TempDir();
        var path = dir.File("attack.glb");
        var r = P8.Ok(s, "animation.export", new { clipId = attack, characterId = man, path });
        var v = r.Data!["validation"]!;
        Assert.True((bool)v["ok"]!, v.ToJsonString());
        Assert.Equal(2, (int)v["propsChecked"]!);
        Assert.InRange((double)v["maxPropPositionError"]!, 0, 5e-4);

        // The file: the sword node is a child of the hand node, the ball a scene root; both have TRS channels.
        var glb = GlbContainer.Parse(File.ReadAllBytes(path));
        var nodes = GltfAnimationReader.ReadNodes(glb.Json);
        var swordNode = nodes.Single(n => n.Name == "Sword");
        Assert.Equal(hand, swordNode.Parent);
        var ballNode = nodes.Single(n => n.Name == "Ball");
        Assert.Null(ballNode.Parent);
        Assert.Contains(ballNode.Index, ((JsonArray)glb.Json["scenes"]![0]!["nodes"]!).Select(x => (int)x!));
        var anim = GltfAnimationReader.ReadAnimations(glb).Single();
        foreach (var node in new[] { swordNode.Index, ballNode.Index })
            Assert.Equal(3, anim.Channels.Count(c => c.Node == node));
        Assert.True(swordNode.Children.Length == 1 && nodes[swordNode.Children[0]].Mesh is not null);
        var sidecar = JsonNode.Parse(File.ReadAllText(AnimationExporter.SidecarPathFor(path)))!;
        Assert.Equal("hand_r", (string?)sidecar["props"]![0]!["attachedTo"]);

        // A re-read validation (no bake reuse) agrees, and a tampered prop channel is caught.
        Assert.True(P8.Ok(s, "animation.validate", new { path, clipId = attack, characterId = man }).Data!["ok"]!.GetValue<bool>());
        var tampered = ExportValidator.Validate(TamperPropNode(File.ReadAllBytes(path), swordNode.Index), s.Project, clip, rig);
        Assert.False(tampered.Ok);
        Assert.Contains(tampered.Issues, i => i.Contains("prop"));

        // Exporting without props leaves them out.
        var bare = AnimationExporter.Export(s.Project, clip, rig, new ExportOptions { IncludeProps = false });
        Assert.DoesNotContain(GltfAnimationReader.ReadNodes(GlbContainer.Parse(bare.Glb).Json), n => n.Name == "Sword");
    }

    /// <summary>Moves a prop node's parent so the world check fails.</summary>
    static byte[] TamperPropNode(byte[] glbBytes, int node)
    {
        var glb = GlbContainer.Parse(glbBytes);
        var json = glb.Json;
        foreach (var n in (JsonArray)json["nodes"]!)
            if (n!["children"] is JsonArray c && c.Any(x => (int)x! == node))
                c.Remove(c.First(x => (int)x! == node));
        ((JsonArray)json["scenes"]![0]!["nodes"]!).Add(node);
        return GlbContainer.Write(json, glb.Bin);
    }

    [Fact]
    public void PropOnImportedCharacter_And_ImportedMeshProp_ExportAndValidate()
    {
        var (s, _, ces) = P8.Scene();
        var wave = P8.ApplyTemplate(s, "builtin.wave", ces);
        var r = P8.Ok(s, "prop.import", new { path = RepoPaths.Sample("models/khronos/RiggedSimple.glb"), name = "Staff", characterId = ces, bone = "Skeleton_arm_joint_R__3_" });
        Assert.Contains(r.Warnings, w => w.Message.Contains("skins"));
        var propId = (string)r.Data!["propId"]!;
        var prop = s.Project.FindProp(ObjectId.Parse(propId))!;
        Assert.Equal(PropShape.Mesh, prop.Shape);
        Assert.NotNull(s.Project.FindAsset(prop.Asset));
        Assert.True(prop.Size.Y > 1, $"bounds {prop.Size}"); // RiggedSimple is a long cylinder
        P8.Ok(s, "prop.set_transform", new { propId, sx = 0.05, sy = 0.05, sz = 0.05 });
        P8.Ok(s, "key.set", new { clipId = wave, frame = 0, props = propId });

        using var dir = new TempDir();
        var path = dir.File("wave.glb");
        var v = P8.Ok(s, "animation.export", new { clipId = wave, characterId = ces, path }).Data!["validation"]!;
        Assert.True((bool)v["ok"]!, v.ToJsonString());
        Assert.Equal(1, (int)v["propsChecked"]!);
        var glb = GlbContainer.Parse(File.ReadAllBytes(path));
        var nodes = GltfAnimationReader.ReadNodes(glb.Json);
        var node = nodes.Single(n => n.Name == "Staff");
        Assert.Equal(P8.RigOf(s, ces).Source.NodeIndex[P8.RigOf(s, ces).FindBoneByName("Skeleton_arm_joint_R__3_")!.Id.ToString()], node.Parent);
        // The copied mesh keeps no skin; the original character keeps its skin.
        Assert.Single((JsonArray)glb.Json["skins"]!);
        Assert.All(nodes.Where(n => n.Index > node.Index), n => Assert.Null(n.Skin));

        // Undo the import: prop and its embedded file are gone; redo brings both back.
        var assets = s.Project.Assets.Count;
        s.Undo(CommandOrigin.Test); // key.set
        s.Undo(CommandOrigin.Test); // set_transform
        s.Undo(CommandOrigin.Test); // import
        Assert.Empty(s.Project.Props);
        Assert.Equal(assets - 1, s.Project.Assets.Count);
        s.Redo(CommandOrigin.Test);
        Assert.Single(s.Project.Props);
        Assert.Equal(assets, s.Project.Assets.Count);
    }

    [Fact]
    public void AttachDetach_KeepWorldPlacement_AndUndoIsExact()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        P8.Ok(s, "pose.set_bone", new { characterId = man, bone = H.UpperArm(Side.Right), rx = 0, ry = 0, rz = 70 });
        var box = (string)P8.Ok(s, "prop.add", new { name = "Box", x = 0.5, y = 1.2, z = 0.3, ry = 30 }).Data!["propId"]!;
        string Snap() => P8.Json(s.Project.Props);
        var before = Snap();
        var world0 = PropCommandsWorld(s, box);

        var a = P8.Ok(s, "prop.attach", new { propId = box, characterId = man, bone = H.Hand(Side.Right) }).Data!;
        Assert.Equal("hand_r", (string?)a["attachedTo"]!["bone"]);
        AssertSame(world0, PropCommandsWorld(s, box));
        // Moving the arm carries the prop.
        P8.Ok(s, "pose.set_bone", new { characterId = man, bone = H.UpperArm(Side.Right), rx = 0, ry = 0, rz = 20 });
        var moved = PropCommandsWorld(s, box);
        Assert.True((moved.Translation - world0.Translation).Length() > 0.1);
        P8.Ok(s, "prop.detach", new { propId = box });
        AssertSame(moved, PropCommandsWorld(s, box));
        Assert.Equal("not_attached", P8.Err(P8.Run(s, "prop.detach", new { propId = box })));
        Assert.Equal("invalid_args", P8.Err(P8.Run(s, "prop.add", new { name = "x", characterId = man })));
        Assert.Equal("invalid_args", P8.Err(P8.Run(s, "prop.add", new { name = "x", shape = "torus" })));
        Assert.Equal("not_found", P8.Err(P8.Run(s, "prop.attach", new { propId = box, characterId = man, bone = "tail" })));

        // Dragging = one undo step.
        using (s.BeginTransaction("drag", CommandOrigin.Test))
        {
            P8.Ok(s, "prop.set_transform", new { propId = box, x = 0.1 });
            P8.Ok(s, "prop.set_transform", new { propId = box, x = 0.2 });
            P8.Ok(s, "prop.set_transform", new { propId = box, x = 0.3 });
        }
        Assert.Equal(0.3, s.Project.FindProp(ObjectId.Parse(box))!.Transform.Translation.X, 12);
        s.Undo(CommandOrigin.Test);
        s.Undo(CommandOrigin.Test); // detach
        s.Undo(CommandOrigin.Test); // arm pose
        s.Undo(CommandOrigin.Test); // attach
        Assert.Equal(before, Snap());
        P8.Ok(s, "prop.remove", new { propId = box });
        Assert.Empty(s.Project.Props);
        s.Undo(CommandOrigin.Test);
        Assert.Equal(before, Snap());

        var ev = P8.Ok(s, "prop.evaluate", new { propId = box }).Data!;
        Assert.Equal(0.5, (double)ev["world"]!["position"]![0]!, 6);
    }

    static Transform PropCommandsWorld(ProjectSession s, string id)
    {
        var data = P8.Ok(s, "prop.evaluate", new { propId = id }).Data!["world"]!;
        var p = data["position"]!.AsArray().Select(x => (double)x!).ToArray();
        var q = data["quaternion"]!.AsArray().Select(x => (double)x!).ToArray();
        return new Transform(new Vec3(p[0], p[1], p[2]), new Quat(q[0], q[1], q[2], q[3]), Vec3.One);
    }

    static void AssertSame(Transform a, Transform b)
    {
        Assert.True((a.Translation - b.Translation).Length() < 1e-4, $"{a.Translation} vs {b.Translation}");
        Assert.True(Quat.AngleBetween(a.Rotation, b.Rotation) < 1e-6);
    }

    [Fact]
    public void KeySet_PropsOnly_AndCopyPaste_KeepPropTracks()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var clip = (string)P8.Ok(s, "clip.create", new { name = "Props", durationFrames = 10 }).Data!["clipId"]!;
        var box = (string)P8.Ok(s, "prop.add", new { name = "Box" }).Data!["propId"]!;
        Assert.Equal("invalid_args", P8.Err(P8.Run(s, "key.set", new { clipId = clip, frame = 0 })));
        Assert.Equal("not_found", P8.Err(P8.Run(s, "key.set", new { clipId = clip, frame = 0, props = "Nope" })));
        P8.Ok(s, "key.set", new { clipId = clip, frame = 0, props = "Box" });
        var c = P8.Clip(s, clip);
        Assert.True(c.Skeleton.IsEmpty); // props only: the clip is not bound to a rig
        Assert.All(c.Tracks, t => Assert.Equal(TargetKind.Prop, t.Target.Kind));
        var copied = (string)P8.Ok(s, "key.copy", new { clipId = clip }).Data!["keysJson"]!;
        Assert.Contains("\"prop\"", copied);
        P8.Ok(s, "key.paste", new { clipId = clip, keysJson = copied, frame = 5 });
        Assert.All(P8.Clip(s, clip).Tracks, t => Assert.Equal(2, t.Keys.Count));
        // Keying a character and a prop together.
        P8.Ok(s, "key.set", new { clipId = clip, characterId = man, frame = 8, props = box });
        Assert.Contains(P8.Clip(s, clip).Tracks, t => t.Target.Kind == TargetKind.Bone);
    }
}
