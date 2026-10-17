using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Agent;
using Blazma.AgentTools;

namespace Blazma.Cli;

/// <summary>Exit codes from docs/AI_INTEGRATION.md.</summary>
static class Exit
{
    public const int Ok = 0, UserError = 1, Validation = 2, Internal = 3, Cancelled = 130;
}

sealed class UsageException(string message) : Exception(message);

sealed class Options
{
    public List<string> Positional { get; } = new();
    public Dictionary<string, List<string>> Values { get; } = new();
    public HashSet<string> Flags { get; } = new();

    static readonly HashSet<string> FlagNames = new() { "json", "attach", "no-save", "help" };
    static readonly HashSet<string> ValueNames = new() { "name", "args", "arg", "pipe", "allow-read", "allow-write" };

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "-h") a = "--help";
            if (!a.StartsWith("--", StringComparison.Ordinal)) { o.Positional.Add(a); continue; }
            var name = a[2..];
            string? inline = null;
            var eq = name.IndexOf('=');
            if (eq >= 0) { inline = name[(eq + 1)..]; name = name[..eq]; }
            if (FlagNames.Contains(name)) o.Flags.Add(name);
            else if (ValueNames.Contains(name))
            {
                var v = inline ?? (i + 1 < args.Length ? args[++i] : throw new UsageException($"--{name} needs a value."));
                if (!o.Values.TryGetValue(name, out var list)) o.Values[name] = list = new List<string>();
                list.Add(v);
            }
            else throw new UsageException($"Unknown option '--{name}'.");
        }
        return o;
    }

    public bool Has(string flag) => Flags.Contains(flag);
    public string? One(string name) => Values.TryGetValue(name, out var l) ? l[^1] : null;
    public IReadOnlyList<string> Many(string name) => Values.TryGetValue(name, out var l) ? l : Array.Empty<string>();
}

static class Program
{
    const string Usage = """
        blazma-anim: command-line control of Blazma Animation projects.

        Usage:
          blazma-anim commands [--json]
          blazma-anim new <file.blazanim> [--name NAME] [--json]
          blazma-anim inspect <file> [--json]
          blazma-anim exec <file> <command-id> [--args '<json>'] [--arg key=value ...] [--no-save] [--json]
          blazma-anim run <file> <script.jsonl> [--no-save] [--json]
          blazma-anim docs <out-dir>

        Add --attach (and leave out <file>) to drive the running Blazma Animation window instead of a file.
        Headless file access: commands read files from the project folder, the current folder and --allow-read DIR
        (or BLAZMA_ALLOW_READ); they create new files (animation_export) only in the project folder, the current
        folder and --allow-write DIR (or BLAZMA_ALLOW_WRITE). Read folders never grant write.
        Exit codes: 0 ok, 1 user/input error or command failed, 2 validation failure, 3 internal error, 130 cancelled.
        """;

    static readonly UTF8Encoding Utf8 = new(false);

