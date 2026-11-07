using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 9 part of the shell: the combat overlay layer, the Combat panel and its top-bar toggle, the view-toolbar toggle, the Hit test
/// dialog, the Combo editor actions and the Combat menu (Advanced mode). The panel, dialog and editor only collect input; every change is
/// a command (event.*, combat.set_phases, hitbox.*, hurtbox.*, reaction.generate, combo.*), so it is undoable and the same as what agents do.
/// </summary>
public partial class MainWindow
{
    const int CPanel = 400, CShow = 401, CGenerate = 402, CAddEvent = 403, CHitTest = 404, CCombo = 405;

    public CombatOverlay CombatView { get; private set; } = null!;
    public CombatPanel CombatPanelNode { get; private set; } = null!;
    public HitTestDialog HitTestDlg { get; private set; } = null!;
    public Button CombatButton { get; private set; } = null!;
    public Button CombatPanelButton { get; private set; } = null!;
    PopupMenu _mCombat = null!;

    /// <summary>The Combat panel is open (it is an Advanced-mode panel).</summary>
    bool CombatPanelOpen => App.Settings.CombatPanel && App.Settings.AdvancedMode;

    // ---- construction ------------------------------------------------------------------------------------------------

    void BuildCombatLayers()
    {
        CombatView = new CombatOverlay(Overlay);
        W.Fill(CombatView);
        _center.AddChild(CombatView);
        _center.MoveChild(CombatView, PhysicsView.GetIndex());
        CombatPanelNode = new CombatPanel(this);
    }

    void AddCombatToolbar(HBoxContainer row)
    {
        CombatButton = ViewToggle("Hitboxes", "Show hurtboxes (where a character can be hit) and hitboxes (the part of an attack that hurts) in the viewport, and the combat strip under the timeline.",
            "<rect x='3' y='3.5' width='7' height='7' rx='1.5'/><circle cx='11' cy='10.5' r='3'/>", on => App.SetShowCombat(on));
        row.AddChild(CombatButton);
    }

    /// <summary>Top-bar toggle for the Combat panel, shown in Advanced mode.</summary>
    Control BuildCombatPanelToggle()
    {
        CombatPanelButton = new Button
        {
            Text = "Combat", ToggleMode = true, ThemeTypeVariation = "Transport", FocusMode = FocusModeEnum.None,
            SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = "Show or hide the Combat panel: attack phases, events, hitboxes and hurtboxes of the clip.",
        };
        CombatPanelButton.Toggled += on => App.SetCombatPanel(on);
        return CombatPanelButton;
    }

    void BuildCombatMenu()
    {
        var m = _mCombat;
        m.AddCheckItem("Combat Panel", CPanel);
        m.AddCheckItem("Show Hitboxes and Hurtboxes", CShow);
        m.AddSeparator();
        m.AddItem("Generate Hurtboxes", CGenerate);
        m.AddItem("Add Event at Playhead…", CAddEvent);
        m.AddSeparator();
        m.AddItem("Hit Test…", CHitTest);
        m.AddItem("Combo Editor", CCombo);
        m.AboutToPopup += UpdateCombatMenu;
        m.IdPressed += id => OnCombatMenu((int)id);
    }

    void BuildPhase9Dialogs()
    {
        HitTestDlg = new HitTestDialog(this);
        AddChild(HitTestDlg);
    }

    // ---- menu -------------------------------------------------------------------------------------------------------------

    void OnCombatMenu(int id)
    {
        switch (id)
        {
            case CPanel: App.SetCombatPanel(!App.Settings.CombatPanel); break;
            case CShow: App.SetShowCombat(!App.Settings.ShowCombat); break;
            case CGenerate:
                if (SelectedCharacter is null) { Toast(Tr("Select a character first."), warn: true); break; }
                if (!App.Settings.CombatPanel) App.SetCombatPanel(true);
                CombatPanelNode.SelectPage(CombatPanel.PageHurtboxes);
                App.Execute("hurtbox.generate", new JsonObject { ["characterId"] = SelectedCharacter.Id.ToString() });
                break;
            case CAddEvent: PromptAddEvent(); break;
            case CHitTest: OpenHitTestDialog(); break;
            case CCombo: OpenComboEditor(); break;
        }
    }

