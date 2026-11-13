using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// 1.0 audit: the window fits common laptop sizes (1280×720, 1366×768, 1440×900, 1536×864) in English and Arabic with every optional
/// part shown (Advanced mode, Props, Combat and Physics panels, the AI dock): no top-bar or timeline-header control is cut off and the
/// page never needs more width than the window. Arabic text is never set in the monospace font, and every text the built UI shows has
/// an Arabic translation. Shortcut: BLAZMA_ONLY_LAYOUT=1.
/// </summary>
public partial class SelfTest
{
    static readonly Vector2I[] LayoutSizes = { new(1280, 720), new(1366, 768), new(1440, 900), new(1536, 864) };

    async Task RunLayout()
    {
        var s = App.Settings;
        var before = (s.Language, s.AdvancedMode, s.PropsPanel, s.CombatPanel, s.PhysicsPanel, Agent: _w.Agent.Visible);
        var win = GetWindow();
        var size0 = win.Size;
        _w.NewProjectNow();
        _w.AddMannequin();
        _w.CreateClipNamed("A clip with a fairly long name for the picker");

        foreach (var lang in new[] { "ar", "en" })
        {
            App.SetLanguage(lang);
            foreach (var full in new[] { false, true })
            {
                App.SetAdvancedMode(full);
                App.SetPropsPanel(full); App.SetCombatPanel(full); App.SetPhysicsPanel(full);
                _w.ShowAgentPanel(full);
                foreach (var size in LayoutSizes)
                {
                    win.Size = size;
                    await Settle();
                    _w.FitLayoutNow();
                    await Frames(3);
                    var tag = $"{lang} {(full ? "advanced, all panels + AI" : "simple")} {size.X}x{size.Y}";
                    Check($"layout {tag}: nothing in the top bar or the timeline header is cut off", () => LayoutProblem(size));
                    if (size.X == 1280 && full) Shot(P($"layout-{lang}-1280-all-panels"), GetViewport());
                    if (size.X == 1440 && full && lang == "ar") Shot(P("layout-ar-1440-all-panels"), GetViewport());
                }
            }
        }

        // The overflow menu holds the switches that left the bar.
        win.Size = LayoutSizes[0];
        App.SetLanguage("ar"); App.SetAdvancedMode(true);
        await Settle(); _w.FitLayoutNow(); await Frames(2);
        Check("layout at 1280 px in Arabic the mode, language and theme switches are in the ⋯ menu", () =>
        {
            if (_w.TopBarLevel < 2) return null; // they fit: nothing to check
            var o = _w.TopOverflowButton;
            if (!o.IsVisibleInTree()) return "⋯ not shown though the switches are hidden";
            o.GetPopup().EmitSignal(Window.SignalName.AboutToPopup);
            var ids = Enumerable.Range(0, o.GetPopup().ItemCount).Select(i => o.GetPopup().GetItemId(i)).ToHashSet();
            return ids.IsSupersetOf(new[] { 0, 1, 10, 11, 20, 21 }) ? null : "items: " + string.Join(",", ids);
        });

        // ---- Arabic is never set in the monospace font ----
        App.SetLanguage("ar"); App.SetAdvancedMode(true);
        _w.OpenGenerateDialog();
        await Settle();
        Check("layout Arabic text is never drawn with the monospace font (main window and the Generate dialog)", () =>
        {
            var bad = new List<string>();
            void Walk(Node n)
            {
                foreach (var c in n.GetChildren())
                {
                    if (c is Window wnd && !wnd.Visible) continue;
                    if (c is CanvasItem ci && !ci.Visible) continue;
                    if (c is Control ctl && (c is Label or Button or LineEdit) && ShownText(ctl) is { } t && t.Any(ch => ch is >= '؀' and <= 'ۿ')
                        && ctl.GetThemeFont("font") == BlazmaTheme.Mono)
                        bad.Add($"{c.GetType().Name} '{t}' ({ctl.ThemeTypeVariation})");
                    Walk(c);
                }
            }
            Walk(_w);
            return bad.Count == 0 ? null : $"{bad.Count} controls: " + string.Join(" | ", bad.Take(6));
        });
        Shot(P("layout-ar-generate-dialog"), _w.GenerateDlg);
        _w.GenerateDlg.Hide();

        // ---- every text the built UI shows has an Arabic translation ----
        Check("layout every auto-translated text in the built UI (windows, dialogs, menus, tooltips) has an ar.po entry", () =>
        {
            var ar = TranslationServer.FindTranslations("ar", false).FirstOrDefault();
            if (ar is null) return "no ar translation loaded";
            var missing = new SortedSet<string>();
            Node? at = null;
            void Need(string text)
            {
                text = text.Trim();
                if (text.Length < 2 || !text.Any(char.IsAsciiLetter) || text.Any(ch => ch is >= '؀' and <= 'ۿ')) return;
                if (UntranslatedOnPurpose.Contains(text)) return;
                if (ar.GetMessage(text).ToString().Length == 0) missing.Add(text + (at is null ? "" : $" [{at.GetParent()?.Name}/{at.Name}]"));
            }
            void Walk(Node n, bool translate)
            {
                // Godot's own file dialog is only a fallback: Blazma asks for the system's file dialog (UseNativeDialog), which
                // speaks the system's language. Its filter descriptions are Blazma's and are checked.
                if (n is FileDialog fd)
                {
                    foreach (var f in fd.Filters) if (f.Split(';') is { Length: > 1 } parts) Need(parts[1]);
                    return;
                }
                at = n;
                var on = n.AutoTranslateMode switch { Node.AutoTranslateModeEnum.Disabled => false, Node.AutoTranslateModeEnum.Always => true, _ => translate };
                if (on)
                {
                    switch (n)
                    {
                        case Label l: Need(l.Text); break;
                        case Button b and not OptionButton: Need(b.Text); break;
                        case Window w: Need(w.Title); break;
                    }
                    if (n is Control c && c.TooltipText.Length > 0) Need(c.TooltipText);
                    if (n is LineEdit le && le.PlaceholderText.Length > 0) Need(le.PlaceholderText);
                    if (n is PopupMenu pm)
                        for (var i = 0; i < pm.ItemCount; i++)
                            if (!pm.IsItemSeparator(i) || pm.GetItemText(i).Length > 0)
                                if (pm.GetItemAutoTranslateMode(i) != Node.AutoTranslateModeEnum.Disabled) Need(pm.GetItemText(i));
                }
                foreach (var c in n.GetChildren(true)) Walk(c, on);
            }
            Walk(_w, true);
            return missing.Count == 0 ? null : $"{missing.Count} without ar.po entry: " + string.Join(" | ", missing);
        });

        win.Size = size0;
        App.SetLanguage(before.Language); App.SetAdvancedMode(before.AdvancedMode);
        App.SetPropsPanel(before.PropsPanel); App.SetCombatPanel(before.CombatPanel); App.SetPhysicsPanel(before.PhysicsPanel);
        _w.ShowAgentPanel(before.Agent);
        await Settle();
    }

