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
    public void SetIndex(int index) => _buttons[index].SetPressedNoSignal(true);
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
    Tween? _tween;

    public Toast()
    {
        ThemeTypeVariation = "Toast";
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        AnchorLeft = 0.5f; AnchorRight = 0.5f; AnchorTop = 1f; AnchorBottom = 1f;
        GrowHorizontal = GrowDirection.Both; GrowVertical = GrowDirection.Begin;
        OffsetTop = -90; OffsetBottom = -44;
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddChild(_dot);
        _label.CustomMinimumSize = new Vector2(120, 0);
        _label.MouseFilter = MouseFilterEnum.Ignore;
        row.AddChild(_label);
        AddChild(row);
    }

    public string Message => _label.Text;
    public bool Showing => Visible;
    public void HideNow() { _tween?.Kill(); Visible = false; }

    public void Notify(string message, Color dotColor)
    {
        _label.Text = message;
        _dot.SetColor(dotColor);
        _label.CustomMinimumSize = new Vector2(Mathf.Min(420, 40 + message.Length * 7.5f), 0);
        ResetSize();
        Visible = true;
        Modulate = Colors.White;
        _tween?.Kill();
        _tween = CreateTween();
        _tween.TweenInterval(4.5);
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
