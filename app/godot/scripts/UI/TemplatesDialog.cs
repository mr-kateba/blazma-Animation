using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 8: the templates browser. Lists the built-in templates (idle, walk and run cycles, jump, wave, attack swing) and the project's
/// own, shows the description of the chosen one, and applies it to a character with a length in seconds: the result is a normal new clip
/// that is selected and starts playing. Advanced mode also offers "Save the current clip as a template" and deleting project templates.
/// Everything goes through the template.* commands (the same ones agents use).
/// </summary>
public partial class TemplatesDialog : ConfirmationDialog
{
    sealed record Row(string Id, string Name, string Description, string Kind, string Category, double Seconds, bool Loop);

    readonly MainWindow _host;
    readonly List<Row> _rows = new();
    public ItemList List { get; } = new()
    {
        AutoTranslateMode = AutoTranslateModeEnum.Disabled, CustomMinimumSize = new Vector2(250, 250), SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        SelectMode = ItemList.SelectModeEnum.Single,
    };
    public OptionButton Character { get; } = P8.Option(220);
    public SpinBox Duration { get; } = P8.Spin(0.2, 30, 0.1, 2, " s");
    public LineEdit ClipName { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    public Button DeleteButton { get; } = new() { Text = "Delete template", FocusMode = Control.FocusModeEnum.None };
    public LineEdit SaveName { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    public LineEdit SaveDescription { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    public Button SaveButton { get; } = new() { Text = "Save clip as template", FocusMode = Control.FocusModeEnum.None };
    public VBoxContainer SaveBox { get; } = new();
    readonly Label _title = W.Label("", "Heading", false);
    readonly Label _kind = W.Label("", "Section", false);
    readonly Label _description = W.Label("", "Dim", false);
    readonly Label _detail = W.Label("", "Faint", false);
    readonly Label _status = W.Label("", null, false);
    readonly Label _saveHint = W.Label("", "Faint", false);
    readonly Label _noChar = W.Label("Add a character first: a template is applied to a character.", "ErrLabel");
    readonly List<string> _charIds = new();

    static AppController App => AppController.Instance;

    public bool IsOpen => Visible;
    public int RowCount => _rows.Count;
    public IReadOnlyList<string> RowIds => _rows.Select(r => r.Id).ToList();
    public string? SelectedId => List.GetSelectedItems().Length == 1 ? _rows[List.GetSelectedItems()[0]].Id : null;
    public string TitleText => _title.Text;
    public string DescriptionText => _description.Text;
    public string KindText => _kind.Text;
    public string StatusText => _status.Text;
    public string? LastError { get; private set; }
    public string? LastClipId { get; private set; }
    public bool ApplyEnabled => !GetOkButton().Disabled;
    public string ItemText(int i) => List.GetItemText(i);

    public TemplatesDialog(MainWindow host)
    {
        _host = host;
        Title = "Clip Templates";
        OkButtonText = "Apply to character";
        CancelButtonText = "Close";
        Exclusive = true;
        MinSize = new Vector2I(700, 0);
        DialogHideOnOk = false;

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        AddChild(root);
        var intro = W.Label("Start from a ready-made motion: pick a template, choose the character and the length, and Blazma adds it as a new clip and plays it.", "Dim");
        P8.Wrap(intro, 660);
        root.AddChild(intro);

        var split = new HBoxContainer();
        split.AddThemeConstantOverride("separation", 16);
        root.AddChild(split);
        split.AddChild(List);

        var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 8);
        split.AddChild(right);
        right.AddChild(_kind);
        right.AddChild(_title);
        P8.Wrap(_description, 360); P8.Wrap(_detail, 360);
        right.AddChild(_description);
        right.AddChild(_detail);
        right.AddChild(new HSeparator());
        var grid = P8.Grid();
        right.AddChild(grid);
        P8.Row(grid, "Character", Character);
        P8.Row(grid, "Length", Duration);
        ClipName.TextDirection = Control.TextDirection.Auto;
        P8.Row(grid, "Clip name", ClipName);
        P8.Wrap(_noChar, 360);
        right.AddChild(_noChar);
        right.AddChild(DeleteButton);
        DeleteButton.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;

        P8.Wrap(_status, 660);
        root.AddChild(_status);

        SaveBox.AddThemeConstantOverride("separation", 8);
        SaveBox.AddChild(new HSeparator());
        SaveBox.AddChild(W.Label("Save the current clip as a template", "Section"));
        var sg = P8.Grid();
        SaveBox.AddChild(sg);
        SaveName.TextDirection = Control.TextDirection.Auto; SaveDescription.TextDirection = Control.TextDirection.Auto;
        P8.Row(sg, "Template name", SaveName);
        P8.Row(sg, "Description", SaveDescription);
        P8.Wrap(_saveHint, 660);
        SaveBox.AddChild(_saveHint);
        SaveBox.AddChild(SaveButton);
        SaveButton.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        root.AddChild(SaveBox);

        List.ItemSelected += _ => ShowSelected();
        List.ItemActivated += _ => Apply();
        DeleteButton.Pressed += DeleteSelected;
        SaveButton.Pressed += SaveCurrent;
        Character.ItemSelected += _ => UpdateButtons();
        Confirmed += Apply;
        CloseRequested += Hide;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady() && Visible) Reload(SelectedId);
    }

    // ---- opening ----------------------------------------------------------------------------------------------------------------

    public void Open()
    {
        FillCharacters();
        var clip = App.ActiveClip;
        SaveName.Text = clip is null ? "" : clip.Name;
        SaveDescription.Text = "";
        _status.Text = ""; LastError = null;
        ClipName.PlaceholderText = Tr("Default: the template name");
        Reload(null);
        ClipName.Text = "";
        PopupCentered(new Vector2I(740, 0));
    }

    void FillCharacters()
    {
        var p = App.Session.Project;
        Character.Clear(); _charIds.Clear();
        foreach (var c in p.Characters) { Character.AddItem(c.Name); _charIds.Add(c.Id.ToString()); }
        var idx = _charIds.IndexOf(App.FocusCharacterId ?? "");
        if (_charIds.Count > 0) Character.Select(Math.Max(0, idx));
        _noChar.Visible = _charIds.Count == 0;
    }

    /// <summary>Re-read template.list and rebuild the list, keeping the selection when it still exists.</summary>
    public void Reload(string? keep)
    {
        _rows.Clear();
        List.Clear();
        var r = App.Execute("template.list");
        if (r.Ok && r.Data?["templates"] is JsonArray arr)
            foreach (var t in arr.OfType<JsonObject>())
                _rows.Add(new Row((string)t["templateId"]!, (string)t["name"]!, (string?)t["description"] ?? "", (string)t["kind"]!, (string?)t["category"] ?? "",
                    (double?)t["defaultSeconds"] ?? 2, (bool?)t["loop"] ?? false));
        foreach (var row in _rows)
        {
            var name = row.Kind == "builtin" ? Tr(row.Name) : row.Name;
            var i = List.AddItem(row.Kind == "builtin" ? name : "★ " + name);
            List.SetItemTooltip(i, row.Kind == "builtin" ? Tr(row.Description) : row.Description);
        }
        var sel = keep is null ? 0 : Math.Max(0, _rows.FindIndex(x => x.Id == keep));
        if (_rows.Count > 0) { List.Select(sel); ShowSelected(); }
        SaveBox.Visible = App.Settings.AdvancedMode;
        _saveHint.Text = Tr("The template keeps the motion of the active clip together with its character's skeleton, so you can apply it to other characters later.");
    }

    Row? Current => List.GetSelectedItems() is { Length: 1 } s ? _rows[s[0]] : null;

    void ShowSelected()
    {
        var row = Current;
        if (row is null) { _title.Text = ""; _description.Text = ""; _detail.Text = ""; _kind.Text = ""; UpdateButtons(); return; }
        var builtin = row.Kind == "builtin";
        _kind.Text = builtin ? Tr("Built-in") : Tr("Project template");
        _title.Text = builtin ? Tr(row.Name) : row.Name;
        _description.Text = builtin ? Tr(row.Description) : (row.Description.Length > 0 ? row.Description : Tr("No description."));
        _detail.Text = string.Format(CultureInfo.InvariantCulture, row.Loop ? Tr("Default length {0} s. Loops.") : Tr("Default length {0} s."), P8.Ltr(P8.Num(row.Seconds)));
        Duration.Value = Math.Clamp(row.Seconds, 0.2, 30);
        UpdateButtons();
    }

    void UpdateButtons()
    {
        var row = Current;
        GetOkButton().Disabled = row is null || Character.Selected < 0 || _charIds.Count == 0;
        DeleteButton.Visible = row is { Kind: "project" };
        SaveButton.Disabled = App.ActiveClip is null;
    }

    // ---- actions ----------------------------------------------------------------------------------------------------------------

    /// <summary>Apply the selected template (OK button or double-click on a row).</summary>
    public void Apply()
    {
        var row = Current;
        if (row is null || Character.Selected < 0 || Character.Selected >= _charIds.Count) return;
        var name = ClipName.Text.Trim();
        var r = _host.ApplyTemplateNow(row.Id, _charIds[Character.Selected], Duration.Value, name.Length > 0 ? name : null);
        if (!r.Ok)
        {
            LastError = r.Error?.Message ?? "";
            _status.Text = FriendlyError(r.Error?.Code, LastError);
            _status.AddThemeColorOverride("font_color", _host.Palette.Err);
            return;
        }
        LastError = null;
        LastClipId = (string?)r.Data?["clipId"];
        _status.RemoveThemeColorOverride("font_color");
        _status.Text = "";
        Hide();
    }

    string FriendlyError(string? code, string message) => _host.CodeMessage(code) ?? code switch
    {
        "not_humanoid" => Tr("This character has no humanoid bone mapping, so templates cannot be applied to it. Map its bones first (Rigging > Bone Mapping)."),
        "invalid_args" => Tr("The length must be between 0.2 and 30 seconds."),
        "not_found" => Tr("The template or the character no longer exists."),
        _ => string.Format(CultureInfo.InvariantCulture, Tr("The template could not be applied: {0}"), message),
    };

    void DeleteSelected()
    {
        if (Current is not { Kind: "project" } row) return;
        var r = App.Execute("template.delete", new JsonObject { ["templateId"] = row.Id });
        if (!r.Ok) return;
        Reload(null);
        _host.ToastNode.NotifyAction("Template deleted.", _host.Palette.Warn, "Undo", () => { _host.DoUndo(); if (Visible) Reload(null); });
    }

    void SaveCurrent()
    {
        var name = SaveName.Text.Trim();
        if (App.ActiveClip is not { } clip) return;
        if (name.Length == 0) { _status.Text = Tr("Give the template a name."); _status.AddThemeColorOverride("font_color", _host.Palette.Err); return; }
        var args = new JsonObject { ["clipId"] = clip.Id.ToString(), ["name"] = name, ["description"] = SaveDescription.Text.Trim() };
        if (App.FocusCharacterId is { } cid) args["characterId"] = cid;
        var r = _host.ExecuteQuiet("template.save_clip", args);
        if (!r.Ok)
        {
            LastError = r.Error?.Message;
            _status.Text = r.Error?.Code == "not_humanoid"
                ? Tr("This clip's character has no humanoid bone mapping, so it cannot be saved as a template.")
                : string.Format(CultureInfo.InvariantCulture, Tr("The template could not be saved: {0}"), r.Error?.Message ?? "");
            _status.AddThemeColorOverride("font_color", _host.Palette.Err);
            return;
        }
        LastError = null;
        var id = (string?)r.Data?["templateId"];
        Reload(id);
        _status.RemoveThemeColorOverride("font_color");
        _status.AddThemeColorOverride("font_color", _host.Palette.Ok);
        _status.Text = string.Format(CultureInfo.InvariantCulture, Tr("Saved the template “{0}”."), name);
    }

    // ---- test hooks ------------------------------------------------------------------------------------------------------------------

    public void SelectById(string id)
    {
        var i = _rows.FindIndex(r => r.Id == id);
        if (i < 0) return;
        List.Select(i);
        ShowSelected();
    }

    public void SelectCharacterById(string id)
    {
        var i = _charIds.IndexOf(id);
        if (i >= 0) { Character.Select(i); UpdateButtons(); }
    }

    public void PressApply() => EmitSignal(ConfirmationDialog.SignalName.Confirmed);
    public void PressSave() => P8.Press(SaveButton);
    public void PressDelete() => P8.Press(DeleteButton);
}
