using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.Advanced;

public class SequenceTests
{
    static (ProjectSession S, string Man, string Walk, string Run, string Seq, string WalkItem, string RunItem) WalkToRun(int blend = 10, bool matchPhase = true)
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var walk = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
        var run = P8.ApplyTemplate(s, "builtin.run_cycle", man);
        var seq = (string)P8.Ok(s, "sequence.create", new { characterId = man, name = "Walk to run" }).Data!["sequenceId"]!;
        var a = (string)P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = walk, loops = 2 }).Data!["itemId"]!;
        var b = (string)P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = run, loops = 2, blendFrames = blend, matchFootPhase = matchPhase }).Data!["itemId"]!;
        return (s, man, walk, run, seq, a, b);
    }

    static SequenceEvaluator Ev(ProjectSession s, string seq) => new(s.Project, s.Project.FindSequence(ObjectId.Parse(seq))!);

    static Transform[][] Frames(SequenceEvaluator ev)
    {
        var e = new RigEvaluator(ev.Rig);
        return Enumerable.Range(0, (int)ev.LastFrame + 1).Select(f => e.Model(ev.Evaluate(f).Pose)).ToArray();
    }

    /// <summary>Largest second difference (m/frame²) of any joint position over frames [from, to].</summary>
    static double MaxJointAcceleration(Transform[][] m, long from, long to)
    {
        var worst = 0.0;
        for (var f = Math.Max(1, from); f <= Math.Min(m.Length - 2, to); f++)
            for (var b = 0; b < m[f].Length; b++)
                worst = Math.Max(worst, (m[f + 1][b].Translation - m[f][b].Translation * 2 + m[f - 1][b].Translation).Length());
        return worst;
    }

    /// <summary>Phase 8 acceptance 3: walk → run with a 10-frame transition, no pose/velocity jump, root continues, bake + foot lock without sliding.</summary>
    [Fact]
    public void Acceptance_Phase8_BlendWalkToRun_ContinuousRoot_NoSlidingAfterBake()
    {
        var (s, man, walk, run, seq, walkItem, runItem) = WalkToRun();
        var ev = Ev(s, seq);
        var range = ev.RangeOf(ObjectId.Parse(runItem))!.Value;
        var start = (long)range.Start;
        var walkEnd = ev.RangeOf(ObjectId.Parse(walkItem))!.Value.End;
        Assert.Equal(walkEnd - 10, range.Start, 6); // default placement: fades in over the end of the walk
        var m = Frames(ev);
        var rm = ev.RootMotionBone;

        // Weights: the walk alone before, both during, the run alone after.
        Assert.Single(ev.Evaluate(start - 1).Active);
        var mid = ev.Evaluate(start + 5).Active;
        Assert.Equal(2, mid.Count);
        Assert.Equal(1.0, mid.Sum(c => c.Weight), 9);
        Assert.Equal(0.5, mid.Single(c => c.ItemId == ObjectId.Parse(runItem)).Weight, 9); // smoothstep(0.5)
        Assert.Single(ev.Evaluate(start + 10).Active);

        // At the transition start the pose is exactly the walk's (weight 0): no jump.
        var walkOnly = new RigEvaluator(ev.Rig).Model(ClipEvaluator.Evaluate(P8.Clip(s, walk), ev.Rig,
            Flicks.FromFrame(start - P8.Clip(s, walk).Loop.End.ToFrame(P8.Clip(s, walk).Fps), P8.Clip(s, walk).Fps)));
        // (the walk is on its second loop there: its pose equals the clip's, moved by one cycle of travel)
        var cycle = m[start][rm].Translation - walkOnly[rm].Translation;
        for (var b = 0; b < m[start].Length; b++)
            Assert.True((m[start][b].Translation - (walkOnly[b].Translation + cycle)).Length() < 1e-9, $"bone {b}");

        // Root continuity: the run continues forward from where the walk is; no velocity jump at the transition.
        for (var f = 1; f < m.Length; f++) Assert.True(m[f][rm].Translation.Z > m[f - 1][rm].Translation.Z, $"root goes back at {f}");
        Assert.True(m[^1][rm].Translation.Z > m[start][rm].Translation.Z + 3, "the run travels on from the walk");
        var accTransition = MaxJointAcceleration(m, start - 2, start + 12);
        var accRun = MaxJointAcceleration(m, start + 14, m.Length - 2);
        var accWalk = MaxJointAcceleration(m, 2, start - 4);
        Assert.True(accTransition <= 1.1 * Math.Max(accRun, accWalk), $"transition {accTransition:0.0000} vs walk {accWalk:0.0000} / run {accRun:0.0000} m/frame²");
        var rootAcc = Enumerable.Range((int)start - 1, 14).Max(f => (m[f + 1][rm].Translation - m[f][rm].Translation * 2 + m[f - 1][rm].Translation).Length());
        Assert.True(rootAcc < 0.02, $"root acceleration {rootAcc:0.0000} m/frame²");

        // Bake (with foot lock): the baked clip plays like the sequence; the support foot does not slide in the transition.
        var bake = P8.Ok(s, "sequence.bake", new { sequenceId = seq, footLock = true }).Data!;
        var clip = P8.Clip(s, (string)bake["clipId"]!);
        Assert.False(clip.IsDraft);
        Assert.Equal(ev.LastFrame, clip.DurationFrames);
        Assert.Contains(clip.Markers, x => x.Name == "Run cycle" && x.Time == Flicks.FromFrame(start, clip.Fps));
        var rig = ev.Rig;
        // Every foot contact that overlaps the transition stays planted (horizontal drift ≤ 1 cm).
        var inTransition = bake["footLock"]!["spans"]!.AsArray()
            .Where(x => (long)x!["endFrame"]! >= start && (long)x["startFrame"]! <= start + 10).ToList();
        Assert.NotEmpty(inTransition);
        foreach (var span in inTransition)
        {
            var foot = P8.Bone(rig, H.Foot((string)span!["side"]! == "left" ? Side.Left : Side.Right));
            var first = P8.Model(rig, clip, (long)span["startFrame"]!)[foot].Translation;
            for (var f = (long)span["startFrame"]!; f <= (long)span["endFrame"]!; f++)
            {
                var p = P8.Model(rig, clip, f)[foot].Translation;
                var drift = Math.Sqrt(Math.Pow(p.X - first.X, 2) + Math.Pow(p.Z - first.Z, 2));
                Assert.True(drift <= 0.01, $"{span["side"]} foot drifts {drift:0.000} m at frame {f}");
            }
        }
        // Without foot lock the bake reproduces the sequence within the key-reduction tolerance.
        var plain = (string)P8.Ok(s, "sequence.bake", new { sequenceId = seq, name = "plain" }).Data!["clipId"]!;
        var pc = P8.Clip(s, plain);
        for (var f = 0; f <= ev.LastFrame; f += 3)
        {
            var b = P8.Model(rig, pc, f);
            for (var i = 0; i < b.Length; i++) Assert.True((b[i].Translation - m[f][i].Translation).Length() < 2e-3, $"frame {f} bone {i}");
        }
        s.Undo(CommandOrigin.Test);
        Assert.Null(s.Project.FindClip(ObjectId.Parse(plain)));
    }

    [Fact]
    public void MatchFootPhase_ReducesSliding_ComparedWithAPhaseMismatch()
    {
        double SupportSlide(bool match)
        {
            var (s, _, _, _, seq, _, runItem) = WalkToRun(10, match);
            var ev = Ev(s, seq);
            var start = (long)ev.RangeOf(ObjectId.Parse(runItem))!.Value.Start;
            var m = Frames(ev);
            var total = 0.0;
            foreach (var side in P8.Sides)
            {
                var foot = P8.Bone(ev.Rig, H.Foot(side));
                for (var f = start + 1; f <= start + 10; f++)
                {
                    var a = m[f - 1][foot].Translation;
                    var b = m[f][foot].Translation;
                    if (a.Y < 0.09 && b.Y < 0.09) total += Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Z - a.Z, 2));
                }
            }
            return total;
        }
        var matched = SupportSlide(true);
        var unmatched = SupportSlide(false);
        Assert.True(matched < unmatched, $"matched {matched:0.000} m vs unmatched {unmatched:0.000} m");
    }

    [Fact]
    public void AlignRoot_MovesTheSecondClipToWhereTheFirstIs()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var walk = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
        var idle = P8.ApplyTemplate(s, "builtin.idle_breathe", man);
        var seq = (string)P8.Ok(s, "sequence.create", new { characterId = man, name = "Walk then stand" }).Data!["sequenceId"]!;
        P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = walk });
        var item = (string)P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = idle, blendFrames = 0 }).Data!["itemId"]!;
        var ev = Ev(s, seq);
        var start = (long)Math.Round(ev.RangeOf(ObjectId.Parse(item))!.Value.Start);
        var m = Frames(ev);
        var rm = ev.RootMotionBone;
        var dz = m[start][rm].Translation.Z - m[start - 1][rm].Translation.Z;
        Assert.InRange(dz, -0.001, 0.06); // continues (a walking step at most), no jump back to the idle's origin
        Assert.True(m[^1][rm].Translation.Z > 2);

        P8.Ok(s, "sequence.update", new { sequenceId = seq, alignRoot = false });
        var m2 = Frames(Ev(s, seq));
        Assert.True(Math.Abs(m2[^1][rm].Translation.Z) < 0.1, "without alignment the idle plays at its own origin");
    }

    [Fact]
    public void Loops_RepeatWithTravel_AndSpeedScalesTiming()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var walk = P8.ApplyTemplate(s, "builtin.walk_cycle", man);
        var clip = P8.Clip(s, walk);
        var loopFrames = clip.Loop.End.ToFrame(clip.Fps);
        var seq = (string)P8.Ok(s, "sequence.create", new { characterId = man, name = "Fast" }).Data!["sequenceId"]!;
        P8.Ok(s, "sequence.add_clip", new { sequenceId = seq, clipId = walk, loops = 3, speed = 2 });
        var ev = Ev(s, seq);
        Assert.Equal((long)Math.Ceiling(3 * loopFrames / 2.0 - 1e-6), ev.LastFrame);
        var rig = ev.Rig;
        var e = new RigEvaluator(rig);
        var rm = ev.RootMotionBone;
        var travel = P8.Model(rig, clip, loopFrames)[rm].Translation.Z - P8.Model(rig, clip, 0)[rm].Translation.Z;
        var endZ = e.Model(ev.Evaluate(ev.LastFrame).Pose)[rm].Translation.Z - e.Model(ev.Evaluate(0).Pose)[rm].Translation.Z;
        Assert.Equal(3 * travel, endZ, 2);
        var sample = ev.Evaluate(loopFrames / 2.0 + 0.5);
        Assert.Equal(1, sample.Active.Single().Cycle);
    }

    [Fact]
    public void SequenceCommands_EditAndUndo_AreExact()
    {
        var (s, man, walk, run, seq, walkItem, runItem) = WalkToRun();
        string Snap() => P8.Json(s.Project.Sequences);
        var before = Snap();

        var r = P8.Ok(s, "sequence.set_blend", new { sequenceId = seq, itemId = runItem, blendFrames = 20, blendCurve = "linear" });
        var item = s.Project.FindSequence(ObjectId.Parse(seq))!.Items.Single(i => i.Id == ObjectId.Parse(runItem));
        Assert.Equal(20, item.BlendInFrames);
        Assert.Equal(BlendCurve.Linear, item.BlendCurve);
        P8.Ok(s, "sequence.update_item", new { sequenceId = seq, itemId = runItem, startFrame = 100, speed = 1.5 });
        P8.Ok(s, "sequence.update_item", new { sequenceId = seq, itemId = runItem, startFrame = 101 });
        Assert.Equal("invalid_args", P8.Err(P8.Run(s, "sequence.update_item", new { sequenceId = seq, itemId = runItem, inFrame = 9999 })));
        Assert.Equal("invalid_args", P8.Err(P8.Run(s, "sequence.set_blend", new { sequenceId = seq, itemId = runItem, blendCurve = "bouncy" })));
        Assert.Equal("not_found", P8.Err(P8.Run(s, "sequence.set_blend", new { sequenceId = seq, itemId = ObjectId.New().ToString(), blendFrames = 1 })));
        var inspect = P8.Ok(s, "sequence.inspect", new { sequenceId = seq }).Data!;
        Assert.Equal(2, inspect["items"]!.AsArray().Count);
        Assert.Equal(101, (long)inspect["items"]![1]!["startFrame"]!);
        var eval = P8.Ok(s, "sequence.evaluate", new { sequenceId = seq, frame = 105 }).Data!;
        Assert.Equal(2, eval["active"]!.AsArray().Count);

        P8.Ok(s, "sequence.remove", new { sequenceId = seq, itemId = walkItem });
        Assert.Single(s.Project.FindSequence(ObjectId.Parse(seq))!.Items);
        P8.Ok(s, "sequence.remove", new { sequenceId = seq });
        Assert.Empty(s.Project.Sequences);
        for (var i = 0; i < 5; i++) s.Undo(CommandOrigin.Test);
        Assert.Equal(before, Snap());

        // A clip of another rig is refused; a deleted clip is skipped with a warning.
        var (s2, man2, ces) = P8.Scene();
        var cw = P8.ApplyTemplate(s2, "builtin.walk_cycle", ces);
        var seq2 = (string)P8.Ok(s2, "sequence.create", new { characterId = man2, name = "x" }).Data!["sequenceId"]!;
        Assert.Equal("rig_mismatch", P8.Err(P8.Run(s2, "sequence.add_clip", new { sequenceId = seq2, clipId = cw })));
        var mw = P8.ApplyTemplate(s2, "builtin.wave", man2);
        P8.Ok(s2, "sequence.add_clip", new { sequenceId = seq2, clipId = mw });
        P8.Ok(s2, "clip.delete", new { clipId = mw });
        Assert.Equal("empty_sequence", P8.Err(P8.Run(s2, "sequence.evaluate", new { sequenceId = seq2, frame = 0 })));
        Assert.Contains(P8.Ok(s2, "sequence.inspect", new { sequenceId = seq2 }).Warnings, w => w.Code == "empty_sequence");
    }

    [Fact]
    public void BlendWeights_FollowTheCurves()
    {
        Assert.Equal(0, SequenceEvaluator.Weight(BlendCurve.Smooth, 0));
        Assert.Equal(1, SequenceEvaluator.Weight(BlendCurve.Smooth, 1));
        Assert.Equal(0.5, SequenceEvaluator.Weight(BlendCurve.Smooth, 0.5), 12);
        Assert.Equal(0.25, SequenceEvaluator.Weight(BlendCurve.Linear, 0.25), 12);
        Assert.Equal(0.0625, SequenceEvaluator.Weight(BlendCurve.EaseIn, 0.25), 12);
        Assert.Equal(0.4375, SequenceEvaluator.Weight(BlendCurve.EaseOut, 0.25), 12);
        Assert.Equal(1, SequenceEvaluator.Weight(BlendCurve.Linear, 7));
        // Slope of smoothstep at the ends is zero (velocity continuity).
        Assert.True(SequenceEvaluator.Weight(BlendCurve.Smooth, 1e-4) < 1e-7);
    }
}
