using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blazma.AI;

/// <summary>One file of the optional CUDA backend (ggml-cuda and the NVIDIA CUDA runtime libraries it needs).</summary>
public sealed record CudaBackendFile(string Name, long Size, string Sha256);

/// <summary>
/// The optional CUDA backend of one Blazma release (DECISIONS D47): a zip release asset with GGML's CUDA backend
/// (<see cref="Library"/>) and the CUDA runtime libraries, unpacked into a user-writable folder only when the user presses the
/// button in Settings → AI. Described by <c>cuda-backend.json</c>, which scripts/build-release.sh writes next to the bundled
/// kimodo-runner: the expected size and SHA-256 of the archive and of every file in it ship inside the installer, nothing about
/// them is read from the network.
/// </summary>
public sealed record CudaBackendPackage(string Version, string Cuda, string Asset, Uri Url, long Size, string Sha256, string Library,
    IReadOnlyList<CudaBackendFile> Files)
{
    public const string FileName = "cuda-backend.json";

    /// <summary>Disk space the unpacked files take.</summary>
    public long InstalledBytes => Files.Sum(f => f.Size);

    sealed record JFile(string Name, long Size, string Sha256);
    sealed record JPackage(int Format, string Version, string Cuda, string Asset, string Url, long Size, string Sha256, string Library, JFile[] Files);

    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };

    /// <exception cref="InvalidDataException">The description is malformed (bad names, hashes or sizes, no library, not a zip).</exception>
    public static CudaBackendPackage Parse(string json)
    {
        JPackage? p;
        try { p = JsonSerializer.Deserialize<JPackage>(json, Json); }
        catch (JsonException e) { throw new InvalidDataException("cuda-backend.json is not valid JSON: " + e.Message, e); }
        if (p is null) throw new InvalidDataException("cuda-backend.json is empty");
        if (p.Format != 1) throw new InvalidDataException($"unsupported cuda-backend.json format {p.Format}");
        if (!PlainName(p.Asset) || !p.Asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("bad asset name");
        if (!Uri.TryCreate(p.Url, UriKind.Absolute, out var url)) throw new InvalidDataException("bad url");
        if (p.Size <= 0 || !Hex(p.Sha256)) throw new InvalidDataException("bad archive size or sha256");
        if (p.Files is not { Length: > 0 } || p.Files.Length > 64) throw new InvalidDataException("no files");
        foreach (var f in p.Files)
            if (!PlainName(f.Name) || f.Size < 0 || !Hex(f.Sha256)) throw new InvalidDataException($"bad file entry '{f.Name}'");
        if (p.Files.Select(f => f.Name.ToLowerInvariant()).Distinct().Count() != p.Files.Length) throw new InvalidDataException("duplicate file names");
        if (!p.Files.Any(f => f.Name == p.Library)) throw new InvalidDataException("the library is not one of the files");
        return new CudaBackendPackage(p.Version ?? "", p.Cuda ?? "", p.Asset, url, p.Size, p.Sha256.ToLowerInvariant(), p.Library,
            p.Files.Select(f => new CudaBackendFile(f.Name, f.Size, f.Sha256.ToLowerInvariant())).ToList());
    }

    /// <summary>The package described next to <paramref name="runnerPath"/> (the bundled kimodo-runner), or null when this build has none
    /// (Linux, a runner the user built, a release without the CUDA asset) or the file is unreadable.</summary>
    public static CudaBackendPackage? Load(string? runnerPath)
    {
        if (runnerPath is not { Length: > 0 }) return null;
        try
        {
            var file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(runnerPath)) ?? "", FileName);
            if (!File.Exists(file) || new FileInfo(file).Length > 256 * 1024) return null;
            return Parse(File.ReadAllText(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException) { return null; }
    }

    internal static bool PlainName(string? n) =>
        n is { Length: > 0 and <= 128 } && n == Path.GetFileName(n) && !n.StartsWith('.') && n.IndexOfAny(new[] { '/', '\\', ':' }) < 0;

    static bool Hex(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);
}

/// <summary>NotOffered: this build has no CUDA package (or no runner). Bundled: the runner ships its own CUDA backend.
/// NotInstalled / Installed: the download is missing / in place and matches this build's runner. Outdated: a download for another
/// runner build is in place (it is not used: a backend must come from the same GGML build as the runner).</summary>
public enum CudaBackendStatus { NotOffered, Bundled, NotInstalled, Installed, Outdated }

