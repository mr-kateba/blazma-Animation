using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Combat;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Rigging;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 9 Combat panel (Advanced mode): a floating card in the inline-end column with four pages for the current clip and character.
/// Phases: set the startup / active / recovery frames or read them from the key-pose markers. Events: typed events (hit, footstep, sound,
/// visual effect, custom) with a payload. Hitboxes: shapes on a bone or a prop with active frames, damage, knockback, hit-stop and tags.
/// Hurtboxes: generated for the character from its skeleton, then editable per bone. Every edit is a command (combat.set_phases, event.*,
/// hitbox.*, hurtbox.*), so it is undoable and the same as what agents do; lists are rebuilt from the project after each change.
/// </summary>
public partial class CombatPanel : PanelContainer
{
    public const int PagePhases = 0, PageEvents = 1, PageHitboxes = 2, PageHurtboxes = 3;

    readonly MainWindow _host;
    readonly ScrollContainer _scroll;
    readonly Control _scrollContent;
    float _lastAvail = -1, _lastNeed = -1;
    /// <summary>Height the lists below keep (the History list).</summary>
    public float ReserveBelow { get; set; }
    bool _dirty = true, _syncing;
    int _page;

    public Segmented Tabs { get; } = new("Phases", "Events", "Hitboxes", "Hurtboxes");
    readonly VBoxContainer[] _pages = { new(), new(), new(), new() };
    readonly Label _context = W.Label("", "Dim", false);
    readonly Label _noClip = W.Label("Select or create a clip first. Combat data belongs to a clip.", "Faint");

    // phases
    public SpinBox StartupStart { get; } = P8.Spin(0, 10_000_000, 1, 0, "", true);
    public SpinBox ActiveStart { get; } = P8.Spin(0, 10_000_000, 1, 0, "", true);
    public SpinBox ActiveEnd { get; } = P8.Spin(0, 10_000_000, 1, 0, "", true);
    public SpinBox RecoveryEnd { get; } = P8.Spin(0, 10_000_000, 1, 0, "", true);
    public Button SetPhasesButton { get; } = CombatKit.Btn("Set phases", "Use these frames as the startup, active and recovery parts of the attack.", "Primary");
    public Button FromMarkersButton { get; } = CombatKit.Btn("From markers", "Read the phases from the key-pose markers: Main Action or Contact starts the active part, Follow-through or Recovery ends it.");
    public Button ClearPhasesButton { get; } = CombatKit.Btn("Clear", "Remove the attack phases of this clip.");
    readonly Label _phaseInfo = W.Label("", null, false);
    string _phaseSig = "";