    void UpdateCombatMenu()
    {
        if (_mCombat is null || CombatPanelNode is null) return;
        var advanced = App.Settings.AdvancedMode;
        _menuBar.SetMenuHidden(7, !advanced);
        void Enable(int id, bool on) => _mCombat.SetItemDisabled(_mCombat.GetItemIndex(id), !on);
        _mCombat.SetItemChecked(_mCombat.GetItemIndex(CPanel), App.Settings.CombatPanel);
        _mCombat.SetItemChecked(_mCombat.GetItemIndex(CShow), App.Settings.ShowCombat);
        Enable(CGenerate, SelectedCharacter is not null);
        Enable(CAddEvent, App.ActiveClip is not null);
        Enable(CHitTest, App.Session.Project.Characters.Count > 0 && App.Session.Project.Clips.Count > 0);
        Enable(CCombo, App.Session.Project.Characters.Count > 0);
        _menuBar.SetMenuTitle(7, Tr("Combat"));
    }

    /// <summary>Settings changed: the toolbar toggle, the panel toggle and the panel itself, the timeline strip and the overlay.</summary>
    void SyncCombatUi()
    {
        if (CombatPanelNode is null) return;
        var s = App.Settings;
        CombatButton.SetPressedNoSignal(s.ShowCombat);
        CombatButton.Visible = s.AdvancedMode;
        CombatPanelButton.SetPressedNoSignal(s.CombatPanel);
        CombatPanelButton.Visible = s.AdvancedMode;
        CombatPanelNode.SyncVisible();
        CombatView.QueueRedraw();
        Timeline.Combat.Sync();
        UpdateCombatMenu();
    }

    void RefreshPhase9Ui()
    {
        CombatPanelNode.Refresh();
        Timeline.Combo.Sync();
        Timeline.Combat.Sync();
        UpdateCombatMenu();
    }

    public void Toast(string message, bool warn = false) => _toast.Notify(message, warn ? _palette.Warn : _palette.Ok);

    // ---- actions ------------------------------------------------------------------------------------------------------------

    public void OpenHitTestDialog()
    {
        if (HitTestDlg.Visible) return;
        if (!App.Settings.AdvancedMode) App.SetAdvancedMode(true);
        if (App.Session.Project.Characters.Count == 0) { Toast(Tr("Add a character first."), warn: true); return; }
        if (App.Session.Project.Clips.Count == 0) { Toast(Tr("Create an attack clip first, then test it."), warn: true); return; }
        Inspector.Flush();
        HitTestDlg.Open();
    }

    /// <summary>Switch to the Combo tab of the dock (Advanced mode).</summary>
    public void OpenComboEditor()
    {
        if (!App.Settings.AdvancedMode) App.SetAdvancedMode(true);
        Timeline.SelectTab(TimelinePanel.TabCombo);
    }

    /// <summary>Show a hit test result: the attacker and its attack clip are selected, the timeline is on the hit frame and the viewport draws the contact.</summary>
    public void ShowHitTest(HitTestView view, long frame)
    {
        if (App.Playing) App.Pause();
        Selection.SelectCharacter(view.AttackerId);
        SelectClip(view.AttackClipId);
        if (!App.Settings.ShowCombat) App.SetShowCombat(true);
        App.SetHitView(view);
        App.GoToFrame(frame);
    }

    /// <summary>Select the defender and its new reaction clip and play it.</summary>
    public void ShowReaction(string defenderId, string clipId)
    {
        Selection.SelectCharacter(defenderId);
        ShowNewClip(clipId);
    }

    void PromptAddEvent()
    {
        if (App.ActiveClip is not { } clip) return;
        PromptName("Add Event", "Add", Tr("event"), name =>
        {
            if (name.Length == 0) return;
            App.Execute("event.add", new JsonObject { ["clipId"] = clip.Id.ToString(), ["frame"] = App.CurrentFrame, ["name"] = name });
        });
    }

    // ---- combos ------------------------------------------------------------------------------------------------------------------

    string NextComboName()
    {
        var all = App.Session.Project.Combos;
        var n = all.Count + 1;
        while (all.Any(c => c.Name == string.Format(CultureInfo.InvariantCulture, Tr("Combo {0}"), n))) n++;
        return string.Format(CultureInfo.InvariantCulture, Tr("Combo {0}"), n);
    }

    public void PromptNewCombo()
    {
        if (App.Session.Project.Characters.Count == 0) { Toast(Tr("Add a character first, then make a combo for it."), warn: true); return; }
        PromptName("New Combo", "Create", NextComboName(), name => CreateComboNow(name));
    }

