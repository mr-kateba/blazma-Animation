using System.Text.Json.Nodes;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 4 scenario: Export Animation dialog. The export runs through the dialog's real code path (Ctrl+E, fields, Export button,
/// overwrite prompt), then the written .glb is loaded back with Godot's own glTF importer (the "reimport" check).
/// </summary>
public partial class SelfTest
{
    async Task WaitRun(ExportDialog d)
    {
        for (var i = 0; i < 600 && (d.RunTask is null || !d.RunTask.IsCompleted); i++) await Frames(1);
        if (d.RunTask is null || !d.RunTask.IsCompleted) throw new TimeoutException("export did not finish");
        await d.RunTask;
    }

    static Node? FindOfClass(Node root, string cls)
    {
        if (root.GetClass() == cls) return root;
        foreach (var c in root.GetChildren())
            if (FindOfClass(c, cls) is { } hit) return hit;
        return null;
    }

    async Task RunPhase4()
    {
        var s = App.Settings;
        var lastDirBackup = s.LastExportDir;
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        var dir = System.IO.Path.Combine(Godot.ProjectSettings.GlobalizePath("user://"), "export_selftest");
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        System.IO.Directory.CreateDirectory(dir);
        s.LastExportDir = dir;

        _w.NewProjectNow();
        await Settle();
        var man = Ch();
        var mid = man.Id.ToString();
        var rig = RigOf(man);
        var uarm = BoneId(man, "upper_arm_l");

        string clip = "";
        Check("p4 small clip with an arm wave (3 keys over 24 frames)", () =>
        {
            clip = _w.CreateClipNamed("Wave Test") ?? "";
            if (clip == "" || !_w.SetClipDurationNow(clip, 24)) return "no clip";
            foreach (var (frame, rz) in new[] { (0, 0.0), (12, 80.0), (24, 10.0) })
            {
                if (!SetBone(man, uarm, 0, 0, rz)) return "pose failed";
                if (!App.Execute("key.set", new JsonObject { ["clipId"] = clip, ["characterId"] = mid, ["frame"] = frame }).Ok) return "key failed at " + frame;
            }
            return Proj.Clips.Count == 1 ? null : "clip count " + Proj.Clips.Count;
        });
        await Settle();

        // ---- open with Ctrl+E (the shortcut path) ----
        Check("p4 Ctrl+E opens the Export dialog with the current clip, character and an automatic file name", () =>
        {
            Key(Godot.Key.E);
            var d = _w.ExportDlg;
            if (!d.Visible) return "dialog not visible";
            if (d.SelectedClipId != clip || d.SelectedCharacterId != mid) return $"clip={d.SelectedClipId} char={d.SelectedCharacterId}";
            if (System.IO.Path.GetFileName(d.PathText) != "Wave Test.glb") return "default name: " + d.PathText;
            if (System.IO.Path.GetDirectoryName(d.PathText) != dir) return "default folder: " + d.PathText;
            return d.WholeClip && d.SidecarChecked ? null : "defaults wrong";
        });
        await Settle();

        var dlg = _w.ExportDlg;
        var glb = System.IO.Path.Combine(dir, "wave.glb");
        dlg.SetPathForTest(glb);
        dlg.SetRootMotionForTest(1); // In place
        Shot(P("p4-en-dark-export-dialog-options"), dlg);
        dlg.PressExport();
        await Frames(1);
        Check("p4 busy state is shown while exporting", () => dlg.Busy && dlg.ResultText.Contains("Exporting") ? null : $"busy={dlg.Busy} text={dlg.ResultText}");
        await WaitRun(dlg);
        await Settle();

        string animName = "";
        Check("p4 export through the dialog wrote the .glb and the sidecar, validation ok", () =>
        {
            var r = dlg.LastResult;
            if (r is null || !r.Ok) return "export failed: " + (r?.Error?.Message ?? dlg.LastError);
            if (!System.IO.File.Exists(glb) || new System.IO.FileInfo(glb).Length < 500) return "glb missing or tiny";
            if (!System.IO.File.Exists(System.IO.Path.Combine(dir, "wave.blazma.json"))) return "sidecar missing";
            if ((bool?)r.Data!["validation"]!["ok"] != true) return "validation: " + r.Data["validation"];
            animName = (string)r.Data["animation"]!;
            var frames = (int)r.Data["frames"]!;
            if (frames != Proj.Clips[0].DurationFrames + 1) return $"frames={frames} clip={Proj.Clips[0].DurationFrames}";
            if (!dlg.OpenFolderVisible) return "Open folder button hidden";
            if (s.LastExportDir != dir) return "last export dir not stored";
            return dlg.ResultText.Contains("Validation: OK") && dlg.ResultText.Contains(glb) ? null : "result text: " + dlg.ResultText;
        });
        Shot(P("p4-en-dark-export-dialog"), dlg);

        // ---- Godot reimport: load the file with Godot's own glTF importer ----
        Check("p4 Godot imports the .glb: all bone nodes and an AnimationPlayer with the clip's animation", () =>
        {
            var doc = new GltfDocument();
            var state = new GltfState();
            var err = doc.AppendFromFile(glb, state);
            if (err != Error.Ok) return "append_from_file: " + err;
            var scene = doc.GenerateScene(state);
            if (scene is null) return "generate_scene returned null";
            try
            {
                if (FindOfClass(scene, "AnimationPlayer") is not AnimationPlayer ap) return "no AnimationPlayer in the imported scene";
                var names = ap.GetAnimationList();
                foreach (var n in names) GD.Print($"p4 reimport anim '{n}' tracks={ap.GetAnimation(n).GetTrackCount()} len={ap.GetAnimation(n).Length}");
                var hit = names.FirstOrDefault(n => n == animName) ?? names.FirstOrDefault(n => n.Contains("Wave", StringComparison.OrdinalIgnoreCase));
                if (hit is null) return $"animation '{animName}' not in [{string.Join(",", names)}]";
                var anim = ap.GetAnimation(hit);
                var fps = Proj.Clips[0].Fps.FramesPerSecond;
                if (Math.Abs(anim.Length - Proj.Clips[0].DurationFrames / fps) > 0.02) return $"length {anim.Length} expected {Proj.Clips[0].DurationFrames / fps}";
                // Godot's importer drops tracks that never change, so only the waving arm is expected to remain.
                var armName = rig.GetBone(Blazma.Core.Ids.ObjectId.Parse(uarm)).Name;
                var paths = Enumerable.Range(0, anim.GetTrackCount()).Select(i => anim.TrackGetPath(i).ToString()).ToList();
                if (!paths.Any(p => p.Contains(armName))) return $"no track for '{armName}': " + string.Join(" | ", paths);
                // The mannequin export is a plain node hierarchy (one Node3D per bone), not a skinned mesh.
                var missing = rig.Bones.Where(b => scene.FindChild(b.Name, true, false) is null).Select(b => b.Name).ToList();
                if (missing.Count > 0) return "bone nodes missing after import: " + string.Join(",", missing);
                GD.Print($"p4 reimport: animation '{hit}' {anim.Length:0.###}s tracks={anim.GetTrackCount()} bone nodes={rig.Bones.Count}");
                return null;
            }
            finally { scene.Free(); }
        });

        // ---- Arabic (RTL) dialog + overwrite confirmation ----
        App.SetLanguage("ar");
        await Settle();
        dlg.PressExport();
        await Settle();
        Check("p4 exporting to an existing file asks before replacing it", () =>
            dlg.OverwritePromptVisible ? null : "no overwrite prompt");
        Shot(P("p4-ar-dark-export-overwrite"), dlg);
        var stamp = System.IO.File.GetLastWriteTimeUtc(glb);
        dlg.ConfirmOverwrite();
        await WaitRun(dlg);
        await Settle();
        Check("p4 confirming the prompt replaces the file (overwrite=true)", () =>
            dlg.LastResult is { Ok: true } && dlg.LastError is null && (bool?)dlg.LastResult.Data!["validation"]!["ok"] == true ? null : $"ok={dlg.LastResult?.Ok} err={dlg.LastError} {dlg.LastResult?.Error?.Message} valid={dlg.LastResult?.Data?["validation"]}");

        Shot(P("p4-ar-dark-export-dialog"), dlg);

        // ---- custom range, keep root motion, no sidecar ----
        App.SetLanguage("en");
        await Settle();
        var glb2 = System.IO.Path.Combine(dir, "range.glb");
        dlg.SetPathForTest(glb2);
        dlg.SetRootMotionForTest(0);
        dlg.SetCustomRangeForTest(4, 10);
        dlg.SidecarChecked = false;
        dlg.PressExport();
        await WaitRun(dlg);
        Check("p4 custom frame range 4..10 exports 7 frames, validation ok, no sidecar", () =>
        {
            var r = dlg.LastResult;
            if (r is null || !r.Ok) return "failed: " + (r?.Error?.Message ?? dlg.LastError);
            if ((int)r.Data!["frames"]! != 7 || (long)r.Data["startFrame"]! != 4 || (long)r.Data["endFrame"]! != 10) return "data: " + r.Data;
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, "range.blazma.json"))) return "sidecar written although unchecked";
            return (bool?)r.Data["validation"]!["ok"] == true ? null : "validation failed";
        });

        // ---- readable errors, in Arabic ----
        App.SetLanguage("ar");
        await Settle();
        dlg.SetPathForTest(System.IO.Path.Combine(dir, "no-such-folder", "x.glb"));
        dlg.PressExport();
        await Settle();
        Check("p4 a missing folder is reported in Arabic inside the dialog (no raw error code)", () =>
        {
            var e = dlg.LastError;
            if (e is null) return "no error shown";
            var arabic = e.Any(c => c >= '؀' && c <= 'ۿ');
            return arabic && !e.Contains("not_found") ? null : "message: " + e;
        });
        Shot(P("p4-ar-dark-export-error"), dlg);

        dlg.Hide();
        await Settle();
        Check("p4 the Export menu item and timeline button are enabled; the dialog is closed", () =>
        {
            var m = _w.MenuPopup(0);
            var idx = m.GetItemIndex(6);
            return idx >= 0 && !m.IsItemDisabled(idx) && !_w.Timeline.ExportButton.Disabled && !dlg.Visible ? null : "menu/button state wrong";
        });

        App.SetLanguage("en");
        s.LastExportDir = lastDirBackup;
        await Settle();
    }
}
