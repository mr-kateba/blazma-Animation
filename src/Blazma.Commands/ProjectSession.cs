using Blazma.Core.Ids;
using Blazma.Core.Model;

namespace Blazma.Commands;

public sealed record SessionChangedArgs(IReadOnlyList<Change> Changes, CommandOrigin Origin);

public sealed record TransactionInfo(string Id, string Label, CommandOrigin Origin, int CommandCount);

/// <summary>
/// The single entry point for editing a project: UI, CLI and MCP all go through
/// <see cref="Execute"/>. Owns one undo stack shared by every source (ARCHITECTURE.md §3).
/// Not thread-safe; use from one thread (the app drains its command queue on the Godot main thread).
/// </summary>
public sealed class ProjectSession
{
    sealed class Transaction(string id, string label, CommandOrigin origin, long sequence)
    {
        public string Id { get; } = id;
        public string Label { get; } = label;
        public CommandOrigin Origin { get; } = origin;
        public long Sequence { get; } = sequence;
        public List<ICommand> Commands { get; } = new();
        public TransactionInfo Info => new(Id, Label, Origin, Commands.Count);
        public long Bytes => Commands.Sum(c => c.HistoryBytes);
    }

    readonly List<Transaction> _undo = new();
    readonly List<Transaction> _redo = new();
    readonly IOpLog? _log;
    Transaction? _open;
    long _nextSequence = 1;
    long _savedSequence;
    long _baseSequence; // sequence of the newest step dropped from the bottom of the history (0: none)
    long _undoBytes;

    /// <summary>Default undo depth: older steps are dropped (the project keeps their result; they just cannot be undone).</summary>
    public const int DefaultMaxHistorySteps = 500;

    /// <summary>Default estimated memory budget for undo history (256 MB).</summary>
    public const long DefaultMaxHistoryBytes = 256L * 1024 * 1024;

    /// <summary>Maximum number of undo steps kept (at least 1).</summary>
    public int MaxHistorySteps { get; set; } = DefaultMaxHistorySteps;

    /// <summary>Estimated memory budget for undo steps (<see cref="ICommand.HistoryBytes"/>); the newest step is always kept.</summary>
    public long MaxHistoryBytes { get; set; } = DefaultMaxHistoryBytes;

    /// <summary>Estimated memory held by the undo steps.</summary>
    public long HistoryBytes => _undoBytes;

    public ProjectSession(Project project, string? filePath = null, IOpLog? log = null)
    {
        Project = project;
        FilePath = filePath;
        _log = log;
    }

    public Project Project { get; private set; }
    public string? FilePath { get; private set; }

    /// <summary>Unsaved changes exist (the undo position differs from the last save).</summary>
    public bool IsDirty => TopSequence != _savedSequence || _open is { Commands.Count: > 0 };

    public bool CanUndo => _open is null && _undo.Count > 0;
    public bool CanRedo => _open is null && _redo.Count > 0;
    public bool InTransaction => _open is not null;
    public IReadOnlyList<TransactionInfo> UndoHistory => _undo.Select(t => t.Info).ToList();
    public IReadOnlyList<TransactionInfo> RedoHistory => _redo.Select(t => t.Info).ToList();

    public event Action<SessionChangedArgs>? Changed;

    long TopSequence => _undo.Count == 0 ? _baseSequence : _undo[^1].Sequence;

    public CommandResult Execute(ICommand command, CommandOrigin origin)
    {
        if (_open is not null && _open.Origin != origin)
            return Log(command, origin, CommandResult.Fail("busy", $"Another edit ({_open.Label}) is in progress."));

        if (command is IOriginAwareCommand aware) aware.Origin = origin;
        CommandOutcome outcome;
        try
        {
            outcome = command.Execute(Project);
        }
        catch (CommandException e)
        {
            return Log(command, origin, CommandResult.Fail(e.Code, e.Message));
        }
        catch (KeyNotFoundException e)
        {
            return Log(command, origin, CommandResult.Fail("not_found", e.Message));
        }
        catch (Exception e) when (IsRecoverable(e))
        {
            // Safety net: a bug or unforeseen input in a command must not crash the app (the UI calls this on its main thread).
            // Commands are exception-safe by contract (ICommand): they validate before mutating or work on copies and swap them in
            // last, and the shared bases (SceneEdit, ClipEdit, PoseEdit, AddClipCommand) restore or never touch the project when
            // Apply/Build throws anything. So nothing is half-applied, and the failed command is not recorded for undo.
            return Log(command, origin, CommandResult.Fail("internal_error", $"{command.Id} failed unexpectedly ({e.GetType().Name}): {e.Message}"));
        }

        string? txId = null;
        if (command.Effect != CommandEffect.ReadOnly)
        {
            var wasDirty = IsDirty;
            if (_open is not null)
            {
                if (_open.Commands.Count == 0 || !_open.Commands[^1].TryMerge(command))
                    _open.Commands.Add(command);
                txId = _open.Id;
            }
            else
            {
                var tx = NewTransaction(command.Label, origin);
                tx.Commands.Add(command);
                Push(tx);
                txId = tx.Id;
            }
            Raise(outcome.Changes, origin, wasDirty);
        }

        var result = new CommandResult(true, outcome.Data, outcome.Changes,
            outcome.Warnings ?? Array.Empty<Diagnostic>(), null, txId);
        return Log(command, origin, result);
    }

