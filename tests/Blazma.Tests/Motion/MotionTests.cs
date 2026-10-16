using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;
using Blazma.Tests.Persistence;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.Motion;

public class TrackSamplerTests
{
    static readonly FrameRate Fps = FrameRate.Fps30;

    static Track Scalar(params (long frame, double v, Interpolation i)[] keys) => new()
    {
        Channel = "custom:x",
        Keys = keys.Select(k => new Keyframe { Time = Flicks.FromFrame(k.frame, Fps), Value = new[] { k.v }, Interp = k.i }).ToList(),
    };

    static double At(Track t, double frame) => TrackSampler.Sample(t, Flicks.FromSeconds(frame / 30.0))![0];

    [Fact]
    public void Step_Linear_AndHoldOutsideRange()
    {
        var t = Scalar((10, 1, Interpolation.Step), (20, 3, Interpolation.Linear), (30, 5, Interpolation.Linear));
        Assert.Equal(1, At(t, 0));
        Assert.Equal(1, At(t, 19.9), 9);
        Assert.Equal(4, At(t, 25), 9);
        Assert.Equal(5, At(t, 40));
    }

    [Fact]
    public void Cubic_PassesThroughKeys_AndDoesNotOvershootMonotoneData()
    {
        var t = Scalar((0, 0, Interpolation.Cubic), (10, 1, Interpolation.Cubic), (12, 5, Interpolation.Cubic), (40, 5.2, Interpolation.Cubic));
        foreach (var (f, v) in new[] { (0.0, 0.0), (10.0, 1.0), (12.0, 5.0), (40.0, 5.2) })
            Assert.Equal(v, At(t, f), 9);
        var prev = double.MinValue;
        for (var f = 0.0; f <= 40; f += 0.25)
        {
            var v = At(t, f);
            Assert.True(v >= prev - 1e-12, $"not monotone at {f}");
            Assert.InRange(v, -1e-12, 5.2 + 1e-12);
            prev = v;
        }
    }

    [Fact]
    public void Cubic_ExtremeKey_IsFlat()
    {
        var t = Scalar((0, 0, Interpolation.Cubic), (10, 2, Interpolation.Cubic), (20, 0, Interpolation.Cubic));
        Assert.True(At(t, 9.9) < 2 && At(t, 10.1) < 2);
        Assert.Equal(2, At(t, 10), 12);
    }

    [Fact]
    public void ManualTangents_FollowGltfHermite()
    {
        // p(s) with p0=0, p1=1, m0=2/s, m1=0, dt=1s at s=0.5: h10*dt*m0 + h01*1 = 0.125*2 + 0.5
        var t = new Track
        {
            Channel = "custom:x",
            Keys =
            {
                new Keyframe { Time = Flicks.Zero, Value = new[] { 0.0 }, OutTangent = new[] { 2.0 }, InTangent = new[] { 0.0 } },
                new Keyframe { Time = Flicks.FromSeconds(1), Value = new[] { 1.0 }, InTangent = new[] { 0.0 }, OutTangent = new[] { 0.0 } },
            },
        };
        Assert.Equal(0.75, TrackSampler.Sample(t, Flicks.FromSeconds(0.5))![0], 12);
    }

    [Fact]
    public void Rotation_TakesShortestPath_EvenWithSignFlippedKeys()
    {
        var a = Quat.FromAxisAngle(Vec3.UnitY, 0.2);
        var b = Quat.FromAxisAngle(Vec3.UnitY, 0.6);
        var negB = new Quat(-b.X, -b.Y, -b.Z, -b.W);
        foreach (var interp in new[] { Interpolation.Linear, Interpolation.Cubic })
        {
            var t = new Track
            {
                Channel = TrackChannels.Rotation,
                Keys =
                {
                    new Keyframe { Time = Flicks.Zero, Value = new[] { a.X, a.Y, a.Z, a.W }, Interp = interp },
                    new Keyframe { Time = Flicks.FromSeconds(1), Value = new[] { negB.X, negB.Y, negB.Z, negB.W }, Interp = interp },
                },
            };
            var v = TrackSampler.Sample(t, Flicks.FromSeconds(0.5))!;
            var mid = new Quat(v[0], v[1], v[2], v[3]);
            Assert.True(mid.IsNormalized(1e-12));
            Assert.True(Quat.AngleBetween(mid, Quat.FromAxisAngle(Vec3.UnitY, 0.4)) < 1e-3, $"{interp}: {mid}");
        }
    }

