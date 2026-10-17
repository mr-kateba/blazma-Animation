using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Agent;
using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Rigging;

namespace Blazma.Tests.Agent;

/// <summary>Starts the real blazma-anim and blazma-mcp executables as processes (see Blazma.Tests.csproj).</summary>
static class Tools
{
    static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Blazma.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Blazma.slnx not found above " + AppContext.BaseDirectory);
    }

    public static readonly string Root = RepoRoot();
    public static string Recipes => Path.Combine(Root, "docs", "recipes");

    static string Dll(string project, string name)
    {
        // Same configuration as the running tests (Debug/Release), found from the test output path.
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var path = Path.Combine(Root, "agent", project, "bin", config, "net10.0", name + ".dll");
        return File.Exists(path) ? path : throw new FileNotFoundException(path);
    }

    public static string Cli => Dll("cli", "blazma-anim");
    public static string Mcp => Dll("mcp", "blazma-mcp");

    public static ProcessStartInfo Start(string dll, IEnumerable<string> args, bool redirectInput)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = redirectInput,
            StandardOutputEncoding = Encoding.UTF8, UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public static async Task<(int Exit, string Out, string Err)> Run(string dll, params string[] args)
    {
        using var p = Process.Start(Start(dll, args, false))!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await p.WaitForExitAsync(cts.Token);
        return (p.ExitCode, await o, await e);
    }

    public static Task<(int Exit, string Out, string Err)> RunCli(params string[] args) => Run(Cli, args);

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "blazma-tools-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }
}

/// <summary>Minimal MCP client: newline-delimited JSON-RPC over the child's stdio.</summary>
sealed class McpClient : IAsyncDisposable
{
    readonly Process _p;
    readonly List<string> _stdoutLines = new();
    int _id;

    public McpClient(params string[] args)
    {
        _p = Process.Start(Tools.Start(Tools.Mcp, args, true))!;
        _p.StandardInput.NewLine = "\n";
    }

    public IReadOnlyList<string> StdoutLines => _stdoutLines;

    async Task<JsonObject> Read(int id)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            var line = await _p.StandardOutput.ReadLineAsync(cts.Token) ?? throw new EndOfStreamException("MCP server closed stdout.");
            _stdoutLines.Add(line);
            var msg = JsonNode.Parse(line)!.AsObject(); // every stdout line must be a protocol message
            if (msg["id"]?.GetValue<int>() == id && (msg["result"] is not null || msg["error"] is not null)) return msg;
        }
    }

    public async Task<JsonObject> Request(string method, JsonObject? p = null)
    {
        var id = ++_id;
        var req = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (p is not null) req["params"] = p;
        await _p.StandardInput.WriteLineAsync(req.ToJsonString());
        await _p.StandardInput.FlushAsync();
        return await Read(id);
    }

    public async Task<JsonObject> Initialize()
    {
        var r = await Request("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "test-client", ["version"] = "1.0" },
        });
        await _p.StandardInput.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToJsonString());
        await _p.StandardInput.FlushAsync();
        return r;
    }

    public async Task<List<string>> ToolNames()
    {
        var r = await Request("tools/list");
        return r["result"]!["tools"]!.AsArray().Select(t => (string)t!["name"]!).ToList();
    }

    /// <summary>Calls a tool; returns (isError, text).</summary>
    public async Task<(bool IsError, string Text)> Call(string tool, object? args = null)
    {
        var r = await Request("tools/call", new JsonObject
        {
            ["name"] = tool,
            ["arguments"] = args is null ? new JsonObject() : JsonNode.Parse(JsonSerializer.Serialize(args)),
        });
        var result = r["result"]!;
        return (result["isError"]?.GetValue<bool>() ?? false, (string)result["content"]![0]!["text"]!);
    }

    /// <summary>Calls a tool and parses its JSON text; throws if it reported an error.</summary>
    public async Task<JsonNode> Ok(string tool, object? args = null)
    {
        var (err, text) = await Call(tool, args);
        Assert.False(err, $"{tool} failed: {text}");
        return JsonNode.Parse(text)!;
    }

    public async ValueTask DisposeAsync()
    {
        try { _p.StandardInput.Close(); } catch (IOException) { }
        if (!_p.WaitForExit(10_000)) _p.Kill(true);
        _p.Dispose();
        await Task.CompletedTask;
    }

    public async Task<int> CloseAndWait()
    {
        _p.StandardInput.Close();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await _p.WaitForExitAsync(cts.Token);
        return _p.ExitCode;
    }
}

