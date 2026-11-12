using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>One stop of the tour: which real control to light up and what to say. Text is English source text (translated at show time).</summary>
public sealed record TutorialStep(string Id, string Title, string Body, Func<MainWindow, Control?>? Target = null, string? Tip = null, bool Plan = false, bool TryIt = false);

/// <summary>
/// "How Blazma works": a guided tour. The rest of the window is dimmed, the real control of each step is lit, and a card explains it
/// (Next / Back / Skip, progress dots, Esc closes). One step lets the user try it: apply the walk template and press Play.
/// </summary>
public partial class TutorialOverlay : Control
{
    readonly MainWindow _host;
    static AppController App => AppController.Instance;
    static ShortcutMap Keys => ShortcutMap.Instance;

    public static IReadOnlyList<TutorialStep> Steps { get; } = new TutorialStep[]
    {
        new("intro", "How Blazma works", "In a few steps you will see how to look around, pose a character, record keys on the timeline, start from a template or from a sentence, and export. Press Esc at any time to leave the tour."),
        new("viewport", "The stage and the camera", "This is your stage. Hold the right mouse button and use W A S D to fly. Orbit with the middle mouse button or Alt + left drag, pan with Shift + drag, zoom with the wheel. Press F to frame what you selected and Home to see everything.", w => w.Stage),
        new("posing", "Select and pose bones", "Click a bone to select it, then rotate it with E (FK: you turn each bone yourself). Move a hand or foot with W and Blazma bends the arm or leg for you (IK). Pin a hand or foot to keep it in place while the body moves.", w => w.Rail),
        new("keys", "Keys on the timeline", "Pose the character, then press Key (K) to record that pose at the playhead. The diamonds on the timeline are keys: Blazma fills in the movement between them. Turn on Auto-key and every change is keyed for you.", w => w.Timeline.KeyButton),
        new("timeline", "Play it back", "Space plays and pauses. Left and Right step one frame, Shift + arrows jump ten, Up and Down jump between keys. Drag in the ruler to scrub.", w => w.Timeline.PlayButton),
        new("templates", "Start from a template", "Not sure where to start? Templates are ready-made motions: idle, walk, run, jump, wave, attack. Apply one to your character and it becomes a new clip you can edit.", w => w.Timeline.TemplatesButton, "Ctrl+T"),
        new("generate", "Generate motion from a sentence", "Describe the movement in your own words, in Arabic or English, even several actions in a row. Example: “walk forward, then jump, then wave”. Blazma splits the sentence into steps, shows them in a plan table so you can fix any step, and joins them into one clip.", w => w.Timeline.GenerateButton, "Ctrl+G", Plan: true),
        new("drafts", "Drafts: accept or discard", "A generated clip arrives as a draft. Play it, then press Accept to keep it or Discard to remove it. Ctrl+Z brings a discarded draft back, so you can experiment freely.", w => w.Timeline),
        new("physics", "Check the balance", "Switch on Balance to see the centre of mass, the support polygon and the contacts. If the character would fall over, the Physics panel can fix the pose for you.", w => w.PhysicsButton, "B"),
        new("export", "Export", "When the clip looks right, Export writes it as a .glb animation for your game engine or 3D tool.", w => w.Timeline.ExportButton, "Ctrl+E"),
        new("save", "Save your work", "Ctrl+S saves the project. Blazma also autosaves in the background and offers to recover your work if the app ever closes unexpectedly.", w => w.MenuBarNode, "Ctrl+S"),
        new("tryit", "Try it", "Let's do it once: press the button to add a walking character with the Walk template and play it.", w => w.Timeline.TemplatesButton, TryIt: true),
        new("done", "You are ready", "That is all you need to begin. F1 shows every keyboard shortcut and you can change them in Settings. The ? button at the top opens this tour again."),
    };

