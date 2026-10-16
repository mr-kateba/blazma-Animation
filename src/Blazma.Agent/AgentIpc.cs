using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blazma.Agent;

/// <summary>
/// Local transport between a running Blazma Animation window and agent tools (blazma-mcp,
/// blazma-anim --attach): newline-delimited JSON-RPC 2.0 over a named pipe that only the current
/// user can open (PipeOptions.CurrentUserOnly: an ACL on Windows, a 0600 socket on Unix).
/// Nothing listens on the network.
/// </summary>
public static class AgentIpc
{
    /// <summary>Pipe name, per user, so two users on one machine never meet.</summary>
    public static string PipeName => "blazma-animation-v1-" + Sanitize(Environment.UserName);

    static string Sanitize(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    public const int MaxMessageBytes = 16 * 1024 * 1024;
}

/// <summary>Accepts agent connections and forwards each request to a handler.</summary>
public sealed class AgentIpcServer : IAsyncDisposable
{
    readonly Func<JsonObject, Task<JsonObject>> _handle;
    readonly Action<string>? _onDisconnect;
    readonly CancellationTokenSource _cts = new();
    readonly string _pipeName;
    Task? _loop;

    /// <param name="handle">Called on a pool thread; marshal to the session's thread inside it.</param>
    /// <param name="onDisconnect">Called with the session ids a closed connection had opened.</param>
    public AgentIpcServer(Func<JsonObject, Task<JsonObject>> handle, Action<string>? onDisconnect = null, string? pipeName = null)
    {
        _handle = handle;
        _onDisconnect = onDisconnect;
        _pipeName = pipeName ?? AgentIpc.PipeName;
    }

    public string PipeName => _pipeName;

    public void Start() => _loop ??= Task.Run(AcceptLoop);

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException)
            {
                // Pipe name busy (another Blazma window owns it) or transient: back off and retry.
                try { await Task.Delay(1000, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }
            _ = Task.Run(() => Serve(pipe));
        }
    }

    async Task Serve(NamedPipeServerStream pipe)
    {
        var sessions = new HashSet<string>();
        await using (pipe)
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            while (!_cts.IsCancellationRequested && pipe.IsConnected)
            {
                string? line;
                try { line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { break; }
                if (line is null) break;
                if (line.Length == 0) continue;

                JsonObject response;
                if (line.Length > AgentIpc.MaxMessageBytes)
                    response = AgentRpcHandler.Error(null, "too_large", "Message too large.");
                else
                {
                    JsonObject? request = null;
                    try { request = JsonNode.Parse(line) as JsonObject; }
                    catch (JsonException) { }
                    if (request is null) response = AgentRpcHandler.Error(null, "parse_error", "Invalid JSON.");
                    else
                    {
                        response = await _handle(request).ConfigureAwait(false);
                        if ((string?)request["method"] == "session.hello" && response["result"]?["sessionId"] is JsonValue sid)
                            sessions.Add((string)sid!);
                    }
                }
                try { await writer.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { break; }
            }
        }
        foreach (var s in sessions) _onDisconnect?.Invoke(s);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        _cts.Dispose();
    }
}

/// <summary>Client side of <see cref="AgentIpcServer"/> (used by blazma-mcp and blazma-anim --attach).</summary>
public sealed class AgentIpcClient : IAsyncDisposable
{
    readonly NamedPipeClientStream _pipe;
    readonly StreamReader _reader;
    readonly StreamWriter _writer;
    readonly SemaphoreSlim _lock = new(1, 1);
    long _nextId;

    AgentIpcClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1 << 16, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    }

    /// <summary>Connects to a running app, or returns null if none answers within the timeout.</summary>
    public static async Task<AgentIpcClient?> TryConnectAsync(TimeSpan timeout, string? pipeName = null, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName ?? AgentIpc.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
            return new AgentIpcClient(pipe);
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>Sends one request and returns the "result" object; throws AgentRpcException on an error response.</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonObject? parameters = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = Interlocked.Increment(ref _nextId);
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
            await _writer.WriteLineAsync(request.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
            var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false)
                ?? throw new AgentRpcException("disconnected", "Blazma Animation closed the connection.");
            var response = JsonNode.Parse(line) as JsonObject ?? throw new AgentRpcException("parse_error", "Invalid response.");
            if (response["error"] is JsonObject err)
                throw new AgentRpcException((string?)err["data"]?["code"] ?? "error", (string?)err["message"] ?? "Error");
            return response["result"];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync().ConfigureAwait(false);
        _reader.Dispose();
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _lock.Dispose();
    }
}
