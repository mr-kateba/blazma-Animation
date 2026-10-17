using System.Globalization;
using Blazma.Physics;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 6: a thin row under the timeline canvas that colours every frame of the active clip by balance state and margin
/// (green = balanced, brighter with more margin; amber = balanced with less than 1 cm to spare; red = unbalanced, stronger with
/// the depth; grey = airborne). Lines up with the canvas ruler. The tooltip names the frame, its margin and state; a click on an
/// unbalanced frame jumps to the worst frame of that unbalanced range, a click elsewhere goes to the frame.
/// Visible only while the Physics toggle is on and the active clip can be analysed. Always left to right.
/// </summary>
public partial class BalanceStrip : Control
{
    public const float RowH = 16;
    /// <summary>Margins (m) at which the colours reach full strength.</summary>
    const double GoodMargin = 0.08, BadDepth = 0.10, Marginal = 0.01;

    readonly MainWindow _host;
    readonly TimelineCanvas _canvas;
    long _hover = -1;

    static AppController App => AppController.Instance;
    Palette P => _host.Palette;

    public BalanceStrip(MainWindow host, TimelineCanvas canvas)
    {
        _host = host;
        _canvas = canvas;
        LayoutDirection = LayoutDirectionEnum.Ltr;
        CustomMinimumSize = new Vector2(0, RowH);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
        MouseExited += () => { if (_hover >= 0) { _hover = -1; QueueRedraw(); } };
        App.PlaybackChanged += QueueRedraw;
        App.ActiveClipChanged += Sync;
        App.SettingsChanged += Sync;
        App.SessionChanged += _ => Sync();
        App.Physics.Invalidated += Sync;
    }

