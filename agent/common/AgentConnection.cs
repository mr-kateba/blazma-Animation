using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Agent;

namespace Blazma.AgentTools;

/// <summary>One command as advertised by the agent API (tools.list).</summary>
public sealed record ToolInfo(string Id, string ToolName, string Summary, string Effect, JsonObject InputSchema);

/// <summary>
/// An agent session on a backend: says hello once, remembers the session id and offers the
/// operations both front ends need. Command failures come back as the CommandResult JSON
/// ({"ok":false,"error":{...}}), never as exceptions, so callers treat every outcome the same way.
/// </summary>
public sealed class AgentConnection(IAgentBackend backend, string clientName) : IAsyncDisposable
{
    string? _sessionId;
    List<ToolInfo>? _tools;

    public IAgentBackend Backend => backend;
    public bool IsHeadless => backend.IsHeadless;
    public string ClientName { get; set; } = clientName;

    /// <summary>Full hello result (mode, userPresent, project) once connected.</summary>
    public JsonNode? Hello { get; private set; }

    public async Task EnsureSessionAsync(CancellationToken ct)
    {
        if (_sessionId is not null) return;
        var hello = await backend.CallAsync("session.hello", new JsonObject { ["client"] = ClientName }, ct).ConfigureAwait(false);
        Hello = hello;
        _sessionId = (string?)hello?["sessionId"] ?? throw new AgentRpcException("protocol", "The app did not return a sessionId.");
    }

    /// <summary>Calls an RPC method with the session id attached (saying hello first if needed).</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonObject? p, CancellationToken ct)
    {
        await EnsureSessionAsync(ct).ConfigureAwait(false);
        p ??= new JsonObject();
        p["sessionId"] = _sessionId;
        return await backend.CallAsync(method, p, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        if (_tools is not null) return _tools;
        var r = await backend.CallAsync("tools.list", new JsonObject(), ct).ConfigureAwait(false);
        var list = new List<ToolInfo>();
        foreach (var t in (JsonArray)r!["tools"]!)
            list.Add(new ToolInfo((string)t!["name"]!, (string)t["toolName"]!, (string)t["summary"]!, (string)t["effect"]!,
                (JsonObject)t["inputSchema"]!.DeepClone()));
        return _tools = list;
    }

    /// <summary>Runs a registry command by id; returns the CommandResult JSON (also for failures).</summary>
    public Task<JsonObject> CallCommandAsync(string commandId, JsonObject? args, CancellationToken ct) =>
        Guard(() => CallAsync("tools.call", new JsonObject { ["name"] = commandId, ["arguments"] = args ?? new JsonObject() }, ct));

    /// <summary>Calls any RPC method, converting an RPC error into {"ok":false,"error":{code,message}}.</summary>
    public Task<JsonObject> CallGuardedAsync(string method, JsonObject? p, CancellationToken ct) =>
        Guard(() => CallAsync(method, p, ct));

    static async Task<JsonObject> Guard(Func<Task<JsonNode?>> call)
    {
        try
        {
            var r = await call().ConfigureAwait(false);
            return r as JsonObject ?? new JsonObject { ["ok"] = true, ["data"] = r };
        }
        catch (AgentRpcException e)
        {
            return Failure(e.Code, e.Message);
        }
    }

    public static JsonObject Failure(string code, string message) => new()
    {
        ["ok"] = false,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    public static bool IsOk(JsonObject r) => r["ok"]?.GetValue<bool>() ?? r["error"] is null;

    public async ValueTask DisposeAsync()
    {
        if (_sessionId is not null)
        {
            try { await backend.CallAsync("session.bye", new JsonObject { ["sessionId"] = _sessionId }, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) when (e is AgentRpcException or IOException or ObjectDisposedException or InvalidOperationException or JsonException) { }
            _sessionId = null;
        }
        await backend.DisposeAsync().ConfigureAwait(false);
    }
}
