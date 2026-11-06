using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Blazma.AI;

/// <summary>One graphics adapter. <paramref name="Integrated"/> is true for a GPU that shares system memory (Intel UHD/Iris,
/// AMD "Radeon Graphics" APUs), false for a discrete card, null when it cannot be told.</summary>
public sealed record GpuInfo(string Name, string Vendor, long? VramBytes, string Source, bool? Integrated = null);

/// <summary>What the machine has, best effort and offline. Missing values are null, never guessed.</summary>
public sealed record HardwareReport(
    string Os,
    string Architecture,
    int LogicalCpus,
    long? TotalRamBytes,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<string> Notes)
{
    public static long GiB => 1024L * 1024 * 1024;

    /// <summary>The GPU Kimodo would best run on: discrete before unknown before integrated, then the most VRAM.</summary>
    public GpuInfo? BestGpu => HardwareProbe.Best(Gpus);
}

/// <summary>Read access to the Windows display-adapter class key, abstracted so the parsing is testable on any OS.</summary>
public interface IDisplayAdapterRegistry
{
    /// <summary>Sub-key names under the display class key (adapter instances are "0000", "0001", …).</summary>
    IEnumerable<string> AdapterKeys();

    /// <summary>A value of one adapter key as the registry returns it (string, int, long, byte[]), or null.</summary>
    object? Value(string adapterKey, string name);
}

/// <summary>
/// Reads hardware facts from local sources only (no network): RAM from the .NET runtime; GPUs on Windows from the display
/// adapter class key in the registry, on Linux from /sys/class/drm and /proc/driver/nvidia. When <c>nvidia-smi</c> is on
/// PATH (installed with the NVIDIA driver) it is run once, with a 5 s timeout, for the exact VRAM of NVIDIA cards.
/// </summary>
public static class HardwareProbe
{
    /// <summary>HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-…} = the "Display adapters" device class.</summary>
    public const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    static HardwareReport? _cached;
    static readonly object Gate = new();

    public static HardwareReport Probe(bool refresh = false)
    {
        lock (Gate)
        {
            if (!refresh && _cached is not null) return _cached;
            _cached = ProbeNow();
            return _cached;
        }
    }

