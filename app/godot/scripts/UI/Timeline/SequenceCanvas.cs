using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Motion;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// The track of the sequence editor: a ruler in sequence frames and the sequence's items as bars on two alternating lanes, so items that
/// overlap (blend) can be seen. A bar starts where the item starts and ends where it ends (clip range, speed and loops included); the
/// triangle at its start is the blend (fade-in) over the items before it. Drag the bar to change its start, the round handle on its top
/// edge to change the blend length, its right edge to change its speed. Every drag is ONE transaction of sequence.* commands, so it is one
/// undo step; Esc or the right mouse button cancels. Click the ruler to scrub: the viewport shows the blended pose. Always left to right.
/// </summary>
public partial class SequenceCanvas : Control
{
    public const float Gutter = 96, RulerH = 24, LaneH = 34, LaneGap = 6;
    const int FontSize = 11;

    public enum Zone { Body, Blend, Edge }
    public sealed record Bar(string ItemId, int Index, Rect2 Rect, double Start, double End, long BlendFrames, string Label);

    readonly MainWindow _host;
    readonly FrameAxis _axis = new();
    readonly List<Bar> _bars = new();
    TransactionScope? _scope;
    enum Drag { None, Scrub, Move, Blend, Speed }
    Drag _drag;
    string? _dragItem;
    double _dragFrame0, _dragStart0, _dragSpeed0, _dragDuration0;
    long _dragBlend0;
    string? _hoverItem;

    static AppController App => AppController.Instance;
    Palette P => _host.Palette;

    public FrameAxis Axis => _axis;
    public IReadOnlyList<Bar> Bars => _bars;
    public string? SelectedItemId { get; set; }
    public bool Dragging => _scope is not null;

    /// <summary>Raised when the user picks an item (or nothing).</summary>
    public event Action? SelectionChanged;

    public SequenceCanvas(MainWindow host)
    {
        _host = host;
        LayoutDirection = LayoutDirectionEnum.Ltr;
        FocusMode = FocusModeEnum.Click;
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(300, RulerH + 2 * LaneH + LaneGap + 14);
        SizeFlagsVertical = SizeFlags.ExpandFill;
        App.SequenceFrameChanged += QueueRedraw;
        App.SequenceStateChanged += QueueRedraw;
        App.SessionChanged += a => { if (a.Changes.Any(c => c.Kind is Change.SequenceChanged or Change.ClipChanged or Change.ProjectReplaced)) QueueRedraw(); };
    }

