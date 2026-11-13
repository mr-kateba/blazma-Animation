using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Physics;
using Blazma.Rigging;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 8 scenario, through the real UI code paths: the Templates browser (apply, save as template), Foot lock and the contact strip,
/// Retarget onto an imported character (CesiumMan), the Sequence editor (walk to run with a blend: add, drag, edit, scrub, play, bake,
/// export), and Props (add a sword, attach it to hand_r, move and rotate it with the gizmo, key it, export the GLB and load it back with
/// GLTFDocument). Undo and redo use the real Ctrl+Z / Ctrl+Shift+Z shortcuts. Screenshots are named p8-lang-theme-what.png.
/// </summary>
public partial class SelfTest
{
    void PushButton(Vector2 g, bool pressed) =>
        GetViewport().PushInput(new InputEventMouseButton { Position = g, GlobalPosition = g, ButtonIndex = MouseButton.Left, Pressed = pressed, ButtonMask = pressed ? MouseButtonMask.Left : 0 }, false);

    void PushMove(Vector2 g) =>
        GetViewport().PushInput(new InputEventMouseMotion { Position = g, GlobalPosition = g, Relative = new Vector2(2, 0), ButtonMask = MouseButtonMask.Left }, false);

    /// <summary>Press at the first point, move through the rest (real mouse events in window coordinates), release at the last.</summary>
    void DragGlobal(IReadOnlyList<Vector2> pts, bool escInstead = false)
    {
        PushButton(pts[0], true);
        for (var i = 1; i < pts.Count; i++) PushMove(pts[i]);
        if (escInstead)
        {
            foreach (var pressed in new[] { true, false })
                GetViewport().PushInput(new InputEventKey { Keycode = Godot.Key.Escape, PhysicalKeycode = Godot.Key.Escape, Pressed = pressed }, false);
        }
        PushButton(pts[^1], false);
    }

    static Vector2[] Line(Vector2 a, Vector2 b, int steps = 6) => Enumerable.Range(0, steps + 1).Select(i => a.Lerp(b, i / (float)steps)).ToArray();

    static double MaxJointDistance(Rig rig, Pose a, Pose b)
    {
        var ev = new RigEvaluator(rig);
        var ma = ev.Model(a); var mb = ev.Model(b);
        var worst = 0.0;
        for (var i = 0; i < ma.Length; i++) worst = Math.Max(worst, (ma[i].Translation - mb[i].Translation).Length());
        return worst;
    }

    int PinnedKeys(Clip c) => c.Tracks.Where(t => t.Channel == TrackChannels.Pinned).Sum(t => t.Keys.Count);

    static string Ltrs(string s) => s;

