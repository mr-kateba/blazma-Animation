using Blazma.AI;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;
using Blazma.Rigging.Import;
using Blazma.Rigging.Mapping;
using Blazma.Tests.Rigging.Import;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.AI;

public class ProceduralAndRetargetTests
{
    static readonly ProceduralMotionProvider Procedural = new();

    static MotionResult Gen(string prompt, double seconds = 2, double fps = 30, ulong seed = 1, MotionConstraints? c = null) =>
        Procedural.GenerateAsync(new MotionRequest(prompt, seconds, fps, seed, c), null, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Model-space joint positions of a SOMA frame.</summary>
    static (Vec3[] Pos, Quat[] Rot) Fk(MotionResult m, int frame)
    {
        var n = m.JointCount;
        var pos = new Vec3[n];
        var rot = new Quat[n];
        for (var j = 0; j < n; j++)
        {
            var p = m.Skeleton.Parents[j];
            var local = m.Rotation(frame, j);
            if (p < 0) { pos[j] = m.RootTranslations[frame]; rot[j] = local; }
            else { rot[j] = rot[p] * local; pos[j] = pos[p] + rot[p].Rotate(m.Skeleton.Rest[j].Translation); }
        }
        return (pos, rot);
    }

    static int J(string name) => SomaSkeleton.IndexOf(name);

    // ------------------------------------------------------------------ SOMA definition

    [Fact]
    public void Soma_Has30Joints_ParentsBeforeChildren_AndPlausibleProportions()
    {
        var d = SomaSkeleton.Definition;
        d.Validate();
        Assert.Equal(30, d.Count);
        var rest = SomaSkeleton.RestPositions();
        Assert.InRange(rest[J("Head")].Y, 1.5, 1.8);
        Assert.True(rest[J("LeftHand")].X > 0.5 && rest[J("RightHand")].X < -0.5); // T-pose, left = +X
        Assert.True(rest[J("LeftToeBase")].Z > rest[J("LeftFoot")].Z);           // toes point +Z
        Assert.InRange(rest[J("LeftToeBase")].Y, -0.01, 0.03);                    // on the ground
        foreach (var (name, std) in SomaSkeleton.ToStandard)
        {
            Assert.True(SomaSkeleton.IndexOf(name) >= 0, name);
            Assert.True(H.IsStandard(std), std);
        }
        foreach (var std in H.Required) Assert.Contains(std, SomaSkeleton.ToStandard.Values);
    }

    // ------------------------------------------------------------------ procedural sanity

    [Fact]
    public void Walk_AlternatesFeet_AdvancesTheRoot_AndStandsOnTheGround()
    {
        var m = Gen("a person walks forward", 3);
        m.Validate();
        Assert.Equal("procedural", m.Provider);
        Assert.Contains(m.Warnings, w => w.Contains("not AI"));
        double minDiff = double.MaxValue, maxDiff = double.MinValue;
        var ankle = SomaSkeleton.RestPositions()[J("LeftFoot")].Y;
        for (var f = 0; f < m.FrameCount; f++)
        {
            var (p, _) = Fk(m, f);
            var d = p[J("LeftFoot")].Z - p[J("RightFoot")].Z;
            minDiff = Math.Min(minDiff, d);
            maxDiff = Math.Max(maxDiff, d);
            // The support foot touches the ground: with heel-to-toe roll that is its heel (ankle at rest height or just above
            // while the toes are up) or its toe base (heel raised), not necessarily the lowest ankle.
            var lowest = new[] { p[J("LeftFoot")].Y - ankle, p[J("RightFoot")].Y - ankle, p[J("LeftToeBase")].Y, p[J("RightToeBase")].Y }.Min();
            Assert.InRange(lowest, -0.01, 0.01);
            Assert.InRange(p[0].Y, 0.85, 1.0);
        }
        Assert.True(maxDiff > 0.25 && minDiff < -0.25, $"feet alternate: {minDiff:0.00}..{maxDiff:0.00}");
        Assert.True(m.RootTranslations[^1].Z - m.RootTranslations[0].Z > 2.5, "root advances +Z (≈1 m/s)");
        for (var f = 1; f < m.FrameCount; f++) Assert.True(m.RootTranslations[f].Z >= m.RootTranslations[f - 1].Z - 1e-9, "never walks backwards");
        Assert.True(Math.Abs(m.RootTranslations[^1].X) < 0.1);
    }

    [Fact]
    public void Procedural_IsDeterministic_AndSeedChangesIt()
    {
        var a = Gen("walk", 1, 30, 5);
        var b = Gen("walk", 1, 30, 5);
        var c = Gen("walk", 1, 30, 6);
        Assert.Equal(a.LocalRotations, b.LocalRotations);
        Assert.Equal(a.RootTranslations, b.RootTranslations);
        Assert.NotEqual(a.RootTranslations, c.RootTranslations);
    }

    [Fact]
    public async Task Procedural_Keywords_ProduceTheirMotions()
    {
        var restHips = SomaSkeleton.RestPositions()[0].Y;
        var run = Gen("run fast", 2);
        Assert.True(run.RootTranslations[^1].Z > 3.5, "runs faster than walking (≈2.3 m/s)");
        var jump = Gen("jump up", 2);
        Assert.True(jump.RootTranslations.Max(p => p.Y) > restHips + 0.2, "airborne");
        Assert.True(jump.RootTranslations.Min(p => p.Y) < restHips - 0.15, "crouches");
        Assert.InRange(jump.RootTranslations[0].Y, restHips - 0.01, restHips + 0.01);
        var squat = Gen("squat", 2);
        Assert.True(squat.RootTranslations.Min(p => p.Y) < restHips - 0.25);
        var wave = Gen("wave hello", 2);
        var (wp, _) = Fk(wave, wave.FrameCount - 1);
        Assert.True(wp[J("RightHand")].Y > wp[J("Head")].Y - 0.1, "right hand raised");
        var idle = Gen("idle", 2);
        Assert.True(idle.RootTranslations.Max(p => Math.Abs(p.Z)) < 0.01);
        Assert.True(Gen("شخص يمشي إلى الأمام", 2).RootTranslations[^1].Z > 2); // Arabic keyword
        var inPlace = Gen("walk", 2, 30, 1, new MotionConstraints { InPlace = true });
        Assert.All(inPlace.RootTranslations, p => Assert.Equal(0, p.Z));
        var e = await Assert.ThrowsAsync<MotionGenerationException>(() => Procedural.GenerateAsync(new MotionRequest("do a backflip", 2, 30, 1), null, CancellationToken.None));
        Assert.Equal("unsupported_prompt", e.Code);
        Assert.Equal("invalid_request", (await Assert.ThrowsAsync<MotionGenerationException>(() =>
            Procedural.GenerateAsync(new MotionRequest("walk", 999, 30, 1), null, CancellationToken.None))).Code);
    }

    // ------------------------------------------------------------------ retarget correctness

    /// <summary>A two-frame SOMA motion: rest at frame 0; at frame 1 the given joint rotations (local, rest-relative).</summary>
    static MotionResult Posed(params (string Joint, Quat Local)[] pose)
    {
        var n = SomaSkeleton.Names.Count;
        var rot = Enumerable.Repeat(Quat.Identity, 2 * n).ToArray();
        foreach (var (joint, q) in pose) rot[n + J(joint)] = q;
        var root = new[] { new Vec3(0, 0.988, 0), new Vec3(0.1, 0.9, 0.5) };
        return new MotionResult("test", SomaSkeleton.Definition, 30, 2, rot, root, Array.Empty<string>());
    }

    static readonly (string Joint, Quat Local)[] KnownPose =
    {
        ("LeftArm", Quat.FromAxisAngle(Vec3.UnitZ, -60 * Math.PI / 180) * Quat.FromAxisAngle(Vec3.UnitY, -20 * Math.PI / 180)),
        ("LeftForeArm", Quat.FromAxisAngle(Vec3.UnitY, -45 * Math.PI / 180)),
        ("RightArm", Quat.FromAxisAngle(Vec3.UnitX, -70 * Math.PI / 180) * Quat.FromAxisAngle(Vec3.UnitZ, 80 * Math.PI / 180)),
        ("LeftLeg", Quat.FromAxisAngle(Vec3.UnitX, -40 * Math.PI / 180)),
        ("LeftShin", Quat.FromAxisAngle(Vec3.UnitX, 50 * Math.PI / 180)),
        ("Spine1", Quat.FromAxisAngle(Vec3.UnitY, 25 * Math.PI / 180)),
    };

    /// <summary>World direction of a bone (from its joint to its child joint), source and target.</summary>
    static void AssertSameDirections(MotionResult m, Rig rig, Clip clip, double toleranceDeg)
    {
        var (sp, _) = Fk(m, 1);
        var pose = ClipEvaluator.Evaluate(clip, rig, Flicks.FromFrame(clip.DurationFrames - 1, clip.Fps));
        var ev = new RigEvaluator(rig);
        var model = ev.Model(pose);
        foreach (var (srcA, srcB, stdA, stdB) in new[]
        {
            ("LeftArm", "LeftForeArm", H.UpperArm(Side.Left), H.LowerArm(Side.Left)),
            ("LeftForeArm", "LeftHand", H.LowerArm(Side.Left), H.Hand(Side.Left)),
            ("RightArm", "RightForeArm", H.UpperArm(Side.Right), H.LowerArm(Side.Right)),
            ("LeftLeg", "LeftShin", H.Thigh(Side.Left), H.Calf(Side.Left)),
            ("LeftShin", "LeftFoot", H.Calf(Side.Left), H.Foot(Side.Left)),
        })
        {
            var ds = sp[J(srcB)] - sp[J(srcA)];
            var a = ev.IndexOf(rig.FindByStandardName(stdA)!.Id);
            var b = ev.IndexOf(rig.FindByStandardName(stdB)!.Id);
            var dt = model[b].Translation - model[a].Translation;
            var angle = Math.Acos(Math.Clamp(Vec3.Dot(ds.Normalized(), dt.Normalized()), -1, 1)) * 180 / Math.PI;
            Assert.True(angle < toleranceDeg, $"{rig.Name}: {stdA}→{stdB} differs by {angle:0.00}°");
        }
    }

    static readonly RetargetOptions Exact = new() { FootCleanup = false, ReduceKeys = false, Name = "Test" };

    [Fact]
    public void Retarget_ToMannequin_KeepsWorldBoneDirections_AndScalesTheRoot()
    {
        var m = Posed(KnownPose);
        var rig = MannequinFactory.Create();
        var r = MotionRetargeter.Retarget(m, rig, Exact);
        Assert.True(r.Clip.IsDraft);
        Assert.Equal(rig.Id, r.Clip.Skeleton);
        Assert.Equal("test", r.Clip.Metadata["source.provider"]);
        AssertSameDirections(m, rig, r.Clip, 0.5);

        // Root: displacement × leg-length ratio (mannequin legs 0.84 m vs SOMA ≈0.85 m).
        var somaLeg = (SomaSkeleton.Offset(J("LeftShin")).Length() + SomaSkeleton.Offset(J("LeftFoot")).Length()
            + SomaSkeleton.Offset(J("RightShin")).Length() + SomaSkeleton.Offset(J("RightFoot")).Length()) / 2;
        Assert.Equal(0.84 / somaLeg, r.Scale, 6);
        var pose = ClipEvaluator.Evaluate(r.Clip, rig, Flicks.FromFrame(1, r.Clip.Fps));
        var pelvis = new RigEvaluator(rig).Model(pose)[rig.IndexOf(rig.FindByStandardName(H.Pelvis)!.Id)].Translation;
        Assert.True(pelvis.ApproxEquals(new Vec3(0.1 * r.Scale, 0.95 + (0.9 - 0.988) * r.Scale, 0.5 * r.Scale), 1e-9), pelvis.ToString());
        // Fingers/face: the mannequin has none; root bone is not driven.
        Assert.Contains("root", r.UnmappedBones);
    }

    [Fact]
    public void Retarget_ToAnAPoseRig_WithRotatedBoneAxes_StillMatchesDirections()
    {
        var rig = MannequinFactory.Create();
        // A-pose: upper arms rest 45° down, and the left forearm's local axes rolled 90° (different bone axes).
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            var ua = rig.FindByStandardName(H.UpperArm(side))!;
            ua.Rest = ua.Rest with { Rotation = Quat.FromAxisAngle(Vec3.UnitZ, (side == Side.Left ? -45 : 45) * Math.PI / 180) };
        }
        var fa = rig.FindByStandardName(H.LowerArm(Side.Left))!;
        fa.Rest = fa.Rest with { Rotation = Quat.FromAxisAngle(Vec3.UnitX, Math.PI / 2) };
        var hand = rig.FindByStandardName(H.Hand(Side.Left))!;
        hand.Rest = hand.Rest with { Translation = Quat.FromAxisAngle(Vec3.UnitX, -Math.PI / 2).Rotate(hand.Rest.Translation) };
        var m = Posed(KnownPose);
        AssertSameDirections(m, rig, MotionRetargeter.Retarget(m, rig, Exact).Clip, 0.5);
    }