/// <summary>What is in the CUDA folder. <see cref="PartialBytes"/>: an interrupted download that "Download" resumes.</summary>
public sealed record CudaBackendState(CudaBackendStatus Status, string Directory, string? InstalledVersion, long InstalledBytes, long PartialBytes)
{
    /// <summary>Something of ours is on disk and can be removed.</summary>
    public bool CanRemove => InstalledVersion is not null || PartialBytes > 0;
}

/// <summary>
/// The optional CUDA backend on disk (DECISIONS D47). Downloads happen only through <see cref="CudaBackendInstaller"/>, which only the
/// app's settings window calls when the user presses its button: agents and the MCP/CLI tools can never trigger it.
/// kimodo-runner picks an installed backend up through GGML's <c>GGML_BACKEND_PATH</c> (<see cref="KimodoSettings.ResolveCudaBackend"/>).
/// </summary>
public static class CudaBackend
{
    /// <summary>Written last by the installer: without it the folder is not considered installed.</summary>
    public const string MarkerFile = "installed.json";
    internal const string DownloadFolder = ".download";
    internal const string StagingFolder = ".staging";

    /// <summary>%LOCALAPPDATA%\BlazmaAnimation\runtime\cuda on Windows, $XDG_DATA_HOME/BlazmaAnimation/runtime/cuda elsewhere.
    /// Portable mode passes "userdata/runtime/cuda" next to the app instead.</summary>
    public static string DefaultDirectory() =>
        Path.Combine(Path.GetDirectoryName(ModelCatalog.DefaultModelsDirectory())!, "runtime", "cuda");

    sealed record JMarker(int Format, string Version, string Library, CudaBackendFile[] Files);

