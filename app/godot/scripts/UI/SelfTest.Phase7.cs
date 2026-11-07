using System.Globalization;
using Blazma.AI;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 7 scenario: text-to-motion through the real dialog and panels. The procedural provider (built in) runs the whole path; the
/// Kimodo path runs against tests/Blazma.FakeKimodoRunner, configured through the real AI settings dialog (so settings, the off-thread
/// Check, availability and cancelling a hanging runner are all exercised). Ctrl+Z is the real shortcut.
/// </summary>
public partial class SelfTest
{
    async Task<bool> WaitFor(Func<bool> condition, double seconds)
    {
        var t0 = Godot.Time.GetTicksMsec();
        while (!condition())
        {
            if ((Godot.Time.GetTicksMsec() - t0) / 1000.0 > seconds) return false;
            await Frames(1);
        }
        return true;
    }

    static void PressButton(BaseButton b) => b.EmitSignal(BaseButton.SignalName.Pressed);

    static string FakeRunnerPath()
    {
        if (System.Environment.GetEnvironmentVariable("BLAZMA_FAKE_RUNNER") is { Length: > 0 } env) return env;
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(Godot.ProjectSettings.GlobalizePath("res://"), "../../tests/Blazma.FakeKimodoRunner/bin/Debug/net10.0/fake-kimodo-runner"));
    }

    int KeyTotal(Clip c) => c.Tracks.Sum(t => t.Keys.Count);

    Clip? DraftNamedLike(string prefix) => Proj.Clips.FirstOrDefault(c => c.Name.StartsWith(prefix, StringComparison.Ordinal));

    void UndoKey()
    {
        GetViewport().GuiReleaseFocus();
        Key(Godot.Key.Z);
    }

    async Task RunPhase7()
    {
        var s = App.Settings;
        var saved = (s.KimodoRunner, s.KimodoModels, s.KimodoDevice, s.KimodoTimeoutSeconds);
        var kimodo = App.Kimodo!;
        var envSettings = kimodo.Settings;
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        _w.ShowAgentPanel(false);
        _w.NewProjectNow();
        _w.Timeline.SelectTab(TimelinePanel.TabTimeline);
        // Deterministic: nothing is configured, whatever the environment of the test machine says.
        s.KimodoRunner = ""; s.KimodoModels = "";
        kimodo.Settings = new KimodoSettings();
        await Settle();
        var man = Ch();
        var dlg = _w.GenerateDlg;

        // ---- entry points --------------------------------------------------------------------------------------------
        Check("p7 Simple mode has a prominent 'Generate motion…' button in the timeline header and an entry in the Animation menu", () =>
        {
            if (s.AdvancedMode) return "not simple mode";
            var b = _w.Timeline.GenerateButton;
            if (!b.IsVisibleInTree() || b.Disabled || b.ThemeTypeVariation != "Primary") return $"button visible={b.IsVisibleInTree()} disabled={b.Disabled} variation={b.ThemeTypeVariation}";
            var menu = _w.MenuPopup(2);
            for (var i = 0; i < menu.ItemCount; i++)
                if (menu.GetItemText(i) == "Generate Motion…") return menu.IsItemDisabled(i) ? "menu item disabled" : null;
            return "no menu item";
        });

        _w.Timeline.GenerateButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Settle();
        Check("p7 the button opens the dialog; Kimodo is not configured so the Procedural generator is preselected", () =>
            dlg.IsOpen && dlg.SelectedProviderId == AiText.ProceduralId && dlg.Character.ItemCount == 1 ? null
                : $"open={dlg.IsOpen} provider={dlg.SelectedProviderId} chars={dlg.Character.ItemCount}");

        dlg.SelectProvider(AiText.KimodoId);
        dlg.Prompt.Text = "a person walks forward";
        dlg.Prompt.EmitSignal(TextEdit.SignalName.TextChanged);
        await Settle();
        Check("p7 Kimodo is shown as 'not installed' with the reason, a way to the settings, and Generate is disabled", () =>
        {
            if (dlg.ProviderBadge != "Kimodo: not installed") return "badge: " + dlg.ProviderBadge;
            if (!dlg.ProviderNote.Contains("not set up") || !dlg.ProviderNote.Contains("Nothing is downloaded automatically")) return "note: " + dlg.ProviderNote;
            if (!dlg.Setup.Visible) return "no settings button";
            if (dlg.Provider.GetItemText(0) != "Kimodo (local AI) — not installed") return "item: " + dlg.Provider.GetItemText(0);
            return dlg.GenerateEnabled ? "Generate is enabled" : null;
        });
        Shot(P("p7-en-dark-dialog-kimodo-missing"), dlg);

        dlg.SelectProvider(AiText.ProceduralId);
        await Settle();
        Check("p7 Procedural is labelled 'not AI' and lists the words it understands", () =>
            dlg.ProviderBadge == "Procedural — not AI" && dlg.KeywordsVisible && new[] { "walk", "run", "jump", "wave", "squat", "idle" }.All(dlg.KeywordsText.Contains) && dlg.GenerateEnabled
                ? null : $"badge={dlg.ProviderBadge} keywords={dlg.KeywordsText} enabled={dlg.GenerateEnabled}");

        // ---- procedural "walk": job -> auto-applied draft that plays ------------------------------------------------
        dlg.Prompt.Text = "walk";
        dlg.Prompt.EmitSignal(TextEdit.SignalName.TextChanged);
        dlg.Seed.Value = 7; dlg.Duration.Value = 2;
        var clipsBefore = Proj.Clips.Count;
        Shot(P("p7-en-dark-dialog"), dlg);
        dlg.PressGenerate();
        Check("p7 Generate starts a job through motion.generate and shows progress with a Cancel button", () =>
            dlg.CurrentJobId is { } id && App.IsUiJob(id) && App.MotionJobs.Get(id) is not null ? null : "no job");
        var applied = await WaitFor(() => _w.LastMotionApply is not null, 15);
        await Settle();
        Clip? draft = null;
        Check("p7 when the job is ready the draft clip is added on the main thread, selected and playing", () =>
        {
            if (!applied) return "timeout; job=" + App.MotionJobs.Get(dlg.CurrentJobId ?? "")?.State + " err=" + _w.LastMotionApplyError;
            draft = Proj.Clips.Find(c => c.Id.ToString() == _w.LastMotionApply!.ClipId);
            if (draft is null || Proj.Clips.Count != clipsBefore + 1) return "no new clip";
            if (!draft.IsDraft) return "not a draft";
            if (draft.Metadata.GetValueOrDefault("provider") != "procedural" || draft.Metadata.GetValueOrDefault("seed") != "7") return "metadata: " + string.Join(",", draft.Metadata);
            if (KeyTotal(draft) == 0 || !AppController.HasKeys(draft)) return "no keys";
            if (App.ActiveClipId != draft.Id.ToString()) return "not selected";
            return App.Playing ? null : "not playing";
        });
        Check("p7 the clip really plays: the frame moves", () =>
        {
            var f0 = App.CurrentFrameExact;
            App.AdvancePlayback(0.25);
            return App.CurrentFrameExact > f0 ? null : $"frame {f0} -> {App.CurrentFrameExact}";
        });
        Check("p7 the dialog reports success and the notes (fingers/face are not generated)", () =>
            dlg.StatusText.StartsWith("Done") && dlg.DetailsText.Contains("Draft clip: ") ? null : dlg.StatusText + " // " + dlg.DetailsText);
        Check("p7 the timeline header shows the Draft badge with Accept and Discard; the clip picker says (Draft)", () =>
        {
            var t = _w.Timeline;
            if (!t.DraftBar.Visible || !t.AcceptDraftButton.IsVisibleInTree() || !t.DiscardDraftButton.IsVisibleInTree()) return "draft bar hidden";
            return t.ClipPick.Selected >= 0 && t.ClipPick.GetItemText(t.ClipPick.Selected).EndsWith("(Draft)") ? null : $"picker: selected={t.ClipPick.Selected} items={t.ClipPick.ItemCount} text='{(t.ClipPick.ItemCount > 0 ? t.ClipPick.GetItemText(0) : "")}' active={App.ActiveClipId}";
        });
        Shot(P("p7-en-dark-dialog-done"), dlg);
        dlg.Hide();
        await Settle();
        Shot(P("p7-en-dark-generated"), GetViewport());
        var draftId = draft!.Id.ToString();

        // ---- Accept / Discard and undo for every step ---------------------------------------------------------------
        PressButton(_w.Timeline.AcceptDraftButton);
        await Settle();
        Check("p7 Accept makes it a normal clip: no badge, no draft", () =>
            Proj.Clips.Find(c => c.Id.ToString() == draftId) is { IsDraft: false } && !_w.Timeline.DraftBar.Visible ? null : "still a draft");
        UndoKey();
        await Settle();
        Check("p7 Ctrl+Z after Accept makes it a draft again", () =>
            Proj.Clips.Find(c => c.Id.ToString() == draftId) is { IsDraft: true } && _w.Timeline.DraftBar.Visible ? null : "not a draft again");
        PressButton(_w.Timeline.DiscardDraftButton);
        await Settle();
        Check("p7 Discard removes the draft clip and offers Undo in the toast", () =>
            Proj.Clips.All(c => c.Id.ToString() != draftId) && _w.ToastNode.ActionText == "Undo" ? null : $"clips={Proj.Clips.Count} toast={_w.ToastNode.Message}/{_w.ToastNode.ActionText}");
        UndoKey();
        await Settle();
        Check("p7 Ctrl+Z after Discard brings the draft back", () =>
            Proj.Clips.Find(c => c.Id.ToString() == draftId) is { IsDraft: true } && KeyTotal(Proj.Clips.Find(c => c.Id.ToString() == draftId)!) > 0 ? null : "not back");
        UndoKey();
        await Settle();
        Check("p7 one more Ctrl+Z undoes the 'add generated motion' step itself; Ctrl+Shift+Z redoes it", () =>
        {
            if (Proj.Clips.Count != clipsBefore) return "clip still there: " + Proj.Clips.Count;
            GetViewport().GuiReleaseFocus();
            Key(Godot.Key.Z, shift: true);
            return null;
        });
        await Settle();
        Check("p7 redo gives the same draft clip back", () => Proj.Clips.Find(c => c.Id.ToString() == draftId) is { IsDraft: true } ? null : "not redone");

        // ---- AI settings with the fake runner ---------------------------------------------------------------------------
        var fake = FakeRunnerPath();
        var modelsDir = System.IO.Path.Combine(_dir, "kimodo-models");
        System.IO.Directory.CreateDirectory(modelsDir);
        foreach (var f in new[] { "kimodo-soma-rp-v1.1-f32.gguf", "Llama-3-Kimodo-Q8_0.gguf", "tokenizer.gguf" })
            System.IO.File.WriteAllText(System.IO.Path.Combine(modelsDir, f), "x");
        Check("p7 the fake Kimodo runner exists (built by scripts/run-ui-tests.sh)", () => System.IO.File.Exists(fake) ? null : "missing: " + fake);

        var set = _w.AiSettingsDlg;
        _w.OpenAiSettings();
        await Settle();
        Check("p7 AI settings open with nothing configured: runner 'not set', models 'folder not set', hardware shown", () =>
            set.IsOpen && set.RunnerLineText == "Runner: not set" && set.ModelsLineText == "Models: folder not set" && set.HardwareText.Contains("CPU cores") && set.HardwareText.Contains("RAM")
                ? null : $"{set.RunnerLineText} // {set.ModelsLineText} // {set.HardwareText}");
        Shot(P("p7-en-dark-settings-empty"), set);

        // A wrapper that waits 3 s before the runner starts answering: the window must keep running meanwhile.
        var slow = System.IO.Path.Combine(_dir, "slow-runner.sh");
        System.IO.File.WriteAllText(slow, $"#!/bin/sh\nsleep 3\nexec \"{fake}\" \"$@\"\n");
        if (!OperatingSystem.IsWindows()) System.IO.File.SetUnixFileMode(slow, (System.IO.UnixFileMode)0b111_101_101);
        set.RunnerEdit.Text = slow;
        set.ModelsEdit.Text = modelsDir;
        set.DeviceBox.Select(1);
        set.TimeoutBox.Value = 5;
        var frames0 = Engine.GetProcessFrames();
        var t0 = Godot.Time.GetTicksMsec();
        set.RunCheck();
        Check("p7 Check starts and returns at once (the window is not blocked)", () =>
            set.Checking && set.CheckButton.Disabled && Godot.Time.GetTicksMsec() - t0 < 500 ? null : $"checking={set.Checking} ms={Godot.Time.GetTicksMsec() - t0}");
        var done = await WaitFor(() => !set.Checking, 60);
        var framesDuring = Engine.GetProcessFrames() - frames0;
        await Settle();
        Check("p7 Check ran off the main thread: frames kept being drawn while the runner started", () =>
            done && set.LastCheck?.Diagnostics is not null && framesDuring >= 30 ? null : $"done={done} frames={framesDuring} err={set.LastCheck?.Error}");
        Check("p7 Check shows runner found, models found, runner answer, CPU/RAM/GPU", () =>
        {
            if (!set.RunnerLineText.StartsWith("Runner found")) return set.RunnerLineText;
            if (!set.ModelsLineText.StartsWith("Models found")) return set.ModelsLineText;
            if (!set.NotesText.Contains("fake runner")) return "notes: " + set.NotesText;
            return set.HardwareText.Contains("CPU cores") && set.HardwareText.Contains("GPU") ? null : set.HardwareText;
        });
        Check("p7 the settings are saved to the app settings (persisted) and applied to Kimodo", () =>
        {
            var again = new AppSettings();
            again.Load();
            var k = App.Kimodo!.Settings;
            if (s.KimodoRunner != slow || s.KimodoModels != modelsDir || s.KimodoDevice != "cpu" || s.KimodoTimeoutSeconds != 300) return $"app settings {s.KimodoRunner} {s.KimodoDevice} {s.KimodoTimeoutSeconds}";
            if (again.KimodoRunner != slow || again.KimodoModels != modelsDir || again.KimodoDevice != "cpu" || again.KimodoTimeoutSeconds != 300) return "not persisted: " + again.KimodoRunner;
            return k.RunnerPath == slow && k.ModelDirectory == modelsDir && k.Device == "cpu" && k.GenerateTimeout == TimeSpan.FromSeconds(300) ? null : $"kimodo {k.RunnerPath} {k.Device} {k.GenerateTimeout}";
        });
        set.RunnerEdit.Text = fake; // the direct runner for the generation tests below
        set.ApplyNow();
        set.ToggleLog();
        await Settle();
        Check("p7 the runner log box opens and shows what the runner wrote", () =>
            set.LogOpen && set.LogBox.Visible && set.LogBox.Text.Contains("fake kimodo-runner ready") ? null : "log: " + set.LogBox.Text);
        Shot(P("p7-en-dark-settings"), set);
        set.Hide();
        await Settle();

        // ---- Kimodo (fake runner): available, runs, then a hanging request is cancelled -------------------------------------
        _w.OpenGenerateDialog();
        await Settle();
        Check("p7 with the runner set up Kimodo is shown as available and preselected", () =>
            dlg.SelectedProviderId == AiText.KimodoId && dlg.ProviderBadge == "Kimodo: available" && !dlg.Setup.Visible ? null : $"{dlg.SelectedProviderId} {dlg.ProviderBadge}");
        dlg.Prompt.Text = "a person walks forward";
        dlg.Prompt.EmitSignal(TextEdit.SignalName.TextChanged);
        dlg.Duration.Value = 1;
        clipsBefore = Proj.Clips.Count;
        _w.LastMotionApply = null;
        dlg.PressGenerate();
        var kimodoDone = await WaitFor(() => _w.LastMotionApply is not null, 40);
        await Settle();
        Check("p7 Kimodo (fake runner) generates a draft; notes say fingers and face are not generated", () =>
        {
            if (!kimodoDone) return "timeout: " + App.MotionJobs.Get(dlg.CurrentJobId ?? "")?.State + " " + App.MotionJobs.Get(dlg.CurrentJobId ?? "")?.Message;
            var c = Proj.Clips.Find(x => x.Id.ToString() == _w.LastMotionApply!.ClipId);
            if (c is null || !c.IsDraft || KeyTotal(c) == 0 || c.Metadata.GetValueOrDefault("provider") != "kimodo") return "clip: " + c?.Name;
            return dlg.DetailsText.Contains("fingers and face") ? null : dlg.DetailsText;
        });
        dlg.Hide();
        await Settle();

        dlg.Open();
        await Settle();
        dlg.Prompt.Text = "please hang forever";
        dlg.Prompt.EmitSignal(TextEdit.SignalName.TextChanged);
        clipsBefore = Proj.Clips.Count;
        var jobsBefore = App.MotionJobs.List().Count;
        dlg.PressGenerate();
        var running = await WaitFor(() => dlg.IsRunning && App.MotionJobs.Get(dlg.CurrentJobId!)!.Progress > 0, 30);
        await Settle();
        Check("p7 a running job shows a progress bar with stage, percent and elapsed time, and a Cancel button", () =>
            running && dlg.ProgressVisible && (dlg.StageText.Contains("Loading the models") || dlg.StageText.Contains("Generating the motion")) && dlg.StageText.Contains("%") && dlg.StageText.Contains(":") && dlg.CancelJob.IsVisibleInTree() && !dlg.GenerateEnabled
                ? null : $"running={running} visible={dlg.ProgressVisible} stage='{dlg.StageText}' enabled={dlg.GenerateEnabled}");
        Shot(P("p7-en-dark-dialog-running"), dlg);
        PressButton(dlg.CancelJob);
        var cancelled = await WaitFor(() => App.MotionJobs.Get(dlg.CurrentJobId!)!.State == MotionJobState.Cancelled, 20);
        await Settle();
        Check("p7 Cancel stops the job (motion.job_cancel): nothing is added, the dialog says so and can generate again", () =>
            cancelled && Proj.Clips.Count == clipsBefore && dlg.StatusText.StartsWith("Cancelled") && !dlg.IsRunning && !dlg.ProgressVisible ? null
                : $"cancelled={cancelled} clips={Proj.Clips.Count}/{clipsBefore} status={dlg.StatusText}");
        dlg.Hide();

        // ---- agent-started jobs: listed in the AI panel, applied by the agent (or by the user from the panel) ---------------
        var mid = man.Id.ToString();
        var r1 = App.Execute("motion.generate", Jo(new { characterId = mid, prompt = "wave", durationSeconds = 1.5, provider = "procedural", seed = 3 }));
        var agentJob = (string?)r1.Data?["jobId"] ?? "";
        clipsBefore = Proj.Clips.Count;
        var ready = await WaitFor(() => App.MotionJobs.Get(agentJob)?.State == MotionJobState.Ready, 15);
        _w.ShowAgentPanel(true);
        await Settle();
        Check("p7 an agent's job is listed in the AI panel with its origin and is not applied behind the user's back", () =>
        {
            if (!ready) return "job not ready";
            if (Proj.Clips.Count != clipsBefore) return "applied automatically";
            if (!_w.Agent.JobRows.Any(j => j.StartsWith("Ready|procedural|AI agent|wave"))) return "rows: " + string.Join(" ; ", _w.Agent.JobRows);
            return _w.Agent.Buttons.ContainsKey($"job:{agentJob}:apply") ? null : "no apply button";
        });
        PressButton(_w.Agent.Buttons[$"job:{agentJob}:apply"]);
        await Settle();
        Check("p7 'Add as draft' in the panel adds the agent's motion as a draft clip", () =>
            Proj.Clips.Count == clipsBefore + 1 && Proj.Clips[^1].IsDraft && Proj.Clips[^1].Metadata.GetValueOrDefault("jobId") == agentJob ? null : "no draft");
        var r2 = App.Execute("motion.generate", Jo(new { characterId = mid, prompt = "jump", durationSeconds = 1.5, provider = "procedural" }));
        var job2 = (string?)r2.Data?["jobId"] ?? "";
        await WaitFor(() => App.MotionJobs.Get(job2)?.State == MotionJobState.Ready, 15);
        clipsBefore = Proj.Clips.Count;
        var r3 = App.Execute("motion.job_apply", Jo(new { jobId = job2 }));
        await Settle();
        Check("p7 when an agent applies its own job the draft simply appears (and the user is told)", () =>
            r3.Ok && Proj.Clips.Count == clipsBefore + 1 && Proj.Clips[^1].IsDraft && _w.ToastNode.Message.Contains("AI agent added a draft") ? null : $"ok={r3.Ok} toast={_w.ToastNode.Message}");
        await Settle();
        Check("p7 the AI panel shows Kimodo's state and all jobs, including the cancelled one", () =>
            _w.Agent.KimodoStatusText == "Kimodo: available" && _w.Agent.JobRows.Any(j => j.StartsWith("Cancelled|")) && _w.Agent.JobRows.Any(j => j.StartsWith("Applied|")) ? null
                : _w.Agent.KimodoStatusText + " / " + string.Join(" ; ", _w.Agent.JobRows));
        _w.ToastNode.HideNow();
        Shot(P("p7-en-dark-panel"), GetViewport());
        _w.ShowAgentPanel(false);

        // ---- an unsupported prompt fails with a helpful message ---------------------------------------------------------------
        _w.OpenGenerateDialog();
        await Settle();
        dlg.SelectProvider(AiText.ProceduralId);
        dlg.Prompt.Text = "xyzzy";
        dlg.Prompt.EmitSignal(TextEdit.SignalName.TextChanged);
        dlg.PressGenerate();
        await WaitFor(() => App.MotionJobs.Get(dlg.CurrentJobId ?? "")?.State == MotionJobState.Failed, 15);
        await Settle();
        Check("p7 a prompt the procedural generator does not know fails with the list of words it knows", () =>
            dlg.StatusText == "Generation failed." && dlg.DetailsText.Contains("walk") ? null : dlg.StatusText + " // " + dlg.DetailsText);
        dlg.Hide();
        await Settle();

        // ---- light theme -----------------------------------------------------------------------------------------------------
        App.SetTheme("light");
        await Settle();
        _w.OpenGenerateDialog();
        await Settle();
        Shot(P("p7-en-light-dialog"), dlg);
        dlg.Hide();

        // ---- Arabic: RTL dialogs, an Arabic prompt that the procedural provider understands ----------------------------------
        App.SetLanguage("ar"); App.SetTheme("dark");
        await Settle();
        _w.OpenGenerateDialog();
        await Settle();
        dlg.SelectProvider(AiText.ProceduralId);
        dlg.Prompt.Text = "شخص يمشي";
        dlg.Prompt.EmitSignal(TextEdit.SignalName.TextChanged);
        dlg.Seed.Value = 11; dlg.Duration.Value = 2;
        await Settle();
        Check("p7 Arabic: the dialog is mirrored (RTL) and its labels are in Arabic", () =>
            App.IsRtl && _w.LayoutDirection == Control.LayoutDirectionEnum.Rtl
                ? (HasArabic(dlg.ProviderBadge) && HasArabic(dlg.ProviderNote) && HasArabic(dlg.KeywordsText) && HasArabic(TranslationServer.Translate(dlg.GetOkButton().Text)) ? null : $"badge='{dlg.ProviderBadge}' ok='{dlg.GetOkButton().Text}'")
                : "not RTL");
        Shot(P("p7-ar-dark-dialog"), dlg);
        _w.LastMotionApply = null;
        dlg.PressGenerate();
        var arApplied = await WaitFor(() => _w.LastMotionApply is not null, 15);
        await Settle();
        Check("p7 Arabic: an Arabic prompt ('شخص يمشي') produces a draft; messages are in Arabic", () =>
            arApplied && Proj.Clips.Find(c => c.Id.ToString() == _w.LastMotionApply!.ClipId) is { IsDraft: true } c && c.Metadata["prompt"] == "شخص يمشي" && HasArabic(dlg.StatusText) && HasArabic(dlg.DetailsText)
                ? null : $"applied={arApplied} status='{dlg.StatusText}'");
        Shot(P("p7-ar-dark-dialog-done"), dlg);
        dlg.Hide();
        await Settle();
        Check("p7 Arabic: the draft header (badge, Accept, Discard) is translated", () =>
        {
            var t = _w.Timeline;
            return t.DraftBar.Visible && HasArabic(TranslationServer.Translate(t.AcceptDraftButton.Text))
                && HasArabic(TranslationServer.Translate(t.DiscardDraftButton.Text)) && HasArabic(TranslationServer.Translate(t.GenerateButton.Text)) ? null : "untranslated";
        });
        Shot(P("p7-ar-dark-generated"), GetViewport());

        _w.OpenAiSettings();
        await Settle();
        Check("p7 Arabic: the AI settings explain that nothing is downloaded, in Arabic, with an Arabic status", () =>
            set.IsOpen && HasArabic(set.RunnerLineText) && HasArabic(set.HardwareText) ? null : $"{set.RunnerLineText} // {set.HardwareText}");
        Shot(P("p7-ar-dark-settings"), set);
        set.Hide();
        _w.ShowAgentPanel(true);
        await Settle();
        _w.ToastNode.HideNow();
        Shot(P("p7-ar-dark-panel"), GetViewport());
        _w.ShowAgentPanel(false);

        // ---- restore --------------------------------------------------------------------------------------------------------
        s.KimodoRunner = saved.KimodoRunner; s.KimodoModels = saved.KimodoModels; s.KimodoDevice = saved.KimodoDevice; s.KimodoTimeoutSeconds = saved.KimodoTimeoutSeconds;
        s.Save();
        kimodo.Settings = envSettings;
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        await Settle();
    }
}
