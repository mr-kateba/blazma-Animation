using Godot;

namespace Blazma.UI.Views;

/// <summary>Colour tokens (mirrors mockups/shared.css). One accent colour, no pure black, no gradients.</summary>
public sealed class Palette
{
    public string Name = "dark";
    public Color Bg, Panel, Panel2, Raised, Line, LineStrong, Text, Text2, Text3;
    public Color Accent, AccentSoft, OnAccent, Ok, Warn, Err;
    public Color Viewport, Grid, GridAxis, X, Y, Z;

    public static readonly Palette Dark = new()
    {
        Name = "dark",
        Bg = C("#1d1b19"), Panel = C("#252320"), Panel2 = C("#2c2a26"), Raised = C("#34312c"),
        Line = C("#3b3833"), LineStrong = C("#4a463f"),
        Text = C("#ece7df"), Text2 = C("#b4ada2"), Text3 = C("#857e73"),
        Accent = C("#c27a3e"), AccentSoft = new Color(0.7608f, 0.4784f, 0.2431f, 0.16f), OnAccent = C("#1d1b19"),
        Ok = C("#7fa36b"), Warn = C("#c9a24a"), Err = C("#c0634f"),
        Viewport = C("#22201e"), Grid = C("#34312c"), GridAxis = C("#4a463f"),
        X = C("#c0634f"), Y = C("#7fa36b"), Z = C("#5f8bb0"),
    };

    public static readonly Palette Light = new()
    {
        Name = "light",
        Bg = C("#ebe7e0"), Panel = C("#f5f2ec"), Panel2 = C("#efebe4"), Raised = C("#ffffff"),
        Line = C("#d9d3c9"), LineStrong = C("#c4bdb1"),
        Text = C("#2a2723"), Text2 = C("#5d574e"), Text3 = C("#7d766b"),
        Accent = C("#a8612a"), AccentSoft = new Color(0.6588f, 0.3804f, 0.1647f, 0.12f), OnAccent = C("#ffffff"),
        Ok = C("#5f8a4b"), Warn = C("#a8812a"), Err = C("#b04f3b"),
        Viewport = C("#e1ddd5"), Grid = C("#cfc9be"), GridAxis = C("#b3ab9e"),
        X = C("#b04f3b"), Y = C("#5f8a4b"), Z = C("#4d7aa0"),
    };

    static Color C(string hex) => new(hex);
}

/// <summary>Builds the Godot <see cref="Theme"/> objects for the dark and light palettes.</summary>
public static class BlazmaTheme
{
    const string FontDir = "res://assets/fonts/";
    static FontFile? _regular, _semi, _mono;
    static Theme? _dark, _light;

    public static Palette PaletteFor(string name) => name == "light" ? Palette.Light : Palette.Dark;
    public static Theme For(string name) => name == "light" ? _light ??= Build(Palette.Light) : _dark ??= Build(Palette.Dark);

    public static FontFile Regular => _regular ??= GD.Load<FontFile>(FontDir + "IBMPlexSansArabic-Regular.ttf");
    public static FontFile SemiBold => _semi ??= GD.Load<FontFile>(FontDir + "IBMPlexSansArabic-SemiBold.ttf");
    public static FontFile Mono => _mono ??= GD.Load<FontFile>(FontDir + "IBMPlexMono-Regular.ttf");

    public const int BaseFontSize = 14;

    // ---- helpers -----------------------------------------------------------------------------

    public static StyleBoxFlat Box(Color bg, Color? border = null, int radius = 6, int bw = 1,
        float l = 8, float t = 4, float r = 8, float b = 4)
    {
        var s = new StyleBoxFlat { BgColor = bg };
        s.SetCornerRadiusAll(radius);
        if (border is { } bc) { s.BorderColor = bc; s.SetBorderWidthAll(bw); }
        s.ContentMarginLeft = l; s.ContentMarginTop = t; s.ContentMarginRight = r; s.ContentMarginBottom = b;
        return s;
    }

    static StyleBoxEmpty Empty(float l = 0, float t = 0, float r = 0, float b = 0) =>
        new() { ContentMarginLeft = l, ContentMarginTop = t, ContentMarginRight = r, ContentMarginBottom = b };

