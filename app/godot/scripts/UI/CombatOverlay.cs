using System.Text.Json.Nodes;
using Blazma.Combat;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 9 viewport layer, drawn in screen space under the skeleton like <see cref="PhysicsOverlay"/>: the focused character's
/// hurtboxes as translucent shapes in the colour of their zone (high amber, middle blue, low green) and the clip's hitboxes as
/// outlines, bold and bright while active at the current frame, thin and dim otherwise. Shapes come from <see cref="CombatEvaluator"/>
/// (the same code the hit test and combat.shapes use), are cached per frame and clip, and are only drawn while the Combat view toggle
/// is on (off by default). After a hit test the overlay also shows the defender's hurtboxes at the tested placement with a ground
/// marker, outlines the hurtbox that was hit, and marks the contact point with an arrow along the contact normal.
/// </summary>
public partial class CombatOverlay : Control
{
    readonly record struct Item(WorldShape Shape, bool IsHitbox, HitZone Zone, bool Active, bool Marked, bool Defender);

    readonly SkeletonOverlay _proj;
    Transform3D _lastCam;
    Vector2 _lastSize;
    string _key = "";
    List<Item> _items = new();
    Vec3? _contact, _normal;
    bool _contactAtFrame;
    long _contactFrame;
    Vec3? _defenderGround;
    double _defenderFacing;

    static AppController App => AppController.Instance;
    Palette P => _proj.Pal;

    /// <summary>What the last draw produced (for tests).</summary>
    public int DrawCount { get; private set; }
    public int DrawnHurtboxes { get; private set; }
    public int DrawnHitboxes { get; private set; }
    public int DrawnActiveHitboxes { get; private set; }
    public int DrawnDefenderHurtboxes { get; private set; }
    public bool DrewContact { get; private set; }
    public bool DrewMarkedHurtbox { get; private set; }
    public Vector2? ContactScreen { get; private set; }
    public Vector2? NormalTipScreen { get; private set; }

    public CombatOverlay(SkeletonOverlay projector)
    {
        _proj = projector;
        MouseFilter = MouseFilterEnum.Ignore;
        LayoutDirection = LayoutDirectionEnum.Ltr;
        ClipContents = true;
        App.PlaybackChanged += QueueRedraw;
        App.SettingsChanged += QueueRedraw;
        App.SessionChanged += _ => QueueRedraw();
        App.HitViewChanged += QueueRedraw;
        _proj.Sel.Changed += QueueRedraw;
    }

    public override void _ExitTree()
    {
        App.PlaybackChanged -= QueueRedraw;
        App.SettingsChanged -= QueueRedraw;
        App.HitViewChanged -= QueueRedraw;
    }

    public override void _Process(double delta)
    {
        var cam = _proj.Cam.GlobalTransform;
        if (!cam.IsEqualApprox(_lastCam) || Size != _lastSize) { _lastCam = cam; _lastSize = Size; QueueRedraw(); }
    }

    bool Project(Vec3 v, out Vector2 s) => _proj.Project(CharacterView.ToV3(v), out s);

    /// <summary>Redraw now and return (the self-test reads what was drawn).</summary>
    public void ForceDraw() { _key = ""; QueueRedraw(); RenderingServer.ForceDraw(); }

    public static bool Enabled => App.Settings.ShowCombat && App.Settings.AdvancedMode;

    // ---- shapes ------------------------------------------------------------------------------------------------------------

