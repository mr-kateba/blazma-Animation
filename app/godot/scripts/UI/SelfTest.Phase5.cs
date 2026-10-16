using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Agent;
using Blazma.Core.Persistence;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 5 scenario: the app hosts the agent API on a private pipe; a real <see cref="AgentIpcClient"/> inside this process plays the
/// agent. Everything the user decides (Approve, Reject, Revert, Deny, mode, switch) is pressed on the real AI Workspace buttons.
/// </summary>
public partial class SelfTest
{
    AgentIpcClient? _ac;
    string _asid = "";

    static JsonObject Jo(object o) => JsonNode.Parse(JsonSerializer.Serialize(o))!.AsObject();

    async Task<T> Wait<T>(Task<T> t, int ms = 10000)
    {
        var until = Time.GetTicksMsec() + (ulong)ms;
        while (!t.IsCompleted)
        {
            await Frames(1);
            if (Time.GetTicksMsec() > until) throw new TimeoutException("background task did not finish");
        }
        return t.GetAwaiter().GetResult();
    }

    Task<JsonNode?> Rpc(string method, JsonObject? p = null) => Wait(_ac!.CallAsync(method, p));

    async Task<JsonNode> Tool(string name, object args) =>
        (await Rpc("tools.call", new JsonObject { ["sessionId"] = _asid, ["name"] = name, ["arguments"] = Jo(args) }))!;

    async Task<bool> AgentConnect(string client)
    {
        _ac = await Wait(AgentIpcClient.TryConnectAsync(TimeSpan.FromSeconds(5), App.AgentPipeName));
        if (_ac is null) return false;
        var hello = await Rpc("session.hello", new JsonObject { ["client"] = client });
        _asid = (string)hello!["sessionId"]!;
        return true;
    }

    sealed record SnapT(int Clips, int Undo, string Name, string PoseJson);

    static SnapT Snap() => new(Proj.Clips.Count, App.Session.UndoHistory.Count, Proj.Name,
        JsonSerializer.Serialize(Proj.Characters[0].Pose, ProjectArchive.JsonOptions));

    /// <summary>Same document? Poses compare by the bone positions they produce (an empty pose and an explicit rest pose are the same pose).</summary>
    bool Same(SnapT a)
    {
        var b = Snap();
        if (a.Clips != b.Clips || a.Undo != b.Undo || a.Name != b.Name) return false;
        var pa = JsonSerializer.Deserialize<Blazma.Core.Model.Pose>(a.PoseJson, ProjectArchive.JsonOptions)!;
        var pb = JsonSerializer.Deserialize<Blazma.Core.Model.Pose>(b.PoseJson, ProjectArchive.JsonOptions)!;
        return PoseDistance(RigOf(Ch()), pa, pb) < 1e-9;
    }

    /// <summary>A realistic agent task: create a clip, key the pose, raise an arm, key again (plus one call that fails on purpose).</summary>
    async Task<(string task, string clip)> AgentWave(string title, string clipName)
    {
        var insp = await Tool("project.inspect", new { });
        var ch = (string)insp["data"]!["characters"]![0]!["characterId"]!;
        await Rpc("task.begin", new JsonObject { ["sessionId"] = _asid, ["title"] = title });
        var clip = (string)(await Tool("clip.create", new { name = clipName, durationFrames = 30 }))["data"]!["clipId"]!;
        await Tool("key.set", new { clipId = clip, characterId = ch, frame = 0 });
        await Tool("pose.set_bone", new { characterId = ch, bone = "upper_arm_r", rx = 0, ry = 0, rz = 80 });
        await Tool("key.set", new { clipId = clip, characterId = ch, frame = 15 });
        await Tool("clip.rename", new { clipId = "01ARZ3NDEKTSV4RRFFQ69G5FAV", name = "nope" }); // fails: not_found
        var fin = await Rpc("task.finish", new JsonObject { ["sessionId"] = _asid, ["summary"] = "Raised the right arm in " + clipName });
        return ((string)fin!["taskId"]!, clip);
    }

    void PressAgent(string key) => _w.Agent.Buttons[key].EmitSignal(BaseButton.SignalName.Pressed);

    AgentTask? TaskById(string id) => App.Agents.Tasks.FirstOrDefault(t => t.Id == id);

