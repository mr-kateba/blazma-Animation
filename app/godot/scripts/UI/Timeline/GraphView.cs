using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Motion;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Advanced-mode graph editor: curves of the selected bone or control, drawn from TrackSampler (re-sampled only when the
/// clip changes). Keys can be dragged (vertical = value, horizontal = time) and cubic keys show tangent handles. Always LTR.
/// </summary>
public partial class GraphView : HBoxContainer
{
    public sealed class Chan
    {
        public Track Track = null!;
        public int Comp;
        public string Label = "";
        public Color Color;
        public bool Visible;
        public string Key = "";
    }

    readonly MainWindow _host;
    readonly VBoxContainer _list = new();
    readonly Label _hint = W.Label("Select a bone or control to see its curves.", "Faint");
    readonly Dictionary<string, bool> _vis = new();
    public GraphCanvas Canvas { get; }
    public Button AutoTangentsButton { get; }
    public Button FrameAllButton { get; }
    public List<Chan> Channels { get; } = new();
    public Dictionary<string, CheckBox> Checks { get; } = new();
    string? _builtTarget, _builtClip;
    ulong _builtVersion = ulong.MaxValue;

    static AppController App => AppController.Instance;
    Palette P => _host.Palette;

    public GraphView(MainWindow host)
    {
        _host = host;
        LayoutDirection = LayoutDirectionEnum.Ltr;
        AddThemeConstantOverride("separation", 8);
        var side = new VBoxContainer { CustomMinimumSize = new Vector2(176, 0) };
        side.AddThemeConstantOverride("separation", 6);
        _hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _hint.CustomMinimumSize = new Vector2(160, 0);
        side.AddChild(_hint);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _list.AddThemeConstantOverride("separation", 2);
        _list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_list);
        side.AddChild(scroll);
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 6);
        AutoTangentsButton = new Button { Text = "Auto tangents", FocusMode = FocusModeEnum.None, ThemeTypeVariation = "Transport",
            TooltipText = "Reset the tangents of the selected keys to automatic smoothing." };
        AutoTangentsButton.Pressed += () => Canvas!.AutoTangents();
        FrameAllButton = new Button { Text = "Frame all", FocusMode = FocusModeEnum.None, ThemeTypeVariation = "Transport", TooltipText = "Fit the visible curves in the view (F)." };
        FrameAllButton.Pressed += () => Canvas!.FrameAll();
        buttons.AddChild(AutoTangentsButton);
        buttons.AddChild(FrameAllButton);
        side.AddChild(buttons);
        AddChild(side);
        Canvas = new GraphCanvas(host, this) { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        AddChild(Canvas);
        App.ActiveClipChanged += () => Rebuild(true);
        App.SessionChanged += a =>
        {
            if (a.Changes.Any(c => c.Kind is Change.ClipChanged or Change.ClipAdded or Change.ClipRemoved or Change.ProjectReplaced)) Rebuild(false);
        };
        _host.Selection.Changed += () => Rebuild(false);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady()) Rebuild(true);
    }

    static string[] CompNames(string channel) => channel == TrackChannels.Rotation ? new[] { "X", "Y", "Z", "W" } : new[] { "X", "Y", "Z" };

    public void Rebuild(bool force)
    {
        var clip = App.ActiveClip;
        var target = _host.Selection.BoneId ?? _host.Selection.ControlId;
        var id = clip?.Id.ToString();
        if (!force && target == _builtTarget && id == _builtClip && App.ClipVersion == _builtVersion) return;
        var targetChanged = target != _builtTarget || id != _builtClip;
        var structureSame = !force && !targetChanged && Channels.Count > 0 && clip is not null
            && Channels.Select(c => c.Track.Id.ToString()).Distinct().OrderBy(x => x).SequenceEqual(
                ClipKeys.TracksOf(clip, target).Where(t => t.Channel != TrackChannels.Pinned).Select(t => t.Id.ToString()).OrderBy(x => x));
        _builtTarget = target; _builtClip = id; _builtVersion = App.ClipVersion;
        if (structureSame)
        {
            // Same tracks: only refresh the Track references (clip edits replace the objects).
            foreach (var ch in Channels) ch.Track = clip!.Tracks.Find(t => t.Id == ch.Track.Id) ?? ch.Track;
            Canvas.QueueRedraw();
            return;
        }
        foreach (var c in _list.GetChildren()) { _list.RemoveChild(c); c.QueueFree(); }
        Channels.Clear();
        Checks.Clear();
        var p = P;
        if (clip is not null && target is not null)
        {
            var tracks = ClipKeys.TracksOf(clip, target).Where(t => t.Channel != TrackChannels.Pinned)
                .OrderBy(t => t.Channel == TrackChannels.Rotation ? 0 : t.Channel == TrackChannels.Translation ? 1 : 2).ToList();
            for (var ti = 0; ti < tracks.Count; ti++)
            {
                var t = tracks[ti];
                var names = CompNames(t.Channel);
                var arity = TrackChannels.Arity(t.Channel);
                var title = TranslationServer.Translate(t.Channel switch { TrackChannels.Rotation => "Rotation", TrackChannels.Translation => "Position", _ => "Scale" });
                for (var c = 0; c < arity && c < names.Length; c++)
                {
                    var key = $"{target}|{t.Channel}|{c}";
                    var color = c switch { 0 => p.X, 1 => p.Y, 2 => p.Z, _ => p.Text2 };
                    if (t.Channel == TrackChannels.Translation) color = color.Lightened(0.18f);
                    if (t.Channel == TrackChannels.Scale) color = color.Darkened(0.2f);
                    if (!_vis.TryGetValue(key, out var visible)) visible = ti == 0;
                    Channels.Add(new Chan { Track = t, Comp = c, Label = $"{title} {names[c]}", Color = color, Visible = visible, Key = key });
                }
            }
            foreach (var ch in Channels) AddRow(ch);
        }
        _hint.Visible = Channels.Count == 0;
        _hint.Text = clip is null ? "No keys yet. Press Key to record the pose." : target is null ? "Select a bone or control to see its curves." : "This bone has no keys in this clip.";
        Canvas.NeedFrame();
        Canvas.QueueRedraw();
    }

    void AddRow(Chan ch)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var sw = new ColorRect { Color = ch.Color, CustomMinimumSize = new Vector2(10, 10), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var cb = new CheckBox { Text = ch.Label, ButtonPressed = ch.Visible, FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cb.AddThemeColorOverride("font_color", ch.Color);
        cb.AddThemeColorOverride("font_pressed_color", ch.Color);
        cb.AddThemeColorOverride("font_hover_color", ch.Color);
        cb.AddThemeColorOverride("font_hover_pressed_color", ch.Color);
        cb.Toggled += on => { ch.Visible = on; _vis[ch.Key] = on; Canvas.NeedFrame(); Canvas.QueueRedraw(); };
        row.AddChild(sw);
        row.AddChild(cb);
        _list.AddChild(row);
        Checks[ch.Label] = cb;
    }

    public void SetVisible(string label, bool on)
    {
        if (Checks.TryGetValue(label, out var cb)) cb.ButtonPressed = on;
    }
}