    [Fact]
    public void PinnedChannel_IsAlwaysStepped()
    {
        var t = new Track { Channel = TrackChannels.Pinned, Keys =
        {
            new Keyframe { Time = Flicks.Zero, Value = new[] { 0.0 }, Interp = Interpolation.Linear },
            new Keyframe { Time = Flicks.FromSeconds(1), Value = new[] { 1.0 } },
        } };
        Assert.Equal(0, TrackSampler.Sample(t, Flicks.FromSeconds(0.9))![0]);
    }

    [Fact]
    public void PlaybackTime_LoopsWithinRange()
    {
        var clip = new Clip { Fps = Fps, Duration = Flicks.FromFrame(30, Fps), Loop = new LoopSettings { Enabled = true, Start = Flicks.FromFrame(10, Fps) } };
        Assert.Equal(Flicks.FromFrame(5, Fps), ClipEvaluator.PlaybackTime(clip, Flicks.FromFrame(5, Fps)));
        Assert.Equal(Flicks.FromFrame(15, Fps), ClipEvaluator.PlaybackTime(clip, Flicks.FromFrame(35, Fps)));
        clip.Loop.Enabled = false;
        Assert.Equal(clip.Duration, ClipEvaluator.PlaybackTime(clip, Flicks.FromFrame(99, Fps)));
    }
}

public class KeyCommandTests
{
    static readonly CommandRegistry Registry = CommandRegistry.CreateDefault();

    static CommandResult Run(ProjectSession s, string id, object args) =>
        s.Execute(Registry.Create(id, JsonNode.Parse(JsonSerializer.Serialize(args))!.AsObject()), CommandOrigin.Test);

    static (ProjectSession s, string ch, string clip) Setup(long frames = 30)
    {
        var s = new ProjectSession(Project.CreateNew("Anim"));
        var ch = (string)Run(s, "scene.add_mannequin", new { }).Data!["characterId"]!;
        var clip = (string)Run(s, "clip.create", new { name = "Jump", durationFrames = frames }).Data!["clipId"]!;
        return (s, ch, clip);
    }

    static Rig Rig(ProjectSession s) => s.Project.Rigs[0];
    static Clip Clip(ProjectSession s) => s.Project.Clips[0];

    static Vec3 PosAt(ProjectSession s, double frame, string bone)
    {
        var rig = Rig(s);
        var pose = ClipEvaluator.Evaluate(Clip(s), rig, Flicks.FromSeconds(frame / 30.0));
        var ev = new RigEvaluator(rig);
        return ev.Model(pose)[ev.IndexOf(rig.FindBoneByName(bone)!.Id)].Translation;
    }

