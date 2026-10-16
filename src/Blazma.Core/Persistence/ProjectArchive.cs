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
            if (project.Id.IsEmpty)
                throw new ProjectFormatException("invalid_json", "project.json has no id.");
            return project;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or ArgumentException)
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

    /// <summary>Reference checks that JSON alone cannot express.</summary>
    static void Validate(Project p)
    {
        foreach (var rig in p.Rigs)
        {
            var seen = new HashSet<Ids.ObjectId>();
            foreach (var bone in rig.Bones)
            {
                if (!bone.Parent.IsEmpty && !seen.Contains(bone.Parent))
                    throw new ProjectFormatException("invalid_json", $"Rig '{rig.Name}': bone '{bone.Name}' appears before its parent.");
                if (!seen.Add(bone.Id))
                    throw new ProjectFormatException("invalid_json", $"Rig '{rig.Name}': duplicate bone id {bone.Id}.");
            }
        }
        foreach (var rig in p.Rigs)
            if (!rig.Source.Asset.IsEmpty && p.FindAsset(rig.Source.Asset) is null)
                throw new ProjectFormatException("invalid_json", $"Rig '{rig.Name}' refers to a missing embedded asset.");
        foreach (var c in p.Characters)
            if (p.FindRig(c.Rig) is null)
                throw new ProjectFormatException("invalid_json", $"Character '{c.Name}' refers to a missing rig.");
    }

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
}
