using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.AI;
using Blazma.Commands;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.AI;

/// <summary>Multi-step prompts (D45): one job per step, assembled into one draft clip; the procedural actions the planner needs.</summary>
public class PlannedGenerationTests
{
    static CommandRegistry NewRegistry() =>
        CommandRegistry.CreateDefault(new MotionJobService(new IMotionGenerationProvider[] { new KimodoProvider(new KimodoSettings()), new ProceduralMotionProvider() })
        {
            AllowBlockingWait = true,
        });

    static CommandResult Run(CommandRegistry reg, ProjectSession s, string id, object args)
    {
        try { return s.Execute(reg.Create(id, JsonNode.Parse(JsonSerializer.Serialize(args))!.AsObject()), CommandOrigin.Test); }
        catch (CommandException e) { return CommandResult.Fail(e.Code, e.Message); }
    }

    static CommandResult Ok(CommandRegistry reg, ProjectSession s, string id, object args)
    {
        var r = Run(reg, s, id, args);
        Assert.True(r.Ok, $"{id}: {r.Error?.Code} {r.Error?.Message}");
        return r;
    }

    static JsonNode Wait(CommandRegistry reg, ProjectSession s, string jobId)
    {
        for (var i = 0; i < 10; i++)
        {
            var st = Ok(reg, s, "motion.job_status", new { jobId, waitSeconds = 25 }).Data!;
            if ((string?)st["state"] != "running") return st;
        }
        throw new TimeoutException();
    }

    [Fact]
    public void UsersSentence_Generates_LookBack_Run_Die_AsOneClip_InOrder()
    {
        var reg = NewRegistry();
        var p = Project.CreateNew("Plan");
        var ch = MannequinFactory.AddTo(p);
        var s = new ProjectSession(p);
        var start = Ok(reg, s, "motion.generate", new { characterId = ch.Id.ToString(), prompt = "في واحد وراك اجري وبعدها موت", durationSeconds = 6, provider = "procedural", seed = 3 }).Data!;
        Assert.Equal(new[] { "look_back", "run", "die" }, start["plan"]!["segments"]!.AsArray().Select(x => (string?)x!["action"]));
        var st = Wait(reg, s, (string)start["jobId"]!);
        Assert.True((string?)st["state"] == "ready", st.ToJsonString());
        var segs = st["segments"]!.AsArray();
        Assert.Equal(3, segs.Count);
        Assert.All(segs, x => { Assert.Equal("done", (string?)x!["state"]); Assert.Equal("procedural", (string?)x["provider"]); });
        Assert.Equal(new[] { "في واحد وراك", "اجري", "موت" }, segs.Select(x => (string?)x!["text"]));
        Assert.Equal(new[] { "look back", "run", "die" }, segs.Select(x => (string?)x!["caption"]));

        var clipId = (string)Ok(reg, s, "motion.job_apply", new { jobId = (string)start["jobId"]! }).Data!["clipId"]!;
        var clip = p.Clips.Single(c => c.Id.ToString() == clipId);
        Assert.True(clip.IsDraft);
        Assert.Equal("3", clip.Metadata["segments"]);
        Assert.Equal(new[] { "1. في واحد وراك", "2. اجري", "3. موت" }, clip.Markers.Select(m => m.Name));
        Assert.InRange(clip.Duration.ToSeconds(), 5.8, 6.2);

        var rig = p.FindRig(ch.Rig)!;
        var ev = new RigEvaluator(rig);
        int B(string std) => ev.IndexOf(rig.FindByStandardName(std)!.Id);
        Vec3 At(double seconds, string std) => ev.Model(ev.Locals(ClipEvaluator.Evaluate(clip, rig, Flicks.FromSeconds(Math.Min(seconds, clip.Duration.ToSeconds())))))[B(std)].Translation;
        var runAt = clip.Markers[1].Time.ToSeconds();
        var dieAt = clip.Markers[2].Time.ToSeconds();
        Assert.True(runAt > 0.5 && dieAt > runAt + 2, $"markers {runAt} {dieAt}");
        var restPelvis = ev.Model(ev.Locals(new Pose()))[B(H.Pelvis)].Translation;
        // The look back stays on the spot; the run travels forward; the fall happens after the run, where the run ended.
        var p0 = At(0, H.Pelvis);
        var pRun = At(runAt, H.Pelvis);
        var pDie = At(dieAt + 0.3, H.Pelvis);
        var pEnd = At(clip.Duration.ToSeconds(), H.Pelvis);
        Assert.True(Flat(pRun - p0) < 0.3, $"look back travelled {Flat(pRun - p0)}");
        Assert.True(pDie.Z - pRun.Z > 3, $"run travelled {pDie.Z - pRun.Z} m forward");
        Assert.True(Flat(pEnd - pDie) < 1.0, $"the fall travelled {Flat(pEnd - pDie)} m");
        // Ends lying on the ground.
        Assert.True(pEnd.Y < 0.35 * restPelvis.Y, $"pelvis at {pEnd.Y} (rest {restPelvis.Y})");
        Assert.True(At(clip.Duration.ToSeconds(), H.Head).Y < 0.4, "head on the ground");
        // While running the pelvis is up.
        Assert.True(At((runAt + dieAt) / 2, H.Pelvis).Y > 0.8 * restPelvis.Y);
    }

