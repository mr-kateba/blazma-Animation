using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.AI;
using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.AI;

/// <summary>Phase 7 review fixes on the job side: ownership, staleness, cancel/undo semantics, pruning, bounded waits, path hiding.</summary>
public class MotionJobReviewTests
{
    static readonly CommandOrigin AgentA = new(CommandSource.Mcp, "agent-a");
    static readonly CommandOrigin AgentB = new(CommandSource.Mcp, "agent-b");

    /// <summary>A provider whose generation is scripted by the test (delegating to the procedural one for real motion).</summary>
    sealed class ScriptedProvider(Func<MotionRequest, IProgress<GenerationProgress>?, CancellationToken, Task<MotionResult>> generate) : IMotionGenerationProvider
    {
        public string Id => "scripted";
        public ProviderInfo Describe() => new(Id, "Scripted (test)", "test", true, null, "", new[] { SomaSkeleton.Key }, 30, Array.Empty<string>());
        public Task<ProviderDiagnostics> DiagnoseAsync(bool probe, CancellationToken ct) => throw new NotSupportedException();
        public Task<MotionResult> GenerateAsync(MotionRequest request, IProgress<GenerationProgress>? progress, CancellationToken ct) => generate(request, progress, ct);
    }

    sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    static (CommandRegistry, MotionJobService) NewRegistry(KimodoSettings? kimodo = null, IMotionGenerationProvider? extra = null)
    {
        var providers = new List<IMotionGenerationProvider> { new KimodoProvider(kimodo ?? new KimodoSettings()), new ProceduralMotionProvider() };
        if (extra is not null) providers.Add(extra);
        var jobs = new MotionJobService(providers) { AllowBlockingWait = true };
        return (CommandRegistry.CreateDefault(jobs), jobs);
    }

    static CommandResult Run(CommandRegistry reg, ProjectSession s, string id, object args, CommandOrigin? origin = null)
    {
        try { return s.Execute(reg.Create(id, JsonNode.Parse(JsonSerializer.Serialize(args))!.AsObject()), origin ?? CommandOrigin.Test); }
        catch (CommandException e) { return CommandResult.Fail(e.Code, e.Message); }
    }

    static JsonNode Ok(CommandRegistry reg, ProjectSession s, string id, object args, CommandOrigin? origin = null)
    {
        var r = Run(reg, s, id, args, origin);
        Assert.True(r.Ok, $"{id}: {r.Error?.Code} {r.Error?.Message}");
        return r.Data!;
    }

    static (ProjectSession, string) NewProject()
    {
        var p = Project.CreateNew("AI");
        var ch = MannequinFactory.AddTo(p);
        return (new ProjectSession(p), ch.Id.ToString());
    }

    static string Generate(CommandRegistry reg, ProjectSession s, string ch, CommandOrigin? origin = null, string provider = "procedural", string prompt = "walk") =>
        (string)Ok(reg, s, "motion.generate", new { characterId = ch, prompt, durationSeconds = 1, seed = 1, provider }, origin)["jobId"]!;

    static string WaitDone(MotionJobService jobs, string jobId)
    {
        var snap = jobs.Wait(jobId, TimeSpan.FromSeconds(60));
        Assert.True(snap.IsFinished, "job did not finish");
        return snap.State.ToString().ToLowerInvariant();
    }

    // ---------------------------------------------------------------- #5 ownership

    [Fact]
    public void AgentsSeeAndUseOnlyTheirOwnJobs_TheUserSeesAll()
    {
        var (reg, jobs) = NewRegistry();
        var (s, ch) = NewProject();
        var a = Generate(reg, s, ch, AgentA);
        var b = Generate(reg, s, ch, AgentB);
        Assert.Equal("ready", WaitDone(jobs, a));
        Assert.Equal("ready", WaitDone(jobs, b));

        // B cannot look at, wait for, cancel or apply A's job.
        Assert.Equal("not_owner", Run(reg, s, "motion.job_status", new { jobId = a }, AgentB).Error?.Code);
        Assert.Equal("not_owner", Run(reg, s, "motion.job_status", new { jobId = a, waitSeconds = 1 }, AgentB).Error?.Code);
        Assert.Equal("not_owner", Run(reg, s, "motion.job_cancel", new { jobId = a }, AgentB).Error?.Code);
        Assert.Equal("not_owner", Run(reg, s, "motion.job_apply", new { jobId = a }, AgentB).Error?.Code);
        var listB = Ok(reg, s, "motion.job_status", new { }, AgentB)["jobs"]!.AsArray();
        Assert.Equal(new[] { b }, listB.Select(j => (string)j!["jobId"]!).ToArray());
        Assert.Equal("mcp:agent-b", (string?)listB[0]!["owner"]);

        // A uses its own; the user (UI) sees and may apply every job.
        Assert.Equal("ready", (string?)Ok(reg, s, "motion.job_status", new { jobId = a }, AgentA)["state"]);
        Assert.Equal(2, Ok(reg, s, "motion.job_status", new { }, CommandOrigin.Ui)["jobs"]!.AsArray().Count);
        Ok(reg, s, "motion.job_apply", new { jobId = b }, CommandOrigin.Ui);
        Ok(reg, s, "motion.job_apply", new { jobId = a }, AgentA);

        // The app's own C# view is unrestricted (backward compatible).
        Assert.Equal(2, jobs.List().Count);
        Assert.NotNull(jobs.Get(a));
        Assert.Equal("mcp:agent-a", jobs.Get(a)!.Owner);
    }

