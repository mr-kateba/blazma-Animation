using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Motion;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// The "Sequence" tab of the bottom dock (Advanced mode): clips placed one after another on a track with blended transitions
/// (docs/ARCHITECTURE.md §8.3). A top row chooses or creates the sequence; a second row adds clips (optionally matching the foot phase so
/// the transition slides less), plays the sequence in the viewport and bakes it into a normal clip (optionally locking the feet) or
/// bakes and exports it; the track shows the items as bars; the bottom row edits the selected item (start, in, out, speed, repeats,
/// fade-in length and curve). Every edit is a sequence.* command, so it is undoable; undo and redo replace the project's sequence list,
/// so everything is looked up by id after each change.
/// </summary>
public partial class SequenceView : VBoxContainer
{
    static readonly string[] CurveIds = { "smooth", "linear", "ease_in", "ease_out" };
    static readonly string[] CurveNames = { "Smooth", "Linear", "Ease in", "Ease out" };

    readonly MainWindow _host;
    public SequenceCanvas Canvas { get; }

    public OptionButton SequencePick { get; } = P8.Option(170);
    public Button NewButton { get; } = new() { Text = "New…", FocusMode = FocusModeEnum.None, TooltipText = "Create an empty sequence for the selected character." };
    public Button RenameButton { get; } = new() { Text = "Rename…", FocusMode = FocusModeEnum.None };
    public Button DeleteButton { get; } = new() { Text = "Delete", FocusMode = FocusModeEnum.None, TooltipText = "Delete the sequence. The clips stay. Ctrl+Z brings it back." };
    public CheckBox AlignRoot { get; } = new() { Text = "Continue from the previous clip", FocusMode = FocusModeEnum.None, TooltipText = "Each clip starts where the previous one left the character, instead of jumping back to its own start." };
    public CheckBox AlignYaw { get; } = new() { Text = "Keep facing", FocusMode = FocusModeEnum.None, TooltipText = "Also turn each clip to face the way the character already faces." };
    public OptionButton ClipPick { get; } = P8.Option(170);
    public Button AddClipButton { get; } = new() { Text = "Add clip", FocusMode = FocusModeEnum.None, ThemeTypeVariation = "Primary", TooltipText = "Add the chosen clip at the end of the sequence, fading in over the end of the previous one." };
    public CheckBox MatchFeet { get; } = new() { Text = "Match foot phase", FocusMode = FocusModeEnum.None, TooltipText = "Start the new clip at the point of its stride that fits the feet at the transition, so they slide less." };
    public Button PlayButton { get; } = new() { Text = "Play", FocusMode = FocusModeEnum.None, ThemeTypeVariation = "Transport", ToggleMode = false, TooltipText = "Play the sequence in the viewport (Space)." };
    public Button BakeButton { get; } = new() { Text = "Bake to clip", FocusMode = FocusModeEnum.None, TooltipText = "Turn the sequence into a normal clip that plays exactly the same. The sequence stays." };
    public CheckBox BakeLock { get; } = new() { Text = "Lock feet", FocusMode = FocusModeEnum.None, TooltipText = "Also lock the feet on the ground in the baked clip." };
    public Button ExportButton { get; } = new() { Text = "Export…", FocusMode = FocusModeEnum.None, TooltipText = "Bake the sequence into a clip, then open the export dialog for it." };
    public SpinBox Start { get; } = P8.Spin(0, 10_000_000, 1, 0, "", true);
    public SpinBox In { get; } = P8.Spin(0, 10_000_000, 1, 0, "", true);
    public SpinBox Out { get; } = P8.Spin(1, 10_000_000, 1, 1, "", true);
    public SpinBox Speed { get; } = P8.Spin(0.1, 10, 0.1, 1, "×");
    public SpinBox Loops { get; } = P8.Spin(1, 1000, 1, 1, "", true);
    public SpinBox Blend { get; } = P8.Spin(0, 10_000, 1, 0, "", true);
    public OptionButton Curve { get; } = P8.Option(0);
    public Button RemoveItem { get; } = new() { Text = "Remove clip", FocusMode = FocusModeEnum.None, TooltipText = "Remove this clip from the sequence. The clip itself is not deleted." };
    readonly Label _frame = W.Label("", "MonoLabel", false);
    readonly Label _info = W.Label("", "Faint", false);
    readonly Label _itemCaption = W.Label("", "Dim", false);
    readonly HBoxContainer _itemRow = new();
    readonly List<string> _seqIds = new(), _clipIds = new();
    string _sigSeq = "", _sigClips = "";
    bool _syncing;