    static HardwareReport ProbeNow()
    {
        var notes = new List<string>();
        long? ram = null;
        try
        {
            var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (total > 0) ram = total;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { }

        var gpus = new List<GpuInfo>();
        var smi = NvidiaSmi(notes);
        if (OperatingSystem.IsWindows())
        {
            try { gpus.AddRange(ReadWindowsAdapters(new WindowsDisplayRegistry())); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { notes.Add("The display adapters could not be read from the registry: " + e.Message); }
        }
        else if (OperatingSystem.IsLinux())
        {
            try { gpus.AddRange(LinuxGpus()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { notes.Add("GPU information could not be read: " + e.Message); }
        }
        else notes.Add("GPU detection is implemented on Windows and Linux; Kimodo picks a device itself (Vulkan if available, else CPU).");
        gpus = MergeNvidiaSmi(gpus, smi);
        if (gpus.Count == 0 && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
            notes.Add("No graphics adapter was found (CPU generation only, or a driver that does not report itself).");
        notes.Add("RAM is the memory available to this process (may be lower than installed RAM in containers).");

        return new HardwareReport(RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount, ram, gpus, notes);
    }

    // ------------------------------------------------------------------ choosing

    public static GpuInfo? Best(IEnumerable<GpuInfo> gpus) =>
        gpus.OrderBy(g => g.Integrated switch { false => 0, null => 1, true => 2 })
            .ThenByDescending(g => g.VramBytes ?? -1)
            .FirstOrDefault();

    public static string VendorFromPciId(string? id) => (id ?? "").ToLowerInvariant().TrimStart('0', 'x') switch
    {
        "10de" => "NVIDIA",
        "1002" or "1022" => "AMD",
        "8086" => "Intel",
        "5143" or "17cb" => "Qualcomm",
        "1414" => "Microsoft",
        "15ad" => "VMware",
        "80ee" => "VirtualBox",
        "1af4" or "1b36" => "Red Hat (virtual)",
        var v => v.Length > 0 ? "PCI " + v : "Unknown",
    };

    /// <summary>Integrated or discrete from the vendor and the marketing name (null when the name does not say).</summary>
    public static bool? GuessIntegrated(string vendor, string name)
    {
        var n = name.ToLowerInvariant();
        switch (vendor)
        {
            case "NVIDIA": return false;
            case "Intel":
                // Arc A/B-series cards are discrete; "Intel(R) Arc(TM) Graphics" (Meteor Lake and later) is integrated.
                if (System.Text.RegularExpressions.Regex.IsMatch(n, @"arc\(tm\)\s*[ab]\d|arc\s+[ab]\d")) return false;
                return true;
            case "AMD":
                if (n.Contains("rx ") || n.Contains("radeon pro") || n.Contains("firepro") || n.Contains("instinct")) return false;
                if (n.Contains("radeon(tm) graphics") || n.Contains("radeon graphics") || n.Contains("vega") && n.Contains("graphics")
                    || n.Contains("radeon(tm) 7") || n.Contains("radeon 7") || n.Contains("radeon(tm) 8") || n.Contains("radeon 8")) return true;
                return null;
            case "Qualcomm": return true;
            default: return null;
        }
    }

    // ------------------------------------------------------------------ Windows

    /// <summary>Adapters from the display class key, skipping software adapters (Microsoft Basic Display/Render, remote and
    /// virtual displays). VRAM: HardwareInformation.qwMemorySize (QWORD), else HardwareInformation.MemorySize (DWORD or binary).</summary>
    public static IReadOnlyList<GpuInfo> ReadWindowsAdapters(IDisplayAdapterRegistry reg)
    {
        var list = new List<GpuInfo>();
        foreach (var key in reg.AdapterKeys().Where(k => k.Length == 4 && k.All(char.IsAsciiDigit)).Order(StringComparer.Ordinal))
        {
            var name = (reg.Value(key, "DriverDesc") as string)?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            var matching = ((reg.Value(key, "MatchingDeviceId") as string) ?? "").Trim().ToLowerInvariant();
            if (IsSoftwareAdapter(name, matching)) continue;
            var vendor = VendorFromMatchingId(matching);
            if (vendor == "Unknown" && reg.Value(key, "ProviderName") is string provider)
                vendor = provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
                    : provider.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) || provider.Contains("AMD", StringComparison.Ordinal) ? "AMD"
                    : provider.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "Unknown";
            var vram = MemoryValue(reg.Value(key, "HardwareInformation.qwMemorySize")) ?? MemoryValue(reg.Value(key, "HardwareInformation.MemorySize"));
            list.Add(new GpuInfo(name, vendor, vram, "registry", GuessIntegrated(vendor, name)));
        }
        return list;
    }

    static readonly string[] SoftwareNames =
    {
        "microsoft basic display", "microsoft basic render", "microsoft remote display", "remote display adapter",
        "microsoft hyper-v video", "citrix indirect display", "parsec virtual display", "virtual display adapter", "idd",
    };

    internal static bool IsSoftwareAdapter(string name, string matchingDeviceId)
    {
        var n = name.ToLowerInvariant();
        if (SoftwareNames.Any(s => s == "idd" ? n.Split(' ').Contains("idd") : n.Contains(s))) return true;
        // Software-enumerated and root-enumerated devices are virtual (remote desktop, screen-sharing, indirect displays).
        return matchingDeviceId.StartsWith("root\\", StringComparison.Ordinal) || matchingDeviceId.StartsWith("swd\\", StringComparison.Ordinal);
    }

    /// <summary>"pci\ven_10de&amp;dev_2484&amp;…" → NVIDIA.</summary>
    internal static string VendorFromMatchingId(string matchingDeviceId)
    {
        var i = matchingDeviceId.IndexOf("ven_", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || i + 8 > matchingDeviceId.Length) return "Unknown";
        return VendorFromPciId(matchingDeviceId.Substring(i + 4, 4));
    }

    /// <summary>A memory size value: QWORD (long), DWORD (int, read as unsigned), or REG_BINARY (4 or 8 bytes, little endian).</summary>
    internal static long? MemoryValue(object? v)
    {
        long? r = v switch
        {
            long l => l,
            int i => (uint)i,
            uint u => u,
            ulong ul => ul > long.MaxValue ? null : (long)ul,
            byte[] { Length: >= 8 } b => BitConverter.ToInt64(LittleEndian(b, 8), 0),
            byte[] { Length: >= 4 } b => BitConverter.ToUInt32(LittleEndian(b, 4), 0),
            _ => null,
        };
        return r is > 0 ? r : null;
    }

    static byte[] LittleEndian(byte[] b, int n)
    {
        var x = b.Take(n).ToArray();
        if (!BitConverter.IsLittleEndian) Array.Reverse(x);
        return x;
    }

    [SupportedOSPlatform("windows")]
    sealed class WindowsDisplayRegistry : IDisplayAdapterRegistry
    {
        public IEnumerable<string> AdapterKeys()
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            return k?.GetSubKeyNames() ?? Array.Empty<string>();
        }

        public object? Value(string adapterKey, string name)
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DisplayClassKey + "\\" + adapterKey);
                return k?.GetValue(name);
            }
            catch (System.Security.SecurityException) { return null; } // "Properties" and some keys are not readable by users
        }
    }

    // ------------------------------------------------------------------ nvidia-smi

