using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.Motion;

public class AnimationEditCommandTests
{
    static readonly CommandRegistry Registry = CommandRegistry.CreateDefault();

    // Argument errors are thrown by Registry.Create (as callers see them); fold them into a failed result.
    static CommandResult Run(ProjectSession s, string id, object args)
    {
        try { return s.Execute(Registry.Create(id, JsonNode.Parse(JsonSerializer.Serialize(args))!.AsObject()), CommandOrigin.Test); }
        catch (CommandException e) { return CommandResult.Fail(e.Code, e.Message); }
    }

    static string Json(Clip c) => JsonSerializer.Serialize(c, ProjectArchive.JsonOptions);
    static Clip Clip(ProjectSession s, int i = 0) => s.Project.Clips[i];
    static string Err(CommandResult r) => r.Error?.Code ?? "ok";

    /// <summary>Mannequin + clip "Jump" keyed at 0, 10, 20 with a moving head.</summary>
    static (ProjectSession s, string ch, string clip) Setup(long frames = 30)
    {
        var s = new ProjectSession(Project.CreateNew("Anim"));
        var ch = (string)Run(s, "scene.add_mannequin", new { }).Data!["characterId"]!;
        var clip = (string)Run(s, "clip.create", new { name = "Jump", durationFrames = frames }).Data!["clipId"]!;
        foreach (var (f, rx) in new[] { (0, 0), (10, 30), (20, 10) })
        {
            Run(s, "pose.set_bone", new { characterId = ch, bone = H.Head, rx, ry = f, rz = 0 });
            Assert.True(Run(s, "key.set", new { clipId = clip, characterId = ch, frame = f }).Ok);
        }
        return (s, ch, clip);
    }

    static Track HeadRot(ProjectSession s)
    {
        var id = s.Project.Rigs[0].FindBoneByName(H.Head)!.Id;
        return Clip(s).Tracks.Single(t => t.Target.Id == id && t.Channel == TrackChannels.Rotation);
    }

    /// <summary>Runs a mutating command and checks undo returns the exact before-state and redo the exact after-state.</summary>
    static CommandResult RunUndoable(ProjectSession s, string id, object args, int clipIndex = 0)
    {
        var before = Json(Clip(s, clipIndex));
        var r = Run(s, id, args);
        Assert.True(r.Ok, r.Error?.Message);
        var after = Json(Clip(s, clipIndex));
        Assert.NotEqual(before, after);
        s.Undo(CommandOrigin.Ui);
        Assert.Equal(before, Json(Clip(s, clipIndex)));
        s.Redo(CommandOrigin.Ui);
        Assert.Equal(after, Json(Clip(s, clipIndex)));
        return r;
    }

    // ---- markers -------------------------------------------------------------------------

    [Fact]
    public void Marker_Add_Update_Delete_KeepSortedAndUndo()
    {
        var (s, _, clip) = Setup();
        var b = (string)RunUndoable(s, "marker.add", new { clipId = clip, frame = 20, name = "B", phase = "contact" }).Data!["markerId"]!;
        var a = (string)RunUndoable(s, "marker.add", new { clipId = clip, frame = 5, name = "A" }).Data!["markerId"]!;
        Assert.Equal(new[] { "A", "B" }, Clip(s).Markers.Select(m => m.Name));
        Assert.Equal(KeyPhase.Contact, Clip(s).Markers[1].Phase);
        Assert.Equal(Flicks.FromFrame(5, FrameRate.Fps30), Clip(s).Markers[0].Time);

        RunUndoable(s, "marker.update", new { clipId = clip, markerId = a, frame = 25, name = "A2", phase = "main_action" });
        Assert.Equal(new[] { "B", "A2" }, Clip(s).Markers.Select(m => m.Name));
        Assert.Equal(KeyPhase.MainAction, Clip(s).Markers[1].Phase);
        RunUndoable(s, "marker.update", new { clipId = clip, markerId = a, phase = "none" });

        RunUndoable(s, "marker.delete", new { clipId = clip, markerId = b });
        Assert.Single(Clip(s).Markers);
    }

