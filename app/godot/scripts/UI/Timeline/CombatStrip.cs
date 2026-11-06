using System.Globalization;
using Blazma.Combat;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 9: a strip under the timeline canvas for the combat data of the active clip, lined up with the ruler like the balance and
/// contact strips. Top lane: the attack phases as coloured bands (startup blue, active red, recovery amber). Middle lane: one thin bar
/// per hitbox and active range. Bottom lane: event markers, a different shape and colour for each kind (hit, footstep, sound, visual
/// effect, custom). The tooltip names what is under the mouse; a click goes to that frame (an event's frame, a range's first frame).
/// Visible while the Combat view toggle is on (Advanced mode) and the clip has phases, hitboxes or events. Always left to right.
/// </summary>
public partial class CombatStrip : Control
{
    public const float PhaseH = 14, BarH = 5, EventH = 16, Pad = 3;
    const int MaxBarLanes = 3;

    readonly MainWindow _host;
    readonly TimelineCanvas _canvas;
    string? _hoverEvent;

    static AppController App => AppController.Instance;
    Palette P => _host.Palette;

    public CombatStrip(MainWindow host, TimelineCanvas canvas)
    {
        _host = host;
        _canvas = canvas;
        LayoutDirection = LayoutDirectionEnum.Ltr;
        CustomMinimumSize = new Vector2(0, PhaseH + BarH * MaxBarLanes + EventH + Pad * 2);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        MouseExited += () => { if (_hoverEvent is not null) { _hoverEvent = null; QueueRedraw(); } };
        App.PlaybackChanged += QueueRedraw;
        App.ActiveClipChanged += Sync;
        App.SettingsChanged += Sync;
        App.SessionChanged += _ => Sync();
    }

