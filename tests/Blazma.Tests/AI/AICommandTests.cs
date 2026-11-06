using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.AI;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Rigging;
using Blazma.Tests.Agent;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.AI;

public class AICommandTests
{
    /// <summary>Registry whose Kimodo provider is not installed (like a fresh machine) or points at the fake runner.</summary>
    static CommandRegistry NewRegistry(KimodoSettings? kimodo = null)
    {
        var jobs = new MotionJobService(new IMotionGenerationProvider[] { new KimodoProvider(kimodo ?? new KimodoSettings()), new ProceduralMotionProvider() })
        {
            AllowBlockingWait = true,
        };
        return CommandRegistry.CreateDefault(jobs);
    }

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

    static (ProjectSession, string) NewProject()
    {
        var p = Project.CreateNew("AI");
        var ch = MannequinFactory.AddTo(p);
        return (new ProjectSession(p), ch.Id.ToString());
    }

    static JsonNode Wait(CommandRegistry reg, ProjectSession s, string jobId)
    {
        var st = Ok(reg, s, "motion.job_status", new { jobId, waitSeconds = 60 }).Data!;
        Assert.NotEqual("running", (string?)st["state"]);
        return st;
    }

    [Fact]
    public void AiHardware_And_AiModels_AreReadOnly_And_Hide_Paths_From_Agents()
    {
        var reg = NewRegistry(new KimodoSettings { ModelDirectory = Path.Combine(Path.GetTempPath(), "blazma-secret-user", "models") });
        var (s, _) = NewProject();
        var hw = Ok(reg, s, "ai.hardware", new { }).Data!;
        Assert.NotNull(hw["recommendation"]?["tier"]);
        Assert.True((int)hw["logicalCpus"]! > 0);

        var m = Ok(reg, s, "ai.models", new { }).Data!;
        var tiers = m["tiers"]!.AsArray();
        Assert.Equal(new[] { "light", "standard", "reference" }, tiers.Select(t => (string)t!["id"]!));
        Assert.Single(tiers, t => (bool)t!["recommended"]!);
        Assert.All(tiers, t => Assert.False((bool)t!["installed"]!));
        Assert.False((bool)m["downloadAvailableToAgents"]!);
        Assert.Null(m["modelsDirectory"]); // the test origin counts as an agent
        Assert.True((bool)m["modelsDirectoryConfigured"]!);
        Assert.DoesNotContain("blazma-secret-user", m.ToJsonString());
        Assert.Equal(CommandEffect.ReadOnly, reg.Create("ai.models", new JsonObject()).Effect);
        Assert.DoesNotContain(reg.All.Select(c => c.Id), id => id.Contains("download", StringComparison.Ordinal));
    }

    [Fact]
    public void Commands_AreRegistered_WithTheRightEffects()
    {
        var reg = NewRegistry();
        foreach (var (id, effect) in new[]
        {
            ("motion.providers", CommandEffect.ReadOnly), ("motion.generate", CommandEffect.ReadOnly), ("motion.job_status", CommandEffect.ReadOnly),
            ("motion.job_cancel", CommandEffect.ReadOnly), ("motion.job_apply", CommandEffect.Mutating), ("clip.accept_draft", CommandEffect.Mutating),
        })
        {
            Assert.True(reg.TryGet(id, out var d), id);
            Assert.Equal(effect, d.Effect);
        }
        Assert.NotNull(reg.Motion);
    }

    [Fact]
    public void Generate_WithoutKimodo_FailsWithProviderUnavailable_AndExplainsWhatToDo()
    {
        var reg = NewRegistry();
        var (s, ch) = NewProject();
        var r = Run(reg, s, "motion.generate", new { characterId = ch, prompt = "a person walks", durationSeconds = 2 });
        Assert.Equal("provider_unavailable", r.Error?.Code);
        Assert.Contains("procedural", r.Error!.Message);
        Assert.Contains("Nothing is downloaded", r.Error.Message);
        Assert.Equal("provider_unavailable", Run(reg, s, "motion.generate", new { characterId = ch, prompt = "walk", durationSeconds = 2, provider = "kimodo" }).Error?.Code);
        Assert.Equal("invalid_args", Run(reg, s, "motion.generate", new { characterId = ch, prompt = "walk", durationSeconds = 2, provider = "nope" }).Error?.Code);
        Assert.Equal("invalid_args", Run(reg, s, "motion.generate", new { characterId = ch, prompt = "walk", durationSeconds = 500, provider = "procedural" }).Error?.Code);
        Assert.False(s.IsDirty);

        var providers = Ok(reg, s, "motion.providers", new { }).Data!;
        Assert.Null((string?)providers["default"]);
        var kimodo = providers["providers"]!.AsArray().First(p => (string?)p!["id"] == "kimodo")!;
        Assert.False((bool)kimodo["available"]!);
        Assert.Equal("false", (string?)kimodo["details"]!["runnerFound"]);
        Assert.True((int)providers["hardware"]!["logicalCpus"]! > 0);
        var proc = providers["providers"]!.AsArray().First(p => (string?)p!["id"] == "procedural")!;
        Assert.True((bool)proc["available"]!);
        Assert.Contains("not AI", (string?)proc["name"]);
    }

