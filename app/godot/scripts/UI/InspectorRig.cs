using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Rigging;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>A tiny coloured axis letter plus a numeric field that commits on Enter or focus loss.</summary>
public partial class AxisField : HBoxContainer
{
    public LineEdit Edit { get; } = new() { ThemeTypeVariation = "MonoEdit", SelectAllOnFocus = true, CustomMinimumSize = new Vector2(0, 0) };
    public event Action? Committed;
    public bool Dirty { get; private set; }
    readonly Label _tag;
    readonly string _format;

    public AxisField(string axis, Color color, string format)
    {
        _format = format;
        AddThemeConstantOverride("separation", 4);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _tag = W.Label(axis, "MonoLabel", false);
        _tag.AddThemeColorOverride("font_color", color);
        AddChild(_tag);
        Edit.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        Edit.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        Edit.LocalizeNumeralSystem = false;
        Edit.TextDirection = Control.TextDirection.Ltr;
        Edit.TextChanged += _ => Dirty = true;
        Edit.TextSubmitted += _ => Commit();
        Edit.FocusExited += Commit;
        AddChild(Edit);
    }

    public void SetTagColor(Color c) => _tag.AddThemeColorOverride("font_color", c);
    public bool Editing => Dirty && Edit.HasFocus();

    public void SetValue(double v)
    {
        if (Dirty && Edit.HasFocus()) return;
        Dirty = false;
        if (Math.Abs(v) < 0.5 * Math.Pow(10, -(_format.Length - 2))) v = 0;
        Edit.Text = v.ToString(_format, CultureInfo.InvariantCulture);
    }

    public double? Value => double.TryParse(Edit.Text.Replace(',', '.').Replace('−', '-').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    void Commit()
    {
        if (!Dirty) return;
        Dirty = false;
        Committed?.Invoke();
    }

    public void Revert() { Dirty = false; }
}

public partial class InspectorPanel
{
    // character
    readonly VBoxContainer _charBox = new();
    readonly OptionButton _charPick = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill, AutoTranslateMode = AutoTranslateModeEnum.Disabled };
    readonly Label _charName = W.Label("", "Heading", false);
    readonly Label _rigName = W.Label("", "MonoLabel", false);
    readonly Label _boneCount = W.Label("", "MonoLabel", false);
    readonly Label _loadError = W.Label("", "ErrLabel", false);
    readonly Label _noChar = W.Label("This project has no character yet. Use Rigging > Add Mannequin.", "Faint");
    // bone
    readonly VBoxContainer _boneBox = new();
    readonly Label _boneName = W.Label("", "Heading", false);
    readonly Label _boneStd = W.Label("", "MonoFaint", false);
    AxisField _rx = null!, _ry = null!, _rz = null!;
    readonly VBoxContainer _transBox = new();
    AxisField _tx = null!, _ty = null!, _tz = null!;
    readonly VBoxContainer _ikBox = new();
    readonly CheckButton _ikPin = new() { Text = "IK pinned" };
    readonly HBoxContainer _drivenRow = new();
    readonly Label _drivenLabel = W.Label("", "Dim", false);
    readonly Button _unpin = new() { Text = "Unpin" };
    readonly Label _limits = W.Label("", "Faint", false);
    // control
    readonly VBoxContainer _ctrlBox = new();
    readonly Label _ctrlName = W.Label("", "Heading", false);
    readonly Label _ctrlKind = W.Label("", "MonoFaint", false);
    AxisField _cx = null!, _cy = null!, _cz = null!;
    readonly CheckButton _ctrlPin = new() { Text = "Pinned" };
    readonly Label _ctrlNote = W.Label("", "Faint", false);
    readonly Label _noSel = W.Label("Click a joint in the viewport, or pick a bone in the Rig panel.", "Faint");
    readonly HSeparator _charSep = new();
    bool _rigBusy;
    // The selection a half-typed value belongs to: committing after the selection changed must not hit the new item.
    string? _eChar, _eBone, _eControl;
    bool _eCaptured;
    string[] _charIds = Array.Empty<string>();

    SelectionState Sel => _host.Selection;

