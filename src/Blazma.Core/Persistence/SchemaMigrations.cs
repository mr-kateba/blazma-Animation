using Blazma.Core.Model;

namespace Blazma.Core.Persistence;

/// <summary>
/// Upgrades older archives step by step (n → n+1). Each step is a pure function over the
/// raw JSON and must ship with a fixture test in tests/fixtures/schema-v&lt;n&gt;/.
/// Files written by a newer build are refused rather than guessed at.
/// </summary>
public static class SchemaMigrations
{
    /// <summary>Index i migrates version i+1 to i+2. Empty while only schema 1 exists.</summary>
    static readonly Action<ArchiveDocument>[] Steps = Array.Empty<Action<ArchiveDocument>>();

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