public partial class GraphCanvas : Control
{
    public const float RulerH = 24, LeftM = 50;

    sealed class Samples
    {
        public double[] Frames = Array.Empty<double>();
        public double[][] Values = Array.Empty<double[]>();
    }

    readonly MainWindow _host;
    readonly GraphView _view;
    readonly FrameAxis _axis = new();
    readonly Dictionary<string, Samples> _cache = new();
    ulong _cacheVersion = ulong.MaxValue;
    string? _cacheClip;
    double _mid;
    double _ppv = 100;
    bool _needFrame = true;
    readonly HashSet<(string track, long frame, int comp)> _sel = new();

    enum Mode { None, Scrub, Pan, Key, Tangent }
    Mode _mode;
    Vector2 _press, _lastMouse;
    enum Axis { None, Horizontal, Vertical }
    Axis _axisLock;
    TransactionScope? _tx;
    Dictionary<(string, long, int), double> _startValues = new();
    long _dragDf;
    double _lastDv = double.NaN;
    (string track, long frame, int comp, bool isOut) _tangent;
    int _hoverKey = -1;

    static AppController App => AppController.Instance;
    Palette P => _host.Palette;
    public FrameAxis Axis_ => _axis;
    public IReadOnlyCollection<(string track, long frame, int comp)> Selection => _sel;
    public double PixelsPerValue => _ppv;

