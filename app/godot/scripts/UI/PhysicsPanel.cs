using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Physics;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 6 Physics panel: a floating card on the inline-start side of the stage (left in English, right in Arabic), next to the
/// tool rail. Shows the balance of the frame on screen (COM height, margin, state), a summary of the active clip with its
/// unbalanced ranges (click to jump to the worst frame), and the two actions: Auto-balance (before/after table) and Secondary
/// motion. The actions run the physics.* commands through the normal command path, so each is one undo step (Ctrl+Z).
/// Opened from the Physics menu; Advanced mode also has a toolbar button for it.
/// </summary>
public partial class PhysicsPanel : PanelContainer
{
    const int MaxRangeButtons = 6;

    readonly MainWindow _host;
    readonly ScrollContainer _scroll;
    readonly Control _scrollContent;
    float _lastAvail = -1, _lastNeed = -1;
    /// <summary>Height the History list below keeps (0 in Simple mode).</summary>
    public float ReserveBelow { get; set; }

    // current frame
    readonly Label _source = W.Label("", "Faint", false);
    readonly Label _frame = ValueLabel(), _height = ValueLabel(), _margin = ValueLabel(), _stateText = W.Label("", null, false);
    readonly Dot _stateDot = new();
    readonly Label _message = W.Label("", "Faint", false);
    readonly VBoxContainer _currentBox = new();

    // clip summary
    readonly VBoxContainer _clipBox = new();
    readonly Label _nBalanced = BigNumber(), _nUnbalanced = BigNumber(), _nAirborne = BigNumber();
    readonly VBoxContainer _ranges = new();
    readonly Label _noRanges = W.Label("No unbalanced frames.", "Faint");
    readonly List<Button> _rangeButtons = new();
    ClipBalanceReport? _shownReport;
    string _shownLang = "";

    // auto-balance
    public SpinBox MarginField { get; } = new() { MinValue = 0, MaxValue = 50, Step = 0.5, Value = 2, CustomArrowStep = 0.5, SizeFlagsHorizontal = SizeFlags.ExpandFill };
    public Button AutoBalanceButton { get; } = new() { Text = "Auto-balance unbalanced frames", ThemeTypeVariation = "Primary", FocusMode = FocusModeEnum.None };
    public Button SecondaryButton { get; } = new() { Text = "Secondary motion…", FocusMode = FocusModeEnum.None };
    public CheckBox OverlayCheck { get; } = new() { Text = "Show balance in the viewport", FocusMode = FocusModeEnum.None };
    public CheckBox PathsCheck { get; } = new() { Text = "Show COM, hip and foot paths", FocusMode = FocusModeEnum.None };
    public CheckBox SlideCheck { get; } = new() { Text = "Allow foot slide", FocusMode = FocusModeEnum.None };
    bool _syncing;
    readonly VBoxContainer _result = new() { Visible = false };
    readonly Label _resultStatus = W.Label("", null, false);
    readonly Label _resultNote = W.Label("", "Dim", false);
    readonly GridContainer _table = new() { Columns = 3 };
    readonly Label _tableBeforeUnbalanced = ValueLabel(), _tableAfterUnbalanced = ValueLabel(), _tableBeforeMargin = ValueLabel(), _tableAfterMargin = ValueLabel();
    ulong _resultVersion;
    bool _dirty = true;

    static AppController App => AppController.Instance;

