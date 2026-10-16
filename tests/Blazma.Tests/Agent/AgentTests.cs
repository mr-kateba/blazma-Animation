using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Agent;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Rigging;
using Blazma.Tests.Persistence;

namespace Blazma.Tests.Agent;

sealed class FakeServices(ProjectSession s) : IAgentHostServices
{
    public string ProjectName => s.Project.Name;
    public string? ProjectPath => s.FilePath;
    public bool IsDirty => s.IsDirty;
    public string Save(string? path)
    {
        var p = path ?? s.FilePath ?? throw new AgentRpcException("no_path", "The project has not been saved yet; pass 'path'.");
        ProjectFile.Save(s.Project, p);
        s.MarkSaved(p);
        return p;
    }
}

public class AgentHostTests
{
    static readonly CommandRegistry Registry = CommandRegistry.CreateDefault();

    static (ProjectSession s, AgentHost host, string sid) Setup(bool userPresent = true, AgentMode mode = AgentMode.Assisted)
    {
        var project = Project.CreateNew("Agent");
        MannequinFactory.AddTo(project);
        var s = new ProjectSession(project);
        var host = new AgentHost(Registry, () => s, userPresent) { Mode = mode };
        return (s, host, host.Connect("test-agent").Id);
    }

    static JsonObject A(object o) => JsonNode.Parse(JsonSerializer.Serialize(o))!.AsObject();

    [Fact]
    public void MutatingCalls_GroupIntoATask_ThatGoesToReview_AndApproveKeepsChanges()
    {
        var (s, host, sid) = Setup();
        var ch = s.Project.Characters[0].Id.ToString();
        host.BeginTask(sid, "Raise arm");
        Assert.True(host.Call(sid, "pose.set_bone", A(new { characterId = ch, bone = "upper_arm_l", rx = 0, ry = 0, rz = 60 })).Ok);
        Assert.True(host.Call(sid, "clip.create", A(new { name = "Agent clip" })).Ok);
        var t = host.FinishTask(sid, "Arm raised")!;
        Assert.Equal(AgentTaskState.Review, t.State);
        Assert.Equal(2, t.Transactions.Count);
        Assert.Equal(2, t.Calls.Count);

        host.Approve(t.Id);
        Assert.Equal(AgentTaskState.Approved, t.State);
        Assert.Single(s.Project.Clips);
    }

    [Fact]
    public void Reject_RevertsTheWholeTask()
    {
        var (s, host, sid) = Setup();
        var ch = s.Project.Characters[0].Id.ToString();
        var before = JsonSerializer.Serialize(s.Project.Characters[0].Pose, ProjectArchive.JsonOptions);
        host.Call(sid, "pose.set_bone", A(new { characterId = ch, bone = "head", rx = 30, ry = 0, rz = 0 }));
        host.Call(sid, "clip.create", A(new { name = "X" }));
        var t = host.FinishTask(sid, null)!;
        Assert.True(host.Reject(t.Id).Ok);
        Assert.Empty(s.Project.Clips);
        Assert.Equal(before, JsonSerializer.Serialize(s.Project.Characters[0].Pose, ProjectArchive.JsonOptions));
        Assert.Equal(AgentTaskState.Rejected, t.State);
    }

    [Fact]
    public void Reject_RefusesToUndoTheUsersLaterWork()
    {
        var (s, host, sid) = Setup();
        host.Call(sid, "clip.create", A(new { name = "Agent" }));
        var t = host.FinishTask(sid, null)!;
        s.Execute(Registry.Create("project.rename", A(new { name = "User renamed" })), CommandOrigin.Ui);
        Assert.Equal("edited_after", host.Reject(t.Id).Error!.Code);
        Assert.Equal("User renamed", s.Project.Name);
        Assert.Single(s.Project.Clips);
    }

    [Fact]
    public void Destructive_WaitsForUserApproval_WhenUserPresent()
    {
        var (s, host, sid) = Setup();
        var clip = (string)host.Call(sid, "clip.create", A(new { name = "Doomed" })).Data!["clipId"]!;
        var r = host.Call(sid, "clip.delete", A(new { clipId = clip }));
        Assert.Equal("approval_required", r.Error!.Code);
        Assert.Single(s.Project.Clips);
        var pending = Assert.Single(host.PendingApprovals);

        Assert.True(host.ApprovePending(pending.Id).Ok);
        Assert.Empty(s.Project.Clips);
        Assert.Empty(host.PendingApprovals);
    }