    [Fact]
    public void UserJobs_AreNotVisibleToAgents()
    {
        var (reg, jobs) = NewRegistry();
        var (s, ch) = NewProject();
        var mine = Generate(reg, s, ch, CommandOrigin.Ui);
        WaitDone(jobs, mine);
        Assert.Empty(Ok(reg, s, "motion.job_status", new { }, AgentA)["jobs"]!.AsArray());
        Assert.Equal("not_owner", Run(reg, s, "motion.job_apply", new { jobId = mine }, AgentA).Error?.Code);
        Assert.Equal("ui", jobs.Get(mine)!.Owner);
    }

    // ---------------------------------------------------------------- #4 staleness

    [Fact]
    public void Apply_IsStale_WhenTheCharacterWasRemoved()
    {
        var (reg, jobs) = NewRegistry();
        var (s, ch) = NewProject();
        var job = Generate(reg, s, ch);
        WaitDone(jobs, job);
        s.Project.Characters.RemoveAll(c => c.Id.ToString() == ch);
        var r = Run(reg, s, "motion.job_apply", new { jobId = job });
        Assert.Equal("job_stale", r.Error?.Code);
        Assert.Contains("no longer exists", r.Error!.Message);
        Assert.Empty(s.Project.Clips);
    }

    [Fact]
    public void Apply_IsStale_WhenTheRigWasRemapped()
    {
        var (reg, jobs) = NewRegistry();
        var (s, ch) = NewProject();
        var job = Generate(reg, s, ch);
        WaitDone(jobs, job);
        var rig = s.Project.GetRig(s.Project.GetCharacter(ObjectId.Parse(ch)).Rig);
        // Swap the left and right upper arm mapping: same bones, different meaning.
        var left = rig.HumanoidMap.First(m => m.Standard == H.UpperArm(Side.Left));
        var right = rig.HumanoidMap.First(m => m.Standard == H.UpperArm(Side.Right));
        (left.Bone, right.Bone) = (right.Bone, left.Bone);
        var r = Run(reg, s, "motion.job_apply", new { jobId = job });
        Assert.Equal("job_stale", r.Error?.Code);
        Assert.Contains("rig", r.Error!.Message);
        (left.Bone, right.Bone) = (right.Bone, left.Bone); // mapped back: the fingerprint matches again
        Ok(reg, s, "motion.job_apply", new { jobId = job });
    }

    [Fact]
    public void Apply_IsStale_WhenTheCharacterUsesAnotherRig()
    {
        var (reg, jobs) = NewRegistry();
        var (s, ch) = NewProject();
        var job = Generate(reg, s, ch);
        WaitDone(jobs, job);
        var character = s.Project.GetCharacter(ObjectId.Parse(ch));
        var copy = JsonSerializer.Deserialize<Rig>(JsonSerializer.SerializeToUtf8Bytes(s.Project.GetRig(character.Rig), Core.Persistence.ProjectArchive.JsonOptions), Core.Persistence.ProjectArchive.JsonOptions)!;
        copy.Id = ObjectId.New();
        s.Project.Rigs.Add(copy);
        character.Rig = copy.Id;
        Assert.Equal("job_stale", Run(reg, s, "motion.job_apply", new { jobId = job }).Error?.Code);
    }