    [Fact]
    public void DraftLifecycle_GenerateApplyUndoRedoAcceptDiscard()
    {
        var reg = NewRegistry();
        var (s, ch) = NewProject();
        var gen = Ok(reg, s, "motion.generate", new { characterId = ch, prompt = "walk forward", durationSeconds = 2, seed = 3, provider = "procedural" });
        Assert.Null(gen.TransactionId); // starting a job does not change the project
        Assert.Contains(gen.Warnings, w => w.Code == "procedural");
        var jobId = (string)gen.Data!["jobId"]!;
        Assert.Equal(3UL, (ulong)gen.Data["seed"]!);
        var st = Wait(reg, s, jobId);
        Assert.Equal("ready", (string?)st["state"]);
        Assert.Equal(1.0, (double)st["progress"]!);
        Assert.False(s.IsDirty);

        var applied = Ok(reg, s, "motion.job_apply", new { jobId });
        var clipId = (string)applied.Data!["clipId"]!;
        var clip = s.Project.FindClip(Core.Ids.ObjectId.Parse(clipId))!;
        Assert.True(clip.IsDraft);
        Assert.Equal("procedural", clip.Metadata["provider"]);
        Assert.Equal("walk forward", clip.Metadata["prompt"]);
        Assert.Equal("3", clip.Metadata["seed"]);
        Assert.Equal(60, clip.DurationFrames); // 2 s at 30 fps
        Assert.Contains(clip.Tracks, t => t.Channel == TrackChannels.Translation);
        Assert.Equal("applied", (string?)Ok(reg, s, "motion.job_status", new { jobId }).Data!["state"]);
        Assert.Equal(clipId, (string?)Ok(reg, s, "motion.job_status", new { jobId }).Data!["clipId"]);
        Assert.Contains(Ok(reg, s, "project.inspect", new { }).Data!["clips"]!.AsArray(), c => (string?)c!["clipId"] == clipId && (bool)c["isDraft"]!);

        // Undo / redo of the apply (same clip id).
        Assert.True(s.Undo(CommandOrigin.Test).Ok);
        Assert.Null(s.Project.FindClip(clip.Id));
        Assert.True(s.Redo(CommandOrigin.Test).Ok);
        Assert.True(s.Project.FindClip(clip.Id)!.IsDraft);

        // Accept (undoable), then not a draft any more.
        Ok(reg, s, "clip.accept_draft", new { clipId });
        Assert.False(s.Project.FindClip(clip.Id)!.IsDraft);
        Assert.Equal("not_draft", Run(reg, s, "clip.accept_draft", new { clipId }).Error?.Code);
        Assert.True(s.Undo(CommandOrigin.Test).Ok);
        Assert.True(s.Project.FindClip(clip.Id)!.IsDraft);

        // Discard = clip.delete (undoable).
        Ok(reg, s, "clip.delete", new { clipId });
        Assert.Null(s.Project.FindClip(clip.Id));
        Assert.True(s.Undo(CommandOrigin.Test).Ok);
        Assert.NotNull(s.Project.FindClip(clip.Id));

        // The job handed its clip to the project: applying it again is refused (undo the apply to get it back).
        Assert.Equal("job_applied", Run(reg, s, "motion.job_apply", new { jobId, name = "Walk 2" }).Error?.Code);
    }

