using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 8 part of the shell: the Templates browser, Retarget and Foot lock dialogs, the Props layer, panel and menu, the sequence
/// editor's actions and the menu entries. The dialogs and panels only collect input; every change is a command (template.*, clip.retarget,
/// clip.foot_lock, prop.*, sequence.*), so it is undoable and the same as what agents do. Undo and redo replace the project's lists of
/// sequences, props and templates (and clips), so everything is looked up by id.
/// </summary>
public partial class MainWindow
{
    const int MTemplates = 82, MRetarget = 83, MFootLock = 84, MShowContacts = 85, MSeqEditor = 86;
    const int PPanel = 300, PBox = 301, PSphere = 302, PCylinder = 303, PImport = 304, PAttach = 305, PDetach = 306, PRemove = 307, PKey = 308;

    public TemplatesDialog TemplatesDlg { get; private set; } = null!;
    public RetargetDialog RetargetDlg { get; private set; } = null!;
    public FootLockDialog FootLockDlg { get; private set; } = null!;
    public PropLayer Props { get; private set; } = null!;
    public PropsPanel PropsPanelNode { get; private set; } = null!;
    public Button PropsPanelButton { get; private set; } = null!;
    public string? LastImportedPropId { get; private set; }
    PopupMenu _mProps = null!;
    FileDialog _propImportDialog = null!;

    // ---- construction ------------------------------------------------------------------------------------------------

    /// <summary>The 3D props next to the characters; the overlay outlines and moves the selected one.</summary>
    void BuildPropLayer()
    {
        Props = new PropLayer(Characters);
        Stage.World.AddChild(Props);
        Overlay.Props = Props;
        Props.PropsChanged += Overlay.QueueRedraw;
        Overlay.PropGizmo.DragEnded += AfterTransaction;
        PropsPanelNode = new PropsPanel(this);
    }

    void BuildPhase8Dialogs()
    {
        TemplatesDlg = new TemplatesDialog(this);
        AddChild(TemplatesDlg);
        RetargetDlg = new RetargetDialog(this);
        AddChild(RetargetDlg);
        FootLockDlg = new FootLockDialog(this);
        AddChild(FootLockDlg);
        _propImportDialog = new FileDialog
        {
            UseNativeDialog = true, Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenFile,
            Filters = new[] { "*.glb ; glTF Binary" },
        };
        _propImportDialog.FileSelected += path => ImportPropPath(path);
        AddChild(_propImportDialog);
    }

    void BuildPhase8AnimationMenu()
    {
        var m = _mAnim;
        m.AddSeparator();
        m.AddItem("Templates…", MTemplates);
        m.AddItem("Retarget Clip…", MRetarget);
        m.AddItem("Foot Lock…", MFootLock);
        m.AddCheckItem("Show Foot Contacts", MShowContacts);
        m.AddItem("Sequence Editor", MSeqEditor);
    }

    void BuildPropsMenu()
    {
        var m = _mProps;
        m.AddCheckItem("Props Panel", PPanel);
        m.AddSeparator();
        m.AddItem("Add Box", PBox);
        m.AddItem("Add Sphere", PSphere);
        m.AddItem("Add Cylinder", PCylinder);
        m.AddItem("Import Prop…", PImport);
        m.AddSeparator();
        m.AddItem("Attach Prop to Selected Bone", PAttach);
        m.AddItem("Detach Prop", PDetach);
        m.AddItem("Key Prop", PKey);
        m.AddItem("Remove Prop", PRemove);
        m.AboutToPopup += UpdatePropsMenu;
        m.IdPressed += id => OnPropsMenu((int)id);
    }

    /// <summary>Top-bar toggle for the Props panel (Simple and Advanced).</summary>
    Control BuildPropsPanelToggle()
    {
        PropsPanelButton = new Button
        {
            Text = "Props", ToggleMode = true, ThemeTypeVariation = "Transport", FocusMode = FocusModeEnum.None,
            SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = "Show or hide the Props panel: add shapes or models, attach them to a bone, move them and key them.",
        };
        PropsPanelButton.Toggled += on => App.SetPropsPanel(on);
        return PropsPanelButton;
    }

    // ---- menus --------------------------------------------------------------------------------------------------------------

