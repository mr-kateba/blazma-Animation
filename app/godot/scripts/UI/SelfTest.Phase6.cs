using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Physics;
using Blazma.UI.App;
using Godot;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 6 scenario: balance overlays, the timeline balance strip and the Physics panel on a deliberately off-balance clip (the
/// acceptance motion of the physics tests: feet pinned, hips pushed forward, trunk leaning 60°). Auto-balance and Secondary motion
/// run through the real panel button and dialog; Ctrl+Z / Ctrl+Shift+Z are the real shortcuts.
/// </summary>
public partial class SelfTest
{
    static bool HasArabic(string s) => s.Any(c => c >= '؀' && c <= 'ۿ');

    string ClipJson(string clipId) => JsonSerializer.Serialize(Proj.Clips.Find(c => c.Id.ToString() == clipId), ProjectArchive.JsonOptions);

    bool Cmd(string id, object args) => App.Execute(id, Jo(args)).Ok;

    int KeyCount(string clipId, params string[] bones)
    {
        var clip = Proj.Clips.Find(c => c.Id.ToString() == clipId)!;
        var ids = bones.Select(b => RigOf(Ch()).FindByStandardName(b)!.Id).ToHashSet();
        return clip.Tracks.Where(t => ids.Contains(t.Target.Id) && t.Channel == TrackChannels.Rotation).Sum(t => t.Keys.Count);
    }

    async Task RunPhase6()
    {
        var s = App.Settings;
        var saved = (s.ShowPhysics, s.ShowPhysicsPaths, s.PhysicsPanel);
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        App.SetShowTrail(false); App.SetShowOnion(false);
        App.SetShowPhysics(false); App.SetShowPhysicsPaths(false); App.SetPhysicsPanel(false);
        _w.ShowAgentPanel(false);
        _w.NewProjectNow();
        _w.Timeline.SelectTab(TimelinePanel.TabTimeline);
        await Settle();
        var man = Ch();
        var mid = man.Id.ToString();
        var strip = _w.Timeline.Balance;
        var panel = _w.PhysicsPanelNode;

        Check("p6 everything is off by default: no overlay, no strip, no panel, no analysis", () =>
        {
            if (s.ShowPhysics || s.ShowPhysicsPaths || s.PhysicsPanel) return "a physics setting is on by default";
            if (strip.Visible || panel.Visible) return "strip or panel visible";
            return _w.PhysicsView.DrawnState is null && App.Physics.ClipComputations == 0 ? null : "something was analysed or drawn";
        });

        // ---- the off-balance motion: feet pinned, frames 15..25 crouch with the hips forward and the trunk leaning 60 degrees ----
        string clip = "";
        Check("p6 build the off-balance clip (the acceptance motion of the physics tests)", () =>
        {
            clip = _w.CreateClipNamed("Lean") ?? "";
            if (clip == "" || !_w.SetClipDurationNow(clip, 41)) return "no clip";
            var ok = Cmd("pose.set_bone", new { characterId = mid, bone = H.UpperArm(Blazma.Rigging.Side.Left), rx = 0, ry = 0, rz = -80 })
                && Cmd("pose.set_bone", new { characterId = mid, bone = H.UpperArm(Blazma.Rigging.Side.Right), rx = 0, ry = 0, rz = 80 })
                && Cmd("pose.set_pinned", new { characterId = mid, control = "ik_foot_l", pinned = true })
                && Cmd("pose.set_pinned", new { characterId = mid, control = "ik_foot_r", pinned = true })
                && Cmd("key.set", new { clipId = clip, characterId = mid, frame = 0 })
                && Cmd("pose.set_bone", new { characterId = mid, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, tx = 0, ty = 0.83, tz = 0.12 })
                && Cmd("pose.set_bone", new { characterId = mid, bone = H.Spine1, rx = 60, ry = 0, rz = 0 })
                && Cmd("key.set", new { clipId = clip, characterId = mid, frame = 15 })
                && Cmd("key.set", new { clipId = clip, characterId = mid, frame = 25 })
                && Cmd("pose.set_bone", new { characterId = mid, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, tx = 0, ty = 0.95, tz = 0 })
                && Cmd("pose.set_bone", new { characterId = mid, bone = H.Spine1, rx = 0, ry = 0, rz = 0 })
                && Cmd("key.set", new { clipId = clip, characterId = mid, frame = 40 });
            return ok && Proj.Clips.Count == 1 && App.ActiveClipId == clip ? null : "could not build the motion";
        });
        if (_w.Characters.Bounds(mid) is { } box) _w.Stage.Frame(box);
        App.GoToFrame(0);
        await Settle();

        // ---- turn the overlays on with the real toolbar toggle ----
        _w.PhysicsButton.ButtonPressed = true;
        await Settle();
        var report = App.Physics.Report;
        Check("p6 the Balance toggle turns the overlays on, persists the setting and shows the timeline strip", () =>
        {
            if (!s.ShowPhysics) return "setting not on";
            if (!strip.Visible) return "strip hidden";
            if (report is null) return "no report";
            return _w.PhysicsView.DrawnState == BalanceState.Balanced && _w.PhysicsView.DrewComMarker ? null : "frame 0 should draw as balanced, state=" + _w.PhysicsView.DrawnState;
        });

        Check("p6 the strip colours the unbalanced frames: one range inside 5..35, none airborne", () =>
        {
            var (good, marginal, bad, air) = strip.CellCounts();
            if (bad == 0) return $"no unbalanced cells ({good}/{marginal}/{bad}/{air})";
            var ranges = report!.UnbalancedRanges;
            if (ranges.Count != 1 || ranges[0].Start < 5 || ranges[0].Start > 15 || ranges[0].End < 25 || ranges[0].End > 35) return "ranges: " + string.Join(",", ranges.Select(r => $"{r.Start}-{r.End}"));
            if (air != 0 || good + marginal + bad != 41) return $"counts {good}/{marginal}/{bad}/{air}";
            // the deeper the frame, the stronger the red
            var worst = BalanceStrip.CellColor(_w.Palette, report.Frames[(int)ranges[0].WorstFrame]);
            var edge = BalanceStrip.CellColor(_w.Palette, report.Frames[(int)ranges[0].Start]);
            return worst.A >= edge.A && worst.R > worst.G ? null : $"heat: worst alpha {worst.A} edge alpha {edge.A}";
        });

        Check("p6 the cached report equals physics.analyze (same numbers as the command)", () =>
        {
            var r = App.Execute("physics.analyze", Jo(new { clipId = clip, summaryOnly = true }));
            if (!r.Ok) return r.Error?.Message;
            var sum = r.Data!["summary"]!;
            return (int)sum["unbalanced"]! == report!.UnbalancedFrames && (int)sum["balanced"]! == report.BalancedFrames
                && (long)sum["unbalancedRanges"]![0]!["worstFrame"]! == report.UnbalancedRanges[0].WorstFrame ? null : sum.ToJsonString();
        });

        Check("p6 scrubbing and playing only look frames up: the clip is analysed once", () =>
        {
            var n = App.Physics.ClipComputations;
            for (var f = 0; f <= 40; f += 4) App.GoToFrame(f);
            App.GoToFrame(0); App.Play();
            for (var i = 0; i < 6; i++) App.AdvancePlayback(0.1);
            App.Pause();
            return App.Physics.ClipComputations == n && n == 1 ? null : $"analyses {n} -> {App.Physics.ClipComputations}";
        });
        Check("p6 timing: analysing the whole clip is fast enough to redo after an edit", () =>
        {
            var an = new BalanceAnalyzer(RigOf(man));
            var c = Proj.Clips.Find(x => x.Id.ToString() == clip)!;
            an.AnalyzeClip(c); // warm up
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 5; i++) an.AnalyzeClip(c);
            var perFrame = sw.Elapsed.TotalMilliseconds / 5 / c.DurationFrames;
            GD.Print($"SELFTEST INFO balance analysis: {perFrame:0.000} ms per frame ({c.DurationFrames} frames); a 600 frame clip takes about {perFrame * 600:0} ms");
            return perFrame < 1.0 ? null : $"{perFrame} ms per frame";
        });
        await Settle();

        var range = report!.UnbalancedRanges[0];
        Check("p6 clicking an unbalanced cell of the strip jumps to the worst frame of that range", () =>
        {
            var edge = range.Start == range.WorstFrame ? range.End : range.Start;
            Click(strip.CellCenter(edge));
            if (App.CurrentFrame != range.WorstFrame) return $"frame {App.CurrentFrame}, worst {range.WorstFrame}";
            Click(strip.CellCenter(0));
            return App.CurrentFrame == 0 ? null : "balanced cell did not go to its own frame: " + App.CurrentFrame;
        });
        Check("p6 the strip tooltip names the frame, the margin and the state", () =>
        {
            var t = strip.TooltipFor(range.WorstFrame);
            var cm = (range.MinMargin * 100).ToString("0.0", CultureInfo.InvariantCulture).Replace('-', '\u2212');
            return t.Contains("Frame") && t.Contains("Unbalanced") && t.Contains("cm") && t.Contains(cm) && t.Contains(range.WorstFrame.ToString(CultureInfo.InvariantCulture)) && t.Contains('\u2212')
                ? null : "tooltip: " + t;
        });

        App.GoToFrame(range.WorstFrame);
        await Settle();
        Check("p6 on the worst frame the viewport draws an unbalanced state: COM sphere, polygon, contact points", () =>
        {
            var v = _w.PhysicsView;
            return v.DrawnState == BalanceState.Unbalanced && v.DrewComMarker && v.DrawnPolygonPoints >= 3 && v.DrawnContacts >= 4 ? null
                : $"state={v.DrawnState} com={v.DrewComMarker} poly={v.DrawnPolygonPoints} contacts={v.DrawnContacts}";
        });

        // ---- Simple mode: the panel is reached from the Physics menu; Advanced adds a toolbar button ----
        Check("p6 Simple mode has no panel button; the Physics menu opens the panel", () =>
        {
            if (_w.PhysicsPanelButton.Visible) return "panel button visible in Simple mode";
            var menu = _w.MenuPopup(5);
            if (menu.ItemCount < 5) return "menu items " + menu.ItemCount;
            menu.EmitSignal(PopupMenu.SignalName.IdPressed, 47);
            if (!s.PhysicsPanel || !panel.Visible) return "panel not opened";
            return panel.GetGlobalRect().GetCenter().X > _w.Stage.GetGlobalRect().GetCenter().X ? null : "panel should sit on the inline-end side (right in English)";
        });
        await Settle();
        Check("p6 the panel shows this frame (COM height, margin, state) and the clip summary with a clickable range", () =>
        {
            if (panel.StateText != "Unbalanced") return "state " + panel.StateText;
            if (!panel.MarginText.Contains('\u2212') || !panel.MarginText.Contains("cm")) return "margin " + panel.MarginText;
            if (!panel.HeightText.Contains("cm")) return "height " + panel.HeightText;
            var counts = panel.CountsText.Split('/');
            if (int.Parse(counts[1]) != report.UnbalancedFrames || int.Parse(counts[0]) != report.BalancedFrames) return "counts " + panel.CountsText;
            return panel.RangeButtons.Count == 1 ? null : "range buttons " + panel.RangeButtons.Count;
        });
        Check("p6 clicking the range in the panel goes to its worst frame", () =>
        {
            App.GoToFrame(0);
            panel.RangeButtons[0].EmitSignal(BaseButton.SignalName.Pressed);
            return App.CurrentFrame == range.WorstFrame ? null : "frame " + App.CurrentFrame;
        });

        App.SetAdvancedMode(true);
        await Settle();
        Check("p6 Advanced mode shows the panel toolbar button (pressed while the panel is open)", () =>
            _w.PhysicsPanelButton.Visible && _w.PhysicsPanelButton.ButtonPressed && panel.Visible ? null : "button state wrong");

        // ---- paths ----
        App.SetShowPhysicsPaths(true);
        await Settle();
        Check("p6 the paths option draws the COM, hip and foot paths over the whole clip", () =>
        {
            var v = _w.PhysicsView;
            return v.DrawnPathPoints >= 3 * 41 && App.Physics.PathComputations == 1 ? null : $"path points {v.DrawnPathPoints}, computations {App.Physics.PathComputations}";
        });

        _w.ToastNode.HideNow();
        await Settle();
        Shot(P("p6-en-dark-advanced-panel"), GetViewport());
        App.SetAdvancedMode(false); // the before / after pair is shot in Simple mode: the stage and the panel have the most room
        await Settle();

        // ---- BEFORE screenshot, then Auto-balance with the real button ----
        var jsonBefore = ClipJson(clip);
        var undoBefore = App.Session.UndoHistory.Count;
        _w.ToastNode.HideNow();
        await Settle();
        Shot(P("p6-en-dark-unbalanced-before"), GetViewport());

        panel.AutoBalanceButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Settle();
        var jsonAfter = ClipJson(clip);
        Check("p6 Auto-balance (panel button) corrects the clip in one undo step", () =>
        {
            if (panel.LastAutoBalance is not { Ok: true } r) return "failed: " + (panel.LastError ?? panel.LastAutoBalance?.Error?.Message);
            if (App.Session.UndoHistory.Count != undoBefore + 1 || App.Session.UndoHistory[^1].Label != "Auto-balance") return "history: " + App.Session.UndoHistory.Count + " " + App.Session.UndoHistory[^1].Label;
            if (jsonAfter == jsonBefore) return "clip not changed";
            return (int)r.Data!["corrected"]! >= report.UnbalancedFrames ? null : "corrected " + r.Data["corrected"];
        });
        Check("p6 afterwards every frame is balanced: strip, report and the command agree", () =>
        {
            var fresh = App.Physics.Report!;
            var (_, _, bad, _) = strip.CellCounts();
            var sum = App.Execute("physics.analyze", Jo(new { clipId = clip, summaryOnly = true })).Data!["summary"]!;
            if (fresh.UnbalancedFrames != 0 || bad != 0 || (int)sum["unbalanced"]! != 0) return $"unbalanced {fresh.UnbalancedFrames}/{bad}/{sum["unbalanced"]}";
            return App.Physics.ClipComputations == 2 && fresh.Worst!.Margin > 0.019 ? null : $"computations {App.Physics.ClipComputations}, worst margin {fresh.Worst?.Margin}";
        });
        Check("p6 the panel shows the before/after table with the right numbers", () =>
        {
            if (!panel.ResultVisible) return "no result";
            var t = panel.TableText;
            var before = report.UnbalancedFrames.ToString(CultureInfo.InvariantCulture);
            return t.StartsWith(before + ">0 ") && t.Contains("\u2212") && t.Contains("+2.0") && panel.ResultText.Contains("Every frame is balanced") ? null : t + " // " + panel.ResultText;
        });
        Check("p6 the corrected frame is drawn balanced", () =>
            _w.PhysicsView.DrawnState == BalanceState.Balanced && panel.StateText == "Balanced" ? null : $"overlay {_w.PhysicsView.DrawnState}, panel {panel.StateText}");
        Shot(P("p6-en-dark-balanced-after"), GetViewport());

        // ---- undo / redo through the real shortcuts ----
        Key(Godot.Key.Z);
        await Settle();
        Check("p6 Ctrl+Z restores the unbalanced clip exactly; strip, overlay and panel follow", () =>
        {
            if (ClipJson(clip) != jsonBefore) return "clip differs after undo";
            var (_, _, bad, _) = strip.CellCounts();
            if (bad != report.UnbalancedFrames) return "strip unbalanced cells " + bad;
            if (panel.ResultVisible) return "stale result still shown";
            return _w.PhysicsView.DrawnState == BalanceState.Unbalanced && panel.StateText == "Unbalanced" ? null : $"overlay {_w.PhysicsView.DrawnState}, panel {panel.StateText}";
        });
        Key(Godot.Key.Z, shift: true);
        await Settle();
        Check("p6 Ctrl+Shift+Z re-applies it exactly", () =>
            ClipJson(clip) == jsonAfter && App.Physics.Report!.UnbalancedFrames == 0 ? null : "redo differs");

        // ---- Arabic, RTL: the same story ----
        Key(Godot.Key.Z);
        await Settle();
        App.SetLanguage("ar");
        await Settle();
        Check("p6 Arabic: panel on the inline-end side (left), mirrored, Arabic text, numbers left to right", () =>
        {
            var stage = _w.Stage.GetGlobalRect();
            var r = panel.GetGlobalRect();
            if (r.GetCenter().X > stage.GetCenter().X) return "panel is on the right in Arabic (the inline-end side is the left)";
            if (!panel.IsLayoutRtl() || _w.Stage.IsLayoutRtl() || _w.PhysicsView.IsLayoutRtl() || strip.IsLayoutRtl()) return "layout directions wrong";
            if (!HasArabic(panel.StateText)) return "state not translated: " + panel.StateText;
            if (!panel.MarginText.StartsWith('\u2066') || !panel.MarginText.EndsWith('\u2069')) return "margin not isolated: " + panel.MarginText;
            if (panel.RangeButtons.Count != 1 || !HasArabic(panel.RangeButtons[0].Text) || !panel.RangeButtons[0].Text.Contains('\u2066')) return "range text: " + panel.RangeButtons.FirstOrDefault()?.Text;
            var tip = strip.TooltipFor(range.WorstFrame);
            return HasArabic(tip) && tip.Contains('\u2066') ? null : "tooltip: " + tip;
        });
        App.GoToFrame(range.WorstFrame);
        await Settle();
        _w.ToastNode.HideNow();
        Shot(P("p6-ar-dark-unbalanced-before"), GetViewport());
        panel.AutoBalanceButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Settle();
        Check("p6 Arabic: Auto-balance works and the result is in Arabic", () =>
            panel.LastAutoBalance is { Ok: true } && App.Physics.Report!.UnbalancedFrames == 0 && HasArabic(panel.ResultText) && panel.TableText.Contains("+2.0") ? null : "result: " + panel.ResultText);
        Shot(P("p6-ar-dark-balanced-after"), GetViewport());

        // ---- Secondary motion through the dialog ----
        App.SetLanguage("en");
        await Settle();
        var spineBones = new[] { H.Spine1, H.Spine2, H.Chest, H.Neck, H.Head };
        var keysBefore = KeyCount(clip, spineBones);
        var jsonPre = ClipJson(clip);
        var undoPre = App.Session.UndoHistory.Count;
        panel.SecondaryButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Settle();
        var dlg = _w.SecondaryDlg;
        Check("p6 'Secondary motion…' opens the dialog with defaults (spine preset, k 150, c 12, 30 degrees, whole clip)", () =>
            dlg.Visible && dlg.Preset.Selected == 0 && dlg.Stiffness.Value == 150 && dlg.Damping.Value == 12 && dlg.MaxAngle.Value == 30 && dlg.WholeClip.ButtonPressed
                && dlg.FeelText.Contains("1.9") && dlg.FeelText.Contains("0.49") ? null
                : $"visible={dlg.Visible} preset={dlg.Preset.Selected} k={dlg.Stiffness.Value} c={dlg.Damping.Value} max={dlg.MaxAngle.Value} whole={dlg.WholeClip.ButtonPressed} {dlg.FeelText}");
        Shot(P("p6-en-dark-secondary-dialog"), dlg);
        dlg.PressApply();
        await Settle();
        Check("p6 Apply adds rotation keys to the spine chain on every frame (one undo step)", () =>
        {
            if (dlg.LastResult is not { Ok: true } r) return "failed: " + (dlg.LastError ?? dlg.LastResult?.Error?.Message);
            var added = KeyCount(clip, spineBones) - keysBefore;
            if (added < 5 * 41 - keysBefore) return $"keys added {added}";
            if (App.Session.UndoHistory.Count != undoPre + 1 || App.Session.UndoHistory[^1].Label != "Secondary motion") return "history: " + App.Session.UndoHistory[^1].Label;
            return dlg.ResultText.Contains("rotation keys") ? null : "result text: " + dlg.ResultText;
        });
        Shot(P("p6-en-dark-secondary-result"), dlg);
        dlg.Hide();
        await Settle();
        Key(Godot.Key.Z);
        await Settle();
        Check("p6 Ctrl+Z removes the secondary motion exactly", () => ClipJson(clip) == jsonPre ? null : "clip differs after undo");

        App.SetLanguage("ar");
        await Settle();
        panel.SecondaryButton.EmitSignal(BaseButton.SignalName.Pressed);
        await Settle();
        Shot(P("p6-ar-dark-secondary-dialog"), dlg);
        dlg.Preset.Select(2); // arms
        dlg.Preset.EmitSignal(OptionButton.SignalName.ItemSelected, 2);
        dlg.PressApply();
        await Settle();
        Check("p6 Arabic: the arms preset keys both arms and the result text is Arabic", () =>
        {
            if (dlg.LastResult is not { Ok: true }) return "failed: " + (dlg.LastError ?? dlg.LastResult?.Error?.Message);
            var arms = new[] { H.UpperArm(Blazma.Rigging.Side.Left), H.UpperArm(Blazma.Rigging.Side.Right), H.LowerArm(Blazma.Rigging.Side.Left), H.LowerArm(Blazma.Rigging.Side.Right) };
            return KeyCount(clip, arms) >= 4 * 41 && HasArabic(dlg.ResultText) && dlg.ResultText.Contains('\u2066') ? null : "text: " + dlg.ResultText;
        });
        Shot(P("p6-ar-dark-secondary-result"), dlg);
        dlg.Hide();
        await Settle();
        Key(Godot.Key.Z);
        await Settle();
        Check("p6 Arabic: Ctrl+Z removes it again", () => ClipJson(clip) == jsonPre ? null : "clip differs after undo");
        Key(Godot.Key.Z, shift: true);
        await Settle();
        Check("p6 Ctrl+Shift+Z brings it back; the analysis was refreshed (no stale cache)", () =>
            ClipJson(clip) != jsonPre && App.Physics.Report!.Frames.Count == 41 ? null : "redo state wrong");
        Key(Godot.Key.Z);
        await Settle();
        Check("p6 and undone once more, the clip is the balanced one again", () => ClipJson(clip) == jsonPre ? null : "clip differs");

        // ---- the working pose is analysed as soon as it is edited, airborne is grey ----
        App.SetLanguage("en");
        await Settle();
        App.GoToFrame(0);
        await Settle();
        Check("p6 editing the pose switches the panel to the working pose; lifting the hips with free feet is airborne", () =>
        {
            var n = App.Physics.PoseComputations;
            if (!(Cmd("pose.set_pinned", new { characterId = mid, control = "ik_foot_l", pinned = false }) && Cmd("pose.set_pinned", new { characterId = mid, control = "ik_foot_r", pinned = false })
                && Cmd("pose.set_bone", new { characterId = mid, bone = H.Pelvis, rx = 0, ry = 0, rz = 0, tx = 0, ty = 1.6, tz = 0 }))) return "pose edit failed";
            if (!App.PoseDirty) return "pose not dirty";
            if (App.Physics.CurrentFromClip) return "still reading the clip";
            var state = App.Physics.Current?.State;
            return App.Physics.PoseComputations > n && state == BalanceState.Airborne ? null : $"state {state}, analyses {n} -> {App.Physics.PoseComputations}";
        });
        await Settle();
        Check("p6 the airborne frame is drawn grey and the panel says so", () =>
            _w.PhysicsView.DrawnState == BalanceState.Airborne && panel.StateText == "Airborne" && panel.MarginText == "—" && panel.MessageText == "" ? null
                : $"overlay {_w.PhysicsView.DrawnState} panel {panel.StateText} margin {panel.MarginText}");
        _w.ToastNode.HideNow();
        await Frames(2);
        Shot(P("p6-en-dark-airborne"), GetViewport());
        App.GoToFrame(range.WorstFrame); // shows the clip again (the unkeyed pose edit is dropped like on any frame change)
        await Settle();

        // ---- a light theme look and the toggles render without errors ----
        App.SetTheme("light");
        App.SetAdvancedMode(false);
        await Settle();
        Shot(P("p6-en-light-balanced"), GetViewport());
        var bad = new List<string>();
        foreach (var lang in new[] { "en", "ar" })
            foreach (var theme in new[] { "dark", "light" })
            {
                App.SetLanguage(lang); App.SetTheme(theme);
                foreach (var (a, b, c) in new[] { (true, true, true), (true, false, false), (false, true, true), (false, false, false) })
                {
                    App.SetShowPhysics(a); App.SetShowPhysicsPaths(b); App.SetPhysicsPanel(c);
                    App.GoToFrame(range.WorstFrame);
                    await Frames(3);
                    var v = _w.PhysicsView;
                    var tag = $"{lang}/{theme}/{(a ? "o" : "-")}{(b ? "p" : "-")}{(c ? "n" : "-")}";
                    if (a != (v.DrawnState is not null)) bad.Add(tag + " overlay");
                    if (b != (v.DrawnPathPoints > 0)) bad.Add(tag + " paths");
                    if (c != panel.Visible || a != strip.Visible) bad.Add(tag + " panel/strip");
                }
            }
        Check("p6 every toggle combination renders (overlay, paths, panel), in both languages and themes", () => bad.Count == 0 ? null : string.Join("; ", bad));
        App.SetLanguage("en"); App.SetTheme("dark");
        await Settle();
        Check("p6 with the toggles off nothing is drawn and the strip is hidden again", () =>
        {
            App.SetShowPhysics(false); App.SetShowPhysicsPaths(false); App.SetPhysicsPanel(false);
            return _w.PhysicsView.DrawnState is null && _w.PhysicsView.DrawnPathPoints == 0 && !strip.Visible && !panel.Visible ? null : "still visible";
        });
        await Settle();

        // ---- no clip, no feet: friendly messages instead of errors ----
        Check("p6 a character without a keyed clip still gets a working-pose reading; the panel never shows an error", () =>
        {
            _w.NewProjectNow();
            App.SetPhysicsPanel(true);
            return null;
        });
        await Settle();
        Check("p6 with no clip the clip summary asks for keys and Auto-balance is disabled", () =>
            panel.Visible && panel.AutoBalanceButton.Disabled && panel.CountsText == "–/–/–" && panel.StateText == "Balanced" ? null : $"{panel.CountsText} {panel.StateText} disabled={panel.AutoBalanceButton.Disabled}");

        App.SetShowPhysics(saved.ShowPhysics); App.SetShowPhysicsPaths(saved.ShowPhysicsPaths); App.SetPhysicsPanel(saved.PhysicsPanel);
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        await Settle();
    }
}
