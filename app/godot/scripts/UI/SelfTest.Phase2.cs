using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Rigging;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>Phase 2 scenario: characters, picking, gizmos, IK, mirror, pose library, import, bone mapping, persistence.</summary>
public partial class SelfTest
{
    string P(string name) => System.IO.Path.Combine(_dir, name + ".png");

    static string Sample(string file) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(Godot.ProjectSettings.GlobalizePath("res://"), "../../samples/models/khronos", file));

    Character Ch(string? id = null) => id is null ? Proj.Characters[0] : Proj.Characters.Find(c => c.Id.ToString() == id)!;
    Rig RigOf(Character c) => Proj.GetRig(c.Rig);
    CharacterView? View(string id) => _w.Characters.View(id);
    string BoneId(Character c, string standardOrName)
    {
        var rig = RigOf(c);
        return (rig.FindByStandardName(standardOrName) ?? rig.FindBoneByName(standardOrName) ?? throw new InvalidOperationException("no bone " + standardOrName)).Id.ToString();
    }

    bool SetBone(Character c, string bone, double rx, double ry, double rz) =>
        App.Execute("pose.set_bone", new JsonObject { ["characterId"] = c.Id.ToString(), ["bone"] = bone, ["rx"] = rx, ["ry"] = ry, ["rz"] = rz }).Ok;

    void MouseButtonAt(Vector2 local, bool pressed)
    {
        var g = _w.Stage.GlobalPosition + local;
        GetViewport().PushInput(new InputEventMouseButton { Position = g, GlobalPosition = g, ButtonIndex = MouseButton.Left, Pressed = pressed, ButtonMask = pressed ? MouseButtonMask.Left : 0 }, false);
    }

    void MouseMoveAt(Vector2 local, bool left = true)
    {
        var g = _w.Stage.GlobalPosition + local;
        GetViewport().PushInput(new InputEventMouseMotion { Position = g, GlobalPosition = g, Relative = new Vector2(2, 0), ButtonMask = left ? MouseButtonMask.Left : 0 }, false);
    }

    void PlainKey(Godot.Key k)
    {
        foreach (var pressed in new[] { true, false })
            GetViewport().PushInput(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = pressed }, false);
    }

    /// <summary>Press, move through the points (the real mouse path), then release (or Esc to cancel).</summary>
    void Drag(Vector2[] pts, bool cancelWithEsc = false)
    {
        MouseButtonAt(pts[0], true);
        for (var i = 1; i < pts.Length; i++) MouseMoveAt(pts[i]);
        if (cancelWithEsc) { _w.Stage.GrabFocus(); PlainKey(Godot.Key.Escape); }
        MouseButtonAt(pts[^1], false);
    }

    static double Dist(Vector3 a, Vector3 b) => (a - b).Length();

    void CheckSkeleton(string label, string characterId)
    {
        Check("skeleton-matches-core " + label, () =>
        {
            var v = View(characterId);
            if (v is null) return "no view";
            var e = v.MaxSkeletonError(out var n);
            GD.Print($"SELFTEST INFO max skeleton error {label}: {e.ToString("0.00E+00", CultureInfo.InvariantCulture)} m over {n} mapped bones ({v.GodotBoneCount} godot bones, {v.MeshCount} meshes)");
            if (v.LoadError is not null) return "load error: " + v.LoadError;
            return n > 0 && e < 1e-4 ? null : $"error {e} m over {n} bones";
        });
    }

    async Task RunPhase2()
    {
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        _w.Selection.SetTool(StageTool.Rotate);
        await Settle();

        // ---- new project + mannequin ----
        Check("p2 new project has one mannequin and is not dirty", () =>
        {
            _w.NewProjectNow();
            return Proj.Characters.Count == 1 && Proj.Rigs.Count == 1 && Proj.Rigs[0].Source.Kind == RigSourceKind.Builtin && !App.IsDirty && App.Session.UndoHistory.Count == 0
                ? null : $"characters={Proj.Characters.Count} rigs={Proj.Rigs.Count} dirty={App.IsDirty}";
        });
        await Settle();
        var man = Ch();
        var mid = man.Id.ToString();
        Check("p2 character view built and selected", () =>
        {
            var v = View(mid);
            if (v is null) return "no CharacterView";
            if (_w.Selection.CharacterId != mid) return "character not selected";
            return v.MeshCount >= 15 && v.Skeleton is { } sk && sk.GetBoneCount() == RigOf(man).Bones.Count ? null : $"meshes={v.MeshCount}";
        });
        CheckSkeleton("mannequin rest", mid);
        Shot(P("p2-en-dark-simple-rest"), GetViewport());

        // ---- picking by screen position ----
        var uarm = BoneId(man, "upper_arm_l");
        Check("p2 pick bone by clicking its projected joint", () =>
        {
            _w.Selection.ClearItem();
            var s = _w.Overlay.ScreenOfBone(mid, uarm);
            if (s is null) return "joint not projectable";
            MouseButtonAt(s.Value, true); MouseButtonAt(s.Value, false);
            return _w.Selection.BoneId == uarm ? null : $"selected {_w.Selection.BoneId ?? "nothing"} after clicking at {s}";
        });
        Check("p2 click on empty space deselects the bone", () =>
        {
            MouseButtonAt(new Vector2(400, 40), true); MouseButtonAt(new Vector2(400, 40), false);
            return _w.Selection.BoneId is null && _w.Selection.CharacterId == mid ? null : "still selected: " + _w.Selection.BoneId;
        });
        Check("p2 clicking the hand joint twice cycles to its IK box", () =>
        {
            var hand = BoneId(man, "hand_l");
            var s = _w.Overlay.ScreenOfBone(mid, hand)!.Value;
            MouseButtonAt(s, true); MouseButtonAt(s, false);
            var first = _w.Selection.BoneId;
            MouseButtonAt(s, true); MouseButtonAt(s, false);
            return first == hand && _w.Selection.ControlId is not null ? null : $"first={first} control={_w.Selection.ControlId}";
        });

        // ---- rotate gizmo: exactness and one undo step ----
        _w.Selection.SelectBone(mid, uarm);
        _w.Selection.SetTool(StageTool.Rotate);
        await Settle();
        Check("p2 rotate gizmo drag rotates exactly and is one undo step", () =>
        {
            var undoBefore = App.Session.UndoHistory.Count;
            var steps = 7;
            var path = _w.Overlay.Gizmo.RingPath(2, steps);
            if (path is null) return "no ring path";
            Drag(path);
            var expected = steps * Gizmo.DegreesPerRingSample;
            var local = Ch().Pose.LocalOf(Proj.GetRig(Ch().Rig).GetBone(ObjectId.Parse(uarm)));
            var e = local.Rotation.ToEulerXYZ() * (180 / Math.PI);
            GD.Print($"SELFTEST INFO rotate drag expected {expected:0.00} deg about local Z, got Euler {e.X:0.00} {e.Y:0.00} {e.Z:0.00}");
            if (Math.Abs(e.Z - expected) > 0.25 || Math.Abs(e.X) > 0.25 || Math.Abs(e.Y) > 0.25) return $"euler {e}";
            if (App.Session.UndoHistory.Count != undoBefore + 1) return $"history grew by {App.Session.UndoHistory.Count - undoBefore}";
            if (App.Session.UndoHistory[^1].Label != "Rotate bone") return "label " + App.Session.UndoHistory[^1].Label;
            return null;
        });
        Check("p2 undo and redo of the whole drag", () =>
        {
            var b = Proj.GetRig(Ch().Rig).GetBone(ObjectId.Parse(uarm));
            _w.DoUndo();
            if (Ch().Pose.Bones.ContainsKey(uarm)) return "undo left the bone posed";
            _w.DoRedo();
            return Ch().Pose.Bones.ContainsKey(uarm) ? null : "redo did not restore";
        });
        Check("p2 Esc cancels a gizmo drag completely", () =>
        {
            var before = Ch().Pose.Clone();
            var undoBefore = App.Session.UndoHistory.Count;
            var path = _w.Overlay.Gizmo.RingPath(0, 5);
            if (path is null) return "no ring path";
            Drag(path, cancelWithEsc: true);
            return Ch().Pose.ContentEquals(before) && App.Session.UndoHistory.Count == undoBefore && !App.Session.InTransaction ? null : "pose or history changed after Esc";
        });
        Check("p2 inspector rotation fields set the bone through pose.set_bone", () =>
        {
            _w.Inspector.TypeRotation(10, 20, 30);
            var rot = Ch().Pose.LocalOf(RigOf(Ch()).GetBone(ObjectId.Parse(uarm))).Rotation.ToEulerXYZ() * (180 / Math.PI);
            return Math.Abs(rot.X - 10) < 1e-6 && Math.Abs(rot.Y - 20) < 1e-6 && Math.Abs(rot.Z - 30) < 1e-6 ? null : $"{rot}";
        });

        Check("p2 inspector position fields move the pelvis and an IK control", () =>
        {
            var rig = RigOf(Ch());
            _w.Selection.SelectBone(mid, BoneId(man, "pelvis"));
            if (!_w.Inspector.BoneSectionVisible) return "bone section hidden";
            _w.Inspector.TypeBonePosition(0.0, 0.9, 0.05);
            var t = Ch().Pose.LocalOf(rig.FindByStandardName("pelvis")!).Translation;
            if (Math.Abs(t.Y - 0.9) > 1e-9 || Math.Abs(t.Z - 0.05) > 1e-9) return "pelvis " + t;
            var ctl = rig.Controls.Find(c => c.Name == "ik_hand_l")!;
            _w.Selection.SelectControl(mid, ctl.Id.ToString());
            if (!_w.Inspector.ControlSectionVisible) return "control section hidden";
            _w.Inspector.TypeControlPosition(0.6, 1.3, 0.2);
            var p = View(mid)!.ControlPositions[rig.Controls.IndexOf(ctl)];
            _w.Selection.SelectBone(mid, uarm);
            return Dist(p, new Vector3(0.6f, 1.3f, 0.2f)) < 1e-5 ? null : "control at " + p;
        });

        // ---- pose the mannequin for screenshots ----
        Check("p2 pose the mannequin", () =>
        {
            var c = Ch();
            return SetBone(c, "upper_arm_l", 0, 0, 55) && SetBone(c, "lower_arm_l", 0, -80, 0) && SetBone(c, "upper_arm_r", 0, 0, -22)
                && SetBone(c, "lower_arm_r", 0, 55, 0) && SetBone(c, "thigh_l", -24, 0, 0) && SetBone(c, "calf_l", 38, 0, 0)
                && SetBone(c, "thigh_r", 6, 0, -5) && SetBone(c, "head", 0, 18, 0) && SetBone(c, "spine_02", 6, -8, 0) ? null : "set_bone failed";
        });
        CheckSkeleton("mannequin posed", mid);

        // A drag that is still in progress: the angle label and the wedge are visible.
        _w.Selection.SelectBone(mid, BoneId(man, "head"));
        await Settle();
        var dragPath = _w.Overlay.Gizmo.RingPath(1, 6);
        if (dragPath is not null)
        {
            MouseButtonAt(dragPath[0], true);
            for (var i = 1; i < dragPath.Length; i++) MouseMoveAt(dragPath[i]);
            await Settle();
            Check("p2 a drag in progress shows its angle", () => _w.Overlay.Gizmo.Dragging && _w.Overlay.Gizmo.DragLabel.StartsWith("+38.") ? null : "label '" + _w.Overlay.Gizmo.DragLabel + "'");
            Shot(P("p2-en-dark-rotate-drag-label"), GetViewport());
            MouseButtonAt(dragPath[^1], false);
            _w.DoUndo();
        }
        var elbow = BoneId(man, "lower_arm_l");
        _w.Selection.SelectBone(mid, elbow);
        await Settle();
        Shot(P("p2-en-dark-rotate-gizmo"), GetViewport());

        // ---- mirror ----
        Check("p2 mirror swaps left and right", () =>
        {
            var v = View(mid)!;
            var rig = RigOf(Ch());
            int Idx(string s) => rig.Bones.FindIndex(b => b.Id == rig.FindByStandardName(s)!.Id);
            var hl = v.Joints[Idx("hand_l")]; var hr = v.Joints[Idx("hand_r")];
            var undoBefore = App.Session.UndoHistory.Count;
            if (!_w.MirrorPose()) return "mirror failed";
            var nl = v.Joints[Idx("hand_l")]; var nr = v.Joints[Idx("hand_r")];
            var ok = Dist(nr, new Vector3(-hl.X, hl.Y, hl.Z)) < 1e-3 && Dist(nl, new Vector3(-hr.X, hr.Y, hr.Z)) < 1e-3;
            _w.DoUndo();
            var back = Dist(v.Joints[Idx("hand_l")], hl) < 1e-4;
            return ok && back && App.Session.UndoHistory.Count == undoBefore ? null : $"mirror ok={ok} undo={back}";
        });

        // ---- pose library, copy/paste, reset ----
        string? poseId = null;
        var posedSnapshot = Ch().Pose.Clone();
        Check("p2 save the pose to the library", () =>
        {
            poseId = _w.SavePoseNamed("Left arm raised");
            return poseId is not null && Proj.PoseLibrary.Count == 1 && Proj.PoseLibrary[0].Name == "Left arm raised" && _w.SelectedPoseId == poseId ? null : "not saved";
        });
        Check("p2 reset pose asks for confirmation, then resets", () =>
        {
            _w.ConfirmResetPose();
            if (!_w.ConfirmDialog.Visible) return "no confirmation shown";
            if (Ch().Pose.Bones.Count == 0) return "reset before confirming";
            _w.ConfirmDialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            return Ch().Pose.Bones.Count == 0 ? null : "pose not reset";
        });
        Check("p2 apply the library pose restores it exactly", () =>
        {
            if (!_w.ApplySelectedPose()) return "apply failed";
            return Ch().Pose.ContentEquals(posedSnapshot) ? null : "pose differs";
        });
        Check("p2 copy and paste pose with Ctrl+C / Ctrl+V", () =>
        {
            _w.Stage.GrabFocus();
            Key(Godot.Key.C);
            if (!_w.HasPoseClipboard) return "nothing copied";
            _w.ResetPoseNow();
            if (Ch().Pose.Bones.Count != 0) return "reset failed";
            Key(Godot.Key.V);
            return Ch().Pose.ContentEquals(posedSnapshot) ? null : "pasted pose differs";
        });
        Check("p2 rename and delete a library pose (undoable)", () =>
        {
            if (!_w.RenamePoseNow(poseId!, "Wave") || Proj.PoseLibrary[0].Name != "Wave") return "rename failed";
            if (!_w.DeletePoseNow(poseId!) || Proj.PoseLibrary.Count != 0) return "delete failed";
            _w.DoUndo();
            return Proj.PoseLibrary.Count == 1 ? null : "undo of delete failed";
        });
        Check("p2 tool keys Q / E / W switch tools", () =>
        {
            _w.Stage.GrabFocus();
            PlainKey(Godot.Key.Q); if (_w.Selection.Tool != StageTool.Select) return "Q";
            PlainKey(Godot.Key.W); if (_w.Selection.Tool != StageTool.Move) return "W";
            PlainKey(Godot.Key.E); if (_w.Selection.Tool != StageTool.Rotate) return "E";
            return null;
        });

        // ---- IK: pin both feet, lower the pelvis with the move gizmo ----
        _w.ResetPoseNow();
        await Settle();
        var footL = BoneId(man, "foot_l"); var footR = BoneId(man, "foot_r");
        Vector3 fl0 = default, fr0 = default;
        Check("p2 pin both feet", () =>
        {
            _w.PinBothFeet();
            var rig = RigOf(Ch());
            var pinned = rig.IkChains.Where(c => c.Name.StartsWith("leg_")).All(c => Ch().Pose.Controls[c.Target.ToString()].Pinned);
            var v = View(mid)!;
            fl0 = v.Joints[rig.IndexOf(ObjectId.Parse(footL))]; fr0 = v.Joints[rig.IndexOf(ObjectId.Parse(footR))];
            return pinned ? null : "feet not pinned";
        });
        var pelvis = BoneId(man, "pelvis");
        _w.Selection.SelectBone(mid, pelvis);
        _w.Selection.SetTool(StageTool.Move);
        await Settle();
        Check("p2 move gizmo lowers the pelvis, pinned feet stay put", () =>
        {
            var g = _w.Overlay.Gizmo;
            var len = g.ArrowMetres;
            var p0 = g.AxisPoint(1, len * 0.6f); var p1 = g.AxisPoint(1, len * 0.6f - 0.22f);
            if (p0 is null || p1 is null) return "no axis points";
            var undoBefore = App.Session.UndoHistory.Count;
            Drag(new[] { p0.Value, (p0.Value + p1.Value) * 0.5f, p1.Value });
            var v = View(mid)!;
            var rig = RigOf(Ch());
            var pelvisY = v.Joints[rig.IndexOf(ObjectId.Parse(pelvis))].Y;
            var f1 = v.Joints[rig.IndexOf(ObjectId.Parse(footL))]; var f2 = v.Joints[rig.IndexOf(ObjectId.Parse(footR))];
            GD.Print($"SELFTEST INFO pelvis y = {pelvisY:0.0000} (expected 0.7300), feet moved {Dist(f1, fl0):0.000000} / {Dist(f2, fr0):0.000000} m");
            if (Math.Abs(pelvisY - 0.73) > 2e-3) return "pelvis y " + pelvisY;
            if (Dist(f1, fl0) > 1e-3 || Dist(f2, fr0) > 1e-3) return "feet moved";
            return App.Session.UndoHistory.Count == undoBefore + 1 ? null : "not a single undo step";
        });
        CheckSkeleton("mannequin ik", mid);
        // Lean the torso a bit so the picture shows the IK doing work, and select a foot control.
        SetBone(Ch(), "spine_01", 12, 0, 0); SetBone(Ch(), "spine_02", 8, 6, 0);
        var ikFootL = RigOf(Ch()).Controls.Find(c => c.Name == "ik_foot_l")!.Id.ToString();
        _w.Selection.SelectControl(mid, ikFootL);
        _w.ToastNode.HideNow();
        await Settle();
        Shot(P("p2-en-dark-ik-feet-pinned-pelvis-lowered"), GetViewport());

        Check("p2 rotating a bone of a pinned leg shows the Unpin toast", () =>
        {
            var calf = BoneId(Ch(), "calf_l");
            _w.Selection.SelectBone(mid, calf);
            _w.Selection.SetTool(StageTool.Rotate);
            var path = _w.Overlay.Gizmo.RingPath(0, 3);
            if (path is null) return "no ring";
            var before = Ch().Pose.Clone();
            Drag(path);
            if (!_w.ToastNode.Showing || _w.ToastNode.ActionText != "Unpin") return $"toast showing={_w.ToastNode.Showing} action='{_w.ToastNode.ActionText}'";
            return Ch().Pose.ContentEquals(before) && !App.Session.InTransaction ? null : "pose changed or transaction left open";
        });
        await Settle();
        Shot(P("p2-en-dark-driven-toast"), GetViewport());
        Check("p2 the toast's Unpin button unpins the driving chain", () =>
        {
            _w.ToastNode.InvokeAction();
            var footTarget = RigOf(Ch()).IkChains.Find(c => c.Name == "leg_l")!.Target.ToString();
            return Ch().Pose.Controls[footTarget].Pinned ? "still pinned" : null;
        });
        Check("p2 unpin all, then frame the character with F", () =>
        {
            _w.UnpinAll();
            var anyPinned = Ch().Pose.Controls.Values.Any(c => c.Pinned);
            _w.Selection.ClearItem(); // with a bone selected F frames that bone
            _w.Stage.GrabFocus();
            _w.Stage.Orbit(30, 0); _w.Stage.Zoom(3);
            var d0 = _w.Stage.Distance;
            PlainKey(Godot.Key.F);
            var box = _w.Characters.Bounds(mid)!.Value;
            return !anyPinned && _w.Stage.Target.DistanceTo(box.GetCenter()) < 1e-3 && _w.Stage.Distance > d0 ? null : $"pinned={anyPinned} target={_w.Stage.Target}";
        });

        Check("p2 move gizmo drags an IK hand target on the camera plane", () =>
        {
            var rig = RigOf(Ch());
            var ctl = rig.Controls.Find(c => c.Name == "ik_hand_r")!;
            _w.Selection.SelectControl(mid, ctl.Id.ToString());
            _w.Selection.SetTool(StageTool.Move);
            var g = _w.Overlay.Gizmo;
            var c0 = g.AxisPoint(0, 0f);
            if (c0 is null) return "no gizmo";
            var undoBefore = App.Session.UndoHistory.Count;
            var v = View(mid)!;
            var idx = rig.Controls.IndexOf(ctl);
            var p0 = v.ControlPositions[idx];
            Drag(new[] { c0.Value, c0.Value + new Vector2(20, -14), c0.Value + new Vector2(40, -28) });
            var p1 = v.ControlPositions[idx];
            var hand = v.Joints[rig.IndexOf(rig.IkChains.Find(c => c.Target == ctl.Id)!.End)];
            GD.Print($"SELFTEST INFO IK hand target moved {Dist(p0, p1):0.000} m, hand joint is {Dist(hand, p1):0.0000} m from the target");
            if (Dist(p0, p1) < 0.03) return "control did not move";
            if (Dist(hand, p1) > 2e-3) return "hand did not follow the target";
            return App.Session.UndoHistory.Count == undoBefore + 1 && App.Session.UndoHistory[^1].Label == "Move IK control" ? null : "history";
        });
        Check("p2 pole controls are pickable", () =>
        {
            var rig = RigOf(Ch());
            var pole = rig.Controls.Find(c => c.Name == "pole_knee_l")!;
            var v = View(mid)!;
            if (!_w.Overlay.Project(v.ControlPositions[rig.Controls.IndexOf(pole)], out var s)) return "not projectable";
            _w.Selection.ClearItem();
            MouseButtonAt(s, true); MouseButtonAt(s, false);
            return _w.Selection.ControlId == pole.Id.ToString() ? null : "selected " + _w.Selection.ControlId;
        });

        // ---- rig tree + library panels (Advanced) ----
        _w.ResetPoseNow();
        SetBone(Ch(), "upper_arm_l", 0, 0, 55); SetBone(Ch(), "lower_arm_l", 0, -80, 0); SetBone(Ch(), "thigh_l", -24, 0, 0); SetBone(Ch(), "calf_l", 38, 0, 0);
        SetBone(Ch(), "upper_arm_r", 0, 0, -22); SetBone(Ch(), "lower_arm_r", 0, 55, 0); SetBone(Ch(), "head", 0, 18, 0);
        _w.Overlay.FrameSelection();
        App.SetAdvancedMode(true);
        _w.Selection.SelectBone(mid, uarm);
        _w.Selection.SetTool(StageTool.Rotate);
        _w.Dock.Show(1);
        await Settle();
        Check("p2 rig tree lists all bones and syncs with the selection both ways", () =>
        {
            var tree = _w.Dock.RigTree.Tree;
            var sel = tree.GetSelected();
            if (sel is null || sel.GetMetadata(0).AsString() != "b:" + uarm) return "tree selection not synced from viewport";
            // Select another bone from the tree.
            var target = HumanoidStandard.Head;
            TreeItem? found = null;
            void Walk(TreeItem it) { for (var c = it.GetFirstChild(); c is not null; c = c.GetNext()) { if (c.GetText(0) == target) found = c; Walk(c); } }
            Walk(tree.GetRoot());
            if (found is null) return "head not in tree";
            found.Select(0);
            tree.EmitSignal(Godot.Tree.SignalName.ItemSelected);
            return _w.Selection.BoneId == BoneId(Ch(), "head") ? null : "viewport selection not synced from tree";
        });
        _w.Selection.SelectBone(mid, uarm);
        _w.SavePoseNamed("Ready stance");
        _w.SavePoseNamed("Hands up");
        await Settle();
        Check("p2 poses panel lists the library", () => _w.Dock.Poses.PoseIds.Count == Proj.PoseLibrary.Count && Proj.PoseLibrary.Count >= 2 ? null : "poses list " + _w.Dock.Poses.PoseIds.Count);
        _w.Dock.Show(1);
        App.SetLanguage("ar"); App.SetTheme("dark");
        await Settle();
        Check("p2 RTL mirrors the panels but not the viewport: rail and inspector swap sides, stage stays LTR", () =>
        {
            var stage = _w.Stage.GetGlobalRect();
            var rail = _w.Rail.GetGlobalRect().GetCenter().X; var insp = _w.Inspector.GetGlobalRect().GetCenter().X;
            return rail > stage.GetCenter().X && insp < stage.GetCenter().X && !_w.Stage.IsLayoutRtl() && !_w.Overlay.IsLayoutRtl() ? null
                : $"rail x={rail} inspector x={insp} stage center={stage.GetCenter().X}";
        });
        Shot(P("p2-ar-dark-advanced-rig"), GetViewport());
        _w.Dock.Show(2);
        await Settle();
        Shot(P("p2-ar-dark-advanced-poses"), GetViewport());
        _w.Dock.Show(1);

        // Light theme and the Simple-mode poses toggle.
        App.SetLanguage("en"); App.SetTheme("light"); App.SetAdvancedMode(false);
        await Settle();
        Shot(P("p2-en-light-posed"), GetViewport());
        App.SetTheme("dark");
        _w.Timeline.SelectTab(TimelinePanel.TabPoses);
        await Settle();
        Check("p2 simple mode Poses toggle shows the library as cards", () => _w.Strip.PoseCards.Count == Proj.PoseLibrary.Count && Proj.PoseLibrary.Count >= 2 ? null : "cards " + _w.Strip.PoseCards.Count);
        Shot(P("p2-en-dark-simple-poses"), GetViewport());
        _w.Timeline.SelectTab(TimelinePanel.TabTimeline);
        await ShotMenu(3, "en-dark-menu-rigging");

        // ---- import CesiumMan ----
        string? cesium = null;
        Check("p2 import CesiumMan.glb opens the bone mapping dialog", () =>
        {
            cesium = _w.ImportCharacterPath(Sample("CesiumMan.glb"));
            return cesium is not null && _w.MappingDialog.IsOpen && _w.Selection.CharacterId == cesium ? null : "import failed: " + _w.LastError;
        });
        await Settle();
        GD.Print($"SELFTEST INFO CesiumMan auto-mapping: coverage {_w.MappingDialog.Coverage:P0}, usable={_w.MappingDialog.Usable}, missing required: {string.Join(",", _w.MappingDialog.MissingRequired)}, issues: {string.Join(",", _w.MappingDialog.IssueCodes)}");
        Check("p2 mapping dialog shows a row for every standard bone", () => _w.MappingDialog.RowCount == HumanoidStandard.Bones.Count ? null : "rows " + _w.MappingDialog.RowCount);
        Shot(P("p2-en-dark-import-mapping"), _w.MappingDialog);
        App.SetLanguage("ar");
        await Settle();
        Shot(P("p2-ar-dark-import-mapping"), _w.MappingDialog);
        App.SetLanguage("en");
        await Settle();
        Check("p2 map CesiumMan's bones through the dialog, rig becomes usable", () =>
        {
            var map = new (string std, string bone)[]
            {
                ("pelvis", "Skeleton_torso_joint_1"), ("spine_01", "Skeleton_torso_joint_2"), ("spine_03", "torso_joint_3"), ("neck", "Skeleton_neck_joint_1"),
                ("head", "Skeleton_neck_joint_2"),
                ("upper_arm_r", "Skeleton_arm_joint_R"), ("lower_arm_r", "Skeleton_arm_joint_R__2_"), ("hand_r", "Skeleton_arm_joint_R__3_"),
                ("upper_arm_l", "Skeleton_arm_joint_L__4_"), ("lower_arm_l", "Skeleton_arm_joint_L__3_"), ("hand_l", "Skeleton_arm_joint_L__2_"),
                ("thigh_l", "leg_joint_L_1"), ("calf_l", "leg_joint_L_2"), ("foot_l", "leg_joint_L_3"), ("toe_l", "leg_joint_L_5"),
                ("thigh_r", "leg_joint_R_1"), ("calf_r", "leg_joint_R_2"), ("foot_r", "leg_joint_R_3"), ("toe_r", "leg_joint_R_5"),
            };
            foreach (var (std, bone) in map)
                if (!_w.MappingDialog.Assign(std, bone)) return $"assign {std} <- {bone} failed";
            return _w.MappingDialog.Usable ? null : "still not usable, missing: " + string.Join(",", _w.MappingDialog.MissingRequired);
        });
        await Settle();
        Shot(P("p2-en-dark-import-mapping-done"), _w.MappingDialog);
        _w.MappingDialog.Close(true);
        CheckSkeleton("CesiumMan rest", cesium!);

        // Remove the mannequin so the imported character is alone in the picture.
        Check("p2 remove the mannequin character", () =>
        {
            _w.Selection.SelectCharacter(mid);
            var ok = App.Execute("character.remove", new JsonObject { ["characterId"] = mid }).Ok;
            return ok && Proj.Characters.Count == 1 && _w.Selection.CharacterId == cesium && View(mid) is null ? null : "remove failed";
        });
        var ces = Ch(cesium);
        var cesRig = RigOf(ces);
        Check("p2 pose CesiumMan with IK (feet pinned, pelvis lowered, hands moved) and FK", () => PoseImported(ces) ? null : "posing failed");
        CheckSkeleton("CesiumMan posed", cesium!);
        _w.Overlay.FrameSelection();
        var cesUpper = cesRig.FindByStandardName("upper_arm_l")?.Id.ToString() ?? cesRig.Bones[0].Id.ToString();
        _w.Selection.SelectBone(cesium!, cesUpper);
        await Settle();
        Shot(P("p2-en-dark-cesiumman-posed"), GetViewport());

        // ---- import RiggedFigure, finish the mapping through the dialog code path ----
        string? figure = null;
        Check("p2 import RiggedFigure.glb", () =>
        {
            figure = _w.ImportCharacterPath(Sample("RiggedFigure.glb"));
            return figure is not null ? null : "import failed: " + _w.LastError;
        });
        await Settle();
        GD.Print($"SELFTEST INFO RiggedFigure auto-mapping: coverage {_w.MappingDialog.Coverage:P0}, usable={_w.MappingDialog.Usable}, missing required: {string.Join(",", _w.MappingDialog.MissingRequired)}");
        Check("p2 assign the required bones in the mapping dialog, rig becomes usable", () =>
        {
            var map = new (string std, string bone)[]
            {
                ("pelvis", "torso_joint_1"), ("spine_01", "torso_joint_2"), ("spine_03", "torso_joint_3"), ("head", "neck_joint_2"),
                ("upper_arm_l", "arm_joint_L_1"), ("lower_arm_l", "arm_joint_L_2"), ("hand_l", "arm_joint_L_3"),
                ("upper_arm_r", "arm_joint_R_1"), ("lower_arm_r", "arm_joint_R_2"), ("hand_r", "arm_joint_R_3"),
                ("thigh_l", "leg_joint_L_1"), ("calf_l", "leg_joint_L_2"), ("foot_l", "leg_joint_L_3"),
                ("thigh_r", "leg_joint_R_1"), ("calf_r", "leg_joint_R_2"), ("foot_r", "leg_joint_R_3"),
            };
            var undoBefore = App.Session.UndoHistory.Count;
            foreach (var (std, bone) in map)
                if (!_w.MappingDialog.Assign(std, bone)) return $"assign {std} <- {bone} failed";
            if (!_w.MappingDialog.Usable) return "still not usable, missing: " + string.Join(",", _w.MappingDialog.MissingRequired);
            _w.MappingDialog.Close(true);
            var added = App.Session.UndoHistory.Count - undoBefore;
            return added == 1 ? null : $"mapping edits made {added} undo steps (expected 1)";
        });
        Check("p2 cancelling the mapping dialog rolls everything back", () =>
        {
            var rig = RigOf(Ch(figure));
            var before = rig.HumanoidMap.Count;
            _w.OpenBoneMapping(rig.Id.ToString());
            _w.MappingDialog.Assign("toe_l", "leg_joint_L_5");
            var changed = rig.HumanoidMap.Count != before;
            _w.MappingDialog.Close(false);
            return changed && rig.HumanoidMap.Count == before ? null : $"changed={changed} before={before} after={rig.HumanoidMap.Count}";
        });
        CheckSkeleton("RiggedFigure rest", figure!);
        Check("p2 pose RiggedFigure with IK and FK", () => PoseImported(Ch(figure)) ? null : "posing failed");
        CheckSkeleton("RiggedFigure posed", figure!);
        // Show only RiggedFigure for its picture: remove CesiumMan (undoable).
        App.Execute("character.remove", new JsonObject { ["characterId"] = cesium });
        _w.Selection.SelectCharacter(figure);
        _w.Selection.SelectBone(figure!, BoneId(Ch(figure), "upper_arm_l"));
        _w.Overlay.FrameSelection();
        await Settle();
        Shot(P("p2-en-dark-riggedfigure-posed"), GetViewport());
        _w.DoUndo(); // bring CesiumMan back so both imported files take part in the persistence check
        await Settle();
        CheckSkeleton("CesiumMan after undo of remove", cesium!);

        // ---- save, reopen, poses identical ----
        var path = System.IO.Path.Combine(_dir, "phase2.blazanim");
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        var snapshot = Proj.Characters.ToDictionary(c => c.Id.ToString(), c => c.Pose.Clone());
        var libCount = Proj.PoseLibrary.Count;
        Check("p2 save the project with characters, poses and library", () => _w.SaveToPath(path) && !App.IsDirty ? null : "save failed");
        Check("p2 reopen: characters, rigs, library and poses are identical", () =>
        {
            _w.NewProjectNow();
            if (!_w.OpenPath(path)) return "open failed: " + _w.LastError;
            if (Proj.Characters.Count != snapshot.Count) return $"characters {Proj.Characters.Count} vs {snapshot.Count}";
            foreach (var c in Proj.Characters)
                if (!snapshot.TryGetValue(c.Id.ToString(), out var p) || !p.ContentEquals(c.Pose)) return "pose differs for " + c.Name;
            return Proj.PoseLibrary.Count == libCount && Proj.Assets.Count == 2 ? null : $"library {Proj.PoseLibrary.Count}/{libCount} assets {Proj.Assets.Count}";
        });
        await Settle();
        foreach (var c in Proj.Characters.ToList())
            CheckSkeleton("after reopen " + c.Name, c.Id.ToString());
        Check("p2 reopened imported characters render with their meshes", () =>
            Proj.Characters.All(c => View(c.Id.ToString()) is { LoadError: null, MeshCount: > 0, IsImported: true }) ? null : "an imported view has no meshes");
        _w.Selection.SetTool(StageTool.Rotate);
        await Settle();
    }

    /// <summary>Pin the feet, lower the pelvis, move both hands with IK and turn the head (FK, relative to rest).</summary>
    bool PoseImported(Character c)
    {
        var cid = c.Id.ToString();
        var rig = RigOf(c);
        _w.Selection.SelectCharacter(cid);
        var v = View(cid)!;
        var h = _w.Characters.Bounds(cid)!.Value.Size.Y;
        _w.PinBothFeet();
        var pel = rig.FindByStandardName("pelvis")!;
        var local = c.Pose.LocalOf(pel);
        var e = local.Rotation.ToEulerXYZ() * (180 / Math.PI);
        var ok = App.Execute("pose.set_bone", new JsonObject
        {
            ["characterId"] = cid, ["bone"] = pel.Id.ToString(), ["rx"] = e.X, ["ry"] = e.Y, ["rz"] = e.Z,
            ["tx"] = local.Translation.X, ["ty"] = local.Translation.Y - 0.07 * h, ["tz"] = local.Translation.Z,
        }).Ok;
        foreach (var (name, d) in new[] { ("ik_hand_l", new Vector3(0.12f, 0.30f, 0.14f)), ("ik_hand_r", new Vector3(-0.05f, 0.10f, 0.30f)) })
        {
            var idx = rig.Controls.FindIndex(x => x.Name == name);
            if (idx < 0) return false;
            var p = v.ControlPositions[idx] + d * h;
            ok &= App.Execute("pose.set_control", new JsonObject { ["characterId"] = cid, ["control"] = name, ["x"] = (double)p.X, ["y"] = (double)p.Y, ["z"] = (double)p.Z }).Ok;
        }
        var head = rig.FindByStandardName("head")!;
        var q = head.Rest.Rotation * Quat.FromEulerXYZ(new Vec3(0, 0, 25 * Math.PI / 180));
        var he = q.ToEulerXYZ() * (180 / Math.PI);
        ok &= SetBone(c, head.Id.ToString(), he.X, he.Y, he.Z);
        return ok;
    }

    bool SetStd(Character c, string standard, double rx, double ry, double rz)
    {
        var rig = RigOf(c);
        var b = rig.FindByStandardName(standard);
        if (b is null) { GD.Print("SELFTEST INFO no bone mapped to " + standard + " in " + rig.Name); return false; }
        return SetBone(c, b.Id.ToString(), rx, ry, rz);
    }
}
