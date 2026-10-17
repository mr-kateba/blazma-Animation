using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Core.Model;

namespace Blazma.Export;

/// <summary>
/// Consistency checks of an export sidecar (docs/DATA_FORMAT.md §7.5, §7.9) against the clip and the export: frames inside the
/// exported range, event kinds and order, hitbox/hurtbox attachment nodes equal to the nodes the sidecar's own bone (or prop) map
/// names, active ranges sorted and non-empty, the hitboxes present exactly as the clip's hitboxes active in the exported frames.
/// The sidecar is untrusted input: only type-checked navigation, bounded loops, no recursion; problems become issues.
/// </summary>
public static class SidecarValidator
{
    /// <summary>Largest sidecar file read back (bytes).</summary>
    public const long MaxBytes = 64L * 1024 * 1024;
    const int MaxItems = 100_000;

    static readonly HashSet<string> EventKinds = Enum.GetNames<EventKind>().Select(n => n.ToLowerInvariant()).ToHashSet();
    static readonly HashSet<string> ShapeKinds = Enum.GetNames<CollisionShapeKind>().Select(n => n.ToLowerInvariant()).ToHashSet();

    public static List<string> CheckFile(string path, Project project, Clip clip, Rig rig, Core.Ids.ObjectId? character)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxBytes) return new List<string> { $"sidecar: larger than {MaxBytes / (1024 * 1024)} MB." };
            return Check(JsonNode.Parse(File.ReadAllBytes(path)), project, clip, rig, character);
        }
        catch (JsonException e) { return new List<string> { "sidecar: not valid JSON: " + e.Message }; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new List<string> { "sidecar: " + e.Message }; }
    }

    public static List<string> Check(JsonNode? root, Project project, Clip clip, Rig rig, Core.Ids.ObjectId? character)
    {
        var issues = new List<string>();
        try { Run(root, project, clip, rig, character, issues); }
        catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException or OverflowException or ArgumentException)
        {
            issues.Add("sidecar: malformed value: " + e.Message);
        }
        return issues;
    }

    static void Run(JsonNode? root, Project project, Clip clip, Rig rig, Core.Ids.ObjectId? character, List<string> issues)
    {
        if (root is not JsonObject s) { issues.Add("sidecar: not a JSON object."); return; }
        if ((string?)(s["schema"] as JsonValue) != Sidecar.Schema) issues.Add("sidecar: wrong 'schema'.");
        if (Int(s["frameCount"]) is not long frames || frames < 1) { issues.Add("sidecar: missing or invalid 'frameCount'."); return; }
        var start = Int(s["startFrame"]) ?? 0;

        var nodes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var b in Items(s["bones"], "bones", issues))
            if (b is JsonObject bo && Str(bo["name"]) is { } n && Int(bo["node"]) is { } node) nodes[n] = node;
        var propNodes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var pn in Items(s["props"], "props", issues))
            if (pn is JsonObject po && Str(po["name"]) is { } n && Int(po["node"]) is { } node) propNodes[n] = node;

        long prevFrame = -1;
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in Items(s["events"], "events", issues))
        {
            if (e is not JsonObject eo || Int(eo["frame"]) is not long f) { issues.Add("sidecar: an event has no frame."); continue; }
            if (f < 0 || f >= frames) issues.Add($"sidecar: event at frame {f} is outside the exported frames 0–{frames - 1}.");
            if (f < prevFrame) issues.Add("sidecar: events are not sorted by frame.");
            prevFrame = f;
            if (Str(eo["kind"]) is { } k && !EventKinds.Contains(k)) issues.Add($"sidecar: event kind '{k}' is unknown.");
            if (Str(eo["id"]) is { } id && !eventIds.Add(id)) issues.Add($"sidecar: duplicate event id {id}.");
        }

        var combat = s["combat"] as JsonObject;
        var expected = (clip.Combat?.Hitboxes ?? new()).Where(h => h.Active.Any(r =>
            Math.Min(frames, r.End.ToFrame(clip.Fps) - start) > Math.Max(0, r.Start.ToFrame(clip.Fps) - start))).Select(h => h.Id.ToString()).ToHashSet();
        if (combat is null)
        {
            if (expected.Count > 0) issues.Add($"sidecar: no 'combat' object but the clip has {expected.Count} hitbox(es) active in the exported frames.");
            return;
        }
        if (combat["phases"] is JsonObject ph)
        {
            long? last = null;
            foreach (var key in new[] { "startup", "active", "recovery" })
            {
                if (ph[key] is not JsonObject r || Int(r["startFrame"]) is not long a || Int(r["endFrame"]) is not long b) { issues.Add($"sidecar: phase '{key}' is missing."); continue; }
                if (a < 0 || b > frames || a > b || last is { } l && a < l) issues.Add($"sidecar: phase '{key}' range {a}–{b} is invalid.");
                last = b;
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in Items(combat["hitboxes"], "combat.hitboxes", issues))
        {
            if (h is not JsonObject ho || Str(ho["id"]) is not { } id) { issues.Add("sidecar: a hitbox has no id."); continue; }
            var name = Str(ho["name"]) ?? id;
            if (!seen.Add(id)) issues.Add($"sidecar: duplicate hitbox {name}.");
            if (!expected.Contains(id)) issues.Add($"sidecar: hitbox {name} is not a hitbox of the clip active in the exported frames.");
            CheckShape(ho["shape"], $"hitbox {name}", issues);
            long prevEnd = 0;
            var any = false;
            foreach (var r in Items(ho["active"], $"hitbox {name} active", issues))
            {
                if (r is not JsonObject ro || Int(ro["startFrame"]) is not long a || Int(ro["endFrame"]) is not long b || a < prevEnd || a >= b || b > frames)
                {
                    issues.Add($"sidecar: hitbox {name} has an invalid active range (must be sorted, non-empty, inside 0–{frames}).");
                    break;
                }
                prevEnd = b;
                any = true;
            }
            if (!any) issues.Add($"sidecar: hitbox {name} has no active range.");
            if (ho["attachment"] is not JsonObject at) { issues.Add($"sidecar: hitbox {name} has no attachment."); continue; }
            var kind = Str(at["kind"]);
            var target = Str(at["name"]);
            var node = Int(at["node"]);
            if (kind == "bone")
            {
                if (target is null || !nodes.TryGetValue(target, out var n) || node != n) issues.Add($"sidecar: hitbox {name} is attached to bone '{target}' but its node {node} is not that bone's node.");
            }
            else if (kind == "prop")
            {
                if (node is { } pn && (target is null || !propNodes.TryGetValue(target, out var exp) || exp != pn))
                    issues.Add($"sidecar: hitbox {name} is attached to prop '{target}' but its node {pn} is not that prop's node.");
            }
            else issues.Add($"sidecar: hitbox {name} has an unknown attachment kind.");
        }
        foreach (var id in expected.Where(id => !seen.Contains(id)))
            issues.Add($"sidecar: hitbox {id} of the clip is missing.");
        var hurtIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in Items(combat["hurtboxes"], "combat.hurtboxes", issues))
        {
            if (h is not JsonObject ho || Str(ho["id"]) is not { } id) { issues.Add("sidecar: a hurtbox has no id."); continue; }
            if (!hurtIds.Add(id)) issues.Add($"sidecar: duplicate hurtbox {id}.");
            var bone = Str(ho["bone"]);
            if (bone is null || !nodes.TryGetValue(bone, out var n) || Int(ho["node"]) != n) issues.Add($"sidecar: hurtbox {id} node does not match bone '{bone}'.");
            CheckShape(ho["shape"], $"hurtbox {id}", issues);
        }
        var ch = character is { } cid ? project.FindCharacter(cid) : null;
        var expectedHurt = ch?.Combat?.Hurtboxes.Count(h => rig.IndexOf(h.Bone) >= 0) ?? 0;
        if (character is not null && hurtIds.Count != expectedHurt) issues.Add($"sidecar: {hurtIds.Count} hurtboxes but the character has {expectedHurt}.");
    }

    static void CheckShape(JsonNode? n, string what, List<string> issues)
    {
        if (n is not JsonObject o || Str(o["kind"]) is not { } k || !ShapeKinds.Contains(k)) { issues.Add($"sidecar: {what} has no valid shape."); return; }
        bool Pos(JsonNode? v) => v is JsonValue jv && jv.TryGetValue<double>(out var d) && d > 0 && d <= CombatLimits.MaxShapeSize;
        var ok = k switch
        {
            "box" => o["size"] is JsonArray a && a.Count == 3 && a.All(Pos),
            _ => Pos(o["radius"]),
        };
        if (!ok) issues.Add($"sidecar: {what} has an invalid {k} size.");
    }

    static IEnumerable<JsonNode?> Items(JsonNode? n, string what, List<string> issues)
    {
        if (n is null) yield break;
        if (n is not JsonArray a) { issues.Add($"sidecar: '{what}' is not an array."); yield break; }
        if (a.Count > MaxItems) { issues.Add($"sidecar: '{what}' has more than {MaxItems:N0} entries."); yield break; }
        foreach (var x in a) yield return x;
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static long? Int(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < 9e15) return (long)d;
        return null;
    }
}
