using Blazma.Core.Mathematics;
using Blazma.Physics;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 6 viewport layer, drawn in screen space under the skeleton like <see cref="MotionOverlay"/>: the centre of mass as a
/// sphere with a drop line to the ground, the support polygon on the ground (translucent fill and outline), the contact points,
/// the projected COM / ZMP dots coloured by state (green balanced, red unbalanced, grey airborne) and optionally the COM, hips and
/// feet paths over the whole clip. Results come from the cached <see cref="PhysicsAnalysis"/>; this class never analyses anything
/// itself and does nothing while the Physics toggle is off.
/// </summary>
public partial class PhysicsOverlay : Control
{
    readonly SkeletonOverlay _proj;
    Transform3D _lastCam;
    Vector2 _lastSize;

    static AppController App => AppController.Instance;
    Palette P => _proj.Pal;

    /// <summary>What the last draw produced (for tests).</summary>
    public BalanceState? DrawnState { get; private set; }
    public int DrawnContacts { get; private set; }
    public int DrawnPolygonPoints { get; private set; }
    public int DrawnPathPoints { get; private set; }
    public int DrawCount { get; private set; }
    public bool DrewComMarker { get; private set; }

    public PhysicsOverlay(SkeletonOverlay projector)
    {
        _proj = projector;
        MouseFilter = MouseFilterEnum.Ignore;
        LayoutDirection = LayoutDirectionEnum.Ltr;
        ClipContents = true;
        App.PlaybackChanged += QueueRedraw;
        App.SettingsChanged += QueueRedraw;
        App.SessionChanged += _ => QueueRedraw();
        App.Physics.Invalidated += QueueRedraw;
        _proj.Sel.Changed += QueueRedraw;
    }

    public override void _ExitTree()
    {
        App.PlaybackChanged -= QueueRedraw;
        App.SettingsChanged -= QueueRedraw;
        App.Physics.Invalidated -= QueueRedraw;
    }

    public override void _Process(double delta)
    {
        var cam = _proj.Cam.GlobalTransform;
        if (!cam.IsEqualApprox(_lastCam) || Size != _lastSize) { _lastCam = cam; _lastSize = Size; QueueRedraw(); }
    }

    public static Color StateColor(Palette p, BalanceState s) => s switch
    {
        BalanceState.Balanced => p.Ok,
        BalanceState.Unbalanced => p.Err,
        _ => p.Text3,
    };

    bool Project(Vec3 v, out Vector2 s) => _proj.Project(CharacterView.ToV3(v), out s);

    public override void _Draw()
    {
        DrawCount++;
        DrawnState = null; DrawnContacts = 0; DrawnPolygonPoints = 0; DrawnPathPoints = 0; DrewComMarker = false;
        var s = App.Settings;
        if (!s.ShowPhysics && !s.ShowPhysicsPaths) return;
        var phys = App.Physics;
        if (!phys.HasFeet) return;
        if (s.ShowPhysicsPaths) DrawPaths(phys);
        if (s.ShowPhysics && phys.Current is { } b) DrawBalance(b);
    }

    // ---- balance ------------------------------------------------------------------------------------------------------

