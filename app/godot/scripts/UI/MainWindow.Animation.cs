using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Phase 3 part of the shell: timeline dock, keying actions, markers, motion trail / onion skin and the Animation menu.</summary>
public partial class MainWindow
{
    const int MKey = 70, MKeySel = 71, MDelKey = 72, MAutoKey = 73, MPlay = 74, MLoop = 75, MStart = 76, MEnd = 77, MPrevKey = 78, MNextKey = 79,
        MAddMarker = 80, MAddPhases = 81, MTrail = 50, MOnion = 51, MOnionBase = 60;
    static readonly int[] OnionChoices = { 2, 3, 5, 8, 10 };

    public TimelinePanel Timeline { get; private set; } = null!;
    public MotionOverlay Motion { get; private set; } = null!;
    public Button TrailButton { get; private set; } = null!;
    public Button OnionButton { get; private set; } = null!;
    PanelContainer _viewTools = null!;
    PopupMenu _mOnion = null!;
    bool _historyStale;

    // ---- construction ------------------------------------------------------------------------------------------------

    void BuildMotionLayers()
    {
        Motion = new MotionOverlay(Overlay, Characters, Selection);
        W.Fill(Motion);
        _center.AddChild(Motion);
        _center.MoveChild(Motion, Overlay.GetIndex());

        _viewTools = new PanelContainer { ThemeTypeVariation = "Floating" };
        var row = new HBoxContainer { LayoutDirection = LayoutDirectionEnum.Ltr };
        row.AddThemeConstantOverride("separation", 4);
        TrailButton = ViewToggle("Motion trail", "Show the path of the selected bone over the whole clip.",
            "<path d='M2 12c3-8 6 2 9-6 1-2 3-2 3-2'/><circle cx='13.5' cy='4' r='1.4'/>", on => App.SetShowTrail(on));
        OnionButton = ViewToggle("Onion skin", "Show ghosts of the pose a few frames before and after the current frame.",
            "<circle cx='6' cy='8' r='4'/><circle cx='10.5' cy='8' r='4'/>", on => App.SetShowOnion(on));
        AddCameraToolbar(row);
        row.AddChild(TrailButton);
        row.AddChild(OnionButton);
        AddPhysicsToolbar(row);
        AddCombatToolbar(row);
        _viewTools.AddChild(W.Margin(row, 4, 4, 4, 4));
        _viewTools.AnchorLeft = 0.5f; _viewTools.AnchorRight = 0.5f;
        _viewTools.GrowHorizontal = GrowDirection.Both;
        _viewTools.OffsetTop = 12;
        _center.AddChild(_viewTools);
    }

    Button ViewToggle(string text, string tip, string svg, Action<bool> toggled)
    {
        var b = new Button
        {
            Text = text, TooltipText = tip, ToggleMode = true, FocusMode = FocusModeEnum.None, ThemeTypeVariation = "Transport",
            Icon = UiIcons.Make(svg, false), ExpandIcon = false,
        };
        b.Toggled += on => toggled(on);
        return b;
    }

    void BuildAnimationMenu()
    {
        var m = _mAnim;
        m.AddSeparator();
        m.AddItem("Key", MKey);
        m.AddItem("Key Selected", MKeySel);
        m.AddItem("Delete Key", MDelKey);
        m.AddCheckItem("Auto-key", MAutoKey);
        m.AddSeparator();
        m.AddItem("Play or Pause", MPlay);
        m.AddCheckItem("Loop", MLoop);
        m.AddItem("Go to Start", MStart);
        m.AddItem("Go to End", MEnd);
        m.AddItem("Previous Key", MPrevKey);
        m.AddItem("Next Key", MNextKey);
        m.AddSeparator();
        m.AddItem("Add Marker", MAddMarker);
        m.AddItem("Add Key-pose Phases", MAddPhases);
    }

    void BuildViewSettingsMenu()
    {
        _mSettings.AddSeparator();
        _mSettings.AddCheckItem("Motion Trail", MTrail);
        _mSettings.AddCheckItem("Onion Skin", MOnion);
        _mOnion = new PopupMenu { Name = "OnionFrames" };
        foreach (var n in OnionChoices) _mOnion.AddRadioCheckItem("", MOnionBase + n);
        RefreshOnionLabels();
        _mOnion.IdPressed += id => App.SetOnionFrames((int)id - MOnionBase);
        _mSettings.AddChild(_mOnion);
        _mSettings.AddSubmenuNodeItem("Onion Skin Range", _mOnion);
        _mSettings.IdPressed += id => OnMenu((int)id);
    }