    // events
    public ItemList EventList { get; } = NewList();
    public SpinBox EventFrame { get; } = P8.Spin(0, 10_000_000, 1, 0, "", true);
    public LineEdit EventName { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "What the game should do or listen for, for example “footstep_left”." };
    public OptionButton EventKind { get; } = CombatKit.StaticOption("Custom", "Hit", "Footstep", "Sound", "Visual effect");
    public LineEdit EventPayload { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Optional extra data for the game as a JSON object, for example {\"volume\": 0.8}." };
    public Button EventAdd { get; } = CombatKit.Btn("Add event", "Add an event at the frame above.", "Primary");
    public Button EventUpdate { get; } = CombatKit.Btn("Update", "Change the selected event.");
    public Button EventRemove { get; } = CombatKit.Btn("Remove", "Remove the selected event.");
    public Button EventAtPlayhead { get; } = CombatKit.Btn("Playhead", "Use the current frame.");
    readonly List<string> _eventIds = new();
    string _eventSig = "";
    string? _selEvent;

    // hitboxes
    public ItemList HitboxList { get; } = NewList();
    public LineEdit HitName { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill };
    public ShapeFields HitShape { get; } = new();
    public OptionButton AttachKind { get; } = CombatKit.StaticOption("Bone", "Prop");
    public OptionButton AttachTarget { get; } = P8.Option(0);
    public Button UseSelected { get; } = CombatKit.Btn("Use selected", "Pick the bone or prop that is selected in the viewport.");
    public SpinBox OffX { get; } = P8.Spin(-100, 100, 0.01, 0, " m");
    public SpinBox OffY { get; } = P8.Spin(-100, 100, 0.01, 0, " m");
    public SpinBox OffZ { get; } = P8.Spin(-100, 100, 0.01, 0, " m");
    public SpinBox RotX { get; } = P8.Spin(-360, 360, 1, 0, "°");
    public SpinBox RotY { get; } = P8.Spin(-360, 360, 1, 0, "°");
    public SpinBox RotZ { get; } = P8.Spin(-360, 360, 1, 0, "°");
    public LineEdit ActiveFrames { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill, TextDirection = TextDirection.Ltr, ThemeTypeVariation = "MonoEdit", TooltipText = "Frames where the hitbox can hit, as ranges: 12-17,20-23. The last frame of a range is not included. Empty: the active phase." };
    public Button ActiveFromPhase { get; } = CombatKit.Btn("Active phase", "Use the active frames of the attack phases.");
    public SpinBox Damage { get; } = P8.Spin(0, 1_000_000, 1, 10);
    public SpinBox Knockback { get; } = P8.Spin(0, 100, 0.05, 0.3, " m");
    public SpinBox Hitstop { get; } = P8.Spin(0, 600, 1, 0, "", true);
    public LineEdit Tags { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Words for the game, separated by commas, for example: slash, heavy." };
    public Button HitAdd { get; } = CombatKit.Btn("Add hitbox", "Add a hitbox with the values below to this clip.", "Primary");
    public Button HitApply { get; } = CombatKit.Btn("Apply to selected", "Change the selected hitbox to the values below.");
    public Button HitRemove { get; } = CombatKit.Btn("Remove", "Remove the selected hitbox.");
    readonly Label _hitHint = W.Label("", "Faint", false);
    readonly List<string> _hitIds = new(), _targetIds = new();
    string _hitSig = "", _targetSig = "";
    string? _selHit;

    // hurtboxes
    public ItemList HurtList { get; } = NewList();
    public SpinBox RadiusScale { get; } = P8.Spin(0.25, 4, 0.05, 1, "×");
    public Button GenerateButton { get; } = CombatKit.Btn("Generate hurtboxes", "Build a hurtbox for each body part of the character from its skeleton. Existing hurtboxes are replaced.", "Primary");
    public ShapeFields HurtShape { get; } = new();
    public OptionButton HurtZone { get; } = CombatKit.StaticOption("High", "Middle", "Low");
    public CheckBox HurtEnabled { get; } = new() { Text = "Can be hit", ButtonPressed = true, FocusMode = FocusModeEnum.None, TooltipText = "A hurtbox that cannot be hit is ignored by hit tests." };
    public Button HurtApply { get; } = CombatKit.Btn("Apply", "Change the selected hurtbox.", "Primary");
    public Button HurtRemove { get; } = CombatKit.Btn("Remove", "Remove the selected hurtbox.");
    public Button HurtRemoveAll { get; } = CombatKit.Btn("Remove all", "Remove every hurtbox of this character.");
    readonly Label _hurtInfo = W.Label("", null, false);
    readonly Label _hurtBone = W.Label("", "Dim", false);
    readonly VBoxContainer _hurtEdit = new();
    readonly List<string> _hurtIds = new();
    string _hurtSig = "";
    string? _selHurt;

    public Button HitTestButton { get; } = CombatKit.Btn("Hit test…", "Swing the attack clip at another character and see where and when it hits.");
    public Button ComboButton { get; } = CombatKit.Btn("Combo editor", "Chain attacks into a combo with cancel windows.");

    static AppController App => AppController.Instance;

    static ItemList NewList() => new()
    {
        AutoTranslateMode = AutoTranslateModeEnum.Disabled, CustomMinimumSize = new Vector2(0, 90), SelectMode = ItemList.SelectModeEnum.Single,
        SizeFlagsHorizontal = SizeFlags.ExpandFill,
    };

    public int Page => _page;
    public string PhaseInfoText => _phaseInfo.Text;
    public string ContextText => _context.Text;
    public string HitHintText => _hitHint.Text;
    public string HurtInfoText => _hurtInfo.Text;
    public int EventCount => _eventIds.Count;
    public int HitboxCount => _hitIds.Count;
    public int HurtboxCount => _hurtIds.Count;
    public string? SelectedHitboxId => _selHit;
    public bool NoClipVisible => _noClip.Visible;

    public CombatPanel(MainWindow host)
    {
        _host = host;
        ThemeTypeVariation = "Floating";
        CustomMinimumSize = new Vector2(286, 0);
        SizeFlagsVertical = SizeFlags.ShrinkBegin;
        LocalizeNumeralSystem = false;
        LayoutDirection = LayoutDirectionEnum.ApplicationLocale;
        Visible = false;

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 7);
        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        var margin = W.Margin(root, 12, 10, 12, 12);
        margin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _scrollContent = margin;
        _scroll.AddChild(margin);
        AddChild(_scroll);
        root.CustomMinimumSize = new Vector2(262, 0);

        var head = new HBoxContainer();
        head.AddChild(W.Label("Combat", "Heading"));
        head.AddChild(W.Spacer());
        var close = new Button { Text = "✕", Flat = true, FocusMode = FocusModeEnum.None, TooltipText = "Close the Combat panel" };
        close.Pressed += () => App.SetCombatPanel(false);
        head.AddChild(close);
        root.AddChild(head);

        P8.Wrap(_context, 262); _context.TextDirection = TextDirection.Auto;
        root.AddChild(_context);
        Tabs.Selected += SelectPage;
        root.AddChild(Tabs);
        P8.Wrap(_noClip, 262);
        root.AddChild(_noClip);
        foreach (var page in _pages) { page.AddThemeConstantOverride("separation", 7); root.AddChild(page); }

        BuildPhases(_pages[PagePhases]);
        BuildEvents(_pages[PageEvents]);
        BuildHitboxes(_pages[PageHitboxes]);
        BuildHurtboxes(_pages[PageHurtboxes]);

        root.AddChild(new HSeparator());
        var foot = new HBoxContainer();
        foot.AddThemeConstantOverride("separation", 6);
        HitTestButton.SizeFlagsHorizontal = SizeFlags.ExpandFill; ComboButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        foot.AddChild(HitTestButton); foot.AddChild(ComboButton);
        root.AddChild(foot);

        HitTestButton.Pressed += () => _host.OpenHitTestDialog();
        ComboButton.Pressed += () => _host.OpenComboEditor();

        App.SettingsChanged += MarkDirty;
        App.PlaybackChanged += OnPlayback;
        App.SessionChanged += _ => MarkDirty();
        App.ActiveClipChanged += MarkDirty;
        _host.Selection.Changed += MarkDirty;
        SelectPage(PagePhases);
    }

    public override void _ExitTree()
    {
        App.SettingsChanged -= MarkDirty;
        App.PlaybackChanged -= OnPlayback;
        App.ActiveClipChanged -= MarkDirty;
    }

    void MarkDirty() => _dirty = true;

    void OnPlayback()
    {
        if (Visible && _page == PagePhases) UpdatePhaseInfo();
        if (!_syncing && Visible && _page == PageEvents && !EventFrame.GetLineEdit().HasFocus() && _selEvent is null) CombatKit.Set(EventFrame, App.CurrentFrame);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady()) { _dirty = true; _phaseSig = _eventSig = _hitSig = _hurtSig = _targetSig = ""; }
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        if (_dirty) { _dirty = false; Refresh(); }
        var parentH = GetParentControl()?.Size.Y ?? 0;
        var other = (_host.PhysicsPanelNode.Visible ? _host.PhysicsPanelNode.Size.Y + 10 : 0) + (_host.PropsPanelNode.Visible ? _host.PropsPanelNode.Size.Y + 10 : 0);
        var avail = Mathf.Max(parentH - 4 - ReserveBelow - other, 160);
        var need = _scrollContent.GetCombinedMinimumSize().Y;
        if (Mathf.IsEqualApprox(avail, _lastAvail) && Mathf.IsEqualApprox(need, _lastNeed)) return;
        _lastAvail = avail; _lastNeed = need;
        _scroll.CustomMinimumSize = new Vector2(0, Mathf.Min(need, avail));
    }

