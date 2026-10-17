using Blazma.Core.Model;
using Blazma.Rigging;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Result of a screen-space pick: a bone joint or an IK control.</summary>
public readonly record struct PickResult(string CharacterId, string? BoneId, string? ControlId, float Distance);

/// <summary>
/// Screen-space layer drawn on top of the 3D stage: bones, joint dots, IK controls and the active gizmo.
/// Everything is projected with the stage camera, so the overlay always lines up with the rendered mesh.
/// Also implements the viewport's input tools (picking and gizmo drags).
/// </summary>
public partial class SkeletonOverlay : Control, IStageInput
{
    public const float PickRadius = 12f;

    public ViewportView Stage { get; }
    public CharacterLayer Layer { get; }
    public SelectionState Sel { get; }
    public Gizmo Gizmo { get; }
    /// <summary>Phase 8: props in the stage (set by the main window right after construction).</summary>
    public PropLayer? Props { get; set; }
    public PropGizmo PropGizmo { get; }
    public Palette Pal { get; private set; } = Palette.Dark;

    Transform3D _lastCam;
    Vector2 _lastSize;
    Vector2 _mouse = new(-1000, -1000);
    (string Char, string? Bone, string? Control)? _hover;

    static AppController App => AppController.Instance;

    public SkeletonOverlay(ViewportView stage, CharacterLayer layer, SelectionState sel)
    {
        Stage = stage;
        Layer = layer;
        Sel = sel;
        Gizmo = new Gizmo(this);
        PropGizmo = new PropGizmo(this);
        MouseFilter = MouseFilterEnum.Ignore;
        LayoutDirection = LayoutDirectionEnum.Ltr;
        ClipContents = true;
        Layer.ViewsChanged += QueueRedraw;
        Sel.Changed += QueueRedraw;
        Gizmo.Changed += QueueRedraw;
        PropGizmo.Changed += QueueRedraw;
    }

    public void SetPalette(Palette p) { Pal = p; QueueRedraw(); }
    public Camera3D Cam => Stage.Camera;

    public override void _Process(double delta)
    {
        var t = Cam.GlobalTransform;
        if (!t.IsEqualApprox(_lastCam) || Size != _lastSize)
        {
            _lastCam = t;
            _lastSize = Size;
            QueueRedraw();
        }
    }

    // ---- projection helpers ----------------------------------------------------------------------------------------

    public bool Project(Vector3 world, out Vector2 screen)
    {
        if (Cam.IsPositionBehind(world)) { screen = default; return false; }
        screen = Cam.UnprojectPosition(world);
        return true;
    }

    /// <summary>World-space size of one screen pixel at the depth of <paramref name="world"/>.</summary>
    public float WorldPerPixel(Vector3 world)
    {
        var depth = Mathf.Max((world - Cam.GlobalPosition).Dot(-Cam.GlobalBasis.Z), 0.05f);
        return 2f * depth * Mathf.Tan(Mathf.DegToRad(Cam.Fov) * 0.5f) / Mathf.Max(Size.Y, 1f);
    }

    IEnumerable<CharacterView> ShownViews()
    {
        if (!App.Settings.ShowSkeleton) yield break;
        if (App.Settings.OverlayAll)
        {
            foreach (var v in Layer.Views.Values) yield return v;
        }
        else if (Layer.View(Sel.CharacterId) is { } v) yield return v;
    }

    // ---- picking --------------------------------------------------------------------------------------------------------

