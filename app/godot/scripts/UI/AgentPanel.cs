using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Agent;
using Blazma.Commands;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// AI Workspace (mockup C, right column): who is connected, what the agents did and what waits for the user.
/// Everything here is a view of <see cref="AppController.Agents"/>; every button calls an AppController method.
/// Tool names and arguments are technical text and always stay left-to-right, even in Arabic.
/// </summary>
public partial class AgentPanel : PanelContainer
{
    const int MaxTasks = 12, MaxLogRows = 30;

    readonly MainWindow _host;
    static AppController App => AppController.Instance;
    Palette P => _host.Palette;

    VBoxContainer _body = null!;
    Button _close = null!;
    bool _dirty = true, _logOpen;
    bool? _helpOpen;
    readonly HashSet<string> _expanded = new();
    readonly Dictionary<string, string> _notes = new();

    // Test hooks -----------------------------------------------------------------------------------------------
    /// <summary>Buttons by key: <c>task:ID:approve|reject|revert|calls</c>, <c>pending:ID:approve|deny</c>, <c>copy</c>, <c>folders</c>, <c>log</c>.</summary>
    public Dictionary<string, Button> Buttons { get; } = new();
    public Dictionary<string, string> TaskStateText { get; } = new();
    public List<string> AgentRows { get; } = new();
    public List<string> PendingTools { get; } = new();
    public Segmented ModeSwitch { get; private set; } = null!;
    public CheckButton AllowSwitch { get; private set; } = null!;
    public string CommandText { get; private set; } = "";
    public string? NoteFor(string taskId) => _notes.GetValueOrDefault(taskId);
    public int CallRowCount { get; private set; }
    public Button CloseButton => _close;

    public AgentPanel(MainWindow host)
    {
        _host = host;
        ThemeTypeVariation = "Card";
        CustomMinimumSize = new Vector2(380, 0);
        SizeFlagsHorizontal = SizeFlags.Fill;
        Visible = false;

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 0);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        head.AddChild(W.Label("AI Workspace", "Heading"));
        head.AddChild(W.Spacer());
        _close = new Button { Text = "×", Flat = true, FocusMode = FocusModeEnum.None, TooltipText = "Close" };
        _close.Pressed += () => _host.ShowAgentPanel(false);
        head.AddChild(_close);
        root.AddChild(W.Margin(head, 14, 8, 8, 6));
        root.AddChild(new HSeparator());

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _body.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(W.Margin(_body, 14, 10, 14, 10));
        ((Control)scroll.GetChild(0)).SizeFlagsHorizontal = SizeFlags.ExpandFill;
        root.AddChild(scroll);

