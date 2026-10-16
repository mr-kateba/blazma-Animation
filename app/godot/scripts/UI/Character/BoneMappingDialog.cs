using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Rigging;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Shows how well an imported rig maps onto the Blazma humanoid and lets the user correct it row by row.
/// Every change is a rig.map_bone command; all edits made while the dialog is open form ONE transaction
/// (Done commits it, Cancel rolls it back).
/// </summary>
public partial class BoneMappingDialog : ConfirmationDialog
{
    readonly MainWindow _host;
    TransactionScope? _tx;
    string _rigId = "";
    bool _busy, _finished;

    readonly Label _coverage = W.Label("", null, false);
    readonly ProgressBar _bar = new() { MinValue = 0, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(180, 8), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
    readonly PanelContainer _badge = new();
    readonly Label _badgeText = W.Label("", "MonoLabel", false);
    readonly Label _missing = W.Label("", null, false);
    readonly VBoxContainer _issues = new();
    readonly GridContainer _grid = new() { Columns = 3 };
    readonly Dictionary<string, (Label name, Label tag, OptionButton pick)> _rows = new();
    readonly Label _builtin = W.Label("The built-in mannequin is already fully mapped.", "Faint");
    List<string> _boneIds = new();
    JsonObject? _report;

    static AppController App => AppController.Instance;

    public bool Usable => (bool?)_report?["usable"] ?? false;
    public double Coverage => (double?)_report?["coverage"] ?? 0;
    public IReadOnlyList<string> MissingRequired => ((JsonArray?)_report?["missingRequired"])?.Select(n => (string)n!).ToList() ?? new List<string>();
    public IReadOnlyList<string> IssueCodes => ((JsonArray?)_report?["issues"])?.Select(n => (string)n!["code"]!).ToList() ?? new List<string>();
    public bool IsOpen => Visible;
    public string RigId => _rigId;
    public int RowCount => _rows.Count;

    public BoneMappingDialog(MainWindow host)
    {
        _host = host;
        Title = "Bone Mapping";
        OkButtonText = "Done";
        CancelButtonText = "Cancel";
        Exclusive = true;
        Unresizable = false;
        MinSize = new Vector2I(660, 560);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        AddChild(root);

        var intro = W.Label("Match your character's bones to the standard humanoid. Posing tools, mirroring and IK need every required bone.", "Dim");
        intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        intro.CustomMinimumSize = new Vector2(560, 0);
        root.AddChild(intro);

        var head = new HBoxContainer { LayoutDirection = Control.LayoutDirectionEnum.ApplicationLocale };
        head.AddThemeConstantOverride("separation", 12);
        head.AddChild(_coverage);
        head.AddChild(_bar);
        head.AddChild(W.Spacer());
        _badge.AddChild(_badgeText);
        head.AddChild(_badge);
        root.AddChild(head);

        _missing.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _missing.CustomMinimumSize = new Vector2(560, 0);
        root.AddChild(_missing);
        _issues.AddThemeConstantOverride("separation", 2);
        root.AddChild(_issues);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 280) };
        _grid.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _grid.AddThemeConstantOverride("h_separation", 14);
        _grid.AddThemeConstantOverride("v_separation", 6);
        scroll.AddChild(_grid);
        root.AddChild(scroll);
        root.AddChild(_builtin);
        _builtin.Visible = false;

