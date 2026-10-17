using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Physics;
using Blazma.Rigging;
using Xunit.Abstractions;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.Physics;

public class PhysicsCommandTests(ITestOutputHelper output)
{
    static readonly CommandRegistry Registry = CommandRegistry.CreateDefault();

    static CommandResult Run(ProjectSession s, string id, object args)
    {
        try { return s.Execute(Registry.Create(id, JsonNode.Parse(JsonSerializer.Serialize(args))!.AsObject()), CommandOrigin.Test); }
        catch (CommandException e) { return CommandResult.Fail(e.Code, e.Message); }
    }

    static CommandResult Ok(ProjectSession s, string id, object args)
    {
        var r = Run(s, id, args);
        Assert.True(r.Ok, $"{id}: {r.Error?.Code} {r.Error?.Message}");
        return r;
    }

    static string Err(CommandResult r) => r.Error?.Code ?? "ok";
    static string Json(Clip c) => JsonSerializer.Serialize(c, ProjectArchive.JsonOptions);
    static double D(JsonNode? n) => n!.GetValue<double>();

    /// <summary>
    /// The acceptance motion: feet pinned; frames 0 and 40 stand straight, frames 15–25 crouch with the hips pushed
    /// forward and the trunk leaning 60° forward, so the COM passes in front of the toes.
    /// </summary>
    static (ProjectSession s, string ch, string clip) LeanClip()
    {
        var s = new ProjectSession(Project.CreateNew("Physics"));
        var ch = (string)Ok(s, "scene.add_mannequin", new { }).Data!["characterId"]!;
        var clip = (string)Ok(s, "clip.create", new { name = "Lean", durationFrames = 41 }).Data!["clipId"]!;
        Ok(s, "pose.set_bone", new { characterId = ch, bone = H.UpperArm(Side.Left), rx = 0, ry = 0, rz = -80 });
        Ok(s, "pose.set_bone", new { characterId = ch, bone = H.UpperArm(Side.Right), rx = 0, ry = 0, rz = 80 });
        Ok(s, "pose.set_pinned", new { characterId = ch, control = "ik_foot_l", pinned = true });
        Ok(s, "pose.set_pinned", new { characterId = ch, control = "ik_foot_r", pinned = true });
        Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = 0 });
        Ok(s, "pose.set_bone", new { characterId = ch, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, tx = 0, ty = 0.83, tz = 0.12 });
        Ok(s, "pose.set_bone", new { characterId = ch, bone = H.Spine1, rx = 60, ry = 0, rz = 0 });
        Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = 15 });
        Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = 25 });
        Ok(s, "pose.set_bone", new { characterId = ch, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, tx = 0, ty = 0.95, tz = 0 });
        Ok(s, "pose.set_bone", new { characterId = ch, bone = H.Spine1, rx = 0, ry = 0, rz = 0 });
        Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = 40 });
        return (s, ch, clip);
    }

    [Fact]
    public void PhysicsCommands_AreRegistered_WithTheRightEffects()
    {
        foreach (var (id, effect) in new[]
        {
            ("physics.analyze", CommandEffect.ReadOnly), ("physics.com", CommandEffect.ReadOnly), ("physics.trajectories", CommandEffect.ReadOnly),
            ("physics.auto_balance", CommandEffect.Mutating), ("physics.secondary_motion", CommandEffect.Mutating),
        })
        {
            Assert.True(Registry.TryGet(id, out var d), id);
            Assert.Equal(effect, d.Effect);
        }
    }

    /// <summary>Phase 6 acceptance: an off-balance motion is detected, corrected, and the comparison numbers prove it.</summary>
    [Fact]
    public void Acceptance_OffBalanceMotion_AnalyzeAutoBalanceAnalyze()
    {
        var (s, ch, clip) = LeanClip();
        var before = Ok(s, "physics.analyze", new { clipId = clip, summaryOnly = true }).Data!["summary"]!;
        var ranges = before["unbalancedRanges"]!.AsArray();
        Assert.Single(ranges);
        var start = (long)ranges[0]!["start"]!;
        var end = (long)ranges[0]!["end"]!;
        Assert.InRange(start, 5, 15);
        Assert.InRange(end, 25, 35);
        Assert.True(D(before["minMargin"]) < -0.04, $"min margin before {before["minMargin"]}");
        Assert.Equal(0, (int)before["airborne"]!);

        var json = Json(s.Project.GetClip(Core.Ids.ObjectId.Parse(clip)));
        var feetBefore = FeetPaths(s, clip);
        var fix = Ok(s, "physics.auto_balance", new { clipId = clip, characterId = ch, margin = 0.02 }).Data!;
        output.WriteLine(fix.ToJsonString(new JsonSerializerOptions { WriteIndented = false })[..Math.Min(1500, fix.ToJsonString().Length)]);
        // Unbalanced frames plus the few that are inside but closer than the margin.
        Assert.InRange((int)fix["corrected"]!, (int)before["unbalanced"]!, (int)before["unbalanced"]! + 6);
        Assert.True(D(fix["minMarginBefore"]) < -0.04);
        Assert.InRange(D(fix["minMarginAfter"]), 0.0195, 0.0205); // minimal: right on the requested margin
        Assert.True(D(fix["maxFootDrift"]) < 1e-4);
        Assert.Empty(fix["stillUnbalanced"]!.AsArray());
        foreach (var f in fix["frames"]!.AsArray())
        {
            Assert.True(D(f!["marginBefore"]) < 0.02);
            Assert.InRange(D(f["marginAfter"]), 0.0195, 0.0205);
            Assert.True(D(f["hipShift"]![1]) < 0); // hips back
            Assert.InRange(Math.Abs(D(f["hipShift"]![0])), 0, 1e-9);
        }

        var after = Ok(s, "physics.analyze", new { clipId = clip, summaryOnly = true }).Data!["summary"]!;
        Assert.Equal(0, (int)after["unbalanced"]!);
        Assert.Empty(after["unbalancedRanges"]!.AsArray());
        Assert.True(D(after["minMargin"]) > 0);
        output.WriteLine($"before: {before.ToJsonString()}\nafter: {after.ToJsonString()}");

        // Feet stay where they were on every frame.
        var feetAfter = FeetPaths(s, clip);
        for (var i = 0; i < feetBefore.Count; i++) Assert.True((feetAfter[i] - feetBefore[i]).Length() < 1e-4);

        // One undo step restores the clip exactly; redo re-applies it exactly.
        var fixedJson = Json(s.Project.GetClip(Core.Ids.ObjectId.Parse(clip)));
        s.Undo(CommandOrigin.Ui);
        Assert.Equal(json, Json(s.Project.GetClip(Core.Ids.ObjectId.Parse(clip))));
        s.Redo(CommandOrigin.Ui);
        Assert.Equal(fixedJson, Json(s.Project.GetClip(Core.Ids.ObjectId.Parse(clip))));

        // Running it again finds nothing to do.
        Assert.Equal("already_balanced", Err(Run(s, "physics.auto_balance", new { clipId = clip, margin = 0.01 })));
    }

    static List<Core.Mathematics.Vec3> FeetPaths(ProjectSession s, string clip)
    {
        var t = Ok(s, "physics.trajectories", new { clipId = clip, what = "feet" }).Data!;
        return t["leftFoot"]!.AsArray().Concat(t["rightFoot"]!.AsArray())
            .Select(v => new Core.Mathematics.Vec3(D(v![0]), D(v[1]), D(v[2]))).ToList();
    }

    [Fact]
    public void AutoBalance_SelectedFrames_OnlyTouchesThoseAndTheirNeighbours()
    {
        var (s, _, clip) = LeanClip();
        var id = Core.Ids.ObjectId.Parse(clip);
        var rig = s.Project.Rigs[0];
        var original = ClipEdit.CloneClip(s.Project.GetClip(id));
        var fix = Ok(s, "physics.auto_balance", new { clipId = clip, frames = "20" }).Data!;
        Assert.Equal(1, (int)fix["corrected"]!);
        Assert.Equal(new long[] { 19, 20, 21 }, fix["keyedFrames"]!.AsArray().Select(n => (long)n!));
        // The neighbours keep their original pose exactly; frame 20 is balanced.
        var edited = s.Project.GetClip(id);
        foreach (var f in new[] { 19, 21, 0, 40 })
        {
            var a = ClipEvaluator.Evaluate(original, rig, Flicks.FromFrame(f, original.Fps));
            var b = ClipEvaluator.Evaluate(edited, rig, Flicks.FromFrame(f, edited.Fps));
            Assert.True(a.ContentEquals(b, 1e-9), $"frame {f} changed");
        }
        var check = Ok(s, "physics.analyze", new { clipId = clip, startFrame = 20, endFrame = 20 }).Data!;
        Assert.Equal("balanced", (string)check["frames"]![0]!["state"]!);
    }

    [Fact]
    public void AutoBalance_Errors_LockedPelvis_NoClip_BadArgs()
    {
        var (s, _, clip) = LeanClip();
        Assert.Equal("not_found", Err(Run(s, "physics.auto_balance", new { clipId = Core.Ids.ObjectId.New().ToString() })));
        Assert.Equal("invalid_args", Err(Run(s, "physics.auto_balance", new { clipId = clip, margin = 2 })));
        Assert.Equal("invalid_args", Err(Run(s, "physics.auto_balance", new { clipId = clip, frames = "99" })));
        var pelvis = s.Project.Rigs[0].FindBoneByName(H.Pelvis)!.Id;
        var track = s.Project.GetClip(Core.Ids.ObjectId.Parse(clip)).Tracks.Single(t => t.Target.Id == pelvis && t.Channel == TrackChannels.Translation);
        Ok(s, "track.set_flags", new { clipId = clip, trackId = track.Id.ToString(), locked = true });
        Assert.Equal("track_locked", Err(Run(s, "physics.auto_balance", new { clipId = clip })));
    }

    [Fact]
    public void Analyze_ReportsFramesSummaryAndCaps()
    {
        var (s, _, clip) = LeanClip();
        var full = Ok(s, "physics.analyze", new { clipId = clip }).Data!;
        Assert.Equal(41, full["frames"]!.AsArray().Count);
        Assert.False((bool)full["truncated"]!);
        Assert.Equal("bone_masses", (string)full["massSource"]!);
        var f0 = full["frames"]![0]!;
        Assert.Equal("balanced", (string)f0["state"]!);
        Assert.Equal(10, (int)f0["contacts"]!);
        Assert.Equal(6, f0["polygon"]!.AsArray().Count);
        Assert.Equal(3, f0["com"]!.AsArray().Count);
        Assert.Equal("unbalanced", (string)full["frames"]![20]!["state"]!);

        var capped = Ok(s, "physics.analyze", new { clipId = clip, maxFrames = 5, startFrame = 10, endFrame = 30 }).Data!;
        Assert.Equal(5, capped["frames"]!.AsArray().Count);
        Assert.True((bool)capped["truncated"]!);
        Assert.Equal(10, (long)capped["frames"]![0]!["frame"]!);
        Assert.Equal(21, (int)capped["summary"]!["frames"]!);
        Assert.Null(Ok(s, "physics.analyze", new { clipId = clip, summaryOnly = true }).Data!["frames"]);

        var zmp = Ok(s, "physics.analyze", new { clipId = clip, metric = "zmp", summaryOnly = true }).Data!;
        Assert.Equal("zmp", (string)zmp["metric"]!);
        Assert.Equal("invalid_args", Err(Run(s, "physics.analyze", new { clipId = clip, metric = "magic" })));
        Assert.Equal("invalid_args", Err(Run(s, "physics.analyze", new { clipId = clip, startFrame = 50 })));
        Assert.Equal("invalid_args", Err(Run(s, "physics.analyze", new { clipId = clip, startFrame = 20, endFrame = 10 })));
    }

    [Fact]
    public void Com_OfWorkingPose_AndOfAClipFrame()
    {
        var (s, ch, clip) = LeanClip();
        Ok(s, "pose.reset", new { characterId = ch });
        var rest = Ok(s, "physics.com", new { characterId = ch, segments = true }).Data!;
        Assert.Equal("pose", (string)rest["source"]!);
        Assert.InRange(D(rest["com"]![1]), 1.0, 1.07);
        Assert.Equal(0, D(rest["com"]![0]), 9);
        Assert.InRange(D(rest["totalMass"]), 74.9, 75.1);
        Assert.Equal(22, rest["segments"]!.AsArray().Count); // every bone but the massless root
        Assert.Equal(10, rest["contactPoints"]!.AsArray().Count);
        Assert.Equal("balanced", (string)rest["state"]!);

        var lean = Ok(s, "physics.com", new { characterId = ch, clipId = clip, frame = 20 }).Data!;
        Assert.Equal("clip", (string)lean["source"]!);
        Assert.Equal("unbalanced", (string)lean["state"]!);
        Assert.True(D(lean["com"]![2]) > 0.2);
        Assert.Equal("invalid_args", Err(Run(s, "physics.com", new { characterId = ch, clipId = clip, frame = 100 })));
        Assert.Equal("not_found", Err(Run(s, "physics.com", new { characterId = Core.Ids.ObjectId.New().ToString() })));
    }

    [Fact]
    public void Trajectories_ReturnOnePointPerFrame()
    {
        var (s, _, clip) = LeanClip();
        var all = Ok(s, "physics.trajectories", new { clipId = clip }).Data!;
        foreach (var k in new[] { "com", "hips", "leftFoot", "rightFoot" }) Assert.Equal(41, all[k]!.AsArray().Count);
        Assert.Equal(0.95, D(all["hips"]![0]![1]), 6);
        Assert.Equal(0.83, D(all["hips"]![20]![1]), 6);
        var com = Ok(s, "physics.trajectories", new { clipId = clip, what = "com", startFrame = 10, endFrame = 12 }).Data!;
        Assert.Equal(3, com["com"]!.AsArray().Count);
        Assert.Null(com["hips"]);
        Assert.Equal("invalid_args", Err(Run(s, "physics.trajectories", new { clipId = clip, what = "hands" })));
    }

    /// <summary>Clip whose neck nods 50° quickly and holds.</summary>
    static (ProjectSession s, string ch, string clip) NodClip()
    {
        var s = new ProjectSession(Project.CreateNew("Nod"));
        var ch = (string)Ok(s, "scene.add_mannequin", new { }).Data!["characterId"]!;
        var clip = (string)Ok(s, "clip.create", new { name = "Nod", durationFrames = 60 }).Data!["clipId"]!;
        Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = 0, bones = $"{H.Neck},{H.Head}" });
        Ok(s, "pose.set_bone", new { characterId = ch, bone = H.Neck, rx = 50, ry = 0, rz = 0 });
        Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = 8, bones = $"{H.Neck},{H.Head}" });
        Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = 59, bones = $"{H.Neck},{H.Head}" });
        return (s, ch, clip);
    }

    [Fact]
    public void SecondaryMotion_BakesKeys_AndUndoRestoresExactly()
    {
        var (s, _, clip) = NodClip();
        var id = Core.Ids.ObjectId.Parse(clip);
        var before = Json(s.Project.GetClip(id));
        var r = Ok(s, "physics.secondary_motion", new { clipId = clip, preset = "head", stiffness = 120, damping = 8 });
        Assert.Equal(new[] { H.Neck, H.Head }, r.Data!["bones"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(120, (int)r.Data!["keys"]!);
        Assert.InRange(D(r.Data!["maxLagDegrees"]), 1, 30);
        Assert.Equal(8 / (2 * Math.Sqrt(120)), D(r.Data!["dampingRatio"]), 3);
        var rig = s.Project.Rigs[0];
        var head = rig.FindBoneByName(H.Head)!.Id;
        var track = s.Project.GetClip(id).Tracks.Single(t => t.Target.Id == head && t.Channel == TrackChannels.Rotation);
        Assert.Equal(60, track.Keys.Count);
        // The head lags during the nod and settles back to the primary (rest) rotation at the end.
        Assert.True(Core.Mathematics.Quat.AngleBetween(Q(track.Keys[8].Value), Core.Mathematics.Quat.Identity) > 0.02);
        Assert.True(Core.Mathematics.Quat.AngleBetween(Q(track.Keys[59].Value), Core.Mathematics.Quat.Identity) < 0.01);

        var after = Json(s.Project.GetClip(id));
        s.Undo(CommandOrigin.Ui);
        Assert.Equal(before, Json(s.Project.GetClip(id)));
        s.Redo(CommandOrigin.Ui);
        Assert.Equal(after, Json(s.Project.GetClip(id)));
    }

    static Core.Mathematics.Quat Q(double[] v) => new(v[0], v[1], v[2], v[3]);

    [Fact]
    public void SecondaryMotion_RespectsLockedTracks_AndValidates()
    {
        var (s, _, clip) = NodClip();
        var id = Core.Ids.ObjectId.Parse(clip);
        var head = s.Project.Rigs[0].FindBoneByName(H.Head)!.Id;
        var headTrack = s.Project.GetClip(id).Tracks.Single(t => t.Target.Id == head && t.Channel == TrackChannels.Rotation);
        var headJson = JsonSerializer.Serialize(headTrack.Keys, ProjectArchive.JsonOptions);
        Ok(s, "track.set_flags", new { clipId = clip, trackId = headTrack.Id.ToString(), locked = true });
        var r = Ok(s, "physics.secondary_motion", new { clipId = clip, bones = $"{H.Neck},{H.Head}", startFrame = 0, endFrame = 30 });
        Assert.Contains(r.Warnings, w => w.Code == "track_locked");
        Assert.Equal(31, (int)r.Data!["keys"]!);
        var after = s.Project.GetClip(id).Tracks.Single(t => t.Target.Id == head && t.Channel == TrackChannels.Rotation);
        Assert.Equal(headJson, JsonSerializer.Serialize(after.Keys, ProjectArchive.JsonOptions));

        Assert.Equal("track_locked", Err(Run(s, "physics.secondary_motion", new { clipId = clip, bones = H.Head })));
        Assert.Equal("invalid_args", Err(Run(s, "physics.secondary_motion", new { clipId = clip })));
        Assert.Equal("invalid_args", Err(Run(s, "physics.secondary_motion", new { clipId = clip, preset = "tail" })));
        Assert.Equal("invalid_args", Err(Run(s, "physics.secondary_motion", new { clipId = clip, preset = "head", stiffness = 0 })));
        Assert.Equal("not_found", Err(Run(s, "physics.secondary_motion", new { clipId = clip, bones = "wing" })));
        Assert.Equal("invalid_args", Err(Run(s, "physics.secondary_motion", new { clipId = clip, preset = "head", startFrame = 5, endFrame = 5 })));
        var arms = Ok(s, "physics.secondary_motion", new { clipId = clip, preset = "arms" });
        Assert.Contains(arms.Warnings, w => w.Code == "ik_driven");
    }
}
