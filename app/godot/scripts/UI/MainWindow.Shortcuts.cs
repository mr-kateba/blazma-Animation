using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Keyboard: one dispatcher turns key presses into actions through the <see cref="ShortcutMap"/>; menus and tooltips show the same keys.</summary>
public partial class MainWindow
{
    const int MHelpTutorial = 400, MHelpShortcuts = 401, MHelpWelcome = 402, MKeyboardSettings = 410, MWelcomeMenu = 411;

    static ShortcutMap Keys => ShortcutMap.Instance;

    PopupMenu _mHelp = null!;
    Button _helpButton = null!;
    readonly List<(PopupMenu m, int id, string action)> _menuKeys = new();
    bool _sidePanelsHidden;
    readonly List<(Label label, string action)> _viewKeyLabels = new();

    public ShortcutsDialog ShortcutsDlg { get; private set; } = null!;
    public CheatSheet CheatSheetView { get; private set; } = null!;
    public WelcomeScreen WelcomeView { get; private set; } = null!;
    public TutorialOverlay TutorialView { get; private set; } = null!;
    public PopupMenu HelpMenu => _mHelp;
    public MenuBar MenuBarNode => _menuBar;
    public Button HelpButton => _helpButton;
    public bool ModalOverlayOpen => WelcomeView.Visible || TutorialView.Visible;

    void BindKey(PopupMenu m, int id, string action)
    {
        _menuKeys.Add((m, id, action));
        ApplyMenuKey(m, id, action);
    }

    static void ApplyMenuKey(PopupMenu m, int id, string action)
    {
        var idx = m.GetItemIndex(id);
        if (idx < 0) return;
        var sc = ShortcutMap.Instance.ShortcutFor(action);
        m.SetItemShortcut(idx, sc);
        if (sc is null) return;
        foreach (var ev in sc.Events) ev.AsGodotObject()?.Dispose(); // the menu keeps its own references
        sc.Dispose();
        m.SetItemShortcutDisabled(idx, true); // shown only; the dispatcher below does the work (and skips text fields)
    }

    void RefreshMenuKeys()
    {
        foreach (var (m, id, action) in _menuKeys) ApplyMenuKey(m, id, action);
        Keys.RefreshHints();
        if (Timeline is not null) Timeline.Sync();
        foreach (var (l, a) in _viewKeyLabels) l.Text = Keys.Display(a);
    }

    void BindMenuKeys()
    {
        BindKey(_mProject, MNew, "file.new"); BindKey(_mProject, MOpen, "file.open"); BindKey(_mProject, MSave, "file.save");
        BindKey(_mProject, MSaveAs, "file.saveAs"); BindKey(_mProject, MExport, "file.export"); BindKey(_mProject, MQuit, "file.quit");
        BindKey(_mEdit, MUndo, "edit.undo"); BindKey(_mEdit, MRedo, "edit.redo");
        BindKey(_mAnim, MKey, "key.insert"); BindKey(_mAnim, MKeySel, "key.insertSel"); BindKey(_mAnim, MDelKey, "key.delete");
        BindKey(_mAnim, MAutoKey, "key.autoKey"); BindKey(_mAnim, MPlay, "play.toggle"); BindKey(_mAnim, MLoop, "play.loop");
        BindKey(_mAnim, MStart, "play.start"); BindKey(_mAnim, MEnd, "play.end");
        BindKey(_mAnim, MPrevKey, "play.prevKey"); BindKey(_mAnim, MNextKey, "play.nextKey");
        BindKey(_mAnim, MGenMotion, "ai.generate"); BindKey(_mAnim, MTemplates, "ai.templates");
        BindKey(_mRigging, MMirror, "tool.mirror"); BindKey(_mRigging, MCopy, "pose.copy"); BindKey(_mRigging, MPaste, "pose.paste");
        BindKey(_mRigging, MPinSelected, "tool.pin"); BindKey(_mRigging, MFrame, "view.frameSel");
        BindKey(_mRigging, MResetBone, "pose.resetBone"); BindKey(_mRigging, MResetPose, "pose.resetPose");
        BindKey(_mSettings, MTrail, "view.paths"); BindKey(_mSettings, MOnion, "view.onion");
        BindKey(_mPhysics, MPhysShow, "view.balance");
                BindKey(_mHelp, MHelpTutorial, "help.tutorial"); BindKey(_mHelp, MHelpShortcuts, "help.shortcuts");
        Keys.Changed += RefreshMenuKeys;
    }

    void BuildHelpMenu()
    {
        _mHelp = new PopupMenu { Name = "Help" };
        _menuBar.AddChild(_mHelp);
        _mHelp.AddItem("Tutorial: how Blazma works", MHelpTutorial);
        _mHelp.AddItem("Keyboard Shortcuts", MHelpShortcuts);
        _mHelp.AddItem("Customise Shortcuts…", MKeyboardSettings);
        _mHelp.AddSeparator();
        _mHelp.AddItem("Welcome", MHelpWelcome);
        _mHelp.IdPressed += id =>
        {
            switch ((int)id)
            {
                case MHelpTutorial: StartTutorial(); break;
                case MHelpShortcuts: ToggleCheatSheet(); break;
                case MKeyboardSettings: OpenShortcutSettings(); break;
                case MHelpWelcome: ShowWelcome(); break;
            }
        };
        _mSettings.AddSeparator();
        _mSettings.AddItem("Keyboard Shortcuts…", MKeyboardSettings);
        _mSettings.AddItem("Welcome", MWelcomeMenu);
    }

