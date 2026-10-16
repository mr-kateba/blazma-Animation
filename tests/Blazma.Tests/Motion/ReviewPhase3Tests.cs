using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.Motion;

/// <summary>Regression tests for the Phase 3 core review findings (each reproduced a real bug).</summary>
public class ReviewPhase3Tests
{
    static readonly CommandRegistry Registry = CommandRegistry.CreateDefault();

    static CommandResult Run(ProjectSession s, string id, object args) =>
        s.Execute(Registry.Create(id, JsonNode.Parse(JsonSerializer.Serialize(args))!.AsObject()), CommandOrigin.Test);

    static (ProjectSession s, string ch, string clip) Setup(long frames = 30)
    {
        var s = new ProjectSession(Project.CreateNew("Review3"));
        var ch = (string)Run(s, "scene.add_mannequin", new { }).Data!["characterId"]!;
        var clip = (string)Run(s, "clip.create", new { name = "C", durationFrames = frames }).Data!["clipId"]!;
        return (s, ch, clip);
    }

    static double PelvisY(ProjectSession s, double frame)
    {
        var rig = s.Project.Rigs[0];
        var ev = new RigEvaluator(rig);
        var pose = ClipEvaluator.Evaluate(s.Project.Clips[0], rig, Flicks.FromSeconds(frame / 30.0));
        return ev.Model(pose)[ev.IndexOf(rig.FindBoneByName(H.Pelvis)!.Id)].Translation.Y;
    }

