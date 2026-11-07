using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Combat;
using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>One attack of the combo drawn on the combined timeline.</summary>
public sealed record ComboBar(string AttackId, int Index, Rect2 Rect, long Start, long End, long? CancelStart, long? CancelEnd, long Link, long Blend, string Label);

/// <summary>
/// The track of the combo editor: a ruler in combined-timeline frames and the combo's attacks on two alternating lanes. A bar spans the
/// attack's clip; the tinted start is its fade-in over the previous attack, the amber part is its cancel window (when the NEXT attack may
/// start) and the white tick is the link frame (where the next attack starts when baked). Red ticks below mark the frames where its
/// hitboxes are active. Click a bar to select the attack. Always left to right.
/// </summary>
public partial class ComboCanvas : Control
{
    public const float Gutter = 96, RulerH = 24, LaneH = 38, LaneGap = 6;
    readonly MainWindow _host;
    readonly FrameAxis _axis = new();
    readonly List<ComboBar> _bars = new();
    string? _hover;

    static AppController App => AppController.Instance;
    Palette P => _host.Palette;

    public FrameAxis Axis => _axis;
    public IReadOnlyList<ComboBar> Bars => _bars;
    public string? SelectedAttackId { get; set; }
    public ComboPlan? Plan { get; set; }
    public event Action? SelectionChanged;

