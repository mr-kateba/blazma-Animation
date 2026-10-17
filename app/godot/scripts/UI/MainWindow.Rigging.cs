using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Rigging;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Phase 2 part of the shell: characters in the stage, selection, tools, rigging menu, pose library, import and bone mapping.</summary>
public partial class MainWindow
{
    public SelectionState Selection { get; } = new();
    public CharacterLayer Characters { get; private set; } = null!;
    public SkeletonOverlay Overlay { get; private set; } = null!;
    public ToolRail Rail { get; private set; } = null!;
    public LeftDock Dock { get; private set; } = null!;
    public BoneMappingDialog MappingDialog { get; private set; } = null!;
    public ConfirmationDialog ConfirmDialog => _confirmDialog;
    public string? SelectedPoseId { get; set; }
    public string? LastImportedCharacterId { get; private set; }

    ConfirmationDialog _confirmDialog = null!;
    FileDialog _importDialog = null!;
    Action? _confirmAction;
    HashSet<string> _knownChars = new();
    string? _lastSelChar;
    string? _poseClip, _poseClipRig;

    // ---- construction ------------------------------------------------------------------------------------------------

    void BuildStageLayers()
    {
        Characters = new CharacterLayer { Name = "Characters" };
        Stage.World.AddChild(Characters);
        Overlay = new SkeletonOverlay(Stage, Characters, Selection);
        W.Fill(Overlay);
        _center.AddChild(Overlay);
        Stage.Tools = Overlay;
        Rail = new ToolRail(this, Selection);
        Rail.AnchorTop = 0.5f; Rail.AnchorBottom = 0.5f;
        Rail.GrowVertical = GrowDirection.Both;
        Overlay.Gizmo.DrivenBlocked += ShowDrivenBlocked;
        Overlay.Gizmo.DragEnded += AfterTransaction;
        Selection.Changed += OnSelectionChanged;
        Selection.Changed += () => App.SetFocusCharacter(Selection.CharacterId);
        BuildPropLayer();
    }

    void BuildRigDialogs()
    {
        _confirmDialog = new ConfirmationDialog { Exclusive = true, AutoTranslateMode = AutoTranslateModeEnum.Disabled };
        _confirmDialog.Confirmed += () => { var a = _confirmAction; _confirmAction = null; a?.Invoke(); };
        _confirmDialog.Canceled += () => _confirmAction = null;
        AddChild(_confirmDialog);

        _importDialog = new FileDialog
        {
            UseNativeDialog = true,
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Filters = new[] { "*.glb ; glTF Binary" },
        };
        _importDialog.FileSelected += path => ImportCharacterPath(path);
        AddChild(_importDialog);

        MappingDialog = new BoneMappingDialog(this);
        AddChild(MappingDialog);
    }

    void InitCharacters()
    {
        Characters.SetPalette(_palette);
        Characters.Rebuild(App.Session.Project, true);
        _knownChars = App.Session.Project.Characters.Select(c => c.Id.ToString()).ToHashSet();
        Selection.Set(App.Session.Project.Characters.FirstOrDefault()?.Id.ToString());
        Overlay.FrameSelection();
        _lastSelChar = Selection.CharacterId;
        Dock.RigTree.Rebuild();
        Dock.Poses.Rebuild();
        UpdateRail();
    }

    void PlaceRail()
    {
        const float gap = 12;
        var rtl = App.IsRtl;
        Rail.AnchorLeft = rtl ? 1 : 0; Rail.AnchorRight = rtl ? 1 : 0;
        Rail.GrowHorizontal = rtl ? GrowDirection.Begin : GrowDirection.End;
        Rail.OffsetLeft = rtl ? -gap : gap; Rail.OffsetRight = rtl ? -gap : gap;
        Rail.OffsetTop = 0; Rail.OffsetBottom = 0;
    }

    // ---- session / selection --------------------------------------------------------------------------------------------

