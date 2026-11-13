using System.Text.Json.Nodes;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Scripted checks that drive the real UI code paths, used by scripts/run-ui-tests.sh.
/// Enabled with user args after "--":  --selftest=DIR | --crashtest-phase1=DIR | --crashtest-phase2=DIR
/// </summary>
public partial class SelfTest : Node
{
    readonly MainWindow _w;
    readonly string _mode, _dir;
    int _failures;

    static AppController App => AppController.Instance;

    public static bool IsRequested => Parse() is not null;

    static (string mode, string dir)? Parse()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            foreach (var m in new[] { "selftest", "crashtest-phase1", "crashtest-phase2" })
                if (a.StartsWith($"--{m}=")) return (m, a[(m.Length + 3)..]);
        return null;
    }

    public static SelfTest? TryCreate(MainWindow w) => Parse() is { } p ? new SelfTest(w, p.mode, p.dir) : null;

    SelfTest(MainWindow w, string mode, string dir) { _w = w; _mode = mode; _dir = System.IO.Path.GetFullPath(dir); }

    public override async void _Ready()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_dir);
            _w.GenerateDlg.AutoCloseAfterApply = false;
            await Frames(3);
            switch (_mode)
            {
                case "selftest": await RunSelfTest(); break;
                case "crashtest-phase1": await CrashPhase1(); break;
                case "crashtest-phase2": await CrashPhase2(); break;
            }
        }
        catch (Exception e)
        {
            Fail("unhandled-exception", e.ToString());
            Finish();
        }
    }

    // ---- helpers -----------------------------------------------------------------------------

    async Task Frames(int n)
    {
        for (var i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    async Task Settle() { await Frames(3); await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout); await Frames(2); }

    void Pass(string name) { GD.Print($"SELFTEST PASS {name}"); }
    void Fail(string name, string reason) { _failures++; GD.Print($"SELFTEST FAIL {name}: {reason}"); }

    void Check(string name, Func<string?> body)
    {
        try { var err = body(); if (err is null) Pass(name); else Fail(name, err); }
        catch (Exception e) { Fail(name, e.Message); }
    }

    void Finish()
    {
        if (System.Environment.GetEnvironmentVariable("BLAZMA_ORPHANS") == "1") Node.PrintOrphanNodes();
        GD.Print($"SELFTEST RESULT {(_failures == 0 ? "PASS" : "FAIL")}");
        try { App.ShutdownCleanly(); } catch (Exception e) { GD.Print($"shutdown error: {e.Message}"); }
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    static Blazma.Core.Model.Project Proj => App.Session.Project;
    static Blazma.Core.Model.Clip? ClipNamed(string n) => Proj.Clips.Find(c => c.Name == n);

    void Shot(string path, Viewport vp)
    {
        var img = vp.GetTexture().GetImage();
        var err = img.SavePng(path);
        if (err != Error.Ok) Fail("screenshot " + System.IO.Path.GetFileName(path), err.ToString());
        else Pass("screenshot " + System.IO.Path.GetFileName(path) + $" ({img.GetWidth()}x{img.GetHeight()})");
    }

    void Click(Vector2 pos)
    {
        foreach (var pressed in new[] { true, false })
            GetViewport().PushInput(new InputEventMouseButton { Position = pos, GlobalPosition = pos, ButtonIndex = MouseButton.Left, Pressed = pressed, ButtonMask = pressed ? MouseButtonMask.Left : 0 }, false);
    }

    void Key(Godot.Key k, bool shift = false)
    {
        foreach (var pressed in new[] { true, false })
            GetViewport().PushInput(new InputEventKey { Keycode = k, PhysicalKeycode = k, CtrlPressed = true, ShiftPressed = shift, Pressed = pressed }, false);
    }

    /// <summary>Development shortcut: BLAZMA_ONLY_PHASES=2,5 runs only those of phases 2-7 (then finishes).</summary>
    static bool WantPhase(int n) =>
        System.Environment.GetEnvironmentVariable("BLAZMA_ONLY_PHASES") is not { } only || only.Split(',').Contains(n.ToString());

    // ---- main scenario -----------------------------------------------------------------------

    async Task RunSelfTest()
    {
        var s = App.Settings;
        var backup = (s.Language, s.Theme, s.AdvancedMode, s.AutosaveMinutes);
        var layoutBackup = new Dictionary<string, Variant>(s.Layout);
        var recentBackup = s.RecentFiles.ToList();
        var path = System.IO.Path.Combine(_dir, "selftest.blazanim");
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);

        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        await Settle();

        string idle = "", walk = "", jump = "";

        Check("new-project", () =>
        {
            _w.NewProjectNow();
            return Proj.Name == "Untitled" && Proj.Clips.Count == 0 && !App.IsDirty ? null : $"name={Proj.Name} clips={Proj.Clips.Count} dirty={App.IsDirty}";
        });

        Check("create-two-clips", () =>
        {
            _w.CreateClip();
            _w.CreateClip();
            return Proj.Clips.Count == 2 && Proj.Clips[0].Name == "Clip 1" && Proj.Clips[1].Name == "Clip 2" && App.IsDirty
                && _w.SelectedClipId == Proj.Clips[1].Id.ToString() ? null : "clips: " + string.Join(",", Proj.Clips.Select(c => c.Name));
        });
        idle = Proj.Clips[0].Id.ToString();
        walk = Proj.Clips[1].Id.ToString();

        Check("rename-clip", () =>
            _w.RenameClipNow(walk, "Walk Cycle") && ClipNamed("Walk Cycle") is not null ? null : "rename failed");

        Check("inspector-edit-project-name-and-fps", () =>
        {
            _w.Inspector.ProjectName.Text = "Selftest Project";
            _w.Inspector.CommitProjectName();
            _w.Inspector.FrameRateBox.Select(1);
            _w.Inspector.FrameRateBox.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
            return Proj.Name == "Selftest Project" && Proj.Settings.DefaultFrameRate.Numerator == 25 && Proj.Settings.DefaultFrameRate.Denominator == 1
                ? null : $"name={Proj.Name} fps={Proj.Settings.DefaultFrameRate}";
        });

        Check("inspector-edit-clip", () =>
        {
            _w.SelectClip(idle);
            _w.Inspector.ClipName.Text = "Idle";
            _w.Inspector.CommitClipName();
            _w.Inspector.Length.Value = 48;
            var c = Proj.Clips[0];
            return c.Name == "Idle" && c.DurationFrames == 48 ? null : $"name={c.Name} frames={c.DurationFrames}";
        });

        Check("create-clip-uses-project-fps", () =>
        {
            jump = _w.CreateClipNamed("Jump Attack") ?? "";
            var c = ClipNamed("Jump Attack");
            return c is not null && c.Fps.Numerator == 25 && _w.SetClipDurationNow(jump, 72) && c.DurationFrames == 72 ? null : "bad clip";
        });

        Check("undo-redo-via-shortcuts", () =>
        {
            var c = ClipNamed("Jump Attack")!;
            Key(Godot.Key.Z);
            if (c.DurationFrames == 72) return "Ctrl+Z did not undo";
            var afterUndo = c.DurationFrames;
            Key(Godot.Key.Z, shift: true);
            if (c.DurationFrames != 72) return $"Ctrl+Shift+Z did not redo (was {afterUndo}, now {c.DurationFrames})";
            Key(Godot.Key.Z);
            if (c.DurationFrames == 72) return "second undo failed";
            Key(Godot.Key.Y);
            return c.DurationFrames == 72 ? null : "Ctrl+Y did not redo";
        });

        Check("validation-error-toast", () =>
        {
            _w.SelectClip(idle);
            _w.Inspector.ClipName.Text = "";
            _w.Inspector.CommitClipName();
            if (!_w.ToastNode.Showing) return "toast not shown";
            return Proj.Clips[0].Name == "Idle" && _w.Inspector.ClipName.Text == "Idle" ? null : "name not restored: " + _w.Inspector.ClipName.Text;
        });

        Check("save", () =>
        {
            if (!_w.SaveToPath(path)) return "SaveToPath returned false";
            return System.IO.File.Exists(path) && !App.IsDirty && App.FilePath == path ? null : $"exists={System.IO.File.Exists(path)} dirty={App.IsDirty}";
        });

        Check("new-then-open", () =>
        {
            _w.NewProjectNow();
            if (Proj.Clips.Count != 0) return "new project not empty";
            if (!_w.OpenPath(path)) return "OpenPath failed: " + _w.LastError;
            var names = string.Join(",", Proj.Clips.Select(c => $"{c.Name}:{c.DurationFrames}"));
            return Proj.Name == "Selftest Project" && names == "Idle:48,Walk Cycle:30,Jump Attack:72" ? null : "got " + Proj.Name + " " + names;
        });

        s.AdvancedMode = false;
        App.SetAdvancedMode(true);
        await Settle();

        Check("history-panel-goto", () =>
        {
            _w.CreateClipNamed("Run Cycle");
            _w.SetClipDurationNow(_w.SelectedClipId!, 30);
            _w.Inspector.ProjectName.Text = "Selftest Project 2";
            _w.Inspector.CommitProjectName();
            if (_w.HistoryPanel.List.ItemCount != 4) return $"history rows={_w.HistoryPanel.List.ItemCount}, expected 4";
            _w.HistoryPanel.GoTo(1);
            if (Proj.Clips.Count != 4 || Proj.Name != "Selftest Project") return $"after GoTo(1): clips={Proj.Clips.Count} name={Proj.Name}";
            _w.HistoryPanel.GoTo(0);
            if (Proj.Clips.Count != 3) return "GoTo(0) did not undo creation";
            _w.HistoryPanel.GoTo(3);
            return Proj.Clips.Count == 4 && Proj.Name == "Selftest Project 2" && ClipNamed("Run Cycle")!.DurationFrames == 30 ? null : "redo path wrong";
        });

        Check("outline-selection-sync", () =>
        {
            _w.SelectClip(idle);
            var sel = _w.OutlinePanel.Tree.GetSelected();
            return sel is not null && sel.GetMetadata(0).AsString() == idle && _w.Strip.Cards[idle] is not null ? null : "tree not synced";
        });

        Check("delete-clip-and-undo", () =>
        {
            var before = Proj.Clips.Count;
            var id = Proj.Clips[^1].Id.ToString();
            _w.DeleteClipNow(id);
            if (Proj.Clips.Count != before - 1) return "delete failed";
            _w.DoUndo();
            return Proj.Clips.Count == before ? null : "undo of delete failed";
        });

        _w.SelectClip(walk);
        await Frames(2);

        _w.Timeline.SelectTab(TimelinePanel.TabClips);
        await Frames(3);
        Check("strip-card-click-selects", () =>
        {
            var card = _w.Strip.Cards[jump];
            var pos = card.GetGlobalRect().GetCenter();
            Click(pos);
            return _w.SelectedClipId == jump ? null : "click did not select the card";
        });

        Check("viewport-orbit-pan-zoom-reset", () =>
        {
            var st = _w.Stage;
            var center = st.GetGlobalRect().GetCenter() + new Vector2(0, 60);
            var yaw0 = st.Yaw; var dist0 = st.Distance; var tgt0 = st.Target;
            GetViewport().PushInput(new InputEventMouseMotion { Position = center, GlobalPosition = center, Relative = new Vector2(40, 10), ButtonMask = MouseButtonMask.Middle }, false);
            if (Mathf.IsEqualApprox(st.Yaw, yaw0)) return "MMB drag did not orbit";
            var yaw1 = st.Yaw;
            GetViewport().PushInput(new InputEventMouseMotion { Position = center, GlobalPosition = center, Relative = new Vector2(30, 0), ButtonMask = MouseButtonMask.Middle, ShiftPressed = true }, false);
            if (st.Target.IsEqualApprox(tgt0)) return "Shift+MMB did not pan";
            if (!Mathf.IsEqualApprox(st.Yaw, yaw1)) return "pan changed yaw";
            GetViewport().PushInput(new InputEventMouseButton { Position = center, GlobalPosition = center, ButtonIndex = MouseButton.WheelUp, Pressed = true, Factor = 1 }, false);
            if (st.Distance >= dist0) return "wheel did not zoom in";
            st.GrabFocus();
            GetViewport().PushInput(new InputEventKey { Keycode = Godot.Key.Home, PhysicalKeycode = Godot.Key.Home, Pressed = true }, false);
            // Home frames every character (an empty stage resets the view).
            string? err = null;
            if (_w.Overlay.AllBounds() is { } all) { if (st.Target.DistanceTo(all.GetCenter()) > 1e-3f) err = "Home did not frame all"; }
            else if (!(Mathf.IsEqualApprox(st.Yaw, ViewportView.DefaultYaw) && Mathf.IsEqualApprox(st.Distance, ViewportView.DefaultDistance) && st.Target.IsEqualApprox(ViewportView.DefaultTarget))) err = "Home did not reset the view";
            st.ResetView(); // later scenarios start from the default camera
            return err;
        });

        _w.SelectClip(walk);
        _w.ToastNode.HideNow();

        // Screenshots ---------------------------------------------------------------------------
        (string lang, string theme, bool adv)[] shots =
        {
            ("en", "dark", false), ("en", "dark", true), ("ar", "dark", false), ("ar", "dark", true),
            ("en", "light", false), ("ar", "light", true),
        };
        foreach (var (lang, theme, adv) in shots)
        {
            App.SetLanguage(lang); App.SetTheme(theme); App.SetAdvancedMode(adv);
            await Settle();
            var name = $"{lang}-{theme}-{(adv ? "advanced" : "simple")}";
            Check("rtl-state " + name, () =>
            {
                var rtl = lang == "ar";
                if (_w.IsLayoutRtl() != rtl) return $"root rtl={_w.IsLayoutRtl()}";
                if (_w.Stage.IsLayoutRtl()) return "viewport is mirrored";
                if (_w.Strip.Cards.Count > 0 && _w.Strip.GetChild(0) is null) return "strip missing";
                return null;
            });
            Shot(System.IO.Path.Combine(_dir, name + ".png"), GetViewport());
        }

        // Toast
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        await Settle();
        _w.Inspector.ClipName.Text = "";
        _w.Inspector.CommitClipName();
        await Frames(4);
        Shot(System.IO.Path.Combine(_dir, "en-dark-simple-toast.png"), GetViewport());
        _w.ToastNode.HideNow();

        // Dialogs in Arabic (they are separate windows)
        App.SetLanguage("ar");
        await Settle();
        await ShotDialog("ar-dark-dialog-unsaved", _w.UnsavedDialog, () => _w.RequestNew());
        _w.UnsavedDialog.EmitSignal(Window.SignalName.CloseRequested);
        _w.UnsavedDialog.Hide();
        await ShotDialog("ar-dark-dialog-delete", _w.DeleteDialog, () => _w.ConfirmDeleteClip(walk));
        _w.DeleteDialog.Hide();
        await ShotDialog("ar-dark-dialog-rename", _w.NameDialog, () => _w.PromptRenameClip(walk));
        _w.NameDialog.Hide();

        // Menu popups (separate windows) in Arabic, to check mirroring.
        App.SetLanguage("ar"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        await Settle();
        await ShotMenu(0, "ar-dark-menu-project");
        await ShotMenu(4, "ar-dark-menu-settings");
        await ShotMenu(3, "ar-dark-menu-rigging");
        App.SetLanguage("en");
        await Settle();
        await ShotMenu(1, "en-dark-menu-edit");

        if (System.Environment.GetEnvironmentVariable("BLAZMA_ONLY_CAM") == "1")
        {
            // Development shortcut: only the camera scenario.
            await RunCamera();
            Finish();
            return;
        }

        if (System.Environment.GetEnvironmentVariable("BLAZMA_ONLY_REELMODELS") == "1")
        {
            await RunReelModels();
            Finish();
            return;
        }

        if (System.Environment.GetEnvironmentVariable("BLAZMA_ONLY_MODELS") == "1")
        {
            // Development shortcut: only the AI models scenario.
            await RunModels();
            Finish();
            return;
        }

        if (System.Environment.GetEnvironmentVariable("BLAZMA_ONLY_CUDA") == "1")
        {
            // Development shortcut: only the optional CUDA backend scenario (D47).
            await RunCuda();
            Finish();
            return;
        }

        if (System.Environment.GetEnvironmentVariable("BLAZMA_ONLY_POLISH") == "1")
        {
            await RunPolish();
            Finish();
            return;
        }

        if (System.Environment.GetEnvironmentVariable("BLAZMA_ONLY_ONBOARD") == "1")
        {
            await RunOnboard();
            Finish();
            return;
        }

        if (System.Environment.GetEnvironmentVariable("BLAZMA_ONLY_P9") == "1")
        {
            // Development shortcut: only the Phase 9 scenario (it starts from a new project).
            await RunPhase9();
            Finish();
            return;
        }

        if (System.Environment.GetEnvironmentVariable("BLAZMA_ONLY_PLAN") == "1")
        {
            // Development shortcut: only the motion prompt planner scenario (D45).
            await RunPlan();
            Finish();
            return;
        }

        if (System.Environment.GetEnvironmentVariable("BLAZMA_ONLY_P8") == "1")
        {
            // Development shortcut: only the Phase 8 scenario (it starts from a new project).
            await RunPhase8();
            Finish();
            return;
        }

        // Phase 2: characters, posing, IK, pose library, import and bone mapping.
        if (WantPhase(2)) await RunPhase2();

        // Phase 3: timeline, keys, playback, dope sheet, graph editor, markers, trail and onion skin.
        if (WantPhase(3)) await RunPhase3();

        // Phase 4: export dialog, .glb written and re-imported by Godot.
        if (WantPhase(4)) await RunPhase4();

        // Phase 5: the app hosts the agent API; AI Workspace panel.
        if (WantPhase(5)) await RunPhase5();

        // Phase 6: balance overlays, timeline strip, Physics panel, auto-balance and secondary motion.
        if (WantPhase(6)) await RunPhase6();

        // Phase 7: text-to-motion dialog, drafts, AI settings.
        if (WantPhase(7)) await RunPhase7();

        // Motion prompt planner (D45): multi-step prompts in the Generate dialog.
        if (WantPhase(7)) await RunPlan();

        if (System.Environment.GetEnvironmentVariable("BLAZMA_ONLY_PHASES") is not null) { Finish(); return; }

        // AI models window: hardware, recommendation, licences, download against a local fake server.
        await RunModels();

        // Optional CUDA backend download in Settings → AI (D47), against the same kind of local server.
        await RunCuda();

        // Phase 8: templates, retarget, foot lock, sequences, props.
        await RunPhase8();

        // Phase 9: combat tools (phases, events, hitboxes, hurtboxes, hit test, reactions, combos).
        await RunPhase9();

        // Viewport camera: fly mode, framing, numpad views, camera settings.
        await RunCamera();

        // First launch, tutorial and keyboard shortcuts.
        await RunOnboard();

        // UI audit fixes: file association, translations, autosave thread, file errors.
        await RunPolish();

        // Restore the user's settings.
        App.SetLanguage(backup.Language); App.SetTheme(backup.Theme); App.SetAdvancedMode(backup.AdvancedMode);
        App.SetAutosaveMinutes(backup.AutosaveMinutes);
        s.Layout.Clear();
        foreach (var (k, v) in layoutBackup) s.Layout[k] = v;
        s.RecentFiles.Clear();
        s.RecentFiles.AddRange(recentBackup);
        s.Save();
        Check("settings-restored", () =>
            s.Language == backup.Language && s.Theme == backup.Theme && s.AdvancedMode == backup.AdvancedMode ? null : "not restored");

        Finish();
    }

    async Task ShotDialog(string name, Window dlg, Action open)
    {
        try
        {
            open();
            await Settle();
            if (!dlg.Visible) { Fail("dialog " + name, "not visible"); return; }
            Shot(System.IO.Path.Combine(_dir, name + ".png"), dlg);
        }
        catch (Exception e) { Fail("dialog " + name, e.Message); }
    }

    async Task ShotMenu(int index, string name)
    {
        try
        {
            var popup = _w.MenuPopup(index);
            popup.Position = new Vector2I(120, 80);
            popup.Popup();
            await Settle();
            Shot(System.IO.Path.Combine(_dir, name + ".png"), popup);
            popup.Hide();
            await Frames(2);
        }
        catch (Exception e) { Fail("menu screenshot " + name, e.Message); }
    }

    // ---- crash recovery ----------------------------------------------------------------------

    async Task CrashPhase1()
    {
        _w.NewProjectNow();
        _w.CreateClipNamed("Before crash");
        App.AutosaveNowAndWait();
        if (App.LastAutosaveUtc is null) { Fail("crash-phase1-autosave", "no autosave was written"); Finish(); return; }
        await Frames(2);
        System.IO.File.WriteAllText(System.IO.Path.Combine(_dir, "phase1.ok"), "ok");
        GD.Print("CRASHTEST PHASE1 READY (killing process now)");
        System.Console.Out.Flush();
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }

    async Task CrashPhase2()
    {
        await Settle();
        var dlg = _w.RecoveryDialogNode;
        Check("recovery-dialog-shown", () =>
        {
            if (dlg is null || !IsInstanceValid(dlg) || !dlg.Visible) return "dialog not visible";
            var texts = string.Join(" // ", dlg.RowTexts);
            GD.Print("recovery rows: " + texts);
            return dlg.Candidates.Any(c => c.ProjectName == "Untitled") ? null : "no candidate named Untitled: " + texts;
        });
        if (dlg is null || !IsInstanceValid(dlg)) { Finish(); return; }

        Shot(System.IO.Path.Combine(_dir, "recovery-dialog.png"), dlg);

        var index = -1;
        Check("recovery-candidate-contains-clip", () =>
        {
            for (var i = 0; i < dlg.Candidates.Count; i++)
            {
                var c = dlg.Candidates[i];
                if (c.ProjectName != "Untitled") continue;
                var p = App.Recovery.Recover(c);
                if (p.Clips.Any(x => x.Name == "Before crash")) { index = i; return null; }
            }
            return "no Untitled candidate holds 'Before crash'";
        });
        if (index < 0) { Finish(); return; }

        Check("recover-restores-project", () =>
        {
            if (!dlg.RecoverAt(index)) return "RestoreFrom failed: " + _w.LastError;
            return ClipNamed("Before crash") is not null && App.Session.IsDirty ? null
                : $"clip={(ClipNamed("Before crash") is not null)} dirty={App.Session.IsDirty}";
        });
        await Settle();
        Shot(System.IO.Path.Combine(_dir, "after-recovery.png"), GetViewport());
        Finish();
    }
}
