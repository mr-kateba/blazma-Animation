using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Floating tool rail on the inline-start edge of the stage (mockup B): Select, Rotate, Move, IK pin | Mirror, Reset.
/// The buttons only call MainWindow actions; the selection state owns the active tool.
/// </summary>
public partial class ToolRail : PanelContainer
{
    public Button Select { get; }
    public Button Rotate { get; }
    public Button Move { get; }
    public Button Pin { get; }
    public Button Mirror { get; }
    public Button Reset { get; }

    readonly ButtonGroup _tools = new();
    readonly List<(Button b, string svg)> _icons = new();

    public ToolRail(MainWindow host, SelectionState sel)
    {
        ThemeTypeVariation = "Floating";
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 2);
        AddChild(W.Margin(v, 5, 5, 5, 5));

        Select = Add(v, "Select", "Select", "<path d='M6 3l12 9-5.5 1 3 6-2.5 1-3-6L6 18z'/>", true);
        Rotate = Add(v, "Rotate", "Rotate the selected bone", "<path d='M19 12a7 7 0 1 1-2.5-5.4'/><path d='M19 4v4h-4'/>", true);
        Move = Add(v, "Move", "Move an IK control or the pelvis", "<path d='M12 3v18M3 12h18M12 3l-3 3M12 3l3 3M12 21l-3-3M12 21l3-3M3 12l3-3M3 12l3 3M21 12l-3-3M21 12l-3 3'/>", true);
        Pin = Add(v, "IK pin", "Pin or unpin the selected hand or foot", "<path d='M12 14v7'/><path d='M8 21h8'/><circle cx='12' cy='8' r='4'/><circle cx='12' cy='8' r='1' fill='white'/>", false);
        v.AddChild(new HSeparator());
        Mirror = Add(v, "Mirror", "Mirror the pose left and right", "<path d='M12 3v18' stroke-dasharray='2 2.5'/><path d='M9 7L3 12l6 5z'/><path d='M15 7l6 5-6 5z'/>", false);
        Reset = Add(v, "Reset", "Reset the selected bone, or the whole pose when nothing is selected", "<path d='M4 12a8 8 0 1 0 2.5-5.8'/><path d='M4 4v4.5h4.5'/>", false);

        var keys = ShortcutMap.Instance;
        keys.Hint(Select, "Select", "tool.select"); keys.Hint(Rotate, "Rotate the selected bone", "tool.rotate");
        keys.Hint(Move, "Move an IK control or the pelvis", "tool.move"); keys.Hint(Pin, "Pin or unpin the selected hand or foot", "tool.pin");
        keys.Hint(Mirror, "Mirror the pose left and right", "tool.mirror");
        keys.Hint(Reset, "Reset the selected bone, or the whole pose when nothing is selected", "pose.resetBone");
        Select.Pressed += () => sel.SetTool(StageTool.Select);
        Rotate.Pressed += () => sel.SetTool(StageTool.Rotate);
        Move.Pressed += () => sel.SetTool(StageTool.Move);
        Pin.Pressed += host.TogglePinSelected;
        Mirror.Pressed += () => host.MirrorPose();
        Reset.Pressed += host.ResetSelected;
        sel.ToolChanged += SyncTool;
        SyncTool_(sel);

        void SyncTool_(SelectionState s)
        {
            // SetPressedNoSignal does not update the ButtonGroup, so every member is set explicitly.
            Select.SetPressedNoSignal(s.Tool == StageTool.Select);
            Rotate.SetPressedNoSignal(s.Tool == StageTool.Rotate);
            Move.SetPressedNoSignal(s.Tool == StageTool.Move);
        }
        void SyncTool() => SyncTool_(sel);
    }

    Button Add(VBoxContainer parent, string text, string tip, string svgBody, bool toggle)
    {
        var b = new Button
        {
            Text = text, TooltipText = tip, ThemeTypeVariation = "Rail", ToggleMode = true,
            IconAlignment = HorizontalAlignment.Center, VerticalIconAlignment = VerticalAlignment.Top,
            CustomMinimumSize = new Vector2(54, 50), FocusMode = FocusModeEnum.None, ExpandIcon = false,
            ClipText = false,
        };
        if (toggle) b.ButtonGroup = _tools;
        else b.ToggleMode = text == "IK pin";
        var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='22' height='22' viewBox='0 0 24 24' fill='none' stroke='white' " +
                  $"stroke-width='1.7' stroke-linecap='round' stroke-linejoin='round'>{svgBody}</svg>";
        var img = new Image();
        img.LoadSvgFromString(svg, 1.0f);
        b.Icon = ImageTexture.CreateFromImage(img);
        parent.AddChild(b);
        _icons.Add((b, svgBody));
        return b;
    }

    readonly Dictionary<Button, string> _labels = new();

    /// <summary>True when the rail shows icons only (a short stage: 720 or 768 px high windows with the timeline open).</summary>
    public bool Compact { get; private set; }

    /// <summary>Icons only (the names stay in the tooltips) when the stage is too short for the labelled rail.</summary>
    public void SetCompact(bool compact)
    {
        if (compact == Compact) return;
        Compact = compact;
        foreach (var (b, _) in _icons)
        {
            if (!_labels.ContainsKey(b)) _labels[b] = b.Text;
            b.Text = compact ? "" : _labels[b];
            b.CustomMinimumSize = compact ? new Vector2(40, 34) : new Vector2(54, 50);
            b.VerticalIconAlignment = compact ? VerticalAlignment.Center : VerticalAlignment.Top;
        }
    }

    /// <summary>Enable/disable according to the selection (called by MainWindow after every selection or pose change).</summary>
    public void Update(bool hasCharacter, bool canPin, bool pinned, bool canReset)
    {
        Select.Disabled = Rotate.Disabled = Move.Disabled = Mirror.Disabled = !hasCharacter;
        Pin.Disabled = !canPin;
        Pin.SetPressedNoSignal(pinned && canPin);
        Reset.Disabled = !canReset;
    }
}