    /// <summary>Parses <c>nvidia-smi --query-gpu=name,memory.total --format=csv,noheader,nounits</c> (memory in MiB).</summary>
    public static IReadOnlyList<GpuInfo> ParseNvidiaSmi(string output)
    {
        var list = new List<GpuInfo>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var comma = line.LastIndexOf(',');
            if (comma <= 0) continue;
            var name = line[..comma].Trim();
            var mem = line[(comma + 1)..].Trim();
            long? bytes = long.TryParse(mem, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mib) && mib > 0 ? mib * 1024 * 1024 : null;
            if (name.Length > 0) list.Add(new GpuInfo(name, "NVIDIA", bytes, "nvidia-smi", false));
        }
        return list;
    }

    /// <summary>NVIDIA entries from nvidia-smi replace the registry/sysfs ones (exact name and VRAM; the registry DWORD stops at 4 GiB).</summary>
    public static List<GpuInfo> MergeNvidiaSmi(IReadOnlyList<GpuInfo> found, IReadOnlyList<GpuInfo> smi)
    {
        if (smi.Count == 0) return found.ToList();
        return found.Where(g => g.Vendor != "NVIDIA").Concat(smi).ToList();
    }

    static IReadOnlyList<GpuInfo> NvidiaSmi(List<string> notes)
    {
        var exe = FindOnPath(OperatingSystem.IsWindows() ? "nvidia-smi.exe" : "nvidia-smi");
        if (exe is null) return Array.Empty<GpuInfo>();
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            foreach (var a in new[] { "--query-gpu=name,memory.total", "--format=csv,noheader,nounits" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return Array.Empty<GpuInfo>();
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                notes.Add("nvidia-smi did not answer within 5 s; NVIDIA VRAM comes from the driver information instead.");
                return Array.Empty<GpuInfo>();
            }
            return p.ExitCode == 0 && stdout.Wait(1000) ? ParseNvidiaSmi(stdout.Result) : Array.Empty<GpuInfo>();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            notes.Add("nvidia-smi could not be run: " + e.Message);
            return Array.Empty<GpuInfo>();
        }
    }

    /// <summary>An executable in an absolute PATH directory (never the current directory).</summary>
    static string? FindOnPath(string file)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (OperatingSystem.IsWindows()) dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.System));
        foreach (var d in dirs)
        {
            try
            {
                if (!Path.IsPathFullyQualified(d)) continue;
                var p = Path.Combine(d, file);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    // ------------------------------------------------------------------ Linux

    static IEnumerable<GpuInfo> LinuxGpus()
    {
        var cards = new List<GpuInfo>();
        const string drm = "/sys/class/drm";
        if (Directory.Exists(drm))
        {
            foreach (var card in Directory.GetDirectories(drm, "card*").OrderBy(x => x, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(card);
                if (name.Contains('-')) continue; // connectors like card0-HDMI-A-1
                var dev = Path.Combine(card, "device");
                var vendorId = ReadTrim(Path.Combine(dev, "vendor"));
                if (vendorId is null) continue;
                long? vram = null;
                if (ReadTrim(Path.Combine(dev, "mem_info_vram_total")) is { } v && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
                    vram = bytes;
                var vendor = VendorFromPciId(vendorId);
                var device = (ReadTrim(Path.Combine(dev, "device")) ?? "?").ToLowerInvariant();
                bool? integrated = vendor switch
                {
                    "NVIDIA" => false,
                    // DG2 (Arc A) and Battlemage (Arc B) device ids; every other Intel GPU is integrated.
                    "Intel" => !(device.StartsWith("0x56", StringComparison.Ordinal) || device.StartsWith("0xe20", StringComparison.Ordinal)),
                    // amdgpu reports the carve-out of an APU as VRAM; discrete cards have at least 2 GiB.
                    "AMD" when vram is { } b => b < 2 * HardwareReport.GiB,
                    _ => null,
                };
                cards.Add(new GpuInfo($"{vendor} GPU {device} ({name})", vendor, vram, "sysfs", integrated));
            }
        }
        // NVIDIA's proprietary driver gives the model name (not the VRAM) under /proc/driver/nvidia; use it for the sysfs entries.
        var names = new List<string>();
        const string nv = "/proc/driver/nvidia/gpus";
        if (Directory.Exists(nv))
            foreach (var g in Directory.GetDirectories(nv).Order(StringComparer.Ordinal))
            {
                var info = Path.Combine(g, "information");
                if (!File.Exists(info)) continue;
                var model = File.ReadLines(info).FirstOrDefault(l => l.StartsWith("Model:", StringComparison.Ordinal));
                if (model is not null) names.Add(model["Model:".Length..].Trim());
            }
        var nvidia = cards.Where(c => c.Vendor == "NVIDIA").ToList();
        if (names.Count > 0 && names.Count == nvidia.Count)
            for (var i = 0; i < nvidia.Count; i++) cards[cards.IndexOf(nvidia[i])] = nvidia[i] with { Name = names[i], Source = "sysfs+procfs" };
        else if (nvidia.Count == 0)
            cards.AddRange(names.Select(n => new GpuInfo(n, "NVIDIA", null, "procfs", false)));
        return cards;
    }

    static string? ReadTrim(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
