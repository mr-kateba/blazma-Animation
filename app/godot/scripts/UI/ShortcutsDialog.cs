using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Settings > Keyboard Shortcuts: search, click a key to rebind it, conflict warning, reset, import and export (JSON).</summary>
public partial class ShortcutsDialog : AcceptDialog
{
    static ShortcutMap Map => ShortcutMap.Instance;

    public LineEdit Search { get; } = new() { PlaceholderText = "Search shortcuts…", ClearButtonEnabled = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    public Button ResetAllButton { get; } = new() { Text = "Reset all to defaults", FocusMode = Control.FocusModeEnum.None };
    public Button ImportButton { get; } = new() { Text = "Import…", FocusMode = Control.FocusModeEnum.None };
    public Button ExportButton { get; } = new() { Text = "Export…", FocusMode = Control.FocusModeEnum.None };
    public Button ReplaceButton { get; } = new() { Text = "Replace", ThemeTypeVariation = "Primary", FocusMode = Control.FocusModeEnum.None };
    public Button CancelPendingButton { get; } = new() { Text = "Cancel", FocusMode = Control.FocusModeEnum.None };
    public PanelContainer WarnBar { get; } = new() { ThemeTypeVariation = "Chip", Visible = false };
    public Label WarnLabel { get; } = W.Label("", null, false);
    readonly VBoxContainer _list = new();
    readonly Dictionary<string, Button> _chordButtons = new();
    readonly FileDialog _file = new() { Access = FileDialog.AccessEnum.Filesystem, Exclusive = true };
    string? _capturing;
    string? _pendingId;
    Chord _pendingChord;
    bool _exporting;

    /// <summary>The action waiting for a key press, or null.</summary>
    public string? CapturingId => _capturing;
    public string? PendingConflictId => _pendingId;
    public int RowCount => _chordButtons.Count;
    public string ChordTextOf(string id) => _chordButtons.TryGetValue(id, out var b) ? b.Text : "";

    public ShortcutsDialog()
    {
        Title = "Keyboard Shortcuts";
        OkButtonText = "Close";
        Exclusive = false;
        MinSize = new Vector2I(700, 560);
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);
        AddChild(root);

        var intro = W.Label("Click a key, then press the new shortcut. Esc cancels. Shortcuts never fire while you type in a text field.", "Dim");
        intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        intro.CustomMinimumSize = new Vector2(640, 0);
        root.AddChild(intro);
        root.AddChild(Search);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(640, 330), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _list.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _list.AddThemeConstantOverride("separation", 3);
        scroll.AddChild(_list);
        root.AddChild(scroll);

        var wr = new HBoxContainer();
        wr.AddThemeConstantOverride("separation", 8);
        WarnLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        WarnLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        wr.AddChild(WarnLabel);
        wr.AddChild(ReplaceButton);
        wr.AddChild(CancelPendingButton);
        WarnBar.AddChild(W.Margin(wr, 10, 6, 10, 6));
        root.AddChild(WarnBar);

        var foot = new HBoxContainer();
        foot.AddThemeConstantOverride("separation", 8);
        foot.AddChild(ResetAllButton);
        foot.AddChild(ImportButton);
        foot.AddChild(ExportButton);
        root.AddChild(foot);

        _file.AddFilter("*.json", "Shortcuts (JSON)");
        AddChild(_file);
        _file.FileSelected += OnFile;

        Search.TextChanged += _ => Rebuild();
        ResetAllButton.Pressed += () => Map.ResetAll();
        ReplaceButton.Pressed += ConfirmReplace;
        CancelPendingButton.Pressed += CancelPending;
        ImportButton.Pressed += () => { _exporting = false; _file.FileMode = FileDialog.FileModeEnum.OpenFile; _file.Title = Tr("Import shortcuts"); _file.PopupCentered(new Vector2I(720, 460)); };
        ExportButton.Pressed += () => { _exporting = true; _file.FileMode = FileDialog.FileModeEnum.SaveFile; _file.CurrentFile = "blazma-shortcuts.json"; _file.Title = Tr("Export shortcuts"); _file.PopupCentered(new Vector2I(720, 460)); };
        Map.Changed += OnMapChanged;
        VisibilityChanged += () => { if (!Visible) { StopCapture(); CancelPending(); } };
        Rebuild();
    }

