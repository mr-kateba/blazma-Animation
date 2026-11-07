using Blazma.AI;
using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Physics;
using Blazma.Rigging;
using Blazma.Rigging.Retargeting;
using Blazma.Tests.AI;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.Advanced;

public class RetargetAndFootLockTests
{
    /// <summary>World directions of every mapped bone (towards its standard child) of the target follow the source within 1°.</summary>
    static double MaxDirectionError(Rig srcRig, Clip src, Rig dstRig, Clip dst)
    {
        var dirs = P8.Directions(srcRig, dstRig);
        Assert.True(dirs.Count >= 9, $"only {dirs.Count} comparable bones");
        var worst = 0.0;
        var where = "";
        for (var f = 0; f <= src.DurationFrames; f++)
        {
            var a = P8.Model(srcRig, src, f);
            var b = P8.Model(dstRig, dst, f);
            foreach (var (std, child) in dirs)
            {
                var e = P8.AngleDeg(P8.Dir(srcRig, a, std, child), P8.Dir(dstRig, b, std, child));
                if (e > worst) { worst = e; where = $"{std}→{child} frame {f}"; }
            }
        }
        Assert.True(worst <= 1.0, $"{worst:0.###}° at {where}");
        return worst;
    }

    static Clip Walk(Rig rig) => BuiltinTemplates.Apply(BuiltinTemplates.Find("builtin.walk_cycle")!, rig, FrameRate.Fps30, 2.2, ObjectId.New(), "Walk").Clip;

    [Theory]
    [InlineData("mannequin", "CesiumMan")]
    [InlineData("CesiumMan", "mannequin")]
    [InlineData("mannequin", "RiggedFigure")]
    public void ClipRetargeter_KeepsWorldBoneDirections_Within1Degree(string from, string to)
    {
        Rig Make(string n) => n switch { "mannequin" => MannequinFactory.Create(), "CesiumMan" => ProceduralAndRetargetTests.CesiumMan(), _ => ProceduralAndRetargetTests.RiggedFigure() };
        var src = Make(from);
        var dst = Make(to);
        var clip = Walk(src);
        var r = ClipRetargeter.Retarget(clip, src, dst, new ClipRetargetOptions { Name = "R" });
        Assert.Equal(dst.Id, r.Clip.Skeleton);
        Assert.False(r.Clip.IsDraft);
        Assert.Equal(clip.DurationFrames, r.Clip.DurationFrames);
        Assert.InRange(MaxDirectionError(src, clip, dst, r.Clip), 0, 1.0);
        Assert.All(r.Clip.Tracks, t => Assert.All(t.Keys, k => Assert.All(k.Value, v => Assert.True(double.IsFinite(v)))));
    }

    [Fact]
    public void ClipRetargeter_ScalesHipTravelByLegLength_AndLeavesUnmappedBonesAtRest()
    {
        var src = MannequinFactory.Create();
        var dst = ProceduralAndRetargetTests.CesiumMan();
        var clip = Walk(src);
        var r = ClipRetargeter.Retarget(clip, src, dst, new ClipRetargetOptions());
        var map = new HumanoidRetargetMap(RetargetSkeleton.FromRig(src), dst);
        Assert.Equal(map.Scale, r.Scale, 12);
        Assert.InRange(r.Scale, 0.4, 0.9); // CesiumMan is much smaller than the 1.80 m mannequin
        int sp = P8.Bone(src, H.Pelvis), dp = P8.Bone(dst, H.Pelvis);
        var s0 = P8.Model(src, clip, 0)[sp].Translation;
        var d0 = P8.Model(dst, r.Clip, 0)[dp].Translation;
        for (var f = 0; f <= clip.DurationFrames; f += 5)
        {
            var ds = P8.Model(src, clip, f)[sp].Translation - s0;
            var dd = P8.Model(dst, r.Clip, f)[dp].Translation - d0;
            Assert.True((dd - ds * r.Scale).Length() < 2e-3, $"frame {f}: {dd} vs {ds * r.Scale}");
        }
        Assert.Contains(r.Warnings, w => w.Contains("stay at rest"));
        var driven = r.Clip.Tracks.Select(t => t.Target.Id).ToHashSet();
        foreach (var b in dst.Bones.Where(b => dst.StandardNameOf(b.Id) is null)) Assert.DoesNotContain(b.Id, driven);

        var noPelvis = MannequinFactory.Create();
        noPelvis.HumanoidMap.RemoveAll(x => x.Standard == H.Pelvis);
        Assert.Equal("not_humanoid", Assert.Throws<RetargetException>(() => ClipRetargeter.Retarget(clip, src, noPelvis, new ClipRetargetOptions())).Code);
    }