    static async Task<int> Main(string[] argv)
    {
        Console.OutputEncoding = Utf8;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return argv.Length == 0 ? Exit.UserError : Exit.Ok; }
            var verb = argv[0];
            var o = Options.Parse(argv[1..]);
            if (o.Has("help")) { Console.WriteLine(Usage); return Exit.Ok; }
            return verb switch
            {
                "commands" => await Commands(o, cts.Token),
                "new" => await New(o, cts.Token),
                "inspect" => await Exec(o, cts.Token, forceCommand: "project.inspect"),
                "exec" => await Exec(o, cts.Token, forceCommand: null),
                "run" => await Run(o, cts.Token),
                "docs" => await Docs(o, cts.Token),
                _ => throw new UsageException($"Unknown sub-command '{verb}'. Try 'blazma-anim --help'."),
            };
        }
        catch (UsageException e) { Console.Error.WriteLine($"blazma-anim: {e.Message}"); return Exit.UserError; }
        catch (AgentRpcException e) { Console.Error.WriteLine($"blazma-anim: {e.Message} [{e.Code}]"); return Exit.UserError; }
        catch (OperationCanceledException) { Console.Error.WriteLine("blazma-anim: cancelled."); return Exit.Cancelled; }
        catch (Exception e)
        {
            Console.Error.WriteLine($"blazma-anim: internal error: {e}");
            return Exit.Internal;
        }
    }

    // ---------------------------------------------------------------- connection

    /// <summary>Headless on a file (opened with its lock), or attached to the running app.</summary>
    static async Task<AgentConnection> Connect(Options o, string? file, CancellationToken ct)
    {
        if (o.Has("attach"))
        {
            var backend = await AttachBackend.TryConnectAsync(TimeSpan.FromSeconds(2), o.One("pipe"), ct)
                ?? throw new UsageException("No running Blazma Animation window answered (is the app open?).");
            return new AgentConnection(backend, "cli");
        }
        if (file is null) throw new UsageException("Missing <file> (or use --attach).");
        var headless = Headless(o);
        try { await headless.CallAsync("project.open", new JsonObject { ["path"] = file }, ct); }
        catch { await headless.DisposeAsync(); throw; }
        return new AgentConnection(headless, "cli");
    }

    /// <summary>In-process backend with the folders from --allow-read / --allow-write (plus BLAZMA_ALLOW_READ / BLAZMA_ALLOW_WRITE).</summary>
    static HeadlessBackend Headless(Options o)
    {
        var h = new HeadlessBackend();
        foreach (var d in o.Many("allow-read")) h.AllowRead(d);
        foreach (var d in o.Many("allow-write")) h.AllowWrite(d);
        return h;
    }

    static void Print(Options o, JsonNode json, string human)
    {
        if (o.Has("json")) Console.WriteLine(json.ToJsonString(ResultText.Pretty));
        else Console.WriteLine(human);
    }

    // ---------------------------------------------------------------- commands

    static async Task<int> Commands(Options o, CancellationToken ct)
    {
        await using var conn = o.Has("attach") ? await Connect(o, null, ct) : new AgentConnection(Headless(o), "cli");
        var tools = await conn.ListToolsAsync(ct);
        if (o.Has("json"))
        {
            Console.WriteLine(new JsonArray(tools.Select(t => (JsonNode?)new JsonObject
            {
                ["id"] = t.Id, ["toolName"] = t.ToolName, ["effect"] = t.Effect, ["summary"] = t.Summary,
                ["inputSchema"] = t.InputSchema.DeepClone(),
            }).ToArray()).ToJsonString(ResultText.Pretty));
            return Exit.Ok;
        }
        var width = tools.Max(t => t.Id.Length);
        foreach (var t in tools)
        {
            var req = (t.InputSchema["required"] as JsonArray)?.Select(x => (string)x!).ToHashSet() ?? new HashSet<string>();
            var args = string.Join(" ", ((JsonObject)t.InputSchema["properties"]!).Select(p => req.Contains(p.Key) ? p.Key + "*" : p.Key));
            Console.WriteLine($"{t.Id.PadRight(width)}  {t.Effect,-11} {args}");
        }
        Console.WriteLine("\n(* = required argument. Use: blazma-anim exec <file> <id> --arg name=value)");
        return Exit.Ok;
    }

    static async Task<int> New(Options o, CancellationToken ct)
    {
        var file = o.Positional.Count == 1 ? o.Positional[0] : throw new UsageException("Usage: blazma-anim new <file.blazanim> [--name NAME]");
        await using var headless = Headless(o);
        var args = new JsonObject { ["path"] = file };
        if (o.One("name") is { } n) args["name"] = n;
        var r = (JsonObject)(await headless.CallAsync("project.new", args, ct))!;
        Print(o, new JsonObject { ["ok"] = true, ["data"] = r.DeepClone() }, $"Created {r["path"]} ({r["name"]}) with the default mannequin.");
        return Exit.Ok;
    }

    static async Task<int> Docs(Options o, CancellationToken ct)
    {
        var dir = o.Positional.Count == 1 ? o.Positional[0] : throw new UsageException("Usage: blazma-anim docs <out-dir>");
        await using var conn = new AgentConnection(Headless(o), "cli");
        var tools = await conn.ListToolsAsync(ct);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "MCP_TOOLS.md"), DocsGenerator.McpTools(tools), Utf8, ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "CLI_REFERENCE.md"), DocsGenerator.CliReference(tools), Utf8, ct);
        Console.WriteLine($"Wrote {Path.Combine(dir, "MCP_TOOLS.md")} and {Path.Combine(dir, "CLI_REFERENCE.md")} ({tools.Count} commands).");
        return Exit.Ok;
    }

    // ---------------------------------------------------------------- exec / run

    static async Task<int> Exec(Options o, CancellationToken ct, string? forceCommand)
    {
        string? file = null, id = forceCommand;
        var pos = o.Positional.ToList();
        if (!o.Has("attach")) { if (pos.Count == 0) throw new UsageException("Missing <file>."); file = pos[0]; pos.RemoveAt(0); }
        if (id is null) { if (pos.Count == 0) throw new UsageException("Missing <command-id>. See 'blazma-anim commands'."); id = pos[0]; pos.RemoveAt(0); }
        if (pos.Count > 0) throw new UsageException($"Unexpected argument '{pos[0]}'.");

        await using var conn = await Connect(o, file, ct);
        var tools = await conn.ListToolsAsync(ct);
        var tool = tools.FirstOrDefault(t => t.Id == id || t.ToolName == id)
            ?? throw new UsageException($"Unknown command '{id}'. See 'blazma-anim commands'.");
        var args = BuildArgs(o, tool);

        var step = await RunStep(conn, tool, args, ct);
        var ok = AgentConnection.IsOk(step);
        var readOnly = tool.Effect == "readonly";
        var saved = false;
        if (ok && !readOnly && !o.Has("no-save") && conn.IsHeadless)
            saved = await Save(conn, ct);
        step["saved"] = saved;

        if (ok || o.Has("json")) Print(o, step, Human(tool.Id, step, saved));
        else Console.Error.WriteLine(Human(tool.Id, step, saved));
        if (!ok && o.Has("json")) Console.Error.WriteLine($"blazma-anim: {tool.Id} failed: {step["error"]?["code"]}: {step["error"]?["message"]}");
        return ok ? Exit.Ok : Exit.UserError;
    }

    static async Task<JsonObject> RunStep(AgentConnection conn, ToolInfo tool, JsonObject args, CancellationToken ct)
    {
        var edits = tool.Effect != "readonly";
        if (edits) await conn.CallGuardedAsync("task.begin", new JsonObject { ["title"] = $"CLI: {tool.Id}" }, ct);
        var r = await conn.CallCommandAsync(tool.Id, args, ct);
        if (edits) await conn.CallGuardedAsync("task.finish", new JsonObject { ["summary"] = $"CLI: {tool.Id}" }, ct);
        return r;
    }

    static async Task<bool> Save(AgentConnection conn, CancellationToken ct)
    {
        var s = await conn.CallGuardedAsync("project.save", null, ct);
        if (AgentConnection.IsOk(s)) return true;
        throw new AgentRpcException((string?)s["error"]?["code"] ?? "save_failed", (string?)s["error"]?["message"] ?? "Save failed.");
    }

    static string Human(string id, JsonObject r, bool saved)
    {
        var sb = new StringBuilder();
        if (AgentConnection.IsOk(r))
        {
            sb.Append("ok ").Append(id);
            if (r["transactionId"] is { } tx) sb.Append("  (transaction ").Append((string?)tx).Append(')');
            if (saved) sb.Append("  [saved]");
            foreach (var w in r["warnings"] as JsonArray ?? new JsonArray()) sb.Append("\nwarning: ").Append((string?)w?["message"]);
            if (r["data"] is { } d) sb.Append('\n').Append(d.ToJsonString(ResultText.Pretty));
        }
        else sb.Append(ResultText.ForAgent(r));
        return sb.ToString();
    }

    static JsonObject BuildArgs(Options o, ToolInfo tool)
    {
        var args = new JsonObject();
        if (o.One("args") is { } json)
        {
            try { args = JsonNode.Parse(json) as JsonObject ?? throw new UsageException("--args must be a JSON object."); }
            catch (JsonException e) { throw new UsageException($"--args is not valid JSON: {e.Message}"); }
        }
        var props = tool.InputSchema["properties"] as JsonObject ?? new JsonObject();
        foreach (var kv in o.Many("arg"))
        {
            var eq = kv.IndexOf('=');
            if (eq <= 0) throw new UsageException($"--arg expects key=value, got '{kv}'.");
            var key = kv[..eq];
            var raw = kv[(eq + 1)..];
            var type = (string?)props[key]?["type"];
            args[key] = type switch
            {
                "integer" => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? JsonValue.Create(l)
                    : throw new UsageException($"--arg {key}: '{raw}' is not an integer."),
                "number" => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d)
                    : throw new UsageException($"--arg {key}: '{raw}' is not a number."),
                "boolean" => bool.TryParse(raw, out var b) ? JsonValue.Create(b)
                    : throw new UsageException($"--arg {key}: '{raw}' is not true/false."),
                _ => JsonValue.Create(raw),
            };
        }
        return args;
    }

    static JsonObject Substitute(JsonObject args, Dictionary<string, string> vars)
    {
        var copy = (JsonObject)args.DeepClone();
        foreach (var key in copy.Select(kv => kv.Key).ToList())
            if (copy[key] is JsonValue v && v.TryGetValue<string>(out var str) && str.Contains("${", StringComparison.Ordinal))
            {
                foreach (var (name, value) in vars) str = str.Replace("${" + name + "}", value, StringComparison.Ordinal);
                copy[key] = str;
            }
        return copy;
    }

    static async Task<int> Run(Options o, CancellationToken ct)
    {
        var pos = o.Positional.ToList();
        string? file = null;
        if (!o.Has("attach")) { if (pos.Count == 0) throw new UsageException("Usage: blazma-anim run <file> <script.jsonl>"); file = pos[0]; pos.RemoveAt(0); }
        var scriptPath = pos.Count == 1 ? pos[0] : throw new UsageException("Usage: blazma-anim run <file> <script.jsonl>");
        if (!File.Exists(scriptPath)) throw new UsageException($"Script not found: {scriptPath}");

        // Parse the whole script first so a typo on line 40 does not leave half the work applied.
        var steps = new List<(int Line, string Id, JsonObject Args, JsonObject? Save)>();
        var lineNo = 0;
        foreach (var line in await File.ReadAllLinesAsync(scriptPath, ct))
        {
            lineNo++;
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            JsonObject obj;
            try { obj = JsonNode.Parse(t) as JsonObject ?? throw new UsageException($"{scriptPath}:{lineNo}: expected a JSON object."); }
            catch (JsonException e) { throw new UsageException($"{scriptPath}:{lineNo}: invalid JSON: {e.Message}"); }
            var cmd = (string?)obj["command"] ?? throw new UsageException($"{scriptPath}:{lineNo}: missing \"command\".");
            steps.Add((lineNo, cmd, obj["args"] as JsonObject ?? new JsonObject(), obj["save"] as JsonObject));
        }

        await using var conn = await Connect(o, file, ct);
        var tools = await conn.ListToolsAsync(ct);
        foreach (var s in steps)
            if (!tools.Any(t => t.Id == s.Id || t.ToolName == s.Id))
                throw new UsageException($"{scriptPath}:{s.Line}: unknown command '{s.Id}'.");

        // Script variables: ${character} is the project's first character, ${clip} its last clip (if any); "save" captures result data
        // ({"save":{"clip":"clipId"}} stores data.clipId as ${clip}) so later lines can refer to new ids.
        var vars = new Dictionary<string, string>();
        var info = await conn.CallCommandAsync("project.inspect", null, ct);
        if (info["data"]?["characters"] is JsonArray chars && chars.Count > 0 && (string?)chars[0]?["characterId"] is { } firstChar)
            vars["character"] = firstChar;
        if (info["data"]?["clips"] is JsonArray clips && clips.Count > 0 && (string?)clips[^1]?["clipId"] is { } lastClip)
            vars["clip"] = lastClip;
        await conn.CallGuardedAsync("task.begin", new JsonObject { ["title"] = $"CLI script {Path.GetFileName(scriptPath)}" }, ct);
        var results = new JsonArray();
        var failed = false;
        var edited = false;
        foreach (var s in steps)
        {
            ct.ThrowIfCancellationRequested();
            var tool = tools.First(t => t.Id == s.Id || t.ToolName == s.Id);
            var args = Substitute(s.Args, vars);
            var r = await conn.CallCommandAsync(tool.Id, args, ct);
            var ok = AgentConnection.IsOk(r);
            if (ok && s.Save is not null)
                foreach (var (varName, field) in s.Save)
                    if ((string?)r["data"]?[(string?)field ?? ""] is { } val) vars[varName] = val;
            edited |= ok && tool.Effect != "readonly";
            results.Add(new JsonObject { ["line"] = s.Line, ["command"] = tool.Id, ["result"] = r });
            if (!o.Has("json")) Console.WriteLine(ok ? $"[{s.Line}] ok {tool.Id}" : $"[{s.Line}] FAILED {tool.Id}: {r["error"]?["code"]}: {r["error"]?["message"]}");
            if (!ok) { failed = true; break; }
        }
        if (failed)
            // All or nothing, also when attached to the app: revert the steps that already ran.
            await conn.CallGuardedAsync("task.abort", new JsonObject(), ct);
        else
            await conn.CallGuardedAsync("task.finish", new JsonObject { ["summary"] = $"Script {Path.GetFileName(scriptPath)}: done" }, ct);

        var saved = false;
        if (!failed && edited && !o.Has("no-save") && conn.IsHeadless) saved = await Save(conn, ct);
        if (o.Has("json"))
            Console.WriteLine(new JsonObject { ["ok"] = !failed, ["steps"] = results, ["saved"] = saved }.ToJsonString(ResultText.Pretty));
        else if (failed) Console.WriteLine("Stopped at the first failure; the earlier steps were reverted and nothing was saved.");
        else Console.WriteLine($"Ran {steps.Count} command(s){(saved ? "; saved." : ".")}");
        return failed ? Exit.UserError : Exit.Ok;
    }
}
