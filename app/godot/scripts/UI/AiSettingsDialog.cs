using System.Globalization;
using Blazma.AI;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 7: where the user installed Kimodo (runner file, model folder), device and time limit, a "Check" button that runs the
/// diagnostics off the main thread, and the runner log. Blazma never downloads AI files: this page only records where they are.
/// Agents cannot change any of this (it chooses a program to run).
/// </summary>
public partial class AiSettingsDialog : ConfirmationDialog
{
    readonly MainWindow _host;
    public LineEdit RunnerEdit { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, TextDirection = Control.TextDirection.Ltr, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ThemeTypeVariation = "MonoEdit", PlaceholderText = "kimodo-runner" };
    public LineEdit ModelsEdit { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, TextDirection = Control.TextDirection.Ltr, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ThemeTypeVariation = "MonoEdit", PlaceholderText = "…/kimodo-models" };
    public OptionButton DeviceBox { get; } = new() { FitToLongestItem = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    /// <summary>"Best available" + the graphics cards kimodo-runner listed (after a Check or a generation).</summary>
    public OptionButton GpuBox { get; } = new() { FitToLongestItem = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    readonly List<string> _gpuNames = new() { "" };
    readonly Label _devicesLine = W.Label("", "Dim", false);
    readonly Label _lastUsedLine = W.Label("", "Dim", false);
    public string DevicesText => _devicesLine.Text;
    public string LastUsedText => _lastUsedLine.Text;
    public SpinBox TimeoutBox { get; } = new UnitSpinBox() { MinValue = 1, MaxValue = 600, Step = 1, Value = 15, Rounded = true };
    public Button CheckButton { get; } = new() { Text = "Check", FocusMode = Control.FocusModeEnum.None };
    public Button LogToggle { get; } = new() { Flat = true, FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
    public TextEdit LogBox { get; } = new() { Editable = false, Visible = false, CustomMinimumSize = new Vector2(0, 130), ThemeTypeVariation = "", AutoTranslateMode = AutoTranslateModeEnum.Disabled, TextDirection = Control.TextDirection.Ltr, WrapMode = TextEdit.LineWrappingMode.Boundary };
    readonly Button _browseRunner = new() { Text = "Browse…", FocusMode = Control.FocusModeEnum.None };
    readonly Button _browseModels = new() { Text = "Browse…", FocusMode = Control.FocusModeEnum.None };
    public Button ModelsButton { get; } = new() { Text = "Download AI model…", FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
    readonly Label _gpuLine = W.Label("", null, false);
    readonly Button _guide = new() { Text = "Open the install guide", FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
    readonly Label _runnerLine = W.Label("", null, false);
    readonly Label _modelsLine = W.Label("", null, false);
    readonly Label _hardware = W.Label("", "Dim", false);
    readonly Label _notes = W.Label("", "Faint", false);
    readonly Label _checking = W.Label("", "Dim", false);
    readonly Dot _runnerDot = new(), _modelsDot = new();

    // optional CUDA backend (DECISIONS D47): downloaded only when the user presses CudaButton
    readonly VBoxContainer _cudaBox = new() { Visible = false };
    readonly Label _cudaLine = W.Label("", null, false);
    readonly Label _cudaNote = W.Label("", "Faint", false);
    readonly Label _cudaProgress = W.Label("", "Dim", false);
    public Button CudaButton { get; } = new() { FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin, AutoTranslateMode = AutoTranslateModeEnum.Disabled };
    public Button CudaRemoveButton { get; } = new() { Text = "Remove GPU acceleration", FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
    public Button CudaCancelButton { get; } = new() { Text = "Cancel", FocusMode = Control.FocusModeEnum.None, Visible = false };
    public ProgressBar CudaBar { get; } = new() { MinValue = 0, MaxValue = 100, ShowPercentage = true, Visible = false, CustomMinimumSize = new Vector2(0, 20), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    /// <summary>Test hooks: the downloader and the hardware the CUDA section looks at.</summary>
    public Func<ModelDownloader> CudaDownloaderFactory { get; set; } = () => new ModelDownloader(ModelDownloaderOptions.GitHubReleases());
    public Func<HardwareReport> CudaHardwareFn { get; set; } = () => HardwareProbe.Probe();
    Task<CudaBackendState>? _cudaTask;
    CancellationTokenSource? _cudaCts;
    ModelDownloader? _cudaDl;
    volatile DownloadProgress? _cudaLatest;
    public bool CudaDownloading => _cudaTask is { IsCompleted: false };
    public bool CudaSectionVisible => _cudaBox.Visible;
    public string CudaLineText => _cudaLine.Text;
    public string CudaProgressText => _cudaProgress.Text;
    public string? CudaLastError { get; private set; }
    public bool CudaLastCancelled { get; private set; }
    /// <summary>Finished CUDA downloads (success, error or cancel); tests wait for it to change.</summary>
    public int CudaRunsDone { get; private set; }
    readonly FileDialog _runnerPicker, _modelsPicker;
    bool _logOpen;
    int _openTimeoutSeconds;

    static AppController App => AppController.Instance;

    public bool Checking { get; private set; }
    /// <summary>Number of finished checks (tests wait for it to change).</summary>
    public int ChecksDone { get; private set; }
    public KimodoCheckResult? LastCheck { get; private set; }
    public string RunnerLineText => _runnerLine.Text;
    public string ModelsLineText => _modelsLine.Text;
    public string GpuText => _gpuLine.Text;
    public string HardwareText => _hardware.Text;
    public string NotesText => _notes.Text;
    public bool IsOpen => Visible;

    public AiSettingsDialog(MainWindow host)
    {
        _host = host;
        Title = "Motion generation (AI)";
        OkButtonText = "Save";
        CancelButtonText = "Close";
        Exclusive = true;
        DialogHideOnOk = false;
        MinSize = new Vector2I(640, 0);

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(620, 560) };
        AddChild(scroll);
        var root = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        root.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(root);

        var intro = W.Label("Blazma can generate motion with Kimodo, an AI model that runs on this computer. Blazma never downloads anything by itself and nothing leaves your computer: you install the runner and the model files yourself, then tell Blazma where they are.", "Dim");
        Wrap(intro);
        root.AddChild(intro);

        var help = W.Label("Manual install steps are in the file docs/AI_INTEGRATION.md (sections 4 and 5) inside the Blazma folder: build or copy kimodo-runner, download the model files from their official pages, read their licences, then pick them below.", "Faint");
        Wrap(help);
        root.AddChild(help);
        root.AddChild(ModelsButton);
        root.AddChild(_guide);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 10);
        root.AddChild(grid);
        Row(grid, "Kimodo runner", PathRow(RunnerEdit, _browseRunner));
        Row(grid, "Models folder", PathRow(ModelsEdit, _browseModels));
        AiText.AddDeviceItems(DeviceBox);
        Row(grid, "Device", DeviceBox);
        GpuBox.TooltipText = "Which graphics card to use. The list shows what kimodo-runner found; press Check to fill it.";
        Row(grid, "Graphics card", GpuBox);
        TimeoutBox.LocalizeNumeralSystem = false;
        TimeoutBox.GetLineEdit().LocalizeNumeralSystem = false;
        TimeoutBox.GetLineEdit().TextDirection = Control.TextDirection.Ltr;
        TimeoutBox.GetLineEdit().ThemeTypeVariation = "MonoEdit";
        TimeoutBox.TooltipText = "Longest a single generation may take before it is stopped. The first one loads the models and is the slowest.";
        ((UnitSpinBox)TimeoutBox).UnitEn = "min";
        Row(grid, "Time limit", TimeoutBox);

        root.AddChild(new HSeparator());
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        head.AddChild(W.Label("Status", "Section"));
        head.AddChild(W.Spacer());
        head.AddChild(_checking);
        head.AddChild(CheckButton);
        root.AddChild(head);
        root.AddChild(StatusRow(_runnerDot, _runnerLine));
        root.AddChild(StatusRow(_modelsDot, _modelsLine));
        Wrap(_hardware); Wrap(_notes);
        _hardware.TextDirection = Control.TextDirection.Auto;
        Wrap(_gpuLine); _gpuLine.TextDirection = Control.TextDirection.Auto;
        root.AddChild(_hardware);
        root.AddChild(_gpuLine);
        Wrap(_devicesLine); _devicesLine.TextDirection = Control.TextDirection.Auto;
        Wrap(_lastUsedLine); _lastUsedLine.TextDirection = Control.TextDirection.Auto;
        root.AddChild(_devicesLine);
        root.AddChild(_lastUsedLine);
        root.AddChild(_notes);

        _cudaBox.AddThemeConstantOverride("separation", 6);
        Wrap(_cudaLine); Wrap(_cudaNote); Wrap(_cudaProgress);
        _cudaLine.TextDirection = Control.TextDirection.Auto;
        _cudaProgress.TextDirection = Control.TextDirection.Auto;
        _cudaBox.AddChild(new HSeparator());
        _cudaBox.AddChild(W.Label("GPU acceleration (CUDA)", "Section"));
        _cudaBox.AddChild(_cudaLine);
        var cudaRow = new HBoxContainer();
        cudaRow.AddThemeConstantOverride("separation", 10);
        cudaRow.AddChild(CudaButton); cudaRow.AddChild(CudaRemoveButton); cudaRow.AddChild(W.Spacer()); cudaRow.AddChild(CudaCancelButton);
        _cudaBox.AddChild(cudaRow);
        _cudaBox.AddChild(CudaBar);
        _cudaBox.AddChild(_cudaProgress);
        _cudaBox.AddChild(_cudaNote);
        root.AddChild(_cudaBox);

        root.AddChild(new HSeparator());
        root.AddChild(LogToggle);
        LogBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        root.AddChild(LogBox);

        _runnerPicker = new FileDialog { UseNativeDialog = true, Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenFile };
        _runnerPicker.FileSelected += p => { RunnerEdit.Text = p; Edited(); };
        AddChild(_runnerPicker);
        _modelsPicker = new FileDialog { UseNativeDialog = true, Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenDir };
        _modelsPicker.DirSelected += p => { ModelsEdit.Text = p; Edited(); };
        AddChild(_modelsPicker);

        _browseRunner.Pressed += () => { _runnerPicker.Title = Tr("Choose the Kimodo runner"); _runnerPicker.PopupCenteredRatio(0.6f); };
        _browseModels.Pressed += () => { _modelsPicker.Title = Tr("Choose the folder with the Kimodo model files"); _modelsPicker.PopupCenteredRatio(0.6f); };
        _guide.Pressed += OpenGuide;
        ModelsButton.Pressed += () => { Hide(); _host.OpenAiModels(); };
        CheckButton.Pressed += () => RunCheck();
        CudaButton.Pressed += StartCudaDownload;
        CudaCancelButton.Pressed += CancelCudaDownload;
        CudaRemoveButton.Pressed += RemoveCuda;
        Canceled += CancelCudaDownload;
        LogToggle.Pressed += () => { _logOpen = !_logOpen; SyncLog(); };
        RunnerEdit.TextChanged += _ => Edited();
        ModelsEdit.TextChanged += _ => Edited();
        Confirmed += () => ApplyNow();
        CloseRequested += () => { CancelCudaDownload(); Hide(); };
    }

    static void Wrap(Label l)
    {
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(560, 0);
        l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
    }

    static Control PathRow(LineEdit edit, Button browse)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 8);
        h.AddChild(edit);
        h.AddChild(browse);
        return h;
    }

    static void Row(GridContainer grid, string label, Control field)
    {
        var l = W.Label(label, "Dim");
        l.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        grid.AddChild(l);
        field.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        grid.AddChild(field);
    }

    static Control StatusRow(Dot dot, Label label)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 8);
        h.AddChild(dot);
        Wrap(label);
        label.CustomMinimumSize = new Vector2(540, 0);
        label.TextDirection = Control.TextDirection.Auto;
        h.AddChild(label);
        return h;
    }

    public override void _Ready()
    {
        App.KimodoLogChanged += SyncLog;
    }

    public override void _ExitTree()
    {
        App.KimodoLogChanged -= SyncLog;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady() && Visible) { RefreshStatic(); SyncLog(); }
    }

