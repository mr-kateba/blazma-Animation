using System.Globalization;

namespace Blazma.AI;

/// <summary>
/// Where the user installed kimodo-runner and the Kimodo GGUF files (docs/AI_INTEGRATION.md §5). Nothing is downloaded
/// automatically: if a file is missing the provider is "unavailable" and says what to install where. The user can download a
/// package from Settings (<see cref="ModelDownloader"/>, only on their click, DECISIONS D43); <see cref="ModelInstallResult.ApplyTo"/>
/// then fills in the folder, model, text encoder and device.
/// Agents cannot change these settings (they choose an executable); only the user (app settings) or the
/// environment of the process can.
/// </summary>
public sealed record KimodoSettings
{
    public const string RunnerEnv = "BLAZMA_KIMODO_RUNNER";
    public const string ModelsEnv = "BLAZMA_KIMODO_MODELS";
    public const string ModelEnv = "BLAZMA_KIMODO_MODEL";
    public const string TextModelEnv = "BLAZMA_KIMODO_TEXT_MODEL";
    public const string DeviceEnv = "BLAZMA_KIMODO_DEVICE";
    public const string TimeoutEnv = "BLAZMA_KIMODO_TIMEOUT_SECONDS";

    /// <summary>Released SOMA checkpoints (30-joint body skeleton) and their file names in the LocalAI-io GGUF repos.
    /// G1 (robot) and SMPL-X (research-only licence, DECISIONS D7) are deliberately not supported.</summary>
    public static readonly IReadOnlyDictionary<string, string> MotionModels = new Dictionary<string, string>
    {
        ["soma-rp-v1.1"] = "kimodo-soma-rp-v1.1-f32.gguf",
        ["soma-seed-v1.1"] = "kimodo-soma-seed-v1.1-f32.gguf",
    };

    /// <summary>Text encoder files in preference order (Q8_0 is kimodo.cpp's default); tokenizer.gguf must sit beside it.</summary>
    public static readonly IReadOnlyList<string> TextModels = new[]
    {
        "Llama-3-Kimodo-Q8_0.gguf", "Llama-3-Kimodo-Q6_K.gguf", "Llama-3-Kimodo-Q5_K.gguf",
        "Llama-3-Kimodo-Q4_K_M.gguf", "Llama-3-Kimodo-Q4_K.gguf", "Llama-3-Kimodo-BF16.gguf",
    };

    public const string TokenizerFile = "tokenizer.gguf";

    /// <summary>kimodo-runner executable (or a host such as "dotnet" with <see cref="RunnerArguments"/>).</summary>
    public string? RunnerPath { get; init; }

    /// <summary>Arguments put before the runner's own (tests use this to start a .dll through dotnet).</summary>
    public IReadOnlyList<string> RunnerArguments { get; init; } = Array.Empty<string>();

    /// <summary>Folder with the GGUF files (also searched: its "models" sub-folder).</summary>
    public string? ModelDirectory { get; init; }

    /// <summary>Motion checkpoint id, see <see cref="MotionModels"/>.</summary>
    public string Model { get; init; } = "soma-rp-v1.1";

    /// <summary>Text encoder file name or absolute path; null picks the first of <see cref="TextModels"/> found.</summary>
    public string? TextModel { get; init; }

    /// <summary>auto | cpu | vulkan. kimodo.cpp chooses the backend from KIMODO_BACKEND (cpu forces CPU);
    /// the runner sets it before loading. "vulkan" fails when the runner was built without Vulkan.</summary>
    public string Device { get; init; } = "auto";

    /// <summary>CPU threads (0 = runtime default); passed to kimodo.cpp as KIMODO_THREADS.</summary>
    public int Threads { get; init; }

    /// <summary>DDIM steps; kimodo.cpp's demo uses 100.</summary>
    public int DiffusionSteps { get; init; } = 100;

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan DiagnoseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Whole generate call, including loading the models on first use.</summary>
    public TimeSpan GenerateTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The runner (and the models it holds in memory) is stopped after this long without requests.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Largest line accepted from the runner (a 30 s, 30 fps motion is ≈1 MB of JSON).</summary>
    public int MaxMessageBytes { get; init; } = 32 * 1024 * 1024;