    public GraphCanvas(MainWindow host, GraphView view)
    {
        _host = host; _view = view;
        LayoutDirection = LayoutDirectionEnum.Ltr;
        FocusMode = FocusModeEnum.Click;
        ClipContents = true;
        CustomMinimumSize = new Vector2(300, 120);
        App.PlaybackChanged += QueueRedraw;
        MouseEntered += () => { if (!HasFocus() && GetViewport().GuiGetFocusOwner() is not LineEdit) GrabFocus(); };
        Resized += () => { if (_needFrame) QueueRedraw(); };
    }

    public void NeedFrame() { _needFrame = true; _sel.Clear(); }

    // ---- sampling (only when the clip changed) ---------------------------------------------------------------------------

    Samples SamplesOf(Track t)
    {
        var clip = App.ActiveClip!;
        var id = clip.Id.ToString();
        if (_cacheVersion != App.ClipVersion || _cacheClip != id) { _cache.Clear(); _cacheVersion = App.ClipVersion; _cacheClip = id; }
        var key = t.Id.ToString();
        if (_cache.TryGetValue(key, out var s)) return s;
        var live = clip.Tracks.Find(x => x.Id == t.Id) ?? t;
        var last = Math.Max(1, clip.DurationFrames - 1);
        const int per = 4;
        var n = (int)last * per + 1;
        var arity = TrackChannels.Arity(live.Channel);
        s = new Samples { Frames = new double[n], Values = new double[arity][] };
        for (var c = 0; c < arity; c++) s.Values[c] = new double[n];
        for (var i = 0; i < n; i++)
        {
            var f = (double)i / per;
            s.Frames[i] = f;
            var v = TrackSampler.Sample(live, Blazma.Core.Timing.Flicks.FromSeconds(f / clip.Fps.FramesPerSecond));
            for (var c = 0; c < arity; c++) s.Values[c][i] = v is null ? 0 : v[c];
        }
        return _cache[key] = s;
    }

    // ---- view ---------------------------------------------------------------------------------------------------------------------

    float PlotTop => RulerH + 6;
    float PlotH => Math.Max(20, Size.Y - PlotTop - 6);
    float YOf(double v) => (float)(PlotTop + PlotH / 2 - (v - _mid) * _ppv);
    double ValueAt(float y) => _mid - (y - (PlotTop + PlotH / 2)) / _ppv;

    public void FrameAll()
    {
        _needFrame = false;
        _axis.Left = LeftM;
        _axis.Fit(0, Math.Max(1, App.FrameCount - 1), Size.X - LeftM, 18);
        var lo = double.MaxValue; var hi = double.MinValue;
        var clip = App.ActiveClip;
        if (clip is not null)
            foreach (var ch in _view.Channels.Where(c => c.Visible))
                foreach (var v in SamplesOf(ch.Track).Values[ch.Comp]) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        if (lo > hi) { lo = -1; hi = 1; }
        if (hi - lo < 1e-4) { lo -= 0.5; hi += 0.5; }
        var pad = (hi - lo) * 0.12;
        lo -= pad; hi += pad;
        _mid = (lo + hi) / 2;
        _ppv = PlotH / (hi - lo);
        QueueRedraw();
    }

