using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.AI;
using Blazma.Commands;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Export;
using Blazma.Motion;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.AI;

/// <summary>Generated travel goes on the bone mapped to "root" (not only on the pelvis), so the root follows the character.</summary>
public class RootTravelTests
{
    static (Project, Character, Rig) Mannequin()
    {
        var p = Project.CreateNew("Travel");
        var ch = MannequinFactory.AddTo(p);
        return (p, ch, p.GetRig(ch.Rig));
    }

    static MotionResult Walk() =>
        new ProceduralMotionProvider().GenerateAsync(new MotionRequest("a person walks forward", 2, 30, 1), null, CancellationToken.None).GetAwaiter().GetResult();

    static Rig Copy(Rig rig) =>
        JsonSerializer.Deserialize<Rig>(JsonSerializer.SerializeToUtf8Bytes(rig, Core.Persistence.ProjectArchive.JsonOptions), Core.Persistence.ProjectArchive.JsonOptions)!;

    static Transform[] ModelAt(Clip clip, Rig rig, long frame) =>
        new RigEvaluator(rig).Model(ClipEvaluator.Evaluate(clip, rig, Flicks.FromFrame(frame, clip.Fps)));

    [Fact]
    public void GeneratedWalk_MovesTheRoot_PelvisStaysOverIt_FeetUnchanged()
    {
        var (_, _, rig) = Mannequin();
        var options = new RetargetOptions { Fps = FrameRate.Fps30, ReduceKeys = false };
        var withRoot = MotionRetargeter.Retarget(Walk(), rig, options).Clip;

        // The previous behaviour, for comparison: the same rig without a "root" mapping keeps all travel on the pelvis.
        var noRootRig = Copy(rig);
        noRootRig.HumanoidMap.RemoveAll(m => m.Standard == H.Root);
        var pelvisOnly = MotionRetargeter.Retarget(Walk(), noRootRig, options).Clip;

        var ev = new RigEvaluator(rig);
        var root = ev.IndexOf(rig.FindByStandardName(H.Root)!.Id);
        var pelvis = ev.IndexOf(rig.FindByStandardName(H.Pelvis)!.Id);
        var feet = new[] { H.Foot(Side.Left), H.Foot(Side.Right), H.Hand(Side.Left), H.Head }.Select(s => ev.IndexOf(rig.FindByStandardName(s)!.Id)).ToArray();
        Assert.Contains(withRoot.Tracks, t => t.Target.Id == rig.Bones[root].Id && t.Channel == TrackChannels.Translation);

        var last = withRoot.DurationFrames - 1;
        var maxHorizontalGap = 0.0;
        for (var f = 0L; f <= last; f++)
        {
            var a = ModelAt(withRoot, rig, f);
            var b = ModelAt(pelvisOnly, noRootRig, f);
            foreach (var i in feet) Assert.True((a[i].Translation - b[i].Translation).Length() < 1e-4, $"frame {f} bone {rig.Bones[i].Name} moved");
            Assert.True((a[pelvis].Translation - b[pelvis].Translation).Length() < 1e-4);
            var gap = a[pelvis].Translation - a[root].Translation;
            maxHorizontalGap = Math.Max(maxHorizontalGap, Math.Sqrt(gap.X * gap.X + gap.Z * gap.Z));
            Assert.Equal(0, a[root].Translation.Y, 9); // travel is on the ground
        }
        Assert.True(maxHorizontalGap < 0.15, $"pelvis strays {maxHorizontalGap:0.000} m from the root");
        var start = ModelAt(withRoot, rig, 0)[root].Translation;
        var end = ModelAt(withRoot, rig, last)[root].Translation;
        Assert.True(end.Z - start.Z > 1.0, $"root advanced only {end.Z - start.Z:0.000} m in +Z");
    }