    public override void _ExitTree() => Map.Changed -= OnMapChanged;

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady()) Rebuild();
    }

    void OnMapChanged() { if (IsNodeReady()) Rebuild(); }

    public void Open()
    {
        Search.Text = "";
        Rebuild();
        PopupCentered(new Vector2I(720, 600));
    }

    string Pretty(string id) => Map.DisplayAll(id) is { Length: > 0 } d ? d : "—";

    void Rebuild()
    {
        foreach (var c in _list.GetChildren()) { _list.RemoveChild(c); c.QueueFree(); }
        _chordButtons.Clear();
        var q = Search.Text.Trim().ToLowerInvariant();
        foreach (var cat in ShortcutMap.Categories)
        {
            var rows = ShortcutMap.All.Where(a => a.Category == cat
                && (q.Length == 0 || Tr(a.Label).ToLowerInvariant().Contains(q) || a.Label.ToLowerInvariant().Contains(q) || Pretty(a.Id).ToLowerInvariant().Contains(q) || Tr(cat).ToLowerInvariant().Contains(q))).ToList();
            if (rows.Count == 0) continue;
            var head = W.Label(cat, "Heading");
            _list.AddChild(W.Margin(head, 0, 8, 0, 2));
            foreach (var a in rows)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 6);
                row.AddChild(new Label { Text = Tr(a.Label), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutoTranslateMode = AutoTranslateModeEnum.Disabled });
                var b = new Button { Text = _capturing == a.Id ? Tr("Press a key…") : Pretty(a.Id), CustomMinimumSize = new Vector2(200, 0), ToggleMode = true, ButtonPressed = _capturing == a.Id, FocusMode = Control.FocusModeEnum.None, AutoTranslateMode = AutoTranslateModeEnum.Disabled, LayoutDirection = Control.LayoutDirectionEnum.Ltr };
                var id = a.Id;
                b.Pressed += () => StartCapture(id);
                row.AddChild(b);
                _chordButtons[a.Id] = b;
                var un = new Button { Text = "×", TooltipText = "Remove the shortcut", FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(30, 0) };
                un.Pressed += () => { CancelPending(); Map.Set(id, null); };
                row.AddChild(un);
                var rs = new Button { Text = "↺", TooltipText = "Back to the default", FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(30, 0), Disabled = !Map.IsCustom(a.Id) };
                rs.Pressed += () => { CancelPending(); Map.Reset(id); };
                row.AddChild(rs);
                _list.AddChild(row);
            }
        }
    }

    public void StartCapture(string id)
    {
        CancelPending();
        _capturing = id;
        Map.Capturing = true;
        Rebuild();
    }

    void StopCapture()
    {
        if (_capturing is null) return;
        _capturing = null;
        Map.Capturing = false;
        if (IsNodeReady()) Rebuild();
    }

    public override void _Input(InputEvent e)
    {
        if (_capturing is null || e is not InputEventKey { Pressed: true, Echo: false } k) return;
        var code = k.Keycode != Key.None ? k.Keycode : k.PhysicalKeycode;
        if (code is Key.Shift or Key.Ctrl or Key.Alt or Key.Meta) { GetViewport().SetInputAsHandled(); return; } // wait for the real key
        GetViewport().SetInputAsHandled();
        if (code == Key.Escape && !k.CtrlPressed && !k.ShiftPressed && !k.AltPressed) { StopCapture(); return; }
        var chord = new Chord(code, k.IsCommandOrControlPressed(), k.ShiftPressed, k.AltPressed);
        var id = _capturing;
        StopCapture();
        TryAssign(id, chord);
    }

    /// <summary>Bind a chord. With a conflict nothing changes yet: the warning bar offers Replace or Cancel. Returns true when it was bound at once.</summary>
    public bool TryAssign(string id, Chord chord)
    {
        var conflicts = Map.Conflicts(id, chord);
        if (conflicts.Count == 0) { CancelPending(); Map.Set(id, chord); return true; }
        _pendingId = id;
        _pendingChord = chord;
        ReplaceButton.Visible = true;
        WarnLabel.Text = string.Format(Tr("{0} is already used by “{1}”. Replace it? That action will have no key until you give it a new one."),
            chord.Display(), string.Join(", ", conflicts.Select(c => Tr(c.Label))));
        WarnBar.Visible = true;
        return false;
    }

    public void ConfirmReplace()
    {
        if (_pendingId is { } id) Map.Set(id, _pendingChord, replaceConflicts: true);
        CancelPending();
    }

    public void CancelPending()
    {
        _pendingId = null;
        WarnBar.Visible = false;
    }

    void OnFile(string path)
    {
        try
        {
            if (_exporting) { System.IO.File.WriteAllText(path, Map.ExportJson()); return; }
            var n = Map.ImportJson(System.IO.File.ReadAllText(path), out var err);
            if (n < 0) { WarnLabel.Text = Tr("This is not a shortcuts file.") + " " + err; WarnBar.Visible = true; ReplaceButton.Visible = false; return; }
            ReplaceButton.Visible = true;
            var c = Map.AllConflicts();
            if (c.Count > 0)
            {
                WarnLabel.Text = string.Format(Tr("Imported. Warning: {0} is used by both “{1}” and “{2}”."), c[0].chord.Display(), Tr(c[0].a.Label), Tr(c[0].b.Label));
                WarnBar.Visible = true; ReplaceButton.Visible = false;
            }
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            WarnLabel.Text = e.Message; WarnBar.Visible = true; ReplaceButton.Visible = false;
        }
    }
}
