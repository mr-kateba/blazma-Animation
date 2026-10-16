using Blazma.Core.Model;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Advanced mode, left: tabs Project | Rig | Poses sharing one card.</summary>
public partial class LeftDock : PanelContainer
{
    public Segmented Tabs { get; }
    public RigTreePanel RigTree { get; }
    public PosesPanel Poses { get; }
    readonly Control[] _pages;

    public LeftDock(MainWindow host, Control projectPage)
    {
        ThemeTypeVariation = "Card";
        CustomMinimumSize = new Vector2(290, 0);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 8);
        Tabs = new Segmented("Project", "Rig", "Poses") { SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
        v.AddChild(Tabs);
        RigTree = new RigTreePanel(host);
        Poses = new PosesPanel(host);
        _pages = new[] { projectPage, RigTree, Poses };
        foreach (var p in _pages) { p.SizeFlagsVertical = SizeFlags.ExpandFill; v.AddChild(p); }
        AddChild(W.Margin(v, 10, 10, 8, 10));
        Tabs.Selected += Show;
        Show(0);
    }

    public int Page { get; private set; }

    public void Show(int i)
    {
        Page = i;
        for (var k = 0; k < _pages.Length; k++) _pages[k].Visible = k == i;
        Tabs.SetIndex(i);
    }
}

/// <summary>Bone hierarchy of the selected character's rig with the standard name in a second column; synced with the viewport selection.</summary>
public partial class RigTreePanel : VBoxContainer
{
    readonly MainWindow _host;
    public Tree Tree { get; } = new();
    bool _busy;
    string? _builtFor;
    long _builtVersion = -1;

    static AppController App => AppController.Instance;

    public RigTreePanel(MainWindow host)
    {
        _host = host;
        AddThemeConstantOverride("separation", 6);
        Tree.Columns = 2;
        Tree.ColumnTitlesVisible = true;
        Tree.SizeFlagsVertical = SizeFlags.ExpandFill;
        Tree.HideRoot = true;
        Tree.SelectMode = Godot.Tree.SelectModeEnum.Single;
        Tree.SetColumnExpand(0, true);
        Tree.SetColumnExpand(1, true);
        Tree.SetColumnExpandRatio(0, 3);
        Tree.SetColumnExpandRatio(1, 2);
        Tree.AddThemeConstantOverride("item_margin", 8);
        Tree.SetColumnCustomMinimumWidth(0, 120);
        Tree.SetColumnCustomMinimumWidth(1, 84);
        Tree.ItemSelected += OnSelected;
        AddChild(Tree);
        SetTitles();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged) SetTitles();
    }

    void SetTitles()
    {
        Tree.SetColumnTitle(0, Tr("Bone"));
        Tree.SetColumnTitle(1, Tr("Standard"));
    }

    public void Rebuild()
    {
        _busy = true;
        Tree.Clear();
        var project = App.Session.Project;
        var character = _host.Selection.CharacterId is null ? null : project.Characters.Find(c => c.Id.ToString() == _host.Selection.CharacterId);
        var rig = character is null ? null : project.FindRig(character.Rig);
        var root = Tree.CreateItem();
        if (rig is not null)
        {
            var pal = _host.Palette;
            var items = new Dictionary<string, TreeItem>();
            foreach (var bone in rig.Bones)
            {
                var parent = !bone.Parent.IsEmpty && items.TryGetValue(bone.Parent.ToString(), out var pi) ? pi : root;
                var it = Tree.CreateItem(parent);
                it.SetText(0, bone.Name);
                it.SetAutoTranslateMode(0, AutoTranslateModeEnum.Disabled);
                it.SetMetadata(0, "b:" + bone.Id);
                var std = rig.StandardNameOf(bone.Id);
                it.SetText(1, std ?? "—");
                it.SetAutoTranslateMode(1, AutoTranslateModeEnum.Disabled);
                it.SetCustomFont(1, BlazmaTheme.Mono);
                it.SetCustomFontSize(1, 11);
                it.SetCustomColor(1, std is null ? pal.Text3 : pal.Text2);
                if (bone.Limits is not null) it.SetTooltipText(0, Tr("Has joint limits"));
                items[bone.Id.ToString()] = it;
            }
            if (rig.Controls.Count > 0)
            {
                var group = Tree.CreateItem(root);
                group.SetText(0, Tr("IK controls"));
                group.SetAutoTranslateMode(0, AutoTranslateModeEnum.Disabled);
                group.SetSelectable(0, false); group.SetSelectable(1, false);
                group.SetCustomColor(0, pal.Text3);
                foreach (var c in rig.Controls)
                {
                    var it = Tree.CreateItem(group);
                    it.SetText(0, c.Name);
                    it.SetAutoTranslateMode(0, AutoTranslateModeEnum.Disabled);
                    it.SetMetadata(0, "c:" + c.Id);
                    it.SetText(1, c.Kind == ControlKind.IkTarget ? Tr("target") : Tr("pole"));
                    it.SetAutoTranslateMode(1, AutoTranslateModeEnum.Disabled);
                    it.SetCustomColor(1, pal.Text3);
                    it.SetSelectable(1, false);
                }
            }
        }
        _busy = false;
        UpdateSelection();
    }

    public void UpdateSelection()
    {
        _busy = true;
        Tree.DeselectAll();
        var sel = _host.Selection;
        var key = sel.BoneId is not null ? "b:" + sel.BoneId : sel.ControlId is not null ? "c:" + sel.ControlId : null;
        if (key is not null && Tree.GetRoot() is { } root) Find(root, key);
        _busy = false;
    }

    bool Find(TreeItem item, string key)
    {
        for (var c = item.GetFirstChild(); c is not null; c = c.GetNext())
        {
            if (c.GetMetadata(0).AsString() == key) { c.Select(0); Tree.ScrollToItem(c); return true; }
            if (Find(c, key)) return true;
        }
        return false;
    }

    void OnSelected()
    {
        if (_busy || Tree.GetSelected() is not { } item) return;
        var key = item.GetMetadata(0).AsString();
        if (key.Length < 3 || _host.Selection.CharacterId is not { } cid) return;
        if (key.StartsWith("b:")) _host.Selection.SelectBone(cid, key[2..]);
        else if (key.StartsWith("c:")) _host.Selection.SelectControl(cid, key[2..]);
    }
}