    /// <summary>"3 frames" is formatted, not a msgid: set again when the language changes.</summary>
    void RefreshOnionLabels()
    {
        for (var i = 0; i < _mOnion.ItemCount; i++)
        {
            _mOnion.SetItemText(i, string.Format(CultureInfo.InvariantCulture, Tr("{0} frames"), _mOnion.GetItemId(i) - MOnionBase));
            _mOnion.SetItemAutoTranslateMode(i, AutoTranslateModeEnum.Disabled);
        }
    }

    void OnAnimationMenu(int id)
    {
        switch (id)
        {
            case MKey: KeyAll(); break;
            case MKeySel: KeySelected(); break;
            case MDelKey: DeleteKeyHere(); break;
            case MAutoKey: App.SetAutoKey(!App.AutoKey); break;
            case MPlay: App.TogglePlay(); break;
            case MLoop: if (App.ActiveClip is { } c) App.SetLoop(!c.Loop.Enabled); break;
            case MStart: App.GoToStart(); break;
            case MEnd: App.GoToEnd(); break;
            case MPrevKey: App.GoToPreviousKey(); break;
            case MNextKey: App.GoToNextKey(); break;
            case MAddMarker: AddMarkerAtPlayhead(); break;
            case MAddPhases: AddPhaseMarkers(); break;
            case MTrail: App.SetShowTrail(!App.Settings.ShowTrail); break;
            case MOnion: App.SetShowOnion(!App.Settings.ShowOnion); break;
        }
    }

    void UpdateAnimationMenu()
    {
        if (_mAnim is null || Timeline is null) return;
        var clip = App.ActiveClip;
        void Enable(int id, bool on) => _mAnim.SetItemDisabled(_mAnim.GetItemIndex(id), !on);
        var has = SelectedCharacter is not null;
        Enable(MKey, has); Enable(MKeySel, has); Enable(MDelKey, clip is not null); Enable(MLoop, clip is not null);
        Enable(MAddMarker, has || clip is not null); Enable(MAddPhases, has || clip is not null);
        _mAnim.SetItemChecked(_mAnim.GetItemIndex(MAutoKey), App.AutoKey);
        _mAnim.SetItemChecked(_mAnim.GetItemIndex(MLoop), clip is { Loop.Enabled: true });
        _mSettings.SetItemChecked(_mSettings.GetItemIndex(MTrail), App.Settings.ShowTrail);
        _mSettings.SetItemChecked(_mSettings.GetItemIndex(MOnion), App.Settings.ShowOnion);
        for (var i = 0; i < _mOnion.ItemCount; i++) _mOnion.SetItemChecked(i, _mOnion.GetItemId(i) == MOnionBase + App.Settings.OnionFrames);
    }

    void SyncViewTools()
    {
        TrailButton.SetPressedNoSignal(App.Settings.ShowTrail);
        OnionButton.SetPressedNoSignal(App.Settings.ShowOnion);
        OnionButton.TooltipText = string.Format(CultureInfo.InvariantCulture, Tr("Show ghosts of the pose {0} frames before and after the current frame."), App.Settings.OnionFrames);
        Motion.QueueRedraw();
        Timeline.Canvas.QueueRedraw(); Timeline.Dope.QueueRedraw(); Timeline.Graph.Canvas.QueueRedraw();
        Timeline.KeyPoses.Rebuild(true);
        Timeline.Graph.Rebuild(true);
        SyncPropsUi();
        SyncPhysicsUi();
        SyncCombatUi();
        Timeline.Contacts.Sync();
    }

    // ---- hooks into the shell -----------------------------------------------------------------------------------------

    void OnActiveClipChanged()
    {
        Strip.UpdateSelection();
        OutlinePanel.UpdateSelection();
        Inspector.Refresh(true);
        UpdateMenus();
    }

    /// <summary>Make the dock tall enough for the dope sheet or graph (never shrinks what the user dragged).</summary>
    public void RequestDockHeight(float height) => CallDeferred(nameof(ApplyDockHeight), height);

    void ApplyDockHeight(float height)
    {
        var current = Timeline.Size.Y;
        if (current < 60 || current >= height - 2) return;
        _splitDock.SplitOffsets = new int[] { _splitDock.SplitOffsets[0] - ((int)(height - current)) };
    }

    string ClipSignature() => string.Join("|", App.Session.Project.Clips.Select(c => $"{c.Id}:{c.Name}:{c.Duration.Value}:{c.IsDraft}:{c.Fps}"));

    // ---- keying actions ------------------------------------------------------------------------------------------------

    public bool KeyAll() => Selection.PropId is { } propId ? App.KeyPropAtCurrent(propId) : App.KeyAtCurrent(null);

