using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.UI.App;
using Godot;
using CoreQuat = Blazma.Core.Mathematics.Quat;
using CoreTransform = Blazma.Core.Mathematics.Transform;

namespace Blazma.UI.Views;

/// <summary>
/// Move and rotate gizmo for the selected prop, drawn in screen space like the bone gizmo (world axes). A drag is ONE transaction: every
/// mouse step calls prop.set_transform with absolute values (consecutive calls merge), so the whole drag is one undo step; Esc or the
/// right mouse button rolls it back. For an attached prop the world placement is converted to the offset from its bone.
/// </summary>
public sealed class PropGizmo
{
    public enum Kind { None, Rotate, Move }

    const int Segments = 56;
    const float RingPixels = 62f;
    const float ArrowPixels = 74f;
    const float HitPixels = 8f;

    readonly SkeletonOverlay _o;
    TransactionScope? _scope;

    public Kind Current { get; private set; }
    string _propId = "";
    Vector3 _origin;

    public bool Dragging => _scope is not null;
    public int HoverHandle { get; private set; } = -1;
    int _handle = -1;
    string _label = "";
    public string DragLabel => _label;
    public string DebugInfo => $"handle={_handle} plane={_planeMode} angle={_angle * 180 / Mathf.Pi:0.00} axis={_axis}";
    public string LastDebug { get; private set; } = "";

    float _angle;
    Vector3 _axis;
    Vector3 _a0, _arcStart;
    bool _planeMode;
    Vector2 _centerScreen, _lastMouse;
    CoreTransform _startWorld;
    Vector3 _startPos;
    float _axisS0;
    Vector3 _planeHit0;

    public event Action? Changed;
    /// <summary>Raised after a drag was committed or rolled back (history and menus need a refresh).</summary>
    public event Action? DragEnded;

    static AppController App => AppController.Instance;
    Camera3D Cam => _o.Cam;

    public PropGizmo(SkeletonOverlay overlay) { _o = overlay; }

    // ---- target ----------------------------------------------------------------------------------------------------------------

    public Kind Resolve()
    {
        if (Dragging) return Current;
        Current = Kind.None;
        var sel = _o.Sel;
        if (_o.Props is not { } layer || sel.PropId is not { } id || sel.Tool == StageTool.Select) return Current;
        if (layer.WorldOf(id) is not { } w) return Current;
        _propId = id;
        _origin = CharacterView.ToV3(w.Translation);
        Current = sel.Tool == StageTool.Rotate ? Kind.Rotate : Kind.Move;
        return Current;
    }

    static Vector3 WorldAxis(int k) => k switch { 0 => Vector3.Right, 1 => Vector3.Up, _ => Vector3.Back };
    Vector3 CameraForward => -Cam.GlobalBasis.Z;
    float Radius => RingPixels * _o.WorldPerPixel(_origin);
    Color AxisColor(int k) => k switch { 0 => _o.Pal.X, 1 => _o.Pal.Y, _ => _o.Pal.Z };

    static void Perp(Vector3 n, out Vector3 u, out Vector3 v)
    {
        var helper = Mathf.Abs(n.Y) < 0.9f ? Vector3.Up : Vector3.Right;
        u = n.Cross(helper).Normalized();
        v = n.Cross(u).Normalized();
    }

    bool RingPoints(int k, Vector2[] screen, bool[] front)
    {
        Perp(WorldAxis(k), out var u, out var v);
        var camPos = Cam.GlobalPosition;
        for (var j = 0; j <= Segments; j++)
        {
            var t = j * Mathf.Tau / Segments;
            var dir = u * Mathf.Cos(t) + v * Mathf.Sin(t);
            var p = _origin + dir * Radius;
            if (!_o.Project(p, out screen[j])) return false;
            front[j] = dir.Dot(camPos - p) > 0;
        }
        return true;
    }

    readonly Vector2[][] _ringScreen = { new Vector2[Segments + 1], new Vector2[Segments + 1], new Vector2[Segments + 1] };
    readonly bool[][] _ringFront = { new bool[Segments + 1], new bool[Segments + 1], new bool[Segments + 1] };

