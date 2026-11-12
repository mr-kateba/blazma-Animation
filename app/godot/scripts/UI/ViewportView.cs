using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// The 3D stage: own World3D, ground grid, soft light and an orbit camera.
/// Mouse: MMB drag or Alt + LMB drag = orbit, Shift + that = pan, wheel = zoom. Hold RMB = fly (mouse look, W A S D Q E, Shift fast, Ctrl slow,
/// wheel = speed). F / numpad . = frame selection, Home = frame all, numpad 1 / 3 / 7 = front / side / top (Ctrl = opposite), numpad 5 = ortho.
/// Always left-to-right, even when the rest of the UI is mirrored for Arabic.
/// </summary>
public partial class ViewportView : SubViewportContainer
{
    public const float DefaultYaw = 35f, DefaultPitch = -22f, DefaultDistance = 8f;
    public static readonly Vector3 DefaultTarget = new(0, 0.9f, 0);

    readonly SubViewport _vp = new();
    readonly Camera3D _cam = new();
    readonly MeshInstance3D _grid = new();
    readonly WorldEnvironment _env = new();
    readonly DirectionalLight3D _sun = new();
    Godot.Environment _environment = new();

    float _yaw = DefaultYaw, _pitch = DefaultPitch, _distance = DefaultDistance;
    Vector3 _target = DefaultTarget;

    static AppSettings Cfg => AppController.Instance.Settings;
    bool _flying, _ortho;
    readonly HashSet<Key> _flyKeys = new();
    Input.MouseModeEnum _prevMouseMode = Input.MouseModeEnum.Visible;

    /// <summary>Right mouse button held: mouse-look and keyboard flight are active.</summary>
    public bool Flying => _flying;
    public bool IsOrtho => _ortho;
    /// <summary>Short text for the on-screen camera notice (fly speed, projection).</summary>
    public event Action<string>? Notice;

    public Camera3D Camera => _cam;
    /// <summary>Root of the stage's own 3D world (characters are added here).</summary>
    public Node World => _vp;
    public SubViewport Viewport3D => _vp;
    /// <summary>Receives left-button / key input before the camera controls (picking, gizmo drags).</summary>
    public IStageInput? Tools { get; set; }
    public float Yaw => _yaw;
    public float Pitch => _pitch;
    public float Distance => _distance;
    public Vector3 Target => _target;

    public ViewportView()
    {
        Stretch = true;
        FocusMode = FocusModeEnum.Click;
        LayoutDirection = LayoutDirectionEnum.Ltr; // never mirrored
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _Ready()
    {
        _vp.OwnWorld3D = true;
        _vp.Msaa3D = Viewport.Msaa.Msaa4X;
        _vp.HandleInputLocally = false;
        _vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        AddChild(_vp);

        _environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightEnergy = 0.8f,
        };
        _env.Environment = _environment;
        _vp.AddChild(_env);

        _sun.RotationDegrees = new Vector3(-48, -35, 0);
        _sun.LightEnergy = 0.9f;
        _sun.ShadowEnabled = false;
        _vp.AddChild(_sun);

        _cam.Fov = 45;
        _cam.Near = 0.05f;
        _cam.Far = 500;
        _vp.AddChild(_cam);

        _grid.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _vp.AddChild(_grid);

        ApplyCameraSettings();
        MouseEntered += () => { if (!HasFocus() && GetViewport().GuiGetFocusOwner() is not LineEdit) GrabFocus(); };
    }

