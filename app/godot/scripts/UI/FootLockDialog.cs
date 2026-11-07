using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Physics;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 8: "Foot lock…" for the active clip. Detects the frames where each foot is on the ground (a sole point lower than the contact
/// height and slower than the maximum speed) and, on request, keys the leg IK pinned on one anchor per contact so the feet stop
/// sliding (clip.foot_lock; one undo step). "Detect" only looks: it shows the contacts as a strip under the timeline.
/// </summary>
public partial class FootLockDialog : ConfirmationDialog
{
    readonly MainWindow _host;
    public SpinBox Threshold { get; } = P8.Spin(0.5, 30, 0.5, 3, " cm");
    public SpinBox MaxSpeed { get; } = P8.Spin(0.05, 5, 0.05, 0.35, " m/s");
    public SpinBox MinFrames { get; } = P8.Spin(1, 60, 1, 3, "", true);
    public SpinBox BlendFrames { get; } = P8.Spin(0, 30, 1, 3, "", true);
    public CheckBox Whole { get; } = new() { Text = "Whole clip", ButtonPressed = true, FocusMode = Control.FocusModeEnum.None };
    public SpinBox From { get; } = P8.Spin(0, 10_000_000, 1, 0, "", true);
    public SpinBox To { get; } = P8.Spin(0, 10_000_000, 1, 0, "", true);
    public CheckBox ShowStrip { get; } = new() { Text = "Show the contacts under the timeline", ButtonPressed = true, FocusMode = Control.FocusModeEnum.None };
    public Button Detect { get; } = new() { Text = "Detect contacts", FocusMode = Control.FocusModeEnum.None, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
    readonly Label _clipLabel = W.Label("", "Section", false);
    readonly Label _status = W.Label("", null, false);
    readonly Label _details = W.Label("", "Dim", false);

    static AppController App => AppController.Instance;

    public bool IsOpen => Visible;
    public string StatusText => _status.Text;
    public string DetailsText => _details.Text;
    public string? LastError { get; private set; }
    public CommandResult? LastResult { get; private set; }
    public CommandResult? LastDetect { get; private set; }
    public bool LockEnabled => !GetOkButton().Disabled;

    public FootLockDialog(MainWindow host)
    {
        _host = host;
        Title = "Foot Lock";
        OkButtonText = "Lock feet";
        CancelButtonText = "Close";
        Exclusive = true;
        MinSize = new Vector2I(540, 0);
        DialogHideOnOk = false;

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        AddChild(root);
        var intro = W.Label("Stop the feet from sliding: wherever a foot rests on the ground, Blazma holds it at one spot. Frames away from a contact keep exactly their pose. Ctrl+Z undoes it.", "Dim");
        P8.Wrap(intro, 500);
        root.AddChild(intro);
        root.AddChild(_clipLabel);
        var grid = P8.Grid();
        root.AddChild(grid);
        Threshold.TooltipText = "A foot counts as on the ground when its sole is lower than this.";
        MaxSpeed.TooltipText = "…and moves slower than this.";
        MinFrames.TooltipText = "Shorter contacts are ignored.";
        BlendFrames.TooltipText = "Frames over which the foot eases onto the spot and off it again.";
        P8.Row(grid, "Ground height", Threshold);
        P8.Row(grid, "Maximum foot speed", MaxSpeed);
        P8.Row(grid, "Shortest contact (frames)", MinFrames);
        P8.Row(grid, "Ease in and out (frames)", BlendFrames);
        grid.AddChild(new Control());
        grid.AddChild(Whole);
        P8.Row(grid, "From frame", From);
        P8.Row(grid, "To frame", To);
        grid.AddChild(new Control());
        grid.AddChild(ShowStrip);
        root.AddChild(Detect);
        root.AddChild(new HSeparator());
        P8.Wrap(_status, 500); P8.Wrap(_details, 500);
        root.AddChild(_status);
        root.AddChild(_details);

        Whole.Toggled += _ => SyncRange();
        Detect.Pressed += () => RunDetect();
        ShowStrip.Toggled += on => App.SetShowContacts(on);
        foreach (var s in new[] { Threshold, MaxSpeed, MinFrames, BlendFrames }) s.ValueChanged += _ => PushOptions();
        Confirmed += Lock;
        CloseRequested += Hide;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady() && Visible) Refresh();
    }

    FootLockOptions Options() => new()
    {
        ContactHeight = Threshold.Value / 100.0, ContactSpeed = MaxSpeed.Value, MinFrames = (int)MinFrames.Value, BlendFrames = (int)BlendFrames.Value,
    };

    void PushOptions() => App.SetContactOptions(Options());

    public void Open()
    {
        var o = App.ContactOptions;
        Threshold.SetValueNoSignal(Math.Round(o.ContactHeight * 100, 2));
        MaxSpeed.SetValueNoSignal(o.ContactSpeed);
        MinFrames.SetValueNoSignal(o.MinFrames);
        BlendFrames.SetValueNoSignal(o.BlendFrames);
        Whole.SetPressedNoSignal(true);
        ShowStrip.SetPressedNoSignal(true);
        App.SetShowContacts(true);
        _status.Text = ""; _details.Text = ""; LastError = null;
        _status.RemoveThemeColorOverride("font_color");
        SyncRange();
        Refresh();
        PopupCentered(new Vector2I(580, 0));
        RunDetect();
    }

    void SyncRange()
    {
        var len = App.ActiveClip?.DurationFrames ?? 0;
        From.MaxValue = len; To.MaxValue = len;
        if (Whole.ButtonPressed) { From.SetValueNoSignal(0); To.SetValueNoSignal(len); }
        From.Editable = To.Editable = !Whole.ButtonPressed;
    }

    void Refresh()
    {
        var clip = App.ActiveClip;
        _clipLabel.Text = clip is null ? Tr("No clip is selected.") : string.Format(CultureInfo.InvariantCulture, Tr("Clip: {0}"), clip.Name);
        var ok = App.Physics.TryResolve(out _, out _, out _) && App.Physics.HasFeet;
        GetOkButton().Disabled = !ok;
        Detect.Disabled = !ok;
        if (!ok && clip is not null)
        {
            _status.Text = App.Physics.TryResolve(out _, out _, out _)
                ? Tr("This character has no humanoid feet to lock. Map its bones first (Rigging > Bone Mapping).")
                : Tr("Key a clip for this character first, then lock its feet.");
            _status.AddThemeColorOverride("font_color", _host.Palette.Warn);
        }
    }

    JsonObject Args(bool lockIt)
    {
        var clip = App.ActiveClip!;
        var o = Options();
        var a = new JsonObject
        {
            ["clipId"] = clip.Id.ToString(), ["threshold"] = o.ContactHeight, ["maxSpeed"] = o.ContactSpeed, ["minFrames"] = o.MinFrames,
        };
        if (App.FocusCharacterId is { } cid) a["characterId"] = cid;
        if (lockIt) a["blendFrames"] = o.BlendFrames;
        if (!Whole.ButtonPressed) { a["startFrame"] = (long)From.Value; a["endFrame"] = (long)To.Value; }
        return a;
    }

    /// <summary>Look for contacts without changing the clip; the strip under the timeline shows them.</summary>
    public void RunDetect()
    {
        if (App.ActiveClip is null || Detect.Disabled) return;
        PushOptions();
        App.SetShowContacts(true);
        var r = _host.ExecuteQuiet("clip.contacts", Args(false));
        LastDetect = r;
        if (!r.Ok) { Fail(r); return; }
        var spans = (r.Data?["spans"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        _status.RemoveThemeColorOverride("font_color");
        if (spans.Count == 0)
        {
            _status.Text = Tr("No foot contact found. Try a higher ground height or a higher foot speed.");
            _details.Text = "";
            return;
        }
        _status.Text = string.Format(CultureInfo.InvariantCulture, Tr("{0} foot contacts found."), P8.Ltr(spans.Count.ToString(CultureInfo.InvariantCulture)));
        _details.Text = Describe(spans);
    }

    string Describe(List<JsonObject> spans)
    {
        string Side(string s) => string.Join(", ", spans.Where(x => (string?)x["side"] == s)
            .Select(x => P8.Ltr($"{(long)x["startFrame"]!}–{(long)x["endFrame"]!}")));
        var left = Side("left"); var right = Side("right");
        var lines = new List<string>();
        if (left.Length > 0) lines.Add(string.Format(CultureInfo.InvariantCulture, Tr("Left foot: frames {0}"), left));
        if (right.Length > 0) lines.Add(string.Format(CultureInfo.InvariantCulture, Tr("Right foot: frames {0}"), right));
        return string.Join("\n", lines);
    }

    /// <summary>Key the foot lock (OK button).</summary>
    public void Lock()
    {
        if (App.ActiveClip is null || GetOkButton().Disabled) return;
        PushOptions();
        var r = _host.ExecuteQuiet("clip.foot_lock", Args(true));
        LastResult = r;
        if (!r.Ok) { Fail(r); return; }
        LastError = null;
        var spans = (r.Data?["spans"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        var anchor = (double?)r.Data?["maxAnchorError"] ?? 0;
        var untouched = (double?)r.Data?["maxUntouchedChange"] ?? 0;
        _status.RemoveThemeColorOverride("font_color");
        _status.AddThemeColorOverride("font_color", _host.Palette.Ok);
        _status.Text = string.Format(CultureInfo.InvariantCulture, Tr("Locked {0} foot contacts."), P8.Ltr(spans.Count.ToString(CultureInfo.InvariantCulture)));
        _details.Text = Describe(spans) + "\n" + string.Format(CultureInfo.InvariantCulture, Tr("The feet stay within {0} mm of their spot. Frames outside the contacts changed by at most {1} mm."),
            P8.Ltr(P8.Num(anchor * 1000, "0.##")), P8.Ltr(P8.Num(untouched * 1000, "0.##")));
        _host.ToastNode.Notify("Feet locked. Ctrl+Z undoes it.", _host.Palette.Ok);
        App.ApplyFrameIfClean();
    }

    void Fail(CommandResult r)
    {
        LastError = r.Error?.Message ?? "";
        _status.Text = _host.CodeMessage(r.Error?.Code) ?? r.Error?.Code switch
        {
            "no_contacts" => Tr("No foot contact found in that range. Try a higher ground height or a higher foot speed."),
            "no_feet" => Tr("This character has no humanoid feet to lock. Map its bones first (Rigging > Bone Mapping)."),
            "no_leg_ik" => Tr("The feet have no leg IK, so they cannot be locked."),
            "track_locked" => Tr("A track needed for the lock is locked. Unlock it in the dope sheet first."),
            "track_muted" => Tr("A track needed for the lock is muted. Unmute it in the dope sheet first."),
            "too_large" => Tr("That range is too long. Lock a shorter range."),
            _ => string.Format(CultureInfo.InvariantCulture, Tr("Foot lock failed: {0}"), LastError),
        };
        _status.AddThemeColorOverride("font_color", _host.Palette.Err);
        _details.Text = "";
    }

    // ---- test hooks --------------------------------------------------------------------------------------------------------------

    public void PressLock() => EmitSignal(ConfirmationDialog.SignalName.Confirmed);
    public void PressDetect() => P8.Press(Detect);
}
