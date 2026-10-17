using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Physics;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 6: bake secondary motion (follow-through, overlap) for the spine, head or arms of the active clip. The dialog collects the
/// preset, spring settings and range, then runs <c>physics.secondary_motion</c> (one undo step) and reports what was keyed.
/// Same pattern as the export dialog: it stays open to show the outcome.
/// </summary>
public partial class SecondaryMotionDialog : ConfirmationDialog
{
    static readonly string[] Presets = { "spine", "head", "arms" };

    readonly MainWindow _host;
    public OptionButton Preset { get; } = new() { FitToLongestItem = false, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    public SpinBox Stiffness { get; } = new() { MinValue = 1, MaxValue = SpringSettings.MaxStiffness, Step = 1, Value = 150 };
    public SpinBox Damping { get; } = new() { MinValue = 0, MaxValue = 200, Step = 0.5, Value = 12 };
    public SpinBox MaxAngle { get; } = new() { MinValue = 1, MaxValue = 180, Step = 1, Value = 30 };
    public CheckBox WholeClip { get; } = new() { Text = "Whole clip", ButtonPressed = true };
    public SpinBox From { get; } = new() { MinValue = 0, MaxValue = 10_000_000, Step = 1, Rounded = true };
    public SpinBox To { get; } = new() { MinValue = 0, MaxValue = 10_000_000, Step = 1, Rounded = true };
    readonly Label _feel = W.Label("", "Faint", false);
    readonly Label _status = W.Label("", null, false);
    readonly Label _details = W.Label("", "Dim", false);
    readonly VBoxContainer _result = new() { Visible = false };
    bool _updating;

    static AppController App => AppController.Instance;

    public CommandResult? LastResult { get; private set; }
    public string? LastError { get; private set; }
    public bool IsOpen => Visible;
    public string ResultText => _status.Text + "\n" + _details.Text;
    public string FeelText => _feel.Text;

    public SecondaryMotionDialog(MainWindow host)
    {
        _host = host;
        Title = "Secondary Motion";
        OkButtonText = "Apply";
        CancelButtonText = "Close";
        Exclusive = true;
        MinSize = new Vector2I(520, 0);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 12);
        AddChild(root);

        var intro = W.Label("Make part of the body lag behind the motion and settle, like a spring. The result is written as ordinary rotation keys, so you can still edit them.", "Dim");
        intro.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        intro.CustomMinimumSize = new Vector2(480, 0);
        root.AddChild(intro);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 10);
        root.AddChild(grid);

        Preset.AddItem("Spine and head", 0);
        Preset.AddItem("Head only", 1);
        Preset.AddItem("Arms", 2);
        Preset.TooltipText = "Which bones follow through: the spine and head, only the head and neck, or both arms.";
        Row(grid, "Bones", Preset);

        Fields(Stiffness, Damping, MaxAngle, From, To);
        Stiffness.TooltipText = "How strongly the bones are pulled back to the original motion. Higher is snappier.";
        Row(grid, "Stiffness", Stiffness);
        Damping.TooltipText = "How quickly the swinging dies out. Higher settles faster.";
        Row(grid, "Damping", Damping);
        MaxAngle.TooltipText = "The most a bone may lag behind, in degrees.";
        Row(grid, "Max lag (degrees)", MaxAngle);
        WholeClip.TooltipText = "Use every frame of the clip.";
        Row(grid, "Frame range", WholeClip);
        Row(grid, "From frame", From);
        Row(grid, "To frame", To);

        _feel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _feel.CustomMinimumSize = new Vector2(480, 0);
        root.AddChild(_feel);

        root.AddChild(new HSeparator());
        _result.AddThemeConstantOverride("separation", 6);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.CustomMinimumSize = new Vector2(480, 0);
        _result.AddChild(_status);
        _details.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _details.CustomMinimumSize = new Vector2(480, 0);
        _result.AddChild(_details);
        root.AddChild(_result);

        Preset.ItemSelected += _ => Changed();
        Stiffness.ValueChanged += _ => Changed();
        Damping.ValueChanged += _ => Changed();
        MaxAngle.ValueChanged += _ => Changed();
        WholeClip.Toggled += _ => { SyncRange(); Changed(); };
        From.ValueChanged += _ => { if (!_updating && From.Value > To.Value) To.SetValueNoSignal(From.Value); Changed(); };
        To.ValueChanged += _ => { if (!_updating && To.Value < From.Value) From.SetValueNoSignal(To.Value); Changed(); };