    public ComboCanvas(MainWindow host)
    {
        _host = host;
        LayoutDirection = LayoutDirectionEnum.Ltr;
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(300, RulerH + 2 * LaneH + LaneGap + 10);
        SizeFlagsVertical = SizeFlags.ExpandFill;
        MouseExited += () => { if (_hover is not null) { _hover = null; QueueRedraw(); } };
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged) QueueRedraw();
    }

    static Color BarColor(Palette p, int i) => (i % 4) switch { 0 => p.Accent, 1 => p.Z, 2 => p.Ok, _ => p.X };

    void Layout()
    {
        _bars.Clear();
        var last = Math.Max(30, Plan?.LastFrame ?? 30);
        _axis.Left = Gutter;
        _axis.Fit(0, last, Math.Max(40, Size.X - Gutter), 14);
        if (Plan is null) return;
        var fps = Plan.Sequence.Fps;
        for (var i = 0; i < Plan.Attacks.Count; i++)
        {
            var a = Plan.Attacks[i];
            var len = ComboPlanner.ToSeq(a.LengthFrames, a.Clip, fps);
            var x0 = _axis.X(a.StartFrame) - (float)_axis.Ppf / 2f;
            var x1 = _axis.X(a.StartFrame + len) - (float)_axis.Ppf / 2f;
            var y = RulerH + 4 + (i % 2) * (LaneH + LaneGap);
            long? cs = a.CancelStartFrame is { } c0 ? a.StartFrame + ComboPlanner.ToSeq(c0, a.Clip, fps) : null;
            long? ce = a.CancelEndFrame is { } c1 ? a.StartFrame + ComboPlanner.ToSeq(c1, a.Clip, fps) : null;
            var label = (a.Attack.Input.Length > 0 ? a.Attack.Input + " · " : "") + a.Clip.Name;
            _bars.Add(new ComboBar(a.Attack.Id.ToString(), i, new Rect2(x0, y, Math.Max(2, x1 - x0), LaneH), a.StartFrame, a.StartFrame + len, cs, ce,
                a.StartFrame + ComboPlanner.ToSeq(a.LinkFrame, a.Clip, fps), a.BlendFrames, label));
        }
    }

    public override void _Draw()
    {
        var p = P;
        var w = Size.X;
        var h = Size.Y;
        Layout();
        DrawRect(new Rect2(0, 0, w, h), p.Panel2 with { A = 0.45f });
        DrawRect(new Rect2(0, 0, Gutter - 6, h), p.Panel);
        DrawRect(new Rect2(Gutter - 6, 0, 1, h), p.Line);
        var font = BlazmaTheme.Regular;
        DrawString(font, new Vector2(10, RulerH + 24), TlText.Fit(font, TranslationServer.Translate("Attacks"), 11, Gutter - 22), HorizontalAlignment.Left, -1, 11, p.Text2);
        DrawLine(new Vector2(Gutter, RulerH), new Vector2(w, RulerH), p.LineStrong);
        var step = _axis.TickStep(40);
        var first = (long)Math.Floor(_axis.FrameAt(Gutter)) - 1;
        var lastF = (long)Math.Ceiling(_axis.FrameAt(w)) + 1;
        for (var f = Math.Max(0, first - first % step); f <= lastF; f += step)
        {
            var x = _axis.X(f);
            if (x < Gutter - 2 || x > w) continue;
            DrawLine(new Vector2(x, RulerH - 5), new Vector2(x, RulerH), p.Text3);
            DrawString(BlazmaTheme.Mono, new Vector2(x + 3, RulerH - 8), f.ToString(CultureInfo.InvariantCulture), HorizontalAlignment.Left, -1, 10, p.Text3);
            DrawLine(new Vector2(x, RulerH), new Vector2(x, h), p.Line with { A = 0.35f });
        }
        if (Plan is null)
        {
            DrawString(font, new Vector2(Gutter + 12, RulerH + 30), TlText.Fit(font, TranslationServer.Translate("Add attacks to see the combined timeline."), 12, Math.Max(40, w - Gutter - 24)),
                HorizontalAlignment.Left, -1, 12, p.Text3);
            return;
        }
        var fps = Plan.Sequence.Fps;
        foreach (var bar in _bars)
        {
            var col = BarColor(p, bar.Index);
            var sel = bar.AttackId == SelectedAttackId;
            var r = bar.Rect;
            DrawRect(r, col with { A = sel ? 0.95f : 0.72f });
            var at = Plan.Attacks[bar.Index];
            // fade-in over the previous attack
            if (bar.Blend > 0 && bar.Index > 0)
                DrawColoredPolygon(new[] { new Vector2(r.Position.X, r.End.Y), new Vector2(r.Position.X + (float)(_axis.Ppf * bar.Blend), r.Position.Y), new Vector2(r.Position.X, r.Position.Y) }, p.Bg with { A = 0.45f });
            // hitbox active frames
            if (at.Clip.Combat is { } cc)
                foreach (var hb in cc.Hitboxes)
                    foreach (var (s, e) in CombatEvaluator.ActiveFrames(hb, at.Clip.Fps))
                    {
                        var hx0 = _axis.X(bar.Start + ComboPlanner.ToSeq(s, at.Clip, fps)) - (float)_axis.Ppf / 2f;
                        var hx1 = Math.Min(_axis.X(Math.Min(bar.Start + ComboPlanner.ToSeq(e, at.Clip, fps), at.EndFrame)) - (float)_axis.Ppf / 2f, r.End.X);
                        if (hx1 > hx0) DrawRect(new Rect2(hx0, r.End.Y - 5, hx1 - hx0, 4), p.Err);
                    }
            // cancel window
            if (bar.CancelStart is { } cs && bar.CancelEnd is { } ce)
            {
                var cx0 = _axis.X(cs) - (float)_axis.Ppf / 2f; var cx1 = _axis.X(ce) - (float)_axis.Ppf / 2f;
                DrawRect(new Rect2(cx0, r.Position.Y, Math.Max(1, cx1 - cx0), 8), p.Warn);
            }
            var lx = _axis.X(bar.Link) - (float)_axis.Ppf / 2f;
            if (bar.Index + 1 < _bars.Count)
            {
                DrawLine(new Vector2(lx, r.Position.Y - 2), new Vector2(lx, r.End.Y + 2), p.Text, 2f);
                DrawColoredPolygon(new[] { new Vector2(lx - 4, r.Position.Y - 5), new Vector2(lx + 4, r.Position.Y - 5), new Vector2(lx, r.Position.Y + 1) }, p.Text);
            }
            DrawRect(r, sel ? p.Text : (_hover == bar.AttackId ? p.Text2 : p.Bg with { A = 0.6f }), false, sel ? 2f : 1f);
            var text = TlText.Fit(font, bar.Label, 11, Math.Max(0, r.Size.X - 10));
            if (text.Length > 0) DrawString(font, new Vector2(r.Position.X + 6, r.Position.Y + 25), text, HorizontalAlignment.Left, -1, 11, p.OnAccent);
        }
    }

    ComboBar? BarAt(Vector2 pos) => _bars.FirstOrDefault(b => b.Rect.Grow(2).HasPoint(pos));

    public override string _GetTooltip(Vector2 atPosition)
    {
        if (BarAt(atPosition) is not { } b || Plan is null) return "";
        var lines = new List<string>
        {
            b.Label,
            string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("Frames {0}–{1} on the combined timeline"), CombatKit.Ltr(b.Start), CombatKit.Ltr(b.End - 1)),
        };
        if (b.CancelStart is { } cs && b.CancelEnd is { } ce)
            lines.Add(string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("Cancel window: frames {0}–{1}"), CombatKit.Ltr(cs), CombatKit.Ltr(ce - 1)));
        if (b.Index + 1 < _bars.Count)
            lines.Add(string.Format(CultureInfo.InvariantCulture, TranslationServer.Translate("The next attack starts at frame {0}"), CombatKit.Ltr(b.Link)));
        return string.Join("\n", lines);
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion mm:
                var id = BarAt(mm.Position)?.AttackId;
                if (id != _hover) { _hover = id; QueueRedraw(); }
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mb:
                SelectedAttackId = BarAt(mb.Position)?.AttackId;
                SelectionChanged?.Invoke();
                QueueRedraw();
                AcceptEvent();
                break;
        }
    }

    public Vector2 BarCenter(string attackId)
    {
        Layout();
        return GlobalPosition + _bars.First(b => b.AttackId == attackId).Rect.GetCenter();
    }
}

