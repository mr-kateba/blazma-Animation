using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Blazma.AI;

namespace Blazma.Tests.AI;

/// <summary>The optional CUDA backend (DECISIONS D47): package description, download through ModelDownloader from a local
/// HttpListener (the same rules as the model downloads), unpacking with per-file checks, state, removal, and how kimodo-runner
/// is pointed at it (GGML_BACKEND_PATH + library search path). No real CUDA files are involved.</summary>
public sealed class CudaBackendTests : IDisposable
{
    readonly HttpListener _listener = new();
    readonly int _port;
    readonly string _root = Directory.CreateTempSubdirectory("blazma-cuda-").FullName;
    readonly ConcurrentQueue<(string Path, string? Range)> _requests = new();
    readonly CancellationTokenSource _stop = new();
    readonly SemaphoreSlim _slowGate = new(0);
    readonly Dictionary<string, byte[]> _files = new() { ["ggml-cuda.dll"] = RandomNumberGenerator.GetBytes(200_000), ["cudart64_12.dll"] = RandomNumberGenerator.GetBytes(40_000), ["NVIDIA_CUDA_NOTICE.txt"] = "notice"u8.ToArray() };
    readonly byte[] _zip;

    public CudaBackendTests()
    {
        _zip = Zip(_files, "blazma-cuda-backend-9.9.9-win-x64/");
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        _port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _slowGate.Release(100);
        _listener.Close();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    static byte[] Zip(IReadOnlyDictionary<string, byte[]> files, string prefix)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            z.CreateEntry(prefix); // folder entry, as `zip -r` writes it
            foreach (var (name, body) in files)
            {
                using var s = z.CreateEntry(prefix + name, CompressionLevel.Fastest).Open();
                s.Write(body);
            }
        }
        return ms.ToArray();
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
            if (path == "/releases/download/cuda.zip") { res.StatusCode = 302; res.RedirectLocation = "/asset/cuda.zip"; res.Close(); return; }
            var body = path == "/asset/bad.zip" ? _zip.Select(b => (byte)~b).ToArray() : _zip;
            long from = 0;
            if (range is { } r && r.StartsWith("bytes=", StringComparison.Ordinal))
            {
                from = long.Parse(r[6..].TrimEnd('-'));
                res.StatusCode = 206;
                res.Headers["Content-Range"] = $"bytes {from}-{body.Length - 1}/{body.Length}";
            }
            res.ContentLength64 = body.Length - from;
            if (path == "/asset/slow.zip")
            {
                await res.OutputStream.WriteAsync(body.AsMemory((int)from, 30_000));
                await res.OutputStream.FlushAsync();
                await _slowGate.WaitAsync(_stop.Token);
            }
            await res.OutputStream.WriteAsync(body.AsMemory((int)from));
            res.Close();
        }
        catch (Exception e) when (e is HttpListenerException or IOException or OperationCanceledException or ObjectDisposedException) { try { res.Abort(); } catch (ObjectDisposedException) { } }
    }

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    string Json(string route = "/releases/download/cuda.zip", Func<string, string>? fileSha = null, string version = "9.9.9") =>
        new JsonObject
        {
            ["format"] = 1, ["version"] = version, ["cuda"] = "12.4", ["asset"] = $"blazma-cuda-backend-{version}-win-x64.zip",
            ["url"] = $"http://127.0.0.1:{_port}{route}", ["size"] = _zip.Length, ["sha256"] = Sha(_zip), ["library"] = "ggml-cuda.dll",
            ["files"] = new JsonArray(_files.Select(kv => (JsonNode?)new JsonObject
            {
                ["name"] = kv.Key, ["size"] = kv.Value.Length, ["sha256"] = fileSha?.Invoke(kv.Key) ?? Sha(kv.Value),
            }).ToArray()),
        }.ToJsonString();

    CudaBackendPackage Package(string route = "/releases/download/cuda.zip", Func<string, string>? fileSha = null) => CudaBackendPackage.Parse(Json(route, fileSha));

    static ModelDownloader Downloader() => new(new ModelDownloaderOptions
    {
        AllowedHosts = new[] { "127.0.0.1" }, AllowedHostSuffixes = Array.Empty<string>(), AllowLoopbackHttp = true,
        FreeSpace = _ => long.MaxValue / 2, FreeSpaceMarginBytes = 0, ReplaceMismatchedFiles = true, StallTimeout = TimeSpan.FromSeconds(20),
    }, new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });

    string Dir => Path.Combine(_root, "runtime", "cuda");

    /// <summary>A fake runner folder with its cuda-backend.json (the layout build-release.sh produces).</summary>
    string Runner(string? json = null, bool bundledCuda = false)
    {
        var d = Directory.CreateDirectory(Path.Combine(_root, "tools", "kimodo")).FullName;
        var exe = Path.Combine(d, "kimodo-runner.exe");
        File.WriteAllText(exe, "fake");
        if (json is not null) File.WriteAllText(Path.Combine(d, CudaBackendPackage.FileName), json);
        if (bundledCuda) File.WriteAllText(Path.Combine(d, "ggml-cuda.dll"), "bundled");
        return exe;
    }

    [Fact]
    public async Task Downloads_Through_A_Redirect_Verifies_Unpacks_And_Becomes_Installed()
    {
        var p = Package();
        using var dl = Downloader();
        var installer = new CudaBackendInstaller(dl);
        var events = new List<DownloadProgress>();
        installer.Progress += events.Add;
        var state = await installer.InstallAsync(p, Dir);

        Assert.Equal(CudaBackendStatus.Installed, state.Status);
        Assert.Equal("9.9.9", state.InstalledVersion);
        foreach (var (name, body) in _files) Assert.Equal(body, File.ReadAllBytes(Path.Combine(Dir, name)));
        Assert.True(File.Exists(Path.Combine(Dir, CudaBackend.MarkerFile)));
        Assert.False(Directory.Exists(Path.Combine(Dir, ".download"))); // the archive is deleted once unpacked
        Assert.False(Directory.Exists(Path.Combine(Dir, ".staging")));
        Assert.Contains(events, e => e.Stage == DownloadStage.Downloading);
        Assert.Contains(events, e => e.Stage == DownloadStage.Verifying);
        Assert.Contains(events, e => e.Stage == DownloadStage.Installing);
        Assert.Equal(DownloadStage.Completed, events[^1].Stage);
        Assert.All(events, e => Assert.Equal(CudaBackendInstaller.PackageId, e.TierId));
        Assert.Contains(_requests, r => r.Path == "/asset/cuda.zip");
        Assert.Equal(Path.Combine(Dir, "ggml-cuda.dll"), CudaBackend.ResolveLibrary(Dir));
        Assert.Equal(_files.Values.Sum(b => (long)b.Length), state.InstalledBytes);
    }

    [Fact]
    public async Task Cancel_Keeps_The_Partial_Archive_And_The_Next_Press_Resumes_With_Range()
    {
        using var dl = Downloader();
        var installer = new CudaBackendInstaller(dl);
        using var cts = new CancellationTokenSource();
        installer.Progress += e => { if (e.Stage == DownloadStage.Downloading) cts.CancelAfter(800); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(Package("/asset/slow.zip"), Dir, cts.Token));
        var st = CudaBackend.State(Package(), Dir, null);
        Assert.Equal(CudaBackendStatus.NotInstalled, st.Status);
        Assert.Equal(30_000, st.PartialBytes);
        Assert.True(st.CanRemove);
        Assert.Null(CudaBackend.ResolveLibrary(Dir));

        var done = await new CudaBackendInstaller(dl).InstallAsync(Package("/asset/cuda.zip"), Dir);
        Assert.Equal(CudaBackendStatus.Installed, done.Status);
        Assert.Contains(_requests, r => r.Path == "/asset/cuda.zip" && r.Range == "bytes=30000-");
    }

    [Fact]
    public async Task A_Corrupted_Archive_Is_Thrown_Away_And_Nothing_Is_Installed()
    {
        using var dl = Downloader();
        var e = await Assert.ThrowsAsync<ModelDownloadException>(() => new CudaBackendInstaller(dl).InstallAsync(Package("/asset/bad.zip"), Dir));
        Assert.Equal("hash_mismatch", e.Code);
        Assert.Equal(CudaBackendStatus.NotInstalled, CudaBackend.State(Package(), Dir, null).Status);
        Assert.False(File.Exists(Path.Combine(Dir, "ggml-cuda.dll")));
    }

    [Fact]
    public async Task An_Archive_Whose_Files_Do_Not_Match_The_List_Is_Refused_Without_Touching_The_Folder()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, "keep-me.txt"), "user file");
        var wrong = Package(fileSha: n => n == "cudart64_12.dll" ? new string('a', 64) : Sha(_files[n]));
        using var dl = Downloader();
        var e = await Assert.ThrowsAsync<ModelDownloadException>(() => new CudaBackendInstaller(dl).InstallAsync(wrong, Dir));
        Assert.Equal("package_invalid", e.Code);
        Assert.False(File.Exists(Path.Combine(Dir, "ggml-cuda.dll")));
        Assert.False(File.Exists(Path.Combine(Dir, CudaBackend.MarkerFile)));
        Assert.False(Directory.Exists(Path.Combine(Dir, ".staging")));
        Assert.False(Directory.Exists(Path.Combine(Dir, ".download"))); // re-downloading the same archive would not help
        Assert.True(File.Exists(Path.Combine(Dir, "keep-me.txt")));
    }

    [Fact]
    public async Task Remove_Deletes_Only_What_The_Installer_Put_There()
    {
        using var dl = Downloader();
        await new CudaBackendInstaller(dl).InstallAsync(Package(), Dir);
        File.WriteAllText(Path.Combine(Dir, "keep-me.txt"), "user file");
        var freed = CudaBackend.Remove(Dir);
        Assert.True(freed >= _files.Values.Sum(b => (long)b.Length));
        foreach (var name in _files.Keys) Assert.False(File.Exists(Path.Combine(Dir, name)), name);
        Assert.False(File.Exists(Path.Combine(Dir, CudaBackend.MarkerFile)));
        Assert.True(File.Exists(Path.Combine(Dir, "keep-me.txt")));
        Assert.Equal(CudaBackendStatus.NotInstalled, CudaBackend.State(Package(), Dir, null).Status);

        File.Delete(Path.Combine(Dir, "keep-me.txt"));
        await new CudaBackendInstaller(dl).InstallAsync(Package(), Dir);
        CudaBackend.Remove(Dir);
        Assert.False(Directory.Exists(Dir)); // empty afterwards: the folder goes too
        Assert.Equal(0, CudaBackend.Remove(Dir));
    }

    [Fact]
    public async Task State_Tells_Installed_Outdated_Bundled_And_NotOffered_Apart()
    {
        using var dl = Downloader();
        Assert.Equal(CudaBackendStatus.NotOffered, CudaBackend.State(null, Dir, null).Status);
        Assert.Equal(CudaBackendStatus.NotInstalled, CudaBackend.State(Package(), Dir, null).Status);
        await new CudaBackendInstaller(dl).InstallAsync(Package(), Dir);
        Assert.Equal(CudaBackendStatus.Installed, CudaBackend.State(Package(), Dir, null).Status);
        // A newer runner build ships a different ggml-cuda: the old download must not be loaded with it.
        var newer = Package(fileSha: n => n == "ggml-cuda.dll" ? new string('b', 64) : Sha(_files[n]));
        Assert.Equal(CudaBackendStatus.Outdated, CudaBackend.State(newer, Dir, null).Status);
        Assert.Equal(CudaBackendStatus.Bundled, CudaBackend.State(Package(), Dir, Runner(bundledCuda: true)).Status);
        // A file that went missing (antivirus, manual deletion) means "not installed".
        File.Delete(Path.Combine(Dir, "cudart64_12.dll"));
        Assert.Equal(CudaBackendStatus.NotInstalled, CudaBackend.State(Package(), Dir, null).Status);
    }

    [Fact]
    public async Task The_Runner_Gets_GGML_BACKEND_PATH_And_The_Library_Folder_Only_When_Installed()
    {
        var runner = Runner(Json());
        var s = new KimodoSettings { RunnerPath = runner, CudaBackendDirectory = Dir };
        var host = new System.Collections.Hashtable { ["Path"] = "C:\\Windows", ["LD_LIBRARY_PATH"] = "/usr/lib" };
        Assert.Null(s.ResolveCudaBackend());
        Assert.False(RunnerProcess.RunnerEnvironment(s, host, 1, windows: true).ContainsKey("GGML_BACKEND_PATH"));

        using var dl = Downloader();
        await new CudaBackendInstaller(dl).InstallAsync(Package(), Dir);
        var lib = Path.Combine(Dir, "ggml-cuda.dll");
        Assert.Equal(lib, s.ResolveCudaBackend());
        var win = RunnerProcess.RunnerEnvironment(s, host, 1, windows: true);
        Assert.Equal(lib, win["GGML_BACKEND_PATH"]);
        Assert.Equal(Dir + ";C:\\Windows", win["path"]);
        var linux = RunnerProcess.RunnerEnvironment(s, host, 1, windows: false);
        Assert.Equal(Dir + ":/usr/lib", linux["LD_LIBRARY_PATH"]);

        // The user's own GGML_BACKEND_PATH wins; a runner with its own CUDA backend never loads a second one.
        var mine = RunnerProcess.RunnerEnvironment(s, new System.Collections.Hashtable { ["GGML_BACKEND_PATH"] = "C:\\x.dll" }, 1, windows: true);
        Assert.Equal("C:\\x.dll", mine["GGML_BACKEND_PATH"]);
        Runner(Json(), bundledCuda: true);
        Assert.Null(s.ResolveCudaBackend());
        Assert.Contains(Dir, s.KnownPaths());
    }

    [Fact]
    public async Task WithInstalledCudaBackend_Picks_Up_Only_A_Download_That_Matches_The_Runner()
    {
        var runner = Runner(Json());
        var s = new KimodoSettings { RunnerPath = runner };
        Assert.Null(s.WithInstalledCudaBackend(Dir).CudaBackendDirectory);
        using var dl = Downloader();
        await new CudaBackendInstaller(dl).InstallAsync(Package(), Dir);
        Assert.Equal(Dir, s.WithInstalledCudaBackend(Dir).CudaBackendDirectory);
        // The runner was updated to a build with another CUDA backend: the old download is ignored until replaced.
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(runner)!, CudaBackendPackage.FileName), Json(fileSha: n => n == "ggml-cuda.dll" ? new string('c', 64) : Sha(_files[n])));
        Assert.Null(s.WithInstalledCudaBackend(Dir).CudaBackendDirectory);
        // An explicit folder (BLAZMA_KIMODO_CUDA_DIR) is kept as is.
        Assert.Equal("/elsewhere", (s with { CudaBackendDirectory = "/elsewhere" }).WithInstalledCudaBackend(Dir).CudaBackendDirectory);
    }

    [Fact]
    public void Package_Description_Is_Loaded_From_Next_To_The_Runner_And_Validated()
    {
        Assert.Null(CudaBackendPackage.Load(Runner()));
        var p = CudaBackendPackage.Load(Runner(Json()));
        Assert.NotNull(p);
        Assert.Equal("ggml-cuda.dll", p!.Library);
        Assert.Equal(_zip.Length, p.Size);
        Assert.Equal(3, p.Files.Count);
        Assert.Null(CudaBackendPackage.Load(Runner("{ not json")));

        string Bad(Action<JsonObject> edit) { var o = JsonNode.Parse(Json())!.AsObject(); edit(o); return o.ToJsonString(); }
        Assert.Throws<InvalidDataException>(() => CudaBackendPackage.Parse(Bad(o => o["files"]![0]!["name"] = "../evil.dll")));
        Assert.Throws<InvalidDataException>(() => CudaBackendPackage.Parse(Bad(o => o["files"]![0]!["name"] = "sub\\evil.dll")));
        Assert.Throws<InvalidDataException>(() => CudaBackendPackage.Parse(Bad(o => o["sha256"] = "xyz")));
        Assert.Throws<InvalidDataException>(() => CudaBackendPackage.Parse(Bad(o => o["library"] = "other.dll")));
        Assert.Throws<InvalidDataException>(() => CudaBackendPackage.Parse(Bad(o => o["asset"] = "cuda.exe")));
        Assert.Throws<InvalidDataException>(() => CudaBackendPackage.Parse(Bad(o => o["format"] = 2)));
    }

    [Fact]
    public async Task Release_Options_Only_Download_From_GitHub_Over_Https()
    {
        using var dl = new ModelDownloader(ModelDownloaderOptions.GitHubReleases());
        var installer = new CudaBackendInstaller(dl);
        var p = Package();
        var http = await Assert.ThrowsAsync<ModelDownloadException>(() => installer.InstallAsync(p with { Url = new Uri("http://github.com/x/cuda.zip") }, Dir));
        Assert.Equal("insecure_url", http.Code);
        var other = await Assert.ThrowsAsync<ModelDownloadException>(() => installer.InstallAsync(p with { Url = new Uri("https://example.com/cuda.zip") }, Dir));
        Assert.Equal("host_not_allowed", other.Code);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Not_Enough_Space_For_Archive_Plus_Unpacked_Files_Fails_Before_Any_Request()
    {
        var p = Package();
        using var dl = new ModelDownloader(new ModelDownloaderOptions
        {
            AllowedHosts = new[] { "127.0.0.1" }, AllowLoopbackHttp = true, FreeSpaceMarginBytes = 0,
            FreeSpace = _ => p.Size + p.InstalledBytes - 1,
        });
        var e = await Assert.ThrowsAsync<ModelDownloadException>(() => new CudaBackendInstaller(dl).InstallAsync(p, Dir));
        Assert.Equal("disk_full", e.Code);
        Assert.Empty(_requests);
    }

    [Fact]
    public void Offer_And_Hint_Need_An_NVIDIA_Card()
    {
        var nvidia = new HardwareReport("Windows", "X64", 8, 16 * HardwareReport.GiB, new[] { new GpuInfo("NVIDIA GeForce RTX 3060", "NVIDIA", 12 * HardwareReport.GiB, "registry", false) }, Array.Empty<string>());
        var amd = nvidia with { Gpus = new[] { new GpuInfo("AMD Radeon RX 7600", "AMD", 8 * HardwareReport.GiB, "registry", false) } };
        var missing = new CudaBackendState(CudaBackendStatus.NotInstalled, Dir, null, 0, 0);
        Assert.True(CudaBackend.ShouldOffer(missing, nvidia));
        Assert.True(CudaBackend.ShouldOffer(missing with { Status = CudaBackendStatus.Outdated }, nvidia));
        Assert.False(CudaBackend.ShouldOffer(missing, amd));
        foreach (var st in new[] { CudaBackendStatus.Installed, CudaBackendStatus.Bundled, CudaBackendStatus.NotOffered })
            Assert.False(CudaBackend.ShouldOffer(missing with { Status = st }, nvidia));

        var vk = new KimodoDevice("Vulkan0", "NVIDIA GeForce RTX 3060", "gpu", "Vulkan", 12L << 30, 11L << 30);
        var cpu = new KimodoDevice("CPU", "Intel Core i7", "cpu", "CPU", 0, 0);
        var cuda = new KimodoDevice("CUDA0", "NVIDIA GeForce RTX 3060", "gpu", "CUDA", 12L << 30, 11L << 30);
        Assert.True(new KimodoDeviceUse("gpu", vk, vk).CudaWouldHelp(new[] { vk, cpu }));
        Assert.False(new KimodoDeviceUse("gpu", vk, vk).CudaWouldHelp(new[] { cuda, vk, cpu })); // CUDA there, Vulkan chosen on purpose
        Assert.False(new KimodoDeviceUse("gpu", cuda, cuda).CudaWouldHelp(new[] { cuda, cpu }));
        Assert.False(new KimodoDeviceUse("cpu", cpu, cpu).CudaWouldHelp(new[] { cpu }));
        var radeon = vk with { Description = "AMD Radeon RX 7600" };
        Assert.False(new KimodoDeviceUse("gpu", radeon, radeon).CudaWouldHelp(new[] { radeon, cpu }));
    }

    [Fact]
    public void Default_Directory_Is_Next_To_The_Models_Folder()
    {
        Assert.Equal(Path.Combine(Path.GetDirectoryName(ModelCatalog.DefaultModelsDirectory())!, "runtime", "cuda"), CudaBackend.DefaultDirectory());
    }
}
