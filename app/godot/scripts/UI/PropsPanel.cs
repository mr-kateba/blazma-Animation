using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 8 Props panel: a floating card in the inline-end column. Lists the project's props, adds a box, sphere or cylinder (sized in
/// metres) or imports a .glb, and for the selected prop shows where it is attached, offers a bone picker with Attach / Detach, the
/// position and rotation (offset from the bone when attached), Key and Remove. Edits run the prop.* commands, so each is undoable; the
/// viewport gizmo edits the same value. Props are Simple-mode features.
/// </summary>
public partial class PropsPanel : PanelContainer
{
    readonly MainWindow _host;
    readonly ScrollContainer _scroll;
    readonly Control _scrollContent;
    float _lastAvail = -1, _lastNeed = -1;
    /// <summary>Height the History list below keeps (0 in Simple mode).</summary>
    public float ReserveBelow { get; set; }
    bool _dirty = true, _syncing;
    readonly List<string> _ids = new();
    readonly List<string> _boneIds = new();
    string _listSig = "", _lastRig = "", _lastBoneProp = "";

    public ItemList List { get; } = new()
    {
        AutoTranslateMode = AutoTranslateModeEnum.Disabled, CustomMinimumSize = new Vector2(0, 96), SelectMode = ItemList.SelectModeEnum.Single,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
    };
    public Button AddBox { get; } = new() { Text = "Box", FocusMode = Control.FocusModeEnum.None, TooltipText = "Add a box prop of the size below." };
    public Button AddSphere { get; } = new() { Text = "Sphere", FocusMode = Control.FocusModeEnum.None, TooltipText = "Add a sphere prop (its diameter is the first size)." };
    public Button AddCylinder { get; } = new() { Text = "Cylinder", FocusMode = Control.FocusModeEnum.None, TooltipText = "Add a cylinder prop (diameter = first size, length = second size)." };
    public Button ImportButton { get; } = new() { Text = "Import .glb…", FocusMode = Control.FocusModeEnum.None, TooltipText = "Add a prop from a .glb model file. The model is stored inside the project." };
    public LineEdit NameEdit { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Name of the new prop (optional). The name is also used in the exported file." };
    public SpinBox SizeX { get; } = P8.Spin(0.001, 100, 0.01, 0.1);
    public SpinBox SizeY { get; } = P8.Spin(0.001, 100, 0.01, 0.1);
    public SpinBox SizeZ { get; } = P8.Spin(0.001, 100, 0.01, 0.1);
    public OptionButton BonePick { get; } = P8.Option(0);
    public Button UseSelectedBone { get; } = new() { Text = "Use selected bone", FocusMode = Control.FocusModeEnum.None, TooltipText = "Pick the bone that is selected in the viewport." };
    public Button AttachButton { get; } = new() { Text = "Attach to bone", FocusMode = Control.FocusModeEnum.None, ThemeTypeVariation = "Primary" };
    public Button DetachButton { get; } = new() { Text = "Detach", FocusMode = Control.FocusModeEnum.None };
    public CheckBox KeepWorld { get; } = new() { Text = "Keep its place in the world", FocusMode = Control.FocusModeEnum.None, TooltipText = "Off: the prop jumps to the bone. On: it stays where it is and follows the bone from there." };
    public SpinBox PosX { get; } = P8.Spin(-100000, 100000, 0.01, 0, " m");
    public SpinBox PosY { get; } = P8.Spin(-100000, 100000, 0.01, 0, " m");
    public SpinBox PosZ { get; } = P8.Spin(-100000, 100000, 0.01, 0, " m");
    public SpinBox RotX { get; } = P8.Spin(-360, 360, 1, 0, "°");
    public SpinBox RotY { get; } = P8.Spin(-360, 360, 1, 0, "°");
    public SpinBox RotZ { get; } = P8.Spin(-360, 360, 1, 0, "°");
    public Button KeyButton { get; } = new() { Text = "Key prop", FocusMode = Control.FocusModeEnum.None, ThemeTypeVariation = "KeyBtn", TooltipText = "Record the prop's position and rotation as a key at this frame (K)." };
    public Button RemoveButton { get; } = new() { Text = "Remove prop", FocusMode = Control.FocusModeEnum.None };
    readonly Label _empty = W.Label("No props yet. Add a shape below, or import a model.", "Faint");
    readonly Label _name = W.Label("", "Heading", false);
    readonly Label _info = W.Label("", "Dim", false);
    readonly Label _attach = W.Label("", null, false);
    readonly Label _transformCaption = W.Label("", "Section", false);
    readonly VBoxContainer _selected = new();

    static AppController App => AppController.Instance;

    public string NameText => _name.Text;
    public string InfoText => _info.Text;
    public string AttachText => _attach.Text;
    public int Count => _ids.Count;
    public string? SelectedId => _host.Selection.PropId;
    public override void _Ready() { NameEdit.PlaceholderText = Tr("Name (optional)"); }
    public bool SelectedSectionVisible => _selected.Visible;
    public string TransformCaption => _transformCaption.Text;

    public PropsPanel(MainWindow host)
    {
        _host = host;
        ThemeTypeVariation = "Floating";
        CustomMinimumSize = new Vector2(260, 0);
        SizeFlagsVertical = SizeFlags.ShrinkBegin;
        LocalizeNumeralSystem = false;
        LayoutDirection = LayoutDirectionEnum.ApplicationLocale;
        Visible = false;

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 7);
        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        var margin = W.Margin(root, 12, 10, 12, 12);
        margin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _scrollContent = margin;
        _scroll.AddChild(margin);
        AddChild(_scroll);
        root.CustomMinimumSize = new Vector2(236, 0);

        var head = new HBoxContainer();
        head.AddChild(W.Label("Props", "Heading"));
        head.AddChild(W.Spacer());
        var close = new Button { Text = "✕", Flat = true, FocusMode = FocusModeEnum.None, TooltipText = "Close the Props panel" };
        close.Pressed += () => App.SetPropsPanel(false);
        head.AddChild(close);
        root.AddChild(head);

        P8.Wrap(_empty, 226);
        root.AddChild(_empty);
        root.AddChild(List);

        root.AddChild(W.Label("Add a prop", "Section"));
        NameEdit.TextDirection = Control.TextDirection.Auto;
        root.AddChild(NameEdit);
        var shapes = new HBoxContainer();
        shapes.AddThemeConstantOverride("separation", 4);
        foreach (var b in new[] { AddBox, AddSphere, AddCylinder }) { b.SizeFlagsHorizontal = SizeFlags.ExpandFill; shapes.AddChild(b); }
        root.AddChild(shapes);
        var size = new HBoxContainer();
        size.AddThemeConstantOverride("separation", 4);
        size.AddChild(W.Label("Size", "Dim"));
        foreach (var s in new[] { SizeX, SizeY, SizeZ }) { s.CustomMinimumSize = new Vector2(62, 0); size.AddChild(s); }
        foreach (var sz in new[] { SizeX, SizeY, SizeZ }) { sz.Step = 0.001; sz.CustomArrowStep = 0.01; }
        SizeX.TooltipText = "Width (box) or diameter (sphere, cylinder), in metres.";
        SizeY.TooltipText = "Height (box) or length (cylinder), in metres.";
        SizeZ.TooltipText = "Depth (box), in metres.";
        root.AddChild(size);
        ImportButton.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        root.AddChild(ImportButton);

        // ---- selected prop ----
        _selected.AddThemeConstantOverride("separation", 7);
        _selected.AddChild(new HSeparator());
        _name.TextDirection = TextDirection.Auto;
        P8.Wrap(_name, 226);
        _selected.AddChild(_name);
        P8.Wrap(_info, 226);
        _selected.AddChild(_info);
        P8.Wrap(_attach, 226);
        _selected.AddChild(_attach);
        _selected.AddChild(W.Label("Bone", "Dim"));
        _selected.AddChild(BonePick);
        _selected.AddChild(UseSelectedBone);
        _selected.AddChild(KeepWorld);
        var ad = new HBoxContainer();
        ad.AddThemeConstantOverride("separation", 6);
        AttachButton.SizeFlagsHorizontal = SizeFlags.ExpandFill; DetachButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ad.AddChild(AttachButton); ad.AddChild(DetachButton);
        _selected.AddChild(ad);

        _selected.AddChild(_transformCaption);
        var g = new GridContainer { Columns = 2 };
        g.AddThemeConstantOverride("h_separation", 8);
        g.AddThemeConstantOverride("v_separation", 4);
        void Field(string label, SpinBox box) { var l = W.Label(label, "Dim"); g.AddChild(l); box.CustomMinimumSize = new Vector2(120, 0); g.AddChild(box); }
        Field("Position X", PosX); Field("Position Y", PosY); Field("Position Z", PosZ);
        Field("Rotation X", RotX); Field("Rotation Y", RotY); Field("Rotation Z", RotZ);
        _selected.AddChild(g);
        var ar = new HBoxContainer();
        ar.AddThemeConstantOverride("separation", 6);
        KeyButton.SizeFlagsHorizontal = SizeFlags.ExpandFill; RemoveButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ar.AddChild(KeyButton); ar.AddChild(RemoveButton);
        _selected.AddChild(ar);
        root.AddChild(_selected);

        List.ItemSelected += i => { if (i >= 0 && i < _ids.Count) _host.Selection.SelectProp(_ids[(int)i]); };
        AddBox.Pressed += () => AddShape("box");
        AddSphere.Pressed += () => AddShape("sphere");
        AddCylinder.Pressed += () => AddShape("cylinder");
        ImportButton.Pressed += () => _host.PromptImportProp();
        UseSelectedBone.Pressed += PickSelectedBone;
        AttachButton.Pressed += () => { if (SelectedId is { } id && BonePick.Selected >= 0 && BonePick.Selected < _boneIds.Count) _host.AttachPropNow(id, _boneIds[BonePick.Selected], KeepWorld.ButtonPressed); };
        DetachButton.Pressed += () => { if (SelectedId is { } id) _host.DetachPropNow(id, true); };
        RemoveButton.Pressed += () => { if (SelectedId is { } id) _host.RemovePropNow(id); };
        KeyButton.Pressed += () => { if (SelectedId is { } id) App.KeyPropAtCurrent(id); };
        foreach (var s in new[] { PosX, PosY, PosZ, RotX, RotY, RotZ }) s.ValueChanged += _ => PushTransform();

        App.SettingsChanged += MarkDirty;
        App.PlaybackChanged += MarkDirty;
        App.SessionChanged += _ => MarkDirty();
        _host.Selection.Changed += MarkDirty;
    }

