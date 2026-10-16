using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 1 application shell (mockup B "Focus Canvas"): top bar with menus, a 3D stage with a floating
/// inspector, a clip strip dock, optional Advanced panels (project outline, history) and a status bar.
/// This class only builds and refreshes views. Every edit goes through AppController commands.
/// </summary>
public partial class MainWindow : Control
{
    // menu ids
    const int MNew = 1, MOpen = 2, MSave = 3, MSaveAs = 4, MQuit = 5, MClearRecent = 99, MRecentBase = 100;
    const int MUndo = 10, MRedo = 11;
    const int MNewClip = 20, MRenameClip = 21, MDeleteClip = 22;

    static AppController App => AppController.Instance;

    Palette _palette = Palette.Dark;
    public Palette Palette => _palette;

    // layout nodes
    Panel _backdrop = null!;
    PanelContainer _brandMark = null!;
    MenuBar _menuBar = null!;
    PopupMenu _mProject = null!, _mEdit = null!, _mAnim = null!, _mSettings = null!;
    PopupMenu _mRecent = null!, _mLang = null!, _mTheme = null!, _mAuto = null!, _mMode = null!;
    Segmented _segMode = null!, _segLang = null!, _segTheme = null!;
    PanelContainer _chip = null!;
    Dot _chipDot = null!;
    Label _chipLabel = null!;
    HSplitContainer _splitOuter = null!, _splitInner = null!;
    VSplitContainer _splitDock = null!;
    Control _center = null!;
    Panel _frame = null!;
    Label _hint = null!;
    Dot _statusDot = null!;
    Label _statusText = null!, _autosaveText = null!, _pathText = null!, _fpsText = null!;
    Toast _toast = null!;
    Godot.Timer _layoutTimer = null!;
    double _fpsTimer;

    public ViewportView Stage { get; private set; } = null!;
    public AxisGizmo Gizmo { get; private set; } = null!;
    public InspectorPanel Inspector { get; private set; } = null!;
    public ClipStrip Strip { get; private set; } = null!;
    public ProjectTreePanel OutlinePanel { get; private set; } = null!;
    public HistoryPanel HistoryPanel { get; private set; } = null!;
    public Toast ToastNode => _toast;
    public RecoveryDialog? RecoveryDialogNode { get; private set; }
    public PanelContainer ChipNode => _chip;
    public ConfirmationDialog UnsavedDialog => _unsavedDialog;
    public ConfirmationDialog DeleteDialog => _deleteDialog;
    public ConfirmationDialog NameDialog => _nameDialog;

    // dialogs
    FileDialog _fileDialog = null!;
    ConfirmationDialog _unsavedDialog = null!, _deleteDialog = null!, _nameDialog = null!;
    AcceptDialog _errorDialog = null!;
    LineEdit _nameEdit = null!;
    Action? _pendingAfterGuard, _afterSave, _nameAccepted;
    string? _deleteId;
    bool _fileDialogForSave, _suppressToast;
    string? _lastError;
    public string? LastError => _lastError;

    // selection
    public string? SelectedClipId { get; private set; }
    List<string> _lastClipIds = new();
    public Clip? SelectedClip => SelectedClipId is null ? null : App.Session.Project.Clips.Find(c => c.Id.ToString() == SelectedClipId);

    // ---- lifecycle ---------------------------------------------------------------------------

    public override void _Ready()
    {
        W.Fill(this);
        BuildUi();
        BuildDialogs();

        App.SessionChanged += OnSessionChanged;
        App.SettingsChanged += OnSettingsChanged;
        App.ErrorRaised += OnError;

        RestoreWindowAndLayout();
        OnSettingsChanged();
        SelectFirstIfNone();
        RefreshAll();

        CallDeferred(nameof(StartupChecks));
    }

    public override void _ExitTree()
    {
        App.SessionChanged -= OnSessionChanged;
        App.SettingsChanged -= OnSettingsChanged;
        App.ErrorRaised -= OnError;
    }

    void StartupChecks()
    {
        ShowRecoveryIfAny();
        var selfTest = SelfTest.TryCreate(this);
        if (selfTest is not null) AddChild(selfTest);
    }

    public override void _Process(double delta)
    {
        _fpsTimer += delta;
        if (_fpsTimer >= 0.5)
        {
            _fpsTimer = 0;
            _fpsText.Text = $"{Engine.GetFramesPerSecond():0} FPS";
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) RequestQuit();
        else if (what == NotificationTranslationChanged && IsNodeReady()) { UpdateMenus(); UpdateStatus(); UpdateChip(); }
    }

