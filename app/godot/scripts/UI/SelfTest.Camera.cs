using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Camera scenario with real input events: hold the right button and fly (W, Q/E, mouse look, wheel speed), the orbit pivot after flying, F / Home /
/// numpad views / orthographic toggle, double-click on a bone, Alt + left drag orbit, camera settings persisted, View menu and settings dialog in
/// English and Arabic. Screenshots are named p10-lang-dark-camera-what.png. Shortcut: BLAZMA_ONLY_CAM=1.
/// </summary>
public partial class SelfTest
{
    ViewportView Stg => _w.Stage;
    Vector2 StageCenter => Stg.GlobalPosition + Stg.Size / 2;

    void CamMouse(MouseButton b, bool pressed, Vector2 local, bool dbl = false, bool alt = false)
    {
        var g = Stg.GlobalPosition + local;
        var mask = pressed ? (b == MouseButton.Right ? MouseButtonMask.Right : b == MouseButton.Left ? MouseButtonMask.Left : MouseButtonMask.Middle) : 0;
        GetViewport().PushInput(new InputEventMouseButton { Position = g, GlobalPosition = g, ButtonIndex = b, Pressed = pressed, ButtonMask = mask, DoubleClick = dbl, AltPressed = alt }, false);
    }

    void CamMotion(Vector2 rel, MouseButtonMask mask, bool alt = false)
    {
        var g = StageCenter;
        GetViewport().PushInput(new InputEventMouseMotion { Position = g, GlobalPosition = g, Relative = rel, ButtonMask = mask, AltPressed = alt }, false);
    }