    // ---- opening / applying -----------------------------------------------------------------------------------------

    public void Open()
    {
        var eff = App.EffectiveKimodoSettings();
        var s = App.Settings;
        RunnerEdit.Text = s.KimodoRunner.Length > 0 ? s.KimodoRunner : eff.RunnerPath ?? "";
        ModelsEdit.Text = s.KimodoModels.Length > 0 ? s.KimodoModels : eff.ModelDirectory ?? "";
        DeviceBox.Select(AiText.DeviceIndex(eff.Device));
        FillGpus(eff.GpuDevice);
        _openTimeoutSeconds = (int)eff.GenerateTimeout.TotalSeconds;
        TimeoutBox.Value = Math.Clamp(Math.Round(_openTimeoutSeconds / 60.0), 1, 600);
        _logOpen = false;
        LastCheck = null;
        RefreshStatic();
        SyncLog();
        PopupCentered(new Vector2I(680, 660));
    }

    /// <summary>Saves the fields (and restarts the runner with them).</summary>
    public void ApplyNow()
    {
        var minutes = (int)TimeoutBox.Value;
        // Leave the time limit alone when the user did not change it (it may come from the environment).
        var seconds = minutes == (int)Math.Round(_openTimeoutSeconds / 60.0) ? App.Settings.KimodoTimeoutSeconds : minutes * 60;
        var gpu = _gpuNames[Math.Clamp(GpuBox.Selected, 0, _gpuNames.Count - 1)];
        App.SetKimodoSettings(RunnerEdit.Text, ModelsEdit.Text, AiText.DeviceModes[Math.Clamp(DeviceBox.Selected, 0, AiText.DeviceModes.Length - 1)], seconds, gpu);
        RefreshStatic();
    }

