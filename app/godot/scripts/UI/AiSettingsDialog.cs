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
    static readonly string[] Devices = { "auto", "cpu", "vulkan" };

    readonly MainWindow _host;
    public LineEdit RunnerEdit { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, TextDirection = Control.TextDirection.Ltr, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ThemeTypeVariation = "MonoEdit", PlaceholderText = "kimodo-runner" };
    public LineEdit ModelsEdit { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, TextDirection = Control.TextDirection.Ltr, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ThemeTypeVariation = "MonoEdit", PlaceholderText = "…/kimodo-models" };
    public OptionButton DeviceBox { get; } = new() { FitToLongestItem = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    public SpinBox TimeoutBox { get; } = new() { MinValue = 1, MaxValue = 600, Step = 1, Value = 15, Rounded = true };
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
        DeviceBox.AddItem("Automatic", 0);
        DeviceBox.AddItem("CPU only", 1);
        DeviceBox.AddItem("GPU (Vulkan)", 2);
        DeviceBox.TooltipText = "Automatic uses the GPU when the runner supports it and falls back to the CPU.";
        Row(grid, "Device", DeviceBox);
        TimeoutBox.LocalizeNumeralSystem = false;
        TimeoutBox.GetLineEdit().LocalizeNumeralSystem = false;
        TimeoutBox.GetLineEdit().TextDirection = Control.TextDirection.Ltr;
        TimeoutBox.GetLineEdit().ThemeTypeVariation = "MonoEdit";
        TimeoutBox.TooltipText = "Longest a single generation may take before it is stopped. The first one loads the models and is the slowest.";
        TimeoutBox.Suffix = "min";
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
        root.AddChild(_notes);

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
        LogToggle.Pressed += () => { _logOpen = !_logOpen; SyncLog(); };
        RunnerEdit.TextChanged += _ => Edited();
        ModelsEdit.TextChanged += _ => Edited();
        Confirmed += () => ApplyNow();
        CloseRequested += Hide;
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
        DeviceBox.Select(Math.Max(0, Array.IndexOf(Devices, eff.Device)));
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
        App.SetKimodoSettings(RunnerEdit.Text, ModelsEdit.Text, Devices[Math.Clamp(DeviceBox.Selected, 0, 2)], seconds);
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
                AiText.Ltr(g.Name), AiText.Ltr(g.VramBytes is { } v ? AiModelsDialog.Size(v) : Tr("unknown")));
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
        if (App.Kimodo is { } k) ShowFiles(k.Settings, d.Info);
        ShowHardware(d.Hardware, d.Warnings);
        var lines = new List<string>();
        foreach (var w in d.Warnings)
            if (w != d.Info.UnavailableReason) lines.Add(w.StartsWith("Runner probe failed", StringComparison.Ordinal) ? string.Format(CultureInfo.InvariantCulture, Tr("The runner did not answer: {0}"), w) : w);
        if (d.Info.UnavailableReason is { } why) lines.Insert(0, AiText.Reason(why));
        else if (lines.Count == 0) lines.Add(Tr("Everything needed for Kimodo was found and the runner answered."));
        foreach (var kv in d.Details)
            if (kv.Key.StartsWith("runner.", StringComparison.Ordinal) && kv.Value is { Length: > 0 }) lines.Add($"{kv.Key[7..]}: {kv.Value}");
        _notes.Text = string.Join("\n", lines);
    }

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
        _host.ToastNode.Notify(Tr("Open docs/AI_INTEGRATION.md in the Blazma folder (section 5)."), _host.Palette.Warn);
    }
}
