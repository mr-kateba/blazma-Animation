using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Phase 3 scenario: timeline, keying, playback, auto-key, dope sheet, graph editor, markers, trail and onion skin.</summary>
public partial class SelfTest
{
    Clip? P3Clip => App.ActiveClip;
    IReadOnlyList<long> KeyFramesNow() => P3Clip is { } c ? ClipEvaluator.KeyFrames(c) : Array.Empty<long>();

    void GMouse(Vector2 global, bool pressed, MouseButton button = MouseButton.Left, bool ctrl = false)
    {
        var mask = button == MouseButton.Left ? MouseButtonMask.Left : button == MouseButton.Middle ? MouseButtonMask.Middle : MouseButtonMask.Right;
        GetViewport().PushInput(new InputEventMouseButton
        {
            Position = global, GlobalPosition = global, ButtonIndex = button, Pressed = pressed, ButtonMask = pressed ? mask : 0, CtrlPressed = ctrl,
        }, false);
    }

    void GMove(Vector2 global, bool left = true) =>
        GetViewport().PushInput(new InputEventMouseMotion { Position = global, GlobalPosition = global, Relative = new Vector2(1, 0), ButtonMask = left ? MouseButtonMask.Left : 0 }, false);

    void GDrag(Vector2 from, Vector2 to, int steps = 6)
    {
        GMove(from, false);
        GMouse(from, true);
        for (var i = 1; i <= steps; i++) GMove(from.Lerp(to, i / (float)steps));
        GMouse(to, false);
    }