    void Edited()
    {
        // Typing changes what the status lines describe, so show the effect of the saved values only after Save.
        _checking.Text = "";
    }

    /// <summary>Cheap status from file checks only (no process is started).</summary>
    void RefreshStatic()
    {
        var k = App.Kimodo;
        if (k is null) return;
        var s = k.Settings;
        ShowFiles(s, k.Describe());
        var hw = HardwareProbe.Probe();
        ShowHardware(hw, Array.Empty<string>());
        RefreshCuda();
    }

    /// <summary>The graphics card list and the runner's device lines; keeps <paramref name="selected"/> even before the runner was asked.</summary>
    void FillGpus(string? selected)
    {
        var devices = App.Kimodo?.RunnerDevices;
        GpuBox.Clear();
        _gpuNames.Clear();
        GpuBox.AddItem(Tr("Best available"));
        _gpuNames.Add("");
        foreach (var d in KimodoDevicePlanner.RankGpus(devices ?? Array.Empty<KimodoDevice>()))
        {
            GpuBox.AddItem($"{d.Label} [{d.Name}]");
            _gpuNames.Add(d.Name);
        }
        if (selected is { Length: > 0 } && !_gpuNames.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            GpuBox.AddItem(selected);
            _gpuNames.Add(selected);
        }
        var i = _gpuNames.FindIndex(n => string.Equals(n, selected ?? "", StringComparison.OrdinalIgnoreCase));
        GpuBox.Select(Math.Max(0, i));
        _devicesLine.Text = devices is null
            ? Tr("Devices kimodo-runner can use: press Check to ask it.")
            : Tr("Devices kimodo-runner can use:") + "\n" + AiText.Devices(devices)
              + (devices.Any(d => d.IsGpu) ? "" : "\n" + Tr("No graphics card is usable by Kimodo (no CUDA or Vulkan device): it will run on the processor. Updating the graphics driver usually fixes this."));
        var last = App.Kimodo?.LastDeviceUse;
        _lastUsedLine.Text = last is null ? "" : AiText.GeneratedOn(last.Mode, last.Motion?.Label) ?? "";
        if (last is not null && last.CudaWouldHelp(devices) && App.CudaDownloadOffered())
            _lastUsedLine.Text += "\n" + Tr("Your NVIDIA card ran Kimodo through Vulkan. Download GPU acceleration (CUDA) below to make it faster.");
    }

