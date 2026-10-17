using System.Globalization;
using Blazma.Physics;
using Blazma.Rigging;
using Blazma.UI.App;
using Godot;
using Side = Blazma.Rigging.Side;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 8: a thin strip under the timeline canvas that shows the foot contacts of the active clip (clip.contacts rules): the left foot
/// in the upper half, the right foot in the lower half, one bar per contact span. It lines up with the canvas ruler, like the balance
/// strip. The tooltip names the foot and the frames of the span; a click goes to the first frame of the span (or the frame under the
/// mouse). Visible while the contacts toggle is on and the clip can be analysed. Always left to right.
/// </summary>
public partial class ContactStrip : Control
{
    public const float RowH = 16;

    readonly MainWindow _host;
    readonly TimelineCanvas _canvas;
    FootContactSpan? _hover;

    static AppController App => AppController.Instance;
    Palette P => _host.Palette;

    public ContactStrip(MainWindow host, TimelineCanvas canvas)
    {
        _host = host;
        _canvas = canvas;
        LayoutDirection = LayoutDirectionEnum.Ltr;
        CustomMinimumSize = new Vector2(0, RowH);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        MouseExited += () => { if (_hover is not null) { _hover = null; QueueRedraw(); } };
        App.PlaybackChanged += QueueRedraw;
        App.ActiveClipChanged += Sync;
        App.SettingsChanged += Sync;
        App.ContactsChanged += Sync;
        App.SessionChanged += _ => Sync();
    }

    public override void _ExitTree()
    {
        App.PlaybackChanged -= QueueRedraw;
        App.ActiveClipChanged -= Sync;
        App.SettingsChanged -= Sync;
        App.ContactsChanged -= Sync;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged) QueueRedraw();
    }

    /// <summary>Show or hide the row. Detection happens only when something draws (and is cached).</summary>
    public void Sync()
    {
        var show = App.Settings.ShowContacts && App.Contacts() is not null;
        if (Visible != show) Visible = show;
        QueueRedraw();
    }

    public static Color SideColor(Palette p, Side side) => side == Side.Left ? p.Z : p.Accent;

    public static string SideName(Side side) => TranslationServer.Translate(side == Side.Left ? "Left foot" : "Right foot");

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
        DrawString(font, new Vector2(10, h - 4), TlText.Fit(font, TranslationServer.Translate("Foot contacts"), 11, TimelineCanvas.Gutter - 22),
            HorizontalAlignment.Left, -1, 11, p.Text2);

        if (App.Contacts() is not { } info) return;
        _canvas.PointForFrame(0, 0);
        var axis = _canvas.Axis;
        var half = (float)axis.Ppf / 2f;
        var laneH = (h - 5) / 2f;
        foreach (var s in info.Spans)
        {
            var x0 = Math.Max(axis.X(s.Start) - half, TimelineCanvas.Gutter);
            var x1 = Math.Min(axis.X(s.End) + half, w);
            if (x1 <= x0) continue;
            var y = 2 + (s.Side == Side.Left ? 0 : laneH + 1);
            var col = SideColor(p, s.Side);
            var hovered = _hover is { } hv && hv == s;
            DrawRect(new Rect2(x0, y, x1 - x0, laneH), col with { A = hovered ? 1f : 0.78f });
        }
        var px = axis.X(App.CurrentFrameExact);
        if (px >= TimelineCanvas.Gutter - 1 && px <= w) DrawLine(new Vector2(px, 0), new Vector2(px, h), p.Accent, 1.6f);
    }

    // ---- hover and click ---------------------------------------------------------------------------------------------------

    FootContactSpan? SpanAt(Vector2 pos)
    {
        if (App.Contacts() is not { } info) return null;
        _canvas.PointForFrame(0, 0);
        var f = (long)Math.Round(_canvas.Axis.FrameAt(pos.X));
        var side = pos.Y < Size.Y / 2 ? Side.Left : Side.Right;
        return info.Spans.FirstOrDefault(s => s.Side == side && f >= s.Start && f <= s.End);
    }

    public override string _GetTooltip(Vector2 atPosition) => SpanAt(atPosition) is { } s ? TooltipFor(s) : "";

    /// <summary>"Left foot: frames 4–12", numbers isolated so they stay left to right in Arabic.</summary>
    public static string TooltipFor(FootContactSpan s) =>
        string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("{0}: frames {1}–{2}"), SideName(s.Side),
            BalanceStrip.Ltr(s.Start.ToString(CultureInfo.InvariantCulture)), BalanceStrip.Ltr(s.End.ToString(CultureInfo.InvariantCulture)));

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion mm:
                var s = SpanAt(mm.Position);
                if (s != _hover) { _hover = s; QueueRedraw(); }
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb:
                if (SpanAt(mb.Position) is { } span) App.GoToFrame(span.Start);
                else { _canvas.PointForFrame(0, 0); App.GoToFrame((long)Math.Round(_canvas.Axis.FrameAt(mb.Position.X))); }
                AcceptEvent();
                break;
        }
    }

    // ---- test hooks ---------------------------------------------------------------------------------------------------------

    /// <summary>Global position of the middle of a span (side decides the lane).</summary>
    public Vector2 SpanCenter(FootContactSpan s)
    {
        _canvas.PointForFrame(0, 0);
        var x = (_canvas.Axis.X(s.Start) + _canvas.Axis.X(s.End)) / 2f;
        return GlobalPosition + new Vector2(x, s.Side == Side.Left ? Size.Y * 0.25f : Size.Y * 0.75f);
    }
}