    public override void _ExitTree()
    {
        App.PlaybackChanged -= QueueRedraw;
        App.ActiveClipChanged -= Sync;
        App.SettingsChanged -= Sync;
        App.Physics.Invalidated -= Sync;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged) QueueRedraw();
    }

    /// <summary>Show or hide the row (cheap: no analysis happens here, only when it draws).</summary>
    public void Sync()
    {
        var show = App.Settings.ShowPhysics && App.Physics.TryResolve(out _, out _, out _) && App.Physics.HasFeet;
        if (Visible != show) Visible = show;
        QueueRedraw();
    }

    // ---- drawing ----------------------------------------------------------------------------------------------------

    /// <summary>Colour of a frame's cell (also used by the tests).</summary>
    public static Color CellColor(Palette p, PoseBalance b)
    {
        switch (b.State)
        {
            case BalanceState.Airborne:
                return p.Text3 with { A = 0.55f };
            case BalanceState.Unbalanced:
            {
                var depth = Math.Clamp(-b.Margin / BadDepth, 0, 1);
                return p.Err with { A = (float)(0.55 + 0.45 * depth) };
            }
            default:
                if (b.Margin < Marginal) return p.Warn with { A = 0.85f };
                var t = Math.Clamp((b.Margin - Marginal) / (GoodMargin - Marginal), 0, 1);
                return p.Ok with { A = (float)(0.38 + 0.52 * t) };
        }
    }

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
        var label = TlText.Fit(font, TranslationServer.Translate("Balance"), 11, TimelineCanvas.Gutter - 22);
        DrawString(font, new Vector2(10, h - 4), label, HorizontalAlignment.Left, -1, 11, p.Text2);

        var report = App.Physics.Report;
        if (report is null) return;
        _canvas.PointForFrame(0, 0); // fits the canvas axis to its current width
        var axis = _canvas.Axis;
        var half = (float)axis.Ppf / 2f;
        var gap = axis.Ppf >= 4 ? 1f : 0f;
        foreach (var b in report.Frames)
        {
            var x = axis.X(b.Frame);
            var x0 = Math.Max(x - half + gap / 2, TimelineCanvas.Gutter);
            var x1 = Math.Min(x + half - gap / 2, w);
            if (x1 <= x0) continue;
            DrawRect(new Rect2(x0, 3, x1 - x0, h - 6), CellColor(p, b));
        }
        // the playhead, so the row reads together with the ruler
        var px = axis.X(App.CurrentFrameExact);
        if (px >= TimelineCanvas.Gutter - 1 && px <= w) DrawLine(new Vector2(px, 0), new Vector2(px, h), p.Accent, 1.6f);
        if (_hover >= 0 && App.Physics.FrameAt(_hover) is not null)
        {
            var hx = axis.X(_hover);
            DrawRect(new Rect2(hx - half, 2, Math.Max(2, 2 * half), h - 4), p.Text with { A = 0.9f }, false, 1.2f);
        }
    }

    // ---- hover and click --------------------------------------------------------------------------------------------

    long FrameFromX(float x)
    {
        _canvas.PointForFrame(0, 0);
        var f = (long)Math.Round(_canvas.Axis.FrameAt(x));
        var r = App.Physics.Report;
        return r is null ? -1 : f < r.StartFrame || f > r.EndFrame ? -1 : f;
    }

    public override string _GetTooltip(Vector2 atPosition) => TooltipFor(FrameFromX(atPosition.X));

    /// <summary>"Frame 12: Unbalanced (margin -4.2 cm)", numbers isolated so they stay left to right in Arabic.</summary>
    public string TooltipFor(long frame)
    {
        if (frame < 0 || App.Physics.FrameAt(frame) is not { } b) return "";
        var state = StateText(b.State);
        if (PhysicsAnalysis.MarginCm(b) is not { } cm)
            return string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("Frame {0}: {1}"), Ltr(frame.ToString(CultureInfo.InvariantCulture)), state);
        return string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("Frame {0}: {1} (margin {2} cm)"),
            Ltr(frame.ToString(CultureInfo.InvariantCulture)), state, Ltr(Signed(cm)));
    }

    public static string StateText(BalanceState s) => TranslationServer.Translate(s switch
    {
        BalanceState.Balanced => "Balanced",
        BalanceState.Unbalanced => "Unbalanced",
        _ => "Airborne",
    });

    public static string Signed(double cm) => (cm < 0 ? "−" : cm > 0 ? "+" : "") + Math.Abs(cm).ToString("0.0", CultureInfo.InvariantCulture);
    public static string Ltr(string s) => "⁦" + s + "⁩";

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion mm:
                var f = FrameFromX(mm.Position.X);
                if (f != _hover) { _hover = f; QueueRedraw(); }
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb:
                ClickFrame(FrameFromX(mb.Position.X));
                AcceptEvent();
                break;
        }
    }

    /// <summary>Click on a frame: an unbalanced frame goes to the worst frame of its range, any other frame is shown as it is.</summary>
    public void ClickFrame(long frame)
    {
        if (frame < 0) return;
        var r = App.Physics.Report;
        if (r?.Frames[(int)(frame - r.StartFrame)] is { State: BalanceState.Unbalanced })
        {
            var range = r.UnbalancedRanges.FirstOrDefault(x => frame >= x.Start && frame <= x.End);
            if (range is not null) { App.GoToFrame(range.WorstFrame); return; }
        }
        App.GoToFrame(frame);
    }

    // ---- test hooks ---------------------------------------------------------------------------------------------------

    /// <summary>Global position of a frame's cell centre.</summary>
    public Vector2 CellCenter(long frame)
    {
        _canvas.PointForFrame(0, 0);
        return GlobalPosition + new Vector2(_canvas.Axis.X(frame), Size.Y / 2);
    }

    /// <summary>Frames per colour class as drawn (balanced, marginal, unbalanced, airborne).</summary>
    public (int balanced, int marginal, int unbalanced, int airborne) CellCounts()
    {
        int g = 0, m = 0, u = 0, a = 0;
        if (App.Physics.Report is { } r)
            foreach (var b in r.Frames)
                switch (b.State)
                {
                    case BalanceState.Unbalanced: u++; break;
                    case BalanceState.Airborne: a++; break;
                    default: if (b.Margin < Marginal) m++; else g++; break;
                }
        return (g, m, u, a);
    }
}