    public void SyncVisible()
    {
        var open = App.Settings.CombatPanel && App.Settings.AdvancedMode;
        if (Visible != open) Visible = open;
        if (open) { _dirty = false; Refresh(); }
    }

    public void SelectPage(int page)
    {
        _page = Math.Clamp(page, 0, 3);
        Tabs.SetIndex(_page);
        for (var i = 0; i < _pages.Length; i++) _pages[i].Visible = i == _page;
        _dirty = true;
        UpdateNoClip();
    }

    void UpdateNoClip() => _noClip.Visible = _page != PageHurtboxes && App.ActiveClip is null;

    // ---- construction of the pages ------------------------------------------------------------------------------------------------

    void BuildPhases(VBoxContainer page)
    {
        P8.Wrap(_phaseInfo, 262);
        page.AddChild(_phaseInfo);
        var g = CombatKit.Grid();
        void Field(string label, SpinBox box, string tip) { var l = W.Label(label, "Dim"); g.AddChild(l); box.TooltipText = tip; box.CustomMinimumSize = new Vector2(110, 0); g.AddChild(box); }
        Field("Startup starts", StartupStart, "First frame of the wind-up (usually 0).");
        Field("Active starts", ActiveStart, "First frame where the attack can hit.");
        Field("Active ends", ActiveEnd, "Where the active part ends and the recovery begins. This frame is not included.");
        Field("Recovery ends", RecoveryEnd, "Where the attack is over. This frame is not included.");
        page.AddChild(g);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        foreach (var b in new[] { SetPhasesButton, FromMarkersButton, ClearPhasesButton }) { b.SizeFlagsHorizontal = SizeFlags.ExpandFill; row.AddChild(b); }
        page.AddChild(row);
        var hint = W.Label("Frames count from 0. An end frame is the first frame after the part.", "Faint");
        P8.Wrap(hint, 262);
        page.AddChild(hint);
        SetPhasesButton.Pressed += PushPhases;
        FromMarkersButton.Pressed += () => { if (App.ActiveClip is { } c) App.Execute("combat.set_phases", CombatKit.Obj(("clipId", c.Id.ToString()), ("fromMarkers", true))); };
        ClearPhasesButton.Pressed += () => { if (App.ActiveClip is { } c) App.Execute("combat.set_phases", CombatKit.Obj(("clipId", c.Id.ToString()), ("clear", true))); };
    }

    void BuildEvents(VBoxContainer page)
    {
        var empty = W.Label("No events yet. Events tell the game when something happens, like a footstep or a sound.", "Faint");
        P8.Wrap(empty, 262);
        page.AddChild(empty);
        _eventEmpty = empty;
        page.AddChild(EventList);
        var g = CombatKit.Grid();
        var frameRow = new HBoxContainer();
        frameRow.AddThemeConstantOverride("separation", 4);
        EventFrame.CustomMinimumSize = new Vector2(80, 0);
        frameRow.AddChild(EventFrame); frameRow.AddChild(EventAtPlayhead);
        P8.Row(g, "Frame", frameRow);
        EventName.TextDirection = TextDirection.Auto; EventPayload.TextDirection = TextDirection.Ltr;
        P8.Row(g, "Name", EventName);
        P8.Row(g, "Kind", EventKind);
        P8.Row(g, "Payload", EventPayload);
        page.AddChild(g);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        foreach (var b in new[] { EventAdd, EventUpdate, EventRemove }) { b.SizeFlagsHorizontal = SizeFlags.ExpandFill; row.AddChild(b); }
        page.AddChild(row);

        EventList.ItemSelected += i => SelectEvent((int)i);
        EventAtPlayhead.Pressed += () => EventFrame.Value = App.CurrentFrame;
        EventAdd.Pressed += AddEvent;
        EventUpdate.Pressed += UpdateEvent;
        EventRemove.Pressed += RemoveEvent;
    }

    Label _eventEmpty = null!;