    void DrawBalance(PoseBalance b)
    {
        var p = P;
        DrawnState = b.State;
        var state = StateColor(p, b.State);
        var y = b.GroundHeight;

        // support polygon
        var pts = new List<Vector2>();
        foreach (var q in b.Polygon)
            if (Project(new Vec3(q.X, y, q.Z), out var sp)) pts.Add(sp);
        DrawnPolygonPoints = b.Polygon.Count;
        if (pts.Count == b.Polygon.Count && pts.Count >= 3 && Math.Abs(Area(pts)) > 4f)
        {
            DrawColoredPolygon(pts.ToArray(), state with { A = 0.22f });
            var ring = new List<Vector2>(pts) { pts[0] };
            DrawPolyline(ring.ToArray(), p.Bg with { A = 0.6f }, 4f, true);
            DrawPolyline(ring.ToArray(), state with { A = 0.95f }, 2f, true);
        }
        else if (pts.Count == b.Polygon.Count && pts.Count >= 2)
        {
            DrawLine(pts[0], pts[^1], p.Bg with { A = 0.6f }, 5f, true);
            DrawLine(pts[0], pts[^1], state, 2.5f, true);
        }
        else if (pts.Count == 1) DrawCircle(pts[0], 4f, state);

        // contact points
        DrawnContacts = b.Contacts.Count;
        foreach (var c in b.Contacts)
            if (Project(c.Position, out var sp))
            {
                DrawRect(new Rect2(sp - new Vector2(3.5f, 3.5f), new Vector2(7, 7)), p.Bg);
                DrawRect(new Rect2(sp - new Vector2(2.5f, 2.5f), new Vector2(5, 5)), p.Text);
            }

        // COM sphere, drop line, projected dot
        var ground = new Vec3(b.Com.X, y, b.Com.Z);
        var haveCom = Project(b.Com, out var com);
        var haveGround = Project(ground, out var gp);
        if (haveCom && haveGround)
        {
            DrawDashedLine(com, gp, p.Bg with { A = 0.7f }, 3.4f, 5f, true);
            DrawDashedLine(com, gp, p.Text, 1.5f, 5f, true);
        }
        if (haveGround)
        {
            DrawCircle(gp, 8.5f, p.Bg);
            DrawCircle(gp, 6.5f, state);
            DrawArc(gp, 6.5f, 0, Mathf.Tau, 20, p.Text with { A = 0.9f }, 1.2f, true);
            if (b.Zmp is { } zmp && Project(zmp, out var zp) && zp.DistanceTo(gp) > 6f)
            {
                var d = 6.5f;
                var diamond = new[] { zp + new Vector2(0, -d), zp + new Vector2(d, 0), zp + new Vector2(0, d), zp + new Vector2(-d, 0), zp + new Vector2(0, -d) };
                DrawPolyline(diamond, p.Bg, 4f, true);
                DrawPolyline(diamond, state, 2f, true);
            }
            DrawMarginLabel(b, gp);
        }
        if (haveCom)
        {
            DrewComMarker = true;
            DrawCircle(com, 11f, p.Bg with { A = 0.8f });
            DrawCircle(com, 9f, p.Accent);
            DrawCircle(com + new Vector2(-2.6f, -2.8f), 3.2f, p.Accent.Lightened(0.55f) with { A = 0.85f }); // highlight: reads as a sphere
            DrawArc(com, 9f, 0, Mathf.Tau, 24, p.Text with { A = 0.9f }, 1.3f, true);
        }
    }

    void DrawMarginLabel(PoseBalance b, Vector2 at)
    {
        if (PhysicsAnalysis.MarginCm(b) is not { } cm) return;
        // numbers are always left-to-right, also in Arabic
        var text = (cm > 0 ? "+" : cm < 0 ? "−" : "") + Math.Abs(cm).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " cm";
        var font = BlazmaTheme.Mono;
        var size = font.GetStringSize(text, HorizontalAlignment.Left, -1, 11);
        var pos = at + new Vector2(11, 4);
        DrawRect(new Rect2(pos + new Vector2(-4, -size.Y + 1), size + new Vector2(8, 4)), P.Bg with { A = 0.78f });
        DrawString(font, pos, text, HorizontalAlignment.Left, -1, 11, StateColor(P, b.State));
    }

    static float Area(IReadOnlyList<Vector2> pts)
    {
        var a = 0f;
        for (var i = 0; i < pts.Count; i++)
        {
            var q = pts[(i + 1) % pts.Count];
            a += pts[i].X * q.Y - q.X * pts[i].Y;
        }
        return a / 2;
    }

    // ---- paths ----------------------------------------------------------------------------------------------------------

    void DrawPaths(PhysicsAnalysis phys)
    {
        if (phys.Paths is not { } t) return;
        var p = P;
        Path(t.Com, p.Accent, 1.0f, true);
        Path(t.Hips, p.Text2, 0.8f, false);
        Path(t.LeftFoot, p.Z, 0.9f, false);
        Path(t.RightFoot, p.X, 0.9f, false);
    }

    void Path(Vec3[] path, Color color, float alpha, bool strong)
    {
        if (path.Length < 2) return;
        var screen = new Vector2?[path.Length];
        var pts = new List<Vector2>(path.Length);
        for (var i = 0; i < path.Length; i++)
            if (Project(path[i], out var sp)) { screen[i] = sp; pts.Add(sp); }
        DrawnPathPoints += pts.Count;
        if (pts.Count >= 2)
        {
            DrawPolyline(pts.ToArray(), P.Bg with { A = 0.6f }, strong ? 4.6f : 3.8f, true);
            DrawPolyline(pts.ToArray(), color with { A = alpha * 0.9f }, strong ? 2.4f : 1.8f, true);
        }
        foreach (var sp in screen)
            if (sp is { } q) DrawCircle(q, strong ? 1.9f : 1.5f, color with { A = 0.9f });
        var cur = Math.Clamp(App.CurrentFrame, 0, path.Length - 1);
        if (screen[cur] is { } c) DrawArc(c, strong ? 7f : 5.5f, 0, Mathf.Tau, 20, P.Text, 1.6f, true);
    }
}
