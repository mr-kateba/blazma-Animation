using Blazma.Agent;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Phase 5 shell parts: the AI button in the top bar, the AI dock on the inline-end side, the AI indicator in the status bar.</summary>
public partial class MainWindow
{
    const int MAgentAllow = 90, MAgentFolders = 92;

    public AgentPanel Agent { get; private set; } = null!;
    HSplitContainer _splitAgent = null!;
    Button _aiButton = null!, _statusAi = null!;
    Dot _aiDot = null!, _statusAiDot = null!;
    PopupMenu _mAgent = null!;
    AcceptDialog _foldersDialog = null!;
    ItemList _foldersList = null!;
    FileDialog _folderPicker = null!;
    Label _foldersIntro = null!;
    Button _foldersAdd = null!, _foldersRemove = null!;
    int _lastSessions, _lastPending, _lastReview;

    public Button AiButton => _aiButton;
    public Button StatusAiButton => _statusAi;
    public Dot StatusAiDot => _statusAiDot;
    public Dot AiDot => _aiDot;
    public Color StatusAiColor { get; private set; }
    public string StatusAiText => _statusAi.Text;
    public AcceptDialog FoldersDialog => _foldersDialog;
    public HSplitContainer AgentSplit => _splitAgent;

    Control BuildAiButton()
    {
        _aiButton = new Button
        {
            Text = "AI", ToggleMode = true, ThemeTypeVariation = "Transport", FocusMode = FocusModeEnum.None,
            SizeFlagsVertical = SizeFlags.ShrinkCenter, CustomMinimumSize = new Vector2(64, 0), TooltipText = "Show or hide the AI Workspace",
        };
        _aiButton.AddThemeStyleboxOverride("normal", BlazmaTheme.Box(_palette.Panel2, _palette.Line, 6, 1, 24, 4, 12, 4));
        _aiButton.Toggled += on => { if (on != Agent.Visible) ShowAgentPanel(on); };
        _aiDot = new Dot { MouseFilter = MouseFilterEnum.Ignore };
        _aiDot.SetAnchorsPreset(Control.LayoutPreset.CenterLeft);
        _aiDot.OffsetLeft = 10; _aiDot.OffsetRight = 18; _aiDot.OffsetTop = -4; _aiDot.OffsetBottom = 4;
        _aiButton.AddChild(_aiDot);
        return _aiButton;
    }

    /// <summary>Right dock (left in Arabic): the splitter wraps the whole work area so the panel runs the full height.</summary>
    void BuildAgentDock(Control workArea)
    {
        Agent = new AgentPanel(this);
        _splitAgent = new HSplitContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        workArea.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _splitAgent.AddChild(workArea);
        _splitAgent.AddChild(Agent);
        _splitAgent.Dragged += _ => ScheduleLayoutSave();
    }

    void BuildStatusAi(HBoxContainer row)
    {
        var box = new HBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        _statusAiDot = new Dot();
        _statusAi = new Button { Flat = true, FocusMode = FocusModeEnum.None, AutoTranslateMode = AutoTranslateModeEnum.Disabled };
        _statusAi.AddThemeFontSizeOverride("font_size", 12);
        _statusAi.AddThemeStyleboxOverride("normal", BlazmaTheme.Box(Colors.Transparent, null, 4, 0, 4, 0, 4, 0));
        _statusAi.AddThemeStyleboxOverride("hover", BlazmaTheme.Box(Colors.Transparent, null, 4, 0, 4, 0, 4, 0));
        _statusAi.AddThemeStyleboxOverride("pressed", BlazmaTheme.Box(Colors.Transparent, null, 4, 0, 4, 0, 4, 0));
        _statusAi.Pressed += () => ShowAgentPanel(!Agent.Visible);
        box.AddChild(_statusAiDot);
        box.AddChild(_statusAi);
        row.AddChild(box);
    }

    void BuildAgentMenu()
    {
        _mSettings.AddSeparator();
        _mSettings.AddCheckItem("Allow AI agents to connect", MAgentAllow);
        _mSettings.AddItem("Folders agents may read…", MAgentFolders);
        _mAgent = _mSettings;
    }