        DialogHideOnOk = false;
        Confirmed += () => Apply();
        CloseRequested += Hide;
    }

    static void Fields(params SpinBox[] boxes)
    {
        foreach (var s in boxes)
        {
            s.LocalizeNumeralSystem = false;
            s.GetLineEdit().LocalizeNumeralSystem = false;
            s.GetLineEdit().TextDirection = Control.TextDirection.Ltr;
            s.GetLineEdit().ThemeTypeVariation = "MonoEdit";
            s.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        }
    }

    static void Row(GridContainer grid, string label, Control field)
    {
        var l = W.Label(label, "Dim");
        l.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        grid.AddChild(l);
        field.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        grid.AddChild(field);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady() && Visible) RefreshFeel();
    }

    /// <summary>Fill the fields from the active clip and show the dialog.</summary>
    public void Open()
    {
        var clip = App.ActiveClip;
        _updating = true;
        var last = Math.Max(0, (clip?.DurationFrames ?? 1) - 1);
        From.MaxValue = last; To.MaxValue = last;
        From.SetValueNoSignal(0); To.SetValueNoSignal(last);
        WholeClip.SetPressedNoSignal(true);
        _updating = false;
        SyncRange();
        LastResult = null; LastError = null;
        _result.Visible = false;
        RefreshFeel();
        GetOkButton().Disabled = clip is null;
        PopupCentered(new Vector2I(560, 0));
    }

    void SyncRange()
    {
        var custom = !WholeClip.ButtonPressed;
        From.Editable = custom; To.Editable = custom;
        if (!custom && App.ActiveClip is { } clip)
        {
            var last = Math.Max(0, clip.DurationFrames - 1);
            From.SetValueNoSignal(0); To.SetValueNoSignal(last);
        }
    }

    void Changed()
    {
        _result.Visible = false;
        RefreshFeel();
    }

    void RefreshFeel()
    {
        var k = Stiffness.Value;
        var hz = Math.Sqrt(k) / (2 * Math.PI);
        var zeta = Damping.Value / (2 * Math.Sqrt(k));
        var kind = zeta < 0.2 ? Tr("very bouncy") : zeta < 0.7 ? Tr("springy") : zeta < 1.1 ? Tr("smooth") : Tr("heavy and slow");
        _feel.Text = string.Format(CultureInfo.InvariantCulture, Tr("About {0} swings per second, damping ratio {1}: {2}."),
            BalanceStrip.Ltr(hz.ToString("0.0", CultureInfo.InvariantCulture)), BalanceStrip.Ltr(zeta.ToString("0.00", CultureInfo.InvariantCulture)), kind);
    }

    /// <summary>Run the command with the dialog's settings. Returns true when keys were written.</summary>
    public bool Apply()
    {
        var phys = App.Physics;
        if (!phys.TryResolve(out var clip, out var character, out _))
        {
            Fail(Tr("Key a clip for this character first."));
            return false;
        }
        _host.Inspector.Flush();
        var args = new JsonObject
        {
            ["clipId"] = clip.Id.ToString(), ["characterId"] = character.Id.ToString(),
            ["preset"] = Presets[Math.Clamp(Preset.Selected, 0, 2)],
            ["stiffness"] = Stiffness.Value, ["damping"] = Damping.Value, ["maxAngle"] = MaxAngle.Value,
        };
        if (!WholeClip.ButtonPressed)
        {
            args["startFrame"] = (long)From.Value;
            args["endFrame"] = (long)To.Value;
        }
        // "Spine and head" is the spine chain plus neck and head; "Head only" the neck and head
        var r = _host.ExecuteQuiet("physics.secondary_motion", args);
        LastResult = r;
        if (!r.Ok) { Fail(Friendly(r.Error?.Code, r.Error?.Message ?? "")); return false; }
        LastError = null;
        var d = r.Data!;
        var keys = long.Parse(d["keys"]!.ToJsonString(), CultureInfo.InvariantCulture);
        var lag = double.Parse(d["maxLagDegrees"]!.ToJsonString(), CultureInfo.InvariantCulture);
        var bones = (d["bones"] as JsonArray)?.Count ?? 0;
        var pal = _host.Palette;
        _status.Text = Tr("Secondary motion added.");
        _status.AddThemeColorOverride("font_color", pal.Ok);
        var lines = new List<string>
        {
            string.Format(CultureInfo.InvariantCulture, Tr("{0} rotation keys on {1} bones. The biggest lag is {2} degrees."),
                BalanceStrip.Ltr(keys.ToString(CultureInfo.InvariantCulture)), BalanceStrip.Ltr(bones.ToString(CultureInfo.InvariantCulture)),
                BalanceStrip.Ltr(lag.ToString("0.#", CultureInfo.InvariantCulture))),
        };
        foreach (var w in r.Warnings)
            lines.Add(w.Code switch
            {
                "bones_missing" => Tr("Some bones of this preset are not mapped in this character and were skipped."),
                "ik_driven" => Tr("Arm and leg bones that an IK control drives are overridden while that control is pinned."),
                "track_locked" => Tr("Locked rotation tracks were skipped."),
                "track_muted" => Tr("Muted rotation tracks were skipped."),
                _ => w.Message,
            });
        lines.Add(Tr("Ctrl+Z undoes it."));
        _details.Text = string.Join("\n", lines);
        _result.Visible = true;
        _host.ToastNode.Notify(Tr("Secondary motion added."), pal.Ok);
        return true;
    }

    void Fail(string message)
    {
        LastError = message;
        _result.Visible = true;
        _status.Text = Tr("Nothing was changed.");
        _status.AddThemeColorOverride("font_color", _host.Palette.Warn);
        _details.Text = message;
    }

    string Friendly(string? code, string message) => code switch
    {
        "track_locked" => Tr("Every chosen bone's rotation track is locked. Unlock it in the dope sheet first."),
        "track_muted" => Tr("Every chosen bone's rotation track is muted. Unmute it first."),
        "non_finite" => Tr("Some frames contain invalid values, so nothing was changed."),
        "too_large" => Tr("That is too much to compute in one go. Use a shorter range."),
        "not_found" => Tr("None of the bones of this preset are mapped in this character."),
        "invalid_args" when message.Contains("at least 2 frames") => Tr("The range needs at least 2 frames."),
        "invalid_args" => Tr("The frame range is outside the clip."),
        "rig_mismatch" => Tr("This clip animates a different character."),
        _ => string.Format(Tr("Secondary motion failed: {0}"), message),
    };

    // ---- test hooks ----------------------------------------------------------------------------------------------------

    public void PressApply() => EmitSignal(ConfirmationDialog.SignalName.Confirmed);
}