    // ---- drawing ------------------------------------------------------------------------------------------------------------------

    public override void _Draw()
    {
        var clip = App.ActiveClip;
        if (_needFrame && Size.X > 50) FrameAll();
        _axis.Left = LeftM;
        var p = P;
        var mono = BlazmaTheme.Mono;
        var font = BlazmaTheme.Regular;
        var w = Size.X; var h = Size.Y;
        DrawRect(new Rect2(0, 0, w, h), p.Panel2 with { A = 0.35f });
        var last = Math.Max(1, App.FrameCount - 1);
        DrawRect(new Rect2(_axis.X(0), RulerH, Math.Max(1, _axis.X(last) - _axis.X(0)), h), p.Panel with { A = 0.55f });

        // horizontal value grid
        var span = PlotH / _ppv;
        var rawStep = 44 / _ppv;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        var vstep = new[] { 1.0, 2.0, 5.0, 10.0 }.Select(m => m * mag).First(s => s >= rawStep);
        var v0 = Math.Floor((_mid - span / 2) / vstep) * vstep;
        for (var v = v0; v <= _mid + span / 2 + vstep; v += vstep)
        {
            var y = YOf(v);
            if (y < RulerH || y > h) continue;
            DrawLine(new Vector2(LeftM, y), new Vector2(w, y), (Math.Abs(v) < 1e-9 ? p.LineStrong : p.Line) with { A = Math.Abs(v) < 1e-9 ? 0.9f : 0.5f });
            DrawString(mono, new Vector2(4, y + 4), v.ToString("0.###", CultureInfo.InvariantCulture), HorizontalAlignment.Left, LeftM - 8, 10, p.Text3);
        }
        // vertical frame grid + ruler
        var step = _axis.TickStep();
        for (var f = 0L; f <= last; f++)
        {
            var x = _axis.X(f);
            if (x < LeftM || x > w) continue;
            var major = f % step == 0;
            if (major) DrawLine(new Vector2(x, RulerH), new Vector2(x, h), p.Line with { A = 0.55f });
            if (major || _axis.Ppf >= 7)
            {
                DrawLine(new Vector2(x, RulerH - (major ? 7 : 3)), new Vector2(x, RulerH), p.LineStrong);
                if (major) DrawString(mono, new Vector2(x + 3, 12), TlText.Frame(f), HorizontalAlignment.Left, -1, 10, p.Text3);
            }
        }
        DrawLine(new Vector2(0, RulerH), new Vector2(w, RulerH), p.LineStrong);
        DrawLine(new Vector2(LeftM - 2, RulerH), new Vector2(LeftM - 2, h), p.Line);

        if (clip is null || _view.Channels.Count == 0)
        {
            DrawString(font, new Vector2(LeftM + 16, RulerH + 34),
                TranslationServer.Translate(clip is null ? "No keys yet. Press Key to record the pose." : "Select a bone or control that has keys."),
                HorizontalAlignment.Left, -1, 12, p.Text3);
        }
        else DrawCurves(clip, p);

        // playhead
        var px = _axis.X(App.CurrentFrameExact);
        if (px >= LeftM && px <= w)
        {
            DrawLine(new Vector2(px, 0), new Vector2(px, h), p.Accent, 1.5f);
            var label = TlText.Frame(App.CurrentFrame);
            var tw = mono.GetStringSize(label, HorizontalAlignment.Left, -1, 10).X;
            DrawStyleBox(BlazmaTheme.Box(p.Accent, null, 3, 0, 0, 0, 0, 0), new Rect2(px - tw / 2 - 4, 1, tw + 8, 15));
            DrawString(mono, new Vector2(px - tw / 2, 12.5f), label, HorizontalAlignment.Left, -1, 10, p.OnAccent);
        }
    }

