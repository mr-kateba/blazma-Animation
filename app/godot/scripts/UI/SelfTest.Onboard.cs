using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// First launch, tutorial and keyboard shortcuts: OS language detection, the welcome choice and its persistence, the tour lighting real
/// nodes and completing, the shortcut map (rebinding, conflicts, persistence, import/export), keys never firing in text fields, the
/// F1 cheat sheet. Screenshots p11-{en,ar}-dark-{welcome,tutorial,shortcuts,cheatsheet}.png. Shortcut: BLAZMA_ONLY_ONBOARD=1.
/// </summary>
public partial class SelfTest
{
    static ShortcutMap Map => ShortcutMap.Instance;

    void PressKey(Godot.Key k, bool ctrl = false, bool shift = false, bool alt = false, Viewport? vp = null)
    {
        foreach (var pressed in new[] { true, false })
        {
            using var ev = new InputEventKey { Keycode = k, PhysicalKeycode = k, CtrlPressed = ctrl, ShiftPressed = shift, AltPressed = alt, Pressed = pressed };
            (vp ?? GetViewport()).PushInput(ev, false);
        }
    }

    async Task RunOnboard()
    {
        var s = App.Settings;
        var settingsPath = AppPaths.Combine("settings.cfg");
        var wasDone = (s.WelcomeDone, s.TutorialDone, s.Language, s.Theme);
        var shortcutBackup = new Dictionary<string, string>(s.Shortcuts);
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        _w.ShowAgentPanel(false);
        _w.NewProjectNow();
        await Settle();

        // ---- first-run detection ---------------------------------------------------------------------------------------------
        Check("onboard-detect-language", () =>
        {
            foreach (var l in new[] { "ar", "ar_SA", "ar-EG", "AR_AE", "ar_EG.UTF-8" }) if (AppSettings.DetectLanguage(l) != "ar") return $"{l} should be Arabic";
            foreach (var l in new[] { "en", "en_US", "fr_FR", "de", "zh_CN", "", "arn_CL", "es_AR" }) if (AppSettings.DetectLanguage(l) != "en") return $"{l} should be English";
            return null;
        });

        Check("onboard-first-run-no-settings-file", () =>
        {
            var backup = settingsPath + ".bak";
            if (System.IO.File.Exists(settingsPath)) System.IO.File.Move(settingsPath, backup, true);
            try
            {
                var fresh = new AppSettings();
                fresh.Load();
                if (!fresh.IsFirstRun || fresh.WelcomeDone) return $"first={fresh.IsFirstRun} done={fresh.WelcomeDone}";
                if (fresh.Language != AppSettings.DetectLanguage(OS.GetLocale())) return "language is not the system language: " + fresh.Language;
                // settings written by an older version (no welcome key) must not bring the welcome screen back
                System.IO.File.WriteAllText(settingsPath, "[ui]\nlanguage=\"ar\"\ntheme=\"dark\"\n");
                var old = new AppSettings();
                old.Load();
                return !old.IsFirstRun && old.WelcomeDone && old.Language == "ar" ? null : $"legacy first={old.IsFirstRun} done={old.WelcomeDone} lang={old.Language}";
            }
            finally { if (System.IO.File.Exists(backup)) System.IO.File.Move(backup, settingsPath, true); }
        });

        // ---- welcome screen -------------------------------------------------------------------------------------------------
        var w = _w.WelcomeView;
        Check("onboard-welcome-preselects-detected", () =>
        {
            s.WelcomeDone = false; s.Language = "ar"; s.Theme = "dark";
            w.Open();
            if (!w.Visible || !w.LangAr.ButtonPressed || w.LangEn.ButtonPressed) return "Arabic not pre-selected";
            s.Language = "en";
            w.Open();
            return w.LangEn.ButtonPressed && !w.LangAr.ButtonPressed && w.ThemeDark.ButtonPressed ? null : "English/dark not pre-selected";
        });
        await Settle();
        foreach (var lang in new[] { "en", "ar" })
        {
            App.SetLanguage(lang); App.SetTheme("dark");
            w.Open();
            await Settle();
            Shot(System.IO.Path.Combine(_dir, $"p11-{lang}-dark-welcome.png"), GetViewport());
        }
        w.Open();
        Check("onboard-welcome-choice-persists", () =>
        {
            w.LangAr.EmitSignal(BaseButton.SignalName.Pressed);
            w.ThemeLight.EmitSignal(BaseButton.SignalName.Pressed);
            if (s.Language != "ar" || s.Theme != "light") return $"not applied live: {s.Language}/{s.Theme}";
            w.ContinueButton.EmitSignal(BaseButton.SignalName.Pressed);
            if (!w.OnTourPage) return "tour offer not shown";
            var disk = new AppSettings();
            disk.Load();
            if (disk.IsFirstRun || !disk.WelcomeDone || disk.Language != "ar" || disk.Theme != "light") return $"on disk: {disk.WelcomeDone} {disk.Language} {disk.Theme}";
            return null;
        });
        w.SkipButton.EmitSignal(BaseButton.SignalName.Pressed);
        App.SetLanguage("en"); App.SetTheme("dark");
        await Settle();
        Check("onboard-welcome-closed-and-reopens-from-menu", () =>
        {
            if (w.Visible) return "still visible";
            _w.Run("help.welcome");
            var ok = w.Visible;
            w.Hide();
            return ok ? null : "Settings > Welcome did not reopen it";
        });

        // ---- tutorial ---------------------------------------------------------------------------------------------------------
        var tut = _w.TutorialView;
        s.TutorialDone = false;
        Check("onboard-tutorial-help-button", () => _w.HelpButton.Visible && _w.HelpButton.Text == "?" ? null : "no ? button");
        {
            tut.Start();
            await Frames(3);
            var checkedTargets = 0;
            string? err = null;
            for (var i = 0; i < TutorialOverlay.Steps.Count && err is null; i++)
            {
                tut.Show(i);
                await Frames(3); // the spotlight follows the target as the layout settles
                var st = TutorialOverlay.Steps[i];
                if (st.Target is null) continue;
                var t = tut.CurrentTarget;
                if (t is null) err = $"step {st.Id}: target not visible";
                else if (!tut.HasSpot || tut.SpotRect.Size.X < 10) err = $"step {st.Id}: no spotlight {tut.SpotRect}";
                else if (!tut.SpotRect.Intersects(t.GetGlobalRect())) err = $"step {st.Id}: spotlight is not on the target";
                else if (!new Rect2(Vector2.Zero, tut.Size).Encloses(tut.Card.GetRect())) err = $"step {st.Id}: card leaves the window {tut.Card.GetRect()}";
                else if (tut.Card.GetRect().Intersects(tut.SpotRect.Grow(-6)) && st.Id is not ("viewport" or "timeline" or "drafts")) err = $"step {st.Id}: card covers the target";
                else checkedTargets++;
            }
            if (err is null && checkedTargets < 10) err = "only " + checkedTargets + " targets";
            if (err is null) Pass("onboard-tutorial-every-target-is-a-real-visible-node"); else Fail("onboard-tutorial-every-target-is-a-real-visible-node", err);
        }
        Check("onboard-tutorial-next-back-and-complete", () =>
        {
            tut.Show(0);
            if (tut.BackButton.Disabled is false) return "Back enabled on the first step";
            tut.Next(); tut.Next(); tut.Back();
            if (tut.Index != 1) return "Next/Back index " + tut.Index;
            for (var i = tut.Index; i < TutorialOverlay.Steps.Count - 1; i++) tut.Next();
            if (tut.Index != TutorialOverlay.Steps.Count - 1) return "not at the last step";
            tut.Next(); // Finish
            if (tut.Visible || !tut.Completed || !s.TutorialDone) return "not completed";
            var disk = new AppSettings(); disk.Load();
            return disk.TutorialDone ? null : "completion not saved";
        });
        Check("onboard-tutorial-try-it-walk-and-play", () =>
        {
            _w.NewProjectNow();
            tut.Start(); // adds a character to practise on
            if (_w.SelectedCharacter is null) return "no character";
            var done = tut.TryIt();
            return done && App.Playing && App.ActiveClip?.Name.Contains("Walk", StringComparison.OrdinalIgnoreCase) == true ? null : $"done={done} playing={App.Playing} clip={App.ActiveClip?.Name}";
        });
        Check("onboard-tutorial-escape-closes", () =>
        {
            tut.Show(3);
            PressKey(Godot.Key.Escape);
            return !tut.Visible ? null : "still open after Esc";
        });
        App.Pause();
        foreach (var lang in new[] { "en", "ar" })
        {
            App.SetLanguage(lang); App.SetTheme("dark");
            await Settle();
            tut.Start();
            var gen = TutorialOverlay.Steps.ToList().FindIndex(x => x.Id == "generate");
            tut.Show(gen);
            await Settle();
            Shot(System.IO.Path.Combine(_dir, $"p11-{lang}-dark-tutorial.png"), GetViewport());
            tut.Close(false);
        }
        App.SetLanguage("en");
        await Settle();

        // ---- shortcut map ------------------------------------------------------------------------------------------------------
        Map.ResetAll();
        Check("onboard-shortcuts-defaults-have-no-conflicts", () =>
        {
            var c = Map.AllConflicts();
            return c.Count == 0 ? null : string.Join("; ", c.Select(x => $"{x.a.Id}/{x.b.Id} {x.chord.Display()}"));
        });
        Check("onboard-shortcuts-defaults-parse", () =>
        {
            foreach (var a in ShortcutMap.All)
            {
                if (a.Defaults.Length == 0) continue;
                if (Map.ChordsOf(a.Id).Count != a.Defaults.Split('|').Length) return a.Id + " has an unreadable default: " + a.Defaults;
            }
            return Map.Display("file.save") == "Ctrl+S" && Map.Display("view.front") == "Num 1" && Map.Display("play.prev10") == "Shift+←" ? null : "display text";
        });
        Check("onboard-shortcuts-context-home", () =>
        {
            using var home = new InputEventKey { Keycode = Godot.Key.Home, Pressed = true };
            return Map.Find(home, true)?.Id == "view.frameAll" && Map.Find(home, false)?.Id == "play.start" ? null : "Home context";
        });

        var dlg = _w.ShortcutsDlg;
        Check("onboard-shortcuts-dialog-search-and-rows", () =>
        {
            dlg.Open();
            var all = dlg.RowCount;
            dlg.Search.Text = "loop"; dlg.Search.EmitSignal(LineEdit.SignalName.TextChanged, "loop");
            var some = dlg.RowCount;
            dlg.Search.Text = ""; dlg.Search.EmitSignal(LineEdit.SignalName.TextChanged, "");
            return all == ShortcutMap.All.Count && some >= 1 && some < all && dlg.RowCount == all ? null : $"rows all={all} loop={some}";
        });
        await Settle();
        Check("onboard-shortcuts-rebind-click-to-capture-and-persist", () =>
        {
            dlg.StartCapture("tool.select");
            if (dlg.CapturingId != "tool.select") return "not capturing";
            using var f5 = new InputEventKey { Keycode = Godot.Key.F5, Pressed = true };
            dlg.PushInput(f5, false);
            if (dlg.CapturingId is not null || Map.Display("tool.select") != "F5") return $"capture ended={dlg.CapturingId is null} display={Map.Display("tool.select")}";
            var disk = new AppSettings(); disk.Load();
            if (!disk.Shortcuts.TryGetValue("tool.select", out var v) || v != "F5") return "not persisted";
            return dlg.ChordTextOf("tool.select") == "F5" ? null : "dialog row not updated: " + dlg.ChordTextOf("tool.select");
        });
        Check("onboard-shortcuts-rebound-key-works-and-old-key-does-not", () =>
        {
            _w.Selection.SetTool(StageTool.Rotate);
            PressKey(Godot.Key.Q);
            if (_w.Selection.Tool != StageTool.Rotate) return "old key Q still selects";
            PressKey(Godot.Key.F5);
            return _w.Selection.Tool == StageTool.Select ? null : "F5 did not select";
        });
        Check("onboard-shortcuts-tooltip-and-menu-follow-bindings", () =>
        {
            Map.Set("play.toggle", new Chord(Godot.Key.P, false, false, true));
            var tip = _w.Timeline.PlayButton.TooltipText;
            _w.Timeline.Sync();
            tip = _w.Timeline.PlayButton.TooltipText;
            var menu = _w.MenuPopup(2);
            var txt = "";
            for (var i = 0; i < menu.ItemCount; i++) if (menu.GetItemShortcut(i) is { } sc && sc.GetAsText().Contains("Alt+P")) txt = sc.GetAsText();
            Map.Reset("play.toggle");
            _w.Timeline.Sync();
            return tip.Contains("Alt+P") && txt.Length > 0 && _w.Timeline.PlayButton.TooltipText.Contains("Space") ? null : $"tip='{tip}' menu='{txt}'";
        });
        Check("onboard-shortcuts-conflict-warns-then-replaces", () =>
        {
            var w = Map.ChordsOf("tool.move")[0];
            if (dlg.TryAssign("tool.rotate", w)) return "conflict not detected";
            if (dlg.PendingConflictId != "tool.rotate" || !dlg.WarnBar.Visible || !dlg.WarnLabel.Text.Contains("W")) return "no warning: " + dlg.WarnLabel.Text;
            if (Map.Display("tool.rotate") != "E") return "changed before confirmation";
            dlg.ConfirmReplace();
            return Map.Display("tool.rotate") == "W" && Map.ChordsOf("tool.move").Count == 0 && !dlg.WarnBar.Visible ? null : $"rotate={Map.Display("tool.rotate")} move={Map.Display("tool.move")}";
        });
        Check("onboard-shortcuts-conflict-cancel-keeps-bindings", () =>
        {
            var before = Map.Display("file.save");
            var ok = dlg.TryAssign("file.new", Map.ChordsOf("file.save")[0]);
            dlg.CancelPending();
            return !ok && Map.Display("file.save") == before && Map.Display("file.new") == "Ctrl+N" && !dlg.WarnBar.Visible ? null : "cancel changed something";
        });
        Check("onboard-shortcuts-context-allows-same-key-in-different-scopes", () =>
            Map.Conflicts("play.start", new Chord(Godot.Key.Home, false, false, false)).Count == 0 ? null : "Home flagged as conflict between viewport and timeline");
        Check("onboard-shortcuts-json-export-import-roundtrip", () =>
        {
            Map.Set("view.onion", new Chord(Godot.Key.F7, false, true, false));
            var json = Map.ExportJson();
            Map.ResetAll();
            if (Map.Display("view.onion") != "O") return "reset failed";
            var n = Map.ImportJson(json, out var err);
            if (n < 0 || Map.Display("view.onion") != "Shift+F7") return $"import n={n} err={err} onion={Map.Display("view.onion")}";
            if (Map.ImportJson("{ nope", out _) != -1 || Map.ImportJson("{\"a\":1}", out _) != -1) return "bad file accepted";
            return Map.IsCustom("view.onion") && !Map.IsCustom("file.save") ? null : "custom flags";
        });
        Check("onboard-shortcuts-reset-all", () =>
        {
            Map.ResetAll();
            var disk = new AppSettings(); disk.Load();
            return Map.Display("tool.rotate") == "E" && Map.Display("tool.move") == "W" && disk.Shortcuts.Count == 0 ? null : "not reset";
        });
        await Settle();
        Check("onboard-shortcuts-duplicate-clip-ctrl-d", () =>
        {
            _w.NewProjectNow(); _w.CreateClip();
            var n = Proj.Clips.Count;
            _w.Stage.GrabFocus();
            PressKey(Godot.Key.D, ctrl: true);
            return Proj.Clips.Count == n + 1 ? null : $"clips {n} -> {Proj.Clips.Count}";
        });

        // ---- keys never fire while typing -------------------------------------------------------------------------------------------
        Check("onboard-shortcuts-silent-in-text-fields", () =>
        {
            var edit = new LineEdit();
            _w.AddChild(edit);
            edit.GrabFocus();
            var playing = App.Playing; var tool = _w.Selection.Tool; var clips = Proj.Clips.Count;
            PressKey(Godot.Key.Space); PressKey(Godot.Key.W); PressKey(Godot.Key.Q); PressKey(Godot.Key.L); PressKey(Godot.Key.K);
            PressKey(Godot.Key.D, ctrl: true); PressKey(Godot.Key.Delete);
            var quiet = App.Playing == playing && _w.Selection.Tool == tool && Proj.Clips.Count == clips && !_w.CheatSheetView.Visible;
            edit.ReleaseFocus();
            edit.QueueFree();
            return quiet ? null : "a shortcut fired inside the text field";
        });
        Check("onboard-shortcuts-fire-outside-text-fields", () =>
        {
            _w.Stage.GrabFocus();
            _w.Selection.SetTool(StageTool.Select);
            PressKey(Godot.Key.W);
            if (_w.Selection.Tool != StageTool.Move) return "W did not select Move";
            PressKey(Godot.Key.Q);
            return _w.Selection.Tool == StageTool.Select ? null : "Q did not select Select";
        });

        // ---- cheat sheet -------------------------------------------------------------------------------------------------------------
        var cs = _w.CheatSheetView;
        dlg.Hide();
        await Frames(2);
        Check("onboard-cheatsheet-opens-with-f1-and-closes", () =>
        {
            _w.Stage.GrabFocus();
            PressKey(Godot.Key.F1);
            if (!cs.Visible) return "F1 did not open it";
            var expected = ShortcutMap.All.Count(a => Map.ChordsOf(a.Id).Count > 0);
            if (cs.RowsShown != expected) return $"rows {cs.RowsShown} != {expected}";
            PressKey(Godot.Key.F1);
            if (cs.Visible) return "F1 did not close it";
            PressKey(Godot.Key.Slash, shift: true);
            if (!cs.Visible) return "? did not open it";
            PressKey(Godot.Key.Escape);
            return !cs.Visible ? null : "Esc did not close it";
        });
        Check("onboard-cheatsheet-search-and-print-html", () =>
        {
            cs.Open();
            cs.Search.Text = "undo"; cs.Search.EmitSignal(LineEdit.SignalName.TextChanged, "undo");
            var n = cs.RowsShown;
            cs.Search.Text = ""; cs.Search.EmitSignal(LineEdit.SignalName.TextChanged, "");
            var html = cs.BuildHtml();
            cs.Close();
            return n is >= 1 and <= 3 && html.Contains("Ctrl+S") && html.Contains("dir=\"ltr\"") && html.Contains("@page") ? null : $"search rows={n}";
        });
        foreach (var lang in new[] { "en", "ar" })
        {
            App.SetLanguage(lang); App.SetTheme("dark");
            await Settle();
            dlg.Open();
            await Settle();
            Shot(System.IO.Path.Combine(_dir, $"p11-{lang}-dark-shortcuts.png"), dlg);
            dlg.Hide();
            cs.Open();
            await Settle();
            Shot(System.IO.Path.Combine(_dir, $"p11-{lang}-dark-cheatsheet.png"), GetViewport());
            if (lang == "ar") Check("onboard-cheatsheet-arabic-html-is-rtl", () => cs.BuildHtml().Contains("dir=\"rtl\"") ? null : "not RTL");
            cs.Close();
        }
        App.SetLanguage("en");

        // ---- planner hints in the Generate dialog -------------------------------------------------------------------------------
        Check("onboard-plan-typo-hint-shown", () =>
        {
            _w.NewProjectNow(); _w.AddMannequin();
            _w.OpenGenerateDialog();
            _w.GenerateDlg.Prompt.Text = "يركظ";
            _w.GenerateDlg.ApplySuggestion("zzz", "x"); // no-op for a word that is not there
            _w.GenerateDlg.Prompt.EmitSignal(TextEdit.SignalName.TextChanged);
            return null;
        });
        await Settle(); await Settle();
        Check("onboard-plan-typo-hint-content", () =>
        {
            var h = _w.GenerateDlg.Hints;
            var ok = h.Count == 1 && h[0].Word == "يركظ" && h[0].Applied && h[0].Candidate == "يركض" && h[0].Message.Contains("هل تقصد");
            _w.GenerateDlg.Hide();
            return ok ? null : "hints: " + string.Join("; ", h.Select(x => $"{x.Word}->{x.Candidate} applied={x.Applied}"));
        });

        // restore
        Map.ResetAll();
        foreach (var (k, v) in shortcutBackup) s.Shortcuts[k] = v;
        s.WelcomeDone = wasDone.Item1; s.TutorialDone = wasDone.Item2;
        App.SetLanguage(wasDone.Item3); App.SetTheme(wasDone.Item4);
        s.Save();
        await Settle();
    }
}