    static Rig ImportMapped(string path, params (string Bone, string Std)[] manual)
    {
        var res = RigImporter.Import(RepoPaths.Sample(path), path);
        var report = res.Mapping;
        foreach (var (bone, std) in manual) report = HumanoidMapper.Reassign(res.Rig, report, res.Rig.FindBoneByName(bone)!.Id, std);
        HumanoidMapper.Apply(res.Rig, report);
        return res.Rig;
    }

    public static Rig RiggedFigure() => ImportMapped("models/khronos/RiggedFigure.glb",
        ("torso_joint_1", "pelvis"), ("torso_joint_2", "spine_01"), ("torso_joint_3", "spine_03"), ("neck_joint_2", "head"),
        ("arm_joint_L_1", "upper_arm_l"), ("arm_joint_L_2", "lower_arm_l"), ("arm_joint_L_3", "hand_l"),
        ("arm_joint_R_1", "upper_arm_r"), ("arm_joint_R_2", "lower_arm_r"), ("arm_joint_R_3", "hand_r"),
        ("leg_joint_L_1", "thigh_l"), ("leg_joint_L_2", "calf_l"), ("leg_joint_L_3", "foot_l"),
        ("leg_joint_R_1", "thigh_r"), ("leg_joint_R_2", "calf_r"), ("leg_joint_R_3", "foot_r"));

