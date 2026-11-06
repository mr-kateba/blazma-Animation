using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blazma.AI;

public sealed record ModelLicence(string Id, string Name, string Url, string SummaryEn, string SummaryAr);

/// <summary>One file of a package: where it comes from (Hugging Face repo pinned to a commit) and what it must be.</summary>
public sealed record ModelFile(string Id, string Repo, string Revision, string Path, string FileName, long Size, string Sha256, string LicenceId)
{
    /// <summary>Another source for this file (tests, mirrors). The downloader's host allowlist still applies.</summary>
    public Uri? OverrideUrl { get; init; }

    /// <summary>https://huggingface.co/{repo}/resolve/{commit}/{path}: pinned, so the bytes always match <see cref="Sha256"/>.</summary>
    public Uri Url => OverrideUrl ?? new($"https://huggingface.co/{Repo}/resolve/{Revision}/{string.Join('/', Path.Split('/').Select(Uri.EscapeDataString))}");
}

/// <summary>A downloadable model package (tier): one motion checkpoint + one text-encoder quantisation + tokenizer + licence files.</summary>
public sealed record ModelTier(
    string Id, string NameEn, string NameAr, string MotionModel, ModelFile TextFile, IReadOnlyList<ModelFile> Files,
    double MinRamGiB, double RecommendedRamGiB, double MinVramGiB, double RecommendedVramGiB, bool AutoNeedsGpu,
    string QualityEn, string QualityAr)
{
    public long TotalBytes => Files.Sum(f => f.Size);
    public IEnumerable<string> LicenceIds => Files.Select(f => f.LicenceId).Distinct();
}

public sealed record RunnerInfo(string BundledFolder, string ExecutableWindows, string ExecutableLinux,
    string ReleaseAssetWindows, string ReleaseAssetLinux, bool Downloadable, string NoteEn, string NoteAr);

/// <summary>Installed state of a tier in a models folder (by existence and exact size; hashes are checked when downloading).</summary>
public sealed record TierInstallState(string TierId, bool Installed, long BytesPresent, long BytesMissing, IReadOnlyList<string> MissingFiles);

/// <summary>
/// The Kimodo packages Blazma can download (embedded ModelCatalog.json, verified against Hugging Face, see "verified").
/// Every URL is pinned to a commit and every file has its size and SHA-256. Nothing here touches the network.
/// </summary>
public sealed class ModelCatalog
{
    public required IReadOnlyList<ModelTier> Tiers { get; init; }
    public required IReadOnlyDictionary<string, ModelLicence> Licences { get; init; }
    public required RunnerInfo Runner { get; init; }
    public required string Verified { get; init; }
    public required string MemoryNote { get; init; }

    public ModelTier? Find(string id) => Tiers.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    static ModelCatalog? _default;

    /// <summary>The catalog embedded in Blazma.AI.</summary>
    public static ModelCatalog Default
    {
        get
        {
            if (_default is not null) return _default;
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Blazma.AI.ModelCatalog.json")
                ?? throw new InvalidOperationException("ModelCatalog.json is not embedded.");
            return _default = Parse(s);
        }
    }

