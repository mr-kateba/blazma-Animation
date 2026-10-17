using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Physics;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.Physics;

/// <summary>Shared poses and clips for the physics tests.</summary>
static class PhysicsFixtures
{
    public const double Deg = Math.PI / 180;

    public static void Rotate(Rig rig, Pose pose, string bone, Vec3 axis, double degrees)
    {
        var b = rig.FindBoneByName(bone)!;
        pose.Bones[b.Id.ToString()] = pose.LocalOf(b) with { Rotation = Quat.FromAxisAngle(axis, degrees * Deg) };
    }

    public static void Translate(Rig rig, Pose pose, string bone, Vec3 local)
    {
        var b = rig.FindBoneByName(bone)!;
        pose.Bones[b.Id.ToString()] = pose.LocalOf(b) with { Translation = local };
    }

    public static void PinFeet(Rig rig, Pose pose)
    {
        foreach (var c in rig.IkChains.Where(c => c.Name.StartsWith("leg", StringComparison.Ordinal)))
            pose.Controls[c.Target.ToString()] = new ControlState { Transform = rig.GetControl(c.Target).Rest, Pinned = true };
    }

    public static Pose ArmsDown(Rig rig)
    {
        var pose = new Pose();
        Rotate(rig, pose, H.UpperArm(Side.Left), Vec3.UnitZ, -90);
        Rotate(rig, pose, H.UpperArm(Side.Right), Vec3.UnitZ, 90);
        PoseSolver.Update(rig, pose);
        return pose;
    }

    /// <summary>Feet planted at rest, knees bent, hips pushed forward and the trunk leaning 60° forward: COM ahead of the toes.</summary>
    public static Pose Lean(Rig rig, double amount = 1)
    {
        var pose = new Pose();
        PinFeet(rig, pose);
        Translate(rig, pose, H.Pelvis, new Vec3(0, 0.95 - 0.12 * amount, 0.12 * amount));
        Rotate(rig, pose, H.Spine1, Vec3.UnitX, 60 * amount);
        PoseSolver.Update(rig, pose);
        return pose;
    }

    /// <summary>A clip with one rotation track on <paramref name="bone"/>: (seconds, degrees about X) keys.</summary>
    public static Clip RotationClip(Rig rig, string bone, FrameRate fps, double seconds, params (double Time, double Degrees)[] keys)
    {
        var clip = new Clip { Fps = fps, Duration = Flicks.FromSeconds(seconds), Skeleton = rig.Id };
        var track = new Track { Target = new TrackTarget { Kind = TargetKind.Bone, Id = rig.FindBoneByName(bone)!.Id }, Channel = TrackChannels.Rotation };
        foreach (var (t, d) in keys)
        {
            var q = Quat.FromAxisAngle(Vec3.UnitX, d * Deg);
            track.Keys.Add(new Keyframe { Time = Flicks.FromSeconds(t), Value = new[] { q.X, q.Y, q.Z, q.W }, Interp = Interpolation.Cubic });
        }
        clip.Tracks.Add(track);
        return clip;
    }
}

public class MassModelTests
{
    [Fact]
    public void Mannequin_TotalMass_And_RestCom_MatchAnthropometry()
    {
        var rig = MannequinFactory.Create();
        var mass = MassModel.For(rig);
        Assert.Equal(MassSource.BoneMasses, mass.Source);
        Assert.InRange(mass.TotalMass, 74.9, 75.1);
        Assert.InRange(mass.EstimatedHeight, 1.75, 1.85);

        // T-pose: arms at shoulder height raise the COM a little.
        var tpose = mass.CenterOfMass(new Pose());
        Assert.InRange(tpose.Y, 1.00, 1.07);
        // Arms down (anatomical standing): ≈ 0.55–0.57 of the 1.80 m height (Winter).
        var standing = mass.CenterOfMass(PhysicsFixtures.ArmsDown(rig));
        Assert.InRange(standing.Y, 0.96, 1.03);
        Assert.InRange(standing.Y / 1.80, 0.53, 0.575);
        Assert.True(standing.Y < tpose.Y);
        Assert.Equal(0, standing.X, 12);
        Assert.InRange(standing.Z, -0.02, 0.02);
    }