    Control BuildHelpButton()
    {
        _helpButton = new Button
        {
            Text = "?", ThemeTypeVariation = "Transport", FocusMode = FocusModeEnum.None, SizeFlagsVertical = SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(30, 0), AutoTranslateMode = AutoTranslateModeEnum.Disabled,
        };
        _helpButton.Pressed += StartTutorial;
        Keys.Hint(_helpButton, "Tutorial: how Blazma works", "help.tutorial");
        return _helpButton;
    }

    void BuildOnboardingUi()
    {
        ShortcutsDlg = new ShortcutsDialog();
        AddChild(ShortcutsDlg);
        CheatSheetView = new CheatSheet(this);
        AddChild(CheatSheetView);
        WelcomeView = new WelcomeScreen(this);
        AddChild(WelcomeView);
        TutorialView = new TutorialOverlay(this);
        AddChild(TutorialView);
    }

    public void OpenShortcutSettings() => ShortcutsDlg.Open();
    public void ToggleCheatSheet() { if (CheatSheetView.Visible) CheatSheetView.Close(); else if (!ModalOverlayOpen) CheatSheetView.Open(); }
    public void StartTutorial() { CheatSheetView.Close(); WelcomeView.Hide(); TutorialView.Start(); }
    public void ShowWelcome() { CheatSheetView.Close(); TutorialView.Close(false); WelcomeView.Open(); }

    // ---- dispatcher ---------------------------------------------------------------------------------------------------------------------

    public enum KeyPhase { Input, Shortcut, Unhandled }

    static bool IsNavKey(Key k) => k is Key.Space or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.Tab;

    bool Typing(Control? owner) => owner is LineEdit or TextEdit or SpinBox;