    static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var l2 = ab.LengthSquared();
        if (l2 < 1e-6f) return p.DistanceTo(a);
        var t = Mathf.Clamp((p - a).Dot(ab) / l2, 0f, 1f);
        return p.DistanceTo(a + ab * t);
    }

    /// <summary>Which handle is under <paramref name="mouse"/>, or -1.</summary>
    public int HitTest(Vector2 mouse)
    {
        var kind = Resolve();
        if (kind == Kind.None) return -1;
        if (kind == Kind.Rotate)
        {
            var best = -1; var bestD = HitPixels;
            for (var k = 0; k < 3; k++)
            {
                if (!RingPoints(k, _ringScreen[k], _ringFront[k])) continue;
                var d = float.MaxValue;
                for (var j = 0; j < Segments; j++)
                    if (_ringFront[k][j]) d = Mathf.Min(d, DistToSegment(mouse, _ringScreen[k][j], _ringScreen[k][j + 1]));
                if (d < bestD) { bestD = d; best = k; }
            }
            return best;
        }
        if (!_o.Project(_origin, out var c)) return -1;
        if (mouse.DistanceTo(c) <= 10f) return 3;
        var bestA = -1; var bestAD = HitPixels;
        for (var k = 0; k < 3; k++)
        {
            if (!_o.Project(_origin + WorldAxis(k) * (ArrowPixels * _o.WorldPerPixel(_origin)), out var tip)) continue;
            var d = DistToSegment(mouse, c, tip);
            if (d < bestAD) { bestAD = d; bestA = k; }
        }
        return bestA;
    }

    public bool UpdateHover(Vector2 mouse)
    {
        var h = HitTest(mouse);
        if (h == HoverHandle) return false;
        HoverHandle = h;
        return true;
    }

    // ---- drag ----------------------------------------------------------------------------------------------------------------------

    public bool TryBegin(Vector2 mouse)
    {
        var handle = HitTest(mouse);
        if (handle < 0) return false;
        var layer = _o.Props!;
        var world = layer.WorldOf(_propId);
        if (world is null) return false;
        _handle = handle;
        _lastMouse = mouse;
        _angle = 0;
        _label = "";
        _startWorld = world.Value;
        var ray = (o: Cam.ProjectRayOrigin(mouse), d: Cam.ProjectRayNormal(mouse));
        if (Current == Kind.Rotate)
        {
            _axis = WorldAxis(handle);
            if (!_o.Project(_origin, out _centerScreen)) return true;
            var denom = ray.d.Dot(_axis);
            _planeMode = Mathf.Abs(denom) > 0.08f;
            if (_planeMode)
            {
                var hit = ray.o + ray.d * ((_origin - ray.o).Dot(_axis) / denom);
                _a0 = _arcStart = hit - _origin;
            }
            else
            {
                Perp(_axis, out var u, out _);
                _arcStart = u * Radius;
            }
            _scope = App.BeginTransaction("Rotate prop");
        }
        else
        {
            _startPos = _origin;
            if (handle < 3) _axisS0 = AxisParam(_startPos, WorldAxis(handle), ray.o, ray.d);
            else _planeHit0 = PlaneHit(_startPos, ray.o, ray.d) ?? _startPos;
            _scope = App.BeginTransaction("Move prop");
        }
        Changed?.Invoke();
        return true;
    }

    static float AxisParam(Vector3 origin, Vector3 axis, Vector3 ro, Vector3 rd)
    {
        var w0 = origin - ro;
        var b = axis.Dot(rd);
        var d = axis.Dot(w0);
        var e = rd.Dot(w0);
        var denom = 1 - b * b;
        return Mathf.Abs(denom) < 1e-4f ? 0f : (b * e - d) / denom;
    }

    Vector3? PlaneHit(Vector3 point, Vector3 ro, Vector3 rd)
    {
        var n = CameraForward;
        var denom = rd.Dot(n);
        if (Mathf.Abs(denom) < 1e-4f) return null;
        return ro + rd * ((point - ro).Dot(n) / denom);
    }

    public void Update(Vector2 mouse)
    {
        if (!Dragging) return;
        var ro = Cam.ProjectRayOrigin(mouse);
        var rd = Cam.ProjectRayNormal(mouse);
        if (Current == Kind.Rotate) UpdateRotate(mouse, ro, rd); else UpdateMove(ro, rd);
        _lastMouse = mouse;
        Changed?.Invoke();
    }

    void UpdateRotate(Vector2 mouse, Vector3 ro, Vector3 rd)
    {
        if (_planeMode)
        {
            var denom = rd.Dot(_axis);
            if (Mathf.Abs(denom) < 1e-3f) return;
            var hit = ro + rd * ((_origin - ro).Dot(_axis) / denom);
            var a1 = hit - _origin;
            if (a1.LengthSquared() < 1e-10f) return;
            _angle += Mathf.Atan2(_axis.Dot(_a0.Cross(a1)), _a0.Dot(a1));
            _a0 = a1;
        }
        else
        {
            var before = (_lastMouse - _centerScreen).Angle();
            var after = (mouse - _centerScreen).Angle();
            var dphi = Mathf.Wrap(after - before, -Mathf.Pi, Mathf.Pi);
            var towardViewer = _axis.Dot(Cam.GlobalBasis.Z) > 0;
            _angle += towardViewer ? -dphi : dphi;
        }
        var q = (CoreQuat.FromAxisAngle(new Vec3(_axis.X, _axis.Y, _axis.Z), _angle) * _startWorld.Rotation).Normalized();
        Apply(_startWorld with { Rotation = q });
        _label = string.Format(CultureInfo.InvariantCulture, "{0:+0.0;-0.0;0.0}°", _angle * 180 / Mathf.Pi);
    }

    void UpdateMove(Vector3 ro, Vector3 rd)
    {
        Vector3 pos;
        if (_handle < 3)
        {
            var axis = WorldAxis(_handle);
            pos = _startPos + axis * (AxisParam(_startPos, axis, ro, rd) - _axisS0);
        }
        else
        {
            if (PlaneHit(_startPos, ro, rd) is not { } hit) return;
            pos = _startPos + (hit - _planeHit0);
        }
        Apply(_startWorld with { Translation = new Vec3(pos.X, pos.Y, pos.Z) });
        var d = pos - _startPos;
        _label = string.Format(CultureInfo.InvariantCulture, "{0:+0.000;-0.000;0.000}  {1:+0.000;-0.000;0.000}  {2:+0.000;-0.000;0.000}" + Unit.M, d.X, d.Y, d.Z);
    }

    /// <summary>Send the new world placement as the prop's working value (the offset from its bone when attached).</summary>
    void Apply(CoreTransform world)
    {
        var layer = _o.Props!;
        if (layer.Find(_propId) is not { } prop) { Cancel(); return; }
        var local = layer.LocalFromWorld(prop, world);
        var e = local.Rotation.ToEulerXYZ() * (180 / Math.PI);
        layer.MarkEdited(_propId);
        var r = App.Execute("prop.set_transform", new JsonObject
        {
            ["propId"] = _propId,
            ["x"] = local.Translation.X, ["y"] = local.Translation.Y, ["z"] = local.Translation.Z,
            ["rx"] = e.X, ["ry"] = e.Y, ["rz"] = e.Z,
        });
        if (!r.Ok) Cancel();
    }

    /// <summary>Finish the drag. The whole drag is one undo step.</summary>
    public void End(bool commit)
    {
        var scope = _scope;
        LastDebug = DebugInfo;
        _scope = null;
        _handle = -1;
        _label = "";
        if (scope is not null)
        {
            if (commit) App.AutoKeyPropInOpenTransaction(_propId);
            if (commit) scope.Commit(); else scope.Rollback();
            DragEnded?.Invoke();
        }
        Changed?.Invoke();
    }

    public void Cancel() => End(false);

    // ---- test hooks ----------------------------------------------------------------------------------------------------------------

    /// <summary>Screen points along a rotate ring in the direction of positive rotation; point i is i·360/56 degrees from point 0.</summary>
    public Vector2[]? RingPath(int ring, int steps)
    {
        if (Resolve() != Kind.Rotate) return null;
        var axis = WorldAxis(ring);
        Perp(axis, out var u, out var v);
        var camPos = Cam.GlobalPosition;
        var bestJ = 0; var bestScore = float.MinValue;
        for (var j = 0; j < Segments; j++)
        {
            var t = j * Mathf.Tau / Segments;
            var p = _origin + (u * Mathf.Cos(t) + v * Mathf.Sin(t)) * Radius;
            var score = (camPos - p).Normalized().Dot((p - _origin).Normalized());
            if (score > bestScore) { bestScore = score; bestJ = j; }
        }
        var pts = new Vector2[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            var t = (bestJ + i) * Mathf.Tau / Segments;
            if (!_o.Project(_origin + (u * Mathf.Cos(t) + v * Mathf.Sin(t)) * Radius, out pts[i])) return null;
        }
        return pts;
    }

    public const float DegreesPerRingSample = 360f / Segments;

    /// <summary>Screen point on a move axis at <paramref name="metres"/> from the gizmo origin (axis 0..2).</summary>
    public Vector2? AxisPoint(int axis, float metres)
    {
        if (Resolve() != Kind.Move) return null;
        return _o.Project(_origin + WorldAxis(axis) * metres, out var s) ? s : null;
    }

    public float ArrowMetres => ArrowPixels * _o.WorldPerPixel(_origin);

    // ---- drawing -----------------------------------------------------------------------------------------------------------------------

    public void Draw(CanvasItem ci, Font font)
    {
        var kind = Resolve();
        if (kind == Kind.None) return;
        var p = _o.Pal;
        if (!_o.Project(_origin, out var c)) return;
        var active = Dragging ? _handle : HoverHandle;
        if (kind == Kind.Rotate)
        {
            ci.DrawCircle(c, RingPixels * 1.02f, p.Bg with { A = 0.12f });
            for (var k = 2; k >= 0; k--)
            {
                if (!RingPoints(k, _ringScreen[k], _ringFront[k])) continue;
                var col = AxisColor(k);
                var hi = active == k;
                var s = _ringScreen[k];
                var f = _ringFront[k];
                var start = 0;
                for (var seg = 0; seg < Segments; seg++)
                {
                    var last = seg + 1 == Segments;
                    if (!last && f[seg + 1] == f[start]) continue;
                    var run = new Vector2[seg + 2 - start];
                    Array.Copy(s, start, run, 0, run.Length);
                    var cc = f[start] ? col : col with { A = 0.32f };
                    if (!hi && Dragging) cc = cc with { A = cc.A * 0.45f };
                    ci.DrawPolyline(run, hi ? col.Lightened(0.15f) : cc, hi ? 3.6f : 2.4f, true);
                    start = seg + 1;
                }
            }
            if (Dragging && _handle >= 0 && Mathf.Abs(_angle) > 1e-4f && Mathf.Abs(_angle) < Mathf.Tau - 0.01f)
            {
                var u = _arcStart.Normalized();
                var v = _axis.Cross(u).Normalized();
                var pts = new List<Vector2> { c };
                var steps = Math.Max(2, (int)(Mathf.Abs(_angle) / 0.12f));
                var ok = true;
                for (var i = 0; i <= steps; i++)
                {
                    var a = _angle * i / steps;
                    if (!_o.Project(_origin + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * Radius, out var sp)) { ok = false; break; }
                    pts.Add(sp);
                }
                if (ok && pts.Count >= 3) ci.DrawColoredPolygon(pts.ToArray(), AxisColor(_handle) with { A = 0.30f });
            }
            ci.DrawCircle(c, 3f, p.Text);
            if (Dragging && _label.Length > 0) DrawLabel(ci, font, p, c + new Vector2(RingPixels + 14, -RingPixels * 0.85f), _label);
            return;
        }
        var len = ArrowPixels * _o.WorldPerPixel(_origin);
        for (var k = 0; k < 3; k++)
        {
            if (!_o.Project(_origin + WorldAxis(k) * len, out var tip)) continue;
            var col = AxisColor(k);
            if (active == k) col = col.Lightened(0.2f);
            else if (Dragging) col = col with { A = 0.4f };
            ci.DrawLine(c, tip, col, active == k ? 4f : 2.8f, true);
            var dir = (tip - c).Normalized();
            var nrm = new Vector2(-dir.Y, dir.X);
            ci.DrawColoredPolygon(new[] { tip + dir * 11f, tip + nrm * 5.5f - dir * 3f, tip - nrm * 5.5f - dir * 3f }, col);
        }
        var half = active == 3 ? 8f : 6.5f;
        var rect = new Rect2(c - new Vector2(half, half), new Vector2(half * 2, half * 2));
        ci.DrawRect(rect, p.Text with { A = active == 3 ? 0.9f : 0.55f }, true);
        ci.DrawRect(rect, p.Bg, false, 1.4f);
        if (Dragging && _label.Length > 0) DrawLabel(ci, font, p, c + new Vector2(ArrowPixels + 18, -ArrowPixels * 0.7f), _label);
    }

    static void DrawLabel(CanvasItem ci, Font font, Palette p, Vector2 at, string text)
    {
        const int size = 14;
        var w = font.GetStringSize(text, HorizontalAlignment.Left, -1, size);
        var rect = new Rect2(at - new Vector2(8, size + 4), w + new Vector2(16, 12));
        ci.DrawStyleBox(BlazmaTheme.Box(p.Raised, p.LineStrong, 6, 1, 0, 0, 0, 0), rect);
        ci.DrawString(font, at, text, HorizontalAlignment.Left, -1, size, p.Text);
    }
}
