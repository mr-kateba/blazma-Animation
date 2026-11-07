using Blazma.AI;

namespace Blazma.Tests.AI;

/// <summary>Device modes and the automatic fallback order (DECISIONS D44), against the fake runner's device lists and failures.</summary>
public class KimodoDeviceTests
{
    static MotionRequest Req(string prompt = "walk") => new(prompt, 1, 30, 7);

    static KimodoSettings With(string devices, string? fail = null, string device = "auto", string? gpu = null)
    {
        var s = FakeRunner.Settings();
        var args = new List<string> { FakeRunner.Dll, "--fake-devices", devices };
        if (fail is not null) args.AddRange(new[] { "--fake-fail", fail });
        return s with { RunnerArguments = args, Device = device, GpuDevice = gpu };
    }

    static KimodoDevice D(string name, string type, string backend) => new(name, name, type, backend, 0, 0);

    [Fact]
    public void Plan_Auto_Is_Cuda_Then_Vulkan_Then_Hybrid_Then_Cpu()
    {
        var devices = new[] { D("Vulkan1", "igpu", "Vulkan"), D("Vulkan0", "gpu", "Vulkan"), D("CUDA0", "gpu", "CUDA"), D("CPU", "cpu", "CPU") };
        Assert.Equal(new[] { "gpu on CUDA0", "gpu on Vulkan0", "hybrid on CUDA0", "cpu" },
            KimodoDevicePlanner.Plan("auto", null, devices).Select(a => a.ToString()));
        Assert.Equal(new[] { "gpu on CUDA0", "gpu on Vulkan0" }, KimodoDevicePlanner.Plan("gpu", null, devices).Select(a => a.ToString()));
        Assert.Equal(new[] { "hybrid on CUDA0", "hybrid on Vulkan0" }, KimodoDevicePlanner.Plan("hybrid", null, devices).Select(a => a.ToString()));
        Assert.Equal(new[] { "cpu" }, KimodoDevicePlanner.Plan("cpu", null, devices).Select(a => a.ToString()));
        // A chosen card is the only one tried; Auto still ends on the processor.
        Assert.Equal(new[] { "gpu on Vulkan1", "hybrid on Vulkan1", "cpu" }, KimodoDevicePlanner.Plan("auto", "vulkan1", devices).Select(a => a.ToString()));
        // Only an integrated GPU: it is still tried before the processor.
        Assert.Equal(new[] { "gpu on Vulkan1", "hybrid on Vulkan1", "cpu" },
            KimodoDevicePlanner.Plan("auto", null, new[] { D("Vulkan1", "igpu", "Vulkan"), D("CPU", "cpu", "CPU") }).Select(a => a.ToString()));
    }

    [Fact]
    public void Plan_Errors_And_Old_Runners()
    {
        var cpuOnly = new[] { D("CPU", "cpu", "CPU") };
        Assert.Equal(new[] { "cpu" }, KimodoDevicePlanner.Plan("auto", null, cpuOnly).Select(a => a.ToString()));
        Assert.Equal("device_unavailable", Assert.Throws<MotionGenerationException>(() => KimodoDevicePlanner.Plan("gpu", null, cpuOnly)).Code);
        Assert.Equal("device_unavailable", Assert.Throws<MotionGenerationException>(() => KimodoDevicePlanner.Plan("hybrid", null, cpuOnly)).Code);
        var e = Assert.Throws<MotionGenerationException>(() => KimodoDevicePlanner.Plan("gpu", "CUDA3", new[] { D("Vulkan0", "gpu", "Vulkan") }));
        Assert.Contains("Vulkan0", e.Message);
        // A 0.2 runner lists no devices: its own device values.
        Assert.Equal("auto", KimodoDevicePlanner.Plan("auto", null, null).Single().Device);
        Assert.Equal("vulkan", KimodoDevicePlanner.Plan("gpu", null, null).Single().Device);
        Assert.Equal("cpu", KimodoDevicePlanner.Plan("cpu", null, null).Single().Device);
        Assert.Equal("device_unavailable", Assert.Throws<MotionGenerationException>(() => KimodoDevicePlanner.Plan("hybrid", null, null)).Code);
        Assert.True(KimodoDevicePlanner.ShouldFallBack("runner_crashed"));
        Assert.False(KimodoDevicePlanner.ShouldFallBack("cancelled"));
        Assert.False(KimodoDevicePlanner.ShouldFallBack("runner_timeout"));
        Assert.False(KimodoDevicePlanner.ShouldFallBack("models_not_found"));
    }

