using Blazma.Commands;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Advanced mode, left: project outline (project -> clips) synced with the clip strip.</summary>
public partial class ProjectTreePanel : VBoxContainer
{
    readonly MainWindow _host;
    public Tree Tree { get; } = new();
    bool _busy;

    public ProjectTreePanel(MainWindow host)
    {
        _host = host;
        AddThemeConstantOverride("separation", 6);
        var v = this;
        v.AddChild(W.Label("Project", "Section"));
        Tree.SizeFlagsVertical = SizeFlags.ExpandFill;
        Tree.HideRoot = false;
        Tree.SelectMode = Godot.Tree.SelectModeEnum.Single;
        Tree.ItemSelected += OnSelected;
        Tree.ItemActivated += () =>
        {
            if (Tree.GetSelected()?.GetMetadata(0).AsString() is { Length: > 0 } id) _host.PromptRenameClip(id);
        };
        Tree.AllowRmbSelect = true;
        v.AddChild(Tree);
    }

    public void Rebuild()
    {
        _busy = true;
        Tree.Clear();
        var project = AppController.Instance.Session.Project;
        var pal = _host.Palette;
        var root = Tree.CreateItem();
        root.SetText(0, project.Name);
        root.SetAutoTranslateMode(0, AutoTranslateModeEnum.Disabled);
        root.SetMetadata(0, "");
        root.SetCustomColor(0, pal.Text);
        root.SetSelectable(0, false);
        foreach (var clip in project.Clips)
        {
            var item = Tree.CreateItem(root);
            item.SetText(0, clip.Name);
            item.SetAutoTranslateMode(0, AutoTranslateModeEnum.Disabled);
            item.SetMetadata(0, clip.Id.ToString());
            item.SetTooltipText(0, $"{Fmt.Rate(clip.Fps)} fps · {clip.DurationFrames}");
        }
        _busy = false;
        UpdateSelection();
    }

    public void UpdateSelection()
    {
        _busy = true;
        var root = Tree.GetRoot();
        if (root is not null)
        {
            Tree.DeselectAll();
            var sel = _host.SelectedClipId;
            for (var c = root.GetFirstChild(); c is not null; c = c.GetNext())
                if (sel is not null && c.GetMetadata(0).AsString() == sel) { c.Select(0); Tree.ScrollToItem(c); }
        }
        _busy = false;
    }

    void OnSelected()
    {
        if (_busy) return;
        if (Tree.GetSelected()?.GetMetadata(0).AsString() is { Length: > 0 } id) _host.SelectClip(id);
    }
}

/// <summary>Advanced mode, right: undo history. Clicking an earlier step walks back with repeated Undo().</summary>
public partial class HistoryPanel : PanelContainer
{
    readonly MainWindow _host;
    public ItemList List { get; } = new();
    int _undoCount, _redoCount;

    public int UndoCount => _undoCount;
    public int RedoCount => _redoCount;

    public HistoryPanel(MainWindow host)
    {
        _host = host;
        ThemeTypeVariation = "Card";
        CustomMinimumSize = new Vector2(260, 0);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        v.AddChild(W.Label("History", "Section"));
        List.SizeFlagsVertical = SizeFlags.ExpandFill;
        List.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        List.SelectMode = ItemList.SelectModeEnum.Single;
        List.ItemClicked += OnClicked;
        v.AddChild(List);
        v.AddChild(W.Label("Click a step to go back or forward to it.", "Faint"));
        ((Label)v.GetChild(v.GetChildCount() - 1)).AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddChild(W.Margin(v, 12, 10, 8, 10));
    }

    public void Rebuild()
    {
        var s = AppController.Instance.Session;
        var pal = _host.Palette;
        var undo = s.UndoHistory;
        var redo = s.RedoHistory;
        _undoCount = undo.Count; _redoCount = redo.Count;
        List.Clear();
        List.AddItem(TranslationServer.Translate("Initial state"));
        List.SetItemCustomFgColor(0, pal.Text2);
        for (var i = 0; i < undo.Count; i++) Add(undo[i], pal.Text2);
        for (var i = redo.Count - 1; i >= 0; i--) Add(redo[i], pal.Text3);
        List.Select(_undoCount);
        List.SetItemCustomFgColor(_undoCount, pal.Text);
        List.EnsureCurrentIsVisible();
    }

    /// <summary>Who made the change, in the current language (the session id of an agent is not shown).</summary>
    static string OriginName(Blazma.Commands.CommandOrigin o) => TranslationServer.Translate(o.Source switch
    {
        Blazma.Commands.CommandSource.Ui => "You", // tr
        Blazma.Commands.CommandSource.Cli => "Command line", // tr
        Blazma.Commands.CommandSource.Mcp => "AI agent", // tr
        _ => "Test", // tr
    });

    void Add(TransactionInfo t, Color color)
    {
        var idx = List.AddItem($"{TranslationServer.Translate(t.Label)}  ·  {OriginName(t.Origin)}");
        List.SetItemCustomFgColor(idx, color);
    }

    /// <summary>Move the document to the state after history entry <paramref name="index"/> (0 = initial state).</summary>
    public void GoTo(int index)
    {
        var app = AppController.Instance;
        while (index < app.Session.UndoHistory.Count)
            if (!app.Undo().Ok) break;
        while (index > app.Session.UndoHistory.Count && app.Session.RedoHistory.Count > 0)
            if (!app.Redo().Ok) break;
    }

    void OnClicked(long index, Vector2 pos, long button)
    {
        if (button != (long)MouseButton.Left) return;
        GoTo((int)index);
    }
}