    void BuildHitboxes(VBoxContainer page)
    {
        var empty = W.Label("No hitboxes in this clip yet. A hitbox is the part of the attack that hurts: a fist, a foot or a blade.", "Faint");
        P8.Wrap(empty, 262);
        page.AddChild(empty);
        _hitEmpty = empty;
        page.AddChild(HitboxList);
        page.AddChild(new HSeparator());
        P8.Wrap(_hitHint, 262);
        page.AddChild(_hitHint);
        var g = CombatKit.Grid();
        HitName.TextDirection = TextDirection.Auto;
        P8.Row(g, "Name", HitName);
        HitShape.AddTo(g);
        P8.Row(g, "Attached to", AttachKind);
        P8.Row(g, "Target", AttachTarget);
        g.AddChild(new Control());
        g.AddChild(UseSelected);
        AttachTarget.ClipText = true;
        AttachKind.TooltipText = "A hitbox follows a bone of the character or a prop (like a sword) held by it.";
        page.AddChild(g);
        page.AddChild(W.Label("Offset from the bone or prop", "Section"));
        var og = CombatKit.Grid();
        void Off(string label, SpinBox box) { var l = W.Label(label, "Dim"); og.AddChild(l); box.CustomMinimumSize = new Vector2(110, 0); og.AddChild(box); }
        Off("Position X", OffX); Off("Position Y", OffY); Off("Position Z", OffZ);
        Off("Rotation X", RotX); Off("Rotation Y", RotY); Off("Rotation Z", RotZ);
        page.AddChild(og);
        page.AddChild(W.Label("When and how hard", "Section"));
        var g2 = CombatKit.Grid();
        var afRow = new HBoxContainer();
        afRow.AddThemeConstantOverride("separation", 4);
        afRow.AddChild(ActiveFrames); afRow.AddChild(ActiveFromPhase);
        P8.Row(g2, "Active frames", afRow);
        P8.Row(g2, "Damage", Damage);
        P8.Row(g2, "Knockback", Knockback);
        P8.Row(g2, "Hit-stop frames", Hitstop);
        Tags.TextDirection = TextDirection.Auto;
        P8.Row(g2, "Tags", Tags);
        Damage.TooltipText = "How much damage the hit does. The game decides what the number means.";
        Knockback.TooltipText = "How far the defender is pushed, in metres.";
        Hitstop.TooltipText = "How many frames both fighters freeze when the hit lands.";
        page.AddChild(g2);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        foreach (var b in new[] { HitAdd, HitApply, HitRemove }) { b.SizeFlagsHorizontal = SizeFlags.ExpandFill; row.AddChild(b); }
        page.AddChild(row);

        HitboxList.ItemSelected += i => SelectHitbox((int)i);
        AttachKind.ItemSelected += _ => { _targetSig = ""; FillTargets(App.ActiveClip); };
        UseSelected.Pressed += PickSelectedTarget;
        ActiveFromPhase.Pressed += () =>
        {
            if (App.ActiveClip is { Combat.Phases: { } ph } c)
                ActiveFrames.Text = $"{ph.ActiveStart.ToFrame(c.Fps)}-{ph.RecoveryStart.ToFrame(c.Fps)}";
            else _host.Toast(Tr("Set the attack phases first (Phases page)."), warn: true);
        };
        HitAdd.Pressed += () => AddHitbox();
        HitApply.Pressed += ApplyHitbox;
        HitRemove.Pressed += RemoveHitbox;
    }

    Label _hitEmpty = null!;

    void BuildHurtboxes(VBoxContainer page)
    {
        P8.Wrap(_hurtInfo, 262);
        _hurtInfo.TextDirection = TextDirection.Auto;
        page.AddChild(_hurtInfo);
        var g = CombatKit.Grid();
        P8.Row(g, "Radius scale", RadiusScale);
        RadiusScale.TooltipText = "Make every generated hurtbox thicker or thinner.";
        page.AddChild(g);
        GenerateButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        page.AddChild(GenerateButton);
        page.AddChild(HurtList);
        _hurtEdit.AddThemeConstantOverride("separation", 7);
        _hurtEdit.AddChild(new HSeparator());
        P8.Wrap(_hurtBone, 262); _hurtBone.TextDirection = TextDirection.Auto;
        _hurtEdit.AddChild(_hurtBone);
        var eg = CombatKit.Grid();
        HurtShape.AddTo(eg);
        P8.Row(eg, "Zone", HurtZone);
        HurtZone.TooltipText = "Which part of the body the hurtbox belongs to. Reactions use it: a high hit makes the head snap back.";
        eg.AddChild(new Control());
        eg.AddChild(HurtEnabled);
        _hurtEdit.AddChild(eg);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        foreach (var b in new[] { HurtApply, HurtRemove, HurtRemoveAll }) { b.SizeFlagsHorizontal = SizeFlags.ExpandFill; row.AddChild(b); }
        _hurtEdit.AddChild(row);
        page.AddChild(_hurtEdit);

        GenerateButton.Pressed += GenerateHurtboxes;
        HurtList.ItemSelected += i => SelectHurtbox((int)i);
        HurtApply.Pressed += ApplyHurtbox;
        HurtRemove.Pressed += RemoveHurtbox;
        HurtRemoveAll.Pressed += () =>
        {
            if (_host.SelectedCharacter is { } c) App.Execute("hurtbox.remove", CombatKit.Obj(("characterId", c.Id.ToString())));
        };
    }

    // ---- refresh -------------------------------------------------------------------------------------------------------------------

    public void Refresh()
    {
        _syncing = true;
        try
        {
            var clip = App.ActiveClip;
            var ch = _host.SelectedCharacter;
            UpdateNoClip();
            _context.Text = clip is null && ch is null ? "" : string.Format(CultureInfo.InvariantCulture, Tr("Clip: {0}  ·  Character: {1}"),
                clip?.Name ?? "—", ch?.Name ?? "—");
            RefreshPhases(clip);
            RefreshEvents(clip);
            RefreshHitboxes(clip);
            RefreshHurtboxes(ch);
        }
        finally { _syncing = false; }
    }

    // phases ------------------------------------------------------------------------------------------------------------------------------