    public static ImageTexture Svg(string body, Color color, int size = 16)
    {
        var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='{size}' height='{size}' viewBox='0 0 16 16' fill='none' " +
                  $"stroke='#{color.ToHtml(false)}' stroke-width='1.6' stroke-linecap='round' stroke-linejoin='round'>{body}</svg>";
        var img = new Image();
        img.LoadSvgFromString(svg, 1.0f);
        return ImageTexture.CreateFromImage(img);
    }

    static ImageTexture Filled(string body, Color color, int size = 16)
    {
        var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='{size}' height='{size}' viewBox='0 0 16 16' fill='#{color.ToHtml(false)}'>{body}</svg>";
        var img = new Image();
        img.LoadSvgFromString(svg, 1.0f);
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>Small switch graphic (track + knob) for CheckButton.</summary>
    static ImageTexture Pill(Color track, Color knob, bool on, Color? border = null)
    {
        var b = border ?? track;
        var cx = on ? 21 : 9;
        var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='30' height='18' viewBox='0 0 30 18'>" +
                  $"<rect x='1' y='1' width='28' height='16' rx='8' fill='#{track.ToHtml(false)}' stroke='#{b.ToHtml(false)}' stroke-width='1'/>" +
                  $"<circle cx='{cx}' cy='9' r='5.5' fill='#{knob.ToHtml(false)}'/></svg>";
        var img = new Image();
        img.LoadSvgFromString(svg, 1.0f);
        return ImageTexture.CreateFromImage(img);
    }

    static void Colors(Theme t, string type, params (string, Color)[] items)
    {
        foreach (var (n, c) in items) t.SetColor(n, type, c);
    }

    // ---- build -------------------------------------------------------------------------------

    static Theme Build(Palette p)
    {
        var t = new Theme { DefaultFont = Regular, DefaultFontSize = BaseFontSize };

        var chevDown = Svg("<path d='M4 6l4 4 4-4'/>", p.Text2);
        var chevRight = Svg("<path d='M6 4l4 4-4 4'/>", p.Text2);
        var chevLeft = Svg("<path d='M10 4L6 8l4 4'/>", p.Text2);
        var chevUp = Svg("<path d='M4 10l4-4 4 4'/>", p.Text2);
        var checkOn = Svg("<path d='M3.5 8.5l3 3 6-7'/>", p.Accent);
        var radioOn = Filled("<circle cx='8' cy='8' r='3.2'/>", p.Accent);
        var none = ImageTexture.CreateFromImage(Image.CreateEmpty(16, 16, false, Image.Format.Rgba8));

        // Labels --------------------------------------------------------------------------------
        Colors(t, "Label", ("font_color", p.Text), ("font_shadow_color", Colors0));
        t.SetTypeVariation("Dim", "Label");
        t.SetColor("font_color", "Dim", p.Text2);
        t.SetTypeVariation("Faint", "Label");
        t.SetColor("font_color", "Faint", p.Text3);
        t.SetFontSize("font_size", "Faint", 12);
        t.SetTypeVariation("Heading", "Label");
        t.SetFont("font", "Heading", SemiBold);
        t.SetColor("font_color", "Heading", p.Text);
        t.SetTypeVariation("Section", "Label");
        t.SetFont("font", "Section", SemiBold);
        t.SetFontSize("font_size", "Section", 13);
        t.SetColor("font_color", "Section", p.Text2);
        t.SetTypeVariation("MonoLabel", "Label");
        t.SetFont("font", "MonoLabel", Mono);
        t.SetFontSize("font_size", "MonoLabel", 12);
        t.SetColor("font_color", "MonoLabel", p.Text2);
        t.SetTypeVariation("MonoFaint", "Label");
        t.SetFont("font", "MonoFaint", Mono);
        t.SetFontSize("font_size", "MonoFaint", 11);
        t.SetColor("font_color", "MonoFaint", p.Text3);
        t.SetTypeVariation("ErrLabel", "Label");
        t.SetColor("font_color", "ErrLabel", p.Err);

        // Panels --------------------------------------------------------------------------------
        t.SetStylebox("panel", "Panel", Box(p.Panel, null, 0));
        t.SetStylebox("panel", "PanelContainer", Box(p.Panel, p.Line, 8, 1, 10, 8, 10, 8));
        t.SetTypeVariation("Backdrop", "Panel");
        t.SetStylebox("panel", "Backdrop", Box(p.Bg, null, 0));
        t.SetTypeVariation("TopBar", "PanelContainer");
        var top = Box(p.Panel, null, 0, 0, 10, 0, 10, 0);
        top.BorderColor = p.Line; top.BorderWidthBottom = 1;
        t.SetStylebox("panel", "TopBar", top);
        t.SetTypeVariation("StatusBar", "PanelContainer");
        var status = Box(p.Panel, null, 0, 0, 12, 3, 12, 3);
        status.BorderColor = p.Line; status.BorderWidthTop = 1;
        t.SetStylebox("panel", "StatusBar", status);
        t.SetTypeVariation("Card", "PanelContainer");
        t.SetStylebox("panel", "Card", Box(p.Panel, p.Line, 8, 1, 0, 0, 0, 0));
        t.SetTypeVariation("Floating", "PanelContainer");
        t.SetStylebox("panel", "Floating", Box(p.Panel, p.Line, 8, 1, 0, 0, 0, 0));
        t.SetTypeVariation("Chip", "PanelContainer");
        t.SetStylebox("panel", "Chip", Box(p.Panel2, p.Line, 12, 1, 10, 3, 10, 3));
        t.SetTypeVariation("Row", "PanelContainer");
        t.SetStylebox("panel", "Row", Box(p.Panel2, p.Line, 6, 1, 6, 6, 6, 6));
        t.SetTypeVariation("DraftChip", "PanelContainer");
        t.SetStylebox("panel", "DraftChip", Box(p.AccentSoft, p.Accent, 8, 1, 6, 0, 6, 0));
        t.SetTypeVariation("SegBox", "PanelContainer");
        t.SetStylebox("panel", "SegBox", Box(p.Panel2, p.Line, 6, 1, 0, 0, 0, 0));
        t.SetTypeVariation("Toast", "PanelContainer");
        var toast = Box(p.Raised, p.LineStrong, 8, 1, 14, 9, 14, 9);
        t.SetStylebox("panel", "Toast", toast);
        t.SetStylebox("panel", "PopupPanel", Box(p.Raised, p.LineStrong, 6, 1, 4, 4, 4, 4));
        t.SetStylebox("panel", "TooltipPanel", Box(p.Raised, p.LineStrong, 5, 1, 8, 4, 8, 4));
        t.SetColor("font_color", "TooltipLabel", p.Text);

        // Buttons -------------------------------------------------------------------------------
        t.SetStylebox("normal", "Button", Box(p.Panel2, p.Line, 6, 1, 12, 5, 12, 5));
        t.SetStylebox("hover", "Button", Box(p.Raised, p.LineStrong, 6, 1, 12, 5, 12, 5));
        t.SetStylebox("pressed", "Button", Box(p.AccentSoft, p.Accent, 6, 1, 12, 5, 12, 5));
        t.SetStylebox("hover_pressed", "Button", Box(p.AccentSoft, p.Accent, 6, 1, 12, 5, 12, 5));
        t.SetStylebox("disabled", "Button", Box(p.Panel2, p.Line, 6, 1, 12, 5, 12, 5));
        t.SetStylebox("focus", "Button", Box(Colors0, p.Accent, 6, 1, 12, 5, 12, 5));
        Colors(t, "Button", ("font_color", p.Text), ("font_hover_color", p.Text), ("font_pressed_color", p.Text),
            ("font_hover_pressed_color", p.Text), ("font_focus_color", p.Text), ("font_disabled_color", p.Text3),
            ("icon_normal_color", p.Text2), ("icon_hover_color", p.Text), ("icon_pressed_color", p.Accent),
            ("icon_focus_color", p.Text2), ("icon_disabled_color", p.Text3));
        t.SetConstant("h_separation", "Button", 6);

        t.SetTypeVariation("Primary", "Button");
        t.SetStylebox("normal", "Primary", Box(p.Accent, p.Accent, 6, 1, 12, 5, 12, 5));
        t.SetStylebox("hover", "Primary", Box(p.Accent.Lightened(0.08f), p.Accent.Lightened(0.08f), 6, 1, 12, 5, 12, 5));
        t.SetStylebox("pressed", "Primary", Box(p.Accent.Darkened(0.1f), p.Accent.Darkened(0.1f), 6, 1, 12, 5, 12, 5));
        t.SetStylebox("hover_pressed", "Primary", Box(p.Accent.Darkened(0.1f), p.Accent.Darkened(0.1f), 6, 1, 12, 5, 12, 5));
        Colors(t, "Primary", ("font_color", p.OnAccent), ("font_hover_color", p.OnAccent), ("font_pressed_color", p.OnAccent),
            ("font_focus_color", p.OnAccent), ("font_hover_pressed_color", p.OnAccent));
        t.SetFont("font", "Primary", SemiBold);

        t.SetTypeVariation("Seg", "Button");
        t.SetStylebox("normal", "Seg", Box(p.Panel2, null, 0, 0, 10, 3, 10, 3));
        t.SetStylebox("hover", "Seg", Box(p.Raised, null, 0, 0, 10, 3, 10, 3));
        var segOn = Box(p.Raised, null, 0, 0, 10, 3, 10, 1);
        segOn.BorderColor = p.Accent; segOn.BorderWidthBottom = 2;
        t.SetStylebox("pressed", "Seg", segOn);
        t.SetStylebox("hover_pressed", "Seg", segOn);
        t.SetStylebox("focus", "Seg", Empty());
        Colors(t, "Seg", ("font_color", p.Text2), ("font_hover_color", p.Text), ("font_pressed_color", p.Text),
            ("font_hover_pressed_color", p.Text), ("font_focus_color", p.Text2));

        // Tool rail buttons (icon above a small label) --------------------------------------------
        t.SetTypeVariation("Rail", "Button");
        t.SetStylebox("normal", "Rail", Box(Colors0, Colors0, 6, 1, 2, 6, 2, 4));
        t.SetStylebox("hover", "Rail", Box(p.Panel2, Colors0, 6, 1, 2, 6, 2, 4));
        t.SetStylebox("pressed", "Rail", Box(p.AccentSoft, p.Accent, 6, 1, 2, 6, 2, 4));
        t.SetStylebox("hover_pressed", "Rail", Box(p.AccentSoft, p.Accent, 6, 1, 2, 6, 2, 4));
        t.SetStylebox("disabled", "Rail", Box(Colors0, Colors0, 6, 1, 2, 6, 2, 4));
        t.SetStylebox("focus", "Rail", Empty());
        Colors(t, "Rail", ("font_color", p.Text2), ("font_hover_color", p.Text), ("font_pressed_color", p.Text),
            ("font_hover_pressed_color", p.Text), ("font_focus_color", p.Text2), ("font_disabled_color", p.Text3),
            ("icon_normal_color", p.Text2), ("icon_hover_color", p.Text), ("icon_pressed_color", p.Accent),
            ("icon_hover_pressed_color", p.Accent), ("icon_focus_color", p.Text2), ("icon_disabled_color", p.Text3));
        t.SetFontSize("font_size", "Rail", 11);
        t.SetConstant("h_separation", "Rail", 0);
        t.SetConstant("icon_max_width", "Rail", 22);

        // Transport / timeline buttons (compact, icon only or short text) ---------------------------------
        foreach (var (name, accent) in new[] { ("Transport", p.Accent), ("Rec", p.Err) })
        {
            t.SetTypeVariation(name, "Button");
            var soft = accent with { A = 0.18f };
            t.SetStylebox("normal", name, Box(p.Panel2, p.Line, 6, 1, 7, 4, 7, 4));
            t.SetStylebox("hover", name, Box(p.Raised, p.LineStrong, 6, 1, 7, 4, 7, 4));
            t.SetStylebox("pressed", name, Box(soft, accent, 6, 1, 7, 4, 7, 4));
            t.SetStylebox("hover_pressed", name, Box(soft, accent, 6, 1, 7, 4, 7, 4));
            t.SetStylebox("disabled", name, Box(p.Panel2, p.Line, 6, 1, 7, 4, 7, 4));
            t.SetStylebox("focus", name, Empty());
            Colors(t, name, ("icon_normal_color", p.Text2), ("icon_hover_color", p.Text), ("icon_pressed_color", accent),
                ("icon_hover_pressed_color", accent), ("icon_focus_color", p.Text2), ("icon_disabled_color", p.Text3),
                ("font_color", p.Text), ("font_hover_color", p.Text), ("font_pressed_color", p.Text), ("font_hover_pressed_color", p.Text),
                ("font_focus_color", p.Text), ("font_disabled_color", p.Text3));
        }
        t.SetTypeVariation("KeyBtn", "Primary");
        foreach (var st in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
        {
            var baseBox = (StyleBoxFlat)t.GetStylebox(st, "Primary").Duplicate();
            baseBox.ContentMarginLeft = 18; baseBox.ContentMarginRight = 18; baseBox.ContentMarginTop = 6; baseBox.ContentMarginBottom = 6;
            t.SetStylebox(st, "KeyBtn", baseBox);
        }
        t.SetStylebox("disabled", "KeyBtn", Box(p.Panel2, p.Line, 6, 1, 18, 6, 18, 6));
        Colors(t, "KeyBtn", ("font_color", p.OnAccent), ("font_hover_color", p.OnAccent), ("font_pressed_color", p.OnAccent),
            ("font_focus_color", p.OnAccent), ("font_hover_pressed_color", p.OnAccent), ("font_disabled_color", p.Text3));
        t.SetFont("font", "KeyBtn", SemiBold);

        // Switch (CheckButton) ---------------------------------------------------------------------
        var swOn = Pill(p.Accent, p.OnAccent, true);
        var swOff = Pill(p.Panel2, p.Text3, false, p.LineStrong);
        t.SetIcon("checked", "CheckButton", swOn);
        t.SetIcon("checked_mirrored", "CheckButton", swOn);
        t.SetIcon("unchecked", "CheckButton", swOff);
        t.SetIcon("unchecked_mirrored", "CheckButton", swOff);
        t.SetIcon("checked_disabled", "CheckButton", swOn);
        t.SetIcon("checked_disabled_mirrored", "CheckButton", swOn);
        t.SetIcon("unchecked_disabled", "CheckButton", swOff);
        t.SetIcon("unchecked_disabled_mirrored", "CheckButton", swOff);
        Colors(t, "CheckButton", ("font_color", p.Text), ("font_hover_color", p.Text), ("font_pressed_color", p.Text),
            ("font_hover_pressed_color", p.Text), ("font_focus_color", p.Text), ("font_disabled_color", p.Text3));
        t.SetStylebox("normal", "CheckButton", Empty());
        t.SetStylebox("hover", "CheckButton", Empty());
        t.SetStylebox("pressed", "CheckButton", Empty());
        t.SetStylebox("hover_pressed", "CheckButton", Empty());
        t.SetStylebox("disabled", "CheckButton", Empty());
        t.SetStylebox("focus", "CheckButton", Empty());
        t.SetConstant("h_separation", "CheckButton", 10);

        // Check boxes (graph channel list): flat, with a small rounded box ----------------------------------
        static ImageTexture Raw(string svg)
        {
            var img = new Image();
            img.LoadSvgFromString(svg, 1.0f);
            return ImageTexture.CreateFromImage(img);
        }
        var cbOn = Raw($"<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16' viewBox='0 0 16 16'><rect x='1.5' y='1.5' width='13' height='13' rx='3' fill='#{p.Accent.ToHtml(false)}'/>" +
                       $"<path d='M4.6 8.2l2.4 2.4 4.5-5' fill='none' stroke='#{p.OnAccent.ToHtml(false)}' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'/></svg>");
        var cbOff = Raw($"<svg xmlns='http://www.w3.org/2000/svg' width='16' height='16' viewBox='0 0 16 16'><rect x='1.5' y='1.5' width='13' height='13' rx='3' fill='#{p.Panel2.ToHtml(false)}' stroke='#{p.LineStrong.ToHtml(false)}' stroke-width='1.2'/></svg>");
        foreach (var st in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
            t.SetStylebox(st, "CheckBox", Empty(0, 2, 0, 2));
        t.SetIcon("checked", "CheckBox", cbOn); t.SetIcon("checked_disabled", "CheckBox", cbOn);
        t.SetIcon("unchecked", "CheckBox", cbOff); t.SetIcon("unchecked_disabled", "CheckBox", cbOff);
        Colors(t, "CheckBox", ("font_color", p.Text), ("font_hover_color", p.Text), ("font_pressed_color", p.Text),
            ("font_hover_pressed_color", p.Text), ("font_focus_color", p.Text), ("font_disabled_color", p.Text3));
        t.SetConstant("h_separation", "CheckBox", 8);

        // Menus ---------------------------------------------------------------------------------
        t.SetStylebox("normal", "MenuBar", Box(Colors0, null, 5, 0, 10, 4, 10, 4));
        t.SetStylebox("hover", "MenuBar", Box(p.Panel2, null, 5, 0, 10, 4, 10, 4));
        t.SetStylebox("pressed", "MenuBar", Box(p.Panel2, null, 5, 0, 10, 4, 10, 4));
        t.SetStylebox("hover_pressed", "MenuBar", Box(p.Panel2, null, 5, 0, 10, 4, 10, 4));
        t.SetStylebox("disabled", "MenuBar", Box(Colors0, null, 5, 0, 10, 4, 10, 4));
        Colors(t, "MenuBar", ("font_color", p.Text2), ("font_hover_color", p.Text), ("font_pressed_color", p.Text),
            ("font_hover_pressed_color", p.Text), ("font_disabled_color", p.Text3), ("font_focus_color", p.Text2));
        t.SetConstant("h_separation", "MenuBar", 2);

        t.SetStylebox("panel", "PopupMenu", Box(p.Raised, p.LineStrong, 6, 1, 4, 4, 4, 4));
        t.SetStylebox("hover", "PopupMenu", Box(p.AccentSoft, null, 4, 0, 4, 2, 4, 2));
        var sep = new StyleBoxFlat { BgColor = p.Line, ContentMarginTop = 1, ContentMarginBottom = 1 };
        t.SetStylebox("separator", "PopupMenu", sep);
        t.SetStylebox("labeled_separator_left", "PopupMenu", sep);
        t.SetStylebox("labeled_separator_right", "PopupMenu", sep);
        Colors(t, "PopupMenu", ("font_color", p.Text), ("font_hover_color", p.Text), ("font_disabled_color", p.Text3),
            ("font_accelerator_color", p.Text3), ("font_separator_color", p.Text3));
        t.SetConstant("v_separation", "PopupMenu", 8);
        t.SetConstant("h_separation", "PopupMenu", 10);
        t.SetConstant("item_start_padding", "PopupMenu", 10);
        t.SetConstant("item_end_padding", "PopupMenu", 10);
        t.SetIcon("checked", "PopupMenu", checkOn);
        t.SetIcon("checked_disabled", "PopupMenu", checkOn);
        t.SetIcon("unchecked", "PopupMenu", none);
        t.SetIcon("unchecked_disabled", "PopupMenu", none);
        t.SetIcon("radio_checked", "PopupMenu", radioOn);
        t.SetIcon("radio_checked_disabled", "PopupMenu", radioOn);
        t.SetIcon("radio_unchecked", "PopupMenu", none);
        t.SetIcon("radio_unchecked_disabled", "PopupMenu", none);
        t.SetIcon("submenu", "PopupMenu", chevRight);
        t.SetIcon("submenu_mirrored", "PopupMenu", chevLeft);

        // Option button ---------------------------------------------------------------------------
        t.SetStylebox("normal", "OptionButton", Box(p.Panel2, p.Line, 5, 1, 9, 4, 9, 4));
        t.SetStylebox("hover", "OptionButton", Box(p.Raised, p.LineStrong, 5, 1, 9, 4, 9, 4));
        t.SetStylebox("pressed", "OptionButton", Box(p.Panel2, p.Accent, 5, 1, 9, 4, 9, 4));
        t.SetStylebox("focus", "OptionButton", Box(Colors0, p.Accent, 5, 1, 9, 4, 9, 4));
        t.SetStylebox("disabled", "OptionButton", Box(p.Panel2, p.Line, 5, 1, 9, 4, 9, 4));
        Colors(t, "OptionButton", ("font_color", p.Text), ("font_hover_color", p.Text), ("font_pressed_color", p.Text),
            ("font_focus_color", p.Text), ("font_disabled_color", p.Text3), ("font_hover_pressed_color", p.Text));
        t.SetIcon("arrow", "OptionButton", chevDown);
        t.SetConstant("arrow_margin", "OptionButton", 6);
        t.SetConstant("h_separation", "OptionButton", 6);
        t.SetFont("font", "OptionButton", Mono);
        t.SetFontSize("font_size", "OptionButton", 13);

        // Text input ------------------------------------------------------------------------------
        t.SetStylebox("normal", "LineEdit", Box(p.Panel2, p.Line, 5, 1, 8, 4, 8, 4));
        t.SetStylebox("focus", "LineEdit", Box(p.Panel2, p.Accent, 5, 1, 8, 4, 8, 4));
        t.SetStylebox("read_only", "LineEdit", Box(p.Panel, p.Line, 5, 1, 8, 4, 8, 4));
        Colors(t, "LineEdit", ("font_color", p.Text), ("font_uneditable_color", p.Text3), ("font_placeholder_color", p.Text3),
            ("caret_color", p.Accent), ("selection_color", p.AccentSoft with { A = 0.35f }), ("font_selected_color", p.Text));
        t.SetConstant("caret_width", "LineEdit", 1);
        t.SetTypeVariation("MonoEdit", "LineEdit");
        t.SetFont("font", "MonoEdit", Mono);
        t.SetFontSize("font_size", "MonoEdit", 13);
        t.SetColor("up_icon_modulate", "SpinBox", p.Text2);
        t.SetColor("up_hover_icon_modulate", "SpinBox", p.Text);
        t.SetColor("up_pressed_icon_modulate", "SpinBox", p.Accent);
        t.SetColor("up_disabled_icon_modulate", "SpinBox", p.Text3);
        t.SetColor("down_icon_modulate", "SpinBox", p.Text2);
        t.SetColor("down_hover_icon_modulate", "SpinBox", p.Text);
        t.SetColor("down_pressed_icon_modulate", "SpinBox", p.Accent);
        t.SetColor("down_disabled_icon_modulate", "SpinBox", p.Text3);
        t.SetIcon("updown", "SpinBox", Svg("<path d='M4 6.5l4-3.5 4 3.5M4 9.5l4 3.5 4-3.5'/>", p.Text2));

        // Lists and trees -------------------------------------------------------------------------
        var sel = Box(p.AccentSoft, null, 4, 0, 4, 2, 4, 2);
        t.SetStylebox("panel", "Tree", Box(Colors0, null, 0, 0, 0, 0, 0, 0));
        t.SetStylebox("focus", "Tree", Empty());
        t.SetStylebox("selected", "Tree", sel);
        t.SetStylebox("selected_focus", "Tree", sel);
        t.SetStylebox("hovered", "Tree", Box(p.Panel2, null, 4, 0, 4, 2, 4, 2));
        t.SetStylebox("cursor", "Tree", Empty());
        t.SetStylebox("cursor_unfocused", "Tree", Empty());
        Colors(t, "Tree", ("font_color", p.Text2), ("font_selected_color", p.Text), ("font_hovered_color", p.Text),
            ("font_hovered_selected_color", p.Text), ("guide_color", p.Line), ("relationship_line_color", p.Line),
            ("parent_hl_line_color", p.LineStrong), ("children_hl_line_color", p.Line));
        t.SetConstant("v_separation", "Tree", 6);
        t.SetConstant("item_margin", "Tree", 14);
        t.SetConstant("draw_guides", "Tree", 0);
        t.SetConstant("draw_relationship_lines", "Tree", 0);
        t.SetIcon("arrow", "Tree", chevDown);
        t.SetIcon("arrow_collapsed", "Tree", chevRight);
        t.SetIcon("arrow_collapsed_mirrored", "Tree", chevLeft);

        t.SetStylebox("panel", "ItemList", Box(Colors0, null, 0, 0, 0, 0, 0, 0));
        t.SetStylebox("focus", "ItemList", Empty());
        t.SetStylebox("selected", "ItemList", sel);
        t.SetStylebox("selected_focus", "ItemList", sel);
        t.SetStylebox("hovered", "ItemList", Box(p.Panel2, null, 4, 0, 4, 2, 4, 2));
        t.SetStylebox("cursor", "ItemList", Empty());
        t.SetStylebox("cursor_unfocused", "ItemList", Empty());
        Colors(t, "ItemList", ("font_color", p.Text2), ("font_selected_color", p.Text), ("font_hovered_color", p.Text),
            ("font_hovered_selected_color", p.Text), ("guide_color", p.Line));
        t.SetConstant("v_separation", "ItemList", 4);

        // Progress bar -----------------------------------------------------------------------------
        t.SetStylebox("background", "ProgressBar", Box(p.Panel2, p.LineStrong, 4, 1, 0, 0, 0, 0));
        t.SetStylebox("fill", "ProgressBar", Box(p.Accent, null, 4, 0, 0, 0, 0, 0));

        // Scroll bars -----------------------------------------------------------------------------
        foreach (var sb in new[] { "HScrollBar", "VScrollBar" })
        {
            t.SetStylebox("scroll", sb, Box(Colors0, null, 3, 0, 3, 3, 3, 3));
            t.SetStylebox("scroll_focus", sb, Box(Colors0, null, 3, 0, 3, 3, 3, 3));
            t.SetStylebox("grabber", sb, Box(p.LineStrong, null, 3, 0, 3, 3, 3, 3));
            t.SetStylebox("grabber_highlight", sb, Box(p.Text3, null, 3, 0, 3, 3, 3, 3));
            t.SetStylebox("grabber_pressed", sb, Box(p.Accent, null, 3, 0, 3, 3, 3, 3));
            foreach (var ic in new[] { "increment", "increment_highlight", "increment_pressed", "decrement", "decrement_highlight", "decrement_pressed" })
                t.SetIcon(ic, sb, none);
        }

        // Splits and separators -----------------------------------------------------------------
        foreach (var sp in new[] { "HSplitContainer", "VSplitContainer" })
        {
            t.SetConstant("separation", sp, 8);
            t.SetConstant("minimum_grab_thickness", sp, 8);
            t.SetConstant("autohide", sp, 0);
            t.SetIcon("grabber", sp, none);
        }
        t.SetIcon("h_grabber", "SplitContainer", none);
        t.SetIcon("v_grabber", "SplitContainer", none);
        var hl = new StyleBoxFlat { BgColor = p.Line, ContentMarginTop = 1, ContentMarginBottom = 1, ContentMarginLeft = 1, ContentMarginRight = 1 };
        t.SetStylebox("separator", "HSeparator", hl);
        t.SetStylebox("separator", "VSeparator", hl);
        t.SetConstant("separation", "HSeparator", 10);
        t.SetConstant("separation", "VSeparator", 10);
        t.SetConstant("separation", "HBoxContainer", 6);
        t.SetConstant("separation", "VBoxContainer", 6);

        // Dialog windows --------------------------------------------------------------------------
        t.SetStylebox("panel", "AcceptDialog", Box(p.Panel, p.Line, 0, 1, 12, 12, 12, 12));
        t.SetStylebox("panel", "ConfirmationDialog", Box(p.Panel, p.Line, 0, 1, 12, 12, 12, 12));
        t.SetStylebox("panel", "Window", Box(p.Panel, p.Line, 0, 1, 12, 12, 12, 12));
        t.SetStylebox("embedded_border", "Window", Box(p.Panel, p.Line, 0, 1, 12, 12, 12, 12));
        t.SetColor("title_color", "Window", p.Text);
        t.SetConstant("buttons_separation", "AcceptDialog", 8);

        return t;
    }

    static readonly Color Colors0 = new(0, 0, 0, 0);
}