    [Fact]
    public void Apply_IsStale_WhenTheProjectWasReplaced()
    {
        var (reg, jobs) = NewRegistry();
        var (s, ch) = NewProject();
        var job = Generate(reg, s, ch);
        WaitDone(jobs, job);
        var other = Project.CreateNew("Other");
        MannequinFactory.AddTo(other);
        s.ReplaceProject(other, null);
        var r = Run(reg, s, "motion.job_apply", new { jobId = job });
        Assert.Equal("job_stale", r.Error?.Code);
        Assert.Contains("project", r.Error!.Message);
        Assert.Empty(s.Project.Clips);
    }

    // ---------------------------------------------------------------- #8 / #9 undo and clip ownership

    [Fact]
    public void UndoOfApply_MakesTheJobReadyAgain_RedoAppliesIt_AndAnAppliedJobCannotBeAppliedTwice()
    {
        var (reg, jobs) = NewRegistry();
        var (s, ch) = NewProject();
        var job = Generate(reg, s, ch);
        WaitDone(jobs, job);
        var clipId = (string)Ok(reg, s, "motion.job_apply", new { jobId = job, name = "Mine" })["clipId"]!;
        Assert.Equal(MotionJobState.Applied, jobs.Get(job)!.State);
        var again = Run(reg, s, "motion.job_apply", new { jobId = job });
        Assert.Equal("job_applied", again.Error?.Code);
        Assert.Contains(clipId, again.Error!.Message);

        Assert.True(s.Undo(CommandOrigin.Test).Ok);
        var snap = jobs.Get(job)!;
        Assert.Equal(MotionJobState.Ready, snap.State);
        Assert.Null(snap.ClipId);
        Assert.Empty(s.Project.Clips);

        Assert.True(s.Redo(CommandOrigin.Test).Ok); // the same clip comes back and the job is applied again
        Assert.Equal(MotionJobState.Applied, jobs.Get(job)!.State);
        Assert.Equal(clipId, jobs.Get(job)!.ClipId);
        Assert.Equal("Mine", s.Project.FindClip(ObjectId.Parse(clipId))!.Name);

        Assert.True(s.Undo(CommandOrigin.Test).Ok);
        var second = (string)Ok(reg, s, "motion.job_apply", new { jobId = job })["clipId"]!; // re-applicable after undo
        Assert.NotEqual(clipId, second);
        var clip = s.Project.FindClip(ObjectId.Parse(second))!;
        Assert.True(clip.IsDraft);
        Assert.StartsWith("Procedural: walk", clip.Name); // the job's own name, not the first apply's
        Assert.Equal(job, clip.Metadata["jobId"]);
    }

    [Fact]
    public void FinishedJobs_ArePruned_ByAgeAndCount()
    {
        var (reg, jobs) = NewRegistry();
        var clock = new ManualClock();
        jobs.Clock = clock;
        var (s, ch) = NewProject();
        var ids = new List<string>();
        for (var i = 0; i < MotionJobService.MaxFinishedJobs + 3; i++)
        {
            var id = Generate(reg, s, ch);
            WaitDone(jobs, id);
            ids.Add(id);
            clock.Now += TimeSpan.FromSeconds(10);
        }
        var kept = jobs.List();
        Assert.Equal(MotionJobService.MaxFinishedJobs, kept.Count);
        Assert.DoesNotContain(kept, j => j.JobId == ids[0]); // the oldest went first
        Assert.Contains(kept, j => j.JobId == ids[^1]);

        clock.Now += MotionJobService.FinishedRetention + TimeSpan.FromMinutes(1);
        Assert.Empty(jobs.List());
        Assert.Equal("not_found", Run(reg, s, "motion.job_apply", new { jobId = ids[^1] }).Error?.Code);
    }

    // ---------------------------------------------------------------- #6 / #7 job end states

    [Fact]
    public void CancelDuringRetarget_EndsCancelled_NotReady()
    {
        var procedural = new ProceduralMotionProvider();
        var (reg, jobs) = NewRegistry(extra: new ScriptedProvider((r, p, ct) => procedural.GenerateAsync(r, p, CancellationToken.None)));
        // The "retarget" progress is reported right before retargeting starts: cancel exactly then.
        jobs.JobChanged += snap => { if (snap.Stage == "retarget") jobs.Cancel(snap.JobId); };
        var (s, ch) = NewProject();
        var job = Generate(reg, s, ch, provider: "scripted");
        Assert.Equal("cancelled", WaitDone(jobs, job));
        Assert.Equal("job_cancelled", Run(reg, s, "motion.job_apply", new { jobId = job }).Error?.Code);
    }

