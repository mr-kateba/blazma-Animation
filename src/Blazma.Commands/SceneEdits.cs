using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Core.Persistence;

namespace Blazma.Commands;

/// <summary>
/// Adds one new clip computed on the first Execute (retarget, template, sequence bake). The clip id is fixed per command and
/// the computed clip is kept, so redo re-adds exactly the same clip without recomputing.
/// </summary>
public abstract class AddClipCommand : ICommand
{
    Clip? _clip;
    JsonObject? _data;
    List<Diagnostic> _warnings = new();

    /// <summary>Id the new clip gets.</summary>
    public ObjectId NewClipId { get; } = ObjectId.New();

    public abstract string Id { get; }
    public CommandEffect Effect => CommandEffect.Mutating;
    public abstract string Label { get; }

    public long HistoryBytes => _clip is null ? 256 : ClipDelta.EstimateBytes(_clip);

    public CommandOutcome Execute(Project p)
    {
        if (_clip is null)
        {
            var warnings = new List<Diagnostic>();
            var clip = Build(p, warnings, out var data);
            clip.Id = NewClipId;
            data["clipId"] = NewClipId.ToString();
            data["name"] = clip.Name;
            data["durationFrames"] = clip.DurationFrames;
            data["fps"] = clip.Fps.ToString();
            data["trackCount"] = clip.Tracks.Count;
            _clip = ClipEdit.CloneClip(clip);
            _data = data;
            _warnings = warnings;
        }
        p.Clips.Add(ClipEdit.CloneClip(_clip));
        return new CommandOutcome(new[] { new Change(Change.ClipAdded, NewClipId.ToString()) }, _data!.DeepClone(), _warnings);
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        p.Clips.RemoveAll(c => c.Id == NewClipId);
        return new[] { new Change(Change.ClipRemoved, NewClipId.ToString()) };
    }

    /// <summary>Compute the clip (do not add it). Throw CommandException to abort; the project must not be changed.</summary>
    protected abstract Clip Build(Project project, List<Diagnostic> warnings, out JsonObject data);
}

/// <summary>The scene collections a <see cref="SceneEdit"/> changes (only those are snapshotted for undo).</summary>
[Flags]
public enum SceneParts
{
    None = 0,
    Sequences = 1,
    Props = 2,
    Templates = 4,
    Assets = 8,

    /// <summary>Attack combos (schema v5), deep copies.</summary>
    Combos = 16,
    All = Sequences | Props | Templates | Assets | Combos,
}

/// <summary>
/// Base for edits of the scene collections added in schema v4: sequences, props, templates (and embedded assets for imported
/// props). Execute works on the live lists; on ANY exception everything is restored. Undo/redo swap snapshots of only the
/// collections the edit declares in <see cref="Touches"/>: sequences and props are deep copies (serialized); templates and
/// assets are immutable once created (no command edits one in place), so their lists are shallow copies sharing the objects —
/// a snapshot never copies the template library's clips and rigs. Redo is exact even if the inputs changed since.
/// </summary>
public abstract class SceneEdit : ICommand
{
    sealed record State(byte[]? Sequences, byte[]? Props, List<ClipTemplate>? Templates, List<ProjectAsset>? Assets, byte[]? Combos);

    State? _before;
    State? _after;
    JsonNode? _data;

    public abstract string Id { get; }
    public virtual CommandEffect Effect => CommandEffect.Mutating;
    public abstract string Label { get; }

    /// <summary>What views must refresh.</summary>
    protected abstract IReadOnlyList<Change> Changes { get; }

    public long HistoryBytes => 256 + Size(_before) + Size(_after);

    static long Size(State? s) => s is null ? 0
        : (s.Sequences?.Length ?? 0) + (s.Props?.Length ?? 0) + (s.Combos?.Length ?? 0) + 8L * ((s.Templates?.Count ?? 0) + (s.Assets?.Count ?? 0));

    public CommandOutcome Execute(Project p)
    {
        if (_after is not null)
        {
            Restore(p, _after);
            return new CommandOutcome(Changes, _data?.DeepClone());
        }
        var before = Capture(p);
        var warnings = new List<Diagnostic>();
        try { _data = Apply(p, warnings); }
        catch
        {
            Restore(p, before);
            throw;
        }
        _before = before;
        _after = Capture(p);
        return new CommandOutcome(Changes, _data?.DeepClone(), warnings);
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        Restore(p, _before!);
        return Changes;
    }

    /// <summary>Change the project's sequences/props/templates/assets (only those in <see cref="Touches"/>). Validate first;
    /// throw CommandException to abort.</summary>
    protected abstract JsonNode? Apply(Project project, List<Diagnostic> warnings);

    public virtual bool TryMerge(ICommand next) => false;

    /// <summary>Lets a later edit of the same kind (e.g. dragging a prop) become part of this undo step.</summary>
    protected bool AbsorbAfterState(SceneEdit next)
    {
        if (next._after is null) return false;
        _after = next._after;
        _data = next._data;
        return true;
    }

    /// <summary>The collections this edit changes (default: all). Only these are captured and restored.</summary>
    protected virtual SceneParts Touches => SceneParts.All;

    State Capture(Project p)
    {
        var t = Touches;
        return new(
            t.HasFlag(SceneParts.Sequences) ? JsonSerializer.SerializeToUtf8Bytes(p.Sequences, ProjectArchive.JsonOptions) : null,
            t.HasFlag(SceneParts.Props) ? JsonSerializer.SerializeToUtf8Bytes(p.Props, ProjectArchive.JsonOptions) : null,
            t.HasFlag(SceneParts.Templates) ? new List<ClipTemplate>(p.Templates) : null,
            t.HasFlag(SceneParts.Assets) ? new List<ProjectAsset>(p.Assets) : null,
            t.HasFlag(SceneParts.Combos) ? JsonSerializer.SerializeToUtf8Bytes(p.Combos, ProjectArchive.JsonOptions) : null);
    }

    static void Restore(Project p, State s)
    {
        if (s.Sequences is not null) p.Sequences = JsonSerializer.Deserialize<List<ClipSequence>>(s.Sequences, ProjectArchive.JsonOptions)!;
        if (s.Props is not null) p.Props = JsonSerializer.Deserialize<List<Prop>>(s.Props, ProjectArchive.JsonOptions)!;
        if (s.Templates is not null) p.Templates = new List<ClipTemplate>(s.Templates);
        if (s.Assets is not null) p.Assets = new List<ProjectAsset>(s.Assets);
        if (s.Combos is not null) p.Combos = JsonSerializer.Deserialize<List<Combo>>(s.Combos, ProjectArchive.JsonOptions)!;
    }

    internal static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, ProjectArchive.JsonOptions), ProjectArchive.JsonOptions)!;
}
