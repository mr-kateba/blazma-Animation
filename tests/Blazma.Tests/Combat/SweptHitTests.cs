using Blazma.Combat;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Tests.Advanced;

namespace Blazma.Tests.Combat;

/// <summary>Phase 9 review regressions for the swept hit test: fast rotations, spinning boxes and moving defenders must not
/// tunnel, the first-hit frame matches a 2000-samples-per-frame ground truth, and the work stays within its budget.</summary>
public class SweptHitTests
{
    const int TruthSamples = 2000;

    internal static (Project P, Character Atk, Rig RigA, Character Def, Rig RigD) TwoSingles()
    {
        var p = new Project();
        var ra = new Rig { Name = "A", Bones = { new Bone { Name = "root" } } };
        var rd = new Rig { Name = "D", Bones = { new Bone { Name = "root" } } };
        p.Rigs.Add(ra); p.Rigs.Add(rd);
        var a = new Character { Name = "Atk", Rig = ra.Id };
        var d = new Character { Name = "Def", Rig = rd.Id };
        p.Characters.Add(a); p.Characters.Add(d);
        return (p, a, ra, d, rd);
    }

    /// <summary>A one-bone clip keyed every frame: rotation <paramref name="degPerFrame"/>·f about <paramref name="axis"/> and translation from + step·f.</summary>
    internal static Clip Motion(Rig rig, int frames, Vec3 axis, double degPerFrame, Vec3 from, Vec3 step, double startDeg = 0)
    {
        var c = new Clip { Skeleton = rig.Id, Fps = FrameRate.Fps30, Duration = Flicks.FromFrame(frames, FrameRate.Fps30) };
        var target = new TrackTarget { Kind = TargetKind.Bone, Id = rig.Bones[0].Id };
        var rot = new Track { Target = target, Channel = TrackChannels.Rotation };
        var pos = new Track { Target = target, Channel = TrackChannels.Translation };
        for (var f = 0; f <= frames; f++)
        {
            var q = Quat.FromAxisAngle(axis, (startDeg + degPerFrame * f) * Math.PI / 180);
            rot.Keys.Add(new Keyframe { Time = Flicks.FromFrame(f, c.Fps), Value = new[] { q.X, q.Y, q.Z, q.W }, Interp = Interpolation.Linear });
            var v = from + step * f;
            pos.Keys.Add(new Keyframe { Time = Flicks.FromFrame(f, c.Fps), Value = new[] { v.X, v.Y, v.Z }, Interp = Interpolation.Linear });
        }
        c.Tracks.Add(rot);
        c.Tracks.Add(pos);
        return c;
    }

    internal static Hitbox Hb(Rig rig, CollisionShape s, Transform offset, int frames, FrameRate fps) => new()
    {
        Name = "hb", Shape = s,
        Attachment = new HitboxAttachment { Kind = HitboxAttachKind.Bone, Target = rig.Bones[0].Id, Offset = offset },
        Active = { new ClipRange { Start = Flicks.Zero, End = Flicks.FromFrame(frames, fps) } },
    };

    internal static Hurtbox Hu(Rig rig, CollisionShape s, Vec3 at) =>
        new() { Bone = rig.Bones[0].Id, Shape = s, Offset = new Transform(at, Quat.Identity, Vec3.One) };

    /// <summary>Ground truth: the first attack frame (ceil of the sample time) at which any fine sample overlaps, or null.</summary>
    static long? TruthFrame(Project p, Character a, Clip atk, Character d, Clip? dc, Transform placement)
    {
        var ra = p.FindRig(a.Rig)!; var rd = p.FindRig(d.Rig)!;
        var h = atk.Combat!.Hitboxes[0];
        var last = atk.DurationFrames - 1;
        for (var i = 0; i <= last * TruthSamples; i++)
        {
            var t = CombatEvaluator.TimeOf((double)i / TruthSamples, atk.Fps);
            var ma = CombatEvaluator.Model(ra, a, atk, t);
            var md = CombatEvaluator.Model(rd, d, dc, t);
            var ws = CombatEvaluator.HitboxShape(p, h, atk, ra, a.Id, ma, t, Transform.Identity)!.Value;
            foreach (var hu in d.Combat!.Hurtboxes)
                if (Collision.Test(ws, CombatEvaluator.HurtboxShape(rd, hu, md, placement)!.Value, out _))
                    return (i + TruthSamples - 1) / TruthSamples;
        }
        return null;
    }

