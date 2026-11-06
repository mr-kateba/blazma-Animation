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
    public const int CurrentSchemaVersion = 5;

    /// <summary>What opening the file repaired (schema migrations); not saved.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<string> LoadWarnings { get; } = new();

    public ObjectId Id { get; set; } = ObjectId.New();
    public string Name { get; set; } = "Untitled";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public ProjectSettings Settings { get; set; } = new();
    public List<Clip> Clips { get; set; } = new();
    public List<Rig> Rigs { get; set; } = new();
    public List<Character> Characters { get; set; } = new();
    public List<PoseAsset> PoseLibrary { get; set; } = new();

    /// <summary>Clip sequences (blending/transitions) on character tracks. Schema v4.</summary>
    public List<ClipSequence> Sequences { get; set; } = new();

    /// <summary>Animatable objects (props). Schema v4.</summary>
    public List<Prop> Props { get; set; } = new();

    /// <summary>The project's clip template library (user templates). Schema v4.</summary>
    public List<ClipTemplate> Templates { get; set; } = new();

    /// <summary>Attack combos (schema v5).</summary>
    public List<Combo> Combos { get; set; } = new();

    /// <summary>Binary files embedded in the project (imported models), so a project is self-contained.</summary>
    public List<ProjectAsset> Assets { get; set; } = new();
    public Dictionary<string, string> Metadata { get; set; } = new();

    public Rig? FindRig(ObjectId id) => Rigs.Find(r => r.Id == id);
    public Rig GetRig(ObjectId id) => FindRig(id) ?? throw new KeyNotFoundException($"Rig '{id}' does not exist.");
    public Character? FindCharacter(ObjectId id) => Characters.Find(c => c.Id == id);
    public Character GetCharacter(ObjectId id) => FindCharacter(id) ?? throw new KeyNotFoundException($"Character '{id}' does not exist.");
    public PoseAsset? FindPose(ObjectId id) => PoseLibrary.Find(p => p.Id == id);
    public ProjectAsset? FindAsset(ObjectId id) => Assets.Find(a => a.Id == id);

    public Clip? FindClip(ObjectId id) => Clips.Find(c => c.Id == id);
    public ClipSequence? FindSequence(ObjectId id) => Sequences.Find(s => s.Id == id);
    public Prop? FindProp(ObjectId id) => Props.Find(p => p.Id == id);
    public ClipTemplate? FindTemplate(ObjectId id) => Templates.Find(t => t.Id == id);
    public Combo? FindCombo(ObjectId id) => Combos.Find(c => c.Id == id);

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

/// <summary>An embedded file, stored as "assets/&lt;id&gt;/&lt;fileName&gt;" in the archive.</summary>
public sealed class ProjectAsset
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public string FileName { get; set; } = "";
    public string MediaType { get; set; } = "application/octet-stream";

    [System.Text.Json.Serialization.JsonIgnore]
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public string ArchivePath => $"assets/{Id}/{FileName}";
}
