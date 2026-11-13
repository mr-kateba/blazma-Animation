using System.IO.Compression;
using System.Text.Json.Nodes;
using Blazma.AI;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Optional CUDA backend (DECISIONS D47) in Settings → AI, against the local fake HTTP server of the models scenario (a tiny zip with
/// real SHA-256s, no CUDA files): the section appears only for an NVIDIA card, nothing is downloaded before the button is pressed,
/// progress, cancel and resume, install (Kimodo's settings then point at it), remove. English and Arabic; screenshots
/// p12-lang-dark-cuda-what.png. Shortcut: BLAZMA_ONLY_CUDA=1.
/// </summary>
public partial class SelfTest
{
    async Task RunCuda()
    {
        var dlg = _w.AiSettingsDlg;
        var s = App.Settings;
        var saved = (s.KimodoRunner, s.KimodoModels, s.KimodoDevice, s.KimodoTextModel);
        var fake = FakeRunnerPath();
        s.KimodoRunner = fake;
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        await Settle();

        using var host = new FakeHost();
        var files = new Dictionary<string, byte[]>
        {
            ["ggml-cuda.dll"] = System.Security.Cryptography.RandomNumberGenerator.GetBytes(1_500_000),
            ["cudart64_12.dll"] = System.Security.Cryptography.RandomNumberGenerator.GetBytes(50_000),
            ["NVIDIA_CUDA_NOTICE.txt"] = "notice"u8.ToArray(),
        };
        byte[] zip;
        using (var ms = new System.IO.MemoryStream())
        {
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (n, b) in files)
                {
                    using var e = z.CreateEntry("blazma-cuda-backend-test-win-x64/" + n, CompressionLevel.NoCompression).Open();
                    e.Write(b);
                }
            zip = ms.ToArray();
        }
        host.Files["/cuda.zip"] = zip;
        host.Slow.Add("/cuda.zip");
        var pkg = CudaBackendPackage.Parse(new JsonObject
        {
            ["format"] = 1, ["version"] = "test", ["cuda"] = "12.4", ["asset"] = "blazma-cuda-backend-test-win-x64.zip",
            ["url"] = $"http://127.0.0.1:{host.Port}/cuda.zip", ["size"] = zip.Length, ["sha256"] = Sha(zip), ["library"] = "ggml-cuda.dll",
            ["files"] = new JsonArray(files.Select(kv => (JsonNode?)new JsonObject { ["name"] = kv.Key, ["size"] = kv.Value.Length, ["sha256"] = Sha(kv.Value) }).ToArray()),
        }.ToJsonString());
        var dir = System.IO.Path.Combine(_dir, "cuda-runtime");
        App.CudaDirectoryOverride = dir;
        App.CudaPackageOverride = pkg;
        var nvidia = FakePc(16, "NVIDIA GeForce RTX 3060", 12);
        var amd = new HardwareReport("Windows", "X64", 8, 16 * HardwareReport.GiB, new[] { new GpuInfo("AMD Radeon RX 7600", "AMD", 8 * HardwareReport.GiB, "test", false) }, Array.Empty<string>());
        var pc = nvidia;
        dlg.CudaHardwareFn = () => pc;
        dlg.CudaDownloaderFactory = () => new ModelDownloader(new ModelDownloaderOptions { AllowLoopbackHttp = true, AllowedHosts = new[] { "127.0.0.1" }, FreeSpaceMarginBytes = 1_000_000, ReplaceMismatchedFiles = true });
        try
        {
            foreach (var lang in new[] { "en", "ar" })
            {
                App.SetLanguage(lang); App.SetTheme("dark");
                await Settle();
                var en = lang == "en";
                pc = amd;
                var ranges0 = host.Ranges.Count;
                _w.OpenAiSettings();
                await Settle();
                Check($"cuda {lang} no NVIDIA card: no GPU acceleration section", () => !dlg.CudaSectionVisible ? null : dlg.CudaLineText);
                pc = nvidia;
                dlg.RefreshCuda();
                await Settle();
                Check($"cuda {lang} NVIDIA card: the download button shows the size, nothing is downloaded yet", () =>
                    dlg.CudaSectionVisible && dlg.CudaButton.Visible && dlg.CudaButton.Text.Contains(AiModelsDialog.FormatSize(zip.Length)) && dlg.CudaButton.Text.Contains("CUDA")
                    && !System.IO.Directory.Exists(dir) && host.Ranges.Count == ranges0 && !dlg.CudaRemoveButton.Visible ? null : $"{dlg.CudaSectionVisible} {dlg.CudaButton.Visible} '{dlg.CudaButton.Text}' exists={System.IO.Directory.Exists(dir)}");
                if (!en)
                    Check("cuda ar the section is translated", () =>
                        HasArabic(dlg.CudaButton.Text) && HasArabic(dlg.CudaLineText) && HasArabic(TranslationServer.Translate(AiText.CudaHint))
                        && new[] { "package_invalid", "host_not_allowed", "io_error", "hash_mismatch" }.All(c => HasArabic(AiSettingsDialog.CudaFriendly(c, "x"))) ? null : dlg.CudaButton.Text);
                ScrollToEnd(dlg);
                await Settle();
                Shot(P($"p12-{lang}-dark-cuda-offer"), dlg);

                if (en)
                {
                    PressButton(dlg.CudaButton);
                    var running = await WaitFor(() => dlg.CudaBar.Value > 5 && dlg.CudaDownloading, 20);
                    await Settle();
                    Check("cuda pressing the button shows progress and Cancel", () =>
                        running && dlg.CudaBar.Visible && dlg.CudaCancelButton.Visible && !dlg.CudaButton.Visible && dlg.CudaProgressText.Contains("/s") ? null : $"{running} '{dlg.CudaProgressText}'");
                    ScrollToEnd(dlg);
                    await Settle();
                    Shot(P($"p12-{lang}-dark-cuda-downloading"), dlg);
                    var runs = dlg.CudaRunsDone;
                    PressButton(dlg.CudaCancelButton);
                    await WaitFor(() => dlg.CudaRunsDone > runs, 20);
                    await Settle();
                    Check("cuda Cancel stops, keeps the partial download and says pressing again continues", () =>
                        dlg.CudaLastCancelled && App.CudaState().PartialBytes > 0 && dlg.CudaButton.Visible && dlg.CudaLineText.Contains("continues where it stopped")
                        && App.EffectiveKimodoSettings().CudaBackendDirectory is null ? null : $"{dlg.CudaLastCancelled} partial={App.CudaState().PartialBytes} '{dlg.CudaLineText}'");
                    host.Slow.Clear();
                }
                var r0 = dlg.CudaRunsDone;
                PressButton(dlg.CudaButton);
                await WaitFor(() => dlg.CudaRunsDone > r0, 30);
                await Settle();
                Check($"cuda {lang} the download is verified, unpacked and Kimodo is pointed at it", () =>
                    dlg.CudaLastError is null && App.CudaState().Status == CudaBackendStatus.Installed && System.IO.File.Exists(System.IO.Path.Combine(dir, "ggml-cuda.dll"))
                    && App.EffectiveKimodoSettings().CudaBackendDirectory == dir && App.Kimodo!.Settings.CudaBackendDirectory == dir
                    ? null : $"err={dlg.CudaLastError} {dlg.CudaProgressText} state={App.CudaState().Status}");
                if (en) Check("cuda resuming used an HTTP Range request", () => host.Ranges.Any(x => x.StartsWith("/cuda.zip bytes=", StringComparison.Ordinal)) ? null : "no range request");
                Check($"cuda {lang} installed: no download button, Remove offered", () =>
                    !dlg.CudaButton.Visible && dlg.CudaRemoveButton.Visible && dlg.CudaLineText.Contains("CUDA") ? null : dlg.CudaLineText);
                ScrollToEnd(dlg);
                await Settle();
                Shot(P($"p12-{lang}-dark-cuda-installed"), dlg);

                PressButton(dlg.CudaRemoveButton);
                await Settle();
                Check($"cuda {lang} Remove deletes the files and Kimodo goes back to its own backends", () =>
                    !System.IO.File.Exists(System.IO.Path.Combine(dir, "ggml-cuda.dll")) && App.CudaState().Status == CudaBackendStatus.NotInstalled
                    && App.Kimodo!.Settings.CudaBackendDirectory is null && dlg.CudaButton.Visible ? null : $"state={App.CudaState().Status}");
                dlg.Hide();
                await Settle();
            }
        }
        finally
        {
            App.CudaDirectoryOverride = null;
            App.CudaPackageOverride = null;
            dlg.CudaHardwareFn = () => HardwareProbe.Probe();
            dlg.CudaDownloaderFactory = () => new ModelDownloader(ModelDownloaderOptions.GitHubReleases());
            s.KimodoRunner = saved.KimodoRunner; s.KimodoModels = saved.KimodoModels; s.KimodoDevice = saved.KimodoDevice; s.KimodoTextModel = saved.KimodoTextModel;
            App.CudaBackendChanged();
            App.SetLanguage("en");
            await Settle();
        }
    }


    static void ScrollToEnd(Node dlg)
    {
        foreach (var c in dlg.GetChildren())
            if (c is ScrollContainer sc) sc.ScrollVertical = 100_000;
    }
}
