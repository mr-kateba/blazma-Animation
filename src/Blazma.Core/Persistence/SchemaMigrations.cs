using Blazma.Core.Model;

namespace Blazma.Core.Persistence;

/// <summary>
/// Upgrades older archives step by step (n → n+1). Each step is a pure function over the
/// raw JSON and must ship with a fixture test in tests/fixtures/schema-v&lt;n&gt;/.
/// Files written by a newer build are refused rather than guessed at.
/// </summary>
public static class SchemaMigrations
{
    /// <summary>Index i migrates version i+1 to i+2.</summary>
    static readonly Action<ArchiveDocument>[] Steps =
    {
        V1ToV2,
        V2ToV3,
        V3ToV4,
        V4ToV5,
    };

    /// <summary>v5 adds combat data: typed events (<c>kind</c>), clip phases and hitboxes, character hurtboxes and reaction rules,
    /// and combos (combos/&lt;id&gt;.json). v4 events become "Custom"; the combos index starts empty. Iterative (no recursion) and
    /// tolerant of odd shapes: anything that is not an event object is left for validation to refuse.</summary>
    static void V4ToV5(ArchiveDocument doc)
    {
        doc.Root["combos"] ??= new System.Text.Json.Nodes.JsonArray();
        foreach (var (path, entry) in doc.Entries)
        {
            if (!path.StartsWith("clips/", StringComparison.Ordinal) && !path.StartsWith("templates/", StringComparison.Ordinal)) continue;
            var clip = path.StartsWith("templates/", StringComparison.Ordinal) ? entry["clip"] as System.Text.Json.Nodes.JsonObject : entry;
            if (clip?["events"] is not System.Text.Json.Nodes.JsonArray events) continue;
            foreach (var e in events)
                if (e is System.Text.Json.Nodes.JsonObject o && o["kind"] is null) o["kind"] = nameof(EventKind.Custom);
        }
    }

    /// <summary>v4 adds clip sequences, props (and prop tracks, target kind "Prop") and the clip template library.
    /// v3 files have none of them: the three indexes start empty.</summary>
    static void V3ToV4(ArchiveDocument doc)
    {
        doc.Root["sequences"] ??= new System.Text.Json.Nodes.JsonArray();
        doc.Root["props"] ??= new System.Text.Json.Nodes.JsonArray();
        doc.Root["templates"] ??= new System.Text.Json.Nodes.JsonArray();
    }

    /// <summary>
    /// v3 adds clip markers and animated IK "pinned" tracks. Both are new optional data, so the
    /// JSON is unchanged; the bump exists so that older builds refuse v3 files instead of
    /// silently dropping the markers when re-saving.
    /// </summary>
    static void V2ToV3(ArchiveDocument doc)
    {
    }

    /// <summary>v2 adds rigs, the pose library and the scene (characters). v1 files have none of them.</summary>
    static void V1ToV2(ArchiveDocument doc)
    {
        doc.Root["rigs"] ??= new System.Text.Json.Nodes.JsonArray();
        doc.Root["poses"] ??= new System.Text.Json.Nodes.JsonArray();
    }

    public static void MigrateToCurrent(ArchiveDocument doc)
    {
        var version = doc.Root["schemaVersion"]?.GetValue<int>()
            ?? throw new ProjectFormatException("invalid_json", "project.json has no schemaVersion.");

        if (version > Project.CurrentSchemaVersion)
            throw new ProjectFormatException("schema_too_new",
                $"This project was saved by a newer version of Blazma Animation (schema {version}; this build supports up to {Project.CurrentSchemaVersion}).");
        if (version < 1)
            throw new ProjectFormatException("invalid_json", $"Invalid schemaVersion {version}.");

        for (var v = version; v < Project.CurrentSchemaVersion; v++)
        {
            Steps[v - 1](doc);
            doc.Root["schemaVersion"] = v + 1;
        }
    }
}