    public override void _ExitTree()
    {
        App.SettingsChanged -= MarkDirty;
        App.PlaybackChanged -= MarkDirty;
    }

    void MarkDirty() => _dirty = true;

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady()) { _dirty = true; NameEdit.PlaceholderText = Tr("Name (optional)"); }
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        if (_dirty) { _dirty = false; Refresh(); }
        var parentH = GetParentControl()?.Size.Y ?? 0;
        var other = _host.PhysicsPanelNode.Visible ? _host.PhysicsPanelNode.Size.Y + 10 : 0;
        var avail = Mathf.Max(parentH - 4 - ReserveBelow - other, 160);
        var need = _scrollContent.GetCombinedMinimumSize().Y;
        if (Mathf.IsEqualApprox(avail, _lastAvail) && Mathf.IsEqualApprox(need, _lastNeed)) return;
        _lastAvail = avail; _lastNeed = need;
        _scroll.CustomMinimumSize = new Vector2(0, Mathf.Min(need, avail));
    }

    public Blazma.Core.Mathematics.Vec3 SizeVector => new(SizeX.Value, SizeY.Value, SizeZ.Value);
    Blazma.Core.Mathematics.Vec3 SizeVec() => SizeVector;

    /// <summary>Show or hide with the setting; refreshes immediately when it opens.</summary>
    public void SyncVisible()
    {
        var open = App.Settings.PropsPanel;
        if (Visible != open) Visible = open;
        if (open) { _dirty = false; Refresh(); }
    }

    // ---- refresh ------------------------------------------------------------------------------------------------------------

    public void Refresh()
    {
        var project = App.Session.Project;
        _syncing = true;
        try
        {
            var selectedId = _host.Selection.PropId;
            var sig = string.Join("|", project.Props.Select(p => p.Id + p.Name + p.Size.X + p.Size.Y + p.Size.Z)) + "#" + selectedId + TranslationServer.GetLocale();
            if (sig != _listSig)
            {
                _listSig = sig;
                _ids.Clear();
                List.Clear();
                foreach (var p in project.Props)
                {
                    _ids.Add(p.Id.ToString());
                    var i = List.AddItem(p.Name);
                    List.SetItemTooltip(i, ShapeName(p));
                }
                var idx = selectedId is null ? -1 : _ids.IndexOf(selectedId);
                if (idx >= 0) List.Select(idx);
            }
            _empty.Visible = project.Props.Count == 0;
            List.Visible = project.Props.Count > 0;
            List.CustomMinimumSize = new Vector2(0, Math.Min(120, 28 + 24 * project.Props.Count));

            var prop = selectedId is null ? null : project.Props.Find(p => p.Id.ToString() == selectedId);
            _selected.Visible = prop is not null;
            if (prop is null) return;
            _name.Text = prop.Name;
            _info.Text = ShapeName(prop);
            var attached = prop.Attachment is not null;
            string? boneName = null;
            if (prop.Attachment is { } a)
            {
                var ch = project.FindCharacter(a.Character);
                var bone = ch is null ? null : project.FindRig(ch.Rig)?.FindBone(a.Bone);
                boneName = bone is null ? null : $"{bone.Name} ({ch!.Name})";
            }
            _attach.Text = attached ? string.Format(CultureInfo.InvariantCulture, Tr("Attached to {0}"), boneName ?? Tr("a bone that no longer exists")) : Tr("Not attached: it stays where you put it in the scene.");
            _attach.AddThemeColorOverride("font_color", attached ? _host.Palette.Ok : _host.Palette.Text2);
            _transformCaption.Text = attached ? Tr("Offset from the bone") : Tr("Position in the scene");
            AttachButton.Text = attached ? Tr("Move to this bone") : Tr("Attach to bone");
            DetachButton.Disabled = !attached;

            FillBones(prop);
            var t = prop.Local;
            var e = t.Rotation.ToEulerXYZ() * (180 / Math.PI);
            Set(PosX, t.Translation.X); Set(PosY, t.Translation.Y); Set(PosZ, t.Translation.Z);
            Set(RotX, e.X); Set(RotY, e.Y); Set(RotZ, e.Z);
            KeyButton.Disabled = false;
        }
        finally { _syncing = false; }
    }

    static void Set(SpinBox box, double v)
    {
        v = Math.Round(v, 4);
        if (!Mathf.IsEqualApprox((float)box.Value, (float)v) && !box.GetLineEdit().HasFocus()) box.SetValueNoSignal(v);
    }

    static string ShapeName(Prop p)
    {
        var s = p.Size;
        string N(double v) => P8.Num(v, "0.###");
        return p.Shape switch
        {
            PropShape.Box => string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("Box, {0} × {1} × {2} m"), P8.Ltr(N(s.X)), P8.Ltr(N(s.Y)), P8.Ltr(N(s.Z))),
            PropShape.Sphere => string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("Sphere, diameter {0} m"), P8.Ltr(N(s.X))),
            PropShape.Cylinder => string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("Cylinder, diameter {0} m, length {1} m"), P8.Ltr(N(s.X)), P8.Ltr(N(s.Y))),
            _ => string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("Model, about {0} × {1} × {2} m"), P8.Ltr(N(s.X)), P8.Ltr(N(s.Y)), P8.Ltr(N(s.Z))),
        };
    }

    /// <summary>The bone list of the selected character's rig; the attached bone, else the selected bone, else the right hand is chosen.</summary>
    void FillBones(Prop prop)
    {
        var character = prop.Attachment is { } a ? App.Session.Project.FindCharacter(a.Character) : null;
        character ??= _host.SelectedCharacter;
        var rig = character is null ? null : App.Session.Project.FindRig(character.Rig);
        var sig = rig is null ? "" : rig.Id + ":" + rig.Bones.Count;
        BonePick.Disabled = rig is null;
        AttachButton.Disabled = rig is null;
        UseSelectedBone.Disabled = rig is null;
        var propKey = prop.Id.ToString();
        if (sig == _lastRig && propKey == _lastBoneProp && BonePick.ItemCount > 0) return; // keep the user's choice while nothing changed
        _boneIds.Clear();
        var keep = BonePick.Selected >= 0 && BonePick.Selected < BonePick.ItemCount && _lastRig == sig && _lastBoneProp == propKey ? BonePick.Selected : -1;
        BonePick.Clear();
        _lastRig = sig;
        _lastBoneProp = propKey;
        if (rig is null) return;
        foreach (var b in rig.Bones) { BonePick.AddItem(b.Name); _boneIds.Add(b.Id.ToString()); }
        int pick = keep;
        if (pick < 0 && prop.Attachment is { } att) pick = _boneIds.IndexOf(att.Bone.ToString());
        if (pick < 0 && _host.Selection.BoneId is { } sb) pick = _boneIds.IndexOf(sb);
        if (pick < 0 && rig.FindByStandardName("hand_r") is { } hand) pick = _boneIds.IndexOf(hand.Id.ToString());
        BonePick.Select(Math.Max(0, pick));
    }

    void AddShape(string shape)
    {
        var name = NameEdit.Text.Trim();
        if (_host.AddPropShape(shape, SizeVec(), name.Length > 0 ? name : null) is not null) NameEdit.Text = "";
    }

    void PickSelectedBone()
    {
        if (_host.Selection.BoneId is { } b && _boneIds.IndexOf(b) is var i and >= 0) BonePick.Select(i);
    }

    void PushTransform()
    {
        if (_syncing || SelectedId is not { } id) return;
        _host.SetPropTransformNow(id, PosX.Value, PosY.Value, PosZ.Value, RotX.Value, RotY.Value, RotZ.Value);
    }

    // ---- test hooks -----------------------------------------------------------------------------------------------------------

    public void SelectBoneByName(string name)
    {
        var i = Enumerable.Range(0, BonePick.ItemCount).FirstOrDefault(k => BonePick.GetItemText(k) == name, -1);
        if (i >= 0) BonePick.Select(i);
    }

    public string BonePickText => BonePick.Selected >= 0 ? BonePick.GetItemText(BonePick.Selected) : "";
    public void PressAttach() => P8.Press(AttachButton);
    public void PressDetach() => P8.Press(DetachButton);
    public void PressRemove() => P8.Press(RemoveButton);
    public void PressKey() => P8.Press(KeyButton);
}
