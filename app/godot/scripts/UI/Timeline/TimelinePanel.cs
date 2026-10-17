using System.Globalization;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Bottom dock: Timeline | Dope Sheet | Graph | Clips | Poses. The transport row (play, keys, frame, auto-key, Key) is shared by
/// the first three tabs. Time always runs left to right, also in Arabic.
/// </summary>
public partial class TimelinePanel : PanelContainer
{
    public const int TabTimeline = 0, TabDope = 1, TabGraph = 2, TabClips = 3, TabPoses = 4;

    readonly MainWindow _host;
    public ClipStrip Strip { get; }
    public TimelineCanvas Canvas { get; }
    public KeyPoseStrip KeyPoses { get; }
    public BalanceStrip Balance { get; }
    public DopeSheetView Dope { get; }
    public GraphView Graph { get; }
    public Segmented Tabs { get; }
    public OptionButton ClipPick { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, FocusMode = FocusModeEnum.None, FitToLongestItem = false };
    public Button AddMarkerButton { get; }
    public Button ExportButton { get; }
    public Button StartButton { get; }
    public Button PrevKeyButton { get; }
    public Button PlayButton { get; }
    public Button NextKeyButton { get; }
    public Button EndButton { get; }
    public Button LoopButton { get; }
    public LineEdit FrameField { get; }
    public Button AutoKeyButton { get; }
    public Button KeyButton { get; }
    public Button DeleteKeyButton { get; }
    public Dot UnkeyedDot { get; } = new();
    readonly Label _total = W.Label("", "MonoLabel", false);
    readonly Label _fps = W.Label("", "MonoLabel", false);
    readonly Control _transport;
    readonly Control _timelineTab;
    int _tab;
    string _clipSig = "";

    public int Tab => _tab;

    static AppController App => AppController.Instance;

    public TimelinePanel(MainWindow host)
    {
        _host = host;
        ThemeTypeVariation = "Floating";
        CustomMinimumSize = new Vector2(0, 284);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);
        AddChild(W.Margin(root, 12, 8, 12, 10));

        // header: tabs, clip picker, add marker
        var head = new HBoxContainer { LayoutDirection = LayoutDirectionEnum.Locale };
        head.AddThemeConstantOverride("separation", 10);
        Tabs = new Segmented("Timeline", "Dope Sheet", "Graph", "Clips", "Poses");
        Tabs.Selected += i => SelectTab(i);
        head.AddChild(Tabs);
        var clipLabel = W.Label("Clip", "Dim");
        head.AddChild(clipLabel);
        ClipPick.CustomMinimumSize = new Vector2(150, 0);
        ClipPick.TooltipText = "The clip you are animating. Keys go into this clip.";
        ClipPick.ItemSelected += OnClipPicked;
        head.AddChild(ClipPick);
        head.AddChild(W.Spacer());
        AddMarkerButton = new Button { Text = "+ Marker", ThemeTypeVariation = "Transport", FocusMode = FocusModeEnum.None,
            TooltipText = "Add a marker at the playhead. Double-click a marker to rename it." };
        AddMarkerButton.Pressed += () => _host.AddMarkerAtPlayhead();
        head.AddChild(AddMarkerButton);
        ExportButton = new Button { Text = "Export", ThemeTypeVariation = "Transport", FocusMode = FocusModeEnum.None,
            TooltipText = "Export the clip as a .glb animation (Ctrl+E)." };
        ExportButton.Pressed += () => _host.OpenExportDialog();
        head.AddChild(ExportButton);
        root.AddChild(head);

