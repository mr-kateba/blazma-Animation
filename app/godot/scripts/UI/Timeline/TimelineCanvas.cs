using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Shared keyboard handling for the transport (Space, arrows, Home/End, K).</summary>
public static class TransportKeys
{
    public static bool Handle(MainWindow host, InputEventKey k)
    {
        if (!k.Pressed || k.Echo || k.CtrlPressed || k.MetaPressed) return false;
        var app = AppController.Instance;
        switch (k.Keycode)
        {
            case Godot.Key.Space: app.TogglePlay(); return true;
            case Godot.Key.Left when k.AltPressed: app.GoToPreviousKey(); return true;
            case Godot.Key.Right when k.AltPressed: app.GoToNextKey(); return true;
            case Godot.Key.Left: app.StepFrames(-1); return true;
            case Godot.Key.Right: app.StepFrames(1); return true;
            case Godot.Key.Up: app.GoToNextKey(); return true;
            case Godot.Key.Down: app.GoToPreviousKey(); return true;
            case Godot.Key.Home: app.GoToStart(); return true;
            case Godot.Key.End: app.GoToEnd(); return true;
            case Godot.Key.K:
                if (k.AltPressed) host.DeleteKeyHere();
                else if (k.ShiftPressed) host.KeySelected();
                else host.KeyAll();
                return true;
        }
        return false;
    }
}

/// <summary>
/// Ruler, playhead, summary row of all keys, a row for the selected bone, markers and the loop range.
/// Always left to right. Drawn by hand: it is cheaper and lines up exactly with the playhead.
/// </summary>
public partial class TimelineCanvas : Control
{
    public const float Gutter = 124, RulerH = 24, RowH = 22, MarkerH = 26;
    const int FontSize = 11;

    readonly MainWindow _host;
    readonly FrameAxis _axis = new();
    readonly PopupMenu _context = new();
    readonly List<(string id, Rect2 rect, long frame)> _chips = new();
    string? _contextMarker;

    enum Drag { None, Scrub, LoopStart, LoopEnd, Marker }
    Drag _drag;
    string? _dragMarker;
    long _dragFrame;
    long _dragLoopStart, _dragLoopEnd;

    static AppController App => AppController.Instance;
    Palette P => _host.Palette;

    public FrameAxis Axis => _axis;
    public IReadOnlyList<(string id, Rect2 rect, long frame)> Chips => _chips;

    public TimelineCanvas(MainWindow host)
    {
        _host = host;
        LayoutDirection = LayoutDirectionEnum.Ltr;
        FocusMode = FocusModeEnum.Click;
        CustomMinimumSize = new Vector2(300, RulerH + 2 * RowH + MarkerH + 4);
        MouseFilter = MouseFilterEnum.Stop;
        _context.AddItem("Rename…", 1);
        _context.AddItem("Delete", 2);
        _context.IdPressed += id =>
        {
            if (_contextMarker is null) return;
            if (id == 1) _host.PromptRenameMarker(_contextMarker); else _host.DeleteMarker(_contextMarker);
        };
        AddChild(_context);
        App.PlaybackChanged += QueueRedraw;
        App.ActiveClipChanged += QueueRedraw;
        App.SessionChanged += OnSession;
        _host.Selection.Changed += QueueRedraw;
        MouseEntered += () => { if (!HasFocus() && GetViewport().GuiGetFocusOwner() is not LineEdit) GrabFocus(); };
    }

    public override void _ExitTree()
    {
        App.PlaybackChanged -= QueueRedraw;
        App.ActiveClipChanged -= QueueRedraw;
        App.SessionChanged -= OnSession;
    }