    [Fact]
    public void Marker_Errors()
    {
        var (s, _, clip) = Setup();
        var m = (string)Run(s, "marker.add", new { clipId = clip, frame = 3, name = "M" }).Data!["markerId"]!;
        var missing = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        Assert.Equal("not_found", Err(Run(s, "marker.add", new { clipId = missing, frame = 3, name = "M" })));
        Assert.Equal("invalid_args", Err(Run(s, "marker.add", new { clipId = clip, frame = 1.5, name = "M" })));
        Assert.Equal("invalid_args", Err(Run(s, "marker.add", new { clipId = clip, frame = -1, name = "M" })));
        Assert.Equal("invalid_args", Err(Run(s, "marker.add", new { clipId = clip, frame = 99, name = "M" })));
        Assert.Equal("invalid_args", Err(Run(s, "marker.add", new { clipId = clip, frame = 3, name = "M", phase = "bogus" })));
        Assert.Equal("invalid_args", Err(Run(s, "marker.update", new { clipId = clip, markerId = m })));
        Assert.Equal("not_found", Err(Run(s, "marker.update", new { clipId = clip, markerId = missing, name = "x" })));
        Assert.Equal("not_found", Err(Run(s, "marker.delete", new { clipId = clip, markerId = missing })));
    }

    [Fact]
    public void AddPhaseMarkers_PositionsNamesAndSkipExisting()
    {
        var (s, _, clip) = Setup(30); // last frame 29
        var r = RunUndoable(s, "clip.add_phase_markers", new { clipId = clip });
        Assert.Equal(7, (int)r.Data!["added"]!);
        var frames = Clip(s).Markers.Select(m => m.Time.ToFrame(FrameRate.Fps30)).ToArray();
        Assert.Equal(new long[] { 0, 4, 10, 13, 17, 23, 29 }, frames);
        Assert.Equal(new[] { "Initial Pose", "Anticipation", "Main Action", "Contact", "Follow-through", "Recovery", "Final Pose" }, Clip(s).Markers.Select(m => m.Name));
        Assert.Equal(new[] { KeyPhase.Initial, KeyPhase.Anticipation, KeyPhase.MainAction, KeyPhase.Contact, KeyPhase.FollowThrough, KeyPhase.Recovery, KeyPhase.Final }, Clip(s).Markers.Select(m => m.Phase));

        var again = Run(s, "clip.add_phase_markers", new { clipId = clip });
        Assert.True(again.Ok);
        Assert.Equal(0, (int)again.Data!["added"]!);
        Assert.Equal(7, again.Warnings.Count(w => w.Code == "phase_exists"));
        Assert.Equal("not_found", Err(Run(s, "clip.add_phase_markers", new { clipId = "01ARZ3NDEKTSV4RRFFQ69G5FAV" })));
    }

    // ---- loop ----------------------------------------------------------------------------

    [Fact]
    public void SetLoop_StoresRange_ClipEndIsZero_AndValidates()
    {
        var (s, _, clip) = Setup(30);
        RunUndoable(s, "clip.set_loop", new { clipId = clip, enabled = true, startFrame = 5, endFrame = 20 });
        Assert.True(Clip(s).Loop.Enabled);
        Assert.Equal(Flicks.FromFrame(5, FrameRate.Fps30), Clip(s).Loop.Start);
        Assert.Equal(Flicks.FromFrame(20, FrameRate.Fps30), Clip(s).Loop.End);

        RunUndoable(s, "clip.set_loop", new { clipId = clip, enabled = true, endFrame = 30 });
        Assert.Equal(Flicks.Zero, Clip(s).Loop.End);
        Assert.Equal(Flicks.FromFrame(5, FrameRate.Fps30), Clip(s).Loop.Start);

        RunUndoable(s, "clip.set_loop", new { clipId = clip, enabled = false });
        Assert.False(Clip(s).Loop.Enabled);

        foreach (var bad in new object[]
        {
            new { clipId = clip, enabled = true, startFrame = 10, endFrame = 10 },
            new { clipId = clip, enabled = true, startFrame = 12, endFrame = 10 },
            new { clipId = clip, enabled = true, startFrame = 0, endFrame = 31 },
            new { clipId = clip, enabled = true, startFrame = 30 },
            new { clipId = clip, enabled = true, startFrame = -2 },
        })
            Assert.Equal("invalid_args", Err(Run(s, "clip.set_loop", bad)));
        Assert.Equal("not_found", Err(Run(s, "clip.set_loop", new { clipId = "01ARZ3NDEKTSV4RRFFQ69G5FAV", enabled = true })));
    }

