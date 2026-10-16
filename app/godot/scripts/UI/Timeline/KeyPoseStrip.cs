using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Simple-mode key-pose strip (mockup B): the clip's phase markers as cards. Click = jump to that frame,
/// "Set pose" keys the current pose there. Without phase markers one button adds the seven phases.
/// </summary>
public partial class KeyPoseStrip : PanelContainer
{
    readonly MainWindow _host;
    readonly HBoxContainer _row = new();
    readonly ScrollContainer _scroll = new();
    readonly List<PhaseCard> _cards = new();
    string? _selected;
    string _signature = "";

    static AppController App => AppController.Instance;

    public IReadOnlyList<PhaseCard> Cards => _cards;
    public string? SelectedMarkerId => _selected;

    public KeyPoseStrip(MainWindow host)
    {
        _host = host;
        LayoutDirection = LayoutDirectionEnum.Ltr;
        ThemeTypeVariation = "";
        AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        CustomMinimumSize = new Vector2(0, 62);
        _scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Disabled;
        _scroll.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        _row.AddThemeConstantOverride("separation", 8);
        _scroll.AddChild(_row);
        AddChild(_scroll);
        App.PlaybackChanged += Restyle;
        App.ActiveClipChanged += () => Rebuild(true);
        App.SessionChanged += a =>
        {
            if (a.Changes.Any(c => c.Kind is Change.ClipChanged or Change.ClipAdded or Change.ClipRemoved or Change.ProjectReplaced)) Rebuild(false);
        };
        Rebuild(true);
    }

    public override void _ExitTree() { App.PlaybackChanged -= Restyle; }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady()) Rebuild(true);
    }

    string Signature(Clip? clip)
    {
        if (clip is null) return "none";
        var keys = ClipKeys.Frames(clip, clip.Tracks);
        return string.Join("|", clip.Markers.Where(m => m.Phase != KeyPhase.None)
            .Select(m => $"{m.Id}:{m.Time.Value}:{m.Name}:{keys.Contains(m.Time.ToFrame(clip.Fps))}")) + ":" + _host.Palette.Name + ":" + TranslationServer.GetLocale();
    }

    public void Rebuild(bool force)
    {
        var clip = App.ActiveClip;
        var sig = Signature(clip);
        if (!force && sig == _signature) return;
        _signature = sig;
        foreach (var c in _row.GetChildren()) { _row.RemoveChild(c); c.QueueFree(); }
        _cards.Clear();
        var palette = _host.Palette;
        var phases = clip?.Markers.Where(m => m.Phase != KeyPhase.None).OrderBy(m => m.Time.Value).ToList() ?? new List<Marker>();
        if (_selected is not null && phases.All(m => m.Id.ToString() != _selected)) _selected = null;
        if (phases.Count == 0)
        {
            var add = new Button { Text = "Add key-pose phases", ThemeTypeVariation = "Primary", FocusMode = FocusModeEnum.None,
                TooltipText = "Adds Initial Pose, Anticipation, Main Action, Contact, Follow-through, Recovery and Final Pose markers to this clip.",
                SizeFlagsVertical = SizeFlags.ShrinkCenter };
            add.Pressed += () => _host.AddPhaseMarkers();
            AddButton = add;
            var hint = W.Label("Key-pose phases mark the main poses of an action. Click one to jump to it, then pose the character and press Set pose.", "Faint");
            hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            hint.CustomMinimumSize = new Vector2(360, 0);
            hint.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            _row.AddChild(add);
            _row.AddChild(hint);
            Restyle();
            return;
        }
        AddButton = null;
        var keys = clip is null ? new SortedSet<long>() : ClipKeys.Frames(clip, clip.Tracks);
        foreach (var m in phases)
        {
            var frame = m.Time.ToFrame(clip!.Fps);
            var card = new PhaseCard(this, m, frame, keys.Contains(frame), palette);
            _cards.Add(card);
            _row.AddChild(card);
        }
        Restyle();
    }

    public Button? AddButton { get; private set; }

    void Restyle()
    {
        foreach (var c in _cards) c.Style(c.MarkerId == _selected, c.Frame == App.CurrentFrame);
    }

    internal void Click(PhaseCard card)
    {
        _selected = card.MarkerId;
        App.GoToFrame(card.Frame);
        Restyle();
    }

    internal void SetPose(PhaseCard card)
    {
        _selected = card.MarkerId;
        _host.SetPoseAtFrame(card.Frame);
        Rebuild(true);
    }
}

