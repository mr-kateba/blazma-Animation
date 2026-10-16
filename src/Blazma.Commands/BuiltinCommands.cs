using System.Text.Json.Nodes;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Core.Timing;

namespace Blazma.Commands;

/// <summary>Phase 1 commands: project and clip management. Each one is covered by tests.</summary>
public static class BuiltinCommands
{
    public static void Register(CommandRegistry r)
    {
        r.Register(new("project.inspect", "Summarize the project: name, frame rate and clips.", CommandEffect.ReadOnly,
            Schema(), _ => new InspectProject()));

        r.Register(new("project.rename", "Rename the project.", CommandEffect.Mutating,
            Schema(("name", "string", "New project name.", true)), a => new RenameProject(Args.Name(a, "name"))));

        r.Register(new("project.set_frame_rate", "Set the default frame rate for new clips.", CommandEffect.Mutating,
            Schema(("numerator", "integer", "Frames per second numerator, e.g. 30 or 30000.", true),
                   ("denominator", "integer", "Denominator, default 1 (1001 for NTSC).", false)),
            a => new SetProjectFrameRate(Args.Rate(a, "numerator", "denominator"))));

        r.Register(new("clip.create", "Create an empty animation clip. Returns its clipId.", CommandEffect.Mutating,
            Schema(("name", "string", "Clip name.", true),
                   ("durationFrames", "integer", "Length in frames (default: 1 second).", false),
                   ("fpsNumerator", "integer", "Frame rate numerator (default: project frame rate).", false),
                   ("fpsDenominator", "integer", "Frame rate denominator (default 1).", false)),
            a => new CreateClip(Args.Name(a, "name"), Args.OptLong(a, "durationFrames"),
                a["fpsNumerator"] is null ? null : Args.Rate(a, "fpsNumerator", "fpsDenominator"))));

        r.Register(new("clip.rename", "Rename a clip.", CommandEffect.Mutating,
            Schema(("clipId", "string", "Clip id.", true), ("name", "string", "New name.", true)),
            a => new RenameClip(Args.Id(a, "clipId"), Args.Name(a, "name"))));

        r.Register(new("clip.set_duration", "Set a clip's length in frames.", CommandEffect.Mutating,
            Schema(("clipId", "string", "Clip id.", true), ("frames", "integer", "Length in frames (≥ 1).", true)),
            a => new SetClipDuration(Args.Id(a, "clipId"), Args.OptLong(a, "frames")!.Value)));

        r.Register(new("clip.delete", "Delete a clip (undoable).", CommandEffect.Destructive,
            Schema(("clipId", "string", "Clip id.", true)), a => new DeleteClip(Args.Id(a, "clipId"))));
    }

    static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in props)
        {
            var prop = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Type == "string") prop["minLength"] = 1;
            if (p.Type == "integer") prop["minimum"] = 1;
            properties[p.Name] = prop;
            if (p.Required) required.Add(p.Name);
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }
}

static class Args
{
    public static string Name(JsonObject a, string key)
    {
        var s = ((string?)a[key])?.Trim();
        if (string.IsNullOrEmpty(s)) throw new CommandException("invalid_args", $"'{key}' must not be empty.");
        if (s.Length > 200) throw new CommandException("invalid_args", $"'{key}' is too long (max 200 characters).");
        return s;
    }

    public static ObjectId Id(JsonObject a, string key) =>
        ObjectId.TryParse((string?)a[key], out var id) ? id : throw new CommandException("invalid_args", $"'{key}' is not a valid id.");

    public static long? OptLong(JsonObject a, string key) =>
        a[key] is JsonValue v && SchemaValidator.TryGetNumber(v, out var d) ? (long)d : null;

    public static FrameRate Rate(JsonObject a, string numKey, string denKey)
    {
        var num = OptLong(a, numKey) ?? throw new CommandException("invalid_args", $"'{numKey}' is required.");
        var den = OptLong(a, denKey) ?? 1;
        if (num > 100_000 || den > 100_000) throw new CommandException("invalid_args", "Frame rate is out of range.");
        var rate = new FrameRate((int)num, (int)den);
        if (rate.FramesPerSecond is < 1 or > 1000) throw new CommandException("invalid_args", "Frame rate must be between 1 and 1000 fps.");
        return rate;
    }
}

public sealed class InspectProject : ICommand
{
    public string Id => "project.inspect";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Inspect project";

    public CommandOutcome Execute(Project p)
    {
        var clips = new JsonArray();
        foreach (var c in p.Clips)
            clips.Add(new JsonObject
            {
                ["clipId"] = c.Id.ToString(),
                ["name"] = c.Name,
                ["fps"] = c.Fps.ToString(),
                ["durationFrames"] = c.DurationFrames,
                ["isDraft"] = c.IsDraft,
                ["trackCount"] = c.Tracks.Count,
            });
        return new CommandOutcome(Array.Empty<Change>(), new JsonObject
        {
            ["projectId"] = p.Id.ToString(),
            ["name"] = p.Name,
            ["schemaVersion"] = Project.CurrentSchemaVersion,
            ["frameRate"] = p.Settings.DefaultFrameRate.ToString(),
            ["clips"] = clips,
        });
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}

public sealed class RenameProject(string name) : ICommand
{
    string? _old;
    public string Id => "project.rename";
    public CommandEffect Effect => CommandEffect.Mutating;
    public string Label => "Rename project";