    public static Rig CesiumMan() => ImportMapped("models/khronos/CesiumMan.glb",
        ("Skeleton_torso_joint_1", "pelvis"), ("Skeleton_torso_joint_2", "spine_01"), ("torso_joint_3", "spine_03"), ("Skeleton_neck_joint_2", "head"),
        ("Skeleton_arm_joint_L__4_", "upper_arm_l"), ("Skeleton_arm_joint_L__3_", "lower_arm_l"), ("Skeleton_arm_joint_L__2_", "hand_l"),
        ("Skeleton_arm_joint_R", "upper_arm_r"), ("Skeleton_arm_joint_R__2_", "lower_arm_r"), ("Skeleton_arm_joint_R__3_", "hand_r"),
        ("leg_joint_L_1", "thigh_l"), ("leg_joint_L_2", "calf_l"), ("leg_joint_L_3", "foot_l"),
        ("leg_joint_R_1", "thigh_r"), ("leg_joint_R_2", "calf_r"), ("leg_joint_R_3", "foot_r"));

    [Theory]
    [InlineData("RiggedFigure")]
    [InlineData("CesiumMan")]
    public void Retarget_ToImportedKhronosRigs_KeepsWorldBoneDirections(string name)
    {
        var rig = name == "RiggedFigure" ? RiggedFigure() : CesiumMan();
        var m = Posed(KnownPose);
        var r = MotionRetargeter.Retarget(m, rig, Exact);
        AssertSameDirections(m, rig, r.Clip, 0.5);
        Assert.True(r.Scale > 0 && double.IsFinite(r.Scale));
        Assert.All(r.Clip.Tracks, t => Assert.All(t.Keys, k => Assert.All(k.Value, v => Assert.True(double.IsFinite(v)))));
    }

