using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Blazma.AI;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// "AI models" scenario against a local fake HTTP server (tiny files with real SHA-256, one tier deliberately corrupted): hardware and the
/// recommended badge, licence gating, progress, cancel and resume, success applying the settings so Kimodo becomes available, and the
/// friendly hash-mismatch error. English and Arabic; screenshots p10-lang-dark-models-what.png. Shortcut: BLAZMA_ONLY_MODELS=1.
/// </summary>
public partial class SelfTest
{
    static HardwareReport FakePc(double ramGiB, string? gpu, double vramGiB) => new("Linux", "x64", 8, (long)(ramGiB * HardwareReport.GiB),
        gpu is null ? Array.Empty<GpuInfo>() : new[] { new GpuInfo(gpu, "NVIDIA", (long)(vramGiB * HardwareReport.GiB), "test", false) }, Array.Empty<string>());

    sealed class FakeHost : IDisposable
    {
        readonly HttpListener _l = new();
        public int Port;
        public readonly ConcurrentQueue<string> Ranges = new();
        public readonly Dictionary<string, byte[]> Files = new();
        public HashSet<string> Slow = new();
        public FakeHost()
        {
            var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); Port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
            _l.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _l.Start();
            _ = Task.Run(Serve);
        }
        async Task Serve()
        {
            while (true)
            {
                HttpListenerContext c;
                try { c = await _l.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Handle(c));
            }
        }
        async Task Handle(HttpListenerContext c)
        {
            var res = c.Response;
            try
            {
                var path = c.Request.Url!.AbsolutePath;
                if (!Files.TryGetValue(path, out var body)) { res.StatusCode = 404; res.Close(); return; }
                long from = 0;
                if (c.Request.Headers["Range"] is { } r && r.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    from = long.Parse(r[6..].TrimEnd('-'));
                    Ranges.Enqueue(path + " " + r);
                    res.StatusCode = 206;
                    res.Headers["Content-Range"] = $"bytes {from}-{body.Length - 1}/{body.Length}";
                }
                res.ContentLength64 = body.Length - from;
                var pos = (int)from;
                while (pos < body.Length)
                {
                    var n = Math.Min(Slow.Contains(path) ? 20_000 : 1 << 20, body.Length - pos);
                    await res.OutputStream.WriteAsync(body.AsMemory(pos, n));
                    await res.OutputStream.FlushAsync();
                    pos += n;
                    if (Slow.Contains(path)) await Task.Delay(100);
                }
                res.Close();
            }
            catch { try { res.Abort(); } catch { } }
        }
        public void Dispose() { try { _l.Close(); } catch { } }
    }

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    async Task RunModels()
    {
        var s = App.Settings;
        var saved = (s.KimodoRunner, s.KimodoModels, s.KimodoDevice, s.KimodoTextModel);
        var kimodo = App.Kimodo!;
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        _w.ShowAgentPanel(false);
        _w.NewProjectNow();
        await Settle();
        var fake = FakeRunnerPath();
        s.KimodoRunner = fake; s.KimodoModels = ""; s.KimodoTextModel = ""; s.KimodoDevice = "auto";
        kimodo.Settings = new KimodoSettings { RunnerPath = fake };

        // ---- fake server + test catalog (the real tiers' names, tiny files) ----
        using var host = new FakeHost();
        byte[] Rnd(int n) => System.Security.Cryptography.RandomNumberGenerator.GetBytes(n);
        var motion = Rnd(300_000); var tok = Rnd(5_000); var textL = Rnd(40_000); var textS = Rnd(60_000); var textR = Rnd(80_000);
        host.Files["/motion"] = motion; host.Files["/tok"] = tok; host.Files["/text-l"] = textL; host.Files["/text-r"] = textR;
        host.Files["/text-s"] = textS.Select(b => (byte)~b).ToArray(); // corrupted on purpose: the declared hash is of the good bytes
        host.Slow.Add("/motion");
        var def = ModelCatalog.Default;
        ModelFile F(string id, string name, string route, byte[] good, string lic) =>
            new(id, "test/repo", "rev", name, name, good.Length, Sha(good), lic) { OverrideUrl = new Uri($"http://127.0.0.1:{host.Port}{route}") };
        var fm = F("m", "kimodo-soma-rp-v1.1-f32.gguf", "/motion", motion, "nvidia-open-model");
        var ft = F("t", "tokenizer.gguf", "/tok", tok, "meta-llama-3");
        ModelTier Tier(string id, string nameEn, string nameAr, ModelFile text, double ram, double vram, bool gpu, ModelTier basis) =>
            basis with { Id = id, NameEn = nameEn, NameAr = nameAr, TextFile = text, Files = new[] { fm, text, ft }, MinRamGiB = ram, RecommendedRamGiB = ram, MinVramGiB = vram, RecommendedVramGiB = vram, AutoNeedsGpu = gpu };
        var tiers = new[]
        {
            Tier("light", "Light (test)", "خفيف (اختبار)", F("tl", "Llama-3-Kimodo-Q4_K_M.gguf", "/text-l", textL, "meta-llama-3"), 6, 6, false, def.Tiers[0]),
            Tier("standard", "Standard (test, corrupted)", "قياسي (اختبار، تالف)", F("ts", "Llama-3-Kimodo-Q8_0.gguf", "/text-s", textS, "meta-llama-3"), 12, 8, true, def.Tiers[1]),
            Tier("reference", "Reference (test)", "مرجعي (اختبار)", F("tr", "Llama-3-Kimodo-BF16.gguf", "/text-r", textR, "meta-llama-3"), 24, 16, true, def.Tiers[2]),
        };
        var catalog = new ModelCatalog { Tiers = tiers, Licences = def.Licences, Runner = def.Runner, Verified = def.Verified, MemoryNote = def.MemoryNote };

        var dlg = _w.AiModelsDlg;
        var weak = FakePc(8, null, 0);
        var strong = FakePc(32, "NVIDIA GeForce RTX 4090", 24);
        var pc = weak;
        var probeSleep = 400;
        dlg.Catalog = catalog;
        dlg.ProbeFn = _ => { Thread.Sleep(probeSleep); return pc; };
        dlg.DownloaderFactory = () => new ModelDownloader(new ModelDownloaderOptions { AllowLoopbackHttp = true, AllowedHosts = new[] { "127.0.0.1" }, FreeSpaceMarginBytes = 1_000_000 });

        // ---- the entry points ----
        _w.OpenGenerateDialog();
        _w.GenerateDlg.SelectProvider(AiText.KimodoId);
        await Settle();
        Check("models Generate dialog offers 'Download AI model…' while Kimodo is not installed", () =>
            _w.GenerateDlg.DownloadModel.Visible && _w.GenerateDlg.Setup.Visible ? null : "buttons hidden");
        PressButton(_w.GenerateDlg.DownloadModel);
        await Settle();
        Check("models pressing it opens the AI models window", () => dlg.IsOpen && dlg.Current == AiModelsDialog.View.List ? null : "not open");
        dlg.Hide();
        await Settle();
        _w.OpenAiSettings();
        await Settle();
        Check("models AI settings has a 'Download AI model…' button that opens the window", () =>
        {
            PressButton(_w.AiSettingsDlg.ModelsButton);
            return dlg.IsOpen && !_w.AiSettingsDlg.IsOpen ? null : "not open";
        });
        dlg.Hide();
        await Settle();

        foreach (var lang in new[] { "en", "ar" })
        {
            App.SetLanguage(lang); App.SetTheme("dark");
            await Settle();
            var en = lang == "en";
            var dir = System.IO.Path.Combine(_dir, "models-" + lang);
            System.IO.Directory.CreateDirectory(dir);
            s.KimodoModels = ""; s.KimodoTextModel = "";
            kimodo.Settings = new KimodoSettings { RunnerPath = fake };
            dlg.ModelsDirOverride = dir;
            host.Slow.Add("/motion");
            pc = weak;
            var probes0 = dlg.ProbesDone;
            var frames0 = Engine.GetProcessFrames();
            _w.OpenAiModels();
            await WaitFor(() => dlg.ProbesDone > probes0, 10);
            var frames = Engine.GetProcessFrames() - frames0;
            await Settle();
            Check($"models {lang} hardware is probed off the main thread (frames kept being drawn)", () => frames >= 10 ? null : $"frames={frames}");
            Check($"models {lang} hardware shows 'no dedicated GPU' and RAM", () =>
                dlg.GpuText.Length > 0 && dlg.HardwareOtherText.Contains("8.0 GB") && dlg.GpuText == TranslationServer.Translate("No dedicated GPU detected") ? null : $"{dlg.GpuText} // {dlg.HardwareOtherText}");
            Check($"models {lang} the advisor's choice carries the 'Recommended' badge, only it", () =>
                dlg.Recommendation?.TierId == "light" && dlg.Cards["light"].Badge.Visible && !dlg.Cards["standard"].Badge.Visible && !dlg.Cards["reference"].Badge.Visible ? null : $"rec={dlg.Recommendation?.TierId}");
            Check($"models {lang} tiers that do not fit warn but stay selectable", () =>
                dlg.Cards["standard"].Fit.Text.Contains(TranslationServer.Translate("May be slow or fail: too big for this PC. You can still download it.")) && !dlg.Cards["standard"].Action.Disabled
                && !dlg.Cards["light"].Fit.Text.Contains(TranslationServer.Translate("May be slow")) ? null : dlg.Cards["standard"].Fit.Text);
            Check($"models {lang} device defaults to CPU for this PC", () => dlg.SelectedDevice == "cpu" ? null : dlg.SelectedDevice);
            if (en)
            {
                pc = strong; probes0 = dlg.ProbesDone;
                PressButton(dlg.RefreshButton);
                Check("models Refresh disables the button while probing", () => dlg.RefreshButton.Disabled ? null : "enabled");
                await WaitFor(() => dlg.ProbesDone > probes0, 10);
                await Settle();
                Check("models Refresh shows the new GPU with its video memory and moves the badge", () =>
                    dlg.GpuText.Contains("RTX 4090") && dlg.GpuText.Contains("24.0 GB") && dlg.Cards["reference"].Badge.Visible && !dlg.Cards["light"].Badge.Visible ? null : $"{dlg.GpuText} rec={dlg.Recommendation?.TierId}");
                pc = weak; probes0 = dlg.ProbesDone;
                PressButton(dlg.RefreshButton);
                await WaitFor(() => dlg.ProbesDone > probes0, 10);
                await Settle();
            }
            Shot(P($"p10-{lang}-dark-models-dialog"), dlg);

            // ---- confirmation + licence gating ----
            PressButton(dlg.Cards["light"].Action);
            await Settle();
            Check($"models {lang} confirmation shows size, free space, folder and both licences", () =>
                dlg.Current == AiModelsDialog.View.Confirm && dlg.Plan is { } p && dlg.ConfirmSizes.Contains(AiModelsDialog.Size(p.BytesTotal)) && dlg.ConfirmSpace.Contains(" GB")
                && dlg.DirEdit.Text == dir && dlg.LicenceChecks.Count == 2 ? null : $"{dlg.Current} {dlg.ConfirmSizes} {dlg.ConfirmSpace} {dlg.LicenceChecks.Count}");
            Check($"models {lang} Download stays disabled until every licence is ticked", () =>
            {
                if (!dlg.StartButton.Disabled) return "enabled at start";
                dlg.LicenceChecks[0].ButtonPressed = true;
                if (!dlg.StartButton.Disabled) return "enabled after one";
                dlg.LicenceChecks[1].ButtonPressed = true;
                return dlg.StartButton.Disabled ? "still disabled" : null;
            });
            Shot(P($"p10-{lang}-dark-models-confirm"), dlg);

            if (en)
            {
                // ---- cancel + resume ----
                PressButton(dlg.StartButton);
                var running = await WaitFor(() => dlg.Bar.Value > 5 && dlg.Downloading, 20);
                await Settle();
                Check("models progress shows file, percent, MB done/total, speed and ETA", () =>
                    running && dlg.Current == AiModelsDialog.View.Progress && dlg.ProgressFileText.Contains("kimodo-soma") && dlg.ProgressBytesText.Contains(" of ") && dlg.ProgressSpeedText.Contains("/s") && dlg.ProgressSpeedText.Contains("Time left")
                        ? null : $"{running} {dlg.Current} {dlg.ProgressFileText} // {dlg.ProgressBytesText} // {dlg.ProgressSpeedText}");
                Shot(P($"p10-{lang}-dark-models-downloading"), dlg);
                var runs = dlg.RunsDone;
                PressButton(dlg.CancelButton);
                await WaitFor(() => dlg.RunsDone > runs, 20);
                await Settle();
                Check("models Cancel stops the download and keeps the partial file", () =>
                    dlg.LastCancelled && System.IO.Directory.GetFiles(dir, "*.part").Length > 0 && !System.IO.File.Exists(System.IO.Path.Combine(dir, "kimodo-soma-rp-v1.1-f32.gguf")) ? null : $"cancelled={dlg.LastCancelled}");
                PressButton(dlg.DoneButton);
                await Settle();
                Check("models the card now offers Resume", () => dlg.Cards["light"].Resumable && dlg.Cards["light"].Action.Text == "Resume" ? null : dlg.Cards["light"].Action.Text);
                PressButton(dlg.Cards["light"].Action);
                await Settle();
                Check("models the confirmation counts only what is left", () => dlg.Plan is { } p && p.BytesToDownload < p.BytesTotal ? null : "full size");
                foreach (var c in dlg.LicenceChecks) c.ButtonPressed = true;
                host.Slow.Clear();
            }
            else
            {
                PressButton(dlg.StartButton);
                await WaitFor(() => dlg.Bar.Value > 5 && dlg.Downloading, 20);
                await Settle();
                Shot(P($"p10-{lang}-dark-models-downloading"), dlg);
            }
            if (en) PressButton(dlg.StartButton);
            var r0 = dlg.RunsDone;
            await WaitFor(() => dlg.RunsDone > r0 || !dlg.Downloading, 30);
            await WaitFor(() => dlg.LastInstall is not null, 30);
            await Settle();
            Check($"models {lang} the download finishes with all files verified", () =>
                dlg.LastInstall is { } i && i.TierId == "light" && System.IO.File.Exists(System.IO.Path.Combine(dir, "Llama-3-Kimodo-Q4_K_M.gguf")) && dlg.Bar.Value == 100 ? null : $"err={dlg.LastErrorCode} {dlg.ErrorText}");
            if (en) Check("models resume used an HTTP Range request", () => host.Ranges.Any(x => x.StartsWith("/motion bytes=", StringComparison.Ordinal)) ? null : "no range request");
            Check($"models {lang} success applies and saves the settings (folder, text encoder, device)", () =>
            {
                var again = new AppSettings(); again.Load();
                var k = App.Kimodo!.Settings;
                if (s.KimodoModels != dir || s.KimodoTextModel != "Llama-3-Kimodo-Q4_K_M.gguf" || s.KimodoDevice != "cpu") return $"{s.KimodoModels} {s.KimodoTextModel} {s.KimodoDevice}";
                if (again.KimodoModels != dir || again.KimodoTextModel != "Llama-3-Kimodo-Q4_K_M.gguf") return "not persisted";
                return k.ModelDirectory == dir && k.TextModel == "Llama-3-Kimodo-Q4_K_M.gguf" && k.Device == "cpu" ? null : $"kimodo {k.ModelDirectory} {k.TextModel}";
            });
            Check($"models {lang} Kimodo shows as available at once", () =>
                App.MotionJobs.FindProvider(AiText.KimodoId)?.Describe().Available == true ? null : App.MotionJobs.FindProvider(AiText.KimodoId)?.Describe().UnavailableReason);
            Shot(P($"p10-{lang}-dark-models-done"), dlg);
            PressButton(dlg.DoneButton);
            await Settle();
            Check($"models {lang} the card shows Installed", () => dlg.Cards["light"].Installed && dlg.Cards["light"].InstalledLabel.Visible && !dlg.Cards["light"].Action.Visible ? null : "not installed");

            if (en)
            {
                // ---- hash mismatch ----
                var dir2 = System.IO.Path.Combine(_dir, "models-bad");
                System.IO.Directory.CreateDirectory(dir2);
                dlg.ModelsDirOverride = dir2;
                s.KimodoModels = dir; // the card state follows the folder the confirmation uses
                PressButton(dlg.Cards["standard"].Action);
                await Settle();
                dlg.DirEdit.Text = dir2;
                foreach (var c in dlg.LicenceChecks) c.ButtonPressed = true;
                await Settle();
                var r1 = dlg.RunsDone;
                PressButton(dlg.StartButton);
                await WaitFor(() => dlg.RunsDone > r1, 30);
                await Settle();
                Check("models a corrupted file shows the friendly hash_mismatch message", () =>
                    dlg.LastErrorCode == "hash_mismatch" && dlg.ErrorText == TranslationServer.Translate(dlg.ErrorText) && dlg.ErrorText.Contains("checksum") && !dlg.ErrorText.Contains("hash_mismatch") ? null : $"{dlg.LastErrorCode} {dlg.ErrorText}");
                Check("models the bad file is not left in the folder and settings are unchanged", () =>
                    !System.IO.File.Exists(System.IO.Path.Combine(dir2, "Llama-3-Kimodo-Q8_0.gguf")) && s.KimodoTextModel == "Llama-3-Kimodo-Q4_K_M.gguf" ? null : "bad file kept / settings changed");
                Shot(P($"p10-{lang}-dark-models-error"), dlg);
                PressButton(dlg.DoneButton);
            }
            dlg.Hide();
            await Settle();
        }

        Check("models Arabic: every message of the dialog has a translation", () =>
        {
            App.SetLanguage("ar");
            foreach (var code in new[] { "disk_full", "hash_mismatch", "size_mismatch", "redirect_refused", "file_conflict", "insecure_url", "http_error", "network_error", "incomplete", "busy", "io_error" })
            {
                var m = AiModelsDialog.Friendly(code, "x");
                if (!m.Any(ch => ch >= 0x600 && ch <= 0x6FF)) return code + " is not translated: " + m;
            }
            return null;
        });

        s.KimodoRunner = saved.KimodoRunner; s.KimodoModels = saved.KimodoModels; s.KimodoDevice = saved.KimodoDevice; s.KimodoTextModel = saved.KimodoTextModel;
        App.SetLanguage("en");
    }
}
