using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Agent;
using Blazma.AgentTools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Blazma.Mcp;

// blazma-mcp: stdout carries ONLY MCP protocol messages; every log line goes to stderr.
static class Program
{
    const string Instructions = """
        Blazma Animation: an AI-native 3D character animation app. You edit the project through these tools.

        Workflow
        1. Call project_info first (headless mode: project_new or project_open first). Then project_inspect to get character, rig and clip ids.
        2. Group related edits: task_begin {title} ... task_finish {summary}. The user reviews each task as a unit.
        3. Pose with pose_set_bone / pose_set_control, then record it with key_set {clipId, characterId, frame}. Animation is keys on frames; use key_move, key_copy / key_paste and clip_add_phase_markers to restructure.
        4. Destructive calls (clip_delete, character_remove, poselib_delete) may need the user's approval in the app: if a result says approval_required, tell the user and carry on with other work. Do not retry in a loop.
        5. Check results (anim_evaluate, pose_get) before saving; call project_save when done. Use edit_undo {transactionId} to take back your latest change.

        Conventions: rotations are in degrees, intrinsic Euler XYZ, local to the parent bone. Positions are in metres. +Y is up, +Z is forward, +X is the character's left. Frames are integers (frame 0 is the first).
        """;

    static async Task<int> Main(string[] args)
    {
        string? pipe = null, startProject = null;
        var forceHeadless = false;
        var allowRead = new List<string>();
        var allowWrite = new List<string>();
        var forceAttach = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--headless": forceHeadless = true; break;
                case "--attach": forceAttach = true; break;
                case "--pipe" when i + 1 < args.Length: pipe = args[++i]; break;
                case "--allow-read" when i + 1 < args.Length: allowRead.Add(args[++i]); break;
                case "--allow-write" when i + 1 < args.Length: allowWrite.Add(args[++i]); break;
                case "--project" when i + 1 < args.Length: startProject = args[++i]; break;
                case "-h" or "--help":
                    Console.Error.WriteLine("blazma-mcp [--headless | --attach] [--pipe NAME] [--project FILE] [--allow-read DIR]... [--allow-write DIR]...\n" +
                        "MCP server on stdio. Attaches to a running Blazma Animation window when there is one, otherwise runs headless.\n" +
                        "Headless: agents read from the project folder, the start folder and --allow-read/BLAZMA_ALLOW_READ folders;\n" +
                        "they create new files (exports) only in the project folder, the start folder and --allow-write/BLAZMA_ALLOW_WRITE folders.");
                    return 0;
                default:
                    Console.Error.WriteLine($"blazma-mcp: unknown argument '{args[i]}'.");
                    return 1;
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        try
        {
            IAgentBackend? backend = null;
            if (!forceHeadless)
            {
                backend = await AttachBackend.TryConnectAsync(TimeSpan.FromSeconds(2), pipe, cts.Token);
                if (backend is null && forceAttach)
                {
                    Console.Error.WriteLine("blazma-mcp: no running Blazma Animation window answered.");
                    return 1;
                }
            }
            if (backend is null)
            {
                var headless = new HeadlessBackend();
                foreach (var dir in allowRead) headless.AllowRead(dir);
                foreach (var dir in allowWrite) headless.AllowWrite(dir);
                backend = headless;
            }
            Console.Error.WriteLine($"blazma-mcp: {(backend.IsHeadless ? "headless" : "attached to the running app")}");

            await using var conn = new AgentConnection(backend, "mcp");
            if (startProject is not null && backend is HeadlessBackend hb)
                await hb.CallAsync("project.open", new JsonObject { ["path"] = startProject }, cts.Token);

            var server = new ToolServer(conn);
            var options = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "blazma-mcp", Version = "0.1.0" },
                ServerInstructions = Instructions,
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                Handlers = new McpServerHandlers
                {
                    ListToolsHandler = server.ListToolsAsync,
                    CallToolHandler = server.CallToolAsync,
                },
            };
            await using var transport = new StdioServerTransport("blazma-mcp", NullLoggerFactory.Instance);
            await using var mcp = McpServer.Create(transport, options, NullLoggerFactory.Instance);
            await mcp.RunAsync(cts.Token);
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception e)
        {
            Console.Error.WriteLine($"blazma-mcp: fatal: {e}");
            return 3;
        }
    }
}

/// <summary>
/// Tools are not declared with attributes: tools/list is answered from the agent API's tools.list
/// (the command registry, plus the fixed session tools), and tools/call is routed back by name.
/// </summary>
sealed class ToolServer(AgentConnection conn)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> ctx, CancellationToken ct)
    {
        var result = new ListToolsResult();
        foreach (var t in SessionTools.All)
        {
            if (t.HeadlessOnly && !conn.IsHeadless) continue;
            result.Tools.Add(MakeTool(t.Name, t.Description, t.Schema));
        }
        foreach (var c in await conn.ListToolsAsync(ct))
            result.Tools.Add(MakeTool(c.ToolName, $"{c.Summary} Effect: {c.Effect}.", c.InputSchema));
        return result;
    }

    static Tool MakeTool(string name, string description, JsonObject schema) => new()
    {
        Name = name,
        Description = description,
        InputSchema = JsonSerializer.SerializeToElement(schema),
    };

    public async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> ctx, CancellationToken ct)
    {
        var p = ctx.Params ?? throw new InvalidOperationException("Missing parameters.");
        if (ctx.Server.ClientInfo?.Name is { Length: > 0 } client) conn.ClientName = client;
        var args = new JsonObject();
        if (p.Arguments is not null)
            foreach (var kv in p.Arguments) args[kv.Key] = JsonNode.Parse(kv.Value.GetRawText());

        JsonObject result;
        var session = SessionTools.All.FirstOrDefault(t => t.Name == p.Name && (!t.HeadlessOnly || conn.IsHeadless));
        if (session is not null)
            result = await conn.CallGuardedAsync(session.Method, args, ct);
        else
        {
            var tool = (await conn.ListToolsAsync(ct)).FirstOrDefault(t => t.ToolName == p.Name);
            result = tool is null
                ? AgentConnection.Failure("unknown_command", $"Unknown tool '{p.Name}'.")
                : await conn.CallCommandAsync(tool.Id, args, ct);
        }
        return new CallToolResult
        {
            IsError = !AgentConnection.IsOk(result),
            Content = { new TextContentBlock { Text = ResultText.ForAgent(result) } },
        };
    }
}