    sealed record JSource(string Repo, string Revision);
    sealed record JFile(string Source, string Path, string? Target, long Size, string Sha256, string Licence);
    sealed record JLicence(string Name, string Url, string SummaryEn, string SummaryAr);
    sealed record JTier(string Id, string NameEn, string NameAr, string MotionModel, string TextFile, string[] Files,
        double MinRamGiB, double RecommendedRamGiB, double MinVramGiB, double RecommendedVramGiB, bool AutoNeedsGpu, string QualityEn, string QualityAr);
    sealed record JRunner(string Bundled, string ExecutableWindows, string ExecutableLinux, string ReleaseAssetWindows, string ReleaseAssetLinux,
        bool Downloadable, string NoteEn, string NoteAr);
    sealed record JCatalog(int Format, string Verified, Dictionary<string, JSource> Sources, Dictionary<string, JLicence> Licences,
        Dictionary<string, JFile> Files, JTier[] Tiers, JRunner Runner, string MemoryNote);

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static ModelCatalog Parse(Stream json)
    {
        var c = JsonSerializer.Deserialize<JCatalog>(json, Json) ?? throw new InvalidDataException("empty catalog");
        if (c.Format != 1) throw new InvalidDataException($"unsupported catalog format {c.Format}");
        var files = c.Files.ToDictionary(kv => kv.Key, kv =>
        {
            var f = kv.Value;
            var src = c.Sources[f.Source];
            var name = f.Target ?? System.IO.Path.GetFileName(f.Path);
            if (name.Length == 0 || name != System.IO.Path.GetFileName(name) || name.StartsWith('.'))
                throw new InvalidDataException($"bad target file name for {kv.Key}");
            if (f.Sha256.Length != 64 || !f.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException($"bad sha256 for {kv.Key}");
            if (!c.Licences.ContainsKey(f.Licence)) throw new InvalidDataException($"unknown licence {f.Licence}");
            return new ModelFile(kv.Key, src.Repo, src.Revision, f.Path, name, f.Size, f.Sha256.ToLowerInvariant(), f.Licence);
        });
        var tiers = c.Tiers.Select(t => new ModelTier(t.Id, t.NameEn, t.NameAr, t.MotionModel, files[t.TextFile], t.Files.Select(id => files[id]).ToList(),
            t.MinRamGiB, t.RecommendedRamGiB, t.MinVramGiB, t.RecommendedVramGiB, t.AutoNeedsGpu, t.QualityEn, t.QualityAr)).ToList();
        foreach (var t in tiers)
        {
            if (!KimodoSettings.MotionModels.TryGetValue(t.MotionModel, out var motionFile) || t.Files.All(f => f.FileName != motionFile))
                throw new InvalidDataException($"tier {t.Id}: motion model file missing");
            if (!t.Files.Contains(t.TextFile) || t.Files.All(f => f.FileName != KimodoSettings.TokenizerFile))
                throw new InvalidDataException($"tier {t.Id}: text encoder or tokenizer missing");
            if (t.Files.Select(f => f.FileName).Distinct().Count() != t.Files.Count) throw new InvalidDataException($"tier {t.Id}: duplicate file names");
        }
        var r = c.Runner;
        return new ModelCatalog
        {
            Tiers = tiers,
            Licences = c.Licences.ToDictionary(kv => kv.Key, kv => new ModelLicence(kv.Key, kv.Value.Name, kv.Value.Url, kv.Value.SummaryEn, kv.Value.SummaryAr)),
            Runner = new RunnerInfo(r.Bundled, r.ExecutableWindows, r.ExecutableLinux, r.ReleaseAssetWindows, r.ReleaseAssetLinux, r.Downloadable, r.NoteEn, r.NoteAr),
            Verified = c.Verified,
            MemoryNote = c.MemoryNote,
        };
    }

    // ------------------------------------------------------------------ local state (no network)

    /// <summary>Is the tier in <paramref name="modelsDirectory"/>? Files are checked by name and exact size (no hashing: 8+ GB).</summary>
    public static TierInstallState State(ModelTier tier, string? modelsDirectory)
    {
        long present = 0, missing = 0;
        var names = new List<string>();
        foreach (var f in tier.Files)
        {
            long? len = null;
            try
            {
                if (modelsDirectory is { Length: > 0 } d && new FileInfo(System.IO.Path.Combine(d, f.FileName)) is { Exists: true } fi) len = fi.Length;
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { }
            if (len == f.Size) present += f.Size;
            else { missing += f.Size; names.Add(f.FileName); }
        }
        return new TierInstallState(tier.Id, names.Count == 0, present, missing, names);
    }

    /// <summary>Default models folder: %LOCALAPPDATA%\BlazmaAnimation\models on Windows (not roaming: these are GBs),
    /// $XDG_DATA_HOME/BlazmaAnimation/models (or ~/.local/share/…) elsewhere. Portable mode passes "userdata/models" instead.</summary>
    public static string DefaultModelsDirectory()
    {
        string root;
        if (OperatingSystem.IsWindows()) root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        else if (Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x && System.IO.Path.IsPathFullyQualified(x)) root = x;
        else root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return System.IO.Path.Combine(root, "BlazmaAnimation", "models");
    }

    /// <summary>The kimodo-runner shipped with the app (<c>tools/kimodo/kimodo-runner[.exe]</c> next to the executable), or null.</summary>
    public string? FindBundledRunner(string appDirectory)
    {
        var exe = OperatingSystem.IsWindows() ? Runner.ExecutableWindows : Runner.ExecutableLinux;
        try
        {
            var p = System.IO.Path.GetFullPath(System.IO.Path.Combine(appDirectory, Runner.BundledFolder, exe));
            return File.Exists(p) ? p : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
