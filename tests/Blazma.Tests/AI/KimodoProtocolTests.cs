using System.Diagnostics;
using Blazma.AI;

namespace Blazma.Tests.AI;

/// <summary>Settings that point the Kimodo provider at the fake runner (tests/Blazma.FakeKimodoRunner) and dummy model files.</summary>
public static class FakeRunner
{
    static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Blazma.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Blazma.slnx not found");
    }

    public static string Dll
    {
        get
        {
            var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
            var path = Path.Combine(RepoRoot(), "tests", "Blazma.FakeKimodoRunner", "bin", config, "net10.0", "fake-kimodo-runner.dll");
            return File.Exists(path) ? path : throw new FileNotFoundException(path);
        }
    }

    /// <summary>Full path of the dotnet host (the provider requires an existing runner file).</summary>
    public static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h && File.Exists(h)) return h;
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("dotnet host not found");
    }

    /// <summary>A temporary folder with empty stand-ins for the GGUF files (the fake runner never reads them).</summary>
    public static string ModelDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "blazma-kimodo-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "models"));
        File.WriteAllText(Path.Combine(dir, "models", "kimodo-soma-rp-v1.1-f32.gguf"), "dummy");
        File.WriteAllText(Path.Combine(dir, "Llama-3-Kimodo-Q8_0.gguf"), "dummy");
        File.WriteAllText(Path.Combine(dir, "tokenizer.gguf"), "dummy");
        return dir;
    }

    public static KimodoSettings Settings(string? modelDir = null) => new()
    {
        RunnerPath = DotnetHost(),
        RunnerArguments = new[] { Dll },
        ModelDirectory = modelDir ?? ModelDir(),
        StartTimeout = TimeSpan.FromSeconds(30),
        GenerateTimeout = TimeSpan.FromSeconds(60),
        IdleTimeout = TimeSpan.FromMinutes(5),
    };
}

public class KimodoProtocolTests
{
    sealed class Collect : IProgress<GenerationProgress>
    {
        public readonly List<GenerationProgress> Items = new();
        public readonly TaskCompletionSource<bool> SawGenerate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(GenerationProgress value)
        {
            lock (Items) Items.Add(value);
            if (value.Stage == "generate") SawGenerate.TrySetResult(true);
        }
    }

    static MotionRequest Req(string prompt, double seconds = 1) => new(prompt, seconds, 30, 7);

