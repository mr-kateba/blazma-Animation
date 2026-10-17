using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Blazma.AI;

namespace Blazma.Tests.AI;

/// <summary>Phase 7 review fixes on the Kimodo side: runner lifetime, busy diagnostics, input/output validation, environment.</summary>
public class KimodoLifetimeTests
{
    sealed class Collect : IProgress<GenerationProgress>
    {
        public readonly TaskCompletionSource<bool> SawGenerate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(GenerationProgress value)
        {
            if (value.Stage == "generate") SawGenerate.TrySetResult(true);
        }
    }

    static MotionRequest Req(string prompt, double seconds = 1) => new(prompt, seconds, 30, 7);

    /// <summary>Exited, or a zombie nobody reaped yet (an orphan in a container whose PID 1 does not reap).</summary>
    internal static bool Exited(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                var state = stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[0];
                return state is "Z" or "X";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
        }
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

    // ---------------------------------------------------------------- #1 diagnostics never wait behind a generation

    [Fact]
    public async Task Diagnose_WhileAGenerationRuns_ReturnsAtOnce_WithTheCachedHello()
    {
        using var k = new KimodoProvider(FakeRunner.Settings());
        var progress = new Collect();
        using var cts = new CancellationTokenSource();
        var hang = k.GenerateAsync(Req("hang"), progress, cts.Token);
        await progress.SawGenerate.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var sw = Stopwatch.StartNew();
        var d = await k.DiagnoseAsync(true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
        Assert.Equal("true", d.Details["runnerBusy"]);
        Assert.Equal("fake-kimodo-runner", d.Details["runner.runner"]); // from the hello cached at start
        Assert.Contains(d.Warnings, w => w.Contains("busy"));
        Assert.True(k.RunnerRunning); // the generation was not disturbed

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hang);
    }

    // ---------------------------------------------------------------- #2 orphaned runner