    void ShowFiles(KimodoSettings s, ProviderInfo info)
    {
        var runner = s.ResolveRunner();
        var palette = _host.Palette;
        _runnerDot.SetColor(runner is not null ? palette.Ok : palette.Warn);
        _runnerLine.Text = runner is not null
            ? string.Format(CultureInfo.InvariantCulture, Tr("Runner found: {0}"), AiText.Ltr(runner))
            : s.RunnerPath is null ? Tr("Runner: not set") : string.Format(CultureInfo.InvariantCulture, Tr("Runner not found: {0}"), AiText.Ltr(s.RunnerPath));
        var motion = s.ResolveMotionModel();
        var text = s.ResolveTextModel();
        var tok = text is not null && KimodoSettings.HasTokenizer(text);
        var all = motion is not null && text is not null && tok;
        _modelsDot.SetColor(all ? palette.Ok : palette.Warn);
        _modelsLine.Text = s.ModelDirectory is null
            ? Tr("Models: folder not set")
            : all ? Tr("Models found: motion model, text encoder and tokenizer.")
            : string.Format(CultureInfo.InvariantCulture, Tr("Models missing: {0}"), string.Join(", ", new[]
            {
                motion is null ? Tr("motion model") : null, text is null ? Tr("text encoder") : null, text is not null && !tok ? Tr("tokenizer") : null,
            }.Where(x => x is not null)));
        _notes.Text = info.Available ? "" : AiText.Reason(info.UnavailableReason ?? "");
    }