    [Fact]
    public void ClipRetargeter_MapsPinnedIkTargets_AndCopiesMarkersEventsAndLoop()
    {
        // A pinned-feet step on the mannequin (Phase 4 export fixture) onto CesiumMan.
        var (s, ch, clipId) = Export.ExportFixture.Walk();
        var src = Export.ExportFixture.RigOf(s, ch);
        var clip = s.Project.GetClip(ObjectId.Parse(clipId));
        clip.Loop = new LoopSettings { Enabled = true, Start = Flicks.FromFrame(2, clip.Fps), End = Flicks.FromFrame(20, clip.Fps) };
        var dst = ProceduralAndRetargetTests.CesiumMan();
        RigSetup.AddStandardIk(dst);
        var r = ClipRetargeter.Retarget(clip, src, dst, new ClipRetargetOptions());
        Assert.True(r.MappedControls >= 2, $"mapped {r.MappedControls}");
        var target = dst.Controls.Single(c => c.Name == "ik_foot_l");
        Assert.Contains(r.Clip.Tracks, t => t.Target.Id == target.Id && t.Channel == TrackChannels.Pinned && t.Keys[0].Value[0] == 1);
        // The pinned left foot stays planted on the target while it is planted on the source (frames 0–12).
        var foot = P8.Bone(dst, H.Foot(Side.Left));
        var at0 = P8.Model(dst, r.Clip, 0)[foot].Translation;
        for (var f = 1; f <= 12; f++) Assert.True((P8.Model(dst, r.Clip, f)[foot].Translation - at0).Length() < 1e-3, $"frame {f}");
        Assert.Equal(clip.Markers.Select(m => (m.Name, m.Time)), r.Clip.Markers.Select(m => (m.Name, m.Time)));
        Assert.Equal(clip.Events.Select(e => (e.Name, e.Time)), r.Clip.Events.Select(e => (e.Name, e.Time)));
        Assert.True(r.Clip.Loop.Enabled);
        Assert.Equal(clip.Loop.End, r.Clip.Loop.End);
    }

    /// <summary>Phase 8 acceptance 1: mannequin walk → CesiumMan (different proportions), feet planted, undoable.</summary>
    [Fact]
    public void Acceptance_Phase8_RetargetToImportedCharacter_FeetStayPlanted()
    {
        var (s, man, ces) = P8.Scene();
        var walk = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
        var before = s.Project.Clips.Count;
        var r = P8.Ok(s, "clip.retarget", new { clipId = walk, fromCharacterId = man, toCharacterId = ces, name = "Cesium walk" });
        var id = (string)r.Data!["clipId"]!;
        Assert.Equal(before + 1, s.Project.Clips.Count);
        var clip = P8.Clip(s, id);
        Assert.Equal("Cesium walk", clip.Name);
        Assert.False(clip.IsDraft);
        var rig = P8.RigOf(s, ces);
        Assert.Equal(rig.Id, clip.Skeleton);
        Assert.InRange((double)r.Data["scale"]!, 0.4, 0.9);

        // Feet locked: every locked foot reaches its planned IK target within 1 cm (maxAnchorError is measured on the edited clip),
        // and the sole points touching the ground stay put. The walk rolls heel → toe, so the ankle itself moves during a contact
        // (the old crude cycle kept it still, and this test used to pin the ankle to one anchor).
        var lockData = r.Data["footLock"]!;
        var spans = lockData["spans"]!.AsArray();
        Assert.True(spans.Count >= 4, $"{spans.Count} spans");
        Assert.InRange((double)lockData["maxAnchorError"]!, 0, 0.01);
        foreach (var span in spans)
        {
            var side = (string)span!["side"]! == "left" ? Side.Left : Side.Right;
            var drift = GaitMetrics.ContactDrift(clip, rig, side, (long)span["startFrame"]!, (long)span["endFrame"]!);
            Assert.True(drift <= 0.01, $"{side} contact {span["startFrame"]}–{span["endFrame"]} drifts {drift:0.000} m");
        }
        // Outside the locked legs, mapped bones still point like the source (within 1°).
        var src = P8.RigOf(s, man);
        var srcClip = P8.Clip(s, walk);
        var upper = P8.Directions(src, rig).Where(d => !d.Std.StartsWith("thigh") && !d.Std.StartsWith("calf") && !d.Std.StartsWith("foot")).ToList();
        for (var f = 0; f <= srcClip.DurationFrames; f += 3)
        {
            var a = P8.Model(src, srcClip, f);
            var b = P8.Model(rig, clip, f);
            foreach (var (std, child) in upper)
                Assert.True(P8.AngleDeg(P8.Dir(src, a, std, child), P8.Dir(rig, b, std, child)) <= 1.0, $"{std} frame {f}: {P8.AngleDeg(P8.Dir(src, a, std, child), P8.Dir(rig, b, std, child)):0.###}°");
        }
        // The character walks forward like the source, scaled.
        Assert.True(P8.Model(rig, clip, clip.DurationFrames)[P8.Bone(rig, H.Pelvis)].Translation.Z > 1.0);

        var json = P8.Json(clip);
        s.Undo(CommandOrigin.Test);
        Assert.Null(s.Project.FindClip(ObjectId.Parse(id)));
        s.Redo(CommandOrigin.Test);
        Assert.Equal(json, P8.Json(P8.Clip(s, id)));
    }