    [Fact]
    public void GeneratedWalk_UnpinnedIkControls_AndPoles_FollowTheCharacter()
    {
        var (_, _, rig) = Mannequin();
        var clip = MotionRetargeter.Retarget(Walk(), rig, new RetargetOptions()).Clip;
        var last = clip.DurationFrames - 1;
        var pose = ClipEvaluator.Evaluate(clip, rig, Flicks.FromFrame(last, clip.Fps));
        var model = new RigEvaluator(rig).Model(pose);
        var rootZ = model[new RigEvaluator(rig).IndexOf(rig.FindByStandardName(H.Root)!.Id)].Translation.Z;
        foreach (var control in rig.Controls)
        {
            var at = PoseSolver.ControlModel(rig, pose, control, model).Translation;
            Assert.True(Math.Abs(at.Z - rootZ) < 0.8, $"{control.Name} ({control.Kind}) stayed behind at z={at.Z:0.00}, root z={rootZ:0.00}");
        }
    }

    [Fact]
    public void GeneratedWalk_ExportsInPlace()
    {
        var jobs = new MotionJobService(new IMotionGenerationProvider[] { new ProceduralMotionProvider() }) { AllowBlockingWait = true };
        var reg = CommandRegistry.CreateDefault(jobs);
        var (p, ch, rig) = Mannequin();
        var s = new ProjectSession(p);
        CommandResult Exec(string id, object args)
        {
            var r = s.Execute(reg.Create(id, JsonNode.Parse(JsonSerializer.Serialize(args))!.AsObject()), CommandOrigin.Test);
            Assert.True(r.Ok, $"{id}: {r.Error?.Code} {r.Error?.Message}");
            return r;
        }
        var job = (string)Exec("motion.generate", new { characterId = ch.Id.ToString(), prompt = "walk", durationSeconds = 2, seed = 1, provider = "procedural" }).Data!["jobId"]!;
        Assert.Equal(MotionJobState.Ready, jobs.Wait(job, TimeSpan.FromSeconds(60)).State);
        var clipId = (string)Exec("motion.job_apply", new { jobId = job }).Data!["clipId"]!;
        var clip = p.FindClip(Core.Ids.ObjectId.Parse(clipId))!;

        var baked = AnimationExporter.Export(p, clip, rig, new ExportOptions { RootMotion = RootMotionMode.InPlace }).Baked;
        var ev = new RigEvaluator(rig);
        var root = ev.IndexOf(rig.FindByStandardName(H.Root)!.Id);
        var pelvis = ev.IndexOf(rig.FindByStandardName(H.Pelvis)!.Id);
        var r0 = ev.Model(baked.Locals[0])[root].Translation;
        double maxRoot = 0, maxPelvis = 0;
        var p0 = ev.Model(baked.Locals[0])[pelvis].Translation;
        foreach (var locals in baked.Locals)
        {
            var m = ev.Model(locals);
            var dr = m[root].Translation - r0;
            var dp = m[pelvis].Translation - p0;
            maxRoot = Math.Max(maxRoot, Math.Sqrt(dr.X * dr.X + dr.Z * dr.Z));
            maxPelvis = Math.Max(maxPelvis, Math.Sqrt(dp.X * dp.X + dp.Z * dp.Z));
        }
        Assert.True(maxRoot < 1e-6, $"root still travels {maxRoot} m");
        Assert.True(maxPelvis < 0.3, $"the body still travels {maxPelvis:0.000} m in place"); // only the sway is left
        Assert.True(baked.RootMotion.Total.Z > 1.0, "the removed travel is reported");

        var dir = Path.Combine(Path.GetTempPath(), "blazma-travel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var export = Exec("animation.export", new { clipId, characterId = ch.Id.ToString(), path = Path.Combine(dir, "walk.glb"), rootMotion = "in_place" }).Data!;
            Assert.True((bool)export["validation"]!["ok"]!, export.ToJsonString());
            Assert.Equal("in_place", (string?)export["rootMotion"]!["mode"]);
        }
        finally { Directory.Delete(dir, true); }
        jobs.Dispose();
    }
}