    public int Index { get; private set; }
    public Rect2 SpotRect { get; private set; }
    public bool HasSpot { get; private set; }
    public bool TryItDone { get; private set; }
    public Button NextButton { get; } = new() { Text = "Next", ThemeTypeVariation = "Primary", FocusMode = FocusModeEnum.None };
    public Button BackButton { get; } = new() { Text = "Back", FocusMode = FocusModeEnum.None };
    public Button SkipButton { get; } = new() { Text = "Skip", ThemeTypeVariation = "Transport", FocusMode = FocusModeEnum.None };
    public Button TryButton { get; } = new() { Text = "Apply the Walk template and play", ThemeTypeVariation = "Primary", FocusMode = FocusModeEnum.None };
    public PanelContainer Card { get; } = new() { ThemeTypeVariation = "Floating" };
    public string CardTitle => _title.Text;
    public bool Completed { get; private set; }
    readonly Label _count = W.Label("", "Faint", false), _title = W.Label("", "Heading", false), _body = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, AutoTranslateMode = AutoTranslateModeEnum.Disabled, CustomMinimumSize = new Vector2(380, 0) };
    readonly Label _tip = W.Label("", "Dim", false), _tryResult = W.Label("", "Dim", false);
    readonly HBoxContainer _dots = new() { Alignment = BoxContainer.AlignmentMode.Center, LayoutDirection = LayoutDirectionEnum.Ltr };
    readonly VBoxContainer _plan = new();

    public TutorialOverlay(MainWindow host)
    {
        _host = host;
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        W.Fill(this);
        Card.CustomMinimumSize = new Vector2(420, 0);
        AddChild(Card);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 8);
        Card.AddChild(W.Margin(v, 18, 14, 18, 14));
        v.AddChild(_count);
        v.AddChild(_title);
        _body.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        v.AddChild(_body);
        v.AddChild(_plan);
        _tip.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(_tip);
        v.AddChild(TryButton);
        _tryResult.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(_tryResult);
        v.AddChild(_dots);
        var foot = new HBoxContainer();
        foot.AddThemeConstantOverride("separation", 8);
        foot.AddChild(SkipButton);
        foot.AddChild(W.Spacer());
        foot.AddChild(BackButton);
        foot.AddChild(NextButton);
        v.AddChild(foot);
        NextButton.Pressed += Next;
        BackButton.Pressed += Back;
        SkipButton.Pressed += () => Close(false);
        TryButton.Pressed += () => TryIt();
        BuildPlanTable();
        Resized += Layout;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady() && Visible) Show(Index);
    }

    /// <summary>The control the current step lights up, or null for a step without one.</summary>
    public Control? CurrentTarget => Steps[Index].Target?.Invoke(_host) is { } c && GodotObject.IsInstanceValid(c) && c.IsVisibleInTree() && c.Size.X > 4 && c.Size.Y > 4 ? c : null;

    public void Start()
    {
        TryItDone = false;
        Completed = false;
        if (App.Session.Project.Characters.Count == 0) _host.AddMannequin(); // something to practise on
        _host.Timeline.SelectTab(TimelinePanel.TabTimeline);
        Visible = true;
        MoveToFront();
        Show(0);
    }

    public void Next()
    {
        if (Index >= Steps.Count - 1) { Close(true); return; }
        Show(Index + 1);
    }

    public void Back() { if (Index > 0) Show(Index - 1); }

    /// <summary>Leave the tour. <paramref name="finished"/> remembers that it was completed.</summary>
    public void Close(bool finished)
    {
        if (!Visible) return;
        Visible = false;
        if (finished) { Completed = true; App.Settings.TutorialDone = true; App.Settings.Save(); }
    }

    public void Show(int index)
    {
        Index = Math.Clamp(index, 0, Steps.Count - 1);
        var s = Steps[Index];
        _count.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, Tr("Step {0} of {1}"), Index + 1, Steps.Count);
        _title.Text = Tr(s.Title);
        _body.Text = Tr(s.Body);
        _tip.Visible = s.Tip is not null;
        if (s.Tip is not null)
        {
            var action = s.Tip switch { "Ctrl+T" => "ai.templates", "Ctrl+G" => "ai.generate", "Ctrl+E" => "file.export", "Ctrl+S" => "file.save", "B" => "view.balance", _ => "" };
            _tip.Text = string.Format(Tr("Shortcut: {0}"), action.Length > 0 ? Keys.Display(action) : s.Tip);
        }
        _plan.Visible = s.Plan;
        TryButton.Visible = s.TryIt;
        _tryResult.Visible = s.TryIt;
        _tryResult.Text = TryItDone ? Tr("Done! The walk is playing. You can edit it like any other clip.") : Tr("Optional: you can also press Next to skip this.");
        BackButton.Disabled = Index == 0;
        NextButton.Text = Tr(Index == Steps.Count - 1 ? "Finish" : "Next");
        SkipButton.Visible = Index < Steps.Count - 1;
        BuildDots();
        CallDeferred(nameof(Layout));
        Layout();
    }

    void BuildDots()
    {
        foreach (var c in _dots.GetChildren()) { _dots.RemoveChild(c); c.QueueFree(); }
        for (var i = 0; i < Steps.Count; i++)
        {
            var d = new Dot { CustomMinimumSize = new Vector2(i == Index ? 14 : 8, 8) };
            d.SetColor(i == Index ? _host.Palette.Accent : i < Index ? _host.Palette.Text2 : _host.Palette.LineStrong);
            _dots.AddChild(d);
        }
    }

    void BuildPlanTable()
    {
        _plan.AddThemeConstantOverride("separation", 2);
        var head = W.Label("The plan Blazma reads from that sentence", "Faint");
        _plan.AddChild(head);
        var g = new GridContainer { Columns = 3, LayoutDirection = LayoutDirectionEnum.Ltr };
        g.AddThemeConstantOverride("h_separation", 18);
        foreach (var h in new[] { "#", "Step", "Length" }) g.AddChild(W.Label(h, "Faint"));
        foreach (var (n, text, len) in new[] { ("1", "walk forward", "2.0 s"), ("2", "jump", "1.0 s"), ("3", "wave", "1.5 s") })
        {
            g.AddChild(W.Label(n, "Dim", false));
            g.AddChild(W.Label(text, null, false));
            g.AddChild(W.Label(len, "Dim", false));
        }
        _plan.AddChild(new PanelContainer { ThemeTypeVariation = "Chip" }.AddChildReturn(g));
    }

    /// <summary>The "try it" task: add a character if needed, apply the Walk template (it starts playing).</summary>
    public bool TryIt()
    {
        if (_host.SelectedCharacter is null) _host.AddMannequin();
        if (_host.SelectedCharacter is not { } c) return false;
        var r = _host.ApplyTemplateNow("builtin.walk_cycle", c.Id.ToString(), null, null);
        TryItDone = r.Ok && App.Playing;
        _tryResult.Text = TryItDone ? Tr("Done! The walk is playing. You can edit it like any other clip.") : Tr("That did not work. You can skip this step.");
        Layout();
        return TryItDone;
    }

    // ---- drawing and placement ----------------------------------------------------------------------------------------------

    void Layout()
    {
        if (!Visible || Steps.Count == 0) return;
        var target = CurrentTarget;
        HasSpot = target is not null;
        SpotRect = target is null ? default : target.GetGlobalRect().Grow(5).Intersection(new Rect2(Vector2.Zero, Size));
        Card.ResetSize();
        var size = Card.GetCombinedMinimumSize();
        Card.Size = size;
        var bounds = new Rect2(Vector2.Zero, Size);
        Vector2 pos;
        const float gap = 16, margin = 16;
        if (!HasSpot) pos = (bounds.Size - size) / 2;
        else
        {
            var below = SpotRect.End.Y + gap;
            var above = SpotRect.Position.Y - gap - size.Y;
            var spotCenterX = SpotRect.GetCenter().X - size.X / 2;
            if (below + size.Y <= bounds.Size.Y - margin) pos = new Vector2(spotCenterX, below);
            else if (above >= margin) pos = new Vector2(spotCenterX, above);
            else if (SpotRect.End.X + gap + size.X <= bounds.Size.X - margin) pos = new Vector2(SpotRect.End.X + gap, SpotRect.GetCenter().Y - size.Y / 2);
            else if (SpotRect.Position.X - gap - size.X >= margin) pos = new Vector2(SpotRect.Position.X - gap - size.X, SpotRect.GetCenter().Y - size.Y / 2);
            else pos = (bounds.Size - size) / 2; // the target fills the window: the card sits on top of it
        }
        pos.X = Mathf.Clamp(pos.X, margin, Math.Max(margin, bounds.Size.X - size.X - margin));
        pos.Y = Mathf.Clamp(pos.Y, margin, Math.Max(margin, bounds.Size.Y - size.Y - margin));
        Card.Position = pos;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        var t = CurrentTarget;
        var r = t is null ? default : t.GetGlobalRect().Grow(5).Intersection(new Rect2(Vector2.Zero, Size));
        if ((t is null) == HasSpot || r != SpotRect) Layout();
    }

    public override void _Draw()
    {
        var dim = new Color(0, 0, 0, 0.62f);
        var full = new Rect2(Vector2.Zero, Size);
        if (!HasSpot) { DrawRect(full, dim); return; }
        var r = SpotRect;
        DrawRect(new Rect2(0, 0, full.Size.X, r.Position.Y), dim);
        DrawRect(new Rect2(0, r.End.Y, full.Size.X, full.Size.Y - r.End.Y), dim);
        DrawRect(new Rect2(0, r.Position.Y, r.Position.X, r.Size.Y), dim);
        DrawRect(new Rect2(r.End.X, r.Position.Y, full.Size.X - r.End.X, r.Size.Y), dim);
        var accent = _host.Palette.Accent;
        DrawRect(r.Grow(2), new Color(accent, 0.35f), false, 5);
        DrawRect(r, accent, false, 2);
    }

    public override void _Input(InputEvent e)
    {
        if (!Visible || e is not InputEventKey { Pressed: true, Echo: false } k) return;
        switch (k.Keycode)
        {
            case Key.Escape: Close(false); break;
            case Key.Enter or Key.KpEnter or Key.Right: if (k.Keycode != Key.Right || App.Settings.Language != "ar") Next(); else Back(); break;
            case Key.Left: if (App.Settings.Language != "ar") Back(); else Next(); break;
            default: return;
        }
        GetViewport().SetInputAsHandled();
    }

    static string Tr(string s) => TranslationServer.Translate(s);
}

static class NodeExt
{
    public static T AddChildReturn<T>(this T parent, Node child) where T : Node { parent.AddChild(child); return parent; }
}
