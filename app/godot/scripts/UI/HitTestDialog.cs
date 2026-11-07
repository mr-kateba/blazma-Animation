using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Combat;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Motion;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 9: "Hit test…". Swings an attack clip at a defender and reports the first hit (combat.hit_test, swept: a fast swing cannot pass
/// through a thin target between two frames). The attacker stands at the origin facing forward; the defender is placed by its position
/// (X to the attacker's left, Z in front) and the direction it faces. On a hit the timeline jumps to the hit frame, the viewport draws
/// the defender's hurtboxes there and marks the contact point with an arrow along the contact normal, and "Generate reaction" turns the
/// hit into a procedural flinch clip for the defender (reaction.generate with the test's reactionArgs). The window stays open so the
/// placement can be adjusted and the test repeated.
/// </summary>
public partial class HitTestDialog : ConfirmationDialog
{
    readonly MainWindow _host;
    public OptionButton Attacker { get; } = P8.Option(250);
    public OptionButton AttackClip { get; } = P8.Option(250);
    public OptionButton Defender { get; } = P8.Option(250);
    public OptionButton DefenderClip { get; } = P8.Option(250);
    public SpinBox DefX { get; } = P8.Spin(-1000, 1000, 0.05, 0, " m");
    public SpinBox DefZ { get; } = P8.Spin(-1000, 1000, 0.05, 1, " m");
    public SpinBox DefFacing { get; } = P8.Spin(-360, 360, 5, 180, "°");
    public Button ReactionButton { get; } = CombatKit.Btn("Generate reaction", "Make a flinch clip for the defender from this hit: its direction, height and strength.", "Primary");
    public Button JumpButton { get; } = CombatKit.Btn("Go to hit frame", "Move the timeline to the frame of the first hit.");
    readonly Label _hint = W.Label("", "Faint", false);
    readonly Label _status = W.Label("", null, false);
    readonly Label _details = W.Label("", "Dim", false);
    readonly List<string> _charIds = new(), _atkIds = new(), _defClipIds = new();
    bool _filling;

    static AppController App => AppController.Instance;

    public bool IsOpen => Visible;
    public string StatusText => _status.Text;
    public string DetailsText => _details.Text;
    public string HintText => _hint.Text;
    public CommandResult? LastResult { get; private set; }
    public string? LastError { get; private set; }
    public string? LastReactionClipId { get; private set; }
    public JsonObject? FirstHit => LastResult?.Data?["firstHit"] as JsonObject;
    public bool RunEnabled => !GetOkButton().Disabled;

    public HitTestDialog(MainWindow host)
    {
        _host = host;
        Title = "Hit Test";
        OkButtonText = "Run hit test";
        CancelButtonText = "Close";
        Exclusive = false;
        MinSize = new Vector2I(560, 0);
        DialogHideOnOk = false;

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        AddChild(root);
        var intro = W.Label("Swing an attack at another character to see where and when it hits. The attacker stands at the middle facing forward.", "Dim");
        P8.Wrap(intro, 520);
        root.AddChild(intro);
        var grid = P8.Grid();
        root.AddChild(grid);
        P8.Row(grid, "Attacker", Attacker);
        P8.Row(grid, "Attack clip", AttackClip);
        P8.Row(grid, "Defender", Defender);
        P8.Row(grid, "Defender plays", DefenderClip);
        var place = new HBoxContainer();
        place.AddThemeConstantOverride("separation", 6);
        foreach (var (label, box, tip) in new[]
        {
            ("Left", DefX, "How far the defender stands to the attacker's left (negative: right), in metres."),
            ("Front", DefZ, "How far in front of the attacker the defender stands, in metres."),
            ("Faces", DefFacing, "The direction the defender faces: 180 looks at the attacker, 0 turns away."),
        })
        {
            place.AddChild(W.Label(label, "Dim"));
            box.CustomMinimumSize = new Vector2(88, 0); box.SizeFlagsHorizontal = Control.SizeFlags.Fill; box.TooltipText = tip;
            place.AddChild(box);
        }
        P8.Row(grid, "Defender stands", place);
        P8.Wrap(_hint, 520);
        root.AddChild(_hint);
        root.AddChild(new HSeparator());
        P8.Wrap(_status, 520); P8.Wrap(_details, 520);
        _status.TextDirection = Control.TextDirection.Auto; _details.TextDirection = Control.TextDirection.Auto;
        root.AddChild(_status);
        root.AddChild(_details);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(ReactionButton); row.AddChild(JumpButton);
        root.AddChild(row);
        ReactionButton.Disabled = JumpButton.Disabled = true;

        Attacker.ItemSelected += _ => { FillAttackClips(); UpdateButtons(); };
        AttackClip.ItemSelected += _ => UpdateButtons();
        Defender.ItemSelected += _ => { FillDefenderClips(); UpdateButtons(); };
        Confirmed += Run;
        CloseRequested += Hide;
        ReactionButton.Pressed += () => GenerateReaction();
        JumpButton.Pressed += JumpToHit;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady() && Visible) UpdateButtons();
    }

    // ---- opening ---------------------------------------------------------------------------------------------------------------

    public void Open()
    {
        var p = App.Session.Project;
        _filling = true;
        Attacker.Clear(); Defender.Clear(); _charIds.Clear();
        foreach (var c in p.Characters) { Attacker.AddItem(c.Name); Defender.AddItem(c.Name); _charIds.Add(c.Id.ToString()); }
        var focus = Math.Max(0, _charIds.IndexOf(App.FocusCharacterId ?? ""));
        if (_charIds.Count > 0) Attacker.Select(focus);
        var other = _charIds.FindIndex(id => id != (focus < _charIds.Count ? _charIds[focus] : ""));
        if (_charIds.Count > 0) Defender.Select(other >= 0 ? other : focus);
        FillAttackClips();
        FillDefenderClips();
        _status.Text = ""; _details.Text = ""; LastError = null;
        _status.RemoveThemeColorOverride("font_color");
        ReactionButton.Disabled = JumpButton.Disabled = true;
        _filling = false;
        UpdateButtons();
        PopupCentered(new Vector2I(600, 0));
    }

    void FillAttackClips()
    {
        var p = App.Session.Project;
        var keep = AttackClip.Selected >= 0 && AttackClip.Selected < _atkIds.Count ? _atkIds[AttackClip.Selected] : null;
        AttackClip.Clear(); _atkIds.Clear();
        var ch = Attacker.Selected >= 0 && Attacker.Selected < _charIds.Count ? p.FindCharacter(ObjectId.Parse(_charIds[Attacker.Selected])) : null;
        var rig = ch is null ? null : p.FindRig(ch.Rig);
        var fit = p.Clips.Where(c => rig is not null && (c.Skeleton.IsEmpty || c.Skeleton == rig.Id)).ToList();
        // clips with hitboxes first: they are the ones worth testing
        foreach (var c in fit.OrderByDescending(c => c.Combat?.Hitboxes.Count > 0))
        {
            AttackClip.AddItem(c.Combat?.Hitboxes.Count > 0 ? c.Name : c.Name + " (" + Tr("no hitbox") + ")");
            _atkIds.Add(c.Id.ToString());
        }
        var pick = keep is not null ? _atkIds.IndexOf(keep) : App.ActiveClipId is { } a ? _atkIds.IndexOf(a) : -1;
        if (_atkIds.Count > 0) AttackClip.Select(Math.Max(0, pick));
    }

    void FillDefenderClips()
    {
        var p = App.Session.Project;
        DefenderClip.Clear(); _defClipIds.Clear();
        DefenderClip.AddItem(Tr("Its current pose")); _defClipIds.Add("");
        var ch = Defender.Selected >= 0 && Defender.Selected < _charIds.Count ? p.FindCharacter(ObjectId.Parse(_charIds[Defender.Selected])) : null;
        var rig = ch is null ? null : p.FindRig(ch.Rig);
        foreach (var c in p.Clips.Where(c => rig is not null && !c.Skeleton.IsEmpty && c.Skeleton == rig.Id))
        {
            DefenderClip.AddItem(c.Name);
            _defClipIds.Add(c.Id.ToString());
        }
        DefenderClip.Select(0);
    }

    void UpdateButtons()
    {
        if (_filling) return;
        var ok = Attacker.Selected >= 0 && AttackClip.Selected >= 0 && Defender.Selected >= 0;
        GetOkButton().Disabled = !ok;
        var p = App.Session.Project;
        if (!ok) { _hint.Text = Tr("Add a character and an attack clip first."); return; }
        var clip = p.FindClip(ObjectId.Parse(_atkIds[AttackClip.Selected]));
        var def = p.FindCharacter(ObjectId.Parse(_charIds[Defender.Selected]));
        if (clip?.Combat?.Hitboxes.Count is null or 0) _hint.Text = Tr("This clip has no hitbox yet. Add one in the Combat panel (Hitboxes page).");
        else if (def?.Combat?.Hurtboxes.Count is null or 0) _hint.Text = Tr("The defender has no hurtboxes yet. Generate them in the Combat panel (Hurtboxes page).");
        else _hint.Text = "";
    }

    // ---- running --------------------------------------------------------------------------------------------------------------------

    public static string? FriendlyError(MainWindow host, CommandResult r) => host.CodeMessage(r.Error?.Code) ?? r.Error?.Code switch
    {
        "rig_mismatch" => TranslationServer.Translate("That clip animates a different character than the one chosen."),
        "too_large" => TranslationServer.Translate("The clip is too long or has too many shapes to test at once."),
        _ => null,
    };

    /// <summary>Run the test with the form's values. On a hit: select the attacker and clip, jump to the hit frame and show it in the viewport.</summary>
    public void Run()
    {
        if (GetOkButton().Disabled) return;
        var args = new JsonObject
        {
            ["attackerCharacterId"] = _charIds[Attacker.Selected], ["attackClipId"] = _atkIds[AttackClip.Selected], ["defenderCharacterId"] = _charIds[Defender.Selected],
            ["defenderX"] = DefX.Value, ["defenderZ"] = DefZ.Value, ["defenderFacing"] = DefFacing.Value,
        };
        var defClip = DefenderClip.Selected > 0 && DefenderClip.Selected < _defClipIds.Count ? _defClipIds[DefenderClip.Selected] : null;
        if (defClip is not null) args["defenderClipId"] = defClip;
        var r = App.Query("combat.hit_test", args);
        LastResult = r;
        _status.RemoveThemeColorOverride("font_color");
        if (!r.Ok)
        {
            LastError = r.Error?.Message ?? "";
            _status.Text = FriendlyError(_host, r) ?? string.Format(CultureInfo.InvariantCulture, Tr("The hit test could not run: {0}"), LastError);
            _status.AddThemeColorOverride("font_color", _host.Palette.Err);
            _details.Text = "";
            ReactionButton.Disabled = JumpButton.Disabled = true;
            App.SetHitView(null);
            return;
        }
        LastError = null;
        var data = (JsonObject)r.Data!;
        var tested = string.Format(CultureInfo.InvariantCulture, Tr("{0} frames tested."), CombatKit.Ltr((long)CombatKit.D(data["framesTested"])));
        var warn = r.Warnings.Count > 0 ? "\n" + string.Join("\n", r.Warnings.Take(3).Select(WarningText.Describe)) : "";
        if (data["firstHit"] is not JsonObject first)
        {
            _status.Text = Tr("No hit. The attack never touches the defender in these frames.");
            _status.AddThemeColorOverride("font_color", _host.Palette.Warn);
            _details.Text = Tr("Move the defender closer, check the hitbox size and active frames, or check that the hurtboxes cover the body.") + "\n" + tested + warn;
            ReactionButton.Disabled = JumpButton.Disabled = true;
            App.SetHitView(null);
            return;
        }

        var hitFrame = long.Parse(first["frame"]!.ToJsonString());
        _host.ShowHitTest(new HitTestView(_charIds[Attacker.Selected], _atkIds[AttackClip.Selected], _charIds[Defender.Selected], defClip, DefX.Value, DefZ.Value, DefFacing.Value, data), hitFrame);

        _status.Text = string.Format(CultureInfo.InvariantCulture, Tr("Hit on frame {0}: {1} touches {2}."), CombatKit.Ltr(hitFrame), (string?)first["hitbox"] ?? "", (string?)first["bone"] ?? (string?)first["hurtbox"] ?? "");
        _status.AddThemeColorOverride("font_color", _host.Palette.Ok);
        var lines = new List<string>
        {
            string.Format(CultureInfo.InvariantCulture, Tr("Exact moment: frame {0}"), P8.Ltr((CombatKit.D(first["time"])).ToString("0.###", CultureInfo.InvariantCulture))),
            string.Format(CultureInfo.InvariantCulture, Tr("Body part: {0}, zone: {1}"), (string?)first["bone"] ?? "—", CombatKit.ZoneLabel((string?)first["zone"])),
            string.Format(CultureInfo.InvariantCulture, Tr("Hit from: {0}"), CombatKit.SideLabel((string?)first["from"])),
            string.Format(CultureInfo.InvariantCulture, Tr("Strength: {0}%"), CombatKit.Ltr((long)Math.Round(CombatKit.D(first["strength"]) * 100))),
            string.Format(CultureInfo.InvariantCulture, Tr("Damage {0}, knockback {1} m, hit-stop {2} frames"), P8.Ltr(P8.Num(CombatKit.D(first["damage"]))), P8.Ltr(P8.Num(CombatKit.D(first["knockback"]))), P8.Ltr(P8.Num(CombatKit.D(first["hitstopFrames"])))),
        };
        if ((bool?)first["swept"] == true) lines.Add(Tr("A fast swing: the hit was caught between two frames, where it would have passed through."));
        lines.Add(data["reactionRule"] is JsonObject rule
            ? string.Format(CultureInfo.InvariantCulture, Tr("Matching reaction rule: {0}"), (string?)rule["name"] ?? "")
            : Tr("No reaction rule matches. A procedural flinch can be generated."));
        lines.Add(tested + (((data["hits"] as JsonArray)?.Count ?? 0) > 1 ? " " + string.Format(CultureInfo.InvariantCulture, Tr("{0} hits in all."), CombatKit.Ltr((data["hits"] as JsonArray)!.Count)) : "") + warn);
        _details.Text = string.Join("\n", lines);
        ReactionButton.Disabled = data["reactionArgs"] is null;
        JumpButton.Disabled = false;
    }

    void JumpToHit()
    {
        if (App.HitView is { } hv && hv.FirstHit is { } first) _host.ShowHitTest(hv, long.Parse(first["frame"]!.ToJsonString()));
    }

    /// <summary>reaction.generate with the test's reactionArgs: a new clip for the defender, selected and played.</summary>
    public string? GenerateReaction()
    {
        if (App.HitView is not { } hv || hv.ReactionArgs is not { } ra) return null;
        var args = (JsonObject)ra.DeepClone();
        args["name"] = string.Format(CultureInfo.InvariantCulture, Tr("Hit reaction {0}"), (string?)hv.FirstHit?["bone"] ?? "");
        // Start from the defender's pose in its clip at the hit, when it plays one.
        if (hv.DefenderClipId is { } dc && App.Session.Project.FindClip(ObjectId.Parse(dc)) is { } defClip
            && App.Session.Project.FindClip(ObjectId.Parse(hv.AttackClipId)) is { } atk && hv.FirstHit is { } fh)
        {
            var t = CombatEvaluator.TimeOf(CombatKit.D(fh["time"]), atk.Fps);
            args["baseClipId"] = dc;
            args["baseFrame"] = Math.Clamp(ClipEvaluator.PlaybackTime(defClip, t).ToFrame(defClip.Fps), 0, defClip.DurationFrames);
        }
        var r = _host.ExecuteQuiet("reaction.generate", args);
        if (!r.Ok)
        {
            _status.Text = FriendlyError(_host, r) ?? string.Format(CultureInfo.InvariantCulture, Tr("The reaction could not be generated: {0}"), r.Error?.Message ?? "");
            _status.AddThemeColorOverride("font_color", _host.Palette.Err);
            return null;
        }
        var id = (string?)r.Data?["clipId"];
        LastReactionClipId = id;
        if (id is not null) _host.ShowReaction(hv.DefenderId, id);
        _status.RemoveThemeColorOverride("font_color");
        _status.AddThemeColorOverride("font_color", _host.Palette.Ok);
        _status.Text = Tr("Reaction clip added. It is selected and playing on the defender.");
        return id;
    }

    // ---- test hooks ---------------------------------------------------------------------------------------------------------------

    void Pick(OptionButton box, List<string> ids, string id, Action? after = null) { var i = ids.IndexOf(id); if (i >= 0) { box.Select(i); after?.Invoke(); } }
    public void SelectAttacker(string id) { Pick(Attacker, _charIds, id, () => { FillAttackClips(); UpdateButtons(); }); }
    public void SelectAttackClip(string id) { Pick(AttackClip, _atkIds, id, UpdateButtons); }
    public void SelectDefender(string id) { Pick(Defender, _charIds, id, () => { FillDefenderClips(); UpdateButtons(); }); }
    public void SelectDefenderClip(string id) { Pick(DefenderClip, _defClipIds, id); }
    public void PressRun() => Run();
    public void PressReaction() => P8.Press(ReactionButton);
}