    [Fact]
    public void ClipRetarget_Command_Errors()
    {
        var (s, man, ces) = P8.Scene();
        var walk = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
        Assert.Equal("rig_mismatch", P8.Err(P8.Run(s, "clip.retarget", new { clipId = walk, fromCharacterId = ces, toCharacterId = man })));
        Assert.Equal("not_found", P8.Err(P8.Run(s, "clip.retarget", new { clipId = ObjectId.New().ToString(), fromCharacterId = man, toCharacterId = ces })));
        var rigId = P8.RigOf(s, ces).Id.ToString();
        P8.Ok(s, "rig.map_bone", new { rigId, bone = "Skeleton_torso_joint_1" });
        Assert.Equal("not_humanoid", P8.Err(P8.Run(s, "clip.retarget", new { clipId = walk, fromCharacterId = man, toCharacterId = ces })));
    }

    /// <summary>Idle on the mannequin whose hips drift forward 5 cm/s: the standing feet slide.</summary>
    static Clip SlidingIdle(Rig rig)
    {
        var provider = new ProceduralMotionProvider();
        var idle = provider.GenerateAsync(new MotionRequest("idle", 3, 30, 1), null, CancellationToken.None).GetAwaiter().GetResult();
        var drifting = idle with { RootTranslations = idle.RootTranslations.Select((p, i) => p + new Vec3(0, 0, 0.05 * i / idle.Fps)).ToArray() };
        var clip = MotionRetargeter.Retarget(drifting, rig, new RetargetOptions { FootCleanup = false, ReduceKeys = true, Name = "Sliding" }).Clip;
        clip.IsDraft = false;
        return clip;
    }

