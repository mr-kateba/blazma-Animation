using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Responsive top bar. The bar holds the menus, the Simple/Advanced, language and theme switches, the panel toggles, AI, Help and the
/// project chip; at 1280–1536 px wide (Arabic is wider than English) they do not all fit. Instead of pushing the window content off
/// screen, the bar gives up space step by step until it fits:
///   1. the "Blazma Animation" name (the brand mark stays),
///   2. the mode, language and theme switches move into the "⋯" menu (they are also in Settings),
///   3. the Props, Combat and Physics toggles show an icon instead of their name (the name stays in the tooltip),
///   4. the project chip keeps its dot only (the name is in the window title and the tooltip).
/// </summary>
public partial class MainWindow
{
    const int TopBarMaxLevel = 4;
    PanelContainer _topBar = null!;
    HBoxContainer _topRow = null!;
    Label _brandName = null!;
    MenuButton _topOverflow = null!;
    int _topBarLevel;
    readonly List<(Button Button, string Text, string Icon)> _compactToggles = new();

    /// <summary>How far the top bar is collapsed (0 = everything shown, 4 = most compact).</summary>
    public int TopBarLevel => _topBarLevel;
    public Control TopBarNode => _topBar;
    public HBoxContainer TopRowNode => _topRow;
    public MenuButton TopOverflowButton => _topOverflow;
    /// <summary>The page (top bar, work area, status bar): its minimum width must fit the window.</summary>
    public Control PageNode { get; private set; } = null!;

    const string PropsIcon = "<path d='M8 1.8l5.4 3.1v6.2L8 14.2l-5.4-3.1V4.9z'/><path d='M2.6 4.9L8 8l5.4-3.1M8 8v6.2'/>";
    const string CombatIcon = "<rect x='3' y='3.5' width='7' height='7' rx='1.5'/><circle cx='11' cy='10.5' r='3'/>";
    const string PhysicsIcon = "<circle cx='8' cy='5' r='2.6'/><path d='M8 8v3'/><path d='M2.5 13.5h11'/><path d='M4 11h8'/>";

    MenuButton BuildTopOverflow()
    {
        _topOverflow = new MenuButton
        {
            Text = "⋯", Flat = false, ThemeTypeVariation = "Transport", FocusMode = FocusModeEnum.None, SizeFlagsVertical = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(30, 0), AutoTranslateMode = AutoTranslateModeEnum.Disabled, TooltipText = "More: mode, language and theme",
            Visible = false,
        };
        var m = _topOverflow.GetPopup();
        m.AutoTranslateMode = AutoTranslateModeEnum.Always;
        m.AboutToPopup += () =>
        {
            var s = App.Settings;
            m.Clear();
            m.AddSeparator("Mode");
            m.AddRadioCheckItem("Simple", 0); m.SetItemChecked(m.ItemCount - 1, !s.AdvancedMode);
            m.AddRadioCheckItem("Advanced", 1); m.SetItemChecked(m.ItemCount - 1, s.AdvancedMode);
            m.AddSeparator("Language");
            m.AddRadioCheckItem("English", 10); m.SetItemChecked(m.ItemCount - 1, s.Language != "ar");
            m.SetItemAutoTranslateMode(m.ItemCount - 1, AutoTranslateModeEnum.Disabled);
            m.AddRadioCheckItem("العربية", 11); m.SetItemChecked(m.ItemCount - 1, s.Language == "ar");
            m.SetItemAutoTranslateMode(m.ItemCount - 1, AutoTranslateModeEnum.Disabled);
            m.AddSeparator("Theme");
            m.AddRadioCheckItem("Dark", 20); m.SetItemChecked(m.ItemCount - 1, s.Theme != "light");
            m.AddRadioCheckItem("Light", 21); m.SetItemChecked(m.ItemCount - 1, s.Theme == "light");
        };
        m.IdPressed += id =>
        {
            switch ((int)id)
            {
                case 0 or 1: App.SetAdvancedMode(id == 1); break;
                case 10 or 11: App.SetLanguage(id == 11 ? "ar" : "en"); break;
                case 20 or 21: App.SetTheme(id == 21 ? "light" : "dark"); break;
            }
        };
        return _topOverflow;
    }

    void RegisterCompactToggle(Button b, string icon) => _compactToggles.Add((b, b.Text, icon));

    void SetupResponsiveLayout()
    {
        RegisterCompactToggle(PropsPanelButton, PropsIcon);
        RegisterCompactToggle(CombatPanelButton, CombatIcon);
        RegisterCompactToggle(PhysicsPanelButton, PhysicsIcon);
        GetViewport().SizeChanged += QueueFitLayout;
        // The tool rail floats on the stage: on a short stage it drops its labels instead of being cut off.
        _center.Resized += () => Rail.SetCompact(_center.Size.Y < 460);
        QueueFitLayout();
    }

    bool _fitQueued;
    void QueueFitLayout()
    {
        if (_fitQueued) return;
        _fitQueued = true;
        Callable.From(FitLayoutNow).CallDeferred();
    }

    string _layoutSig = "";

    /// <summary>Called every frame: refits when something that changes the needed width changed (window size, language, mode, the
    /// docks and panels shown, the project name).</summary>
    void FitLayoutIfChanged()
    {
        if (_compactToggles.Count == 0 || LayoutSignature() == _layoutSig) return;
        FitLayoutNow();
    }

    string LayoutSignature()
    {
        var s = App.Settings;
        return $"{GetViewportRect().Size.X}|{s.Language}|{s.AdvancedMode}|{_sidePanelsHidden}|{Agent.Visible}|{Dock.Visible}|{_rightDock.Visible}|{_chipLabel.Text}|"
               + string.Concat(_rightDock.GetChildren().OfType<Control>().Select(c => c.Visible ? "1" : "0"));
    }

    /// <summary>True while the project outline (left dock) is hidden because the window is too narrow for it.</summary>
    public bool OutlineSqueezed { get; private set; }

    /// <summary>Fit the window (public for the self-test): collapse the top bar just enough, and when the work area still needs more
    /// width than there is (Advanced mode with the AI dock and the side panels open on a 1280–1366 px screen), hide the project
    /// outline until the window is wide enough again. The timeline header wraps by itself.</summary>
    public void FitLayoutNow()
    {
        _fitQueued = false;
        if (!IsInsideTree()) return;
        var width = GetViewportRect().Size.X;
        var level = 0;
        for (; level < TopBarMaxLevel; level++)
        {
            ApplyTopBarLevel(level);
            if (_topBar.GetCombinedMinimumSize().X <= width) break;
        }
        ApplyTopBarLevel(level);

        var wanted = App.Settings.AdvancedMode && !_sidePanelsHidden;
        Dock.Visible = wanted;
        OutlineSqueezed = wanted && PageNode.GetCombinedMinimumSize().X > width;
        if (OutlineSqueezed) Dock.Visible = false;
        _layoutSig = LayoutSignature();
    }

    void ApplyTopBarLevel(int level)
    {
        _topBarLevel = level;
        _brandName.Visible = level < 1;
        foreach (var seg in new Control[] { _segMode, _segLang, _segTheme }) seg.Visible = level < 2;
        _topOverflow.Visible = level >= 2;
        foreach (var (b, text, icon) in _compactToggles)
        {
            var compact = level >= 3;
            b.Text = compact ? "" : text;
            b.Icon = compact ? UiIcons.Make(icon, false) : null;
            b.CustomMinimumSize = compact ? new Vector2(32, 0) : Vector2.Zero; // the tooltip still names the panel
        }
        _chipLabel.Visible = level < 4;
    }
}