    public void ApplyPalette(Palette p)
    {
        _environment.BackgroundColor = p.Viewport;
        _environment.AmbientLightColor = p.Text2.Lerp(p.Viewport, 0.5f);
        _grid.Mesh = BuildGrid(p);
        _grid.MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            DisableFog = true,
        };
    }

    static ArrayMesh BuildGrid(Palette p)
    {
        const int half = 20;
        var verts = new List<Vector3>();
        var cols = new List<Color>();

        static float Fade(float a, float b) => Mathf.Pow(Mathf.Clamp(1f - Mathf.Sqrt(a * a + b * b) / half, 0f, 1f), 0.6f);

        void Line(Vector3 from, Vector3 to, Color color, float strength)
        {
            const int steps = 20;
            for (var i = 0; i < steps; i++)
            {
                var a = from.Lerp(to, i / (float)steps);
                var b = from.Lerp(to, (i + 1) / (float)steps);
                verts.Add(a); verts.Add(b);
                cols.Add(color with { A = Fade(a.X, a.Z) * strength });
                cols.Add(color with { A = Fade(b.X, b.Z) * strength });
            }
        }

        for (var i = -half; i <= half; i++)
        {
            if (i == 0) continue;
            var major = i % 5 == 0;
            var c = major ? p.GridAxis.Lerp(p.Text3, 0.45f) : p.Grid.Lerp(p.GridAxis, 0.55f);
            var s = major ? 1f : 0.9f;
            Line(new Vector3(i, 0, -half), new Vector3(i, 0, half), c, s);
            Line(new Vector3(-half, 0, i), new Vector3(half, 0, i), c, s);
        }
        Line(new Vector3(-half, 0, 0), new Vector3(half, 0, 0), p.X, 1.4f);  // X axis
        Line(new Vector3(0, 0, -half), new Vector3(0, 0, half), p.Z, 1.4f);  // Z axis

        var mesh = new ArrayMesh();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = cols.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
        return mesh;
    }

    // ---- camera ------------------------------------------------------------------------------

    public void ResetView()
    {
        _yaw = DefaultYaw; _pitch = DefaultPitch; _distance = DefaultDistance; _target = DefaultTarget;
        _ortho = false;
        ApplyCamera();
    }

    /// <summary>Apply FOV from the settings (30-100 degrees).</summary>
    public void ApplyCameraSettings()
    {
        _cam.Fov = Mathf.Clamp(Cfg.CameraFov, 30f, 100f);
        ApplyCamera();
    }

    /// <summary>Look at <paramref name="pivot"/> from where the camera is now; orbiting continues around it.</summary>
    public void SetPivot(Vector3 pivot)
    {
        var pos = _cam.Position;
        var d = pos - pivot;
        if (d.Length() < 0.05f) return;
        _distance = Mathf.Clamp(d.Length(), 0.6f, 120f);
        var n = d.Normalized();
        _pitch = Mathf.Clamp(-Mathf.RadToDeg(Mathf.Asin(n.Y)), -89.9f, 89.9f);
        _yaw = Mathf.RadToDeg(Mathf.Atan2(n.X, n.Z));
        _target = pivot;
        ApplyCamera();
    }

    /// <summary>Numpad views: yaw/pitch of the camera around the target. 0 = front (looks toward -Z), 1 = right side, 2 = top.</summary>
    public void SetView(int view, bool opposite = false)
    {
        (_yaw, _pitch) = view switch { 0 => (0f, 0f), 1 => (90f, 0f), _ => (0f, -89.9f) };
        if (opposite) { _yaw = view == 2 ? 0f : _yaw + 180f; _pitch = view == 2 ? 89.9f : 0f; }
        ApplyCamera();
    }

    public void ToggleOrtho()
    {
        _ortho = !_ortho;
        ApplyCamera();
        Notice?.Invoke(_ortho ? "Orthographic" : "Perspective");
    }

    /// <summary>Forward direction of the camera.</summary>
    public Vector3 Forward => -_cam.GlobalBasis.Z;

    /// <summary>Frame the whole scene (Home) or reset when it is empty; F / numpad . frame the selected bone, else the character.</summary>
    public void FrameAll()
    {
        if (Tools?.AllBounds() is { } box) Frame(box); else ResetView();
    }

    public void FrameFocus()
    {
        if (Tools?.SelectionPoint() is { } p) Frame(new Aabb(p - Vector3.One * 0.45f, Vector3.One * 0.9f));
        else if (Tools is null || !Tools.FrameSelection()) ResetView();
    }

    // ---- fly mode ------------------------------------------------------------------------------

    void BeginFly()
    {
        if (_flying) return;
        _flying = true;
        _ortho = false;
        _flyKeys.Clear();
        _prevMouseMode = Input.MouseMode;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        ApplyCamera();
        Notice?.Invoke("SPEED");
    }

    void EndFly()
    {
        if (!_flying) return;
        _flying = false;
        _flyKeys.Clear();
        Input.MouseMode = _prevMouseMode;
        Cfg.Save();
        ApplyCamera();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut || what == NotificationExitTree) EndFly();
    }

    /// <summary>Mouse look: rotate the camera in place (the orbit pivot keeps its distance in front of it).</summary>
    void Look(float dx, float dy)
    {
        var pos = _cam.Position;
        var sign = Cfg.CameraInvertY ? -1f : 1f;
        _yaw -= dx * 0.15f;
        _pitch = Mathf.Clamp(_pitch - dy * 0.15f * sign, -89f, 89f);
        var yaw = Mathf.DegToRad(_yaw);
        var pitch = Mathf.DegToRad(_pitch);
        var dir = new Vector3(Mathf.Cos(pitch) * Mathf.Sin(yaw), -Mathf.Sin(pitch), Mathf.Cos(pitch) * Mathf.Cos(yaw));
        _target = pos - dir * _distance;
        ApplyCamera();
    }

    public override void _Process(double delta)
    {
        if (!_flying) return;
        var move = Vector3.Zero;
        if (_flyKeys.Contains(Key.W)) move += Forward;
        if (_flyKeys.Contains(Key.S)) move -= Forward;
        if (_flyKeys.Contains(Key.D)) move += _cam.GlobalBasis.X;
        if (_flyKeys.Contains(Key.A)) move -= _cam.GlobalBasis.X;
        if (_flyKeys.Contains(Key.E)) move += Vector3.Up;
        if (_flyKeys.Contains(Key.Q)) move -= Vector3.Up;
        if (move == Vector3.Zero) return;
        var speed = Cfg.CameraFlySpeed * (_flyKeys.Contains(Key.Shift) ? 3f : 1f) * (_flyKeys.Contains(Key.Ctrl) ? 0.25f : 1f);
        _target += move.Normalized() * speed * (float)delta;
        ApplyCamera();
    }

    /// <summary>While flying every event is taken here, before shortcuts and text fields see it.</summary>
    public override void _Input(InputEvent e)
    {
        if (!_flying) return;
        switch (e)
        {
            case InputEventMouseMotion mm:
                Look(mm.Relative.X, mm.Relative.Y);
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false }:
                EndFly();
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } mb:
                var steps = mb.Factor > 0 ? mb.Factor : 1;
                Cfg.CameraFlySpeed = Mathf.Clamp(Cfg.CameraFlySpeed * Mathf.Pow(1.2f, mb.ButtonIndex == MouseButton.WheelUp ? steps : -steps), 0.5f, 40f);
                Notice?.Invoke("SPEED");
                GetViewport().SetInputAsHandled();
                break;
            case InputEventKey k:
                var key = k.Keycode != Key.None ? k.Keycode : k.PhysicalKeycode;
                if (k.Pressed) _flyKeys.Add(key); else _flyKeys.Remove(key);
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    /// <summary>Move the camera so that <paramref name="box"/> fills the view, keeping the current orbit angles.</summary>
    public void Frame(Aabb box)
    {
        _target = box.GetCenter();
        var radius = Mathf.Max(box.Size.Length() * 0.5f, 0.3f);
        var halfFov = Mathf.DegToRad(_cam.Fov) * 0.5f;
        _distance = Mathf.Clamp(radius / Mathf.Sin(halfFov) * 0.92f, 0.6f, 120f);
        ApplyCamera();
    }

    public void Orbit(float dxPixels, float dyPixels)
    {
        _yaw -= dxPixels * 0.4f;
        _pitch = Mathf.Clamp(_pitch - dyPixels * 0.4f, -89f, 89f);
        ApplyCamera();
    }

    public void Pan(float dxPixels, float dyPixels)
    {
        var k = _distance * 0.0016f;
        _target += -_cam.GlobalBasis.X * dxPixels * k + _cam.GlobalBasis.Y * dyPixels * k;
        ApplyCamera();
    }

    public void Zoom(float steps)
    {
        _distance = Mathf.Clamp(_distance * Mathf.Pow(0.9f, steps), 0.6f, 120f);
        ApplyCamera();
    }

    void ApplyCamera()
    {
        var yaw = Mathf.DegToRad(_yaw);
        var pitch = Mathf.DegToRad(_pitch);
        var dir = new Vector3(Mathf.Cos(pitch) * Mathf.Sin(yaw), -Mathf.Sin(pitch), Mathf.Cos(pitch) * Mathf.Cos(yaw));
        _cam.Position = _target + dir * _distance;
        _cam.LookAt(_target, Vector3.Up);
        if (_ortho)
        {
            _cam.Projection = Camera3D.ProjectionType.Orthogonal;
            _cam.Size = 2f * _distance * Mathf.Tan(Mathf.DegToRad(_cam.Fov) * 0.5f);
        }
        else _cam.Projection = Camera3D.ProjectionType.Perspective;
    }

    void PivotFromSelection()
    {
        if (Cfg.CameraOrbitSelection && Tools?.SelectionPoint() is { } p) SetPivot(p);
    }

    public override void _GuiInput(InputEvent e)
    {
        // Take focus first so a half-typed inspector value is committed before a click changes the selection.
        if (e is InputEventMouseButton { Pressed: true }) GrabFocus();
        if (Tools is not null && Tools.HandleInput(e)) { AcceptEvent(); return; }
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }:
                BeginFly();
                AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: true } mb:
                if (mb.ButtonIndex == MouseButton.WheelUp) Zoom(mb.Factor > 0 ? mb.Factor : 1);
                else if (mb.ButtonIndex == MouseButton.WheelDown) Zoom(-(mb.Factor > 0 ? mb.Factor : 1));
                else if (mb.ButtonIndex == MouseButton.Middle || (mb.ButtonIndex == MouseButton.Left && mb.AltPressed)) PivotFromSelection();
                GrabFocus();
                break;
            case InputEventMouseMotion mm:
                var orbitOrPan = (mm.ButtonMask & MouseButtonMask.Middle) != 0 || ((mm.ButtonMask & MouseButtonMask.Left) != 0 && mm.AltPressed);
                if (!orbitOrPan) break;
                if (mm.ShiftPressed) Pan(mm.Relative.X, mm.Relative.Y);
                else Orbit(mm.Relative.X, mm.Relative.Y);
                AcceptEvent();
                break;
        }
    }
}

