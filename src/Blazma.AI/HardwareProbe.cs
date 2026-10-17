using System.Globalization;
using System.Runtime.InteropServices;

namespace Blazma.AI;

public sealed record GpuInfo(string Name, string Vendor, long? VramBytes, string Source);

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
}

/// <summary>
/// Reads hardware facts from local sources only (no network, no external tools started):
/// RAM from the .NET runtime; on Linux GPUs from /sys/class/drm (vendor id, AMD/Intel VRAM size) and
/// /proc/driver/nvidia. On Windows the GPU list is left to the runner's diagnose call (not implemented
/// without starting a process), and the report says so.
/// </summary>
public static class HardwareProbe
{
    static HardwareReport? _cached;

    public static HardwareReport Probe(bool refresh = false)
    {
        if (!refresh && _cached is not null) return _cached;
        var notes = new List<string>();
        long? ram = null;
        try
        {
            var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (total > 0) ram = total;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { }

        var gpus = new List<GpuInfo>();
        if (OperatingSystem.IsLinux())
        {
            try { gpus.AddRange(LinuxGpus()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { notes.Add("GPU information could not be read: " + e.Message); }
            if (gpus.Count == 0) notes.Add("No GPU found under /sys/class/drm (CPU generation only, or a driver without sysfs info).");
        }
        else notes.Add("GPU/VRAM detection is only implemented on Linux; Kimodo picks a device itself (Vulkan if available, else CPU).");
        notes.Add("RAM is the memory available to this process (may be lower than installed RAM in containers).");

        _cached = new HardwareReport(RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount, ram, gpus, notes);
        return _cached;
    }

    static IEnumerable<GpuInfo> LinuxGpus()
    {
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
                var vendor = vendorId.ToLowerInvariant() switch
                {
                    "0x10de" => "NVIDIA",
                    "0x1002" => "AMD",
                    "0x8086" => "Intel",
                    _ => vendorId,
                };
                var device = ReadTrim(Path.Combine(dev, "device")) ?? "?";
                yield return new GpuInfo($"{vendor} GPU {device} ({name})", vendor, vram, "sysfs");
            }
        }
        const string nv = "/proc/driver/nvidia/gpus";
        if (Directory.Exists(nv))
            foreach (var g in Directory.GetDirectories(nv))
            {
                var info = Path.Combine(g, "information");
                if (!File.Exists(info)) continue;
                var model = File.ReadLines(info).FirstOrDefault(l => l.StartsWith("Model:", StringComparison.Ordinal));
                if (model is not null) yield return new GpuInfo(model["Model:".Length..].Trim(), "NVIDIA", null, "procfs");
            }
    }

    static string? ReadTrim(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