    [Fact]
    public void FailedAndUnknownJobs_CannotBeApplied()
    {
        var reg = NewRegistry();
        var (s, ch) = NewProject();
        var jobId = (string)Ok(reg, s, "motion.generate", new { characterId = ch, prompt = "do a backflip", durationSeconds = 1, provider = "procedural" }).Data!["jobId"]!;
        var st = Wait(reg, s, jobId);
        Assert.Equal("failed", (string?)st["state"]);
        Assert.Equal("unsupported_prompt", (string?)st["errorCode"]);
        Assert.Equal("job_failed", Run(reg, s, "motion.job_apply", new { jobId }).Error?.Code);
        Assert.Equal("not_found", Run(reg, s, "motion.job_apply", new { jobId = "nope" }).Error?.Code);
        Assert.False(s.IsDirty);
    }

    [Fact]
    public void KimodoThroughCommands_DefaultProvider_Progress_Cancel()
    {
        var reg = NewRegistry(FakeRunner.Settings());
        var events = new List<MotionJobSnapshot>();
        reg.Motion!.JobChanged += e => { lock (events) events.Add(e); };
        var (s, ch) = NewProject();
        var providers = Ok(reg, s, "motion.providers", new { probe = true }).Data!;
        Assert.Equal("kimodo", (string?)providers["default"]);

        // Default provider = kimodo once it is installed.
        var gen = Ok(reg, s, "motion.generate", new { characterId = ch, prompt = "a person waves", durationSeconds = 1, seed = 11 });
        Assert.Equal("kimodo", (string?)gen.Data!["provider"]);
        var jobId = (string)gen.Data["jobId"]!;
        Assert.Equal("ready", (string?)Wait(reg, s, jobId)["state"]);
        lock (events)
        {
            Assert.Contains(events, e => e.JobId == jobId && e.Stage == "generate");
            Assert.Contains(events, e => e.JobId == jobId && e.State == MotionJobState.Ready);
        }
        var clipId = (string)Ok(reg, s, "motion.job_apply", new { jobId }).Data!["clipId"]!;
        var clip = s.Project.FindClip(Core.Ids.ObjectId.Parse(clipId))!;
        Assert.Equal("kimodo", clip.Metadata["source.provider"]);
        Assert.Equal("kimodo-soma-rp-v1.1-f32.gguf", clip.Metadata["kimodo.model"]);

        // A running job cannot be applied; cancelling stops it.
        var hang = (string)Ok(reg, s, "motion.generate", new { characterId = ch, prompt = "hang", durationSeconds = 1 }).Data!["jobId"]!;
        Assert.Equal("job_running", Run(reg, s, "motion.job_apply", new { jobId = hang }).Error?.Code);
        Assert.True((bool)Ok(reg, s, "motion.job_cancel", new { jobId = hang }).Data!["cancelRequested"]!);
        Assert.Equal("cancelled", (string?)Wait(reg, s, hang)["state"]);
        Assert.Equal("job_cancelled", Run(reg, s, "motion.job_apply", new { jobId = hang }).Error?.Code);
        reg.Motion.Dispose();
    }

    [Fact]
    public void JobStatus_DoesNotBlock_InTheApp()
    {
        var reg = NewRegistry(FakeRunner.Settings());
        reg.Motion!.AllowBlockingWait = false;
        var (s, ch) = NewProject();
        var job = (string)Ok(reg, s, "motion.generate", new { characterId = ch, prompt = "hang", durationSeconds = 1 }).Data!["jobId"]!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var st = Ok(reg, s, "motion.job_status", new { jobId = job, waitSeconds = 30 });
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Contains(st.Warnings, w => w.Code == "wait_ignored");
        Assert.Equal("running", (string?)st.Data!["state"]);
        Ok(reg, s, "motion.job_cancel", new { jobId = job });
        reg.Motion.Dispose();
    }

    [Fact]
    public void Generate_OnAnImportedRig_AndRejectsUnmappedRigs()
    {
        var reg = NewRegistry();
        var p = Project.CreateNew("AI");
        var rig = ProceduralAndRetargetTests.RiggedFigure();
        p.Rigs.Add(rig);
        var ch = MannequinFactory.CreateCharacter(rig, "Figure");
        p.Characters.Add(ch);
        var s = new ProjectSession(p);
        var job = (string)Ok(reg, s, "motion.generate", new { characterId = ch.Id.ToString(), prompt = "squat", durationSeconds = 2, provider = "procedural", fps = 24 }).Data!["jobId"]!;
        Assert.Equal("ready", (string?)Wait(reg, s, job)["state"]);
        var clipId = (string)Ok(reg, s, "motion.job_apply", new { jobId = job }).Data!["clipId"]!;
        var clip = s.Project.FindClip(Core.Ids.ObjectId.Parse(clipId))!;
        Assert.Equal(rig.Id, clip.Skeleton);
        Assert.Equal(24, clip.Fps.FramesPerSecond);

        rig.HumanoidMap.RemoveAll(m => m.Standard == H.Pelvis);
        Assert.Equal("not_humanoid", Run(reg, s, "motion.generate", new { characterId = ch.Id.ToString(), prompt = "walk", durationSeconds = 1, provider = "procedural" }).Error?.Code);
    }