    public override void _ExitTree()
    {
        App.SequenceFrameChanged -= QueueRedraw;
        App.SequenceStateChanged -= QueueRedraw;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged) QueueRedraw();
    }

    void Layout(SequenceEvaluator? ev)
    {
        var last = Math.Max(30, ev?.LastFrame ?? 30);
        _axis.Left = Gutter;
        _axis.Fit(0, last, Math.Max(40, Size.X - Gutter), 14);
    }

    static Color ItemColor(Palette p, int index) => (index % 5) switch { 0 => p.Accent, 1 => p.Z, 2 => p.Ok, 3 => p.Warn, _ => p.X };

    float LaneY(int index) => RulerH + 6 + (index % 2) * (LaneH + LaneGap);

    /// <summary>Rebuild the bars from the active sequence and its evaluator (called by Draw and by the hit tests).</summary>
    void BuildBars()
    {
        _bars.Clear();
        var seq = App.ActiveSequence;
        var ev = App.SequenceEval();
        Layout(ev);
        if (seq is null || ev is null) return;
        var project = App.Session.Project;
        for (var i = 0; i < seq.Items.Count; i++)
        {
            var it = seq.Items[i];
            if (ev.RangeOf(it.Id) is not { } r) continue;
            var x0 = _axis.X(r.Start);
            var x1 = Math.Max(x0 + 6, _axis.X(r.End));
            var name = project.FindClip(it.Clip)?.Name ?? "?";
            _bars.Add(new Bar(it.Id.ToString(), i, new Rect2(x0, LaneY(i), x1 - x0, LaneH), r.Start, r.End, it.BlendInFrames, name));
        }
    }

    // ---- drawing ---------------------------------------------------------------------------------------------------------------

    public override void _Draw()
    {
        var p = P;
        var seq = App.ActiveSequence;
        var ev = App.SequenceEval();
        BuildBars();
        var w = Size.X; var h = Size.Y;
        var font = BlazmaTheme.Regular;
        var mono = BlazmaTheme.Mono;
        DrawRect(new Rect2(0, 0, w, h), p.Panel2 with { A = 0.5f });
        DrawRect(new Rect2(0, 0, Gutter - 6, h), p.Panel);
        DrawRect(new Rect2(Gutter - 6, 0, 1, h), p.Line);
        DrawRect(new Rect2(Gutter - 5, 0, w - Gutter + 5, RulerH), p.Panel with { A = 0.9f });
        DrawLine(new Vector2(0, RulerH - 0.5f), new Vector2(w, RulerH - 0.5f), p.Line);

        // ruler
        var step = _axis.TickStep(40);
        var first = (long)Math.Floor(_axis.FrameAt(Gutter) / step) * step;
        for (var f = Math.Max(0, first); _axis.X(f) < w; f += step)
        {
            var x = _axis.X(f);
            if (x < Gutter) continue;
            DrawLine(new Vector2(x, RulerH - 7), new Vector2(x, RulerH), p.LineStrong);
            DrawString(mono, new Vector2(x + 3, RulerH - 9), f.ToString(CultureInfo.InvariantCulture), HorizontalAlignment.Left, -1, FontSize, p.Text3);
        }
        DrawString(font, new Vector2(10, RulerH - 8), TlText.Fit(font, TranslationServer.Translate("Sequence"), FontSize, Gutter - 22), HorizontalAlignment.Left, -1, FontSize, p.Text2);

        // lanes
        for (var lane = 0; lane < 2; lane++)
        {
            var y = LaneY(lane);
            DrawRect(new Rect2(Gutter - 5, y - 2, w - Gutter + 5, LaneH + 4), p.Bg with { A = 0.35f });
            var label = TranslationServer.Translate(lane == 0 ? "Track A" : "Track B");
            DrawString(font, new Vector2(10, y + LaneH / 2 + 4), TlText.Fit(font, label, FontSize, Gutter - 22), HorizontalAlignment.Left, -1, FontSize, p.Text3);
        }

        if (seq is null)
        {
            DrawCentered(font, TranslationServer.Translate("No sequence yet. Press New to make one, then add clips."), w, h, p.Text3);
            return;
        }
        if (ev is null)
        {
            var key = App.SequenceError == "empty_sequence" ? "Add a clip to start this sequence."
                : App.SequenceError == "too_large" ? "This sequence is too big to play: it has too many clips or is too long."
                : "This sequence cannot be played: a clip or the character it uses is missing or does not fit.";
            DrawCentered(font, TranslationServer.Translate(key), w, h, p.Text3);
            return;
        }

        // blend overlaps first (under the bars)
        foreach (var b in _bars)
        {
            var col = ItemColor(p, b.Index);
            var selected = b.ItemId == SelectedItemId;
            var r = b.Rect;
            DrawRect(r, col with { A = selected ? 0.45f : 0.30f });
            // fade-in ramp
            if (b.BlendFrames > 0)
            {
                var bx = Math.Min(_axis.X(b.Start + b.BlendFrames), r.End.X);
                var ramp = new[] { new Vector2(r.Position.X, r.End.Y), new Vector2(bx, r.Position.Y), new Vector2(bx, r.End.Y) };
                DrawColoredPolygon(ramp, col with { A = 0.55f });
                DrawLine(ramp[0], ramp[1], col.Lightened(0.2f), 1.6f, true);
            }
            DrawRect(r, selected ? p.Text : col, false, selected ? 2.2f : 1.3f);
            // handles
            var hx = Math.Min(_axis.X(b.Start + b.BlendFrames), r.End.X);
            DrawCircle(new Vector2(hx, r.Position.Y + 1), selected || _hoverItem == b.ItemId ? 5.5f : 4f, p.Bg);
            DrawCircle(new Vector2(hx, r.Position.Y + 1), selected || _hoverItem == b.ItemId ? 4f : 2.8f, col.Lightened(0.25f));
            DrawRect(new Rect2(r.End.X - 3, r.Position.Y + 6, 3, r.Size.Y - 12), col.Lightened(0.15f));
            var item = seq.Items.ElementAtOrDefault(b.Index);
            var text = b.Label + (item is { Loops: > 1 } ? $"  ×{item.Loops}" : "") + (item is not null && Math.Abs(item.Speed - 1) > 1e-6 ? $"  {P8.Num(item.Speed, "0.##")}×" : "");
            var tx = Math.Max(r.Position.X, Gutter) + 8;
            DrawString(font, new Vector2(tx, r.Position.Y + LaneH / 2 + 5), TlText.Fit(font, text, 12, Math.Max(0, r.End.X - tx - 8)), HorizontalAlignment.Left, -1, 12, p.Text);
        }

        // playhead
        var px = _axis.X(App.SequenceFrame);
        if (px >= Gutter - 1 && px <= w)
        {
            DrawLine(new Vector2(px, 0), new Vector2(px, h), p.Accent, 1.8f);
            DrawColoredPolygon(new[] { new Vector2(px - 6, 0), new Vector2(px + 6, 0), new Vector2(px, 9) }, p.Accent);
        }
    }

    void DrawCentered(Font font, string text, float w, float h, Color col)
    {
        var size = font.GetStringSize(text, HorizontalAlignment.Left, -1, 13);
        DrawString(font, new Vector2(Gutter + (w - Gutter - size.X) / 2, RulerH + (h - RulerH) / 2 + 5), text, HorizontalAlignment.Left, -1, 13, col);
    }

    // ---- hit testing ----------------------------------------------------------------------------------------------------------

    public (Bar bar, Zone zone)? HitTest(Vector2 pos)
    {
        BuildBars();
        // Later bars are drawn on top: test them first.
        for (var i = _bars.Count - 1; i >= 0; i--)
        {
            var b = _bars[i];
            var hx = Math.Min(_axis.X(b.Start + b.BlendFrames), b.Rect.End.X);
            if (new Vector2(hx, b.Rect.Position.Y + 1).DistanceTo(pos) <= 8) return (b, Zone.Blend);
            if (!b.Rect.Grow(1).HasPoint(pos)) continue;
            if (pos.X >= b.Rect.End.X - 6) return (b, Zone.Edge);
            return (b, Zone.Body);
        }
        return null;
    }

    public override string _GetTooltip(Vector2 at)
    {
        if (HitTest(at) is not { } hit || App.ActiveSequence is not { } seq) return "";
        return TooltipFor(seq, hit.bar, hit.zone);
    }

    string TooltipFor(ClipSequence seq, Bar b, Zone zone)
    {
        var it = seq.Items.Find(i => i.Id.ToString() == b.ItemId);
        if (it is null) return "";
        var info = string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("{0}: starts at frame {1}, fade-in {2} frames, speed {3}, repeats {4}"),
            b.Label, P8.Ltr(it.StartFrame.ToString(CultureInfo.InvariantCulture)), P8.Ltr(it.BlendInFrames.ToString(CultureInfo.InvariantCulture)),
            P8.Ltr(P8.Num(it.Speed, "0.##")), P8.Ltr(it.Loops.ToString(CultureInfo.InvariantCulture)));
        var hint = zone switch
        {
            Zone.Blend => "Drag to change the fade-in length.",
            Zone.Edge => "Drag to change the speed.",
            _ => "Drag to move this clip in time.",
        };
        return info + "\n" + TranslationServer.Translate(hint);
    }

    // ---- input -----------------------------------------------------------------------------------------------------------------

    double FrameAtX(float x) => _axis.FrameAt(x);

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey { Pressed: true, Keycode: Key.Escape } when Dragging:
                Cancel();
                AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } when Dragging:
                Cancel();
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                if (mb.Pressed) { GrabFocus(); Press(mb.Position); } else Release();
                AcceptEvent();
                break;
            case InputEventMouseMotion mm:
                if (_drag != Drag.None)
                {
                    if ((mm.ButtonMask & MouseButtonMask.Left) == 0) { Release(); break; }
                    Motion(mm.Position);
                }
                else
                {
                    var hover = HitTest(mm.Position);
                    var id = hover?.bar.ItemId;
                    if (id != _hoverItem) { _hoverItem = id; QueueRedraw(); }
                    MouseDefaultCursorShape = hover is null ? CursorShape.Arrow : hover.Value.zone switch
                    {
                        Zone.Edge => CursorShape.Hsize, Zone.Blend => CursorShape.Hsize, _ => CursorShape.Drag,
                    };
                }
                break;
        }
    }

    void Press(Vector2 pos)
    {
        var seq = App.ActiveSequence;
        if (seq is null) return;
        if (pos.Y < RulerH)
        {
            _drag = Drag.Scrub;
            App.SetSequenceFrame(FrameAtX(pos.X));
            return;
        }
        if (HitTest(pos) is not { } hit)
        {
            Select(null);
            return;
        }
        var (bar, zone) = hit;
        Select(bar.ItemId);
        var item = seq.Items.Find(i => i.Id.ToString() == bar.ItemId);
        if (item is null) return;
        _dragItem = bar.ItemId;
        _dragFrame0 = FrameAtX(pos.X);
        _dragStart0 = item.StartFrame;
        _dragSpeed0 = item.Speed;
        _dragBlend0 = item.BlendInFrames;
        _dragDuration0 = Math.Max(1, bar.End - bar.Start);
        _drag = zone switch { Zone.Blend => Drag.Blend, Zone.Edge => Drag.Speed, _ => Drag.Move };
        _scope = App.BeginTransaction(zone switch { Zone.Blend => "Change fade-in", Zone.Edge => "Change speed", _ => "Move sequence item" });
    }

    void Motion(Vector2 pos)
    {
        if (_drag == Drag.Scrub) { App.SetSequenceFrame(FrameAtX(pos.X)); return; }
        if (_scope is null || _dragItem is null || App.ActiveSequence is not { } seq) return;
        var delta = FrameAtX(pos.X) - _dragFrame0;
        JsonObject args;
        string cmd;
        switch (_drag)
        {
            case Drag.Move:
                cmd = "sequence.update_item";
                args = new JsonObject { ["startFrame"] = (long)Math.Max(0, Math.Round(_dragStart0 + delta)) };
                break;
            case Drag.Blend:
                cmd = "sequence.set_blend";
                args = new JsonObject { ["blendFrames"] = (long)Math.Max(0, Math.Round(_dragBlend0 + delta)) };
                break;
            default:
                cmd = "sequence.update_item";
                args = new JsonObject { ["speed"] = Math.Round(Math.Clamp(_dragSpeed0 * _dragDuration0 / Math.Max(1, _dragDuration0 + delta), 0.1, 10), 3) };
                break;
        }
        args["sequenceId"] = seq.Id.ToString();
        args["itemId"] = _dragItem;
        var r = App.Execute(cmd, args);
        if (!r.Ok) Cancel();
        QueueRedraw();
    }

    void Release()
    {
        var d = _drag;
        _drag = Drag.None;
        var scope = _scope;
        _scope = null;
        if (scope is not null)
        {
            scope.Commit();
            _host.AfterTransaction();
        }
        _dragItem = null;
        _ = d;
        QueueRedraw();
    }

    void Cancel()
    {
        _drag = Drag.None;
        var scope = _scope;
        _scope = null;
        scope?.Rollback();
        _dragItem = null;
        _host.AfterTransaction();
        QueueRedraw();
    }

    void Select(string? id)
    {
        if (SelectedItemId == id) return;
        SelectedItemId = id;
        SelectionChanged?.Invoke();
        QueueRedraw();
    }

    public void SelectItem(string? id) => Select(id);

    // ---- test hooks ------------------------------------------------------------------------------------------------------------

    /// <summary>Global position of a point on a bar: <paramref name="along"/> 0..1 of its length, in the lane's middle.</summary>
    public Vector2 BarPoint(string itemId, double along = 0.5)
    {
        BuildBars();
        var b = _bars.First(x => x.ItemId == itemId);
        return GlobalPosition + new Vector2(b.Rect.Position.X + (float)(b.Rect.Size.X * along), b.Rect.Position.Y + b.Rect.Size.Y / 2);
    }

    public Vector2 BlendHandle(string itemId)
    {
        BuildBars();
        var b = _bars.First(x => x.ItemId == itemId);
        return GlobalPosition + new Vector2(Math.Min(_axis.X(b.Start + b.BlendFrames), b.Rect.End.X), b.Rect.Position.Y + 1);
    }

    public Vector2 EdgePoint(string itemId)
    {
        BuildBars();
        var b = _bars.First(x => x.ItemId == itemId);
        return GlobalPosition + new Vector2(b.Rect.End.X - 2, b.Rect.Position.Y + b.Rect.Size.Y / 2);
    }

    public Vector2 RulerPoint(double frame)
    {
        BuildBars();
        return GlobalPosition + new Vector2(_axis.X(frame), RulerH / 2);
    }

    public double PixelsPerFrame { get { BuildBars(); return _axis.Ppf; } }
}