    void ShotRegion(string path, Rect2I region)
    {
        var img = GetViewport().GetTexture().GetImage().GetRegion(region);
        var err = img.SavePng(path);
        if (err != Error.Ok) Fail("screenshot " + System.IO.Path.GetFileName(path), err.ToString());
        else Pass("screenshot " + System.IO.Path.GetFileName(path) + $" ({img.GetWidth()}x{img.GetHeight()})");
    }

    async Task RunPhase5()
    {
        var s = App.Settings;
        var saved = (s.AllowAgents, s.AgentAutonomous, s.AgentReadRoots.ToList());
        App.SetLanguage("en"); App.SetTheme("dark"); App.SetAdvancedMode(false);
        App.SetAgentReadRoots(Array.Empty<string>());
        App.SetAgentAutonomous(false);
        App.SetAllowAgents(true);
        _w.ShowAgentPanel(false);
        _w.NewProjectNow();
        await Settle();

        // ---- the server, the switch and the first connection ------------------------------------------------------
        Check("p5 server listens on a private pipe, not on the user's", () =>
            App.AgentServerRunning && App.AgentPipeName.StartsWith("blazma-selftest-") ? null : $"running={App.AgentServerRunning} pipe={App.AgentPipeName}");

        _w.ShowAgentPanel(true);
        await Settle();
        App.SetAllowAgents(false);
        await Settle();
        var refused = await Wait(AgentIpcClient.TryConnectAsync(TimeSpan.FromMilliseconds(400), App.AgentPipeName));
        Check("p5 'Agents allowed' off stops the server (no connection possible)", () =>
            refused is null && !App.AgentServerRunning && !_w.Agent.AllowSwitch.ButtonPressed ? null : $"reachable={refused is not null} running={App.AgentServerRunning} switch={_w.Agent.AllowSwitch.ButtonPressed}");
        if (refused is not null) await refused.DisposeAsync();

        _w.Agent.AllowSwitch.ButtonPressed = true; // the real switch (Toggled -> SetAllowAgents)
        await Settle();
        Check("p5 switching 'Agents allowed' on starts the server again", () => App.AgentServerRunning && s.AllowAgents ? null : "server not running");

        _w.ShowAgentPanel(false);
        await Settle();
        var connected = await AgentConnect("selftest-agent");
        await Settle();
        Check("p5 session.hello over the real pipe; the panel opens by itself and lists the agent", () =>
        {
            if (!connected) return "could not connect";
            if (App.Agents.Sessions.Count != 1) return "sessions=" + App.Agents.Sessions.Count;
            if (!_w.Agent.Visible) return "panel did not auto-open";
            if (!_w.Agent.AgentRows.Contains("selftest-agent")) return "rows: " + string.Join(",", _w.Agent.AgentRows);
            if (!_w.StatusAiText.Contains('1')) return "status text: " + _w.StatusAiText;
            return _w.StatusAiColor.IsEqualApprox(_w.Palette.Ok) ? null : "status dot is not green";
        });
        Check("p5 help is folded away while an agent is connected", () => _w.Agent.Buttons.ContainsKey("copy") ? "help is open" : null);
        PressAgent("help");
        await Settle();
        Check("p5 connect command 'claude mcp add blazma -- ...' is shown with a Copy button", () =>
            _w.Agent.CommandText.StartsWith("claude mcp add blazma -- ") && _w.Agent.Buttons.ContainsKey("copy") ? null : _w.Agent.CommandText);
        PressAgent("help");
        await Settle();

        // ---- A: a task is reviewed and REJECTED -> the project is exactly as before ---------------------------------
        var before = Snap();
        var (tA, _) = await AgentWave("Raise the arm", "Wave");
        await Settle();
        Check("p5 task A finishes in Review and the panel shows it with Approve / Reject", () =>
        {
            var t = TaskById(tA);
            if (t is null || t.State != AgentTaskState.Review) return "state " + t?.State;
            if (Same(before)) return "the agent changed nothing";
            if (!_w.Agent.TaskStateText.TryGetValue(tA, out var st) || st != "Waiting for review") return "chip: " + st;
            return _w.Agent.Buttons.ContainsKey($"task:{tA}:approve") && _w.Agent.Buttons.ContainsKey($"task:{tA}:reject") ? null : "buttons missing";
        });
        Check("p5 status bar indicator is amber while a task waits", () =>
            _w.StatusAiColor.IsEqualApprox(_w.Palette.Warn) ? null : "colour " + _w.StatusAiColor);
        PressAgent($"task:{tA}:calls");
        await Settle();
        Check("p5 expanding a task lists its tool calls (success and failure)", () =>
        {
            var t = TaskById(tA)!;
            if (_w.Agent.CallRowCount != t.Calls.Count || t.Calls.Count < 5) return $"rows={_w.Agent.CallRowCount} calls={t.Calls.Count}";
            return t.Calls.Any(c => !c.Ok && c.ErrorCode == "not_found") ? null : "failed call not recorded";
        });
        Shot(System.IO.Path.Combine(_dir, "p5-en-dark-review.png"), GetViewport());
        ShotRegion(System.IO.Path.Combine(_dir, "p5-en-dark-statusbar-amber.png"), new Rect2I(0, (int)GetViewport().GetVisibleRect().Size.Y - 34, (int)GetViewport().GetVisibleRect().Size.X, 34));

        PressAgent($"task:{tA}:reject");
        await Settle();
        Check("p5 Reject brings the project back to exactly how it was", () =>
            Same(before) && TaskById(tA)!.State == AgentTaskState.Rejected && _w.Agent.TaskStateText[tA] == "Rejected" ? null : $"state={TaskById(tA)!.State} before={before.Clips}/{before.Undo} after={Snap().Clips}/{Snap().Undo}");

        // ---- B: APPROVE keeps the work; Revert takes an approved task back --------------------------------------------
        var (tB, _) = await AgentWave("Raise the arm again", "Wave");
        await Settle();
        PressAgent($"task:{tB}:approve");
        await Settle();
        Check("p5 Approve keeps the changes", () =>
            TaskById(tB)!.State == AgentTaskState.Approved && Proj.Clips.Count == 1 && _w.Agent.Buttons.ContainsKey($"task:{tB}:revert") ? null : "state " + TaskById(tB)!.State);
        Check("p5 status bar goes back to green when nothing waits", () => _w.StatusAiColor.IsEqualApprox(_w.Palette.Ok) ? null : "colour " + _w.StatusAiColor);
        PressAgent($"task:{tB}:revert");
        await Settle();
        Check("p5 Revert on an approved task undoes it", () =>
            Same(before) && TaskById(tB)!.State == AgentTaskState.Rejected ? null : $"same={Same(before)}");

        // ---- C: Destructive call waits for the user; Approve runs it ------------------------------------------------------
        var (tC, clipC) = await AgentWave("Make a clip", "Doomed");
        await Settle();
        PressAgent($"task:{tC}:approve");
        await Rpc("task.begin", new JsonObject { ["sessionId"] = _asid, ["title"] = "Clean up" });
        var del = await Tool("clip.delete", new { clipId = clipC });
        await Settle();
        Check("p5 clip.delete is held: nothing deleted, an approval card with Approve / Deny appears", () =>
        {
            if ((string?)del["error"]?["code"] != "approval_required") return del.ToJsonString();
            if (Proj.Clips.All(c => c.Id.ToString() != clipC)) return "clip was deleted without approval";
            var p = App.Agents.PendingApprovals.SingleOrDefault();
            if (p is null) return "no pending approval";
            if (!_w.Agent.PendingTools.Contains("clip.delete")) return "card missing";
            if (!_w.StatusAiColor.IsEqualApprox(_w.Palette.Warn)) return "status dot not amber";
            return _w.Agent.Buttons.ContainsKey($"pending:{p.Id}:approve") && _w.Agent.Buttons.ContainsKey($"pending:{p.Id}:deny") ? null : "buttons missing";
        });
        Shot(System.IO.Path.Combine(_dir, "p5-en-dark-pending.png"), GetViewport());
        PressAgent($"pending:{App.Agents.PendingApprovals[0].Id}:approve");
        await Settle();
        Check("p5 Approve on the card deletes the clip", () =>
            Proj.Clips.All(c => c.Id.ToString() != clipC) && App.Agents.PendingApprovals.Count == 0 ? null : "clip still there or card still pending");
        await Rpc("task.finish", new JsonObject { ["sessionId"] = _asid, ["summary"] = "Deleted the clip" });
        await Settle();
        PressAgent($"task:{App.Agents.Tasks[^1].Id}:approve");

        // ---- D: Deny leaves everything alone ---------------------------------------------------------------------------------
        var (tD, clipD) = await AgentWave("Make another clip", "Keeper");
        PressAgent($"task:{tD}:approve");
        await Rpc("task.begin", new JsonObject { ["sessionId"] = _asid, ["title"] = "Try to delete" });
        await Tool("clip.delete", new { clipId = clipD });
        await Settle();
        PressAgent($"pending:{App.Agents.PendingApprovals[0].Id}:deny");
        await Settle();
        Check("p5 Deny keeps the clip, removes the card and logs the refusal", () =>
        {
            if (Proj.Clips.All(c => c.Id.ToString() != clipD)) return "clip deleted";
            if (App.Agents.PendingApprovals.Count != 0) return "still pending";
            return App.Agents.Tasks[^1].Calls.Any(c => c.ErrorCode == "denied") ? null : "denial not logged";
        });
        await Rpc("task.finish", new JsonObject { ["sessionId"] = _asid, ["summary"] = null });

        // ---- E: Autonomous mode ----------------------------------------------------------------------------------------------------
        _w.Agent.ModeSwitch[1].EmitSignal(BaseButton.SignalName.Pressed);
        await Settle();
        var (tE, _) = await AgentWave("Autonomous work", "Auto clip");
        await Settle();
        Check("p5 Autonomous: finishing a task gives Approved straight away (and the choice is saved)", () =>
            App.Agents.Mode == AgentMode.Autonomous && s.AgentAutonomous && TaskById(tE)!.State == AgentTaskState.Approved
            && _w.Agent.TaskStateText[tE] == "Approved" ? null : $"mode={App.Agents.Mode} state={TaskById(tE)!.State}");
        _w.Agent.ModeSwitch[0].EmitSignal(BaseButton.SignalName.Pressed);
        await Settle();
        Check("p5 switching back to Assisted", () => App.Agents.Mode == AgentMode.Assisted && !s.AgentAutonomous ? null : "mode " + App.Agents.Mode);

        // ---- F: the user edited after the agent -> Reject must not undo the user's work -----------------------------------
        var (tF, clipF) = await AgentWave("Another change", "Newer");
        await Settle();
        App.Execute("project.rename", new JsonObject { ["name"] = "User renamed" });
        var snapUser = Snap();
        PressAgent($"task:{tF}:reject");
        await Settle();
        Check("p5 Reject after a user edit shows 'newer changes' and undoes nothing", () =>
        {
            if (!Same(snapUser)) return "something was undone";
            if (TaskById(tF)!.State != AgentTaskState.Review) return "state " + TaskById(tF)!.State;
            var note = _w.Agent.NoteFor(tF);
            return note is not null && note.Contains("newer changes") ? null : "note: " + note;
        });
        Shot(System.IO.Path.Combine(_dir, "p5-en-dark-newer-changes.png"), GetViewport());
        App.Undo(); // the user's own undo makes the agent's task the latest again
        PressAgent($"task:{tF}:reject");
        await Settle();
        Check("p5 after the user undoes their edit, Reject works", () =>
            Proj.Clips.All(c => c.Id.ToString() != clipF) && TaskById(tF)!.State == AgentTaskState.Rejected ? null : "not rejected");

        // ---- G: project replaced while a task waits -------------------------------------------------------------------------------
        var (tG, _) = await AgentWave("Will be orphaned", "Orphan");
        _w.NewProjectNow();
        await Settle();
        Check("p5 New project: agent stays connected, the old task can no longer be rejected (graceful)", () =>
        {
            if (App.Agents.Sessions.Count != 1) return "agent was dropped";
            if (!App.IsAgentTaskStale(tG)) return "task not marked";
            if (!_w.Agent.Buttons[$"task:{tG}:reject"].Disabled) return "Reject button still enabled";
            var r = App.AgentReject(tG);
            return !r.Ok && r.Error!.Code == "project_replaced" && TaskById(tG)!.State == AgentTaskState.Review ? null : "reject result " + r.Ok;
        });
        var stillWorks = await Tool("project.inspect", new { });
        Check("p5 the agent keeps working on the new project", () => (bool)stillWorks["ok"]! ? null : stillWorks.ToJsonString());
        PressAgent($"task:{tG}:approve");

        // ---- H: saving through the API ------------------------------------------------------------------------------------------
        string? e1 = null, e2 = null;
        try { await Rpc("project.save", new JsonObject { ["sessionId"] = _asid }); } catch (AgentRpcException e) { e1 = e.Code; }
        var target = System.IO.Path.Combine(_dir, "agent-saved.blazanim");
        try { await Rpc("project.save", new JsonObject { ["sessionId"] = _asid, ["path"] = target }); } catch (AgentRpcException e) { e2 = e.Code; }
        Check("p5 project.save: no path on an unsaved project -> no_path; a path outside allowed folders -> path_not_allowed", () =>
            e1 == "no_path" && e2 == "path_not_allowed" && !System.IO.File.Exists(target) ? null : $"{e1} / {e2}");
        App.SetAgentReadRoots(new[] { _dir });
        var saveRes = await Rpc("project.save", new JsonObject { ["sessionId"] = _asid, ["path"] = target });
        Check("p5 project.save into an allowed folder writes the file through the app's Save logic", () =>
            System.IO.File.Exists(target) && App.FilePath == target && !App.IsDirty && (string)saveRes!["path"]! == target ? null : "not saved");
        _w.NewProjectNow();
        App.SetAgentReadRoots(Array.Empty<string>());

        // ---- the Post queue is safe to feed from many threads ---------------------------------------------------------------------
        var counter = 0;
        var feeders = Enumerable.Range(0, 4).Select(_ => Task.Run(() => { for (var i = 0; i < 500; i++) App.Post(() => counter++); })).ToArray();
        await Wait(Task.WhenAll(feeders).ContinueWith(_ => true));
        for (var i = 0; i < 120 && counter < 2000; i++) await Frames(1);
        Check("p5 AppController.Post from four threads runs every action exactly once on the main thread", () => counter == 2000 ? null : "ran " + counter);

        // ---- showcase screenshots: Arabic, light ---------------------------------------------------------------------------------------
        var (tS, clipS) = await AgentWave("Raise the arm", "Wave");
        PressAgent($"task:{tS}:calls");
        await Rpc("task.begin", new JsonObject { ["sessionId"] = _asid, ["title"] = "Clean up" });
        await Tool("clip.delete", new { clipId = clipS });
        await Settle();
        App.SetLanguage("ar"); await Settle();
        Check("p5 Arabic: panel is mirrored (dock on the left), tool names stay left-to-right", () =>
        {
            if (!_w.Agent.IsLayoutRtl()) return "panel not RTL";
            return _w.Agent.GetGlobalRect().Position.X < _w.GetGlobalRect().Size.X / 2 ? null : "panel is on the right in RTL";
        });
        Shot(System.IO.Path.Combine(_dir, "p5-ar-dark.png"), GetViewport());
        App.SetLanguage("en"); App.SetTheme("light"); await Settle();
        Shot(System.IO.Path.Combine(_dir, "p5-en-light.png"), GetViewport());
        App.SetLanguage("ar"); await Settle();
        Shot(System.IO.Path.Combine(_dir, "p5-ar-light.png"), GetViewport());
        App.SetLanguage("en"); App.SetTheme("dark"); await Settle();
        PressAgent($"pending:{App.Agents.PendingApprovals[0].Id}:deny");
        await Rpc("task.finish", new JsonObject { ["sessionId"] = _asid, ["summary"] = null });
        await Settle();

        // ---- disconnect --------------------------------------------------------------------------------------------------------------------------
        await _ac!.DisposeAsync();
        _ac = null;
        for (var i = 0; i < 300 && App.Agents.Sessions.Count > 0; i++) await Frames(1);
        await Settle();
        Check("p5 disconnect: the agent disappears from the panel and the status bar", () =>
            App.Agents.Sessions.Count == 0 && _w.Agent.AgentRows.Count == 0 && _w.StatusAiText == "AI" ? null
                : $"sessions={App.Agents.Sessions.Count} rows={_w.Agent.AgentRows.Count} text={_w.StatusAiText}");
        Shot(System.IO.Path.Combine(_dir, "p5-en-dark-disconnected.png"), GetViewport());

        // ---- restore ---------------------------------------------------------------------------------------------------------------------------------
        _w.ShowAgentPanel(false);
        App.SetAgentReadRoots(saved.Item3);
        App.SetAgentAutonomous(saved.AgentAutonomous);
        App.SetAllowAgents(saved.AllowAgents);
        await Settle();
    }
}