    // ---- key values ----------------------------------------------------------------------

    [Fact]
    public void SetValue_Translation_AndRotationRenormalizes()
    {
        var (s, _, clip) = Setup();
        var rot = HeadRot(s);
        var before = rot.Keys[1].Value.ToArray();
        RunUndoable(s, "key.set_value", new { clipId = clip, trackId = rot.Id.ToString(), frame = 10, component = 0, value = 0.5 });
        var v = HeadRot(s).Keys[1].Value;
        Assert.NotEqual(before[0], v[0]);
        Assert.Equal(1.0, Math.Sqrt(v.Sum(x => x * x)), 12);
        var norm = Math.Sqrt(0.25 + before[1] * before[1] + before[2] * before[2] + before[3] * before[3]);
        Assert.Equal(before[1] / norm, v[1], 12);
    }

    [Fact]
    public void SetValue_Scalar_PinnedAndErrors()
    {
        var (s, ch, clip) = Setup();
        _ = ch;
        var pin = Clip(s).Tracks.First(t => t.Channel == TrackChannels.Pinned);
        var tid = pin.Id.ToString();

        RunUndoable(s, "key.set_value", new { clipId = clip, trackId = tid, frame = 10, component = 0, value = 1 });
        Assert.Equal(1, Clip(s).Tracks.First(t => t.Id == pin.Id).Keys[1].Value[0]);
        Assert.Equal("invalid_args", Err(Run(s, "key.set_value", new { clipId = clip, trackId = tid, frame = 10, component = 0, value = 0.5 })));
        Assert.Equal("invalid_args", Err(Run(s, "key.set_value", new { clipId = clip, trackId = tid, frame = 10, component = 1, value = 1 })));

        var rot = HeadRot(s).Id.ToString();
        Assert.Equal("invalid_args", Err(Run(s, "key.set_value", new { clipId = clip, trackId = rot, frame = 10, component = 4, value = 1 })));
        Assert.Equal("invalid_args", Err(Run(s, "key.set_value", new { clipId = clip, trackId = rot, frame = 10, component = -1, value = 1 })));
        Assert.Equal("not_found", Err(Run(s, "key.set_value", new { clipId = clip, trackId = rot, frame = 11, component = 0, value = 1 })));
        Assert.Equal("not_found", Err(Run(s, "key.set_value", new { clipId = clip, trackId = "01ARZ3NDEKTSV4RRFFQ69G5FAV", frame = 10, component = 0, value = 1 })));
        Assert.Equal("not_found", Err(Run(s, "key.set_value", new { clipId = "01ARZ3NDEKTSV4RRFFQ69G5FAV", trackId = rot, frame = 10, component = 0, value = 1 })));
    }