    void OnSession(SessionChangedArgs a)
    {
        if (a.Changes.Any(c => c.Kind is Change.ClipChanged or Change.ClipAdded or Change.ClipRemoved or Change.ProjectReplaced)) QueueRedraw();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged) QueueRedraw();
    }

    Clip? Clip => App.ActiveClip;

    void Layout()
    {
        var last = Math.Max(1, App.FrameCount - 1);
        _axis.Left = Gutter;
        _axis.Fit(0, last, Size.X - Gutter, 16);
    }

    string? SelectedTargetId => _host.Selection.BoneId ?? _host.Selection.ControlId;

    // ---- drawing --------------------------------------------------------------------------------------------------------

    public override void _Draw()
    {
        Layout();
        var p = P;
        var clip = Clip;
        var font = BlazmaTheme.Regular;
        var mono = BlazmaTheme.Mono;
        var w = Size.X;
        var bottom = RulerH + 2 * RowH + MarkerH;
        var x0 = _axis.X(0);
        var last = Math.Max(0, App.FrameCount - 1);
        var x1 = _axis.X(last);

        // row backgrounds
        DrawRect(new Rect2(0, 0, w, bottom), p.Panel2 with { A = 0.45f });
        DrawRect(new Rect2(0, 0, Gutter - 6, bottom), p.Panel);
        DrawRect(new Rect2(Gutter - 6, 0, 1, bottom), p.Line);
        for (var r = 0; r < 3; r++)
            DrawLine(new Vector2(0, RulerH + r * RowH + (r == 2 ? 0 : RowH)), new Vector2(w, RulerH + r * RowH + (r == 2 ? 0 : RowH)), p.Line with { A = 0.6f });
        // clip extent
        DrawRect(new Rect2(x0, RulerH, Math.Max(1, x1 - x0), bottom - RulerH), p.Panel with { A = 0.5f });

        // gutter labels
        Label(font, 10, RulerH + RowH - 7, "All keys", p.Text2);
        var selName = SelectedName();
        Label(font, 10, RulerH + RowH * 2 - 7, selName ?? TranslationServer.Translate("No bone selected"), selName is null ? p.Text3 : p.Warn, Gutter - 22);
        Label(font, 10, RulerH + 2 * RowH + 17, "Markers", p.Text2);

        // ruler ticks
        var step = _axis.TickStep();
        var first = (long)Math.Floor(_axis.FrameAt(Gutter)) - 1;
        for (var f = Math.Max(0, first); f <= last; f++)
        {
            var x = _axis.X(f);
            if (x < Gutter - 2 || x > w) continue;
            var major = f % step == 0;
            if (!major && _axis.Ppf < 6) continue;
            DrawLine(new Vector2(x, RulerH - (major ? 7 : 3)), new Vector2(x, RulerH), p.LineStrong);
            if (major) DrawString(mono, new Vector2(x + 3, 12), TlText.Frame(f), HorizontalAlignment.Left, -1, 10, p.Text3);
        }
        DrawLine(new Vector2(Gutter, RulerH), new Vector2(w, RulerH), p.LineStrong);

        // loop range
        if (clip is { Loop.Enabled: true })
        {
            var (ls, le) = Drag_IsLoop() ? (_dragLoopStart, _dragLoopEnd) : AppController.LoopFrames(clip);
            var lx0 = _axis.X(ls);
            var lx1 = _axis.X(Math.Max(ls, le - 1));
            DrawRect(new Rect2(lx0, 0, Math.Max(2, lx1 - lx0), bottom), p.AccentSoft);
            DrawRect(new Rect2(Gutter, 0, Math.Max(0, lx0 - Gutter), bottom), p.Bg with { A = 0.3f });
            DrawRect(new Rect2(lx1, 0, Math.Max(0, w - lx1), bottom), p.Bg with { A = 0.3f });
            DrawLine(new Vector2(lx0, 0), new Vector2(lx0, bottom), p.Accent with { A = 0.8f }, 1);
            DrawLine(new Vector2(lx1, 0), new Vector2(lx1, bottom), p.Accent with { A = 0.8f }, 1);
            DrawStyleBox(BlazmaTheme.Box(p.Accent, null, 2, 0, 0, 0, 0, 0), new Rect2(lx0 - 1, 0, 7, 11));
            DrawStyleBox(BlazmaTheme.Box(p.Accent, null, 2, 0, 0, 0, 0, 0), new Rect2(lx1 - 6, 0, 7, 11));
        }

        // key rows
        if (clip is not null)
        {
            var all = ClipKeys.Frames(clip, clip.Tracks);
            foreach (var f in all)
                KeyGlyph.Draw(this, new Vector2(_axis.X(f), RulerH + RowH / 2f), 5, Interpolation.Linear,
                    f == App.CurrentFrame ? p.Accent : p.Text2, p.Bg);
            if (SelectedTargetId is { } tid)
            {
                var tracks = ClipKeys.TracksOf(clip, tid).ToList();
                foreach (var f in ClipKeys.Frames(clip, tracks))
                    KeyGlyph.Draw(this, new Vector2(_axis.X(f), RulerH + RowH * 1.5f), 5, ClipKeys.InterpAt(clip, tracks, f),
                        f == App.CurrentFrame ? p.Accent : p.Warn, p.Bg);
            }
        }
        else
        {
            var msg = TranslationServer.Translate("Pose the character, then press Key. A clip is created for you.");
            DrawString(font, new Vector2(x0 + 8, RulerH + RowH - 7), msg, HorizontalAlignment.Left, -1, FontSize, p.Text3);
        }

        DrawMarkers(clip, font, mono, bottom);

        // playhead
        var px = _axis.X(App.CurrentFrameExact);
        if (px >= Gutter - 1 && px <= w)
        {
            DrawLine(new Vector2(px, 0), new Vector2(px, bottom), p.Accent, 1.6f);
            var label = TlText.Frame(App.CurrentFrame);
            var tw = mono.GetStringSize(label, HorizontalAlignment.Left, -1, 10).X;
            DrawStyleBox(BlazmaTheme.Box(p.Accent, null, 3, 0, 0, 0, 0, 0), new Rect2(px - tw / 2 - 4, 1, tw + 8, 15));
            DrawString(mono, new Vector2(px - tw / 2, 12.5f), label, HorizontalAlignment.Left, -1, 10, p.OnAccent);
        }
    }

    bool Drag_IsLoop() => _drag is Drag.LoopStart or Drag.LoopEnd;

    void Label(Font font, float x, float baseline, string text, Color color, float maxW = 100)
    {
        var shown = TlText.Fit(font, TranslationServer.Translate(text), FontSize, maxW);
        DrawString(font, new Vector2(x, baseline), shown, HorizontalAlignment.Left, -1, FontSize, color);
    }

    string? SelectedName()
    {
        var rig = _host.SelectedRig;
        if (rig is null) return null;
        if (_host.Selection.BoneId is { } b) return rig.Bones.Find(x => x.Id.ToString() == b)?.Name;
        if (_host.Selection.ControlId is { } c) return rig.Controls.Find(x => x.Id.ToString() == c)?.Name;
        return null;
    }

    void DrawMarkers(Clip? clip, Font font, Font mono, float bottom)
    {
        _chips.Clear();
        if (clip is null) return;
        var p = P;
        var top = RulerH + 2 * RowH;
        var markers = clip.Markers.OrderBy(m => m.Time.Value).ToList();
        for (var i = 0; i < markers.Count; i++)
        {
            var m = markers[i];
            var f = m.Time.ToFrame(clip.Fps);
            if (_drag == Drag.Marker && m.Id.ToString() == _dragMarker) f = _dragFrame;
            var x = _axis.X(f);
            var nextX = i + 1 < markers.Count ? _axis.X(markers[i + 1].Time.ToFrame(clip.Fps)) : x + 200;
            var phase = m.Phase != KeyPhase.None;
            var col = phase ? p.Accent : p.Text2;
            DrawLine(new Vector2(x, top), new Vector2(x, bottom), col with { A = 0.7f }, 1);
            var maxW = Math.Max(14, nextX - x - 10);
            var text = TlText.Fit(font, TlText.MarkerLabel(m), FontSize, Math.Min(maxW, 150));
            var tw = text.Length == 0 ? 0 : font.GetStringSize(text, HorizontalAlignment.Left, -1, FontSize).X;
            var chipW = tw + 14;
            var rect = new Rect2(x + chipW > Size.X - 2 ? Size.X - 2 - chipW : x, top + 4, chipW, 18);
            var tx = rect.Position.X;
            DrawStyleBox(BlazmaTheme.Box(phase ? p.AccentSoft : p.Raised, col, 4, 1, 0, 0, 0, 0), rect);
            DrawCircle(new Vector2(tx + 6, top + 13), 2.2f, col);
            if (tw > 0) DrawString(font, new Vector2(tx + 12, top + 17), text, HorizontalAlignment.Left, -1, FontSize, p.Text);
            _chips.Add((m.Id.ToString(), rect, f));
        }
    }

    // ---- input ----------------------------------------------------------------------------------------------------------

    long FrameFromX(float x) => Math.Clamp((long)Math.Round(_axis.FrameAt(x)), 0, Math.Max(0, App.FrameCount - 1));

    public override void _GuiInput(InputEvent e)
    {
        Layout();
        switch (e)
        {
            case InputEventKey k:
                if (GetViewport().GuiGetFocusOwner() is LineEdit) return;
                if (TransportKeys.Handle(_host, k)) AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                if (mb.Pressed) OnPress(mb); else OnRelease();
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } rb:
                if (ChipAt(rb.Position) is { } chip)
                {
                    _contextMarker = chip;
                    _context.Position = (Vector2I)rb.GlobalPosition + GetWindow().Position;
                    _context.Popup();
                    AcceptEvent();
                }
                break;
            case InputEventMouseMotion mm:
                OnMotion(mm);
                break;
        }
    }

    string? ChipAt(Vector2 pos)
    {
        foreach (var c in _chips) if (c.rect.Grow(1).HasPoint(pos)) return c.id;
        return null;
    }

    void OnPress(InputEventMouseButton mb)
    {
        GrabFocus();
        var pos = mb.Position;
        var clip = Clip;
        if (ChipAt(pos) is { } chip)
        {
            if (mb.DoubleClick) { _host.PromptRenameMarker(chip); return; }
            _drag = Drag.Marker;
            _dragMarker = chip;
            _dragFrame = _chips.First(c => c.id == chip).frame;
            return;
        }
        if (clip is { Loop.Enabled: true } && pos.Y < 16)
        {
            var (ls, le) = AppController.LoopFrames(clip);
            var xs = _axis.X(ls); var xe = _axis.X(Math.Max(ls, le - 1));
            if (Math.Abs(pos.X - xs) <= 7) { _drag = Drag.LoopStart; _dragLoopStart = ls; _dragLoopEnd = le; return; }
            if (Math.Abs(pos.X - xe) <= 7) { _drag = Drag.LoopEnd; _dragLoopStart = ls; _dragLoopEnd = le; return; }
        }
        if (pos.X < Gutter - 6) return;
        // Click on a key diamond jumps to it; anything else scrubs.
        if (clip is not null && pos.Y > RulerH && pos.Y < RulerH + 2 * RowH)
        {
            var near = ClipKeys.Frames(clip, clip.Tracks).Where(f => Math.Abs(_axis.X(f) - pos.X) <= 7).Select(f => (long?)f).FirstOrDefault();
            if (near is { } nf) { App.GoToFrame(nf); _drag = Drag.Scrub; return; }
        }
        _drag = Drag.Scrub;
        App.GoToFrame(FrameFromX(pos.X));
    }

    void OnMotion(InputEventMouseMotion mm)
    {
        switch (_drag)
        {
            case Drag.Scrub:
                if ((mm.ButtonMask & MouseButtonMask.Left) == 0) { _drag = Drag.None; break; }
                App.GoToFrame(FrameFromX(mm.Position.X));
                break;
            case Drag.Marker:
                _dragFrame = FrameFromX(mm.Position.X);
                QueueRedraw();
                break;
            case Drag.LoopStart:
                _dragLoopStart = Math.Min(FrameFromX(mm.Position.X), _dragLoopEnd - 1);
                QueueRedraw();
                break;
            case Drag.LoopEnd:
                _dragLoopEnd = Math.Max(FrameFromX(mm.Position.X) + 1, _dragLoopStart + 1);
                QueueRedraw();
                break;
        }
        if (_drag == Drag.None)
            MouseDefaultCursorShape = ChipAt(mm.Position) is not null ? CursorShape.Drag : CursorShape.Arrow;
    }

    void OnRelease()
    {
        var d = _drag;
        _drag = Drag.None;
        switch (d)
        {
            case Drag.Marker when _dragMarker is { } id:
                var orig = _chips.FirstOrDefault(c => c.id == id).frame;
                _host.MoveMarker(id, _dragFrame);
                _dragMarker = null;
                _ = orig;
                break;
            case Drag.LoopStart or Drag.LoopEnd:
                App.SetLoop(true, _dragLoopStart, _dragLoopEnd);
                break;
        }
        QueueRedraw();
    }

    // ---- test hooks -----------------------------------------------------------------------------------------------------------

    public Vector2 PointForFrame(long frame, float y)
    {
        Layout();
        return new Vector2(_axis.X(frame), y);
    }

    public (float x, float y) PlayheadPos => (_axis.X(App.CurrentFrameExact), RulerH / 2);
}