        // transport (always LTR: time order)
        var tr = new HBoxContainer { LayoutDirection = LayoutDirectionEnum.Ltr };
        tr.AddThemeConstantOverride("separation", 5);
        StartButton = Icon(tr, UiIcons.Start, "Go to the first frame (Home)", () => App.GoToStart());
        PrevKeyButton = Icon(tr, UiIcons.PrevKey, "Previous key (Up or Alt+Left)", () => App.GoToPreviousKey());
        PlayButton = Icon(tr, UiIcons.Play, "Play or pause (Space)", () => App.TogglePlay());
        PlayButton.CustomMinimumSize = new Vector2(40, 30);
        NextKeyButton = Icon(tr, UiIcons.NextKey, "Next key (Down or Alt+Right)", () => App.GoToNextKey());
        EndButton = Icon(tr, UiIcons.End, "Go to the last frame (End)", () => App.GoToEnd());
        LoopButton = Icon(tr, UiIcons.Loop, "Loop the clip. Drag the handles on the ruler to set the loop range.", () => ToggleLoop(), false);
        LoopButton.ToggleMode = true;
        tr.AddChild(new VSeparator());
        tr.AddChild(W.Label("Frame", "Dim"));
        FrameField = new LineEdit
        {
            ThemeTypeVariation = "MonoEdit", CustomMinimumSize = new Vector2(58, 0), SelectAllOnFocus = true, AutoTranslateMode = AutoTranslateModeEnum.Disabled,
            TextDirection = Control.TextDirection.Ltr, LocalizeNumeralSystem = false, Alignment = HorizontalAlignment.Center,
            TooltipText = "Current frame. Type a number and press Enter to jump there.",
        };
        FrameField.TextSubmitted += OnFrameTyped;
        FrameField.FocusExited += () => SyncFrame();
        tr.AddChild(FrameField);
        _total.LocalizeNumeralSystem = false; _fps.LocalizeNumeralSystem = false;
        _total.TextDirection = Control.TextDirection.Ltr; _fps.TextDirection = Control.TextDirection.Ltr;
        tr.AddChild(_total);
        tr.AddChild(new Control { CustomMinimumSize = new Vector2(8, 0) });
        tr.AddChild(_fps);
        tr.AddChild(W.Spacer());
        AutoKeyButton = new Button
        {
            Text = "Auto-key", ThemeTypeVariation = "Rec", ToggleMode = true, FocusMode = FocusModeEnum.None, Icon = UiIcons.Make("<circle cx='8' cy='8' r='4.2'/>"),
            TooltipText = "When on, every pose change is keyed at the current frame automatically.",
        };
        AutoKeyButton.Toggled += on => App.SetAutoKey(on);
        tr.AddChild(AutoKeyButton);
        DeleteKeyButton = new Button { Text = "Delete key", ThemeTypeVariation = "Transport", FocusMode = FocusModeEnum.None, TooltipText = "Delete all keys at this frame (Alt+K)." };
        DeleteKeyButton.Pressed += () => _host.DeleteKeyHere();
        tr.AddChild(DeleteKeyButton);
        KeyButton = new Button { Text = "Key", ThemeTypeVariation = "KeyBtn", FocusMode = FocusModeEnum.None };
        KeyButton.Pressed += () => _host.KeyAll();
        UnkeyedDot.SetAnchorsPreset(LayoutPreset.TopRight);
        UnkeyedDot.OffsetLeft = -15; UnkeyedDot.OffsetRight = -7; UnkeyedDot.OffsetTop = 5; UnkeyedDot.OffsetBottom = 13;
        UnkeyedDot.Visible = false;
        KeyButton.AddChild(UnkeyedDot);
        tr.AddChild(KeyButton);
        _transport = tr;
        root.AddChild(tr);

