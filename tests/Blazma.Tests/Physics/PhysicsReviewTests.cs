using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Physics;
using Blazma.Rigging;
using Blazma.Rigging.Import;
using Blazma.Rigging.Mapping;
using Blazma.Tests.Rigging.Import;
using Xunit.Abstractions;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.Physics;

/// <summary>Regression tests for the Phase 6 review findings (auto-balance leaks, FK knees, work caps, imported rigs, …).</summary>
public class PhysicsReviewTests(ITestOutputHelper output)
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
    static double D(JsonNode? n) => n!.GetValue<double>();
    static string Json(Clip c) => JsonSerializer.Serialize(c, ProjectArchive.JsonOptions);
    static Clip C(ProjectSession s, string clip) => s.Project.GetClip(ObjectId.Parse(clip));
    static int Bone(Rig rig, string name) => rig.IndexOf(rig.FindBoneByName(name)!.Id);
    static Transform[] Model(Rig rig, Clip clip, long f) => new RigEvaluator(rig).Model(ClipEvaluator.Evaluate(clip, rig, Flicks.FromFrame(f, clip.Fps)));

    /// <summary>The acceptance lean (feet pinned): stand at 0 and 40, crouch + 60° trunk lean on frames mid1…mid2.</summary>
    static (ProjectSession s, string ch, string clip) LeanClip(int duration = 41, int mid1 = 15, int mid2 = 25, int endF = 40)
    {
        var s = new ProjectSession(Project.CreateNew("Physics"));
        var ch = (string)Ok(s, "scene.add_mannequin", new { }).Data!["characterId"]!;
        var clip = (string)Ok(s, "clip.create", new { name = "Lean", durationFrames = duration }).Data!["clipId"]!;
        Ok(s, "pose.set_bone", new { characterId = ch, bone = H.UpperArm(Side.Left), rx = 0, ry = 0, rz = -80 });
        Ok(s, "pose.set_bone", new { characterId = ch, bone = H.UpperArm(Side.Right), rx = 0, ry = 0, rz = 80 });
        Ok(s, "pose.set_pinned", new { characterId = ch, control = "ik_foot_l", pinned = true });
        Ok(s, "pose.set_pinned", new { characterId = ch, control = "ik_foot_r", pinned = true });
        Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = 0 });
        Ok(s, "pose.set_bone", new { characterId = ch, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, tx = 0, ty = 0.83, tz = 0.12 });
        Ok(s, "pose.set_bone", new { characterId = ch, bone = H.Spine1, rx = 60, ry = 0, rz = 0 });
        Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = mid1 });
        Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = mid2 });
        Ok(s, "pose.set_bone", new { characterId = ch, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, tx = 0, ty = 0.95, tz = 0 });
        Ok(s, "pose.set_bone", new { characterId = ch, bone = H.Spine1, rx = 0, ry = 0, rz = 0 });
        Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = endF });
        return (s, ch, clip);
    }

    /// <summary>The same lean with FK legs: IK control tracks removed, so the keyed leg rotations drive the feet.</summary>
    static (ProjectSession s, string ch, string clip) FkLeanClip()
    {
        var (s, ch, clip) = LeanClip();
        C(s, clip).Tracks.RemoveAll(t => t.Target.Kind == TargetKind.Control);
        return (s, ch, clip);
    }

    /// <summary>
    /// Every frame outside <paramref name="corrected"/> has the same model transforms in both clips (1e-9);
    /// returns the largest movement of the supporting feet' sole points on every frame.
    /// </summary>
    double AssertOnlyCorrectedFramesChanged(Rig rig, Clip original, Clip edited, ISet<long> corrected)
    {
        var an = new BalanceAnalyzer(rig);
        double maxFoot = 0;
        for (long f = 0; f < original.DurationFrames; f++)
        {
            var a = Model(rig, original, f);
            var b = Model(rig, edited, f);
            if (!corrected.Contains(f))
                for (var i = 0; i < a.Length; i++)
                {
                    Assert.True((a[i].Translation - b[i].Translation).Length() < 1e-9, $"frame {f}: {rig.Bones[i].Name} moved {(a[i].Translation - b[i].Translation).Length()}");
                    Assert.True(Quat.AngleBetween(a[i].Rotation, b[i].Rotation) < 1e-7, $"frame {f}: {rig.Bones[i].Name} turned");
                }
            foreach (var foot in an.Feet.Feet)
                foreach (var p in foot.Points)
                    maxFoot = Math.Max(maxFoot, (a[p.Bone].TransformPoint(p.Local) - b[p.Bone].TransformPoint(p.Local)).Length());
        }
        output.WriteLine($"max sole point movement: {maxFoot}");
        return maxFoot;
    }

    static HashSet<long> CorrectedFrames(JsonNode fix) => fix["frames"]!.AsArray().Select(n => (long)n!["frame"]!).ToHashSet();

    // ---- finding 1: auto-balance must not leak into frames it did not correct ----------------------------------------------

    [Fact]
    public void AutoBalance_PinnedFeet_FramesOutsideTheCorrection_StayExactlyTheSame()
    {
        var (s, ch, clip) = LeanClip();
        var rig = s.Project.Rigs[0];
        var original = ClipEdit.CloneClip(C(s, clip));
        var fix = Ok(s, "physics.auto_balance", new { clipId = clip, characterId = ch, margin = 0.02 }).Data!;
        var corrected = CorrectedFrames(fix);
        Assert.InRange(corrected.Count, 15, 30);
        var foot = AssertOnlyCorrectedFramesChanged(rig, original, C(s, clip), corrected);
        Assert.True(foot < 1e-4);
        Assert.Equal(0, D(fix["maxUntouchedChange"]), 9);
        // Frames 1–7 and 33–39 were the reviewer's leak (pelvis up to 14.5 mm): now keyed with their original values.
        var keyed = fix["keyedFrames"]!.AsArray().Select(n => (long)n!).ToList();
        Assert.Contains(5L, keyed);
        Assert.Contains(35L, keyed);
        Assert.DoesNotContain(0L, keyed);
        Assert.DoesNotContain(40L, keyed);
    }

    [Fact]
    public void AutoBalance_FkFeet_DoNotSlide_AndTheReportedDriftIsHonest()
    {
        var (s, ch, clip) = FkLeanClip();
        var rig = s.Project.Rigs[0];
        var original = ClipEdit.CloneClip(C(s, clip));
        var fix = Ok(s, "physics.auto_balance", new { clipId = clip, characterId = ch, margin = 0.02 }).Data!;
        var foot = AssertOnlyCorrectedFramesChanged(rig, original, C(s, clip), CorrectedFrames(fix));
        Assert.True(foot < 1e-4, $"feet slid {foot} m");
        Assert.True(D(fix["maxFootDrift"]) >= foot - 1e-6, $"reported {fix["maxFootDrift"]}, measured {foot}");
        Assert.Empty(fix["stillUnbalanced"]!.AsArray());
        Assert.Equal(new long[] { 0, 40 }, fix["checkedFrames"]!.AsArray().Select(n => (long)n!));
    }

    [Theory]
    [InlineData(Interpolation.Step)]
    [InlineData(Interpolation.Linear)]
    public void AutoBalance_KeepsStepAndLinearInterpolation(Interpolation interp)
    {
        var (s, ch, clip) = LeanClip();
        var rig = s.Project.Rigs[0];
        var c = C(s, clip);
        var pelvisId = rig.FindBoneByName(H.Pelvis)!.Id;
        var tr = c.Tracks.Single(t => t.Target.Id == pelvisId && t.Channel == TrackChannels.Translation);
        foreach (var k in tr.Keys) k.Interp = interp;
        tr.Keys.Single(k => k.Time == Flicks.FromFrame(25, c.Fps)).Value = new[] { 0, 0.83, 0.05 }; // a hold that differs from frame 15
        var original = ClipEdit.CloneClip(c);
        var fix = Ok(s, "physics.auto_balance", new { clipId = clip, characterId = ch, frames = "18" }).Data!;
        Assert.Equal(new long[] { 18 }, CorrectedFrames(fix).ToArray());
        AssertOnlyCorrectedFramesChanged(rig, original, C(s, clip), new HashSet<long> { 18 });
        // The new keys split a segment of this interpolation, so they take it (a step hold stays a hold, not a ramp).
        var after = C(s, clip).Tracks.Single(t => t.Target.Id == pelvisId && t.Channel == TrackChannels.Translation);
        foreach (var f in new[] { 16, 18, 19, 24 })
            Assert.Equal(interp, after.Keys.Single(k => k.Time == Flicks.FromFrame(f, c.Fps)).Interp);
        if (interp == Interpolation.Step)
            foreach (var f in new[] { 19, 20, 24 }) // still holding frame 15's value until frame 25
                Assert.Equal(0.12, Model(rig, C(s, clip), f)[Bone(rig, H.Pelvis)].Translation.Z, 9);
    }

    [Fact]
    public void AutoBalance_TrackWithNoKeysNearby_IsHeldOnBothSides()
    {
        // Correcting frame 20 of a pelvis track whose keys are far away (0 and 40): outer frames keep their values.
        var (s, ch, clip) = LeanClip();
        var rig = s.Project.Rigs[0];
        var original = ClipEdit.CloneClip(C(s, clip));
        Ok(s, "physics.auto_balance", new { clipId = clip, characterId = ch, frames = "12" });
        AssertOnlyCorrectedFramesChanged(rig, original, C(s, clip), new HashSet<long> { 12 });
        // Original keys outside the baked span got explicit tangents only where needed; values untouched.
        var pelvisId = rig.FindBoneByName(H.Pelvis)!.Id;
        var tr = C(s, clip).Tracks.Single(t => t.Target.Id == pelvisId && t.Channel == TrackChannels.Translation);
        Assert.Equal(new[] { 0, 0.95, 0 }, tr.Keys[0].Value);
    }

    // ---- finding 2: FK legs keep their knee plane ----------------------------------------------------------------------------

    [Fact]
    public void AutoBalance_FkLegs_KeepTheKneePlane_EvenWithAFarAwayPoleControl()
    {
        var (s, ch, clip) = FkLeanClip();
        var c = C(s, clip);
        var rig = s.Project.Rigs[0];
        foreach (var name in new[] { "pole_knee_l", "pole_knee_r" })
        {
            var pole = rig.Controls.Single(x => x.Name == name);
            var v = pole.Rest.Translation + new Vec3(0.6, 0, -0.4); // sideways and behind: a pole-driven solve would swing the knee out
            c.Tracks.Add(new Track
            {
                Target = new TrackTarget { Kind = TargetKind.Control, Id = pole.Id }, Channel = TrackChannels.Translation,
                Keys = { new Keyframe { Time = Flicks.Zero, Value = new[] { v.X, v.Y, v.Z }, Interp = Interpolation.Linear } },
            });
        }
        var original = ClipEdit.CloneClip(c);
        var fix = Ok(s, "physics.auto_balance", new { clipId = clip, characterId = ch, frames = "20" }).Data!;
        var shift = Math.Abs(D(fix["frames"]![0]!["hipShift"]![1]));
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            int thigh = Bone(rig, H.Thigh(side)), calf = Bone(rig, H.Calf(side)), foot = Bone(rig, H.Foot(side));
            var a = Model(rig, original, 20);
            var b = Model(rig, C(s, clip), 20);
            var kneeMove = (a[calf].Translation - b[calf].Translation).Length();
            output.WriteLine($"{side}: hip shift {shift:0.###} m, knee moved {kneeMove:0.###} m");
            Assert.True(kneeMove < shift, $"knee jumped {kneeMove} m for a {shift} m hip shift"); // was 0.30 m
            // Same bend side (in front of the hip→ankle line), and the knee hinge axis keeps its direction (no thigh twist).
            Vec3 Side(Transform[] m) => m[calf].Translation - (m[thigh].Translation + m[foot].Translation) * 0.5;
            Assert.True(Vec3.Dot(Side(a).Normalized(), Side(b).Normalized()) > 0.95);
            var hingeA = a[thigh].Rotation.Rotate(Vec3.UnitX);
            var hingeB = b[thigh].Rotation.Rotate(Vec3.UnitX);
            Assert.True(Vec3.Dot(hingeA, hingeB) > Math.Cos(3 * Math.PI / 180), $"thigh twisted {Math.Acos(Math.Min(1, Vec3.Dot(hingeA, hingeB))) * 180 / Math.PI}°");
            Assert.True((a[foot].Translation - b[foot].Translation).Length() < 1e-4);
            Assert.True(Quat.AngleBetween(a[foot].Rotation, b[foot].Rotation) < 1e-6);
            // Continuity with the neighbouring frames: the knee path does not spike at the corrected frame.
            var k19 = Model(rig, C(s, clip), 19)[calf].Translation;
            var k21 = Model(rig, C(s, clip), 21)[calf].Translation;
            Assert.True((b[calf].Translation - (k19 + k21) * 0.5).Length() < shift);
        }
    }

    [Fact]
    public void CorrectPose_FkLeg_IgnoresThePoleControl_AndKeepsTheKnee()
    {
        var rig = MannequinFactory.Create();
        var an = new BalanceAnalyzer(rig);
        var pose = PhysicsFixtures.Lean(rig);
        foreach (var state in pose.Controls.Values) state.Pinned = false;
        var pole = rig.Controls.Single(x => x.Name == "pole_knee_l");
        pose.Controls[pole.Id.ToString()] = new ControlState { Transform = pole.Rest with { Translation = pole.Rest.Translation + new Vec3(1, 0, -1) } };
        var c = AutoBalance.CorrectPose(an, pose, an.AnalyzePose(pose));
        Assert.True(c.Changed);
        Assert.True(c.FootDrift < 1e-4);
        var m0 = an.Evaluator.Model(pose);
        var m1 = an.Evaluator.Model(c.Pose!);
        var calf = Bone(rig, H.Calf(Side.Left));
        Assert.True((m0[calf].Translation - m1[calf].Translation).Length() < c.HipShift.Length(), "knee followed the pole");
        Assert.Equal(m0[calf].Translation.X, m1[calf].Translation.X, 3); // stays in its sagittal plane
    }

    // ---- finding 3: work caps ------------------------------------------------------------------------------------------------

    static (ProjectSession s, string clip, string bones) NodClip(int frames, int fps)
    {
        var s = new ProjectSession(Project.CreateNew("Nod"));
        var ch = (string)Ok(s, "scene.add_mannequin", new { }).Data!["characterId"]!;
        var clip = (string)Ok(s, "clip.create", new { name = "Nod", durationFrames = frames, fpsNumerator = fps }).Data!["clipId"]!;
        Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = 0 });
        Ok(s, "pose.set_bone", new { characterId = ch, bone = H.Neck, rx = 50, ry = 0, rz = 0 });
        Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = Math.Min(frames - 1, 20) });
        Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = frames - 1 });
        return (s, clip, string.Join(",", s.Project.Rigs[0].Bones.Select(b => b.Name)));
    }

    [Fact]
    public void SecondaryMotion_TypicalRun_IsFast()
    {
        var (s, clip, all) = NodClip(600, 30);
        Assert.Equal(23, all.Split(',').Length);
        var sw = Stopwatch.StartNew();
        var r = Ok(s, "physics.secondary_motion", new { clipId = clip, bones = all });
        output.WriteLine($"600 frames × 23 bones: {sw.ElapsedMilliseconds} ms, keys {r.Data!["keys"]}");
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms");
        Assert.Equal(600 * 23, (int)r.Data["keys"]!);
    }

    [Theory]
    [InlineData(20000, 30)] // 20k × 23 × 8 sub-steps
    [InlineData(2000, 1)]   // 1 fps: 240 sub-steps per frame
    [InlineData(9000, 120)] // work fits (2 sub-steps), but 9000 × 23 keys > 200,000
    public void SecondaryMotion_TooMuchWork_IsRefusedQuickly(int frames, int fps)
    {
        var (s, clip, all) = NodClip(frames, fps);
        var before = Json(C(s, clip));
        var sw = Stopwatch.StartNew();
        var r = Run(s, "physics.secondary_motion", new { clipId = clip, bones = all });
        Assert.Equal("too_large", Err(r));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms");
        Assert.Equal(before, Json(C(s, clip)));
    }

    [Fact]
    public void AutoBalance_And_Analyze_AreCapped_WithTooLarge()
    {
        var (s, ch, clip) = LeanClip(6001, 1, 5999, 6000);
        Assert.Equal("too_large", Err(Run(s, "physics.auto_balance", new { clipId = clip, characterId = ch })));
        Assert.Equal("too_large", Err(Run(s, "physics.auto_balance", new { clipId = clip, characterId = ch, frames = "0,5500" })));
        var (s2, _, clip2) = LeanClip(20001, 1, 19999, 20000);
        Assert.Equal("too_large", Err(Run(s2, "physics.analyze", new { clipId = clip2, summaryOnly = true })));
        Assert.Equal("too_large", Err(Run(s2, "physics.trajectories", new { clipId = clip2 })));
    }

    [Fact]
    public void AutoBalance_TypicalRun_IsFast()
    {
        var (s, ch, clip) = LeanClip(600, 150, 450, 599);
        var sw = Stopwatch.StartNew();
        var r = Ok(s, "physics.auto_balance", new { clipId = clip, characterId = ch });
        output.WriteLine($"600 frames: {sw.ElapsedMilliseconds} ms, corrected {r.Data!["corrected"]}, keys {r.Data["keys"]}");
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms");
    }

    // ---- findings 4 & 5: imported humanoid rigs --------------------------------------------------------------------------

    static Rig ImportMapped(string path, params (string Bone, string Std)[] manual)
    {
        var res = RigImporter.Import(RepoPaths.Sample(path), path);
        var rig = res.Rig;
        var report = res.Mapping;
        foreach (var (bone, std) in manual) report = HumanoidMapper.Reassign(rig, report, rig.FindBoneByName(bone)!.Id, std);
        HumanoidMapper.Apply(rig, report);
        Assert.DoesNotContain(rig.Bones, b => b.Mass > 0);
        return rig;
    }

    static Rig RiggedFigure() => ImportMapped("models/khronos/RiggedFigure.glb",
        ("torso_joint_1", "pelvis"), ("torso_joint_2", "spine_01"), ("torso_joint_3", "spine_03"), ("neck_joint_2", "head"),
        ("arm_joint_L_1", "upper_arm_l"), ("arm_joint_L_2", "lower_arm_l"), ("arm_joint_L_3", "hand_l"),
        ("arm_joint_R_1", "upper_arm_r"), ("arm_joint_R_2", "lower_arm_r"), ("arm_joint_R_3", "hand_r"),
        ("leg_joint_L_1", "thigh_l"), ("leg_joint_L_2", "calf_l"), ("leg_joint_L_3", "foot_l"),
        ("leg_joint_R_1", "thigh_r"), ("leg_joint_R_2", "calf_r"), ("leg_joint_R_3", "foot_r"));

    static Rig CesiumMan() => ImportMapped("models/khronos/CesiumMan.glb",
        ("Skeleton_torso_joint_1", "pelvis"), ("Skeleton_torso_joint_2", "spine_01"), ("torso_joint_3", "spine_03"), ("Skeleton_neck_joint_2", "head"),
        ("Skeleton_arm_joint_L__4_", "upper_arm_l"), ("Skeleton_arm_joint_L__3_", "lower_arm_l"), ("Skeleton_arm_joint_L__2_", "hand_l"),
        ("Skeleton_arm_joint_R", "upper_arm_r"), ("Skeleton_arm_joint_R__2_", "lower_arm_r"), ("Skeleton_arm_joint_R__3_", "hand_r"),
        ("leg_joint_L_1", "thigh_l"), ("leg_joint_L_2", "calf_l"), ("leg_joint_L_3", "foot_l"),
        ("leg_joint_R_1", "thigh_r"), ("leg_joint_R_2", "calf_r"), ("leg_joint_R_3", "foot_r"));

    public static IEnumerable<object[]> ImportedRigs() => new[] { new object[] { "RiggedFigure" }, new object[] { "CesiumMan" } };

    [Theory]
    [MemberData(nameof(ImportedRigs))]
    public void ImportedHumanoid_WithoutMasses_UsesWinterFractions_AndAPlausibleCom(string name)
    {
        var rig = name == "RiggedFigure" ? RiggedFigure() : CesiumMan();
        var an = new BalanceAnalyzer(rig);
        var mass = an.Mass;
        Assert.Equal(MassSource.HumanoidFractions, mass.Source);
        Assert.Equal(MassModel.DefaultTotalMass, mass.TotalMass, 9);
        double M(string std) => mass.Segments.Where(sg => rig.StandardNameOf(rig.Bones[sg.Bone].Id) == std).Sum(sg => sg.Mass);
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            var arm = M(H.UpperArm(side)) + M(H.LowerArm(side)) + M(H.Hand(side));
            Assert.Equal(75 * (0.028 + 0.016 + 0.006), arm, 6); // 3.75 kg (was 13.9 kg spread by length)
            Assert.Equal(75 * 0.100, M(H.Thigh(side)), 6);
            Assert.Equal(75 * (0.012 + 0.0025), M(H.Foot(side)), 6); // no toe bone: the toe's share goes to the foot
        }
        // Unmapped bones (neck joint, toe helper, …) weigh nothing.
        Assert.All(mass.Segments, sg => Assert.NotNull(rig.StandardNameOf(rig.Bones[sg.Bone].Id)));

        var rest = an.Evaluator.RestModel();
        var headBase = rest[rig.IndexOf(rig.FindByStandardName(H.Head)!.Id)].Translation.Y - an.Ground;
        var height = headBase / 0.88;
        var b = an.AnalyzePose(new Pose());
        var ratio = (b.Com.Y - an.Ground) / height;
        output.WriteLine($"{name}: height ≈ {height:0.###} m, COM at {ratio:0.###} of it, margin {b.Margin:0.####}, state {b.State}");
        Assert.InRange(ratio, 0.5, 0.6);
        Assert.Equal(BalanceState.Balanced, b.State);
        Assert.Equal(10, b.Contacts.Count);
    }

    [Theory]
    [MemberData(nameof(ImportedRigs))]
    public void ImportedFoot_WithoutToeBone_HasItsSoleForwardOfTheAnkle(string name)
    {
        var rig = name == "RiggedFigure" ? RiggedFigure() : CesiumMan();
        var an = new BalanceAnalyzer(rig);
        var rest = an.Evaluator.RestModel();
        Assert.Equal(2, an.Feet.Feet.Count);
        foreach (var foot in an.Feet.Feet)
        {
            Assert.Equal(-1, foot.ToeBone);
            var ankle = rest[foot.FootBone].Translation;
            Vec3 P(string point) { var sp = foot.Points.Single(x => x.Name == point); return rest[sp.Bone].TransformPoint(sp.Local); }
            output.WriteLine($"{foot.Side}: ankle z {ankle.Z:0.###}, heel z {P("heel_in").Z:0.###}, ball z {P("ball_in").Z:0.###}, tip z {P("tip").Z:0.###}");
            Assert.True(P("tip").Z > ankle.Z + 0.08, "the sole must point forward (+Z)");
            Assert.True(P("ball_in").Z > ankle.Z + 0.05);
            Assert.True(P("heel_in").Z < ankle.Z);
            Assert.True(P("tip").Z - P("heel_in").Z is > 0.12 and < 0.3);
        }
    }

    [Fact]
    public void FootWithNoToeAndNoChild_PointsForward()
    {
        var rig = MannequinFactory.Create();
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            var toe = rig.FindBoneByName(H.Toe(side))!;
            rig.Bones.Remove(toe);
            rig.HumanoidMap.RemoveAll(m => m.Bone == toe.Id);
            // Turn the foot's local axes so +Z points backwards: the sole must not follow them.
            var foot = rig.FindBoneByName(H.Foot(side))!;
            foot.Rest = foot.Rest with { Rotation = Quat.FromAxisAngle(Vec3.UnitY, Math.PI) };
        }
        var an = new BalanceAnalyzer(rig);
        var rest = an.Evaluator.RestModel();
        foreach (var foot in an.Feet.Feet)
        {
            var tip = foot.Points.Single(p => p.Name == "tip");
            Assert.True(rest[tip.Bone].TransformPoint(tip.Local).Z > rest[foot.FootBone].Translation.Z + 0.15);
        }
    }

    [Fact]
    public void MannequinWithoutMasses_MatchesItsWinterMasses_AndTheFloorRootAddsNothing()
    {
        var withMasses = MannequinFactory.Create();
        foreach (var b in withMasses.Bones)
        {
            var std = withMasses.StandardNameOf(b.Id)!;
            Assert.Equal(MassModel.WinterFractions.TryGetValue(std, out var f) ? Math.Round(f * MannequinFactory.TotalMassKg, 3) : 0, b.Mass, 9);
        }
        Assert.Equal(1, MassModel.WinterFractions.Values.Sum(), 9);

        var rig = MannequinFactory.Create();
        foreach (var b in rig.Bones) b.Mass = 0;
        var mass = MassModel.For(rig, 75);
        Assert.Equal(MassSource.HumanoidFractions, mass.Source);
        Assert.DoesNotContain(mass.Segments, sg => rig.Bones[sg.Bone].Name == H.Root);
        var reference = MassModel.For(withMasses).CenterOfMass(new Pose());
        Assert.True((mass.CenterOfMass(new Pose()) - reference).Length() < 1e-3);
    }

    [Fact]
    public void RigWithoutPelvisOrMasses_FallsBackToLengthDistribution()
    {
        var rig = MannequinFactory.Create();
        foreach (var b in rig.Bones) b.Mass = 0;
        rig.HumanoidMap.Clear();
        var mass = MassModel.For(rig, 80);
        Assert.Equal(MassSource.LengthDistributed, mass.Source);
        Assert.Equal(80, mass.TotalMass, 9);
    }

    // ---- finding 6 & 10: refuse before editing when a foot could not stay planted, or keys would not play ------------------

    [Fact]
    public void AutoBalance_SupportingFootWithoutLegIk_IsRefused_UnlessFootSlideIsAllowed()
    {
        var (s, ch, clip) = FkLeanClip();
        s.Project.Rigs[0].IkChains.Clear();
        var before = Json(C(s, clip));
        Assert.Equal("no_leg_ik", Err(Run(s, "physics.auto_balance", new { clipId = clip, characterId = ch, frames = "20" })));
        Assert.Equal(before, Json(C(s, clip)));
        var r = Ok(s, "physics.auto_balance", new { clipId = clip, characterId = ch, frames = "20", allowFootSlide = true });
        Assert.Contains(r.Warnings, w => w.Code == "foot_slide");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AutoBalance_LockedOrMutedLegTracks_AreRefused_UnlessFootSlideIsAllowed(bool locked)
    {
        var (s, ch, clip) = FkLeanClip();
        var thigh = s.Project.Rigs[0].FindBoneByName(H.Thigh(Side.Left))!.Id;
        var track = C(s, clip).Tracks.Single(t => t.Target.Id == thigh && t.Channel == TrackChannels.Rotation);
        if (locked) track.Locked = true; else track.Muted = true;
        var before = Json(C(s, clip));
        Assert.Equal(locked ? "track_locked" : "track_muted", Err(Run(s, "physics.auto_balance", new { clipId = clip, characterId = ch, frames = "20" })));
        Assert.Equal(before, Json(C(s, clip)));
        var r = Ok(s, "physics.auto_balance", new { clipId = clip, characterId = ch, frames = "20", allowFootSlide = true });
        Assert.Contains(r.Warnings, w => w.Code == "foot_slide");
    }

    [Fact]
    public void AutoBalance_MutedPelvisTrack_IsRefused()
    {
        var (s, ch, clip) = LeanClip();
        var pelvis = s.Project.Rigs[0].FindBoneByName(H.Pelvis)!.Id;
        C(s, clip).Tracks.Single(t => t.Target.Id == pelvis && t.Channel == TrackChannels.Translation).Muted = true;
        Assert.Equal("track_muted", Err(Run(s, "physics.auto_balance", new { clipId = clip, characterId = ch, frames = "20" })));
    }

    // ---- finding 7: locked bones and gaps in a secondary-motion chain ------------------------------------------------------

    static (ProjectSession s, string clip) SpineSwing()
    {
        var s = new ProjectSession(Project.CreateNew("Swing"));
        var ch = (string)Ok(s, "scene.add_mannequin", new { }).Data!["characterId"]!;
        var clip = (string)Ok(s, "clip.create", new { name = "Swing", durationFrames = 40 }).Data!["clipId"]!;
        var spine = $"{H.Spine1},{H.Spine2},{H.Chest},{H.Neck},{H.Head}";
        Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = 0, bones = spine });
        Ok(s, "pose.set_bone", new { characterId = ch, bone = H.Spine1, rx = 35, ry = 20, rz = 0 });
        Ok(s, "pose.set_bone", new { characterId = ch, bone = H.Chest, rx = 10, ry = 0, rz = 15 });
        Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = 8, bones = spine });
        Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = 39, bones = spine });
        return (s, clip);
    }

    void AssertPlaybackMatchesSimulation(ProjectSession s, string clip, SecondaryResult sim)
    {
        var rig = s.Project.Rigs[0];
        var edited = C(s, clip);
        var worst = 0.0;
        for (var i = 0; i < sim.World[0].Length; i++)
        {
            var m = Model(rig, edited, sim.StartFrame + i);
            for (var b = 0; b < sim.Bones.Count; b++)
                worst = Math.Max(worst, Quat.AngleBetween(m[sim.Bones[b]].Rotation, sim.World[b][i]));
        }
        output.WriteLine($"playback vs simulated world rotation: {worst * 180 / Math.PI:0.##########}°");
        Assert.True(worst < 1e-7, $"{worst * 180 / Math.PI}°");
    }

    [Fact]
    public void SecondaryMotion_LockedBone_IsLeftOut_AndItsChildPlaysBackAsSimulated()
    {
        var (s, clip) = SpineSwing();
        var rig = s.Project.Rigs[0];
        var chest = rig.FindBoneByName(H.Chest)!.Id;
        var chestTrack = C(s, clip).Tracks.Single(t => t.Target.Id == chest && t.Channel == TrackChannels.Rotation);
        chestTrack.Locked = true;
        var chestKeys = JsonSerializer.Serialize(chestTrack.Keys, ProjectArchive.JsonOptions);
        var original = ClipEdit.CloneClip(C(s, clip));
        var r = Ok(s, "physics.secondary_motion", new { clipId = clip, bones = $"{H.Spine2},{H.Chest},{H.Neck},{H.Head}" });
        Assert.Contains(r.Warnings, w => w.Code == "track_locked");
        Assert.Equal(new[] { H.Spine2, H.Neck, H.Head }, r.Data!["bones"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(chestKeys, JsonSerializer.Serialize(C(s, clip).Tracks.Single(t => t.Target.Id == chest && t.Channel == TrackChannels.Rotation).Keys, ProjectArchive.JsonOptions));
        var sim = SecondaryMotion.Simulate(rig, original, new[] { Bone(rig, H.Spine2), Bone(rig, H.Neck), Bone(rig, H.Head) }, new SpringSettings(), 0, 39);
        Assert.True(sim.MaxLagDegrees > 1);
        AssertPlaybackMatchesSimulation(s, clip, sim);
    }

    [Fact]
    public void SecondaryMotion_GapInTheChain_ChildPlaysBackAsSimulated()
    {
        var (s, clip) = SpineSwing();
        var rig = s.Project.Rigs[0];
        var original = ClipEdit.CloneClip(C(s, clip));
        Ok(s, "physics.secondary_motion", new { clipId = clip, bones = $"{H.Spine1},{H.Head}" }); // spine_02, chest, neck skipped
        var sim = SecondaryMotion.Simulate(rig, original, new[] { Bone(rig, H.Spine1), Bone(rig, H.Head) }, new SpringSettings(), 0, 39);
        AssertPlaybackMatchesSimulation(s, clip, sim);
        // The skipped bones keep their primary local rotations.
        foreach (var f in new long[] { 5, 12, 30 })
        {
            var a = ClipEvaluator.Evaluate(original, rig, Flicks.FromFrame(f, original.Fps));
            var b = ClipEvaluator.Evaluate(C(s, clip), rig, Flicks.FromFrame(f, original.Fps));
            Assert.True(Quat.AngleBetween(a.LocalOf(rig.FindBoneByName(H.Chest)!).Rotation, b.LocalOf(rig.FindBoneByName(H.Chest)!).Rotation) < 1e-9);
        }
    }

    [Fact]
    public void SecondaryMotion_MutedBone_IsLeftOut()
    {
        var (s, clip) = SpineSwing();
        var neck = s.Project.Rigs[0].FindBoneByName(H.Neck)!.Id;
        C(s, clip).Tracks.Single(t => t.Target.Id == neck && t.Channel == TrackChannels.Rotation).Muted = true;
        var r = Ok(s, "physics.secondary_motion", new { clipId = clip, preset = "head" });
        Assert.Contains(r.Warnings, w => w.Code == "track_muted");
        Assert.Equal(new[] { H.Head }, r.Data!["bones"]!.AsArray().Select(n => (string)n!));
    }

    [Fact]
    public void SecondaryMotion_SubRange_LeavesFramesOutsideItExactlyTheSame()
    {
        var (s, clip) = SpineSwing();
        var rig = s.Project.Rigs[0];
        var original = ClipEdit.CloneClip(C(s, clip));
        Ok(s, "physics.secondary_motion", new { clipId = clip, preset = "spine", startFrame = 4, endFrame = 20 });
        var outside = Enumerable.Range(0, 40).Where(f => f < 4 || f > 20).Select(f => (long)f);
        foreach (var f in outside)
        {
            var a = Model(rig, original, f);
            var b = Model(rig, C(s, clip), f);
            for (var i = 0; i < a.Length; i++) Assert.True(Quat.AngleBetween(a[i].Rotation, b[i].Rotation) < 1e-7, $"frame {f} bone {rig.Bones[i].Name}");
        }
    }

    // ---- finding 8: non-finite values ------------------------------------------------------------------------------------------

    [Fact]
    public void NonFiniteValues_AreReportedNotKeyed()
    {
        var hull = Geometry2D.ConvexHull(new[] { new Vec2(double.NaN, 0), new Vec2(1, 0), new Vec2(0, 1), new Vec2(0, 0) });
        Assert.Equal(3, hull.Count);
        Assert.All(hull, v => Assert.True(double.IsFinite(v.X) && double.IsFinite(v.Z)));

        var rig = MannequinFactory.Create();
        var an = new BalanceAnalyzer(rig);
        var pose = PhysicsFixtures.Lean(rig);
        PhysicsFixtures.Translate(rig, pose, H.Spine1, new Vec3(double.NaN, 0.1, 0));
        var c = AutoBalance.CorrectPose(an, pose, an.AnalyzePose(pose));
        Assert.False(c.Changed);
        Assert.Equal("non_finite", c.Note);

        // NaN cannot be stored in a clip (the project format rejects it), but finite keys can still overflow: a 1e308 m
        // spine offset on frame 15 makes that frame's COM infinite.
        var (s, ch, clip) = LeanClip();
        var rig2 = s.Project.Rigs[0];
        var spine = rig2.FindBoneByName(H.Spine1)!;
        var cl = C(s, clip);
        var t = spine.Rest.Translation;
        cl.Tracks.Add(new Track
        {
            Target = new TrackTarget { Kind = TargetKind.Bone, Id = spine.Id }, Channel = TrackChannels.Translation,
            Keys =
            {
                new Keyframe { Time = Flicks.FromFrame(14, cl.Fps), Value = new[] { t.X, t.Y, t.Z }, Interp = Interpolation.Step },
                new Keyframe { Time = Flicks.FromFrame(15, cl.Fps), Value = new[] { 1e308, t.Y, t.Z }, Interp = Interpolation.Step },
                new Keyframe { Time = Flicks.FromFrame(16, cl.Fps), Value = new[] { t.X, t.Y, t.Z }, Interp = Interpolation.Step },
            },
        });
        var analysis = Ok(s, "physics.analyze", new { clipId = clip, startFrame = 15, endFrame = 15 }).Data!; // serializes: no NaN/∞ in JSON
        Assert.Null(analysis["frames"]![0]!["com"]);
        Assert.Null(analysis["frames"]![0]!["margin"]);
        var fix = Ok(s, "physics.auto_balance", new { clipId = clip, characterId = ch });
        Assert.Contains(fix.Warnings, w => w.Code == "non_finite");
        Assert.DoesNotContain(15L, CorrectedFrames(fix.Data!));
        Assert.All(C(s, clip).Tracks.SelectMany(k => k.Keys), k => Assert.All(k.Value, v => Assert.True(double.IsFinite(v))));
    }

    // ---- finding 9: requested margin, point/segment supports --------------------------------------------------------------

    [Fact]
    public void StillUnbalanced_ComparesAgainstTheRequestedMargin()
    {
        var (s, ch, clip) = LeanClip();
        var fix = Ok(s, "physics.auto_balance", new { clipId = clip, characterId = ch, frames = "20", margin = 0.05, maxShift = 0.12 }).Data!;
        var after = D(fix["frames"]![0]!["marginAfter"]);
        output.WriteLine($"margin after {after}");
        Assert.InRange(after, 0.0, 0.05 - 1e-3); // inside the polygon, but not by the requested 5 cm
        Assert.Equal(new long[] { 20 }, fix["stillUnbalanced"]!.AsArray().Select(n => (long)n!));
        Assert.Equal("max_shift", (string)fix["frames"]![0]!["note"]!);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PointOrSegmentSupport_IsReported(int points)
    {
        var rig = MannequinFactory.Create();
        var an = new BalanceAnalyzer(rig);
        var pose = PhysicsFixtures.Lean(rig);
        var b = an.AnalyzePose(pose);
        var polygon = points == 1 ? new[] { new Vec2(0.09, 0.15) } : new[] { new Vec2(-0.09, 0.15), new Vec2(0.09, 0.15) };
        var c = AutoBalance.CorrectPose(an, pose, b with { Polygon = polygon });
        Assert.True(c.Changed);
        Assert.Equal(points == 1 ? AutoBalance.PointSupport : AutoBalance.LineSupport, c.Note);
        Assert.True(c.MarginAfter <= 0 && c.MarginAfter > -0.01, $"COM moved onto the support: {c.MarginAfter}");
    }

    // ---- finding 12 and frame rates ----------------------------------------------------------------------------------------

    [Fact]
    public void SecondaryMotion_StartingMidMotion_DoesNotKink()
    {
        var rig = MannequinFactory.Create();
        // The neck turns steadily (linear) for 2 s; start the simulation in the middle of it.
        var clip = PhysicsFixtures.RotationClip(rig, H.Neck, FrameRate.Fps30, 2, (0, 0), (2, 90));
        clip.Tracks[0].Keys[0].Interp = Interpolation.Linear;
        var head = Bone(rig, H.Head);
        var r = SecondaryMotion.Simulate(rig, clip, new[] { head }, new SpringSettings(), 30, 59);
        var step = 90.0 / 60; // degrees per frame of the primary motion
        var lag1 = Quat.AngleBetween(r.Rotations[0][1], Quat.Identity) * 180 / Math.PI;
        output.WriteLine($"lag one frame after the start: {lag1:0.####}° (primary moves {step}° per frame)");
        // Starting at rest (ω = 0) would lag ≈ 1.4° after one frame; started on the motion it only builds up the damping lag.
        Assert.True(lag1 < 0.3 * step, $"{lag1}°");
    }

    [Fact]
    public void SecondaryMotion_At29_97Fps_RunsAndMatches30Fps()
    {
        double Lag(int num, int den)
        {
            var s = new ProjectSession(Project.CreateNew("Nod"));
            var ch = (string)Ok(s, "scene.add_mannequin", new { }).Data!["characterId"]!;
            var dur = (int)Math.Round(2.0 * num / den);
            var clip = (string)Ok(s, "clip.create", new { name = "Nod", durationFrames = dur, fpsNumerator = num, fpsDenominator = den }).Data!["clipId"]!;
            Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = 0, bones = $"{H.Neck},{H.Head}" });
            Ok(s, "pose.set_bone", new { characterId = ch, bone = H.Neck, rx = 50, ry = 0, rz = 0 });
            Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = (int)Math.Round(0.27 * num / den), bones = $"{H.Neck},{H.Head}" });
            Ok(s, "key.set", new { clipId = clip, characterId = ch, frame = dur - 1, bones = $"{H.Neck},{H.Head}" });
            var r = Ok(s, "physics.secondary_motion", new { clipId = clip, preset = "head" }).Data!;
            Assert.Equal(dur * 2, (int)r["keys"]!);
            Assert.Equal(num == 30000 ? 9 : 8, (int)r["substeps"]!);
            Assert.All(C(s, clip).Tracks.SelectMany(t => t.Keys), k => Assert.All(k.Value, v => Assert.True(double.IsFinite(v))));
            return D(r["maxLagDegrees"]);
        }
        var ntsc = Lag(30000, 1001);
        var thirty = Lag(30, 1);
        output.WriteLine($"max lag 29.97 fps {ntsc}°, 30 fps {thirty}°");
        Assert.InRange(ntsc, 1, 30);
        Assert.True(Math.Abs(ntsc - thirty) < 1.0);
    }
}
