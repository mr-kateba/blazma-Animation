using System.Globalization;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Viewport camera UI: the View button / popup in the stage toolbar, the on-screen camera notice and the Camera Settings dialog.</summary>
public partial class MainWindow
{
    const int MCameraSettings = 97;

    public Button ViewButton { get; private set; } = null!;
    public PopupPanel ViewPopup { get; private set; } = null!;
    public HSlider ViewSpeedSlider { get; private set; } = null!;
    public CameraSettingsDialog CameraDlg { get; private set; } = null!;
    PanelContainer _camHud = null!;
    Label _camHudLabel = null!;
    Tween? _camHudTween;
    Label _viewSpeedValue = null!;

    public string CameraHudText => _camHudLabel.Text;
    public bool CameraHudVisible => _camHud.Visible;

    static string LtrIso(string s) => "⁦" + s + "⁩";
    public static string FormatSpeed(float v) => LtrIso(v.ToString("0.0", CultureInfo.InvariantCulture) + " m/s");

    void AddCameraToolbar(HBoxContainer row)
    {
        ViewButton = new Button
        {
            Text = "View", FocusMode = FocusModeEnum.None, ThemeTypeVariation = "Transport",
            TooltipText = "Camera views, framing, projection and camera speed.",
        };
        row.AddChild(ViewButton);
        row.AddChild(new VSeparator());

        ViewPopup = new PopupPanel { Name = "ViewPopup" };
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(250, 0) };
        box.AddThemeConstantOverride("separation", 2);
        void Item(string text, string key, Action act)
        {
            var r = new HBoxContainer { LayoutDirection = LayoutDirectionEnum.Locale };
            var b = new Button { Text = text, Flat = true, Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            b.Pressed += () => { ViewPopup.Hide(); act(); Stage.GrabFocus(); };
            r.AddChild(b);
            var k = W.Label(key, "Dim", false);
            k.LayoutDirection = LayoutDirectionEnum.Ltr;
            r.AddChild(k);
            box.AddChild(r);
        }
        Item("Front view", "Num 1", () => Stage.SetView(0));
        Item("Side view", "Num 3", () => Stage.SetView(1));
        Item("Top view", "Num 7", () => Stage.SetView(2));
        Item("Perspective / Orthographic", "Num 5", () => Stage.ToggleOrtho());
        box.AddChild(new HSeparator());
        Item("Frame selection", "F / Num .", () => Stage.FrameFocus());
        Item("Frame all", "Home", () => Stage.FrameAll());
        Item("Reset camera", "", () => Stage.ResetView());
        box.AddChild(new HSeparator());
        box.AddChild(W.Label("Camera speed"));
        var sr = new HBoxContainer { LayoutDirection = LayoutDirectionEnum.Ltr };
        ViewSpeedSlider = new HSlider { MinValue = 0.5, MaxValue = 40, Step = 0.5, SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None };
        _viewSpeedValue = W.Label("", "Dim", false);
        _viewSpeedValue.CustomMinimumSize = new Vector2(70, 0);
        ViewSpeedSlider.ValueChanged += v =>
        {
            _viewSpeedValue.Text = FormatSpeed((float)v);
            var s = App.Settings;
            if (Math.Abs(s.CameraFlySpeed - v) > 1e-4) App.SetCamera((float)v, s.CameraInvertY, s.CameraOrbitSelection, s.CameraFov);
        };
        sr.AddChild(ViewSpeedSlider);
        sr.AddChild(_viewSpeedValue);
        box.AddChild(sr);
        ViewPopup.AddChild(W.Margin(box, 6, 6, 6, 6));
        AddChild(ViewPopup);
        ViewButton.Pressed += () =>
        {
            SyncCameraUi();
            var r = ViewButton.GetGlobalRect();
            ViewPopup.Popup(new Rect2I((Vector2I)(r.Position + new Vector2(0, r.Size.Y + 4)), new Vector2I(0, 0)));
        };

        _camHudLabel = W.Label("", null, false);
        _camHud = new PanelContainer { ThemeTypeVariation = "Floating", MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _camHud.AddChild(W.Margin(_camHudLabel, 8, 2, 8, 2));
        _camHud.AnchorLeft = 0.5f; _camHud.AnchorRight = 0.5f; _camHud.AnchorTop = 1f; _camHud.AnchorBottom = 1f;
        _camHud.GrowHorizontal = GrowDirection.Both; _camHud.GrowVertical = GrowDirection.Begin;
        _camHud.OffsetBottom = -50;
        _camHud.LayoutDirection = LayoutDirectionEnum.Locale;
        _center.AddChild(_camHud);
        Stage.Notice += ShowCameraNotice;
    }

    void BuildCameraDialogs()
    {
        CameraDlg = new CameraSettingsDialog();
        AddChild(CameraDlg);
    }

    void BuildCameraSettingsMenu() => _mSettings.AddItem("Camera Settings…", MCameraSettings);

    void ShowCameraNotice(string what)
    {
        var text = what switch
        {
            "SPEED" => Tr("Fly speed") + " " + FormatSpeed(App.Settings.CameraFlySpeed),
            _ => Tr(what),
        };
        _camHudLabel.Text = text;
        _camHud.Visible = true;
        _camHud.Modulate = Colors.White;
        _camHudTween?.Kill();
        _camHudTween = CreateTween();
        _camHudTween.TweenInterval(1.1);
        _camHudTween.TweenProperty(_camHud, "modulate:a", 0.0, 0.4);
        _camHudTween.TweenCallback(Callable.From(() => _camHud.Visible = false));
        if (what == "SPEED") SyncCameraUi();
    }

    void SyncCameraUi()
    {
        if (ViewSpeedSlider is null) return;
        ViewSpeedSlider.SetValueNoSignal(App.Settings.CameraFlySpeed);
        _viewSpeedValue.Text = FormatSpeed(App.Settings.CameraFlySpeed);
    }

    void OnCameraSettingsChanged()
    {
        Stage.ApplyCameraSettings();
        SyncCameraUi();
        CameraDlg?.Sync();
    }
}

/// <summary>Small dialog in the Settings menu: fly speed, inverted vertical look, orbit around the selected bone, field of view.</summary>
public partial class CameraSettingsDialog : AcceptDialog
{
    public HSlider Speed { get; } = new() { MinValue = 0.5, MaxValue = 40, Step = 0.5, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    public HSlider Fov { get; } = new() { MinValue = 30, MaxValue = 100, Step = 1, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    public CheckBox InvertY { get; } = new() { Text = "Invert vertical look", FocusMode = Control.FocusModeEnum.None };
    public CheckBox OrbitSel { get; } = new() { Text = "Orbit around the selected bone", FocusMode = Control.FocusModeEnum.None };
    readonly Label _speedV = W.Label("", "Dim", false), _fovV = W.Label("", "Dim", false);
    bool _syncing;

    static AppController App => AppController.Instance;

    public CameraSettingsDialog()
    {
        Title = "Camera Settings";
        OkButtonText = "Close";
        Exclusive = true;
        MinSize = new Vector2I(420, 0);
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        AddChild(root);
        var intro = W.Label("Hold the right mouse button in the viewport to fly: W A S D move, Q and E go down and up, Shift is faster, Ctrl is slower, the wheel changes the speed.", "Dim");
        intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        intro.CustomMinimumSize = new Vector2(380, 0);
        root.AddChild(intro);
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 8);
        root.AddChild(grid);
        void Row(string label, HSlider s, Label v)
        {
            grid.AddChild(W.Label(label));
            s.CustomMinimumSize = new Vector2(180, 0);
            s.FocusMode = Control.FocusModeEnum.None;
            s.LayoutDirection = Control.LayoutDirectionEnum.Ltr;
            grid.AddChild(s);
            v.CustomMinimumSize = new Vector2(70, 0);
            v.LayoutDirection = Control.LayoutDirectionEnum.Ltr;
            grid.AddChild(v);
        }
        Row("Fly speed", Speed, _speedV);
        Row("Field of view", Fov, _fovV);
        root.AddChild(InvertY);
        root.AddChild(OrbitSel);
        Speed.ValueChanged += _ => Apply();
        Fov.ValueChanged += _ => Apply();
        InvertY.Toggled += _ => Apply();
        OrbitSel.Toggled += _ => Apply();
        Sync();
    }

    void Apply()
    {
        if (_syncing) return;
        App.SetCamera((float)Speed.Value, InvertY.ButtonPressed, OrbitSel.ButtonPressed, (float)Fov.Value);
    }

    public void Sync()
    {
        var s = App.Settings;
        _syncing = true;
        Speed.Value = s.CameraFlySpeed;
        Fov.Value = s.CameraFov;
        InvertY.SetPressedNoSignal(s.CameraInvertY);
        OrbitSel.SetPressedNoSignal(s.CameraOrbitSelection);
        _syncing = false;
        _speedV.Text = MainWindow.FormatSpeed(s.CameraFlySpeed);
        _fovV.Text = "⁦" + ((int)Math.Round(s.CameraFov)).ToString(CultureInfo.InvariantCulture) + "°⁩";
    }
}