    void DrawCurves(Clip clip, Palette p)
    {
        var pts = new List<Vector2>();
        foreach (var ch in _view.Channels.Where(c => c.Visible))
        {
            var s = SamplesOf(ch.Track);
            pts.Clear();
            var vals = s.Values[ch.Comp];
            for (var i = 0; i < s.Frames.Length; i++) pts.Add(new Vector2(_axis.X(s.Frames[i]), YOf(vals[i])));
            if (pts.Count >= 2) DrawPolyline(pts.ToArray(), ch.Color, 1.8f, true);
        }
        foreach (var ch in _view.Channels.Where(c => c.Visible))
        {
            var track = clip.Tracks.Find(t => t.Id == ch.Track.Id);
            if (track is null) continue;
            for (var ki = 0; ki < track.Keys.Count; ki++)
            {
                var k = track.Keys[ki];
                var f = ClipKeys.FrameOf(clip, k);
                var selected = _sel.Contains((track.Id.ToString(), f, ch.Comp));
                var dv = selected && _mode == Mode.Key && _axisLock == Axis.Vertical ? 0 : 0.0;
                var drawF = selected && _mode == Mode.Key && _axisLock == Axis.Horizontal ? f + _dragDf : f;
                var c = new Vector2(_axis.X(drawF), YOf(k.Value[ch.Comp] + dv));
                if (selected)
                {
                    DrawCircle(c, 6.5f, ch.Color with { A = 0.28f });
                    DrawCircle(c, 4.6f, p.Text);
                    DrawCircle(c, 3.4f, ch.Color);
                    if (k.Interp == Interpolation.Cubic && track.Channel != TrackChannels.Pinned) DrawHandles(clip, track, ki, ch, c, p);
                }
                else
                {
                    DrawCircle(c, 4f, p.Bg);
                    DrawCircle(c, 3f, ch.Color);
                }
            }
        }
    }

    (Vector2 inPt, Vector2 outPt, bool ok) HandleGeometry(Clip clip, Track track, int keyIndex, int comp, Vector2 keyPos)
    {
        var arity = TrackChannels.Arity(track.Channel);
        var isRot = track.Channel == TrackChannels.Rotation;
        var sIn = TrackSampler.InTangent(track, keyIndex, arity, isRot)[comp];
        var sOut = TrackSampler.OutTangent(track, keyIndex, arity, isRot)[comp];
        const float len = 46;
        Vector2 Dir(double s)
        {
            var d = new Vector2((float)(_axis.Ppf * clip.Fps.FramesPerSecond), (float)(-s * _ppv));
            return d.Normalized();
        }
        return (keyPos - Dir(sIn) * len, keyPos + Dir(sOut) * len, true);
    }

    void DrawHandles(Clip clip, Track track, int ki, GraphView.Chan ch, Vector2 c, Palette p)
    {
        var (a, b, _) = HandleGeometry(clip, track, ki, ch.Comp, c);
        DrawLine(a, c, p.Text2, 1.2f, true);
        DrawLine(c, b, p.Text2, 1.2f, true);
        foreach (var pt in new[] { a, b })
        {
            DrawCircle(pt, 4.6f, p.Bg);
            DrawArc(pt, 4.6f, 0, Mathf.Tau, 16, ch.Color, 1.6f, true);
        }
    }

    // ---- hit testing -----------------------------------------------------------------------------------------------------------------

    public Vector2? KeyPos(string trackId, long frame, int comp)
    {
        var clip = App.ActiveClip;
        var track = clip?.Tracks.Find(t => t.Id.ToString() == trackId);
        var key = track?.Keys.Find(k => ClipKeys.FrameOf(clip!, k) == frame);
        if (key is null) return null;
        _axis.Left = LeftM;
        return new Vector2(_axis.X(frame), YOf(key.Value[comp]));
    }