    /// <summary>The installed files, from the marker; null when nothing (complete) is installed.</summary>
    public static (string Version, string Library, IReadOnlyList<CudaBackendFile> Files)? Installed(string? directory)
    {
        if (directory is not { Length: > 0 }) return null;
        try
        {
            var marker = Path.Combine(directory, MarkerFile);
            if (!File.Exists(marker) || new FileInfo(marker).Length > 64 * 1024) return null;
            var m = JsonSerializer.Deserialize<JMarker>(File.ReadAllText(marker), CudaBackendPackage.Json);
            if (m is not { Format: 1, Files.Length: > 0 } || !CudaBackendPackage.PlainName(m.Library) || m.Files.Any(f => !CudaBackendPackage.PlainName(f.Name))) return null;
            if (!m.Files.Any(f => f.Name == m.Library)) return null;
            // Cheap check (sizes): the files were hashed when they were installed.
            foreach (var f in m.Files)
                if (new FileInfo(Path.Combine(directory, f.Name)) is not { Exists: true } fi || fi.Length != f.Size) return null;
            return (m.Version ?? "", m.Library, m.Files);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>The installed backend library (full path), or null.</summary>
    public static string? ResolveLibrary(string? directory) =>
        Installed(directory) is { } i ? Path.GetFullPath(Path.Combine(directory!, i.Library)) : null;

    /// <summary>True when <paramref name="runnerPath"/>'s folder has its own CUDA backend (a full build): nothing to download.</summary>
    public static bool IsBundled(string? runnerPath, string library = "ggml-cuda.dll")
    {
        if (runnerPath is not { Length: > 0 }) return false;
        try { return File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(runnerPath)) ?? "", library)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static CudaBackendState State(CudaBackendPackage? package, string directory, string? runnerPath)
    {
        var installed = Installed(directory);
        var partial = PartialBytes(directory);
        var bytes = installed?.Files.Sum(f => f.Size) ?? 0;
        CudaBackendStatus status;
        if (IsBundled(runnerPath, package?.Library ?? "ggml-cuda.dll")) status = CudaBackendStatus.Bundled;
        else if (package is null) status = CudaBackendStatus.NotOffered;
        else if (installed is null) status = CudaBackendStatus.NotInstalled;
        else status = SameFiles(package, installed.Value.Library, installed.Value.Files) ? CudaBackendStatus.Installed : CudaBackendStatus.Outdated;
        return new CudaBackendState(status, directory, installed?.Version, bytes, partial);
    }

    static bool SameFiles(CudaBackendPackage p, string library, IReadOnlyList<CudaBackendFile> files) =>
        p.Library == library && p.Files.Count == files.Count
        && p.Files.All(f => files.Any(g => g.Name == f.Name && g.Size == f.Size && g.Sha256 == f.Sha256));

    static long PartialBytes(string directory)
    {
        try
        {
            var d = Path.Combine(directory, DownloadFolder);
            return Directory.Exists(d) ? new DirectoryInfo(d).EnumerateFiles().Sum(f => f.Length) : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }

    /// <summary>An NVIDIA graphics card is in the machine (the case where the CUDA backend is worth offering).</summary>
    public static bool HasNvidiaGpu(HardwareReport hw) =>
        hw.Gpus.Any(g => g.Vendor.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) || g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));

    /// <summary>The download button is shown: this build offers the package, an NVIDIA card is present, and it is not (or not the right one) installed.</summary>
    public static bool ShouldOffer(CudaBackendState state, HardwareReport hw) =>
        state.Status is CudaBackendStatus.NotInstalled or CudaBackendStatus.Outdated && HasNvidiaGpu(hw);

    /// <summary>
    /// Removes what the installer put into <paramref name="directory"/>: the files listed in its marker, the marker, the download and
    /// staging folders, then the folder itself if it is empty. Nothing else is touched. Stop kimodo-runner first (it holds the libraries open).
    /// Returns the bytes freed.
    /// </summary>
    /// <exception cref="ModelDownloadException">io_error when a file is in use or cannot be deleted.</exception>
    public static long Remove(string directory)
    {
        long freed = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            var names = new List<string>();
            var marker = Path.Combine(directory, MarkerFile);
            if (File.Exists(marker) && new FileInfo(marker).Length <= 64 * 1024)
            {
                try
                {
                    var m = JsonSerializer.Deserialize<JMarker>(File.ReadAllText(marker), CudaBackendPackage.Json);
                    if (m?.Files is { } fs) names.AddRange(fs.Select(f => f.Name).Where(CudaBackendPackage.PlainName));
                }
                catch (JsonException) { }
                freed += new FileInfo(marker).Length;
                File.Delete(marker); // first: a half-removed folder is never "installed"
            }
            foreach (var n in names)
            {
                var f = new FileInfo(Path.Combine(directory, n));
                if (!f.Exists) continue;
                freed += f.Length;
                f.Delete();
            }
            foreach (var sub in new[] { DownloadFolder, StagingFolder })
            {
                var d = new DirectoryInfo(Path.Combine(directory, sub));
                if (!d.Exists) continue;
                freed += d.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                d.Delete(true);
            }
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            return freed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ModelDownloadException("io_error", $"The CUDA backend could not be removed ({e.Message}). Close anything that uses it and try again.", e);
        }
    }
}

/// <summary>
/// Downloads and unpacks the optional CUDA backend, only when the user asks (DECISIONS D47). The archive goes through
/// <see cref="ModelDownloader"/> (allowlisted HTTPS, resumable <c>.part</c>, free space, SHA-256 against <see cref="CudaBackendPackage"/>),
/// then every listed file is extracted to a staging folder and checked again (size and SHA-256), moved into place, and the marker
/// is written last. Cancelling keeps the partial download (the next press resumes it). Stop kimodo-runner before installing.
/// </summary>
public sealed class CudaBackendInstaller(ModelDownloader downloader)
{
    public const string PackageId = "cuda-backend";

    /// <summary>Download progress (from the downloader) and unpacking progress (<see cref="DownloadStage.Installing"/>), on a worker thread.</summary>
    public event Action<DownloadProgress>? Progress;

    public async Task<CudaBackendState> InstallAsync(CudaBackendPackage package, string directory, CancellationToken ct = default)
    {
        var dir = Path.GetFullPath(directory);
        var downloads = Path.Combine(dir, CudaBackend.DownloadFolder);
        var archive = new ModelFile(PackageId, "", "", package.Asset, package.Asset, package.Size, package.Sha256, "nvidia-cuda-eula") { OverrideUrl = package.Url };
        void Forward(DownloadProgress p) => Progress?.Invoke(p);
        downloader.Progress += Forward;
        try
        {
            await downloader.DownloadFilesAsync(PackageId, new[] { archive }, downloads, package.InstalledBytes, reportCompleted: false, ct).ConfigureAwait(false);
        }
        finally { downloader.Progress -= Forward; }
        await Task.Run(() => Unpack(package, dir, Path.Combine(downloads, package.Asset), ct), ct).ConfigureAwait(false);
        return CudaBackend.State(package, dir, null);
    }