    [Fact]
    public void SetKeys_RecordsPose_AndPlaybackInterpolatesBetweenKeys()
    {
        var (s, ch, clip) = Setup();
        Assert.True(Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 0 }).Ok);
        Run(s, "pose.set_bone", new { characterId = ch, bone = H.UpperArm(Side.Left), rx = 0, ry = 0, rz = -80 });
        var r = Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 20, interpolation = "linear" });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Equal(Rig(s).Id, Clip(s).Skeleton);

        var hand0 = PosAt(s, 0, H.Hand(Side.Left));
        var hand20 = PosAt(s, 20, H.Hand(Side.Left));
        var hand10 = PosAt(s, 10, H.Hand(Side.Left));
        Assert.True(hand0.Y > hand20.Y + 0.4);
        Assert.InRange(hand10.Y, hand20.Y + 0.05, hand0.Y - 0.05); // in between
    }

    [Fact]
    public void SetKeys_ReplacesExistingKey_AndExtendsClip()
    {
        var (s, ch, clip) = Setup(frames: 10);
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 5 });
        Run(s, "pose.set_bone", new { characterId = ch, bone = H.Head, rx = 30, ry = 0, rz = 0 });
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 5 });
        var headTrack = Clip(s).Tracks.Single(t => t.Target.Id == Rig(s).FindBoneByName(H.Head)!.Id && t.Channel == TrackChannels.Rotation);
        Assert.Single(headTrack.Keys);

        var r = Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 40 });
        Assert.Contains(r.Warnings, w => w.Code == "clip_extended");
        Assert.Equal(41, Clip(s).DurationFrames);
    }

    [Fact]
    public void SetKeys_OnlySelectedBones()
    {
        var (s, ch, clip) = Setup();
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 0, bones = "head, ik_foot_l" });
        var targets = Clip(s).Tracks.Select(t => t.Target.Id).Distinct().ToList();
        Assert.Equal(2, targets.Count);
        Assert.Equal("not_found", Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 0, bones = "nope" }).Error!.Code);
    }

    [Fact]
    public void UndoRedo_IsExact_EvenAfterThePoseChanged()
    {
        var (s, ch, clip) = Setup();
        Run(s, "pose.set_bone", new { characterId = ch, bone = H.Spine2, rx = 10, ry = 20, rz = 0 });
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 3 });
        var recorded = ClipEdit.CloneClip(Clip(s));
        s.Undo(CommandOrigin.Ui);                    // undo key
        Assert.Empty(Clip(s).Tracks);
        s.Undo(CommandOrigin.Ui);                    // undo the pose edit too
        s.Redo(CommandOrigin.Ui);
        Run(s, "pose.reset", new { characterId = ch }); // pose differs now; also clears redo
        Assert.False(s.CanRedo);

        // Redo straight after undo restores the recorded keys exactly.
        var (s2, ch2, clip2) = Setup();
        Run(s2, "pose.set_bone", new { characterId = ch2, bone = H.Spine2, rx = 10, ry = 20, rz = 0 });
        Run(s2, "key.set", new { clipId = clip2, characterId = ch2, frame = 3 });
        var before = JsonSerializer.Serialize(Clip(s2), ProjectArchive.JsonOptions);
        s2.Undo(CommandOrigin.Ui);
        s2.Redo(CommandOrigin.Ui);
        Assert.Equal(before, JsonSerializer.Serialize(Clip(s2), ProjectArchive.JsonOptions));
        _ = recorded;
    }

    [Fact]
    public void RigMismatch_IsRejected()
    {
        var (s, ch, clip) = Setup();
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 0 });
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "models", "khronos", "CesiumMan.glb"));
        var other = (string)Run(s, "rig.import", new { path }).Data!["characterId"]!;
        Assert.Equal("rig_mismatch", Run(s, "key.set", new { clipId = clip, characterId = other, frame = 1 }).Error!.Code);
    }

    [Fact]
    public void DeleteMoveAndInterpolation()
    {
        var (s, ch, clip) = Setup();
        foreach (var f in new[] { 0, 10, 20 }) Run(s, "key.set", new { clipId = clip, characterId = ch, frame = f });
        Assert.Equal(new long[] { 0, 10, 20 }, ClipEvaluator.KeyFrames(Clip(s)));

        Assert.True(Run(s, "key.move", new { clipId = clip, frames = "10", delta = 5 }).Ok);
        Assert.Equal(new long[] { 0, 15, 20 }, ClipEvaluator.KeyFrames(Clip(s)));
        var r = Run(s, "key.move", new { clipId = clip, frames = "15", delta = 5 });
        Assert.Contains(r.Warnings, w => w.Code == "keys_replaced");
        Assert.Equal(new long[] { 0, 20 }, ClipEvaluator.KeyFrames(Clip(s)));
        Assert.Equal("invalid_args", Run(s, "key.move", new { clipId = clip, frames = "0", delta = -1 }).Error!.Code);

        Assert.True(Run(s, "key.set_interpolation", new { clipId = clip, interpolation = "step" }).Ok);
        Assert.All(Clip(s).Tracks.SelectMany(t => t.Keys), k => Assert.Equal(Interpolation.Step, k.Interp));

        Assert.True(Run(s, "key.delete", new { clipId = clip, frames = "20" }).Ok);
        Assert.Equal(new long[] { 0 }, ClipEvaluator.KeyFrames(Clip(s)));
        Assert.Equal("not_found", Run(s, "key.delete", new { clipId = clip, frames = "7" }).Error!.Code);
    }

    [Fact]
    public void PinnedFoot_StaysPlanted_ThroughoutPlayback()
    {
        // A squat: feet pinned, pelvis goes down and up. No foot sliding on any frame.
        // Starts slightly crouched: at rest the legs are fully extended, so any upward hip
        // movement would (correctly) put the pinned feet out of reach.
        var (s, ch, clip) = Setup();
        Run(s, "pose.set_pinned", new { characterId = ch, control = "ik_foot_l", pinned = true });
        Run(s, "pose.set_pinned", new { characterId = ch, control = "ik_foot_r", pinned = true });
        Run(s, "pose.set_bone", new { characterId = ch, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, ty = 0.92 });
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 0 });
        Run(s, "pose.set_bone", new { characterId = ch, bone = H.Pelvis, rx = 15, ry = 0, rz = 0, ty = 0.70 });
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 12 });
        Run(s, "pose.set_bone", new { characterId = ch, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, ty = 0.92 });
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 24 });

        var foot = PosAt(s, 0, H.Foot(Side.Left));
        for (var f = 0.0; f <= 24; f += 0.5)
        {
            Assert.True(PosAt(s, f, H.Foot(Side.Left)).ApproxEquals(foot, 1e-9), $"slid at frame {f}");
            Assert.True(PosAt(s, f, H.Pelvis).Y <= 0.92 + 1e-9);
        }
        Assert.True(PosAt(s, 12, H.Pelvis).Y < 0.75);
    }

    [Fact]
    public void Evaluate_AndPoseFromFrame()
    {
        var (s, ch, clip) = Setup();
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 0 });
        Run(s, "pose.set_bone", new { characterId = ch, bone = H.Head, rx = 40, ry = 0, rz = 0 });
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 10, interpolation = "linear" });
        Run(s, "key.set_interpolation", new { clipId = clip, interpolation = "linear" });

        var data = Run(s, "anim.evaluate", new { clipId = clip, characterId = ch, frame = 5 }).Data!;
        var headRot = data["bones"]!.AsArray().Single(b => (string)b!["name"]! == H.Head)!["rotation"]!.AsArray();
        Assert.Equal(20, (double)headRot[0]!, 6);

        Assert.True(Run(s, "anim.pose_from_frame", new { clipId = clip, characterId = ch, frame = 0 }).Ok);
        var head = Rig(s).FindBoneByName(H.Head)!;
        Assert.True(s.Project.Characters[0].Pose.LocalOf(head).Rotation.ApproxEquals(Quat.Identity, 1e-9));
    }

    [Fact]
    public void Preview_DoesNotDirtyOrRecordHistory()
    {
        var (s, ch, clip) = Setup();
        s.MarkSaved("/tmp/x.blazanim");
        var history = s.UndoHistory.Count;
        var fired = false;
        s.Changed += _ => fired = true;
        s.ApplyPreview(p => p.Characters[0].Pose = new Pose(), new[] { new Change(Change.PoseChanged, ch) }, CommandOrigin.Ui);
        Assert.True(fired);
        Assert.False(s.IsDirty);
        Assert.Equal(history, s.UndoHistory.Count);
        _ = clip;
    }

    /// <summary>Phase 3 acceptance (core part): build an animation by hand, save, reload, same motion.</summary>
    [Fact]
    public void Acceptance_HandMadeAnimation_SurvivesSaveReload()
    {
        using var dir = new TempDir();
        var (s, ch, clip) = Setup(48);
        Run(s, "pose.set_pinned", new { characterId = ch, control = "ik_foot_l", pinned = true });
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 0 });
        Run(s, "pose.set_bone", new { characterId = ch, bone = H.UpperArm(Side.Right), rx = 0, ry = 30, rz = 70 });
        Run(s, "pose.set_bone", new { characterId = ch, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, ty = 0.8 });
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 12 });
        Run(s, "pose.set_control", new { characterId = ch, control = "ik_hand_l", x = 0.3, y = 1.5, z = 0.3, pinned = true });
        Run(s, "key.set", new { clipId = clip, characterId = ch, frame = 30 });

        var path = dir.File("anim.blazanim");
        ProjectFile.Save(s.Project, path);
        var loaded = ProjectFile.Load(path);
        var rigA = s.Project.Rigs[0];
        var rigB = loaded.Rigs[0];
        var evA = new RigEvaluator(rigA);
        var evB = new RigEvaluator(rigB);
        for (var f = 0; f <= 48; f++)
        {
            var t = Flicks.FromFrame(f, FrameRate.Fps30);
            var a = evA.Model(ClipEvaluator.Evaluate(s.Project.Clips[0], rigA, t));
            var b = evB.Model(ClipEvaluator.Evaluate(loaded.Clips[0], rigB, t));
            for (var i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]);
        }
    }
}