    public (Vector2 inPt, Vector2 outPt)? HandlePositions(string trackId, long frame, int comp)
    {
        var clip = App.ActiveClip;
        var track = clip?.Tracks.Find(t => t.Id.ToString() == trackId);
        if (track is null) return null;
        var ki = track.Keys.FindIndex(k => ClipKeys.FrameOf(clip!, k) == frame);
        if (ki < 0 || KeyPos(trackId, frame, comp) is not { } c) return null;
        var (a, b, _) = HandleGeometry(clip!, track, ki, comp, c);
        return (a, b);
    }

    (string track, long frame, int comp)? KeyAt(Vector2 pos)
    {
        var clip = App.ActiveClip;
        if (clip is null) return null;
        (string, long, int)? best = null; var bd = 9f;
        foreach (var ch in _view.Channels.Where(c => c.Visible))
        {
            var track = clip.Tracks.Find(t => t.Id == ch.Track.Id);
            if (track is null) continue;
            foreach (var k in track.Keys)
            {
                var f = ClipKeys.FrameOf(clip, k);
                var d = new Vector2(_axis.X(f), YOf(k.Value[ch.Comp])).DistanceTo(pos);
                if (d < bd) { bd = d; best = (track.Id.ToString(), f, ch.Comp); }
            }
        }
        return best;
    }

    (string track, long frame, int comp, bool isOut)? HandleAt(Vector2 pos)
    {
        var clip = App.ActiveClip;
        if (clip is null) return null;
        foreach (var (tid, f, comp) in _sel)
        {
            if (_view.Channels.All(c => !(c.Visible && c.Track.Id.ToString() == tid && c.Comp == comp))) continue;
            var track = clip.Tracks.Find(t => t.Id.ToString() == tid);
            var ki = track?.Keys.FindIndex(k => ClipKeys.FrameOf(clip, k) == f) ?? -1;
            if (track is null || ki < 0 || track.Keys[ki].Interp != Interpolation.Cubic || KeyPos(tid, f, comp) is not { } c) continue;
            var (a, b, _) = HandleGeometry(clip, track, ki, comp, c);
            if (b.DistanceTo(pos) <= 8) return (tid, f, comp, true);
            if (a.DistanceTo(pos) <= 8) return (tid, f, comp, false);
        }
        return null;
    }

    // ---- input -----------------------------------------------------------------------------------------------------------------------------

    long FrameFromX(float x) => Math.Clamp((long)Math.Round(_axis.FrameAt(x)), 0, Math.Max(0, App.FrameCount - 1));

    public override void _GuiInput(InputEvent e)
    {
        _axis.Left = LeftM;
        switch (e)
        {
            case InputEventKey k when k.Pressed && !k.Echo:
                if (GetViewport().GuiGetFocusOwner() is LineEdit) return;
                if (!k.CtrlPressed && !k.AltPressed && (k.Keycode == Godot.Key.F || k.Keycode == Godot.Key.Home)) { FrameAll(); AcceptEvent(); }
                else if (TransportKeys.Handle(_host, k)) AcceptEvent();
                break;
            case InputEventMouseButton mb: Button(mb); break;
            case InputEventMouseMotion mm: Motion(mm); break;
        }
    }

    void Button(InputEventMouseButton mb)
    {
        var pos = mb.Position;
        switch (mb.ButtonIndex)
        {
            case MouseButton.WheelUp or MouseButton.WheelDown:
            {
                var zoomIn = mb.ButtonIndex == MouseButton.WheelUp;
                var f = zoomIn ? 1.15 : 1 / 1.15;
                if (mb.CtrlPressed)
                {
                    var v = ValueAt(pos.Y);
                    _ppv = Math.Clamp(_ppv * f, 0.01, 1e6);
                    _mid = v + (pos.Y - (PlotTop + PlotH / 2)) / _ppv;
                }
                else _axis.ZoomAt(pos.X, f);
                QueueRedraw();
                AcceptEvent();
                break;
            }
            case MouseButton.Middle:
                _mode = mb.Pressed ? Mode.Pan : Mode.None;
                _lastMouse = pos;
                AcceptEvent();
                break;
            case MouseButton.Left:
                if (mb.Pressed) Press(mb); else Release();
                AcceptEvent();
                break;
        }
    }

