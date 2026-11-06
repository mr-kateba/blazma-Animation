using Blazma.AI;

namespace Blazma.Tests.AI;

public class HardwareAndCatalogTests
{
    sealed class FakeRegistry(Dictionary<string, Dictionary<string, object?>> keys) : IDisplayAdapterRegistry
    {
        public IEnumerable<string> AdapterKeys() => keys.Keys;
        public object? Value(string adapterKey, string name) => keys[adapterKey].GetValueOrDefault(name);
    }

    [Fact]
    public void Windows_Registry_Adapters_AreParsed_AndSoftwareAdaptersSkipped()
    {
        var reg = new FakeRegistry(new()
        {
            ["0000"] = new() { ["DriverDesc"] = "Intel(R) UHD Graphics 770", ["ProviderName"] = "Intel Corporation",
                ["MatchingDeviceId"] = @"PCI\VEN_8086&DEV_4680", ["HardwareInformation.MemorySize"] = new byte[] { 0, 0, 0, 0x08 } }, // 128 MiB binary
            ["0001"] = new() { ["DriverDesc"] = "NVIDIA GeForce RTX 3060", ["ProviderName"] = "NVIDIA",
                ["MatchingDeviceId"] = @"PCI\VEN_10DE&DEV_2504&SUBSYS_1", ["HardwareInformation.qwMemorySize"] = 12L * 1024 * 1024 * 1024,
                ["HardwareInformation.MemorySize"] = unchecked((int)0xFFF00000) },
            ["0002"] = new() { ["DriverDesc"] = "Microsoft Basic Display Adapter", ["MatchingDeviceId"] = @"PCI\CC_0300" },
            ["0003"] = new() { ["DriverDesc"] = "Parsec Virtual Display Adapter", ["MatchingDeviceId"] = @"root\parsec\vdd" },
            ["0004"] = new() { ["DriverDesc"] = "AMD Radeon RX 6700 XT", ["MatchingDeviceId"] = @"pci\ven_1002&dev_73df",
                ["HardwareInformation.MemorySize"] = unchecked((int)0xFFFFFFFF) }, // DWORD stops at 4 GiB, read unsigned
            ["Configuration"] = new() { ["DriverDesc"] = "not an adapter" },
        });
        var gpus = HardwareProbe.ReadWindowsAdapters(reg);
        Assert.Equal(new[] { "Intel(R) UHD Graphics 770", "NVIDIA GeForce RTX 3060", "AMD Radeon RX 6700 XT" }, gpus.Select(g => g.Name));
        Assert.Equal(new[] { "Intel", "NVIDIA", "AMD" }, gpus.Select(g => g.Vendor));
        Assert.Equal(128L * 1024 * 1024, gpus[0].VramBytes);
        Assert.Equal(12L * 1024 * 1024 * 1024, gpus[1].VramBytes); // QWORD wins over the DWORD
        Assert.Equal(0xFFFFFFFFL, gpus[2].VramBytes);
        Assert.Equal(new bool?[] { true, false, false }, gpus.Select(g => g.Integrated));
        Assert.Equal("NVIDIA GeForce RTX 3060", HardwareProbe.Best(gpus)!.Name);
    }

    [Fact]
    public void Vendor_From_ProviderName_When_MatchingDeviceId_Is_Missing()
    {
        var reg = new FakeRegistry(new() { ["0000"] = new() { ["DriverDesc"] = "Radeon RX 7900 XTX", ["ProviderName"] = "Advanced Micro Devices, Inc." } });
        Assert.Equal("AMD", HardwareProbe.ReadWindowsAdapters(reg).Single().Vendor);
    }

    [Fact]
    public void NvidiaSmi_Output_IsParsed_And_Replaces_Driver_Entries()
    {
        var smi = HardwareProbe.ParseNvidiaSmi("NVIDIA GeForce RTX 4090, 24564\r\nNVIDIA GeForce GTX 1050, 2048\n\ngarbage\n");
        Assert.Equal(2, smi.Count);
        Assert.Equal(24564L * 1024 * 1024, smi[0].VramBytes);
        var merged = HardwareProbe.MergeNvidiaSmi(new[]
        {
            new GpuInfo("Intel UHD", "Intel", null, "registry", true),
            new GpuInfo("NVIDIA GeForce RTX 4090", "NVIDIA", 4L << 30, "registry", false),
        }, smi);
        Assert.Equal(3, merged.Count);
        Assert.Equal("NVIDIA GeForce RTX 4090", HardwareProbe.Best(merged)!.Name);
        Assert.Equal("nvidia-smi", HardwareProbe.Best(merged)!.Source);
    }

    [Theory]
    [InlineData("Intel", "Intel(R) Arc(TM) A770 Graphics", false)]
    [InlineData("Intel", "Intel(R) Arc(TM) Graphics", true)]
    [InlineData("Intel", "Intel(R) Iris(R) Xe Graphics", true)]
    [InlineData("AMD", "AMD Radeon(TM) Graphics", true)]
    [InlineData("AMD", "AMD Radeon RX 7600", false)]
    [InlineData("NVIDIA", "NVIDIA GeForce MX450", false)]
    public void Integrated_Is_Guessed_From_The_Name(string vendor, string name, bool integrated) =>
        Assert.Equal(integrated, HardwareProbe.GuessIntegrated(vendor, name));

    [Fact]
    public void Probe_Runs_On_This_Machine()
    {
        var r = HardwareProbe.Probe(refresh: true);
        Assert.True(r.LogicalCpus > 0);
        Assert.NotEmpty(r.Notes);
    }

