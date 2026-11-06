using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Blazma.Core.Model;

namespace Blazma.Core.Persistence;

/// <summary>Raised when a .blazanim file cannot be read. <see cref="Code"/> is stable for tools.</summary>
public sealed class ProjectFormatException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// Reads and writes the .blazanim ZIP container (docs/DATA_FORMAT.md §1).
/// Layout: project.json (index + SHA-256 of every other entry) and clips/&lt;id&gt;.json.
/// </summary>
public static class ProjectArchive
{
    public const string FormatName = "blazanim";
    public const string ProjectEntry = "project.json";
    public const string SceneEntry = "scenes/main.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static void Write(Project project, Stream output)
    {
        var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var clip in project.Clips)
            entries[$"clips/{clip.Id}.json"] = JsonSerializer.SerializeToUtf8Bytes(clip, JsonOptions);
        foreach (var rig in project.Rigs)
            entries[$"rigs/{rig.Id}.json"] = JsonSerializer.SerializeToUtf8Bytes(rig, JsonOptions);
        foreach (var pose in project.PoseLibrary)
            entries[$"poses/{pose.Id}.json"] = JsonSerializer.SerializeToUtf8Bytes(pose, JsonOptions);
        foreach (var seq in project.Sequences)
            entries[$"sequences/{seq.Id}.json"] = JsonSerializer.SerializeToUtf8Bytes(seq, JsonOptions);
        foreach (var prop in project.Props)
            entries[$"props/{prop.Id}.json"] = JsonSerializer.SerializeToUtf8Bytes(prop, JsonOptions);
        foreach (var template in project.Templates)
            entries[$"templates/{template.Id}.json"] = JsonSerializer.SerializeToUtf8Bytes(template, JsonOptions);
        foreach (var combo in project.Combos)
            entries[$"combos/{combo.Id}.json"] = JsonSerializer.SerializeToUtf8Bytes(combo, JsonOptions);
        foreach (var asset in project.Assets)
            entries[asset.ArchivePath] = asset.Data;
        entries[SceneEntry] = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["characters"] = JsonSerializer.SerializeToNode(project.Characters, JsonOptions),
        }, JsonOptions);

        var files = new JsonObject();
        foreach (var (path, bytes) in entries)
            files[path] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var root = new JsonObject
        {
            ["format"] = FormatName,
            ["schemaVersion"] = Project.CurrentSchemaVersion,
            ["generator"] = "Blazma Animation",
            ["id"] = JsonSerializer.SerializeToNode(project.Id, JsonOptions),
            ["name"] = project.Name,
            ["createdUtc"] = project.CreatedUtc,
            ["settings"] = JsonSerializer.SerializeToNode(project.Settings, JsonOptions),
            ["clips"] = new JsonArray(project.Clips.Select(c => (JsonNode?)c.Id.ToString()).ToArray()),
            ["rigs"] = new JsonArray(project.Rigs.Select(r => (JsonNode?)r.Id.ToString()).ToArray()),
            ["poses"] = new JsonArray(project.PoseLibrary.Select(p => (JsonNode?)p.Id.ToString()).ToArray()),
            ["sequences"] = new JsonArray(project.Sequences.Select(x => (JsonNode?)x.Id.ToString()).ToArray()),
            ["props"] = new JsonArray(project.Props.Select(x => (JsonNode?)x.Id.ToString()).ToArray()),
            ["templates"] = new JsonArray(project.Templates.Select(x => (JsonNode?)x.Id.ToString()).ToArray()),
            ["combos"] = new JsonArray(project.Combos.Select(x => (JsonNode?)x.Id.ToString()).ToArray()),
            ["scene"] = SceneEntry,
            ["assets"] = JsonSerializer.SerializeToNode(project.Assets, JsonOptions),
            ["metadata"] = JsonSerializer.SerializeToNode(project.Metadata, JsonOptions),
            ["files"] = files,
        };

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        AddEntry(zip, ProjectEntry, JsonSerializer.SerializeToUtf8Bytes(root, JsonOptions));
        foreach (var (path, bytes) in entries)
            AddEntry(zip, path, bytes);
    }

    public static Project Read(Stream input)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException e) { throw new ProjectFormatException("not_a_project", "The file is not a valid .blazanim archive.", e); }

        using (zip)
        {
            var doc = new ArchiveDocument(
                ParseObject(ReadEntry(zip, ProjectEntry) ?? throw new ProjectFormatException("missing_entry", "project.json is missing."), ProjectEntry));

            if ((string?)doc.Root["format"] != FormatName)
                throw new ProjectFormatException("not_a_project", "project.json does not declare the blazanim format.");

            var files = doc.Root["files"] as JsonObject ?? new JsonObject();
            foreach (var (path, hashNode) in files)
            {
                var bytes = ReadEntry(zip, path) ?? throw new ProjectFormatException("missing_entry", $"'{path}' is listed in project.json but missing.");
                var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(actual, (string?)hashNode, StringComparison.OrdinalIgnoreCase))
                    throw new ProjectFormatException("integrity", $"Checksum mismatch for '{path}'. The file is damaged.");
                if (path.StartsWith("assets/", StringComparison.Ordinal)) doc.Binary[path] = bytes;
                else doc.Entries[path] = ParseObject(bytes, path);
            }

            SchemaMigrations.MigrateToCurrent(doc);
            return Materialize(doc);
        }
    }

    static Project Materialize(ArchiveDocument doc)
    {
        try
        {
            var r = doc.Root;
            var project = new Project
            {
                Id = r["id"].Deserialize<Ids.ObjectId>(JsonOptions),
                Name = (string?)r["name"] ?? "Untitled",
                CreatedUtc = r["createdUtc"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UtcNow,
                Settings = r["settings"].Deserialize<ProjectSettings>(JsonOptions) ?? new ProjectSettings(),
                Metadata = r["metadata"].Deserialize<Dictionary<string, string>>(JsonOptions) ?? new(),
            };
            project.Clips.AddRange(ReadIndexed<Clip>(doc, "clips"));
            project.Rigs.AddRange(ReadIndexed<Rig>(doc, "rigs"));
            project.PoseLibrary.AddRange(ReadIndexed<PoseAsset>(doc, "poses"));
            project.Sequences.AddRange(ReadIndexed<ClipSequence>(doc, "sequences"));
            project.Props.AddRange(ReadIndexed<Prop>(doc, "props"));
            project.Templates.AddRange(ReadIndexed<ClipTemplate>(doc, "templates"));
            project.Combos.AddRange(ReadIndexed<Combo>(doc, "combos"));
            if ((string?)r["scene"] is { } scenePath)
            {
                if (!doc.Entries.TryGetValue(scenePath, out var sceneNode))
                    throw new ProjectFormatException("missing_entry", $"Scene entry '{scenePath}' is missing.");
                project.Characters.AddRange(sceneNode["characters"].Deserialize<List<Character>>(JsonOptions) ?? new());
            }
            foreach (var asset in r["assets"].Deserialize<List<ProjectAsset>>(JsonOptions) ?? new())
            {
                if (!doc.Binary.TryGetValue(asset.ArchivePath, out var data))
                    throw new ProjectFormatException("missing_entry", $"Embedded asset '{asset.ArchivePath}' is missing.");
                asset.Data = data;
                project.Assets.Add(asset);
            }
            Validate(project);
            project.LoadWarnings.AddRange(doc.Warnings);
            if (project.Id.IsEmpty)
                throw new ProjectFormatException("invalid_json", "project.json has no id.");
            return project;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or ArgumentException or NullReferenceException or OverflowException)
        {
            throw new ProjectFormatException("invalid_json", $"Project data is invalid: {e.Message}", e);
        }
    }

    static IEnumerable<T> ReadIndexed<T>(ArchiveDocument doc, string folder)
    {
        foreach (var idNode in doc.Root[folder] as JsonArray ?? new JsonArray())
        {
            var path = $"{folder}/{(string?)idNode}.json";
            if (!doc.Entries.TryGetValue(path, out var node))
                throw new ProjectFormatException("missing_entry", $"Entry '{path}' is missing.");
            yield return node.Deserialize<T>(JsonOptions)
                ?? throw new ProjectFormatException("invalid_json", $"'{path}' is empty.");
        }
    }

    /// <summary>
    /// Reference checks that JSON alone cannot express. Every limit here matches what the commands enforce, so a project edited
    /// only through commands always loads again (ProjectFile.Save re-reads what it wrote); each error names the offending object.
    /// </summary>
    static void Validate(Project p)
    {
        foreach (var rig in p.Rigs) ValidateRig(rig, $"Rig '{rig.Name}'");
        foreach (var rig in p.Rigs)
            if (!rig.Source.Asset.IsEmpty && p.FindAsset(rig.Source.Asset) is null)
                throw Invalid($"Rig '{rig.Name}' refers to a missing embedded asset.");
        foreach (var clip in p.Clips) ValidateClip(clip, $"Clip '{clip.Name}'");
        var characterIds = new HashSet<Ids.ObjectId>();
        foreach (var c in p.Characters)
        {
            if (c is null) throw Invalid("A character entry is empty.");
            if (p.FindRig(c.Rig) is null)
                throw Invalid($"Character '{c.Name}' refers to a missing rig.");
            if (!characterIds.Add(c.Id)) throw Invalid($"Character '{c.Name}': duplicate character id {c.Id}.");
            if (c.Combat is { } cc && CombatLimits.CheckCharacterCombat(cc) is { } e)
                throw Invalid($"Character '{c.Name}' ({c.Id}): {e}.");
        }

        // Combos may refer to clips deleted later (deleting is allowed; combo.inspect/bake report them), not a load error.
        if (p.Combos.Count > CombatLimits.MaxCombos) throw Invalid($"The project has {p.Combos.Count} combos (at most {CombatLimits.MaxCombos}).");
        var comboIds = new HashSet<Ids.ObjectId>();
        foreach (var combo in p.Combos)
        {
            if (CombatLimits.CheckCombo(combo) is { } e) throw Invalid($"Combo {(combo is null ? "" : $"'{combo.Name}' ({combo.Id})")}: {e}.");
            if (!comboIds.Add(combo!.Id)) throw Invalid($"Combo '{combo.Name}': duplicate combo id {combo.Id}.");
        }

        var propIds = new HashSet<Ids.ObjectId>();
        foreach (var prop in p.Props)
        {
            if (prop is null) throw Invalid("A prop entry is empty.");
            if (!propIds.Add(prop.Id)) throw Invalid($"Prop '{prop.Name}': duplicate prop id {prop.Id}.");
            if (prop.Shape == PropShape.Mesh && p.FindAsset(prop.Asset) is null)
                throw Invalid($"Prop '{prop.Name}' ({prop.Id}) refers to a missing embedded asset.");
            // Per component (no Length(): 1e200² would overflow to infinity even though each value is finite).
            var size = prop.Size;
            if (!(size.X > 0 && size.X <= Prop.MaxSize && size.Y > 0 && size.Y <= Prop.MaxSize && size.Z > 0 && size.Z <= Prop.MaxSize))
                throw Invalid($"Prop '{prop.Name}' ({prop.Id}) has an invalid size ({size.X:G4} × {size.Y:G4} × {size.Z:G4} m; each must be > 0 and ≤ {Prop.MaxSize:N0}).");
        }

        // Sequence items and prop attachments may refer to clips/characters deleted later (deleting is allowed);
        // the evaluators skip such references, so they are not load errors.
        var seqIds = new HashSet<Ids.ObjectId>();
        foreach (var seq in p.Sequences)
        {
            if (seq is null) throw Invalid("A sequence entry is empty.");
            if (!seqIds.Add(seq.Id)) throw Invalid($"Sequence '{seq.Name}': duplicate sequence id {seq.Id}.");
            if (seq.Items is null) throw Invalid($"Sequence '{seq.Name}' has no item list.");
            if (seq.Items.Count > ClipSequence.MaxItems) throw Invalid($"Sequence '{seq.Name}' has {seq.Items.Count} items (at most {ClipSequence.MaxItems}).");
            // 'fps' is limited to 240 when given, but a sequence may take the project rate (project.set_frame_rate: up to 1000).
            if (seq.Fps.Numerator <= 0 || seq.Fps.Denominator <= 0 || !(seq.Fps.FramesPerSecond <= 1000))
                throw Invalid($"Sequence '{seq.Name}' has an invalid frame rate.");
            var itemIds = new HashSet<Ids.ObjectId>();
            foreach (var item in seq.Items)
            {
                if (item is null) throw Invalid($"Sequence '{seq.Name}' has an empty item.");
                if (!itemIds.Add(item.Id)) throw Invalid($"Sequence '{seq.Name}': duplicate item id {item.Id}.");
                if (!ClipSequence.ItemInLimits(item))
                    throw Invalid($"Sequence '{seq.Name}': item {item.Id} has invalid timing (speed {ClipSequence.MinSpeed}–{ClipSequence.MaxSpeed}, loops 1–{ClipSequence.MaxLoops}, frames 0–{ClipSequence.MaxFrame:N0}, out after in).");
            }
        }

        var templateIds = new HashSet<Ids.ObjectId>();
        foreach (var t in p.Templates)
        {
            if (t is null) throw Invalid("A template entry is empty.");
            if (!templateIds.Add(t.Id)) throw Invalid($"Template '{t.Name}': duplicate template id {t.Id}.");
            if (t.Rig is null || t.Clip is null) throw Invalid($"Template '{t.Name}' has no rig or no clip.");
            ValidateRig(t.Rig, $"Template '{t.Name}' (rig '{t.Rig.Name}')");
            if (t.Rig.Bones.Count == 0 || t.Clip.Skeleton != t.Rig.Id)
                throw Invalid($"Template '{t.Name}': its clip must animate its rig snapshot (which must have bones).");
            ValidateClip(t.Clip, $"Template '{t.Name}' (clip '{t.Clip.Name}')");
        }
    }

    /// <summary>Bones non-null with unique ids, every parent present and listed before its children (so no cycles); O(n).</summary>
    static void ValidateRig(Rig rig, string what)
    {
        if (rig.Bones is null) throw Invalid($"{what} has no bone list.");
        var seen = new HashSet<Ids.ObjectId>();
        foreach (var bone in rig.Bones)
        {
            if (bone is null) throw Invalid($"{what} has an empty bone entry.");
            if (!bone.Parent.IsEmpty && !seen.Contains(bone.Parent))
                throw Invalid($"{what}: bone '{bone.Name}' appears before its parent (or its parent is missing).");
            if (!seen.Add(bone.Id))
                throw Invalid($"{what}: duplicate bone id {bone.Id}.");
        }
    }

    static void ValidateClip(Clip clip, string what)
    {
        if (clip is null) throw Invalid("A clip entry is empty.");
        if (clip.Tracks is null) throw Invalid($"{what} has no track list.");
        // Combat data (schema v5): bounded counts, finite values; the error names the clip and the entry.
        if (clip.Events is null) throw Invalid($"{what} has no event list.");
        if (clip.Events.Count > CombatLimits.MaxEventsPerClip) throw Invalid($"{what} has {clip.Events.Count} events (at most {CombatLimits.MaxEventsPerClip}).");
        foreach (var e in clip.Events)
            if (CombatLimits.CheckEvent(e) is { } err) throw Invalid($"{what}: {err}.");
        if (clip.Combat is { } combat && CombatLimits.CheckClipCombat(combat) is { } ce) throw Invalid($"{what}: {ce}.");
        foreach (var track in clip.Tracks)
        {
            if (track?.Keys is null) throw Invalid($"{what} has an empty track.");
            for (var i = 1; i < track.Keys.Count; i++)
                if (track.Keys[i] is null || track.Keys[i - 1] is null || track.Keys[i].Time <= track.Keys[i - 1].Time)
                    throw Invalid($"{what}: keys must be in strictly increasing time order.");
        }
    }

    static ProjectFormatException Invalid(string message) => new("invalid_json", message);

    static void AddEntry(ZipArchive zip, string path, byte[] bytes)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); // reproducible archives
        using var s = entry.Open();
        s.Write(bytes);
    }

    static byte[]? ReadEntry(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null) return null;
        try
        {
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch (InvalidDataException e)
        {
            throw new ProjectFormatException("integrity", $"'{path}' is damaged.", e);
        }
    }

    static JsonObject ParseObject(byte[] bytes, string path)
    {
        try
        {
            return JsonNode.Parse(bytes) as JsonObject
                ?? throw new ProjectFormatException("invalid_json", $"'{path}' is not a JSON object.");
        }
        catch (JsonException e)
        {
            throw new ProjectFormatException("invalid_json", $"'{path}' is not valid JSON: {e.Message}", e);
        }
    }
}

/// <summary>Raw JSON view of an archive, used by schema migrations.</summary>
public sealed class ArchiveDocument(JsonObject root)
{
    public JsonObject Root { get; } = root;
    public Dictionary<string, JsonObject> Entries { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, byte[]> Binary { get; } = new(StringComparer.Ordinal);

    /// <summary>What the schema migrations repaired (shown to the user; see <see cref="Model.Project.LoadWarnings"/>).</summary>
    public List<string> Warnings { get; } = new();
}