    static double Slide(Rig rig, Clip clip, Side side, long from, long to)
    {
        var foot = P8.Bone(rig, H.Foot(side));
        var total = 0.0;
        for (var f = from + 1; f <= to; f++)
        {
            var a = P8.Model(rig, clip, f - 1)[foot].Translation;
            var b = P8.Model(rig, clip, f)[foot].Translation;
            total += Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Z - a.Z, 2));
        }
        return total;
    }

    /// <summary>Phase 8 acceptance 2: foot locking pins sliding contacts, keeps other frames exactly, and undo is exact.</summary>
    [Fact]
    public void Acceptance_Phase8_FootLock_PinsContacts_ExactOutside_UndoExact()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var rig = P8.RigOf(s, man);
        var clip = SlidingIdle(rig);
        s.Project.Clips.Add(clip);
        var id = clip.Id.ToString();
        Assert.True(Slide(rig, clip, Side.Left, 30, 60) > 0.012, "the source slides");
        var original = P8.Json(clip);
        var originalModels = Enumerable.Range(0, (int)clip.DurationFrames + 1).Select(f => P8.Model(rig, clip, f)).ToArray();

        var r = P8.Ok(s, "clip.foot_lock", new { clipId = id, characterId = man, startFrame = 30, endFrame = 60, blendFrames = 4 });
        var d = r.Data!;
        Assert.Equal(2, d["spans"]!.AsArray().Count); // one contact per foot over the whole range
        Assert.InRange((double)d["maxAnchorError"]!, 0, 0.002);
        Assert.Equal(0, (double)d["maxUntouchedChange"]!);
        // Writes stay inside the range (review 8 #16): the contacts are cut at both edges, so they are partial (no ease there).
        Assert.Equal(30, (long)d["changedStartFrame"]!);
        Assert.Equal(60, (long)d["changedEndFrame"]!);
        foreach (var span in d["spans"]!.AsArray())
        {
            Assert.Equal(30, (long)span!["startFrame"]!); // partial contacts: locked right up to the cut edges, no ease
            Assert.Equal(60, (long)span["endFrame"]!);
        }
        var locked = P8.Clip(s, id);
        foreach (var side in P8.Sides) Assert.InRange(Slide(rig, locked, side, 30, 60), 0, 0.01);
        // Every frame outside the range gives exactly the original pose.
        for (var f = 0; f <= clip.DurationFrames; f++)
        {
            if (f is >= 30 and <= 60) continue;
            var m = P8.Model(rig, locked, f);
            for (var b = 0; b < m.Length; b++)
                Assert.True((m[b].Translation - originalModels[f][b].Translation).Length() <= 1e-9 && Quat.AngleBetween(m[b].Rotation, originalModels[f][b].Rotation) <= 1e-9, $"frame {f} bone {b}");
        }
        // Keys are pinned IK targets.
        var target = rig.Controls.Single(c => c.Name == "ik_foot_l");
        Assert.Contains(locked.Tracks, t => t.Target.Id == target.Id && t.Channel == TrackChannels.Pinned);

        var after = P8.Json(locked);
        s.Undo(CommandOrigin.Test);
        Assert.Equal(original, P8.Json(P8.Clip(s, id)));
        s.Redo(CommandOrigin.Test);
        Assert.Equal(after, P8.Json(P8.Clip(s, id)));
    }

    [Fact]
    public void FootLock_And_Contacts_Commands_ReportAndRefuse()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var rig = P8.RigOf(s, man);
        var walk = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
        var c = P8.Ok(s, "clip.contacts", new { clipId = walk, characterId = man }).Data!;
        var spans = c["spans"]!.AsArray();
        Assert.True(spans.Count >= 4);
        Assert.Contains(spans, x => (string)x!["side"]! == "left");
        Assert.Contains(spans, x => (string)x!["side"]! == "right");
        Assert.True(s.IsDirty);
        var undoCount = s.UndoHistory.Count;
        Assert.Equal(undoCount, s.UndoHistory.Count); // read-only

        // A jump's flight frames have no contact.
        var jump = P8.ApplyTemplate(s, "builtin.jump", man);
        Assert.Equal("no_contacts", P8.Err(P8.Run(s, "clip.foot_lock", new { clipId = jump, characterId = man, startFrame = 22, endFrame = 26, minFrames = 5 })));

        // Locked tracks are refused before anything changes.
        var lockedClip = P8.Clip(s, walk);
        var before = P8.Json(lockedClip);
        P8.Ok(s, "clip.foot_lock", new { clipId = walk, characterId = man });
        var pinTrack = P8.Clip(s, walk).Tracks.First(t => t.Channel == TrackChannels.Pinned);
        P8.Ok(s, "track.set_flags", new { clipId = walk, trackId = pinTrack.Id.ToString(), locked = true });
        Assert.Equal("track_locked", P8.Err(P8.Run(s, "clip.foot_lock", new { clipId = walk, characterId = man })));
        s.Undo(CommandOrigin.Test);
        s.Undo(CommandOrigin.Test);
        Assert.Equal(before, P8.Json(P8.Clip(s, walk)));
        Assert.Equal("invalid_args", P8.Err(P8.Run(s, "clip.foot_lock", new { clipId = walk, characterId = man, threshold = -1 })));
    }

    [Fact]
    public void FootLocker_Spans_MergeFlickerAndDropShortContacts()
    {
        var o = new FootLockOptions { MinFrames = 3, MergeGap = 1 };
        var spans = FootLocker.Spans(0, Side.Left, new long[] { 1, 2, 4, 5, 9, 10, 20, 21, 22, 23 }, o).ToList();
        Assert.Equal(new[] { (1L, 5L), (20L, 23L) }, spans.Select(x => (x.Start, x.End)));
    }

    [Fact]
    public void HumanoidRetargetMap_SomaPath_IsUnchanged_ByTheRefactor()
    {
        // MotionRetargeter now uses the shared map: the same SOMA walk gives the same mannequin clip as before (direction check
        // against the SOMA joints is covered by ProceduralAndRetargetTests); here, the map exposes what the AI path relies on.
        var rig = MannequinFactory.Create();
        var src = RetargetSkeleton.FromRig(rig);
        var map = new HumanoidRetargetMap(src, rig);
        Assert.Equal(1.0, map.Scale, 12);
        Assert.Equal(map.TargetPelvis, map.SourcePelvis);
        Assert.True(map.TargetRoot >= 0);
        Assert.Empty(map.Unmapped);
        // Identity retarget: transferring the rest pose gives the rest locals.
        var rest = new RigEvaluator(rig).RestModel();
        var locals = map.Transfer(rest.Select(t => t.Rotation).ToArray(), rest.Select(t => t.Translation).ToArray());
        for (var b = 0; b < rig.Bones.Count; b++)
        {
            Assert.True(locals[b].Translation.ApproxEquals(rig.Bones[b].Rest.Translation, 1e-12));
            Assert.True(Quat.AngleBetween(locals[b].Rotation, rig.Bones[b].Rest.Rotation) < 1e-9);
        }
    }
}