    static readonly HitTestOptions AtOrigin = new() { DefenderPlacement = Transform.Identity };

    [Fact]
    public void RotatingThinCapsule_90DegreesPerFrame_IsFoundWithDefaults()
    {
        var (p, a, ra, d, rd) = TwoSingles();
        var clip = Motion(ra, 2, Vec3.UnitY, 90, Vec3.Zero, Vec3.Zero);
        // A 2 m capsule along X (radius 1 cm) spinning about its centre; a 1 cm target 0.5 m out at 40°.
        clip.Combat = new ClipCombat { Hitboxes = { Hb(ra, new CollisionShape { Kind = CollisionShapeKind.Capsule, Radius = 0.01, Length = 2 },
            new Transform(Vec3.Zero, Quat.FromAxisAngle(Vec3.UnitZ, -Math.PI / 2), Vec3.One), 2, clip.Fps) } };
        p.Clips.Add(clip);
        var th = 40 * Math.PI / 180;
        d.Combat = new CharacterCombat { Hurtboxes = { Hu(rd, new CollisionShape { Kind = CollisionShapeKind.Sphere, Radius = 0.01 }, new Vec3(0.5 * Math.Cos(th), 0, -0.5 * Math.Sin(th))) } };
        var rep = HitDetector.Run(p, a, clip, d, null, AtOrigin);
        Assert.Equal(1, TruthFrame(p, a, clip, d, null, Transform.Identity));
        Assert.Equal(1, Assert.Single(rep.Hits).Frame);
        Assert.InRange(rep.Hits[0].Time, 0.40, 0.48); // 40° of the 90° swing
        // 314 steps would be needed for the bound; the cap (8) was used and said so.
        Assert.True(rep.MayHaveMissedContacts);
        Assert.Contains(rep.Warnings, w => w.StartsWith(HitDetector.MissedWarningCode, StringComparison.Ordinal));
    }

    [Fact]
    public void SpinningLongBox_IsFoundWithDefaults()
    {
        var (p, a, ra, d, rd) = TwoSingles();
        var clip = Motion(ra, 2, Vec3.UnitY, 90, Vec3.Zero, Vec3.Zero);
        clip.Combat = new ClipCombat { Hitboxes = { Hb(ra, new CollisionShape { Kind = CollisionShapeKind.Box, Size = new Vec3(1.0, 0.02, 0.02) }, Transform.Identity, 2, clip.Fps) } };
        p.Clips.Add(clip);
        d.Combat = new CharacterCombat { Hurtboxes = { Hu(rd, new CollisionShape { Kind = CollisionShapeKind.Sphere, Radius = 0.02 },
            new Vec3(0.35 * Math.Cos(Math.PI / 4), 0, -0.35 * Math.Sin(Math.PI / 4))) } };
        var rep = HitDetector.Run(p, a, clip, d, null, AtOrigin);
        Assert.Equal(1, TruthFrame(p, a, clip, d, null, Transform.Identity));
        Assert.Equal(1, Assert.Single(rep.Hits).Frame);
        // The sub-step need is measured from the box corners, not its centre (which does not move at all).
        Assert.True(rep.SubstepsRequired > 8);
    }