    /// <summary>Outcome of the last Auto-balance run from this panel (null before the first).</summary>
    public CommandResult? LastAutoBalance { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<Button> RangeButtons => _rangeButtons;
    public string StateText => _stateText.Text;
    public string MarginText => _margin.Text;
    public string HeightText => _height.Text;
    public string FrameText => _frame.Text;
    public string CountsText => $"{_nBalanced.Text}/{_nUnbalanced.Text}/{_nAirborne.Text}";
    public string MessageText => _message.Visible ? _message.Text : "";
    public string ResultText => _result.Visible ? _resultStatus.Text + "\n" + _resultNote.Text : "";
    public string TableText => _result.Visible ? $"{_tableBeforeUnbalanced.Text}>{_tableAfterUnbalanced.Text} {_tableBeforeMargin.Text}>{_tableAfterMargin.Text}" : "";
    public bool ResultVisible => _result.Visible;

    static Label ValueLabel()
    {
        var l = W.Label("", "MonoLabel", false);
        l.TextDirection = Control.TextDirection.Ltr;
        l.LocalizeNumeralSystem = false;
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return l;
    }

    static Label BigNumber()
    {
        var l = W.Label("0", "Heading", false);
        l.TextDirection = Control.TextDirection.Ltr;
        l.LocalizeNumeralSystem = false;
        l.HorizontalAlignment = HorizontalAlignment.Center;
        return l;
    }

    public PhysicsPanel(MainWindow host)
    {
        _host = host;
        ThemeTypeVariation = "Floating";
        CustomMinimumSize = new Vector2(260, 0);
        SizeFlagsVertical = SizeFlags.ShrinkBegin;
        LocalizeNumeralSystem = false;
        LayoutDirection = LayoutDirectionEnum.ApplicationLocale; // the stage layer is left to right; the card itself mirrors in Arabic
        Visible = false;

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 7);
        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        _scroll.AddChild(W.Margin(root, 12, 10, 12, 12));
        _scrollContent = (Control)_scroll.GetChild(0);
        _scrollContent.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddChild(_scroll);
        root.CustomMinimumSize = new Vector2(236, 0);

        // header
        var head = new HBoxContainer();
        head.AddChild(W.Label("Physics", "Heading"));
        head.AddChild(W.Spacer());
        var close = new Button { Text = "✕", Flat = true, FocusMode = FocusModeEnum.None, TooltipText = "Close the Physics panel" };
        close.Pressed += () => App.SetPhysicsPanel(false);
        head.AddChild(close);
        root.AddChild(head);

        _message.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _message.CustomMinimumSize = new Vector2(226, 0);
        root.AddChild(_message);

        // this frame
        _currentBox.AddThemeConstantOverride("separation", 7);
        var frameHead = new HBoxContainer();
        frameHead.AddChild(W.Label("This frame", "Section"));
        frameHead.AddChild(W.Spacer());
        _frame.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        frameHead.AddChild(_frame);
        _currentBox.AddChild(frameHead);
        var g = Grid();
        g.AddChild(W.Label("COM height", "Dim")); g.AddChild(_height);
        g.AddChild(W.Label("Margin", "Dim")); g.AddChild(_margin);
        g.AddChild(W.Label("State", "Dim"));
        var st = new HBoxContainer();
        st.AddThemeConstantOverride("separation", 7);
        st.AddChild(_stateDot);
        st.AddChild(_stateText);
        g.AddChild(st);
        _currentBox.AddChild(g);
        _source.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _currentBox.AddChild(_source);
        root.AddChild(_currentBox);

        // clip
        _clipBox.AddThemeConstantOverride("separation", 7);
        _clipBox.AddChild(new HSeparator());
        _clipBox.AddChild(W.Label("Clip balance", "Section"));
        var counts = new HBoxContainer();
        counts.AddThemeConstantOverride("separation", 6);
        counts.AddChild(Stat(_nBalanced, "Balanced"));
        counts.AddChild(Stat(_nUnbalanced, "Unbalanced"));
        counts.AddChild(Stat(_nAirborne, "Airborne"));
        _clipBox.AddChild(counts);
        _clipBox.AddChild(W.Label("Unbalanced ranges", "Dim"));
        _ranges.AddThemeConstantOverride("separation", 3);
        _ranges.AddChild(_noRanges);
        _clipBox.AddChild(_ranges);
        root.AddChild(_clipBox);

        // actions
        root.AddChild(new HSeparator());
        root.AddChild(W.Label("Fix and polish", "Section"));
        var mg = Grid();
        var ml = W.Label("Margin (cm)", "Dim");
        ml.TooltipText = "How far inside the support polygon the centre of mass is kept.";
        mg.AddChild(ml);
        MarginField.LocalizeNumeralSystem = false;
        MarginField.GetLineEdit().LocalizeNumeralSystem = false;
        MarginField.GetLineEdit().TextDirection = Control.TextDirection.Ltr;
        MarginField.GetLineEdit().ThemeTypeVariation = "MonoEdit";
        MarginField.TooltipText = "How far inside the support polygon the centre of mass is kept.";
        mg.AddChild(MarginField);
        root.AddChild(mg);
        AutoBalanceButton.TooltipText = "Shift the hips so the centre of mass stays over the feet on every unbalanced frame. The feet stay planted. One undo step.";
        SlideCheck.TooltipText = "Correct even when a supporting foot cannot stay planted (no leg IK, or its leg tracks are locked or muted). That foot may slide.";
        root.AddChild(SlideCheck);
        AutoBalanceButton.Pressed += () => RunAutoBalance();
        root.AddChild(AutoBalanceButton);

        _result.AddThemeConstantOverride("separation", 6);
        _resultStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _resultStatus.CustomMinimumSize = new Vector2(226, 0);
        _result.AddChild(_resultStatus);
        _table.AddThemeConstantOverride("h_separation", 12);
        _table.AddThemeConstantOverride("v_separation", 4);
        _table.AddChild(new Control());
        _table.AddChild(W.Label("Before", "Faint"));
        _table.AddChild(W.Label("After", "Faint"));
        _table.AddChild(W.Label("Unbalanced frames", "Dim")); _table.AddChild(_tableBeforeUnbalanced); _table.AddChild(_tableAfterUnbalanced);
        _table.AddChild(W.Label("Worst margin", "Dim")); _table.AddChild(_tableBeforeMargin); _table.AddChild(_tableAfterMargin);
        _result.AddChild(_table);
        _resultNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _resultNote.CustomMinimumSize = new Vector2(226, 0);
        _result.AddChild(_resultNote);
        root.AddChild(_result);

        SecondaryButton.TooltipText = "Make the spine, head or arms lag behind the motion and settle, like springs. One undo step.";
        SecondaryButton.Pressed += () => _host.OpenSecondaryMotionDialog();
        root.AddChild(SecondaryButton);

        root.AddChild(new HSeparator());
        // view switches
        OverlayCheck.TooltipText = "Draw the centre of mass, the support polygon and the contact points in the viewport, and the balance strip under the timeline.";
        OverlayCheck.Toggled += on => { if (!_syncing) App.SetShowPhysics(on); };
        PathsCheck.TooltipText = "Draw the paths of the centre of mass, the hips and the feet over the whole clip.";
        PathsCheck.Toggled += on => { if (!_syncing) App.SetShowPhysicsPaths(on); };
        root.AddChild(OverlayCheck);
        root.AddChild(PathsCheck);


        App.PlaybackChanged += MarkDirty;
        App.SettingsChanged += MarkDirty;
        App.ActiveClipChanged += MarkDirty;
        App.SessionChanged += _ => MarkDirty();
        App.Physics.Invalidated += MarkDirty;
        _host.Selection.Changed += MarkDirty;
    }

