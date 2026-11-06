using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Blazma.AI;

namespace Blazma.Tests.AI;

/// <summary>ModelDownloader against a local HttpListener (plain http on loopback is allowed only through the internal test option).</summary>
public sealed class ModelDownloaderTests : IDisposable
{
    readonly HttpListener _listener = new();
    readonly int _port;
    readonly string _dir = Directory.CreateTempSubdirectory("blazma-dl-").FullName;
    readonly byte[] _content = RandomNumberGenerator.GetBytes(300_000);
    readonly ConcurrentQueue<(string Path, string? Range)> _requests = new();
    readonly CancellationTokenSource _stop = new();
    readonly SemaphoreSlim _slowGate = new(0);

    public ModelDownloaderTests()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        _port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Prefixes.Add($"http://localhost:{_port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _slowGate.Release(100);
        _listener.Close();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    async Task Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        var range = ctx.Request.Headers["Range"];
        _requests.Enqueue((path, range));
        var res = ctx.Response;
        try
        {
            switch (path)
            {
                case "/redirect":
                    res.StatusCode = 302; res.RedirectLocation = "/file"; res.Close(); return;
                case "/evil":
                    res.StatusCode = 302; res.RedirectLocation = $"http://localhost:{_port}/file"; res.Close(); return;
                case "/https-evil":
                    res.StatusCode = 302; res.RedirectLocation = "https://example.com/file"; res.Close(); return;
            }
            var body = path == "/bad" ? _content.Select(b => (byte)~b).ToArray() : _content;
            long from = 0;
            if (path != "/norange" && range is { } r && r.StartsWith("bytes=", StringComparison.Ordinal))
            {
                from = long.Parse(r[6..].TrimEnd('-'));
                res.StatusCode = 206;
                res.Headers["Content-Range"] = $"bytes {from}-{body.Length - 1}/{body.Length}";
            }
            res.ContentLength64 = body.Length - from;
            if (path == "/slow")
            {
                await res.OutputStream.WriteAsync(body.AsMemory((int)from, 50_000));
                await res.OutputStream.FlushAsync();
                await _slowGate.WaitAsync(_stop.Token); // hang until the test ends
            }
            await res.OutputStream.WriteAsync(body.AsMemory((int)from));
            res.Close();
        }
        catch (Exception e) when (e is HttpListenerException or IOException or OperationCanceledException or ObjectDisposedException) { try { res.Abort(); } catch (ObjectDisposedException) { } }
    }

    ModelTier Tier(string route = "/file", string? sha = null)
    {
        var hash = sha ?? Convert.ToHexString(SHA256.HashData(_content)).ToLowerInvariant();
        var f = new ModelFile("t", "r", new string('0', 40), "kimodo-soma-rp-v1.1-f32.gguf", "kimodo-soma-rp-v1.1-f32.gguf", _content.Length, hash, "lic")
        { OverrideUrl = new Uri($"http://127.0.0.1:{_port}{route}") };
        return new ModelTier("test", "Test", "اختبار", "soma-rp-v1.1", f, new[] { f }, 1, 1, 1, 1, false, "", "");
    }