    void BuildRigSections(VBoxContainer root)
    {
        // Character --------------------------------------------------------------------------------
        _charBox.AddThemeConstantOverride("separation", 6);
        _charBox.AddChild(W.Label("Character", "Section"));
        _charPick.ItemSelected += i =>
        {
            if (_rigBusy || i < 0 || i >= _charIds.Length) return;
            Sel.SelectCharacter(_charIds[i]);
        };
        _charBox.AddChild(_charPick);
        _charBox.AddChild(_charName);
        var g = Grid();
        g.AddChild(W.Label("Rig", "Dim")); g.AddChild(_rigName);
        g.AddChild(W.Label("Bones", "Dim")); g.AddChild(_boneCount);
        _charBox.AddChild(g);
        _loadError.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _loadError.CustomMinimumSize = new Vector2(200, 0);
        _charBox.AddChild(_loadError);
        _rigName.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _rigName.ClipText = true;
        _rigName.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _rigName.TextDirection = Control.TextDirection.Auto;
        _charName.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _charName.ClipText = true;
        root.AddChild(_charBox);
        _noChar.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _noChar.CustomMinimumSize = new Vector2(200, 0);
        root.AddChild(_noChar);

        // Selected bone ------------------------------------------------------------------------------
        _boneBox.AddThemeConstantOverride("separation", 8);
        var head = new VBoxContainer();
        head.AddThemeConstantOverride("separation", 0);
        head.AddChild(W.Label("Selected bone", "Section"));
        head.AddChild(_boneName);
        head.AddChild(_boneStd);
        _boneName.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _boneName.ClipText = true;
        _boneName.TextDirection = Control.TextDirection.Ltr;
        _boneStd.TextDirection = Control.TextDirection.Ltr;
        _boneBox.AddChild(head);
        _boneBox.AddChild(W.Label("Rotation (degrees, local)", "Dim"));
        _boneBox.AddChild(Triple(out _rx, out _ry, out _rz, "0.0"));
        _rx.Committed += CommitBoneRotation; _ry.Committed += CommitBoneRotation; _rz.Committed += CommitBoneRotation;
        _transBox.AddThemeConstantOverride("separation", 8);
        _transBox.AddChild(W.Label("Position (metres, local)", "Dim"));
        _transBox.AddChild(Triple(out _tx, out _ty, out _tz, "0.000"));
        _tx.Committed += CommitBoneTranslation; _ty.Committed += CommitBoneTranslation; _tz.Committed += CommitBoneTranslation;
        _boneBox.AddChild(_transBox);

        _ikBox.AddThemeConstantOverride("separation", 6);
        _ikPin.FocusMode = FocusModeEnum.None;
        _ikPin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _ikPin.Toggled += on =>
        {
            if (_rigBusy) return;
            _host.SetPinnedFor(Sel.BoneId, Sel.ControlId, on);
        };
        _ikBox.AddChild(_ikPin);
        _drivenRow.AddThemeConstantOverride("separation", 8);
        _drivenLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _drivenLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _drivenLabel.CustomMinimumSize = new Vector2(120, 0);
        _drivenRow.AddChild(_drivenLabel);
        _unpin.FocusMode = FocusModeEnum.None;
        _unpin.Pressed += () => _host.UnpinDriver(Sel.BoneId);
        _drivenRow.AddChild(_unpin);
        _ikBox.AddChild(_drivenRow);
        _boneBox.AddChild(_ikBox);
        _limits.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _limits.CustomMinimumSize = new Vector2(200, 0);
        _boneBox.AddChild(_limits);
        root.AddChild(_boneBox);

        // Selected control ----------------------------------------------------------------------------
        _ctrlBox.AddThemeConstantOverride("separation", 8);
        var ch = new VBoxContainer();
        ch.AddThemeConstantOverride("separation", 0);
        ch.AddChild(W.Label("Selected control", "Section"));
        ch.AddChild(_ctrlName);
        ch.AddChild(_ctrlKind);
        _ctrlName.TextDirection = Control.TextDirection.Ltr;
        _ctrlBox.AddChild(ch);
        _ctrlBox.AddChild(W.Label("Position (metres, model space)", "Dim"));
        _ctrlBox.AddChild(Triple(out _cx, out _cy, out _cz, "0.000"));
        _cx.Committed += CommitControlPosition; _cy.Committed += CommitControlPosition; _cz.Committed += CommitControlPosition;
        _ctrlPin.FocusMode = FocusModeEnum.None;
        _ctrlPin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _ctrlPin.Toggled += on => { if (!_rigBusy) _host.SetPinnedFor(null, Sel.ControlId, on); };
        _ctrlBox.AddChild(_ctrlPin);
        _ctrlNote.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _ctrlNote.CustomMinimumSize = new Vector2(200, 0);
        _ctrlBox.AddChild(_ctrlNote);
        root.AddChild(_ctrlBox);

        _noSel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _noSel.CustomMinimumSize = new Vector2(200, 0);
        root.AddChild(_noSel);
        root.AddChild(_charSep);
    }