    static GridContainer Grid()
    {
        var g = new GridContainer { Columns = 2 };
        g.AddThemeConstantOverride("h_separation", 10);
        g.AddThemeConstantOverride("v_separation", 6);
        return g;
    }

    static Control Stat(Label number, string caption)
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 0);
        box.AddChild(number);
        var c = W.Label(caption, "Faint");
        c.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(c);
        return box;
    }

    public override void _ExitTree()
    {
        App.PlaybackChanged -= MarkDirty;
        App.SettingsChanged -= MarkDirty;
        App.ActiveClipChanged -= MarkDirty;
        App.Physics.Invalidated -= MarkDirty;
    }

    void MarkDirty() => _dirty = true;

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady()) _dirty = true;
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        if (_dirty) { _dirty = false; Refresh(); }
        var parentH = GetParentControl()?.Size.Y ?? 0;
        var avail = Mathf.Max(parentH - 4 - ReserveBelow, 160);
        var need = _scrollContent.GetCombinedMinimumSize().Y;
        if (Mathf.IsEqualApprox(avail, _lastAvail) && Mathf.IsEqualApprox(need, _lastNeed)) return;
        _lastAvail = avail; _lastNeed = need;
        _scroll.CustomMinimumSize = new Vector2(0, Mathf.Min(need, avail));
    }

    /// <summary>Show or hide with the setting; refreshes immediately when it opens.</summary>
    public void SyncVisible()
    {
        var open = App.Settings.PhysicsPanel;
        if (Visible != open) Visible = open;
        if (open) { _dirty = false; Refresh(); }
    }

    // ---- refresh -------------------------------------------------------------------------------------------------------

    static string Ltr(string s) => BalanceStrip.Ltr(s);
    static string Cm(double cm) => Ltr(BalanceStrip.Signed(cm) + Unit.Cm);
    static string Plain(double cm) => Ltr(cm.ToString("0.0", CultureInfo.InvariantCulture) + Unit.Cm);

    public void Refresh()
    {
        var phys = App.Physics;
        var pal = _host.Palette;
        _syncing = true;
        OverlayCheck.SetPressedNoSignal(App.Settings.ShowPhysics);
        PathsCheck.SetPressedNoSignal(App.Settings.ShowPhysicsPaths);
        _syncing = false;
        string? reason = null;
        if (App.FocusCharacter is null) reason = Tr("Select a character to see its balance.");
        else if (!phys.HasFeet) reason = Tr("This character has no feet mapped to the humanoid skeleton, so its balance cannot be analysed. Map the foot bones first.");

        _message.Text = reason ?? "";
        _message.Visible = reason is not null;
        _currentBox.Visible = reason is null;
        _clipBox.Visible = reason is null;
        AutoBalanceButton.Disabled = true;
        SecondaryButton.Disabled = reason is not null || !phys.TryResolve(out _, out _, out _);

        if (reason is null)
        {
            var cur = phys.Current;
            if (cur is not null)
            {
                _frame.Text = App.CurrentFrame.ToString(CultureInfo.InvariantCulture);
                _height.Text = Plain((cur.Com.Y - cur.GroundHeight) * 100);
                _margin.Text = PhysicsAnalysis.MarginCm(cur) is { } cm ? Cm(cm) : "—";
                var color = PhysicsOverlay.StateColor(pal, cur.State);
                _stateDot.SetColor(color);
                _stateText.Text = BalanceStrip.StateText(cur.State);
                _stateText.AddThemeColorOverride("font_color", color);
                _source.Visible = !phys.CurrentFromClip;
                _source.Text = Tr("Measured on the working pose (edited since the last key).");
            }
            RefreshClip(phys);
        }
        _resultVersionCheck();
    }

    void _resultVersionCheck()
    {
        if (_result.Visible && _resultVersion != App.ClipVersion) _result.Visible = false; // the clip changed since: the numbers are stale
    }

    void RefreshClip(PhysicsAnalysis phys)
    {
        var report = phys.Report;
        var hasClip = report is not null;
        _clipBox.Visible = true;
        if (!hasClip)
        {
            _nBalanced.Text = _nUnbalanced.Text = _nAirborne.Text = "–";
            ClearRanges();
            _noRanges.Text = Tr("Key a clip to analyse the whole motion.");
            _noRanges.Visible = true;
            _shownReport = null;
            return;
        }
        _nBalanced.Text = report!.BalancedFrames.ToString(CultureInfo.InvariantCulture);
        _nUnbalanced.Text = report.UnbalancedFrames.ToString(CultureInfo.InvariantCulture);
        _nAirborne.Text = report.AirborneFrames.ToString(CultureInfo.InvariantCulture);
        _nUnbalanced.AddThemeColorOverride("font_color", report.UnbalancedFrames > 0 ? _host.Palette.Err : _host.Palette.Text);
        AutoBalanceButton.Disabled = false;
        var lang = TranslationServer.GetLocale();
        if (ReferenceEquals(report, _shownReport) && lang == _shownLang) return;
        _shownReport = report; _shownLang = lang;
        ClearRanges();
        _noRanges.Text = Tr("No unbalanced frames.");
        _noRanges.Visible = report.UnbalancedRanges.Count == 0;
        foreach (var r in report.UnbalancedRanges.Take(MaxRangeButtons))
        {
            var range = r;
            var text = string.Format(CultureInfo.InvariantCulture, Tr("Frames {0}–{1} · worst {2}"),
                Ltr(range.Start.ToString(CultureInfo.InvariantCulture)), Ltr(range.End.ToString(CultureInfo.InvariantCulture)),
                Cm(Math.Round(range.MinMargin * 100, 1)));
            var b = new Button
            {
                Text = text, AutoTranslateMode = AutoTranslateModeEnum.Disabled, FocusMode = FocusModeEnum.None, ThemeTypeVariation = "Transport",
                Alignment = HorizontalAlignment.Left, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, ClipText = true,
                TooltipText = string.Format(CultureInfo.InvariantCulture, Tr("Go to frame {0}, the worst frame of this range."), Ltr(range.WorstFrame.ToString(CultureInfo.InvariantCulture))), CustomMinimumSize = new Vector2(226, 0),
            };
            b.Pressed += () => App.GoToFrame(range.WorstFrame);
            _ranges.AddChild(b);
            _rangeButtons.Add(b);
        }
        if (report.UnbalancedRanges.Count > MaxRangeButtons)
        {
            var more = W.Label(string.Format(CultureInfo.InvariantCulture, Tr("and {0} more"), report.UnbalancedRanges.Count - MaxRangeButtons), "Faint", false);
            _ranges.AddChild(more);
            _extra = more;
        }
    }

    Label? _extra;

    void ClearRanges()
    {
        foreach (var b in _rangeButtons) { _ranges.RemoveChild(b); b.QueueFree(); }
        _rangeButtons.Clear();
        if (_extra is not null) { _ranges.RemoveChild(_extra); _extra.QueueFree(); _extra = null; }
    }

    // ---- actions ---------------------------------------------------------------------------------------------------------

    /// <summary>Auto-balance the active clip with the margin field. Returns true when the clip was changed.</summary>
    public bool RunAutoBalance()
    {
        var phys = App.Physics;
        if (!phys.TryResolve(out var clip, out var character, out _) || phys.Report is not { } pre)
        {
            ShowFailure(Tr("Key a clip for this character first."));
            return false;
        }
        _host.Inspector.Flush();
        var preUnbalanced = pre.UnbalancedFrames;
        double? preWorst = pre.Worst is { } w ? w.Margin : null;
        var args = new JsonObject
        {
            ["clipId"] = clip.Id.ToString(), ["characterId"] = character.Id.ToString(),
            ["margin"] = Math.Round(MarginField.Value / 100.0, 5),
        };
        if (SlideCheck.ButtonPressed) args["allowFootSlide"] = true;
        var r = _host.ExecuteQuiet("physics.auto_balance", args);
        LastAutoBalance = r;
        if (!r.Ok)
        {
            ShowFailure(FriendlyAutoBalanceError(r.Error?.Code, r.Error?.Message ?? ""));
            return false;
        }
        LastError = null;
        _table.Visible = true;
        var post = phys.Report; // recomputed from the edited clip
        var corrected = Num(r.Data?["corrected"]);
        var drift = r.Data?["maxFootDrift"] is { } d ? double.Parse(d.ToJsonString(), CultureInfo.InvariantCulture) : 0;
        var pal = _host.Palette;
        var allGood = post is not null && post.UnbalancedFrames == 0;
        _resultStatus.Text = allGood ? Tr("Done. Every frame is balanced now.") : Tr("Done, but some frames are still unbalanced.");
        _resultStatus.AddThemeColorOverride("font_color", allGood ? pal.Ok : pal.Warn);
        _tableBeforeUnbalanced.Text = preUnbalanced.ToString(CultureInfo.InvariantCulture);
        _tableAfterUnbalanced.Text = post is null ? "–" : post.UnbalancedFrames.ToString(CultureInfo.InvariantCulture);
        _tableBeforeMargin.Text = preWorst is { } pw ? Cm(Math.Round(pw * 100, 1)) : "—";
        _tableAfterMargin.Text = post?.Worst is { } aw ? Cm(Math.Round(aw.Margin * 100, 1)) : "—";
        var lines = new List<string>
        {
            string.Format(CultureInfo.InvariantCulture, Tr("Corrected {0} frame(s). The supporting feet moved at most {1} mm."),
                Ltr(corrected.ToString(CultureInfo.InvariantCulture)), Ltr((drift * 1000).ToString("0.##", CultureInfo.InvariantCulture))),
        };
        foreach (var wn in r.Warnings)
            lines.Add(wn.Code switch
            {
                "still_unbalanced" => Tr("Some frames could not be fully balanced: the hips can only shift so far, or the legs cannot reach."),
                "feet_moved" => Tr("The supporting feet had to move a little because the legs could not reach. Try a smaller margin."),
                "track_locked" or "foot_slide" => Tr("Some supporting feet could not stay planted (no leg IK, or locked or muted leg tracks), so they may slide."),
                "degenerate_support" => Tr("Some frames stand on a single point or edge (tiptoe or heel), where the centre of mass cannot be kept well inside."),
                "untouched_frames_changed" => Tr("Frames that were not corrected moved slightly because of the new keys."),
                "non_finite" => Tr("Some frames had invalid values and were skipped."),
                "estimated_mass" => Tr("This character has no bone masses, so standard body proportions were used."),
                _ => WarningText.Describe(wn),
            });
        lines.Add(Tr("Ctrl+Z undoes it."));
        _resultNote.Text = string.Join("\n", lines);
        _result.Visible = true;
        _resultVersion = App.ClipVersion;
        _dirty = true;
        CallDeferred(nameof(ScrollToResult));
        return true;
    }

    /// <summary>Bring the before/after numbers into view (the card scrolls when the stage is short).</summary>
    void ScrollToResult()
    {
        if (_result.Visible) _scroll.EnsureControlVisible(_result);
    }

    /// <summary>Scroll back to the top (tests, screenshots).</summary>
    public void ScrollToTop() => _scroll.ScrollVertical = 0;

    void ShowFailure(string message)
    {
        LastError = message;
        _resultStatus.Text = Tr("Nothing was changed.");
        _resultStatus.AddThemeColorOverride("font_color", _host.Palette.Warn);
        _resultNote.Text = message;
        _table.Visible = false;
        _result.Visible = true;
        _resultVersion = App.ClipVersion;
    }

    string FriendlyAutoBalanceError(string? code, string message) => code switch
    {
        "already_balanced" => Tr("Every frame is already balanced with this margin. Nothing to correct."),
        "track_locked" => Tr("The hips' movement track is locked. Unlock it in the dope sheet first."),
        "no_feet" => Tr("This character has no feet mapped to the humanoid skeleton. Map the foot bones first."),
        "track_muted" => Tr("The hips' movement track is muted, so new keys would not play. Unmute it first."),
        "no_leg_ik" => Tr("A supporting foot has no leg IK, so it would slide when the hips move. Add leg IK, or tick Allow foot slide."),
        "non_finite" => Tr("Some frames contain invalid values, so nothing was changed."),
        "too_large" => Tr("The clip is too long to correct in one go."),
        "no_pelvis" => Tr("This character has no hips mapped to the humanoid skeleton. Map the hips bone first."),
        "rig_mismatch" => Tr("This clip animates a different character."),
        _ => string.Format(Tr("Auto-balance failed: {0}"), message),
    };

    static long Num(JsonNode? n) => n is null ? 0 : long.Parse(n.ToJsonString(), CultureInfo.InvariantCulture);
}