    /// <summary>Nearest joint or control within <see cref="PickRadius"/> pixels of <paramref name="screen"/>.</summary>
    public PickResult? PickAt(Vector2 screen)
    {
        var found = new List<PickResult>();
        foreach (var view in ShownViews())
        {
            for (var i = 0; i < view.Joints.Length; i++)
            {
                if (!Project(view.Joints[i], out var s)) continue;
                var d = s.DistanceTo(screen);
                if (d <= PickRadius) found.Add(new PickResult(view.CharacterId, view.Rig.Bones[i].Id.ToString(), null, d));
            }
            var controls = view.Rig.Controls;
            for (var i = 0; i < controls.Count && i < view.ControlPositions.Length; i++)
            {
                if (!Project(view.ControlPositions[i], out var s)) continue;
                var d = s.DistanceTo(screen);
                if (d <= PickRadius) found.Add(new PickResult(view.CharacterId, null, controls[i].Id.ToString(), d + 0.5f));
            }
        }
        if (found.Count == 0) return null;
        found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        // Clicking the same spot again cycles to the next candidate (a hand joint and its IK box coincide at rest).
        var best = found[0];
        var isSelected = best.CharacterId == Sel.CharacterId && (best.BoneId is not null ? best.BoneId == Sel.BoneId : best.ControlId == Sel.ControlId);
        if (isSelected)
            foreach (var other in found.Skip(1))
                if (other.Distance <= best.Distance + 4f) return other;
        return best;
    }

    /// <summary>Screen position of a bone joint (used by the self-test to click on it).</summary>
    public Vector2? ScreenOfBone(string characterId, string boneId)
    {
        var view = Layer.View(characterId);
        if (view is null) return null;
        var idx = view.Rig.Bones.FindIndex(b => b.Id.ToString() == boneId);
        return idx >= 0 && Project(view.Joints[idx], out var s) ? s : null;
    }

    public void ApplyPick(PickResult? pick)
    {
        if (pick is null) { Sel.ClearItem(); return; }
        var p = pick.Value;
        if (p.BoneId is not null) Sel.SelectBone(p.CharacterId, p.BoneId);
        else Sel.SelectControl(p.CharacterId, p.ControlId!);
    }

    // ---- input ---------------------------------------------------------------------------------------------------------------

    public bool FrameSelection()
    {
        if (Layer.Bounds(Sel.CharacterId) is not { } box) return false;
        Stage.Frame(box);
        QueueRedraw();
        return true;
    }

