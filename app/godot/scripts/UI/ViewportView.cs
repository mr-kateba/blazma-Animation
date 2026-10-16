using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// The 3D stage: own World3D, ground grid, soft light and an orbit camera.
/// Mouse: MMB or RMB drag = orbit, Shift+MMB (or Shift+RMB) = pan, wheel = zoom, F = reset.
/// Always left-to-right, even when the rest of the UI is mirrored for Arabic.
/// </summary>
public partial class ViewportView : SubViewportContainer
{
    const float DefaultYaw = 35f, DefaultPitch = -22f, DefaultDistance = 8f;
    static readonly Vector3 DefaultTarget = new(0, 0.9f, 0);

    readonly SubViewport _vp = new();
    readonly Camera3D _cam = new();
    readonly MeshInstance3D _grid = new();
    readonly WorldEnvironment _env = new();
    readonly DirectionalLight3D _sun = new();
    Godot.Environment _environment = new();

    float _yaw = DefaultYaw, _pitch = DefaultPitch, _distance = DefaultDistance;
    Vector3 _target = DefaultTarget;

    public Camera3D Camera => _cam;
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

        ApplyCamera();
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
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true } mb:
                if (mb.ButtonIndex == MouseButton.WheelUp) Zoom(mb.Factor > 0 ? mb.Factor : 1);
                else if (mb.ButtonIndex == MouseButton.WheelDown) Zoom(-(mb.Factor > 0 ? mb.Factor : 1));
                GrabFocus();
                break;
            case InputEventMouseMotion mm:
                var orbitOrPan = (mm.ButtonMask & (MouseButtonMask.Middle | MouseButtonMask.Right)) != 0;
                if (!orbitOrPan) break;
                if (mm.ShiftPressed) Pan(mm.Relative.X, mm.Relative.Y);
                else Orbit(mm.Relative.X, mm.Relative.Y);
                AcceptEvent();
                break;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.F, CtrlPressed: false, AltPressed: false }:
                ResetView();
                AcceptEvent();
                break;
        }
    }
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