        Confirmed += () => Finish(true);
        Canceled += () => Finish(false);
        CloseRequested += () => Finish(false);
        BuildRows();
    }

    void BuildRows()
    {
        _grid.AddChild(W.Label("Standard bone", "Section"));
        _grid.AddChild(W.Label("Status", "Section"));
        _grid.AddChild(W.Label("Your bone", "Section"));
        foreach (var (name, _) in HumanoidStandard.Bones)
        {
            var label = W.Label(name, "MonoLabel", false);
            label.TextDirection = Control.TextDirection.Ltr;
            label.CustomMinimumSize = new Vector2(120, 0);
            var tag = W.Label("", "MonoFaint", false);
            var pick = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, AutoTranslateMode = AutoTranslateModeEnum.Disabled, FitToLongestItem = false, CustomMinimumSize = new Vector2(230, 0) };
            var std = name;
            pick.ItemSelected += i => OnRowChanged(std, (int)i);
            _grid.AddChild(label);
            _grid.AddChild(tag);
            _grid.AddChild(pick);
            _rows[name] = (label, tag, pick);
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady() && Visible && _rigId.Length > 0) Rebuild();
    }

    // ---- open / close ---------------------------------------------------------------------------------------------------

    public void Open(string rigId)
    {
        _rigId = rigId;
        _finished = false;
        _tx = null;
        var rig = App.Session.Project.FindRig(ObjectIdOf(rigId));
        var builtin = rig is null || rig.Source.Kind == RigSourceKind.Builtin;
        if (!builtin)
        {
            try { _tx = App.BeginTransaction("Map bones"); }
            catch (InvalidOperationException) { _tx = null; }
        }
        Rebuild();
        Title = Tr("Bone Mapping");
        OkButtonText = Tr("Done");
        CancelButtonText = Tr("Cancel");
        PopupCentered(new Vector2I(700, 640));
    }

    static Blazma.Core.Ids.ObjectId ObjectIdOf(string id) => Blazma.Core.Ids.ObjectId.TryParse(id, out var o) ? o : default;

    void Finish(bool commit)
    {
        if (_finished) return;
        _finished = true;
        var tx = _tx;
        _tx = null;
        if (tx is not null) { if (commit) tx.Commit(); else tx.Rollback(); }
        Hide();
        _host.AfterTransaction();
    }

    /// <summary>Programmatic OK / Cancel (used by tests and menu flows).</summary>
    public void Close(bool commit) => Finish(commit);

    // ---- content ------------------------------------------------------------------------------------------------------------

    public void Rebuild()
    {
        var rig = App.Session.Project.FindRig(ObjectIdOf(_rigId));
        if (rig is null) { Finish(false); return; }
        _busy = true;
        try
        {
            var builtin = rig.Source.Kind == RigSourceKind.Builtin;
            _builtin.Visible = builtin;
            var r = App.Execute("rig.mapping_report", new JsonObject { ["rigId"] = _rigId });
            _report = r.Data as JsonObject;
            var pal = _host.Palette;

            var coverage = Coverage;
            _coverage.Text = string.Format(CultureInfo.InvariantCulture, Tr("Coverage {0}%"), Math.Round(coverage * 100));
            _bar.Value = coverage * 100;
            var usable = Usable;
            _badgeText.Text = usable ? Tr("Ready to pose") : Tr("Not usable yet");
            _badgeText.AddThemeColorOverride("font_color", usable ? pal.Ok : pal.Err);
            _badge.AddThemeStyleboxOverride("panel", BlazmaTheme.Box((usable ? pal.Ok : pal.Err) with { A = 0.14f }, usable ? pal.Ok : pal.Err, 10, 1, 10, 3, 10, 3));

            var missing = MissingRequired;
            _missing.Visible = missing.Count > 0;
            _missing.Text = string.Format(Tr("Required bones still missing: {0}"), string.Join(", ", missing));
            _missing.AddThemeColorOverride("font_color", pal.Err);

            foreach (var c in _issues.GetChildren()) { _issues.RemoveChild(c); c.QueueFree(); }
            foreach (var issue in (JsonArray?)_report?["issues"] ?? new JsonArray())
            {
                var code = (string)issue!["code"]!;
                var bone = (string?)issue["bone"];
                var l = W.Label("• " + DescribeIssue(code, bone, (string?)issue["message"] ?? code), "Dim", false);
                l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                l.CustomMinimumSize = new Vector2(560, 0);
                l.AddThemeColorOverride("font_color", pal.Warn);
                _issues.AddChild(l);
            }

            _boneIds = rig.Bones.Select(b => b.Id.ToString()).ToList();
            foreach (var (std, (name, tag, pick)) in _rows)
            {
                pick.Clear();
                pick.AddItem(Tr("— none —"));
                foreach (var b in rig.Bones) pick.AddItem(b.Name);
                var mapped = rig.FindByStandardName(std);
                pick.Select(mapped is null ? 0 : 1 + _boneIds.IndexOf(mapped.Id.ToString()));
                pick.Disabled = builtin;
                var required = HumanoidStandard.Required.Contains(std);
                var isMissing = mapped is null && required;
                tag.Text = isMissing ? Tr("required, missing") : required ? Tr("required") : mapped is null ? Tr("optional") : "";
                tag.AddThemeColorOverride("font_color", isMissing ? pal.Err : pal.Text3);
                name.AddThemeColorOverride("font_color", isMissing ? pal.Err : pal.Text);
            }
        }
        finally { _busy = false; }
    }

    string DescribeIssue(string code, string? bone, string fallback)
    {
        var text = code switch
        {
            "hierarchy_violation" => Tr("A mapped bone is not below its expected parent in the skeleton."),
            "spine_path_not_found" => Tr("No clear spine path was found between the pelvis and the head."),
            "spine_extra_bones_unmapped" => Tr("Some spine bones are not mapped; the spine is simplified."),
            "faces_negative_z" => Tr("The character seems to face −Z; Blazma expects +Z as forward."),
            "side_mismatch" => Tr("A left bone sits on the right side of the body, or the other way round."),
            _ => fallback,
        };
        return bone is null ? text : text + "  (" + bone + ")";
    }

    void OnRowChanged(string standard, int index)
    {
        if (_busy) return;
        AssignInternal(standard, index == 0 ? null : _boneIds[index - 1]);
    }

    /// <summary>The code path used by the dropdowns: map a rig bone (id or name) to a standard bone; null unmaps the standard bone.</summary>
    public bool Assign(string standard, string? boneIdOrName)
    {
        var rig = App.Session.Project.FindRig(ObjectIdOf(_rigId));
        if (rig is null) return false;
        string? id = null;
        if (boneIdOrName is not null)
        {
            var b = rig.FindBoneByName(boneIdOrName) ?? rig.Bones.Find(x => x.Id.ToString() == boneIdOrName);
            if (b is null) return false;
            id = b.Id.ToString();
        }
        return AssignInternal(standard, id);
    }

    bool AssignInternal(string standard, string? boneId)
    {
        var rig = App.Session.Project.FindRig(ObjectIdOf(_rigId));
        if (rig is null) return false;
        JsonObject args;
        if (boneId is null)
        {
            var current = rig.FindByStandardName(standard);
            if (current is null) { Rebuild(); return true; }
            args = new JsonObject { ["rigId"] = _rigId, ["bone"] = current.Id.ToString() };
        }
        else args = new JsonObject { ["rigId"] = _rigId, ["bone"] = boneId, ["standard"] = standard };
        var res = App.Execute("rig.map_bone", args);
        Rebuild();
        return res.Ok;
    }
}