    public string? CreateComboNow(string name)
    {
        var character = SelectedCharacter ?? App.Session.Project.Characters.FirstOrDefault();
        if (character is null) return null;
        var r = App.Execute("combo.create", new JsonObject { ["characterId"] = character.Id.ToString(), ["name"] = name });
        if (!r.Ok) return null;
        var id = (string?)r.Data?["comboId"];
        if (id is not null) Timeline.Combo.SelectCombo(id);
        return id;
    }

    public bool DeleteComboNow(string id)
    {
        var name = App.Session.Project.Combos.Find(c => c.Id.ToString() == id)?.Name ?? "";
        var ok = App.Execute("combo.remove", new JsonObject { ["comboId"] = id }).Ok;
        if (ok) _toast.NotifyActionFormat("Combo deleted: {0}", name, _palette.Warn, "Undo", DoUndo);
        return ok;
    }

    /// <summary>combo.add_attack. A clip with attack phases gets a cancel window over its recovery part, so the next attack can follow it.</summary>
    public string? AddComboAttackNow(string comboId, string clipId, string input)
    {
        var clip = App.Session.Project.Clips.Find(c => c.Id.ToString() == clipId);
        var args = new JsonObject { ["comboId"] = comboId, ["clipId"] = clipId };
        if (input.Length > 0) args["input"] = input;
        if (clip?.Combat?.Phases is { } ph)
        {
            var from = ph.RecoveryStart.ToFrame(clip.Fps);
            var to = Math.Min(ph.End.ToFrame(clip.Fps), clip.DurationFrames);
            if (to > from) { args["cancelStartFrame"] = from; args["cancelEndFrame"] = to; }
        }
        var r = ExecuteQuiet("combo.add_attack", args);
        if (!r.Ok) { _toast.Notify(FriendlyCombatError(r), _palette.Err); return null; }
        if (r.Warnings.Any(w => w.Code == "no_hitboxes")) _toast.Notify("This clip has no hitbox yet, so the attack cannot hit anything. Add one in the Combat panel.", _palette.Warn);
        var id = (string?)r.Data?["attackId"];
        Timeline.Combo.Canvas.SelectedAttackId = id;
        Timeline.Combo.Sync();
        return id;
    }

    public bool RemoveComboAttackNow(string comboId, string attackId)
    {
        var ok = App.Execute("combo.remove", new JsonObject { ["comboId"] = comboId, ["attackId"] = attackId }).Ok;
        if (ok) Timeline.Combo.Canvas.SelectedAttackId = null;
        return ok;
    }

    string FriendlyCombatError(CommandResult r) => CodeMessage(r.Error?.Code) ?? r.Error?.Code switch
    {
        "rig_mismatch" => Tr("That clip animates a different character than this combo."),
        "too_large" => Tr("The combo is too big: it has too many attacks or is too long to process."),
        _ => FriendlyError(r.Error?.Message ?? ""),
    };

    /// <summary>combo.bake into a new clip (selected) or a sequence (opened in the Sequence tab). Returns the new id.</summary>
    public string? BakeComboNow(string comboId, bool toSequence, bool footLock)
    {
        var args = new JsonObject { ["comboId"] = comboId, ["target"] = toSequence ? "sequence" : "clip" };
        if (!toSequence) args["footLock"] = footLock;
        var r = ExecuteQuiet("combo.bake", args);
        if (!r.Ok) { _toast.Notify(FriendlyCombatError(r), _palette.Err); return null; }
        if (toSequence)
        {
            var sid = (string?)r.Data?["sequenceId"];
            if (sid is null) return null;
            var sname = App.Session.Project.Sequences.Find(s => s.Id.ToString() == sid)?.Name ?? "";
            App.SetActiveSequence(sid);
            _toast.NotifyActionFormat("Baked into the sequence “{0}”.", sname, _palette.Ok, "Show sequence",
                () => Timeline.SelectTab(TimelinePanel.TabSequence));
            return sid;
        }
        var clipId = (string?)r.Data?["clipId"];
        if (clipId is null) return null;
        SelectClip(clipId);
        var name = App.Session.Project.Clips.Find(c => c.Id.ToString() == clipId)?.Name ?? "";
        _toast.NotifyActionFormat("Baked into the clip “{0}”.", name, _palette.Ok, "Show clip",
            () => Timeline.SelectTab(TimelinePanel.TabTimeline));
        return clipId;
    }
}