    [Fact]
    public void DefenderDashingThroughAStaticHitbox_IsFound()
    {
        var (p, a, ra, d, rd) = TwoSingles();
        var clip = new Clip { Skeleton = ra.Id, Fps = FrameRate.Fps30, Duration = Flicks.FromFrame(3, FrameRate.Fps30) };
        clip.Combat = new ClipCombat { Hitboxes = { Hb(ra, new CollisionShape { Kind = CollisionShapeKind.Sphere, Radius = 0.05 }, Transform.Identity, 3, clip.Fps) } };
        p.Clips.Add(clip);
        // 9 m/s at 30 fps: 0.3 m per frame, frames 0 and 1 on either side of the hitbox.
        var dash = Motion(rd, 3, Vec3.UnitY, 0, new Vec3(-0.15, 0, 0), new Vec3(0.3, 0, 0));
        p.Clips.Add(dash);
        d.Combat = new CharacterCombat { Hurtboxes = { Hu(rd, new CollisionShape { Kind = CollisionShapeKind.Sphere, Radius = 0.05 }, Vec3.Zero) } };
        foreach (var maxSub in new[] { 1, 8, 64 })
        {
            var rep = HitDetector.Run(p, a, clip, d, dash, AtOrigin with { MaxSubsteps = maxSub });
            Assert.Equal(1, Assert.Single(rep.Hits).Frame);
            Assert.False(rep.Hits[0].Swept && maxSub == 64);
        }
        Assert.Equal(1, TruthFrame(p, a, clip, d, dash, Transform.Identity));
    }

    [Fact]
    public void RandomMotions_FirstHitFrame_MatchesFineGroundTruth()
    {
        var rnd = new Random(9091);
        var compared = 0;
        var hits = 0;
        for (var n = 0; n < 60; n++)
        {
            var (p, a, ra, d, rd) = TwoSingles();
            var frames = 4;
            double R(double lo, double hi) => lo + (hi - lo) * rnd.NextDouble();
            Vec3 Dir() => new Vec3(R(-1, 1), R(-1, 1), R(-1, 1)).Normalized();
            var kind = (CollisionShapeKind)(n % 3);
            var shape = kind switch
            {
                CollisionShapeKind.Sphere => new CollisionShape { Kind = kind, Radius = R(0.01, 0.06) },
                CollisionShapeKind.Capsule => new CollisionShape { Kind = kind, Radius = R(0.01, 0.04), Length = R(0.3, 1.5) },
                _ => new CollisionShape { Kind = kind, Size = new Vec3(R(0.3, 1.2), R(0.02, 0.06), R(0.02, 0.06)) },
            };
            var clip = Motion(ra, frames, Dir(), R(10, 120), new Vec3(R(-0.3, 0.3), 0, R(-0.3, 0.3)), Dir() * R(0, 0.25));
            clip.Combat = new ClipCombat { Hitboxes = { Hb(ra, shape, new Transform(Dir() * R(0, 0.4), Quat.FromAxisAngle(Dir(), R(0, Math.PI)), Vec3.One), frames, clip.Fps) } };
            p.Clips.Add(clip);
            Clip? dc = null;
            if (n % 2 == 0)
            {
                dc = Motion(rd, frames, Vec3.UnitY, R(-60, 60), new Vec3(R(-0.5, 0.5), 0, R(-0.5, 0.5)), new Vec3(R(-0.3, 0.3), 0, R(-0.3, 0.3)));
                p.Clips.Add(dc);
            }
            // Put the target near where the hitbox passes at a random moment, so about half the scenarios hit.
            var tStar = CombatEvaluator.TimeOf(R(0.5, frames - 1), clip.Fps);
            var hw = CombatEvaluator.HitboxShape(p, clip.Combat.Hitboxes[0], clip, ra, a.Id, CombatEvaluator.Model(ra, a, clip, tStar), tStar, Transform.Identity)!.Value;
            var bone = CombatEvaluator.Model(rd, d, dc, tStar)[0];
            var near = bone.Inverse().TransformPoint(hw.A + (hw.B - hw.A) * R(0, 1)) + Dir() * R(0, 0.3);
            d.Combat = new CharacterCombat { Hurtboxes = { Hu(rd, new CollisionShape { Kind = CollisionShapeKind.Sphere, Radius = R(0.02, 0.08) }, near) } };
            var placement = Transform.Identity;
            var truth = TruthFrame(p, a, clip, d, dc, placement);
            var rep = HitDetector.Run(p, a, clip, d, dc, new HitTestOptions { DefenderPlacement = placement });
            var got = rep.First?.Frame;
            Assert.True(truth == got, $"case {n}: truth {truth?.ToString() ?? "none"}, detector {got?.ToString() ?? "none"} (time {rep.First?.Time})");
            compared++;
            if (truth is not null) hits++;
        }
        Assert.Equal(60, compared);
        Assert.True(hits is >= 10 and <= 50, $"{hits} of 60 scenarios hit"); // the scenarios exercise both outcomes
    }

