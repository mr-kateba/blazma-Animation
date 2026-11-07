using System.Text.Json.Nodes;

namespace Blazma.AI;

/// <summary>A GGML device kimodo-runner can run on, as its <c>hello</c>/<c>diagnose</c> list it (the equivalent of
/// llama.cpp's <c>--list-devices</c>): <see cref="Name"/> is GGML's name ("CUDA0", "Vulkan0", "CPU"),
/// <see cref="Type"/> is cpu | gpu | igpu, <see cref="Backend"/> is CUDA | Vulkan | CPU.</summary>
public sealed record KimodoDevice(string Name, string Description, string Type, string Backend, long MemoryTotalBytes, long MemoryFreeBytes)
{
    public bool IsGpu => Type is "gpu" or "igpu";

    /// <summary>"NVIDIA GeForce RTX 4070 (CUDA)"; the processor is "Intel(R) Core(TM) i7 (CPU)".</summary>
    public string Label => $"{(Description.Length > 0 ? Description : Name)} ({Backend})";

    internal static KimodoDevice? FromJson(JsonNode? n)
    {
        if (n is not JsonObject o || (string?)o["name"] is not { Length: > 0 } name) return null;
        static long L(JsonNode? x) => x is JsonValue v && v.TryGetValue<long>(out var l) ? l : x is JsonValue d && d.TryGetValue<double>(out var f) && double.IsFinite(f) ? (long)f : 0;
        return new KimodoDevice(name, (string?)o["description"] ?? "", (string?)o["type"] ?? "", (string?)o["backend"] ?? "", L(o["memoryTotal"]), L(o["memoryFree"]));
    }

    /// <summary>The devices of a hello/diagnose answer; null for a runner older than 0.3 (it does not list them).</summary>
    internal static IReadOnlyList<KimodoDevice>? ListFrom(JsonObject? answer) =>
        answer?["devices"] is JsonArray a ? a.Select(FromJson).Where(d => d is not null).Select(d => d!).Take(64).ToList() : null;

    public JsonObject ToJson() => new()
    {
        ["name"] = Name, ["description"] = Description, ["type"] = Type, ["backend"] = Backend,
        ["memoryTotalBytes"] = MemoryTotalBytes, ["memoryFreeBytes"] = MemoryFreeBytes, ["label"] = Label,
    };
}

/// <summary>Where a generation actually ran: <see cref="Mode"/> gpu | hybrid | cpu (or the old runner's auto / vulkan),
/// the text encoder's and the motion model's device (null when an old runner did not say).</summary>
public sealed record KimodoDeviceUse(string Mode, KimodoDevice? Text, KimodoDevice? Motion)
{
    /// <summary>"NVIDIA GeForce RTX 4070 (CUDA)", "NVIDIA GeForce RTX 4070 (Vulkan) + processor", "processor (CPU)".</summary>
    public string Summary => Mode switch
    {
        "gpu" when Motion is { } m => m.Label,
        "hybrid" when Motion is { } m => $"{m.Label} + processor (text encoder)",
        "cpu" => Motion is { } c ? $"processor: {c.Label}" : "processor (CPU)",
        _ => Motion?.Label ?? $"device '{Mode}'",
    };

    public bool UsedGpu => Mode is "gpu" or "hybrid" || Motion?.IsGpu == true;

    public JsonObject ToJson() => new()
    {
        ["mode"] = Mode, ["summary"] = Summary, ["usedGpu"] = UsedGpu,
        ["text"] = Text?.ToJson(), ["motion"] = Motion?.ToJson(),
    };

    internal static KimodoDeviceUse? FromResult(JsonObject result) =>
        result["device"] is JsonObject d && (string?)d["mode"] is { Length: > 0 } mode
            ? new KimodoDeviceUse(mode, KimodoDevice.FromJson(d["text"]), KimodoDevice.FromJson(d["motion"]))
            : null;
}

/// <summary>One try of a generation: the runner's <c>device</c> parameter and optional <c>gpu</c> device name.</summary>
public sealed record KimodoAttempt(string Device, string? Gpu)
{
    public override string ToString() => Gpu is null ? Device : $"{Device} on {Gpu}";
}

