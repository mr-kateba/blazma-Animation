using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Rigging;
using Blazma.UI.App;
using Godot;
using CoreQuat = Blazma.Core.Mathematics.Quat;

namespace Blazma.UI.Views;

/// <summary>
/// Rotate and Move gizmos, drawn in screen space and driven through commands. A drag is ONE transaction:
/// every mouse step calls pose.set_bone / pose.set_control with absolute values (so the command merges and the
/// whole drag is a single undo step); Esc or the right mouse button rolls the transaction back.
/// </summary>
public sealed class Gizmo
{
    public enum Kind { None, RotateBone, MoveControl, MoveBone }

    const int Segments = 56;
    const float RingPixels = 66f;
    const float ArrowPixels = 74f;
    const float HitPixels = 8f;

    readonly SkeletonOverlay _o;
    TransactionScope? _scope;

    // Resolved target (valid when Kind != None).
    public Kind Current { get; private set; }
    CharacterView? _view;
    int _bone = -1, _control = -1;
    Vector3 _origin;
    Quaternion _boneRot;   // model rotation of the bone (local axes)

    // Drag state.
    public bool Dragging => _scope is not null;
    public int HoverHandle { get; private set; } = -1;
    int _handle = -1;
    string _label = "";
    public string DragLabel => _label;
    float _angle;                // accumulated rotation (radians)
    Vector3 _axis;               // world axis of the active ring
    Vector3 _a0;                 // last in-plane vector from the origin (plane mode)
    Vector3 _arcStart;           // fixed in-plane start vector (drawing)
    bool _planeMode;
    Vector2 _centerScreen, _lastMouse;
    CoreQuat _startLocalRot;
    CoreQuat _parentRot;
    Vector3 _startPos;
    float _axisS0;
    Vector3 _planeHit0;
    Vector3 _dragEuler;
    CharacterView? _dragView;
    string _dragBoneId = "", _dragControlId = "";
    Blazma.Core.Mathematics.Transform _startParentModel, _startBoneModel;

    public event Action? Changed;
    /// <summary>Raised after a drag was committed or rolled back (history and menus need a refresh).</summary>
    public event Action? DragEnded;
    /// <summary>Raised when a rotate is attempted on a bone driven by a pinned IK chain: (view, boneIndex).</summary>
    public event Action<CharacterView, int>? DrivenBlocked;

    static AppController App => AppController.Instance;
    Camera3D Cam => _o.Cam;

    public Gizmo(SkeletonOverlay overlay) { _o = overlay; }

    // ---- target resolution -----------------------------------------------------------------------------------------------------------

    public Kind Resolve()
    {
        if (Dragging) return Current;
        Current = Kind.None;
        _view = null; _bone = -1; _control = -1;
        var sel = _o.Sel;
        var view = _o.Layer.View(sel.CharacterId);
        if (view is null || sel.Tool == StageTool.Select) return Current;
        var rig = view.Rig;

        if (sel.Tool == StageTool.Rotate && sel.BoneId is { } rb)
        {
            var bi = rig.Bones.FindIndex(b => b.Id.ToString() == rb);
            if (bi < 0 || bi >= view.Model.Length) return Current;
            _view = view; _bone = bi;
            _origin = view.Joints[bi];
            _boneRot = ToQuaternion(view.Model[bi].Rotation);
            Current = Kind.RotateBone;
        }
        else if (sel.Tool == StageTool.Move)
        {
            if (sel.ControlId is { } cid)
            {
                var ci = rig.Controls.FindIndex(c => c.Id.ToString() == cid);
                if (ci < 0 || ci >= view.ControlPositions.Length) return Current;
                _view = view; _control = ci; _origin = view.ControlPositions[ci];
                Current = Kind.MoveControl;
            }
            else if (sel.BoneId is { } mb)
            {
                var bi = rig.Bones.FindIndex(b => b.Id.ToString() == mb);
                if (bi < 0 || bi >= view.Model.Length) return Current;
                var chain = rig.IkChains.Find(c => c.End == rig.Bones[bi].Id);
                var ci = chain is null ? -1 : rig.Controls.FindIndex(c => c.Id == chain.Target);
                if (ci >= 0 && ci < view.ControlPositions.Length)
                {
                    _view = view; _control = ci; _origin = view.ControlPositions[ci];
                    Current = Kind.MoveControl;
                }
                else if (IsRootLike(rig, bi))
                {
                    _view = view; _bone = bi; _origin = view.Joints[bi];
                    Current = Kind.MoveBone;
                }
            }
        }
        return Current;
    }