    [Fact]
    public void SegmentCom_UsesWinterFractions()
    {
        var rig = MannequinFactory.Create();
        var mass = MassModel.For(rig);
        var ev = new RigEvaluator(rig);
        var rest = ev.RestModel();
        Vec3 P(string n) => rest[rig.IndexOf(rig.FindBoneByName(n)!.Id)].Translation;
        MassSegment Seg(string n) => mass.Segments.Single(s => s.Bone == rig.IndexOf(rig.FindBoneByName(n)!.Id));

        var thigh = Seg(H.Thigh(Side.Left));
        Assert.True(thigh.RestCom.ApproxEquals(Vec3.Lerp(P(H.Thigh(Side.Left)), P(H.Calf(Side.Left)), 0.433), 1e-12));
        Assert.Equal(7.5, thigh.Mass, 6);
        var forearm = Seg(H.LowerArm(Side.Right));
        Assert.True(forearm.RestCom.ApproxEquals(Vec3.Lerp(P(H.LowerArm(Side.Right)), P(H.Hand(Side.Right)), 0.430), 1e-12));
        // Leaf segment: the head's COM is above the head joint.
        Assert.InRange(Seg(H.Head).RestCom.Y, P(H.Head).Y + 0.05, P(H.Head).Y + 0.15);
        // The root has no mass and no segment.
        Assert.DoesNotContain(mass.Segments, s => s.Bone == 0);
    }

    [Fact]
    public void SymmetricPose_HasComOnTheMidline_AsymmetricShiftsIt()
    {
        var rig = MannequinFactory.Create();
        var mass = MassModel.For(rig);
        // Mirroring across the YZ plane maps a rotation about (x, y, z) to one about (x, −y, −z).
        var pose = new Pose();
        PhysicsFixtures.Rotate(rig, pose, H.UpperArm(Side.Left), new Vec3(0.3, 1, -0.5), 50);
        PhysicsFixtures.Rotate(rig, pose, H.UpperArm(Side.Right), new Vec3(0.3, -1, 0.5), 50);
        PhysicsFixtures.Rotate(rig, pose, H.Thigh(Side.Left), new Vec3(1, 0.2, 0.1), -30);
        PhysicsFixtures.Rotate(rig, pose, H.Thigh(Side.Right), new Vec3(1, -0.2, -0.1), -30);
        PhysicsFixtures.Rotate(rig, pose, H.Spine1, Vec3.UnitX, 25);
        var com = mass.CenterOfMass(pose);
        Assert.Equal(0, com.X, 12);
        Assert.True(com.Z > 0.01); // the forward lean moves it forward

        var leftOnly = new Pose();
        PhysicsFixtures.Rotate(rig, leftOnly, H.UpperArm(Side.Right), Vec3.UnitZ, 90); // right arm down: COM shifts left (+X)
        Assert.True(mass.CenterOfMass(leftOnly).X > 0.01);
    }

    [Fact]
    public void RigWithoutMasses_DistributesFallbackMassByLength()
    {
        var rig = MannequinFactory.Create();
        foreach (var b in rig.Bones) b.Mass = 0;
        var mass = MassModel.For(rig, 80);
        Assert.Equal(MassSource.LengthDistributed, mass.Source);
        Assert.Equal(80, mass.TotalMass, 9);
        var thigh = mass.Segments.Single(s => s.Bone == rig.IndexOf(rig.FindBoneByName(H.Thigh(Side.Left))!.Id));
        var calf = mass.Segments.Single(s => s.Bone == rig.IndexOf(rig.FindBoneByName(H.Calf(Side.Left))!.Id));
        Assert.Equal(0.43 / 0.41, thigh.Mass / calf.Mass, 9); // proportional to segment length
        var com = mass.CenterOfMass(new Pose());
        Assert.True(double.IsFinite(com.Y));
        Assert.Equal(0, com.X, 9);
    }
}