    [Fact]
    public void DeniedDestructive_DoesNothing()
    {
        var (s, host, sid) = Setup();
        var clip = (string)host.Call(sid, "clip.create", A(new { name = "Keep" })).Data!["clipId"]!;
        host.Call(sid, "clip.delete", A(new { clipId = clip }));
        host.DenyPending(host.PendingApprovals[0].Id);
        Assert.Single(s.Project.Clips);
    }

    [Fact]
    public void Headless_AllowsDestructive_AndAutoApprovesTasks()
    {
        var (s, host, sid) = Setup(userPresent: false);
        var clip = (string)host.Call(sid, "clip.create", A(new { name = "X" })).Data!["clipId"]!;
        Assert.True(host.Call(sid, "clip.delete", A(new { clipId = clip })).Ok);
        Assert.Equal(AgentTaskState.Approved, host.FinishTask(sid, null)!.State);
    }

    [Fact]
    public void Autonomous_TasksAreApprovedWhenFinished_ButStillRejectable()
    {
        var (s, host, sid) = Setup(mode: AgentMode.Autonomous);
        host.Call(sid, "clip.create", A(new { name = "Auto" }));
        var t = host.FinishTask(sid, null)!;
        Assert.Equal(AgentTaskState.Approved, t.State);
        Assert.True(host.Reject(t.Id).Ok);
        Assert.Empty(s.Project.Clips);
    }

    [Fact]
    public void External_ImportOutsideAllowedFolders_NeedsApproval()
    {
        var (s, host, sid) = Setup();
        var model = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "models", "khronos", "RiggedSimple.glb"));
        Assert.Equal("approval_required", host.Call(sid, "rig.import", A(new { path = model })).Error!.Code);

        host.AllowedReadRoots.Add(Path.GetDirectoryName(model)!);
        Assert.True(host.Call(sid, "rig.import", A(new { path = model })).Ok);
        // Path tricks do not escape the root.
        var sneaky = Path.Combine(Path.GetDirectoryName(model)!, "..", "..", "..", "README.md");
        Assert.Equal("approval_required", host.Call(sid, "rig.import", A(new { path = sneaky })).Error!.Code);
    }

    [Fact]
    public void Agent_CannotUndoUserWork_ButCanUndoItsOwn()
    {
        var (s, host, sid) = Setup();
        var mine = host.Call(sid, "clip.create", A(new { name = "Mine" })).TransactionId!;
        var userTx = s.Execute(Registry.Create("project.rename", A(new { name = "User" })), CommandOrigin.Ui).TransactionId!;
        Assert.Equal("not_owner", host.Undo(sid, userTx).Error!.Code); // latest, but the user's: never
        Assert.Equal("not_latest", host.Undo(sid, mine).Error!.Code);
        s.Undo(CommandOrigin.Ui);
        Assert.True(host.Undo(sid, mine).Ok);
        Assert.Empty(s.Project.Clips);
    }

    [Fact]
    public void ReadOnlyCalls_DoNotCreateTasks()
    {
        var (_, host, sid) = Setup();
        Assert.True(host.Call(sid, "project.inspect", null).Ok);
        Assert.Empty(host.Tasks);
        Assert.Single(host.ReadOnlyLog);
    }

    [Fact]
    public void Disconnect_FinishesTask_AndDropsPendingApprovals()
    {
        var (_, host, sid) = Setup();
        var clip = (string)host.Call(sid, "clip.create", A(new { name = "X" })).Data!["clipId"]!;
        host.Call(sid, "clip.delete", A(new { clipId = clip }));
        host.Disconnect(sid);
        Assert.Empty(host.PendingApprovals);
        Assert.Equal(AgentTaskState.Review, host.Tasks[0].State);
        Assert.Throws<InvalidOperationException>(() => host.Call(sid, "project.inspect", null));
    }
}

public class AgentRpcTests
{
    static (AgentRpcHandler rpc, ProjectSession s) Setup()
    {
        var project = Project.CreateNew("Rpc");
        MannequinFactory.AddTo(project);
        var s = new ProjectSession(project);
        var reg = CommandRegistry.CreateDefault();
        var host = new AgentHost(reg, () => s, userPresent: false);
        return (new AgentRpcHandler(host, reg, new FakeServices(s)), s);
    }