    void OnPhase8Menu(int id)
    {
        switch (id)
        {
            case MTemplates: OpenTemplatesDialog(); break;
            case MRetarget: OpenRetargetDialog(); break;
            case MFootLock: OpenFootLockDialog(); break;
            case MShowContacts: App.SetShowContacts(!App.Settings.ShowContacts); break;
            case MSeqEditor: OpenSequenceEditor(); break;
        }
    }

    void OnPropsMenu(int id)
    {
        switch (id)
        {
            case PPanel: App.SetPropsPanel(!App.Settings.PropsPanel); break;
            case PBox: AddPropShape("box", PropsPanelNode.SizeVector); break;
            case PSphere: AddPropShape("sphere", PropsPanelNode.SizeVector); break;
            case PCylinder: AddPropShape("cylinder", PropsPanelNode.SizeVector); break;
            case PImport: PromptImportProp(); break;
            case PAttach:
                if (Selection.PropId is { } a && Selection.BoneId is { } b) AttachPropNow(a, b, false);
                else _toast.Notify(Tr("Select a prop and a bone first."), _palette.Warn);
                break;
            case PDetach: if (Selection.PropId is { } d) DetachPropNow(d, true); break;
            case PKey: if (Selection.PropId is { } k) App.KeyPropAtCurrent(k); break;
            case PRemove: if (Selection.PropId is { } r) RemovePropNow(r); break;
        }
    }

    void UpdatePropsMenu()
    {
        if (_mProps is null || PropsPanelNode is null) return;
        var prop = Props.Find(Selection.PropId);
        void Enable(int id, bool on) => _mProps.SetItemDisabled(_mProps.GetItemIndex(id), !on);
        _mProps.SetItemChecked(_mProps.GetItemIndex(PPanel), App.Settings.PropsPanel);
        Enable(PAttach, prop is not null && Selection.BoneId is not null);
        Enable(PDetach, prop?.Attachment is not null);
        Enable(PKey, prop is not null);
        Enable(PRemove, prop is not null);
    }

    void UpdatePhase8Menu()
    {
        if (_mAnim is null || TemplatesDlg is null) return;
        var chars = App.Session.Project.Characters.Count;
        void Enable(int id, bool on) => _mAnim.SetItemDisabled(_mAnim.GetItemIndex(id), !on);
        Enable(MTemplates, chars > 0);
        Enable(MRetarget, App.Session.Project.Clips.Count > 0 && chars > 1);
        Enable(MFootLock, App.Physics.TryResolve(out _, out _, out _));
        Enable(MSeqEditor, App.Settings.AdvancedMode);
        _mAnim.SetItemChecked(_mAnim.GetItemIndex(MShowContacts), App.Settings.ShowContacts);
        UpdatePropsMenu();
    }

    /// <summary>Settings changed: the Props toggle, panel placement and visibility.</summary>
    void SyncPropsUi()
    {
        if (PropsPanelNode is null) return;
        PropsPanelButton.SetPressedNoSignal(App.Settings.PropsPanel);
        PropsPanelNode.SyncVisible();
        PropsPanelNode.ReserveBelow = HistoryPanel.Visible ? 170 : 0;
        Props.Refresh();
        UpdatePropsMenu();
    }

    // ---- templates ----------------------------------------------------------------------------------------------------------------

    public void OpenTemplatesDialog()
    {
        if (TemplatesDlg.Visible) return;
        if (App.Session.Project.Characters.Count == 0)
        {
            _toast.Notify(Tr("Add a character first, then apply a template to it."), _palette.Warn);
            return;
        }
        Inspector.Flush();
        TemplatesDlg.Open();
    }

    /// <summary>template.apply; on success the new clip is selected and playing. The caller shows the error.</summary>
    public CommandResult ApplyTemplateNow(string templateId, string characterId, double? seconds, string? name)
    {
        Inspector.Flush();
        var args = new JsonObject { ["templateId"] = templateId, ["characterId"] = characterId };
        if (seconds is { } s) args["durationSeconds"] = Math.Round(s, 3);
        if (!string.IsNullOrWhiteSpace(name)) args["name"] = name;
        var r = ExecuteQuiet("template.apply", args);
        if (r.Ok && (string?)r.Data?["clipId"] is { } clipId)
        {
            ShowNewClip(clipId);
            var clipName = Proj8.Clips.Find(c => c.Id.ToString() == clipId)?.Name ?? "";
            _toast.Notify(string.Format(CultureInfo.InvariantCulture, Tr("Clip added: {0}"), clipName), _palette.Ok);
        }
        return r;
    }

