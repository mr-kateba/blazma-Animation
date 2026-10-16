using Blazma.Core.Model;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Bottom dock for Phase 1: the project's clips as cards. Always left-to-right, like the timeline.</summary>
public partial class ClipStrip : PanelContainer
{
    readonly MainWindow _host;
    readonly HBoxContainer _row = new();
    readonly ScrollContainer _scroll = new();
    readonly Label _count = W.Label("", "Faint", false);
    readonly PopupMenu _context = new();
    readonly HBoxContainer _poseRow = new();
    readonly ScrollContainer _poseScroll = new();
    readonly Label _posesHint = W.Label("Click a pose to select it, double-click to apply it", "Faint");
    readonly Label _clipsHint = W.Label("Click to select, double-click to rename", "Faint");
    Segmented _tabs = null!;
    readonly Label _clipsTitle = W.Label("Clips", "Section");
    readonly HBoxContainer _poseButtons = new();
    Button _poseSave = null!, _poseApply = null!, _poseRename = null!, _poseDelete = null!;
    bool _posesMode;
    readonly Dictionary<string, ClipCard> _cards = new();
    string? _contextClip;

    const int RenameId = 1, DeleteId = 2;

    public IReadOnlyDictionary<string, ClipCard> Cards => _cards;

    public ClipStrip(MainWindow host)
    {
        _host = host;
        ThemeTypeVariation = "Floating";
        CustomMinimumSize = new Vector2(0, 138);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);
        AddChild(W.Margin(root, 12, 8, 12, 10));

        var head = new HBoxContainer { LayoutDirection = LayoutDirectionEnum.Locale };
        _tabs = new Segmented("Clips", "Poses");
        _tabs.Selected += i => SetPosesMode(i == 1);
        head.AddChild(_tabs);
        head.AddChild(_clipsTitle);
        head.AddChild(_count);
        head.AddChild(W.Spacer());
        _poseButtons.AddThemeConstantOverride("separation", 6);
        _poseSave = MiniButton("Save current pose…", () => _host.PromptSavePose(), true);
        _poseApply = MiniButton("Apply", () => _host.ApplySelectedPose());
        _poseRename = MiniButton("Rename…", () => _host.PromptRenamePose());
        _poseDelete = MiniButton("Delete", () => _host.ConfirmDeletePose());
        _poseButtons.Visible = false;
        head.AddChild(_poseButtons);
        head.AddChild(_clipsHint);
        head.AddChild(_posesHint);
        _posesHint.Visible = false;
        root.AddChild(head);

        _scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
        _scroll.LayoutDirection = LayoutDirectionEnum.Ltr; // time-like strips never mirror
        _row.AddThemeConstantOverride("separation", 10);
        _scroll.AddChild(_row);
        root.AddChild(_scroll);

        _poseScroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        _poseScroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
        _poseScroll.LayoutDirection = LayoutDirectionEnum.Ltr;
        _poseRow.AddThemeConstantOverride("separation", 10);
        _poseScroll.AddChild(_poseRow);
        _poseScroll.Visible = false;
        root.AddChild(_poseScroll);