    static double Flat(Vec3 v) => Math.Sqrt(v.X * v.X + v.Z * v.Z);

    [Fact]
    public void PlanPrompt_And_ExplicitSegments_ReturnThePlanUsed()
    {
        var reg = NewRegistry();
        var p = Project.CreateNew("Plan");
        var ch = MannequinFactory.AddTo(p);
        var s = new ProjectSession(p);
        var plan = Ok(reg, s, "motion.plan_prompt", new { prompt = "someone is behind you, run, and then die", durationSeconds = 5 }).Data!;
        Assert.Equal(3, (int)plan["segmentCount"]!);
        Assert.Equal(5, (double)plan["totalSeconds"]!, 2);
        Assert.Equal("a person runs forward", (string?)plan["segments"]![1]!["caption"]);
        Assert.Equal("invalid_args", Run(reg, s, "motion.plan_prompt", new { prompt = "walk", durationSeconds = 99 }).Error?.Code);

        var start = Ok(reg, s, "motion.generate", new
        {
            characterId = ch.Id.ToString(), prompt = "walk then wave", durationSeconds = 3.5, provider = "procedural", seed = 1,
            segments = new object[] { new { text = "walk", durationSeconds = 1.5 }, new { text = "يلوح" } },
        }).Data!;
        var segs = start["plan"]!["segments"]!.AsArray();
        Assert.Equal(new[] { 1.5, 2.0 }, segs.Select(x => (double)x!["durationSeconds"]!));
        Assert.Equal("a person waves with the right hand", (string?)segs[1]!["caption"]);
        var st = Wait(reg, s, (string)start["jobId"]!);
        Assert.Equal("ready", (string?)st["state"]);
        Assert.Equal(new[] { "walk", "wave" }, st["segments"]!.AsArray().Select(x => (string?)x!["caption"]));
        Assert.InRange((int)st["frames"]!, 100, 110);

        Assert.Equal("invalid_args", Run(reg, s, "motion.generate", new { characterId = ch.Id.ToString(), prompt = "x", durationSeconds = 2, provider = "procedural", segments = Array.Empty<object>() }).Error?.Code);
        Assert.Equal("invalid_args", Run(reg, s, "motion.generate", new { characterId = ch.Id.ToString(), prompt = "x", durationSeconds = 2, provider = "procedural", segments = new object[] { new { text = "walk", durationSeconds = 99 } } }).Error?.Code);
        Assert.Equal("invalid_args", Run(reg, s, "motion.generate", new { characterId = ch.Id.ToString(), prompt = "x", durationSeconds = 2, provider = "procedural", segments = new object[] { new { text = "walk", speed = 2 } } }).Error?.Code);

        // A step the procedural generator cannot make fails the job and names the step.
        var bad = Ok(reg, s, "motion.generate", new { characterId = ch.Id.ToString(), prompt = "walk then do a backflip", durationSeconds = 3, provider = "procedural" }).Data!;
        var failed = Wait(reg, s, (string)bad["jobId"]!);
        Assert.Equal("unsupported_prompt", (string?)failed["errorCode"]);
        Assert.Contains("Step 2", (string?)failed["message"]);
    }