/// <summary>
/// The "Combo" tab of the bottom dock (Advanced mode): chain attack clips into a combo (docs/ARCHITECTURE.md §9.5). A top row chooses or
/// creates a combo and bakes it into one normal clip (blended, root-aligned, with the attacks' hitboxes and events cut at each cancel) or
/// into an editable sequence; a second row adds an attack clip; the track shows the attacks on the combined timeline; the bottom row edits
/// the selected attack (input, cancel window, link frame, fade-in). Every edit is a combo.* command, so it is undoable.
/// </summary>
public partial class ComboView : VBoxContainer
{
    readonly MainWindow _host;
    public ComboCanvas Canvas { get; }

    public OptionButton ComboPick { get; } = P8.Option(170);
    public Button NewButton { get; } = CombatKit.Btn("New…", "Create an empty combo for the selected character.");
    public Button DeleteButton { get; } = CombatKit.Btn("Delete", "Delete the combo. The clips stay. Ctrl+Z brings it back.");
    public OptionButton ClipPick { get; } = P8.Option(170);
    public LineEdit InputEdit { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, CustomMinimumSize = new Vector2(90, 0), TooltipText = "The button or command that starts this attack, for example X or light." };
    public Button AddButton { get; } = CombatKit.Btn("Add attack", "Add the chosen clip at the end of the combo. It gets a cancel window over the recovery part when the clip has attack phases.", "Primary");
    public CheckBox BakeLock { get; } = new() { Text = "Lock feet", FocusMode = FocusModeEnum.None, TooltipText = "Also lock the feet on the ground in the baked clip." };
    public Button BakeClipButton { get; } = CombatKit.Btn("Bake to clip", "Turn the combo into one normal clip that plays all the attacks. The combo stays.");
    public Button BakeSequenceButton { get; } = CombatKit.Btn("Bake to sequence", "Turn the combo into a sequence you can edit further in the Sequence tab.");
    public SpinBox CancelStart { get; } = P8.Spin(0, 10_000_000, 1, 0, "", true);
    public SpinBox CancelEnd { get; } = P8.Spin(0, 10_000_000, 1, 0, "", true);
    public SpinBox Link { get; } = P8.Spin(0, 10_000_000, 1, 0, "", true);
    public SpinBox Blend { get; } = P8.Spin(0, 600, 1, 3, "", true);
    public LineEdit SelInput { get; } = new() { AutoTranslateMode = AutoTranslateModeEnum.Disabled, CustomMinimumSize = new Vector2(80, 0) };
    public Button ApplyButton { get; } = CombatKit.Btn("Apply", "Change the selected attack.", "Primary");
    public Button ClearWindowButton { get; } = CombatKit.Btn("No cancel window", "Remove the cancel window and link: the next attack starts near the end of this one.");
    public Button RemoveButton { get; } = CombatKit.Btn("Remove attack", "Remove this attack from the combo. The clip itself is not deleted.");
    readonly Label _info = W.Label("", "Faint", false);
    readonly Label _caption = W.Label("", "Dim", false);
    readonly HBoxContainer _attackRow = new();
    readonly List<string> _comboIds = new(), _clipIds = new();
    string _sigCombo = "", _sigClips = "", _sigAttack = "";
    bool _syncing;
    ulong _planVersion = ulong.MaxValue;
    string? _planFor;

