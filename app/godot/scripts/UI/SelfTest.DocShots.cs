using System.Net;
using System.Net.Sockets;
using Blazma.AI;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Documentation captures. BLAZMA_DOC_EXPORT_DIR (optional) makes the export scenarios write under a neutral folder, so the dialogs show a
/// normal user path. BLAZMA_ONLY_DOCMODELS=1 captures the AI models window with the real catalogue (real tier names and sizes) and this
/// machine's real hardware probe; the progress screen streams a file of the true size from a local host and is cancelled afterwards.
/// </summary>
public sealed partial class SelfTest
{
    static string? DocExportDir(string sub = "")
    {
        if (System.Environment.GetEnvironmentVariable("BLAZMA_DOC_EXPORT_DIR") is not { Length: > 0 } root) return null;
        return sub.Length == 0 ? root : System.IO.Path.Combine(root, sub);
    }

    /// <summary>A loopback HTTP host that streams zero bytes of a declared length at a fixed pace (for the progress screen only).</summary>
    sealed class PacedHost : IDisposable
    {
        readonly HttpListener _l = new();
        public int Port;
        public readonly Dictionary<string, long> Sizes = new();
        public PacedHost()
        {
            var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); Port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop();
            _l.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _l.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    HttpListenerContext c;
                    try { c = await _l.GetContextAsync(); } catch { return; }
                    _ = Task.Run(() => Handle(c));
                }
            });
        }
        async Task Handle(HttpListenerContext c)
        {
            var res = c.Response;
            try
            {
                if (!Sizes.TryGetValue(c.Request.Url!.AbsolutePath, out var size)) { res.StatusCode = 404; res.Close(); return; }
                long from = 0;
                if (c.Request.Headers["Range"] is { } r && r.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    from = long.Parse(r[6..].TrimEnd('-'));
                    res.StatusCode = 206;
                    res.Headers["Content-Range"] = $"bytes {from}-{size - 1}/{size}";
                }
                res.ContentLength64 = size - from;
                var buf = new byte[1 << 20];
                for (var pos = from; pos < size; pos += buf.Length)
                {
                    await res.OutputStream.WriteAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, size - pos)));
                    await res.OutputStream.FlushAsync();
                    await Task.Delay(25);
                }
                res.Close();
            }
            catch { try { res.Abort(); } catch { } }
        }
        public void Dispose() { try { _l.Close(); } catch { } }
    }

    async Task RunDocModels()
    {
        var s = App.Settings;
        var def = ModelCatalog.Default;
        using var host = new PacedHost();
        var files = def.Tiers.SelectMany(t => t.Files).DistinctBy(f => f.Id).ToList();
        foreach (var f in files) host.Sizes["/" + f.Id] = f.Size;
        ModelFile Route(ModelFile f) => f with { OverrideUrl = new Uri($"http://127.0.0.1:{host.Port}/{f.Id}") };
        var tiers = def.Tiers.Select(t => t with { Files = t.Files.Select(Route).ToArray(), TextFile = Route(t.TextFile) }).ToArray();
        var catalog = new ModelCatalog { Tiers = tiers, Licences = def.Licences, Runner = def.Runner, Verified = def.Verified, MemoryNote = def.MemoryNote };

        var dlg = _w.AiModelsDlg;
        dlg.Catalog = catalog;
        dlg.DownloaderFactory = () => new ModelDownloader(new ModelDownloaderOptions { AllowLoopbackHttp = true, AllowedHosts = new[] { "127.0.0.1" }, FreeSpaceMarginBytes = 1_000_000 });
        foreach (var lang in new[] { "en", "ar" })
        {
            App.SetLanguage(lang); App.SetTheme("dark"); App.SetAdvancedMode(false);
            _w.ShowAgentPanel(false);
            _w.NewProjectNow();
            s.KimodoModels = ""; s.KimodoTextModel = "";
            dlg.ModelsDirOverride = null;
            await Settle();
            _w.OpenAiModels();
            await WaitFor(() => dlg.ProbesDone > 0, 30);
            await Settle();
            GD.Print($"DOCMODELS {lang} hardware: {dlg.GpuText} | {dlg.HardwareOtherText}");
            Shot(P($"p10-{lang}-dark-models-dialog"), dlg);
            var tier = dlg.Recommendation?.TierId ?? "light";
            PressButton(dlg.Cards[tier].Action);
            await Settle();
            GD.Print($"DOCMODELS {lang} confirm: {dlg.ConfirmSizes} | {dlg.ConfirmSpace} | {dlg.DirEdit.Text}");
            foreach (var c in dlg.LicenceChecks) c.ButtonPressed = true;
            await Settle();
            Shot(P($"p10-{lang}-dark-models-confirm"), dlg);
            PressButton(dlg.StartButton);
            await WaitFor(() => dlg.Downloading, 60);
            await ToSignal(GetTree().CreateTimer(15), SceneTreeTimer.SignalName.Timeout);
            await Settle();
            GD.Print($"DOCMODELS {lang} progress: {dlg.ProgressBytesText} | {dlg.ProgressSpeedText}");
            Shot(P($"p10-{lang}-dark-models-downloading"), dlg);
            var runs = dlg.RunsDone;
            PressButton(dlg.CancelButton);
            await WaitFor(() => dlg.RunsDone > runs, 60);
            await Settle();
            var target = dlg.DirEdit.Text;
            dlg.Hide();
            await Settle();
            // The partial files are ours (zeros streamed by the local host): remove them so no fake model is left behind.
            try { foreach (var f in System.IO.Directory.GetFiles(target, "*.part")) System.IO.File.Delete(f); } catch { }
        }
    }
}