    [Fact]
    public void AnyException_EndsTheJob_AsFailed()
    {
        var oom = new ScriptedProvider((_, _, _) => throw new OutOfMemoryException());
        var (reg, jobs) = NewRegistry(extra: oom);
        var (s, ch) = NewProject();
        var job = Generate(reg, s, ch, provider: "scripted");
        Assert.Equal("failed", WaitDone(jobs, job));
        Assert.Equal("out_of_memory", jobs.Get(job)!.ErrorCode);

        var (reg2, jobs2) = NewRegistry(extra: new ScriptedProvider((_, _, _) => Task.FromException<MotionResult>(new IndexOutOfRangeException("boom"))));
        var job2 = Generate(reg2, s, ch, provider: "scripted");
        Assert.Equal("failed", WaitDone(jobs2, job2));
        Assert.Equal("internal_error", jobs2.Get(job2)!.ErrorCode);
    }

    // ---------------------------------------------------------------- #1 bounded waits

    [Fact]
    public async Task JobStatusWait_IsCapped_AndDisposeReleasesWaiters()
    {
        var (reg, jobs) = NewRegistry(FakeRunner.Settings());
        jobs.MaxBlockingWait = TimeSpan.FromSeconds(1);
        var (s, ch) = NewProject();
        var job = Generate(reg, s, ch, provider: "kimodo", prompt: "hang");
        var sw = Stopwatch.StartNew();
        var r = Run(reg, s, "motion.job_status", new { jobId = job, waitSeconds = 600 });
        Assert.True(r.Ok);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), sw.Elapsed.ToString());
        Assert.Contains(r.Warnings, w => w.Code == "wait_capped");
        Assert.Equal("running", (string?)r.Data!["state"]);

        var waiter = Task.Run(() => jobs.Wait(job, TimeSpan.FromMinutes(10), null, CancellationToken.None));
        await Task.Delay(200);
        jobs.Dispose();
        await waiter.WaitAsync(TimeSpan.FromSeconds(15)); // throws TimeoutException when Dispose did not release it
    }

    [Fact]
    public void JobStatusWait_HonoursCancellation()
    {
        var (reg, jobs) = NewRegistry(FakeRunner.Settings());
        var (s, ch) = NewProject();
        var job = Generate(reg, s, ch, provider: "kimodo", prompt: "hang");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = Stopwatch.StartNew();
        var snap = jobs.Wait(job, TimeSpan.FromMinutes(10), null, cts.Token);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(MotionJobState.Running, snap.State);
        jobs.Dispose();
    }

    // ---------------------------------------------------------------- #12 no absolute paths for agents

    [Fact]
    public void Providers_HideAbsolutePaths_FromAgents_ButNotFromTheUser()
    {
        var modelDir = FakeRunner.ModelDir("private-user-folder-");
        var (reg, jobs) = NewRegistry(FakeRunner.Settings(modelDir));
        var (s, _) = NewProject();
        var agent = Ok(reg, s, "motion.providers", new { probe = true }, AgentA);
        var text = agent.ToJsonString();
        var runnerDir = Path.GetDirectoryName(FakeRunner.DotnetHost())!;
        Assert.DoesNotContain(modelDir, text);
        Assert.DoesNotContain(JsonSerializer.Serialize(modelDir).Trim('"'), text);
        Assert.DoesNotContain(runnerDir, text);
        var details = agent["providers"]!.AsArray().First(p => (string?)p!["id"] == "kimodo")!["details"]!;
        Assert.Equal(Path.GetFileName(FakeRunner.DotnetHost()), (string?)details["runnerPath"]);
        Assert.Equal("kimodo-soma-rp-v1.1-f32.gguf", (string?)details["motionModel"]);
        Assert.Equal("true", (string?)details["runnerFound"]);
        Assert.Equal("true", (string?)details["motionModelFound"]);

        var user = Ok(reg, s, "motion.providers", new { }, CommandOrigin.Ui);
        Assert.Contains(JsonSerializer.Serialize(modelDir).Trim('"'), user.ToJsonString());

        // The unavailable reason names the folder too: hidden as well.
        var (reg2, _) = NewRegistry(FakeRunner.Settings(modelDir) with { Model = "soma-seed-v1.1" });
        var missing = Ok(reg2, s, "motion.providers", new { }, AgentA);
        var reason = (string?)missing["providers"]!.AsArray().First(p => (string?)p!["id"] == "kimodo")!["unavailableReason"];
        Assert.Contains("kimodo-soma-seed-v1.1-f32.gguf", reason);
        Assert.DoesNotContain(modelDir, reason);
        jobs.Dispose();
    }
}