/// <summary>Viewport tools (selection, gizmos). Return true from <see cref="HandleInput"/> to consume the event.</summary>
public interface IStageInput
{
    bool HandleInput(InputEvent e);
    /// <summary>F: frame the selected character. Returns false if there is none (the view is reset instead).</summary>
    bool FrameSelection();
    /// <summary>World position of the selected bone, if any.</summary>
    Vector3? SelectionPoint();
    /// <summary>Bounds of every character.</summary>
    Aabb? AllBounds();
}

/// <summary>Small X/Y/Z orientation readout drawn in a corner of the stage.</summary>
public partial class AxisGizmo : Control
{
    Camera3D? _cam;
    Palette _p = Palette.Dark;
    Basis _last;

    public AxisGizmo()
    {
        CustomMinimumSize = new Vector2(76, 76);
        MouseFilter = MouseFilterEnum.Ignore;
        LayoutDirection = LayoutDirectionEnum.Ltr;
    }

    public void Bind(Camera3D cam, Palette p) { _cam = cam; _p = p; QueueRedraw(); }

    public override void _Process(double delta)
    {
        if (_cam is null) return;
        if (_cam.GlobalBasis != _last) { _last = _cam.GlobalBasis; QueueRedraw(); }
    }

    public override void _Draw()
    {
        if (_cam is null) return;
        var c = Size / 2 + new Vector2(0, 2);
        const float len = 26;
        var basis = _cam.GlobalBasis;
        var axes = new (Vector3 v, Color col, string name)[]
        {
            (Vector3.Right, _p.X, "X"), (Vector3.Up, _p.Y, "Y"), (Vector3.Back, _p.Z, "Z"),
        };
        // draw far axes first
        var sorted = axes.OrderBy(a => -(basis.Z.Dot(a.v))).ToArray();
        var font = BlazmaTheme.Mono;
        foreach (var a in sorted)
        {
            var d = new Vector2(basis.X.Dot(a.v), -basis.Y.Dot(a.v)) * len;
            DrawLine(c, c + d, a.col, 2f, true);
            DrawCircle(c + d, 3f, a.col);
            var tp = c + d * 1.0f + new Vector2(-4, 4) + d.Normalized() * 8f;
            DrawString(font, tp, a.name, HorizontalAlignment.Left, -1, 11, a.col);
        }
    }
}