    /// <summary>Key only the selected bone, or the selected IK control with the bones it drives.</summary>
    public bool KeySelected()
    {
        if (Selection.PropId is { } propId) return App.KeyPropAtCurrent(propId);
        if (SelectedCharacter is not { } c) return App.KeyAtCurrent(null);
        string[]? targets = null;
        if (Selection.BoneId is { } b) targets = new[] { b };
        else if (Selection.ControlId is { } ctl) targets = App.ControlKeyTargets(c.Id.ToString(), ctl);
        if (targets is null)
        {
            _toast.Notify("Select a bone or control first, or use Key to key the whole pose.", _palette.Warn);
            return false;
        }
        return App.KeyAtCurrent(targets);
    }

    public bool DeleteKeyHere() => App.DeleteKeysAtCurrent();

    // ---- markers ------------------------------------------------------------------------------------------------------------

    string NextMarkerName(Clip? clip)
    {
        var n = (clip?.Markers.Count ?? 0) + 1;
        while (clip is not null && clip.Markers.Any(m => m.Name == string.Format(CultureInfo.InvariantCulture, Tr("Marker {0}"), n))) n++;
        return string.Format(CultureInfo.InvariantCulture, Tr("Marker {0}"), n);
    }

    /// <summary>Adds a marker at the playhead (creating a clip first if there is none). Returns the marker id.</summary>
    public string? AddMarkerAtPlayhead(string? name = null)
    {
        string? markerId = null;
        var created = false;
        var previous = App.ActiveClipId;
        using (var tx = App.BeginTransaction("Add marker"))
        {
            var clipId = App.EnsureClip(out created);
            if (clipId is null) { tx.Rollback(); return null; }
            var clip = App.ActiveClip;
            var r = App.Execute("marker.add", new JsonObject
            {
                ["clipId"] = clipId, ["frame"] = App.CurrentFrame, ["name"] = name ?? NextMarkerName(clip),
            });
            if (!r.Ok) tx.Rollback(); else { markerId = (string?)r.Data?["markerId"]; tx.Commit(); }
        }
        if (created && markerId is not null) App.NotifyClipCreated();
        else if (created) App.SetActiveClipSilent(previous);
        return markerId;
    }

    public void PromptRenameMarker(string markerId)
    {
        var marker = App.ActiveClip?.Markers.Find(m => m.Id.ToString() == markerId);
        if (marker is null) return;
        var shown = TlText.MarkerLabel(marker);
        PromptName("Rename Marker", "Rename", shown, name => { if (name != shown) RenameMarker(markerId, name); });
    }

    public bool RenameMarker(string markerId, string name) =>
        App.ActiveClip is { } c && App.Execute("marker.update", new JsonObject { ["clipId"] = c.Id.ToString(), ["markerId"] = markerId, ["name"] = name }).Ok;

    public bool DeleteMarker(string markerId) =>
        App.ActiveClip is { } c && App.Execute("marker.delete", new JsonObject { ["clipId"] = c.Id.ToString(), ["markerId"] = markerId }).Ok;

    public bool MoveMarker(string markerId, long frame)
    {
        var clip = App.ActiveClip;
        var marker = clip?.Markers.Find(m => m.Id.ToString() == markerId);
        if (clip is null || marker is null || marker.Time.ToFrame(clip.Fps) == frame) return false;
        return App.Execute("marker.update", new JsonObject { ["clipId"] = clip.Id.ToString(), ["markerId"] = markerId, ["frame"] = frame }).Ok;
    }

    /// <summary>Adds the seven key-pose phase markers (and a clip if needed).</summary>
    public bool AddPhaseMarkers()
    {
        var ok = false;
        var created = false;
        var previous = App.ActiveClipId;
        using (var tx = App.BeginTransaction("Add phase markers"))
        {
            var clipId = App.EnsureClip(out created);
            if (clipId is null) { tx.Rollback(); return false; }
            ok = App.Execute("clip.add_phase_markers", new JsonObject { ["clipId"] = clipId }).Ok;
            if (ok) tx.Commit(); else tx.Rollback();
        }
        if (created && ok) App.NotifyClipCreated();
        else if (created) App.SetActiveClipSilent(previous);
        return ok;
    }

    /// <summary>"Set pose" on a key-pose card: key the working pose at that phase's frame, then show it.</summary>
    public bool SetPoseAtFrame(long frame)
    {
        if (!App.KeyAtCurrent(null, frame)) return false;
        App.GoToFrame(frame);
        return true;
    }

    // ---- housekeeping ----------------------------------------------------------------------------------------------------------

    void FlushHistoryIfStale()
    {
        if (!_historyStale || App.Session.InTransaction) return;
        _historyStale = false;
        if (App.Settings.AdvancedMode) HistoryPanel.Rebuild();
        UpdateMenus();
    }
}