    /// <summary>Top-level menu popup by index (Project, Edit, Animation, Settings); used by screenshot tests.</summary>
    public PopupMenu MenuPopup(int index) => index switch { 0 => _mProject, 1 => _mEdit, 2 => _mAnim, _ => _mSettings };

    // ---- UI construction ---------------------------------------------------------------------

    void BuildUi()
    {
        _backdrop = new Panel { ThemeTypeVariation = "Backdrop", MouseFilter = MouseFilterEnum.Ignore };
        W.Fill(_backdrop);
        AddChild(_backdrop);

        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 0);
        W.Fill(page);
        AddChild(page);

        page.AddChild(BuildTopBar());

        // Body ----------------------------------------------------------------------------------
        Stage = new ViewportView { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        Inspector = new InspectorPanel(this);
        OutlinePanel = new ProjectTreePanel(this);
        HistoryPanel = new HistoryPanel(this);
        Strip = new ClipStrip(this);
        Gizmo = new AxisGizmo();

        _center = new Control
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(420, 220), ClipContents = true, LayoutDirection = LayoutDirectionEnum.Ltr,
        };
        W.Fill(Stage);
        _center.AddChild(Stage);
        _frame = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        W.Fill(_frame);
        _center.AddChild(_frame);

        Gizmo.SetAnchorsPreset(LayoutPreset.BottomLeft);
        Gizmo.OffsetLeft = 10; Gizmo.OffsetBottom = -8; Gizmo.OffsetTop = -84; Gizmo.OffsetRight = 86;
        _center.AddChild(Gizmo);

        _hint = W.Label("Orbit: middle or right mouse drag  ·  Pan: Shift + drag  ·  Zoom: wheel  ·  Reset view: F", "MonoFaint");
        _hint.MouseFilter = MouseFilterEnum.Ignore;
        _hint.SetAnchorsPreset(LayoutPreset.BottomRight);
        _hint.GrowHorizontal = GrowDirection.Begin;
        _hint.GrowVertical = GrowDirection.Begin;
        _hint.OffsetRight = -14; _hint.OffsetBottom = -10;
        _hint.LayoutDirection = LayoutDirectionEnum.Locale;
        _center.AddChild(_hint);

        _center.AddChild(Inspector);

        _splitInner = new HSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        _splitInner.AddChild(_center);
        HistoryPanel.SizeFlagsHorizontal = SizeFlags.Fill;
        _splitInner.AddChild(HistoryPanel);

        _splitOuter = new HSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        OutlinePanel.SizeFlagsHorizontal = SizeFlags.Fill;
        _splitOuter.AddChild(OutlinePanel);
        _splitOuter.AddChild(_splitInner);

        _splitDock = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        Strip.SizeFlagsVertical = SizeFlags.Fill;
        _splitDock.AddChild(_splitOuter);
        _splitDock.AddChild(Strip);

        foreach (var s in new SplitContainer[] { _splitOuter, _splitInner, _splitDock })
            s.Dragged += _ => ScheduleLayoutSave();

        page.AddChild(W.Margin(_splitDock, 10, 10, 10, 8));
        _splitDock.GetParent<Control>().SizeFlagsVertical = SizeFlags.ExpandFill;

        page.AddChild(BuildStatusBar());

        _toast = new Toast();
        AddChild(_toast);

