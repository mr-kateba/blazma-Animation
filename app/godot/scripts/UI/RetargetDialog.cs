using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 8: "Retarget clip…". Copies the motion of a clip from the character it animates onto another character (different proportions
/// or an imported model) with clip.retarget: a new normal clip is added, selected and played. Foot lock keeps the feet planted where the
/// source clip has ground contacts. The result panel names the bones that could not be mapped.
/// </summary>
public partial class RetargetDialog : ConfirmationDialog
{
    readonly MainWindow _host;
    public OptionButton Clip { get; } = P8.Option(260);
    public OptionButton Source { get; } = P8.Option(260);
    public OptionButton Target { get; } = P8.Option(260);
    public CheckBox FootLock { get; } = new() { Text = "Lock the feet on the ground", ButtonPressed = true, FocusMode = Control.FocusModeEnum.None };
    public LineEdit NewName { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    readonly Label _hint = W.Label("", "Faint", false);
    readonly Label _status = W.Label("", null, false);
    readonly Label _details = W.Label("", "Dim", false);
    readonly List<string> _clipIds = new(), _charIds = new();
    bool _nameTouched, _filling;

    static AppController App => AppController.Instance;

    public bool IsOpen => Visible;
    public string StatusText => _status.Text;
    public string DetailsText => _details.Text;
    public string HintText => _hint.Text;
    public string? LastError { get; private set; }
    public string? LastClipId { get; private set; }
    public CommandResult? LastResult { get; private set; }

    public RetargetDialog(MainWindow host)
    {
        _host = host;
        Title = "Retarget Clip";
        OkButtonText = "Retarget";
        CancelButtonText = "Close";
        Exclusive = true;
        MinSize = new Vector2I(560, 0);
        DialogHideOnOk = false;

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        AddChild(root);
        var intro = W.Label("Copy a clip onto another character, for example an imported model with different proportions. The original clip is not changed: a new clip is added.", "Dim");
        P8.Wrap(intro, 520);
        root.AddChild(intro);
        var grid = P8.Grid();
        root.AddChild(grid);
        P8.Row(grid, "Clip to copy", Clip);
        P8.Row(grid, "Plays now on", Source);
        P8.Row(grid, "Copy onto", Target);
        grid.AddChild(new Control());
        FootLock.TooltipText = "Keep the feet planted where the original clip has the feet on the ground, so the new character does not slide.";
        grid.AddChild(FootLock);
        NewName.TextDirection = Control.TextDirection.Auto;
        P8.Row(grid, "New clip name", NewName);
        P8.Wrap(_hint, 520);
        root.AddChild(_hint);
        root.AddChild(new HSeparator());
        P8.Wrap(_status, 520); P8.Wrap(_details, 520);
        root.AddChild(_status);
        root.AddChild(_details);

        Clip.ItemSelected += _ => { PickSource(); SuggestName(); UpdateButtons(); };
        Source.ItemSelected += _ => UpdateButtons();
        Target.ItemSelected += _ => { SuggestName(); UpdateButtons(); };
        NewName.TextChanged += _ => { if (!_filling) _nameTouched = true; };
        Confirmed += Run;
        CloseRequested += Hide;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady() && Visible) UpdateButtons();
    }

    public void Open()
    {
        var p = App.Session.Project;
        _filling = true;
        Clip.Clear(); _clipIds.Clear();
        foreach (var c in p.Clips) { Clip.AddItem(c.Name); _clipIds.Add(c.Id.ToString()); }
        var ci = _clipIds.IndexOf(App.ActiveClipId ?? "");
        if (_clipIds.Count > 0) Clip.Select(Math.Max(0, ci));
        Source.Clear(); Target.Clear(); _charIds.Clear();
        foreach (var c in p.Characters) { Source.AddItem(c.Name); Target.AddItem(c.Name); _charIds.Add(c.Id.ToString()); }
        PickSource();
        // Default target: the first character that is not the source.
        var src = Source.Selected;
        var t = _charIds.FindIndex(id => id != (src >= 0 && src < _charIds.Count ? _charIds[src] : ""));
        if (_charIds.Count > 0) Target.Select(t >= 0 ? t : 0);
        FootLock.ButtonPressed = true;
        _nameTouched = false;
        _status.Text = ""; _details.Text = ""; LastError = null;
        _status.RemoveThemeColorOverride("font_color");
        _filling = false;
        NewName.PlaceholderText = Tr("Default: clip name and target character");
        SuggestName();
        UpdateButtons();
        PopupCentered(new Vector2I(600, 0));
    }

    /// <summary>The source character defaults to a character that uses the clip's skeleton.</summary>
    void PickSource()
    {
        if (Clip.Selected < 0 || Clip.Selected >= _clipIds.Count || _charIds.Count == 0) return;
        var p = App.Session.Project;
        var clip = p.Clips.Find(c => c.Id.ToString() == _clipIds[Clip.Selected]);
        if (clip is null) return;
        var match = clip.Skeleton.IsEmpty ? -1 : p.Characters.FindIndex(c => c.Rig == clip.Skeleton);
        var focus = _charIds.IndexOf(App.FocusCharacterId ?? "");
        var matching = p.Characters.Select((c, i) => (c, i)).Where(x => x.c.Rig == clip.Skeleton).Select(x => x.i).ToList();
        Source.Select(matching.Contains(focus) ? focus : match >= 0 ? match : Math.Max(0, focus));
    }

    void SuggestName()
    {
        if (_nameTouched || Clip.Selected < 0 || Target.Selected < 0) return;
        _filling = true;
        NewName.Text = $"{Clip.GetItemText(Clip.Selected)} ({Target.GetItemText(Target.Selected)})";
        _filling = false;
    }

    void UpdateButtons()
    {
        var ok = Clip.Selected >= 0 && Source.Selected >= 0 && Target.Selected >= 0;
        var same = ok && Source.Selected == Target.Selected;
        GetOkButton().Disabled = !ok || same;
        _hint.Text = _charIds.Count < 2 ? Tr("Add or import a second character to copy a clip onto it.") : same ? Tr("Choose a different character to copy onto.") : "";
    }

    public void Run()
    {
        if (GetOkButton().Disabled) return;
        var name = NewName.Text.Trim();
        var r = _host.RetargetNow(_clipIds[Clip.Selected], _charIds[Source.Selected], _charIds[Target.Selected], name.Length > 0 ? name : null, FootLock.ButtonPressed);
        LastResult = r;
        if (!r.Ok)
        {
            LastError = r.Error?.Message ?? "";
            _status.Text = _host.CodeMessage(r.Error?.Code) ?? r.Error?.Code switch
            {
                "not_humanoid" or "unmapped_target" or "unmapped_source" => Tr("One of the characters has no humanoid bone mapping. Map its bones first (Rigging > Bone Mapping)."),
                "rig_mismatch" => Tr("The clip animates a different character than the one chosen as “Plays now on”."),
                "too_large" => Tr("The clip is too long to copy at once."),
                _ => string.Format(CultureInfo.InvariantCulture, Tr("The clip could not be copied: {0}"), LastError),
            };
            _status.AddThemeColorOverride("font_color", _host.Palette.Err);
            _details.Text = "";
            return;
        }
        LastError = null;
        LastClipId = (string?)r.Data?["clipId"];
        var mapped = (int?)r.Data?["mappedBones"] ?? 0;
        var unmapped = (r.Data?["unmappedBones"] as JsonArray)?.Select(n => (string?)n ?? "").ToList() ?? new List<string>();
        _status.RemoveThemeColorOverride("font_color");
        _status.AddThemeColorOverride("font_color", _host.Palette.Ok);
        _status.Text = Tr("Done. The new clip is selected and playing.");
        var lines = new List<string>
        {
            string.Format(CultureInfo.InvariantCulture, Tr("{0} bones copied."), P8.Ltr(mapped.ToString(CultureInfo.InvariantCulture))),
        };
        if (unmapped.Count > 0) lines.Add(string.Format(CultureInfo.InvariantCulture, Tr("{0} bones have no match and stay at rest: {1}"), P8.Ltr(unmapped.Count.ToString(CultureInfo.InvariantCulture)), string.Join(", ", unmapped.Take(8)) + (unmapped.Count > 8 ? "…" : "")));
        if (r.Data?["footLock"] is JsonObject fl)
            lines.Add(string.Format(CultureInfo.InvariantCulture, Tr("Feet locked on {0} contacts."), P8.Ltr(((fl["spans"] as JsonArray)?.Count ?? 0).ToString(CultureInfo.InvariantCulture))));
        _details.Text = string.Join("\n", lines);
    }

    // ---- test hooks ----------------------------------------------------------------------------------------------------------------

    public void SelectClipById(string id) { var i = _clipIds.IndexOf(id); if (i >= 0) { Clip.Select(i); PickSource(); SuggestName(); UpdateButtons(); } }
    public void SelectTargetById(string id) { var i = _charIds.IndexOf(id); if (i >= 0) { Target.Select(i); SuggestName(); UpdateButtons(); } }
    public void SelectSourceById(string id) { var i = _charIds.IndexOf(id); if (i >= 0) { Source.Select(i); UpdateButtons(); } }
    public bool RunEnabled => !GetOkButton().Disabled;
}