    void CaptureEditTarget()
    {
        if (_eCaptured) return;
        _eCaptured = true;
        _eChar = Sel.CharacterId; _eBone = Sel.BoneId; _eControl = Sel.ControlId;
    }

    HBoxContainer Triple(out AxisField x, out AxisField y, out AxisField z, string format)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 6);
        var pal = _host.Palette;
        x = new AxisField("X", pal.X, format);
        y = new AxisField("Y", pal.Y, format);
        z = new AxisField("Z", pal.Z, format);
        foreach (var f in new[] { x, y, z }) f.Edit.TextChanged += _ => CaptureEditTarget();
        row.AddChild(x); row.AddChild(y); row.AddChild(z);
        return row;
    }

    public void ApplyPaletteToFields(Palette p)
    {
        _rx.SetTagColor(p.X); _ry.SetTagColor(p.Y); _rz.SetTagColor(p.Z);
        _tx.SetTagColor(p.X); _ty.SetTagColor(p.Y); _tz.SetTagColor(p.Z);
        _cx.SetTagColor(p.X); _cy.SetTagColor(p.Y); _cz.SetTagColor(p.Z);
    }

    // ---- display ------------------------------------------------------------------------------------------------

    Character? SelChar => Sel.CharacterId is null ? null : AppController.Instance.Session.Project.Characters.Find(c => c.Id.ToString() == Sel.CharacterId);

    /// <summary>Refresh the character / bone / control sections from the current selection and pose.</summary>
    public void RefreshRig(bool force = false)
    {
        _rigBusy = true;
        try
        {
            var project = AppController.Instance.Session.Project;
            var character = SelChar;
            var rig = character is null ? null : project.FindRig(character.Rig);
            var has = character is not null && rig is not null;
            _charBox.Visible = has;
            _noChar.Visible = !has;
            _charSep.Visible = has;

            // Character picker (only when there is a choice).
            _charIds = project.Characters.Select(c => c.Id.ToString()).ToArray();
            _charPick.Visible = project.Characters.Count > 1;
            if (_charPick.ItemCount != _charIds.Length || force || !Enumerable.Range(0, _charIds.Length).All(i => _charPick.GetItemText(i) == project.Characters[i].Name))
            {
                _charPick.Clear();
                foreach (var c in project.Characters) _charPick.AddItem(c.Name);
            }
            if (has) _charPick.Select(Array.IndexOf(_charIds, character!.Id.ToString()));
            _charName.Visible = project.Characters.Count <= 1;

            if (!has)
            {
                _boneBox.Visible = _ctrlBox.Visible = _noSel.Visible = false;
                return;
            }
            _charName.Text = character!.Name;
            _rigName.Text = rig!.Name;
            _boneCount.Text = rig.Bones.Count.ToString(CultureInfo.InvariantCulture);
            var loadError = _host.Characters.View(character.Id.ToString())?.LoadError;
            _loadError.Visible = loadError is not null;
            if (loadError is not null) _loadError.Text = string.Format(Tr("The 3D model could not be shown: {0}"), Tr(loadError));

            var bone = Sel.BoneId is null ? null : rig.Bones.Find(b => b.Id.ToString() == Sel.BoneId);
            var control = Sel.ControlId is null ? null : rig.Controls.Find(c => c.Id.ToString() == Sel.ControlId);
            _boneBox.Visible = bone is not null;
            _ctrlBox.Visible = control is not null;
            _noSel.Visible = bone is null && control is null;

            var ev = new RigEvaluator(rig);
            if (bone is not null) FillBone(character, rig, bone, ev);
            if (control is not null) FillControl(character, rig, control, ev);
        }
        finally { _rigBusy = false; }
    }

    void FillBone(Character character, Rig rig, Bone bone, RigEvaluator ev)
    {
        var local = character.Pose.LocalOf(bone);
        var e = local.Rotation.ToEulerXYZ() * (180 / Math.PI);
        _boneName.Text = bone.Name;
        _boneStd.Text = rig.StandardNameOf(bone.Id) is { } std ? std : Tr("Not mapped to a standard bone");
        _boneStd.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        _rx.SetValue(e.X); _ry.SetValue(e.Y); _rz.SetValue(e.Z);

        var rootLike = Gizmo.IsRootLike(rig, ev.IndexOf(bone.Id));
        _transBox.Visible = rootLike;
        if (rootLike) { _tx.SetValue(local.Translation.X); _ty.SetValue(local.Translation.Y); _tz.SetValue(local.Translation.Z); }

        // IK state.
        var chainAsEnd = rig.IkChains.Find(c => c.End == bone.Id);
        var driver = PoseSolver.DrivingChain(rig, character.Pose, bone.Id);
        _ikBox.Visible = chainAsEnd is not null || driver is not null;
        _ikPin.Visible = chainAsEnd is not null;
        if (chainAsEnd is not null)
            _ikPin.SetPressedNoSignal(PoseSolver.StateOf(rig, character.Pose, chainAsEnd.Target).Pinned);
        _drivenRow.Visible = driver is not null;
        if (driver is not null)
            _drivenLabel.Text = string.Format(Tr("Driven by IK chain “{0}”"), driver.Name);
        var fk = driver is null;
        foreach (var f in new[] { _rx, _ry, _rz }) f.Edit.Editable = fk;

        _limits.Visible = bone.Limits is not null;
        if (bone.Limits is { } lim) _limits.Text = DescribeLimits(lim);
    }

    string DescribeLimits(JointLimits lim)
    {
        if (lim.Hinge is { } h)
        {
            var a = h.Axis.Normalized();
            var axis = Math.Abs(a.X) > 0.99 ? "X" : Math.Abs(a.Y) > 0.99 ? "Y" : Math.Abs(a.Z) > 0.99 ? "Z" : "?";
            return string.Format(CultureInfo.InvariantCulture, Tr("Joint limit: hinge about {0}, {1}° to {2}°"), axis, Math.Round(h.MinDegrees), Math.Round(h.MaxDegrees));
        }
        return string.Format(CultureInfo.InvariantCulture, Tr("Joint limits: X {0}° to {1}°, Y {2}° to {3}°, Z {4}° to {5}°"),
            Math.Round(lim.MinDegrees.X), Math.Round(lim.MaxDegrees.X), Math.Round(lim.MinDegrees.Y), Math.Round(lim.MaxDegrees.Y),
            Math.Round(lim.MinDegrees.Z), Math.Round(lim.MaxDegrees.Z));
    }

    void FillControl(Character character, Rig rig, RigControl control, RigEvaluator ev)
    {
        var model = ev.Model(character.Pose);
        var pos = PoseSolver.ControlModel(rig, character.Pose, control, model).Translation;
        _ctrlName.Text = control.Name;
        _ctrlKind.Text = control.Kind == ControlKind.IkTarget ? Tr("IK target") : Tr("Bend direction (pole)");
        _ctrlKind.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        _cx.SetValue(pos.X); _cy.SetValue(pos.Y); _cz.SetValue(pos.Z);
        var isTarget = control.Kind == ControlKind.IkTarget;
        _ctrlPin.Visible = isTarget;
        if (isTarget) _ctrlPin.SetPressedNoSignal(PoseSolver.StateOf(rig, character.Pose, control.Id).Pinned);
        _ctrlNote.Text = isTarget ? Tr("A pinned hand or foot stays where it is when the body moves.") : Tr("Moves the knee or elbow towards this point.");
        _ctrlNote.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
    }

    // ---- edits ----------------------------------------------------------------------------------------------------

    Character? CharById(string? id) => id is null ? null : AppController.Instance.Session.Project.Characters.Find(c => c.Id.ToString() == id);

    void CommitBoneRotation()
    {
        var c = CharById(_eCaptured ? _eChar : Sel.CharacterId); var id = _eCaptured ? _eBone : Sel.BoneId;
        _eCaptured = false;
        if (c is null || id is null || _rx.Value is not { } x || _ry.Value is not { } y || _rz.Value is not { } z) { RefreshRig(true); return; }
        var r = AppController.Instance.Execute("pose.set_bone", new JsonObject
        {
            ["characterId"] = c.Id.ToString(), ["bone"] = id, ["rx"] = x, ["ry"] = y, ["rz"] = z,
        });
        if (r.Ok && r.Warnings.Any(w => w.Code == "clamped_by_limits")) _host.ToastNode.Notify("The value was limited to the joint range.", _host.Palette.Warn);
        RefreshRig(true);
    }

    void CommitBoneTranslation()
    {
        var c = CharById(_eCaptured ? _eChar : Sel.CharacterId); var id = _eCaptured ? _eBone : Sel.BoneId;
        _eCaptured = false;
        if (c is null || id is null || _rx.Value is not { } rx || _ry.Value is not { } ry || _rz.Value is not { } rz
            || _tx.Value is not { } x || _ty.Value is not { } y || _tz.Value is not { } z) { RefreshRig(true); return; }
        AppController.Instance.Execute("pose.set_bone", new JsonObject
        {
            ["characterId"] = c.Id.ToString(), ["bone"] = id, ["rx"] = rx, ["ry"] = ry, ["rz"] = rz,
            ["tx"] = x, ["ty"] = y, ["tz"] = z,
        });
        RefreshRig(true);
    }

    void CommitControlPosition()
    {
        var c = CharById(_eCaptured ? _eChar : Sel.CharacterId); var id = _eCaptured ? _eControl : Sel.ControlId;
        _eCaptured = false;
        if (c is null || id is null || _cx.Value is not { } x || _cy.Value is not { } y || _cz.Value is not { } z) { RefreshRig(true); return; }
        AppController.Instance.Execute("pose.set_control", new JsonObject
        {
            ["characterId"] = c.Id.ToString(), ["control"] = id, ["x"] = x, ["y"] = y, ["z"] = z,
        });
        RefreshRig(true);
    }

    // Test hooks: set a field the way typing + Enter would.
    public void TypeRotation(double x, double y, double z)
    {
        _rx.Edit.Text = x.ToString(CultureInfo.InvariantCulture); _ry.Edit.Text = y.ToString(CultureInfo.InvariantCulture); _rz.Edit.Text = z.ToString(CultureInfo.InvariantCulture);
        CommitBoneRotation();
    }
    public void TypeBonePosition(double x, double y, double z)
    {
        _tx.Edit.Text = x.ToString(CultureInfo.InvariantCulture); _ty.Edit.Text = y.ToString(CultureInfo.InvariantCulture); _tz.Edit.Text = z.ToString(CultureInfo.InvariantCulture);
        CommitBoneTranslation();
    }
    public void TypeControlPosition(double x, double y, double z)
    {
        _cx.Edit.Text = x.ToString(CultureInfo.InvariantCulture); _cy.Edit.Text = y.ToString(CultureInfo.InvariantCulture); _cz.Edit.Text = z.ToString(CultureInfo.InvariantCulture);
        CommitControlPosition();
    }
    public string RotationText => $"{_rx.Edit.Text} {_ry.Edit.Text} {_rz.Edit.Text}";
    public bool BoneSectionVisible => _boneBox.Visible;
    public bool ControlSectionVisible => _ctrlBox.Visible;
}