/// <summary>Pose library of the selected character's rig: save the current pose, apply, rename, delete.</summary>
public partial class PosesPanel : VBoxContainer
{
    readonly MainWindow _host;
    public ItemList List { get; } = new();
    readonly Label _title = W.Label("", "Faint", false);
    readonly Button _save = new() { Text = "Save current pose…", ThemeTypeVariation = "Primary" };
    readonly Button _apply = new() { Text = "Apply" };
    readonly Button _rename = new() { Text = "Rename…" };
    readonly Button _delete = new() { Text = "Delete" };
    readonly Label _empty = W.Label("No saved poses for this rig yet. Pose the character, then save it here.", "Faint");
    List<string> _ids = new();
    bool _busy;

    static AppController App => AppController.Instance;

    public PosesPanel(MainWindow host)
    {
        _host = host;
        AddThemeConstantOverride("separation", 8);
        AddChild(_title);
        _title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _save.Pressed += _host.PromptSavePose;
        AddChild(_save);
        List.SizeFlagsVertical = SizeFlags.ExpandFill;
        List.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        List.SelectMode = ItemList.SelectModeEnum.Single;
        List.ItemSelected += i => { if (!_busy && i >= 0 && i < _ids.Count) { _host.SelectedPoseId = _ids[(int)i]; UpdateButtons(); } };
        List.ItemActivated += i => { if (i >= 0 && i < _ids.Count) { _host.SelectedPoseId = _ids[(int)i]; _host.ApplySelectedPose(); } };
        AddChild(List);
        _empty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddChild(_empty);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        foreach (var b in new[] { _apply, _rename, _delete }) { b.SizeFlagsHorizontal = SizeFlags.ExpandFill; b.FocusMode = FocusModeEnum.None; row.AddChild(b); }
        _save.FocusMode = FocusModeEnum.None;
        _apply.Pressed += () => _host.ApplySelectedPose();
        _rename.Pressed += _host.PromptRenamePose;
        _delete.Pressed += _host.ConfirmDeletePose;
        AddChild(row);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady()) Rebuild();
    }

    public void Rebuild()
    {
        _busy = true;
        List.Clear();
        _ids = new List<string>();
        var project = App.Session.Project;
        var character = _host.Selection.CharacterId is null ? null : project.Characters.Find(c => c.Id.ToString() == _host.Selection.CharacterId);
        var rig = character is null ? null : project.FindRig(character.Rig);
        _title.Text = rig is null ? "" : string.Format(Tr("Poses for {0}"), rig.Name);
        if (rig is not null)
            foreach (var pose in project.PoseLibrary.Where(p => p.Rig == rig.Id))
            {
                var idx = List.AddItem(pose.Name);
                List.SetItemTooltip(idx, pose.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
                _ids.Add(pose.Id.ToString());
                if (pose.Id.ToString() == _host.SelectedPoseId) List.Select(idx);
            }
        if (_host.SelectedPoseId is not null && !_ids.Contains(_host.SelectedPoseId)) _host.SelectedPoseId = null;
        _empty.Visible = _ids.Count == 0 && rig is not null;
        _save.Disabled = rig is null;
        _busy = false;
        UpdateButtons();
    }

    void UpdateButtons()
    {
        var has = _host.SelectedPoseId is not null;
        _apply.Disabled = _rename.Disabled = _delete.Disabled = !has;
    }

    public IReadOnlyList<string> PoseIds => _ids;
}