    /// <summary>Returns true when the change was fully handled (pose-only edits).</summary>
    bool OnRigSessionChanged(SessionChangedArgs args, bool replaced, bool poseOnly)
    {
        Characters.OnSessionChanged(args);
        var project = App.Session.Project;
        var ids = project.Characters.Select(c => c.Id.ToString()).ToList();
        var added = ids.Where(i => !_knownChars.Contains(i)).ToList();
        _knownChars = ids.ToHashSet();

        var cid = Selection.CharacterId;
        if (replaced) cid = ids.FirstOrDefault();
        else if (added.Count > 0) cid = added[^1];
        else if (cid is null || !ids.Contains(cid)) cid = ids.FirstOrDefault();
        string? bone = Selection.BoneId, control = Selection.ControlId;
        var character = cid is null ? null : project.Characters.Find(c => c.Id.ToString() == cid);
        var rig = character is null ? null : project.FindRig(character.Rig);
        if (cid != Selection.CharacterId || rig is null) { bone = null; control = null; }
        else
        {
            if (bone is not null && rig.Bones.All(b => b.Id.ToString() != bone)) bone = null;
            if (control is not null && rig.Controls.All(c => c.Id.ToString() != control)) control = null;
        }
        Selection.Set(cid, bone, control);
        if (replaced || added.Count > 0) { Overlay.FrameSelection(); LastImportedCharacterId = added.Count > 0 ? added[^1] : null; }

        if (!poseOnly) return false;
        Inspector.RefreshRig();
        UpdateStatus(); UpdateChip(); UpdateMenus(); UpdateRail();
        if (App.IsPreviewing) return true;
        if (App.Session.InTransaction) _historyStale = true;
        else if (App.Settings.AdvancedMode) HistoryPanel.Rebuild();
        return true;
    }

    void OnSelectionChanged()
    {
        if (_lastSelChar != Selection.CharacterId)
        {
            _lastSelChar = Selection.CharacterId;
            Dock.RigTree.Rebuild();
            Dock.Poses.Rebuild();
            Strip.RebuildPoses();
            Overlay.QueueRedraw();
        }
        else Dock.RigTree.UpdateSelection();
        Inspector.RefreshRig();
        UpdateRail();
        UpdateRiggingMenu();
    }

    /// <summary>Everything that depends on the character / rig / library state (not on every pose step).</summary>
    void RefreshRigUi(bool structural)
    {
        Inspector.RefreshRig(structural);
        Dock.RigTree.Rebuild();
        Dock.Poses.Rebuild();
        Strip.RebuildPoses();
        UpdateRail();
        UpdateRiggingMenu();
    }

    /// <summary>Called after a transaction (drag, mapping dialog) ended: history, menus and the pose list may have changed.</summary>
    public void AfterTransaction()
    {
        if (App.Settings.AdvancedMode) HistoryPanel.Rebuild();
        UpdateMenus();
        UpdateStatus();
        UpdateChip();
        Inspector.RefreshRig(true);
        Dock.Poses.Rebuild();
        Strip.RebuildPoses();
        UpdateRail();
    }

    // ---- selection helpers ---------------------------------------------------------------------------------------------------

    public Character? SelectedCharacter => Selection.CharacterId is null ? null : App.Session.Project.Characters.Find(c => c.Id.ToString() == Selection.CharacterId);
    public Rig? SelectedRig => SelectedCharacter is { } c ? App.Session.Project.FindRig(c.Rig) : null;

    /// <summary>The IK chain a bone (upper, lower or end) or control (target or pole) belongs to.</summary>
    (Character character, Rig rig, IkChain chain)? ChainOf(string? boneId, string? controlId)
    {
        var character = SelectedCharacter;
        var rig = SelectedRig;
        if (character is null || rig is null) return null;
        IkChain? chain = null;
        if (boneId is not null)
            chain = rig.IkChains.Find(c => c.End.ToString() == boneId) ?? rig.IkChains.Find(c => c.Lower.ToString() == boneId || c.Upper.ToString() == boneId);
        else if (controlId is not null)
            chain = rig.IkChains.Find(c => c.Target.ToString() == controlId || c.Pole.ToString() == controlId);
        return chain is null ? null : (character, rig, chain);
    }