    [Fact]
    public void LockedTrack_BlocksEdits()
    {
        var (s, _, clip) = Setup();
        var rot = HeadRot(s);
        var tid = rot.Id.ToString();
        RunUndoable(s, "track.set_flags", new { clipId = clip, trackId = tid, locked = true });
        var snapshot = Json(Clip(s));
        Assert.Equal("track_locked", Err(Run(s, "key.set_value", new { clipId = clip, trackId = tid, frame = 10, component = 0, value = 0.1 })));
        Assert.Equal("track_locked", Err(Run(s, "key.set_tangents", new { clipId = clip, trackId = tid, frame = 10, component = 0, outSlope = 1 })));
        Assert.Equal(snapshot, Json(Clip(s)));

        var r = Run(s, "key.auto_tangents", new { clipId = clip, trackId = tid });
        Assert.Contains(r.Warnings, w => w.Code == "track_locked");

        // Paste skips the locked track but still pastes the others.
        var copy = (string)Run(s, "key.copy", new { clipId = clip, frames = "10" }).Data!["keysJson"]!;
        var p = Run(s, "key.paste", new { clipId = clip, keysJson = copy, frame = 12 });
        Assert.True(p.Ok);
        Assert.Contains(p.Warnings, w => w.Code == "track_locked");
        Assert.DoesNotContain(HeadRot(s).Keys, k => k.Time == Flicks.FromFrame(12, FrameRate.Fps30));
        Assert.Contains(Clip(s).Tracks.Where(t => !t.Locked).SelectMany(t => t.Keys), k => k.Time == Flicks.FromFrame(12, FrameRate.Fps30));
    }

    [Fact]
    public void TrackFlags_SetAndErrors()
    {
        var (s, _, clip) = Setup();
        var tid = HeadRot(s).Id.ToString();
        RunUndoable(s, "track.set_flags", new { clipId = clip, trackId = tid, muted = true });
        Assert.True(HeadRot(s).Muted);
        Assert.False(HeadRot(s).Locked);
        RunUndoable(s, "track.set_flags", new { clipId = clip, trackId = tid, locked = true, muted = false });
        Assert.True(HeadRot(s).Locked);
        Assert.False(HeadRot(s).Muted);
        RunUndoable(s, "track.set_flags", new { clipId = clip, trackId = tid, locked = false }); // unlocking a locked track works
        Assert.Equal("invalid_args", Err(Run(s, "track.set_flags", new { clipId = clip, trackId = tid })));
        Assert.Equal("not_found", Err(Run(s, "track.set_flags", new { clipId = clip, trackId = "01ARZ3NDEKTSV4RRFFQ69G5FAV", locked = true })));
    }

    // ---- tangents ------------------------------------------------------------------------

    static double[][] Samples(Track t) =>
        Enumerable.Range(0, 81).Select(i => TrackSampler.Sample(t, Flicks.FromSeconds(i * 0.25 / 30.0))!).ToArray();

    [Fact]
    public void SetTangents_ConversionKeepsCurveContinuous()
    {
        var (s, _, clip) = Setup();
        var track = HeadRot(s);
        var before = Samples(track);
        var auto = TrackSampler.AutoTangent(track.Keys, 1, 4, true);
        Assert.Contains(auto, x => Math.Abs(x) > 1e-9); // the test is meaningful: the middle key has a slope

        var r = RunUndoable(s, "key.set_tangents", new { clipId = clip, trackId = track.Id.ToString(), frame = 10, component = 0, inSlope = auto[0], outSlope = auto[0] });
        Assert.True(r.Ok);
        var after = HeadRot(s);
        Assert.NotNull(after.Keys[1].InTangent);
        Assert.NotNull(after.Keys[1].OutTangent);
        Assert.Null(after.Keys[0].OutTangent);
        for (var c = 0; c < 4; c++) Assert.Equal(auto[c], after.Keys[1].OutTangent![c], 12);

        var now = Samples(after);
        for (var i = 0; i < before.Length; i++)
            for (var c = 0; c < 4; c++)
                Assert.True(Math.Abs(before[i][c] - now[i][c]) < 1e-12, $"jump at sample {i} component {c}");
    }