        _context.AddItem("Rename…", RenameId);
        _context.AddItem("Delete", DeleteId);
        _context.IdPressed += id =>
        {
            if (_contextClip is null) return;
            if (id == RenameId) _host.PromptRenameClip(_contextClip);
            else if (id == DeleteId) _host.ConfirmDeleteClip(_contextClip);
        };
        AddChild(_context);
    }

    Button MiniButton(string text, Action pressed, bool primary = false)
    {
        var b = new Button { Text = text, FocusMode = FocusModeEnum.None, ThemeTypeVariation = primary ? "Primary" : "" };
        b.Pressed += pressed;
        _poseButtons.AddChild(b);
        return b;
    }

    bool _embedded;

    /// <summary>Used inside the timeline dock: no card background and the dock's own tabs choose Clips or Poses.</summary>
    public void Embed()
    {
        _embedded = true;
        _tabs.Visible = false;
        _clipsTitle.Visible = false;
        ThemeTypeVariation = "";
        AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        CustomMinimumSize = Vector2.Zero;
        if (GetChild(0) is MarginContainer m)
            foreach (var k in new[] { "margin_left", "margin_top", "margin_right", "margin_bottom" }) m.AddThemeConstantOverride(k, 0);
    }

    public bool PosesMode => _posesMode;
    public Segmented Tabs => _tabs;
    public IReadOnlyCollection<PoseCard> PoseCards => _poseCards;
    readonly List<PoseCard> _poseCards = new();

    /// <summary>The Clips | Poses toggle is for Simple mode; Advanced mode has its own Poses tab in the left dock.</summary>
    public void SetTabsAvailable(bool on)
    {
        if (_embedded) return;
        _tabs.Visible = on;
        _clipsTitle.Visible = !on;
        if (!on) SetPosesMode(false);
    }

    public void SetPosesMode(bool poses)
    {
        _posesMode = poses;
        _tabs.SetIndex(poses ? 1 : 0);
        _scroll.Visible = !poses;
        _poseScroll.Visible = poses;
        _count.Visible = !poses;
        _clipsHint.Visible = !poses;
        _posesHint.Visible = poses;
        _poseButtons.Visible = poses;
        if (poses) RebuildPoses();
    }

    /// <summary>Rebuild the pose cards (Simple mode, Poses toggle).</summary>
    public void RebuildPoses()
    {
        if (!_posesMode) return;
        foreach (var c in _poseRow.GetChildren()) { _poseRow.RemoveChild(c); c.QueueFree(); }
        _poseCards.Clear();
        var palette = _host.Palette;
        foreach (var pose in _host.LibraryPoses())
        {
            var card = new PoseCard(this, pose, palette);
            card.SetSelected(pose.Id.ToString() == _host.SelectedPoseId);
            _poseCards.Add(card);
            _poseRow.AddChild(card);
        }
        _poseRow.AddChild(new NewPoseCard(this, palette));
        var has = _host.SelectedPoseId is not null;
        _poseApply.Disabled = _poseRename.Disabled = _poseDelete.Disabled = !has;
        _poseSave.Disabled = _host.SelectedCharacter is null;
    }

    internal void SelectPose(string id) { _host.SelectedPoseId = id; RebuildPoses(); _host.Dock.Poses.Rebuild(); }
    internal void ApplyPose(string id) { _host.SelectedPoseId = id; _host.ApplySelectedPose(); RebuildPoses(); }
    internal void SavePose() => _host.PromptSavePose();

    public void Rebuild()
    {
        foreach (var c in _row.GetChildren()) { _row.RemoveChild(c); c.QueueFree(); }
        _cards.Clear();
        var palette = _host.Palette;
        var clips = AppController.Instance.Session.Project.Clips;
        foreach (var clip in clips)
        {
            var card = new ClipCard(this, clip, palette);
            _cards[clip.Id.ToString()] = card;
            _row.AddChild(card);
        }
        _row.AddChild(new NewClipCard(this, palette));
        _count.Text = string.Format("· {0}", clips.Count);
        UpdateSelection();
        RebuildPoses();
    }

    public void UpdateSelection()
    {
        foreach (var (id, card) in _cards) card.SetSelected(id == _host.SelectedClipId);
        if (_host.SelectedClipId is { } sel && _cards.TryGetValue(sel, out var c)) EnsureVisible(c);
    }

    void EnsureVisible(Control c)
    {
        _scroll.EnsureControlVisible(c);
    }

    internal void Select(string id) => _host.SelectClip(id);
    internal void Rename(string id) => _host.PromptRenameClip(id);
    internal void NewClip() => _host.CreateClip();

    internal void ShowContext(string id, Vector2 globalPos)
    {
        _contextClip = id;
        _host.SelectClip(id);
        _context.Position = (Vector2I)globalPos + GetWindow().Position;
        _context.Popup();
    }
}

public partial class ClipCard : PanelContainer
{
    readonly ClipStrip _strip;
    readonly Palette _p;
    public string ClipId { get; }
    public Label NameLabel { get; }
    bool _selected, _hover;