    void Press(InputEventMouseButton mb)
    {
        GrabFocus();
        var pos = mb.Position;
        _press = pos; _lastMouse = pos; _axisLock = Axis.None; _dragDf = 0; _lastDv = double.NaN;
        if (pos.Y < RulerH && pos.X >= LeftM) { _mode = Mode.Scrub; App.GoToFrame(FrameFromX(pos.X)); return; }
        if (HandleAt(pos) is { } h)
        {
            _mode = Mode.Tangent; _tangent = h;
            _tx = App.BeginTransaction("Set tangents");
            return;
        }
        var add = mb.CtrlPressed || mb.ShiftPressed;
        if (KeyAt(pos) is { } key)
        {
            if (add && _sel.Contains(key)) { _sel.Remove(key); QueueRedraw(); return; }
            if (!add && !_sel.Contains(key)) _sel.Clear();
            _sel.Add(key);
            _mode = Mode.Key;
            CaptureStart();
            QueueRedraw();
            return;
        }
        if (!add) _sel.Clear();
        _mode = Mode.None;
        QueueRedraw();
    }

    void CaptureStart()
    {
        _startValues.Clear();
        var clip = App.ActiveClip!;
        foreach (var (tid, f, comp) in _sel)
        {
            var key = clip.Tracks.Find(t => t.Id.ToString() == tid)?.Keys.Find(k => ClipKeys.FrameOf(clip, k) == f);
            if (key is not null) _startValues[(tid, f, comp)] = key.Value[comp];
        }
    }

    void Motion(InputEventMouseMotion mm)
    {
        var pos = mm.Position;
        switch (_mode)
        {
            case Mode.Scrub:
                if ((mm.ButtonMask & MouseButtonMask.Left) == 0) { _mode = Mode.None; break; }
                App.GoToFrame(FrameFromX(pos.X));
                break;
            case Mode.Pan:
                _axis.Pan(pos.X - _lastMouse.X);
                _mid += (pos.Y - _lastMouse.Y) / _ppv;
                _lastMouse = pos;
                QueueRedraw();
                break;
            case Mode.Key:
                if ((mm.ButtonMask & MouseButtonMask.Left) == 0) { Release(); break; }
                DragKeys(pos);
                break;
            case Mode.Tangent:
                if ((mm.ButtonMask & MouseButtonMask.Left) == 0) { Release(); break; }
                DragTangent(pos, mm.AltPressed);
                break;
        }
    }

    void DragKeys(Vector2 pos)
    {
        var d = pos - _press;
        if (_axisLock == Axis.None)
        {
            if (d.Length() < 5) return;
            _axisLock = Math.Abs(d.X) > Math.Abs(d.Y) ? Axis.Horizontal : Axis.Vertical;
            if (_axisLock == Axis.Vertical) _tx = App.BeginTransaction("Edit curve");
        }
        if (_axisLock == Axis.Horizontal)
        {
            var minF = _sel.Min(s => s.frame);
            _dragDf = (long)Math.Max(-minF, Math.Round(d.X / _axis.Ppf));
            QueueRedraw();
            return;
        }
        var dv = -d.Y / _ppv;
        if (Math.Abs(dv - _lastDv) < 1e-12) return;
        _lastDv = dv;
        SetValues(dv);
    }

    /// <summary>Set every selected key to its start value plus <paramref name="dv"/> (key.set_value).</summary>
    public void SetValues(double dv)
    {
        var clip = App.ActiveClip;
        if (clip is null) return;
        foreach (var (key, start) in _startValues)
            App.Execute("key.set_value", new JsonObject
            {
                ["clipId"] = clip.Id.ToString(), ["trackId"] = key.Item1, ["frame"] = key.Item2, ["component"] = key.Item3, ["value"] = start + dv,
            });
        QueueRedraw();
    }

