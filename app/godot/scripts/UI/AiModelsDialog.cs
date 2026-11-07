using System.Globalization;
using Blazma.AI;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// "AI models" (DECISIONS D43): shows what this PC has, which Kimodo package fits it, and downloads one only when the user asks:
/// pick a package, confirm size / free space / folder / licences, watch the progress, cancel and resume later. Three views in one
/// window: the list (hardware + package cards), the confirmation, and the progress. Hardware is probed off the main thread and
/// download progress (raised on the download thread) is picked up in <see cref="_Process"/>.
/// </summary>
public partial class AiModelsDialog : AcceptDialog
{
    public enum View { List, Confirm, Progress }

    /// <summary>One package card; the fields are public for the UI tests.</summary>
    public sealed class Card
    {
        public ModelTier Tier = null!;
        public PanelContainer Panel = null!;
        public Label Name = null!, Info = null!, Fit = null!, Badge = null!, Reason = null!, InstalledLabel = null!;
        public Button Action = null!;
        public bool Installed, Resumable;
    }

    // ---- test hooks ---------------------------------------------------------------------------------------------------
    public ModelCatalog Catalog { get; set; } = ModelCatalog.Default;
    public Func<bool, HardwareReport> ProbeFn { get; set; } = HardwareProbe.Probe;
    public Func<ModelDownloader> DownloaderFactory { get; set; } = () => new ModelDownloader();
    /// <summary>When set, the confirmation starts with this folder instead of the usual one.</summary>
    public string? ModelsDirOverride { get; set; }

    readonly MainWindow _host;
    static AppController App => AppController.Instance;

    // list view
    readonly VBoxContainer _listView = new(), _confirmView = new(), _progressView = new();
    readonly Label _hwTitle = W.Label("Your hardware", "Section");
    readonly Label _hwGpu = W.Label("", null, false), _hwOther = W.Label("", "Dim", false), _hwNote = W.Label("", "Faint", false), _probing = W.Label("", "Dim", false);
    public Button RefreshButton { get; } = new() { Text = "Refresh", FocusMode = Control.FocusModeEnum.None };
    public OptionButton DeviceBox { get; } = new() { FitToLongestItem = false };
    readonly Label _recommendLine = W.Label("", null, false);
    readonly VBoxContainer _cards = new();
    public Dictionary<string, Card> Cards { get; } = new();

    // confirm view
    readonly Label _confTitle = W.Label("", "Section", false), _confSizes = W.Label("", null, false), _confSpace = W.Label("", null, false), _confWarn = W.Label("", null, false);
    public LineEdit DirEdit { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, TextDirection = Control.TextDirection.Ltr, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ThemeTypeVariation = "MonoEdit" };
    readonly Button _browse = new() { Text = "Browse…", FocusMode = Control.FocusModeEnum.None };
    readonly VBoxContainer _licenceBox = new();
    public List<CheckBox> LicenceChecks { get; } = new();
    public Button StartButton { get; } = new() { Text = "Download", FocusMode = Control.FocusModeEnum.None };
    public Button BackButton { get; } = new() { Text = "Back", FocusMode = Control.FocusModeEnum.None };
    readonly FileDialog _picker;