    void RefreshPhases(Clip? clip)
    {
        var enabled = clip is not null;
        foreach (var b in new Button[] { SetPhasesButton, FromMarkersButton }) b.Disabled = !enabled;
        ClearPhasesButton.Disabled = clip?.Combat?.Phases is null;
        foreach (var s in new[] { StartupStart, ActiveStart, ActiveEnd, RecoveryEnd }) { s.Editable = enabled; s.MaxValue = Math.Max(1, clip?.DurationFrames ?? 1); }
        var ph = clip?.Combat?.Phases;
        var sig = clip is null ? "" : clip.Id + ":" + clip.DurationFrames + ":" + (ph is null ? "-" : $"{ph.StartupStart.Value},{ph.ActiveStart.Value},{ph.RecoveryStart.Value},{ph.End.Value}");
        if (sig != _phaseSig)
        {
            _phaseSig = sig;
            if (clip is not null)
            {
                if (ph is not null)
                {
                    CombatKit.Set(StartupStart, ph.StartupStart.ToFrame(clip.Fps)); CombatKit.Set(ActiveStart, ph.ActiveStart.ToFrame(clip.Fps));
                    CombatKit.Set(ActiveEnd, ph.RecoveryStart.ToFrame(clip.Fps)); CombatKit.Set(RecoveryEnd, ph.End.ToFrame(clip.Fps));
                }
                else
                {
                    var n = clip.DurationFrames;
                    CombatKit.Set(StartupStart, 0); CombatKit.Set(ActiveStart, n / 3); CombatKit.Set(ActiveEnd, Math.Max(n / 3 + 1, n * 2 / 3)); CombatKit.Set(RecoveryEnd, n);
                }
            }
        }
        UpdatePhaseInfo();
    }

    void UpdatePhaseInfo()
    {
        var clip = App.ActiveClip;
        if (clip is null) { _phaseInfo.Text = ""; return; }
        if (clip.Combat?.Phases is not { } ph) { _phaseInfo.Text = Tr("This clip has no attack phases yet. Set the frames below, or read them from the key-pose markers."); return; }
        var f = App.CurrentFrame;
        var t = Flicks.FromFrame(f, clip.Fps);
        var phase = t < ph.StartupStart ? "" : t < ph.ActiveStart ? "startup" : t < ph.RecoveryStart ? "active" : t < ph.End ? "recovery" : "";
        _phaseInfo.Text = phase.Length == 0
            ? string.Format(CultureInfo.InvariantCulture, Tr("Frame {0} is outside the attack."), CombatKit.Ltr(f))
            : string.Format(CultureInfo.InvariantCulture, Tr("Frame {0} is in the {1} part."), CombatKit.Ltr(f), CombatKit.PhaseLabel(phase));
        _phaseInfo.AddThemeColorOverride("font_color", phase.Length == 0 ? _host.Palette.Text2 : CombatKit.PhaseColor(_host.Palette, phase));
    }

    void PushPhases()
    {
        if (App.ActiveClip is not { } c) return;
        var s0 = (long)StartupStart.Value; var a0 = (long)ActiveStart.Value; var a1 = (long)ActiveEnd.Value; var e = (long)RecoveryEnd.Value;
        if (!(s0 <= a0 && a0 < a1 && a1 <= e && e <= c.DurationFrames))
        {
            _host.Toast(Tr("The phases must go in order: startup, then active (at least one frame), then recovery, all inside the clip."), warn: true);
            return;
        }
        App.Execute("combat.set_phases", CombatKit.Obj(("clipId", c.Id.ToString()), ("startupStart", s0), ("activeStart", a0), ("activeEnd", a1), ("recoveryEnd", e)));
    }

    // events ---------------------------------------------------------------------------------------------------------------------------------

    void RefreshEvents(Clip? clip)
    {
        var enabled = clip is not null;
        EventFrame.MaxValue = Math.Max(0, (clip?.DurationFrames ?? 1) - 1);
        EventAdd.Disabled = !enabled;
        var events = clip?.Events ?? new List<AnimationEvent>();
        var sig = (clip?.Id.ToString() ?? "") + "|" + string.Join("|", events.Select(e => $"{e.Id}{e.Time.Value}{e.Name}{(int)e.Kind}{string.Join(",", e.Data.Select(kv => kv.Key + kv.Value))}")) + "#" + _selEvent + TranslationServer.GetLocale();
        if (sig != _eventSig)
        {
            _eventSig = sig;
            _eventIds.Clear();
            EventList.Clear();
            foreach (var e in events)
            {
                _eventIds.Add(e.Id.ToString());
                EventList.AddItem($"{CombatKit.Ltr(e.Time.ToFrame(clip!.Fps))}  {(e.Name.Length > 0 ? e.Name : "—")}  ·  {CombatKit.KindLabel(e.Kind)}");
            }
            var idx = _selEvent is null ? -1 : _eventIds.IndexOf(_selEvent);
            if (idx >= 0) EventList.Select(idx); else _selEvent = null;
        }
        _eventEmpty.Visible = enabled && events.Count == 0;
        EventList.Visible = events.Count > 0;
        EventList.CustomMinimumSize = new Vector2(0, Math.Min(130, 28 + 22 * events.Count));
        EventUpdate.Disabled = EventRemove.Disabled = _selEvent is null;
        EventAtPlayhead.Disabled = !enabled;
    }

