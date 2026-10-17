using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Tests.Advanced;

namespace Blazma.Tests.Combat;

/// <summary>Shared Phase 9 setup: an attacker mannequin with the built-in attack swing, a sword hitbox, and a defender mannequin
/// with generated hurtboxes (the same steps as docs/recipes/combat_combo.jsonl).</summary>
static class C9
{
    public sealed record Scene(ProjectSession S, string Attacker, string Defender, string Attack, string Hitbox, string Sword);

    public static Scene Build()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var atk = P8.ApplyTemplate(s, "builtin.attack_swing", man);
        P8.Ok(s, "combat.set_phases", new { clipId = atk, startupStart = 0, activeStart = 10, activeEnd = 18 });
        var sword = (string)P8.Ok(s, "prop.add", new { name = "Sword", sizeX = 0.04, sizeY = 0.9, sizeZ = 0.04, characterId = man, bone = "hand_r", x = -0.08, y = 0.35 }).Data!["propId"]!;
        var hb = (string)P8.Ok(s, "hitbox.add", new { clipId = atk, name = "Blade", shape = "capsule", radius = 0.05, length = 0.8, propId = "Sword", damage = 20, knockback = 0.4 })
            .Data!["hitboxId"]!;
        var def = (string)P8.Ok(s, "scene.add_mannequin", new { name = "Defender" }).Data!["characterId"]!;
        P8.Ok(s, "hurtbox.generate", new { characterId = def });
        return new Scene(s, man, def, atk, hb, sword);
    }

    /// <summary>Serialized state the combat commands can touch (clips, characters, combos, sequences, props).</summary>
    public static string State(Project p) =>
        P8.Json(p.Clips) + P8.Json(p.Characters) + P8.Json(p.Combos) + P8.Json(p.Sequences) + P8.Json(p.Props);

    public static Project RoundTrip(Project p)
    {
        using var ms = new MemoryStream();
        ProjectArchive.Write(p, ms);
        ms.Position = 0;
        return ProjectArchive.Read(ms);
    }

    /// <summary>Writes <paramref name="p"/>, lets <paramref name="edit"/> change one raw JSON entry, fixes the checksum and reads it back.</summary>
    public static Project ReadEdited(Project p, string entryPrefix, Action<JsonObject> edit)
    {
        using var ms = new MemoryStream();
        ProjectArchive.Write(p, ms);
        var entries = new Dictionary<string, byte[]>();
        using (var zip = new ZipArchive(new MemoryStream(ms.ToArray()), ZipArchiveMode.Read))
            foreach (var e in zip.Entries)
            {
                using var s = e.Open();
                using var b = new MemoryStream();
                s.CopyTo(b);
                entries[e.FullName] = b.ToArray();
            }
        var target = entries.Keys.First(k => k.StartsWith(entryPrefix, StringComparison.Ordinal));
        var node = JsonNode.Parse(entries[target])!.AsObject();
        edit(node);
        entries[target] = System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
        var root = JsonNode.Parse(entries["project.json"])!.AsObject();
        root["files"]![target] = Convert.ToHexString(SHA256.HashData(entries[target])).ToLowerInvariant();
        entries["project.json"] = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        using var outMs = new MemoryStream();
        using (var zip = new ZipArchive(outMs, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (k, v) in entries)
            {
                using var s = zip.CreateEntry(k).Open();
                s.Write(v);
            }
        outMs.Position = 0;
        return ProjectArchive.Read(outMs);
    }

    public static ObjectId Id(string s) => ObjectId.Parse(s);
}