    static (Project P, Character A, Clip Clip, Character D) ManyBones(int bones, int frames)
    {
        var (p, a, ra, d, rd) = TwoSingles();
        for (var i = 1; i < bones; i++) ra.Bones.Add(new Bone { Name = "a" + i, Parent = ra.Bones[i - 1].Id, Rest = new Transform(new Vec3(0, 0.001, 0), Quat.Identity, Vec3.One) });
        for (var i = 1; i < bones; i++) rd.Bones.Add(new Bone { Name = "d" + i, Parent = rd.Bones[i - 1].Id, Rest = new Transform(new Vec3(0, 0.001, 0), Quat.Identity, Vec3.One) });
        var clip = Motion(ra, frames, Vec3.UnitY, 30, Vec3.Zero, Vec3.Zero);
        clip.Combat = new ClipCombat();
        // 32 hitboxes at different radii (so different speeds): the shared grid must still evaluate each time once.
        for (var i = 0; i < 32; i++)
            clip.Combat.Hitboxes.Add(Hb(ra, new CollisionShape { Kind = CollisionShapeKind.Sphere, Radius = 0.01 },
                new Transform(new Vec3(0.2 + 0.11 * i, 0, 0), Quat.Identity, Vec3.One), frames, clip.Fps));
        p.Clips.Add(clip);
        d.Combat = new CharacterCombat();
        for (var i = 0; i < 15; i++) d.Combat.Hurtboxes.Add(Hu(rd, new CollisionShape { Kind = CollisionShapeKind.Sphere, Radius = 0.01 }, new Vec3(0, 50 + i, 0)));
        return (p, a, clip, d);
    }

    [Fact]
    public void PoseWork_StaysWithinTheBudget_AndHugeTestsAreRefusedUpFront()
    {
        // 1000 bones each, 32 hitboxes, 156 frames at maxSubsteps 64: refused before any work.
        var (p, a, clip, d) = ManyBones(1000, 156);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var e = Assert.Throws<CombatException>(() => HitDetector.Run(p, a, clip, d, null, AtOrigin with { MaxSubsteps = 64 }));
        Assert.Equal("too_large", e.Code);
        Assert.True(sw.ElapsedMilliseconds < 500, $"refusal took {sw.ElapsedMilliseconds} ms");

        // A smaller one runs: each sample time evaluated once for all 32 hitboxes (one shared grid per frame).
        var (p2, a2, clip2, d2) = ManyBones(50, 40);
        var rep = HitDetector.Run(p2, a2, clip2, d2, null, AtOrigin with { MaxSubsteps = 64 });
        Assert.Empty(rep.Hits);
        Assert.True(rep.Evaluations <= rep.FramesTested * 64 + 1, $"{rep.Evaluations} evaluations for {rep.FramesTested} frames");
        Assert.True(rep.BoneWork <= HitDetector.PlannedPoseWork(rep.FramesTested, 64, 32, 100), $"bone work {rep.BoneWork}");
    }

    [Fact]
    public void MannequinHitTest_RunsUnderOneSecond()
    {
        var c = C9.Build();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = P8.Ok(c.S, "combat.hit_test", new { attackerCharacterId = c.Attacker, defenderCharacterId = c.Defender, attackClipId = c.Attack, defenderZ = 0.75, maxSubsteps = 64 });
        sw.Stop();
        Assert.True((bool)r.Data!["hit"]!);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"{sw.ElapsedMilliseconds} ms");
    }
}
