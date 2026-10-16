using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blazma.Agent;

/// <summary>
/// Local transport between a running Blazma Animation window and agent tools (blazma-mcp,
/// blazma-anim --attach): newline-delimited JSON-RPC 2.0 over a named pipe that only the current
/// user can open (PipeOptions.CurrentUserOnly). On Unix the socket lives in $XDG_RUNTIME_DIR
/// (a 0700 per-user directory) when available, so other users cannot squat its name in /tmp.
/// Nothing listens on the network.
/// </summary>
public static class AgentIpc
{
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    /// <summary>Pipe name (Windows) or socket path (Unix), per user.</summary>
    public static string PipeName
    {
        get
        {
            var user = Environment.UserName;
            var tag = new string(user.Where(char.IsLetterOrDigit).ToArray());
            // Short hash of the exact user name avoids collisions like "a.b" vs "ab".
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..8].ToLowerInvariant();
            var name = $"blazma-animation-v1-{tag}-{hash}";
            if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run
                && Directory.Exists(run))
            {
                var path = Path.Combine(run, name + ".sock");
                if (path.Length < 100) return path; // sockaddr_un limit
            }
            return name;
        }
    }
}

/// <summary>Per-connection state: the sessions this connection opened (others' ids are refused).</summary>
public sealed class AgentRpcContext
{
    readonly HashSet<string> _sessions = new();
    public IReadOnlyCollection<string> Sessions { get { lock (_sessions) return _sessions.ToList(); } }
    public void Add(string sessionId) { lock (_sessions) _sessions.Add(sessionId); }
    public void Remove(string sessionId) { lock (_sessions) _sessions.Remove(sessionId); }
    public bool Owns(string sessionId) { lock (_sessions) return _sessions.Contains(sessionId); }
}

/// <summary>Accepts agent connections and forwards each request to a handler.</summary>
public sealed class AgentIpcServer : IAsyncDisposable
{
    readonly Func<JsonObject, AgentRpcContext, Task<JsonObject>> _handle;
    readonly Action<string>? _onDisconnect;
    readonly CancellationTokenSource _cts = new();
    readonly string _pipeName;
    Task? _loop;

    /// <param name="handle">Called on a pool thread; marshal to the session's thread inside it.</param>
    /// <param name="onDisconnect">Called with each session id a closed connection had opened.</param>
    public AgentIpcServer(Func<JsonObject, AgentRpcContext, Task<JsonObject>> handle, Action<string>? onDisconnect = null, string? pipeName = null)
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
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Dispose the listening instance, otherwise the OS keeps accepting connections nobody serves.
                if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception)
            {
                // Name busy, a refused foreign client, transient OS errors…: never let the loop die.
                if (pipe is not null) await pipe.DisposeAsync().ConfigureAwait(false);
                try { await Task.Delay(1000, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }
            _ = Task.Run(() => Serve(pipe));
        }
    }

    async Task Serve(NamedPipeServerStream pipe)
    {
        var context = new AgentRpcContext();
        try
        {
            await using (pipe)
            {
                var reader = new BoundedLineReader(pipe, AgentIpc.MaxMessageBytes);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                while (!_cts.IsCancellationRequested && pipe.IsConnected)
                {
                    BoundedLineReader.Line line;
                    try { line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false); }
                    catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { break; }
                    if (line.EndOfStream) break;
                    if (line.TooLarge)
                    {
                        // Cannot resynchronise inside an oversized message: answer and drop the connection.
                        try { await writer.WriteLineAsync(AgentRpcHandler.Error(null, "too_large", "Message too large.").ToJsonString()).ConfigureAwait(false); }
                        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
                        break;
                    }
                    if (line.Text.Length == 0) continue;

                    JsonObject response;
                    try
                    {
                        response = JsonNode.Parse(line.Text) is JsonObject request
                            ? await _handle(request, context).ConfigureAwait(false)
                            : AgentRpcHandler.Error(null, "parse_error", "Expected a JSON object.");
                    }
                    catch (JsonException) { response = AgentRpcHandler.Error(null, "parse_error", "Invalid JSON."); }
                    catch (Exception e) { response = AgentRpcHandler.Error(null, "internal_error", e.Message); }

                    try { await writer.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false); }
                    catch (Exception e) when (e is IOException or ObjectDisposedException) { break; }
                }
            }
        }
        catch (Exception)
        {
            // A broken connection must never take the server down.
        }
        finally
        {
            foreach (var s in context.Sessions)
            {
                try { _onDisconnect?.Invoke(s); } catch (Exception) { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        _cts.Dispose();
    }
}

/// <summary>Reads UTF-8 lines without ever buffering more than a fixed number of bytes.</summary>
internal sealed class BoundedLineReader(Stream stream, int maxBytes)
{
    readonly byte[] _buffer = new byte[64 * 1024];
    int _start, _end;

    public readonly record struct Line(string Text, bool TooLarge, bool EndOfStream);

    public async Task<Line> ReadLineAsync(CancellationToken ct)
    {
        var acc = new MemoryStream();
        var skipping = false;
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await stream.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
                if (_end == 0) return new Line("", false, true); // a partial last line is dropped
            }
            var nl = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            var take = (nl < 0 ? _end : nl) - _start;
            if (!skipping)
            {
                if (acc.Length + take > maxBytes) skipping = true;
                else acc.Write(_buffer, _start, take);
            }
            if (nl < 0) { _start = _end; if (skipping) return new Line("", true, false); continue; }
            _start = nl + 1;
            if (skipping) return new Line("", true, false);
            var text = Encoding.UTF8.GetString(acc.GetBuffer(), 0, (int)acc.Length);
            return new Line(text.TrimEnd('\r'), false, false);
        }
    }
}