public class SupportPolygonTests
{
    static readonly Vec2[] Square = { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };

    [Fact]
    public void ConvexHull_DropsInteriorDuplicateAndCollinearPoints_CounterClockwise()
    {
        var pts = Square.Concat(new Vec2[] { new(0.5, 0.5), new(0.5, 0), new(1, 1), new(0.2, 0.7), new(0, 0.5) });
        var hull = Geometry2D.ConvexHull(pts);
        Assert.Equal(4, hull.Count);
        Assert.Equal(1, Geometry2D.Area(hull), 12); // positive area = counter-clockwise
        Assert.All(Square, p => Assert.Contains(p, hull));
        Assert.Equal(new Vec2(0.5, 0.5), Geometry2D.Centroid(hull));

        Assert.Empty(Geometry2D.ConvexHull(Array.Empty<Vec2>()));
        Assert.Single(Geometry2D.ConvexHull(new Vec2[] { new(1, 2), new(1, 2) }));
        Assert.Equal(2, Geometry2D.ConvexHull(new Vec2[] { new(0, 0), new(1, 1), new(2, 2) }).Count); // collinear → segment
    }

    [Fact]
    public void SignedDistance_InsidePositive_OutsideNegative_DegenerateNonPositive()
    {
        var hull = Geometry2D.ConvexHull(Square);
        Assert.Equal(0.25, Geometry2D.SignedDistance(hull, new Vec2(0.25, 0.5)), 12);
        Assert.Equal(0.5, Geometry2D.SignedDistance(hull, new Vec2(0.5, 0.5)), 12);
        Assert.Equal(-0.5, Geometry2D.SignedDistance(hull, new Vec2(1.5, 0.5)), 12);
        Assert.Equal(-Math.Sqrt(2), Geometry2D.SignedDistance(hull, new Vec2(2, 2)), 12);
        Assert.Equal(0, Geometry2D.SignedDistance(hull, new Vec2(1, 0.3)), 12);

        var segment = Geometry2D.ConvexHull(new Vec2[] { new(0, 0), new(1, 0) });
        Assert.Equal(-0.5, Geometry2D.SignedDistance(segment, new Vec2(0.5, 0.5)), 12);
        Assert.Equal(-5, Geometry2D.SignedDistance(new[] { new Vec2(3, 4) }, new Vec2(0, 0)), 12);
        Assert.True(double.IsNaN(Geometry2D.SignedDistance(Array.Empty<Vec2>(), new Vec2(0, 0))));
    }

    [Fact]
    public void Inset_And_ClosestPoint()
    {
        var hull = Geometry2D.ConvexHull(Square);
        var inset = Geometry2D.Inset(hull, 0.1);
        Assert.Equal(4, inset.Count);
        Assert.Equal(0.64, Geometry2D.Area(inset), 9);
        Assert.Empty(Geometry2D.Inset(hull, 0.6)); // thinner than twice the margin
        var c = Geometry2D.ClosestPoint(inset, new Vec2(2, 0.5));
        Assert.Equal(0.9, c.X, 12);
        Assert.Equal(0.5, c.Z, 12);
        Assert.Equal(new Vec2(0.3, 0.3), Geometry2D.ClosestPoint(hull, new Vec2(0.3, 0.3)));
    }

    [Fact]
    public void RestPose_StandsOnBothFeet_WithAPositiveMargin()
    {
        var rig = MannequinFactory.Create();
        var an = new BalanceAnalyzer(rig);
        Assert.Equal(0, an.Ground);
        var b = an.AnalyzePose(PhysicsFixtures.ArmsDown(rig));
        Assert.Equal(10, b.Contacts.Count);
        Assert.Equal(6, b.Polygon.Count); // heels and balls, plus both toe tips
        Assert.Equal(BalanceState.Balanced, b.State);
        Assert.InRange(b.Margin, 0.04, 0.1);
        // Foot is ~23 cm long and ~10 cm wide; feet 18 cm apart.
        Assert.InRange(b.Polygon.Max(p => p.X) - b.Polygon.Min(p => p.X), 0.25, 0.30);
        Assert.InRange(b.Polygon.Max(p => p.Z) - b.Polygon.Min(p => p.Z), 0.2, 0.26);
        Assert.Equal(new[] { 0, 1 }, b.ContactFeet);
    }