    public override void _ExitTree()
    {
        App.PlaybackChanged -= QueueRedraw;
        App.ActiveClipChanged -= Sync;
        App.SettingsChanged -= Sync;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged) QueueRedraw();
    }

    public static bool HasData(Clip? clip) => clip is not null && (clip.Combat?.Phases is not null || clip.Combat?.Hitboxes.Count > 0 || clip.Events.Count > 0);

    public void Sync()
    {
        var show = CombatOverlay.Enabled && HasData(App.ActiveClip);
        if (Visible != show) Visible = show;
        QueueRedraw();
    }

    // ---- what is drawn (also used by tests) ---------------------------------------------------------------------------------------------

    /// <summary>The phase bands as (name, first frame, end frame exclusive).</summary>
    public List<(string Name, long Start, long End)> Bands()
    {
        var list = new List<(string, long, long)>();
        if (App.ActiveClip is not { Combat.Phases: { } ph } c) return list;
        list.Add(("startup", ph.StartupStart.ToFrame(c.Fps), ph.ActiveStart.ToFrame(c.Fps)));
        list.Add(("active", ph.ActiveStart.ToFrame(c.Fps), ph.RecoveryStart.ToFrame(c.Fps)));
        list.Add(("recovery", ph.RecoveryStart.ToFrame(c.Fps), ph.End.ToFrame(c.Fps)));
        return list;
    }

    /// <summary>Hitbox active ranges as (hitbox index, hitbox, first frame, end frame exclusive).</summary>
    public List<(int Lane, Hitbox Box, long Start, long End)> Ranges()
    {
        var list = new List<(int, Hitbox, long, long)>();
        if (App.ActiveClip is not { Combat: { } combat } c) return list;
        for (var i = 0; i < combat.Hitboxes.Count; i++)
            foreach (var (s, e) in CombatEvaluator.ActiveFrames(combat.Hitboxes[i], c.Fps)) list.Add((i, combat.Hitboxes[i], s, e));
        return list;
    }

    public List<(AnimationEvent Event, long Frame)> Events() =>
        App.ActiveClip is { } c ? c.Events.Select(e => (e, e.Time.ToFrame(c.Fps))).ToList() : new();

    public int BandCount => Bands().Count;
    public int RangeCount => Ranges().Count;
    public int EventCount => Events().Count;

    // ---- drawing ----------------------------------------------------------------------------------------------------------------------------

    float PhaseY => Pad;
    float BarsY => Pad + PhaseH;
    float EventY => Pad + PhaseH + BarH * MaxBarLanes;

    public override void _Draw()
    {
        var p = P;
        var w = Size.X;
        var h = Size.Y;
        DrawRect(new Rect2(0, 0, w, h), p.Panel2 with { A = 0.45f });
        DrawRect(new Rect2(0, 0, TimelineCanvas.Gutter - 6, h), p.Panel);
        DrawRect(new Rect2(TimelineCanvas.Gutter - 6, 0, 1, h), p.Line);
        DrawLine(new Vector2(0, h - 0.5f), new Vector2(w, h - 0.5f), p.Line with { A = 0.6f });
        var font = BlazmaTheme.Regular;
        DrawString(font, new Vector2(10, h - 6), TlText.Fit(font, TranslationServer.Translate("Combat"), 11, TimelineCanvas.Gutter - 22), HorizontalAlignment.Left, -1, 11, p.Text2);
        if (App.ActiveClip is not { } clip) return;

        _canvas.PointForFrame(0, 0);
        var axis = _canvas.Axis;
        var half = (float)axis.Ppf / 2f;
        float X0(long frame) => Math.Max(axis.X(frame) - half, TimelineCanvas.Gutter);
        float X1(long frame) => Math.Min(axis.X(frame) - half, w);

        foreach (var (name, start, end) in Bands())
        {
            var x0 = X0(start); var x1 = X1(end);
            if (x1 <= x0) continue;
            var col = CombatKit.PhaseColor(p, name);
            DrawRect(new Rect2(x0, PhaseY, x1 - x0, PhaseH), col with { A = 0.78f });
            var label = TlText.Fit(BlazmaTheme.Regular, CombatKit.PhaseLabel(name), 10, x1 - x0 - 8);
            if (label.Length > 0) DrawString(BlazmaTheme.Regular, new Vector2(x0 + 4, PhaseY + PhaseH - 3.5f), label, HorizontalAlignment.Left, -1, 10, p.OnAccent);
        }

        foreach (var (lane, box, start, end) in Ranges())
        {
            var x0 = X0(start); var x1 = X1(end);
            if (x1 <= x0) continue;
            var y = BarsY + 1 + Math.Min(lane, MaxBarLanes - 1) * BarH;
            DrawRect(new Rect2(x0, y, x1 - x0, BarH - 1.5f), CombatKit.HitboxColor(p) with { A = 0.9f });
        }

        foreach (var (ev, frame) in Events())
        {
            var x = axis.X(frame);
            if (x < TimelineCanvas.Gutter - 2 || x > w) continue;
            DrawEventGlyph(new Vector2(x, EventY + EventH / 2f), ev.Kind, ev.Id.ToString() == _hoverEvent);
        }

        var px = axis.X(App.CurrentFrameExact);
        if (px >= TimelineCanvas.Gutter - 1 && px <= w) DrawLine(new Vector2(px, 0), new Vector2(px, h), p.Accent, 1.6f);
    }

    void DrawEventGlyph(Vector2 c, EventKind kind, bool hover)
    {
        var p = P;
        var col = CombatKit.KindColor(p, kind);
        var r = hover ? 6.2f : 5f;
        switch (kind)
        {
            case EventKind.Hit: // diamond
                DrawColoredPolygon(new[] { c + new Vector2(0, -r), c + new Vector2(r, 0), c + new Vector2(0, r), c + new Vector2(-r, 0) }, col);
                break;
            case EventKind.Footstep: // triangle
                DrawColoredPolygon(new[] { c + new Vector2(0, -r), c + new Vector2(r, r * 0.8f), c + new Vector2(-r, r * 0.8f) }, col);
                break;
            case EventKind.Sound: // square
                DrawRect(new Rect2(c - new Vector2(r * 0.8f, r * 0.8f), new Vector2(r * 1.6f, r * 1.6f)), col);
                break;
            case EventKind.Vfx: // plus
                DrawLine(c - new Vector2(r, 0), c + new Vector2(r, 0), col, 2.6f);
                DrawLine(c - new Vector2(0, r), c + new Vector2(0, r), col, 2.6f);
                break;
            default: // circle
                DrawCircle(c, r * 0.85f, col);
                break;
        }
        if (hover) DrawArc(c, r + 3, 0, Mathf.Tau, 20, p.Text, 1.2f, true);
    }

    // ---- hover and click ------------------------------------------------------------------------------------------------------------

    (AnimationEvent Event, long Frame)? EventAt(Vector2 pos)
    {
        if (App.ActiveClip is null || pos.Y < EventY - 2) return null;
        _canvas.PointForFrame(0, 0);
        var axis = _canvas.Axis;
        (AnimationEvent, long)? best = null;
        var bestD = 8f;
        foreach (var (ev, frame) in Events())
        {
            var d = Mathf.Abs(axis.X(frame) - pos.X);
            if (d <= bestD) { bestD = d; best = (ev, frame); }
        }
        return best;
    }

    long FrameAtX(float x)
    {
        _canvas.PointForFrame(0, 0);
        return (long)Math.Round(_canvas.Axis.FrameAt(x));
    }

    public override string _GetTooltip(Vector2 atPosition)
    {
        if (App.ActiveClip is not { } clip) return "";
        if (EventAt(atPosition) is { } e)
        {
            var text = string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("{0} ({1}) at frame {2}"),
                e.Event.Name.Length > 0 ? e.Event.Name : "—", CombatKit.KindLabel(e.Event.Kind), CombatKit.Ltr(e.Frame));
            if (e.Event.Data.Count > 0) text += "\n" + string.Join(", ", e.Event.Data.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key} = {kv.Value}"));
            return text;
        }
        var f = FrameAtX(atPosition.X);
        if (atPosition.Y >= BarsY && atPosition.Y < EventY)
        {
            var lane = (int)((atPosition.Y - BarsY) / BarH);
            var hit = Ranges().FirstOrDefault(r => r.Lane == lane && f >= r.Start && f < r.End);
            if (hit.Box is not null)
                return string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("{0}: hits on frames {1}–{2}"), hit.Box.Name, CombatKit.Ltr(hit.Start), CombatKit.Ltr(hit.End - 1));
        }
        if (atPosition.Y < BarsY)
            foreach (var (name, start, end) in Bands())
                if (f >= start && f < end)
                    return string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("{0}: frames {1}–{2}"), CombatKit.PhaseLabel(name), CombatKit.Ltr(start), CombatKit.Ltr(end - 1));
        _ = clip;
        return "";
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion mm:
                var id = EventAt(mm.Position)?.Event.Id.ToString();
                if (id != _hoverEvent) { _hoverEvent = id; QueueRedraw(); }
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb:
                if (EventAt(mb.Position) is { } ev)
                {
                    if (_host.CombatPanelNode.Visible) { _host.CombatPanelNode.SelectPage(CombatPanel.PageEvents); _host.CombatPanelNode.SelectEventById(ev.Event.Id.ToString()); }
                    else App.GoToFrame(ev.Frame);
                }
                else App.GoToFrame(FrameAtX(mb.Position.X));
                AcceptEvent();
                break;
        }
    }

    // ---- test hooks -------------------------------------------------------------------------------------------------------------------------

    /// <summary>Global position of an event marker.</summary>
    public Vector2 EventCenter(string eventId)
    {
        _canvas.PointForFrame(0, 0);
        var frame = Events().First(x => x.Event.Id.ToString() == eventId).Frame;
        return GlobalPosition + new Vector2(_canvas.Axis.X(frame), EventY + EventH / 2f);
    }

    public Vector2 BandCenter(string name)
    {
        _canvas.PointForFrame(0, 0);
        var b = Bands().First(x => x.Name == name);
        return GlobalPosition + new Vector2((_canvas.Axis.X(b.Start) + _canvas.Axis.X(b.End)) / 2f - (float)_canvas.Axis.Ppf / 2f, PhaseY + PhaseH / 2f);
    }
}