    void DragTangent(Vector2 pos, bool alt)
    {
        var clip = App.ActiveClip;
        if (clip is null) return;
        if (KeyPos(_tangent.track, _tangent.frame, _tangent.comp) is not { } c) return;
        var v = _tangent.isOut ? pos - c : c - pos;
        if (v.X < 3) v.X = 3;
        var slope = -(v.Y / _ppv) / (v.X / (_axis.Ppf * clip.Fps.FramesPerSecond));
        SetTangent(_tangent.track, _tangent.frame, _tangent.comp, slope, _tangent.isOut, !alt);
    }

    public void SetTangent(string trackId, long frame, int comp, double slope, bool isOut, bool both)
    {
        var clip = App.ActiveClip;
        if (clip is null) return;
        var args = new JsonObject { ["clipId"] = clip.Id.ToString(), ["trackId"] = trackId, ["frame"] = frame, ["component"] = comp };
        if (isOut || both) args["outSlope"] = slope;
        if (!isOut || both) args["inSlope"] = slope;
        App.Execute("key.set_tangents", args);
        QueueRedraw();
    }

    void Release()
    {
        var mode = _mode;
        _mode = Mode.None;
        var clip = App.ActiveClip;
        if (mode == Mode.Key && _axisLock == Axis.Horizontal && _dragDf != 0 && clip is not null)
            MoveSelectedKeys((int)_dragDf);
        if (_tx is not null)
        {
            _tx.Commit();
            _tx = null;
            App.ApplyFrameIfClean();
        }
        _dragDf = 0;
        _axisLock = Axis.None;
        QueueRedraw();
    }

    /// <summary>Horizontal drag: key.move for each bone/control that has selected keys (all its channels move together).</summary>
    public bool MoveSelectedKeys(int delta)
    {
        var clip = App.ActiveClip;
        if (clip is null || delta == 0 || _sel.Count == 0) return false;
        var byTarget = _sel.GroupBy(s => clip.Tracks.Find(t => t.Id.ToString() == s.track)?.Target.Id.ToString() ?? "")
            .Where(g => g.Key.Length > 0).ToList();
        var ok = true;
        var own = _tx is null;
        var tx = own ? App.BeginTransaction("Move keys") : null;
        foreach (var g in byTarget)
            ok &= App.Execute("key.move", new JsonObject
            {
                ["clipId"] = clip.Id.ToString(), ["frames"] = string.Join(",", g.Select(s => s.frame).Distinct()), ["delta"] = delta, ["bones"] = g.Key,
            }).Ok;
        tx?.Commit();
        var moved = _sel.ToList();
        _sel.Clear();
        foreach (var s in moved) _sel.Add((s.track, s.frame + delta, s.comp));
        if (own) App.ApplyFrameIfClean();
        QueueRedraw();
        return ok;
    }

    public bool AutoTangents()
    {
        var clip = App.ActiveClip;
        if (clip is null) return false;
        if (_sel.Count == 0) { App.RaiseInfo(TranslationServer.Translate("Select some keys first.")); return false; }
        var ok = true;
        using (var tx = App.BeginTransaction("Auto tangents"))
        {
            foreach (var g in _sel.GroupBy(s => s.track))
                ok &= App.Execute("key.auto_tangents", new JsonObject
                {
                    ["clipId"] = clip.Id.ToString(), ["trackId"] = g.Key, ["frames"] = string.Join(",", g.Select(s => s.frame).Distinct()),
                }).Ok;
            tx.Commit();
        }
        App.ApplyFrameIfClean();
        QueueRedraw();
        return ok;
    }

    // ---- test hooks ---------------------------------------------------------------------------------------------------------------------

    public void SelectKey(string trackId, long frame, int comp, bool add = false)
    {
        if (!add) _sel.Clear();
        _sel.Add((trackId, frame, comp));
        CaptureStart();
        QueueRedraw();
    }
}