    [Fact]
    public void OneLegStance_UsesOnlyTheStandingFoot()
    {
        var rig = MannequinFactory.Create();
        var an = new BalanceAnalyzer(rig);
        var pose = PhysicsFixtures.ArmsDown(rig);
        PhysicsFixtures.Rotate(rig, pose, H.Thigh(Side.Right), Vec3.UnitX, -40); // right knee up
        PoseSolver.Update(rig, pose);
        var b = an.AnalyzePose(pose);
        Assert.All(b.Contacts, c => Assert.Equal(Side.Left, c.Side));
        Assert.Equal(BalanceState.Unbalanced, b.State); // COM between the feet, outside the left foot
        Assert.True(b.Margin < 0);
    }

    [Fact]
    public void RaisedBody_IsAirborne()
    {
        var rig = MannequinFactory.Create();
        var an = new BalanceAnalyzer(rig);
        var pose = new Pose();
        PhysicsFixtures.Translate(rig, pose, H.Root, new Vec3(0, 0.3, 0));
        var b = an.AnalyzePose(pose);
        Assert.Equal(BalanceState.Airborne, b.State);
        Assert.Empty(b.Polygon);
        Assert.True(double.IsNaN(b.Margin));
    }

    [Fact]
    public void StaticClip_ZmpAndCapturePointEqualProjectedCom()
    {
        var rig = MannequinFactory.Create();
        var clip = PhysicsFixtures.RotationClip(rig, H.Spine1, FrameRate.Fps30, 1, (0, 10), (1, 10));
        var an = new BalanceAnalyzer(rig, new BalanceOptions { Metric = BalanceMetric.Zmp });
        var report = an.AnalyzeClip(clip);
        Assert.Equal(30, report.Frames.Count);
        foreach (var f in report.Frames)
        {
            Assert.True(f.Zmp!.Value.ApproxEquals(new Vec3(f.Com.X, 0, f.Com.Z), 1e-9));
            Assert.True(f.CapturePoint!.Value.ApproxEquals(new Vec3(f.Com.X, 0, f.Com.Z), 1e-9));
            Assert.Equal(f.ComMargin, f.Margin, 9);
        }
        Assert.Empty(report.UnbalancedRanges);
    }

    [Fact]
    public void Zmp_ShiftsOppositeToHorizontalAcceleration()
    {
        // The trunk swings forward and back quickly: the ZMP leads/lags the COM by −h·a/(a_y+g).
        var rig = MannequinFactory.Create();
        var clip = PhysicsFixtures.RotationClip(rig, H.Spine1, FrameRate.Fps30, 1, (0, 0), (0.3, 40), (0.6, 0));
        var an = new BalanceAnalyzer(rig);
        var report = an.AnalyzeClip(clip);
        var f = report.Frames.First(x => Math.Abs(x.ComAcceleration.Z) > 1);
        var h = f.Com.Y;
        var expected = f.Com.Z - h * f.ComAcceleration.Z / (f.ComAcceleration.Y + 9.81);
        Assert.Equal(expected, f.Zmp!.Value.Z, 9);
        Assert.Equal(Math.Sign(f.ComAcceleration.Z), -Math.Sign(f.Zmp.Value.Z - f.Com.Z));
    }
}