    [Fact]
    public async Task Runner_Exits_WhenItsHostIsKilled()
    {
        // A separate host process (the fake runner in --host mode) starts the runner through KimodoProvider and begins a
        // generation that never ends; then the host is killed without any chance to clean up.
        var psi = new ProcessStartInfo(FakeRunner.DotnetHost()) { UseShellExecute = false, RedirectStandardOutput = true };
        psi.ArgumentList.Add(FakeRunner.Dll);
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add(FakeRunner.ModelDir());
        using var host = Process.Start(psi)!;
        int runnerPid;
        try
        {
            var line = await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(90));
            Assert.StartsWith("runner-pid ", line);
            runnerPid = int.Parse(line!["runner-pid ".Length..], System.Globalization.CultureInfo.InvariantCulture);
            Assert.False(Exited(runnerPid));
        }
        finally
        {
            host.Kill(); // SIGKILL / TerminateProcess: no finally blocks, no ProcessExit handlers in the host
        }
        await host.WaitForExitAsync();
        // The runner notices the end of its input (all platforms), PR_SET_PDEATHSIG (the real runner on Linux) or the job
        // object closing (Windows), and is gone within seconds.
        await WaitUntil(() => Exited(runnerPid), 5000);
    }

    [Fact]
    public async Task Runner_ExitsOnEndOfInput_EvenDuringAGeneration()
    {
        var psi = new ProcessStartInfo(FakeRunner.DotnetHost()) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(FakeRunner.Dll);
        psi.ArgumentList.Add("--stdio");
        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync();
        await p.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"generate\",\"params\":{\"prompt\":\"hang\",\"frames\":30}}");
        await p.StandardInput.FlushAsync();
        // Wait until it is inside the endless generation (it reported progress).
        while (await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) is { } l && !l.Contains("\"generate\"")) { }
        p.StandardInput.Close();
        Assert.True(await Task.Run(() => p.WaitForExit(5000)), "the runner kept running after its input closed");
    }

    // ---------------------------------------------------------------- #6 / #11 malformed or hostile results

    [Fact]
    public async Task HugeFrameCount_FromTheRunner_IsRejected_WithoutAllocating()
    {
        using var k = new KimodoProvider(FakeRunner.Settings());
        var e = await Assert.ThrowsAsync<MotionGenerationException>(() => k.GenerateAsync(Req("huge-frames"), null, CancellationToken.None));
        Assert.Equal("invalid_motion", e.Code);
        Assert.Contains("frames", e.Message);
    }

    /// <summary>As the runner's line arrives: parsed from JSON text (numbers are JsonElement-backed).</summary>
    static JsonObject Wire(JsonObject o) => JsonNode.Parse(o.ToJsonString())!.AsObject();

    static JsonObject ValidResult(int frames)
    {
        var rot = new JsonArray();
        var pos = new JsonArray();
        for (var f = 0; f < frames; f++)
        {
            for (var j = 0; j < SomaSkeleton.Names.Count; j++) { rot.Add(0.0); rot.Add(0.0); rot.Add(0.0); rot.Add(1.0); }
            pos.Add(0.0); pos.Add(1.0); pos.Add(0.0);
        }
        var offsets = new JsonArray();
        for (var j = 0; j < SomaSkeleton.Names.Count; j++)
        {
            var o = SomaSkeleton.Offset(j);
            offsets.Add(new JsonArray(o.X, o.Y, o.Z));
        }
        return new JsonObject
        {
            ["fps"] = 30, ["frames"] = frames, ["joints"] = SomaSkeleton.Names.Count,
            ["skeleton"] = new JsonObject
            {
                ["key"] = "soma30", ["up"] = "+Y", ["forward"] = "+Z",
                ["names"] = new JsonArray(SomaSkeleton.Names.Select(n => (JsonNode?)n).ToArray()),
                ["parents"] = new JsonArray(SomaSkeleton.Parents.Select(x => (JsonNode?)x).ToArray()),
                ["offsets"] = offsets,
            },
            ["rotations"] = rot, ["rootPositions"] = pos,
        };
    }

    [Theory]
    [InlineData("frames", 4294967396L)]
    [InlineData("frames", 10001L)]
    [InlineData("frames", 0L)]
    [InlineData("joints", 1000000000L)]
    [InlineData("joints", 29L)]
    public void Parse_ValidatesSizes_BeforeAllocating(string field, long value)
    {
        var r = ValidResult(2);
        r[field] = value;
        Assert.Equal("invalid_motion", Assert.Throws<MotionGenerationException>(() => KimodoProvider.Parse(Wire(r), Req("x"))).Code);
    }

    [Fact]
    public void Parse_RejectsFractionalCounts_AndAcceptsAValidResult()
    {
        var r = ValidResult(3);
        Assert.Equal(3, KimodoProvider.Parse(Wire(r), Req("x")).FrameCount);
        r["frames"] = 2.5;
        Assert.Equal("invalid_motion", Assert.Throws<MotionGenerationException>(() => KimodoProvider.Parse(Wire(r), Req("x"))).Code);
    }

    [Fact]
    public async Task ErrorWithoutId_FailsTheCallInFlight_Immediately()
    {
        using var k = new KimodoProvider(FakeRunner.Settings() with { GenerateTimeout = TimeSpan.FromMinutes(5) });
        var sw = Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<MotionGenerationException>(() => k.GenerateAsync(Req("anon-error"), null, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("invalid_request", e.Code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), sw.Elapsed.ToString());
    }

    // ---------------------------------------------------------------- #10 provider lifetime

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference CreateAndDispose()
    {
        var k = new KimodoProvider(new KimodoSettings());
        k.Dispose();
        return new WeakReference(k);
    }

    [Fact]
    public void DisposedProvider_IsNotKeptAlive_ByTheProcessExitHandler()
    {
        var w = CreateAndDispose();
        for (var i = 0; i < 5 && w.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(w.IsAlive);
    }

    [Fact]
    public async Task DisposeWhileTheRunnerStarts_NeverKeepsTheRunner()
    {
        var k = new KimodoProvider(FakeRunner.Settings());
        var logs = new ConcurrentQueue<string>();
        k.Log += logs.Enqueue;
        var task = k.GenerateAsync(Req("walk"), null, CancellationToken.None);
        await WaitUntil(() => logs.Any(l => l.StartsWith("kimodo-runner started (pid ", StringComparison.Ordinal)), 30000);
        k.Dispose();
        var e = await Assert.ThrowsAsync<MotionGenerationException>(() => task).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Contains(e.Code, new[] { "runner_stopped", "runner_crashed" });
        var started = logs.First(l => l.StartsWith("kimodo-runner started (pid ", StringComparison.Ordinal));
        var pid = int.Parse(started["kimodo-runner started (pid ".Length..].TrimEnd(')', '.'), System.Globalization.CultureInfo.InvariantCulture);
        await WaitUntil(() => Exited(pid));
        Assert.False(k.RunnerRunning);
        Assert.Equal("runner_stopped", (await Assert.ThrowsAsync<MotionGenerationException>(() => k.GenerateAsync(Req("walk"), null, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task FailedProbe_StopsOnlyTheRunnerItProbed_AndAWaitingGenerationSucceeds()
    {
        using var k = new KimodoProvider(FakeRunner.Settings(FakeRunner.ModelDir("diagnose-fail-")));
        await k.GenerateAsync(Req("warm up"), null, CancellationToken.None);
        var first = k.RunnerProcessId!.Value;

        var probe = k.DiagnoseAsync(true, CancellationToken.None); // holds the runner; diagnose fails after 0.5 s
        var generate = k.GenerateAsync(Req("walk"), null, CancellationToken.None); // queued behind the probe
        var d = await probe.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Contains(d.Warnings, w => w.Contains("diagnose_failed"));
        var r = await generate.WaitAsync(TimeSpan.FromSeconds(60)); // not killed by the probe's cleanup
        Assert.Equal(30, r.FrameCount);
        await WaitUntil(() => Exited(first));
        Assert.True(k.RunnerRunning);
        Assert.NotEqual(first, k.RunnerProcessId);
    }

    // ---------------------------------------------------------------- #3 non-ASCII paths, #12 environment and paths

    [Fact]
    public async Task NonAsciiModelFolder_Works()
    {
        using var k = new KimodoProvider(FakeRunner.Settings(FakeRunner.ModelDir("نماذج-حركة-")));
        Assert.True(k.Describe().Available, k.Describe().UnavailableReason);
        var r = await k.GenerateAsync(Req("walk"), null, CancellationToken.None);
        Assert.Equal("kimodo-soma-rp-v1.1-f32.gguf", r.Metadata["kimodo.model"]);
        var d = await k.DiagnoseAsync(true, CancellationToken.None);
        Assert.Equal("true", d.Details["runner.motionModelExists"]);
    }

    [Fact]
    public void RunnerEnvironment_IsAllowListed()
    {
        var host = new System.Collections.Hashtable
        {
            ["PATH"] = "/usr/bin", ["HOME"] = "/home/u", ["VK_ICD_FILENAMES"] = "/icd.json", ["GGML_VK_VISIBLE_DEVICES"] = "0",
            ["CUDA_VISIBLE_DEVICES"] = "1", ["OPENAI_API_KEY"] = "secret", ["GITHUB_TOKEN"] = "secret", ["KIMODO_THREADS"] = "64",
            ["KIMODO_TEXT_RESIDENT_LIMIT_MIB"] = "1", ["BLAZMA_KIMODO_RUNNER"] = "/x", ["LD_PRELOAD"] = "/evil.so",
        };
        var env = RunnerProcess.RunnerEnvironment(new KimodoSettings { Device = "cpu" }, host, 4242, windows: false);
        Assert.Equal("/usr/bin", env["PATH"]);
        Assert.Equal("/home/u", env["HOME"]);
        Assert.Equal("/icd.json", env["VK_ICD_FILENAMES"]);
        Assert.Equal("0", env["GGML_VK_VISIBLE_DEVICES"]);
        Assert.Equal("1", env["CUDA_VISIBLE_DEVICES"]);
        Assert.Equal("cpu", env["KIMODO_BACKEND"]);
        Assert.Equal("4242", env["KIMODO_RUNNER_PARENT_PID"]);
        foreach (var hidden in new[] { "OPENAI_API_KEY", "GITHUB_TOKEN", "KIMODO_THREADS", "KIMODO_TEXT_RESIDENT_LIMIT_MIB", "BLAZMA_KIMODO_RUNNER", "LD_PRELOAD" })
            Assert.False(env.ContainsKey(hidden), hidden);

        var win = RunnerProcess.RunnerEnvironment(new KimodoSettings(), new System.Collections.Hashtable { ["Path"] = "C:\\Windows", ["SystemRoot"] = "C:\\Windows", ["APIKEY"] = "x" }, 1, windows: true);
        Assert.True(win.ContainsKey("PATH"));
        Assert.True(win.ContainsKey("systemroot"));
        Assert.False(win.ContainsKey("APIKEY"));
        Assert.False(win.ContainsKey("KIMODO_RUNNER_PARENT_PID"));
        Assert.False(win.ContainsKey("KIMODO_BACKEND")); // auto
    }

    [Fact]
    public async Task Runner_DoesNotInherit_TheHostsOtherVariables()
    {
        Environment.SetEnvironmentVariable("BLAZMA_TEST_SECRET", "do-not-pass");
        try
        {
            using var k = new KimodoProvider(FakeRunner.Settings());
            var d = await k.DiagnoseAsync(true, CancellationToken.None);
            Assert.Equal("false", d.Details["runner.inheritedSecret"]);
            if (!OperatingSystem.IsWindows()) Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), d.Details["runner.parentPid"]);
        }
        finally { Environment.SetEnvironmentVariable("BLAZMA_TEST_SECRET", null); }
    }

    [Fact]
    public void RunnerPath_MustBeAbsolute()
    {
        foreach (var relative in new[] { "kimodo-runner", Path.Combine("bin", "kimodo-runner"), "dotnet" })
        {
            var s = FakeRunner.Settings() with { RunnerPath = relative };
            Assert.Null(s.ResolveRunner());
            Assert.Contains("absolute", s.MissingReason());
            using var k = new KimodoProvider(s);
            Assert.False(k.Describe().Available);
        }
        Assert.NotNull(FakeRunner.Settings().ResolveRunner());
    }
}