    [Fact]
    public void Retarget_UnmappedBones_StayAtRest_WithAWarning()
    {
        var rig = CesiumMan();
        var mappedBefore = rig.HumanoidMap.Count;
        var r = MotionRetargeter.Retarget(Gen("walk", 1), rig, Exact);
        Assert.Contains(r.Warnings, w => w.Contains("not driven"));
        var driven = r.Clip.Tracks.Select(t => t.Target.Id).ToHashSet();
        foreach (var b in rig.Bones.Where(b => rig.StandardNameOf(b.Id) is null)) Assert.DoesNotContain(b.Id, driven);
        Assert.Equal(mappedBefore, rig.HumanoidMap.Count); // the input rig is not modified

        var noPelvis = MannequinFactory.Create();
        noPelvis.HumanoidMap.RemoveAll(x => x.Standard == H.Pelvis);
        Assert.Equal("not_humanoid", Assert.Throws<MotionGenerationException>(() => MotionRetargeter.Retarget(Gen("walk", 1), noPelvis, Exact)).Code);
    }

    [Fact]
    public void Retarget_ResamplesToTheClipFrameRate_AndReducesKeys()
    {
        var m = Gen("walk", 2, 30);
        var rig = MannequinFactory.Create();
        var full = MotionRetargeter.Retarget(m, rig, Exact with { Fps = FrameRate.Fps24 });
        Assert.Equal(new FrameRate(24), full.Clip.Fps);
        Assert.Equal((long)Math.Round(m.DurationSeconds * 24) + 1, full.Clip.DurationFrames);

        var reduced = MotionRetargeter.Retarget(m, rig, Exact with { Fps = FrameRate.Fps24, ReduceKeys = true });
        Assert.True(reduced.KeyCount < full.KeyCount, $"{reduced.KeyCount} < {full.KeyCount}");
        // Within tolerance on every frame.
        for (var f = 0; f < full.Clip.DurationFrames; f++)
        {
            var t = Flicks.FromFrame(f, full.Clip.Fps);
            var a = new RigEvaluator(rig).Model(ClipEvaluator.Evaluate(full.Clip, rig, t));
            var b = new RigEvaluator(rig).Model(ClipEvaluator.Evaluate(reduced.Clip, rig, t));
            for (var i = 0; i < a.Length; i++) Assert.True((a[i].Translation - b[i].Translation).Length() < 0.01, $"frame {f} bone {rig.Bones[i].Name}");
        }
    }