    static AppController App => AppController.Instance;

    public string InfoText => _info.Text;
    public string CaptionText => _caption.Text;
    public bool AttackRowVisible => _attackRow.Visible;
    public string? SelectedAttackId => Canvas.SelectedAttackId;
    public string? ActiveComboId => _comboIds.Count > 0 && ComboPick.Selected >= 0 && ComboPick.Selected < _comboIds.Count ? _comboIds[ComboPick.Selected] : null;
    public Combo? ActiveCombo => ActiveComboId is { } id ? App.Session.Project.Combos.Find(c => c.Id.ToString() == id) : null;

    public ComboView(MainWindow host)
    {
        _host = host;
        LayoutDirection = LayoutDirectionEnum.Ltr;
        AddThemeConstantOverride("separation", 6);

        var r1 = Row();
        r1.AddChild(W.Label("Combo", "Dim"));
        r1.AddChild(ComboPick);
        r1.AddChild(NewButton); r1.AddChild(DeleteButton);
        r1.AddChild(W.Spacer());
        r1.AddChild(BakeLock); r1.AddChild(BakeClipButton); r1.AddChild(BakeSequenceButton);
        AddChild(r1);

        var r2 = Row();
        r2.AddChild(W.Label("Attack clip", "Dim"));
        r2.AddChild(ClipPick);
        r2.AddChild(W.Label("Input", "Dim"));
        r2.AddChild(InputEdit);
        r2.AddChild(AddButton);
        r2.AddChild(W.Spacer());
        _info.TextDirection = TextDirection.Auto;
        r2.AddChild(_info);
        AddChild(r2);

        Canvas = new ComboCanvas(host);
        AddChild(Canvas);

        _attackRow.AddThemeConstantOverride("separation", 8);
        _caption.TextDirection = TextDirection.Auto;
        _attackRow.AddChild(_caption);
        void Field(string label, Control box, string tip)
        {
            _attackRow.AddChild(W.Label(label, "Dim"));
            box.CustomMinimumSize = new Vector2(Math.Max(box.CustomMinimumSize.X, 76), 0);
            box.TooltipText = tip;
            _attackRow.AddChild(box);
        }
        Field("Input", SelInput, "The button or command that starts this attack.");
        Field("Cancel from", CancelStart, "First frame of this clip where the next attack may start.");
        Field("Cancel to", CancelEnd, "Where the cancel window ends. This frame is not included.");
        Field("Link at", Link, "Frame of this clip where the next attack starts when the combo is baked. 0: the start of the cancel window.");
        Field("Fade in", Blend, "Frames over which this attack fades in over the one before it.");
        _attackRow.AddChild(W.Spacer());
        _attackRow.AddChild(ApplyButton); _attackRow.AddChild(ClearWindowButton); _attackRow.AddChild(RemoveButton);
        AddChild(_attackRow);

        ComboPick.ItemSelected += _ => { Canvas.SelectedAttackId = null; Sync(); };
        NewButton.Pressed += () => _host.PromptNewCombo();
        DeleteButton.Pressed += () => { if (ActiveComboId is { } id) _host.DeleteComboNow(id); };
        AddButton.Pressed += AddSelectedClip;
        BakeClipButton.Pressed += () => { if (ActiveComboId is { } id) _host.BakeComboNow(id, false, BakeLock.ButtonPressed); };
        BakeSequenceButton.Pressed += () => { if (ActiveComboId is { } id) _host.BakeComboNow(id, true, false); };
        ApplyButton.Pressed += ApplyAttack;
        ClearWindowButton.Pressed += () => EditAttack(new JsonObject { ["clear"] = true });
        RemoveButton.Pressed += () => { if (ActiveComboId is { } id && SelectedAttackId is { } aid) _host.RemoveComboAttackNow(id, aid); };
        Canvas.SelectionChanged += () => { _sigAttack = ""; SyncAttack(); };

        App.SettingsChanged += Sync;
        App.SessionChanged += a =>
        {
            if (a.Changes.Any(c => c.Kind is Change.ComboChanged or Change.ClipAdded or Change.ClipRemoved or Change.ClipChanged or Change.ProjectReplaced or Change.CharacterAdded or Change.CharacterRemoved))
                Sync();
        };
        Sync();
    }