    /// <summary>Exceptions the safety net in <see cref="Execute"/> turns into an "internal_error" result: everything except
    /// conditions the process cannot continue after (out of memory, stack overflow, invalid program, access violations).</summary>
    static bool IsRecoverable(Exception e) =>
        e is not (OutOfMemoryException or InsufficientExecutionStackException or StackOverflowException or InvalidProgramException
            or AccessViolationException or BadImageFormatException);

    /// <summary>
    /// Groups several commands into one undo step (gizmo drags, multi-step agent tasks).
    /// Dispose commits; call <see cref="TransactionScope.Rollback"/> to revert instead.
    /// </summary>
    public TransactionScope BeginTransaction(string label, CommandOrigin origin)
    {
        if (_open is not null) throw new InvalidOperationException($"Transaction '{_open.Label}' is already open.");
        _open = NewTransaction(label, origin);
        return new TransactionScope(this, _open.Id);
    }

    internal void EndTransaction(string id, bool commit)
    {
        if (_open is null || _open.Id != id) return;
        var tx = _open;
        var wasDirty = IsDirty;
        _open = null;
        if (tx.Commands.Count == 0) return;

        if (commit)
        {
            Push(tx);
            Raise(Array.Empty<Change>(), tx.Origin, wasDirty);
        }
        else
        {
            var changes = new List<Change>();
            for (var i = tx.Commands.Count - 1; i >= 0; i--) changes.AddRange(tx.Commands[i].Undo(Project));
            Raise(changes, tx.Origin, wasDirty);
        }
    }

    /// <summary>
    /// Undo the latest step. Agents must pass the transaction id they created, so they can
    /// never undo the user's work by accident; the UI passes null.
    /// </summary>
    public CommandResult Undo(CommandOrigin origin, string? transactionId = null)
    {
        if (_open is not null) return CommandResult.Fail("busy", "Finish the current edit first.");
        if (_undo.Count == 0) return CommandResult.Fail("nothing_to_undo", "Nothing to undo.");
        var tx = _undo[^1];
        if (transactionId is not null && tx.Id != transactionId)
            return CommandResult.Fail("not_latest", "That change is not the most recent one; undo newer changes first.");
        if (origin.Source is CommandSource.Mcp or CommandSource.Cli)
        {
            if (transactionId is null)
                return CommandResult.Fail("transaction_required", "Agents must name the transaction to undo.");
            if (tx.Origin != origin)
                return CommandResult.Fail("not_owner", "Agents can only undo their own changes.");
        }

        var wasDirty = IsDirty;
        _undo.RemoveAt(_undo.Count - 1);
        _undoBytes -= tx.Bytes;
        var changes = new List<Change>();
        for (var i = tx.Commands.Count - 1; i >= 0; i--) changes.AddRange(tx.Commands[i].Undo(Project));
        _redo.Add(tx);
        Raise(changes, origin, wasDirty);
        _log?.Append(new OpLogEntry(DateTimeOffset.UtcNow, origin.ToString(), "edit.undo", tx.Label, true, null, tx.Id));
        return new CommandResult(true, null, changes, Array.Empty<Diagnostic>(), null, tx.Id);
    }