    // progress view
    readonly Label _progTitle = W.Label("", "Section", false), _progFile = W.Label("", null, false), _progBytes = W.Label("", null, false), _progSpeed = W.Label("", "Dim", false), _progError = W.Label("", null, false);
    public ProgressBar Bar { get; } = new() { MinValue = 0, MaxValue = 100, ShowPercentage = true, CustomMinimumSize = new Vector2(0, 22), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    public Button CancelButton { get; } = new() { Text = "Cancel", FocusMode = Control.FocusModeEnum.None };
    public Button DoneButton { get; } = new() { Text = "Back to the list", FocusMode = Control.FocusModeEnum.None, Visible = false };

    // state
    ModelTier? _tier;
    string _dir = "";
    DownloadPlan? _plan;
    HardwareReport? _hw;
    ModelRecommendation? _rec;
    Task<HardwareReport>? _probeTask;
    Task<ModelInstallResult>? _dlTask;
    CancellationTokenSource? _cts;
    ModelDownloader? _dl;
    volatile DownloadProgress? _latest;
    bool _deviceTouched;

    public View Current { get; private set; } = View.List;
    public bool IsOpen => Visible;
    public ModelRecommendation? Recommendation => _rec;
    public DownloadPlan? Plan => _plan;
    public string GpuText => _hwGpu.Text;
    public string HardwareOtherText => _hwOther.Text;
    public string ConfirmSizes => _confSizes.Text;
    public string ConfirmSpace => _confSpace.Text;
    public string ProgressFileText => _progFile.Text;
    public string ProgressBytesText => _progBytes.Text;
    public string ProgressSpeedText => _progSpeed.Text;
    public string ErrorText => _progError.Text;
    public string? LastErrorCode { get; private set; }
    public int ProbesDone { get; private set; }
    public bool Downloading => _dlTask is { IsCompleted: false };
    /// <summary>Number of finished downloads (success, error or cancel); tests wait for it to change.</summary>
    public int RunsDone { get; private set; }
    public ModelInstallResult? LastInstall { get; private set; }
    public bool LastCancelled { get; private set; }
    public string SelectedDevice => AiText.DeviceModes[Math.Clamp(DeviceBox.Selected, 0, AiText.DeviceModes.Length - 1)];

    public AiModelsDialog(MainWindow host)
    {
        _host = host;
        Title = "AI models";
        OkButtonText = "Close";
        Exclusive = true;
        MinSize = new Vector2I(720, 0);

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(700, 600) };
        AddChild(scroll);
        var root = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(root);
        root.AddChild(_listView); root.AddChild(_confirmView); root.AddChild(_progressView);
        foreach (var v in new[] { _listView, _confirmView, _progressView }) { v.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; v.AddThemeConstantOverride("separation", 10); }

        BuildList();
        BuildConfirm();
        BuildProgress();

        _picker = new FileDialog { UseNativeDialog = true, Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenDir };
        _picker.DirSelected += p => { DirEdit.Text = p; DirChanged(); };
        AddChild(_picker);
        _browse.Pressed += () => { _picker.Title = Tr("Choose the folder for the AI model files"); _picker.PopupCenteredRatio(0.6f); };
        RefreshButton.Pressed += () => Probe(true);
        DeviceBox.ItemSelected += _ => _deviceTouched = true;
        DirEdit.TextChanged += _ => DirChanged();
        BackButton.Pressed += () => SetView(View.List);
        StartButton.Pressed += StartDownload;
        CancelButton.Pressed += CancelDownload;
        DoneButton.Pressed += () => { RefreshCards(); SetView(View.List); };
        CloseRequested += OnCloseRequested;
        Canceled += OnCloseRequested;
        Confirmed += OnCloseRequested;
    }

    static void Wrap(Label l)
    {
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(640, 0);
        l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        l.TextDirection = Control.TextDirection.Auto;
    }

    void BuildList()
    {
        var intro = W.Label("Blazma generates motion with Kimodo, an AI model that runs on this computer. The files are large, so nothing is downloaded until you press Download and confirm.", "Dim");
        Wrap(intro);
        _listView.AddChild(intro);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        head.AddChild(_hwTitle); head.AddChild(W.Spacer()); head.AddChild(_probing); head.AddChild(RefreshButton);
        _listView.AddChild(head);
        Wrap(_hwGpu); Wrap(_hwOther); Wrap(_hwNote); Wrap(_recommendLine);
        _listView.AddChild(_hwGpu); _listView.AddChild(_hwOther); _listView.AddChild(_hwNote);
        _listView.AddChild(new HSeparator());
        _listView.AddChild(W.Label("Packages", "Section"));
        _listView.AddChild(_recommendLine);
        _cards.AddThemeConstantOverride("separation", 8);
        _listView.AddChild(_cards);
        var dev = new HBoxContainer();
        dev.AddThemeConstantOverride("separation", 10);
        dev.AddChild(W.Label("Run on", "Dim"));
        AiText.AddDeviceItems(DeviceBox);
        dev.AddChild(DeviceBox);
        _listView.AddChild(dev);
        var note = W.Label("Files come from Hugging Face over a secure connection and are checked after download. Nothing you make leaves your computer.", "Faint");
        Wrap(note);
        _listView.AddChild(note);
    }

    void BuildConfirm()
    {
        Wrap(_confSizes); Wrap(_confSpace); Wrap(_confWarn);
        _confirmView.AddChild(_confTitle);
        _confirmView.AddChild(_confSizes);
        _confirmView.AddChild(_confSpace);
        _confirmView.AddChild(_confWarn);
        _confirmView.AddChild(W.Label("Save to", "Dim"));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(DirEdit); row.AddChild(_browse);
        _confirmView.AddChild(row);
        _confirmView.AddChild(new HSeparator());
        _confirmView.AddChild(W.Label("Licences: read and accept each one to continue", "Section"));
        _confirmView.AddChild(_licenceBox);
        _licenceBox.AddThemeConstantOverride("separation", 10);
        var btns = new HBoxContainer();
        btns.AddThemeConstantOverride("separation", 10);
        btns.AddChild(BackButton); btns.AddChild(W.Spacer()); btns.AddChild(StartButton);
        _confirmView.AddChild(btns);
    }