    static HBoxContainer Row()
    {
        var r = new HBoxContainer { LayoutDirection = LayoutDirectionEnum.ApplicationLocale };
        r.AddThemeConstantOverride("separation", 8);
        return r;
    }

    public override void _ExitTree() => App.SettingsChanged -= Sync;

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady()) { _sigCombo = ""; _sigClips = ""; _sigAttack = ""; Sync(); }
    }

    /// <summary>Make a combo the active one (used after creating it).</summary>
    public void SelectCombo(string id)
    {
        Sync();
        var i = _comboIds.IndexOf(id);
        if (i >= 0) ComboPick.Select(i);
        Sync();
    }

    ComboPlan? PlanOf(Combo? combo, out string? error)
    {
        error = null;
        if (combo is null) return null;
        try { return ComboPlanner.Plan(App.Session.Project, combo); }
        catch (CombatException e) { error = e.Code; return null; }
    }

    public void Sync()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            var project = App.Session.Project;
            var chars = project.Characters;
            var combos = project.Combos;
            var sigCombo = string.Join("|", combos.Select(c => c.Id + c.Name)) + "#" + ActiveComboId + TranslationServer.GetLocale();
            if (sigCombo != _sigCombo)
            {
                var keep = ActiveComboId;
                _sigCombo = sigCombo;
                ComboPick.Clear(); _comboIds.Clear();
                foreach (var c in combos) { ComboPick.AddItem(c.Name); _comboIds.Add(c.Id.ToString()); }
                if (combos.Count == 0) ComboPick.AddItem(Tr("No combo yet"));
                else ComboPick.Select(Math.Max(0, keep is null ? 0 : _comboIds.IndexOf(keep)));
            }
            var combo = ActiveCombo;
            ComboPick.Disabled = combo is null;
            DeleteButton.Disabled = combo is null;
            NewButton.Disabled = chars.Count == 0;

            var character = combo is null ? null : project.FindCharacter(combo.Character);
            var rig = character is null ? null : project.FindRig(character.Rig);
            var fit = project.Clips.Where(c => combo is not null && rig is not null && (c.Skeleton.IsEmpty || c.Skeleton == rig.Id)).ToList();
            var sigClips = string.Join("|", fit.Select(c => c.Id + c.Name + (c.Combat?.Hitboxes.Count ?? 0))) + TranslationServer.GetLocale();
            if (sigClips != _sigClips)
            {
                _sigClips = sigClips;
                var keep = ClipPick.Selected >= 0 && ClipPick.Selected < _clipIds.Count ? _clipIds[ClipPick.Selected] : null;
                ClipPick.Clear(); _clipIds.Clear();
                foreach (var c in fit)
                {
                    var n = c.Combat?.Hitboxes.Count ?? 0;
                    ClipPick.AddItem(n > 0 ? c.Name : c.Name + " (" + Tr("no hitbox") + ")");
                    _clipIds.Add(c.Id.ToString());
                }
                var k = keep is null ? -1 : _clipIds.IndexOf(keep);
                if (_clipIds.Count > 0) ClipPick.Select(k >= 0 ? k : 0);
            }
            AddButton.Disabled = combo is null || _clipIds.Count == 0;

            var plan = PlanOf(combo, out var planError);
            Canvas.Plan = plan;
            BakeClipButton.Disabled = BakeSequenceButton.Disabled = plan is null;
            BakeLock.Disabled = plan is null;
            if (combo is null) _info.Text = chars.Count == 0 ? "" : Tr("Create a combo, then add the attack clips in order.");
            else if (combo.Attacks.Count == 0) _info.Text = _clipIds.Count == 0 ? Tr("This character has no clips to add yet. Key or generate an attack clip first.") : Tr("Add the first attack.");
            else if (plan is null) _info.Text = planError == "empty_combo" ? Tr("None of the attacks can be played. Their clips are missing or belong to another character.") : "";
            else _info.Text = string.Format(CultureInfo.InvariantCulture, Tr("{0} attacks, {1} frames in all."), CombatKit.Ltr(plan.Attacks.Count), CombatKit.Ltr(plan.LastFrame + 1));

            if (Canvas.SelectedAttackId is { } sel && combo?.Attacks.All(a => a.Id.ToString() != sel) != false) Canvas.SelectedAttackId = null;
            SyncAttack();
            Canvas.QueueRedraw();
        }
        finally { _syncing = false; }
    }

    void SyncAttack()
    {
        var combo = ActiveCombo;
        var attack = combo?.Attacks.Find(a => a.Id.ToString() == Canvas.SelectedAttackId);
        _attackRow.Visible = attack is not null;
        if (attack is null || combo is null) { _sigAttack = ""; return; }
        var clip = App.Session.Project.FindClip(attack.Clip);
        var plan = PlanOf(combo, out _);
        var planned = plan?.Attacks.FirstOrDefault(p => p.Attack.Id == attack.Id);
        _caption.Text = clip?.Name ?? "?";
        var len = clip?.DurationFrames ?? 1;
        var sig = $"{attack.Id}|{attack.Input}|{attack.CancelStart?.Value}|{attack.CancelEnd?.Value}|{attack.Link?.Value}|{attack.BlendFrames}|{len}";
        if (sig == _sigAttack) return;
        _sigAttack = sig;
        var was = _syncing;
        _syncing = true;
        CancelStart.MaxValue = CancelEnd.MaxValue = Link.MaxValue = Math.Max(1, len);
        SelInput.Text = attack.Input;
        CombatKit.Set(CancelStart, clip is not null && attack.CancelStart is { } cs ? cs.ToFrame(clip.Fps) : 0);
        CombatKit.Set(CancelEnd, clip is not null && attack.CancelEnd is { } ce ? ce.ToFrame(clip.Fps) : 0);
        CombatKit.Set(Link, clip is not null && attack.Link is { } l ? l.ToFrame(clip.Fps) : 0);
        CombatKit.Set(Blend, attack.BlendFrames);
        _ = planned;
        ClearWindowButton.Disabled = attack.CancelStart is null;
        _syncing = was;
    }

    void AddSelectedClip()
    {
        if (ActiveComboId is not { } id || ClipPick.Selected < 0 || ClipPick.Selected >= _clipIds.Count) return;
        _host.AddComboAttackNow(id, _clipIds[ClipPick.Selected], InputEdit.Text.Trim());
    }

    void ApplyAttack()
    {
        var combo = ActiveCombo;
        var attack = combo?.Attacks.Find(a => a.Id.ToString() == Canvas.SelectedAttackId);
        var clip = attack is null ? null : App.Session.Project.FindClip(attack.Clip);
        if (attack is null || clip is null) return;
        var a = new JsonObject { ["input"] = SelInput.Text.Trim(), ["blendFrames"] = (long)Blend.Value };
        var cs = (long)CancelStart.Value; var ce = (long)CancelEnd.Value; var link = (long)Link.Value;
        if (ce > 0 || cs > 0)
        {
            if (!(cs < ce) || ce > clip.DurationFrames) { _host.Toast(Tr("The cancel window must start before it ends and stay inside the clip."), warn: true); return; }
            a["cancelStartFrame"] = cs; a["cancelEndFrame"] = ce;
            if (link > 0)
            {
                if (link < cs || link >= ce) { _host.Toast(Tr("The link frame must be inside the cancel window."), warn: true); return; }
                a["linkFrame"] = link;
            }
        }
        else if (link > 0) a["linkFrame"] = link;
        EditAttack(a);
    }

    void EditAttack(JsonObject a)
    {
        if (ActiveComboId is not { } id || SelectedAttackId is not { } aid) return;
        a["comboId"] = id; a["attackId"] = aid;
        App.Execute("combo.set_cancel_window", a);
        _sigAttack = "";
    }

    // ---- test hooks -----------------------------------------------------------------------------------------------------------------

    public void PressAdd() => P8.Press(AddButton);
    public void PressBakeClip() => P8.Press(BakeClipButton);
    public void PressBakeSequence() => P8.Press(BakeSequenceButton);
    public void PressApply() => P8.Press(ApplyButton);
    public void PressNew() => P8.Press(NewButton);
    public void PressRemove() => P8.Press(RemoveButton);

    public void ChooseClip(string clipId)
    {
        var i = _clipIds.IndexOf(clipId);
        if (i >= 0) ClipPick.Select(i);
    }
}