    public ClipCard(ClipStrip strip, Clip clip, Palette p)
    {
        _strip = strip; _p = p;
        ClipId = clip.Id.ToString();
        CustomMinimumSize = new Vector2(168, 0);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        LayoutDirection = AppController.Instance.IsRtl ? LayoutDirectionEnum.Rtl : LayoutDirectionEnum.Ltr;

        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 6);
        NameLabel = W.Label(clip.Name, null, false);
        NameLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        NameLabel.ClipText = true;
        NameLabel.TextDirection = Control.TextDirection.Auto;
        NameLabel.MouseFilter = MouseFilterEnum.Ignore;
        v.AddChild(NameLabel);

        var meta = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        var fpsWord = W.Label(string.Format(TranslationServer.Translate("{0} fps · {1} f"), Fmt.Rate(clip.Fps), clip.DurationFrames), "MonoFaint", false);
        fpsWord.MouseFilter = MouseFilterEnum.Ignore;
        meta.AddChild(fpsWord);
        meta.AddChild(W.Spacer());
        if (clip.IsDraft)
        {
            var chip = new PanelContainer { ThemeTypeVariation = "DraftChip", MouseFilter = MouseFilterEnum.Ignore };
            var l = W.Label("Draft", "MonoFaint");
            l.AddThemeColorOverride("font_color", p.Accent);
            l.MouseFilter = MouseFilterEnum.Ignore;
            chip.AddChild(l);
            meta.AddChild(chip);
        }
        v.AddChild(meta);
        v.AddChild(W.Spacer());
        AddChild(W.Margin(v, 12, 10, 12, 10));
        ApplyStyle();
        MouseEntered += () => { _hover = true; ApplyStyle(); };
        MouseExited += () => { _hover = false; ApplyStyle(); };
    }

    public void SetSelected(bool on) { _selected = on; ApplyStyle(); }

    void ApplyStyle()
    {
        var sb = _selected
            ? BlazmaTheme.Box(_p.AccentSoft, _p.Accent, 6, 1, 0, 0, 0, 0)
            : BlazmaTheme.Box(_hover ? _p.Raised : _p.Panel2, _hover ? _p.LineStrong : _p.Line, 6, 1, 0, 0, 0, 0);
        AddThemeStyleboxOverride("panel", sb);
        NameLabel.AddThemeColorOverride("font_color", _selected ? _p.Text : _p.Text2);
        NameLabel.ThemeTypeVariation = _selected ? "Heading" : "";
    }

    public void Activate() => _strip.Select(ClipId);

    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true } mb) return;
        if (mb.ButtonIndex == MouseButton.Left)
        {
            _strip.Select(ClipId);
            if (mb.DoubleClick) _strip.Rename(ClipId);
            AcceptEvent();
        }
        else if (mb.ButtonIndex == MouseButton.Right)
        {
            _strip.ShowContext(ClipId, GetGlobalMousePosition());
            AcceptEvent();
        }
    }
}

public partial class NewClipCard : PanelContainer
{
    readonly ClipStrip _strip;
    readonly Palette _p;
    bool _hover;

    public NewClipCard(ClipStrip strip, Palette p)
    {
        _strip = strip; _p = p;
        CustomMinimumSize = new Vector2(132, 0);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        LayoutDirection = AppController.Instance.IsRtl ? LayoutDirectionEnum.Rtl : LayoutDirectionEnum.Ltr;
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
        v.AddThemeConstantOverride("separation", 2);
        var plus = W.Label("+", null, false);
        plus.AddThemeFontSizeOverride("font_size", 24);
        plus.AddThemeColorOverride("font_color", p.Accent);
        plus.HorizontalAlignment = HorizontalAlignment.Center;
        plus.MouseFilter = MouseFilterEnum.Ignore;
        var txt = W.Label("New clip", "Dim");
        txt.HorizontalAlignment = HorizontalAlignment.Center;
        txt.MouseFilter = MouseFilterEnum.Ignore;
        v.AddChild(plus); v.AddChild(txt);
        AddChild(W.Margin(v, 10, 6, 10, 6));
        Style();
        MouseEntered += () => { _hover = true; Style(); };
        MouseExited += () => { _hover = false; Style(); };
    }

