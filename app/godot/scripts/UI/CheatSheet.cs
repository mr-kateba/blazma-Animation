using System.Net;
using System.Text;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>F1 or ?: every shortcut by category, searchable, in the current language; "Print" writes a clean one-page HTML sheet.</summary>
public partial class CheatSheet : Control
{
    readonly MainWindow _host;
    static ShortcutMap Map => ShortcutMap.Instance;
    static AppController App => AppController.Instance;

    public LineEdit Search { get; } = new() { PlaceholderText = "Search shortcuts…", ClearButtonEnabled = true, CustomMinimumSize = new Vector2(260, 0) };
    public Button PrintButton { get; } = new() { Text = "Print…", FocusMode = FocusModeEnum.None };
    public Button CustomiseButton { get; } = new() { Text = "Customise…", FocusMode = FocusModeEnum.None };
    public Button CloseButton { get; } = new() { Text = "Close", ThemeTypeVariation = "Primary", FocusMode = FocusModeEnum.None };
    readonly GridContainer _grid = new() { Columns = 3 };
    readonly PanelContainer _panel = new() { ThemeTypeVariation = "Floating" };
    int _shown;

    /// <summary>Number of shortcut rows currently listed (after the search filter).</summary>
    public int RowsShown => _shown;
    public PanelContainer Panel => _panel;

    public CheatSheet(MainWindow host)
    {
        _host = host;
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        W.Fill(this);
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = MouseFilterEnum.Ignore };
        W.Fill(dim);
        AddChild(dim);
        _panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.Minsize, 36);
        AddChild(_panel);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        _panel.AddChild(W.Margin(root, 22, 16, 22, 16));
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        head.AddChild(W.Label("Keyboard Shortcuts", "Heading"));
        head.AddChild(W.Spacer());
        head.AddChild(Search);
        head.AddChild(CustomiseButton);
        head.AddChild(PrintButton);
        head.AddChild(CloseButton);
        root.AddChild(head);
        var hint = W.Label("Shortcuts do not fire while you type in a text field. F1 or ? closes this page.", "Faint");
        root.AddChild(hint);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _grid.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _grid.AddThemeConstantOverride("h_separation", 28);
        _grid.AddThemeConstantOverride("v_separation", 14);
        scroll.AddChild(_grid);
        root.AddChild(scroll);

        Search.TextChanged += _ => Rebuild();
        CloseButton.Pressed += Close;
        CustomiseButton.Pressed += () => { Close(); _host.OpenShortcutSettings(); };
        PrintButton.Pressed += () => Print();
        Map.Changed += () => { if (Visible) Rebuild(); };
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady() && Visible) Rebuild();
    }

    public void Open()
    {
        Search.Text = "";
        Rebuild();
        Visible = true;
        MoveToFront();
        Search.GrabFocus();
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        Search.ReleaseFocus();
    }

    public override void _Input(InputEvent e)
    {
        if (Visible && e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true } mb && !_panel.GetGlobalRect().HasPoint(mb.GlobalPosition)) Close();
    }

    void Rebuild()
    {
        foreach (var c in _grid.GetChildren()) { _grid.RemoveChild(c); c.QueueFree(); }
        _shown = 0;
        var q = Search.Text.Trim().ToLowerInvariant();
        _grid.Columns = Math.Clamp((int)(GetViewportRect().Size.X / 360), 1, 3);
        foreach (var cat in ShortcutMap.Categories)
        {
            var rows = Rows(cat, q);
            if (rows.Count == 0) continue;
            var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkBegin };
            col.AddThemeConstantOverride("separation", 4);
            col.AddChild(W.Label(cat, "Heading"));
            col.AddChild(new HSeparator());
            foreach (var a in rows)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 8);
                row.AddChild(new Label { Text = Tr(a.Label), SizeFlagsHorizontal = SizeFlags.ExpandFill, AutowrapMode = TextServer.AutowrapMode.WordSmart, AutoTranslateMode = AutoTranslateModeEnum.Disabled, CustomMinimumSize = new Vector2(120, 0) });
                var chips = new HBoxContainer { LayoutDirection = LayoutDirectionEnum.Ltr };
                chips.AddThemeConstantOverride("separation", 4);
                foreach (var c in Map.ChordsOf(a.Id).Take(2))
                {
                    var chip = new PanelContainer { ThemeTypeVariation = "Chip" };
                    chip.AddChild(W.Label(c.Display(), "MonoFaint", false));
                    chips.AddChild(chip);
                }
                row.AddChild(chips);
                col.AddChild(row);
                _shown++;
            }
            _grid.AddChild(col);
        }
    }

    static List<ShortcutAction> Rows(string cat, string q) => ShortcutMap.All.Where(a => a.Category == cat && Map.ChordsOf(a.Id).Count > 0
        && (q.Length == 0 || Tr(a.Label).ToLowerInvariant().Contains(q) || a.Label.ToLowerInvariant().Contains(q) || Map.DisplayAll(a.Id).ToLowerInvariant().Contains(q) || Tr(cat).ToLowerInvariant().Contains(q))).ToList();

    static string Tr(string s) => TranslationServer.Translate(s);

    /// <summary>A light, A4-friendly page with all shortcuts; right to left in Arabic.</summary>
    public string BuildHtml()
    {
        var ar = App.Settings.Language == "ar";
        var sb = new StringBuilder();
        sb.Append($"<!doctype html><html lang=\"{(ar ? "ar" : "en")}\" dir=\"{(ar ? "rtl" : "ltr")}\"><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(Tr("Keyboard Shortcuts"))}</title>");
        sb.Append("<style>@page{size:A4;margin:14mm}body{font:12px/1.5 system-ui,'Segoe UI',Tahoma,sans-serif;color:#222}h1{font-size:20px;margin:0 0 10px}h2{font-size:13px;margin:14px 0 4px;border-bottom:1px solid #999}.cols{column-count:3;column-gap:24px}.cat{break-inside:avoid}table{width:100%;border-collapse:collapse}td{padding:2px 0;vertical-align:top}td.k{text-align:end;white-space:nowrap}kbd{font:11px ui-monospace,Consolas,monospace;border:1px solid #888;border-radius:3px;padding:0 4px;background:#f4f4f4}</style></head><body>");
        sb.Append($"<h1>Blazma Animation · {WebUtility.HtmlEncode(Tr("Keyboard Shortcuts"))}</h1><div class=\"cols\">");
        foreach (var cat in ShortcutMap.Categories)
        {
            var rows = Rows(cat, "");
            if (rows.Count == 0) continue;
            sb.Append($"<div class=\"cat\"><h2>{WebUtility.HtmlEncode(Tr(cat))}</h2><table>");
            foreach (var a in rows)
                sb.Append($"<tr><td>{WebUtility.HtmlEncode(Tr(a.Label))}</td><td class=\"k\" dir=\"ltr\">{string.Join(" ", Map.ChordsOf(a.Id).Take(2).Select(c => $"<kbd>{WebUtility.HtmlEncode(c.Display())}</kbd>"))}</td></tr>");
            sb.Append("</table></div>");
        }
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    /// <summary>Write the printable page next to the settings and open it in the browser (use its Print command).</summary>
    public string Print()
    {
        var path = AppPaths.Combine("keyboard-shortcuts.html");
        System.IO.Directory.CreateDirectory(AppPaths.UserDir);
        System.IO.File.WriteAllText(path, BuildHtml());
        if (System.Environment.GetEnvironmentVariable("BLAZMA_NO_OPEN") != "1") OS.ShellOpen(path);
        return path;
    }
}