    async Task RunPhase8()
    {
        var s = App.Settings;
        var saved = (s.ShowContacts, s.PropsPanel);
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        _w.ShowAgentPanel(false);
        _w.NewProjectNow();
        _w.Timeline.SelectTab(TimelinePanel.TabTimeline);
        await Settle();
        var man = Ch();
        var mid = man.Id.ToString();
        var rig = RigOf(man);
        var dir = DocExportDir("Props") ?? System.IO.Path.Combine(Godot.ProjectSettings.GlobalizePath("user://"), "p8_selftest");
        if (DocExportDir("Props") is null && System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        var lastExportBackup = s.LastExportDir;
        s.LastExportDir = dir;

        // ================================================================ 1. templates (Simple mode)
        var tdlg = _w.TemplatesDlg;
        Check("p8 Simple mode: a prominent 'Templates…' button in the timeline header and an Animation menu entry", () =>
        {
            var b = _w.Timeline.TemplatesButton;
            if (s.AdvancedMode || !b.IsVisibleInTree() || b.Disabled || b.ThemeTypeVariation != "Primary") return $"button visible={b.IsVisibleInTree()} disabled={b.Disabled} variation={b.ThemeTypeVariation}";
            if (_w.Timeline.Tabs[TimelinePanel.TabSequence].Visible) return "the Sequence tab is visible in Simple mode";
            var menu = _w.MenuPopup(2);
            for (var i = 0; i < menu.ItemCount; i++)
                if (menu.GetItemText(i) == "Templates…") return menu.IsItemDisabled(i) ? "menu item disabled" : null;
            return "no menu item";
        });
        PressButton(_w.Timeline.TemplatesButton);
        await Settle();
        Check("p8 the Templates browser lists the 6 built-in templates with names and descriptions; saving is an Advanced feature", () =>
        {
            if (!tdlg.IsOpen || tdlg.RowCount != 6) return $"open={tdlg.IsOpen} rows={tdlg.RowCount}";
            var want = new[] { "Idle (breathing)", "Walk cycle", "Run cycle", "Jump", "Wave", "Attack swing" };
            for (var i = 0; i < want.Length; i++) if (tdlg.ItemText(i) != want[i]) return $"row {i}: '{tdlg.ItemText(i)}'";
            return tdlg.SaveBox.Visible ? "the save section is visible in Simple mode" : null;
        });
        tdlg.SelectById("builtin.walk_cycle");
        await Settle();
        Check("p8 choosing a template shows its description and default length", () =>
            tdlg.TitleText == "Walk cycle" && tdlg.DescriptionText.Contains("Walking forward") && Math.Abs(tdlg.Duration.Value - 2.2) < 1e-9 && tdlg.ApplyEnabled && tdlg.KindText == "Built-in"
                ? null : $"{tdlg.TitleText} / {tdlg.DescriptionText} / {tdlg.Duration.Value} / {tdlg.KindText}");
        Shot(P("p8-en-dark-templates"), tdlg);
        tdlg.Duration.Value = 2;
        tdlg.PressApply();
        await Settle();
        string walkId = "";
        Check("p8 Apply adds a normal clip, selects it and starts playing; the dialog closes", () =>
        {
            var c = Proj.Clips.FirstOrDefault(x => x.Name == "Walk cycle");
            if (c is null) return "no clip: " + string.Join(",", Proj.Clips.Select(x => x.Name)) + " err=" + tdlg.LastError;
            walkId = c.Id.ToString();
            if (c.IsDraft || c.Skeleton != man.Rig || !c.Loop.Enabled || !AppController.HasKeys(c)) return $"draft={c.IsDraft} loop={c.Loop.Enabled} keys={KeyTotal(c)}";
            if (Math.Abs(c.Duration.ToSeconds() - 2) > 0.05) return "length " + c.Duration.ToSeconds();
            if (App.ActiveClipId != walkId || !App.Playing || tdlg.IsOpen) return $"active={App.ActiveClipId == walkId} playing={App.Playing} open={tdlg.IsOpen}";
            return Proj.Clips.Count == 1 ? null : "clips " + Proj.Clips.Count;
        });
        Check("p8 the template clip really plays: the frame moves and the mannequin is posed", () =>
        {
            var f0 = App.CurrentFrameExact;
            App.AdvancePlayback(0.25);
            return App.CurrentFrameExact > f0 && Ch().Pose.Bones.Count > 0 ? null : $"frame {f0} -> {App.CurrentFrameExact}, bones {Ch().Pose.Bones.Count}";
        });
        App.Pause();
        UndoKey();
        await Settle();
        Check("p8 Ctrl+Z removes the applied clip; Ctrl+Shift+Z brings it back", () =>
        {
            if (Proj.Clips.Count != 0) return "clip still there";
            GetViewport().GuiReleaseFocus();
            Key(Godot.Key.Z, shift: true);
            return null;
        });
        await Settle();
        Check("p8 after redo the clip is back and selected", () =>
            Proj.Clips.Count == 1 && Proj.Clips[0].Id.ToString() == walkId && _w.SelectedClipId == walkId ? null : $"clips={Proj.Clips.Count} sel={_w.SelectedClipId}");

        // Arabic browser
        App.SetLanguage("ar");
        await Settle();
        _w.OpenTemplatesDialog();
        await Settle();
        tdlg.SelectById("builtin.walk_cycle");
        await Settle();
        Check("p8 Arabic: the browser is RTL; names, descriptions and buttons are Arabic; numbers stay left to right", () =>
        {
            if (!tdlg.IsOpen || !App.IsRtl) return "not open / not rtl";
            if (!HasArabic(tdlg.ItemText(1)) || !HasArabic(tdlg.TitleText) || !HasArabic(tdlg.DescriptionText) || !HasArabic(tdlg.KindText)) return $"item='{tdlg.ItemText(1)}' title='{tdlg.TitleText}' desc='{tdlg.DescriptionText}'";
            if (!HasArabic(TranslationServer.Translate(tdlg.GetOkButton().Text))) return "Apply button not translated";
            return null;
        });
        Shot(P("p8-ar-dark-templates"), tdlg);
        tdlg.Hide();
        App.SetLanguage("en");
        await Settle();

        // ---- save a clip as a template (Advanced), apply it, delete it
        App.SetAdvancedMode(true);
        await Settle();
        _w.OpenTemplatesDialog();
        await Settle();
        Check("p8 Advanced mode shows 'Save clip as template'", () => tdlg.SaveBox.Visible && tdlg.SaveButton.IsVisibleInTree() && !tdlg.SaveButton.Disabled ? null : "save section hidden");
        tdlg.SaveName.Text = "My walk";
        tdlg.SaveDescription.Text = "Walk saved from the project";
        tdlg.PressSave();
        await Settle();
        string myTemplate = "";
        Check("p8 saving adds a project template to the list (marked with a star) and to the project", () =>
        {
            if (Proj.Templates.Count != 1 || Proj.Templates[0].Name != "My walk") return "templates " + Proj.Templates.Count;
            myTemplate = Proj.Templates[0].Id.ToString();
            return tdlg.RowCount == 7 && tdlg.SelectedId == myTemplate && tdlg.ItemText(6) == "★ My walk" && tdlg.KindText == "Project template" && tdlg.DescriptionText == "Walk saved from the project" ? null
                : $"rows={tdlg.RowCount} sel={tdlg.SelectedId} item='{tdlg.ItemText(tdlg.RowCount - 1)}' kind={tdlg.KindText}";
        });
        tdlg.Duration.Value = 1.5;
        tdlg.PressApply();
        await Settle();
        Check("p8 applying the project template creates a clip with its name and length", () =>
        {
            var c = Proj.Clips.FirstOrDefault(x => x.Name == "My walk");
            return c is not null && Math.Abs(c.Duration.ToSeconds() - 1.5) < 0.06 && App.ActiveClipId == c.Id.ToString() && AppController.HasKeys(c) ? null : "clip: " + c?.Name + " " + c?.Duration.ToSeconds();
        });
        var myWalkClip = Proj.Clips.First(x => x.Name == "My walk").Id.ToString();
        App.Pause();
        _w.OpenTemplatesDialog();
        await Settle();
        tdlg.SelectById(myTemplate);
        tdlg.PressDelete();
        await Settle();
        Check("p8 deleting a project template removes it (and the toast offers Undo)", () =>
            Proj.Templates.Count == 0 && tdlg.RowCount == 6 && _w.ToastNode.ActionText == "Undo" ? null : $"templates={Proj.Templates.Count} rows={tdlg.RowCount} toast={_w.ToastNode.ActionText}");
        tdlg.Hide();
        await Settle();
        UndoKey();
        await Settle();
        Check("p8 Ctrl+Z brings the deleted template back", () => Proj.Templates.Count == 1 ? null : "templates " + Proj.Templates.Count);
        _w.DeleteClipNow(myWalkClip);
        _w.SelectClip(walkId);
        App.SetAdvancedMode(false);
        await Settle();

        // ================================================================ 2. foot lock + contact strip
        _w.ToastNode.HideNow();
        Check("p8 the Foot lock button is in the timeline header and enabled for a keyed clip", () =>
            _w.Timeline.FootLockButton.IsVisibleInTree() && !_w.Timeline.FootLockButton.Disabled ? null : "button hidden or disabled");
        var fdlg = _w.FootLockDlg;
        PressButton(_w.Timeline.FootLockButton);
        await Settle();
        Check("p8 the Foot lock dialog detects contacts: both feet, several spans", () =>
        {
            if (!fdlg.IsOpen || fdlg.LastDetect is not { Ok: true } r) return $"open={fdlg.IsOpen} detect={fdlg.LastDetect?.Error?.Message}";
            var spans = (r.Data!["spans"] as JsonArray)!.OfType<JsonObject>().ToList();
            var left = spans.Count(x => (string?)x["side"] == "left"); var right = spans.Count(x => (string?)x["side"] == "right");
            GD.Print($"SELFTEST INFO foot contacts of the walk template: left={left} right={right} status='{fdlg.StatusText}'");
            return left >= 1 && right >= 1 && fdlg.StatusText.Contains("foot contacts found") ? null : $"left={left} right={right} {fdlg.StatusText}";
        });
        Check("p8 the timeline shows the contacts as a thin strip with one bar per span, and a tooltip", () =>
        {
            var strip = _w.Timeline.Contacts;
            var info = App.Contacts();
            if (!strip.Visible || info is null || info.Spans.Count < 2) return $"strip visible={strip.Visible} spans={info?.Spans.Count}";
            if (strip.Size.Y > 24) return "strip not thin: " + strip.Size.Y;
            var t = ContactStrip.TooltipFor(info.Spans[0]);
            return t.Contains("foot") && t.Contains("frames") ? null : "tooltip: " + t;
        });
        Shot(P("p8-en-dark-footlock-dialog"), fdlg);
        var keysBeforeLock = KeyTotal(Proj.Clips.First(c => c.Id.ToString() == walkId));
        var undoBeforeLock = App.Session.UndoHistory.Count;
        fdlg.PressLock();
        await Settle();
        Check("p8 'Lock feet' runs clip.foot_lock: anchors held within 1 cm, one undo step, status in the dialog", () =>
        {
            var r = fdlg.LastResult;
            if (r is not { Ok: true }) return "lock failed: " + (fdlg.LastError ?? r?.Error?.Message);
            var anchor = (double)r.Data!["maxAnchorError"]!;
            var untouched = (double)r.Data["maxUntouchedChange"]!;
            GD.Print($"SELFTEST INFO foot lock: anchor error {anchor * 1000:0.00} mm, untouched change {untouched * 1000:0.00} mm, keys {r.Data["keys"]}");
            if (anchor > 0.01) return "anchor error " + anchor;
            if (App.Session.UndoHistory.Count != undoBeforeLock + 1 || App.Session.UndoHistory[^1].Label != "Foot lock") return $"history +{App.Session.UndoHistory.Count - undoBeforeLock} '{App.Session.UndoHistory[^1].Label}'";
            return fdlg.StatusText.StartsWith("Locked") && PinnedKeys(Proj.Clips.First(c => c.Id.ToString() == walkId)) > 0 ? null : fdlg.StatusText;
        });
        Shot(P("p8-en-dark-footlock-done"), fdlg);
        fdlg.Hide();
        await Settle();
        Shot(P("p8-en-dark-footlock-strip"), GetViewport());
        UndoKey();
        await Settle();
        Check("p8 Ctrl+Z undoes the foot lock exactly (same key count); redo restores it", () =>
        {
            var c = Proj.Clips.First(x => x.Id.ToString() == walkId);
            if (KeyTotal(c) != keysBeforeLock) return $"keys {KeyTotal(c)} vs {keysBeforeLock}";
            GetViewport().GuiReleaseFocus();
            Key(Godot.Key.Z, shift: true);
            return null;
        });
        await Settle();
        Check("p8 after redo the foot lock is back", () => Proj.Clips.First(x => x.Id.ToString() == walkId) is var c && PinnedKeys(c) > 0 ? null : "no pinned keys");
        UndoKey();
        await Settle();
        _w.OpenFootLockDialog();
        await Settle();
        fdlg.MinFrames.Value = 60;
        fdlg.PressDetect();
        await Settle();
        Check("p8 with nothing to lock the dialog says so in words (no raw error)", () =>
            fdlg.StatusText.StartsWith("No foot contact found") && !fdlg.StatusText.Contains("no_contacts") ? null : fdlg.StatusText);
        fdlg.PressLock();
        await Settle();
        Check("p8 locking with no contacts fails with a readable message and changes nothing", () =>
            fdlg.LastError is not null && fdlg.StatusText.StartsWith("No foot contact found") && App.Session.UndoHistory.Count == undoBeforeLock ? null : $"err={fdlg.LastError} status={fdlg.StatusText} hist={App.Session.UndoHistory.Count}/{undoBeforeLock}");
        fdlg.MinFrames.Value = 3;
        fdlg.Hide();
        await Settle();

        // ================================================================ 3. retarget onto CesiumMan
        string? cesium = null;
        Check("p8 import CesiumMan (the existing import path) and map its bones through the dialog", () =>
        {
            cesium = _w.ImportCharacterPath(Sample("CesiumMan.glb"));
            if (cesium is null) return "import failed: " + _w.LastError;
            var map = new (string std, string bone)[]
            {
                ("pelvis", "Skeleton_torso_joint_1"), ("spine_01", "Skeleton_torso_joint_2"), ("spine_03", "torso_joint_3"), ("neck", "Skeleton_neck_joint_1"),
                ("head", "Skeleton_neck_joint_2"),
                ("upper_arm_r", "Skeleton_arm_joint_R"), ("lower_arm_r", "Skeleton_arm_joint_R__2_"), ("hand_r", "Skeleton_arm_joint_R__3_"),
                ("upper_arm_l", "Skeleton_arm_joint_L__4_"), ("lower_arm_l", "Skeleton_arm_joint_L__3_"), ("hand_l", "Skeleton_arm_joint_L__2_"),
                ("thigh_l", "leg_joint_L_1"), ("calf_l", "leg_joint_L_2"), ("foot_l", "leg_joint_L_3"), ("toe_l", "leg_joint_L_5"),
                ("thigh_r", "leg_joint_R_1"), ("calf_r", "leg_joint_R_2"), ("foot_r", "leg_joint_R_3"), ("toe_r", "leg_joint_R_5"),
            };
            foreach (var (std, bone) in map) if (!_w.MappingDialog.Assign(std, bone)) return $"assign {std} failed";
            return _w.MappingDialog.Usable ? null : "mapping not usable";
        });
        _w.MappingDialog.Close(true);
        await Settle();
        _w.Selection.SelectCharacter(mid);
        _w.SelectClip(walkId);
        await Settle();
        var rdlg = _w.RetargetDlg;
        Check("p8 the Animation menu and the Advanced header offer 'Retarget Clip…' once there are two characters", () =>
        {
            var menu = _w.MenuPopup(2);
            for (var i = 0; i < menu.ItemCount; i++)
                if (menu.GetItemText(i) == "Retarget Clip…") return menu.IsItemDisabled(i) ? "menu item disabled" : null;
            return "no menu item";
        });
        _w.OpenRetargetDialog();
        await Settle();
        var cesRig = RigOf(Ch(cesium));
        Check("p8 the Retarget dialog proposes the walk clip, its character as source, the other character as target, foot lock on", () =>
        {
            if (!rdlg.IsOpen || !rdlg.RunEnabled) return $"open={rdlg.IsOpen} enabled={rdlg.RunEnabled} hint={rdlg.HintText}";
            return rdlg.FootLock.ButtonPressed && rdlg.NewName.Text.StartsWith("Walk cycle (") && rdlg.Clip.GetItemText(rdlg.Clip.Selected) == "Walk cycle"
                && rdlg.Source.GetItemText(rdlg.Source.Selected) == man.Name && rdlg.Target.Selected != rdlg.Source.Selected ? null
                : $"footlock={rdlg.FootLock.ButtonPressed} name='{rdlg.NewName.Text}' clip={rdlg.Clip.GetItemText(rdlg.Clip.Selected)} src={rdlg.Source.GetItemText(rdlg.Source.Selected)}";
        });
        Shot(P("p8-en-dark-retarget"), rdlg);
        App.SetLanguage("ar");
        await Settle();
        Shot(P("p8-ar-dark-retarget"), rdlg);
        Check("p8 Arabic: the Retarget dialog is mirrored and translated", () =>
            App.IsRtl && HasArabic(TranslationServer.Translate(rdlg.GetOkButton().Text)) && HasArabic(TranslationServer.Translate(rdlg.Title)) ? null : $"title '{rdlg.Title}'");
        App.SetLanguage("en");
        await Settle();
        var clipsBeforeRetarget = Proj.Clips.Count;
        rdlg.Run();
        await Settle();
        string retargeted = "";
        Check("p8 Retarget adds a new clip on CesiumMan's rig, selected and playing; the source clip is unchanged", () =>
        {
            if (rdlg.LastResult is not { Ok: true } r) return "failed: " + rdlg.LastError;
            retargeted = (string)r.Data!["clipId"]!;
            var c = Proj.Clips.Find(x => x.Id.ToString() == retargeted);
            if (c is null || Proj.Clips.Count != clipsBeforeRetarget + 1) return "no new clip";
            if (c.Skeleton != Ch(cesium).Rig || !AppController.HasKeys(c) || c.IsDraft) return $"skeleton ok={c.Skeleton == Ch(cesium).Rig} keys={KeyTotal(c)}";
            if (_w.SelectedClipId != retargeted || !App.Playing) return $"selected={_w.SelectedClipId == retargeted} playing={App.Playing}";
            GD.Print($"SELFTEST INFO retarget: {rdlg.StatusText} // {rdlg.DetailsText.Replace('\n', ' ')}");
            return rdlg.StatusText.StartsWith("Done") && rdlg.DetailsText.Contains("bones copied") ? null : rdlg.StatusText + " / " + rdlg.DetailsText;
        });
        Check("p8 retarget with foot lock keyed the target's feet (pinned IK on the contacts)", () =>
            PinnedKeys(Proj.Clips.First(c => c.Id.ToString() == retargeted)) > 0 && rdlg.DetailsText.Contains("Feet locked") ? null : $"pinned={PinnedKeys(Proj.Clips.First(c => c.Id.ToString() == retargeted))} {rdlg.DetailsText}");
        rdlg.Hide();
        App.Pause();
        App.GoToFrame(15);
        await Settle();
        CheckSkeleton("CesiumMan with the retargeted walk at frame 15", cesium!);
        Check("p8 the retargeted pose on CesiumMan is not the rest pose (the character really moves)", () =>
        {
            var c = Proj.Clips.First(x => x.Id.ToString() == retargeted);
            var pose = ClipEvaluator.Evaluate(c, cesRig, Flicks.FromFrame(15, c.Fps));
            return MaxJointDistance(cesRig, pose, new Pose()) > 0.05 ? null : "pose equals rest";
        });
        Shot(P("p8-en-dark-retargeted"), GetViewport());
        UndoKey();
        await Settle();
        Check("p8 Ctrl+Z removes the retargeted clip; Ctrl+Shift+Z brings it back", () =>
        {
            if (Proj.Clips.Any(c => c.Id.ToString() == retargeted)) return "still there";
            GetViewport().GuiReleaseFocus();
            Key(Godot.Key.Z, shift: true);
            return null;
        });
        await Settle();
        Check("p8 after redo the retargeted clip is back", () => Proj.Clips.Any(c => c.Id.ToString() == retargeted) ? null : "missing");

        // ================================================================ 4. sequence editor (Advanced)
        _w.Selection.SelectCharacter(mid);
        App.SetAdvancedMode(true);
        await Settle();
        _w.SelectClip(walkId);
        _w.OpenTemplatesDialog();
        await Settle();
        tdlg.SelectById("builtin.run_cycle");
        tdlg.SelectCharacterById(mid);
        tdlg.Duration.Value = 1.5;
        tdlg.PressApply();
        await Settle();
        App.Pause();
        var runId = Proj.Clips.First(c => c.Name == "Run cycle").Id.ToString();
        _w.Timeline.SelectTab(TimelinePanel.TabSequence);
        await Settle();
        var sv = _w.Timeline.Sequence;
        Check("p8 the Sequence tab is an Advanced tab next to Timeline, Dope Sheet, Graph, Clips and Poses; the transport row gives way to it", () =>
            _w.Timeline.Tabs[TimelinePanel.TabSequence].Visible && _w.Timeline.Tab == TimelinePanel.TabSequence && sv.IsVisibleInTree() && App.SequenceMode && !_w.Timeline.KeyButton.IsVisibleInTree()
                ? null : $"tab={_w.Timeline.Tab} visible={sv.IsVisibleInTree()} mode={App.SequenceMode}");
        sv.PressNew();
        await Settle();
        Check("p8 'New…' asks for a name; confirming creates the sequence on the selected character", () =>
        {
            var nd = _w.NameDialog;
            if (!nd.Visible || nd.Title != "New Sequence") return "dialog: " + nd.Visible + " " + nd.Title;
            var edit = nd.FindChild("*", true, false) as LineEdit ?? nd.GetChildren().OfType<LineEdit>().First();
            edit.Text = "Walk to run";
            nd.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            nd.Hide();
            return Proj.Sequences.Count == 1 && Proj.Sequences[0].Name == "Walk to run" && Proj.Sequences[0].Character == man.Id && App.ActiveSequenceId == Proj.Sequences[0].Id.ToString() ? null : "sequences " + Proj.Sequences.Count;
        });
        await Settle();
        var sid = Proj.Sequences[0].Id.ToString();
        ClipSequence Seq() => Proj.Sequences.Find(x => x.Id.ToString() == sid)!;
        Check("p8 the clip list holds the clips of this character only (CesiumMan's retargeted clip is not offered)", () =>
        {
            var texts = Enumerable.Range(0, sv.ClipPick.ItemCount).Select(i => sv.ClipPick.GetItemText(i)).ToList();
            return texts.Contains("Walk cycle") && texts.Contains("Run cycle") && texts.All(t => !t.Contains("(")) ? null : string.Join(",", texts);
        });
        sv.ChooseClip(walkId);
        sv.PressAdd();
        await Settle();
        sv.ChooseClip(runId);
        sv.MatchFeet.ButtonPressed = true;
        sv.PressAdd();
        await Settle();
        string walkItem = "", runItem = "";
        Check("p8 two clips on the track: the run fades in over the end of the walk (0.3 s = 9 frames), starts at the walk's end minus the blend", () =>
        {
            var seq = Seq();
            if (seq.Items.Count != 2) return "items " + seq.Items.Count;
            walkItem = seq.Items[0].Id.ToString(); runItem = seq.Items[1].Id.ToString();
            var ev = App.SequenceEval();
            if (ev is null) return "no evaluator: " + App.SequenceError;
            var walkEnd = ev.RangeOf(seq.Items[0].Id)!.Value.End;
            GD.Print($"SELFTEST INFO sequence: walk 0..{walkEnd:0.##}, run starts {seq.Items[1].StartFrame} blend {seq.Items[1].BlendInFrames} inFrame {seq.Items[1].InFrame}, last frame {ev.LastFrame}");
            if (seq.Items[1].BlendInFrames != 9 || seq.Items[1].StartFrame != (long)Math.Round(walkEnd) - 9) return $"start={seq.Items[1].StartFrame} blend={seq.Items[1].BlendInFrames}";
            return sv.Canvas.Bars.Count == 2 && sv.Canvas.SelectedItemId == runItem && sv.ItemRowVisible ? null : $"bars={sv.Canvas.Bars.Count} selected={sv.Canvas.SelectedItemId}";
        });
        await Settle();
        Shot(P("p8-en-dark-sequence"), GetViewport());

        // drag the run bar
        Check("p8 dragging a bar with the mouse moves its start; the whole drag is ONE undo step ('Move sequence item')", () =>
        {
            var item = Seq().Items.Find(i => i.Id.ToString() == runItem)!;
            var start0 = item.StartFrame;
            var undoBefore = App.Session.UndoHistory.Count;
            var ppf = sv.Canvas.PixelsPerFrame;
            var p0 = sv.Canvas.BarPoint(runItem, 0.6);
            DragGlobal(Line(p0, p0 + new Vector2((float)(ppf * 8), 0), 5));
            var now = Seq().Items.Find(i => i.Id.ToString() == runItem)!;
            GD.Print($"SELFTEST INFO bar drag: start {start0} -> {now.StartFrame} (ppf {ppf:0.00})");
            if (now.StartFrame != start0 + 8) return $"start {start0} -> {now.StartFrame}";
            if (App.Session.UndoHistory.Count != undoBefore + 1 || App.Session.UndoHistory[^1].Label != "Move sequence item") return $"history +{App.Session.UndoHistory.Count - undoBefore} '{App.Session.UndoHistory[^1].Label}'";
            return App.Session.InTransaction ? "transaction left open" : null;
        });
        UndoKey();
        await Settle();
        Check("p8 Ctrl+Z puts the bar back where it was", () =>
        {
            var seq = Seq();
            var item = seq.Items.Find(i => i.Id.ToString() == runItem);
            return item is not null && item.StartFrame == (long)Math.Round(App.SequenceEval()!.RangeOf(seq.Items[0].Id)!.Value.End) - 9 ? null : "start " + item?.StartFrame;
        });
        Check("p8 dragging the round handle on the bar's top edge changes the fade-in length (one undo step)", () =>
        {
            var item = Seq().Items.Find(i => i.Id.ToString() == runItem)!;
            var blend0 = item.BlendInFrames;
            var undoBefore = App.Session.UndoHistory.Count;
            var ppf = sv.Canvas.PixelsPerFrame;
            var h = sv.Canvas.BlendHandle(runItem);
            DragGlobal(Line(h, h + new Vector2((float)(ppf * 6), 0), 4));
            var now = Seq().Items.Find(i => i.Id.ToString() == runItem)!;
            if (now.BlendInFrames != blend0 + 6) return $"blend {blend0} -> {now.BlendInFrames}";
            return App.Session.UndoHistory.Count == undoBefore + 1 && App.Session.UndoHistory[^1].Label == "Change fade-in" ? null : $"history +{App.Session.UndoHistory.Count - undoBefore} '{App.Session.UndoHistory[^1].Label}'";
        });
        Shot(P("p8-en-dark-sequence-blend"), GetViewport());
        UndoKey();
        await Settle();
        Check("p8 dragging the bar's right edge changes its speed (clamped, one undo step), and Esc cancels a drag completely", () =>
        {
            var undoBefore = App.Session.UndoHistory.Count;
            var ppf = sv.Canvas.PixelsPerFrame;
            var e = sv.Canvas.EdgePoint(runItem);
            DragGlobal(Line(e, e - new Vector2((float)(ppf * 10), 0), 5));
            var now = Seq().Items.Find(i => i.Id.ToString() == runItem)!;
            GD.Print($"SELFTEST INFO edge drag: speed 1 -> {now.Speed}");
            if (now.Speed <= 1.05) return "speed " + now.Speed;
            if (App.Session.UndoHistory.Count != undoBefore + 1) return "history +" + (App.Session.UndoHistory.Count - undoBefore);
            UndoKey();
            var before = Seq().Items.Find(i => i.Id.ToString() == runItem)!.Speed;
            if (Math.Abs(before - 1) > 1e-9) return "undo left speed " + before;
            var p0 = sv.Canvas.BarPoint(runItem, 0.5);
            var u2 = App.Session.UndoHistory.Count;
            DragGlobal(Line(p0, p0 + new Vector2(60, 0), 4), escInstead: true);
            var s2 = Seq().Items.Find(i => i.Id.ToString() == runItem)!;
            return App.Session.UndoHistory.Count == u2 && !App.Session.InTransaction && s2.StartFrame == (long)Math.Round(App.SequenceEval()!.RangeOf(Seq().Items[0].Id)!.Value.End) - 9 ? null
                : $"after Esc: history +{App.Session.UndoHistory.Count - u2} start={s2.StartFrame}";
        });
        await Settle();

        // the item row
        Click(sv.Canvas.BarPoint(walkItem, 0.4));
        await Settle();
        Check("p8 clicking a bar selects it and the item row shows its values", () =>
            sv.SelectedItemId == walkItem && sv.ItemRowVisible && sv.ItemCaptionText == "Walk cycle" && Math.Abs(sv.Speed.Value - 1) < 1e-9 && sv.Loops.Value == 1 ? null : $"sel={sv.SelectedItemId} caption={sv.ItemCaptionText}");
        var lastBefore = App.SequenceEval()!.LastFrame;
        sv.Loops.Value = 2;
        await Settle();
        Check("p8 the Repeat field edits the item (sequence.update_item): the walk plays twice and the sequence gets longer", () =>
            Seq().Items.Find(i => i.Id.ToString() == walkItem)!.Loops == 2 && App.SequenceEval()!.LastFrame > lastBefore ? null : $"loops={Seq().Items.Find(i => i.Id.ToString() == walkItem)!.Loops} last={App.SequenceEval()?.LastFrame}/{lastBefore}");
        UndoKey();
        await Settle();
        Check("p8 Ctrl+Z restores a single repeat", () => Seq().Items.Find(i => i.Id.ToString() == walkItem)!.Loops == 1 && sv.Loops.Value == 1 ? null : "loops " + Seq().Items.Find(i => i.Id.ToString() == walkItem)!.Loops);
        sv.Blend.Value = 12;
        sv.Curve.Select(1);
        sv.Curve.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
        Click(sv.Canvas.BarPoint(runItem, 0.5));
        await Settle();
        Check("p8 the fade-in curve and length can be typed (sequence.set_blend) on the selected item", () =>
        {
            var walk = Seq().Items.Find(i => i.Id.ToString() == walkItem)!;
            return walk.BlendInFrames == 12 && walk.BlendCurve == BlendCurve.Linear ? null : $"blend={walk.BlendInFrames} curve={walk.BlendCurve}";
        });
        UndoKey(); UndoKey();
        await Settle();

        // scrub and play in the viewport
        Click(sv.Canvas.RulerPoint(20));
        await Settle();
        Check("p8 clicking the ruler scrubs: the viewport shows the blended pose of the sequence at that frame", () =>
        {
            if (Math.Abs(App.SequenceFrame - 20) > 0.5) return "frame " + App.SequenceFrame;
            var expected = App.SequenceEval()!.Evaluate(20).Pose;
            return MaxJointDistance(rig, Ch().Pose, expected) < 1e-6 ? null : "pose differs: " + MaxJointDistance(rig, Ch().Pose, expected);
        });
        var poseAt20 = Ch().Pose.Clone();
        sv.PressPlay();
        Check("p8 Play plays the sequence in the viewport: the frame advances and the pose changes (the clip's own playback is not used)", () =>
        {
            if (!App.SequencePlaying || App.Playing) return $"seqPlaying={App.SequencePlaying} clipPlaying={App.Playing}";
            App.AdvancePlayback(0.4); App.AdvancePlayback(0.4);
            if (App.SequenceFrame < 40) return "frame " + App.SequenceFrame;
            return MaxJointDistance(rig, Ch().Pose, poseAt20) > 0.01 ? null : "pose did not change";
        });
        PlainKey(Godot.Key.Space);
        Check("p8 Space stops the sequence (the transport keys follow the editor)", () => !App.SequencePlaying ? null : "still playing");
        Check("p8 playing to the end stops at the last frame", () =>
        {
            sv.PressPlay();
            for (var i = 0; i < 60 && App.SequencePlaying; i++) App.AdvancePlayback(0.25);
            return !App.SequencePlaying && Math.Abs(App.SequenceFrame - App.SequenceEval()!.LastFrame) < 0.5 ? null : $"playing={App.SequencePlaying} frame={App.SequenceFrame}";
        });

        // bake
        var clipsBeforeBake = Proj.Clips.Count;
        sv.BakeLock.ButtonPressed = false;
        sv.PressBake();
        await Settle();
        string baked = "";
        Check("p8 'Bake to clip' makes a NEW normal clip named like the sequence, selected, that reproduces the blended pose", () =>
        {
            if (Proj.Clips.Count != clipsBeforeBake + 1) return $"clips {Proj.Clips.Count}/{clipsBeforeBake}";
            var c = Proj.Clips[^1];
            baked = c.Id.ToString();
            if (c.Name != "Walk to run" || c.IsDraft || c.Skeleton != man.Rig || c.Metadata.GetValueOrDefault("source") != "sequence" || _w.SelectedClipId != baked) return $"name={c.Name} draft={c.IsDraft} sel={_w.SelectedClipId == baked}";
            var ev = App.SequenceEval()!;
            if (c.DurationFrames != ev.LastFrame) return $"duration {c.DurationFrames} vs {ev.LastFrame}";
            var worst = 0.0;
            foreach (var f in new long[] { 0, 8, 15, 30, 45, 60, ev.LastFrame })
                worst = Math.Max(worst, MaxJointDistance(rig, ClipEvaluator.Evaluate(c, rig, Flicks.FromFrame(f, c.Fps)), ev.Evaluate(f).Pose));
            GD.Print($"SELFTEST INFO bake: worst joint error {worst * 1000:0.000} mm over 7 frames");
            return worst < 0.005 ? null : "bake differs from the sequence by " + worst;
        });
        UndoKey();
        await Settle();
        Check("p8 Ctrl+Z removes the baked clip; redo brings it back", () =>
        {
            if (Proj.Clips.Count != clipsBeforeBake) return "clip still there";
            GetViewport().GuiReleaseFocus();
            Key(Godot.Key.Z, shift: true);
            return null;
        });
        await Settle();
        Check("p8 after redo the baked clip exists again", () => Proj.Clips.Any(c => c.Id.ToString() == baked) ? null : "missing");
        sv.BakeLock.ButtonPressed = true;
        sv.PressBake();
        await Settle();
        Check("p8 'Bake to clip' with 'Lock feet' also locks the feet of the baked clip", () =>
            Proj.Clips[^1].Id.ToString() != baked && PinnedKeys(Proj.Clips[^1]) > 0 ? null : $"pinned keys {PinnedKeys(Proj.Clips[^1])}");

        // export = bake, then the existing export dialog
        var edlg = _w.ExportDlg;
        var clipsBeforeExport = Proj.Clips.Count;
        sv.BakeLock.ButtonPressed = false;
        sv.PressExport();
        await Settle();
        var seqGlb = System.IO.Path.Combine(dir, "walk_to_run.glb");
        Check("p8 'Export…' bakes the sequence and opens the existing export dialog on the baked clip", () =>
            Proj.Clips.Count == clipsBeforeExport + 1 && edlg.IsOpen && edlg.SelectedClipId == Proj.Clips[^1].Id.ToString() && edlg.SelectedCharacterId == mid ? null
                : $"clips={Proj.Clips.Count}/{clipsBeforeExport} open={edlg.IsOpen} clip={edlg.SelectedClipId} char={edlg.SelectedCharacterId}");
        edlg.SetPathForTest(seqGlb);
        edlg.PressExport();
        await WaitRun(edlg);
        Check("p8 the baked sequence exports to a valid .glb", () =>
            edlg.LastResult is { Ok: true } r && System.IO.File.Exists(seqGlb) && (bool?)r.Data!["validation"]!["ok"] == true ? null : "export: " + (edlg.LastError ?? edlg.LastResult?.Error?.Message));
        edlg.Hide();
        await Settle();

        // remove an item, delete the sequence, align toggle: all undoable
        Click(sv.Canvas.BarPoint(runItem, 0.5));
        await Settle();
        sv.PressRemoveItem();
        await Settle();
        Check("p8 'Remove clip' takes the item out of the sequence (the clip stays); Ctrl+Z restores it", () =>
        {
            if (Seq().Items.Count != 1 || Proj.Clips.All(c => c.Id.ToString() != runId)) return "items " + Seq().Items.Count;
            UndoKey();
            return null;
        });
        await Settle();
        Check("p8 the removed item is back with its timing", () => Seq().Items.Count == 2 && Seq().Items.Any(i => i.Id.ToString() == runItem) ? null : "items " + Seq().Items.Count);
        sv.AlignRoot.ButtonPressed = false;
        await Settle();
        Check("p8 the 'Continue from the previous clip' switch changes the sequence (sequence.update) and Ctrl+Z reverts it", () =>
        {
            if (Seq().AlignRoot) return "still aligned";
            UndoKey();
            return null;
        });
        await Settle();
        Check("p8 alignment is back on after undo", () => Seq().AlignRoot && sv.AlignRoot.ButtonPressed ? null : "align " + Seq().AlignRoot);
        sv.DeleteButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Settle();
        Check("p8 'Delete' removes the sequence (clips stay) and offers Undo; Ctrl+Z brings it back", () =>
        {
            if (Proj.Sequences.Count != 0 || _w.ToastNode.ActionText != "Undo") return $"sequences={Proj.Sequences.Count} toast={_w.ToastNode.ActionText}";
            UndoKey();
            return null;
        });
        await Settle();
        Check("p8 the sequence is back with both items", () => Proj.Sequences.Count == 1 && Seq().Items.Count == 2 ? null : "sequences " + Proj.Sequences.Count);
        _w.ToastNode.HideNow();
        Click(sv.Canvas.BarPoint(runItem, 0.5));
        sv.Canvas.SelectItem(runItem);
        await Settle();
        Shot(P("p8-en-dark-sequence-final"), GetViewport());

        App.SetLanguage("ar");
        await Settle();
        Check("p8 Arabic: the sequence editor is translated and RTL, the track itself stays left to right", () =>
        {
            if (!App.IsRtl || sv.Canvas.IsLayoutRtl()) return "rtl state";
            var texts = new[] { sv.BakeButton.Text, sv.AddClipButton.Text, sv.NewButton.Text, sv.MatchFeet.Text };
            return texts.All(t => HasArabic(TranslationServer.Translate(t))) && HasArabic(TranslationServer.Translate(_w.Timeline.Tabs[TimelinePanel.TabSequence].Text)) ? null : string.Join(",", texts);
        });
        Shot(P("p8-ar-dark-sequence"), GetViewport());
        App.SetLanguage("en");
        _w.Timeline.SelectTab(TimelinePanel.TabTimeline);
        App.SetAdvancedMode(false);
        await Settle();
        Check("p8 back in Simple mode the Sequence tab is hidden and the editor no longer drives playback", () =>
            !_w.Timeline.Tabs[TimelinePanel.TabSequence].Visible && !App.SequenceMode ? null : "tab still shown");

        // ================================================================ 5. props
        _w.NewProjectNow();
        await Settle();
        man = Ch(); mid = man.Id.ToString(); rig = RigOf(man);
        var pp = _w.PropsPanelNode;
        Check("p8 the top bar has a Props toggle in Simple mode; the Props menu exists", () =>
            _w.PropsPanelButton.IsVisibleInTree() && !pp.Visible ? null : $"button visible={_w.PropsPanelButton.IsVisibleInTree()} panel visible={pp.Visible}");
        _w.PropsPanelButton.ButtonPressed = true;
        await Settle();
        Check("p8 the Props panel opens; with no props it says so", () => pp.Visible && App.Settings.PropsPanel && pp.Count == 0 && !pp.SelectedSectionVisible ? null : $"visible={pp.Visible} count={pp.Count}");
        pp.NameEdit.Text = "Sword";
        pp.SizeX.Value = 0.05; pp.SizeY.Value = 0.9; pp.SizeZ.Value = 0.05;
        pp.AddCylinder.EmitSignal(BaseButton.SignalName.Pressed);
        await Settle();
        string swordId = "";
        Check("p8 adding a cylinder (metres) creates the prop 'Sword', selects it and shows its details", () =>
        {
            if (Proj.Props.Count != 1) return "props " + Proj.Props.Count;
            var p = Proj.Props[0];
            swordId = p.Id.ToString();
            if (p.Name != "Sword" || p.Shape != PropShape.Cylinder || Math.Abs(p.Size.Y - 0.9) > 1e-9 || Math.Abs(p.Size.X - 0.05) > 1e-9 || p.Attachment is not null) return $"{p.Name} {p.Shape} {p.Size}";
            if (_w.Selection.PropId != swordId || !pp.SelectedSectionVisible || pp.NameText != "Sword" || !pp.InfoText.Contains("Cylinder") || !pp.AttachText.StartsWith("Not attached")) return $"sel={_w.Selection.PropId} info='{pp.InfoText}' attach='{pp.AttachText}'";
            return _w.Props.NodeOf(swordId) is not null && _w.Props.NodeCount == 1 ? null : "no 3D node";
        });
        pp.SelectBoneByName("hand_r");
        pp.PressAttach();
        await Settle();
        var handIdx = rig.IndexOf(rig.FindByStandardName("hand_r")!.Id);
        Check("p8 'Attach to bone' (hand_r) glues the prop to the hand: its drawn position is the hand's joint", () =>
        {
            var p = Proj.Props[0];
            if (p.Attachment is null || p.Attachment.Bone != rig.FindByStandardName("hand_r")!.Id || p.Attachment.Character != man.Id) return "not attached to hand_r";
            var w = _w.Props.WorldOf(swordId)!.Value;
            var hand = View(mid)!.Joints[handIdx];
            var d = (CharacterView.ToV3(w.Translation) - hand).Length();
            return d < 1e-4 && pp.AttachText.Contains("hand_r") && pp.TransformCaption == "Offset from the bone" ? null : $"distance {d}, attach text '{pp.AttachText}', caption '{pp.TransformCaption}'";
        });
        UndoKey();
        await Settle();
        Check("p8 Ctrl+Z detaches it again; Ctrl+Shift+Z attaches it", () =>
        {
            if (Proj.Props[0].Attachment is not null) return "still attached";
            GetViewport().GuiReleaseFocus();
            Key(Godot.Key.Z, shift: true);
            return null;
        });
        await Settle();
        Check("p8 after redo the sword is in the hand again", () => Proj.Props[0].Attachment is not null ? null : "not attached");

        // pose the character, the prop follows
        Check("p8 the attached prop follows the hand when the character is posed", () =>
        {
            var before = _w.Props.WorldOf(swordId)!.Value.Translation;
            if (!SetBone(Ch(), BoneId(Ch(), "upper_arm_r"), 0, 0, 60)) return "pose failed";
            var after = _w.Props.WorldOf(swordId)!.Value.Translation;
            var hand = View(mid)!.Joints[handIdx];
            var d = (CharacterView.ToV3(after) - hand).Length();
            return (after - before).Length() > 0.05 && d < 1e-4 ? null : $"moved {(after - before).Length()} distance to hand {d}";
        });
        UndoKey();
        await Settle();

        // select by clicking in the viewport, then move with the gizmo
        _w.Selection.SelectProp(null);
        _w.Selection.SelectCharacter(mid);
        await Settle();
        Check("p8 clicking the prop in the viewport selects it (Props panel list follows)", () =>
        {
            var at = _w.Overlay.ScreenOfProp(swordId);
            if (at is null) return "off screen";
            // aim at the middle of the blade, away from the hand joint
            var w = _w.Props.WorldOf(swordId)!.Value;
            var up = View(mid)!.Skeleton is null ? Vector3.Up : Vector3.Up;
            _ = up;
            MouseButtonAt(at.Value, true); MouseButtonAt(at.Value, false);
            return _w.Selection.PropId == swordId || _w.Selection.BoneId is not null ? null : "nothing selected";
        });
        _w.Selection.SelectProp(swordId);
        _w.Selection.SetTool(StageTool.Move);
        await Settle();
        Check("p8 the Move gizmo drags the prop along a world axis: one undo step ('Move prop'), the offset follows", () =>
        {
            var g = _w.Overlay.PropGizmo;
            var undoBefore = App.Session.UndoHistory.Count;
            var w0 = _w.Props.WorldOf(swordId)!.Value.Translation;
            var len = g.ArrowMetres;
            var p0 = g.AxisPoint(0, len * 0.6f); var p1 = g.AxisPoint(0, len * 0.6f + 0.25f);
            if (p0 is null || p1 is null) return "no gizmo";
            var stage = _w.Stage.GlobalPosition;
            Drag(new[] { p0.Value, (p0.Value + p1.Value) * 0.5f, p1.Value });
            var w1 = _w.Props.WorldOf(swordId)!.Value.Translation;
            var d = w1 - w0;
            GD.Print($"SELFTEST INFO prop move drag: world delta {d.X:0.0000} {d.Y:0.0000} {d.Z:0.0000} (expected +0.25 along x)");
            _ = stage;
            if (Math.Abs(d.X - 0.25) > 0.01 || Math.Abs(d.Y) > 0.01 || Math.Abs(d.Z) > 0.01) return $"delta {d}";
            if (App.Session.UndoHistory.Count != undoBefore + 1 || App.Session.UndoHistory[^1].Label != "Move prop") return $"history +{App.Session.UndoHistory.Count - undoBefore} '{App.Session.UndoHistory[^1].Label}'";
            return Proj.Props[0].Attachment!.Offset.Translation.Length() > 0.1 ? null : "offset unchanged";
        });
        await Settle();
        UndoKey();
        await Settle();
        Check("p8 Ctrl+Z puts the prop back in the hand", () =>
            (_w.Props.WorldOf(swordId)!.Value.Translation - new Vec3(View(mid)!.Joints[handIdx].X, View(mid)!.Joints[handIdx].Y, View(mid)!.Joints[handIdx].Z)).Length() < 1e-4 ? null : "not back");
        _w.Selection.SetTool(StageTool.Rotate);
        await Settle();
        Check("p8 the Rotate gizmo turns the prop about a world axis by exactly the dragged angle (one undo step)", () =>
        {
            var g = _w.Overlay.PropGizmo;
            var q0 = _w.Props.WorldOf(swordId)!.Value.Rotation;
            var undoBefore = App.Session.UndoHistory.Count;
            var path = g.RingPath(1, 7);
            if (path is null) return "no ring";
            Drag(path);
            var q1 = _w.Props.WorldOf(swordId)!.Value.Rotation;
            var a = new Quaternion((float)q0.X, (float)q0.Y, (float)q0.Z, (float)q0.W);
            var b = new Quaternion((float)q1.X, (float)q1.Y, (float)q1.Z, (float)q1.W);
            var angle = Mathf.RadToDeg(a.AngleTo(b));
            var expected = 7 * PropGizmo.DegreesPerRingSample;
            var rel = q1 * q0.Inverse();
            GD.Print($"SELFTEST INFO prop rotate drag: {angle:0.00} deg (expected {expected:0.00}); relative q {rel.X:0.000} {rel.Y:0.000} {rel.Z:0.000} {rel.W:0.000}; dbg '{g.LastDebug}' q0 {q0.X:0.000} {q0.Y:0.000} {q0.Z:0.000} {q0.W:0.000}");
            if (Math.Abs(angle - expected) > 0.5) return $"angle {angle}";
            return App.Session.UndoHistory.Count == undoBefore + 1 && App.Session.UndoHistory[^1].Label == "Rotate prop" ? null : $"history +{App.Session.UndoHistory.Count - undoBefore} '{App.Session.UndoHistory[^1].Label}'";
        });
        UndoKey();
        await Settle();
        Check("p8 Esc cancels a prop gizmo drag completely", () =>
        {
            var before = Proj.Props[0].Attachment!.Offset;
            var undoBefore = App.Session.UndoHistory.Count;
            var path = _w.Overlay.PropGizmo.RingPath(0, 4);
            if (path is null) return "no ring";
            Drag(path, cancelWithEsc: true);
            return Proj.Props[0].Attachment!.Offset.Rotation.ApproxEquals(before.Rotation, 1e-12) && App.Session.UndoHistory.Count == undoBefore && !App.Session.InTransaction ? null : "changed after Esc";
        });

        // typed transform, key, scrub
        Check("p8 the panel's fields set the offset (prop.set_transform) and undo restores it", () =>
        {
            pp.PosY.Value = 0.1;
            var off = Proj.Props[0].Attachment!.Offset.Translation;
            if (Math.Abs(off.Y - 0.1) > 1e-6) return "offset " + off;
            UndoKey();
            return null;
        });
        await Settle();
        Check("p8 the offset is back after undo", () => Math.Abs(Proj.Props[0].Attachment!.Offset.Translation.Y) < 1e-6 && Math.Abs(pp.PosY.Value) < 1e-6 ? null : "offset " + Proj.Props[0].Attachment!.Offset.Translation);
        App.GoToFrame(0);
        Check("p8 the Key button says 'Key prop' while a prop is selected and keys it (key.set props): a clip is created in the same undo step", () =>
        {
            if (_w.Timeline.KeyButton.Text != "Key prop" || _w.Timeline.KeyButton.Disabled) return $"button '{_w.Timeline.KeyButton.Text}' disabled={_w.Timeline.KeyButton.Disabled}";
            var undoBefore = App.Session.UndoHistory.Count;
            _w.Timeline.KeyButton.EmitSignal(BaseButton.SignalName.Pressed);
            if (Proj.Clips.Count != 1) return "clips " + Proj.Clips.Count;
            var c = Proj.Clips[0];
            var propTracks = c.Tracks.Where(t => t.Target.Kind == TargetKind.Prop && t.Target.Id.ToString() == swordId).ToList();
            if (propTracks.Count < 2 || propTracks.Any(t => t.Keys.Count != 1)) return "tracks " + propTracks.Count;
            if (c.Tracks.Any(t => t.Target.Kind != TargetKind.Prop)) return "the character was keyed too";
            return App.Session.UndoHistory.Count == undoBefore + 1 ? null : "history +" + (App.Session.UndoHistory.Count - undoBefore);
        });
        await Settle();
        App.GoToFrame(20);
        pp.PosY.Value = 0.2;
        pp.PressKey();
        await Settle();
        var swordClip = Proj.Clips[0];
        Check("p8 keyed again at frame 20: the sword slides along the hand between the keys exactly as PropEvaluator says", () =>
        {
            App.GoToFrame(0);
            var w0 = _w.Props.WorldOf(swordId)!.Value;
            App.GoToFrame(20);
            var w20 = _w.Props.WorldOf(swordId)!.Value;
            if (Math.Abs((w20.Translation - w0.Translation).Length() - 0.2) > 2e-3) return $"moved {(w20.Translation - w0.Translation).Length()}";
            foreach (var f in new long[] { 0, 7, 10, 20 })
            {
                App.GoToFrame(f);
                var shown = _w.Props.WorldOf(swordId)!.Value;
                var expect = PropEvaluator.WorldAt(Proj, Proj.Props[0], swordClip, rig, Flicks.FromFrame(f, swordClip.Fps));
                if ((shown.Translation - expect.Translation).Length() > 1e-6) return $"frame {f}: shown {shown.Translation} vs PropEvaluator {expect.Translation}";
            }
            return null;
        });
        App.GoToFrame(10);
        await Settle();
        _w.Selection.SetTool(StageTool.Move);
        await Settle();
        Shot(P("p8-en-dark-props"), GetViewport());
        Check("p8 a prop key can be undone with Ctrl+Z", () =>
        {
            var before = swordClip.Tracks.Where(t => t.Target.Kind == TargetKind.Prop).Sum(t => t.Keys.Count);
            UndoKey();
            var c = Proj.Clips[0];
            var after = c.Tracks.Where(t => t.Target.Kind == TargetKind.Prop).Sum(t => t.Keys.Count);
            GetViewport().GuiReleaseFocus();
            Key(Godot.Key.Z, shift: true);
            return after < before ? null : $"keys {before} -> {after}";
        });
        await Settle();
        swordClip = Proj.Clips[0];

        // export the GLB and load it back
        _w.Selection.SelectCharacter(mid);
        _w.OpenExportDialog();
        await Settle();
        var glb = System.IO.Path.Combine(dir, "sword.glb");
        edlg.SetPathForTest(glb);
        edlg.PressExport();
        await WaitRun(edlg);
        Check("p8 Export writes a valid .glb for the clip with the prop animation", () =>
            edlg.LastResult is { Ok: true } r && (bool?)r.Data!["validation"]!["ok"] == true && System.IO.File.Exists(glb) ? null : "export: " + (edlg.LastError ?? edlg.LastResult?.Error?.Message));
        Check("p8 GLTFDocument loads it back: the 'Sword' prop node exists, hangs under the hand bone, and an animation track moves it", () =>
        {
            var doc = new GltfDocument();
            var state = new GltfState();
            var err = doc.AppendFromFile(glb, state);
            if (err != Error.Ok) return "append: " + err;
            var scene = doc.GenerateScene(state);
            if (scene is null) return "no scene";
            try
            {
                var node = scene.FindChild("Sword", true, false);
                if (node is null) return "no node named Sword";
                var parentChain = new List<string>();
                for (var n = node.GetParent(); n is not null && n != scene; n = n.GetParent()) parentChain.Add(n.Name);
                GD.Print("SELFTEST INFO prop node parents: " + string.Join(" < ", parentChain));
                if (!parentChain.Contains("hand_r")) return "Sword is not under hand_r: " + string.Join(" < ", parentChain);
                if (FindOfClass(scene, "AnimationPlayer") is not AnimationPlayer ap) return "no AnimationPlayer";
                var paths = ap.GetAnimationList().SelectMany(a => Enumerable.Range(0, ap.GetAnimation(a).GetTrackCount()).Select(i => ap.GetAnimation(a).TrackGetPath(i).ToString())).ToList();
                GD.Print("SELFTEST INFO animation tracks: " + string.Join(" | ", paths));
                return paths.Any(p => p.Contains("Sword")) ? null : "no track for Sword";
            }
            finally { scene.Free(); }
        });
        edlg.Hide();
        await Settle();

        // detach, remove, import
        _w.Selection.SelectProp(swordId);
        await Settle();
        var handNow = View(mid)!.Joints[handIdx];
        pp.PressDetach();
        await Settle();
        Check("p8 'Detach' frees the prop where it is (keeps its place in the world)", () =>
        {
            var p = Proj.Props[0];
            if (p.Attachment is not null) return "still attached";
            var w = _w.Props.WorldOf(swordId)!.Value.Translation;
            return Math.Abs(p.Transform.Translation.X - w.X) < 1e-6 && pp.AttachText.StartsWith("Not attached") ? null : "position moved";
        });
        UndoKey();
        await Settle();
        pp.PressRemove();
        await Settle();
        Check("p8 'Remove' deletes the prop (the 3D node goes away, the toast offers Undo); Ctrl+Z brings it back", () =>
        {
            if (Proj.Props.Count != 0 || _w.Props.NodeCount != 0 || _w.Selection.PropId is not null || _w.ToastNode.ActionText != "Undo") return $"props={Proj.Props.Count} nodes={_w.Props.NodeCount} toast={_w.ToastNode.ActionText}";
            UndoKey();
            return null;
        });
        await Settle();
        Check("p8 the prop and its 3D node are back after undo", () => Proj.Props.Count == 1 && _w.Props.NodeCount == 1 && Proj.Props[0].Attachment is not null ? null : $"props={Proj.Props.Count} nodes={_w.Props.NodeCount}");
        var crate = Sample("RiggedSimple.glb");
        string? imported = null;
        Check("p8 'Import .glb…' adds a model prop (embedded in the project) and selects it", () =>
        {
            imported = _w.ImportPropPath(crate);
            if (imported is null) return "import failed: " + _w.LastError;
            var p = Proj.Props.Find(x => x.Id.ToString() == imported)!;
            return p.Shape == PropShape.Mesh && Proj.FindAsset(p.Asset) is not null && _w.Selection.PropId == imported && _w.Props.NodeOf(imported) is not null ? null : $"shape={p.Shape}";
        });
        await Settle();
        Shot(P("p8-en-dark-props-imported"), GetViewport());
        _w.RemovePropNow(imported!);
        _w.ToastNode.HideNow();
        await Settle();

        // Arabic panel
        _w.Selection.SelectProp(swordId);
        App.SetLanguage("ar");
        await Settle();
        pp.Refresh();
        Check("p8 Arabic: the Props panel is mirrored and translated; sizes and offsets stay left to right", () =>
        {
            if (!App.IsRtl || !pp.Visible) return "not rtl / hidden";
            return HasArabic(pp.AttachText) && HasArabic(pp.InfoText) && HasArabic(TranslationServer.Translate(pp.AddCylinder.Text)) && pp.InfoText.Contains('⁦') ? null : $"attach='{pp.AttachText}' info='{pp.InfoText}'";
        });
        Shot(P("p8-ar-dark-props"), GetViewport());
        App.SetLanguage("en");
        await Settle();

        // light theme
        App.SetTheme("light");
        await Settle();
        _w.Timeline.SelectTab(TimelinePanel.TabTimeline);
        Shot(P("p8-en-light-props"), GetViewport());
        App.SetTheme("dark");

        // restore
        _w.PropsPanelButton.ButtonPressed = saved.PropsPanel;
        s.ShowContacts = saved.ShowContacts;
        s.LastExportDir = lastExportBackup;
        s.Save();
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        await Settle();
    }
}