    public bool HandleInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey { Pressed: true, Keycode: Key.Escape } when Gizmo.Dragging || PropGizmo.Dragging:
                Gizmo.Cancel(); PropGizmo.Cancel();
                return true;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } when Gizmo.Dragging || PropGizmo.Dragging:
                Gizmo.Cancel(); PropGizmo.Cancel();
                return true;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                _mouse = mb.Position;
                if (mb.Pressed)
                {
                    if (mb.ShiftPressed) return false;
                    if (Gizmo.TryBegin(mb.Position)) return true;
                    if (PropGizmo.TryBegin(mb.Position)) return true;
                    var pick = PickAt(mb.Position);
                    if (pick is null && PickProp(mb.Position) is { } prop) { Sel.SelectProp(prop); return true; }
                    ApplyPick(pick);
                    return true;
                }
                if (Gizmo.Dragging) { Gizmo.End(true); return true; }
                if (PropGizmo.Dragging) { PropGizmo.End(true); return true; }
                return false;
            case InputEventMouseMotion mm:
                _mouse = mm.Position;
                if (Gizmo.Dragging)
                {
                    if ((mm.ButtonMask & MouseButtonMask.Left) == 0) { Gizmo.End(true); return true; }
                    Gizmo.Update(mm.Position);
                    return true;
                }
                if (PropGizmo.Dragging)
                {
                    if ((mm.ButtonMask & MouseButtonMask.Left) == 0) { PropGizmo.End(true); return true; }
                    PropGizmo.Update(mm.Position);
                    return true;
                }
                UpdateHover(mm.Position);
                return false;
        }
        return false;
    }

    void UpdateHover(Vector2 pos)
    {
        var handleChanged = Gizmo.UpdateHover(pos) | PropGizmo.UpdateHover(pos);
        var pick = Gizmo.HoverHandle >= 0 || PropGizmo.HoverHandle >= 0 ? null : PickAt(pos);
        var next = pick is { } p ? (p.CharacterId, p.BoneId, p.ControlId) : ((string, string?, string?)?)null;
        if (handleChanged || next != _hover) { _hover = next; QueueRedraw(); }
    }

    // ---- drawing ----------------------------------------------------------------------------------------------------------------

    public override void _Draw()
    {
        var p = Pal;
        var font = BlazmaTheme.Mono;
        foreach (var view in ShownViews())
        {
            var isSel = view.CharacterId == Sel.CharacterId;
            var rig = view.Rig;
            var ev = view.Evaluator;
            var n = Math.Min(view.Joints.Length, rig.Bones.Count);
            var fill = p.Panel2 with { A = 0.78f };
            var line = p.Text with { A = isSel ? 0.85f : 0.45f };
            var amber = p.Warn;
            var selBone = isSel ? Sel.BoneId : null;

            // Bone shapes (parent joint → child joint).
            for (var i = 0; i < n; i++)
            {
                var parent = ev.ParentOf(i);
                if (parent < 0) continue;
                if (!Project(view.Joints[parent], out var a) || !Project(view.Joints[i], out var b)) continue;
                var highlighted = selBone is not null && rig.Bones[parent].Id.ToString() == selBone;
                DrawBone(a, b, highlighted ? amber with { A = 0.55f } : fill, highlighted ? amber : line, highlighted ? 1.8f : 1.1f);
            }

            // Joints.
            for (var i = 0; i < n; i++)
            {
                if (!Project(view.Joints[i], out var s)) continue;
                var id = rig.Bones[i].Id.ToString();
                var selected = selBone == id;
                var hovered = _hover is { } h && h.Char == view.CharacterId && h.Bone == id;
                if (selected)
                {
                    DrawCircle(s, 8.5f, amber with { A = 0.25f });
                    DrawArc(s, 8.5f, 0, Mathf.Tau, 24, amber, 1.6f, true);
                }
                DrawCircle(s, selected ? 5.2f : hovered ? 5f : 3.6f, p.Bg with { A = 0.95f });
                DrawCircle(s, selected ? 4.2f : hovered ? 4f : 2.7f, selected ? amber : hovered ? p.Accent : p.Text);
            }

            // IK controls.
            var chainOfPole = new Dictionary<string, IkChain>();
            foreach (var ch in rig.IkChains) if (!ch.Pole.IsEmpty) chainOfPole[ch.Pole.ToString()] = ch;
            for (var i = 0; i < rig.Controls.Count && i < view.ControlPositions.Length; i++)
            {
                var c = rig.Controls[i];
                if (!Project(view.ControlPositions[i], out var s)) continue;
                var id = c.Id.ToString();
                var selected = isSel && Sel.ControlId == id;
                var hovered = _hover is { } h && h.Char == view.CharacterId && h.Control == id;
                if (c.Kind == ControlKind.IkTarget)
                {
                    var half = selected || hovered ? 7.5f : 6.5f;
                    var rect = new Rect2(s - new Vector2(half, half), new Vector2(half * 2, half * 2));
                    if (selected) DrawRect(rect.Grow(3), p.Text, false, 1.4f);
                    DrawRect(rect, view.ControlPinned[i] ? amber : p.Bg with { A = 0.55f }, true);
                    DrawRect(rect, hovered && !selected ? p.Accent : amber, false, 2f);
                }
                else
                {
                    var half = selected || hovered ? 7.5f : 6.2f;
                    if (chainOfPole.TryGetValue(id, out var chain))
                    {
                        var lower = ev.IndexOf(chain.Lower);
                        if (lower >= 0 && Project(view.Joints[lower], out var knee))
                            DrawDashedLine(knee, s, amber with { A = 0.8f }, 1.4f, 4f, true, true);
                    }
                    var pts = new[] { s + new Vector2(0, -half), s + new Vector2(half, 0), s + new Vector2(0, half), s + new Vector2(-half, 0) };
                    if (selected) DrawColoredPolygon(new[] { s + new Vector2(0, -half - 3.5f), s + new Vector2(half + 3.5f, 0), s + new Vector2(0, half + 3.5f), s + new Vector2(-half - 3.5f, 0) }, p.Text with { A = 0.35f });
                    DrawColoredPolygon(pts, p.Bg with { A = 0.7f });
                    DrawPolyline(new[] { pts[0], pts[1], pts[2], pts[3], pts[0] }, hovered && !selected ? p.Accent : amber, 2f, true);
                }
            }
        }
        Gizmo.Draw(this, font);
        DrawSelectedProp(font);
        PropGizmo.Draw(this, font);
    }

    // ---- props (Phase 8) ---------------------------------------------------------------------------------------------------

    /// <summary>Half extents of the box that stands for a prop when picking and outlining (in the prop's own space).</summary>
    public static Vector3 PropHalfExtents(Prop prop)
    {
        var s = new Vector3((float)prop.Size.X, (float)prop.Size.Y, (float)prop.Size.Z) / 2f;
        if (prop.Shape == PropShape.Sphere) s = new Vector3(s.X, s.X, s.X);
        else if (prop.Shape == PropShape.Cylinder) s = new Vector3(s.X, s.Y, s.X);
        return new Vector3(Mathf.Max(s.X, 0.03f), Mathf.Max(s.Y, 0.03f), Mathf.Max(s.Z, 0.03f));
    }

    /// <summary>The prop under <paramref name="screen"/> (nearest along the camera ray), or null.</summary>
    public string? PickProp(Vector2 screen)
    {
        if (Props is null) return null;
        var ro = Cam.ProjectRayOrigin(screen);
        var rd = Cam.ProjectRayNormal(screen);
        string? best = null;
        var bestT = float.MaxValue;
        foreach (var prop in App.Session.Project.Props)
        {
            var id = prop.Id.ToString();
            if (Props.WorldOf(id) is not { } w) continue;
            var inv = CharacterView.ToT3(w).AffineInverse();
            var o = inv * ro;
            var d = inv.Basis * rd;
            var h = PropHalfExtents(prop);
            float t0 = 0, t1 = float.MaxValue;
            var hit = true;
            for (var a = 0; a < 3 && hit; a++)
            {
                var oa = o[a]; var da = d[a]; var ha = h[a];
                if (Mathf.Abs(da) < 1e-8f) { if (oa < -ha || oa > ha) hit = false; continue; }
                var ta = (-ha - oa) / da; var tb = (ha - oa) / da;
                if (ta > tb) (ta, tb) = (tb, ta);
                t0 = Mathf.Max(t0, ta); t1 = Mathf.Min(t1, tb);
                if (t0 > t1) hit = false;
            }
            if (hit && t0 < bestT) { bestT = t0; best = id; }
        }
        return best;
    }

    /// <summary>Screen position of a prop's origin (used by the self-test to click on it).</summary>
    public Vector2? ScreenOfProp(string propId) =>
        Props?.WorldOf(propId) is { } w && Project(CharacterView.ToV3(w.Translation), out var s) ? s : null;

    void DrawSelectedProp(Font font)
    {
        if (Props is null || Sel.PropId is not { } id || Props.Find(id) is not { } prop || Props.WorldOf(id) is not { } w) return;
        var t = CharacterView.ToT3(w);
        var h = PropHalfExtents(prop);
        var corners = new Vector2[8];
        for (var i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z);
            if (!Project(t * c, out corners[i])) return;
        }
        var col = Pal.Accent;
        foreach (var (a, b) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) })
            DrawLine(corners[a], corners[b], col with { A = 0.9f }, 1.4f, true);
        if (Project(t.Origin, out var o))
        {
            DrawCircle(o, 4.5f, Pal.Bg with { A = 0.95f });
            DrawCircle(o, 3.4f, col);
            DrawString(BlazmaTheme.Regular, o + new Vector2(10, -10), prop.Name, HorizontalAlignment.Left, -1, 12, Pal.Text);
        }
    }

    void DrawBone(Vector2 a, Vector2 b, Color fill, Color outline, float width)
    {
        var dir = b - a;
        var len = dir.Length();
        if (len < 3f) { DrawLine(a, b, outline, width, true); return; }
        var nrm = new Vector2(-dir.Y, dir.X) / len;
        var w = Mathf.Clamp(len * 0.11f, 1.8f, 6.5f);
        var mid = a + dir * 0.2f;
        var poly = new[] { a, mid + nrm * w, b, mid - nrm * w };
        DrawColoredPolygon(poly, fill);
        DrawPolyline(new[] { poly[0], poly[1], poly[2], poly[3], poly[0] }, outline, width, true);
    }
}
