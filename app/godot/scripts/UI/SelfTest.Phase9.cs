using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Combat;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 9 scenario, through the real UI code paths: the Combat panel (phases from markers and typed, events of every kind, a sword prop
/// hitbox and a bone hitbox with active ranges, hurtboxes generated on a second mannequin and edited), the viewport overlay (off by
/// default, persisted toggle, hurtboxes per zone, active hitboxes bright), the timeline strip (phases, hitbox ranges, event markers), the
/// Hit test dialog (swept hit found in the active frames, timeline jumps to it, contact point and normal drawn, "no hit" message), the
/// generated reaction clip, a three-attack combo edited on the combined timeline and baked to a clip and a sequence, the exported sidecar
/// with its combat data, and undo / redo with the real Ctrl+Z / Ctrl+Shift+Z shortcuts. Screenshots are named p9-lang-theme-what.png.
/// </summary>
public partial class SelfTest
{
    Blazma.Core.Model.Clip Swing => ClipNamed("Attack swing")!;
    void RedoKey()
    {
        GetViewport().GuiReleaseFocus();
        Key(Godot.Key.Z, shift: true);
    }

    static string Fr(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    async Task RunPhase9()
    {
        var s = App.Settings;
        var saved = (s.ShowCombat, s.CombatPanel, s.PropsPanel, s.LastExportDir);
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        _w.ShowAgentPanel(false);
        s.ShowCombat = false; s.CombatPanel = false; s.PropsPanel = false;
        _w.NewProjectNow();
        _w.Timeline.SelectTab(TimelinePanel.TabTimeline);
        await Settle();
        var man = Ch();
        var mid = man.Id.ToString();
        var rig = RigOf(man);
        var cp = _w.CombatPanelNode;
        var dir = System.IO.Path.Combine(Godot.ProjectSettings.GlobalizePath("user://"), "p9_selftest");
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        s.LastExportDir = dir;

        // ================================================================ 0. defaults and modes
        Check("p9 combat tools are Advanced: in Simple mode there is no Combat toggle, no hitbox toggle and no panel; the overlay is off by default", () =>
            !_w.CombatPanelButton.IsVisibleInTree() && !_w.CombatButton.IsVisibleInTree() && !cp.Visible && !s.ShowCombat && !s.CombatPanel && !_w.Timeline.Tabs[TimelinePanel.TabCombo].Visible
                ? null : $"panelBtn={_w.CombatPanelButton.IsVisibleInTree()} viewBtn={_w.CombatButton.IsVisibleInTree()} panel={cp.Visible} show={s.ShowCombat} comboTab={_w.Timeline.Tabs[TimelinePanel.TabCombo].Visible}");
        App.SetAdvancedMode(true);
        await Settle();
        Check("p9 Advanced mode shows the Combat toggle (top bar), the Hitboxes toggle (view toolbar, off) and the Combo tab; the Combat menu is enabled", () =>
        {
            if (!_w.CombatPanelButton.IsVisibleInTree() || !_w.CombatButton.IsVisibleInTree() || _w.CombatButton.ButtonPressed || !_w.Timeline.Tabs[TimelinePanel.TabCombo].Visible) return "toggles/tab missing";
            var menu = _w.MenuPopup(8);
            var items = Enumerable.Range(0, menu.ItemCount).Select(i => menu.GetItemText(i)).Where(t => t.Length > 0).ToList();
            return items.Contains("Hit Test…") && items.Contains("Combo Editor") && items.Contains("Combat Panel") ? null : "menu: " + string.Join(",", items);
        });

        // ================================================================ 1. a clip to work on: the attack swing template
        _w.OpenTemplatesDialog();
        await Settle();
        _w.TemplatesDlg.SelectById("builtin.attack_swing");
        _w.TemplatesDlg.Duration.Value = 1.2;
        _w.TemplatesDlg.PressApply();
        await Settle();
        App.Pause();
        var swingId = Swing.Id.ToString();
        Check("p9 the attack swing template is the clip to work on (36 frames)", () => Swing.DurationFrames == 36 && App.ActiveClipId == swingId ? null : $"frames={Swing.DurationFrames}");

        // ================================================================ 2. the Combat panel: phases
        _w.CombatPanelButton.ButtonPressed = true;
        await Settle();
        Check("p9 the Combat toggle opens the panel (persisted) on the Phases page; with no phases it says so; the clip and character are named", () =>
            cp.Visible && s.CombatPanel && cp.Page == CombatPanel.PagePhases && cp.PhaseInfoText.Contains("no attack phases") && cp.ContextText.Contains("Attack swing") && cp.ContextText.Contains(man.Name)
                ? null : $"visible={cp.Visible} page={cp.Page} info='{cp.PhaseInfoText}' ctx='{cp.ContextText}'");
        cp.StartupStart.Value = 0; cp.ActiveStart.Value = 20; cp.ActiveEnd.Value = 10; cp.RecoveryEnd.Value = 36;
        var historyBefore = App.Session.UndoHistory.Count;
        PressButton(cp.SetPhasesButton);
        await Settle();
        Check("p9 phases out of order are refused with a readable message and change nothing", () =>
            Swing.Combat?.Phases is null && App.Session.UndoHistory.Count == historyBefore && _w.ToastNode.Message.Contains("in order") ? null : $"phases={Swing.Combat?.Phases is not null} toast='{_w.ToastNode.Message}'");
        PressButton(cp.FromMarkersButton);
        await Settle();
        Check("p9 'From markers' with no phase markers explains what to do (friendly message, not the raw error)", () =>
            Swing.Combat?.Phases is null && _w.ToastNode.Message.Contains("key-pose phases") && !_w.ToastNode.Message.Contains("fromMarkers") ? null : $"toast='{_w.ToastNode.Message}'");
        Check("p9 Animation > Add Key-pose Phases adds the markers; 'From markers' then reads them: phases set, one undo step ('Set attack phases')", () =>
        {
            if (!_w.AddPhaseMarkers()) return "markers failed";
            GD.Print("SELFTEST INFO markers: " + string.Join(",", Swing.Markers.Select(m => m.Phase + "@" + m.Time.ToFrame(Swing.Fps))) + " active=" + App.ActiveClipId + " Swing=" + swingId);
            var before = App.Session.UndoHistory.Count;
            PressButton(cp.FromMarkersButton);
            var ph = Swing.Combat?.Phases;
            if (ph is null) return "no phases: " + _w.ToastNode.Message;
            var main = Swing.Markers.First(m => m.Phase == KeyPhase.MainAction).Time.ToFrame(Swing.Fps);
            var follow = Swing.Markers.First(m => m.Phase == KeyPhase.FollowThrough).Time.ToFrame(Swing.Fps);
            GD.Print($"SELFTEST INFO phases from markers: startup {ph.StartupStart.ToFrame(Swing.Fps)} active {ph.ActiveStart.ToFrame(Swing.Fps)}-{ph.RecoveryStart.ToFrame(Swing.Fps)} end {ph.End.ToFrame(Swing.Fps)}");
            if (ph.ActiveStart.ToFrame(Swing.Fps) != main || ph.RecoveryStart.ToFrame(Swing.Fps) != follow) return $"active {ph.ActiveStart.ToFrame(Swing.Fps)}-{ph.RecoveryStart.ToFrame(Swing.Fps)} vs markers {main}-{follow}";
            return App.Session.UndoHistory.Count == before + 1 && App.Session.UndoHistory[^1].Label == "Set attack phases" ? null : $"history +{App.Session.UndoHistory.Count - before}";
        });
        await Settle();
        Check("p9 the fields show the phases read from the markers", () =>
        {
            var ph = Swing.Combat!.Phases!;
            return (long)cp.ActiveStart.Value == ph.ActiveStart.ToFrame(Swing.Fps) && (long)cp.ActiveEnd.Value == ph.RecoveryStart.ToFrame(Swing.Fps) && (long)cp.RecoveryEnd.Value == ph.End.ToFrame(Swing.Fps)
                ? null : $"fields {cp.ActiveStart.Value}-{cp.ActiveEnd.Value}-{cp.RecoveryEnd.Value}";
        });
        // typed phases: the scenario uses fixed frames
        cp.StartupStart.Value = 0; cp.ActiveStart.Value = 10; cp.ActiveEnd.Value = 18; cp.RecoveryEnd.Value = 36;
        PressButton(cp.SetPhasesButton);
        await Settle();
        Check("p9 typed phases 0 / 10 / 18 / 36 are stored as startup 0-10, active 10-18, recovery 18-36", () =>
        {
            var ph = Swing.Combat?.Phases;
            if (ph is null) return "no phases";
            long F(Flicks t) => t.ToFrame(Swing.Fps);
            return F(ph.StartupStart) == 0 && F(ph.ActiveStart) == 10 && F(ph.RecoveryStart) == 18 && F(ph.End) == 36 ? null : $"{F(ph.StartupStart)} {F(ph.ActiveStart)} {F(ph.RecoveryStart)} {F(ph.End)}";
        });
        App.GoToFrame(14);
        await Settle();
        Check("p9 the panel tells which part the playhead is in (active at frame 14), in the part's colour", () =>
            cp.PhaseInfoText.Contains("14") && cp.PhaseInfoText.Contains("Active") ? null : cp.PhaseInfoText);
        PressButton(cp.ClearPhasesButton);
        await Settle();
        Check("p9 'Clear' removes the phases; Ctrl+Z brings them back; redo clears again", () =>
        {
            if (Swing.Combat?.Phases is not null) return "not cleared";
            UndoKey();
            if (Swing.Combat?.Phases is null) return "undo did not restore";
            RedoKey();
            if (Swing.Combat?.Phases is not null) return "redo did not clear";
            UndoKey();
            return Swing.Combat?.Phases is null ? "final undo failed" : null;
        });
        await Settle();

        // ================================================================ 3. events
        cp.SelectPage(CombatPanel.PageEvents);
        await Settle();
        void AddEvent(string name, int kind, int frame, string payload)
        {
            cp.EventName.Text = name; cp.EventKind.Select(kind); cp.EventFrame.Value = frame; cp.EventPayload.Text = payload;
            PressButton(cp.EventAdd);
        }
        AddEvent("footstep_l", 2, 4, "{\"foot\": \"left\", \"volume\": 0.8}");
        AddEvent("whoosh", 3, 10, "");
        AddEvent("impact", 1, 14, "{\"strong\": true}");
        AddEvent("trail_on", 4, 11, "");
        await Settle();
        Check("p9 events of every kind are added with their payload and listed in frame order with frame, name and kind", () =>
        {
            if (Swing.Events.Count != 4) return "events " + Swing.Events.Count;
            var f = Swing.Events.First(e => e.Name == "footstep_l");
            if (f.Kind != EventKind.Footstep || f.Time.ToFrame(Swing.Fps) != 4 || f.Data.GetValueOrDefault("foot") != "left" || f.Data.GetValueOrDefault("volume") != "0.8") return $"footstep: {f.Kind} {string.Join(",", f.Data)}";
            if (Swing.Events.First(e => e.Name == "impact").Kind != EventKind.Hit || Swing.Events.First(e => e.Name == "whoosh").Kind != EventKind.Sound || Swing.Events.First(e => e.Name == "trail_on").Kind != EventKind.Vfx) return "kinds";
            if (cp.EventCount != 4 || !cp.ItemTextOfEvent(0).Contains("footstep_l") || !cp.ItemTextOfEvent(0).Contains("Footstep") || !cp.ItemTextOfEvent(3).Contains("impact")) return $"list: {cp.ItemTextOfEvent(0)} / {cp.ItemTextOfEvent(3)}";
            return null;
        });
        Check("p9 an event with a payload that is not JSON is refused with a message and not added", () =>
        {
            var n = Swing.Events.Count;
            AddEvent("bad", 0, 5, "volume=0.8");
            return Swing.Events.Count == n && _w.ToastNode.Showing ? null : $"events {Swing.Events.Count} toast={_w.ToastNode.Message}";
        });
        var whooshId = Swing.Events.First(e => e.Name == "whoosh").Id.ToString();
        cp.SelectEventById(whooshId);
        await Settle();
        Check("p9 selecting an event loads it into the form and moves the playhead to its frame", () =>
            cp.EventName.Text == "whoosh" && cp.EventKind.Selected == 3 && (long)cp.EventFrame.Value == 10 && App.CurrentFrame == 10 ? null : $"{cp.EventName.Text} kind={cp.EventKind.Selected} frame={cp.EventFrame.Value} playhead={App.CurrentFrame}");
        cp.EventName.Text = "swoosh"; cp.EventFrame.Value = 11;
        PressButton(cp.EventUpdate);
        await Settle();
        Check("p9 'Update' renames and moves the event", () =>
            Swing.Events.FirstOrDefault(e => e.Id.ToString() == whooshId) is { } e && e.Name == "swoosh" && e.Time.ToFrame(Swing.Fps) == 11 ? null : "not updated");
        PressButton(cp.EventRemove);
        await Settle();
        Check("p9 'Remove' deletes it; Ctrl+Z brings it back", () =>
        {
            if (Swing.Events.Any(e => e.Id.ToString() == whooshId)) return "still there";
            UndoKey();
            return Swing.Events.Any(e => e.Id.ToString() == whooshId) ? null : "undo failed";
        });
        await Settle();

        // ================================================================ 4. a sword prop and the hitboxes
        var pp = _w.PropsPanelNode;
        pp.NameEdit.Text = "Sword";
        pp.SizeX.Value = 0.05; pp.SizeY.Value = 0.9; pp.SizeZ.Value = 0.05;
        pp.AddCylinder.EmitSignal(BaseButton.SignalName.Pressed);
        await Settle();
        pp.SelectBoneByName("hand_r");
        pp.PressAttach();
        await Settle();
        var swordId = Proj.Props.First(p => p.Name == "Sword").Id.ToString();
        App.GoToFrame(14);
        _w.Selection.SelectCharacter(mid);
        cp.SelectPage(CombatPanel.PageHitboxes);
        await Settle();
        Check("p9 the Hitboxes page starts empty and offers the clip's bones and the project's props as targets", () =>
            cp.HitboxCount == 0 && cp.AttachTarget.ItemCount > 10 ? null : $"hitboxes={cp.HitboxCount} targets={cp.AttachTarget.ItemCount}");
        cp.HitName.Text = "Blade";
        cp.HitShape.Kind.Select(1); cp.HitShape.UpdateVisibility();
        cp.HitShape.Radius.Value = 0.06; cp.HitShape.Length.Value = 0.7;
        cp.ChooseAttach(true, "Sword");
        cp.OffY.Value = 0.25;
        cp.Damage.Value = 25; cp.Knockback.Value = 0.6; cp.Hitstop.Value = 4; cp.Tags.Text = "slash, heavy";
        var undoBeforeAdd = App.Session.UndoHistory.Count;
        PressButton(cp.HitAdd);
        await Settle();
        string bladeId = "";
        Check("p9 a hitbox on the sword prop: capsule 0.06 x 0.7, offset, damage, knockback, hit-stop, tags, active during the active phase; one undo step", () =>
        {
            var h = Swing.Combat?.Hitboxes.FirstOrDefault();
            if (h is null) return "no hitbox: " + _w.ToastNode.Message;
            bladeId = h.Id.ToString();
            if (h.Name != "Blade" || h.Shape.Kind != CollisionShapeKind.Capsule || Math.Abs(h.Shape.Radius - 0.06) > 1e-9 || Math.Abs(h.Shape.Length - 0.7) > 1e-9) return $"shape {h.Shape.Kind} {h.Shape.Radius} {h.Shape.Length}";
            if (h.Attachment.Kind != HitboxAttachKind.Prop || h.Attachment.Target.ToString() != swordId || Math.Abs(h.Attachment.Offset.Translation.Y - 0.25) > 1e-6) return "attachment";
            if (h.Damage != 25 || Math.Abs(h.Knockback - 0.6) > 1e-9 || h.HitstopFrames != 4 || string.Join(",", h.Tags) != "slash,heavy") return $"values {h.Damage} {h.Knockback} {h.HitstopFrames} {string.Join(",", h.Tags)}";
            var ranges = CombatEvaluator.ActiveFrames(h, Swing.Fps).ToList();
            if (ranges.Count != 1 || ranges[0] != (10, 18)) return "active " + string.Join(";", ranges);
            if (App.Session.UndoHistory.Count != undoBeforeAdd + 1 || App.Session.UndoHistory[^1].Label != "Add hitbox") return $"history +{App.Session.UndoHistory.Count - undoBeforeAdd}";
            return cp.HitboxCount == 1 && cp.ItemTextOfHitbox(0).Contains("Blade") && cp.ItemTextOfHitbox(0).Contains("Sword") && cp.ItemTextOfHitbox(0).Contains("10–18") ? null : "list: " + cp.ItemTextOfHitbox(0);
        });
        // a second hitbox on a bone with two active ranges, edited and removed
        cp.HitName.Text = "Fist";
        cp.HitShape.Kind.Select(0); cp.HitShape.UpdateVisibility(); cp.HitShape.Radius.Value = 0.08;
        cp.ChooseAttach(false, "hand_l");
        cp.OffY.Value = 0; cp.ActiveFrames.Text = "10-12,14-16";
        PressButton(cp.HitAdd);
        await Settle();
        string fistId = "";
        Check("p9 a hitbox on the bone hand_l with the active ranges '10-12,14-16'", () =>
        {
            var h = Swing.Combat?.Hitboxes.FirstOrDefault(x => x.Name == "Fist");
            if (h is null) return "none: " + _w.ToastNode.Message;
            fistId = h.Id.ToString();
            var ranges = CombatEvaluator.ActiveFrames(h, Swing.Fps).ToList();
            return h.Attachment.Kind == HitboxAttachKind.Bone && h.Attachment.Target == rig.FindByStandardName("hand_l")!.Id && ranges.SequenceEqual(new[] { (10L, 12L), (14L, 16L) }) && h.Shape.Kind == CollisionShapeKind.Sphere
                ? null : $"{h.Attachment.Kind} {string.Join(";", ranges)}";
        });
        cp.SelectHitboxById(fistId);
        cp.Damage.Value = 7; cp.ActiveFrames.Text = "10-13";
        PressButton(cp.HitApply);
        await Settle();
        Check("p9 selecting a hitbox loads it; 'Apply to selected' changes damage and ranges", () =>
        {
            var h = Swing.Combat!.Hitboxes.First(x => x.Id.ToString() == fistId);
            return h.Damage == 7 && CombatEvaluator.ActiveFrames(h, Swing.Fps).SequenceEqual(new[] { (10L, 13L) }) ? null : $"{h.Damage} {string.Join(";", CombatEvaluator.ActiveFrames(h, Swing.Fps))}";
        });
        PressButton(cp.HitRemove);
        await Settle();
        Check("p9 'Remove' deletes the selected hitbox (the sword's stays); Ctrl+Z / Ctrl+Shift+Z restore and remove it", () =>
        {
            if (Swing.Combat!.Hitboxes.Count != 1 || Swing.Combat.Hitboxes[0].Id.ToString() != bladeId) return "after remove: " + Swing.Combat.Hitboxes.Count;
            UndoKey();
            if (Swing.Combat!.Hitboxes.Count != 2) return "undo failed";
            RedoKey();
            return Swing.Combat!.Hitboxes.Count == 1 ? null : "redo failed";
        });
        await Settle();

        // ================================================================ 5. hurtboxes on a second mannequin
        var dummy = _w.AddMannequin();
        await Settle();
        Check("p9 scene.add_mannequin adds the second character and selects it", () => dummy is not null && _w.Selection.CharacterId == dummy ? null : "dummy " + dummy);
        var dc = Ch(dummy);
        cp.SelectPage(CombatPanel.PageHurtboxes);
        await Settle();
        Check("p9 the Hurtboxes page says the new character has none", () => cp.HurtboxCount == 0 && cp.HurtInfoText.Contains("no hurtboxes") && cp.GenerateButton.Text == "Generate hurtboxes" ? null : $"count={cp.HurtboxCount} info='{cp.HurtInfoText}'");
        var undoBeforeGen = App.Session.UndoHistory.Count;
        PressButton(cp.GenerateButton);
        await Settle();
        Check("p9 'Generate hurtboxes' builds a capsule per body part from the skeleton (one undo step); zones: head high, legs low, trunk middle", () =>
        {
            var hb = dc.Combat?.Hurtboxes;
            if (hb is null || hb.Count < 14) return "hurtboxes " + hb?.Count;
            var drig = RigOf(dc);
            HitZone Z(string std) => hb.First(h => h.Bone == drig.FindByStandardName(std)!.Id).Zone;
            if (Z("head") != HitZone.High || Z("calf_l") != HitZone.Low || Z("spine_02") != HitZone.Mid) return $"zones {Z("head")} {Z("calf_l")} {Z("spine_02")}";
            if (cp.HurtboxCount != hb.Count || cp.GenerateButton.Text != "Regenerate hurtboxes") return $"panel {cp.HurtboxCount} '{cp.GenerateButton.Text}'";
            return App.Session.UndoHistory.Count == undoBeforeGen + 1 ? null : "history +" + (App.Session.UndoHistory.Count - undoBeforeGen);
        });
        var headBox = dc.Combat!.Hurtboxes.First(h => h.Bone == RigOf(dc).FindByStandardName("head")!.Id);
        var radiusBefore = headBox.Shape.Radius;
        cp.SelectHurtboxById(headBox.Id.ToString());
        await Settle();
        Check("p9 selecting a hurtbox shows its editor with the bone it follows", () => cp.HurtEditVisible ? null : "editor hidden");
        cp.HurtShape.Radius.Value = 0.2;
        cp.HurtZone.Select(1);
        PressButton(cp.HurtApply);
        await Settle();
        Check("p9 editing a hurtbox changes its radius and zone", () =>
        {
            var h = Ch(dummy).Combat!.Hurtboxes.First(x => x.Id == headBox.Id);
            return Math.Abs(h.Shape.Radius - 0.2) < 1e-9 && h.Zone == HitZone.Mid && radiusBefore != 0.2 ? null : $"{h.Shape.Radius} {h.Zone}";
        });
        UndoKey();
        await Settle();
        Check("p9 Ctrl+Z restores the hurtbox exactly", () => Math.Abs(Ch(dummy).Combat!.Hurtboxes.First(x => x.Id == headBox.Id).Shape.Radius - radiusBefore) < 1e-9 ? null : "radius not restored");
        var countBefore = Ch(dummy).Combat!.Hurtboxes.Count;
        cp.SelectHurtboxById(Ch(dummy).Combat!.Hurtboxes.First(h => h.Bone == RigOf(dc).FindByStandardName("hand_l")!.Id).Id.ToString());
        PressButton(cp.HurtRemove);
        await Settle();
        Check("p9 'Remove' deletes one hurtbox; undo brings it back", () =>
        {
            if (Ch(dummy).Combat!.Hurtboxes.Count != countBefore - 1) return "not removed";
            UndoKey();
            return Ch(dummy).Combat!.Hurtboxes.Count == countBefore ? null : "undo failed";
        });
        await Settle();

        // ================================================================ 6. the viewport overlay and the timeline strip
        _w.Selection.SelectCharacter(mid);
        _w.SelectClip(swingId);
        App.GoToFrame(14);
        await Settle();
        Check("p9 with the toggle off nothing is drawn and the timeline strip is hidden", () =>
            _w.CombatView.DrawnHurtboxes == 0 && _w.CombatView.DrawnHitboxes == 0 && !_w.Timeline.Combat.Visible ? null : $"hurt={_w.CombatView.DrawnHurtboxes} hit={_w.CombatView.DrawnHitboxes} strip={_w.Timeline.Combat.Visible}");
        _w.CombatButton.ButtonPressed = true;
        await Settle();
        Check("p9 the Hitboxes toggle turns the overlay and the strip on and is persisted in the settings file", () =>
        {
            if (!s.ShowCombat) return "setting not on";
            var reloaded = new AppSettings();
            reloaded.Load();
            return reloaded.ShowCombat && _w.Timeline.Combat.Visible ? null : $"reloaded={reloaded.ShowCombat} strip={_w.Timeline.Combat.Visible}";
        });
        var at14 = (_w.CombatView.DrawnHitboxes, _w.CombatView.DrawnActiveHitboxes);
        App.GoToFrame(2);
        await Settle();
        var at2 = (_w.CombatView.DrawnHitboxes, _w.CombatView.DrawnActiveHitboxes);
        App.GoToFrame(14);
        await Settle();
        Check("p9 at frame 14 (active) the sword hitbox is drawn bright; at frame 2 it is drawn dim", () =>
            at14 == (1, 1) && at2 == (1, 0) ? null : $"frame14 {at14} frame2 {at2}");
        _w.Selection.SelectCharacter(dummy);
        await Settle();
        Check("p9 the selected character's hurtboxes are drawn (one shape each)", () =>
            _w.CombatView.DrawnHurtboxes == Ch(dummy).Combat!.Hurtboxes.Count ? null : $"drawn {_w.CombatView.DrawnHurtboxes} of {Ch(dummy).Combat!.Hurtboxes.Count}");
        _w.Selection.SelectCharacter(mid);
        await Settle();
        var strip = _w.Timeline.Combat;
        Check("p9 the timeline strip shows 3 phase bands, the sword's active range and 4 event markers", () =>
        {
            var bands = strip.Bands();
            return strip.Visible && bands.Count == 3 && bands[1] == ("active", 10, 18) && strip.RangeCount == 1 && strip.EventCount == 4 && strip.Size.Y is > 20 and < 60 ? null
                : $"visible={strip.Visible} bands={string.Join(",", bands)} ranges={strip.RangeCount} events={strip.EventCount} h={strip.Size.Y}";
        });
        var impact = Swing.Events.First(e => e.Name == "impact");
        Check("p9 hovering an event marker names it with its kind and frame; clicking it with the mouse selects it and jumps there", () =>
        {
            var g = strip.EventCenter(impact.Id.ToString());
            var tip = strip._GetTooltip(g - strip.GlobalPosition);
            if (!tip.Contains("impact") || !tip.Contains("Hit") || !tip.Contains("14")) return "tooltip: " + tip;
            App.GoToFrame(0);
            Click(g);
            return App.CurrentFrame == 14 && cp.SelectedEventId == impact.Id.ToString() && cp.Page == CombatPanel.PageEvents ? null : $"frame={App.CurrentFrame} sel={cp.SelectedEventId}";
        });
        await Settle();
        App.GoToFrame(14);
        cp.SelectPage(CombatPanel.PageHitboxes);
        cp.SelectHitboxById(bladeId);
        _w.Selection.SelectCharacter(mid);
        await Settle();
        Shot(P("p9-en-dark-combat-panel"), GetViewport());

        // hurtboxes for the attacker too (the exported sidecar carries them)
        cp.SelectPage(CombatPanel.PageHurtboxes);
        PressButton(cp.GenerateButton);
        await Settle();
        Check("p9 hurtboxes on the attacker as well", () => Ch(mid).Combat?.Hurtboxes.Count >= 14 ? null : "none");

        // ================================================================ 7. the hit test
        var hdlg = _w.HitTestDlg;
        _w.Selection.SelectCharacter(mid);
        PressButton(cp.HitTestButton);
        await Settle();
        Check("p9 the Hit test dialog opens from the panel; attacker, clip and defender are preselected", () =>
            hdlg.IsOpen && hdlg.RunEnabled && hdlg.Attacker.GetItemText(hdlg.Attacker.Selected) == man.Name && hdlg.AttackClip.GetItemText(hdlg.AttackClip.Selected) == "Attack swing" && hdlg.Defender.GetItemText(hdlg.Defender.Selected) == dc.Name
                ? null : $"open={hdlg.IsOpen} atk={hdlg.Attacker.GetItemText(hdlg.Attacker.Selected)} clip={hdlg.AttackClip.GetItemText(hdlg.AttackClip.Selected)} def={hdlg.Defender.GetItemText(hdlg.Defender.Selected)}");
        hdlg.SelectAttacker(mid); hdlg.SelectAttackClip(swingId); hdlg.SelectDefender(dummy!);
        hdlg.DefZ.Value = 8; hdlg.DefX.Value = 0; hdlg.DefFacing.Value = 180;
        hdlg.PressRun();
        await Settle();
        Check("p9 a defender far away: 'No hit' in words, no result is kept, Generate reaction stays off", () =>
            hdlg.StatusText.StartsWith("No hit") && App.HitView is null && hdlg.ReactionButton.Disabled ? null : $"status='{hdlg.StatusText}' view={App.HitView is not null}");
        double hitZ = double.NaN;
        foreach (var z in new[] { 1.2, 1.0, 0.9, 0.8, 0.7, 0.6, 0.5, 0.4, 0.3 })
        {
            hdlg.DefZ.Value = z;
            hdlg.PressRun();
            GD.Print($"SELFTEST INFO hit test at z={z}: {hdlg.StatusText}");
            if (App.HitView is not null) { hitZ = z; break; }
        }
        await Settle();
        JsonObject? hit = null;
        Check("p9 with the defender in reach the Swing hits: a first hit inside the sword's active frames 10-17", () =>
        {
            if (App.HitView is null) return "no hit at any distance: " + hdlg.StatusText;
            hit = hdlg.FirstHit;
            var frame = long.Parse(hit!["frame"]!.ToJsonString());
            GD.Print($"SELFTEST INFO hit at z={hitZ}: frame {frame} time {hit["time"]} hitbox {hit["hitbox"]} bone {hit["bone"]} zone {hit["zone"]} from {hit["from"]} strength {hit["strength"]} swept {hit["swept"]} point {hit["point"]} normal {hit["normal"]}");
            return frame is >= 10 and <= 17 && (string?)hit["hitbox"] == "Blade" && !string.IsNullOrEmpty((string?)hit["bone"]) && hdlg.StatusText.StartsWith("Hit on frame") ? null : $"frame={frame} hitbox={hit["hitbox"]} status='{hdlg.StatusText}'";
        });
        Check("p9 the result names frame, body part, zone, side and strength; the timeline jumped to the hit frame and the attack clip is selected", () =>
        {
            var d = hdlg.DetailsText;
            var frame = long.Parse(hit!["frame"]!.ToJsonString());
            if (!d.Contains("Body part") || !d.Contains("Hit from") || !d.Contains("Strength") || !d.Contains("Exact moment")) return "details: " + d;
            return App.CurrentFrame == frame && _w.SelectedClipId == swingId && _w.Selection.CharacterId == mid && s.ShowCombat ? null : $"frame={App.CurrentFrame} (hit {frame}) clip={_w.SelectedClipId == swingId}";
        });
        await Settle();
        Shot(P("p9-en-dark-hit-test-dialog"), hdlg);
        hdlg.Hide();
        await Settle();
        Check("p9 the viewport marks the contact point with its normal arrow, outlines the hit hurtbox and shows the defender's hurtboxes at the tested place", () =>
        {
            var v = _w.CombatView;
            v.ForceDraw();
            if (!v.DrewContact || !v.DrewMarkedHurtbox || v.DrawnDefenderHurtboxes < 10) return $"contact={v.DrewContact} marked={v.DrewMarkedHurtbox} defender={v.DrawnDefenderHurtboxes}";
            var rect = new Rect2(Vector2.Zero, _w.Stage.Size);
            return v.ContactScreen is { } c && rect.HasPoint(c) && v.NormalTipScreen is { } n && n.DistanceTo(c) > 8 ? null : $"contact {v.ContactScreen} tip {v.NormalTipScreen}";
        });
        Shot(P("p9-en-dark-overlay-hit"), GetViewport());
        Check("p9 editing the clip drops the stale result (no contact point is drawn for old data)", () =>
        {
            var fistsBefore = Swing.Combat!.Hitboxes.Count;
            App.Execute("hitbox.update", new JsonObject { ["clipId"] = swingId, ["hitboxId"] = bladeId, ["damage"] = 26 });
            var dropped = App.HitView is null;
            UndoKey();
            return dropped && Swing.Combat!.Hitboxes.Count == fistsBefore ? null : "result kept after an edit";
        });
        await Settle();
        // run again for the reaction
        _w.OpenHitTestDialog();
        await Settle();
        hdlg.SelectAttacker(mid); hdlg.SelectAttackClip(swingId); hdlg.SelectDefender(dummy!);
        hdlg.DefZ.Value = hitZ;
        hdlg.PressRun();
        await Settle();
        var clipsBefore = Proj.Clips.Count;
        var reactArgs = (JsonObject)App.HitView!.ReactionArgs!.DeepClone();
        PressButton(hdlg.ReactionButton);
        await Settle();
        string reactionId = "";
        Check("p9 'Generate reaction' makes a new flinch clip for the defender from the hit (direction, height, strength); it is selected and playing on the defender", () =>
        {
            if (Proj.Clips.Count != clipsBefore + 1 || hdlg.LastReactionClipId is null) return $"clips {clipsBefore} -> {Proj.Clips.Count}: {hdlg.StatusText}";
            reactionId = hdlg.LastReactionClipId;
            var c = Proj.Clips.First(x => x.Id.ToString() == reactionId);
            GD.Print($"SELFTEST INFO reaction clip: {c.Name}, {c.DurationFrames} frames, {KeyTotal(c)} keys, args {reactArgs.ToJsonString()}");
            if (c.Skeleton != dc.Rig || c.Metadata.GetValueOrDefault("source") != "reaction" || KeyTotal(c) < 4 || c.DurationFrames < 8) return $"clip: skeleton={c.Skeleton == dc.Rig} source={c.Metadata.GetValueOrDefault("source")} keys={KeyTotal(c)}";
            if (c.Metadata.GetValueOrDefault("reaction.height") != (string?)reactArgs["height"]) return "height " + c.Metadata.GetValueOrDefault("reaction.height");
            return App.ActiveClipId == reactionId && _w.Selection.CharacterId == dummy && (App.Playing || App.CurrentFrame > 0) ? null : $"active={App.ActiveClipId == reactionId} char={_w.Selection.CharacterId == dummy} playing={App.Playing}";
        });
        App.Pause();
        UndoKey();
        await Settle();
        Check("p9 Ctrl+Z removes the reaction clip; Ctrl+Shift+Z brings it back", () =>
        {
            if (Proj.Clips.Count != clipsBefore) return "clip still there";
            RedoKey();
            return null;
        });
        await Settle();
        Check("p9 after redo the reaction clip is back", () => Proj.Clips.Any(x => x.Id.ToString() == reactionId) ? null : "missing");
        hdlg.Hide();
        _w.Selection.SelectCharacter(mid);
        _w.SelectClip(swingId);
        await Settle();

        // ================================================================ 8. a three-attack combo
        _w.ApplyTemplateNow("builtin.attack_swing", mid, 1.5, "Heavy swing");
        await Settle();
        App.Pause();
        var heavyId0 = ClipNamed("Heavy swing")!.Id.ToString();
        var heavyId = heavyId0;
        Blazma.Core.Model.Clip Heavy() => ClipNamed("Heavy swing")!;
        _w.SelectClip(heavyId);
        cp.SelectPage(CombatPanel.PagePhases);
        cp.StartupStart.Value = 0; cp.ActiveStart.Value = 12; cp.ActiveEnd.Value = 22; cp.RecoveryEnd.Value = 45;
        PressButton(cp.SetPhasesButton);
        cp.SelectPage(CombatPanel.PageHitboxes);
        await Settle();
        cp.HitName.Text = "Blade";
        cp.HitShape.Kind.Select(1); cp.HitShape.UpdateVisibility(); cp.HitShape.Radius.Value = 0.08; cp.HitShape.Length.Value = 0.7;
        cp.ChooseAttach(true, "Sword");
        cp.OffY.Value = 0.25; cp.Damage.Value = 40; cp.Knockback.Value = 0.9; cp.Hitstop.Value = 6; cp.Tags.Text = "heavy";
        PressButton(cp.HitAdd);
        await Settle();
        Check("p9 a second attack clip 'Heavy Swing' (45 frames) with its own phases 12-22 and sword hitbox", () =>
            Heavy().Combat?.Phases is not null && Heavy().Combat!.Hitboxes.Count == 1 && Heavy().DurationFrames == 45 ? null : $"phases={Heavy().Combat?.Phases is not null} boxes={Heavy().Combat?.Hitboxes.Count} frames={Heavy().DurationFrames}");
        _w.SelectClip(swingId);
        _w.OpenComboEditor();
        await Settle();
        var cv = _w.Timeline.Combo;
        Check("p9 the Combo tab opens (Advanced), the transport row gives way, nothing exists yet", () =>
            _w.Timeline.Tab == TimelinePanel.TabCombo && cv.IsVisibleInTree() && !_w.Timeline.KeyButton.IsVisibleInTree() && Proj.Combos.Count == 0 && cv.InfoText.Contains("Create a combo") ? null : $"tab={_w.Timeline.Tab} info='{cv.InfoText}'");
        cv.PressNew();
        await Settle();
        Check("p9 'New…' asks for a name; confirming creates the combo for the selected character", () =>
        {
            var nd = _w.NameDialog;
            if (!nd.Visible || nd.Title != "New Combo") return "dialog: " + nd.Visible + " " + nd.Title;
            var edit = nd.GetChildren().OfType<LineEdit>().First();
            edit.Text = "Triple slash";
            nd.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
            nd.Hide();
            return Proj.Combos.Count == 1 && Proj.Combos[0].Name == "Triple slash" && Proj.Combos[0].Character == man.Id && cv.ActiveComboId == Proj.Combos[0].Id.ToString() ? null : "combos " + Proj.Combos.Count;
        });
        await Settle();
        var comboId = Proj.Combos[0].Id.ToString();
        Combo Cb() => Proj.Combos.Find(c => c.Id.ToString() == comboId)!;
        void AddAttack(string clip, string input)
        {
            cv.ChooseClip(clip); cv.InputEdit.Text = input;
            cv.PressAdd();
        }
        AddAttack(swingId, "X");
        AddAttack(heavyId, "X");
        AddAttack(swingId, "Y");
        await Settle();
        Check("p9 three attacks are added in order, each with its input and a cancel window over its recovery part", () =>
        {
            var a = Cb().Attacks;
            if (a.Count != 3) return "attacks " + a.Count;
            if (a[0].Input != "X" || a[1].Input != "X" || a[2].Input != "Y" || a[0].Clip.ToString() != swingId || a[1].Clip.ToString() != heavyId) return "order/inputs";
            if (a[0].CancelStart?.ToFrame(Swing.Fps) != 18 || a[0].CancelEnd?.ToFrame(Swing.Fps) != 36 || a[1].CancelStart?.ToFrame(Heavy().Fps) != 22 || a[1].CancelEnd?.ToFrame(Heavy().Fps) != 45) return "windows";
            return cv.Canvas.Bars.Count == 3 || cv.Canvas.Plan?.Attacks.Count == 3 ? null : "plan";
        });
        var a2 = Cb().Attacks[1].Id.ToString();
        cv.Canvas.QueueRedraw();
        await Settle();
        Check("p9 clicking the second attack's bar on the combined timeline selects it; its fields show the window", () =>
        {
            Click(cv.Canvas.BarCenter(a2));
            return cv.SelectedAttackId == a2 && cv.AttackRowVisible && (long)cv.CancelStart.Value == 22 && (long)cv.CancelEnd.Value == 45 && cv.CaptionText == "Heavy swing" ? null
                : $"sel={cv.SelectedAttackId} row={cv.AttackRowVisible} {cv.CancelStart.Value}-{cv.CancelEnd.Value} caption='{cv.CaptionText}'";
        });
        await Settle();
        cv.CancelStart.Value = 24; cv.CancelEnd.Value = 40; cv.Link.Value = 30; cv.Blend.Value = 5;
        cv.PressApply();
        await Settle();
        Check("p9 'Apply' sets the cancel window 24-40, the link frame 30 and the 5-frame fade-in; the next attack starts at the link", () =>
        {
            var at = Cb().Attacks[1];
            if (at.CancelStart?.ToFrame(Heavy().Fps) != 24 || at.CancelEnd?.ToFrame(Heavy().Fps) != 40 || at.Link?.ToFrame(Heavy().Fps) != 30 || at.BlendFrames != 5) return $"{at.CancelStart?.ToFrame(Heavy().Fps)} {at.CancelEnd?.ToFrame(Heavy().Fps)} {at.Link?.ToFrame(Heavy().Fps)} {at.BlendFrames}";
            var plan = cv.Canvas.Plan!;
            var inspect = App.Query("combo.inspect", new JsonObject { ["comboId"] = comboId });
            var attacks = (inspect.Data!["attacks"] as JsonArray)!.OfType<JsonObject>().ToList();
            // the canvas and combo.inspect agree on where every attack starts
            for (var i = 0; i < 3; i++) if ((long)attacks[i]["startFrame"]! != plan.Attacks[i].StartFrame) return $"attack {i}: inspect {attacks[i]["startFrame"]} vs plan {plan.Attacks[i].StartFrame}";
            return plan.Attacks[2].StartFrame == plan.Attacks[1].StartFrame + 30 && plan.Attacks[1].StartFrame == 18 ? null : $"starts {string.Join(",", plan.Attacks.Select(p => p.StartFrame))}";
        });
        Check("p9 an inverted cancel window is refused with a message and changes nothing", () =>
        {
            var before = App.Session.UndoHistory.Count;
            cv.CancelStart.Value = 40; cv.CancelEnd.Value = 30;
            cv.PressApply();
            return App.Session.UndoHistory.Count == before && _w.ToastNode.Message.Contains("cancel window") ? null : $"history +{App.Session.UndoHistory.Count - before} toast='{_w.ToastNode.Message}'";
        });
        UndoKey();
        await Settle();
        Check("p9 Ctrl+Z undoes the last combo edit (the window goes back), Ctrl+Shift+Z redoes it", () =>
        {
            var at = Cb().Attacks[1];
            if (at.CancelStart?.ToFrame(Heavy().Fps) != 22) return "undo: " + at.CancelStart?.ToFrame(Heavy().Fps);
            RedoKey();
            return null;
        });
        await Settle();
        Check("p9 after redo the window is 24-40 again", () => Cb().Attacks[1].CancelStart?.ToFrame(Heavy().Fps) == 24 ? null : "window " + Cb().Attacks[1].CancelStart?.ToFrame(Heavy().Fps));
        Shot(P("p9-en-dark-combo-editor"), GetViewport());

        var clipsBeforeBake = Proj.Clips.Count;
        var plan0 = cv.Canvas.Plan!;
        cv.PressBakeClip();
        await Settle();
        string bakedId = "";
        Check("p9 'Bake to clip' adds one normal clip (blended, the length of the combined timeline) with the attacks' hitboxes cut at each cancel and a cancel_window event per window; it is selected", () =>
        {
            if (Proj.Clips.Count != clipsBeforeBake + 1) return $"clips {clipsBeforeBake} -> {Proj.Clips.Count}: {_w.ToastNode.Message}";
            var c = Proj.Clips.First(x => x.Name == "Triple slash");
            bakedId = c.Id.ToString();
            var windows = c.Events.Count(e => e.Name == "cancel_window");
            GD.Print($"SELFTEST INFO baked combo: {c.DurationFrames} frames, {c.Combat?.Hitboxes.Count} hitboxes, {windows} cancel_window events, plan last frame {plan0.LastFrame}");
            if (Math.Abs(c.DurationFrames - (plan0.LastFrame + 1)) > 1 || c.IsDraft || c.Skeleton != man.Rig) return $"frames {c.DurationFrames} vs {plan0.LastFrame + 1}";
            if ((c.Combat?.Hitboxes.Count ?? 0) < 3 || windows < 2) return $"hitboxes={c.Combat?.Hitboxes.Count} windows={windows}";
            return App.ActiveClipId == bakedId ? null : "not selected";
        });
        UndoKey();
        await Settle();
        Check("p9 Ctrl+Z removes the baked clip; Ctrl+Shift+Z brings it back", () =>
        {
            if (Proj.Clips.Count != clipsBeforeBake) return "clip still there";
            RedoKey();
            return null;
        });
        await Settle();
        _w.OpenComboEditor();
        await Settle();
        var seqBefore = Proj.Sequences.Count;
        cv.PressBakeSequence();
        await Settle();
        Check("p9 'Bake to sequence' adds an editable sequence with one item per attack and makes it the active one", () =>
        {
            if (Proj.Sequences.Count != seqBefore + 1) return "sequences " + Proj.Sequences.Count;
            var seq = Proj.Sequences[^1];
            return seq.Items.Count == 3 && App.ActiveSequenceId == seq.Id.ToString() && _w.ToastNode.ActionText == "Show sequence" ? null : $"items={seq.Items.Count} toast={_w.ToastNode.ActionText}";
        });
        UndoKey();
        await Settle();
        Check("p9 Ctrl+Z removes the baked sequence", () => Proj.Sequences.Count == seqBefore ? null : "sequence still there");
        RedoKey();
        await Settle();
        _w.ToastNode.HideNow();

        // ================================================================ 9. export: the sidecar carries the combat data
        _w.Timeline.SelectTab(TimelinePanel.TabTimeline);
        _w.Selection.SelectCharacter(mid);
        _w.SelectClip(swingId);
        App.GoToFrame(14);
        await Settle();
        _w.OpenExportDialog();
        await Settle();
        var edlg = _w.ExportDlg;
        var glb = System.IO.Path.Combine(dir, "Swing.glb");
        edlg.SetPathForTest(glb);
        edlg.PressExport();
        await WaitRun(edlg);
        await Settle();
        Check("p9 the export validates and writes the sidecar; its 'combat' has the phases, the sword hitbox, the hurtboxes; its events have their kinds", () =>
        {
            if (edlg.LastResult is not { Ok: true } r || (bool?)r.Data!["validation"]!["ok"] != true) return "export: " + (edlg.LastError ?? edlg.LastResult?.Error?.Message);
            var path = System.IO.Path.Combine(dir, "Swing.blazma.json");
            if (!System.IO.File.Exists(path)) return "no sidecar";
            var side = JsonNode.Parse(System.IO.File.ReadAllText(path))!.AsObject();
            if (side["combat"] is not JsonObject combat) return "no combat object in the sidecar";
            var active = combat["phases"]?["active"];
            var boxes = (combat["hitboxes"] as JsonArray)!.OfType<JsonObject>().ToList();
            var hurt = (combat["hurtboxes"] as JsonArray)?.Count ?? 0;
            var kinds = (side["events"] as JsonArray)!.OfType<JsonObject>().Select(e => (string?)e["kind"]).ToList();
            GD.Print($"SELFTEST INFO sidecar combat: active {active}, hitboxes {boxes.Count} ({string.Join(",", boxes.Select(b => (string?)b["name"] + "/" + (string?)b["attachment"]?["kind"] + "/" + (string?)b["attachment"]?["name"]))}), hurtboxes {hurt}, event kinds {string.Join(",", kinds)}");
            if ((int?)active?["startFrame"] != 10 || (int?)active?["endFrame"] != 18) return "phases " + combat["phases"];
            if (boxes.Count != 1 || (string?)boxes[0]["attachment"]?["kind"] != "prop" || (string?)boxes[0]["attachment"]?["name"] != "Sword") return "hitboxes " + boxes.Count;
            return hurt == Ch(mid).Combat!.Hurtboxes.Count && kinds.Contains("footstep") && kinds.Contains("hit") && kinds.Contains("vfx") ? null : $"hurtboxes {hurt}, kinds {string.Join(",", kinds)}";
        });
        edlg.Hide();
        await Settle();

        // ================================================================ 10. Arabic and light theme
        _w.SelectClip(swingId);
        App.GoToFrame(14);
        cp.SelectPage(CombatPanel.PageHitboxes);
        cp.SelectHitboxById(bladeId);
        App.SetLanguage("ar");
        await Settle();
        cp.Refresh();
        Check("p9 Arabic: the Combat panel is mirrored and translated; frame numbers inside Arabic text stay left to right", () =>
        {
            if (!App.IsRtl || !cp.Visible) return "not rtl / hidden";
            cp.SelectPage(CombatPanel.PagePhases);
            cp.Refresh();
            var ok = HasArabic(cp.PhaseInfoText) && HasArabic(cp.ContextText) && cp.PhaseInfoText.Contains('⁦') && HasArabic(TranslationServer.Translate(cp.SetPhasesButton.Text)) && HasArabic(TranslationServer.Translate(cp.HitTestButton.Text));
            cp.SelectPage(CombatPanel.PageHitboxes);
            cp.Refresh();
            return ok && cp.ItemTextOfHitbox(0).Contains('⁦') ? null : $"info='{cp.PhaseInfoText}' hitbox='{cp.ItemTextOfHitbox(0)}'";
        });
        cp.SelectHitboxById(bladeId);
        await Settle();
        Shot(P("p9-ar-dark-combat-panel"), GetViewport());
        _w.OpenHitTestDialog();
        await Settle();
        hdlg.SelectAttacker(mid); hdlg.SelectAttackClip(swingId); hdlg.SelectDefender(dummy!);
        foreach (var z in new[] { hitZ, 0.7, 0.9, 0.6, 1.0, 0.5, 0.4, 0.3, 1.1, 1.2 })
        {
            hdlg.DefZ.Value = z;
            hdlg.PressRun();
            await Settle();
            if (hdlg.FirstHit is not null) break;
        }
        GD.Print($"SELFTEST INFO dbgAr hb={Swing.Combat?.Hitboxes.Count} act={string.Join(";", Swing.Combat?.Hitboxes.Select(h => h.Name + ":" + string.Join(",", CombatEvaluator.ActiveFrames(h, Swing.Fps))) ?? new string[0])} hurt={Ch(dummy).Combat?.Hurtboxes.Count} atk={hdlg.AttackClip.GetItemText(hdlg.AttackClip.Selected)} defclip={hdlg.DefenderClip.GetItemText(hdlg.DefenderClip.Selected)} x={hdlg.DefX.Value} f={hdlg.DefFacing.Value} err={hdlg.LastError}");
        Check("p9 Arabic: the Hit test result is in Arabic, numbers stay left to right, the contact is shown", () =>
            HasArabic(hdlg.StatusText) && HasArabic(hdlg.DetailsText) && hdlg.DetailsText.Contains('⁦') && App.HitView is not null && HasArabic(TranslationServer.Translate(hdlg.ReactionButton.Text)) ? null : $"status='{hdlg.StatusText}' details='{hdlg.DetailsText}'");
        Shot(P("p9-ar-dark-hit-test-dialog"), hdlg);
        hdlg.Hide();
        await Settle();
        _w.CombatView.ForceDraw();
        Shot(P("p9-ar-dark-overlay-hit"), GetViewport());
        _w.OpenComboEditor();
        await Settle();
        cv.Canvas.SelectedAttackId = a2;
        cv.Sync();
        await Settle();
        Check("p9 Arabic: the Combo editor is translated and its track stays left to right", () =>
            HasArabic(TranslationServer.Translate(cv.BakeClipButton.Text)) && HasArabic(cv.InfoText) && cv.InfoText.Contains('⁦') && cv.Canvas.LayoutDirection == Control.LayoutDirectionEnum.Ltr ? null : $"info='{cv.InfoText}'");
        Shot(P("p9-ar-dark-combo-editor"), GetViewport());
        _w.Timeline.SelectTab(TimelinePanel.TabTimeline);
        App.GoToFrame(14);
        await Settle();
        Shot(P("p9-ar-dark-combat-strip"), GetViewport());
        App.SetLanguage("en");
        await Settle();
        Shot(P("p9-en-dark-combat-strip"), GetViewport());
        App.SetTheme("light");
        await Settle();
        Shot(P("p9-en-light-combat-panel"), GetViewport());
        App.SetTheme("dark");
        await Settle();

        // ================================================================ 11. reload: combat data survives save and load
        Check("p9 the project saves and loads back with the combat data intact (phases, hitboxes, events, hurtboxes, combo)", () =>
        {
            var file = System.IO.Path.Combine(dir, "combat.blazanim");
            if (!App.SaveAs(file)) return "save failed";
            if (!App.Open(file)) return "open failed: " + _w.LastError;
            var c = Proj.Clips.First(x => x.Name == "Attack swing");
            return c.Combat?.Phases is not null && c.Combat.Hitboxes.Count == 1 && c.Events.Count == 4 && Proj.Characters.Any(x => x.Combat?.Hurtboxes.Count >= 14) && Proj.Combos.Count == 1 && Proj.Combos[0].Attacks.Count == 3 ? null : "data missing after reload";
        });
        await Settle();

        // restore
        _w.CombatPanelButton.ButtonPressed = saved.CombatPanel;
        s.ShowCombat = saved.ShowCombat; s.PropsPanel = saved.PropsPanel; s.LastExportDir = saved.LastExportDir;
        s.Save();
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        await Settle();
    }
}