    void SelectEvent(int i)
    {
        if (i < 0 || i >= _eventIds.Count || App.ActiveClip is not { } c) return;
        _selEvent = _eventIds[i];
        var e = c.Events.Find(x => x.Id.ToString() == _selEvent);
        if (e is null) return;
        _syncing = true;
        EventFrame.Value = e.Time.ToFrame(c.Fps);
        EventName.Text = e.Name;
        EventKind.Select(Array.IndexOf(CombatKit.EventKinds, e.Kind));
        EventPayload.Text = e.Data.Count == 0 ? "" : "{" + string.Join(", ", e.Data.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"\"{kv.Key}\": {PayloadValue(kv.Value)}")) + "}";
        _syncing = false;
        EventUpdate.Disabled = EventRemove.Disabled = false;
        App.GoToFrame(e.Time.ToFrame(c.Fps));
    }

    static string PayloadValue(string v) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _) || v is "true" or "false" ? v : "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    JsonObject EventArgs(Clip c)
    {
        var a = CombatKit.Obj(("clipId", c.Id.ToString()), ("frame", (long)EventFrame.Value), ("name", EventName.Text.Trim()),
            ("kind", CombatKit.KindId(CombatKit.EventKinds[Math.Max(0, EventKind.Selected)])));
        var payload = EventPayload.Text.Trim();
        if (payload.Length > 0) a["payload"] = payload;
        return a;
    }

    void AddEvent()
    {
        if (App.ActiveClip is not { } c) return;
        if (EventName.Text.Trim().Length == 0) { _host.Toast(Tr("Give the event a name first."), warn: true); return; }
        var r = App.Execute("event.add", EventArgs(c));
        if (r.Ok) { _selEvent = (string?)r.Data?["eventId"]; _eventSig = ""; _dirty = true; }
    }

    void UpdateEvent()
    {
        if (App.ActiveClip is not { } c || _selEvent is null) return;
        var a = EventArgs(c);
        a["eventId"] = _selEvent;
        if (EventPayload.Text.Trim().Length == 0) a["payload"] = "{}"; // an emptied field clears the payload
        App.Execute("event.update", a);
    }

    void RemoveEvent()
    {
        if (App.ActiveClip is not { } c || _selEvent is null) return;
        if (App.Execute("event.remove", CombatKit.Obj(("clipId", c.Id.ToString()), ("eventId", _selEvent))).Ok) { _selEvent = null; _eventSig = ""; }
    }

    // hitboxes -----------------------------------------------------------------------------------------------------------------------------------

    void RefreshHitboxes(Clip? clip)
    {
        var enabled = clip is not null;
        var boxes = clip?.Combat?.Hitboxes ?? new List<Hitbox>();
        var project = App.Session.Project;
        var sig = (clip?.Id.ToString() ?? "") + "|" + string.Join("|", boxes.Select(h => $"{h.Id}{h.Name}{(int)h.Shape.Kind}{h.Shape.Radius}{h.Shape.Length}{h.Shape.Size}{h.Attachment.Target}{h.Damage}{h.Knockback}{h.HitstopFrames}{string.Join(",", h.Tags)}{string.Join(",", h.Active.Select(a => a.Start.Value + "-" + a.End.Value))}"))
            + "#" + _selHit + TranslationServer.GetLocale();
        if (sig != _hitSig)
        {
            _hitSig = sig;
            _hitIds.Clear();
            HitboxList.Clear();
            foreach (var h in boxes)
            {
                _hitIds.Add(h.Id.ToString());
                var target = h.Attachment.Kind == HitboxAttachKind.Bone
                    ? (clip!.Skeleton.IsEmpty ? null : project.FindRig(clip.Skeleton)?.FindBone(h.Attachment.Target)?.Name)
                    : project.FindProp(h.Attachment.Target)?.Name;
                HitboxList.AddItem($"{h.Name}  ·  {target ?? Tr("(missing)")}  ·  {CombatKit.RangesDisplay(h, clip!.Fps)}");
            }
            var idx = _selHit is null ? -1 : _hitIds.IndexOf(_selHit);
            if (idx >= 0) HitboxList.Select(idx); else _selHit = null;
        }
        _hitEmpty.Visible = enabled && boxes.Count == 0;
        HitboxList.Visible = boxes.Count > 0;
        HitboxList.CustomMinimumSize = new Vector2(0, Math.Min(130, 28 + 22 * boxes.Count));
        HitAdd.Disabled = !enabled;
        HitApply.Disabled = HitRemove.Disabled = _selHit is null;
        FillTargets(clip);
        if (_hitName_NeedsDefault && enabled) { _hitName_NeedsDefault = false; HitName.Text = NextHitboxName(clip!); }
        _hitHint.Text = clip is null ? "" : clip.Skeleton.IsEmpty && AttachKind.Selected == 0 ? Tr("This clip has no keys yet. Key a pose first so a hitbox can follow a bone, or attach to a prop.")
            : clip.Combat?.Phases is null ? Tr("Tip: set the attack phases first, then new hitboxes are active during the active part.") : "";
    }

    bool _hitName_NeedsDefault = true;

    static string NextHitboxName(Clip clip)
    {
        var n = (clip.Combat?.Hitboxes.Count ?? 0) + 1;
        while (clip.Combat?.Hitboxes.Any(h => h.Name == string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("Hitbox {0}"), n)) == true) n++;
        return string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("Hitbox {0}"), n);
    }

    /// <summary>Fill the target drop-down with the bones of the clip's rig, or the props of the project.</summary>
    void FillTargets(Clip? clip)
    {
        var project = App.Session.Project;
        var bone = AttachKind.Selected <= 0;
        var rig = clip is null || clip.Skeleton.IsEmpty ? null : project.FindRig(clip.Skeleton);
        var sig = bone ? "b:" + (rig is null ? "" : rig.Id + ":" + rig.Bones.Count) : "p:" + string.Join("|", project.Props.Select(p => p.Id + p.Name));
        sig += TranslationServer.GetLocale();
        if (sig == _targetSig) { AttachTarget.Disabled = AttachTarget.ItemCount == 0; return; }
        _targetSig = sig;
        var keep = AttachTarget.Selected >= 0 && AttachTarget.Selected < _targetIds.Count ? _targetIds[AttachTarget.Selected] : null;
        AttachTarget.Clear(); _targetIds.Clear();
        if (bone && rig is not null) foreach (var b in rig.Bones) { AttachTarget.AddItem(b.Name); _targetIds.Add(b.Id.ToString()); }
        if (!bone) foreach (var p in project.Props) { AttachTarget.AddItem(p.Name); _targetIds.Add(p.Id.ToString()); }
        AttachTarget.Disabled = AttachTarget.ItemCount == 0;
        if (AttachTarget.ItemCount == 0) return;
        var pick = keep is null ? -1 : _targetIds.IndexOf(keep);
        if (pick < 0 && !bone && _host.Selection.PropId is { } sp) pick = _targetIds.IndexOf(sp);
        if (pick < 0 && bone && _host.Selection.BoneId is { } sb) pick = _targetIds.IndexOf(sb);
        if (pick < 0 && bone && rig!.FindByStandardName("hand_r") is { } hand) pick = _targetIds.IndexOf(hand.Id.ToString());
        AttachTarget.Select(Math.Max(0, pick));
    }

    void PickSelectedTarget()
    {
        if (AttachKind.Selected == 1 && _host.Selection.PropId is { } p && _targetIds.IndexOf(p) is var i and >= 0) AttachTarget.Select(i);
        else if (AttachKind.Selected == 0 && _host.Selection.BoneId is { } b && _targetIds.IndexOf(b) is var j and >= 0) AttachTarget.Select(j);
    }

    void SelectHitbox(int i)
    {
        if (i < 0 || i >= _hitIds.Count || App.ActiveClip is not { } c) return;
        _selHit = _hitIds[i];
        var h = c.Combat?.Hitboxes.Find(x => x.Id.ToString() == _selHit);
        if (h is null) return;
        _syncing = true;
        HitName.Text = h.Name;
        HitShape.Load(h.Shape);
        AttachKind.Select(h.Attachment.Kind == HitboxAttachKind.Prop ? 1 : 0);
        _targetSig = "";
        FillTargets(c);
        var at = _targetIds.IndexOf(h.Attachment.Target.ToString());
        if (at >= 0) AttachTarget.Select(at);
        var o = h.Attachment.Offset;
        var e = o.Rotation.ToEulerXYZ();
        OffX.SetValueNoSignal(Math.Round(o.Translation.X, 4)); OffY.SetValueNoSignal(Math.Round(o.Translation.Y, 4)); OffZ.SetValueNoSignal(Math.Round(o.Translation.Z, 4));
        RotX.SetValueNoSignal(Math.Round(CombatKit.ToDegrees(e.X), 3)); RotY.SetValueNoSignal(Math.Round(CombatKit.ToDegrees(e.Y), 3)); RotZ.SetValueNoSignal(Math.Round(CombatKit.ToDegrees(e.Z), 3));
        ActiveFrames.Text = CombatKit.Ranges(h, c.Fps);
        Damage.SetValueNoSignal(h.Damage); Knockback.SetValueNoSignal(h.Knockback); Hitstop.SetValueNoSignal(h.HitstopFrames);
        Tags.Text = string.Join(", ", h.Tags);
        _syncing = false;
        HitApply.Disabled = HitRemove.Disabled = false;
    }

    JsonObject? HitboxArgs(Clip c)
    {
        var name = HitName.Text.Trim();
        if (name.Length == 0) { _host.Toast(Tr("Give the hitbox a name first."), warn: true); return null; }
        if (AttachTarget.Selected < 0 || AttachTarget.Selected >= _targetIds.Count)
        {
            _host.Toast(Tr("Choose the bone or prop the hitbox follows first."), warn: true);
            return null;
        }
        var a = CombatKit.Obj(("clipId", c.Id.ToString()), ("name", name));
        HitShape.Fill(a);
        a[AttachKind.Selected == 1 ? "propId" : "bone"] = _targetIds[AttachTarget.Selected];
        a["x"] = OffX.Value; a["y"] = OffY.Value; a["z"] = OffZ.Value; a["rx"] = RotX.Value; a["ry"] = RotY.Value; a["rz"] = RotZ.Value;
        if (ActiveFrames.Text.Trim().Length > 0) a["activeFrames"] = ActiveFrames.Text.Trim();
        a["damage"] = Damage.Value; a["knockback"] = Knockback.Value; a["hitstopFrames"] = (long)Hitstop.Value;
        a["tags"] = Tags.Text.Trim();
        return a;
    }

    /// <summary>Add a hitbox with the form's values. Returns its id (null on error; the toast shows why).</summary>
    public string? AddHitbox()
    {
        if (App.ActiveClip is not { } c || HitboxArgs(c) is not { } a) return null;
        var r = App.Execute("hitbox.add", a);
        if (!r.Ok) return null;
        _selHit = (string?)r.Data?["hitboxId"];
        _hitSig = ""; _hitName_NeedsDefault = true; _dirty = true;
        return _selHit;
    }

    void ApplyHitbox()
    {
        if (App.ActiveClip is not { } c || _selHit is null || HitboxArgs(c) is not { } a) return;
        a["hitboxId"] = _selHit;
        App.Execute("hitbox.update", a);
    }

    void RemoveHitbox()
    {
        if (App.ActiveClip is not { } c || _selHit is null) return;
        if (App.Execute("hitbox.remove", CombatKit.Obj(("clipId", c.Id.ToString()), ("hitboxId", _selHit))).Ok) { _selHit = null; _hitSig = ""; _hitName_NeedsDefault = true; }
    }

    // hurtboxes ---------------------------------------------------------------------------------------------------------------------------------------

    void RefreshHurtboxes(Character? ch)
    {
        var project = App.Session.Project;
        var rig = ch is null ? null : project.FindRig(ch.Rig);
        var boxes = ch?.Combat?.Hurtboxes ?? new List<Hurtbox>();
        GenerateButton.Disabled = ch is null;
        GenerateButton.Text = boxes.Count > 0 ? Tr("Regenerate hurtboxes") : Tr("Generate hurtboxes");
        HurtRemoveAll.Disabled = boxes.Count == 0;
        _hurtInfo.Text = ch is null ? Tr("Select a character first.") : boxes.Count == 0
            ? string.Format(CultureInfo.InvariantCulture, Tr("{0} has no hurtboxes yet. Generate them from its skeleton, then fine-tune each one."), ch.Name)
            : string.Format(CultureInfo.InvariantCulture, Tr("{0}: {1} hurtboxes."), ch.Name, CombatKit.Ltr(boxes.Count));
        var sig = (ch?.Id.ToString() ?? "") + "|" + string.Join("|", boxes.Select(h => $"{h.Id}{h.Name}{(int)h.Shape.Kind}{h.Shape.Radius}{h.Shape.Length}{h.Shape.Size}{(int)h.Zone}{h.Enabled}")) + "#" + _selHurt + TranslationServer.GetLocale();
        if (sig != _hurtSig)
        {
            _hurtSig = sig;
            _hurtIds.Clear();
            HurtList.Clear();
            foreach (var h in boxes)
            {
                _hurtIds.Add(h.Id.ToString());
                var i = HurtList.AddItem($"{h.Name}  ·  {CombatKit.ZoneLabel(h.Zone)}{(h.Enabled ? "" : "  ·  " + Tr("off"))}");
                HurtList.SetItemCustomFgColor(i, CombatKit.ZoneColor(_host.Palette, h.Zone));
            }
            var idx = _selHurt is null ? -1 : _hurtIds.IndexOf(_selHurt);
            if (idx >= 0) HurtList.Select(idx); else _selHurt = null;
        }
        HurtList.Visible = boxes.Count > 0;
        HurtList.CustomMinimumSize = new Vector2(0, Math.Min(150, 28 + 22 * Math.Min(boxes.Count, 6)));
        _hurtEdit.Visible = _selHurt is not null && boxes.Count > 0;
        if (_selHurt is not null && rig is not null && boxes.Find(x => x.Id.ToString() == _selHurt) is { } sel)
            _hurtBone.Text = string.Format(CultureInfo.InvariantCulture, Tr("Follows the bone {0}"), rig.FindBone(sel.Bone)?.Name ?? Tr("(missing)"));
    }

    void SelectHurtbox(int i)
    {
        if (i < 0 || i >= _hurtIds.Count || _host.SelectedCharacter is not { } ch) return;
        _selHurt = _hurtIds[i];
        var h = ch.Combat?.Hurtboxes.Find(x => x.Id.ToString() == _selHurt);
        if (h is null) return;
        _syncing = true;
        HurtShape.Load(h.Shape);
        HurtZone.Select((int)h.Zone);
        HurtEnabled.SetPressedNoSignal(h.Enabled);
        _syncing = false;
        _hurtEdit.Visible = true;
        _dirty = true;
    }

    void GenerateHurtboxes()
    {
        if (_host.SelectedCharacter is not { } ch) return;
        var r = App.Execute("hurtbox.generate", CombatKit.Obj(("characterId", ch.Id.ToString()), ("radiusScale", RadiusScale.Value)));
        if (r.Ok) { _selHurt = null; _hurtSig = ""; }
    }

    void ApplyHurtbox()
    {
        if (_host.SelectedCharacter is not { } ch || _selHurt is null) return;
        var a = CombatKit.Obj(("characterId", ch.Id.ToString()), ("hurtboxId", _selHurt));
        HurtShape.Fill(a);
        a["zone"] = HurtZone.Selected switch { 0 => "high", 2 => "low", _ => "mid" };
        a["enabled"] = HurtEnabled.ButtonPressed;
        App.Execute("hurtbox.update", a);
    }

    void RemoveHurtbox()
    {
        if (_host.SelectedCharacter is not { } ch || _selHurt is null) return;
        if (App.Execute("hurtbox.remove", CombatKit.Obj(("characterId", ch.Id.ToString()), ("hurtboxId", _selHurt))).Ok) { _selHurt = null; _hurtSig = ""; }
    }

    // ---- test hooks ----------------------------------------------------------------------------------------------------------------------------------

    public void SelectHitboxById(string id) { var i = _hitIds.IndexOf(id); if (i >= 0) { HitboxList.Select(i); SelectHitbox(i); } }
    public void SelectHurtboxById(string id) { var i = _hurtIds.IndexOf(id); if (i >= 0) { HurtList.Select(i); SelectHurtbox(i); } }
    public void SelectEventById(string id) { var i = _eventIds.IndexOf(id); if (i >= 0) { EventList.Select(i); SelectEvent(i); } }
    public string? SelectedEventId => _selEvent;
    public string? SelectedHurtboxId => _selHurt;
    public string? AttachTargetText => AttachTarget.Selected >= 0 ? AttachTarget.GetItemText(AttachTarget.Selected) : null;

    public void ChooseAttach(bool prop, string name)
    {
        AttachKind.Select(prop ? 1 : 0);
        _targetSig = "";
        FillTargets(App.ActiveClip);
        for (var i = 0; i < AttachTarget.ItemCount; i++) if (AttachTarget.GetItemText(i) == name) AttachTarget.Select(i);
    }

    public string ItemTextOfHitbox(int i) => HitboxList.GetItemText(i);
    public string ItemTextOfEvent(int i) => EventList.GetItemText(i);
    public string ItemTextOfHurtbox(int i) => HurtList.GetItemText(i);
    public bool HurtEditVisible => _hurtEdit.Visible;
}