    /// <summary>Rebuild the world shapes when the frame, the clip, the project or the hit test changed.</summary>
    void Refresh()
    {
        var clip = App.ActiveClip;
        var frame = App.CurrentFrameExact;
        var subject = App.FocusCharacter;
        var hv = App.HitView;
        var key = $"{App.SessionVersion}|{App.ClipVersion}|{subject?.Id}|{clip?.Id}|{frame:R}|{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(hv)}";
        if (key == _key) return;
        _key = key;
        _items = new List<Item>();
        _contact = _normal = null;
        _defenderGround = null;
        if (subject is null) return;
        var project = App.Session.Project;
        var rig = project.FindRig(subject.Rig);
        if (rig is null) return;
        try
        {
            var playing = clip is not null && AppController.HasKeys(clip) && (clip.Skeleton.IsEmpty || clip.Skeleton == rig.Id) ? clip : null;
            var shapeClip = clip is not null && (clip.Skeleton.IsEmpty || clip.Skeleton == rig.Id) ? clip : null;
            var t = playing is null ? Flicks.Zero : CombatEvaluator.TimeOf(frame, playing.Fps);
            var model = CombatEvaluator.Model(rig, subject, playing, t);
            var inTest = hv is not null && clip is not null && hv.AttackClipId == clip.Id.ToString() && hv.AttackerId == subject.Id.ToString();
            var first = inTest ? hv!.FirstHit : null;
            var hitFrame = first is null ? -1 : long.Parse((first["frame"] ?? -1).ToJsonString());
            var atHitFrame = first is not null && Math.Abs(frame - hitFrame) < 0.5;
            var hurtMarked = first is null ? null : (string?)first["hurtboxId"];
            var hitMarked = first is null ? null : (string?)first["hitboxId"];

            foreach (var h in subject.Combat?.Hurtboxes ?? new List<Hurtbox>())
                if (h.Enabled && CombatEvaluator.HurtboxShape(rig, h, model, Transform.Identity) is { } w && w.IsFinite)
                    _items.Add(new Item(w, false, h.Zone, false, false, false));
            if (shapeClip is not null)
                foreach (var h in shapeClip.Combat?.Hitboxes ?? new List<Hitbox>())
                {
                    if (CombatEvaluator.HitboxShape(project, h, shapeClip, rig, subject.Id, model, t, Transform.Identity) is not { } w || !w.IsFinite) continue;
                    var active = playing is not null && CombatEvaluator.IsActive(h, t);
                    _items.Add(new Item(w, true, HitZone.Mid, active, atHitFrame && h.Id.ToString() == hitMarked, false));
                }

            if (inTest && project.FindCharacter(Blazma.Core.Ids.ObjectId.Parse(hv!.DefenderId)) is { } def && project.FindRig(def.Rig) is { } rigD)
            {
                var defClip = hv.DefenderClipId is null ? null : project.FindClip(Blazma.Core.Ids.ObjectId.Parse(hv.DefenderClipId));
                var placement = CombatEvaluator.Placement(new Vec3(hv.X, 0, hv.Z), hv.Facing);
                var dt = defClip is null ? Flicks.Zero : ClipEvaluator.PlaybackTime(defClip, t);
                var dmodel = CombatEvaluator.Model(rigD, def, defClip, dt);
                foreach (var h in def.Combat?.Hurtboxes ?? new List<Hurtbox>())
                    if (h.Enabled && CombatEvaluator.HurtboxShape(rigD, h, dmodel, placement) is { } w && w.IsFinite)
                        _items.Add(new Item(w, false, h.Zone, false, atHitFrame && h.Id.ToString() == hurtMarked, true));
                _defenderGround = new Vec3(hv.X, 0, hv.Z);
                _defenderFacing = hv.Facing;
            }
            if (first is not null)
            {
                _contact = Vec(first["point"]); _normal = Vec(first["normal"]);
                _contactAtFrame = atHitFrame; _contactFrame = hitFrame;
            }
        }
        catch (Exception e) when (e is CombatException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            GD.PushWarning("Combat overlay: " + e.Message);
            _items.Clear();
        }
    }

    static Vec3? Vec(JsonNode? n) => n is JsonArray { Count: 3 } a ? new Vec3((double)a[0]!, (double)a[1]!, (double)a[2]!) : null;

    // ---- drawing ------------------------------------------------------------------------------------------------------------

    public override void _Draw()
    {
        DrawCount++;
        DrawnHurtboxes = DrawnHitboxes = DrawnActiveHitboxes = DrawnDefenderHurtboxes = 0;
        DrewContact = DrewMarkedHurtbox = false;
        ContactScreen = NormalTipScreen = null;
        if (!Enabled) return;
        Refresh();
        if (_defenderGround is { } g) DrawGroundMarker(g);
        foreach (var it in _items.Where(i => !i.IsHitbox)) DrawHurtbox(it);
        foreach (var it in _items.Where(i => i.IsHitbox)) DrawHitbox(it);
        DrawContact();
    }

    void DrawHurtbox(Item it)
    {
        var p = P;
        var col = CombatKit.ZoneColor(p, it.Zone);
        DrawnHurtboxes++;
        if (it.Defender) DrawnDefenderHurtboxes++;
        if (it.Marked)
        {
            DrewMarkedHurtbox = true;
            DrawShape(it.Shape, p.Text with { A = 0.0f }, p.Bg with { A = 0.9f }, 5.2f);
            DrawShape(it.Shape, col with { A = 0.62f }, p.Text, 2.6f);
        }
        else DrawShape(it.Shape, col with { A = 0.24f }, col with { A = 0.95f }, 1.3f);
    }

    void DrawHitbox(Item it)
    {
        var p = P;
        var col = CombatKit.HitboxColor(p);
        DrawnHitboxes++;
        if (it.Active)
        {
            DrawnActiveHitboxes++;
            DrawShape(it.Shape, col with { A = 0.18f }, p.Bg with { A = 0.85f }, 5.2f);
            DrawShape(it.Shape, col with { A = 0.18f }, col.Lightened(0.15f), 2.6f);
        }
        else DrawShape(it.Shape, col with { A = 0f }, col with { A = 0.5f }, 1.2f);
    }