    [Fact]
    public void SetTangents_ChangesOnlyRequestedSlope_AndErrors()
    {
        var (s, ch, clip) = Setup();
        var track = HeadRot(s);
        var tid = track.Id.ToString();
        var auto = TrackSampler.AutoTangent(track.Keys, 1, 4, true);
        Run(s, "key.set_tangents", new { clipId = clip, trackId = tid, frame = 10, component = 1, outSlope = 3.5 });
        var k = HeadRot(s).Keys[1];
        Assert.Equal(3.5, k.OutTangent![1]);
        Assert.Equal(auto[1], k.InTangent![1], 12);   // inSlope untouched
        Assert.Equal(auto[2], k.OutTangent[2], 12);   // other components stay auto

        Assert.Equal("invalid_args", Err(Run(s, "key.set_tangents", new { clipId = clip, trackId = tid, frame = 10, component = 1 })));
        Assert.Equal("invalid_args", Err(Run(s, "key.set_tangents", new { clipId = clip, trackId = tid, frame = 10, component = 9, outSlope = 1 })));
        Assert.Equal("not_found", Err(Run(s, "key.set_tangents", new { clipId = clip, trackId = tid, frame = 11, component = 0, outSlope = 1 })));

        _ = ch;
        var pin = Clip(s).Tracks.First(t => t.Channel == TrackChannels.Pinned).Id.ToString();
        Assert.Equal("invalid_args", Err(Run(s, "key.set_tangents", new { clipId = clip, trackId = pin, frame = 10, component = 0, outSlope = 1 })));
    }

    [Fact]
    public void AutoTangents_ResetsToNull()
    {
        var (s, _, clip) = Setup();
        var tid = HeadRot(s).Id.ToString();
        Run(s, "key.set_tangents", new { clipId = clip, trackId = tid, frame = 10, component = 0, outSlope = 2 });
        Run(s, "key.set_tangents", new { clipId = clip, trackId = tid, frame = 20, component = 0, outSlope = 2 });
        RunUndoable(s, "key.auto_tangents", new { clipId = clip, trackId = tid, frames = "10" });
        Assert.Null(HeadRot(s).Keys[1].InTangent);
        Assert.Null(HeadRot(s).Keys[1].OutTangent);
        Assert.NotNull(HeadRot(s).Keys[2].OutTangent);

        RunUndoable(s, "key.auto_tangents", new { clipId = clip });
        Assert.All(Clip(s).Tracks.SelectMany(t => t.Keys), k => { Assert.Null(k.InTangent); Assert.Null(k.OutTangent); });

        Assert.Equal("not_found", Err(Run(s, "key.auto_tangents", new { clipId = clip, frames = "7" })));
        Assert.Equal("not_found", Err(Run(s, "key.auto_tangents", new { clipId = clip, trackId = "01ARZ3NDEKTSV4RRFFQ69G5FAV" })));
        Assert.Equal("invalid_args", Err(Run(s, "key.auto_tangents", new { clipId = clip, frames = "x" })));
    }

    // ---- copy / paste --------------------------------------------------------------------

    [Fact]
    public void Copy_IsReadOnly_RelativeFrames_AndFormat()
    {
        var (s, _, clip) = Setup();
        var history = s.UndoHistory.Count;
        var before = Json(Clip(s));
        var r = Run(s, "key.copy", new { clipId = clip, frames = "10,20", bones = H.Head });
        Assert.True(r.Ok);
        Assert.Equal(history, s.UndoHistory.Count);
        Assert.Equal(before, Json(Clip(s)));

        var doc = JsonNode.Parse((string)r.Data!["keysJson"]!)!.AsObject();
        Assert.Equal("30", (string)doc["fps"]!);
        var keys = doc["keys"]!.AsArray();
        Assert.Equal(new long[] { 0, 10 }, keys.Select(k => (long)k!["frame"]!).Distinct().OrderBy(x => x));
        var first = keys[0]!.AsObject();
        Assert.Equal("bone", (string)first["target"]!["kind"]!);
        Assert.Equal("rotation", (string)first["channel"]!);
        Assert.Equal("cubic", (string)first["interp"]!);
        Assert.Null(first["inTangent"]);
        Assert.Equal(4, first["value"]!.AsArray().Count);

        Assert.Equal("not_found", Err(Run(s, "key.copy", new { clipId = clip, frames = "7" })));
        Assert.Equal("not_found", Err(Run(s, "key.copy", new { clipId = clip, bones = "nope" })));
        Assert.Equal("not_found", Err(Run(s, "key.copy", new { clipId = "01ARZ3NDEKTSV4RRFFQ69G5FAV" })));
    }