    /// <summary>Returns a procedural walk for the first call, then blocks until cancelled.</summary>
    sealed class BlockingProvider : IMotionGenerationProvider
    {
        public int Calls;
        public readonly SemaphoreSlim Entered = new(0);
        readonly ProceduralMotionProvider _inner = new();
        public string Id => "blocking";
        public ProviderInfo Describe() => _inner.Describe() with { Id = Id };
        public Task<ProviderDiagnostics> DiagnoseAsync(bool probe, CancellationToken ct) => _inner.DiagnoseAsync(probe, ct);

        public async Task<MotionResult> GenerateAsync(MotionRequest request, IProgress<GenerationProgress>? progress, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Calls) == 1) return await _inner.GenerateAsync(request with { Prompt = "walk" }, progress, ct);
            Entered.Release();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public void Cancel_DuringASecondStep_EndsCancelled_WithoutResult()
    {
        var provider = new BlockingProvider();
        using var jobs = new MotionJobService(new IMotionGenerationProvider[] { provider });
        var p = Project.CreateNew("Plan");
        var ch = MannequinFactory.AddTo(p);
        var rig = p.FindRig(ch.Rig)!;
        var parts = new[] { new MotionJobPart("walk", "walk", 1, "walk"), new MotionJobPart("run", "run", 1, "run"), new MotionJobPart("die", "die", 1, "die") };
        var job = jobs.StartPlan(provider, new MotionRequest("walk run die", 3, 30, 1), parts, rig, ch.Id, new RetargetOptions { Name = "Plan" });
        Assert.True(provider.Entered.Wait(TimeSpan.FromSeconds(20)));
        var running = jobs.Get(job.JobId)!;
        Assert.Equal(MotionJobState.Running, running.State);
        Assert.Equal(1, running.CurrentSegment);
        Assert.Equal(new[] { "done", "generating", "pending" }, running.Segments.Select(x => x.State));
        Assert.True(jobs.Cancel(job.JobId));
        var done = jobs.Wait(job.JobId, TimeSpan.FromSeconds(20));
        Assert.Equal(MotionJobState.Cancelled, done.State);
        Assert.Null(done.ClipId);
        Assert.Equal(2, provider.Calls);
        Assert.Equal("pending", done.Segments[2].State);
    }

    // ------------------------------------------------------------------ procedural actions

    static readonly ProceduralMotionProvider Procedural = new();

    static MotionResult Gen(string prompt, double seconds, ulong seed = 1) =>
        Procedural.GenerateAsync(new MotionRequest(prompt, seconds, 30, seed), null, CancellationToken.None).GetAwaiter().GetResult();

    static (Vec3[] Pos, Quat[] Rot) Fk(MotionResult m, int frame)
    {
        var n = m.JointCount;
        var pos = new Vec3[n];
        var rot = new Quat[n];
        for (var j = 0; j < n; j++)
        {
            var par = m.Skeleton.Parents[j];
            var local = m.Rotation(frame, j);
            if (par < 0) { pos[j] = m.RootTranslations[frame]; rot[j] = local; }
            else { rot[j] = rot[par] * local; pos[j] = pos[par] + rot[par].Rotate(m.Skeleton.Rest[j].Translation); }
        }
        return (pos, rot);
    }

    static int J(string name) => SomaSkeleton.IndexOf(name);

    /// <summary>Largest per-frame horizontal move of an ankle that stays on the ground (both frames), over frames [from, to).</summary>
    static double MaxPlantedSlide(MotionResult m, int from, int to)
    {
        var ground = SomaSkeleton.RestPositions()[J("LeftFoot")].Y + 0.008;
        var worst = 0.0;
        for (var f = Math.Max(1, from); f < to; f++)
        {
            var (a, _) = Fk(m, f - 1);
            var (b, _) = Fk(m, f);
            foreach (var foot in new[] { J("LeftFoot"), J("RightFoot") })
                if (a[foot].Y < ground && b[foot].Y < ground) worst = Math.Max(worst, Flat(b[foot] - a[foot]));
        }
        return worst;
    }