    void Style()
    {
        var sb = BlazmaTheme.Box(_hover ? _p.Panel2 : _p.Panel, _hover ? _p.Accent : _p.LineStrong, 6, 1, 0, 0, 0, 0);
        AddThemeStyleboxOverride("panel", sb);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            _strip.NewClip();
            AcceptEvent();
        }
    }
}

/// <summary>A saved pose in the Simple-mode bottom dock.</summary>
public partial class PoseCard : PanelContainer
{
    readonly ClipStrip _strip;
    readonly Palette _p;
    public string PoseId { get; }
    public Label NameLabel { get; }
    bool _selected, _hover;

    public PoseCard(ClipStrip strip, PoseAsset pose, Palette p)
    {
        _strip = strip; _p = p;
        PoseId = pose.Id.ToString();
        CustomMinimumSize = new Vector2(150, 0);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        LayoutDirection = AppController.Instance.IsRtl ? LayoutDirectionEnum.Rtl : LayoutDirectionEnum.Ltr;
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 6);
        NameLabel = W.Label(pose.Name, null, false);
        NameLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        NameLabel.ClipText = true;
        NameLabel.TextDirection = Control.TextDirection.Auto;
        NameLabel.MouseFilter = MouseFilterEnum.Ignore;
        v.AddChild(NameLabel);
        var meta = W.Label(pose.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), "MonoFaint", false);
        meta.MouseFilter = MouseFilterEnum.Ignore;
        v.AddChild(meta);
        v.AddChild(W.Spacer());
        AddChild(W.Margin(v, 12, 10, 12, 10));
        Style();
        MouseEntered += () => { _hover = true; Style(); };
        MouseExited += () => { _hover = false; Style(); };
    }

    public void SetSelected(bool on) { _selected = on; Style(); }

    void Style()
    {
        AddThemeStyleboxOverride("panel", _selected
            ? BlazmaTheme.Box(_p.AccentSoft, _p.Accent, 6, 1, 0, 0, 0, 0)
            : BlazmaTheme.Box(_hover ? _p.Raised : _p.Panel2, _hover ? _p.LineStrong : _p.Line, 6, 1, 0, 0, 0, 0));
        NameLabel.AddThemeColorOverride("font_color", _selected ? _p.Text : _p.Text2);
    }

    public void Activate() => _strip.SelectPose(PoseId);

    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb) return;
        if (mb.DoubleClick) _strip.ApplyPose(PoseId); else _strip.SelectPose(PoseId);
        AcceptEvent();
    }
}

public partial class NewPoseCard : PanelContainer
{
    readonly ClipStrip _strip;
    readonly Palette _p;
    bool _hover;

    public NewPoseCard(ClipStrip strip, Palette p)
    {
        _strip = strip; _p = p;
        CustomMinimumSize = new Vector2(132, 0);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        LayoutDirection = AppController.Instance.IsRtl ? LayoutDirectionEnum.Rtl : LayoutDirectionEnum.Ltr;
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
        v.AddThemeConstantOverride("separation", 2);
        var plus = W.Label("+", null, false);
        plus.AddThemeFontSizeOverride("font_size", 24);
        plus.AddThemeColorOverride("font_color", p.Accent);
        plus.HorizontalAlignment = HorizontalAlignment.Center;
        plus.MouseFilter = MouseFilterEnum.Ignore;
        var txt = W.Label("Save current pose", "Dim");
        txt.HorizontalAlignment = HorizontalAlignment.Center;
        txt.MouseFilter = MouseFilterEnum.Ignore;
        v.AddChild(plus); v.AddChild(txt);
        AddChild(W.Margin(v, 10, 6, 10, 6));
        Style();
        MouseEntered += () => { _hover = true; Style(); };
        MouseExited += () => { _hover = false; Style(); };
    }

    void Style() => AddThemeStyleboxOverride("panel", BlazmaTheme.Box(_hover ? _p.Panel2 : _p.Panel, _hover ? _p.Accent : _p.LineStrong, 6, 1, 0, 0, 0, 0));

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { _strip.SavePose(); AcceptEvent(); }
    }
}
