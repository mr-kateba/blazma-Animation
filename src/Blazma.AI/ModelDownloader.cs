using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Blazma.AI;

public enum DownloadStage { Checking, Downloading, Verifying, Completed }

/// <summary>Progress of a package download. Bytes are for the whole package; files already in place count as done.</summary>
public sealed record DownloadProgress(
    string TierId, DownloadStage Stage, string FileName, int FileIndex, int FileCount,
    long BytesDone, long BytesTotal, double BytesPerSecond, TimeSpan? Eta)
{
    public double Fraction => BytesTotal <= 0 ? 0 : Math.Clamp(BytesDone / (double)BytesTotal, 0, 1);
}

/// <summary>A failed download. <see cref="Code"/>: licence_not_accepted, busy, insecure_url, host_not_allowed, redirect_refused,
/// too_many_redirects, http_error, network_error, incomplete, size_mismatch, hash_mismatch, file_conflict, disk_full, io_error.
/// A cancelled download throws <see cref="OperationCanceledException"/> instead; its .part files stay for resuming.</summary>
public sealed class ModelDownloadException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>What pressing "Download" would do: shown to the user (size, free space, licences) before they confirm.</summary>
public sealed record DownloadPlan(ModelTier Tier, string ModelDirectory, long BytesTotal, long BytesToDownload, long? FreeBytes,
    bool EnoughSpace, IReadOnlyList<ModelLicence> Licences, IReadOnlyList<string> Conflicts);

/// <summary>A finished install. <see cref="ApplyTo"/> points Kimodo at it.</summary>
public sealed record ModelInstallResult(string TierId, string ModelDirectory, string MotionModel, string TextModelFile, string Device,
    IReadOnlyList<string> Files, IReadOnlyList<string> Skipped)
{
    /// <summary>The settings with this package selected (folder, motion model, text encoder file, device). The runner path is kept.</summary>
    public KimodoSettings ApplyTo(KimodoSettings s) => s with { ModelDirectory = ModelDirectory, Model = MotionModel, TextModel = TextModelFile, Device = Device };
}

public sealed class ModelDownloaderOptions
{
    /// <summary>Hosts allowed exactly (the start of every download).</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = new[] { "huggingface.co" };

    /// <summary>Host suffixes allowed for redirects: Hugging Face's LFS/Xet CDNs (cdn-lfs*.huggingface.co, cas-bridge.xethub.hf.co,
    /// us.aws.cdn.hf.co, …). Anything else is refused.</summary>
    public IReadOnlyList<string> AllowedHostSuffixes { get; init; } = new[] { ".huggingface.co", ".hf.co" };

    /// <summary>Replace an existing file whose size or SHA-256 is wrong. Off by default: the UI asks the user first.</summary>
    public bool ReplaceMismatchedFiles { get; init; }

    /// <summary>Kept free on the disk after the download.</summary>
    public long FreeSpaceMarginBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>A read that gets no bytes for this long fails with network_error (the .part file stays).</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public int MaxRedirects { get; init; } = 10;

    /// <summary>Free bytes of the disk holding a folder (tests replace it).</summary>
    public Func<string, long?> FreeSpace { get; init; } = DiskFree;

    /// <summary>Tests only: plain http to 127.0.0.1/localhost (a local fake server).</summary>
    internal bool AllowLoopbackHttp { get; init; }