public class AutoBalanceTests
{
    [Fact]
    public void LeaningPose_IsUnbalanced_AutoBalanceFixesItWithFeetPlanted()
    {
        var rig = MannequinFactory.Create();
        var an = new BalanceAnalyzer(rig);
        var pose = PhysicsFixtures.Lean(rig);
        var before = an.AnalyzePose(pose);
        Assert.Equal(BalanceState.Unbalanced, before.State);
        Assert.True(before.Margin < -0.03, $"margin {before.Margin}");

        var c = AutoBalance.CorrectPose(an, pose, before);
        Assert.True(c.Changed);
        Assert.Null(c.Note);
        // Minimal: the COM lands on the boundary of the polygon shrunk by the margin (not deeper).
        Assert.Equal(0.02, c.MarginAfter, 4);
        Assert.Equal(0.02, Geometry2D.SignedDistance(before.Polygon, Vec2.Of(an.Mass.CenterOfMass(c.Pose!))), 4);
        // Hips go backwards only, by more than the COM must move (planted legs do not follow).
        var comMove = before.Com.Z - c.ComAfter.Z;
        Assert.True(c.HipShift.Z < -comMove && c.HipShift.Z > -3 * comMove, $"shift {c.HipShift}, com {comMove}");
        Assert.Equal(0, c.HipShift.X, 9);

        // Feet stay planted: foot, toe and every sole point within 1e-4 m.
        var m0 = an.Evaluator.Model(pose);
        var m1 = an.Evaluator.Model(c.Pose!);
        foreach (var foot in an.Feet.Feet)
        {
            Assert.True((m1[foot.FootBone].Translation - m0[foot.FootBone].Translation).Length() < 1e-4);
            Assert.True((m1[foot.ToeBone].Translation - m0[foot.ToeBone].Translation).Length() < 1e-4);
            foreach (var p in foot.Points)
                Assert.True((m1[p.Bone].TransformPoint(p.Local) - m0[p.Bone].TransformPoint(p.Local)).Length() < 1e-4);
        }
        var after = an.AnalyzePose(c.Pose!);
        Assert.Equal(BalanceState.Balanced, after.State);
    }

    [Fact]
    public void BalancedPose_IsLeftAlone_AndUnpinnedFeetArePinnedForTheSolve()
    {
        var rig = MannequinFactory.Create();
        var an = new BalanceAnalyzer(rig);
        var ok = an.AnalyzePose(PhysicsFixtures.ArmsDown(rig));
        var c = AutoBalance.CorrectPose(an, PhysicsFixtures.ArmsDown(rig), ok);
        Assert.False(c.Changed);
        Assert.Equal("balanced", c.Note);

        // Same lean, but the feet are not pinned in the pose: legs are re-solved and reported for keying.
        var pose = PhysicsFixtures.Lean(rig);
        foreach (var state in pose.Controls.Values) state.Pinned = false;
        var b = an.AnalyzePose(pose);
        var fixedPose = AutoBalance.CorrectPose(an, pose, b);
        Assert.True(fixedPose.Changed);
        Assert.Equal(7, fixedPose.ChangedBones.Count); // pelvis + 2 × (thigh, calf, foot)
        Assert.True(fixedPose.FootDrift < 1e-4);
    }

    [Fact]
    public void MaxShift_LimitsTheCorrection()
    {
        var rig = MannequinFactory.Create();
        var an = new BalanceAnalyzer(rig);
        var pose = PhysicsFixtures.Lean(rig);
        var c = AutoBalance.CorrectPose(an, pose, an.AnalyzePose(pose), new AutoBalanceOptions { MaxShift = 0.02 });
        Assert.Equal("max_shift", c.Note);
        Assert.Equal(0.02, Math.Abs(c.HipShift.Z), 9);
        Assert.True(c.MarginAfter > c.MarginBefore && c.MarginAfter < 0);
    }
}

public class SecondaryMotionTests
{
    static readonly SpringSettings Spring = new();

    static (Rig, Clip) HeadTurn(FrameRate fps)
    {
        var rig = MannequinFactory.Create();
        // Head nods 60° in 1/3 s, then holds for 1.67 s.
        return (rig, PhysicsFixtures.RotationClip(rig, H.Neck, fps, 2, (0, 0), (1.0 / 3, 60), (2, 60)));
    }

    static int[] Bones(Rig rig, params string[] names) => names.Select(n => rig.IndexOf(rig.FindBoneByName(n)!.Id)).ToArray();

