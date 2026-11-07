using System.Text.Json.Nodes;
using Blazma.Core.Model;

namespace Blazma.Commands;

/// <summary>What a command does; drives agent permissions (docs/AI_INTEGRATION.md §3).</summary>
public enum CommandEffect { ReadOnly, Mutating, Destructive, External }

public enum CommandSource { Ui, Cli, Mcp, Test }

/// <summary>Who issued a command. Agent edits carry their session id so they can only undo their own work.</summary>
public sealed record CommandOrigin(CommandSource Source, string? AgentSessionId = null)
{
    public static readonly CommandOrigin Ui = new(CommandSource.Ui);
    public static readonly CommandOrigin Test = new(CommandSource.Test);
    public override string ToString() => AgentSessionId is null ? Source.ToString().ToLowerInvariant() : $"{Source.ToString().ToLowerInvariant()}:{AgentSessionId}";
}

/// <summary>
/// A command that needs to know who issued it (e.g. generation jobs belong to the agent session that started them).
/// <see cref="ProjectSession.Execute"/> sets <see cref="Origin"/> before the first execution; redo keeps it. Null (a command
/// executed directly, outside a session) is treated like the user.
/// </summary>
public interface IOriginAwareCommand : ICommand
{
    CommandOrigin? Origin { get; set; }
}

/// <summary>What changed, so views refresh only the affected parts.</summary>
public sealed record Change(string Kind, string? TargetId = null)
{
    public const string ProjectChanged = "project";
    public const string ClipAdded = "clip.added";
    public const string ClipRemoved = "clip.removed";
    public const string ClipChanged = "clip.changed";
    public const string ProjectReplaced = "project.replaced";
    public const string SaveStateChanged = "save.state";
    public const string PoseChanged = "pose.changed";
    public const string CharacterAdded = "character.added";
    public const string CharacterRemoved = "character.removed";
    public const string RigChanged = "rig.changed";
    public const string PoseLibraryChanged = "poselib.changed";
    public const string SequenceChanged = "sequence.changed";
    public const string PropChanged = "prop.changed";
    public const string TemplateChanged = "template.changed";
    public const string CombatChanged = "combat.changed";
    public const string ComboChanged = "combo.changed";
}

public sealed record Diagnostic(string Code, string Message);
public sealed record ErrorInfo(string Code, string Message);

public sealed record CommandResult(
    bool Ok,
    JsonNode? Data,
    IReadOnlyList<Change> Changes,
    IReadOnlyList<Diagnostic> Warnings,
    ErrorInfo? Error,
    string? TransactionId)
{
    public static CommandResult Fail(string code, string message) =>
        new(false, null, Array.Empty<Change>(), Array.Empty<Diagnostic>(), new ErrorInfo(code, message), null);
}

/// <summary>A validation or precondition failure. Commands throw it before mutating anything.</summary>
public sealed class CommandException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record CommandOutcome(IReadOnlyList<Change> Changes, JsonNode? Data = null, IReadOnlyList<Diagnostic>? Warnings = null);

/// <summary>
/// One edit. Rules:
/// <list type="bullet">
/// <item>Validate first and throw <see cref="CommandException"/> before touching the project.</item>
/// <item>Execute must be repeatable after Undo (that is how Redo works), so any generated ids
/// are fixed when the command is constructed.</item>
/// <item>ReadOnly commands never mutate and are never put on the undo stack.</item>
/// <item>Exception safety: when Execute throws ANY exception the project must be exactly as before (validate first, or edit a
/// copy and swap it in as the last step). <see cref="ProjectSession.Execute"/> reports CommandException as its code and any
/// other non-fatal exception as "internal_error", and records nothing for undo. The bases SceneEdit, ClipEdit, PoseEdit and
/// AddClipCommand guarantee this for whatever their Apply/Build throws.</item>
/// </list>
/// </summary>
public interface ICommand
{
    string Id { get; }
    CommandEffect Effect { get; }

    /// <summary>Short human-readable label for the undo history.</summary>
    string Label { get; }

    CommandOutcome Execute(Project project);
    IReadOnlyList<Change> Undo(Project project);

    /// <summary>
    /// Absorb a command executed right after this one inside the same transaction
    /// (e.g. successive values while dragging). Return true if <paramref name="next"/> is now
    /// represented by this command and can be dropped.
    /// </summary>
    bool TryMerge(ICommand next) => false;

    /// <summary>Estimated memory this command keeps for undo/redo; the session drops the oldest steps past its budget
    /// (<see cref="ProjectSession.MaxHistoryBytes"/>). Commands that keep clips or other large snapshots override it.</summary>
    long HistoryBytes => 256;
}