    void BuildAgentDialogs()
    {
        _foldersDialog = new AcceptDialog { Exclusive = true, AutoTranslateMode = AutoTranslateModeEnum.Disabled };
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0) };
        v.AddThemeConstantOverride("separation", 8);
        var intro = _foldersIntro = W.Label("", "Dim", false);
        intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(intro);
        _foldersList = new ItemList { CustomMinimumSize = new Vector2(0, 140), AutoTranslateMode = AutoTranslateModeEnum.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        v.AddChild(_foldersList);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        var add = _foldersAdd = new Button { FocusMode = FocusModeEnum.None };
        var remove = _foldersRemove = new Button { FocusMode = FocusModeEnum.None };
        add.Pressed += () => _folderPicker.PopupCenteredRatio(0.6f);
        remove.Pressed += () =>
        {
            var sel = _foldersList.GetSelectedItems();
            if (sel.Length == 0) return;
            var roots = App.Settings.AgentReadRoots.ToList();
            roots.RemoveAt(sel[0]);
            App.SetAgentReadRoots(roots);
            RefreshFolders();
        };
        row.AddChild(add); row.AddChild(remove);
        v.AddChild(row);
        _foldersDialog.AddChild(v);
        AddChild(_foldersDialog);

        _folderPicker = new FileDialog { UseNativeDialog = true, Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenDir };
        _folderPicker.DirSelected += dir =>
        {
            App.SetAgentReadRoots(App.Settings.AgentReadRoots.Append(dir));
            RefreshFolders();
        };
        AddChild(_folderPicker);
    }

    void RefreshFolders()
    {
        _foldersDialog.Title = Tr("Folders agents may read");
        _foldersDialog.OkButtonText = Tr("Close");
        _foldersIntro.Text = Tr("Agents can always use the project's own folder. Add other folders they may import files from and save new files to (they never overwrite existing files).");
        _foldersAdd.Text = Tr("Add folder…");
        _foldersRemove.Text = Tr("Remove");
        _foldersList.Clear();
        foreach (var r in App.Settings.AgentReadRoots) _foldersList.AddItem(r);
    }

    public void ShowAgentFolders()
    {
        RefreshFolders();
        _foldersDialog.PopupCentered(new Vector2I(500, 0));
    }

    public void ShowAgentPanel(bool show)
    {
        Agent.Visible = show;
        if (show) Agent.MarkDirty();
        _aiButton.SetPressedNoSignal(show);
        if (show) _splitAgent.SplitOffset = Math.Min(_splitAgent.SplitOffset, 0);
        UpdateAgentUi();
    }

    /// <summary>Called when anything agent related changed: refresh the indicators and open the dock when something needs attention.</summary>
    void OnAgentChanged()
    {
        var host = App.Agents;
        var sessions = host.Sessions.Count;
        var pending = host.PendingApprovals.Count;
        var review = host.Tasks.Count(t => t.State == AgentTaskState.Review);
        var attention = sessions > _lastSessions || pending > _lastPending || review > _lastReview;
        _lastSessions = sessions; _lastPending = pending; _lastReview = review;
        if (attention && !Agent.Visible) ShowAgentPanel(true);
        UpdateAgentUi();
    }

    void UpdateAgentUi()
    {
        if (_statusAi is null) return;
        var host = App.Agents;
        var n = host.Sessions.Count;
        var waiting = host.PendingApprovals.Count > 0 || host.Tasks.Any(t => t.State == AgentTaskState.Review);
        var allowed = App.Settings.AllowAgents;
        var color = !allowed ? _palette.Text3 : waiting ? _palette.Warn : n > 0 ? _palette.Ok : _palette.Text3;
        StatusAiColor = color;
        _aiDot.SetColor(color);
        _statusAiDot.SetColor(color);
        _statusAi.Text = n > 0 ? $"{Tr("AI")} · {n}" : Tr("AI");
        _statusAi.TooltipText = !allowed ? Tr("AI agents are turned off")
            : waiting ? Tr("An AI agent is waiting for your approval")
            : n > 0 ? string.Format(Tr("AI agents connected: {0}"), n)
            : Tr("No AI agent connected");
        _aiButton.Text = Tr("AI");
        _aiButton.SetPressedNoSignal(Agent.Visible);
        _aiButton.TooltipText = Tr("Show or hide the AI Workspace");
        var rtl = App.IsRtl;
        // Godot mirrors anchors itself in an RTL layout: the dot always sits at the inline start (right in Arabic).
        _aiButton.AddThemeStyleboxOverride("normal", BlazmaTheme.Box(_palette.Panel2, _palette.Line, 6, 1, rtl ? 12 : 24, 4, rtl ? 24 : 12, 4));
        _aiButton.AddThemeStyleboxOverride("hover", BlazmaTheme.Box(_palette.Raised, _palette.LineStrong, 6, 1, rtl ? 12 : 24, 4, rtl ? 24 : 12, 4));
        _aiButton.AddThemeStyleboxOverride("pressed", BlazmaTheme.Box(_palette.AccentSoft, _palette.Accent, 6, 1, rtl ? 12 : 24, 4, rtl ? 24 : 12, 4));
        _aiButton.AddThemeStyleboxOverride("hover_pressed", BlazmaTheme.Box(_palette.AccentSoft, _palette.Accent, 6, 1, rtl ? 12 : 24, 4, rtl ? 24 : 12, 4));
        _mSettings.SetItemChecked(_mSettings.GetItemIndex(MAgentAllow), allowed);
    }
}