    static AppController App => AppController.Instance;

    public string? SelectedItemId => Canvas.SelectedItemId;
    public string InfoText => _info.Text;
    public string FrameText => _frame.Text;
    public bool ItemRowVisible => _itemRow.Visible;
    public string ItemCaptionText => _itemCaption.Text;

    public SequenceView(MainWindow host)
    {
        _host = host;
        LayoutDirection = LayoutDirectionEnum.Ltr;
        AddThemeConstantOverride("separation", 6);

        // row 1: which sequence
        var r1 = Row();
        r1.AddChild(W.Label("Sequence", "Dim"));
        r1.AddChild(SequencePick);
        r1.AddChild(NewButton); r1.AddChild(RenameButton); r1.AddChild(DeleteButton);
        r1.AddChild(new VSeparator());
        r1.AddChild(AlignRoot); r1.AddChild(AlignYaw);
        r1.AddChild(W.Spacer());
        r1.AddChild(BakeLock); r1.AddChild(BakeButton); r1.AddChild(ExportButton);
        AddChild(r1);

        // row 2: add clips and play
        var r2 = Row();
        r2.AddChild(W.Label("Clip", "Dim"));
        r2.AddChild(ClipPick);
        r2.AddChild(AddClipButton);
        r2.AddChild(MatchFeet);
        r2.AddChild(W.Spacer());
        r2.AddChild(PlayButton);
        _frame.TextDirection = TextDirection.Ltr; _frame.LocalizeNumeralSystem = false;
        r2.AddChild(_frame);
        AddChild(r2);

        Canvas = new SequenceCanvas(host);
        AddChild(Canvas);

        _info.TextDirection = TextDirection.Auto;
        AddChild(_info);

        // row 3: the selected item
        _itemRow.AddThemeConstantOverride("separation", 8);
        _itemCaption.TextDirection = TextDirection.Auto;
        _itemRow.AddChild(_itemCaption);
        void Field(string label, SpinBox box, string tip)
        {
            _itemRow.AddChild(W.Label(label, "Dim"));
            box.CustomMinimumSize = new Vector2(76, 0);
            box.SizeFlagsHorizontal = SizeFlags.Fill;
            box.TooltipText = tip;
            _itemRow.AddChild(box);
        }
        Field("Start", Start, "Sequence frame where this clip starts.");
        Field("From", In, "First frame of the clip that is played.");
        Field("To", Out, "Frame of the clip where each pass ends.");
        Field("Speed", Speed, "Playback speed: 2 is twice as fast.");
        Field("Repeat", Loops, "How many times the part from “From” to “To” is played.");
        Field("Fade in", Blend, "Frames over which this clip fades in over the previous clips.");
        for (var i = 0; i < CurveIds.Length; i++) Curve.AddItem(CurveNames[i], i);
        Curve.TooltipText = "How the fade-in progresses. Smooth keeps the motion free of sudden speed changes.";
        Curve.AutoTranslateMode = AutoTranslateModeEnum.Inherit;
        _itemRow.AddChild(Curve);
        _itemRow.AddChild(W.Spacer());
        _itemRow.AddChild(RemoveItem);
        AddChild(_itemRow);

        SequencePick.ItemSelected += i => { if (i >= 0 && i < _seqIds.Count) App.SetActiveSequence(_seqIds[(int)i]); };
        NewButton.Pressed += () => _host.PromptNewSequence();
        RenameButton.Pressed += () => { if (App.ActiveSequenceId is { } id) _host.PromptRenameSequence(id); };
        DeleteButton.Pressed += () => { if (App.ActiveSequenceId is { } id) _host.DeleteSequenceNow(id); };
        AlignRoot.Toggled += on => { if (!_syncing) _host.UpdateSequenceNow(alignRoot: on); };
        AlignYaw.Toggled += on => { if (!_syncing) _host.UpdateSequenceNow(alignYaw: on); };
        AddClipButton.Pressed += AddSelectedClip;
        PlayButton.Pressed += () => App.ToggleSequencePlay();
        BakeButton.Pressed += () => _host.BakeSequenceNow(BakeLock.ButtonPressed);
        ExportButton.Pressed += () => _host.ExportSequence(BakeLock.ButtonPressed);
        RemoveItem.Pressed += () => { if (SelectedItemId is { } id) { _host.RemoveSequenceItemNow(id); } };
        Canvas.SelectionChanged += SyncItem;
        Start.ValueChanged += v => EditItem("sequence.update_item", "startFrame", (long)v);
        In.ValueChanged += v => EditItem("sequence.update_item", "inFrame", (long)v);
        Out.ValueChanged += v => EditItem("sequence.update_item", "outFrame", (long)v);
        Speed.ValueChanged += v => EditItem("sequence.update_item", "speed", Math.Round(v, 3));
        Loops.ValueChanged += v => EditItem("sequence.update_item", "loops", (long)v);
        Blend.ValueChanged += v => EditItem("sequence.set_blend", "blendFrames", (long)v);
        Curve.ItemSelected += i => EditItem("sequence.set_blend", "blendCurve", CurveIds[(int)i]);

        App.SequenceStateChanged += Sync;
        App.SequenceFrameChanged += SyncFrame;
        App.SettingsChanged += Sync;
        App.SessionChanged += a =>
        {
            if (a.Changes.Any(c => c.Kind is Change.SequenceChanged or Change.ClipAdded or Change.ClipRemoved or Change.ClipChanged or Change.ProjectReplaced or Change.CharacterAdded or Change.CharacterRemoved))
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

    public override void _ExitTree()
    {
        App.SequenceStateChanged -= Sync;
        App.SequenceFrameChanged -= SyncFrame;
        App.SettingsChanged -= Sync;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsNodeReady()) { _sigSeq = ""; _sigClips = ""; Sync(); }
    }

    // ---- sync -------------------------------------------------------------------------------------------------------------------------

    void SyncFrame()
    {
        var ev = App.SequenceEval();
        var last = ev?.LastFrame ?? 0;
        _frame.Text = $"{(long)Math.Round(App.SequenceFrame)} / {last}";
        PlayButton.Text = Tr(App.SequencePlaying ? "Stop" : "Play");
        PlayButton.Disabled = ev is null;
    }

    public void Sync()
    {
        _syncing = true;
        try
        {
            var project = App.Session.Project;
            var seq = App.ActiveSequence;

            // sequences
            var sigSeq = string.Join("|", project.Sequences.Select(s => s.Id + s.Name)) + "#" + seq?.Id;
            if (sigSeq != _sigSeq)
            {
                _sigSeq = sigSeq;
                SequencePick.Clear(); _seqIds.Clear();
                foreach (var s in project.Sequences) { SequencePick.AddItem(s.Name); _seqIds.Add(s.Id.ToString()); }
                if (seq is not null) SequencePick.Select(_seqIds.IndexOf(seq.Id.ToString()));
                else SequencePick.AddItem(Tr("No sequence yet"));
            }
            SequencePick.Disabled = seq is null;
            RenameButton.Disabled = DeleteButton.Disabled = seq is null;
            NewButton.Disabled = project.Characters.Count == 0;
            AlignRoot.SetPressedNoSignal(seq?.AlignRoot ?? true);
            AlignYaw.SetPressedNoSignal(seq?.AlignYaw ?? true);
            AlignRoot.Disabled = AlignYaw.Disabled = seq is null;
            AlignYaw.Disabled |= seq is { AlignRoot: false };

            // clips that fit this sequence's character
            var rig = seq is null ? null : project.FindCharacter(seq.Character) is { } ch ? project.FindRig(ch.Rig) : null;
            var fit = project.Clips.Where(c => seq is not null && rig is not null && (c.Skeleton.IsEmpty || c.Skeleton == rig.Id)).ToList();
            var sigClips = string.Join("|", fit.Select(c => c.Id + c.Name)) + TranslationServer.GetLocale();
            if (sigClips != _sigClips)
            {
                _sigClips = sigClips;
                var keep = ClipPick.Selected >= 0 && ClipPick.Selected < _clipIds.Count ? _clipIds[ClipPick.Selected] : null;
                ClipPick.Clear(); _clipIds.Clear();
                foreach (var c in fit) { ClipPick.AddItem(c.Name); _clipIds.Add(c.Id.ToString()); }
                var k = keep is null ? -1 : _clipIds.IndexOf(keep);
                if (_clipIds.Count > 0) ClipPick.Select(k >= 0 ? k : 0);
            }
            AddClipButton.Disabled = seq is null || _clipIds.Count == 0;
            MatchFeet.Disabled = AddClipButton.Disabled;
            var ev = App.SequenceEval();
            BakeButton.Disabled = ExportButton.Disabled = ev is null;
            BakeLock.Disabled = ev is null;
            SyncFrame();

            // info line
            if (seq is null) _info.Text = "";
            else if (_clipIds.Count == 0) _info.Text = Tr("This character has no clips to add yet. Key or generate a clip first.");
            else if (ev is not null && ev.Warnings.Count > 0) _info.Text = string.Format(CultureInfo.InvariantCulture, Tr("{0} items could not be played and are skipped."), P8.Ltr(ev.Warnings.Count.ToString(CultureInfo.InvariantCulture)));
            else _info.Text = "";

            if (Canvas.SelectedItemId is { } sel && seq?.Items.All(i => i.Id.ToString() != sel) != false) Canvas.SelectItem(null);
            SyncItem();
            Canvas.QueueRedraw();
        }
        finally { _syncing = false; }
    }

    /// <summary>Fill the item row from the selected item (looked up by id in the current sequence).</summary>
    void SyncItem()
    {
        var was = _syncing;
        _syncing = true;
        try
        {
            var seq = App.ActiveSequence;
            var item = Canvas.SelectedItemId is { } id ? seq?.Items.Find(i => i.Id.ToString() == id) : null;
            _itemRow.Visible = item is not null;
            if (item is null) return;
            var clip = App.Session.Project.FindClip(item.Clip);
            _itemCaption.Text = clip?.Name ?? "?";
            var length = clip?.DurationFrames ?? 1;
            In.MaxValue = Math.Max(0, length - 1);
            Out.MaxValue = Math.Max(1, length);
            var outFrame = item.OutFrame ?? (clip is null ? length : SequenceEvaluator.DefaultOut(clip).ToFrame(clip.Fps));
            Set(Start, item.StartFrame); Set(In, item.InFrame); Set(Out, outFrame); Set(Speed, item.Speed); Set(Loops, item.Loops); Set(Blend, item.BlendInFrames);
            var ci = Array.IndexOf(CurveIds, item.BlendCurve switch { BlendCurve.Linear => "linear", BlendCurve.EaseIn => "ease_in", BlendCurve.EaseOut => "ease_out", _ => "smooth" });
            if (Curve.Selected != ci) Curve.Select(ci);
        }
        finally { _syncing = was; }
    }

    static void Set(SpinBox box, double v)
    {
        if (Math.Abs(box.Value - v) > 1e-9 && !box.GetLineEdit().HasFocus()) box.SetValueNoSignal(v);
    }

    // ---- actions ---------------------------------------------------------------------------------------------------------------------

    void AddSelectedClip()
    {
        if (ClipPick.Selected < 0 || ClipPick.Selected >= _clipIds.Count || App.ActiveSequenceId is not { } sid) return;
        var r = _host.AddClipToSequenceNow(sid, _clipIds[ClipPick.Selected], MatchFeet.ButtonPressed);
        if (r.Ok && (string?)r.Data?["itemId"] is { } itemId) Canvas.SelectItem(itemId);
    }

    void EditItem(string command, string key, JsonNode value)
    {
        if (_syncing || Canvas.SelectedItemId is not { } itemId || App.ActiveSequenceId is not { } sid) return;
        var r = App.Execute(command, new JsonObject { ["sequenceId"] = sid, ["itemId"] = itemId, [key] = value });
        if (!r.Ok) SyncItem(); // put the old value back
    }

    // ---- test hooks ---------------------------------------------------------------------------------------------------------------------

    public void PressAdd() => P8.Press(AddClipButton);
    public void PressBake() => P8.Press(BakeButton);
    public void PressExport() => P8.Press(ExportButton);
    public void PressPlay() => P8.Press(PlayButton);
    public void PressNew() => P8.Press(NewButton);
    public void PressRemoveItem() => P8.Press(RemoveItem);

    public void ChooseClip(string clipId)
    {
        var i = _clipIds.IndexOf(clipId);
        if (i >= 0) ClipPick.Select(i);
    }

    public void SetField(SpinBox box, double value) => box.Value = value;
}