    void ShowHardware(HardwareReport hw, IEnumerable<string> extraNotes)
    {
        var gpus = hw.Gpus.Count == 0
            ? Tr("no GPU found")
            : string.Join("; ", hw.Gpus.Select(g => g.VramBytes is { } v ? $"{g.Name} ({AiText.Bytes(v)})" : g.Name));
        _hardware.Text = string.Format(CultureInfo.InvariantCulture, Tr("This computer: {0} CPU cores, {1} RAM, GPU: {2}"),
            AiText.Ltr(hw.LogicalCpus.ToString(CultureInfo.InvariantCulture)), AiText.Ltr(AiText.Bytes(hw.TotalRamBytes)), AiText.Ltr(gpus));
        var g = hw.BestGpu;
        _gpuLine.Text = g is null ? Tr("No dedicated GPU detected: Kimodo will run on the processor.")
            : string.Format(CultureInfo.InvariantCulture, g.Integrated == true ? Tr("Recognised graphics: {0} (built into the processor, shares system memory)") : Tr("Recognised graphics card: {0}, video memory {1}"),
                AiText.Ltr(g.Name), AiText.Ltr(g.VramBytes is { } v ? AiModelsDialog.FormatSize(v) : Tr("unknown")));
    }

    // ---- check --------------------------------------------------------------------------------------------------------

