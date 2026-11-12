using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// First launch: the OS language is detected (Arabic variants give Arabic, everything else English) and offered together with the theme.
/// After "Continue" it offers the interactive tutorial. Shown once; Settings > Welcome opens it again.
/// </summary>
public partial class WelcomeScreen : Control
{
    readonly MainWindow _host;
    static AppController App => AppController.Instance;

    public Button LangAr { get; } = Toggle("العربية");
    public Button LangEn { get; } = Toggle("English");
    public Button ThemeDark { get; } = Toggle("Dark", true);
    public Button ThemeLight { get; } = Toggle("Light", true);
    public Button ContinueButton { get; } = new() { Text = "Continue", ThemeTypeVariation = "Primary", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(160, 36) };
    public Button TourButton { get; } = new() { Text = "Show me how it works", ThemeTypeVariation = "Primary", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(200, 36) };
    public Button SkipButton { get; } = new() { Text = "Skip for now", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(140, 36) };
    readonly VBoxContainer _pageChoice = new(), _pageTour = new();
    readonly PanelContainer _card = new() { ThemeTypeVariation = "Floating" };
    readonly Label _detected = W.Label("", "Faint", false);

    /// <summary>Language chosen on this screen ("ar" or "en").</summary>
    public string ChosenLanguage => LangAr.ButtonPressed ? "ar" : "en";
    public string ChosenTheme => ThemeLight.ButtonPressed ? "light" : "dark";
    public bool OnTourPage => _pageTour.Visible;
    public PanelContainer Card => _card;

    static Button Toggle(string text, bool translate = false) => new()
    {
        Text = text, ToggleMode = true, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(180, 52),
        AutoTranslateMode = translate ? AutoTranslateModeEnum.Inherit : AutoTranslateModeEnum.Disabled,
    };

    public WelcomeScreen(MainWindow host)
    {
        _host = host;
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        W.Fill(this);
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.62f), MouseFilter = MouseFilterEnum.Ignore };
        W.Fill(dim);
        AddChild(dim);
        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        W.Fill(center);
        AddChild(center);
        center.AddChild(_card);

        // page 1: language and theme
        _pageChoice.AddThemeConstantOverride("separation", 14);
        _pageChoice.CustomMinimumSize = new Vector2(520, 0);
        var title = W.Label("Welcome to Blazma Animation  ·  مرحباً بك في بلازما أنيميشن", "Heading", false);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _pageChoice.AddChild(title);
        var sub = W.Label("Pick your language and look. You can change both any time in Settings.", "Dim");
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        sub.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _pageChoice.AddChild(sub);
        _pageChoice.AddChild(W.Label("Language"));
        var langRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, LayoutDirection = LayoutDirectionEnum.Ltr };
        langRow.AddThemeConstantOverride("separation", 10);
        langRow.AddChild(LangEn);
        langRow.AddChild(LangAr);
        _pageChoice.AddChild(langRow);
        _detected.HorizontalAlignment = HorizontalAlignment.Center;
        _pageChoice.AddChild(_detected);
        _pageChoice.AddChild(W.Label("Theme"));
        var themeRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        themeRow.AddThemeConstantOverride("separation", 10);
        themeRow.AddChild(ThemeDark);
        themeRow.AddChild(ThemeLight);
        _pageChoice.AddChild(themeRow);
        var go = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        go.AddChild(ContinueButton);
        _pageChoice.AddChild(W.Margin(go, 0, 10, 0, 0));

        // page 2: offer the tutorial
        _pageTour.AddThemeConstantOverride("separation", 14);
        _pageTour.CustomMinimumSize = new Vector2(520, 0);
        var t2 = W.Label("Welcome! Want a two-minute tour?", "Heading");
        t2.HorizontalAlignment = HorizontalAlignment.Center;
        t2.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _pageTour.AddChild(t2);
        var b2 = W.Label("A short guided tour shows the stage, posing, the timeline, templates, generating motion from a sentence, and exporting. You can open it again any time from the ? button.", "Dim");
        b2.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        b2.HorizontalAlignment = HorizontalAlignment.Center;
        _pageTour.AddChild(b2);
        var row2 = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row2.AddThemeConstantOverride("separation", 10);
        row2.AddChild(TourButton);
        row2.AddChild(SkipButton);
        _pageTour.AddChild(W.Margin(row2, 0, 10, 0, 0));
        _pageTour.Visible = false;

        var holder = new VBoxContainer();
        holder.AddChild(_pageChoice);
        holder.AddChild(_pageTour);
        _card.AddChild(W.Margin(holder, 32, 26, 32, 26));

        LangEn.Pressed += () => Choose("en");
        LangAr.Pressed += () => Choose("ar");
        ThemeDark.Pressed += () => ChooseTheme("dark");
        ThemeLight.Pressed += () => ChooseTheme("light");
        ContinueButton.Pressed += Confirm;
        TourButton.Pressed += () => { Hide(); _host.StartTutorial(); };
        SkipButton.Pressed += () => Hide();
    }

    /// <summary>Show the screen with the current language and theme selected (on the first launch the language is the one detected from the system).</summary>
    public void Open()
    {
        _pageChoice.Visible = true;
        _pageTour.Visible = false;
        var s = App.Settings;
        LangAr.SetPressedNoSignal(s.Language == "ar");
        LangEn.SetPressedNoSignal(s.Language != "ar");
        ThemeDark.SetPressedNoSignal(s.Theme != "light");
        ThemeLight.SetPressedNoSignal(s.Theme == "light");
        _detected.Text = App.Settings.IsFirstRun
            ? (s.Language == "ar" ? "اللغة المكتشفة من نظامك: العربية" : "Detected from your system: English") : "";
        Visible = true;
        MoveToFront();
    }

    void Choose(string lang)
    {
        LangAr.SetPressedNoSignal(lang == "ar");
        LangEn.SetPressedNoSignal(lang == "en");
        App.SetLanguage(lang); // the screen itself switches at once, so the choice is visible
    }

    void ChooseTheme(string theme)
    {
        ThemeDark.SetPressedNoSignal(theme == "dark");
        ThemeLight.SetPressedNoSignal(theme == "light");
        App.SetTheme(theme);
    }

    /// <summary>Save the choice and move on to the tutorial offer.</summary>
    public void Confirm()
    {
        var s = App.Settings;
        s.Language = ChosenLanguage;
        s.Theme = ChosenTheme;
        s.WelcomeDone = true;
        s.Save();
        App.SetLanguage(s.Language);
        App.SetTheme(s.Theme);
        _pageChoice.Visible = false;
        _pageTour.Visible = true;
    }

    public override void _Input(InputEvent e)
    {
        if (!Visible || e is not InputEventKey { Pressed: true, Echo: false } k) return;
        if (k.Keycode is Key.Enter or Key.KpEnter)
        {
            if (_pageChoice.Visible) Confirm(); else { Hide(); _host.StartTutorial(); }
            GetViewport().SetInputAsHandled();
        }
        else if (k.Keycode == Key.Escape && _pageTour.Visible) { Hide(); GetViewport().SetInputAsHandled(); }
    }
}
