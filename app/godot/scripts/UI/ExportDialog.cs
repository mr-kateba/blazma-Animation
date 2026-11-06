using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Export;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 4: export a clip as a .glb animation. The dialog only collects options and shows the outcome; the work is the
/// <c>animation.export</c> command, run through the same path as every other UI command. The command validates the written
/// file itself, so the result panel can say whether the file matches the clip.
/// </summary>
public partial class ExportDialog : ConfirmationDialog
{
    static readonly string[] RootModes = { "keep", "in_place", "in_place_all" };

    readonly MainWindow _host;
    readonly OptionButton _clip = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, FitToLongestItem = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(280, 0) };
    readonly OptionButton _character = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, FitToLongestItem = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    readonly OptionButton _root = new() { FitToLongestItem = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    readonly CheckBox _whole = new() { Text = "Whole clip", ButtonPressed = true };
    readonly SpinBox _start = new() { MinValue = 0, MaxValue = 10_000_000, Step = 1, Rounded = true };
    readonly SpinBox _end = new() { MinValue = 0, MaxValue = 10_000_000, Step = 1, Rounded = true };
    readonly CheckBox _sidecar = new() { Text = "Write sidecar JSON", ButtonPressed = true };
    readonly LineEdit _path = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutoTranslateMode = AutoTranslateModeEnum.Disabled, TextDirection = Control.TextDirection.Ltr, CustomMinimumSize = new Vector2(300, 0) };
    readonly Button _browse = new() { Text = "Browse…" };
    readonly Label _hint = W.Label("", "Faint", false);
    readonly Label _status = W.Label("", null, false);
    readonly Label _details = W.Label("", "Dim", false);
    readonly Button _openFolder = new() { Text = "Open folder", Visible = false };
    readonly VBoxContainer _result = new() { Visible = false };
    readonly FileDialog _fileDialog;
    readonly ConfirmationDialog _overwrite;
    readonly List<(string id, string name)> _clips = new();
    readonly List<(string id, string name)> _chars = new();

    string _confirmedPath = "";
    string _autoName = "";
    bool _userPath, _updating, _busy;
    string? _lastPath;

    static AppController App => AppController.Instance;

    /// <summary>Outcome of the last export run through this dialog (null before the first run).</summary>
    public CommandResult? LastResult { get; private set; }
    /// <summary>Message shown for the last failure (translated), or null.</summary>
    public string? LastError { get; private set; }
    public string? LastOutputPath => _lastPath;
    public bool IsOpen => Visible;
    public bool Busy => _busy;
    public Task? RunTask { get; private set; }
    public string ResultText => _status.Text + "\n" + _details.Text;
    public bool OpenFolderVisible => _openFolder.Visible;
    public bool OverwritePromptVisible => _overwrite.Visible;

    public ExportDialog(MainWindow host)
    {
        _host = host;
        Title = "Export Animation";
        OkButtonText = "Export";
        CancelButtonText = "Close";
        Exclusive = true;
        Unresizable = false;
        MinSize = new Vector2I(560, 0);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 12);
        AddChild(root);

        var intro = W.Label("Save a clip as a .glb animation that other tools and game engines can open. The project itself is not changed.", "Dim");
        intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        intro.CustomMinimumSize = new Vector2(500, 0);
        root.AddChild(intro);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 10);
        root.AddChild(grid);

        Row(grid, "Clip", _clip);
        Row(grid, "Character", _character);

        _root.AddItem("Keep", 0);
        _root.AddItem("In place", 1);
        _root.AddItem("In place (all)", 2);
        _root.TooltipText = "Keep: the root bone moves as animated. In place: removes sideways and forward travel but keeps height. In place (all): removes all root movement, so the character stays put.";
        Row(grid, "Root motion", _root);

        _whole.TooltipText = "Export every frame of the clip, from the first to the last.";
        Row(grid, "Frame range", _whole);
        foreach (var s in new[] { _start, _end })
        {
            s.LocalizeNumeralSystem = false; // numbers stay Latin digits in Arabic
            s.GetLineEdit().LocalizeNumeralSystem = false;
            s.GetLineEdit().TextDirection = Control.TextDirection.Ltr;
            s.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        }
        Row(grid, "From frame", _start);
        Row(grid, "To frame", _end);

        grid.AddChild(new Control());
        _sidecar.TooltipText = "Also write a small .blazma.json file next to the .glb with frame rate, loop, markers, events and root motion details.";
        grid.AddChild(_sidecar);

        var pathRow = new HBoxContainer();
        pathRow.AddThemeConstantOverride("separation", 6);
        pathRow.AddChild(_path);
        pathRow.AddChild(_browse);
        Row(grid, "Output file", pathRow);

        _hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _hint.CustomMinimumSize = new Vector2(500, 0);
        root.AddChild(_hint);

        // result panel (shown after a run)
        root.AddChild(new HSeparator());
        _result.AddThemeConstantOverride("separation", 6);
        var statusRow = new HBoxContainer();
        statusRow.AddThemeConstantOverride("separation", 8);
        _status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.CustomMinimumSize = new Vector2(300, 0);
        statusRow.AddChild(_status);
        statusRow.AddChild(_openFolder);
        _result.AddChild(statusRow);
        _details.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _details.CustomMinimumSize = new Vector2(500, 0);
        _result.AddChild(_details);
        root.AddChild(_result);

        _fileDialog = new FileDialog
        {
            UseNativeDialog = true,
            Access = FileDialog.AccessEnum.Filesystem,
            FileMode = FileDialog.FileModeEnum.SaveFile,
            Filters = new[] { "*.glb ; glTF Binary" },
        };
        _fileDialog.FileSelected += OnFilePicked;
        AddChild(_fileDialog);

        _overwrite = new ConfirmationDialog { Exclusive = true, AutoTranslateMode = AutoTranslateModeEnum.Disabled };
        _overwrite.Confirmed += () => RunTask = RunAsync(true);
        AddChild(_overwrite);

        _clip.ItemSelected += _ => OnClipChanged();
        _character.ItemSelected += _ => RefreshHint();
        _root.ItemSelected += _ => RefreshHint();
        _whole.Toggled += _ => SyncRange();
        _start.ValueChanged += _ => ClampRange(true);
        _end.ValueChanged += _ => ClampRange(false);
        _path.TextChanged += _ => { if (!_updating) _userPath = true; ClearResult(); };
        _browse.Pressed += PromptBrowse;
        _openFolder.Pressed += OpenFolder;

        DialogHideOnOk = false; // stays open to show the result
        Confirmed += OnExportPressed;
        CloseRequested += Hide;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady() && Visible) RefreshHint();
    }

    static void Row(GridContainer grid, string label, Control field)
    {
        var l = W.Label(label, "Dim");
        l.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        grid.AddChild(l);
        field.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        grid.AddChild(field);
    }

    // ---- opening ---------------------------------------------------------------------------------------------------

    /// <summary>Fill the fields from the project (current clip and character) and show the dialog.</summary>
    public void Open()
    {
        var p = App.Session.Project;
        _clips.Clear(); _clip.Clear();
        foreach (var c in p.Clips) { _clips.Add((c.Id.ToString(), c.Name)); _clip.AddItem(c.Name); }
        _chars.Clear(); _character.Clear();
        foreach (var c in p.Characters) { _chars.Add((c.Id.ToString(), c.Name)); _character.AddItem(c.Name); }
        var ci = _clips.FindIndex(c => c.id == App.ActiveClipId);
        if (_clips.Count > 0) _clip.Select(Math.Max(ci, 0));
        PickCharacterForClip();
        _root.Select(0);
        _whole.SetPressedNoSignal(true);
        _sidecar.ButtonPressed = true;
        _userPath = false;
        _confirmedPath = "";
        LastResult = null; LastError = null;
        ClearResult();
        SyncClipRange();
        SetDefaultPath();
        RefreshHint();
        SetBusy(false);
        PopupCentered(new Vector2I(600, 0));
    }

    /// <summary>The character the clip belongs to (its rig), else the current one, else the first.</summary>
    void PickCharacterForClip()
    {
        if (_chars.Count == 0) return;
        var p = App.Session.Project;
        var clip = SelectedClip;
        var idx = -1;
        if (clip is not null && !clip.Skeleton.IsEmpty)
        {
            var current = App.FocusCharacterId;
            var match = p.Characters.Where(c => c.Rig == clip.Skeleton).ToList();
            if (match.Count > 0) idx = _chars.FindIndex(c => c.id == (match.FirstOrDefault(m => m.Id.ToString() == current) ?? match[0]).Id.ToString());
        }
        if (idx < 0) idx = _chars.FindIndex(c => c.id == App.FocusCharacterId);
        _character.Select(Math.Max(idx, 0));
    }

    Clip? SelectedClip => _clip.Selected >= 0 && _clip.Selected < _clips.Count ? App.Session.Project.Clips.Find(c => c.Id.ToString() == _clips[_clip.Selected].id) : null;

    void OnClipChanged()
    {
        PickCharacterForClip();
        SyncClipRange();
        if (!_userPath) SetDefaultPath();
        else
        {
            // keep a hand-typed folder, but follow the clip name when the file still has the automatic name
            var cur = _path.Text.Trim();
            if (_autoName.Length > 0 && System.IO.Path.GetFileName(cur) == _autoName)
                SetPathText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(cur) ?? "", DefaultFileName()));
        }
        ClearResult();
        RefreshHint();
    }

    void SyncClipRange()
    {
        var len = SelectedClip?.DurationFrames ?? 0;
        _start.MaxValue = len; _end.MaxValue = len;
        _start.SetValueNoSignal(0); _end.SetValueNoSignal(len);
        SyncRange();
    }

    void SyncRange()
    {
        var custom = !_whole.ButtonPressed;
        _start.Editable = custom; _end.Editable = custom;
        if (!custom)
        {
            _start.SetValueNoSignal(0);
            _end.SetValueNoSignal(SelectedClip?.DurationFrames ?? 0);
        }
        ClearResult();
    }

    void ClampRange(bool startMoved)
    {
        if (_start.Value > _end.Value)
        {
            if (startMoved) _end.SetValueNoSignal(_start.Value); else _start.SetValueNoSignal(_end.Value);
        }
        ClearResult();
    }

    string DefaultFileName()
    {
        _autoName = MainWindow.SafeFileName(SelectedClip?.Name ?? "Animation") + ".glb";
        return _autoName;
    }

    void SetDefaultPath() => SetPathText(System.IO.Path.Combine(DefaultFolder(), DefaultFileName()));

    void SetPathText(string text)
    {
        _updating = true;
        _path.Text = text;
        _updating = false;
    }

    /// <summary>Last export folder, else the project's own folder, else Documents.</summary>
    string DefaultFolder()
    {
        var last = App.Settings.LastExportDir;
        if (!string.IsNullOrEmpty(last) && System.IO.Directory.Exists(last)) return last;
        if (App.FilePath is { } fp && System.IO.Path.GetDirectoryName(fp) is { Length: > 0 } dir && System.IO.Directory.Exists(dir)) return dir;
        var docs = OS.GetSystemDir(OS.SystemDir.Documents);
        return !string.IsNullOrEmpty(docs) && System.IO.Directory.Exists(docs) ? docs : AppPaths.UserDir;
    }

    void RefreshHint()
    {
        var clip = SelectedClip;
        if (clip is null) { _hint.Text = Tr("This project has no clips yet. Create a clip first."); return; }
        var fps = Fmt.Rate(clip.Fps);
        _hint.Text = string.Format(CultureInfo.InvariantCulture, Tr("{0} frames at {1} fps"), clip.DurationFrames + 1, fps);
        GetOkButton().Disabled = _busy || clip is null || _chars.Count == 0;
    }

    // ---- file picking -----------------------------------------------------------------------------------------------

    void PromptBrowse()
    {
        var cur = _path.Text.Trim();
        var dir = cur.Length > 0 ? System.IO.Path.GetDirectoryName(cur) : null;
        _fileDialog.Title = Tr("Export Animation");
        _fileDialog.CurrentDir = !string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir) ? dir : DefaultFolder();
        _fileDialog.CurrentFile = cur.Length > 0 && System.IO.Path.GetFileName(cur).Length > 0 ? System.IO.Path.GetFileName(cur) : DefaultFileName();
        _fileDialog.PopupCenteredRatio(0.6f);
    }

    void OnFilePicked(string path)
    {
        if (!path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) path += ".glb";
        _userPath = true;
        SetPathText(path);
        _confirmedPath = path; // the file dialog already asked about replacing an existing file
        ClearResult();
    }

    void OpenFolder()
    {
        if (_lastPath is not null) OS.ShellShowInFileManager(_lastPath);
    }

    // ---- running ----------------------------------------------------------------------------------------------------

    void OnExportPressed()
    {
        if (_busy) return;
        var clip = SelectedClip;
        if (clip is null || _character.Selected < 0 || _character.Selected >= _chars.Count) return;
        var path = _path.Text.Trim();
        if (path.Length == 0) { ShowError(Tr("Choose where to save the file.")); return; }
        if (!path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) { path += ".glb"; SetPathText(path); }
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception) { ShowError(Tr("That file name is not valid.")); return; }
        var dir = System.IO.Path.GetDirectoryName(full);
        if (dir is null || !System.IO.Directory.Exists(dir))
        {
            ShowError(string.Format(Tr("The folder “{0}” does not exist."), Ltr(dir ?? full)));
            return;
        }

        var existing = new List<string>();
        if (System.IO.File.Exists(full) && _confirmedPath != path) existing.Add(System.IO.Path.GetFileName(full));
        var side = AnimationExporter.SidecarPathFor(full);
        if (_sidecar.ButtonPressed && System.IO.File.Exists(side)) existing.Add(System.IO.Path.GetFileName(side));
        if (existing.Count == 0)
        {
            RunTask = RunAsync(System.IO.File.Exists(full) || (_sidecar.ButtonPressed && System.IO.File.Exists(side)));
            return;
        }
        _overwrite.Title = Tr("Replace existing file?");
        _overwrite.DialogText = string.Format(Tr("“{0}” already exists. Do you want to replace it?"), Ltr(string.Join(", ", existing)));
        _overwrite.OkButtonText = Tr("Replace");
        _overwrite.CancelButtonText = Tr("Cancel");
        _overwrite.PopupCentered(new Vector2I(460, 0));
    }

    async Task RunAsync(bool overwrite)
    {
        SetBusy(true);
        _result.Visible = true;
        _status.Text = Tr("Exporting…");
        _status.RemoveThemeColorOverride("font_color");
        _details.Text = "";
        _openFolder.Visible = false;
        // let the busy message paint before the (synchronous) export starts
        var tree = GetTree();
        await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        try { Run(overwrite); }
        catch (Exception e) { ShowError(string.Format(Tr("The export failed: {0}"), e.Message)); }
        finally { SetBusy(false); }
    }

    void Run(bool overwrite)
    {
        var clip = SelectedClip!;
        var path = System.IO.Path.GetFullPath(_path.Text.Trim());
        var args = new JsonObject
        {
            ["clipId"] = clip.Id.ToString(),
            ["characterId"] = _chars[_character.Selected].id,
            ["path"] = path,
            ["rootMotion"] = RootModes[Math.Clamp(_root.Selected, 0, 2)],
            ["sidecar"] = _sidecar.ButtonPressed,
            ["overwrite"] = overwrite,
        };
        if (!_whole.ButtonPressed)
        {
            args["startFrame"] = (long)_start.Value;
            args["endFrame"] = (long)_end.Value;
        }
        var r = _host.ExecuteQuiet("animation.export", args);
        LastResult = r;
        if (!r.Ok) { ShowError(FriendlyExportError(r.Error?.Code, r.Error?.Message ?? "")); return; }
        LastError = null;

        var d = r.Data!;
        _lastPath = (string?)d["path"] ?? path;
        App.Settings.LastExportDir = System.IO.Path.GetDirectoryName(_lastPath) ?? "";
        App.Settings.Save();

        var ok = d["validation"] is null || ((bool?)d["validation"]?["ok"] ?? false);
        var bytes = Num(d["bytes"]);
        var frames = Num(d["frames"]);
        var dur = double.Parse(d["durationSeconds"]?.ToJsonString() ?? "0", CultureInfo.InvariantCulture);
        var pal = _host.Palette;
        _status.Text = ok ? Tr("Exported. The file matches the clip.") : Tr("Exported, but the check found differences.");
        _status.AddThemeColorOverride("font_color", ok ? pal.Ok : pal.Warn);
        var lines = new List<string>
        {
            string.Format(CultureInfo.InvariantCulture, Tr("File: {0}"), Ltr(_lastPath)),
            string.Format(CultureInfo.InvariantCulture, Tr("Size: {0}"), Ltr(FormatSize(bytes))),
            string.Format(CultureInfo.InvariantCulture, Tr("Frames: {0} ({1} s)"), frames, Ltr(dur.ToString("0.###", CultureInfo.InvariantCulture))),
        };
        if ((string?)d["sidecarPath"] is { } sc) lines.Add(string.Format(CultureInfo.InvariantCulture, Tr("Sidecar: {0}"), Ltr(sc)));
        if (ok) lines.Add(Tr("Validation: OK"));
        else
        {
            lines.Add(string.Format(CultureInfo.InvariantCulture, Tr("Validation: {0} problem(s)"), Num(d["validation"]?["issueCount"])));
            foreach (var i in (d["validation"]?["issues"] as JsonArray) ?? new JsonArray()) lines.Add("• " + (string?)i);
        }
        foreach (var w in r.Warnings.Where(w => w.Code == "export")) lines.Add(Tr("Note") + ": " + w.Message);
        _details.Text = string.Join("\n", lines);
        _openFolder.Visible = true;
        _host.ToastNode.Notify(Tr("Animation exported."), ok ? pal.Ok : pal.Warn);
    }

    void ShowError(string message)
    {
        LastError = message;
        _result.Visible = true;
        _openFolder.Visible = false;
        _status.Text = Tr("The export did not finish.");
        _status.AddThemeColorOverride("font_color", _host.Palette.Err);
        _details.Text = message;
    }

    void ClearResult()
    {
        if (_busy) return;
        _result.Visible = false;
        _openFolder.Visible = false;
        _status.Text = ""; _details.Text = "";
        LastError = null;
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        var clip = SelectedClip;
        GetOkButton().Disabled = busy || clip is null || _chars.Count == 0;
        _browse.Disabled = busy;
    }

    string FriendlyExportError(string? code, string message) => code switch
    {
        "not_found" when message.Contains("folder") => string.Format(Tr("The folder “{0}” does not exist."), Ltr(FolderFrom(message))),
        "not_found" => Tr("The clip or the character no longer exists."),
        "rig_mismatch" => Tr("This clip animates a different character. Choose the character the clip was made for."),
        "unsupported_format" => Tr("The file name must end in .glb."),
        "file_exists" => Tr("A file with this name already exists."),
        "io_error" => string.Format(Tr("Could not write the file: {0}"), message.Replace("Could not write the export: ", "")),
        "invalid_range" => Tr("The frame range is outside the clip."),
        "too_large" => Tr("The clip is too long to export in one file. Choose a shorter frame range."),
        "empty_rig" => Tr("This character has no bones to export."),
        "unsupported_source" or "missing_asset" or "missing_node" =>
            Tr("This imported character cannot be exported because its original file is missing or has changed."),
        _ => string.Format(Tr("The export failed: {0}"), message),
    };

    static string FolderFrom(string message)
    {
        var a = message.IndexOf('\'');
        var b = message.LastIndexOf('\'');
        return a >= 0 && b > a ? message[(a + 1)..b] : message;
    }

    /// <summary>Integer fields of the command result (they may be stored as int or long).</summary>
    static long Num(JsonNode? n) => n is null ? 0 : long.Parse(n.ToJsonString(), CultureInfo.InvariantCulture);

    /// <summary>Keeps paths and numbers in left-to-right order inside right-to-left text.</summary>
    static string Ltr(string s) => "\u2066" + s + "\u2069";

    static string FormatSize(long bytes) =>
        bytes >= 1 << 20 ? (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
        : bytes >= 1024 ? (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB"
        : bytes.ToString(CultureInfo.InvariantCulture) + " B";

    // ---- test hooks -------------------------------------------------------------------------------------------------

    public void SetPathForTest(string path) { _userPath = true; SetPathText(path); }
    public void SetRootMotionForTest(int index) => _root.Select(index);
    public void SetCustomRangeForTest(long start, long end) { _whole.ButtonPressed = false; _start.Value = start; _end.Value = end; }
    public string PathText => _path.Text;
    public bool WholeClip => _whole.ButtonPressed;
    public bool SidecarChecked { get => _sidecar.ButtonPressed; set => _sidecar.ButtonPressed = value; }
    public int ClipCount => _clips.Count;
    public string? SelectedClipId => _clip.Selected >= 0 && _clip.Selected < _clips.Count ? _clips[_clip.Selected].id : null;
    public string? SelectedCharacterId => _character.Selected >= 0 && _character.Selected < _chars.Count ? _chars[_character.Selected].id : null;
    public void PressExport() => EmitSignal(ConfirmationDialog.SignalName.Confirmed);
    public void ConfirmOverwrite() => _overwrite.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
    public void PressOpenFolderForTest() => _openFolder.EmitSignal(BaseButton.SignalName.Pressed);
}