    [Fact]
    public void Simulation_IsStable_Bounded_LagsThenConvergesToThePrimaryPose()
    {
        var (rig, clip) = HeadTurn(FrameRate.Fps30);
        var head = Bones(rig, H.Head);
        var r = SecondaryMotion.Simulate(rig, clip, head, Spring, 0, clip.DurationFrames - 1);
        Assert.Equal(60, r.Rotations[0].Length);
        Assert.All(r.Rotations[0], q => Assert.True(double.IsFinite(q.W) && q.IsNormalized(1e-9)));
        Assert.InRange(r.MaxLagDegrees, 2, Spring.MaxAngleDegrees + 1e-6);

        // The head's own primary rotation is rest; lagging behind the neck shows as a non-identity local rotation.
        var mid = Quat.AngleBetween(r.Rotations[0][8], Quat.Identity) / PhysicsFixtures.Deg;
        Assert.True(mid > 2, $"lag at frame 8: {mid}°");
        var end = Quat.AngleBetween(r.Rotations[0][^1], Quat.Identity) / PhysicsFixtures.Deg;
        Assert.True(end < 0.5, $"residual {end}°");
        Assert.Equal(Quat.Identity, r.Rotations[0][0]);
    }

    [Fact]
    public void Simulation_IsFrameRateIndependent()
    {
        var (rig30, clip30) = HeadTurn(FrameRate.Fps30);
        var (rig60, clip60) = HeadTurn(FrameRate.Fps60);
        var a = SecondaryMotion.Simulate(rig30, clip30, Bones(rig30, H.Head), Spring, 0, 59);
        var b = SecondaryMotion.Simulate(rig60, clip60, Bones(rig60, H.Head), Spring, 0, 119);
        var worst = 0.0;
        for (var i = 0; i < 60; i++)
            worst = Math.Max(worst, Quat.AngleBetween(a.Rotations[0][i], b.Rotations[0][2 * i]) / PhysicsFixtures.Deg);
        Assert.True(worst < 1.0, $"30 vs 60 fps differ by {worst}°");
    }

    [Fact]
    public void StiffUndampedSpring_StaysFiniteAndClamped()
    {
        var (rig, clip) = HeadTurn(new FrameRate(12));
        var r = SecondaryMotion.Simulate(rig, clip, Bones(rig, H.Spine2, H.Chest, H.Neck, H.Head),
            new SpringSettings { Stiffness = SpringSettings.MaxStiffness, Damping = 0, MaxAngleDegrees = 10 }, 0, clip.DurationFrames - 1);
        foreach (var bone in r.Rotations)
            Assert.All(bone, q => Assert.True(double.IsFinite(q.X + q.Y + q.Z + q.W) && q.IsNormalized(1e-9)));
        Assert.True(r.MaxLagDegrees <= 10 + 1e-6);
    }

    [Fact]
    public void Chain_LagAccumulatesTowardsTheTip()
    {
        var rig = MannequinFactory.Create();
        var clip = PhysicsFixtures.RotationClip(rig, H.Spine1, FrameRate.Fps30, 1, (0, 0), (0.25, 40), (1, 40));
        var bones = Bones(rig, H.Spine2, H.Chest, H.Neck, H.Head);
        var r = SecondaryMotion.Simulate(rig, clip, bones, Spring, 0, 29);
        var ev = new RigEvaluator(rig);
        // Model-space lag of each bone at frame 6 relative to the primary pose.
        var primary = ev.Model(ClipEvaluator.Evaluate(clip, rig, Flicks.FromFrame(6, clip.Fps)));
        var simPose = ClipEvaluator.Evaluate(clip, rig, Flicks.FromFrame(6, clip.Fps));
        for (var b = 0; b < bones.Length; b++)
            simPose.Bones[rig.Bones[bones[b]].Id.ToString()] = simPose.LocalOf(rig.Bones[bones[b]]) with { Rotation = r.Rotations[b][6] };
        var sim = ev.Model(simPose);
        var lags = bones.Select(i => Quat.AngleBetween(primary[i].Rotation, sim[i].Rotation)).ToArray();
        Assert.True(lags[0] > 1e-3);
        Assert.True(lags[^1] >= lags[0] - 1e-9);
    }
}