        // tab content
        var stack = new Control { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        var tl = new VBoxContainer { LayoutDirection = LayoutDirectionEnum.Ltr };
        tl.AddThemeConstantOverride("separation", 8);
        Canvas = new TimelineCanvas(host) { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        KeyPoses = new KeyPoseStrip(host) { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        Balance = new BalanceStrip(host, Canvas) { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        tl.AddChild(Canvas);
        tl.AddChild(Balance);
        tl.AddChild(KeyPoses);
        _timelineTab = tl;
        Dope = new DopeSheetView(host);
        Graph = new GraphView(host);
        Strip = new ClipStrip(host);
        Strip.Embed();
        foreach (var c in new Control[] { tl, Dope, Graph, Strip }) { W.Fill(c); stack.AddChild(c); }
        root.AddChild(stack);

        App.PlaybackChanged += Sync;
        App.ActiveClipChanged += () => SyncClips();
        App.SessionChanged += a =>
        {
            if (a.Changes.Any(c => c.Kind is Change.ClipChanged or Change.ClipAdded or Change.ClipRemoved or Change.ProjectReplaced)) SyncClips();
        };
        SelectTab(TabTimeline);
        SyncClips();
        Sync();
    }

    Button Icon(Container parent, string svg, string tip, Action pressed, bool filled = true)
    {
        var b = new Button
        {
            Icon = UiIcons.Make(svg, filled), ThemeTypeVariation = "Transport", FocusMode = FocusModeEnum.None, TooltipText = tip,
            CustomMinimumSize = new Vector2(34, 30), IconAlignment = HorizontalAlignment.Center, ExpandIcon = false,
        };
        b.Pressed += pressed;
        parent.AddChild(b);
        return b;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady()) { Sync(); SyncClips(true); }
    }

    // ---- tabs -----------------------------------------------------------------------------------------------------------------

    public void SelectTab(int index)
    {
        if (index == TabGraph && !App.Settings.AdvancedMode) index = TabTimeline;
        _tab = index;
        Tabs.SetIndex(_tab);
        _timelineTab.Visible = _tab == TabTimeline;
        Dope.Visible = _tab == TabDope;
        Graph.Visible = _tab == TabGraph;
        Strip.Visible = _tab >= TabClips;
        _transport.Visible = _tab <= TabGraph;
        AddMarkerButton.Visible = _tab <= TabGraph;
        if (_tab >= TabClips) Strip.SetPosesMode(_tab == TabPoses);
        if (_tab == TabGraph) Graph.Rebuild(true);
        Dope.QueueRedraw(); Canvas.QueueRedraw();
        CustomMinimumSize = new Vector2(0, _tab switch { TabDope => 336, TabGraph => 350, _ => 284 });
    }

    /// <summary>Graph is an Advanced-mode tab.</summary>
    public void ApplyMode(bool advanced)
    {
        Tabs[TabGraph].Visible = advanced;
        if (!advanced && _tab == TabGraph) SelectTab(TabTimeline);
    }

    // ---- sync ------------------------------------------------------------------------------------------------------------------

    void SyncFrame()
    {
        if (FrameField.HasFocus()) return;
        FrameField.Text = TlText.Frame(App.CurrentFrame);
    }

    public void Sync()
    {
        var clip = App.ActiveClip;
        PlayButton.Icon = UiIcons.Make(App.Playing ? UiIcons.Pause : UiIcons.Play);
        PlayButton.TooltipText = App.Playing ? "Pause (Space)" : "Play or pause (Space)";
        LoopButton.SetPressedNoSignal(clip is { Loop.Enabled: true });
        LoopButton.Disabled = clip is null;
        AutoKeyButton.SetPressedNoSignal(App.AutoKey);
        SyncFrame();
        var last = Math.Max(0, App.FrameCount - 1);
        _total.Text = "/ " + TlText.Frame(last);
        _fps.Text = string.Format(CultureInfo.InvariantCulture, Tr("{0} fps"), Fmt.Rate(App.ActiveFps));
        var dirty = App.PoseDirty;
        UnkeyedDot.Visible = dirty;
        UnkeyedDot.SetColor(_host.Palette.Warn);
        KeyButton.TooltipText = Tr("Key the whole pose at this frame (K). Shift+K keys only the selected bone or control.")
            + (dirty ? "\n" + Tr("Unkeyed changes are lost when you change frame") : "");
        KeyButton.Disabled = _host.SelectedCharacter is null;
        DeleteKeyButton.Disabled = clip is null;
    }

    void OnFrameTyped(string text)
    {
        if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var f)) App.GoToFrame(f);
        FrameField.ReleaseFocus();
        SyncFrame();
    }

    void ToggleLoop()
    {
        if (App.ActiveClip is not { } clip) return;
        App.SetLoop(!clip.Loop.Enabled);
    }

    // ---- clip picker -----------------------------------------------------------------------------------------------------

    List<string> _pickIds = new();

    public void SyncClips(bool force = false)
    {
        var clips = App.Session.Project.Clips;
        var sig = string.Join("|", clips.Select(c => c.Id + c.Name)) + "#" + App.ActiveClipId + TranslationServer.GetLocale();
        if (!force && sig == _clipSig) { Sync(); return; }
        _clipSig = sig;
        ClipPick.Clear();
        _pickIds = clips.Select(c => c.Id.ToString()).ToList();
        if (clips.Count == 0)
        {
            ClipPick.AddItem(Tr("No clip yet"));
            ClipPick.Disabled = true;
            ClipPick.Select(0);
        }
        else
        {
            ClipPick.Disabled = false;
            foreach (var c in clips) ClipPick.AddItem(c.Name);
            var idx = App.ActiveClipId is { } a ? _pickIds.IndexOf(a) : -1;
            if (idx >= 0) ClipPick.Select(idx); else ClipPick.Selected = -1;
        }
        Sync();
    }

    void OnClipPicked(long index)
    {
        if (index < 0 || index >= _pickIds.Count) return;
        _host.SelectClip(_pickIds[(int)index]);
    }
}