    static JsonObject Req(string method, object? p = null, int id = 1) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method,
        ["params"] = p is null ? new JsonObject() : JsonNode.Parse(JsonSerializer.Serialize(p)),
    };

    [Fact]
    public void FullAgentFlow_ViaRpc_ProducesAnAnimatedClip_AndSavesIt()
    {
        using var dir = new TempDir();
        var (rpc, s) = Setup();
        var hello = rpc.Handle(Req("session.hello", new { client = "claude-code" }))["result"]!;
        var sid = (string)hello["sessionId"]!;
        Assert.Equal(AgentRpcHandler.ProtocolVersion, (string)hello["protocol"]!);

        var tools = rpc.Handle(Req("tools.list"))["result"]!["tools"]!.AsArray();
        Assert.Contains(tools, t => (string)t!["name"]! == "key.set");

        JsonNode Call(string name, object args)
        {
            var r = rpc.Handle(Req("tools.call", new { sessionId = sid, name, arguments = args }))["result"]!;
            Assert.True((bool)r["ok"]!, r.ToJsonString());
            return r;
        }
        rpc.Handle(Req("task.begin", new { sessionId = sid, title = "Wave" }));
        var ch = (string)rpc.Handle(Req("tools.call", new { sessionId = sid, name = "project.inspect", arguments = new { } }))["result"]!["data"]!["characters"]![0]!["characterId"]!;
        var clip = (string)Call("clip.create", new { name = "Wave", durationFrames = 30 })["data"]!["clipId"]!;
        Call("key.set", new { clipId = clip, characterId = ch, frame = 0 });
        Call("pose.set_bone", new { characterId = ch, bone = "upper_arm_r", rx = 0, ry = 0, rz = 80 });
        Call("key.set", new { clipId = clip, characterId = ch, frame = 15 });
        var fin = rpc.Handle(Req("task.finish", new { sessionId = sid, summary = "done" }))["result"]!;
        Assert.Equal("approved", (string)fin["state"]!);

        var path = dir.File("agent.blazanim");
        var saved = rpc.Handle(Req("project.save", new { sessionId = sid, path }))["result"]!;
        Assert.Equal(path, (string)saved["path"]!);
        var loaded = ProjectFile.Load(path);
        Assert.Equal(new long[] { 0, 15 }, Blazma.Motion.ClipEvaluator.KeyFrames(loaded.Clips[0]));
    }

    [Theory]
    [InlineData("nope", "unknown_method")]
    [InlineData("tools.call", "invalid_args")]
    public void Errors_HaveStableCodes(string method, string code)
    {
        var (rpc, _) = Setup();
        var r = rpc.Handle(Req(method));
        Assert.Equal(code, (string)r["error"]!["data"]!["code"]!);
        Assert.Equal(1, (int)r["id"]!);
    }

    [Fact]
    public void FailedCommand_IsAResult_NotAnRpcError()
    {
        var (rpc, _) = Setup();
        var sid = (string)rpc.Handle(Req("session.hello"))["result"]!["sessionId"]!;
        var r = rpc.Handle(Req("tools.call", new { sessionId = sid, name = "clip.rename", arguments = new { clipId = "01ARZ3NDEKTSV4RRFFQ69G5FAV", name = "x" } }))["result"]!;
        Assert.False((bool)r["ok"]!);
        Assert.Equal("not_found", (string)r["error"]!["code"]!);
    }
}

public class AgentIpcTests
{
    [Fact]
    public async Task ClientAndServer_TalkOverANamedPipe()
    {
        var project = Project.CreateNew("Ipc");
        var s = new ProjectSession(project);
        var reg = CommandRegistry.CreateDefault();
        var host = new AgentHost(reg, () => s, userPresent: true);
        var rpc = new AgentRpcHandler(host, reg, new FakeServices(s));
        var gate = new object();
        var disconnected = new TaskCompletionSource<string>();
        var name = "blazma-test-" + Guid.NewGuid().ToString("N")[..8];
        await using var server = new AgentIpcServer(req => { lock (gate) return Task.FromResult(rpc.Handle(req)); },
            sid => { lock (gate) host.Disconnect(sid); disconnected.TrySetResult(sid); }, name);
        server.Start();

        await using (var client = await AgentIpcClient.TryConnectAsync(TimeSpan.FromSeconds(5), name))
        {
            Assert.NotNull(client);
            var sid = (string)(await client!.CallAsync("session.hello", new JsonObject { ["client"] = "test" }))!["sessionId"]!;
            var r = await client.CallAsync("tools.call", new JsonObject
            {
                ["sessionId"] = sid, ["name"] = "clip.create", ["arguments"] = new JsonObject { ["name"] = "Over pipe" },
            });
            Assert.True((bool)r!["ok"]!);
            var ex = await Assert.ThrowsAsync<AgentRpcException>(() => client.CallAsync("bogus"));
            Assert.Equal("unknown_method", ex.Code);
        }
        Assert.Equal("Over pipe", s.Project.Clips.Single().Name);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(host.Sessions);
    }

    [Fact]
    public async Task NoServer_ConnectReturnsNull()
    {
        var c = await AgentIpcClient.TryConnectAsync(TimeSpan.FromMilliseconds(200), "blazma-none-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Null(c);
    }
}