/// <summary>Client side of <see cref="AgentIpcServer"/> (used by blazma-mcp and blazma-anim --attach).</summary>
public sealed class AgentIpcClient : IAsyncDisposable
{
    readonly NamedPipeClientStream _pipe;
    readonly BoundedLineReader _reader;
    readonly StreamWriter _writer;
    readonly SemaphoreSlim _lock = new(1, 1);
    long _nextId;
    bool _broken;

    AgentIpcClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new BoundedLineReader(pipe, AgentIpc.MaxMessageBytes);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 1 << 16, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    }

    /// <summary>False after a cancelled or failed call: request/response pairing can no longer be trusted.</summary>
    public bool IsUsable => !_broken && _pipe.IsConnected;

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
        catch (OperationCanceledException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            // Timeout, no server, a foreign-owned socket (EACCES/UnauthorizedAccess)…
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
            if (_broken) throw new AgentRpcException("disconnected", "The connection to Blazma Animation was reset; reconnect.");
            var id = Interlocked.Increment(ref _nextId);
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
            try
            {
                await _writer.WriteLineAsync(request.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
                while (true)
                {
                    var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line.EndOfStream) throw new AgentRpcException("disconnected", "Blazma Animation closed the connection.");
                    if (line.TooLarge) throw new AgentRpcException("too_large", "The response was too large.");
                    if (line.Text.Length == 0) continue;
                    var response = JsonNode.Parse(line.Text) as JsonObject ?? throw new AgentRpcException("parse_error", "Invalid response.");
                    // Responses are matched by id; anything else (a late answer) is discarded.
                    if (response["id"] is not JsonValue rid || !rid.TryGetValue<long>(out var got) || got != id) continue;
                    if (response["error"] is JsonObject err)
                        throw new AgentRpcException((string?)err["data"]?["code"] ?? "error", (string?)err["message"] ?? "Error");
                    return response["result"];
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or JsonException)
            {
                // A cancelled/failed call leaves its response in flight: never reuse this connection.
                _broken = true;
                try { _pipe.Dispose(); } catch (Exception) { }
                if (e is OperationCanceledException) throw;
                throw new AgentRpcException("disconnected", "The connection to Blazma Animation was lost: " + e.Message);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _writer.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _lock.Dispose();
    }
}