    static Project Proj8 => AppController.Instance.Session.Project;

    /// <summary>Select a clip a command just added and play it from the start.</summary>
    void ShowNewClip(string clipId)
    {
        SelectClip(clipId);
        App.GoToStart();
        App.Play();
    }

    // ---- retarget -----------------------------------------------------------------------------------------------------------------

    public void OpenRetargetDialog()
    {
        if (RetargetDlg.Visible) return;
        if (App.Session.Project.Clips.Count == 0) { _toast.Notify(Tr("Create a clip first, then copy it onto another character."), _palette.Warn); return; }
        if (App.Session.Project.Characters.Count < 2) { _toast.Notify(Tr("Add or import a second character first, then copy the clip onto it."), _palette.Warn); return; }
        Inspector.Flush();
        RetargetDlg.Open();
    }

    public CommandResult RetargetNow(string clipId, string fromCharacterId, string toCharacterId, string? name, bool footLock)
    {
        Inspector.Flush();
        var args = new JsonObject { ["clipId"] = clipId, ["fromCharacterId"] = fromCharacterId, ["toCharacterId"] = toCharacterId, ["footLock"] = footLock };
        if (!string.IsNullOrWhiteSpace(name)) args["name"] = name;
        var r = ExecuteQuiet("clip.retarget", args);
        if (r.Ok && (string?)r.Data?["clipId"] is { } id)
        {
            Selection.SelectCharacter(toCharacterId);
            ShowNewClip(id);
        }
        return r;
    }

    // ---- foot lock ------------------------------------------------------------------------------------------------------------------

    public void OpenFootLockDialog()
    {
        if (FootLockDlg.Visible) return;
        if (!App.Physics.TryResolve(out _, out _, out _))
        {
            _toast.Notify(Tr("Key a clip for this character first, then lock its feet."), _palette.Warn);
            return;
        }
        Inspector.Flush();
        FootLockDlg.Open();
    }

    // ---- props ------------------------------------------------------------------------------------------------------------------------

    string NextPropName(string shape)
    {
        var baseName = Tr(shape switch { "sphere" => "Sphere", "cylinder" => "Cylinder", _ => "Box" });
        var props = App.Session.Project.Props;
        if (props.All(p => p.Name != baseName)) return baseName;
        var n = 2;
        while (props.Any(p => p.Name == $"{baseName} {n}")) n++;
        return $"{baseName} {n}";
    }

    /// <summary>prop.add a box, sphere or cylinder in the scene (free), select it and open the Props panel. Returns the prop id.</summary>
    public string? AddPropShape(string shape, Vec3 size, string? name = null)
    {
        var args = new JsonObject { ["name"] = name ?? NextPropName(shape), ["shape"] = shape, ["sizeX"] = size.X, ["sizeY"] = size.Y, ["sizeZ"] = size.Z };
        var r = App.Execute("prop.add", args);
        if (!r.Ok) return null;
        var id = (string?)r.Data?["propId"];
        if (id is not null) Selection.SelectProp(id);
        if (!App.Settings.PropsPanel) App.SetPropsPanel(true);
        return id;
    }

    public void PromptImportProp()
    {
        _propImportDialog.Title = Tr("Import Prop");
        _propImportDialog.PopupCenteredRatio(0.6f);
    }

    /// <summary>prop.import a .glb file as a prop and select it. Returns the prop id.</summary>
    public string? ImportPropPath(string path)
    {
        _suppressToast = true;
        _lastError = null;
        CommandResult r;
        try { r = App.Execute("prop.import", new JsonObject { ["path"] = path }); }
        finally { _suppressToast = false; }
        if (!r.Ok)
        {
            ShowErrorDialog(CodeMessage(r.Error?.Code) ?? _lastError ?? r.Error?.Message ?? "Import failed.");
            return null;
        }
        var id = (string?)r.Data?["propId"];
        LastImportedPropId = id;
        if (id is not null) Selection.SelectProp(id);
        if (!App.Settings.PropsPanel) App.SetPropsPanel(true);
        return id;
    }

