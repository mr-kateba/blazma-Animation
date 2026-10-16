using Blazma.Core.Ids;
using Blazma.Core.Timing;

namespace Blazma.Core.Model;

/// <summary>
/// Root of a Blazma Animation project. Mutated only through commands
/// (Blazma.Commands) so that every edit is undoable and logged.
/// Not thread-safe: owned by a single thread (the Godot main thread in the app).
/// </summary>
public sealed class Project
{
    /// <summary>Schema version written by this build. See docs/DATA_FORMAT.md §4.</summary>
    public const int CurrentSchemaVersion = 1;

    public ObjectId Id { get; set; } = ObjectId.New();
    public string Name { get; set; } = "Untitled";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public ProjectSettings Settings { get; set; } = new();
    public List<Clip> Clips { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();

    public Clip? FindClip(ObjectId id) => Clips.Find(c => c.Id == id);

    public Clip GetClip(ObjectId id) =>
        FindClip(id) ?? throw new KeyNotFoundException($"Clip '{id}' does not exist.");

    public static Project CreateNew(string name, FrameRate? frameRate = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Project name is required.", nameof(name));
        return new Project
        {
            Name = name.Trim(),
            Settings = new ProjectSettings { DefaultFrameRate = frameRate ?? FrameRate.Fps30 },
        };
    }
}

public sealed class ProjectSettings
{
    public FrameRate DefaultFrameRate { get; set; } = FrameRate.Fps30;
}