    void UpdateRail()
    {
        var has = SelectedCharacter is not null;
        var chain = ChainOf(Selection.BoneId, Selection.ControlId);
        var pinned = chain is { } c && PoseSolver.StateOf(c.rig, c.character.Pose, c.chain.Target).Pinned;
        Rail.Update(has, chain is not null, pinned, has);
    }

    // ---- tool actions (rail, menu, keys) -------------------------------------------------------------------------------------

    bool HandleToolKey(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return false;
        if (k.CtrlPressed || k.AltPressed || k.MetaPressed || k.ShiftPressed) return false;
        if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit) return false;
        switch (k.Keycode)
        {
            case Godot.Key.Q: Selection.SetTool(StageTool.Select); return true;
            case Godot.Key.E: Selection.SetTool(StageTool.Rotate); return true;
            case Godot.Key.W: Selection.SetTool(StageTool.Move); return true;
            case Godot.Key.P: TogglePinSelected(); return true;
            case Godot.Key.M: MirrorPose(); return true;
        }
        return false;
    }

    public bool MirrorPose()
    {
        if (SelectedCharacter is not { } c) return false;
        return App.Execute("pose.mirror", new JsonObject { ["characterId"] = c.Id.ToString() }).Ok;
    }

    public void TogglePinSelected()
    {
        if (ChainOf(Selection.BoneId, Selection.ControlId) is not { } c) return;
        var pinned = PoseSolver.StateOf(c.rig, c.character.Pose, c.chain.Target).Pinned;
        SetPin(c.character, c.chain.Target.ToString(), !pinned);
    }

    bool SetPin(Character character, string controlId, bool pinned) =>
        App.Execute("pose.set_pinned", new JsonObject { ["characterId"] = character.Id.ToString(), ["control"] = controlId, ["pinned"] = pinned }).Ok;

    /// <summary>Pin or unpin the chain that belongs to the given bone or control (inspector switch).</summary>
    public void SetPinnedFor(string? boneId, string? controlId, bool pinned)
    {
        if (ChainOf(boneId, controlId) is { } c) SetPin(c.character, c.chain.Target.ToString(), pinned);
    }

    /// <summary>Unpin the chain that currently drives the given bone.</summary>
    public void UnpinDriver(string? boneId)
    {
        var character = SelectedCharacter; var rig = SelectedRig;
        if (character is null || rig is null || boneId is null || !ObjectId.TryParse(boneId, out var id)) return;
        if (PoseSolver.DrivingChain(rig, character.Pose, id) is { } chain) SetPin(character, chain.Target.ToString(), false);
    }

    public void PinBothFeet() => PinChains("Pin both feet", c => c.Name.StartsWith("leg_", StringComparison.Ordinal), true);
    public void UnpinAll() => PinChains("Unpin all", _ => true, false);

    void PinChains(string label, Func<IkChain, bool> pick, bool pinned)
    {
        var character = SelectedCharacter; var rig = SelectedRig;
        if (character is null || rig is null) return;
        using var tx = App.BeginTransaction(label);
        foreach (var chain in rig.IkChains.Where(pick))
        {
            var isPinned = PoseSolver.StateOf(rig, character.Pose, chain.Target).Pinned;
            if (isPinned != pinned) SetPin(character, chain.Target.ToString(), pinned);
        }
        tx.Commit();
        AfterTransaction();
    }

    /// <summary>Reset the selected bone; with no bone selected, ask before resetting the whole pose.</summary>
    public void ResetSelected()
    {
        if (SelectedCharacter is not { } c) return;
        if (Selection.BoneId is { } b) App.Execute("pose.reset", new JsonObject { ["characterId"] = c.Id.ToString(), ["bone"] = b });
        else ConfirmResetPose();
    }

    public void ConfirmResetPose()
    {
        if (SelectedCharacter is not { } c) return;
        Confirm("Reset pose", string.Format(Tr("Reset the whole pose of “{0}” to rest? You can bring it back with Edit > Undo."), c.Name), "Reset", () => ResetPoseNow());
    }

    public bool ResetPoseNow() => SelectedCharacter is { } c && App.Execute("pose.reset", new JsonObject { ["characterId"] = c.Id.ToString() }).Ok;

    void Confirm(string title, string text, string ok, Action action)
    {
        _confirmAction = action;
        ShowDialogText(_confirmDialog, title, text, ok, "Cancel");
        _confirmDialog.PopupCentered(new Vector2I(460, 0));
    }

    public void CopyPose()
    {
        if (SelectedCharacter is not { } c) return;
        var r = App.Execute("pose.get", new JsonObject { ["characterId"] = c.Id.ToString() });
        if (!r.Ok || r.Data?["pose"] is not JsonValue v) return;
        _poseClip = (string)v!;
        _poseClipRig = c.Rig.ToString();
        _toast.Notify(Tr("Pose copied"), _palette.Ok);
    }

    public bool PastePose()
    {
        if (SelectedCharacter is not { } c) return false;
        if (_poseClip is null) { _toast.Notify(Tr("Nothing copied yet. Copy a pose first (Ctrl+C)."), _palette.Warn); return false; }
        if (_poseClipRig != c.Rig.ToString()) { _toast.Notify(Tr("That pose was copied from a different rig."), _palette.Warn); return false; }
        return App.Execute("pose.apply", new JsonObject { ["characterId"] = c.Id.ToString(), ["poseJson"] = _poseClip }).Ok;
    }

    public bool HasPoseClipboard => _poseClip is not null;

    // ---- driven-by-IK feedback --------------------------------------------------------------------------------------------------

    void ShowDrivenBlocked(CharacterView view, int boneIndex)
    {
        var boneId = view.Rig.Bones[boneIndex].Id.ToString();
        Selection.SelectBone(view.CharacterId, boneId);
        ShowDrivenToast(boneId);
    }

    void ShowDrivenToast(string? boneId = null)
    {
        boneId ??= Selection.BoneId;
        var msg = Tr("This bone is driven by a pinned hand or foot. Move that target, or unpin it first.");
        if (boneId is null) { _toast.Notify(msg, _palette.Warn); return; }
        _toast.NotifyAction(msg, _palette.Warn, Tr("Unpin"), () => UnpinDriver(boneId));
    }

    // ---- pose library -------------------------------------------------------------------------------------------------------------------

    public IEnumerable<PoseAsset> LibraryPoses()
    {
        var rig = SelectedRig;
        return rig is null ? Enumerable.Empty<PoseAsset>() : App.Session.Project.PoseLibrary.Where(p => p.Rig == rig.Id);
    }

    public void PromptSavePose()
    {
        if (SelectedCharacter is null) return;
        var n = LibraryPoses().Count() + 1;
        PromptName("Save Pose", "Save", string.Format(CultureInfo.InvariantCulture, "Pose {0}", n), name => SavePoseNamed(name));
    }

    public string? SavePoseNamed(string name)
    {
        if (SelectedCharacter is not { } c) return null;
        var r = App.Execute("poselib.save", new JsonObject { ["characterId"] = c.Id.ToString(), ["name"] = name });
        if (!r.Ok) return null;
        SelectedPoseId = (string?)r.Data?["poseId"];
        Dock.Poses.Rebuild();
        Strip.RebuildPoses();
        return SelectedPoseId;
    }

    public bool ApplySelectedPose() => SelectedCharacter is { } c && SelectedPoseId is { } p
        && App.Execute("poselib.apply", new JsonObject { ["characterId"] = c.Id.ToString(), ["poseId"] = p }).Ok;

    public void PromptRenamePose()
    {
        if (SelectedPoseId is not { } id || App.Session.Project.PoseLibrary.Find(p => p.Id.ToString() == id) is not { } pose) return;
        PromptName("Rename Pose", "Rename", pose.Name, name => RenamePoseNow(id, name));
    }

    public bool RenamePoseNow(string id, string name) =>
        App.Execute("poselib.rename", new JsonObject { ["poseId"] = id, ["name"] = name }).Ok;

    public void ConfirmDeletePose()
    {
        if (SelectedPoseId is not { } id || App.Session.Project.PoseLibrary.Find(p => p.Id.ToString() == id) is not { } pose) return;
        Confirm("Delete pose", string.Format(Tr("Delete the pose “{0}”? You can bring it back with Edit > Undo."), pose.Name), "Delete", () => DeletePoseNow(id));
    }

    public bool DeletePoseNow(string id) => App.Execute("poselib.delete", new JsonObject { ["poseId"] = id }).Ok;

    // ---- characters / import / mapping -----------------------------------------------------------------------------------------------------

    public string? AddMannequin()
    {
        var r = App.Execute("scene.add_mannequin", new JsonObject { ["name"] = NextCharacterName("Mannequin") });
        return r.Ok ? (string?)r.Data?["characterId"] : null;
    }

    string NextCharacterName(string baseName)
    {
        var chars = App.Session.Project.Characters;
        if (chars.All(c => c.Name != baseName)) return baseName;
        var n = 2;
        while (chars.Any(c => c.Name == $"{baseName} {n}")) n++;
        return $"{baseName} {n}";
    }

    public void PromptImport()
    {
        _importDialog.Title = Tr("Import Character");
        _importDialog.PopupCenteredRatio(0.6f);
    }

    /// <summary>Import a .glb as a new character, then open the bone mapping dialog. Returns the character id.</summary>
    public string? ImportCharacterPath(string path, bool openMapping = true)
    {
        _suppressToast = true;
        _lastError = null;
        CommandResult r;
        try { r = App.Execute("rig.import", new JsonObject { ["path"] = path }); }
        finally { _suppressToast = false; }
        if (!r.Ok)
        {
            ShowErrorDialog(_lastError ?? r.Error?.Message ?? "Import failed.");
            return null;
        }
        var characterId = (string?)r.Data?["characterId"];
        var rigId = (string?)r.Data?["rigId"];
        if (characterId is not null) Selection.SelectCharacter(characterId);
        if (openMapping && rigId is not null) OpenBoneMapping(rigId);
        return characterId;
    }

    public void OpenBoneMapping(string? rigId = null)
    {
        rigId ??= SelectedRig?.Id.ToString();
        if (rigId is null) return;
        MappingDialog.Open(rigId);
    }

    public void ConfirmRemoveCharacter()
    {
        if (SelectedCharacter is not { } c) return;
        var id = c.Id.ToString();
        Confirm("Remove character", string.Format(Tr("Remove “{0}” from the scene? You can bring it back with Edit > Undo."), c.Name), "Remove",
            () => App.Execute("character.remove", new JsonObject { ["characterId"] = id }));
    }

    // ---- Rigging menu --------------------------------------------------------------------------------------------------------------------------

    static Shortcut Plain(Godot.Key key, bool ctrl = false)
    {
        var sc = new Shortcut();
        sc.Events.Add(new InputEventKey { Keycode = key, CtrlPressed = ctrl, CommandOrControlAutoremap = ctrl });
        return sc;
    }

    void BuildRiggingMenu()
    {
        var m = _mRigging;
        m.AddItem("Add Mannequin", MAddMannequin);
        m.AddItem("Import Character…", MImport);
        m.AddItem("Remove Character…", MRemoveChar);
        m.AddSeparator();
        m.AddItem("Bone Mapping…", MMapping);
        m.AddSeparator();
        m.AddItem("Mirror Pose", MMirror);
        m.AddItem("Reset Selected Bone", MResetBone);
        m.AddItem("Reset Pose…", MResetPose);
        m.AddItem("Copy Pose", MCopy);
        m.AddItem("Paste Pose", MPaste);
        m.AddSeparator();
        m.AddItem("Pin or Unpin Selected Hand or Foot", MPinSelected);
        m.AddItem("Pin Both Feet", MPinFeet);
        m.AddItem("Unpin All", MUnpinAll);
        m.AddSeparator();
        m.AddItem("Frame Character", MFrame);
        m.AddCheckItem("Show Skeleton", MShowSkeleton);
        m.AddCheckItem("Skeleton on All Characters", MOverlayAll);
        ShortcutOf(m, MMirror, Plain(Godot.Key.M));
        ShortcutOf(m, MCopy, Plain(Godot.Key.C, true));
        ShortcutOf(m, MPaste, Plain(Godot.Key.V, true));
        ShortcutOf(m, MPinSelected, Plain(Godot.Key.P));
        ShortcutOf(m, MFrame, Plain(Godot.Key.F));
        m.AboutToPopup += () => { UpdateRiggingMenu(); };
        m.IdPressed += id => OnMenu((int)id);
    }

    void UpdateRiggingMenu()
    {
        if (_mRigging is null || Characters is null) return;
        var character = SelectedCharacter;
        var rig = SelectedRig;
        var has = character is not null;
        void Enable(int id, bool on) => _mRigging.SetItemDisabled(_mRigging.GetItemIndex(id), !on);
        Enable(MRemoveChar, has);
        Enable(MMirror, has); Enable(MResetBone, has && Selection.BoneId is not null); Enable(MResetPose, has);
        Enable(MCopy, has); Enable(MPaste, has && _poseClip is not null);
        Enable(MPinSelected, ChainOf(Selection.BoneId, Selection.ControlId) is not null);
        var hasLegs = rig is not null && rig.IkChains.Any(c => c.Name.StartsWith("leg_", StringComparison.Ordinal));
        Enable(MPinFeet, hasLegs);
        Enable(MUnpinAll, rig is not null && rig.IkChains.Count > 0);
        Enable(MFrame, has);
        var builtin = rig is null || rig.Source.Kind == RigSourceKind.Builtin;
        var idx = _mRigging.GetItemIndex(MMapping);
        _mRigging.SetItemDisabled(idx, builtin);
        _mRigging.SetItemTooltip(idx, builtin ? Tr("The built-in mannequin is already fully mapped.") : "");
        _mRigging.SetItemChecked(_mRigging.GetItemIndex(MShowSkeleton), App.Settings.ShowSkeleton);
        _mRigging.SetItemChecked(_mRigging.GetItemIndex(MOverlayAll), App.Settings.OverlayAll);
    }

    void OnRiggingMenu(int id)
    {
        switch (id)
        {
            case MAddMannequin: AddMannequin(); break;
            case MImport: PromptImport(); break;
            case MRemoveChar: ConfirmRemoveCharacter(); break;
            case MMapping: OpenBoneMapping(); break;
            case MMirror: MirrorPose(); break;
            case MResetBone: if (SelectedCharacter is { } c && Selection.BoneId is { } b) App.Execute("pose.reset", new JsonObject { ["characterId"] = c.Id.ToString(), ["bone"] = b }); break;
            case MResetPose: ConfirmResetPose(); break;
            case MCopy: CopyPose(); break;
            case MPaste: PastePose(); break;
            case MPinSelected: TogglePinSelected(); break;
            case MPinFeet: PinBothFeet(); break;
            case MUnpinAll: UnpinAll(); break;
            case MFrame: Overlay.FrameSelection(); break;
            case MShowSkeleton: App.SetShowSkeleton(!App.Settings.ShowSkeleton); break;
            case MOverlayAll: App.SetOverlayAll(!App.Settings.OverlayAll); break;
        }
    }

    // ---- bottom dock (Simple mode) ----------------------------------------------------------------------------------------------------------

    void ApplyStripMode()
    {
        Timeline.ApplyMode(App.Settings.AdvancedMode);
    }
}