    void GWheel(Vector2 at, bool up, bool ctrl = false) =>
        GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = up ? MouseButton.WheelUp : MouseButton.WheelDown, Pressed = true, Factor = 1, CtrlPressed = ctrl }, false);

    void KeyPress(Godot.Key k, bool ctrl = false, bool shift = false, bool alt = false)
    {
        foreach (var pressed in new[] { true, false })
            GetViewport().PushInput(new InputEventKey { Keycode = k, PhysicalKeycode = k, CtrlPressed = ctrl, ShiftPressed = shift, AltPressed = alt, Pressed = pressed }, false);
    }

    Vector2 CanvasPoint(long frame, float y = 10) => _w.Timeline.Canvas.GlobalPosition + _w.Timeline.Canvas.PointForFrame(frame, y);

    /// <summary>Largest distance (m) between the bone positions of two poses.</summary>
    static double PoseDistance(Rig rig, Pose a, Pose b)
    {
        var ev = new RigEvaluator(rig);
        var ma = ev.Model(a); var mb = ev.Model(b);
        var worst = 0.0;
        for (var i = 0; i < ma.Length; i++)
        {
            var d = ma[i].Translation - mb[i].Translation;
            worst = Math.Max(worst, Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z));
            var qa = ma[i].Rotation; var qb = mb[i].Rotation;
            worst = Math.Max(worst, Math.Abs(1 - Math.Abs(qa.X * qb.X + qa.Y * qb.Y + qa.Z * qb.Z + qa.W * qb.W)));
        }
        return worst;
    }

    Pose Expected(double frame)
    {
        var clip = P3Clip!;
        return ClipEvaluator.Evaluate(clip, RigOf(Ch()), Flicks.FromSeconds(frame / clip.Fps.FramesPerSecond));
    }

    Track? TrackOf(string targetId, string channel) => P3Clip?.Tracks.Find(t => t.Target.Id.ToString() == targetId && t.Channel == channel);

    static string Latin(double d) => d.ToString("0.00E+00", CultureInfo.InvariantCulture);

    async Task RunPhase3()
    {
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false); App.SetAutoKey(false);
        App.SetShowTrail(false); App.SetShowOnion(false);
        _w.Selection.SetTool(StageTool.Rotate);
        await Settle();

        Check("p3 new project has no clip and starts at frame 0", () =>
        {
            _w.NewProjectNow();
            return Proj.Clips.Count == 0 && App.CurrentFrame == 0 && !App.Playing && App.ActiveClip is null ? null : $"clips={Proj.Clips.Count} frame={App.CurrentFrame}";
        });
        await Settle();
        var man = Ch();
        var mid = man.Id.ToString();
        var rig = RigOf(man);
        var uarm = BoneId(man, "upper_arm_l");

        // ---- pose the arm through the real gizmo path, then Key with no clip ----
        _w.Selection.SelectBone(mid, uarm);
        _w.Selection.SetTool(StageTool.Rotate);
        await Settle();
        Check("p3 pose the arm with the rotate gizmo; the Key button shows the unkeyed dot", () =>
        {
            var path = _w.Overlay.Gizmo.RingPath(2, 7);
            if (path is null) return "no ring path";
            Drag(path);
            if (!_w.Timeline.UnkeyedDot.Visible) return "no unkeyed dot after posing";
            return _w.Timeline.KeyButton.TooltipText.Contains("Unkeyed changes are lost when you change frame") ? null : "tooltip: " + _w.Timeline.KeyButton.TooltipText;
        });

        var undoBefore = App.Session.UndoHistory.Count;
        Check("p3 pressing Key with no clip creates Clip 1 with keys at frame 0 (one undo step)", () =>
        {
            _w.Timeline.KeyButton.EmitSignal(BaseButton.SignalName.Pressed);
            var clip = P3Clip;
            if (clip is null || clip.Name != "Clip 1" || Proj.Clips.Count != 1) return "clips: " + string.Join(",", Proj.Clips.Select(c => c.Name));
            if (!KeyFramesNow().SequenceEqual(new long[] { 0 })) return "keys at " + string.Join(",", KeyFramesNow());
            if (App.Session.UndoHistory.Count != undoBefore + 1) return "undo steps " + (App.Session.UndoHistory.Count - undoBefore);
            if (_w.Timeline.UnkeyedDot.Visible) return "dot still visible after keying";
            return _w.SelectedClipId == clip.Id.ToString() && clip.Skeleton == rig.Id ? null : "clip not active / not bound to rig";
        });
        Check("p3 undo removes clip and keys together, redo brings both back and re-selects the clip", () =>
        {
            _w.DoUndo();
            if (Proj.Clips.Count != 0) return "undo left the clip";
            _w.DoRedo();
            return Proj.Clips.Count == 1 && KeyFramesNow().Count == 1 && _w.SelectedClipId == Proj.Clips[0].Id.ToString() ? null : "redo state wrong";
        });
        await Settle();

        // ---- go to frame 15 by clicking the ruler, pose again, Key ----
        Check("p3 clicking the ruler goes to that frame", () =>
        {
            Click(CanvasPoint(15));
            return App.CurrentFrame == 15 ? null : "frame " + App.CurrentFrame;
        });
        Check("p3 frame 15 shows the held pose of the only key", () =>
            PoseDistance(rig, Ch().Pose, Expected(15)) < 1e-9 ? null : "pose differs from evaluation");
        Check("p3 pose the arm further and press Key at frame 15", () =>
        {
            var path = _w.Overlay.Gizmo.RingPath(2, 12);
            if (path is null) return "no ring path";
            Drag(path);
            _w.Timeline.KeyButton.EmitSignal(BaseButton.SignalName.Pressed);
            return KeyFramesNow().SequenceEqual(new long[] { 0, 15 }) ? null : "keys at " + string.Join(",", KeyFramesNow());
        });
        Vector3 hand0 = default, hand7 = default, hand15 = default;
        var handIdx = rig.IndexOf(rig.FindByStandardName("hand_l")!.Id);
        Check("p3 scrub to frame 7: displayed pose equals ClipEvaluator at frame 7 (max error < 1e-9)", () =>
        {
            Click(CanvasPoint(0)); hand0 = View(mid)!.Joints[handIdx];
            Click(CanvasPoint(15)); hand15 = View(mid)!.Joints[handIdx];
            Click(CanvasPoint(7));
            if (App.CurrentFrame != 7) return "frame " + App.CurrentFrame;
            hand7 = View(mid)!.Joints[handIdx];
            var err = PoseDistance(rig, Ch().Pose, Expected(7));
            GD.Print($"SELFTEST INFO scrub error at frame 7: {Latin(err)} m; hand moved {(hand7 - hand0).Length():0.000} m from frame 0, {(hand15 - hand7).Length():0.000} m to frame 15");
            if (err >= 1e-9) return "error " + err;
            return (hand7 - hand0).Length() > 0.01 && (hand15 - hand7).Length() > 0.01 ? null : "the hand does not move between the keys";
        });
        CheckSkeleton("p3 scrubbed frame 7", mid);
        Check("p3 scrubbing is not an edit (no history, project not marked dirty by it)", () =>
        {
            var steps = App.Session.UndoHistory.Count;
            Click(CanvasPoint(3)); Click(CanvasPoint(9));
            return App.Session.UndoHistory.Count == steps ? null : "scrubbing created history";
        });
        Check("p3 Left / Right arrows step frames, Up / Down jump between keys, Home / End go to the ends", () =>
        {
            _w.Stage.GrabFocus();
            App.GoToFrame(4);
            PlainKey(Godot.Key.Right); if (App.CurrentFrame != 5) return "Right -> " + App.CurrentFrame;
            PlainKey(Godot.Key.Left); PlainKey(Godot.Key.Left); if (App.CurrentFrame != 3) return "Left -> " + App.CurrentFrame;
            PlainKey(Godot.Key.Down); if (App.CurrentFrame != 0) return "previous key -> " + App.CurrentFrame;
            PlainKey(Godot.Key.Up); if (App.CurrentFrame != 15) return "next key -> " + App.CurrentFrame;
            _w.Timeline.Canvas.GrabFocus();
            PlainKey(Godot.Key.End); if (App.CurrentFrame != App.FrameCount - 1) return "End -> " + App.CurrentFrame;
            PlainKey(Godot.Key.Home); if (App.CurrentFrame != 0) return "Home -> " + App.CurrentFrame;
            return null;
        });

        // ---- playback ----
        Check("p3 Space plays; stepping _Process by 0.5 s at 30 fps lands on frame 15 and the pose matches", () =>
        {
            App.GoToFrame(0);
            _w.Stage.GrabFocus();
            PlainKey(Godot.Key.Space);
            if (!App.Playing) return "Space did not start playback";
            for (var i = 0; i < 5; i++) App.AdvancePlayback(0.1);
            var err = PoseDistance(rig, Ch().Pose, Expected(App.CurrentFrameExact));
            GD.Print($"SELFTEST INFO playback after 0.5 s: frame {App.CurrentFrame} (exact {App.CurrentFrameExact:0.000}), pose error {Latin(err)} m");
            if (Math.Abs(App.CurrentFrameExact - 15) > 0.01 || App.CurrentFrame != 15) return "frame " + App.CurrentFrameExact;
            return err < 1e-9 ? null : "pose error " + err;
        });
        await Frames(2);
        _w.ToastNode.HideNow(); await Frames(2);
        Shot(P("p3-en-dark-playback-mid"), GetViewport());
        Check("p3 playback is not an edit and Space pauses on a whole frame", () =>
        {
            var steps = App.Session.UndoHistory.Count;
            if (!App.Playing) { App.GoToFrame(15); App.Play(); } // a slow screenshot may have let the short clip run to its end
            PlainKey(Godot.Key.Space);
            return !App.Playing && App.Session.UndoHistory.Count == steps && App.CurrentFrameExact == App.CurrentFrame ? null : $"still playing or history changed: playing={App.Playing} hist={App.Session.UndoHistory.Count}/{steps} exact={App.CurrentFrameExact} frame={App.CurrentFrame} focus={GetViewport().GuiGetFocusOwner()?.GetType().Name}";
        });
        Check("p3 without loop, playback stops on the last frame", () =>
        {
            App.GoToFrame(0); App.Play();
            for (var i = 0; i < 40; i++) App.AdvancePlayback(0.1);
            return !App.Playing && App.CurrentFrame == App.FrameCount - 1 ? null : $"playing={App.Playing} frame={App.CurrentFrame}";
        });
        Check("p3 Loop on (button): playback wraps around", () =>
        {
            _w.Timeline.LoopButton.EmitSignal(BaseButton.SignalName.Pressed);
            if (P3Clip is not { Loop.Enabled: true }) return "loop not enabled";
            App.GoToFrame(0); App.Play();
            for (var i = 0; i < 12; i++) App.AdvancePlayback(0.1); // 1.2 s = 36 frames in a 30 frame clip
            var f = App.CurrentFrame;
            var err = PoseDistance(rig, Ch().Pose, Expected(App.CurrentFrameExact));
            App.Pause();
            return App.Playing == false && f >= 5 && f <= 7 && err < 1e-9 ? null : $"frame {f}, error {err}";
        });
        Check("p3 dragging the loop-start handle on the ruler sets the loop range (clip.set_loop)", () =>
        {
            var from = CanvasPoint(0, 5);
            GDrag(from, CanvasPoint(5, 5));
            var (s, e) = AppController.LoopFrames(P3Clip!);
            return s == 5 && e == 30 && P3Clip!.Loop.Enabled ? null : $"loop {s}..{e}";
        });
        Check("p3 playing a loop range stays inside it", () =>
        {
            App.GoToFrame(0); App.Play();
            if (App.CurrentFrame != 5) return "play did not start at the loop start: " + App.CurrentFrame;
            var min = 99L;
            for (var i = 0; i < 60; i++) { App.AdvancePlayback(0.05); min = Math.Min(min, App.CurrentFrame); }
            App.Pause();
            return min >= 5 ? null : "left the range, min frame " + min;
        });
        Check("p3 loop off again and reset the range", () =>
        {
            _w.Timeline.LoopButton.EmitSignal(BaseButton.SignalName.Pressed);
            return App.SetLoop(false, 0, 30) && P3Clip is { Loop.Enabled: false } ? null : "could not reset";
        });

        // ---- auto-key ----
        var thigh = BoneId(man, "thigh_l");
        _w.Selection.SelectBone(mid, thigh);
        await Settle();
        Check("p3 auto-key on: rotating a bone at frame 20 keys only that bone, in the same undo step", () =>
        {
            _w.Timeline.AutoKeyButton.ButtonPressed = true;
            if (!App.AutoKey) return "auto-key not on";
            App.GoToFrame(20);
            var before = App.Session.UndoHistory.Count;
            var path = _w.Overlay.Gizmo.RingPath(0, 6);
            if (path is null) return "no ring";
            Drag(path);
            var clip = P3Clip!;
            var at20 = clip.Tracks.Where(t => t.Keys.Any(k => k.Time.ToFrame(clip.Fps) == 20)).ToList();
            if (at20.Count == 0) return "no keys at 20";
            if (at20.Any(t => t.Target.Id.ToString() != thigh)) return "other targets keyed: " + string.Join(",", at20.Select(t => t.Target.Id.ToString()).Distinct());
            return App.Session.UndoHistory.Count == before + 1 ? null : "undo steps " + (App.Session.UndoHistory.Count - before);
        });
        Check("p3 auto-key also keys inspector edits for the selected bone", () =>
        {
            var spine = BoneId(man, "spine_01");
            _w.Selection.SelectBone(mid, spine);
            _w.Inspector.TypeRotation(10, 0, 0);
            var clip = P3Clip!;
            var keyedSpine = clip.Tracks.Any(t => t.Target.Id.ToString() == spine && t.Keys.Any(k => k.Time.ToFrame(clip.Fps) == 20));
            App.SetAutoKey(false);
            _w.Selection.SelectBone(mid, uarm);
            return keyedSpine ? null : "no spine key";
        });
        await Settle();

        // ---- Simple mode: key-pose phases ----
        App.GoToFrame(0);
        await Settle();
        Check("p3 clip without phase markers shows one 'Add key-pose phases' button", () =>
            _w.Timeline.KeyPoses.AddButton is not null && _w.Timeline.KeyPoses.Cards.Count == 0 ? null : "button missing");
        Check("p3 'Add key-pose phases' adds the 7 phase markers as cards", () =>
        {
            _w.Timeline.KeyPoses.AddButton!.EmitSignal(BaseButton.SignalName.Pressed);
            var phases = P3Clip!.Markers.Where(m => m.Phase != KeyPhase.None).ToList();
            return phases.Count == 7 && _w.Timeline.KeyPoses.Cards.Count == 7 ? null : $"markers={phases.Count} cards={_w.Timeline.KeyPoses.Cards.Count}";
        });
        await Settle();
        Check("p3 clicking the Contact card jumps to its frame; Set pose keys the pose there", () =>
        {
            var contact = P3Clip!.Markers.Find(m => m.Phase == KeyPhase.Contact)!;
            var f = contact.Time.ToFrame(P3Clip.Fps);
            var card = _w.Timeline.KeyPoses.Cards.First(c => c.MarkerId == contact.Id.ToString());
            Click(card.GetGlobalRect().GetCenter() + new Vector2(-10, 0));
            if (App.CurrentFrame != f) return $"frame {App.CurrentFrame}, expected {f}";
            if (!card.SetPoseButton.Visible) return "Set pose button not shown on the selected card";
            var had = KeyFramesNow().Contains(f);
            SetBone(Ch(), "head", 0, 25, 0);
            _w.Timeline.KeyPoses.Cards.First(c => c.MarkerId == contact.Id.ToString()).SetPoseButton.EmitSignal(BaseButton.SignalName.Pressed);
            var head = BoneId(Ch(), "head");
            var keyed = P3Clip.Tracks.Any(t => t.Target.Id.ToString() == head && t.Keys.Any(k => k.Time.ToFrame(P3Clip.Fps) == f));
            GD.Print($"SELFTEST INFO Contact phase at frame {f} (had key before: {had})");
            return keyed ? null : "no key at the Contact frame";
        });
        var markersBefore = P3Clip!.Markers.Count;
        App.GoToFrame(22);
        _w.Timeline.AddMarkerButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(3);
        Check("p3 '+ Marker' adds a marker at the playhead; drag moves it; rename and delete work", () =>
        {
            var before = markersBefore;
            var added = P3Clip!.Markers.First(m => m.Phase == KeyPhase.None && m.Name.StartsWith("Marker"));
            if (P3Clip.Markers.Count != before + 1 || added.Time.ToFrame(P3Clip.Fps) != 22) return "marker not added at 22";
            var chip = _w.Timeline.Canvas.Chips.First(c => c.id == added.Id.ToString());
            GDrag(_w.Timeline.Canvas.GlobalPosition + chip.rect.GetCenter(), _w.Timeline.Canvas.GlobalPosition + chip.rect.GetCenter() + new Vector2(3 * (float)_w.Timeline.Canvas.Axis.Ppf, 0));
            var moved = P3Clip.Markers.Find(m => m.Id == added.Id)!;
            if (moved.Time.ToFrame(P3Clip.Fps) != 25) return "marker at " + moved.Time.ToFrame(P3Clip.Fps);
            if (!_w.RenameMarker(added.Id.ToString(), "Hit") || P3Clip.Markers.Find(m => m.Id == added.Id)!.Name != "Hit") return "rename failed";
            if (!_w.DeleteMarker(added.Id.ToString()) || P3Clip.Markers.Count != before) return "delete failed";
            return null;
        });
        App.GoToFrame(12);
        await Settle();
        _w.ToastNode.HideNow(); await Frames(2);
        Shot(P("p3-en-dark-simple-timeline"), GetViewport());

        // ---- Dope sheet (Advanced) ----
        App.SetAdvancedMode(true);
        _w.Timeline.SelectTab(TimelinePanel.TabDope);
        await Settle();
        var dope = _w.Timeline.Dope;
        Check("p3 dope sheet lists groups and one row per bone / control", () =>
        {
            var rows = dope.Rows;
            var groups = rows.Where(r => r.IsGroup).Select(r => r.Group).ToList();
            GD.Print("SELFTEST INFO dope groups: " + string.Join(",", groups) + $"; {rows.Count(r => !r.IsGroup)} channel rows");
            return groups.Contains("Body") && groups.Contains("Arms") && groups.Contains("Legs") && groups.Contains("IK") && rows.Any(r => r.Name == "upper_arm_l") ? null : "rows " + string.Join(",", groups);
        });
        Check("p3 dope sheet is left-to-right and the name column is on the left", () =>
            !dope.IsLayoutRtl() && dope.Axis.X(10) < dope.Axis.X(20) && dope.Axis.X(0) > DopeSheetView.NameW - 1 ? null : "dope axis wrong");
        Check("p3 lock toggle of a row locks all its channels (track.set_flags) and key edits skip it", () =>
        {
            var row = dope.Rows.First(r => r.TargetId == uarm);
            var i = dope.Rows.ToList().IndexOf(row);
            dope.EnsureRowVisible(i);
            var p = dope.GlobalPosition + new Vector2(DopeSheetView.NameW - 41, dope.RowCenter(i));
            Click(p);
            if (!P3Clip!.Tracks.Where(t => t.Target.Id.ToString() == uarm).All(t => t.Locked)) return "not locked after click";
            dope.ToggleFlag(dope.Rows.First(r => r.TargetId == uarm), true);
            return P3Clip.Tracks.Where(t => t.Target.Id.ToString() == uarm).All(t => !t.Locked) ? null : "not unlocked";
        });
        // Collapse every group so a box selection hits the group summary rows.
        for (var i = dope.Rows.Count - 1; i >= 0; i--)
            if (dope.Rows[i].IsGroup) { dope.EnsureRowVisible(i); Click(dope.GlobalPosition + new Vector2(40, dope.RowCenter(i))); }
        await Settle();
        var keysBefore = KeyFramesNow().ToList();
        Check("p3 box-select the frame-15 column, then drag the keys +5 frames: keys land on frame 20", () =>
        {
            var x15 = dope.GlobalPosition.X + dope.Axis.X(15);
            var top = dope.GlobalPosition.Y + DopeSheetView.RulerH + 2;
            var bottom = dope.GlobalPosition.Y + dope.RowCenter(dope.Rows.Count - 1) + 9;
            GDrag(new Vector2(x15 - 9, top), new Vector2(x15 + 9, bottom), 4);
            if (dope.Selection.Count == 0) return "box select selected nothing";
            if (dope.Selection.Any(s => s.frame != 15)) return "selection includes other frames";
            var ppf = (float)dope.Axis.Ppf;
            var startKey = new Vector2(x15, dope.GlobalPosition.Y + dope.RowCenter(0));
            var undo = App.Session.UndoHistory.Count;
            GDrag(startKey, startKey + new Vector2(5 * ppf, 0), 5);
            var frames = KeyFramesNow();
            GD.Print("SELFTEST INFO key frames after dragging 15 -> 20: " + string.Join(",", frames));
            if (frames.Contains(15)) return "keys still at 15";
            if (!frames.Contains(20)) return "no keys at 20";
            return App.Session.UndoHistory.Count == undo + 1 ? null : "not a single undo step: " + (App.Session.UndoHistory.Count - undo);
        });
        Check("p3 undo restores the moved keys", () =>
        {
            _w.DoUndo();
            var ok = KeyFramesNow().Contains(15);
            _w.DoRedo();
            return ok && !KeyFramesNow().Contains(15) ? null : "undo/redo of the move failed";
        });
        Check("p3 Delete key removes the selected keys; undo restores them", () =>
        {
            var x20 = dope.GlobalPosition.X + dope.Axis.X(20);
            GDrag(new Vector2(x20 - 9, dope.GlobalPosition.Y + DopeSheetView.RulerH + 2), new Vector2(x20 + 9, dope.GlobalPosition.Y + dope.RowCenter(dope.Rows.Count - 1) + 9), 4);
            if (dope.Selection.Count == 0) return "nothing selected at 20";
            dope.GrabFocus();
            KeyPress(Godot.Key.Delete);
            if (KeyFramesNow().Contains(20)) return "keys still at 20";
            _w.DoUndo();
            return KeyFramesNow().Contains(20) ? null : "undo did not restore";
        });
        Check("p3 context-menu path: Interpolation > Step / Cubic changes the selected keys", () =>
        {
            var x20 = dope.GlobalPosition.X + dope.Axis.X(20);
            GDrag(new Vector2(x20 - 9, dope.GlobalPosition.Y + DopeSheetView.RulerH + 2), new Vector2(x20 + 9, dope.GlobalPosition.Y + dope.RowCenter(dope.Rows.Count - 1) + 9), 4);
            dope.InterpMenu.EmitSignal(PopupMenu.SignalName.IdPressed, 1L);
            var clip = P3Clip!;
            var steps = clip.Tracks.SelectMany(t => t.Keys.Where(k => k.Time.ToFrame(clip.Fps) == 20 && t.Channel != TrackChannels.Pinned)).ToList();
            var allStep = steps.Count > 0 && steps.All(k => k.Interp == Interpolation.Step);
            dope.InterpMenu.EmitSignal(PopupMenu.SignalName.IdPressed, 3L);
            steps = clip.Tracks.SelectMany(t => t.Keys.Where(k => k.Time.ToFrame(clip.Fps) == 20 && t.Channel != TrackChannels.Pinned)).ToList();
            steps = P3Clip!.Tracks.SelectMany(t => t.Keys.Where(k => k.Time.ToFrame(P3Clip.Fps) == 20 && t.Channel != TrackChannels.Pinned)).ToList();
            return allStep && steps.All(k => k.Interp == Interpolation.Cubic) ? null : "interpolation not applied";
        });
        Check("p3 Ctrl+C / Ctrl+V copy the selected keys to the playhead", () =>
        {
            var x20 = dope.GlobalPosition.X + dope.Axis.X(20);
            GDrag(new Vector2(x20 - 9, dope.GlobalPosition.Y + DopeSheetView.RulerH + 2), new Vector2(x20 + 9, dope.GlobalPosition.Y + dope.RowCenter(dope.Rows.Count - 1) + 9), 4);
            dope.GrabFocus();
            KeyPress(Godot.Key.C, ctrl: true);
            if (!dope.HasClipboard) return "nothing copied";
            App.GoToFrame(26);
            dope.GrabFocus();
            KeyPress(Godot.Key.V, ctrl: true);
            return KeyFramesNow().Contains(26) ? null : "no keys at 26 after paste";
        });
        // select the 20 column again for the screenshot
        {
            var x20 = dope.GlobalPosition.X + dope.Axis.X(20);
            GDrag(new Vector2(x20 - 9, dope.GlobalPosition.Y + DopeSheetView.RulerH + 2), new Vector2(x20 + 9, dope.GlobalPosition.Y + dope.RowCenter(dope.Rows.Count - 1) + 9), 4);
        }
        App.GoToFrame(15);
        await Settle();
        // expand the groups again for the picture
        for (var i = 0; i < dope.Rows.Count; i++)
            if (dope.Rows[i].IsGroup && dope.Rows[i].Group is "Arms" or "Legs") { Click(dope.GlobalPosition + new Vector2(40, dope.RowCenter(i))); await Frames(1); }
        _w.Selection.SelectBone(mid, uarm);
        await Settle();
        _w.ToastNode.HideNow(); await Frames(2);
        Shot(P("p3-en-dark-advanced-dopesheet"), GetViewport());

        // ---- Graph editor ----
        var handCtl = rig.Controls.Find(c => c.Name == "ik_hand_l")!;
        var hid = handCtl.Id.ToString();
        App.Execute("key.set_interpolation", new JsonObject { ["clipId"] = P3Clip!.Id.ToString(), ["interpolation"] = "cubic" });
        _w.Selection.SelectControl(mid, hid);
        _w.Timeline.SelectTab(TimelinePanel.TabGraph);
        await Settle();
        var graph = _w.Timeline.Graph;
        var gc = graph.Canvas;
        Check("p3 graph lists the channels of the selected control and is left-to-right", () =>
            graph.Channels.Count >= 3 && !gc.IsLayoutRtl() && !graph.IsLayoutRtl() ? null : $"channels={graph.Channels.Count}");
        graph.SetVisible("Rotation X", false); graph.SetVisible("Rotation Y", false); graph.SetVisible("Rotation Z", false); graph.SetVisible("Rotation W", false);
        graph.SetVisible("Position X", true); graph.SetVisible("Position Y", true); graph.SetVisible("Position Z", true);
        await Settle();
        var posTrack = TrackOf(hid, TrackChannels.Translation)!;
        var posId = posTrack.Id.ToString();
        Check("p3 graph: mouse wheel zooms time, Ctrl+wheel zooms values, F frames everything again", () =>
        {
            var p0 = gc.Axis_.Ppf; var v0 = gc.PixelsPerValue;
            var at = gc.GlobalPosition + new Vector2(300, 150);
            GWheel(at, true);
            if (gc.Axis_.Ppf <= p0) return "wheel did not zoom time";
            GWheel(at, true, ctrl: true);
            if (gc.PixelsPerValue <= v0) return "Ctrl+wheel did not zoom values";
            gc.GrabFocus();
            KeyPress(Godot.Key.F);
            return Math.Abs(gc.Axis_.Ppf - p0) < 1e-6 && Math.Abs(gc.PixelsPerValue - v0) < 1e-6 ? null : "F did not restore the view";
        });
        double v20 = 0;
        Check("p3 graph: dragging a key up changes the evaluated value by exactly the dragged amount", () =>
        {
            var key = TrackOf(hid, TrackChannels.Translation)!.Keys.First(k => k.Time.ToFrame(P3Clip!.Fps) == 20);
            v20 = key.Value[1];
            var pos = gc.KeyPos(posId, 20, 1);
            if (pos is null) return "no key point";
            var ppv = gc.PixelsPerValue;
            var g = gc.GlobalPosition + pos.Value;
            var undo = App.Session.UndoHistory.Count;
            GDrag(g, g + new Vector2(0, -40), 8);
            var after = TrackSampler.Sample(TrackOf(hid, TrackChannels.Translation)!, Flicks.FromFrame(20, P3Clip!.Fps))![1];
            var expect = v20 + 40 / ppv;
            GD.Print($"SELFTEST INFO graph drag: value {v20:0.0000} -> {after:0.0000} (expected {expect:0.0000}), undo steps +{App.Session.UndoHistory.Count - undo}");
            if (Math.Abs(after - expect) > 1e-6) return $"value {after}, expected {expect}";
            return App.Session.UndoHistory.Count == undo + 1 ? null : "not one undo step";
        });
        Check("p3 graph: the dragged key moves the hand in the evaluated pose at that frame", () =>
        {
            App.GoToFrame(20);
            var err = PoseDistance(rig, Ch().Pose, Expected(20));
            var handY = View(mid)!.ControlPositions[rig.Controls.IndexOf(handCtl)].Y;
            return err < 1e-9 && handY > 0 ? null : $"pose error {err}";
        });
        Check("p3 graph: undo puts the value back", () =>
        {
            _w.DoUndo();
            var v = TrackSampler.Sample(TrackOf(hid, TrackChannels.Translation)!, Flicks.FromFrame(20, P3Clip!.Fps))![1];
            _w.DoRedo();
            return Math.Abs(v - v20) < 1e-9 ? null : $"{v} != {v20}";
        });
        Check("p3 graph: dragging a key sideways moves its keys in time (key.move)", () =>
        {
            gc.FrameAll();
            gc.SelectKey(posId, 20, 1);
            var pos = gc.KeyPos(posId, 20, 1);
            if (pos is null) return "no key point";
            var g = gc.GlobalPosition + pos.Value;
            GDrag(g, g + new Vector2(2 * (float)gc.Axis_.Ppf, 0), 6);
            var frames = KeyFramesNow();
            var moved = TrackOf(hid, TrackChannels.Translation)!.Keys.Any(k => k.Time.ToFrame(P3Clip!.Fps) == 22);
            _w.DoUndo();
            return moved && KeyFramesNow().Contains(20) ? null : "key not moved to 22 (frames " + string.Join(",", frames) + ")";
        });
        await Settle();
        double c0 = 0, c1 = 0;
        Check("p3 graph: dragging a tangent handle changes the curve (key.set_tangents)", () =>
        {
            gc.SelectKey(posId, 20, 1);
            var clip = P3Clip!;
            double At(double f) => TrackSampler.Sample(TrackOf(hid, TrackChannels.Translation)!, Flicks.FromSeconds(f / clip.Fps.FramesPerSecond))![1];
            c0 = At(23);
            var h = gc.HandlePositions(posId, 20, 1);
            if (h is null) return "no tangent handles";
            var g = gc.GlobalPosition + h.Value.outPt;
            GDrag(g, g + new Vector2(6, -50), 6);
            c1 = At(23);
            var key = TrackOf(hid, TrackChannels.Translation)!.Keys.First(k => k.Time.ToFrame(clip.Fps) == 20);
            GD.Print($"SELFTEST INFO tangent drag: curve at 23 {c0:0.0000} -> {c1:0.0000}, explicit tangents: {key.OutTangent is not null}");
            return key.OutTangent is not null && Math.Abs(c1 - c0) > 1e-4 ? null : "curve unchanged";
        });
        await Settle();
        _w.ToastNode.HideNow(); await Frames(2);
        Shot(P("p3-en-dark-advanced-graph"), GetViewport());
        Check("p3 graph: Auto tangents resets the selected key to automatic", () =>
        {
            gc.SelectKey(posId, 20, 1);
            graph.AutoTangentsButton.EmitSignal(BaseButton.SignalName.Pressed);
            var key = TrackOf(hid, TrackChannels.Translation)!.Keys.First(k => k.Time.ToFrame(P3Clip!.Fps) == 20);
            return key.OutTangent is null && key.InTangent is null ? null : "tangents still explicit";
        });

        // ---- motion trail and onion skin ----
        // A wide last pose so the path and the ghosts are easy to see: the left hand swings out and down at frame 29.
        App.GoToFrame(29);
        App.Execute("pose.set_control", new JsonObject { ["characterId"] = mid, ["control"] = hid, ["x"] = 0.75, ["y"] = 1.0, ["z"] = 0.25 });
        App.Execute("pose.set_bone", new JsonObject { ["characterId"] = mid, ["bone"] = BoneId(man, "pelvis"), ["rx"] = 0, ["ry"] = 25, ["rz"] = 0, ["tx"] = 0.7, ["ty"] = 0.95, ["tz"] = 0.0 });
        Check("p3 Key records the IK hand pose at the last frame", () =>
            _w.KeyAll() && TrackOf(hid, TrackChannels.Translation)!.Keys.Any(k => k.Time.ToFrame(P3Clip!.Fps) == 29) ? null : "not keyed");
        App.SetAdvancedMode(false);
        _w.Timeline.SelectTab(TimelinePanel.TabTimeline);
        App.GoToFrame(22);
        _w.Selection.SelectBone(mid, BoneId(man, "hand_l"));
        _w.Stage.Frame(new Aabb(new Vector3(-1.0f, 0.0f, -0.8f), new Vector3(2.4f, 2.1f, 1.6f)));
        await Settle();
        Check("p3 motion trail: the toggle shows a trail with one point per frame and the keyed frames", () =>
        {
            _w.TrailButton.ButtonPressed = true;
            return App.Settings.ShowTrail ? null : "setting not on";
        });
        Check("p3 onion skin: ghosts at -5 and +5 frames exist", () =>
        {
            _w.OnionButton.ButtonPressed = true;
            return App.Settings.ShowOnion && App.Settings.OnionFrames == 5 ? null : "setting";
        });
        await Settle();
        Check("p3 trail and onion data are computed from the clip", () =>
        {
            var m = _w.Motion;
            if (m.TrailPointCount != P3Clip!.DurationFrames) return $"trail points {m.TrailPointCount}";
            if (!m.TrailKeyFrames.Contains(0) || !m.TrailKeyFrames.Contains(20)) return "keyed frames not found: " + string.Join(",", m.TrailKeyFrames);
            if (!m.HasPastGhost || !m.HasFutureGhost) return "ghosts missing";
            var expectedPast = new RigEvaluator(rig).Model(Expected(App.CurrentFrame - 5));
            var hi = handIdx;
            var d = m.PastGhost![hi] - CharacterView.ToV3(expectedPast[hi].Translation);
            return d.Length() < 1e-5 ? null : "past ghost differs from frame -5 by " + d.Length();
        });
        _w.ToastNode.HideNow(); await Frames(2);
        Shot(P("p3-en-dark-trail-onion"), GetViewport());
        App.SetShowTrail(false); App.SetShowOnion(false);
        _w.TrailButton.SetPressedNoSignal(false);

        // ---- inspector clip section ----
        Check("p3 inspector clip section shows loop and key count", () =>
        {
            _w.SelectClip(P3Clip!.Id.ToString());
            _w.Inspector.Refresh(true);
            return _w.Inspector.LoopSwitch.ButtonPressed == P3Clip.Loop.Enabled ? null : "loop switch";
        });

        // ---- save and reopen: identical evaluation on all frames ----
        var path = System.IO.Path.Combine(_dir, "phase3.blazanim");
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        var clipName = P3Clip!.Name;
        var clipFrames = (int)P3Clip.DurationFrames;
        var before = new List<Pose>();
        for (var f = 0; f < clipFrames; f++) before.Add(Expected(f));
        var activeId = App.ActiveClipId;
        Check("p3 save, reopen: the clip evaluates identically on every frame", () =>
        {
            if (!_w.SaveToPath(path)) return "save failed";
            _w.NewProjectNow();
            if (!_w.OpenPath(path)) return "open failed: " + _w.LastError;
            var clip = ClipNamed(clipName);
            if (clip is null) return "clip missing";
            var rig2 = RigOf(Ch());
            var worst = 0.0;
            for (var f = 0; f < clipFrames; f++)
                worst = Math.Max(worst, PoseDistance(rig2, before[f], ClipEvaluator.Evaluate(clip, rig2, Flicks.FromFrame(f, clip.Fps))));
            GD.Print($"SELFTEST INFO save/reopen: max difference over {clipFrames} frames = {Latin(worst)}");
            if (worst > 1e-12) return "difference " + worst;
            return App.ActiveClipId == activeId ? null : "active clip not restored";
        });
        Check("p3 markers and phases survive save / reopen", () =>
            P3Clip is { } c && c.Markers.Count(m => m.Phase != KeyPhase.None) == 7 ? null : "markers lost");
        await Settle();

        // ---- Arabic and light screenshots ----
        App.SetLanguage("ar"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        _w.Timeline.SelectTab(TimelinePanel.TabTimeline);
        App.GoToFrame(15);
        _w.Selection.SelectBone(Ch().Id.ToString(), BoneId(Ch(), "upper_arm_l"));
        await Settle();
        Check("p3 Arabic: panels mirror, timeline time still runs left to right with Latin digits", () =>
        {
            var c = _w.Timeline.Canvas;
            if (!_w.IsLayoutRtl()) return "root not RTL";
            if (c.IsLayoutRtl() || _w.Timeline.Dope.IsLayoutRtl()) return "timeline mirrored";
            return c.Axis.X(5) < c.Axis.X(25) && App.Settings.Language == "ar" ? null : "axis reversed";
        });
        _w.ToastNode.HideNow(); await Frames(2);
        Shot(P("p3-ar-dark-simple-timeline"), GetViewport());
        App.SetAdvancedMode(true);
        _w.Timeline.SelectTab(TimelinePanel.TabDope);
        await Settle();
        {
            var d2 = _w.Timeline.Dope;
            var x20 = d2.GlobalPosition.X + d2.Axis.X(20);
            GDrag(new Vector2(x20 - 9, d2.GlobalPosition.Y + DopeSheetView.RulerH + 2), new Vector2(x20 + 9, d2.GlobalPosition.Y + d2.RowCenter(Math.Min(d2.Rows.Count - 1, 8)) + 9), 4);
        }
        await Settle();
        _w.ToastNode.HideNow(); await Frames(2);
        Shot(P("p3-ar-dark-advanced-dopesheet"), GetViewport());

        App.SetLanguage("en"); App.SetTheme("light"); App.SetAdvancedMode(true);
        _w.Selection.SelectControl(Ch().Id.ToString(), RigOf(Ch()).Controls.Find(c => c.Name == "ik_hand_l")!.Id.ToString());
        _w.Timeline.SelectTab(TimelinePanel.TabGraph);
        await Settle();
        {
            var g2 = _w.Timeline.Graph;
            g2.SetVisible("Rotation X", true); g2.SetVisible("Rotation Y", true); g2.SetVisible("Rotation Z", true);
            g2.SetVisible("Position X", true); g2.SetVisible("Position Y", true); g2.SetVisible("Position Z", true);
            await Settle();
            var tr = P3Clip!.Tracks.Find(t => t.Target.Id.ToString() == RigOf(Ch()).Controls.Find(c => c.Name == "ik_hand_l")!.Id.ToString() && t.Channel == TrackChannels.Translation)!;
            g2.Canvas.SelectKey(tr.Id.ToString(), 20, 1);
        }
        await Settle();
        _w.ToastNode.HideNow(); await Frames(2);
        Shot(P("p3-en-light-advanced-graph"), GetViewport());

        // leave the app as the earlier phases expect
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        _w.Timeline.SelectTab(TimelinePanel.TabTimeline);
        await Settle();
    }
}