    /// <summary>Saves the fields, then runs the diagnostics on a worker thread. The window keeps running while it waits for the runner.</summary>
    public void RunCheck()
    {
        if (Checking) return;
        ApplyNow();
        Checking = true;
        CheckButton.Disabled = true;
        _checking.Text = Tr("Checking…");
        App.RunKimodoCheck(r =>
        {
            Checking = false;
            LastCheck = r;
            ChecksDone++;
            CheckButton.Disabled = false;
            _checking.Text = "";
            ShowCheck(r);
        });
    }

    void ShowCheck(KimodoCheckResult r)
    {
        if (r.Diagnostics is not { } d)
        {
            _notes.Text = string.Format(CultureInfo.InvariantCulture, Tr("The check failed: {0}"), r.Error ?? "");
            return;
        }
        if (App.Kimodo is { } k) { ShowFiles(k.Settings, d.Info); FillGpus(k.Settings.GpuDevice); }
        ShowHardware(d.Hardware, d.Warnings);
        var lines = new List<string>();
        foreach (var w in d.Warnings)
            if (w != d.Info.UnavailableReason) lines.Add(w.StartsWith("Runner probe failed", StringComparison.Ordinal) ? string.Format(CultureInfo.InvariantCulture, Tr("The runner did not answer: {0}"), w) : w);
        if (d.Info.UnavailableReason is { } why) lines.Insert(0, AiText.Reason(why));
        else if (lines.Count == 0) lines.Add(Tr("Everything needed for Kimodo was found and the runner answered."));
        foreach (var kv in d.Details)
            if (kv.Key.StartsWith("runner.", StringComparison.Ordinal) && kv.Key is not ("runner.devices" or "runner.deviceModes") && kv.Value is { Length: > 0 })
                lines.Add($"{kv.Key[7..]}: {kv.Value}");
        _notes.Text = string.Join("\n", lines);
    }

    // ---- optional CUDA backend (DECISIONS D47) ------------------------------------------------------------------------

    /// <summary>Shows the CUDA section for an NVIDIA card (or when something of ours is on disk): status, the download button with its
    /// size, Remove. Nothing is downloaded until <see cref="CudaButton"/> is pressed.</summary>
    public void RefreshCuda()
    {
        if (CudaDownloading) return;
        var st = App.CudaState();
        var pkg = App.CudaPackage();
        var nvidia = CudaBackend.HasNvidiaGpu(CudaHardwareFn());
        var offer = CudaBackend.ShouldOffer(st, CudaHardwareFn());
        _cudaBox.Visible = nvidia && st.Status != CudaBackendStatus.NotOffered || st.CanRemove;
        CudaButton.Visible = offer && pkg is not null;
        if (pkg is not null)
            CudaButton.Text = string.Format(CultureInfo.InvariantCulture, Tr("Download GPU acceleration (CUDA, ~{0})"), AiText.Ltr(AiModelsDialog.FormatSize(pkg.Size)));
        CudaRemoveButton.Visible = st.CanRemove && st.Status != CudaBackendStatus.Bundled;
        CudaBar.Visible = false;
        CudaCancelButton.Visible = false;
        _cudaProgress.Text = "";
        _cudaLine.RemoveThemeColorOverride("font_color");
        _cudaLine.Text = st.Status switch
        {
            CudaBackendStatus.Bundled => Tr("This copy of Blazma already includes GPU acceleration (CUDA) for NVIDIA cards."),
            CudaBackendStatus.Installed => string.Format(CultureInfo.InvariantCulture, Tr("GPU acceleration (CUDA) is installed ({0}). Kimodo uses it automatically on NVIDIA cards."), AiText.Ltr(AiModelsDialog.FormatSize(st.InstalledBytes))),
            CudaBackendStatus.Outdated => Tr("The downloaded GPU acceleration belongs to another version of Blazma and is not used. Download the matching one, or remove it."),
            CudaBackendStatus.NotInstalled when nvidia => Tr("Your NVIDIA graphics card can run Kimodo faster with CUDA. Without it Kimodo uses Vulkan, which also works. The download is optional and only starts when you press the button."),
            _ => Tr("Downloaded GPU acceleration files are on this computer but this copy of Blazma cannot use them."),
        };
        if (st.PartialBytes > 0 && st.Status is CudaBackendStatus.NotInstalled or CudaBackendStatus.Outdated)
            _cudaLine.Text += "\n" + string.Format(CultureInfo.InvariantCulture, Tr("{0} already downloaded: pressing the button continues where it stopped."), AiText.Ltr(AiModelsDialog.FormatSize(st.PartialBytes)));
        _cudaNote.Text = offer && pkg is not null
            ? string.Format(CultureInfo.InvariantCulture, Tr("From the Blazma release on GitHub, checked with SHA-256, saved to {0}. It needs {1} of disk space and includes NVIDIA CUDA runtime libraries under NVIDIA's licence (legal/NVIDIA_CUDA_NOTICE.txt)."),
                AiText.Ltr(st.Directory), AiText.Ltr(AiModelsDialog.FormatSize(pkg.Size + pkg.InstalledBytes)))
            : "";
    }