    /// <summary>Phase 7 acceptance: text → motion → keys → edit → export (GLB validated).</summary>
    [Fact]
    public void Acceptance_TextToMotion_Keys_Edit_Export()
    {
        var reg = NewRegistry();
        var (s, ch) = NewProject();
        var jobId = (string)Ok(reg, s, "motion.generate", new { characterId = ch, prompt = "walk", durationSeconds = 2, seed = 1, provider = "procedural" }).Data!["jobId"]!;
        Assert.Equal("ready", (string?)Wait(reg, s, jobId)["state"]);
        var clipId = (string)Ok(reg, s, "motion.job_apply", new { jobId }).Data!["clipId"]!;
        Ok(reg, s, "clip.accept_draft", new { clipId });

        // Edit: pose from frame 10, raise the left arm, key it.
        Ok(reg, s, "anim.pose_from_frame", new { clipId, characterId = ch, frame = 10 });
        Ok(reg, s, "pose.set_bone", new { characterId = ch, bone = H.UpperArm(Side.Left), rx = 0, ry = 0, rz = 60 });
        Ok(reg, s, "key.set", new { clipId, characterId = ch, frame = 10 });
        var ev = Ok(reg, s, "anim.evaluate", new { clipId, characterId = ch, frame = 10 }).Data!;
        var arm = ev["bones"]!.AsArray().First(b => (string?)b!["name"] == H.UpperArm(Side.Left))!;
        Assert.Equal(60, (double)arm["rotation"]![2]!, 3);

        var dir = Path.Combine(Path.GetTempPath(), "blazma-ai-accept-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "walk.glb");
        var export = Ok(reg, s, "animation.export", new { clipId, characterId = ch, path });
        Assert.True(File.Exists(path));
        Assert.True((bool)export.Data!["validation"]!["ok"]!, export.Data.ToJsonString());
        var validate = Ok(reg, s, "animation.validate", new { path, clipId, characterId = ch }).Data!;
        Assert.True((bool)validate["ok"]!, validate.ToJsonString());
        Directory.Delete(dir, true);
    }

    /// <summary>Agents: the same flow through the real CLI process (headless), as one script.</summary>
    [Fact]
    public async Task Cli_Script_GeneratesAppliesAndAccepts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "blazma-ai-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "demo.blazanim");
        var (code, _, err) = await Tools.RunCli("new", file);
        Assert.True(code == 0, err);
        var script = Path.Combine(dir, "gen.jsonl");
        File.WriteAllLines(script, new[]
        {
            "{\"command\":\"motion.generate\",\"args\":{\"characterId\":\"${character}\",\"prompt\":\"jump\",\"durationSeconds\":2,\"provider\":\"procedural\"},\"save\":{\"job\":\"jobId\"}}",
            "{\"command\":\"motion.job_status\",\"args\":{\"jobId\":\"${job}\",\"waitSeconds\":60}}",
            "{\"command\":\"motion.job_apply\",\"args\":{\"jobId\":\"${job}\"},\"save\":{\"clip\":\"clipId\"}}",
            "{\"command\":\"clip.accept_draft\",\"args\":{\"clipId\":\"${clip}\"}}",
        });
        (code, var output, err) = await Tools.RunCli("run", file, script, "--json");
        Assert.True(code == 0, output + err);
        var json = JsonNode.Parse(output)!;
        Assert.True((bool)json["saved"]!);
        (code, output, err) = await Tools.RunCli("inspect", file, "--json");
        var clips = JsonNode.Parse(output)!["data"]!["clips"]!.AsArray();
        Assert.Contains(clips, c => ((string?)c!["name"])!.StartsWith("Procedural: jump") && !(bool)c["isDraft"]!);
        Directory.Delete(dir, true);
    }
}