    /// <summary>Spheres and capsules as the outline of the two end circles' convex hull, boxes as their 12 edges over the hull of their corners.</summary>
    void DrawShape(WorldShape w, Color fill, Color line, float width)
    {
        if (w.IsBox)
        {
            var corners = new Vector2[8];
            for (var i = 0; i < 8; i++)
            {
                var local = new Vec3((i & 1) == 0 ? -w.Half.X : w.Half.X, (i & 2) == 0 ? -w.Half.Y : w.Half.Y, (i & 4) == 0 ? -w.Half.Z : w.Half.Z);
                if (!Project(w.Center + w.Rotation.Rotate(local), out corners[i])) return;
            }
            var hull = Geometry2D.ConvexHull(corners);
            if (fill.A > 0 && hull.Length >= 4) DrawColoredPolygon(hull[..^1], fill);
            int[][] edges = { new[] { 0, 1 }, new[] { 2, 3 }, new[] { 4, 5 }, new[] { 6, 7 }, new[] { 0, 2 }, new[] { 1, 3 }, new[] { 4, 6 }, new[] { 5, 7 }, new[] { 0, 4 }, new[] { 1, 5 }, new[] { 2, 6 }, new[] { 3, 7 } };
            foreach (var e in edges) DrawLine(corners[e[0]], corners[e[1]], line, width, true);
            return;
        }
        var pts = new List<Vector2>();
        foreach (var end in w.A == w.B ? new[] { w.A } : new[] { w.A, w.B })
        {
            if (!Project(end, out var c)) return;
            var r = (float)(w.Radius / _proj.WorldPerPixel(CharacterView.ToV3(end)));
            r = Mathf.Max(r, 2f);
            for (var i = 0; i < 24; i++)
            {
                var a = i * Mathf.Tau / 24;
                pts.Add(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r);
            }
        }
        var outline = pts.Count > 24 ? Geometry2D.ConvexHull(pts.ToArray()) : pts.Append(pts[0]).ToArray();
        if (fill.A > 0) DrawColoredPolygon(outline.Length > 3 && outline[0] == outline[^1] ? outline[..^1] : outline, fill);
        DrawPolyline(outline, line, width, true);
    }

    void DrawGroundMarker(Vec3 g)
    {
        var p = P;
        var ring = new List<Vector2>();
        for (var i = 0; i <= 32; i++)
        {
            var a = i * Math.PI * 2 / 32;
            if (!Project(new Vec3(g.X + Math.Cos(a) * 0.4, 0, g.Z + Math.Sin(a) * 0.4), out var s)) return;
            ring.Add(s);
        }
        DrawPolyline(ring.ToArray(), p.Bg with { A = 0.6f }, 4f, true);
        DrawPolyline(ring.ToArray(), p.Text2 with { A = 0.9f }, 1.6f, true);
        var f = _defenderFacing * Math.PI / 180;
        var dir = new Vec3(Math.Sin(f), 0, Math.Cos(f));
        if (Project(g, out var a0) && Project(g + dir * 0.65, out var a1))
        {
            DrawLine(a0, a1, p.Bg with { A = 0.6f }, 4f, true);
            DrawLine(a0, a1, p.Text2, 1.8f, true);
            Arrow(a0, a1, p.Text2, 7f);
        }
    }

    void DrawContact()
    {
        if (_contact is not { } point || !Project(point, out var c)) return;
        var p = P;
        var strong = _contactAtFrame;
        var a = strong ? 1f : 0.45f;
        DrewContact = true;
        ContactScreen = c;
        DrawCircle(c, 12f, p.Bg with { A = 0.75f * a });
        DrawCircle(c, 9f, p.Err with { A = a });
        DrawCircle(c, 4f, p.Text with { A = a });
        DrawArc(c, 12f, 0, Mathf.Tau, 28, p.Text with { A = a }, 1.6f, true);
        if (_normal is { } n && Project(point + n.Normalized() * 0.28, out var tip))
        {
            NormalTipScreen = tip;
            DrawLine(c, tip, p.Bg with { A = 0.75f * a }, 5.4f, true);
            DrawLine(c, tip, p.Warn with { A = a }, 3f, true);
            Arrow(c, tip, p.Warn with { A = a }, 10f);
        }
        if (strong)
        {
            var text = _contactFrame.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var font = BlazmaTheme.Mono;
            var size = font.GetStringSize(text, HorizontalAlignment.Left, -1, 12);
            var pos = c + new Vector2(16, -10);
            DrawRect(new Rect2(pos + new Vector2(-4, -size.Y + 2), size + new Vector2(8, 4)), p.Bg with { A = 0.8f });
            DrawString(font, pos, text, HorizontalAlignment.Left, -1, 12, p.Text);
        }
    }

    void Arrow(Vector2 from, Vector2 to, Color col, float size)
    {
        var d = to - from;
        if (d.Length() < 1f) return;
        var u = d.Normalized();
        var n = new Vector2(-u.Y, u.X);
        DrawColoredPolygon(new[] { to, to - u * size + n * size * 0.45f, to - u * size - n * size * 0.45f }, col);
    }
}