    [Fact]
    public void PasteRoundTrip_IntoAnotherClip_AtOffset_EvaluatesEqual()
    {
        var (s, _, a) = Setup(30);
        var copy = Run(s, "key.copy", new { clipId = a, frames = "0,10,20" });
        var json = (string)copy.Data!["keysJson"]!;
        var b = (string)Run(s, "clip.create", new { name = "Other", durationFrames = 25 }).Data!["clipId"]!;
        Assert.True(Clip(s, 1).Skeleton.IsEmpty);

        var before = Json(Clip(s, 1));
        var r = RunUndoable(s, "key.paste", new { clipId = b, keysJson = json, frame = 20 }, clipIndex: 1);
        Assert.Contains(r.Warnings, w => w.Code == "clip_extended");
        Assert.Equal(41, Clip(s, 1).DurationFrames);
        Assert.Equal(Clip(s).Skeleton, Clip(s, 1).Skeleton);
        Assert.NotEqual(before, Json(Clip(s, 1)));

        var rig = s.Project.Rigs[0];
        var ev = new RigEvaluator(rig);
        for (var f = 0.0; f <= 20; f += 0.5)
        {
            var pa = ev.Model(ClipEvaluator.Evaluate(Clip(s, 0), rig, Flicks.FromSeconds(f / 30.0)));
            var pb = ev.Model(ClipEvaluator.Evaluate(Clip(s, 1), rig, Flicks.FromSeconds((f + 20) / 30.0)));
            for (var i = 0; i < pa.Length; i++)
                Assert.True(pa[i].Translation.ApproxEquals(pb[i].Translation, 1e-12) && pa[i].Rotation.ApproxEquals(pb[i].Rotation, 1e-12), $"bone {i} differs at frame {f}");
        }
    }

    [Fact]
    public void Paste_ReplacesKeysAndCopiesGroup()
    {
        var (s, _, clip) = Setup();
        var copy = (string)Run(s, "key.copy", new { clipId = clip, frames = "10", bones = H.Head }).Data!["keysJson"]!;
        var count = HeadRot(s).Keys.Count;
        // Paste on top of the key at frame 20: replaces, no extra key.
        var r = RunUndoable(s, "key.paste", new { clipId = clip, keysJson = copy, frame = 20 });
        Assert.Equal(count, HeadRot(s).Keys.Count);
        Assert.Equal(HeadRot(s).Keys[1].Value, HeadRot(s).Keys[2].Value);
        Assert.Empty(r.Warnings);

        // A new track created for an existing target inherits its Group.
        var head = s.Project.Rigs[0].FindBoneByName(H.Head)!.Id;
        var transTrack = Clip(s).Tracks.FirstOrDefault(t => t.Target.Id == head && t.Channel == TrackChannels.Translation);
        Assert.Null(transTrack);
        var doc = JsonNode.Parse(copy)!.AsObject();
        doc["keys"]!.AsArray().Add(new JsonObject
        {
            ["target"] = new JsonObject { ["kind"] = "bone", ["id"] = head.ToString() },
            ["channel"] = "translation", ["frame"] = 0, ["value"] = new JsonArray(0.0, 0.1, 0.0), ["interp"] = "linear",
            ["inTangent"] = null, ["outTangent"] = null,
        });
        Assert.True(Run(s, "key.paste", new { clipId = clip, keysJson = doc.ToJsonString(), frame = 3 }).Ok);
        var created = Clip(s).Tracks.Single(t => t.Target.Id == head && t.Channel == TrackChannels.Translation);
        Assert.Equal(HeadRot(s).Group, created.Group);
        Assert.Equal(Interpolation.Linear, created.Keys[0].Interp);
    }