    static bool Exited(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.HasExited; }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    static async Task WaitUntil(Func<bool> condition, int ms = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < ms) await Task.Delay(50);
        Assert.True(condition(), "condition not reached in time");
    }

    [Fact]
    public async Task NotConfigured_IsUnavailable_WithAHelpfulMessage_AndStartsNothing()
    {
        using var k = new KimodoProvider(new KimodoSettings());
        var info = k.Describe();
        Assert.False(info.Available);
        Assert.Contains(KimodoSettings.RunnerEnv, info.UnavailableReason);
        var e = await Assert.ThrowsAsync<MotionGenerationException>(() => k.GenerateAsync(Req("walk"), null, CancellationToken.None));
        Assert.Equal("provider_unavailable", e.Code);
        Assert.False(k.RunnerRunning);

        // Runner set but no models: still unavailable, names the missing file.
        using var k2 = new KimodoProvider(FakeRunner.Settings() with { ModelDirectory = Path.GetTempPath() + "missing-" + Guid.NewGuid() });
        Assert.Contains("kimodo-soma-rp-v1.1-f32.gguf", k2.Describe().UnavailableReason);
        // SMPL-X / G1 are rejected by design.
        using var k3 = new KimodoProvider(FakeRunner.Settings() with { Model = "smplx-rp-v1" });
        Assert.Contains("Unsupported", k3.Describe().UnavailableReason);
    }

    [Fact]
    public async Task Generate_EndToEnd_ReportsProgress_ReturnsSoma_AndReusesTheProcess()
    {
        using var k = new KimodoProvider(FakeRunner.Settings());
        Assert.True(k.Describe().Available, k.Describe().UnavailableReason);
        Assert.False(k.RunnerRunning); // started on demand only
        var progress = new Collect();
        var r = await k.GenerateAsync(Req("a person walks", 2), progress, CancellationToken.None);
        Assert.Equal(SomaSkeleton.Key, r.Skeleton.Key);
        Assert.Equal(30, r.JointCount);
        Assert.Equal(60, r.FrameCount);
        Assert.Equal(30, r.Fps);
        Assert.True(r.RootTranslations[^1].Z > 1.9);
        Assert.Equal("kimodo-soma-rp-v1.1-f32.gguf", r.Metadata["kimodo.model"]);
        Assert.Contains(r.Warnings, w => w.Contains("fingers"));
        Assert.Contains(progress.Items, p => p.Stage == "generate");
        Assert.Contains(progress.Items, p => p.Stage == "done" && p.Fraction == 1);
        Assert.True(k.RunnerRunning);
        var pid = k.RunnerProcessId!.Value;

        await k.GenerateAsync(Req("again"), null, CancellationToken.None);
        Assert.Equal(pid, k.RunnerProcessId); // models stay loaded between requests
        Assert.Contains(k.RecentLog(), l => l.Contains("fake kimodo-runner ready")); // stderr captured
    }

    [Fact]
    public async Task RunnerStops_WhenIdle()
    {
        using var k = new KimodoProvider(FakeRunner.Settings() with { IdleTimeout = TimeSpan.FromMilliseconds(300) });
        await k.GenerateAsync(Req("idle test"), null, CancellationToken.None);
        var pid = k.RunnerProcessId!.Value;
        await WaitUntil(() => !k.RunnerRunning && Exited(pid));
    }

    [Fact]
    public async Task Cancel_KillsTheRunner_AndTheNextRequestStartsAFreshOne()
    {
        using var k = new KimodoProvider(FakeRunner.Settings());
        var progress = new Collect();
        using var cts = new CancellationTokenSource();
        var task = k.GenerateAsync(Req("hang please"), progress, cts.Token);
        await progress.SawGenerate.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var pid = k.RunnerProcessId!.Value;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await WaitUntil(() => Exited(pid));
        Assert.False(k.RunnerRunning);

        var r = await k.GenerateAsync(Req("walk"), null, CancellationToken.None);
        Assert.Equal(30, r.FrameCount);
        Assert.NotEqual(pid, k.RunnerProcessId);
    }

    [Fact]
    public async Task Crash_BecomesACleanError_WithTheRunnerLog()
    {
        using var k = new KimodoProvider(FakeRunner.Settings());
        var e = await Assert.ThrowsAsync<MotionGenerationException>(() => k.GenerateAsync(Req("crash-now"), null, CancellationToken.None));
        Assert.Equal("runner_crashed", e.Code);
        Assert.Contains("exit code 3", e.Message);
        Assert.Contains("simulated crash", e.Message);
        Assert.False(k.RunnerRunning);
        var r = await k.GenerateAsync(Req("walk"), null, CancellationToken.None); // recovers
        Assert.Equal(30, r.FrameCount);
    }

    [Fact]
    public async Task OversizedMessage_IsRejected_AndTheRunnerKilled()
    {
        using var k = new KimodoProvider(FakeRunner.Settings() with { MaxMessageBytes = 1024 * 1024 });
        await k.GenerateAsync(Req("warm up"), null, CancellationToken.None);
        var pid = k.RunnerProcessId!.Value;
        var e = await Assert.ThrowsAsync<MotionGenerationException>(() => k.GenerateAsync(Req("oversize"), null, CancellationToken.None));
        Assert.Equal("message_too_large", e.Code);
        await WaitUntil(() => Exited(pid));
    }

    [Fact]
    public async Task Garbage_IsAProtocolError()
    {
        using var k = new KimodoProvider(FakeRunner.Settings());
        var e = await Assert.ThrowsAsync<MotionGenerationException>(() => k.GenerateAsync(Req("garbage"), null, CancellationToken.None));
        Assert.Equal("protocol_error", e.Code);
    }

    [Fact]
    public async Task Timeout_KillsTheRunner()
    {
        using var k = new KimodoProvider(FakeRunner.Settings() with { GenerateTimeout = TimeSpan.FromSeconds(2) });
        await k.GenerateAsync(Req("warm up"), null, CancellationToken.None); // the first start (dotnet host) can be slow
        var pid = k.RunnerProcessId!.Value;
        var sw = Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<MotionGenerationException>(() => k.GenerateAsync(Req("hang"), null, CancellationToken.None));
        Assert.Equal("runner_timeout", e.Code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
        await WaitUntil(() => Exited(pid));
    }

    [Fact]
    public async Task RunnerError_IsReported_AndTheRunnerKeepsRunning()
    {
        using var k = new KimodoProvider(FakeRunner.Settings());
        await k.GenerateAsync(Req("warm up"), null, CancellationToken.None);
        var pid = k.RunnerProcessId;
        var e = await Assert.ThrowsAsync<MotionGenerationException>(() => k.GenerateAsync(Req("fail"), null, CancellationToken.None));
        Assert.Equal("generation_failed", e.Code);
        Assert.Contains("simulated model error", e.Message);
        await k.GenerateAsync(Req("ok"), null, CancellationToken.None);
        Assert.Equal(pid, k.RunnerProcessId);
    }

    [Fact]
    public async Task Diagnose_WithoutProbe_StartsNothing_WithProbe_AsksTheRunner()
    {
        using var k = new KimodoProvider(FakeRunner.Settings());
        var d = await k.DiagnoseAsync(false, CancellationToken.None);
        Assert.Equal("true", d.Details["runnerFound"]);
        Assert.Equal("true", d.Details["motionModelFound"]);
        Assert.Equal("true", d.Details["tokenizerFound"]);
        Assert.False(k.RunnerRunning);
        Assert.True(d.Hardware.LogicalCpus > 0);

        var p = await k.DiagnoseAsync(true, CancellationToken.None);
        Assert.Equal("fake-kimodo-runner", p.Details["runner.runner"]);
        Assert.Equal("true", p.Details["runner.motionModelExists"]);
        Assert.Contains(p.Warnings, w => w.Contains("no model is ever loaded"));
    }

    [Fact]
    public void SomaFromRunner_RejectsADifferentSkeleton()
    {
        var offsets = Enumerable.Range(0, 30).Select(SomaSkeleton.Offset).ToList();
        Assert.Equal(SomaSkeleton.Key, SomaSkeleton.FromRunner("soma30", SomaSkeleton.Names, SomaSkeleton.Parents, offsets).Key);
        Assert.Equal("unsupported_skeleton", Assert.Throws<MotionGenerationException>(() =>
            SomaSkeleton.FromRunner("g1skel34", SomaSkeleton.Names, SomaSkeleton.Parents, offsets)).Code);
        var swapped = SomaSkeleton.Names.ToList();
        (swapped[11], swapped[17]) = (swapped[17], swapped[11]);
        Assert.Equal("unsupported_skeleton", Assert.Throws<MotionGenerationException>(() =>
            SomaSkeleton.FromRunner("soma30", swapped, SomaSkeleton.Parents, offsets)).Code);
    }
}