    [Fact]
    public void Settings_Device_Values()
    {
        Assert.Equal("gpu", new KimodoSettings { Device = "Vulkan" }.Device); // 0.10 settings keep working
        Assert.Equal("auto", new KimodoSettings { Device = "" }.Device);
        Assert.Equal("hybrid", new KimodoSettings { Device = " HYBRID " }.Device);
        var bad = FakeRunner.Settings() with { Device = "turbo" };
        Assert.Contains("auto, gpu, cpu or hybrid", bad.MissingReason());
        Assert.Null((FakeRunner.Settings() with { Device = "hybrid", GpuDevice = "CUDA0" }).MissingReason());
    }

    [Fact]
    public async Task Auto_Uses_Cuda_And_Reports_It()
    {
        using var k = new KimodoProvider(With("cuda,vulkan"));
        var r = await k.GenerateAsync(Req(), null, CancellationToken.None);
        Assert.Equal("gpu", r.Metadata["kimodo.device"]);
        Assert.Equal("NVIDIA GeForce RTX 4070 (CUDA)", r.Metadata["kimodo.generatedOn"]);
        Assert.Equal("NVIDIA GeForce RTX 4070 (CUDA)", r.Metadata["kimodo.textDevice"]);
        Assert.Equal("CUDA0", k.LastDeviceUse!.Motion!.Name);
        Assert.Equal(new[] { "CUDA0", "Vulkan0", "CPU" }, k.RunnerDevices!.Select(d => d.Name));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("failed attempt"));
    }

    [Fact]
    public async Task Auto_Falls_Back_Cuda_Oom_Then_Vulkan_Crash_Then_Hybrid_And_Says_So()
    {
        using var k = new KimodoProvider(With("cuda,vulkan", "gpu:CUDA0=oom,gpu:Vulkan0=crash"));
        var progress = new List<GenerationProgress>();
        var r = await k.GenerateAsync(Req(), new SyncProgress(progress.Add), CancellationToken.None);
        Assert.Equal("hybrid", r.Metadata["kimodo.device"]);
        Assert.Equal("NVIDIA GeForce RTX 4070 (CUDA) + processor (text encoder)", r.Metadata["kimodo.generatedOn"]);
        Assert.Equal("Fake CPU (CPU)", r.Metadata["kimodo.textDevice"]);
        var w = Assert.Single(r.Warnings, x => x.Contains("failed attempt"));
        Assert.Contains("2 failed attempt", w);
        Assert.Contains("out of device memory", w);
        Assert.Contains("runner_crashed", w);
        Assert.Contains(progress, p => p.Stage == "fallback");
        Assert.True(k.RunnerRunning); // a fresh runner after the crash
    }

    [Fact]
    public async Task Auto_Ends_On_The_Cpu_When_Every_Gpu_Path_Fails()
    {
        using var k = new KimodoProvider(With("vulkan", "gpu:Vulkan0=load,hybrid:Vulkan0=oom"));
        var r = await k.GenerateAsync(Req(), null, CancellationToken.None);
        Assert.Equal("cpu", r.Metadata["kimodo.device"]);
        Assert.False(k.LastDeviceUse!.UsedGpu);
        Assert.StartsWith("processor", r.Metadata["kimodo.generatedOn"]);
    }

    [Fact]
    public async Task Gpu_Only_Never_Uses_The_Processor()
    {
        using var k = new KimodoProvider(With("cuda,vulkan", "gpu:CUDA0=oom,gpu:Vulkan0=oom", device: "gpu"));
        var e = await Assert.ThrowsAsync<MotionGenerationException>(() => k.GenerateAsync(Req(), null, CancellationToken.None));
        Assert.Equal("generation_failed", e.Code);

        using var none = new KimodoProvider(With("", device: "gpu"));
        var e2 = await Assert.ThrowsAsync<MotionGenerationException>(() => none.GenerateAsync(Req(), null, CancellationToken.None));
        Assert.Equal("device_unavailable", e2.Code);
        Assert.Contains("Processor only", e2.Message);
    }

    [Fact]
    public async Task Cpu_Hybrid_And_A_Chosen_Gpu()
    {
        using var cpu = new KimodoProvider(With("cuda", device: "cpu"));
        Assert.Equal("cpu", (await cpu.GenerateAsync(Req(), null, CancellationToken.None)).Metadata["kimodo.device"]);

        using var hybrid = new KimodoProvider(With("cuda,vulkan", device: "hybrid", gpu: "Vulkan0"));
        var r = await hybrid.GenerateAsync(Req(), null, CancellationToken.None);
        Assert.Equal("hybrid", r.Metadata["kimodo.device"]);
        Assert.Equal("NVIDIA GeForce RTX 4070 (Vulkan)", r.Metadata["kimodo.motionDevice"]);
    }

    [Fact]
    public async Task Probe_Lists_Devices_And_Errors_Are_Not_Retried()
    {
        using var k = new KimodoProvider(With("cuda,igpu"));
        var list = await k.ProbeDevicesAsync(CancellationToken.None);
        Assert.Equal(new[] { "gpu", "igpu", "cpu" }, list!.Select(d => d.Type));
        // A bad request is not retried on another device.
        var e = await Assert.ThrowsAsync<MotionGenerationException>(() => k.GenerateAsync(Req("fail"), null, CancellationToken.None));
        Assert.Equal("generation_failed", e.Code); // "fail" fails everywhere: the last device's error is reported
        using var old = new KimodoProvider(FakeRunner.Settings() with { Device = "hybrid" });
        Assert.Null(await old.ProbeDevicesAsync(CancellationToken.None)); // a 0.2 runner lists none
        Assert.Equal("device_unavailable", (await Assert.ThrowsAsync<MotionGenerationException>(() => old.GenerateAsync(Req(), null, CancellationToken.None))).Code);
    }

    [Fact]
    public void Advisor_Never_Forces_The_Cpu_On_A_Discrete_Card()
    {
        static HardwareReport Pc(GpuInfo g) => new("test", "X64", 8, 32L * HardwareReport.GiB, new[] { g }, Array.Empty<string>());
        // VRAM unreadable (some drivers): automatic, not "processor only" (the 0.10 behaviour that kept Kimodo on the CPU).
        var unknown = HardwareAdvisor.Recommend(Pc(new GpuInfo("NVIDIA GeForce RTX 4070", "NVIDIA", null, "test", false)), ModelCatalog.Default);
        Assert.All(unknown.Tiers, t => Assert.Equal("auto", t.Device));
        // Too little VRAM for the text encoder: graphics card + processor.
        var small = HardwareAdvisor.Recommend(Pc(new GpuInfo("NVIDIA GeForce GT 1030", "NVIDIA", 2L * HardwareReport.GiB, "test", false)), ModelCatalog.Default);
        Assert.Contains(small.Tiers, t => t.Device == "hybrid");
    }

    sealed class SyncProgress(Action<GenerationProgress> report) : IProgress<GenerationProgress>
    {
        public void Report(GenerationProgress value) { lock (this) report(value); }
    }
}