    [Fact]
    public void Paste_BindsUnboundClipToMatchingRig_AndIgnoresUnknownTargets()
    {
        var (s, _, a) = Setup();
        var b = (string)Run(s, "clip.create", new { name = "B", durationFrames = 30 }).Data!["clipId"]!;
        var doc = JsonNode.Parse((string)Run(s, "key.copy", new { clipId = a, frames = "10", bones = H.Head }).Data!["keysJson"]!)!.AsObject();
        doc["keys"]!.AsArray().Add(new JsonObject
        {
            ["target"] = new JsonObject { ["kind"] = "bone", ["id"] = "01ARZ3NDEKTSV4RRFFQ69G5FAV" },
            ["channel"] = "rotation", ["frame"] = 0, ["value"] = new JsonArray(0.0, 0.0, 0.0, 1.0), ["interp"] = "cubic",
            ["inTangent"] = null, ["outTangent"] = null,
        });
        var r = Run(s, "key.paste", new { clipId = b, keysJson = doc.ToJsonString(), frame = 0 });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Contains(r.Warnings, w => w.Code == "ignored_targets");
        Assert.Equal(s.Project.Rigs[0].Id, Clip(s, 1).Skeleton);
        Assert.Single(Clip(s, 1).Tracks);

        // Now bound: pasting only unknown targets fails with rig_mismatch.
        var only = new JsonObject { ["fps"] = "30", ["keys"] = new JsonArray(doc["keys"]!.AsArray()[^1]!.DeepClone()) };
        Assert.Equal("rig_mismatch", Err(Run(s, "key.paste", new { clipId = b, keysJson = only.ToJsonString(), frame = 0 })));
        // Unbound clip + unknown targets only: no rig contains them.
        var c = (string)Run(s, "clip.create", new { name = "C", durationFrames = 30 }).Data!["clipId"]!;
        Assert.Equal("rig_mismatch", Err(Run(s, "key.paste", new { clipId = c, keysJson = only.ToJsonString(), frame = 0 })));
        Assert.True(Clip(s, 2).Skeleton.IsEmpty);
    }

    [Fact]
    public void Paste_Errors()
    {
        var (s, _, clip) = Setup();
        Assert.Equal("invalid_args", Err(Run(s, "key.paste", new { clipId = clip, keysJson = "not json", frame = 0 })));
        Assert.Equal("invalid_args", Err(Run(s, "key.paste", new { clipId = clip, keysJson = "{\"keys\":[]}", frame = 0 })));
        Assert.Equal("invalid_args", Err(Run(s, "key.paste", new { clipId = clip, keysJson = "{\"keys\":[{\"channel\":\"rotation\"}]}", frame = 0 })));
        Assert.Equal("invalid_args", Err(Run(s, "key.paste", new { clipId = clip, keysJson = "{}", frame = -3 })));
        var copy = (string)Run(s, "key.copy", new { clipId = clip }).Data!["keysJson"]!;
        Assert.Equal("not_found", Err(Run(s, "key.paste", new { clipId = "01ARZ3NDEKTSV4RRFFQ69G5FAV", keysJson = copy, frame = 0 })));
    }

    [Fact]
    public void AllCommands_AreRegistered()
    {
        foreach (var id in new[] { "marker.add", "marker.update", "marker.delete", "clip.add_phase_markers", "clip.set_loop", "key.set_value", "key.set_tangents", "key.auto_tangents", "key.copy", "key.paste", "track.set_flags" })
            Assert.True(Registry.TryGet(id, out var d), id);
        Assert.True(Registry.TryGet("key.copy", out var copy));
        Assert.Equal(CommandEffect.ReadOnly, copy.Effect);
    }
}