    /// <summary>The bones whose translation can be edited: roots and the pelvis.</summary>
    public static bool IsRootLike(Rig rig, int boneIndex)
    {
        var b = rig.Bones[boneIndex];
        var std = rig.StandardNameOf(b.Id);
        return b.Parent.IsEmpty || std is HumanoidStandard.Pelvis or HumanoidStandard.Root;
    }

    // ---- geometry ----------------------------------------------------------------------------------------------------------------------

    static Quaternion ToQuaternion(CoreQuat q) => new((float)q.X, (float)q.Y, (float)q.Z, (float)q.W);

    Vector3 CameraForward => -Cam.GlobalBasis.Z;

    float Radius => RingPixels * _o.WorldPerPixel(_origin);

    Vector3 RingAxis(int k) => k switch
    {
        0 => _boneRot * Vector3.Right,
        1 => _boneRot * Vector3.Up,
        2 => _boneRot * Vector3.Back,
        _ => CameraForward,
    };

    static void Perp(Vector3 n, out Vector3 u, out Vector3 v)
    {
        var helper = Mathf.Abs(n.Y) < 0.9f ? Vector3.Up : Vector3.Right;
        u = n.Cross(helper).Normalized();
        v = n.Cross(u).Normalized();
    }

    Color AxisColor(int k) => k switch { 0 => _o.Pal.X, 1 => _o.Pal.Y, 2 => _o.Pal.Z, _ => _o.Pal.Text };

    bool RingPoints(int k, Vector2[] screen, bool[] front)
    {
        var axis = RingAxis(k);
        Perp(axis, out var u, out var v);
        var r = Radius * (k == 3 ? 1.16f : 1f);
        var camPos = Cam.GlobalPosition;
        for (var j = 0; j <= Segments; j++)
        {
            var t = j * Mathf.Tau / Segments;
            var dir = u * Mathf.Cos(t) + v * Mathf.Sin(t);
            var p = _origin + dir * r;
            if (!_o.Project(p, out screen[j])) return false;
            front[j] = k == 3 || dir.Dot(camPos - p) > 0;
        }
        return true;
    }

    readonly Vector2[][] _ringScreen = { new Vector2[Segments + 1], new Vector2[Segments + 1], new Vector2[Segments + 1], new Vector2[Segments + 1] };
    readonly bool[][] _ringFront = { new bool[Segments + 1], new bool[Segments + 1], new bool[Segments + 1], new bool[Segments + 1] };