    [Fact]
    public void FootCleanup_PinsContactFeet_AndReducesSliding()
    {
        // Standing motion whose hips drift forward 5 cm/s: the feet slide along with them.
        var idle = Gen("idle", 3);
        var drifting = idle with { RootTranslations = idle.RootTranslations.Select((p, i) => p + new Vec3(0, 0, 0.05 * i / idle.Fps)).ToArray() };
        var rig = MannequinFactory.Create();
        var raw = MotionRetargeter.Retarget(drifting, rig, Exact);
        var clean = MotionRetargeter.Retarget(drifting, rig, Exact with { FootCleanup = true });
        Assert.True(clean.PinnedFrames > 100, $"pinned {clean.PinnedFrames}");
        Assert.True(Sliding(raw.Clip, rig) > 0.2, $"raw sliding {Sliding(raw.Clip, rig):0.000}");
        Assert.True(Sliding(clean.Clip, rig) < Sliding(raw.Clip, rig) * 0.2, $"sliding {Sliding(raw.Clip, rig):0.000} → {Sliding(clean.Clip, rig):0.000}");

        // A planted walk keeps its contacts (pinning finds them and changes little).
        var walk = MotionRetargeter.Retarget(Gen("walk", 3), rig, Exact with { FootCleanup = true });
        Assert.True(walk.PinnedFrames > 20, $"walk pinned {walk.PinnedFrames}");
    }

    /// <summary>Total horizontal travel of the feet while they are within 3 cm of their lowest height.</summary>
    static double Sliding(Clip clip, Rig rig)
    {
        var ev = new RigEvaluator(rig);
        var feet = new[] { H.Foot(Side.Left), H.Foot(Side.Right) }.Select(s => ev.IndexOf(rig.FindByStandardName(s)!.Id)).ToArray();
        var frames = (int)clip.DurationFrames;
        var pos = Enumerable.Range(0, frames).Select(f => ev.Model(ClipEvaluator.Evaluate(clip, rig, Flicks.FromFrame(f, clip.Fps)))).ToArray();
        var total = 0.0;
        foreach (var foot in feet)
        {
            var ground = pos.Min(p => p[foot].Translation.Y);
            for (var f = 1; f < frames; f++)
            {
                var a = pos[f - 1][foot].Translation;
                var b = pos[f][foot].Translation;
                if (a.Y - ground < 0.03 && b.Y - ground < 0.03) total += Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Z - a.Z, 2));
            }
        }
        return total;
    }
}