    /// <summary>Run the action a key press stands for. Returns true when the key was used.</summary>
    public bool DispatchKey(InputEventKey k, KeyPhase? phase = null)
    {
        if (!k.Pressed || k.Echo || Keys.Capturing) return false;
        var key = k.Keycode != Key.None ? k.Keycode : k.PhysicalKeycode;
        if (key is Key.Shift or Key.Ctrl or Key.Alt or Key.Meta) return false;
        var nav = IsNavKey(key);
        var mod = k.IsCommandOrControlPressed() || k.AltPressed || k.MetaPressed || key >= Key.F1 && key <= Key.F12;
        var mine = !nav ? (mod ? KeyPhase.Shortcut : KeyPhase.Unhandled) : KeyPhase.Input;
        if (phase is { } p && p != mine) return false;
        var owner = GetViewport().GuiGetFocusOwner();
        var typing = Typing(owner);
        var inViewport = owner is ViewportView;
        if (Keys.Find(k, inViewport) is not { } action) return false;
        if (typing && !(action.AllowInText && mod)) return false;
        if (ModalOverlayOpen || (CheatSheetView.Visible && action.Id != "help.shortcuts")) return false;
        // The dope sheet and the graph keep their own keys for copy/paste/select-all/delete and framing.
        if (owner is DopeSheetView && action.Id is "pose.copy" or "pose.paste" or "pose.pasteMirror" or "edit.selectAll" or "edit.delete") return false;
        if (owner is GraphCanvas && action.Id is "play.start" or "view.frameSel") return false;
        return Run(action.Id);
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey k && DispatchKey(k, KeyPhase.Input)) GetViewport().SetInputAsHandled();
    }

    public override void _ShortcutInput(InputEvent e)
    {
        if (e is InputEventKey k && DispatchKey(k, KeyPhase.Shortcut)) GetViewport().SetInputAsHandled();
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is InputEventKey k && DispatchKey(k, KeyPhase.Unhandled)) GetViewport().SetInputAsHandled();
    }

    /// <summary>Perform an action by id (keys, menus and tests all come here). Returns false when it does not apply right now.</summary>
    public bool Run(string id)
    {
        switch (id)
        {
            case "file.new": RequestNew(); return true;
            case "file.open": RequestOpen(); return true;
            case "file.save": RequestSave(); return true;
            case "file.saveAs": PromptSaveAs(null); return true;
            case "file.export": OpenExportDialog(); return true;
            case "file.quit": RequestQuit(); return true;
            case "edit.undo": DoUndo(); return true;
            case "edit.redo": DoRedo(); return true;
            case "edit.delete":
                if (Selection.PropId is { } propId) { RemovePropNow(propId); return true; }
                if (SelectedClipId is { } clipId) { ConfirmDeleteClip(clipId); return true; }
                return false;
            case "edit.duplicate": return DuplicateClip();
            case "edit.selectAll": Timeline.Dope.SelectAll(); return true;
            case "play.toggle": App.TogglePlay(); return true;
            case "play.next": App.StepFrames(1); return true;
            case "play.prev": App.StepFrames(-1); return true;
            case "play.next10": App.StepFrames(10); return true;
            case "play.prev10": App.StepFrames(-10); return true;
            case "play.nextKey": App.GoToNextKey(); return true;
            case "play.prevKey": App.GoToPreviousKey(); return true;
            case "play.start": App.GoToStart(); return true;
            case "play.end": App.GoToEnd(); return true;
            case "play.loop": if (App.ActiveClip is { } c) App.SetLoop(!c.Loop.Enabled); return true;
            case "key.insert": KeyAll(); return true;
            case "key.insertSel": KeySelected(); return true;
            case "key.delete": DeleteKeyHere(); return true;
            case "key.autoKey": App.SetAutoKey(!App.AutoKey); return true;
            case "tool.select": Selection.SetTool(StageTool.Select); return true;
            case "tool.move": Selection.SetTool(StageTool.Move); return true;
            case "tool.rotate": Selection.SetTool(StageTool.Rotate); return true;
            case "tool.mirror": MirrorPose(); return true;
            case "tool.pin": TogglePinSelected(); return true;
            case "pose.resetBone":
                if (SelectedCharacter is { } rc && Selection.BoneId is { } rb) return App.Execute("pose.reset", new JsonObject { ["characterId"] = rc.Id.ToString(), ["bone"] = rb }).Ok;
                return false;
            case "pose.resetPose": ConfirmResetPose(); return true;
            case "pose.copy": CopyPose(); return true;
            case "pose.paste": PastePose(); return true;
            case "pose.pasteMirror": if (PastePose()) MirrorPose(); return true;
            case "view.frameSel": Stage.FrameFocus(); return true;
            case "view.frameAll": Stage.FrameAll(); return true;
            case "view.front": Stage.SetView(0); return true;
            case "view.side": Stage.SetView(1); return true;
            case "view.top": Stage.SetView(2); return true;
            case "view.ortho": Stage.ToggleOrtho(); return true;
            case "view.panels": _sidePanelsHidden = !_sidePanelsHidden; ApplySideToggles(); return true;
            case "view.inspector": Inspector.Visible = !Inspector.Visible; return true;
            case "view.mode": App.SetAdvancedMode(!App.Settings.AdvancedMode); return true;
            case "view.fullscreen":
                var win = GetWindow();
                win.Mode = win.Mode == Window.ModeEnum.Fullscreen ? Window.ModeEnum.Windowed : Window.ModeEnum.Fullscreen;
                return true;
            case "view.balance": App.SetShowPhysics(!App.Settings.ShowPhysics); return true;
            case "view.paths": App.SetShowTrail(!App.Settings.ShowTrail); return true;
            case "view.onion": App.SetShowOnion(!App.Settings.ShowOnion); return true;
            case "ai.generate": OpenGenerateDialog(); return true;
            case "ai.templates": OpenTemplatesDialog(); return true;
            case "help.shortcuts": ToggleCheatSheet(); return true;
            case "help.tutorial": StartTutorial(); return true;
            case "help.welcome": ShowWelcome(); return true;
        }
        return false;
    }

    /// <summary>T hides the left and right docks to give the stage the whole window; settings changes keep that choice.</summary>
    void ApplySideToggles()
    {
        if (_sidePanelsHidden) { Dock.Visible = false; _rightDock.Visible = false; }
        else { Dock.Visible = App.Settings.AdvancedMode; SyncPhysicsUi(); }
    }

    public bool SidePanelsHidden => _sidePanelsHidden;

    public bool DuplicateClip()
    {
        if (SelectedClip is not { } clip) return false;
        Inspector.Flush();
        var cmd = new DuplicateClipCommand(clip.Id);
        var r = App.Execute(cmd);
        if (r.Ok) SelectClip(cmd.NewId.ToString());
        return r.Ok;
    }
}

/// <summary>Edit > Duplicate: an exact copy of a clip next to the original (undoable).</summary>
public sealed class DuplicateClipCommand(ObjectId source) : ICommand
{
    public ObjectId NewId { get; } = ObjectId.New();
    public string Id => "clip.duplicate";
    public CommandEffect Effect => CommandEffect.Mutating;
    public string Label => "Duplicate clip";

    public CommandOutcome Execute(Project p)
    {
        var clip = p.GetClip(source);
        var copy = clip.DeepClone();
        copy.Id = NewId;
        copy.Name = clip.Name + " copy";
        copy.IsDraft = false;
        p.Clips.Add(copy);
        return new(new[] { new Change(Change.ClipAdded, NewId.ToString()) }, new JsonObject { ["clipId"] = NewId.ToString() });
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        p.Clips.RemoveAll(c => c.Id == NewId);
        return new[] { new Change(Change.ClipRemoved, NewId.ToString()) };
    }
}