    // ---- hit testing ----------------------------------------------------------------------------------------------------------------------

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
        if (kind == Kind.RotateBone)
        {
            var best = -1; var bestD = HitPixels;
            for (var k = 0; k < 4; k++)
            {
                if (!RingPoints(k, _ringScreen[k], _ringFront[k])) continue;
                var d = float.MaxValue;
                for (var j = 0; j < Segments; j++)
                {
                    // Only the near half is grabbable, like most gizmos, so the far side never steals clicks.
                    if (!_ringFront[k][j]) continue;
                    d = Mathf.Min(d, DistToSegment(mouse, _ringScreen[k][j], _ringScreen[k][j + 1]));
                }
                // View ring wins ties because it is the outermost.
                if (d < bestD) { bestD = d; best = k; }
            }
            return best;
        }
        if (!_o.Project(_origin, out var c)) return -1;
        if (mouse.DistanceTo(c) <= 10f) return 3;
        var bestA = -1; var bestAD = HitPixels;
        for (var k = 0; k < 3; k++)
        {
            var tip = c;
            if (!_o.Project(_origin + MoveAxis(k) * (ArrowPixels * _o.WorldPerPixel(_origin)), out tip)) continue;
            var d = DistToSegment(mouse, c, tip);
            if (d < bestAD) { bestAD = d; bestA = k; }
        }
        return bestA;
    }

    static Vector3 MoveAxis(int k) => k switch { 0 => Vector3.Right, 1 => Vector3.Up, _ => Vector3.Back };

    /// <summary>Updates the hovered handle; returns true if it changed.</summary>
    public bool UpdateHover(Vector2 mouse)
    {
        var h = HitTest(mouse);
        if (h == HoverHandle) return false;
        HoverHandle = h;
        return true;
    }

    // ---- drag ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Start a drag if a handle is under <paramref name="mouse"/>. Returns true if the click was consumed.</summary>
    public bool TryBegin(Vector2 mouse)
    {
        var handle = HitTest(mouse);
        if (handle < 0) return false;
        var view = _view!;
        var character = view.Character;
        var rig = view.Rig;
        _dragView = view;
        _handle = handle;
        _lastMouse = mouse;
        _angle = 0;
        _label = "";
        _startLocalRot = default;

        if (Current == Kind.RotateBone)
        {
            var bone = rig.Bones[_bone];
            if (PoseSolver.DrivingChain(rig, character.Pose, bone.Id) is not null)
            {
                DrivenBlocked?.Invoke(view, _bone);
                return true; // swallow the click: nothing to drag
            }
            _dragBoneId = bone.Id.ToString();
            _startLocalRot = character.Pose.LocalOf(bone).Rotation;
            var parent = view.Evaluator.ParentOf(_bone);
            _parentRot = parent < 0 ? CoreQuat.Identity : view.Model[parent].Rotation;
            _axis = RingAxis(handle);
            if (!_o.Project(_origin, out _centerScreen)) return true;
            var ray = (o: Cam.ProjectRayOrigin(mouse), d: Cam.ProjectRayNormal(mouse));
            var denom = ray.d.Dot(_axis);
            _planeMode = Mathf.Abs(denom) > 0.25f;
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
            _scope = App.BeginTransaction("Rotate bone");
        }
        else
        {
            var ray = (o: Cam.ProjectRayOrigin(mouse), d: Cam.ProjectRayNormal(mouse));
            _startPos = _origin;
            if (Current == Kind.MoveControl)
            {
                _dragControlId = rig.Controls[_control].Id.ToString();
                _scope = App.BeginTransaction("Move IK control");
            }
            else
            {
                var bone = rig.Bones[_bone];
                _dragBoneId = bone.Id.ToString();
                var local = character.Pose.LocalOf(bone);
                _dragEuler = ToDeg(local.Rotation.ToEulerXYZ());
                var parent = view.Evaluator.ParentOf(_bone);
                _startParentModel = parent < 0 ? Blazma.Core.Mathematics.Transform.Identity : view.Model[parent];
                _startBoneModel = view.Model[_bone];
                _scope = App.BeginTransaction("Move bone");
            }
            if (handle < 3) _axisS0 = AxisParam(_startPos, MoveAxis(handle), ray.o, ray.d);
            else _planeHit0 = PlaneHit(_startPos, ray.o, ray.d) ?? _startPos;
        }
        Changed?.Invoke();
        return true;
    }

    static Vector3 ToDeg(Vec3 radians) => new((float)(radians.X * 180 / Math.PI), (float)(radians.Y * 180 / Math.PI), (float)(radians.Z * 180 / Math.PI));

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

    /// <summary>Continue a drag with the current mouse position.</summary>
    public void Update(Vector2 mouse)
    {
        if (!Dragging || _dragView is null) return;
        var ray = (o: Cam.ProjectRayOrigin(mouse), d: Cam.ProjectRayNormal(mouse));
        if (Current == Kind.RotateBone) UpdateRotate(mouse, ray.o, ray.d);
        else UpdateMove(ray.o, ray.d);
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
            var step = Mathf.Atan2(_axis.Dot(_a0.Cross(a1)), _a0.Dot(a1));
            _angle += step;
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

        var view = _dragView!;
        var rig = view.Rig;
        var qLocal = _handle < 3
            ? _startLocalRot * CoreQuat.FromAxisAngle(_handle switch { 0 => Vec3.UnitX, 1 => Vec3.UnitY, _ => Vec3.UnitZ }, _angle)
            : (_parentRot.Inverse() * CoreQuat.FromAxisAngle(new Vec3(_axis.X, _axis.Y, _axis.Z), _angle) * _parentRot * _startLocalRot);
        var e = qLocal.Normalized().ToEulerXYZ() * (180 / Math.PI);
        var r = App.Execute("pose.set_bone", new JsonObject
        {
            ["characterId"] = view.CharacterId, ["bone"] = _dragBoneId,
            ["rx"] = e.X, ["ry"] = e.Y, ["rz"] = e.Z,
        });
        var deg = _angle * 180 / Mathf.Pi;
        _label = string.Format(CultureInfo.InvariantCulture, "{0:+0.0;-0.0;0.0}°", deg);
        if (!r.Ok) { Cancel(); return; }
        if (r.Warnings.Any(w => w.Code == "clamped_by_limits")) _label += "  " + TranslationServer.Translate("(joint limit)");
    }

    void UpdateMove(Vector3 ro, Vector3 rd)
    {
        Vector3 pos;
        if (_handle < 3)
        {
            var axis = MoveAxis(_handle);
            pos = _startPos + axis * (AxisParam(_startPos, axis, ro, rd) - _axisS0);
        }
        else
        {
            if (PlaneHit(_startPos, ro, rd) is not { } hit) return;
            pos = _startPos + (hit - _planeHit0);
        }
        var view = _dragView!;
        JsonObject args;
        if (Current == Kind.MoveControl)
        {
            args = new JsonObject
            {
                ["characterId"] = view.CharacterId, ["control"] = _dragControlId,
                ["x"] = (double)pos.X, ["y"] = (double)pos.Y, ["z"] = (double)pos.Z,
            };
            App.Execute("pose.set_control", args);
        }
        else
        {
            var local = RigEvaluator.LocalFromModel(_startParentModel,
                _startBoneModel with { Translation = new Vec3(pos.X, pos.Y, pos.Z) });
            args = new JsonObject
            {
                ["characterId"] = view.CharacterId, ["bone"] = _dragBoneId,
                ["rx"] = (double)_dragEuler.X, ["ry"] = (double)_dragEuler.Y, ["rz"] = (double)_dragEuler.Z,
                ["tx"] = local.Translation.X, ["ty"] = local.Translation.Y, ["tz"] = local.Translation.Z,
            };
            App.Execute("pose.set_bone", args);
        }
        var d = pos - _startPos;
        _label = string.Format(CultureInfo.InvariantCulture, "{0:+0.000;-0.000;0.000}  {1:+0.000;-0.000;0.000}  {2:+0.000;-0.000;0.000}" + Unit.M, d.X, d.Y, d.Z);
    }

    /// <summary>Finish the drag. The whole drag is one undo step.</summary>
    public void End(bool commit)
    {
        var scope = _scope;
        _scope = null;
        _handle = -1;
        _label = "";
        if (scope is not null)
        {
            // With auto-key on, the drag and its key are one undo step.
            if (commit && _dragView is { } dv)
                App.AutoKeyInOpenTransaction(dv.CharacterId, Current == Kind.RotateBone || Current == Kind.MoveBone ? _dragBoneId : null,
                    Current == Kind.MoveControl ? _dragControlId : null);
            if (commit) scope.Commit(); else scope.Rollback();
            DragEnded?.Invoke();
        }
        Changed?.Invoke();
    }

    public void Cancel() => End(false);

    // ---- test hooks (the self-test drives the real input path with points computed from the real geometry) -----------------

    /// <summary>
    /// Screen points along a rotate ring in the direction of positive rotation, starting at the sample facing the camera.
    /// Point i is exactly <c>i * 360 / 56</c> degrees from point 0.
    /// </summary>
    public Vector2[]? RingPath(int ring, int steps)
    {
        if (Resolve() != Kind.RotateBone) return null;
        var axis = RingAxis(ring);
        Perp(axis, out var u, out var v);
        var r = Radius * (ring == 3 ? 1.16f : 1f);
        var camPos = Cam.GlobalPosition;
        var bestJ = 0; var bestScore = float.MinValue;
        for (var j = 0; j < Segments; j++)
        {
            var t = j * Mathf.Tau / Segments;
            var p = _origin + (u * Mathf.Cos(t) + v * Mathf.Sin(t)) * r;
            var score = (camPos - p).Normalized().Dot((p - _origin).Normalized());
            if (score > bestScore) { bestScore = score; bestJ = j; }
        }
        var pts = new Vector2[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            var t = (bestJ + i) * Mathf.Tau / Segments;
            if (!_o.Project(_origin + (u * Mathf.Cos(t) + v * Mathf.Sin(t)) * r, out pts[i])) return null;
        }
        return pts;
    }

    public const float DegreesPerRingSample = 360f / Segments;

    /// <summary>Screen point on a move axis at <paramref name="metres"/> from the gizmo origin (axis 0..2).</summary>
    public Vector2? AxisPoint(int axis, float metres)
    {
        var kind = Resolve();
        if (kind is Kind.None or Kind.RotateBone) return null;
        return _o.Project(_origin + MoveAxis(axis) * metres, out var s) ? s : null;
    }

    /// <summary>Length of the move arrows in metres at the current zoom.</summary>
    public float ArrowMetres => ArrowPixels * _o.WorldPerPixel(_origin);

    // ---- drawing ----------------------------------------------------------------------------------------------------------------------------

    public void Draw(CanvasItem ci, Font font)
    {
        var kind = Resolve();
        if (kind == Kind.None) return;
        var p = _o.Pal;
        if (kind == Kind.RotateBone) DrawRotate(ci, font, p);
        else DrawMove(ci, font, p);
    }

    void DrawRotate(CanvasItem ci, Font font, Palette p)
    {
        if (!_o.Project(_origin, out var center)) return;
        // Dim disc so the rings read on any background.
        ci.DrawCircle(center, RingPixels * 1.02f, p.Bg with { A = 0.12f });
        var active = Dragging ? _handle : HoverHandle;
        for (var k = 3; k >= 0; k--)
        {
            if (!RingPoints(k, _ringScreen[k], _ringFront[k])) continue;
            var col = AxisColor(k);
            var highlighted = active == k;
            var w = highlighted ? 3.6f : 2.4f;
            var s = _ringScreen[k];
            var f = _ringFront[k];
            // Segments are split into runs of the same facing so the far half can be drawn dimmer.
            var start = 0;
            for (var seg = 0; seg < Segments; seg++)
            {
                var last = seg + 1 == Segments;
                if (!last && f[seg + 1] == f[start]) continue;
                var run = new Vector2[seg + 2 - start];
                Array.Copy(s, start, run, 0, run.Length);
                var c = f[start] ? col : col with { A = 0.32f };
                if (!highlighted && Dragging) c = c with { A = c.A * 0.45f };
                ci.DrawPolyline(run, highlighted ? col.Lightened(0.15f) : c, w, true);
                start = seg + 1;
            }
        }

        if (Dragging && _handle >= 0 && Mathf.Abs(_angle) > 1e-4f)
        {
            // Wedge from the start direction to the current angle, in the ring's plane.
            var axis = _axis;
            var u = _arcStart.Normalized();
            var v = axis.Cross(u).Normalized();
            var r = Radius * (_handle == 3 ? 1.16f : 1f);
            var pts = new List<Vector2> { center };
            var steps = Math.Max(2, (int)(Mathf.Abs(_angle) / 0.12f));
            var ok = true;
            for (var i = 0; i <= steps; i++)
            {
                var a = _angle * i / steps;
                if (!_o.Project(_origin + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r, out var sp)) { ok = false; break; }
                pts.Add(sp);
            }
            if (ok && pts.Count >= 3 && Mathf.Abs(_angle) < Mathf.Tau - 0.01f)
            {
                ci.DrawColoredPolygon(pts.ToArray(), AxisColor(_handle) with { A = 0.30f });
            }
        }
        ci.DrawCircle(center, 3f, p.Text);
        if (Dragging && _label.Length > 0) DrawLabel(ci, font, p, center + new Vector2(RingPixels + 14, -RingPixels * 0.85f), _label);
    }

    void DrawMove(CanvasItem ci, Font font, Palette p)
    {
        if (!_o.Project(_origin, out var c)) return;
        var active = Dragging ? _handle : HoverHandle;
        var len = ArrowPixels * _o.WorldPerPixel(_origin);
        for (var k = 0; k < 3; k++)
        {
            if (!_o.Project(_origin + MoveAxis(k) * len, out var tip)) continue;
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
        var style = BlazmaTheme.Box(p.Raised, p.LineStrong, 6, 1, 0, 0, 0, 0);
        ci.DrawStyleBox(style, rect);
        ci.DrawString(font, at + new Vector2(0, 0), text, HorizontalAlignment.Left, -1, size, p.Text);
    }
}
