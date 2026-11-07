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
/// <para>D45: while the user types, the offline planner (<see cref="MotionPromptPlanner"/>) shows the steps the prompt is split into: order,
/// the user's words → the English text sent to the generator (editable), the length (editable) and whether it was understood. Generate
/// sends the (edited) plan as <c>segments</c>; every step is generated separately and joined into one draft clip.</para>
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
    public SpinBox Duration { get; } = new UnitSpinBox() { MinValue = MotionRequest.MinDurationSeconds, MaxValue = MotionRequest.MaxDurationSeconds, Step = 0.1, Value = 4, UnitEn = "s" };
    public SpinBox Seed { get; } = new() { MinValue = 0, MaxValue = int.MaxValue, Step = 1, Rounded = true };
    public Button RandomSeed { get; } = new() { Text = "Random", FocusMode = Control.FocusModeEnum.None, TooltipText = "Pick a new random seed. The same prompt and seed give the same motion." };
    public OptionButton Provider { get; } = new() { FitToLongestItem = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    public Button DownloadModel { get; } = new() { Text = "Download AI model…", FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
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

    // ---- plan (D45)
    readonly VBoxContainer _planBox = new() { Visible = false };
    readonly Label _planSummary = W.Label("", "Dim", false);
    readonly Label _planHint = W.Label("", "Faint", false);
    readonly Label _planWarn = W.Label("", "ErrLabel", false);
    readonly GridContainer _planGrid = new() { Columns = 5 };
    readonly Godot.Timer _planTimer = new() { OneShot = true, WaitTime = 0.35 };
    MotionPlan? _plan;
    bool _planEdited, _settingDuration;
    readonly List<(LineEdit Caption, SpinBox Seconds, Label Status)> _planRows = new();

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
        MinSize = new Vector2I(640, 0);
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

        _planBox.AddThemeConstantOverride("separation", 6);
        _planGrid.AddThemeConstantOverride("h_separation", 8);
        _planGrid.AddThemeConstantOverride("v_separation", 4);
        foreach (var l in new[] { _planSummary, _planHint, _planWarn }) { Wrap(l); l.TextDirection = Control.TextDirection.Auto; }
        _planBox.AddChild(_planSummary);
        _planBox.AddChild(_planGrid);
        _planBox.AddChild(_planWarn);
        _planBox.AddChild(_planHint);
        var stepsLabel = W.Label("Steps", "Dim");
        stepsLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        grid.AddChild(stepsLabel);
        _planBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        grid.AddChild(_planBox);
        _planBox.VisibilityChanged += () => stepsLabel.Visible = _planBox.Visible;
        stepsLabel.Visible = false;
        AddChild(_planTimer);
        _planTimer.Timeout += RefreshPlan;

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
        providerBox.AddChild(DownloadModel);
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
        Prompt.TextChanged += () => { UpdateButtons(); _planEdited = false; _planTimer.Start(); };
        Duration.ValueChanged += _ => { if (!_settingDuration && !_planEdited) RefreshPlan(); };
        Character.ItemSelected += _ => UpdateButtons();
        Setup.Pressed += () => _host.OpenAiSettings();
        DownloadModel.Pressed += () => { Hide(); _host.OpenAiModels(); };
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
        if (what == NotificationTranslationChanged && IsNodeReady() && Visible) { RefreshProvider(); Show(_snap); ShowPlanSummary(); for (var i = 0; i < _planRows.Count; i++) ShowStatus(i); }
    }

    /// <summary>The AI models window installed or closed: re-read whether Kimodo is available.</summary>
    public void NotifyModelsChanged()
    {
        RefreshProvider();
        UpdateButtons();
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
        if (!IsRunning) { _planEdited = false; RefreshPlan(); }
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
        DownloadModel.Visible = Setup.Visible;
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
                + "\n" + Tr("Arabic words:") + " " + "مشي، ركض، قفز، تلويح، قرفصاء، وقوف، يلتفت، يلف، يقع، يموت، وقف";
        }
    }

    void UpdateButtons()
    {
        var running = IsRunning;
        var available = MotionProviderAvailable(SelectedProviderId);
        var ok = !running && _charIds.Count > 0 && available && Prompt.Text.Trim().Length > 0;
        GetOkButton().Disabled = !ok;
        foreach (Control c in new Control[] { Character, Prompt, Duration, Seed, RandomSeed, Provider }) SetEnabled(c, !running);
        foreach (var (caption, seconds, _) in _planRows) { caption.Editable = !running; seconds.Editable = !running; }
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
        // Several steps, or a plan the user edited: send it as is (each step generated separately, joined into one clip).
        RefreshPlanIfPending();
        if (_plan is { } plan && (plan.Segments.Count > 1 || _planEdited) && _planRows.Count == plan.Segments.Count)
        {
            var segs = new JsonArray();
            for (var i = 0; i < plan.Segments.Count; i++)
                segs.Add(new JsonObject
                {
                    ["text"] = plan.Segments[i].Text.Length > 0 ? plan.Segments[i].Text : _planRows[i].Caption.Text.Trim(),
                    ["durationSeconds"] = Math.Round(_planRows[i].Seconds.Value, 2),
                    ["caption"] = _planRows[i].Caption.Text.Trim(),
                });
            args["segments"] = segs;
            args["durationSeconds"] = Math.Clamp(Math.Round(_planRows.Sum(r => r.Seconds.Value), 2), MotionRequest.MinDurationSeconds, MotionRequest.MaxDurationSeconds);
        }
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
    public void ShowApplied(string jobId, string clipName, IEnumerable<string> warnings, string? generatedOn = null)
    {
        if (jobId != _jobId) return;
        _progress.Visible = false;
        _result.Visible = true;
        _status.Text = Tr("Done. The motion was added as a draft and is playing.");
        _status.AddThemeColorOverride("font_color", _host.Palette.Ok);
        var lines = new List<string> { string.Format(CultureInfo.InvariantCulture, Tr("Draft clip: {0}"), clipName) };
        if (generatedOn is not null) lines.Add(generatedOn);
        if (App.MotionJobs.Get(jobId)?.Segments is { Count: > 1 } steps)
        {
            lines.Add(Tr("Steps in the clip:"));
            lines.AddRange(steps.Select(x => $"{AiText.Ltr((x.Index + 1).ToString(CultureInfo.InvariantCulture))}. {x.Text} → {AiText.Ltr(x.Caption)} · {AiText.Ltr(x.DurationSeconds.ToString("0.0#", CultureInfo.InvariantCulture) + " " + Unit.Localize("s").Trim())}"));
        }
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
        if (s.CurrentSegment is int k && s.Segments.Count > 1)
            _stage.Text = string.Format(CultureInfo.InvariantCulture, Tr("Step {0} of {1}"), AiText.Ltr((k + 1).ToString(CultureInfo.InvariantCulture)),
                AiText.Ltr(s.Segments.Count.ToString(CultureInfo.InvariantCulture))) + " · " + _stage.Text;
    }

    // ---- plan (D45) -----------------------------------------------------------------------------------------------------

    void RefreshPlanIfPending()
    {
        if (_planTimer.TimeLeft > 0) { _planTimer.Stop(); RefreshPlan(); }
    }

    /// <summary>Re-plans the prompt (debounced while typing) and rebuilds the step rows.</summary>
    void RefreshPlan()
    {
        _planTimer.Stop();
        var text = Prompt.Text.Trim();
        foreach (var c in _planGrid.GetChildren()) { _planGrid.RemoveChild(c); c.QueueFree(); }
        _planRows.Clear();
        _plan = text.Length == 0 || text.Length > MotionRequest.MaxPromptLength ? null : MotionPromptPlanner.Plan(text, Duration.Value);
        _planBox.Visible = _plan is { Segments.Count: > 0 };
        if (_plan is null) return;
        foreach (var h in new[] { "#", "Your words", "Sent to the generator", "Length", "" })
            _planGrid.AddChild(W.Label(h, "Faint"));
        foreach (var seg in _plan.Segments)
        {
            var index = W.Label(AiText.Ltr((seg.Index + 1).ToString(CultureInfo.InvariantCulture)), "Dim", false);
            var words = W.Label(seg.Text, null, false);
            words.AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled;
            words.TextDirection = Control.TextDirection.Auto;
            words.CustomMinimumSize = new Vector2(110, 0);
            words.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            var caption = new LineEdit
            {
                Text = seg.Caption, TextDirection = Control.TextDirection.Auto, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(220, 0), TooltipText = "The English text the generator receives for this step. Kimodo understands English motion descriptions best.",
            };
            var seconds = new UnitSpinBox { MinValue = MotionRequest.MinDurationSeconds, MaxValue = MotionRequest.MaxDurationSeconds, Step = 0.05, Value = seg.DurationSeconds, UnitEn = "s" };
            seconds.LocalizeNumeralSystem = false;
            seconds.GetLineEdit().LocalizeNumeralSystem = false;
            seconds.GetLineEdit().TextDirection = Control.TextDirection.Ltr;
            seconds.GetLineEdit().ThemeTypeVariation = "MonoEdit";
            seconds.CustomMinimumSize = new Vector2(92, 0);
            var status = W.Label("", null, false);
            var row = _planRows.Count;
            _planRows.Add((caption, seconds, status));
            caption.TextChanged += _ => { _planEdited = true; ShowStatus(row); };
            seconds.ValueChanged += _ => { _planEdited = true; ShowPlanSummary(); };
            foreach (var c in new Control[] { index, words, caption, seconds, status }) _planGrid.AddChild(c);
            ShowStatus(row);
        }
        ShowPlanSummary();
    }

    void ShowStatus(int row)
    {
        if (_plan is null || row >= _plan.Segments.Count) return;
        var seg = _plan.Segments[row];
        var (caption, _, status) = _planRows[row];
        var edited = caption.Text.Trim() != seg.Caption;
        var ok = seg.Understood || (edited && caption.Text.Trim().Length > 0);
        status.Text = edited ? Tr("Edited") : !seg.Understood ? Tr("Not understood") : seg.Action is null ? Tr("As written (English)") : Tr("Understood");
        status.AddThemeColorOverride("font_color", ok ? _host.Palette.Ok : _host.Palette.Warn);
        ShowPlanSummary();
    }

    void ShowPlanSummary()
    {
        if (_plan is null) return;
        var total = _planRows.Sum(r => r.Seconds.Value);
        var totalText = AiText.Ltr(total.ToString("0.0#", CultureInfo.InvariantCulture) + " " + Unit.Localize("s").Trim());
        _planSummary.Text = _plan.Segments.Count == 1
            ? string.Format(CultureInfo.InvariantCulture, Tr("One step, {0}"), totalText)
            : string.Format(CultureInfo.InvariantCulture, Tr("{0} steps, total {1}"), AiText.Ltr(_plan.Segments.Count.ToString(CultureInfo.InvariantCulture)), totalText);
        _planHint.Visible = _plan.Segments.Count > 1;
        _planHint.Text = Tr("Each step is generated separately and joined into one clip, in this order. You can edit the English text and the length of each step.");
        var notUnderstood = PlanRows.Any(r => !r.Understood);
        _planWarn.Visible = notUnderstood;
        _planWarn.Text = notUnderstood ? Tr("Some steps were not understood. Rephrase them in the prompt, or type what the character should do in English in the step's box.") : "";
        // The total length follows the steps (without re-planning).
        if (_plan.Segments.Count > 1 && Math.Abs(Duration.Value - total) > 1e-6 && total >= Duration.MinValue && total <= Duration.MaxValue)
        {
            _settingDuration = true;
            Duration.Value = total;
            _settingDuration = false;
        }
    }

    /// <summary>The steps as shown: the user's words, the (edited) English text, the length, and whether it is understood.</summary>
    public IReadOnlyList<(string Text, string Caption, double Seconds, bool Understood)> PlanRows =>
        _plan is null ? Array.Empty<(string, string, double, bool)>() : _plan.Segments.Select((x, i) =>
        {
            var cap = i < _planRows.Count ? _planRows[i].Caption.Text.Trim() : x.Caption;
            return (x.Text, cap, i < _planRows.Count ? _planRows[i].Seconds.Value : x.DurationSeconds, x.Understood || (cap != x.Caption && cap.Length > 0));
        }).ToList();

    public string PlanSummary => _planSummary.Text;
    public bool PlanVisible => _planBox.Visible;
    public string PlanWarning => _planWarn.Visible ? _planWarn.Text : "";
    public IReadOnlyList<string> PlanStatuses => _planRows.Select(r => r.Status.Text).ToList();

    /// <summary>Runs the debounced re-plan now (tests).</summary>
    public void RefreshPlanNow() => RefreshPlan();

    /// <summary>Edits one step like the user would (tests).</summary>
    public void EditStep(int index, string? caption = null, double? seconds = null)
    {
        if (caption is not null) { _planRows[index].Caption.Text = caption; _planRows[index].Caption.EmitSignal(LineEdit.SignalName.TextChanged, caption); }
        if (seconds is double s) _planRows[index].Seconds.Value = s;
    }

    // ---- test hooks ---------------------------------------------------------------------------------------------------

    public void PressGenerate() => EmitSignal(ConfirmationDialog.SignalName.Confirmed);
    public bool GenerateEnabled => !GetOkButton().Disabled;
    public void SelectProvider(string id) { Provider.Select(id == AiText.KimodoId ? 0 : 1); RefreshProvider(); UpdateButtons(); }
}