    public CommandResult Redo(CommandOrigin origin)
    {
        if (_open is not null) return CommandResult.Fail("busy", "Finish the current edit first.");
        if (_redo.Count == 0) return CommandResult.Fail("nothing_to_redo", "Nothing to redo.");
        var wasDirty = IsDirty;
        var tx = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        var changes = new List<Change>();
        foreach (var c in tx.Commands) changes.AddRange(c.Execute(Project).Changes);
        _undo.Add(tx);
        _undoBytes += tx.Bytes;
        Trim();
        Raise(changes, origin, wasDirty);
        _log?.Append(new OpLogEntry(DateTimeOffset.UtcNow, origin.ToString(), "edit.redo", tx.Label, true, null, tx.Id));
        return new CommandResult(true, null, changes, Array.Empty<Diagnostic>(), null, tx.Id);
    }

    /// <summary>Record that the current state is on disk at <paramref name="path"/>.</summary>
    public void MarkSaved(string path)
    {
        var wasDirty = IsDirty;
        FilePath = path;
        _savedSequence = TopSequence;
        Raise(Array.Empty<Change>(), CommandOrigin.Ui, wasDirty, forceSaveState: true);
    }

    /// <summary>
    /// Display-only change, e.g. showing another frame while scrubbing or playing: not recorded in
    /// history and does not change the saved/dirty state. Only use it for state that is derived
    /// from the document (an evaluated pose), never for real edits.
    /// </summary>
    public void ApplyPreview(Action<Project> apply, IReadOnlyList<Change> changes, CommandOrigin origin)
    {
        if (_open is not null) throw new InvalidOperationException("Cannot preview during a transaction.");
        apply(Project);
        if (changes.Count > 0) Changed?.Invoke(new SessionChangedArgs(changes, origin));
    }

    /// <summary>New/Open: swap the project and clear history. Not undoable by design.</summary>
    public void ReplaceProject(Project project, string? filePath, bool startDirty = false)
    {
        if (_open is not null) throw new InvalidOperationException("Cannot replace the project during a transaction.");
        Project = project;
        FilePath = filePath;
        _undo.Clear();
        _redo.Clear();
        _undoBytes = 0;
        _baseSequence = 0;
        _savedSequence = startDirty ? -1 : 0;
        Changed?.Invoke(new SessionChangedArgs(new[] { new Change(Change.ProjectReplaced) }, CommandOrigin.Ui));
    }

    Transaction NewTransaction(string label, CommandOrigin origin) =>
        new(ObjectId.New().ToString(), label, origin, _nextSequence++);

    void Push(Transaction tx)
    {
        _undo.Add(tx);
        _undoBytes += tx.Bytes;
        _redo.Clear();
        Trim();
    }

    /// <summary>Drop the oldest undo steps past the step/memory budget. The dirty state stays right: the saved position is a
    /// sequence number, and undoing everything that is left now ends at the dropped step's sequence, not at 0.</summary>
    void Trim()
    {
        var drop = 0;
        var bytes = _undoBytes;
        while (_undo.Count - drop > 1 && (_undo.Count - drop > Math.Max(1, MaxHistorySteps) || bytes > MaxHistoryBytes))
        {
            bytes -= _undo[drop].Bytes;
            drop++;
        }
        if (drop == 0) return;
        _baseSequence = _undo[drop - 1].Sequence;
        _undo.RemoveRange(0, drop);
        _undoBytes = bytes;
    }

    void Raise(IReadOnlyList<Change> changes, CommandOrigin origin, bool wasDirty, bool forceSaveState = false)
    {
        if (forceSaveState || wasDirty != IsDirty)
            changes = changes.Append(new Change(Change.SaveStateChanged)).ToList();
        if (changes.Count > 0) Changed?.Invoke(new SessionChangedArgs(changes, origin));
    }

    CommandResult Log(ICommand command, CommandOrigin origin, CommandResult result)
    {
        _log?.Append(new OpLogEntry(DateTimeOffset.UtcNow, origin.ToString(), command.Id, command.Label,
            result.Ok, result.Error?.Code, result.TransactionId));
        return result;
    }
}

public sealed class TransactionScope : IDisposable
{
    readonly ProjectSession _session;
    bool _done;

    internal TransactionScope(ProjectSession session, string id)
    {
        _session = session;
        Id = id;
    }

    public string Id { get; }

    public void Commit() { if (!_done) { _done = true; _session.EndTransaction(Id, commit: true); } }
    public void Rollback() { if (!_done) { _done = true; _session.EndTransaction(Id, commit: false); } }
    public void Dispose() => Commit();
}