    ModelDownloader Downloader(Func<string, long?>? free = null, bool replace = false) =>
        new(new ModelDownloaderOptions
        {
            AllowedHosts = new[] { "127.0.0.1" }, AllowedHostSuffixes = Array.Empty<string>(), AllowLoopbackHttp = true,
            FreeSpace = free ?? (_ => long.MaxValue / 2), FreeSpaceMarginBytes = 0, ReplaceMismatchedFiles = replace, StallTimeout = TimeSpan.FromSeconds(20),
        }, new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });

    static readonly string[] Lic = { "lic" };
    string Final => Path.Combine(_dir, "kimodo-soma-rp-v1.1-f32.gguf");

    [Fact]
    public async Task Downloads_Verifies_Renames_And_Configures_Kimodo()
    {
        using var d = Downloader();
        var events = new List<DownloadProgress>();
        d.Progress += events.Add;
        var r = await d.DownloadAsync(Tier("/redirect"), _dir, Lic, "cpu");
        Assert.Equal(_content, File.ReadAllBytes(Final));
        Assert.False(File.Exists(Final + ".part"));
        Assert.Contains(events, e => e.Stage == DownloadStage.Downloading);
        Assert.Contains(events, e => e.Stage == DownloadStage.Verifying);
        Assert.Equal(DownloadStage.Completed, events[^1].Stage);
        Assert.Equal(_content.Length, events[^1].BytesDone);
        var s = r.ApplyTo(new KimodoSettings { RunnerPath = "/x/kimodo-runner" });
        Assert.Equal(_dir, s.ModelDirectory);
        Assert.Equal("cpu", s.Device);
        Assert.Equal("/x/kimodo-runner", s.RunnerPath);
        Assert.Equal(Final, s.ResolveMotionModel());

        // A verified file is not downloaded again.
        var n = _requests.Count;
        var again = await d.DownloadAsync(Tier(), _dir, Lic);
        Assert.Equal(n, _requests.Count);
        Assert.Single(again.Skipped);
    }

    [Fact]
    public async Task Resumes_A_Part_File_With_Range()
    {
        File.WriteAllBytes(Final + ".part", _content[..100_000]);
        using var d = Downloader();
        await d.DownloadAsync(Tier(), _dir, Lic);
        Assert.Equal(_content, File.ReadAllBytes(Final));
        Assert.Contains(_requests, q => q.Range == "bytes=100000-");
    }

    [Fact]
    public async Task Server_Ignoring_Range_Restarts_From_Zero()
    {
        File.WriteAllBytes(Final + ".part", _content[..100_000]);
        using var d = Downloader();
        await d.DownloadAsync(Tier("/norange"), _dir, Lic);
        Assert.Equal(_content, File.ReadAllBytes(Final));
    }

    [Fact]
    public async Task Cancel_Keeps_The_Part_File_And_A_Later_Download_Resumes()
    {
        using var d = Downloader();
        using var cts = new CancellationTokenSource();
        // The server sends 50 000 bytes and then hangs: cancel a moment after the transfer started.
        d.Progress += p => { if (p.Stage == DownloadStage.Downloading) cts.CancelAfter(1000); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => d.DownloadAsync(Tier("/slow"), _dir, Lic, ct: cts.Token));
        Assert.False(File.Exists(Final));
        Assert.Equal(50_000, new FileInfo(Final + ".part").Length);
        Assert.False(d.IsBusy);
        await d.DownloadAsync(Tier(), _dir, Lic);
        Assert.Equal(_content, File.ReadAllBytes(Final));
        Assert.Contains(_requests, q => q.Path == "/file" && q.Range == "bytes=50000-");
    }

    [Fact]
    public async Task Bad_Hash_Deletes_The_Download()
    {
        using var d = Downloader();
        var e = await Assert.ThrowsAsync<ModelDownloadException>(() => d.DownloadAsync(Tier("/bad"), _dir, Lic));
        Assert.Equal("hash_mismatch", e.Code);
        Assert.False(File.Exists(Final));
        Assert.False(File.Exists(Final + ".part"));
    }

    [Theory]
    [InlineData("/evil")]       // another host
    [InlineData("/https-evil")] // https, but not allowlisted
    public async Task Redirect_To_A_Host_Not_Allowed_Is_Refused(string route)
    {
        using var d = Downloader();
        var e = await Assert.ThrowsAsync<ModelDownloadException>(() => d.DownloadAsync(Tier(route), _dir, Lic));
        Assert.Equal("redirect_refused", e.Code);
        Assert.DoesNotContain(_requests, q => q.Path == "/file");
    }

    [Fact]
    public async Task Default_Options_Refuse_Http_And_Other_Hosts()
    {
        using var d = new ModelDownloader();
        var e = await Assert.ThrowsAsync<ModelDownloadException>(() => d.DownloadAsync(Tier(), _dir, Lic));
        Assert.Equal("insecure_url", e.Code);
        var f = Tier().Files[0] with { OverrideUrl = new Uri("https://example.com/x.gguf") };
        var t = Tier() with { Files = new[] { f }, TextFile = f };
        Assert.Equal("host_not_allowed", (await Assert.ThrowsAsync<ModelDownloadException>(() => d.DownloadAsync(t, _dir, Lic))).Code);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Not_Enough_Disk_Space_Fails_Before_Any_Request()
    {
        using var d = Downloader(free: _ => 1000);
        var plan = d.Plan(Tier(), _dir);
        Assert.False(plan.EnoughSpace);
        Assert.Equal(_content.Length, plan.BytesToDownload);
        var e = await Assert.ThrowsAsync<ModelDownloadException>(() => d.DownloadAsync(Tier(), _dir, Lic));
        Assert.Equal("disk_full", e.Code);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Licences_Must_Be_Accepted()
    {
        using var d = Downloader();
        var e = await Assert.ThrowsAsync<ModelDownloadException>(() => d.DownloadAsync(Tier(), _dir, Array.Empty<string>()));
        Assert.Equal("licence_not_accepted", e.Code);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task A_Different_Existing_File_Is_Not_Replaced_Silently()
    {
        File.WriteAllBytes(Final, new byte[] { 1, 2, 3 });
        using var d = Downloader();
        var e = await Assert.ThrowsAsync<ModelDownloadException>(() => d.DownloadAsync(Tier(), _dir, Lic));
        Assert.Equal("file_conflict", e.Code);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Final));
        Assert.Equal(new[] { "kimodo-soma-rp-v1.1-f32.gguf" }, d.Plan(Tier(), _dir).Conflicts);

        using var replace = Downloader(replace: true);
        await replace.DownloadAsync(Tier(), _dir, Lic);
        Assert.Equal(_content, File.ReadAllBytes(Final));
    }
}