    [Fact]
    public void Catalog_Is_Embedded_And_Consistent()
    {
        var c = ModelCatalog.Default;
        Assert.Equal(new[] { "light", "standard", "reference" }, c.Tiers.Select(t => t.Id));
        foreach (var t in c.Tiers)
        {
            Assert.Contains(t.Files, f => f.FileName == KimodoSettings.MotionModels[t.MotionModel]);
            Assert.Contains(t.Files, f => f.FileName == KimodoSettings.TokenizerFile);
            Assert.Contains(t.TextFile.FileName, KimodoSettings.TextModels);
            Assert.All(t.Files, f => Assert.Equal("huggingface.co", f.Url.Host));
            Assert.All(t.Files, f => Assert.Matches("/resolve/[0-9a-f]{40}/", f.Url.AbsolutePath));
            Assert.Equal(new[] { "nvidia-open-model", "meta-llama-3" }, t.LicenceIds.Order(StringComparer.Ordinal).Reverse());
        }
        Assert.True(c.Tiers[0].TotalBytes < c.Tiers[1].TotalBytes && c.Tiers[1].TotalBytes < c.Tiers[2].TotalBytes);
        Assert.Equal(1133166784L + 5058327904 + 7295054 + 7801 + 148 + 163, c.Tiers[0].TotalBytes);
        Assert.False(c.Runner.Downloadable);
        Assert.Equal("https://huggingface.co/LocalAI-io/Kimodo-SOMA-RP-v1.1-GGML/resolve/65f8b8ab34ee7524161cc87b20ae4cb4f5d3a12d/models/kimodo-soma-rp-v1.1-f32.gguf",
            c.Tiers[0].Files[0].Url.ToString());
    }

    static HardwareReport Machine(double ramGiB, params GpuInfo[] gpus) =>
        new("test", "X64", 8, (long)(ramGiB * HardwareReport.GiB), gpus, Array.Empty<string>());

    static GpuInfo Card(string name, double vramGiB, bool integrated = false) =>
        new(name, name.StartsWith("Intel", StringComparison.Ordinal) ? "Intel" : name.StartsWith("AMD", StringComparison.Ordinal) ? "AMD" : "NVIDIA",
            (long)(vramGiB * HardwareReport.GiB), "test", integrated);

    [Fact]
    public void Advisor_Weak_PC_Gets_Light_On_Cpu()
    {
        var r = HardwareAdvisor.Recommend(Machine(8, Card("Intel UHD 620", 0.125, integrated: true)), ModelCatalog.Default);
        Assert.Equal("light", r.TierId);
        Assert.Equal("cpu", r.Device);
        Assert.True(r.Tiers.Single(t => t.TierId == "light").Fits);
        Assert.False(r.Tiers.Single(t => t.TierId == "reference").Fits);
        Assert.Contains("CPU", r.ReasonEn);
        Assert.Contains("المعالج", r.ReasonAr);
    }

    [Fact]
    public void Advisor_Strong_Gpu_Gets_Standard_On_Gpu_And_Very_Strong_Gets_Reference()
    {
        var mid = HardwareAdvisor.Recommend(Machine(16, Card("Intel UHD 770", 0.125, true), Card("NVIDIA GeForce RTX 3060", 12)), ModelCatalog.Default);
        Assert.Equal("standard", mid.TierId);
        Assert.Equal("auto", mid.Device);
        Assert.Equal("NVIDIA GeForce RTX 3060", mid.Gpu!.Name);

        var big = HardwareAdvisor.Recommend(Machine(64, Card("NVIDIA GeForce RTX 4090", 24)), ModelCatalog.Default);
        Assert.Equal("reference", big.TierId);

        // Lots of RAM but no discrete GPU: the light tier on the CPU, the bigger ones still marked as fitting (CPU).
        var cpu = HardwareAdvisor.Recommend(Machine(32), ModelCatalog.Default);
        Assert.Equal("light", cpu.TierId);
        Assert.True(cpu.Tiers.All(t => t.Fits && !t.GpuFits));
    }

    [Fact]
    public void Advisor_Too_Little_Ram_Says_So()
    {
        var r = HardwareAdvisor.Recommend(Machine(4), ModelCatalog.Default);
        Assert.Equal("light", r.TierId);
        Assert.All(r.Tiers, t => Assert.False(t.Fits));
        Assert.Contains("smallest", r.ReasonEn);
    }

    [Fact]
    public void State_Checks_Names_And_Sizes()
    {
        var dir = Directory.CreateTempSubdirectory("blazma-models-").FullName;
        try
        {
            var f1 = new ModelFile("a", "r", new string('0', 40), "a.bin", "a.bin", 3, new string('a', 64), "x");
            var f2 = new ModelFile("b", "r", new string('0', 40), "b.bin", "b.bin", 2, new string('b', 64), "x");
            var tier = new ModelTier("t", "T", "T", "soma-rp-v1.1", f2, new[] { f1, f2 }, 1, 1, 1, 1, false, "", "");
            File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[3]);
            File.WriteAllBytes(Path.Combine(dir, "b.bin"), new byte[5]);
            var s = ModelCatalog.State(tier, dir);
            Assert.False(s.Installed);
            Assert.Equal(new[] { "b.bin" }, s.MissingFiles);
            File.WriteAllBytes(Path.Combine(dir, "b.bin"), new byte[2]);
            Assert.True(ModelCatalog.State(tier, dir).Installed);
            Assert.False(ModelCatalog.State(tier, null).Installed);
        }
        finally { Directory.Delete(dir, true); }
    }
}