    /// <summary>The user pressed "Download GPU acceleration": stop the runner (it would hold the libraries), download, verify, unpack.</summary>
    public void StartCudaDownload()
    {
        if (CudaDownloading || App.CudaPackage() is not { } pkg) return;
        var dir = App.CudaDirectory();
        App.Kimodo?.StopRunner("installing the CUDA backend");
        CudaLastError = null; CudaLastCancelled = false;
        CudaButton.Visible = false; CudaRemoveButton.Visible = false;
        CudaBar.Visible = true; CudaBar.Value = 0;
        CudaCancelButton.Visible = true; CudaCancelButton.Disabled = false;
        _cudaProgress.Text = Tr("Starting…");
        _cudaLine.RemoveThemeColorOverride("font_color");
        _cudaCts = new CancellationTokenSource();
        var ct = _cudaCts.Token;
        _cudaDl = CudaDownloaderFactory();
        var installer = new CudaBackendInstaller(_cudaDl);
        installer.Progress += p => _cudaLatest = p;
        _cudaLatest = null;
        _cudaTask = Task.Run(() => installer.InstallAsync(pkg, dir, ct));
    }

    public void CancelCudaDownload()
    {
        if (!CudaDownloading) return;
        CudaCancelButton.Disabled = true;
        _cudaProgress.Text = Tr("Cancelling…");
        _cudaCts?.Cancel();
    }

    public void RemoveCuda()
    {
        if (CudaDownloading) return;
        App.Kimodo?.StopRunner("removing the CUDA backend");
        try
        {
            var freed = CudaBackend.Remove(App.CudaDirectory());
            App.CudaBackendChanged();
            RefreshCuda();
            _host.ToastNode.NotifyFormat("GPU acceleration removed ({0} freed)", _host.Palette.Ok, AiText.Ltr(AiModelsDialog.FormatSize(freed)));
        }
        catch (ModelDownloadException e)
        {
            RefreshCuda();
            _cudaLine.Text = string.Format(CultureInfo.InvariantCulture, Tr("GPU acceleration could not be removed: {0}"), e.Message);
            _cudaLine.AddThemeColorOverride("font_color", _host.Palette.Err);
        }
    }