    static long? DiskFree(string dir)
    {
        try { return new DriveInfo(Path.GetFullPath(dir)).AvailableFreeSpace; }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// Downloads a catalog package, only when the user asks (DECISIONS D43): HTTPS to allowlisted Hugging Face hosts (redirects followed
/// by hand and only to allowlisted HTTPS hosts), resumable (<c>.part</c> + Range), cancellable, disk space checked first,
/// SHA-256 verified, then renamed into place atomically. A verified file is never replaced; a wrong one only with
/// <see cref="ModelDownloaderOptions.ReplaceMismatchedFiles"/>. One download at a time per instance.
/// </summary>
public sealed class ModelDownloader : IDisposable
{
    readonly ModelDownloaderOptions _o;
    readonly HttpClient _http;
    int _busy;

    /// <summary>Raised on the downloading thread (at most ~5 times a second, plus every stage change).</summary>
    public event Action<DownloadProgress>? Progress;

    public ModelDownloader(ModelDownloaderOptions? options = null, HttpMessageHandler? handler = null)
    {
        _o = options ?? new ModelDownloaderOptions();
        handler ??= new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(30) };
        if (handler is SocketsHttpHandler sh && sh.AllowAutoRedirect) throw new ArgumentException("the handler must not follow redirects itself", nameof(handler));
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BlazmaAnimation-ModelDownloader/1");
    }

    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    public void Dispose() => _http.Dispose();

    // ------------------------------------------------------------------ plan (no network)

    public DownloadPlan Plan(ModelTier tier, string modelsDirectory, ModelCatalog? catalog = null)
    {
        catalog ??= ModelCatalog.Default;
        var dir = Path.GetFullPath(modelsDirectory);
        long todo = 0;
        var conflicts = new List<string>();
        foreach (var f in tier.Files)
        {
            var final = Path.Combine(dir, f.FileName);
            if (File.Exists(final))
            {
                if (new FileInfo(final).Length != f.Size) { conflicts.Add(f.FileName); todo += f.Size; }
                continue; // same size: hashed when downloading
            }
            var part = final + ".part";
            todo += f.Size - (File.Exists(part) ? Math.Min(new FileInfo(part).Length, f.Size) : 0);
        }
        var free = _o.FreeSpace(ExistingAncestor(dir));
        var licences = tier.LicenceIds.Select(id => catalog.Licences.TryGetValue(id, out var l) ? l : new ModelLicence(id, id, "", "", "")).ToList();
        return new DownloadPlan(tier, dir, tier.TotalBytes, todo, free, free is null || free >= todo + _o.FreeSpaceMarginBytes, licences, conflicts);
    }

    static string ExistingAncestor(string dir)
    {
        var d = dir;
        while (!Directory.Exists(d) && Path.GetDirectoryName(d) is { } parent && parent != d) d = parent;
        return d;
    }

    // ------------------------------------------------------------------ download

    /// <param name="acceptedLicences">Licence ids the user accepted in the dialog; every licence of the tier must be in it.</param>
    /// <param name="device">Device to configure afterwards: auto | gpu | cpu | hybrid (usually the advisor's choice; "vulkan" = gpu).</param>
    public async Task<ModelInstallResult> DownloadAsync(ModelTier tier, string modelsDirectory, IEnumerable<string> acceptedLicences,
        string device = "auto", CancellationToken ct = default)
    {
        var missing = tier.LicenceIds.Except(acceptedLicences, StringComparer.Ordinal).ToList();
        if (missing.Count > 0) throw new ModelDownloadException("licence_not_accepted", "The user has not accepted: " + string.Join(", ", missing) + ".");
        device = KimodoSettings.NormalizeDevice(device);
        if (!KimodoSettings.DeviceModes.Contains(device)) throw new ArgumentException("device must be auto, gpu, cpu or hybrid", nameof(device));
        if (Interlocked.Exchange(ref _busy, 1) != 0) throw new ModelDownloadException("busy", "A download is already running.");
        try
        {
            foreach (var f in tier.Files) CheckUrl(f.Url, initial: true);
            var dir = Path.GetFullPath(modelsDirectory);
            try { Directory.CreateDirectory(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ModelDownloadException("io_error", $"Cannot create '{dir}': {e.Message}", e); }

            var plan = Plan(tier, dir);
            if (!plan.EnoughSpace)
                throw new ModelDownloadException("disk_full", $"Not enough free disk space: {Mb(plan.BytesToDownload + _o.FreeSpaceMarginBytes)} needed, {Mb(plan.FreeBytes ?? 0)} free.");

            var meter = new Meter(this, tier, plan.BytesTotal);
            var skipped = new List<string>();
            for (var i = 0; i < tier.Files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = tier.Files[i];
                meter.File(f.FileName, i);
                if (await InstallFileAsync(f, dir, meter, ct).ConfigureAwait(false)) skipped.Add(f.FileName);
            }
            meter.Report(DownloadStage.Completed, force: true);
            return new ModelInstallResult(tier.Id, dir, tier.MotionModel, tier.TextFile.FileName, device, tier.Files.Select(f => f.FileName).ToList(), skipped);
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    /// <summary>True when the file was already there and verified.</summary>
    async Task<bool> InstallFileAsync(ModelFile f, string dir, Meter meter, CancellationToken ct)
    {
        var final = Path.Combine(dir, f.FileName);
        var part = final + ".part";
        try
        {
            if (File.Exists(final))
            {
                meter.Report(DownloadStage.Checking, force: true);
                if (new FileInfo(final).Length == f.Size && await HashFileAsync(final, meter, ct).ConfigureAwait(false) == f.Sha256)
                {
                    meter.FileDone(f.Size);
                    return true; // verified: never touched again
                }
                if (!_o.ReplaceMismatchedFiles)
                    throw new ModelDownloadException("file_conflict", $"'{f.FileName}' already exists but is not the expected file (size or SHA-256 differs). It was left alone; remove it or allow replacing it.");
                meter.Rewind();
                File.Delete(final);
            }

            for (var attempt = 0; ; attempt++)
            {
                long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (have > f.Size) { File.Delete(part); have = 0; }
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var fs = new FileStream(part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous))
                {
                    if (have > 0)
                    {
                        meter.Report(DownloadStage.Checking, force: true);
                        await HashStreamAsync(fs, have, sha, ct).ConfigureAwait(false); // resuming: the bytes already there count towards the hash
                    }
                    meter.Advance(have);
                    if (have < f.Size)
                    {
                        var restart = await FetchAsync(f, fs, have, sha, meter, ct).ConfigureAwait(false);
                        if (restart)
                        {
                            if (attempt > 0) throw new ModelDownloadException("http_error", $"The server refused to resume '{f.FileName}' twice.");
                            meter.Advance(-fs.Length);
                            fs.SetLength(0);
                            continue;
                        }
                    }
                    if (fs.Length != f.Size)
                        throw new ModelDownloadException("incomplete", $"'{f.FileName}' stopped at {fs.Length} of {f.Size} bytes. Press Download again to resume.");
                }
                meter.Report(DownloadStage.Verifying, force: true);
                var hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                if (hash != f.Sha256)
                {
                    File.Delete(part);
                    meter.Advance(-f.Size);
                    throw new ModelDownloadException("hash_mismatch", $"'{f.FileName}' was downloaded but its SHA-256 ({hash}) is not the published one ({f.Sha256}). It was deleted.");
                }
                File.Move(part, final, overwrite: false); // atomic on the same volume; fails rather than replacing a file that appeared meanwhile
                return false;
            }
        }
        catch (IOException e) when (e is not FileNotFoundException && IsDiskFull(e))
        { throw new ModelDownloadException("disk_full", $"The disk is full while writing '{f.FileName}'. The partial file was kept for resuming.", e); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new ModelDownloadException("io_error", $"'{f.FileName}': {e.Message}", e); }
    }

    static bool IsDiskFull(IOException e) => (e.HResult & 0xFFFF) is 0x70 or 0x27 || e.HResult == 28 || e.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase);

    /// <summary>Streams the file from <paramref name="offset"/> into <paramref name="fs"/>. True = the server ignored the range
    /// (the caller restarts from 0).</summary>
    async Task<bool> FetchAsync(ModelFile f, FileStream fs, long offset, IncrementalHash sha, Meter meter, CancellationToken ct)
    {
        using var response = await SendAsync(f.Url, offset, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return offset > 0;
        if (!response.IsSuccessStatusCode)
            throw new ModelDownloadException("http_error", $"Downloading '{f.FileName}' failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        if (offset > 0)
        {
            if (response.StatusCode != HttpStatusCode.PartialContent) return true;
            if (response.Content.Headers.ContentRange is not { From: { } from } || from != offset) return true;
        }
        else if (response.StatusCode == HttpStatusCode.PartialContent) return true;
        if (response.Content.Headers.ContentLength is { } len && offset + len != f.Size)
            throw new ModelDownloadException("size_mismatch", $"The server sends {offset + len} bytes for '{f.FileName}', the catalog says {f.Size}.");

        fs.Seek(offset, SeekOrigin.Begin);
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[1 << 17];
        long pos = offset;
        meter.Report(DownloadStage.Downloading, force: true);
        while (true)
        {
            int n;
            using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                stall.CancelAfter(_o.StallTimeout);
                try { n = await body.ReadAsync(buf, stall.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw new ModelDownloadException("network_error", $"No data for {_o.StallTimeout.TotalSeconds:0} s while downloading '{f.FileName}'. Press Download again to resume."); }
                catch (Exception e) when (e is HttpRequestException or IOException && !ct.IsCancellationRequested)
                { throw new ModelDownloadException("network_error", $"The connection broke while downloading '{f.FileName}' ({e.Message}). Press Download again to resume.", e); }
            }
            if (n == 0) break;
            if (pos + n > f.Size) throw new ModelDownloadException("size_mismatch", $"The server sent more than {f.Size} bytes for '{f.FileName}'.");
            await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            sha.AppendData(buf, 0, n);
            pos += n;
            meter.Advance(n);
            meter.Report(DownloadStage.Downloading);
        }
        await fs.FlushAsync(ct).ConfigureAwait(false);
        return false;
    }

    async Task<HttpResponseMessage> SendAsync(Uri url, long offset, CancellationToken ct)
    {
        for (var hop = 0; ; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (offset > 0) req.Headers.Range = new RangeHeaderValue(offset, null);
            HttpResponseMessage r;
            try { r = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
            catch (HttpRequestException e) { throw new ModelDownloadException("network_error", $"Cannot reach {url.Host}: {e.Message}", e); }
            if ((int)r.StatusCode is not (301 or 302 or 303 or 307 or 308)) return r;
            var location = r.Headers.Location;
            r.Dispose();
            if (location is null) throw new ModelDownloadException("http_error", $"{url.Host} answered a redirect without a location.");
            if (hop >= _o.MaxRedirects) throw new ModelDownloadException("too_many_redirects", $"More than {_o.MaxRedirects} redirects from {url.Host}.");
            var next = location.IsAbsoluteUri ? location : new Uri(url, location);
            CheckUrl(next, initial: false);
            url = next;
        }
    }

    void CheckUrl(Uri url, bool initial)
    {
        var loopback = _o.AllowLoopbackHttp && url.Scheme == Uri.UriSchemeHttp && url.IsLoopback;
        if (url.Scheme != Uri.UriSchemeHttps && !loopback)
            throw new ModelDownloadException(initial ? "insecure_url" : "redirect_refused", $"Refusing a non-HTTPS {(initial ? "address" : "redirect")}: {url.Scheme}://{url.Host}.");
        var host = url.IdnHost.ToLowerInvariant();
        var ok = _o.AllowedHosts.Any(h => host == h) || (!initial && _o.AllowedHostSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal) && host.Length > s.Length));
        if (!ok) throw new ModelDownloadException(initial ? "host_not_allowed" : "redirect_refused", $"Refusing to download from '{host}': only Hugging Face hosts are allowed.");
    }

    async Task<string> HashFileAsync(string path, Meter meter, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await HashStreamAsync(fs, fs.Length, sha, ct).ConfigureAwait(false);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    static async Task HashStreamAsync(Stream s, long length, IncrementalHash sha, CancellationToken ct)
    {
        s.Seek(0, SeekOrigin.Begin);
        var buf = new byte[1 << 20];
        long left = length;
        while (left > 0)
        {
            var n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("the file shrank while it was read");
            sha.AppendData(buf, 0, n);
            left -= n;
        }
    }

    static string Mb(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";

    /// <summary>Package byte counter, speed over the last few seconds, throttled reports.</summary>
    sealed class Meter(ModelDownloader owner, ModelTier tier, long total)
    {
        long _done, _fileStart;
        string _file = "";
        int _index;
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly Queue<(double t, long bytes)> _window = new();
        double _lastReport = double.NegativeInfinity;

        public void File(string name, int index) { _file = name; _index = index; _fileStart = _done; }
        public void FileDone(long size) => _done = _fileStart + size;
        public void Rewind() => _done = _fileStart;
        public void Advance(long n) => _done = Math.Max(_fileStart, _done + n);

        public void Report(DownloadStage stage, bool force = false)
        {
            var now = _clock.Elapsed.TotalSeconds;
            if (!force && now - _lastReport < 0.2) return;
            _lastReport = now;
            _window.Enqueue((now, _done));
            while (_window.Count > 2 && now - _window.Peek().t > 5) _window.Dequeue();
            var (t0, b0) = _window.Peek();
            var speed = now - t0 > 0.1 ? Math.Max(0, (_done - b0) / (now - t0)) : 0;
            TimeSpan? eta = speed > 1 && stage == DownloadStage.Downloading ? TimeSpan.FromSeconds((total - _done) / speed) : null;
            if (stage == DownloadStage.Completed) { _done = total; eta = TimeSpan.Zero; }
            owner.Progress?.Invoke(new DownloadProgress(tier.Id, stage, _file, _index, tier.Files.Count, _done, total, speed, eta));
        }
    }
}