public class CliIntegrationTests
{
    [Fact]
    public async Task New_Pose_Key_Inspect_Json_And_ExitCodes()
    {
        var dir = Tools.TempDir();
        var file = Path.Combine(dir, "demo.blazanim");
        try
        {
            var created = await Tools.RunCli("new", file, "--name", "Demo");
            Assert.Equal(0, created.Exit);
            Assert.True(File.Exists(file));
            // Existing file: user error.
            Assert.Equal(1, (await Tools.RunCli("new", file)).Exit);

            var info = JsonNode.Parse((await Tools.RunCli("inspect", file, "--json")).Out)!;
            Assert.True((bool)info["ok"]!);
            var character = (string)info["data"]!["characters"]![0]!["characterId"]!;

            var clip = await Tools.RunCli("exec", file, "clip.create", "--arg", "name=Walk", "--arg", "durationFrames=24", "--json");
            Assert.Equal(0, clip.Exit);
            var clipJson = JsonNode.Parse(clip.Out)!;
            Assert.True((bool)clipJson["saved"]!);
            var clipId = (string)clipJson["data"]!["clipId"]!;

            Assert.Equal(0, (await Tools.RunCli("exec", file, "pose.set_bone", "--args",
                $$"""{"characterId":"{{character}}","bone":"head","rx":20,"ry":0,"rz":0}""")).Exit);
            Assert.Equal(0, (await Tools.RunCli("exec", file, "key.set", "--arg", $"clipId={clipId}", "--arg", $"characterId={character}", "--arg", "frame=0")).Exit);

            // pose.set_bone is only in the working pose, but it was saved with the project; the key is in the clip.
            var project = ProjectFile.Load(file);
            Assert.Equal("Demo", project.Name);
            Assert.NotEmpty(project.Clips.Single().Tracks);

            // Failing command: exit 1, JSON still printed, nothing saved.
            var bad = await Tools.RunCli("exec", file, "clip.rename", "--arg", "clipId=nope", "--arg", "name=x", "--json");
            Assert.Equal(1, bad.Exit);
            var badJson = JsonNode.Parse(bad.Out)!;
            Assert.False((bool)badJson["ok"]!);
            Assert.Equal("invalid_args", (string)badJson["error"]!["code"]!);
            Assert.Equal(1, (await Tools.RunCli("exec", file, "no.such")).Exit);
            Assert.Equal(1, (await Tools.RunCli("exec", file, "key.set", "--arg", "frame=abc")).Exit);
            Assert.Equal(1, (await Tools.RunCli("frobnicate")).Exit);
            Assert.Equal(1, (await Tools.RunCli("inspect", Path.Combine(dir, "missing.blazanim"))).Exit);

            // --no-save leaves the file alone.
            Assert.Equal(0, (await Tools.RunCli("exec", file, "project.rename", "--arg", "name=Other", "--no-save")).Exit);
            Assert.Equal("Demo", ProjectFile.Load(file).Name);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Commands_ListsTheRegistry()
    {
        var json = JsonNode.Parse((await Tools.RunCli("commands", "--json")).Out)!.AsArray();
        var registry = CommandRegistry.CreateDefault();
        Assert.Equal(registry.All.Count(), json.Count);
        Assert.Contains(json, c => (string)c!["id"]! == "key.set" && (string)c["toolName"]! == "key_set");
        Assert.Contains("key.set", (await Tools.RunCli("commands")).Out);
    }

    [Fact]
    public async Task Run_StopsAtFirstFailureWithoutSaving_AndSavesOtherwise()
    {
        var dir = Tools.TempDir();
        var file = Path.Combine(dir, "run.blazanim");
        try
        {
            await Tools.RunCli("new", file);
            var script = Path.Combine(dir, "bad.jsonl");
            File.WriteAllText(script, """
                {"command":"project.rename","args":{"name":"Changed"}}
                {"command":"clip.rename","args":{"clipId":"nope","name":"x"}}
                {"command":"project.rename","args":{"name":"Never"}}
                """);
            var bad = await Tools.RunCli("run", file, script, "--json");
            Assert.Equal(1, bad.Exit);
            var steps = JsonNode.Parse(bad.Out)!["steps"]!.AsArray();
            Assert.Equal(2, steps.Count);
            Assert.NotEqual("Changed", ProjectFile.Load(file).Name);

            var good = Path.Combine(dir, "good.jsonl");
            File.WriteAllText(good, """
                # comment
                {"command":"project.rename","args":{"name":"Changed"}}
                {"command":"clip.create","args":{"name":"A"},"save":{"c":"clipId"}}
                {"command":"clip.rename","args":{"clipId":"${c}","name":"B"}}
                """);
            Assert.Equal(0, (await Tools.RunCli("run", file, good)).Exit);
            var p = ProjectFile.Load(file);
            Assert.Equal("Changed", p.Name);
            Assert.Equal("B", p.Clips.Single().Name);

            // Unknown command in a script is caught before anything runs.
            var unknown = Path.Combine(dir, "unknown.jsonl");
            File.WriteAllText(unknown, "{\"command\":\"project.rename\",\"args\":{\"name\":\"Z\"}}\n{\"command\":\"zzz\"}\n");
            Assert.Equal(1, (await Tools.RunCli("run", file, unknown)).Exit);
            Assert.Equal("Changed", ProjectFile.Load(file).Name);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Docs_GeneratesBothReferences_FromTheRegistry()
    {
        var dir = Tools.TempDir();
        try
        {
            Assert.Equal(0, (await Tools.RunCli("docs", dir)).Exit);
            var mcp = File.ReadAllText(Path.Combine(dir, "MCP_TOOLS.md"));
            var cli = File.ReadAllText(Path.Combine(dir, "CLI_REFERENCE.md"));
            foreach (var d in CommandRegistry.CreateDefault().All)
            {
                Assert.Contains($"`{d.ToolName}`", mcp);
                Assert.Contains($"`{d.Id}`", cli);
            }
            Assert.Contains("project_new", mcp);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The scripts in docs/recipes are what docs/AGENT_RECIPES.md describes: they must really run.</summary>
    [Fact]
    public async Task Recipes_RunEndToEnd()
    {
        var dir = Tools.TempDir();
        var file = Path.Combine(dir, "recipes.blazanim");
        try
        {
            Assert.Equal(0, (await Tools.RunCli("new", file)).Exit);
            foreach (var name in new[] { "walk_cycle", "walk_cycle_repeat", "attack_light", "attack_heavier", "ik_pin_feet", "key_pose_phases", "walk_to_run_with_sword", "combat_combo" })
            {
                var r = await Tools.RunCli("run", file, Path.Combine(Tools.Recipes, name + ".jsonl"));
                Assert.True(r.Exit == 0, $"{name}: {r.Out}\n{r.Err}");
            }
            var p = ProjectFile.Load(file);
            Assert.Equal(new[] { "Walk", "Punch", "Crouch", "Phases", "Walk (template)", "Run (template)", "Walk to run", "Attack", "Hit reaction", "Attack 2", "Triple combo" },
                p.Clips.Select(c => c.Name).ToArray());
            Assert.Equal(49, p.Clips[0].DurationFrames);
            Assert.True(p.Clips[0].Loop.Enabled);
            Assert.Equal(35, p.Clips[1].DurationFrames);
            Assert.Contains(p.Clips[3].Markers, m => m.Name == "Impact (late)");
            Assert.True(p.Clips[3].Markers.Count >= 8);
            Assert.Single(p.Sequences);
            Assert.Equal("hand_r", p.Rigs[0].FindBone(p.Props.First().Attachment!.Bone)!.Name);
            // Phase 9 recipe: phases, a hitbox on the katana, typed events, hurtboxes on the defender, a combo baked with its hitboxes.
            Assert.Equal(10, p.Clips[7].Combat!.Phases!.ActiveStart.ToFrame(p.Clips[7].Fps));
            Assert.Equal(Blazma.Core.Model.EventKind.Hit, p.Clips[7].Events.Single(e => e.Name == "impact").Kind);
            Assert.NotEmpty(p.Characters[1].Combat!.Hurtboxes);
            Assert.Single(p.Combos);
            Assert.Equal(3, p.Combos[0].Attacks.Count);
            Assert.Equal(2, p.Clips[10].Combat!.Hitboxes.Count);
            Assert.Contains(p.Clips[6].Tracks, t => t.Target.Kind == Blazma.Core.Model.TargetKind.Prop);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task LockedProject_IsAUserError()
    {
        var dir = Tools.TempDir();
        var file = Path.Combine(dir, "locked.blazanim");
        try
        {
            await Tools.RunCli("new", file);
            using var held = SessionLock.TryAcquire(file)!;
            var r = await Tools.RunCli("inspect", file);
            Assert.Equal(1, r.Exit);
            Assert.Contains("project_locked", r.Err);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class McpIntegrationTests
{
    [Fact]
    public async Task Headless_FullSession_ProducesAProjectWithKeys()
    {
        var dir = Tools.TempDir();
        var file = Path.Combine(dir, "mcp.blazanim");
        try
        {
            await using var mcp = new McpClient("--headless");
            var init = await mcp.Initialize();
            Assert.Equal("blazma-mcp", (string)init["result"]!["serverInfo"]!["name"]!);
            Assert.Contains("project_info", (string)init["result"]!["instructions"]!);

            var names = await mcp.ToolNames();
            foreach (var d in CommandRegistry.CreateDefault().All) Assert.Contains(d.ToolName, names);
            foreach (var n in new[] { "key_set", "project_new", "project_open", "project_close", "project_info", "project_save", "task_begin", "task_finish", "task_status", "edit_undo" })
                Assert.Contains(n, names);

            // Without a project, edits are refused with a clear code.
            var (noProjectErr, noProjectText) = await mcp.Call("clip_create", new { name = "x" });
            Assert.True(noProjectErr);
            Assert.Contains("no_project", noProjectText);

            var created = await mcp.Ok("project_new", new { path = file, name = "Via MCP" });
            var character = (string)created["characters"]![0]!["characterId"]!;
            Assert.Equal("Via MCP", (string)(await mcp.Ok("project_info"))["name"]!);

            await mcp.Ok("task_begin", new { title = "Raise arm" });
            var clip = (string)(await mcp.Ok("clip_create", new { name = "Wave", durationFrames = 30 }))["data"]!["clipId"]!;
            await mcp.Ok("pose_set_bone", new { characterId = character, bone = "upper_arm_l", rx = 0, ry = 0, rz = 60 });
            var key = await mcp.Ok("key_set", new { clipId = clip, characterId = character, frame = 0 });
            Assert.False(string.IsNullOrEmpty((string)key["transactionId"]!));
            await mcp.Ok("pose_set_bone", new { characterId = character, bone = "upper_arm_l", rx = 0, ry = 0, rz = 80 });
            await mcp.Ok("key_set", new { clipId = clip, characterId = character, frame = 15 });
            var finished = await mcp.Ok("task_finish", new { summary = "done" });
            Assert.Equal("approved", (string)finished["state"]!); // headless: the person running the agent asked for it
            Assert.True((bool)(await mcp.Ok("project_info"))["dirty"]!);

            // Errors: isError + code, and a read-only call works.
            var (err, text) = await mcp.Call("key_set", new { clipId = "bogus", characterId = character, frame = 1 });
            Assert.True(err);
            Assert.Contains("ERROR", text);
            Assert.Contains("\"ok\": false", text);
            var (unknownErr, _) = await mcp.Call("not_a_tool");
            Assert.True(unknownErr);

            await mcp.Ok("project_save");
            Assert.False((bool)(await mcp.Ok("project_info"))["dirty"]!);

            // While open, the file is locked against other processes.
            Assert.Equal(1, (await Tools.RunCli("inspect", file)).Exit);

            await mcp.Ok("project_close");
            Assert.Equal(0, (await Tools.RunCli("inspect", file)).Exit);
            Assert.Equal(0, await mcp.CloseAndWait());

            var project = ProjectFile.Load(file);
            Assert.Equal("Via MCP", project.Name);
            var saved = project.Clips.Single();
            Assert.Equal("Wave", saved.Name);
            Assert.NotEmpty(saved.Tracks);
            Assert.All(mcp.StdoutLines, l => Assert.Equal("2.0", (string)JsonNode.Parse(l)!["jsonrpc"]!));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Headless_UnsavedChanges_AreProtected()
    {
        var dir = Tools.TempDir();
        try
        {
            await using var mcp = new McpClient("--headless");
            await mcp.Initialize();
            await mcp.Ok("project_new", new { path = Path.Combine(dir, "a.blazanim") });
            await mcp.Ok("clip_create", new { name = "dirty" });
            var (err, text) = await mcp.Call("project_new", new { path = Path.Combine(dir, "b.blazanim") });
            Assert.True(err);
            Assert.Contains("unsaved_changes", text);
            await mcp.Ok("project_new", new { path = Path.Combine(dir, "b.blazanim"), discard = true });
            var (existsErr, existsText) = await mcp.Call("project_new", new { path = Path.Combine(dir, "a.blazanim"), discard = true });
            Assert.True(existsErr);
            Assert.Contains("file_exists", existsText);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Attach_ForwardsToolCallsIntoTheRunningApp()
    {
        var project = Project.CreateNew("In the app");
        var character = MannequinFactory.AddTo(project);
        var session = new ProjectSession(project);
        var registry = CommandRegistry.CreateDefault();
        var host = new AgentHost(registry, () => session, userPresent: true);
        var rpc = new AgentRpcHandler(host, registry, new FakeServices(session));
        var gate = new object();
        var pipe = "blazma-test-" + Guid.NewGuid().ToString("N");
        await using var server = new AgentIpcServer((req, ctx) => { lock (gate) return Task.FromResult(rpc.Handle(req, ctx)); }, pipeName: pipe);
        server.Start();

        await using var mcp = new McpClient("--pipe", pipe);
        await mcp.Initialize();
        var names = await mcp.ToolNames();
        Assert.Contains("key_set", names);
        Assert.DoesNotContain("project_new", names); // lifecycle tools exist only in headless mode

        Assert.Equal("In the app", (string)(await mcp.Ok("project_info"))["name"]!);
        await mcp.Ok("task_begin", new { title = "From MCP" });
        var clip = (string)(await mcp.Ok("clip_create", new { name = "Forwarded" }))["data"]!["clipId"]!;
        await mcp.Ok("key_set", new { clipId = clip, characterId = character.Id.ToString(), frame = 0 });
        lock (gate)
        {
            Assert.Equal("Forwarded", session.Project.Clips.Single().Name);
            Assert.NotEmpty(session.Project.Clips[0].Tracks);
            Assert.Equal("test-client", host.Sessions.Single().ClientName); // MCP clientInfo became the agent name
            Assert.Contains(host.Tasks, t => t.Title == "From MCP");
        }

        // Destructive: held for the user, and the agent is told so.
        var (err, text) = await mcp.Call("clip_delete", new { clipId = clip });
        Assert.True(err);
        Assert.Contains("approval_required", text);
        Assert.Contains("approve", text);
        lock (gate) Assert.Single(session.Project.Clips);

        await mcp.Ok("task_finish");
        lock (gate) Assert.Equal(AgentTaskState.Review, host.Tasks.Last().State);
        Assert.Equal(0, await mcp.CloseAndWait());
        lock (gate) Assert.Empty(host.Sessions); // session.bye on exit
    }

    [Fact]
    public async Task Cli_Attach_UsesTheRunningApp()
    {
        var project = Project.CreateNew("In the app");
        MannequinFactory.AddTo(project);
        var session = new ProjectSession(project);
        var registry = CommandRegistry.CreateDefault();
        var host = new AgentHost(registry, () => session, userPresent: true);
        var rpc = new AgentRpcHandler(host, registry, new FakeServices(session));
        var gate = new object();
        var pipe = "blazma-test-" + Guid.NewGuid().ToString("N");
        await using var server = new AgentIpcServer((req, ctx) => { lock (gate) return Task.FromResult(rpc.Handle(req, ctx)); }, pipeName: pipe);
        server.Start();

        var r = await Tools.RunCli("exec", "--attach", "--pipe", pipe, "clip.create", "--arg", "name=ViaCli");
        Assert.Equal(0, r.Exit);
        lock (gate) Assert.Equal("ViaCli", session.Project.Clips.Single().Name);
        // Nothing listening: user error, not a crash.
        Assert.Equal(1, (await Tools.RunCli("inspect", "--attach", "--pipe", "blazma-nobody-" + Guid.NewGuid().ToString("N"))).Exit);
    }
}