    public static KimodoSettings FromEnvironment()
    {
        var s = new KimodoSettings
        {
            RunnerPath = Env(RunnerEnv),
            ModelDirectory = Env(ModelsEnv),
            TextModel = Env(TextModelEnv),
        };
        if (Env(ModelEnv) is { } m) s = s with { Model = m };
        if (Env(DeviceEnv) is { } d) s = s with { Device = d.ToLowerInvariant() };
        if (Env(TimeoutEnv) is { } t && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) && sec > 0)
            s = s with { GenerateTimeout = TimeSpan.FromSeconds(Math.Min(sec, 24 * 3600)) };
        return s;
    }

    static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v.Trim() : null;

    // ------------------------------------------------------------------ file resolution (no network)

    /// <summary>The runner executable, or null when it is not set, not an absolute path (a relative path would depend on the
    /// working directory and the PATH search order), or missing.</summary>
    public string? ResolveRunner()
    {
        if (RunnerPath is not { Length: > 0 } p) return null;
        try
        {
            if (!Path.IsPathFullyQualified(p)) return null;
            var full = Path.GetFullPath(p);
            if (File.Exists(full)) return full;
            if (OperatingSystem.IsWindows() && File.Exists(full + ".exe")) return full + ".exe";
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        return null;
    }

    static bool IsAbsolute(string p)
    {
        try { return Path.IsPathFullyQualified(p); }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Absolute paths these settings point at (configured and resolved), longest first, for hiding them from agents.</summary>
    public IReadOnlyList<string> KnownPaths()
    {
        var list = new List<string?> { RunnerPath, ResolveRunner(), ModelDirectory, ResolveMotionModel(), ResolveTextModel(), TextModel };
        try { if (ModelDirectory is { Length: > 0 } d) list.Add(Path.GetFullPath(d)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
        foreach (var dir in SearchDirs()) list.Add(dir);
        return list.Where(x => x is { Length: > 1 } && (x.Contains('/') || x.Contains('\\'))).Select(x => x!).Distinct().OrderByDescending(x => x.Length).ToList();
    }

    IEnumerable<string> SearchDirs()
    {
        if (ModelDirectory is not { Length: > 0 } d) yield break;
        string full;
        try { full = Path.GetFullPath(d); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { yield break; }
        yield return full;
        yield return Path.Combine(full, "models");
    }

    public string? ResolveMotionModel()
    {
        if (!MotionModels.TryGetValue(Model, out var file)) return null;
        return SearchDirs().Select(dir => Path.Combine(dir, file)).FirstOrDefault(File.Exists);
    }

    public string? ResolveTextModel()
    {
        if (TextModel is { Length: > 0 } t && Path.IsPathRooted(t)) return File.Exists(t) ? t : null;
        var names = TextModel is { Length: > 0 } n ? new[] { n } : TextModels;
        foreach (var dir in SearchDirs())
            foreach (var name in names)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) return path;
            }
        return null;
    }

    public static bool HasTokenizer(string textModel) =>
        File.Exists(Path.Combine(Path.GetDirectoryName(textModel) ?? ".", TokenizerFile));

    /// <summary>Null when everything needed is in place; otherwise a message telling the user what to install.</summary>
    public string? MissingReason()
    {
        if (RunnerPath is null)
            return $"kimodo-runner is not configured. Build or install it (see docs/AI_INTEGRATION.md) and set its path in Settings or {RunnerEnv}.";
        if (!IsAbsolute(RunnerPath)) return $"The kimodo-runner path must be an absolute path (got '{RunnerPath}').";
        if (ResolveRunner() is null) return $"kimodo-runner was not found at '{RunnerPath}'.";
        if (!MotionModels.ContainsKey(Model))
            return $"Unsupported Kimodo model '{Model}'. Supported: {string.Join(", ", MotionModels.Keys)} (G1 and SMPL-X are not supported).";
        if (ModelDirectory is null) return $"The Kimodo model folder is not configured. Download a model package in Settings → AI, or download the GGUF files yourself and set the folder in Settings or {ModelsEnv}.";
        if (ResolveMotionModel() is null) return $"'{MotionModels[Model]}' was not found in '{ModelDirectory}' (or its 'models' folder).";
        if (ResolveTextModel() is not { } text) return $"No Kimodo text encoder ({string.Join(", ", TextModels.Take(2))}, …) was found in '{ModelDirectory}'.";
        if (!HasTokenizer(text)) return $"'{TokenizerFile}' must be next to '{Path.GetFileName(text)}'.";
        if (Device is not ("auto" or "cpu" or "vulkan")) return $"Device must be auto, cpu or vulkan (got '{Device}').";
        return null;
    }
}
