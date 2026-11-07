using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blazma.AI;

/// <summary>
/// One kimodo-runner child process speaking newline-delimited JSON-RPC 2.0 on stdin/stdout (one JSON object per
/// line, UTF-8). stderr is free-form log text and is captured (last lines kept for error messages). Every line from
/// the runner is bounded in size; a crash, an oversized or malformed line fails all outstanding calls with a clean
/// <see cref="MotionGenerationException"/> and the process is killed. Thread-safe.
/// </summary>
internal sealed class RunnerProcess : IDisposable
{
    sealed record Pending(TaskCompletionSource<JsonObject> Tcs, Action<JsonObject>? OnNotification);

    readonly Process _process;
    readonly int _maxLine;
    readonly Action<string>? _log;
    readonly ConcurrentDictionary<long, Pending> _pending = new();
    readonly object _writeLock = new();
    readonly Queue<string> _stderrTail = new();
    readonly Stream _stdin;
    long _nextId;
    volatile MotionGenerationException? _fatal;
    int _killed;
    bool _disposed;
    readonly object _killLock = new();

    RunnerProcess(Process process, int maxLine, Action<string>? log)
    {
        _process = process;
        _maxLine = maxLine;
        _log = log;
        _stdin = process.StandardInput.BaseStream;
        ProcessId = process.Id;
        _ = Task.Run(ReadStdoutAsync);
        _stderrDone = Task.Run(ReadStderrAsync);
    }

    readonly Task _stderrDone;