public partial class PhaseCard : PanelContainer
{
    readonly KeyPoseStrip _strip;
    readonly Palette _p;
    readonly Label _name;
    readonly Button _set;
    readonly Dot _dot = new();
    readonly Label _state;
    bool _hover, _selected, _current;
    public string MarkerId { get; }
    public long Frame { get; }
    public bool Keyed { get; }
    public Button SetPoseButton => _set;
    public string DisplayName => _name.Text;

    public PhaseCard(KeyPoseStrip strip, Marker m, long frame, bool keyed, Palette p)
    {
        _strip = strip; _p = p;
        MarkerId = m.Id.ToString(); Frame = frame; Keyed = keyed;
        CustomMinimumSize = new Vector2(124, 0);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        LayoutDirection = AppController.Instance.IsRtl ? LayoutDirectionEnum.Rtl : LayoutDirectionEnum.Ltr;

        var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        h.AddThemeConstantOverride("separation", 8);
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        v.AddThemeConstantOverride("separation", 3);
        _name = W.Label(TlText.MarkerLabel(m), null, false);
        _name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _name.ClipText = true;
        _name.TextDirection = Control.TextDirection.Auto;
        _name.MouseFilter = MouseFilterEnum.Ignore;
        v.AddChild(_name);
        var meta = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, LayoutDirection = LayoutDirectionEnum.Ltr };
        meta.AddThemeConstantOverride("separation", 6);
        var fr = W.Label("f " + TlText.Frame(frame), "MonoFaint", false);
        fr.LocalizeNumeralSystem = false;
        fr.MouseFilter = MouseFilterEnum.Ignore;
        _dot.SetColor(keyed ? p.Accent : p.Text3);
        meta.AddChild(fr);
        meta.AddChild(_dot);
        _state = W.Label(keyed ? "Keyed" : "No key yet", "MonoFaint");
        _state.MouseFilter = MouseFilterEnum.Ignore;
        meta.AddChild(_state);
        v.AddChild(meta);
        h.AddChild(v);
        _set = new Button { Text = "Set pose", ThemeTypeVariation = "Primary", FocusMode = FocusModeEnum.None, Visible = false,
            TooltipText = "Key the current pose at this phase's frame.", SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _set.Pressed += () => _strip.SetPose(this);
        h.AddChild(_set);
        AddChild(W.Margin(h, 10, 7, 10, 7));
        Style(false, false);
        MouseEntered += () => { _hover = true; Style(_selected, _current); };
        MouseExited += () => { _hover = false; Style(_selected, _current); };
    }

    public void Style(bool selected, bool current)
    {
        _selected = selected; _current = current;
        var sb = selected
            ? BlazmaTheme.Box(_p.AccentSoft, _p.Accent, 6, 1, 0, 0, 0, 0)
            : BlazmaTheme.Box(_hover ? _p.Raised : _p.Panel2, current ? _p.Accent : _hover ? _p.LineStrong : _p.Line, 6, 1, 0, 0, 0, 0);
        AddThemeStyleboxOverride("panel", sb);
        _name.AddThemeColorOverride("font_color", selected || current ? _p.Text : _p.Text2);
        _set.Visible = selected;
    }

    public void Activate() => _strip.Click(this);

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            _strip.Click(this);
            AcceptEvent();
        }
    }
}
