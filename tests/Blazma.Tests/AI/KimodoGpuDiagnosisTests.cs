using System.Text;
using Blazma.AI;

namespace Blazma.Tests.AI;

/// <summary>Why kimodo-runner offers no graphics card (1.0.1): the PE import reader and the file checks behind it, on small
/// PE32+ files written by the test (no Windows needed).</summary>
public sealed class KimodoGpuDiagnosisTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "blazma-gpudiag-" + Guid.NewGuid().ToString("N"));
    string Runner => Path.Combine(_root, "kimodo");
    string System32 => Path.Combine(_root, "System32");

    public KimodoGpuDiagnosisTests()
    {
        Directory.CreateDirectory(Runner);
        Directory.CreateDirectory(System32);
        File.WriteAllBytes(Path.Combine(Runner, "kimodo-runner.exe"), Pe(new() { ["kimodo.dll"] = new[] { "kimodo_version" } }));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    /// <summary>A minimal PE32+ image with one section holding the export directory and the import table.</summary>
    internal static byte[] Pe(Dictionary<string, string[]> imports, string[]? exports = null)
    {
        const uint va = 0x1000, raw = 0x400;
        var sec = new List<byte>();
        uint Rva() => va + (uint)sec.Count;
        void U16(int v) => sec.AddRange(BitConverter.GetBytes((ushort)v));
        void U32(uint v) => sec.AddRange(BitConverter.GetBytes(v));
        uint Str(string s) { var r = Rva(); sec.AddRange(Encoding.ASCII.GetBytes(s)); sec.Add(0); if (sec.Count % 2 == 1) sec.Add(0); return r; }

        uint exportRva = 0, importRva = 0;
        if (exports is { Length: > 0 })
        {
            var names = exports.Select(Str).ToList();
            var namesRva = Rva();
            foreach (var n in names) U32(n);
            exportRva = Rva();
            for (var i = 0; i < 6; i++) U32(0);
            U32((uint)exports.Length); // +24 NumberOfNames
            U32(0);
            U32(namesRva); // +32 AddressOfNames
            U32(0);
        }
        if (imports.Count > 0)
        {
            var thunks = new List<uint>();
            var dllNames = new List<uint>();
            foreach (var (dll, fns) in imports)
            {
                dllNames.Add(Str(dll));
                var hints = fns.Select(f => { var r = Rva(); U16(0); Str(f); return r; }).ToList();
                while (sec.Count % 8 != 0) sec.Add(0);
                thunks.Add(Rva());
                foreach (var h in hints) { U32(h); U32(0); }
                U32(0); U32(0);
            }
            importRva = Rva();
            for (var i = 0; i < imports.Count; i++) { U32(thunks[i]); U32(0); U32(0); U32(dllNames[i]); U32(thunks[i]); }
            for (var i = 0; i < 5; i++) U32(0);
        }
        while (sec.Count % 0x200 != 0) sec.Add(0);

        var f = new byte[raw + sec.Count];
        f[0] = (byte)'M'; f[1] = (byte)'Z';
        BitConverter.GetBytes(0x80u).CopyTo(f, 0x3C);
        var pe = 0x80;
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(f, pe);
        BitConverter.GetBytes((ushort)0x8664).CopyTo(f, pe + 4);
        BitConverter.GetBytes((ushort)1).CopyTo(f, pe + 6);
        const int optSize = 240;
        BitConverter.GetBytes((ushort)optSize).CopyTo(f, pe + 20);
        var opt = pe + 24;
        BitConverter.GetBytes((ushort)0x20b).CopyTo(f, opt);
        BitConverter.GetBytes(16u).CopyTo(f, opt + 108);
        BitConverter.GetBytes(exportRva).CopyTo(f, opt + 112);
        BitConverter.GetBytes(importRva).CopyTo(f, opt + 120);
        var sh = opt + optSize;
        Encoding.ASCII.GetBytes(".rdata").CopyTo(f, sh);
        BitConverter.GetBytes((uint)sec.Count).CopyTo(f, sh + 8);
        BitConverter.GetBytes(va).CopyTo(f, sh + 12);
        BitConverter.GetBytes((uint)sec.Count).CopyTo(f, sh + 16);
        BitConverter.GetBytes(raw).CopyTo(f, sh + 20);
        sec.ToArray().CopyTo(f, raw);
        return f;
    }

    void Put(string dir, string name, Dictionary<string, string[]>? imports = null, string[]? exports = null) =>
        File.WriteAllBytes(Path.Combine(dir, name), Pe(imports ?? new(), exports));

    static readonly Dictionary<string, string[]> VulkanImports = new()
    {
        ["ggml-base.dll"] = new[] { "ggml_init" },
        ["vulkan-1.dll"] = new[] { "vkCreateInstance" },
        ["MSVCP140.dll"] = new[] { "?_Xlength_error@std@@YAXPEBD@Z", "_Thrd_yield" },
        ["VCRUNTIME140.dll"] = new[] { "memcpy" },
        ["api-ms-win-crt-runtime-l1-1-0.dll"] = new[] { "_initterm" },
        ["KERNEL32.dll"] = new[] { "LoadLibraryW" },
    };

    void Kernel() => Put(System32, "KERNEL32.dll", exports: new[] { "LoadLibraryW" });

    [Fact]
    public void PeFile_Reads_Imports_And_Exports()
    {
        var p = Path.Combine(_root, "x.dll");
        File.WriteAllBytes(p, Pe(VulkanImports, new[] { "ggml_backend_init", "ggml_backend_score" }));
        var imports = PeFile.Imports(p);
        Assert.Equal(VulkanImports.Keys, imports.Select(i => i.Library));
        Assert.Equal(new[] { "?_Xlength_error@std@@YAXPEBD@Z", "_Thrd_yield" }, imports.Single(i => i.Library == "MSVCP140.dll").Functions);
        Assert.Equal(new[] { "ggml_backend_init", "ggml_backend_score" }, PeFile.Exports(p).Order());
        File.WriteAllText(Path.Combine(_root, "text.dll"), "not a library");
        Assert.Throws<InvalidDataException>(() => PeFile.Imports(Path.Combine(_root, "text.dll")));
    }

    [Fact]
    public void Missing_Vulkan_Loader_And_Missing_Runtime_Are_Named()
    {
        Kernel();
        Put(Runner, "ggml-vulkan.dll", VulkanImports);
        Put(Runner, "ggml-base.dll", new() { ["VCRUNTIME140.dll"] = new[] { "memcpy" } });
        var problems = KimodoGpuDiagnosis.Diagnose(Path.Combine(Runner, "kimodo-runner.exe"), null, new[] { "CPU" }, System32, true);
        Assert.Contains(problems, p => p is { Code: "no_vulkan_loader", File: "ggml-vulkan.dll", WindowsError: 126 });
        Assert.Contains(problems, p => p is { Code: "missing_dependency", Dependency: "MSVCP140.dll" });
        Assert.Contains(problems, p => p is { Code: "missing_dependency", Dependency: "VCRUNTIME140.dll" });
        Assert.All(problems, p => Assert.Equal("Vulkan", p.Backend));
    }

    [Fact]
    public void An_Old_System_Runtime_Is_Named_And_The_App_Local_One_Wins()
    {
        Kernel();
        Put(System32, "vulkan-1.dll", exports: new[] { "vkCreateInstance" });
        Put(Runner, "ggml-vulkan.dll", VulkanImports);
        Put(Runner, "ggml-base.dll");
        Put(System32, "VCRUNTIME140.dll", exports: new[] { "memcpy" });
        Put(System32, "MSVCP140.dll", exports: new[] { "?_Xlength_error@std@@YAXPEBD@Z" }); // 14.2x: no _Thrd_yield
        var runner = Path.Combine(Runner, "kimodo-runner.exe");
        var old = Assert.Single(KimodoGpuDiagnosis.Diagnose(runner, null, new[] { "CPU" }, System32, true));
        Assert.Equal(("missing_function", "MSVCP140.dll", "_Thrd_yield", 127), (old.Code, old.Dependency, old.Function, old.WindowsError));
        Assert.Equal(System32, old.Location);

        // The runtime shipped next to kimodo-runner (DECISIONS D49) is found first: the files are fine, the driver offers no device.
        Put(Runner, "MSVCP140.dll", exports: new[] { "?_Xlength_error@std@@YAXPEBD@Z", "_Thrd_yield" });
        var ok = Assert.Single(KimodoGpuDiagnosis.Diagnose(runner, null, new[] { "CPU" }, System32, true));
        Assert.Equal("no_vulkan_device", ok.Code);
        // GGML registered Vulkan but listed no device: the same.
        Assert.Equal("no_vulkan_device", Assert.Single(KimodoGpuDiagnosis.Diagnose(runner, null, new[] { "Vulkan", "CPU" }, System32, true)).Code);
    }

    [Fact]
    public void Cuda_Without_The_Nvidia_Driver_And_A_Missing_Backend()
    {
        Kernel();
        var cudaDir = Path.Combine(_root, "cuda");
        Directory.CreateDirectory(cudaDir);
        Put(cudaDir, "ggml-cuda.dll", new() { ["nvcuda.dll"] = new[] { "cuInit" }, ["cudart64_12.dll"] = new[] { "cudaMalloc" } });
        Put(cudaDir, "cudart64_12.dll");
        var problems = KimodoGpuDiagnosis.Diagnose(Path.Combine(Runner, "kimodo-runner.exe"), Path.Combine(cudaDir, "ggml-cuda.dll"), new[] { "CPU" }, System32, true);
        Assert.Contains(problems, p => p is { Backend: "Vulkan", Code: "backend_missing", File: "ggml-vulkan.dll" });
        Assert.Contains(problems, p => p is { Backend: "CUDA", Code: "no_nvidia_driver" });
        Assert.DoesNotContain(problems, p => p.Dependency == "cudart64_12.dll"); // found in the CUDA folder (on PATH for the runner)
    }

    [Fact]
    public void Messages_Read_Back_For_Translation()
    {
        var all = new[]
        {
            new GpuProblem("Vulkan", "backend_missing", "ggml-vulkan.dll"),
            new GpuProblem("Vulkan", "missing_dependency", "ggml-vulkan.dll", "MSVCP140.dll"),
            new GpuProblem("Vulkan", "missing_function", "ggml-vulkan.dll", "MSVCP140.dll", @"C:\Windows\System32", "_Thrd_yield"),
            new GpuProblem("Vulkan", "no_vulkan_loader", "ggml-vulkan.dll", "vulkan-1.dll"),
            new GpuProblem("CUDA", "no_nvidia_driver", "ggml-cuda.dll", "nvcuda.dll"),
            new GpuProblem("Vulkan", "no_vulkan_device"),
            new GpuProblem("CUDA", "no_cuda_device"),
        };
        var w = KimodoGpuDiagnosis.Warning(all);
        Assert.StartsWith(KimodoGpuDiagnosis.NoGpuWarning, w);
        var back = KimodoGpuDiagnosis.ParseWarning(w);
        Assert.Equal(all.Select(p => p.Code), back.Select(p => p.Code));
        Assert.Equal(@"C:\Windows\System32", back[2].Location);
        Assert.Equal("_Thrd_yield", back[2].Function);
        Assert.Equal("MSVCP140.dll", back[1].Dependency);
        Assert.Empty(KimodoGpuDiagnosis.ParseWarning(KimodoGpuDiagnosis.NoGpuWarning));
        Assert.Equal("ggml-vulkan.dll", KimodoGpuDiagnosis.ParseWarning("No graphics card." + KimodoGpuDiagnosis.Describe(all.Take(1).ToList()))[0].File);
    }

    [Fact]
    public void Linux_Only_Checks_The_Library()
    {
        var runner = Path.Combine(Runner, "kimodo-runner");
        Assert.Equal("backend_missing", Assert.Single(KimodoGpuDiagnosis.Diagnose(runner, null, null, null, false)).Code);
        File.WriteAllText(Path.Combine(Runner, "libggml-vulkan.so"), "");
        Assert.Equal("no_vulkan_device", Assert.Single(KimodoGpuDiagnosis.Diagnose(runner, null, null, null, false)).Code);
    }
}