    public CommandOutcome Execute(Project p)
    {
        _old ??= p.Name;
        p.Name = name;
        return new(new[] { new Change(Change.ProjectChanged) });
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        p.Name = _old!;
        return new[] { new Change(Change.ProjectChanged) };
    }
}

public sealed class SetProjectFrameRate(FrameRate rate) : ICommand
{
    FrameRate? _old;
    public string Id => "project.set_frame_rate";
    public CommandEffect Effect => CommandEffect.Mutating;
    public string Label => "Set frame rate";

    public CommandOutcome Execute(Project p)
    {
        _old ??= p.Settings.DefaultFrameRate;
        p.Settings.DefaultFrameRate = rate;
        return new(new[] { new Change(Change.ProjectChanged) });
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        p.Settings.DefaultFrameRate = _old!.Value;
        return new[] { new Change(Change.ProjectChanged) };
    }
}

public sealed class CreateClip(string name, long? durationFrames = null, FrameRate? fps = null) : ICommand
{
    readonly ObjectId _id = ObjectId.New(); // fixed so Redo recreates the same clip
    public string Id => "clip.create";
    public CommandEffect Effect => CommandEffect.Mutating;
    public string Label => "Create clip";
    public ObjectId ClipId => _id;

    public CommandOutcome Execute(Project p)
    {
        var rate = fps ?? p.Settings.DefaultFrameRate;
        if (durationFrames is < 1 or > 10_000_000) throw new CommandException("invalid_args", "durationFrames must be between 1 and 10,000,000.");
        var duration = durationFrames is long f ? Flicks.FromFrame(f, rate) : Flicks.FromSeconds(1);
        p.Clips.Add(new Clip { Id = _id, Name = name, Fps = rate, Duration = duration });
        return new(new[] { new Change(Change.ClipAdded, _id.ToString()) }, new JsonObject { ["clipId"] = _id.ToString() });
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        p.Clips.RemoveAll(c => c.Id == _id);
        return new[] { new Change(Change.ClipRemoved, _id.ToString()) };
    }
}

public sealed class RenameClip(ObjectId clipId, string name) : ICommand
{
    string? _old;
    public string Id => "clip.rename";
    public CommandEffect Effect => CommandEffect.Mutating;
    public string Label => "Rename clip";

    public CommandOutcome Execute(Project p)
    {
        var clip = p.GetClip(clipId);
        _old ??= clip.Name;
        clip.Name = name;
        return new(new[] { new Change(Change.ClipChanged, clipId.ToString()) });
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        p.GetClip(clipId).Name = _old!;
        return new[] { new Change(Change.ClipChanged, clipId.ToString()) };
    }

    /// <summary>Typing in a name field becomes one undo step.</summary>
    public bool TryMerge(ICommand next)
    {
        if (next is not RenameClip r || r.ClipIdValue != clipId) return false;
        name = r.NewName;
        return true;
    }

    internal ObjectId ClipIdValue => clipId;
    internal string NewName => name;
}

public sealed class SetClipDuration(ObjectId clipId, long frames) : ICommand
{
    Flicks? _old;
    public string Id => "clip.set_duration";
    public CommandEffect Effect => CommandEffect.Mutating;
    public string Label => "Set clip length";

    public CommandOutcome Execute(Project p)
    {
        if (frames is < 1 or > 10_000_000) throw new CommandException("invalid_args", "frames must be between 1 and 10,000,000.");
        var clip = p.GetClip(clipId);
        _old ??= clip.Duration;
        clip.Duration = Flicks.FromFrame(frames, clip.Fps);
        return new(new[] { new Change(Change.ClipChanged, clipId.ToString()) });
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        p.GetClip(clipId).Duration = _old!.Value;
        return new[] { new Change(Change.ClipChanged, clipId.ToString()) };
    }
}

public sealed class DeleteClip(ObjectId clipId) : ICommand
{
    Clip? _removed;
    int _index;
    public string Id => "clip.delete";
    public CommandEffect Effect => CommandEffect.Destructive;
    public string Label => "Delete clip";

    public CommandOutcome Execute(Project p)
    {
        _index = p.Clips.FindIndex(c => c.Id == clipId);
        if (_index < 0) throw new CommandException("not_found", $"Clip '{clipId}' does not exist.");
        _removed = p.Clips[_index];
        p.Clips.RemoveAt(_index);
        return new(new[] { new Change(Change.ClipRemoved, clipId.ToString()) });
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        p.Clips.Insert(Math.Min(_index, p.Clips.Count), _removed!);
        return new[] { new Change(Change.ClipAdded, clipId.ToString()) };
    }
}