    [Theory]
    [InlineData("look back", "lookback")]
    [InlineData("a person is startled and looks back over their shoulder", "lookback")]
    [InlineData("turn around", "turnaround")]
    [InlineData("a person turns around", "turnaround")]
    [InlineData("fall", "fall")]
    [InlineData("a person falls to the ground and dies", "die")]
    [InlineData("stop", "stop")]
    [InlineData("punch", "attack")]
    [InlineData("يلتفت", "lookback")]
    [InlineData("يموت", "die")]
    public void Procedural_Recognises_TheNewActions(string prompt, string kind) =>
        Assert.Equal(kind, Gen(prompt, 1.5).Metadata["procedural.kind"]);

    [Theory]
    [InlineData("look back", 1.2)]
    [InlineData("turn around", 1.2)]
    [InlineData("stop", 1.5)]
    [InlineData("idle", 1.5)]
    public void Procedural_NewActions_KeepPlantedFeetStill(string prompt, double seconds)
    {
        var m = Gen(prompt, seconds);
        Assert.True(MaxPlantedSlide(m, 0, m.FrameCount) < 0.004, $"{prompt}: slide {MaxPlantedSlide(m, 0, m.FrameCount)}");
        // Never below the ground.
        for (var f = 0; f < m.FrameCount; f++) Assert.All(Fk(m, f).Pos, p => Assert.True(p.Y > -0.02));
    }

    [Fact]
    public void TurnAround_EndsFacingBack_LookBack_LooksBackThenForward()
    {
        var turn = Gen("turn around", 1.5);
        var (_, r) = Fk(turn, turn.FrameCount - 1);
        Assert.True(r[J("Hips")].Rotate(Vec3.UnitZ).Z < -0.95, "faces -Z after the turn");
        Assert.True(Flat(turn.RootTranslations[^1] with { Y = 0 }) < 0.05, "turns on the spot");

        var look = Gen("look back", 1.5);
        var (_, mid) = Fk(look, look.FrameCount / 2);
        var (_, end) = Fk(look, look.FrameCount - 1);
        Assert.True(mid[J("Head")].Rotate(Vec3.UnitZ).Z < -0.5, $"head looks back: {mid[J("Head")].Rotate(Vec3.UnitZ)}");
        Assert.True(end[J("Head")].Rotate(Vec3.UnitZ).Z > 0.9, "faces forward again");
        Assert.True(end[J("Hips")].Rotate(Vec3.UnitZ).Z > 0.95);
    }

    [Theory]
    [InlineData("fall")]
    [InlineData("die")]
    public void Fall_KeepsFeetPlanted_WhileSinking_AndEndsLyingOnTheGround(string prompt)
    {
        var m = Gen(prompt, 2.5);
        Assert.True(MaxPlantedSlide(m, 0, (int)(m.FrameCount * 0.45)) < 0.004, $"slide {MaxPlantedSlide(m, 0, (int)(m.FrameCount * 0.45))}");
        var (pos, rot) = Fk(m, m.FrameCount - 1);
        Assert.True(pos[J("Hips")].Y < 0.2, $"hips {pos[J("Hips")].Y}");
        Assert.True(pos[J("Head")].Y < 0.35, $"head {pos[J("Head")].Y}");
        Assert.True(rot[J("Hips")].Rotate(Vec3.UnitY).Y < 0.2, "the body lies flat");
        for (var f = 0; f < m.FrameCount; f++) Assert.All(Fk(m, f).Pos, p => Assert.True(p.Y > -0.03, $"{prompt} frame {f}: {p.Y}"));
        // Still at the end: the last 10 % does not move.
        var (before, _) = Fk(m, (int)(m.FrameCount * 0.9));
        Assert.True(Enumerable.Range(0, pos.Length).Max(j => (pos[j] - before[j]).Length()) < 0.01);
    }
}