        _layoutTimer = new Godot.Timer { OneShot = true, WaitTime = 0.6 };
        _layoutTimer.Timeout += () => { SaveLayoutValues(); App.Settings.Save(); };
        AddChild(_layoutTimer);
    }

    Control BuildTopBar()
    {
        var bar = new PanelContainer { ThemeTypeVariation = "TopBar", CustomMinimumSize = new Vector2(0, 40) };
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        bar.AddChild(row);

        _brandMark = new PanelContainer { CustomMinimumSize = new Vector2(16, 16), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var brand = new HBoxContainer();
        brand.AddThemeConstantOverride("separation", 8);
        brand.AddChild(_brandMark);
        var name = W.Label("Blazma Animation", "Heading", false);
        brand.AddChild(name);
        row.AddChild(W.Margin(brand, 0, 0, 12, 0));

        BuildMenus();
        row.AddChild(_menuBar);
        row.AddChild(W.Spacer());

        _segMode = new Segmented("Simple", "Advanced");
        _segMode.Selected += i => App.SetAdvancedMode(i == 1);
        _segLang = new Segmented("EN", "عربي");
        _segLang.Selected += i => App.SetLanguage(i == 1 ? "ar" : "en");
        foreach (var i in new[] { 0, 1 }) _segLang[i].AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        _segTheme = new Segmented("Dark", "Light");
        _segTheme.Selected += i => App.SetTheme(i == 1 ? "light" : "dark");
        row.AddChild(_segMode);
        row.AddChild(_segLang);
        row.AddChild(_segTheme);

        _chip = new PanelContainer { ThemeTypeVariation = "Chip", SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var chipRow = new HBoxContainer();
        chipRow.AddThemeConstantOverride("separation", 7);
        _chipDot = new Dot();
        _chipLabel = W.Label("", null, false);
        _chipLabel.TextDirection = Control.TextDirection.Auto;
        chipRow.AddChild(_chipDot);
        chipRow.AddChild(_chipLabel);
        _chip.AddChild(chipRow);
        row.AddChild(_chip);
        return bar;
    }

    Control BuildStatusBar()
    {
        var bar = new PanelContainer { ThemeTypeVariation = "StatusBar" };
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 14);
        bar.AddChild(row);

        var left = new HBoxContainer();
        left.AddThemeConstantOverride("separation", 7);
        _statusDot = new Dot();
        _statusText = W.Label("", "Dim");
        left.AddChild(_statusDot);
        left.AddChild(_statusText);
        row.AddChild(left);

        _autosaveText = W.Label("", "Faint", false);
        row.AddChild(_autosaveText);

        _pathText = W.Label("", "MonoFaint", false);
        _pathText.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _pathText.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _pathText.TextDirection = Control.TextDirection.Ltr;
        _pathText.ClipText = true;
        row.AddChild(_pathText);

        _fpsText = W.Label("", "MonoFaint", false);
        _fpsText.TextDirection = Control.TextDirection.Ltr;
        row.AddChild(_fpsText);
        return bar;
    }

    // ---- menus -------------------------------------------------------------------------------

    static Shortcut Combo(Godot.Key key, bool shift = false, Godot.Key? alt = null)
    {
        var sc = new Shortcut();
        void Add(Godot.Key k, bool sh) =>
            sc.Events.Add(new InputEventKey { Keycode = k, CtrlPressed = true, ShiftPressed = sh, CommandOrControlAutoremap = true });
        Add(key, shift);
        if (alt is { } a) Add(a, false);
        return sc;
    }

    void ShortcutOf(PopupMenu m, int id, Shortcut sc)
    {
        var idx = m.GetItemIndex(id);
        m.SetItemShortcut(idx, sc);
        m.SetItemShortcutDisabled(idx, true); // shown only; MainWindow dispatches keys itself (skips text fields)
    }

    void BuildMenus()
    {
        _menuBar = new MenuBar { Flat = true, PreferGlobalMenu = false, SizeFlagsVertical = SizeFlags.ShrinkCenter };

        _mProject = new PopupMenu { Name = "Project" };
        _mEdit = new PopupMenu { Name = "Edit" };
        _mAnim = new PopupMenu { Name = "Animation" };
        _mSettings = new PopupMenu { Name = "Settings" };
        foreach (var m in new[] { _mProject, _mEdit, _mAnim, _mSettings }) _menuBar.AddChild(m);

        _mProject.AddItem("New", MNew);
        _mProject.AddItem("Open…", MOpen);
        _mProject.AddItem("Save", MSave);
        _mProject.AddItem("Save As…", MSaveAs);
        _mProject.AddSeparator();
        _mRecent = new PopupMenu { Name = "OpenRecent", AutoTranslateMode = AutoTranslateModeEnum.Disabled };
        _mProject.AddChild(_mRecent);
        _mProject.AddSubmenuNodeItem("Open Recent", _mRecent);
        _mProject.AddSeparator();
        _mProject.AddItem("Quit", MQuit);
        ShortcutOf(_mProject, MNew, Combo(Godot.Key.N));
        ShortcutOf(_mProject, MOpen, Combo(Godot.Key.O));
        ShortcutOf(_mProject, MSave, Combo(Godot.Key.S));
        ShortcutOf(_mProject, MSaveAs, Combo(Godot.Key.S, true));
        _mProject.AboutToPopup += RebuildRecent;
        _mProject.IdPressed += id => OnMenu((int)id);
        _mRecent.IdPressed += id => OnMenu((int)id);

        _mEdit.AddItem("Undo", MUndo);
        _mEdit.AddItem("Redo", MRedo);
        ShortcutOf(_mEdit, MUndo, Combo(Godot.Key.Z));
        ShortcutOf(_mEdit, MRedo, Combo(Godot.Key.Z, true, Godot.Key.Y));
        _mEdit.AboutToPopup += UpdateMenus;
        _mEdit.IdPressed += id => OnMenu((int)id);

        _mAnim.AddItem("New Clip…", MNewClip);
        _mAnim.AddItem("Rename Clip…", MRenameClip);
        _mAnim.AddItem("Delete Clip", MDeleteClip);
        _mAnim.AboutToPopup += UpdateMenus;
        _mAnim.IdPressed += id => OnMenu((int)id);

        _mLang = new PopupMenu { Name = "Language", AutoTranslateMode = AutoTranslateModeEnum.Disabled };
        _mLang.AddRadioCheckItem("English", 0);
        _mLang.AddRadioCheckItem("العربية", 1);
        _mLang.IdPressed += id => App.SetLanguage(id == 1 ? "ar" : "en");
        _mTheme = new PopupMenu { Name = "Theme" };
        _mTheme.AddRadioCheckItem("Dark", 0);
        _mTheme.AddRadioCheckItem("Light", 1);
        _mTheme.IdPressed += id => App.SetTheme(id == 1 ? "light" : "dark");
        _mAuto = new PopupMenu { Name = "Autosave" };
        _mAuto.AddRadioCheckItem("Off", 0);
        _mAuto.AddRadioCheckItem("Every minute", 1);
        _mAuto.AddRadioCheckItem("Every 3 minutes", 3);
        _mAuto.AddRadioCheckItem("Every 5 minutes", 5);
        _mAuto.AddRadioCheckItem("Every 10 minutes", 10);
        _mAuto.IdPressed += id => App.SetAutosaveMinutes((int)id);
        _mMode = new PopupMenu { Name = "Mode" };
        _mMode.AddRadioCheckItem("Simple", 0);
        _mMode.AddRadioCheckItem("Advanced", 1);
        _mMode.IdPressed += id => App.SetAdvancedMode(id == 1);
        foreach (var sub in new[] { _mLang, _mTheme, _mAuto, _mMode }) _mSettings.AddChild(sub);
        _mSettings.AddSubmenuNodeItem("Language", _mLang);
        _mSettings.AddSubmenuNodeItem("Theme", _mTheme);
        _mSettings.AddSubmenuNodeItem("Autosave", _mAuto);
        _mSettings.AddSubmenuNodeItem("Mode", _mMode);
    }

    void OnMenu(int id)
    {
        switch (id)
        {
            case MNew: RequestNew(); break;
            case MOpen: RequestOpen(); break;
            case MSave: RequestSave(); break;
            case MSaveAs: PromptSaveAs(null); break;
            case MQuit: RequestQuit(); break;
            case MUndo: DoUndo(); break;
            case MRedo: DoRedo(); break;
            case MNewClip: PromptNewClip(); break;
            case MRenameClip: if (SelectedClipId is { } r) PromptRenameClip(r); break;
            case MDeleteClip: if (SelectedClipId is { } d) ConfirmDeleteClip(d); break;
            case MClearRecent: App.Settings.RecentFiles.Clear(); App.Settings.Save(); break;
            case >= MRecentBase:
                var i = id - MRecentBase;
                if (i < App.Settings.RecentFiles.Count)
                {
                    var path = App.Settings.RecentFiles[i];
                    GuardUnsaved(() => OpenPath(path));
                }
                break;
        }
    }

    void RebuildRecent()
    {
        _mRecent.Clear();
        var recent = App.Settings.RecentFiles;
        if (recent.Count == 0)
        {
            _mRecent.AddItem(Tr("No recent projects"), -1);
            _mRecent.SetItemDisabled(0, true);
            return;
        }
        for (var i = 0; i < recent.Count; i++)
        {
            _mRecent.AddItem(System.IO.Path.GetFileName(recent[i]), MRecentBase + i);
            _mRecent.SetItemTooltip(_mRecent.ItemCount - 1, recent[i]);
        }
        _mRecent.AddSeparator();
        _mRecent.AddItem(Tr("Clear"), MClearRecent);
    }

    void UpdateMenus()
    {
        var s = App.Session;
        var undo = s.UndoHistory;
        var redo = s.RedoHistory;
        SetItemText(_mEdit, MUndo, undo.Count > 0 ? string.Format(Tr("Undo: {0}"), Tr(undo[^1].Label)) : Tr("Undo"), !s.CanUndo);
        SetItemText(_mEdit, MRedo, redo.Count > 0 ? string.Format(Tr("Redo: {0}"), Tr(redo[^1].Label)) : Tr("Redo"), !s.CanRedo);
        var has = SelectedClipId is not null;
        _mAnim.SetItemDisabled(_mAnim.GetItemIndex(MRenameClip), !has);
        _mAnim.SetItemDisabled(_mAnim.GetItemIndex(MDeleteClip), !has);

        _menuBar.SetMenuTitle(0, Tr("Project"));
        _menuBar.SetMenuTitle(1, Tr("Edit"));
        _menuBar.SetMenuTitle(2, Tr("Animation"));
        _menuBar.SetMenuTitle(3, Tr("Settings"));
    }

    static void SetItemText(PopupMenu m, int id, string text, bool disabled)
    {
        var idx = m.GetItemIndex(id);
        m.SetItemText(idx, text);
        m.SetItemDisabled(idx, disabled);
    }

    static void CheckOnly(PopupMenu m, int id)
    {
        for (var i = 0; i < m.ItemCount; i++) m.SetItemChecked(i, m.GetItemId(i) == id);
    }

    // ---- dialogs -----------------------------------------------------------------------------

    void BuildDialogs()
    {
        _fileDialog = new FileDialog
        {
            UseNativeDialog = true,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.blazanim ; Blazma Animation Project" },
        };
        _fileDialog.FileSelected += OnFileSelected;
        _fileDialog.Canceled += () => _afterSave = null;
        AddChild(_fileDialog);

        _unsavedDialog = new ConfirmationDialog { Title = "Unsaved changes", OkButtonText = "Save", CancelButtonText = "Cancel", Exclusive = true };
        _unsavedDialog.AddButton("Don't Save", false, "dont_save");
        _unsavedDialog.Confirmed += () => { var a = _pendingAfterGuard; _pendingAfterGuard = null; if (a is not null) SaveThen(a); };
        _unsavedDialog.CustomAction += action =>
        {
            if (action != "dont_save") return;
            _unsavedDialog.Hide();
            var a = _pendingAfterGuard; _pendingAfterGuard = null; a?.Invoke();
        };
        _unsavedDialog.Canceled += () => _pendingAfterGuard = null;
        AddChild(_unsavedDialog);

        _deleteDialog = new ConfirmationDialog { Title = "Delete clip", OkButtonText = "Delete", CancelButtonText = "Cancel", Exclusive = true };
        _deleteDialog.AutoTranslateMode = AutoTranslateModeEnum.Disabled; // text is built from user content
        _deleteDialog.Confirmed += () => { if (_deleteId is { } id) DeleteClipNow(id); _deleteId = null; };
        AddChild(_deleteDialog);

        _nameDialog = new ConfirmationDialog { Exclusive = true };
        _nameEdit = new LineEdit { CustomMinimumSize = new Vector2(320, 0), AutoTranslateMode = AutoTranslateModeEnum.Disabled, SelectAllOnFocus = true };
        _nameDialog.AddChild(_nameEdit);
        _nameDialog.RegisterTextEnter(_nameEdit);
        _nameDialog.Confirmed += () => { var a = _nameAccepted; _nameAccepted = null; a?.Invoke(); };
        _nameDialog.Canceled += () => _nameAccepted = null;
        AddChild(_nameDialog);

        _errorDialog = new AcceptDialog { Title = "Something went wrong", OkButtonText = "OK", Exclusive = true };
        _errorDialog.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        AddChild(_errorDialog);
    }

    /// <summary>Text is translated here, then the dialog's own auto-translation is off so user content is never "translated".</summary>
    void ShowDialogText(AcceptDialog d, string title, string text, string ok, string? cancel = null)
    {
        d.Title = Tr(title);
        d.DialogText = text;
        d.OkButtonText = Tr(ok);
        if (d is ConfirmationDialog c && cancel is not null) c.CancelButtonText = Tr(cancel);
    }

    void GuardUnsaved(Action proceed)
    {
        if (!App.IsDirty) { proceed(); return; }
        _pendingAfterGuard = proceed;
        ShowDialogText(_unsavedDialog, "Unsaved changes",
            string.Format(Tr("Do you want to save the changes to “{0}”?"), App.Session.Project.Name), "Save", "Cancel");
        _unsavedDialog.GetOkButton().Text = Tr("Save");
        _unsavedDialog.PopupCentered(new Vector2I(460, 0));
    }

    void SaveThen(Action proceed)
    {
        if (App.FilePath is null) { PromptSaveAs(proceed); return; }
        if (App.Save()) proceed();
    }

    public void PromptSaveAs(Action? then)
    {
        _afterSave = then;
        _fileDialogForSave = true;
        _fileDialog.FileMode = FileDialog.FileModeEnum.SaveFile;
        _fileDialog.Title = Tr("Save As…");
        _fileDialog.CurrentFile = SafeFileName(App.Session.Project.Name) + ProjectFile.Extension;
        _fileDialog.PopupCenteredRatio(0.6f);
    }

    void PromptOpenFile()
    {
        _fileDialogForSave = false;
        _fileDialog.FileMode = FileDialog.FileModeEnum.OpenFile;
        _fileDialog.Title = Tr("Open…");
        _fileDialog.PopupCenteredRatio(0.6f);
    }

    void OnFileSelected(string path)
    {
        if (_fileDialogForSave)
        {
            if (SaveToPath(path)) { var a = _afterSave; _afterSave = null; a?.Invoke(); }
        }
        else OpenPath(path);
    }

    static string SafeFileName(string name)
    {
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "Untitled" : s;
    }

    void PromptName(string title, string ok, string initial, Action<string> accept)
    {
        _nameDialog.Title = Tr(title);
        _nameDialog.OkButtonText = Tr(ok);
        _nameEdit.Text = initial;
        _nameAccepted = () => accept(_nameEdit.Text.Trim());
        _nameDialog.PopupCentered(new Vector2I(380, 0));
        _nameEdit.GrabFocus();
        _nameEdit.SelectAll();
    }

    public void ShowErrorDialog(string message)
    {
        ShowDialogText(_errorDialog, "Something went wrong", message, "OK");
        _errorDialog.PopupCentered(new Vector2I(440, 0));
    }

    // ---- file / project actions (shared by menus, shortcuts and SelfTest) -------------------------

    public void RequestNew() => GuardUnsaved(NewProjectNow);
    public void RequestOpen() => GuardUnsaved(PromptOpenFile);
    public void RequestSave() { if (App.FilePath is null) PromptSaveAs(null); else App.Save(); }
    public void RequestQuit() => GuardUnsaved(QuitNow);

    public void NewProjectNow()
    {
        App.NewProject();
        SelectClip(null);
    }

    public bool OpenPath(string path)
    {
        _suppressToast = true;
        _lastError = null;
        bool ok;
        try { ok = App.Open(path); }
        finally { _suppressToast = false; }
        if (!ok && _lastError is not null) ShowErrorDialog(_lastError);
        return ok;
    }

    public bool SaveToPath(string path) => App.SaveAs(path);

    void QuitNow()
    {
        SaveLayoutValues();
        App.ShutdownCleanly();
        GetTree().Quit();
    }

    // ---- clip actions -----------------------------------------------------------------------

    string NextClipName()
    {
        var clips = App.Session.Project.Clips;
        var n = clips.Count + 1;
        while (clips.Any(c => c.Name == $"Clip {n}")) n++;
        return $"Clip {n}";
    }

    public void CreateClip() => CreateClipNamed(NextClipName());

    public void PromptNewClip() =>
        PromptName("New Clip", "Create", NextClipName(), name => CreateClipNamed(name));

    public string? CreateClipNamed(string name)
    {
        var r = App.Execute("clip.create", new JsonObject { ["name"] = name });
        if (!r.Ok) return null;
        var id = (string?)r.Data?["clipId"];
        if (id is not null) SelectClip(id);
        return id;
    }

    public void PromptRenameClip(string id)
    {
        var clip = App.Session.Project.Clips.Find(c => c.Id.ToString() == id);
        if (clip is null) return;
        PromptName("Rename Clip", "Rename", clip.Name, name => RenameClipNow(id, name));
    }

    public bool RenameClipNow(string id, string name) =>
        App.Execute("clip.rename", new JsonObject { ["clipId"] = id, ["name"] = name }).Ok;

    public bool SetClipDurationNow(string id, long frames) =>
        App.Execute("clip.set_duration", new JsonObject { ["clipId"] = id, ["frames"] = frames }).Ok;

    public void ConfirmDeleteClip(string id)
    {
        var clip = App.Session.Project.Clips.Find(c => c.Id.ToString() == id);
        if (clip is null) return;
        _deleteId = id;
        ShowDialogText(_deleteDialog, "Delete clip",
            string.Format(Tr("Delete the clip “{0}”? You can bring it back with Edit > Undo."), clip.Name), "Delete", "Cancel");
        _deleteDialog.PopupCentered(new Vector2I(440, 0));
    }

    public bool DeleteClipNow(string id) => App.Execute("clip.delete", new JsonObject { ["clipId"] = id }).Ok;

    public void DoUndo() => App.Undo();
    public void DoRedo() => App.Redo();

    // ---- selection ---------------------------------------------------------------------------

    public void SelectClip(string? id)
    {
        if (id == SelectedClipId) return;
        Inspector.Flush(); // commit text typed for the previous clip first
        SelectedClipId = id;
        Strip.UpdateSelection();
        OutlinePanel.UpdateSelection();
        Inspector.Refresh(true);
        UpdateMenus();
    }

    void SelectFirstIfNone()
    {
        if (SelectedClipId is null && App.Session.Project.Clips.Count > 0)
            SelectedClipId = App.Session.Project.Clips[0].Id.ToString();
    }

    void ValidateSelection(bool replaced)
    {
        var clips = App.Session.Project.Clips;
        var ids = clips.Select(c => c.Id.ToString()).ToList();
        if (replaced)
        {
            SelectedClipId = ids.Count > 0 ? ids[0] : null;
        }
        else if (SelectedClipId is not null && !ids.Contains(SelectedClipId))
        {
            var oldIndex = _lastClipIds.IndexOf(SelectedClipId);
            SelectedClipId = ids.Count == 0 ? null : ids[Math.Clamp(oldIndex, 0, ids.Count - 1)];
        }
        _lastClipIds = ids;
    }

    // ---- refreshing --------------------------------------------------------------------------

    void OnSessionChanged(SessionChangedArgs args)
    {
        var onlySaveState = args.Changes.All(c => c.Kind == Change.SaveStateChanged);
        if (onlySaveState) { UpdateStatus(); UpdateChip(); UpdateMenus(); return; }
        ValidateSelection(args.Changes.Any(c => c.Kind == Change.ProjectReplaced));
        RefreshAll();
    }

    void RefreshAll()
    {
        var advanced = App.Settings.AdvancedMode;
        if (_lastClipIds.Count == 0) _lastClipIds = App.Session.Project.Clips.Select(c => c.Id.ToString()).ToList();
        Strip.Rebuild();
        Inspector.Refresh();
        if (advanced) { OutlinePanel.Rebuild(); HistoryPanel.Rebuild(); }
        UpdateStatus();
        UpdateChip();
        UpdateMenus();
    }

    void UpdateChip()
    {
        var p = App.Session.Project;
        var shown = p.Name.Length > 28 ? p.Name[..27] + "…" : p.Name;
        _chipLabel.Text = $"{shown} · {string.Format(Tr("{0} fps"), Fmt.Rate(p.Settings.DefaultFrameRate))}";
        _chipDot.SetColor(App.IsDirty ? _palette.Warn : _palette.Ok);
        _chip.TooltipText = App.IsDirty ? Tr("Unsaved changes") : Tr("All changes saved");
        GetWindow().Title = $"{p.Name}{(App.IsDirty ? " •" : "")} — Blazma Animation";
    }

    void UpdateStatus()
    {
        var dirty = App.IsDirty;
        _statusDot.SetColor(dirty ? _palette.Warn : _palette.Ok);
        _statusText.Text = dirty ? Tr("Unsaved changes") : Tr("All changes saved");
        _autosaveText.Text = App.LastAutosaveUtc is { } t
            ? string.Format(Tr("Autosaved {0}"), t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture))
            : "";
        _autosaveText.Visible = _autosaveText.Text.Length > 0;
        _pathText.Text = App.FilePath ?? Tr("Not saved to a file yet");
        _pathText.TooltipText = App.FilePath ?? "";
    }

    // ---- settings ----------------------------------------------------------------------------

    void OnSettingsChanged()
    {
        var s = App.Settings;
        _palette = BlazmaTheme.PaletteFor(s.Theme);
        Theme = BlazmaTheme.For(s.Theme);
        LayoutDirection = App.IsRtl ? LayoutDirectionEnum.Rtl : LayoutDirectionEnum.Ltr;

        _frame.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            DrawCenter = false, BorderColor = _palette.Line, BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
        });
        _brandMark.AddThemeStyleboxOverride("panel", BlazmaTheme.Box(_palette.Accent, null, 4, 0, 0, 0, 0, 0));
        Stage.ApplyPalette(_palette);
        Gizmo.Bind(Stage.Camera, _palette);

        _segMode.SetIndex(s.AdvancedMode ? 1 : 0);
        _segLang.SetIndex(s.Language == "ar" ? 1 : 0);
        _segTheme.SetIndex(s.Theme == "light" ? 1 : 0);
        CheckOnly(_mLang, s.Language == "ar" ? 1 : 0);
        CheckOnly(_mTheme, s.Theme == "light" ? 1 : 0);
        CheckOnly(_mAuto, s.AutosaveMinutes);
        CheckOnly(_mMode, s.AdvancedMode ? 1 : 0);

        OutlinePanel.Visible = s.AdvancedMode;
        HistoryPanel.Visible = s.AdvancedMode;
        PlaceInspector();
        RefreshAll();
    }

    /// <summary>The inspector sits at the inline end: right in English, left in Arabic (the stage itself never mirrors).</summary>
    void PlaceInspector()
    {
        const float w = 264, gap = 12;
        Inspector.GrowVertical = GrowDirection.End;
        if (App.IsRtl)
        {
            Inspector.AnchorLeft = 0; Inspector.AnchorRight = 0;
            Inspector.OffsetLeft = gap; Inspector.OffsetRight = gap + w;
            Inspector.GrowHorizontal = GrowDirection.End;
        }
        else
        {
            Inspector.AnchorLeft = 1; Inspector.AnchorRight = 1;
            Inspector.OffsetLeft = -gap - w; Inspector.OffsetRight = -gap;
            Inspector.GrowHorizontal = GrowDirection.Begin;
        }
        Inspector.AnchorTop = 0; Inspector.AnchorBottom = 0;
        Inspector.OffsetTop = gap; Inspector.OffsetBottom = gap;
    }

    /// <summary>Core messages are short English technical text; show a friendly, translatable version for the common cases.</summary>
    string FriendlyError(string message)
    {
        if (message.Contains("must not be empty")) return Tr("This field cannot be empty.");
        if (message.Contains("is too long")) return Tr("That name is too long (200 characters at most).");
        if (System.Text.RegularExpressions.Regex.Match(message, @"must be ≥ (\d+)") is { Success: true } m)
            return string.Format(Tr("The value must be at least {0}."), m.Groups[1].Value);
        if (message.Contains("must be between 1 and 10,000,000")) return Tr("The length must be between 1 and 10,000,000 frames.");
        return Tr(message);
    }

    void OnError(string message)
    {
        _lastError = message;
        if (_suppressToast) return;
        _toast.Notify(FriendlyError(message), _palette.Err);
    }

    // ---- layout persistence -------------------------------------------------------------------

    void ScheduleLayoutSave()
    {
        SaveLayoutValues();
        _layoutTimer.Start();
    }

    void SaveLayoutValues()
    {
        var l = App.Settings.Layout;
        l["split_left"] = _splitOuter.SplitOffset;
        l["split_right"] = _splitInner.SplitOffset;
        l["split_dock"] = _splitDock.SplitOffset;
        var win = GetWindow();
        if (win.Mode == Window.ModeEnum.Windowed)
        {
            l["window_w"] = win.Size.X;
            l["window_h"] = win.Size.Y;
        }
    }

    void RestoreWindowAndLayout()
    {
        var l = App.Settings.Layout;
        int Get(string key) => l.TryGetValue(key, out var v) ? v.AsInt32() : 0;
        _splitOuter.SplitOffset = Get("split_left");
        _splitInner.SplitOffset = Get("split_right");
        _splitDock.SplitOffset = Get("split_dock");
        var win = GetWindow();
        if (!SelfTest.IsRequested && win.Mode == Window.ModeEnum.Windowed && Get("window_w") >= 800 && Get("window_h") >= 500)
            win.Size = new Vector2I(Get("window_w"), Get("window_h"));
    }

    // ---- recovery ---------------------------------------------------------------------------

    public void ShowRecoveryIfAny()
    {
        var found = App.Recovery.FindRecoverable();
        if (found.Count == 0) return;
        RecoveryDialogNode = new RecoveryDialog(found);
        AddChild(RecoveryDialogNode);
        RecoveryDialogNode.PopupCentered(new Vector2I(600, 0));
    }

    // ---- keyboard ---------------------------------------------------------------------------

    public override void _ShortcutInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
        var inText = GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit;
        if (!k.IsCommandOrControlPressed() || k.AltPressed) return;
        switch (k.Keycode)
        {
            case Godot.Key.N: RequestNew(); break;
            case Godot.Key.O: RequestOpen(); break;
            case Godot.Key.S:
                if (k.ShiftPressed) PromptSaveAs(null); else RequestSave();
                break;
            case Godot.Key.Z:
                if (inText) return; // let the text field undo its own typing
                if (k.ShiftPressed) DoRedo(); else DoUndo();
                break;
            case Godot.Key.Y:
                if (inText) return;
                DoRedo();
                break;
            default: return;
        }
        GetViewport().SetInputAsHandled();
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Godot.Key.Delete } && SelectedClipId is { } id)
        {
            ConfirmDeleteClip(id);
            GetViewport().SetInputAsHandled();
        }
    }
}