    void BuildProgress()
    {
        Wrap(_progFile); Wrap(_progBytes); Wrap(_progSpeed); Wrap(_progError);
        _progressView.AddChild(_progTitle);
        _progressView.AddChild(_progFile);
        _progressView.AddChild(Bar);
        _progressView.AddChild(_progBytes);
        _progressView.AddChild(_progSpeed);
        _progressView.AddChild(_progError);
        var b = new HBoxContainer();
        b.AddThemeConstantOverride("separation", 10);
        b.AddChild(W.Spacer()); b.AddChild(DoneButton); b.AddChild(CancelButton);
        _progressView.AddChild(b);
    }

    // ---- formatting ---------------------------------------------------------------------------------------------------

    public static string FormatSize(long? bytes)
    {
        if (bytes is not { } b) return "?";
        return b >= 1L << 30 ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB", b / (double)(1L << 30))
            : b >= 1L << 20 ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", b / (double)(1L << 20))
            : string.Format(CultureInfo.InvariantCulture, "{0:0} KB", b / 1024.0);
    }

    static string L(string s) => AiText.Ltr(s);
    static bool Arabic => TranslationServer.GetLocale().StartsWith("ar", StringComparison.Ordinal);
    string TierName(ModelTier t) => Arabic ? t.NameAr : t.NameEn;

    // ---- opening ------------------------------------------------------------------------------------------------------

    public void Open()
    {
        _deviceTouched = false;
        DefaultDir();
        SetView(View.List);
        BuildCards();
        if (_hw is null) Probe(false); else ShowHardware();
        RefreshCards();
        PopupCentered(new Vector2I(760, 720));
    }

    void DefaultDir()
    {
        _dir = ModelsDirOverride ?? (App.Settings.KimodoModels.Length > 0 ? App.Settings.KimodoModels : AppPaths.Portable ? AppPaths.Combine("models") : ModelCatalog.DefaultModelsDirectory());
    }

    void SetView(View v)
    {
        Current = v;
        _listView.Visible = v == View.List;
        _confirmView.Visible = v == View.Confirm;
        _progressView.Visible = v == View.Progress;
        OkButtonText = "Close";
    }