    void Unpack(CudaBackendPackage package, string dir, string zipPath, CancellationToken ct)
    {
        var staging = Path.Combine(dir, CudaBackend.StagingFolder);
        var total = package.InstalledBytes;
        long done = 0;
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var wanted = package.Files.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in zip.Entries)
                {
                    if (e.Name.Length == 0 || !wanted.ContainsKey(e.Name)) continue; // folders and files this build does not list are ignored
                    if (!entries.TryAdd(e.Name, e)) throw new ModelDownloadException("package_invalid", $"The CUDA archive contains '{e.Name}' twice.");
                }
                for (var i = 0; i < package.Files.Count; i++)
                {
                    var f = package.Files[i];
                    if (!entries.TryGetValue(f.Name, out var entry)) throw new ModelDownloadException("package_invalid", $"The CUDA archive has no '{f.Name}'.");
                    Report(f.Name, i, done);
                    var target = Path.Combine(staging, f.Name);
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long written = 0;
                    using (var src = entry.Open())
                    using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        var buf = new byte[1 << 20];
                        int n;
                        var last = Environment.TickCount64;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            written += n;
                            if (written > f.Size) throw new ModelDownloadException("package_invalid", $"'{f.Name}' in the CUDA archive is larger than expected.");
                            dst.Write(buf, 0, n);
                            sha.AppendData(buf, 0, n);
                            if (Environment.TickCount64 - last > 200) { last = Environment.TickCount64; Report(f.Name, i, done + written); }
                        }
                    }
                    var hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                    if (written != f.Size || hash != f.Sha256)
                        throw new ModelDownloadException("package_invalid", $"'{f.Name}' in the CUDA archive is not the expected file (size or SHA-256 differs).");
                    done += f.Size;
                }
            }
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(dir);
            var marker = Path.Combine(dir, CudaBackend.MarkerFile);
            if (File.Exists(marker)) File.Delete(marker); // a half-replaced folder is never "installed"
            foreach (var f in package.Files) File.Move(Path.Combine(staging, f.Name), Path.Combine(dir, f.Name), overwrite: true);
            var json = JsonSerializer.Serialize(new
            {
                format = 1, version = package.Version, cuda = package.Cuda, library = package.Library,
                files = package.Files.Select(f => new { name = f.Name, size = f.Size, sha256 = f.Sha256 }),
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(marker + ".tmp", json);
            File.Move(marker + ".tmp", marker, overwrite: true);
            // The archive is no longer needed (hundreds of MB).
            try { Directory.Delete(Path.GetDirectoryName(zipPath)!, true); } catch (IOException) { }
            Progress?.Invoke(new DownloadProgress(CudaBackendInstaller.PackageId, DownloadStage.Completed, package.Library, package.Files.Count - 1, package.Files.Count, total, total, 0, TimeSpan.Zero));
        }
        catch (ModelDownloadException e) when (e.Code == "package_invalid")
        {
            // The archive matched its SHA-256 but not the file list: downloading it again would not help. Drop it.
            TryDelete(staging);
            TryDelete(Path.GetDirectoryName(zipPath)!);
            throw;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            TryDelete(staging);
            var full = e is IOException io && ((io.HResult & 0xFFFF) is 0x70 or 0x27 || io.HResult == 28 || io.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase));
            throw new ModelDownloadException(full ? "disk_full" : e is InvalidDataException ? "package_invalid" : "io_error",
                $"The CUDA backend could not be unpacked: {e.Message}", e);
        }
        catch (OperationCanceledException)
        {
            TryDelete(staging); // the verified archive stays: the next press unpacks it without downloading
            throw;
        }
        finally { TryDelete(staging); }

        void Report(string name, int index, long bytes) =>
            Progress?.Invoke(new DownloadProgress(CudaBackendInstaller.PackageId, DownloadStage.Installing, name, index, package.Files.Count, bytes, total, 0, null));
    }

    static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