    /// <summary>Names, symbols and file types that read the same in both languages.</summary>
    static readonly HashSet<string> UntranslatedOnPurpose = new() { "Blazma Animation", "EN", "AI", "IK", "FK", "GLB", "glTF", "Kimodo", "X", "Y", "Z" };

    static string? ShownText(Control c) => c switch
    {
        Label l => l.Text.Length > 0 ? c.Tr(l.Text) : null,
        Button b => b.Text.Length > 0 ? c.Tr(b.Text) : null,
        LineEdit e => e.Text.Length > 0 ? e.Text : null,
        _ => null,
    };

    string? LayoutProblem(Vector2I size)
    {
        var width = GetViewport().GetVisibleRect().Size.X;
        if (Math.Abs(width - size.X) > 1) return $"the window is {width} px wide, expected {size.X}";
        var need = _w.PageNode.GetCombinedMinimumSize().X;
        if (need > width + 0.5f) return $"the page needs {need:0} px (top bar {_w.TopBarNode.GetCombinedMinimumSize().X:0}, timeline header {_w.Timeline.Header.GetCombinedMinimumSize().X:0}) at level {_w.TopBarLevel}";
        var height = GetViewport().GetVisibleRect().Size.Y;
        var needH = _w.PageNode.GetCombinedMinimumSize().Y;
        if (needH > height + 0.5f) return $"the page needs {needH:0} px of height, the window has {height:0}";
        var cut = new List<string>();
        void Walk(Control root)
        {
            foreach (var n in root.GetChildren())
            {
                if (n is not Control c || !c.IsVisibleInTree()) continue;
                var r = c.GetGlobalRect();
                if (r.Size.X > 0 && (r.Position.X < -0.5f || r.End.X > width + 0.5f))
                    cut.Add($"{c.GetType().Name} '{ShownText(c) ?? c.Name}' x={r.Position.X:0}..{r.End.X:0}");
                Walk(c);
            }
        }
        Walk(_w.TopBarNode);
        Walk(_w.Timeline.Header);
        foreach (var panel in new Control[] { _w.PropsPanelNode, _w.CombatPanelNode, _w.PhysicsPanelNode, _w.Agent, _w.Timeline.ExportButton, _w.Timeline.RetargetButton })
        {
            if (!panel.IsVisibleInTree()) continue;
            var r = panel.GetGlobalRect();
            if (r.Position.X < -0.5f || r.End.X > width + 0.5f) cut.Add($"{panel.Name} x={r.Position.X:0}..{r.End.X:0}");
        }
        // The tool rail floats on the stage: it must not be cut off by the stage's edges.
        var stage = _w.Rail.GetParent<Control>().GetGlobalRect();
        var rail = _w.Rail.GetGlobalRect();
        if (_w.Rail.IsVisibleInTree() && (rail.Position.Y < stage.Position.Y - 0.5f || rail.End.Y > stage.End.Y + 0.5f))
            cut.Add($"tool rail y={rail.Position.Y:0}..{rail.End.Y:0} outside the stage y={stage.Position.Y:0}..{stage.End.Y:0} (compact={_w.Rail.Compact})");
        // Controls inside the header must also be inside it (a FlowContainer that cannot wrap would push them past its edge).
        var h = _w.Timeline.Header.GetGlobalRect();
        foreach (var n in _w.Timeline.Header.GetChildren())
            if (n is Control c && c.IsVisibleInTree() && c.Size.X > 0 && (c.GetGlobalRect().Position.X < h.Position.X - 0.5f || c.GetGlobalRect().End.X > h.End.X + 0.5f))
                cut.Add($"header child {c.Name} outside the header");
        return cut.Count == 0 ? null : $"level {_w.TopBarLevel}: " + string.Join("; ", cut.Take(6));
    }
}