    void OnCloseRequested()
    {
        // Closing during a download keeps it running in the background would be surprising: stop it (the .part files stay for resuming).
        if (Downloading) _cts?.Cancel();
        Hide();
        _host.GenerateDlg.NotifyModelsChanged();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady() && Visible) { if (_hw is not null) ShowHardware(); BuildCards(); RefreshCards(); if (Current == View.Confirm && _tier is not null) ShowConfirm(); }
    }

    // ---- hardware -----------------------------------------------------------------------------------------------------

    void Probe(bool refresh)
    {
        if (_probeTask is { IsCompleted: false }) return;
        RefreshButton.Disabled = true;
        _probing.Text = Tr("Looking at your PC…");
        var fn = ProbeFn;
        _probeTask = Task.Run(() => fn(refresh));
    }

    void ShowHardware()
    {
        var hw = _hw!;
        var g = hw.BestGpu;
        _hwGpu.Text = g is null || g.Integrated == true && g.VramBytes is null
            ? (g is null ? Tr("No dedicated GPU detected") : string.Format(CultureInfo.InvariantCulture, Tr("Graphics: {0} (built into the processor, shares system memory)"), L(g.Name)))
            : g.Integrated == true ? string.Format(CultureInfo.InvariantCulture, Tr("Graphics: {0} (built into the processor, shares system memory)"), L(g.Name))
            : string.Format(CultureInfo.InvariantCulture, Tr("Graphics card: {0}, video memory {1}"), L(g.Name), L(g.VramBytes is { } v ? FormatSize(v) : Tr("unknown")));
        _hwOther.Text = string.Format(CultureInfo.InvariantCulture, Tr("Memory (RAM): {0}   Processor: {1} cores"), L(FormatSize(hw.TotalRamBytes)), L(hw.LogicalCpus.ToString(CultureInfo.InvariantCulture)));
        _hwNote.Text = hw.Notes.Count > 0 ? string.Join("\n", hw.Notes) : "";
        _rec = HardwareAdvisor.Recommend(hw, Catalog);
        if (!_deviceTouched) DeviceBox.Select(AiText.DeviceIndex(_rec.Device));
        _recommendLine.Text = (Arabic ? _rec.ReasonAr : _rec.ReasonEn);
        RefreshCards();
    }

    // ---- cards --------------------------------------------------------------------------------------------------------

    void BuildCards()
    {
        foreach (var c in _cards.GetChildren()) { _cards.RemoveChild(c); c.QueueFree(); }
        var old = Cards.Values.ToDictionary(c => c.Tier.Id, c => c);
        Cards.Clear();
        foreach (var t in Catalog.Tiers)
        {
            var card = new Card { Tier = t };
            var panel = new PanelContainer();
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 4);
            var inner = W.Margin(box, 12, 10, 12, 10);
            panel.AddChild(inner);
            var top = new HBoxContainer();
            top.AddThemeConstantOverride("separation", 10);
            card.Name = W.Label(TierName(t), "Section", false);
            card.Badge = W.Label("Recommended for your PC", "Dim");
            card.Badge.AddThemeColorOverride("font_color", _host.Palette.Ok);
            card.Badge.Visible = false;
            card.InstalledLabel = W.Label("Installed", "Dim");
            card.InstalledLabel.AddThemeColorOverride("font_color", _host.Palette.Accent);
            card.InstalledLabel.Visible = false;
            top.AddChild(card.Name); top.AddChild(card.Badge); top.AddChild(W.Spacer()); top.AddChild(card.InstalledLabel);
            card.Info = W.Label("", "Dim", false); Wrap(card.Info);
            card.Reason = W.Label("", "Faint", false); Wrap(card.Reason);
            card.Fit = W.Label("", null, false); Wrap(card.Fit);
            card.Action = new Button { Text = "Download", FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd };
            var tier = t;
            card.Action.Pressed += () => BeginConfirm(tier);
            box.AddChild(top); box.AddChild(card.Info); box.AddChild(card.Reason); box.AddChild(card.Fit);
            var row = new HBoxContainer(); row.AddChild(W.Spacer()); row.AddChild(card.Action);
            box.AddChild(row);
            card.Panel = panel;
            _cards.AddChild(panel);
            Cards[t.Id] = card;
        }
    }

    public void RefreshCards()
    {
        if (Cards.Count == 0) return;
        using var dl = DownloaderFactory();
        foreach (var c in Cards.Values)
        {
            var t = c.Tier;
            c.Name.Text = TierName(t);
            c.Info.Text = string.Format(CultureInfo.InvariantCulture, Tr("Download size {0}. Needs {1} RAM (more is better: {2}); a graphics card needs {3} video memory."),
                L(FormatSize(t.TotalBytes)), L($"{t.MinRamGiB:0} GB"), L($"{t.RecommendedRamGiB:0} GB"), L($"{t.MinVramGiB:0} GB"));
            c.Reason.Text = Arabic ? t.QualityAr : t.QualityEn;
            var st = ModelCatalog.State(t, _dir);
            c.Installed = st.Installed;
            var plan = dl.Plan(t, _dir, Catalog);
            c.Resumable = !st.Installed && plan.BytesToDownload < plan.BytesTotal;
            c.InstalledLabel.Visible = st.Installed;
            c.Action.Visible = !st.Installed;
            c.Action.Text = c.Resumable ? Tr("Resume") : Tr("Download");
            var fit = _rec?.Tiers.FirstOrDefault(f => f.TierId == t.Id);
            c.Badge.Visible = _rec is not null && _rec.TierId == t.Id;
            var warn = fit is { Fits: false };
            c.Fit.Text = fit is null ? "" : warn
                ? Tr("May be slow or fail: too big for this PC. You can still download it.") + "  " + (Arabic ? fit.ReasonAr : fit.ReasonEn)
                : (Arabic ? fit.ReasonAr : fit.ReasonEn);
            c.Fit.AddThemeColorOverride("font_color", warn ? _host.Palette.Warn : _host.Palette.Text2);
        }
    }

    // ---- confirmation -------------------------------------------------------------------------------------------------

    public void BeginConfirm(ModelTier tier)
    {
        _tier = tier;
        DefaultDir();
        DirEdit.Text = _dir;
        BuildLicences();
        SetView(View.Confirm);
        ShowConfirm();
    }

    public void BeginConfirm(string tierId) { if (Catalog.Find(tierId) is { } t) BeginConfirm(t); }

    void BuildLicences()
    {
        foreach (var c in _licenceBox.GetChildren()) { _licenceBox.RemoveChild(c); c.QueueFree(); }
        LicenceChecks.Clear();
        foreach (var id in _tier!.LicenceIds)
        {
            var lic = Catalog.Licences.TryGetValue(id, out var l) ? l : new ModelLicence(id, id, "", "", "");
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 3);
            var summary = W.Label(Arabic ? lic.SummaryAr : lic.SummaryEn, "Dim", false); Wrap(summary);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            var check = new CheckBox { Text = string.Format(CultureInfo.InvariantCulture, Tr("I have read and accept: {0}"), lic.Name), AutoTranslateMode = AutoTranslateModeEnum.Disabled, FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            check.SetMeta("licence", id);
            check.Toggled += _ => UpdateStart();
            var link = new Button { Text = Tr("Open the licence"), FocusMode = Control.FocusModeEnum.None, Disabled = lic.Url.Length == 0, TooltipText = lic.Url };
            link.SetMeta("url", lic.Url);
            var url = lic.Url;
            link.Pressed += () => { if (url.StartsWith("https://", StringComparison.Ordinal)) OS.ShellOpen(url); };
            row.AddChild(check); row.AddChild(link);
            box.AddChild(summary); box.AddChild(row);
            _licenceBox.AddChild(box);
            LicenceChecks.Add(check);
        }
    }

    void DirChanged()
    {
        _dir = DirEdit.Text.Trim();
        ShowConfirm();
    }

    void ShowConfirm()
    {
        var t = _tier!;
        if (_dir.Length == 0) { StartButton.Disabled = true; return; }
        using var dl = DownloaderFactory();
        _plan = dl.Plan(t, _dir, Catalog);
        var p = _plan;
        _confTitle.Text = TierName(t);
        _confSizes.Text = string.Format(CultureInfo.InvariantCulture, Tr("Total size {0}. Still to download: {1}."), L(FormatSize(p.BytesTotal)), L(FormatSize(p.BytesToDownload)));
        _confSpace.Text = string.Format(CultureInfo.InvariantCulture, Tr("Free space on that drive: {0}"), L(p.FreeBytes is { } f ? FormatSize(f) : Tr("unknown")));
        _confWarn.Text = !p.EnoughSpace ? Tr("There is not enough free space for this package. Choose another folder or free some space.")
            : p.Conflicts.Count > 0 ? string.Format(CultureInfo.InvariantCulture, Tr("These files already exist but are different, so Blazma will not overwrite them: {0}"), L(string.Join(", ", p.Conflicts)))
            : "";
        _confWarn.AddThemeColorOverride("font_color", _host.Palette.Warn);
        UpdateStart();
    }

    void UpdateStart()
    {
        StartButton.Disabled = _plan is null || !_plan.EnoughSpace || _plan.Conflicts.Count > 0 || LicenceChecks.Any(c => !c.ButtonPressed);
    }

    // ---- download -----------------------------------------------------------------------------------------------------

    public void StartDownload()
    {
        if (_tier is null || StartButton.Disabled || Downloading) return;
        var tier = _tier;
        var accepted = LicenceChecks.Where(c => c.ButtonPressed).Select(c => (string)c.GetMeta("licence")).ToList();
        var device = SelectedDevice;
        var dir = _dir;
        LastErrorCode = null; LastCancelled = false; LastInstall = null;
        _progTitle.Text = TierName(tier);
        _progFile.Text = Tr("Starting…");
        _progBytes.Text = ""; _progSpeed.Text = ""; _progError.Text = "";
        Bar.Value = 0;
        CancelButton.Visible = true; CancelButton.Disabled = false; DoneButton.Visible = false;
        SetView(View.Progress);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _dl = DownloaderFactory();
        _dl.Progress += p => _latest = p;
        var dl = _dl;
        _dlTask = Task.Run(() => dl.DownloadAsync(tier, dir, accepted, device, ct));
    }

    public void CancelDownload()
    {
        if (!Downloading) return;
        CancelButton.Disabled = true;
        _progFile.Text = Tr("Cancelling…");
        _cts?.Cancel();
    }

    public override void _Process(double delta)
    {
        if (_probeTask is { IsCompleted: true } pt)
        {
            _probeTask = null;
            RefreshButton.Disabled = false;
            _probing.Text = "";
            if (pt.IsCompletedSuccessfully) { _hw = pt.Result; ShowHardware(); }
            else _hwGpu.Text = Tr("Could not read the hardware information.");
            ProbesDone++;
        }
        if (_dlTask is null) return;
        if (_latest is { } p && !_dlTask.IsCompleted) ShowProgress(p);
        if (_dlTask.IsCompleted)
        {
            var t = _dlTask; _dlTask = null;
            if (_latest is { } last && t.IsCompletedSuccessfully) ShowProgress(last);
            Finished(t);
        }
    }

    void ShowProgress(DownloadProgress p)
    {
        Bar.Value = p.Fraction * 100;
        _progFile.Text = p.Stage switch
        {
            DownloadStage.Verifying => string.Format(CultureInfo.InvariantCulture, Tr("Checking {0}"), L(p.FileName)),
            DownloadStage.Completed => Tr("Done"),
            _ => string.Format(CultureInfo.InvariantCulture, Tr("Downloading {0} (file {1} of {2})"), L(p.FileName), L((p.FileIndex + 1).ToString(CultureInfo.InvariantCulture)), L(p.FileCount.ToString(CultureInfo.InvariantCulture))),
        };
        _progBytes.Text = string.Format(CultureInfo.InvariantCulture, Tr("{0} of {1}"), L(FormatSize(p.BytesDone)), L(FormatSize(p.BytesTotal)));
        _progSpeed.Text = string.Format(CultureInfo.InvariantCulture, Tr("Speed {0}/s. Time left: {1}"), L(FormatSize((long)p.BytesPerSecond)),
            L(p.Eta is { } e ? AiText.Elapsed(e) : "--:--"));
    }

    void Finished(Task<ModelInstallResult> t)
    {
        _dl?.Dispose(); _dl = null;
        CancelButton.Visible = false;
        DoneButton.Visible = true;
        RunsDone++;
        if (t.IsCompletedSuccessfully)
        {
            LastInstall = t.Result;
            Bar.Value = 100;
            App.ApplyModelInstall(t.Result, Catalog.FindBundledRunner(System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? ""));
            _progFile.Text = Tr("Done");
            _progError.Text = Tr("The AI model is installed and Kimodo is ready to use.");
            _progError.AddThemeColorOverride("font_color", _host.Palette.Ok);
            _host.ToastNode.Notify("AI model ready", _host.Palette.Ok);
            _host.GenerateDlg.NotifyModelsChanged();
            return;
        }
        var ex = t.Exception?.GetBaseException();
        if (t.IsCanceled || ex is OperationCanceledException)
        {
            LastCancelled = true;
            _progFile.Text = Tr("Download cancelled");
            _progError.Text = Tr("What was downloaded so far is kept. Press Resume later to continue where it stopped.");
            _progError.AddThemeColorOverride("font_color", _host.Palette.Text2);
            return;
        }
        LastErrorCode = (ex as ModelDownloadException)?.Code ?? "io_error";
        _progFile.Text = Tr("The download did not finish");
        _progError.Text = Friendly(LastErrorCode, ex?.Message);
        _progError.AddThemeColorOverride("font_color", _host.Palette.Err);
    }

    public static string Friendly(string code, string? detail) => code switch
    {
        "disk_full" => TranslationServer.Translate("There is not enough free disk space. Free some space or choose another folder, then try again."),
        "hash_mismatch" => TranslationServer.Translate("A downloaded file did not match its checksum, so it was thrown away. Try again; if it keeps happening, the download may be corrupted by your network."),
        "size_mismatch" => TranslationServer.Translate("A downloaded file has the wrong size, so it was thrown away. Try again."),
        "redirect_refused" or "too_many_redirects" => TranslationServer.Translate("The download server sent Blazma somewhere it does not trust, so it stopped. Try again later."),
        "file_conflict" => TranslationServer.Translate("A file with the same name but different content is already in that folder. Blazma will not overwrite it: choose another folder or move that file."),
        "insecure_url" or "host_not_allowed" => TranslationServer.Translate("The download address is not allowed (only secure connections to Hugging Face are). Nothing was downloaded."),
        "http_error" => TranslationServer.Translate("The download server refused the request. Try again later."),
        "network_error" => TranslationServer.Translate("The connection failed. Check your internet connection and press Resume to continue."),
        "incomplete" => TranslationServer.Translate("The connection ended before the file was complete. Press Resume to continue."),
        "busy" => TranslationServer.Translate("A download is already running."),
        "licence_not_accepted" => TranslationServer.Translate("The licences must be accepted first."),
        _ => string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("The files could not be written: {0}"), detail ?? code),
    };
}