    public override void _Process(double delta)
    {
        if (_cudaTask is null) return;
        if (_cudaLatest is { } p && !_cudaTask.IsCompleted) ShowCudaProgress(p);
        if (!_cudaTask.IsCompleted) return;
        var t = _cudaTask; _cudaTask = null;
        _cudaDl?.Dispose(); _cudaDl = null;
        CudaRunsDone++;
        RefreshCuda();
        if (t.IsCompletedSuccessfully)
        {
            App.CudaBackendChanged();
            RefreshCuda();
            _cudaProgress.Text = Tr("GPU acceleration is ready. Kimodo will use CUDA for the next generation.");
            _host.ToastNode.Notify("GPU acceleration (CUDA) ready", _host.Palette.Ok);
            return;
        }
        var ex = t.Exception?.GetBaseException();
        if (t.IsCanceled || ex is OperationCanceledException)
        {
            CudaLastCancelled = true;
            _cudaProgress.Text = Tr("Download cancelled. What was downloaded so far is kept: press the button again to continue.");
            return;
        }
        CudaLastError = (ex as ModelDownloadException)?.Code ?? "io_error";
        _cudaProgress.Text = CudaFriendly(CudaLastError, ex?.Message);
        _cudaProgress.AddThemeColorOverride("font_color", _host.Palette.Err);
    }

    void ShowCudaProgress(DownloadProgress p)
    {
        _cudaProgress.RemoveThemeColorOverride("font_color");
        CudaBar.Value = p.Fraction * 100;
        _cudaProgress.Text = p.Stage switch
        {
            DownloadStage.Verifying or DownloadStage.Checking => Tr("Checking the download…"),
            DownloadStage.Installing => string.Format(CultureInfo.InvariantCulture, Tr("Installing {0}"), AiText.Ltr(p.FileName)),
            DownloadStage.Completed => Tr("Done"),
            _ => string.Format(CultureInfo.InvariantCulture, Tr("{0} of {1}"), AiText.Ltr(AiModelsDialog.FormatSize(p.BytesDone)), AiText.Ltr(AiModelsDialog.FormatSize(p.BytesTotal)))
                 + "   " + string.Format(CultureInfo.InvariantCulture, Tr("Speed {0}/s. Time left: {1}"), AiText.Ltr(AiModelsDialog.FormatSize((long)p.BytesPerSecond)),
                     AiText.Ltr(p.Eta is { } e ? AiText.Elapsed(e) : "--:--")),
        };
    }

    public static string CudaFriendly(string code, string? detail) => code switch
    {
        "insecure_url" or "host_not_allowed" => TranslationServer.Translate("The download address is not allowed (only secure connections to GitHub are). Nothing was downloaded."),
        "package_invalid" => TranslationServer.Translate("The downloaded GPU acceleration package does not contain the expected files, so nothing was installed."),
        "io_error" => string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("GPU acceleration could not be installed: {0}"), detail ?? code),
        _ => AiModelsDialog.Friendly(code, detail),
    };

    // ---- log ----------------------------------------------------------------------------------------------------------

    void SyncLog()
    {
        LogToggle.Text = Tr(_logOpen ? "Hide the runner log" : "Show the runner log");
        LogBox.Visible = _logOpen;
        if (!_logOpen) return;
        var log = App.Kimodo?.RecentLog() ?? Array.Empty<string>();
        LogBox.Text = log.Count == 0 ? Tr("The runner has not written anything yet.") : string.Join("\n", log);
        LogBox.ScrollVertical = LogBox.GetLineCount();
    }

    public bool LogOpen => _logOpen;
    public void ToggleLog() => LogToggle.EmitSignal(BaseButton.SignalName.Pressed);

    void OpenGuide()
    {
        foreach (var dir in new[] { ProjectSettings.GlobalizePath("res://"), System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? "" })
        {
            var d = dir;
            for (var i = 0; i < 5 && d.Length > 0; i++)
            {
                var f = System.IO.Path.Combine(d, "docs", "AI_INTEGRATION.md");
                if (System.IO.File.Exists(f)) { OS.ShellOpen(f); return; }
                d = System.IO.Path.GetDirectoryName(d) ?? "";
            }
        }
        _host.ToastNode.Notify("Open docs/AI_INTEGRATION.md in the Blazma folder (section 5).", _host.Palette.Warn);
    }
}
