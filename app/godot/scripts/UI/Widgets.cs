using Godot;

namespace Blazma.UI.Views;

/// <summary>Small helpers for building controls in code.</summary>
public static class W
{
    public static Label Label(string text, string? variation = null, bool translate = true)
    {
        var l = new Label { Text = text };
        if (variation is not null) l.ThemeTypeVariation = variation;
        if (!translate) l.AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled;
        return l;
    }

    public static Control Spacer() => new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };

    public static MarginContainer Margin(Control child, int l, int t, int r, int b)
    {
        var m = new MarginContainer();
        m.AddThemeConstantOverride("margin_left", l);
        m.AddThemeConstantOverride("margin_top", t);
        m.AddThemeConstantOverride("margin_right", r);
        m.AddThemeConstantOverride("margin_bottom", b);
        m.AddChild(child);
        return m;
    }

    public static void Fill(Control c) => c.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
}

/// <summary>Pill-shaped segmented toggle (Simple | Advanced, EN | عربي, Dark | Light).</summary>
public partial class Segmented : PanelContainer
{
    readonly ButtonGroup _group = new();
    readonly List<Button> _buttons = new();

    public event Action<int>? Selected;

    public Segmented(params string[] labels)
    {
        ThemeTypeVariation = "SegBox";
        ClipContents = true;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 0);
        AddChild(row);
        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var b = new Button
            {
                Text = labels[i], ToggleMode = true, ButtonGroup = _group, ThemeTypeVariation = "Seg",
                FocusMode = FocusModeEnum.None,
            };
            b.Pressed += () => Selected?.Invoke(index);
            row.AddChild(b);
            _buttons.Add(b);
        }
    }

    public Button this[int i] => _buttons[i];
    public void SetIndex(int index)
    {
        for (var i = 0; i < _buttons.Count; i++) _buttons[i].SetPressedNoSignal(i == index);
    }
}

/// <summary>Small filled status dot.</summary>
public partial class Dot : Control
{
    Color _color = Colors.Gray;
    public Dot() { CustomMinimumSize = new Vector2(8, 8); SizeFlagsVertical = SizeFlags.ShrinkCenter; MouseFilter = MouseFilterEnum.Ignore; }
    public void SetColor(Color c) { _color = c; QueueRedraw(); }
    public override void _Draw() => DrawCircle(Size / 2, 4f, _color);
}

/// <summary>Non-modal message that fades after a few seconds (validation errors etc.).</summary>
public partial class Toast : PanelContainer
{
    readonly Label _label = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, AutowrapMode = TextServer.AutowrapMode.WordSmart };
    readonly Dot _dot = new();
    readonly Button _action = new() { Visible = false, FocusMode = FocusModeEnum.None, MouseFilter = MouseFilterEnum.Stop };
    Action? _onAction;
    Tween? _tween;

    public Toast()
    {
        ThemeTypeVariation = "Toast";
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        AnchorLeft = 0.5f; AnchorRight = 0.5f; AnchorTop = 1f; AnchorBottom = 1f;
        GrowHorizontal = GrowDirection.Both; GrowVertical = GrowDirection.Begin;
        OffsetTop = -90; OffsetBottom = -44;
        MinimumSizeChanged += Reposition;
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddChild(_dot);
        _label.CustomMinimumSize = new Vector2(120, 0);
        _label.MouseFilter = MouseFilterEnum.Ignore;
        row.AddChild(_label);
        _action.Pressed += () => { var a = _onAction; HideNow(); a?.Invoke(); };
        row.AddChild(_action);
        AddChild(row);
    }

    /// <summary>Keep the toast centred above the bottom edge whatever its (wrapped) size is.</summary>
    void Reposition()
    {
        var m = GetCombinedMinimumSize();
        OffsetLeft = -m.X / 2; OffsetRight = m.X / 2;
        OffsetBottom = -44; OffsetTop = -44 - m.Y;
    }

    public string Message => _label.Text;
    public string ActionText => _action.Visible ? _action.Text : "";
    /// <summary>Press the toast's action button (used by tests).</summary>
    public void InvokeAction() => _action.EmitSignal(BaseButton.SignalName.Pressed);

    /// <summary>A toast with a button (for example "Unpin"). The text of both must already be translated.</summary>
    public void NotifyAction(string message, Color dotColor, string actionText, Action onAction)
    {
        _actionKey = actionText;
        _action.Text = TranslationServer.Translate(actionText);
        _action.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        _onAction = onAction;
        Present(message, dotColor, 7.5, true);
    }

    /// <summary>A toast built from a message key and format arguments, so it is re-translated if the language changes while it is visible.</summary>
    public void NotifyFormat(string key, Color dotColor, params string[] args)
    {
        _onAction = null;
        Present(key, dotColor, 4.5, false, args);
    }

    public void NotifyActionFormat(string key, string arg, Color dotColor, string actionText, Action onAction)
    {
        _actionKey = actionText;
        _action.Text = TranslationServer.Translate(actionText);
        _action.AutoTranslateMode = AutoTranslateModeEnum.Disabled;
        _onAction = onAction;
        Present(key, dotColor, 7.5, true, new[] { arg });
    }

    // The toast keeps the message key (not the translated text): a language switch re-translates it while it is still on screen.
    string _key = "", _actionKey = "";
    string[] _args = Array.Empty<string>();

    string Render() => _args.Length == 0 ? TranslationServer.Translate(_key)
        : string.Format(System.Globalization.CultureInfo.InvariantCulture, TranslationServer.Translate(_key), _args.Cast<object>().ToArray());

    public override void _Notification(int what)
    {
        if (what != NotificationTranslationChanged || !Visible || _key.Length == 0) return;
        _label.Text = Render();
        if (_action.Visible && _actionKey.Length > 0) _action.Text = TranslationServer.Translate(_actionKey);
        Reposition();
    }
    public bool Showing => Visible;
    public void HideNow() { _tween?.Kill(); Visible = false; }

    public void Notify(string message, Color dotColor)
    {
        _onAction = null;
        Present(message, dotColor, 4.5, false);
    }

    void Present(string message, Color dotColor, double seconds, bool withAction, string[]? args = null)
    {
        _key = message;
        _args = args ?? Array.Empty<string>();
        message = Render();
        _action.Visible = withAction;
        MouseFilter = withAction ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        _label.Text = message;
        _dot.SetColor(dotColor);
        _label.CustomMinimumSize = new Vector2(Mathf.Clamp(40 + message.Length * 7.5f, 120, 420), 0);
        Visible = true;
        Reposition();
        Modulate = Colors.White;
        _tween?.Kill();
        _tween = CreateTween();
        _tween.TweenInterval(seconds);
        _tween.TweenProperty(this, "modulate:a", 0.0, 0.4);
        _tween.TweenCallback(Callable.From(() => Visible = false));
    }
}

public static class Fmt
{
    public static string Rate(Blazma.Core.Timing.FrameRate r) => r switch
    {
        { Numerator: 24000, Denominator: 1001 } => "23.976",
        { Numerator: 30000, Denominator: 1001 } => "29.97",
        { Denominator: 1 } => r.Numerator.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => r.FramesPerSecond.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
    };
}