    [Fact]
    public void TranslationKeyedLater_DoesNotLeakBackToEarlierKeys()
    {
        var (s, ch, clip) = Setup();
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 0 });          // rest: pelvis 0.95
        Run(s, "pose.set_bone", new { characterId = ch, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, ty = 0.70 });
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 12 });

        Assert.Equal(0.95, PelvisY(s, 0), 9);
        Assert.InRange(PelvisY(s, 6), 0.70 + 1e-6, 0.95 - 1e-6);
        Assert.Equal(0.70, PelvisY(s, 12), 9);
    }

    [Fact]
    public void TranslationKeyedInTheMiddle_LeavesOtherKeysAtRest()
    {
        var (s, ch, clip) = Setup(40);
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 0 });
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 10 });
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 30 });
        Run(s, "pose.set_bone", new { characterId = ch, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, ty = 0.70 });
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 20 });

        Assert.Equal(0.95, PelvisY(s, 0), 9);
        Assert.Equal(0.95, PelvisY(s, 10), 9);
        Assert.Equal(0.70, PelvisY(s, 20), 9);
        Assert.Equal(0.95, PelvisY(s, 30), 9);
    }

    [Fact]
    public void PasteCreatingNewTrack_BackfillsRestAtExistingKeys()
    {
        var (s, ch, clip) = Setup(40);
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 0 });
        // Source clip with a pelvis translation key only.
        var src = (string)Run(s, "clip.create", new { name = "Src", durationFrames = 10 }).Data!["clipId"]!;
        Run(s, "pose.set_bone", new { characterId = ch, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, ty = 0.6 });
        Run(s, "key.set", new { clipId = src, characterId = ch, frame = 0, bones = H.Pelvis });
        var json = (string)Run(s, "key.copy", new { clipId = src }).Data!["keysJson"]!;
        Assert.True(Run(s, "key.paste", new { clipId = clip, keysJson = json, frame = 20 }).Ok);

        Assert.Equal(0.95, PelvisY(s, 0), 9);
        Assert.Equal(0.6, PelvisY(s, 20), 9);
    }

    [Fact]
    public void Evaluate_IsIndependentOfTheWorkingPose()
    {
        var (s, ch, clip) = Setup();
        Run(s, "pose.set_bone", new { characterId = ch, bone = H.Head, rx = 20, ry = 0, rz = 0 });
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 0, bones = H.Head });
        // Change unrelated parts of the working pose afterwards.
        Run(s, "pose.set_bone", new { characterId = ch, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, ty = 0.5 });
        Run(s, "pose.set_control", new { characterId = ch, control = "ik_foot_l", x = 0.3, y = 0.4, z = 0.3, pinned = true });

        var bones = Run(s, "anim.evaluate", new { clipId = clip, characterId = ch, frame = 0 }).Data!["bones"]!.AsArray();
        var pelvis = bones.Single(b => (string)b!["name"]! == H.Pelvis)!["position"]!.AsArray();
        Assert.Equal(0.95, (double)pelvis[1]!, 6);
    }

    [Fact]
    public void LoopRange_IsIgnoredWhenLoopIsOff()
    {
        var clip = new Clip { Fps = FrameRate.Fps30, Duration = Flicks.FromFrame(30, FrameRate.Fps30) };
        clip.Loop = new LoopSettings { Enabled = false, Start = Flicks.FromFrame(5, clip.Fps), End = Flicks.FromFrame(10, clip.Fps) };
        Assert.Equal(Flicks.FromFrame(25, clip.Fps), ClipEvaluator.PlaybackTime(clip, Flicks.FromFrame(25, clip.Fps)));
    }

    [Fact]
    public void LoopRange_DoesNotDriftWhenClipGrows_AndIsClampedWhenItShrinks()
    {
        var (s, _, clip) = Setup(15);
        Run(s, "clip.set_loop", new { clipId = clip, enabled = true, startFrame = 5, endFrame = 15 });
        Run(s, "clip.set_duration", new { clipId = clip, frames = 30 });
        var c = s.Project.Clips[0];
        Assert.Equal(Flicks.FromFrame(9, c.Fps), ClipEvaluator.PlaybackTime(c, Flicks.FromFrame(29, c.Fps))); // 5 + (29−5) mod 10; the end stays 15

        Run(s, "clip.set_loop", new { clipId = clip, enabled = true, startFrame = 20, endFrame = 30 });
        Run(s, "clip.set_duration", new { clipId = clip, frames = 15 });
        c = s.Project.Clips[0];
        Assert.True(c.Loop.Start < c.Duration);
        Assert.Equal(Flicks.FromFrame(10, c.Fps), ClipEvaluator.PlaybackTime(c, Flicks.FromFrame(10, c.Fps)));

        s.Undo(CommandOrigin.Ui);
        Assert.Equal(Flicks.FromFrame(20, c.Fps), s.Project.Clips[0].Loop.Start);
    }

    [Fact]
    public void LockedTracks_WarnOnDeleteAndMove()
    {
        var (s, ch, clip) = Setup();
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 0 });
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 10 });
        var track = s.Project.Clips[0].Tracks[0];
        Run(s, "track.set_flags", new { clipId = clip, trackId = track.Id.ToString(), locked = true });
        Assert.Contains(Run(s, "key.move", new { clipId = clip, frames = "10", delta = 2 }).Warnings, w => w.Code == "track_locked");
        Assert.Contains(Run(s, "key.delete", new { clipId = clip, frames = "0" }).Warnings, w => w.Code == "track_locked");
    }

    [Fact]
    public void PhaseMarkers_AreAddressableByAgents()
    {
        var (s, ch, clip) = Setup();
        var added = Run(s, "clip.add_phase_markers", new { clipId = clip }).Data!["markers"]!.AsArray();
        Assert.Equal(7, added.Count);
        var contact = added.Single(m => (string)m!["phase"]! == "contact")!;
        Assert.True(Run(s, "marker.update", new { clipId = clip, markerId = (string)contact["markerId"]!, frame = 15 }).Ok);
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 15 });

        var info = Run(s, "project.inspect", new { }).Data!["clips"]![0]!;
        Assert.Contains(info["markers"]!.AsArray(), m => (string)m!["phase"]! == "contact" && (long)m["frame"]! == 15);
        Assert.Equal(new long[] { 15 }, info["keyFrames"]!.AsArray().Select(f => (long)f!));
        Assert.Equal(s.Project.Rigs[0].Id.ToString(), (string)info["skeleton"]!);
    }
}