/// <summary>
/// The order in which a generation tries devices (DECISIONS D44), the same idea as Blazma AI's llama.cpp variants:
/// newest/fastest first, every failure falls through to the next one. Auto: each graphics card backend in turn
/// (CUDA, then Vulkan; integrated graphics last), then graphics card + processor (the text encoder, the largest part,
/// moves to the processor — what helps when the card runs out of memory), then the processor alone.
/// </summary>
public static class KimodoDevicePlanner
{
    /// <summary>Failures after which the next device is tried. Not: a cancelled or timed-out request, bad input,
    /// missing model files or a protocol mismatch (the next device would fail the same way or take even longer).</summary>
    public static bool ShouldFallBack(string code) =>
        code is "device_unavailable" or "model_load_failed" or "generation_failed" or "runner_crashed" or "invalid_motion" or "internal_error";

    /// <summary>The graphics cards in preference order: CUDA, other discrete GPUs (Vulkan), integrated GPUs; at most one
    /// device per backend (the first), so a card that both CUDA and Vulkan see is tried once with each.</summary>
    public static IReadOnlyList<KimodoDevice> RankGpus(IEnumerable<KimodoDevice> devices) =>
        devices.Where(d => d.IsGpu)
            .Select((d, i) => (d, i))
            .OrderBy(x => x.d.Type == "igpu" ? 2 : x.d.Backend.Equals("CUDA", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.i)
            .Select(x => x.d)
            .ToList();

    /// <param name="mode">auto | gpu | cpu | hybrid (<see cref="KimodoSettings.Device"/>).</param>
    /// <param name="gpu">Optional GGML device name (<see cref="KimodoSettings.GpuDevice"/>).</param>
    /// <param name="devices">The runner's devices; null for a runner older than 0.3, which only knows auto | cpu | vulkan.</param>
    /// <exception cref="MotionGenerationException">device_unavailable when the mode needs a graphics card and there is none.</exception>
    public static IReadOnlyList<KimodoAttempt> Plan(string mode, string? gpu, IReadOnlyList<KimodoDevice>? devices)
    {
        mode = KimodoSettings.NormalizeDevice(mode);
        if (devices is null)
            return mode switch
            {
                "cpu" => new[] { new KimodoAttempt("cpu", null) },
                "gpu" => new[] { new KimodoAttempt("vulkan", null) },
                "hybrid" => throw new MotionGenerationException("device_unavailable",
                    "This kimodo-runner is too old for \"graphics card + processor\" (it needs kimodo-runner 0.3 or later). Update Blazma, or choose another device."),
                _ => new[] { new KimodoAttempt("auto", null) },
            };
        if (mode == "cpu") return new[] { new KimodoAttempt("cpu", null) };

        var ranked = RankGpus(devices);
        List<KimodoDevice> gpus;
        if (gpu is { Length: > 0 } wanted && ranked.FirstOrDefault(d => d.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) is { } named)
            gpus = new() { named };
        else if (gpu is { Length: > 0 } && mode != "auto")
            throw new MotionGenerationException("device_unavailable",
                $"The graphics card '{gpu}' is not available. kimodo-runner sees: {Describe(ranked)}.");
        else
            gpus = ranked.GroupBy(d => d.Backend, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

        if (gpus.Count == 0 && mode != "auto")
            throw new MotionGenerationException("device_unavailable",
                "No graphics card usable by Kimodo was found (no CUDA or Vulkan device). Update the graphics driver, or choose \"Processor only\".");
        var list = new List<KimodoAttempt>();
        if (mode is "auto" or "gpu") list.AddRange(gpus.Select(g => new KimodoAttempt("gpu", g.Name)));
        if (mode == "hybrid") list.AddRange(gpus.Select(g => new KimodoAttempt("hybrid", g.Name)));
        if (mode == "auto")
        {
            if (gpus.Count > 0) list.Add(new KimodoAttempt("hybrid", gpus[0].Name));
            list.Add(new KimodoAttempt("cpu", null));
        }
        return list;
    }

    static string Describe(IReadOnlyList<KimodoDevice> gpus) =>
        gpus.Count == 0 ? "no graphics card" : string.Join(", ", gpus.Select(g => $"{g.Name} = {g.Label}"));
}