        root.AddChild(new HSeparator());
        var guard = new HBoxContainer();
        guard.AddThemeConstantOverride("separation", 8);
        _shield = new TextureRect { StretchMode = TextureRect.StretchModeEnum.KeepCentered, SizeFlagsVertical = SizeFlags.ShrinkBegin, CustomMinimumSize = new Vector2(16, 18) };
        guard.AddChild(_shield);
        var note = W.Label("Agents cannot run system commands or write outside the project. Every change can be reverted.", "Faint");
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        note.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        guard.AddChild(note);
        root.AddChild(W.Margin(guard, 14, 8, 14, 10));
        AddChild(root);
    }

    TextureRect _shield = null!;

    public override void _Ready()
    {
        App.AgentChanged += MarkDirty;
        App.SettingsChanged += MarkDirty;
    }

    public override void _ExitTree()
    {
        App.AgentChanged -= MarkDirty;
        App.SettingsChanged -= MarkDirty;
    }

    public void MarkDirty() => _dirty = true;

    public override void _Process(double delta)
    {
        if (!_dirty || !IsVisibleInTree()) return;
        _dirty = false;
        Rebuild();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged || what == NotificationVisibilityChanged) _dirty = true;
    }

    // ---- building blocks --------------------------------------------------------------------------------------------

    /// <summary>Text-only button without the usual padding, so it lines up with the labels around it.</summary>
    Button Link(string text, bool translate = true)
    {
        var b = new Button { Text = text, Flat = true, FocusMode = FocusModeEnum.None };
        if (!translate) b.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        foreach (var st in new[] { "normal", "hover", "pressed", "focus" })
            b.AddThemeStyleboxOverride(st, BlazmaTheme.Box(Colors.Transparent, null, 4, 0, 0, 3, 0, 3));
        b.AddThemeColorOverride("font_color", P.Text2);
        b.AddThemeColorOverride("font_hover_color", P.Text);
        return b;
    }

    static Label Wrapped(Label l)
    {
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        l.CustomMinimumSize = new Vector2(40, 0);
        return l;
    }

    /// <summary>Technical text (tool names, arguments, commands): monospace, never translated, always left-to-right.</summary>
    Label Tech(string text, string variation = "MonoLabel", bool wrap = false)
    {
        var l = new Label { Text = text, ThemeTypeVariation = variation, AutoTranslateMode = AutoTranslateModeEnum.Disabled, TextDirection = Control.TextDirection.Ltr };
        l.HorizontalAlignment = HorizontalAlignment.Left;
        if (wrap)
        {
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            l.CustomMinimumSize = new Vector2(40, 0);
        }
        return l;
    }

    static Label Plain(string text, string? variation = null, bool wrap = true)
    {
        var l = W.Label(text, variation, false);
        return wrap ? Wrapped(l) : l;
    }

    Control SectionHeader(string text, string? trailing = null)
    {
        var row = new HBoxContainer();
        row.AddChild(W.Label(text, "Section"));
        row.AddChild(W.Spacer());
        if (trailing is not null) row.AddChild(W.Label(trailing, "Faint", false));
        return row;
    }

    PanelContainer Chip(string text, Color c)
    {
        var p = new PanelContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        p.AddThemeStyleboxOverride("panel", BlazmaTheme.Box(c with { A = 0.16f }, c, 10, 1, 8, 1, 8, 1));
        var l = new Label { Text = text };
        l.AddThemeColorOverride("font_color", c);
        l.AddThemeFontSizeOverride("font_size", 12);
        p.AddChild(l);
        return p;
    }

    PanelContainer Card(Color? border = null)
    {
        var p = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        p.AddThemeStyleboxOverride("panel", BlazmaTheme.Box(P.Panel2, border ?? P.Line, 6, 1, 10, 8, 10, 8));
        return p;
    }

    TextureRect Icon(string svgBody, Color c, int size = 14) => new()
    {
        Texture = BlazmaTheme.Svg(svgBody, c, size), StretchMode = TextureRect.StretchModeEnum.KeepCentered,
        SizeFlagsVertical = SizeFlags.ShrinkCenter, CustomMinimumSize = new Vector2(size + 2, size), MouseFilter = MouseFilterEnum.Ignore,
    };

    Control OkIcon() => Icon("<path d='M3 8.5l3.2 3.2L13 4.5'/>", P.Ok);
    Control FailIcon() => Icon("<path d='M4 4l8 8M12 4l-8 8'/>", P.Err);
    Control WarnIcon() => Icon("<path d='M8 2.5l6 10.5H2z'/><path d='M8 6.8v3M8 11.6v.1'/>", P.Warn);

    static string Local(DateTimeOffset t, string fmt = "HH:mm") => t.ToLocalTime().ToString(fmt, CultureInfo.InvariantCulture);

    string Count(string what, int n) => what == "change"
        ? n switch { 0 => Tr("No changes"), 1 => Tr("1 change"), _ => string.Format(Tr("{0} changes"), n) }
        : n switch { 0 => Tr("No calls"), 1 => Tr("1 call"), _ => string.Format(Tr("{0} calls"), n) };

    string TaskTitle(AgentTask t) => t.Title == "Agent task" ? Tr("Agent task") : t.Title;

    string StateText(AgentTaskState s) => s switch
    {
        AgentTaskState.Running => Tr("Running"),
        AgentTaskState.Review => Tr("Waiting for review"),
        AgentTaskState.Approved => Tr("Approved"),
        _ => Tr("Rejected"),
    };

    Color StateColor(AgentTaskState s) => s switch
    {
        AgentTaskState.Running => P.Accent,
        AgentTaskState.Review => P.Warn,
        AgentTaskState.Approved => P.Ok,
        _ => P.Err,
    };

    /// <summary>Core messages are short English text; the common ones get a translatable version.</summary>
    string FriendlyResult(string code, string message) => code switch
    {
        "edited_after" => Tr("There are newer changes after this task. Undo them first, then reject."),
        "busy" => Tr("Finish the current edit first."),
        "already_rejected" => Tr("This task was already rejected."),
        "project_replaced" => Tr("The project was replaced; this task can no longer be reverted."),
        "expired" => Tr("The agent or the action is no longer available."),
        _ => message,
    };

    // ---- rebuild ----------------------------------------------------------------------------------------------------

    public void Rebuild()
    {
        _shield.Texture = BlazmaTheme.Svg("<path d='M8 1.8l5 1.8v4.2c0 3-2.1 5-5 6.4-2.9-1.4-5-3.4-5-6.4V3.6z'/>", P.Text3, 14);
        foreach (var c in _body.GetChildren()) { _body.RemoveChild(c); c.QueueFree(); }
        Buttons.Clear(); TaskStateText.Clear(); AgentRows.Clear(); PendingTools.Clear();
        CallRowCount = 0;

        var host = App.Agents;
        if (host.PendingApprovals.Count > 0) BuildPending(host); // what needs the user comes first
        BuildConnection(host);
        BuildTasks(host);
        BuildLog(host);
    }

    void BuildConnection(AgentHost host)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        box.AddChild(SectionHeader("Connection"));

        var allow = new HBoxContainer();
        allow.AddChild(Wrapped(W.Label("Agents allowed")));
        AllowSwitch = new CheckButton { ButtonPressed = App.Settings.AllowAgents, FocusMode = FocusModeEnum.None };
        AllowSwitch.Toggled += on => { if (on != App.Settings.AllowAgents) App.SetAllowAgents(on); };
        allow.AddChild(AllowSwitch);
        box.AddChild(allow);

        if (!App.Settings.AllowAgents)
            box.AddChild(Wrapped(W.Label("Agents cannot connect while this is off.", "Faint")));
        else if (host.Sessions.Count == 0)
            box.AddChild(Wrapped(W.Label("No agents connected", "Faint")));
        foreach (var s in host.Sessions.OrderBy(s => s.Connected))
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var dot = new Dot(); dot.SetColor(P.Ok);
            row.AddChild(dot);
            var name = Plain(s.ClientName, null, false);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            name.TextDirection = Control.TextDirection.Auto;
            row.AddChild(name);
            var since = string.Format(Tr("Connected since {0}"), Local(s.Connected));
            row.AddChild(Plain(since, "Faint", false));
            AgentRows.Add(s.ClientName);
            box.AddChild(row);
        }

        var mode = new HBoxContainer();
        mode.AddChild(Wrapped(W.Label("Mode")));
        ModeSwitch = new Segmented("Assisted", "Autonomous");
        ModeSwitch.SetIndex(App.Settings.AgentAutonomous ? 1 : 0);
        ModeSwitch.Selected += i => App.SetAgentAutonomous(i == 1);
        mode.AddChild(ModeSwitch);
        box.AddChild(mode);
        box.AddChild(Wrapped(W.Label(App.Settings.AgentAutonomous
            ? "Autonomous: tasks are accepted when they finish. You can still revert them."
            : "Assisted: every task waits for your review before it counts.", "Faint")));

        // The "how to connect" help stays out of the way while an agent is connected.
        var helpOpen = _helpOpen ?? host.Sessions.Count == 0;
        var helpToggle = Link(helpOpen ? "Hide connection help" : "How to connect an agent");
        helpToggle.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        helpToggle.Pressed += () => { _helpOpen = !helpOpen; MarkDirty(); };
        Buttons["help"] = helpToggle;
        box.AddChild(helpToggle);
        CommandText = ConnectCommand();
        if (helpOpen)
        {
            var head = new HBoxContainer();
            head.AddChild(Wrapped(W.Label("Connect Claude Code:", "Dim")));
            var copy = new Button { Text = "Copy", FocusMode = FocusModeEnum.None };
            copy.Pressed += () =>
            {
                DisplayServer.ClipboardSet(CommandText);
                _host.ToastNode.Notify(Tr("Copied to the clipboard"), P.Ok);
            };
            head.AddChild(copy);
            Buttons["copy"] = copy;
            box.AddChild(head);

            var cmdBox = Card();
            cmdBox.AddChild(Tech(CommandText, "MonoLabel", true));
            box.AddChild(cmdBox);

            var folders = new Button { Text = "Folders agents may read…", FocusMode = FocusModeEnum.None, SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
            folders.Pressed += _host.ShowAgentFolders;
            box.AddChild(folders);
            Buttons["folders"] = folders;
        }
        _body.AddChild(box);
    }

    /// <summary>"claude mcp add blazma -- &lt;path to blazma-mcp&gt;"; in the editor only the program name is known.</summary>
    public static string ConnectCommand()
    {
        string exe;
        if (OS.HasFeature("editor")) exe = "blazma-mcp";
        else
        {
            var dir = System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
            exe = System.IO.Path.Combine(dir, OperatingSystem.IsWindows() ? "blazma-mcp.exe" : "blazma-mcp");
            if (exe.Contains(' ')) exe = $"\"{exe}\"";
        }
        return $"claude mcp add blazma -- {exe}";
    }

    void BuildPending(AgentHost host)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        box.AddChild(SectionHeader("Needs your approval", host.PendingApprovals.Count.ToString(CultureInfo.InvariantCulture)));
        foreach (var p in host.PendingApprovals)
        {
            var card = Card(P.Warn);
            var v = new VBoxContainer();
            v.AddThemeConstantOverride("separation", 6);
            var top = new HBoxContainer();
            top.AddThemeConstantOverride("separation", 8);
            top.AddChild(WarnIcon());
            var tool = Tech(p.Tool, "MonoLabel");
            tool.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            top.AddChild(tool);
            var from = host.Sessions.FirstOrDefault(s => s.Id == p.SessionId)?.ClientName ?? "agent";
            top.AddChild(Plain(string.Format(Tr("From {0}"), from), "Faint", false));
            v.AddChild(top);
            v.AddChild(Tech(ArgsText(p.Args), "MonoFaint", true));
            v.AddChild(Plain(Reason(p), null));
            var buttons = new HBoxContainer();
            buttons.AddThemeConstantOverride("separation", 8);
            var ok = new Button { Text = "Approve", ThemeTypeVariation = "Primary", FocusMode = FocusModeEnum.None };
            var no = new Button { Text = "Deny", FocusMode = FocusModeEnum.None };
            var id = p.Id;
            ok.Pressed += () =>
            {
                var r = App.AgentApprovePending(id);
                if (!r.Ok && r.Error is { } e) _host.ToastNode.Notify(FriendlyResult(e.Code, e.Message), P.Err);
            };
            no.Pressed += () => App.AgentDenyPending(id);
            buttons.AddChild(ok); buttons.AddChild(no);
            v.AddChild(buttons);
            Buttons[$"pending:{id}:approve"] = ok;
            Buttons[$"pending:{id}:deny"] = no;
            PendingTools.Add(p.Tool);
            card.AddChild(v);
            box.AddChild(card);
        }
        _body.AddChild(box);
    }

    static string ArgsText(JsonObject args)
    {
        var s = args.ToJsonString();
        return s.Length > 240 ? s[..240] + "…" : s;
    }

    string Reason(AgentPendingApproval p)
    {
        if (App.Registry.TryGet(p.Tool, out var d))
        {
            if (d.Effect == CommandEffect.Destructive) return Tr("This action deletes something. Allow it?");
            if (d.Effect == CommandEffect.External && p.Args["path"] is JsonValue path)
                return string.Format(Tr("This action reads a file outside the allowed folders: {0}"), (string?)path ?? "");
        }
        return Tr("This action needs your approval.");
    }

    void BuildTasks(AgentHost host)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        box.AddChild(SectionHeader("Tasks"));
        if (host.Tasks.Count == 0)
            box.AddChild(Wrapped(W.Label("No tasks yet. When an agent edits the project, its work appears here.", "Faint")));
        foreach (var t in host.Tasks.Reverse().Take(MaxTasks)) box.AddChild(BuildTaskCard(t));
        _body.AddChild(box);
    }

    Control BuildTaskCard(AgentTask t)
    {
        var stale = App.IsAgentTaskStale(t.Id);
        var card = Card(t.State == AgentTaskState.Review ? P.Warn : null);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);

        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 8);
        var title = Plain(TaskTitle(t), "Heading");
        title.TextDirection = Control.TextDirection.Auto;
        top.AddChild(title);
        var state = StateText(t.State);
        top.AddChild(Chip(state, StateColor(t.State)));
        TaskStateText[t.Id] = state;
        v.AddChild(top);

        if (!string.IsNullOrWhiteSpace(t.Summary))
        {
            var summary = t.Summary == "Agent disconnected." ? Tr("Agent disconnected.") : t.Summary!;
            v.AddChild(Plain(summary, "Dim"));
        }
        v.AddChild(Plain($"{Count("change", t.Transactions.Count)} · {Count("call", t.Calls.Count)} · {Local(t.Started)}", "Faint"));

        if (stale && t.State != AgentTaskState.Rejected)
            v.AddChild(Plain(Tr("The project was replaced. This task can no longer be reverted."), "Faint"));
        if (_notes.TryGetValue(t.Id, out var note))
        {
            var n = Plain(note, "ErrLabel");
            v.AddChild(n);
        }

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        var id = t.Id;
        if (t.State == AgentTaskState.Review)
        {
            var approve = new Button { Text = "Approve", ThemeTypeVariation = "Primary", FocusMode = FocusModeEnum.None };
            approve.Pressed += () => { _notes.Remove(id); App.AgentApprove(id); };
            var reject = new Button { Text = "Reject", FocusMode = FocusModeEnum.None, Disabled = stale };
            reject.AddThemeColorOverride("font_color", P.Err);
            reject.Pressed += () => RunReject(id);
            buttons.AddChild(approve); buttons.AddChild(reject);
            Buttons[$"task:{id}:approve"] = approve;
            Buttons[$"task:{id}:reject"] = reject;
        }
        else if (t.State == AgentTaskState.Approved && t.HasChanges && !stale)
        {
            var revert = new Button { Text = "Revert", FocusMode = FocusModeEnum.None };
            revert.Pressed += () => RunReject(id);
            buttons.AddChild(revert);
            Buttons[$"task:{id}:revert"] = revert;
        }
        buttons.AddChild(W.Spacer());
        var open = _expanded.Contains(id);
        var calls = Link(open ? "Hide calls" : "Show calls");
        calls.Pressed += () => { if (!_expanded.Remove(id)) _expanded.Add(id); MarkDirty(); };
        buttons.AddChild(calls);
        Buttons[$"task:{id}:calls"] = calls;
        v.AddChild(buttons);

        if (open) v.AddChild(BuildCalls(t.Calls));
        card.AddChild(v);
        return card;
    }

    void RunReject(string taskId)
    {
        var r = App.AgentReject(taskId);
        if (r.Ok) _notes.Remove(taskId);
        else if (r.Error is { } e) _notes[taskId] = FriendlyResult(e.Code, e.Message);
        MarkDirty();
    }

    Control BuildCalls(IReadOnlyList<AgentCall> calls)
    {
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 5);
        list.AddChild(new HSeparator());
        if (calls.Count == 0) list.AddChild(Plain(Tr("No calls yet."), "Faint"));
        foreach (var c in calls) { list.AddChild(CallRow(c)); CallRowCount++; }
        return list;
    }

    Control CallRow(AgentCall c)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 2);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        row.AddChild(Tech(Local(c.Time, "HH:mm:ss"), "MonoFaint"));
        row.AddChild(c.Ok ? OkIcon() : FailIcon());
        var tool = Tech(c.Tool, "MonoLabel");
        tool.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(tool);
        if (!c.Ok && c.ErrorCode is not null)
        {
            var code = Tech(c.ErrorCode, "MonoFaint");
            code.AddThemeColorOverride("font_color", c.ErrorCode == "approval_required" ? P.Warn : P.Err);
            row.AddChild(code);
        }
        v.AddChild(row);
        if (c.ArgsSummary.Length > 0) v.AddChild(Tech(c.ArgsSummary, "MonoFaint", true));
        if (!c.Ok && c.ErrorCode is not null)
        {
            var msg = c.ErrorCode switch
            {
                "approval_required" => Tr("Waiting for your approval."),
                "denied" => Tr("You denied this action."),
                _ => c.Message ?? "",
            };
            if (msg.Length > 0)
            {
                var l = Plain(msg, "Faint");
                l.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
                v.AddChild(l);
            }
        }
        foreach (var w in c.Warnings)
        {
            var wr = new HBoxContainer();
            wr.AddThemeConstantOverride("separation", 6);
            wr.AddChild(WarnIcon());
            var l = Plain(w, "Faint");
            l.AddThemeColorOverride("font_color", P.Warn);
            wr.AddChild(l);
            v.AddChild(wr);
        }
        return v;
    }

    void BuildLog(AgentHost host)
    {
        var log = host.ReadOnlyLog;
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        var toggle = Link(string.Format(Tr(_logOpen ? "Hide read-only calls ({0})" : "Read-only calls ({0})"), log.Count), false);
        toggle.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        toggle.Pressed += () => { _logOpen = !_logOpen; MarkDirty(); };
        Buttons["log"] = toggle;
        box.AddChild(toggle);
        if (_logOpen)
        {
            if (log.Count == 0) box.AddChild(Plain(Tr("No calls yet."), "Faint"));
            foreach (var c in log.TakeLast(MaxLogRows).Reverse()) box.AddChild(CallRow(c));
        }
        _body.AddChild(box);
    }
}