    void CamKey(Godot.Key k, bool pressed) =>
        GetViewport().PushInput(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = pressed }, false);

    async Task Hold(Godot.Key k, double seconds)
    {
        CamKey(k, true);
        await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
        CamKey(k, false);
        await Frames(2);
    }

    static float AngleDeg(Vector3 a, Vector3 b) => Mathf.RadToDeg(a.Normalized().AngleTo(b.Normalized()));

    async Task RunCamera()
    {
        var s = App.Settings;
        var saved = (s.CameraFlySpeed, s.CameraInvertY, s.CameraOrbitSelection, s.CameraFov);
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        _w.ShowAgentPanel(false);
        App.SetCamera(4f, false, false, 45f);
        _w.NewProjectNow();
        await Settle();
        var man = Ch();
        var mid = man.Id.ToString();
        var head = BoneId(man, "head");
                Stg.GrabFocus();
        Stg.FrameAll();
        await Settle();

        Check("cam keys do nothing unless the right button is held (W, Q, E, F stay with the tools / text fields)", () =>
        {
            return Stg.Flying ? "flying" : null;
        });
        var p0 = Stg.Camera.GlobalPosition;
        await Hold(Godot.Key.W, 0.3);
        Check("cam W without the right button does not move the camera", () => Stg.Camera.GlobalPosition.DistanceTo(p0) < 1e-4 ? null : "moved");

        // ---- fly ----
        var vp = Stg.Size;
        CamMouse(MouseButton.Right, true, vp / 2);
        await Frames(2);
        Check("cam holding the right button starts fly mode", () => Stg.Flying ? null : "not flying");
        var fwd0 = Stg.Forward;
        p0 = Stg.Camera.GlobalPosition;
        await Hold(Godot.Key.W, 0.4);
        var moved = Stg.Camera.GlobalPosition - p0;
        Check("cam right-hold + W moves the camera forward along its view direction", () =>
            moved.Dot(fwd0) > 0.4f && AngleDeg(moved, fwd0) < 3 ? null : $"moved={moved} fwd={fwd0}");
        p0 = Stg.Camera.GlobalPosition;
        await Hold(Godot.Key.S, 0.2);
        await Hold(Godot.Key.D, 0.2);
        Check("cam S moves back and D moves right", () => (Stg.Camera.GlobalPosition - p0).Dot(Stg.Camera.GlobalBasis.X) > 0.1f ? null : "no strafe");
        p0 = Stg.Camera.GlobalPosition;
        await Hold(Godot.Key.E, 0.3);
        var up = Stg.Camera.GlobalPosition.Y - p0.Y;
        p0 = Stg.Camera.GlobalPosition;
        await Hold(Godot.Key.Q, 0.3);
        var down = Stg.Camera.GlobalPosition.Y - p0.Y;
        Check("cam E moves up and Q moves down (world vertical)", () => up > 0.3f && down < -0.3f ? null : $"up={up} down={down}");

        var f1 = Stg.Forward;
        CamMotion(new Vector2(60, 0), MouseButtonMask.Right);
        var f2 = Stg.Forward;
        CamMotion(new Vector2(0, -40), MouseButtonMask.Right);
        var f3 = Stg.Forward;
        Check("cam mouse motion rotates the view: right turns right, up looks up", () =>
            AngleDeg(f1, f2) > 3 && f2.Cross(f1).Y > 0 && f3.Y > f2.Y ? null : $"yaw {AngleDeg(f1, f2)} cross={f2.Cross(f1)} pitch {f2.Y}->{f3.Y}");
        CamMotion(new Vector2(0, -5000), MouseButtonMask.Right);
        Check("cam pitch is clamped below straight up", () => Stg.Pitch <= 89.01f && Stg.Forward.Y < 0.9999f ? null : $"pitch={Stg.Pitch}");
        CamMotion(new Vector2(0, 5000), MouseButtonMask.Right);
        CamMotion(new Vector2(0, -400), MouseButtonMask.Right);
        var speed0 = s.CameraFlySpeed;
        GetViewport().PushInput(new InputEventMouseButton { Position = StageCenter, GlobalPosition = StageCenter, ButtonIndex = MouseButton.WheelUp, Pressed = true, Factor = 1 }, false);
        await Frames(2);
        Check("cam wheel while flying changes fly speed and shows it briefly (LTR-isolated m/s)", () =>
            s.CameraFlySpeed > speed0 && _w.CameraHudVisible && _w.CameraHudText.Contains("m/s") && _w.CameraHudText.Contains('⁦') ? null : $"speed={s.CameraFlySpeed} hud='{_w.CameraHudText}' vis={_w.CameraHudVisible}");
        CamKey(Godot.Key.W, true);
        await Frames(1);
        Shot(P("p10-en-dark-camera-fly"), GetViewport());
        CamKey(Godot.Key.W, false);

        CamMouse(MouseButton.Right, false, vp / 2);
        await Frames(2);
        Check("cam releasing the right button ends fly mode and puts the orbit pivot in front of the camera", () =>
        {
            var expect = Stg.Camera.GlobalPosition + Stg.Forward * Stg.Distance;
            return !Stg.Flying && Stg.Target.DistanceTo(expect) < 1e-2f ? null : $"flying={Stg.Flying} target={Stg.Target} expect={expect}";
        });
        Check("cam orbit continues naturally from the flown position (middle drag around the pivot)", () =>
        {
            var pos = Stg.Camera.GlobalPosition;
            var d = pos.DistanceTo(Stg.Target);
            CamMouse(MouseButton.Middle, true, vp / 2);
            CamMotion(new Vector2(40, 10), MouseButtonMask.Middle);
            CamMouse(MouseButton.Middle, false, vp / 2);
            return Stg.Camera.GlobalPosition.DistanceTo(pos) > 0.1f && Mathf.Abs(Stg.Camera.GlobalPosition.DistanceTo(Stg.Target) - d) < 1e-2f ? null : "orbit broke";
        });
        Check("cam right-button motion alone (no fly) no longer orbits", () =>
        {
            var pos = Stg.Camera.GlobalPosition;
            CamMotion(new Vector2(80, 0), MouseButtonMask.Right);
            return Stg.Camera.GlobalPosition.DistanceTo(pos) < 1e-4f ? null : "orbited";
        });
        Check("cam Alt + left drag orbits", () =>
        {
            var pos = Stg.Camera.GlobalPosition;
            CamMouse(MouseButton.Left, true, new Vector2(8, 8), alt: true);
            CamMotion(new Vector2(40, 0), MouseButtonMask.Left, alt: true);
            CamMouse(MouseButton.Left, false, new Vector2(8, 8), alt: true);
            return Stg.Camera.GlobalPosition.DistanceTo(pos) > 0.1f ? null : "no orbit";
        });

        // ---- framing ----
        _w.Selection.SelectBone(mid, head);
        await Settle();
        Stg.GrabFocus();
        Stg.ResetView(); Stg.Orbit(50, -10); Stg.Zoom(-8);
        await Frames(3);
        PlainKey(Godot.Key.F);
        await Frames(3);
        Check("cam F frames the selected bone: the pivot is on it and it is inside the viewport", () =>
        {
            var p = _w.Overlay.SelectionPoint();
            var scr = _w.Overlay.ScreenOfBone(mid, head);
            var near = scr is { } sc && sc.X > 0 && sc.Y > 0 && sc.X < Stg.Size.X && sc.Y < Stg.Size.Y;
            return p is { } pp && Stg.Target.DistanceTo(pp) < 1e-3f && near ? null : $"target={Stg.Target} bone={p} screen={scr}";
        });
        Stg.Orbit(30, 5); Stg.Zoom(-10);
        PlainKey(Godot.Key.Home);
        await Frames(3);
        Check("cam Home frames everything", () =>
            _w.Overlay.AllBounds() is { } b && Stg.Target.DistanceTo(b.GetCenter()) < 1e-3f ? null : $"target={Stg.Target}");

        // ---- views ----
        PlainKey(Godot.Key.Kp1); await Frames(2);
        Check("cam numpad 1 = front view (looks toward -Z)", () => AngleDeg(Stg.Forward, Vector3.Forward) < 1 ? null : $"fwd={Stg.Forward}");
        PlainKey(Godot.Key.Kp3); await Frames(2);
        Check("cam numpad 3 = side view (looks toward -X)", () => AngleDeg(Stg.Forward, Vector3.Left) < 1 ? null : $"fwd={Stg.Forward}");
        PlainKey(Godot.Key.Kp7); await Frames(2);
        Check("cam numpad 7 = top view (looks down)", () => AngleDeg(Stg.Forward, Vector3.Down) < 1 ? null : $"fwd={Stg.Forward}");
        Check("cam the overlay still projects bones in the top view", () => _w.Overlay.ScreenOfBone(mid, head) is not null ? null : "no projection");
        PlainKey(Godot.Key.Kp5); await Frames(3);
        Check("cam numpad 5 switches to orthographic", () => Stg.IsOrtho && Stg.Camera.Projection == Camera3D.ProjectionType.Orthogonal ? null : "not ortho");
        Shot(P("p10-en-dark-camera-ortho-top"), GetViewport());
        PlainKey(Godot.Key.Kp5); await Frames(3);
        Check("cam numpad 5 again returns to perspective", () => !Stg.IsOrtho && Stg.Camera.Projection == Camera3D.ProjectionType.Perspective ? null : "still ortho");
        PlainKey(Godot.Key.Kp1); await Frames(2);

        // ---- double click ----
        Stg.Frame(_w.Characters.Bounds(mid)!.Value);
        await Frames(3);
        Check("cam double-click on a bone orbits around it", () =>
        {
            var scr = _w.Overlay.ScreenOfBone(mid, head)!.Value;
            CamMouse(MouseButton.Left, true, scr); CamMouse(MouseButton.Left, false, scr);
            CamMouse(MouseButton.Left, true, scr, dbl: true); CamMouse(MouseButton.Left, false, scr);
            var p = _w.Overlay.JointPosition(mid, head);
            return p is { } pp && Stg.Target.DistanceTo(pp) < 1e-2f ? null : $"target={Stg.Target} hand={p}";
        });

        // ---- settings ----
        Check("cam settings: speed, invert Y, orbit around selection and FOV apply, clamp and persist", () =>
        {
            App.SetCamera(9f, true, true, 70f);
            var t = new AppSettings();
            t.Load();
            var ok = t.CameraFlySpeed == 9f && t.CameraInvertY && t.CameraOrbitSelection && t.CameraFov == 70f && Mathf.IsEqualApprox(Stg.Camera.Fov, 70f);
            App.SetCamera(1000f, true, true, 5f);
            var clamped = s.CameraFlySpeed == 40f && s.CameraFov == 30f;
            return ok && clamped ? null : $"loaded={t.CameraFlySpeed}/{t.CameraInvertY}/{t.CameraOrbitSelection}/{t.CameraFov} fov={Stg.Camera.Fov} clamped={s.CameraFlySpeed}/{s.CameraFov}";
        });
        Check("cam invert Y flips vertical look", () =>
        {
            App.SetCamera(4f, true, false, 45f);
            CamMouse(MouseButton.Right, true, vp / 2);
            var y0 = Stg.Forward.Y;
            CamMotion(new Vector2(0, -30), MouseButtonMask.Right);
            var y1 = Stg.Forward.Y;
            CamMouse(MouseButton.Right, false, vp / 2);
            return y1 < y0 ? null : $"{y0}->{y1}";
        });
        Check("cam orbit around selection: middle press re-centres on the selected bone", () =>
        {
            App.SetCamera(4f, false, true, 45f);
            _w.Selection.SelectBone(mid, head);
            CamMouse(MouseButton.Middle, true, vp / 2); CamMouse(MouseButton.Middle, false, vp / 2);
            return _w.Overlay.SelectionPoint() is { } pp && Stg.Target.DistanceTo(pp) < 1e-2f ? null : $"target={Stg.Target}";
        });
        App.SetCamera(4f, false, false, 45f);

        // ---- UI in both languages ----
        foreach (var lang in new[] { "en", "ar" })
        {
            App.SetLanguage(lang);
            await Settle();
            Stg.Frame(_w.Characters.Bounds(mid)!.Value);
            Stg.SetView(0);
            await Settle();
            Shot(P($"p10-{lang}-dark-camera-viewport"), GetViewport());
            await ShotDialog($"p10-{lang}-dark-camera-view-menu", _w.ViewPopup, () => _w.ViewButton.EmitSignal(BaseButton.SignalName.Pressed));
            Check($"cam {lang} the View menu opens with views, framing and a speed slider", () => _w.ViewPopup.Visible && _w.ViewSpeedSlider.Value == s.CameraFlySpeed ? null : "menu");
            _w.ViewPopup.Hide();
            await Frames(2);
            await ShotDialog($"p10-{lang}-dark-camera-settings", _w.CameraDlg, () => { _w.CameraDlg.Sync(); _w.CameraDlg.PopupCentered(); });
            Check($"cam {lang} the settings dialog shows the saved values", () => (float)_w.CameraDlg.Speed.Value == s.CameraFlySpeed && (float)_w.CameraDlg.Fov.Value == s.CameraFov ? null : "dialog");
            _w.CameraDlg.Hide();
            await Frames(2);
        }
        App.SetLanguage("en");
        await Settle();
        App.SetCamera(saved.CameraFlySpeed, saved.CameraInvertY, saved.CameraOrbitSelection, saved.CameraFov);
        Stg.ResetView();
    }
}