    public int ProcessId { get; }
    public bool HasExited { get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } } }

    public static RunnerProcess Start(string runner, KimodoSettings settings, Action<string>? log)
    {
        var psi = new ProcessStartInfo(runner)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(runner) ?? Environment.CurrentDirectory,
        };
        foreach (var a in settings.RunnerArguments) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--stdio");
        // A minimal environment (DECISIONS D29): the runner does not need, and should not see, the editor's
        // variables (tokens, API keys of other tools...). psi.Environment starts as a copy of ours.
        var env = RunnerEnvironment(settings, Environment.GetEnvironmentVariables(), Environment.ProcessId, OperatingSystem.IsWindows());
        psi.Environment.Clear();
        foreach (var (k, v) in env) psi.Environment[k] = v;
        Process? p;
        try { p = RunnerSpawner.Start(psi); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new MotionGenerationException("runner_start_failed", $"kimodo-runner could not be started: {e.Message}", e);
        }
        if (p is null) throw new MotionGenerationException("runner_start_failed", "kimodo-runner could not be started.");
        if (OperatingSystem.IsWindows() && !RunnerJob.Assign(p, out var why)) log?.Invoke($"kimodo-runner is not tied to Blazma's lifetime ({why}); it still exits when its input closes.");
        return new RunnerProcess(p, settings.MaxMessageBytes, log);
    }

    /// <summary>Variables passed through from the host, by exact name (case-insensitive on Windows).</summary>
    static readonly string[] PassThrough =
    {
        // Process basics: executable/DLL search, temp files, user profile (GPU driver caches).
        "PATH", "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE", "LOCALAPPDATA", "APPDATA",
        "SystemRoot", "windir", "SystemDrive", "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "NUMBER_OF_PROCESSORS",
        // Linux: shared libraries and the Vulkan loader's ICD search (XDG dirs), GPU device access.
        "LD_LIBRARY_PATH", "XDG_DATA_DIRS", "XDG_CONFIG_DIRS", "XDG_DATA_HOME", "XDG_CONFIG_HOME", "XDG_RUNTIME_DIR", "XDG_CACHE_HOME",
        // A runner started through the dotnet host (tests, or a managed wrapper).
        "DOTNET_ROOT",
    };

    /// <summary>Prefixes passed through: Vulkan loader/driver (VK_*), GGML backend options (GGML_*), CUDA device selection (CUDA_*).</summary>
    static readonly string[] PassThroughPrefixes = { "VK_", "GGML_", "CUDA_" };

    /// <summary>
    /// The runner's environment: the allow-listed host variables plus the KIMODO_* values Blazma sets. Any KIMODO_* of the host
    /// is dropped (the runner gets the backend and thread count per request). On Linux KIMODO_RUNNER_PARENT_PID lets the
    /// runner bind its lifetime to this process (PR_SET_PDEATHSIG).
    /// </summary>
    internal static Dictionary<string, string> RunnerEnvironment(KimodoSettings settings, System.Collections.IDictionary host, int hostPid, bool windows)
    {
        var cmp = windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var env = new Dictionary<string, string>(cmp);
        foreach (System.Collections.DictionaryEntry e in host)
        {
            if (e.Key is not string k || e.Value is not string v) continue;
            var allowed = PassThrough.Contains(k, cmp) || PassThroughPrefixes.Any(p => k.StartsWith(p, windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (allowed) env[k] = v;
        }
        // kimodo.cpp reads its backend from the environment (the C API's runtime options are accepted but not used in the
        // reviewed revision). The runner also sets it, and KIMODO_THREADS, for every request.
        if (settings.Device == "cpu") env["KIMODO_BACKEND"] = "cpu";
        if (!windows) env["KIMODO_RUNNER_PARENT_PID"] = hostPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return env;
    }

    /// <summary>Calls a method and waits for its result. Notifications for this call (progress) go to <paramref name="onNotification"/>.
    /// Timeout or cancellation kills the process (kimodo.cpp has no way to interrupt a running generation).</summary>
    public async Task<JsonObject> CallAsync(string method, JsonObject? parameters, Action<JsonObject>? onNotification, TimeSpan timeout, CancellationToken ct)
    {
        if (_fatal is { } f) throw f;
        var id = Interlocked.Increment(ref _nextId);
        var pending = new Pending(new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously), onNotification);
        _pending[id] = pending;
        try
        {
            Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() });
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            var done = await Task.WhenAny(pending.Tcs.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token)).ConfigureAwait(false);
            if (done == pending.Tcs.Task) return await pending.Tcs.Task.ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                TrySend(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "cancel", ["params"] = new JsonObject { ["id"] = id } });
                Kill("cancelled");
                throw new OperationCanceledException(ct);
            }
            Kill("timeout");
            throw new MotionGenerationException("runner_timeout", $"kimodo-runner did not answer '{method}' within {timeout.TotalSeconds:0} s; it was stopped.");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>Asks the runner to exit, then kills it if it has not exited after <paramref name="grace"/>.</summary>
    public void Shutdown(TimeSpan grace)
    {
        TrySend(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "shutdown", ["params"] = new JsonObject() });
        try { _stdin.Close(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        try { if (!_process.WaitForExit((int)grace.TotalMilliseconds)) Kill("idle"); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public void Kill(string reason)
    {
        lock (_killLock)
        {
            if (_disposed) return;
            if (Interlocked.Exchange(ref _killed, 1) == 0) _log?.Invoke($"kimodo-runner stopped ({reason}).");
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
    }

    public string StderrTail()
    {
        lock (_stderrTail) return string.Join("\n", _stderrTail);
    }

    public void Dispose()
    {
        Kill("disposed");
        lock (_killLock)
        {
            if (_disposed) return;
            _disposed = true;
            _process.Dispose();
        }
    }

    void Send(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        try
        {
            lock (_writeLock)
            {
                _stdin.Write(bytes);
                _stdin.Flush();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw _fatal ?? Crashed("kimodo-runner closed its input", e);
        }
    }

    void TrySend(JsonObject message)
    {
        try { Send(message); } catch (MotionGenerationException) { }
    }

    MotionGenerationException Crashed(string what, Exception? inner = null)
    {
        string code = "";
        try
        {
            if (_process.WaitForExit(2000))
            {
                code = $" (exit code {_process.ExitCode})";
                // The process is gone, so stderr reaches end of input shortly: let its last lines arrive.
                _stderrDone.Wait(1000);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
        var tail = StderrTail();
        return new MotionGenerationException("runner_crashed",
            $"{what}{code}." + (tail.Length > 0 ? $" Last log lines:\n{tail}" : ""), inner);
    }

    void FailAll(MotionGenerationException e)
    {
        _fatal ??= e;
        foreach (var (_, p) in _pending) p.Tcs.TrySetException(_fatal);
    }

    async Task ReadStdoutAsync()
    {
        var stream = _process.StandardOutput.BaseStream;
        var buffer = new byte[64 * 1024];
        var line = new MemoryStream();
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (n == 0) break;
                var start = 0;
                for (var i = 0; i < n; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    line.Write(buffer, start, i - start);
                    start = i + 1;
                    if (line.Length > _maxLine) { TooLarge(); return; }
                    HandleLine(line.GetBuffer().AsSpan(0, (int)line.Length));
                    line.SetLength(0);
                    if (_fatal is not null) return;
                }
                line.Write(buffer, start, n - start);
                if (line.Length > _maxLine) { TooLarge(); return; }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        FailAll(_killed == 1 ? new MotionGenerationException("runner_stopped", "kimodo-runner was stopped.") : Crashed("kimodo-runner exited unexpectedly"));
    }

    void TooLarge()
    {
        FailAll(new MotionGenerationException("message_too_large", $"kimodo-runner sent a message larger than {_maxLine / (1024 * 1024)} MiB; it was stopped."));
        Kill("oversized message");
    }

    void HandleLine(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > 0 && utf8[^1] == (byte)'\r') utf8 = utf8[..^1];
        if (utf8.Length == 0) return;
        JsonObject? msg;
        try { msg = JsonNode.Parse(utf8) as JsonObject; }
        catch (JsonException) { msg = null; }
        if (msg is null)
        {
            FailAll(new MotionGenerationException("protocol_error", "kimodo-runner wrote something that is not a JSON-RPC message on stdout; it was stopped."));
            Kill("protocol error");
            return;
        }

        if (msg["method"] is JsonValue m && m.TryGetValue<string>(out var method))
        {
            // Notification: progress for a call.
            if (msg["params"] is JsonObject p && p["id"] is JsonValue idv && idv.TryGetValue<long>(out var forId) && _pending.TryGetValue(forId, out var target))
            {
                try { target.OnNotification?.Invoke(p); }
                catch (Exception e) { _log?.Invoke($"progress handler failed: {e.Message}"); }
            }
            else if (method == "log" && msg["params"]?["message"] is JsonValue lm) _log?.Invoke("kimodo-runner: " + (string?)lm);
            return;
        }

        if (msg["id"] is null && msg["error"] is JsonObject anon)
        {
            // An error the runner could not attribute (a request it could not parse, or one too large to read): it belongs to
            // a call in flight, so fail those now instead of letting them run into their timeout.
            var code = ErrorCode(anon, "protocol_error");
            var e = new MotionGenerationException(code, $"kimodo-runner rejected a request: {(string?)anon["message"] ?? "error"}.");
            foreach (var (_, p) in _pending) p.Tcs.TrySetException(e);
            _log?.Invoke($"kimodo-runner rejected a request without an id [{code}].");
            return;
        }
        if (msg["id"] is not JsonValue rid || !rid.TryGetValue<long>(out var id) || !_pending.TryGetValue(id, out var pending)) return;
        if (msg["error"] is JsonObject err)
        {
            var code = ErrorCode(err, "generation_failed");
            pending.Tcs.TrySetException(new MotionGenerationException(code, (string?)err["message"] ?? "kimodo-runner reported an error."));
        }
        else if (msg["result"] is JsonObject result) pending.Tcs.TrySetResult(result);
        else pending.Tcs.TrySetException(new MotionGenerationException("protocol_error", "kimodo-runner answered without a result object."));
    }

    static string ErrorCode(JsonObject err, string fallback) =>
        err["data"] is JsonObject d && d["code"] is JsonValue c && c.TryGetValue<string>(out var code) && code.Length is > 0 and <= 64 ? code : fallback;

    async Task ReadStderrAsync()
    {
        // Read in chunks so a runaway log line cannot grow without bound: lines are cut at 2000 characters.
        const int maxLine = 2000;
        var reader = _process.StandardError;
        var buf = new char[4096];
        var sb = new StringBuilder();
        var truncated = false;
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buf.AsMemory()).ConfigureAwait(false)) > 0)
                for (var i = 0; i < n; i++)
                {
                    var c = buf[i];
                    if (c == '\n') { Emit(); continue; }
                    if (c == '\r') continue;
                    if (sb.Length < maxLine) sb.Append(c); else truncated = true;
                }
            if (sb.Length > 0) Emit();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }

        void Emit()
        {
            var line = sb.ToString() + (truncated ? "…" : "");
            sb.Clear();
            truncated = false;
            lock (_stderrTail)
            {
                _stderrTail.Enqueue(line);
                while (_stderrTail.Count > 40) _stderrTail.Dequeue();
            }
            _log?.Invoke("kimodo-runner: " + line);
        }
    }
}