    /// <summary>prop.attach the prop to a bone of the selected character (or of the character it is already attached to).</summary>
    public bool AttachPropNow(string propId, string boneId, bool keepWorld)
    {
        var prop = Props.Find(propId);
        if (prop is null) return false;
        var characterId = prop.Attachment?.Character.ToString() ?? Selection.CharacterId;
        if (characterId is null) { _toast.Notify(Tr("Select a character first."), _palette.Warn); return false; }
        return App.Execute("prop.attach", new JsonObject { ["propId"] = propId, ["characterId"] = characterId, ["bone"] = boneId, ["keepWorld"] = keepWorld }).Ok;
    }

    public bool DetachPropNow(string propId, bool keepWorld) =>
        App.Execute("prop.detach", new JsonObject { ["propId"] = propId, ["keepWorld"] = keepWorld }).Ok;

    public bool RemovePropNow(string propId)
    {
        var name = Props.Find(propId)?.Name ?? "";
        Selection.SelectProp(null);
        var ok = App.Execute("prop.remove", new JsonObject { ["propId"] = propId }).Ok;
        if (ok) _toast.NotifyAction(string.Format(CultureInfo.InvariantCulture, Tr("Prop removed: {0}"), name), _palette.Warn, Tr("Undo"), DoUndo);
        return ok;
    }

    public bool SetPropTransformNow(string propId, double x, double y, double z, double rx, double ry, double rz)
    {
        Props.MarkEdited(propId);
        return App.Execute("prop.set_transform", new JsonObject { ["propId"] = propId, ["x"] = x, ["y"] = y, ["z"] = z, ["rx"] = rx, ["ry"] = ry, ["rz"] = rz }).Ok;
    }

    // ---- sequences ----------------------------------------------------------------------------------------------------------------------

    /// <summary>Switch to the Sequence tab of the dock (Advanced mode).</summary>
    public void OpenSequenceEditor()
    {
        if (!App.Settings.AdvancedMode) App.SetAdvancedMode(true);
        Timeline.SelectTab(TimelinePanel.TabSequence);
    }

    string NextSequenceName()
    {
        var all = App.Session.Project.Sequences;
        var n = all.Count + 1;
        while (all.Any(s => s.Name == string.Format(CultureInfo.InvariantCulture, Tr("Sequence {0}"), n))) n++;
        return string.Format(CultureInfo.InvariantCulture, Tr("Sequence {0}"), n);
    }

    public void PromptNewSequence()
    {
        if (App.Session.Project.Characters.Count == 0) { _toast.Notify(Tr("Add a character first, then make a sequence for it."), _palette.Warn); return; }
        PromptName("New Sequence", "Create", NextSequenceName(), name => CreateSequenceNow(name));
    }

    public string? CreateSequenceNow(string name)
    {
        var character = SelectedCharacter ?? App.Session.Project.Characters.FirstOrDefault();
        if (character is null) return null;
        var r = App.Execute("sequence.create", new JsonObject { ["characterId"] = character.Id.ToString(), ["name"] = name });
        if (!r.Ok) return null;
        var id = (string?)r.Data?["sequenceId"];
        if (id is not null) App.SetActiveSequence(id);
        return id;
    }

    public void PromptRenameSequence(string id)
    {
        var seq = App.Session.Project.Sequences.Find(s => s.Id.ToString() == id);
        if (seq is null) return;
        PromptName("Rename Sequence", "Rename", seq.Name, name => App.Execute("sequence.update", new JsonObject { ["sequenceId"] = id, ["name"] = name }));
    }

    public bool DeleteSequenceNow(string id)
    {
        var name = App.Session.Project.Sequences.Find(s => s.Id.ToString() == id)?.Name ?? "";
        if (App.SequencePlaying) App.StopSequence();
        var ok = App.Execute("sequence.remove", new JsonObject { ["sequenceId"] = id }).Ok;
        if (ok) _toast.NotifyAction(string.Format(CultureInfo.InvariantCulture, Tr("Sequence deleted: {0}"), name), _palette.Warn, Tr("Undo"), DoUndo);
        return ok;
    }

