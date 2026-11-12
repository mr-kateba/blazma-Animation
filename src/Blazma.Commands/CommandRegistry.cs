using System.Text.Json.Nodes;

namespace Blazma.Commands;

/// <summary>
/// Describes one command for every front end. MCP tools, CLI sub-commands and their docs are
/// generated from these descriptors, so there is a single source of truth (DECISIONS.md D3).
/// </summary>
public sealed record CommandDescriptor(
    string Id,
    string Summary,
    CommandEffect Effect,
    JsonObject InputSchema,
    Func<JsonObject, ICommand> Factory)
{
    /// <summary>MCP tool name: "clip.create" → "clip_create".</summary>
    public string ToolName => Id.Replace('.', '_');

    /// <summary>
    /// The argument naming a file outside the project that this command reads or writes. Agents get the
    /// allowed-folder rules for it whatever the <see cref="Effect"/> (e.g. a ReadOnly export that writes a
    /// file but does not change the project). Null: commands with Effect External use "path" for reading.
    /// </summary>
    public FileArgument? FileArg { get; init; }
}

/// <param name="Argument">Name of the path argument.</param>
/// <param name="Write">True when the command creates the file. Agents may then only create new files.</param>
/// <param name="Extension">Required extension of a written file (e.g. ".glb").</param>
/// <param name="OverwriteArgument">Boolean argument that allows replacing an existing file; always forced off for agents.</param>
public sealed record FileArgument(string Argument, bool Write = false, string? Extension = null, string? OverwriteArgument = null);

public sealed class CommandRegistry
{
    readonly SortedDictionary<string, CommandDescriptor> _commands = new(StringComparer.Ordinal);

    public IEnumerable<CommandDescriptor> All => _commands.Values;

    /// <summary>Background motion generation used by the motion.* commands (null when they are not registered).
    /// Hosts use it for job events, provider settings and diagnostics.</summary>
    public MotionJobService? Motion { get; private set; }

    public void Register(CommandDescriptor descriptor)
    {
        if (!_commands.TryAdd(descriptor.Id, descriptor))
            throw new InvalidOperationException($"Command '{descriptor.Id}' is already registered.");
    }

    public bool TryGet(string id, out CommandDescriptor descriptor) => _commands.TryGetValue(id, out descriptor!);

    /// <summary>Builds a command from JSON arguments, validating them against the input schema.</summary>
    public ICommand Create(string id, JsonObject? args)
    {
        if (!_commands.TryGetValue(id, out var d))
            throw new CommandException("unknown_command", $"Unknown command '{id}'.");
        args ??= new JsonObject();
        SchemaValidator.Validate(d.InputSchema, args);
        return d.Factory(args);
    }

    /// <summary>Registry with every built-in command of this build.</summary>
    /// <param name="motion">Motion generation service; default: Kimodo from the BLAZMA_KIMODO_* environment variables + procedural.</param>
    public static CommandRegistry CreateDefault(MotionJobService? motion = null)
    {
        var r = new CommandRegistry { Motion = motion ?? MotionJobService.CreateDefault() };
        BuiltinCommands.Register(r);
        PoseCommands.Register(r);
        RigCommands.Register(r);
        KeyCommands.Register(r);
        AnimationEditCommands.Register(r);
        ExportCommands.Register(r);
        PhysicsCommands.Register(r);
        PhysicsPolishCommands.Register(r);
        AICommands.Register(r, r.Motion);
        RetargetCommands.Register(r);
        SequenceCommands.Register(r);
        PropCommands.Register(r);
        TemplateCommands.Register(r);
        CombatCommands.Register(r);
        return r;
    }
}

/// <summary>
/// Validates the subset of JSON Schema the descriptors use: an object with typed properties
/// (string, integer, number, boolean), "required", "minimum", "minLength" and no extra properties.
/// </summary>
public static class SchemaValidator
{
    public static void Validate(JsonObject schema, JsonObject args)
    {
        var props = schema["properties"] as JsonObject ?? new JsonObject();
        foreach (var req in schema["required"] as JsonArray ?? new JsonArray())
            if (args[(string)req!] is null)
                throw new CommandException("invalid_args", $"Missing required argument '{(string)req!}'.");

        foreach (var (name, value) in args)
        {
            if (props[name] is not JsonObject p)
                throw new CommandException("invalid_args", $"Unknown argument '{name}'.");
            if (value is null) continue;
            var type = (string?)p["type"];
            var ok = type switch
            {
                "string" => value is JsonValue v && v.TryGetValue<string>(out _),
                "integer" => value is JsonValue v && TryGetInteger(v, out _),
                "number" => value is JsonValue v && TryGetNumber(v, out _),
                "boolean" => value is JsonValue v && v.TryGetValue<bool>(out _),
                _ => true,
            };
            if (!ok) throw new CommandException("invalid_args", $"Argument '{name}' must be of type {type}.");

            if (p["minimum"] is JsonValue min && TryGetNumber((JsonValue)value, out var n) && TryGetNumber(min, out var m) && n < m)
                throw new CommandException("invalid_args", $"Argument '{name}' must be ≥ {min}.");
            if (p["minLength"] is JsonValue minLen && value.GetValue<string>().Trim().Length < minLen.GetValue<int>())
                throw new CommandException("invalid_args", $"Argument '{name}' must not be empty.");
        }
    }

    /// <summary>Reads any numeric JsonValue (parsed or created from int/long/double) as double.</summary>
    public static bool TryGetNumber(JsonValue v, out double result)
    {
        if (v.TryGetValue(out result)) return true;
        if (v.TryGetValue<long>(out var l)) { result = l; return true; }
        if (v.TryGetValue<int>(out var i)) { result = i; return true; }
        return false;
    }

    static bool TryGetInteger(JsonValue v, out long result)
    {
        if (v.TryGetValue(out result)) return true;
        if (v.TryGetValue<int>(out var i)) { result = i; return true; }
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < 9e15) { result = (long)d; return true; }
        return false;
    }
}
