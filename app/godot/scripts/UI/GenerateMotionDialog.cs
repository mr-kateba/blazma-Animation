using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.AI;
using Blazma.Commands;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 7: "Generate motion…". Collects a prompt, length, seed, provider and character, starts a job with <c>motion.generate</c> (the
/// same command agents use), shows progress with a Cancel button and reports the outcome. When the job is ready the main window adds it
/// as a draft clip (<c>motion.job_apply</c>) and starts playing it. Closing the dialog does not stop a running job.
/// </summary>
public partial class GenerateMotionDialog : ConfirmationDialog
{
    readonly MainWindow _host;
    public OptionButton Character { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, FitToLongestItem = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    public TextEdit Prompt { get; } = new()
    {
        CustomMinimumSize = new Vector2(0, 84), WrapMode = TextEdit.LineWrappingMode.Boundary, TextDirection = Control.TextDirection.Auto,
        PlaceholderText = "Describe the motion, for example: a person walks forward and waves", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
    };
    public SpinBox Duration { get; } = new() { MinValue = MotionRequest.MinDurationSeconds, MaxValue = MotionRequest.MaxDurationSeconds, Step = 0.1, Value = 4, Suffix = "s" };
    public SpinBox Seed { get; } = new() { MinValue = 0, MaxValue = int.MaxValue, Step = 1, Rounded = true };
    public Button RandomSeed { get; } = new() { Text = "Random", FocusMode = Control.FocusModeEnum.None, TooltipText = "Pick a new random seed. The same prompt and seed give the same motion." };
    public OptionButton Provider { get; } = new() { FitToLongestItem = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    public Button Setup { get; } = new() { Text = "AI settings…", FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
    public Button CancelJob { get; } = new() { Text = "Cancel generation", FocusMode = Control.FocusModeEnum.None };
    readonly Label _providerNote = W.Label("", "Dim", false);
    readonly Label _providerBadge = W.Label("", "Section", false);
    readonly Label _keywords = W.Label("", "Faint", false);
    readonly Label _noChar = W.Label("Add a character first: motion is generated for a character.", "ErrLabel");
    readonly VBoxContainer _progress = new() { Visible = false };
    readonly ProgressBar _bar = new() { ShowPercentage = false, MinValue = 0, MaxValue = 100, CustomMinimumSize = new Vector2(0, 14) };
    readonly Label _stage = W.Label("", null, false);
    readonly VBoxContainer _result = new() { Visible = false };
    readonly Label _status = W.Label("", null, false);
    readonly Label _details = W.Label("", "Dim", false);
    readonly List<string> _charIds = new();
    string? _jobId;
    MotionJobSnapshot? _snap;
    string? _noteReason;

    static AppController App => AppController.Instance;

    public bool IsOpen => Visible;
    public string? CurrentJobId => _jobId;
    public bool IsRunning => _snap is { State: MotionJobState.Running };
    public string StatusText => _status.Text;
    public string DetailsText => _details.Text;
    public string StageText => _stage.Text;
    public double ProgressValue => _bar.Value;
    public bool ProgressVisible => _progress.Visible;
    public string ProviderNote => _providerNote.Text;
    public string ProviderBadge => _providerBadge.Text;
    public string KeywordsText => _keywords.Text;
    public bool KeywordsVisible => _keywords.Visible;
    public string? LastError { get; private set; }
    public string SelectedProviderId => Provider.Selected == 0 ? AiText.KimodoId : AiText.ProceduralId;

    public GenerateMotionDialog(MainWindow host)
    {
        _host = host;
        Title = "Generate motion";
        OkButtonText = "Generate";
        CancelButtonText = "Close";
        Exclusive = true;
        MinSize = new Vector2I(580, 0);
        DialogHideOnOk = false;

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        AddChild(root);

        var intro = W.Label("Describe a movement and Blazma adds it to your project as a draft clip. Review it, then accept or discard it. Nothing is sent over the internet.", "Dim");
        Wrap(intro);
        root.AddChild(intro);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 10);
        root.AddChild(grid);

        Row(grid, "Character", Character);
        Row(grid, "What should it do?", Prompt, top: true);

        foreach (var s in new[] { Duration, Seed })
        {
            s.LocalizeNumeralSystem = false;
            s.GetLineEdit().LocalizeNumeralSystem = false;
            s.GetLineEdit().TextDirection = Control.TextDirection.Ltr;
            s.GetLineEdit().ThemeTypeVariation = "MonoEdit";
            s.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        }
        Duration.TooltipText = "Length of the motion in seconds.";
        Row(grid, "Length", Duration);
        var seedRow = new HBoxContainer();
        seedRow.AddThemeConstantOverride("separation", 8);
        seedRow.AddChild(Seed);
        seedRow.AddChild(RandomSeed);
        Row(grid, "Seed", seedRow);

        Provider.AddItem("Kimodo (local AI)", 0);
        Provider.AddItem("Procedural — not AI", 1);
        Row(grid, "Generator", Provider);

        var providerBox = new VBoxContainer();
        providerBox.AddThemeConstantOverride("separation", 4);
        Wrap(_providerBadge); Wrap(_providerNote); Wrap(_keywords);
        providerBox.AddChild(_providerBadge);
        providerBox.AddChild(_providerNote);
        providerBox.AddChild(_keywords);
        providerBox.AddChild(Setup);
        root.AddChild(providerBox);

        Wrap(_noChar);
        root.AddChild(_noChar);

        root.AddChild(new HSeparator());
        _progress.AddThemeConstantOverride("separation", 6);
        _progress.AddChild(_bar);
        var prow = new HBoxContainer();
        prow.AddThemeConstantOverride("separation", 8);
        _stage.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _stage.TextDirection = Control.TextDirection.Auto;
        prow.AddChild(_stage);
        prow.AddChild(CancelJob);
        _progress.AddChild(prow);
        root.AddChild(_progress);

        _result.AddThemeConstantOverride("separation", 6);
        Wrap(_status); Wrap(_details);
        _status.TextDirection = Control.TextDirection.Auto; _details.TextDirection = Control.TextDirection.Auto; _providerNote.TextDirection = Control.TextDirection.Auto;
        _result.AddChild(_status);
        _result.AddChild(_details);
        root.AddChild(_result);

        RandomSeed.Pressed += () => Seed.Value = NewSeed();
        Provider.ItemSelected += _ => { RefreshProvider(); UpdateButtons(); };
        Prompt.TextChanged += UpdateButtons;
        Character.ItemSelected += _ => UpdateButtons();
        Setup.Pressed += () => _host.OpenAiSettings();
        CancelJob.Pressed += CancelRunning;
        Confirmed += () => Generate();
        CloseRequested += Hide;
    }

    static void Wrap(Label l)
    {
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(480, 0);
        l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
    }

    static void Row(GridContainer grid, string label, Control field, bool top = false)
    {
        var l = W.Label(label, "Dim");
        l.SizeFlagsVertical = top ? Control.SizeFlags.ShrinkBegin : Control.SizeFlags.ShrinkCenter;
        grid.AddChild(l);
        field.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        grid.AddChild(field);
    }

    static ulong NewSeed() => (ulong)Random.Shared.NextInt64(0, int.MaxValue);

    public override void _Ready()
    {
        App.MotionJobChanged += OnJob;
        App.SettingsChanged += OnSettings;
    }

    public override void _ExitTree()
    {
        App.MotionJobChanged -= OnJob;
        App.SettingsChanged -= OnSettings;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady() && Visible) { RefreshProvider(); Show(_snap); }
    }

    void OnSettings()
    {
        if (!Visible) return;
        RefreshProvider(); // the user may have just set up Kimodo
        UpdateButtons();
    }

    public override void _Process(double delta)
    {
        if (Visible && _snap is { State: MotionJobState.Running } s) ShowRunning(s);
    }

    // ---- opening ------------------------------------------------------------------------------------------------------

    /// <summary>Fill the fields and show the dialog. A job that is still running is shown again instead of resetting the form.</summary>
    public void Open()
    {
        FillCharacters();
        if (!IsRunning)
        {
            _jobId = null; _snap = null;
            _progress.Visible = false; _result.Visible = false;
            Seed.Value = NewSeed();
            Provider.Select(MotionProviderAvailable(AiText.KimodoId) ? 0 : 1);
        }
        RefreshProvider();
        UpdateButtons();
        Show(_snap);
        PopupCentered(new Vector2I(600, 0));
        Prompt.GrabFocus();
    }

    void FillCharacters()
    {
        var keep = Character.Selected >= 0 && Character.Selected < _charIds.Count ? _charIds[Character.Selected] : App.FocusCharacterId;
        Character.Clear();
        _charIds.Clear();
        foreach (var c in App.Session.Project.Characters)
        {
            Character.AddItem(c.Name);
            _charIds.Add(c.Id.ToString());
        }
        var idx = keep is null ? -1 : _charIds.IndexOf(keep);
        if (idx >= 0) Character.Select(idx); else if (_charIds.Count > 0) Character.Select(0);
        _noChar.Visible = _charIds.Count == 0;
    }

    static bool MotionProviderAvailable(string id) => App.MotionJobs.FindProvider(id)?.Describe().Available == true;

    void RefreshProvider()
    {
        var kimodo = SelectedProviderId == AiText.KimodoId;
        var info = App.MotionJobs.FindProvider(SelectedProviderId)?.Describe();
        Provider.SetItemText(0, MotionProviderAvailable(AiText.KimodoId) ? Tr("Kimodo (local AI)") : Tr("Kimodo (local AI) — not installed"));
        _keywords.Visible = !kimodo;
        Setup.Visible = kimodo && info is { Available: false };
        _noteReason = info?.UnavailableReason;
        if (kimodo)
        {
            var ok = info?.Available == true;
            _providerBadge.Text = ok ? Tr("Kimodo: available") : Tr("Kimodo: not installed");
            _providerBadge.AddThemeColorOverride("font_color", ok ? _host.Palette.Ok : _host.Palette.Warn);
            _providerNote.Text = ok
                ? Tr("Runs on this computer. The first request loads the models and can take a while. Only the body is generated, not fingers or face.")
                : AiText.Reason(_noteReason ?? "") + "\n" + Tr("Nothing is downloaded automatically. The “Procedural” generator below works without any setup.");
        }
        else
        {
            _providerBadge.Text = Tr("Procedural — not AI");
            _providerBadge.AddThemeColorOverride("font_color", _host.Palette.Accent);
            _providerNote.Text = Tr("A small built-in generator that only recognises a few words. It does not understand sentences and no AI model is involved. Use it to try the workflow offline.");
            _keywords.Text = string.Format(CultureInfo.InvariantCulture, Tr("Words it understands: {0}"), AiText.Keywords)
                + "\n" + Tr("Arabic words:") + " " + "مشي، ركض، قفز، تلويح، قرفصاء، وقوف";
        }
    }

    void UpdateButtons()
    {
        var running = IsRunning;
        var available = MotionProviderAvailable(SelectedProviderId);
        var ok = !running && _charIds.Count > 0 && available && Prompt.Text.Trim().Length > 0;
        GetOkButton().Disabled = !ok;
        foreach (Control c in new Control[] { Character, Prompt, Duration, Seed, RandomSeed, Provider }) SetEnabled(c, !running);
    }

    static void SetEnabled(Control c, bool on)
    {
        switch (c)
        {
            case BaseButton b: b.Disabled = !on; break;
            case TextEdit t: t.Editable = on; break;
            case SpinBox s: s.Editable = on; break;
        }
    }

    // ---- actions ------------------------------------------------------------------------------------------------------

    /// <summary>Starts the job with the dialog's values. Returns the job id, or null (the reason is shown in the dialog).</summary>
    public string? Generate()
    {
        LastError = null;
        if (IsRunning) return _jobId;
        if (Character.Selected < 0 || Character.Selected >= _charIds.Count) { Problem(Tr("Add a character first: motion is generated for a character.")); return null; }
        var prompt = Prompt.Text.Trim();
        if (prompt.Length == 0) { Problem(Tr("Write what the character should do first.")); return null; }
        if (prompt.Length > MotionRequest.MaxPromptLength)
        {
            Problem(string.Format(CultureInfo.InvariantCulture, Tr("The text is too long (at most {0} characters)."), MotionRequest.MaxPromptLength));
            return null;
        }
        _host.Inspector.Flush();
        var args = new JsonObject
        {
            ["characterId"] = _charIds[Character.Selected],
            ["prompt"] = prompt,
            ["durationSeconds"] = Duration.Value,
            ["seed"] = (long)Seed.Value,
            ["provider"] = SelectedProviderId,
        };
        var id = App.StartMotionJob(args, out var error, _host.ExecuteQuiet);
        if (id is null) { Problem(Friendly(error)); return null; }
        _jobId = id;
        _snap = App.MotionJobs.Get(id);
        _result.Visible = false;
        UpdateButtons();
        Show(_snap);
        return id;
    }

    string Friendly(string error) =>
        error.Contains("is not available", StringComparison.Ordinal) ? AiText.Reason(error[(error.IndexOf(':') + 1)..].Trim())
        : error.Contains("no bone mapped to 'pelvis'", StringComparison.Ordinal) || error.Contains("mapped to 'pelvis'", StringComparison.Ordinal)
            ? Tr("This character has no bone mapped to the pelvis. Map its bones first (Rigging menu).")
        : error;

    void Problem(string message)
    {
        LastError = message;
        _progress.Visible = false;
        _result.Visible = true;
        _status.Text = Tr("Nothing was generated.");
        _status.AddThemeColorOverride("font_color", _host.Palette.Warn);
        _details.Text = message;
    }

    public void CancelRunning()
    {
        if (_jobId is { } id && IsRunning) App.CancelMotionJob(id);
    }

    // ---- job events ---------------------------------------------------------------------------------------------------

    void OnJob(MotionJobSnapshot snap)
    {
        if (snap.JobId != _jobId) return;
        _snap = snap;
        if (Visible) Show(snap);
        UpdateButtons();
    }

    /// <summary>The main window added the finished job to the project: say so, with the warnings.</summary>
    public void ShowApplied(string jobId, string clipName, IEnumerable<string> warnings)
    {
        if (jobId != _jobId) return;
        _progress.Visible = false;
        _result.Visible = true;
        _status.Text = Tr("Done. The motion was added as a draft and is playing.");
        _status.AddThemeColorOverride("font_color", _host.Palette.Ok);
        var lines = new List<string> { string.Format(CultureInfo.InvariantCulture, Tr("Draft clip: {0}"), clipName) };
        lines.AddRange(warnings.Select(AiText.Warning));
        lines.Add(Tr("Accept it to keep it, or discard it. Ctrl+Z undoes either."));
        _details.Text = string.Join("\n", lines);
    }

    void Show(MotionJobSnapshot? snap)
    {
        if (snap is null) return;
        switch (snap.State)
        {
            case MotionJobState.Running: ShowRunning(snap); break;
            case MotionJobState.Failed:
                _progress.Visible = false; _result.Visible = true;
                _status.Text = Tr("Generation failed.");
                _status.AddThemeColorOverride("font_color", _host.Palette.Err);
                _details.Text = AiText.Failure(snap.ErrorCode, snap.Message);
                break;
            case MotionJobState.Cancelled:
                _progress.Visible = false; _result.Visible = true;
                _status.Text = Tr("Cancelled. Nothing was added to the project.");
                _status.AddThemeColorOverride("font_color", _host.Palette.Warn);
                _details.Text = "";
                break;
            case MotionJobState.Ready:
                _progress.Visible = true; _result.Visible = false;
                _stage.Text = Tr("Adding the motion…");
                _bar.Value = 100;
                break;
        }
    }

    void ShowRunning(MotionJobSnapshot s)
    {
        _progress.Visible = true; _result.Visible = false;
        _bar.Value = s.Progress * 100;
        var elapsed = DateTimeOffset.UtcNow - s.Created;
        _stage.Text = string.Format(CultureInfo.InvariantCulture, Tr("{0} · {1}% · {2}"), AiText.Stage(s.Stage),
            AiText.Ltr(((int)Math.Round(s.Progress * 100)).ToString(CultureInfo.InvariantCulture)), AiText.Ltr(AiText.Elapsed(elapsed)));
    }

    // ---- test hooks ---------------------------------------------------------------------------------------------------

    public void PressGenerate() => EmitSignal(ConfirmationDialog.SignalName.Confirmed);
    public bool GenerateEnabled => !GetOkButton().Disabled;
    public void SelectProvider(string id) { Provider.Select(id == AiText.KimodoId ? 0 : 1); RefreshProvider(); UpdateButtons(); }
}