    public CommandResult AddClipToSequenceNow(string sequenceId, string clipId, bool matchFootPhase)
    {
        var r = ExecuteQuiet("sequence.add_clip", new JsonObject { ["sequenceId"] = sequenceId, ["clipId"] = clipId, ["matchFootPhase"] = matchFootPhase });
        if (!r.Ok) _toast.Notify(FriendlySequenceError(r), _palette.Err);
        else if (r.Warnings.Any(w => w.Code == "no_feet")) _toast.Notify(Tr("This character has no humanoid feet, so the foot phase could not be matched."), _palette.Warn);
        return r;
    }

    /// <summary>Translated text for the error codes the Phase 8 commands add (null for other codes).</summary>
    public string? CodeMessage(string? code) => code switch
    {
        "too_many_blends" => Tr("Too many clips blend at the same time (at most 8). Move a clip so fewer overlap."),
        "invalid_gltf" => Tr("This file is not a valid .glb model."),
        "unsupported_source" => Tr("This kind of model file is not supported."),
        "internal_error" => Tr("Something went wrong inside Blazma. Your project is unchanged. Please try again."),
        _ => null,
    };

    string FriendlySequenceError(CommandResult r) => CodeMessage(r.Error?.Code) ?? r.Error?.Code switch
    {
        "rig_mismatch" => Tr("That clip animates a different character than this sequence."),
        "empty_sequence" => Tr("Add a clip to the sequence first."),
        "too_large" => Tr("The sequence is too big: it has too many clips or is too long to process."),
        _ => FriendlyError(r.Error?.Message ?? ""),
    };

    public bool UpdateSequenceNow(bool? alignRoot = null, bool? alignYaw = null)
    {
        if (App.ActiveSequenceId is not { } id) return false;
        var args = new JsonObject { ["sequenceId"] = id };
        if (alignRoot is { } r) args["alignRoot"] = r;
        if (alignYaw is { } y) args["alignYaw"] = y;
        return App.Execute("sequence.update", args).Ok;
    }

    public bool RemoveSequenceItemNow(string itemId)
    {
        if (App.ActiveSequenceId is not { } id) return false;
        return App.Execute("sequence.remove", new JsonObject { ["sequenceId"] = id, ["itemId"] = itemId }).Ok;
    }

    /// <summary>sequence.bake into a new normal clip, which is selected. Returns the clip id (null on error, which is shown as a toast).</summary>
    public string? BakeSequenceNow(bool footLock)
    {
        if (App.ActiveSequenceId is not { } id) return null;
        if (App.SequencePlaying) App.StopSequence();
        var r = ExecuteQuiet("sequence.bake", new JsonObject { ["sequenceId"] = id, ["footLock"] = footLock });
        if (!r.Ok) { _toast.Notify(FriendlySequenceError(r), _palette.Err); return null; }
        var clipId = (string?)r.Data?["clipId"];
        if (clipId is null) return null;
        SelectClip(clipId);
        var name = App.Session.Project.Clips.Find(c => c.Id.ToString() == clipId)?.Name ?? "";
        _toast.NotifyAction(string.Format(CultureInfo.InvariantCulture, Tr("Baked into the clip “{0}”."), name), _palette.Ok, Tr("Show clip"),
            () => Timeline.SelectTab(TimelinePanel.TabTimeline));
        return clipId;
    }

    /// <summary>Bake the sequence, then open the export dialog for the baked clip.</summary>
    public string? ExportSequence(bool footLock)
    {
        var clipId = BakeSequenceNow(footLock);
        if (clipId is null) return null;
        OpenExportDialog();
        return clipId;
    }

    // ---- selection housekeeping --------------------------------------------------------------------------------------------------------------

    /// <summary>A removed prop (or an undo that removed it) must not stay selected.</summary>
    void ValidatePropSelection()
    {
        if (Selection.PropId is { } id && Props.Find(id) is null) Selection.SelectProp(null);
    }

    void RefreshPhase8Ui()
    {
        ValidatePropSelection();
        PropsPanelNode.Refresh();
        Timeline.Sequence.Sync();
        UpdatePhase8Menu();
    }
}
