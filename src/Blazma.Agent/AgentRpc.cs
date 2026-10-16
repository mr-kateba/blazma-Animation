using System.Text.Json.Nodes;
using Blazma.Commands;

namespace Blazma.Agent;

/// <summary>Error raised to the RPC caller with a stable code.</summary>
public sealed class AgentRpcException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>What the host (app or headless server) offers besides commands.</summary>
public interface IAgentHostServices
{
    string ProjectName { get; }
    string? ProjectPath { get; }
    bool IsDirty { get; }

    /// <summary>Save to the current path (or to <paramref name="path"/> when given). Returns the saved path.</summary>
    string Save(string? path);
}

/// <summary>
/// JSON-RPC 2.0 method dispatcher shared by the app's IPC server and the headless MCP server, so
/// every front end exposes exactly the same agent API (methods are documented in docs/AI_GUIDE.md).
/// Call <see cref="Handle"/> on the thread that owns the project session.
/// </summary>
public sealed class AgentRpcHandler(AgentHost host, CommandRegistry registry, IAgentHostServices services)
{
    public const string ProtocolVersion = "blazma-agent/1";

    AgentRpcContext? _context;

    /// <param name="context">The calling connection; sessions opened on it are the only ones it may use.
    /// Null for in-process callers (headless tools), which own every session.</param>
    public JsonObject Handle(JsonObject request, AgentRpcContext? context = null)
    {
        _context = context;
        var id = request["id"]?.DeepClone();
        try
        {
            var method = (string?)request["method"] ?? throw new AgentRpcException("invalid_request", "Missing 'method'.");
            var p = request["params"] as JsonObject ?? new JsonObject();
            var result = Dispatch(method, p); // before attaching 'id', so the error path can still use it
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (Exception e) when (e is AgentRpcException or InvalidOperationException or KeyNotFoundException or CommandException
            or IOException or UnauthorizedAccessException or Blazma.Core.Persistence.ProjectFormatException or ArgumentException or OverflowException)
        {
            var code = e switch
            {
                AgentRpcException a => a.Code,
                CommandException c => c.Code,
                Blazma.Core.Persistence.ProjectFormatException f => f.Code,
                KeyNotFoundException => "not_found",
                IOException or UnauthorizedAccessException => "io_error",
                _ => "invalid_request",
            };
            return Error(id, code, e.Message);
        }
    }

    public static JsonObject Error(JsonNode? id, string code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = -32000, ["message"] = message, ["data"] = new JsonObject { ["code"] = code } },
    };

    JsonNode Dispatch(string method, JsonObject p)
    {
        switch (method)
        {
            case "session.hello":
            {
                var info = host.Connect((string?)p["client"] ?? "agent");
                _context?.Add(info.Id);
                return new JsonObject
                {
                    ["sessionId"] = info.Id,
                    ["protocol"] = ProtocolVersion,
                    ["app"] = "Blazma Animation",
                    ["mode"] = host.Mode.ToString().ToLowerInvariant(),
                    ["userPresent"] = host.UserPresent,
                    ["project"] = ProjectInfo(),
                };
            }
            case "session.bye":
            {
                var sid = Session(p);
                host.Disconnect(sid);
                _context?.Remove(sid);
                return new JsonObject { ["ok"] = true };
            }
            case "project.info":
                return ProjectInfo();
            case "project.save":
            {
                var sid = Session(p);
                try
                {
                    var saved = services.Save((string?)p["path"]);
                    host.Log(sid, "project.save", saved, true);
                    return new JsonObject { ["path"] = saved };
                }
                catch (AgentRpcException e)
                {
                    host.Log(sid, "project.save", (string?)p["path"] ?? "", false, e.Code, e.Message);
                    throw;
                }
            }
            case "task.abort":
                return ToJson(host.AbortTask(Session(p)));
            case "tools.list":
            {
                var list = new JsonArray();
                foreach (var d in registry.All)
                    list.Add(new JsonObject
                    {
                        ["name"] = d.Id,
                        ["toolName"] = d.ToolName,
                        ["summary"] = d.Summary,
                        ["effect"] = d.Effect.ToString().ToLowerInvariant(),
                        ["inputSchema"] = d.InputSchema.DeepClone(),
                    });
                return new JsonObject { ["tools"] = list };
            }
            case "tools.call":
            {
                var sessionId = Session(p);
                var name = (string?)p["name"] ?? throw new AgentRpcException("invalid_args", "Missing 'name'.");
                return ToJson(host.Call(sessionId, name, p["arguments"] as JsonObject));
            }
            case "task.begin":
            {
                var t = host.BeginTask(Session(p), (string?)p["title"] ?? "Agent task");
                return new JsonObject { ["taskId"] = t.Id };
            }
            case "task.finish":
            {
                var t = host.FinishTask(Session(p), (string?)p["summary"]);
                return t is null
                    ? new JsonObject { ["taskId"] = null, ["state"] = "none" }
                    : new JsonObject { ["taskId"] = t.Id, ["state"] = t.State.ToString().ToLowerInvariant(), ["changes"] = t.Transactions.Count };
            }
            case "task.status":
            {
                var sessionId = Session(p);
                var tasks = new JsonArray();
                foreach (var t in host.Tasks.Where(t => t.SessionId == sessionId).TakeLast(20))
                    tasks.Add(new JsonObject
                    {
                        ["taskId"] = t.Id, ["title"] = t.Title, ["state"] = t.State.ToString().ToLowerInvariant(),
                        ["changes"] = t.Transactions.Count, ["calls"] = t.Calls.Count,
                    });
                return new JsonObject { ["tasks"] = tasks, ["pendingApprovals"] = host.PendingApprovals.Count(a => a.SessionId == sessionId) };
            }
            case "edit.undo":
            {
                var tx = (string?)p["transactionId"] ?? throw new AgentRpcException("invalid_args", "Missing 'transactionId'.");
                return ToJson(host.Undo(Session(p), tx));
            }
            default:
                throw new AgentRpcException("unknown_method", $"Unknown method '{method}'.");
        }
    }

    JsonObject ProjectInfo() => new()
    {
        ["name"] = services.ProjectName,
        ["path"] = services.ProjectPath,
        ["dirty"] = services.IsDirty,
        // Lets the agent tell the user whether they see the edits live (app) or in a file (headless).
        ["userPresent"] = host.UserPresent,
        ["mode"] = host.Mode.ToString().ToLowerInvariant(),
    };

    string Session(JsonObject p)
    {
        var sid = (string?)p["sessionId"] ?? throw new AgentRpcException("invalid_args", "Missing 'sessionId' (call session.hello first).");
        if (_context is not null && !_context.Owns(sid))
            throw new AgentRpcException("invalid_session", "This session does not belong to this connection.");
        if (!host.HasSession(sid)) throw new AgentRpcException("invalid_session", "Unknown or closed session (call session.hello).");
        return sid;
    }

    public static JsonObject ToJson(CommandResult r)
    {
        var o = new JsonObject
        {
            ["ok"] = r.Ok,
            ["data"] = r.Data?.DeepClone(),
            ["transactionId"] = r.TransactionId,
            ["warnings"] = new JsonArray(r.Warnings.Select(w => (JsonNode?)new JsonObject { ["code"] = w.Code, ["message"] = w.Message }).ToArray()),
        };
        if (r.Error is { } e) o["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message };
        return o;
    }
}
