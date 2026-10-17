using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Combat;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;

namespace Blazma.Commands;

/// <summary>
/// Phase 9: combat tools (docs/ARCHITECTURE.md §9). Typed events, combat phases and hitboxes are clip data (whole-clip undo with
/// <see cref="ClipEdit"/>); hurtboxes and reaction rules are character data (<see cref="CharacterCombatEdit"/>); combos are project
/// objects (<see cref="SceneEdit"/> with <see cref="SceneParts.Combos"/>). Hit tests and shape queries are read-only; reactions and
/// combo bakes add new clips (<see cref="AddClipCommand"/>). Every count and value has the archive's limits (CombatLimits).
/// </summary>
public static class CombatCommands
{
    const string KindHelp = "hit | footstep | sound | vfx | custom (default).";
    const string ShapeHelp = "sphere (default) | capsule (two sphere centres 'length' apart along local +Y) | box.";
    const string OffsetHelp = "Offset in the bone's (or prop's) frame: x/y/z metres, rx/ry/rz intrinsic Euler XYZ degrees.";
    const string RangesHelp = "Active frames as 'start-end' ranges, end exclusive like durations, comma separated (e.g. '12-17,20-23'); a single number is one frame.";
    const string FromHelp = "front | back | left | right: the side the hit comes FROM (the reaction bends away from it).";

    public static void Register(CommandRegistry r)
    {
        // ---- events --------------------------------------------------------------------------------------------------
        r.Register(new("event.add", "Add a typed event (hit, footstep, sound, vfx, custom) at a frame of a clip, with an optional flat JSON payload. Exported in the sidecar. Returns eventId.",
            CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Num("frame", "Frame (0 ≤ frame < clip length).", true), S.Str("name", "Event name.", true),
                 S.Str("kind", KindHelp, false), S.Str("payload", $"Flat JSON object of strings/numbers/booleans (≤ {CombatLimits.MaxPayloadEntries} entries).", false)),
            a => new AddEvent(S.Id(a, "clipId"), AnimationEditCommands.Frame(a, "frame"), S.Name(a, "name"), Kind(a) ?? EventKind.Custom, Payload(a))));

        r.Register(new("event.update", "Change an event's frame, name, kind and/or payload (at least one).", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("eventId", "Event id.", true), S.Num("frame", "New frame.", false), S.Str("name", "New name.", false),
                 S.Str("kind", KindHelp, false), S.Str("payload", "New payload (replaces the old one); '{}' clears it.", false)),
            a =>
            {
                if (a["frame"] is null && a["name"] is null && a["kind"] is null && a["payload"] is null)
                    throw new CommandException("invalid_args", "Give at least one of frame, name, kind, payload.");
                return new UpdateEvent(S.Id(a, "clipId"), S.Id(a, "eventId"), a["frame"] is null ? null : AnimationEditCommands.Frame(a, "frame"),
                    S.OptName(a, "name"), Kind(a), a["payload"] is null ? null : Payload(a));
            }));

        r.Register(new("event.remove", "Remove an event from a clip.", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("eventId", "Event id.", true)),
            a => new RemoveEvent(S.Id(a, "clipId"), S.Id(a, "eventId"))));

        r.Register(new("event.list", "Events of a clip (frame, name, kind, payload), optionally of one kind. Does not change anything.", CommandEffect.ReadOnly,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("kind", "Only this kind.", false)),
            a => new ListEvents(S.Id(a, "clipId"), Kind(a))));

        // ---- phases and hitboxes -------------------------------------------------------------------------------------
        r.Register(new("combat.set_phases",
            "Set a clip's attack phases: startup [startupStart, activeStart), active [activeStart, activeEnd), recovery [activeEnd, recoveryEnd) (frames, ends exclusive). " +
            "fromMarkers=true reads them from the key-pose markers (anticipation/initial → startup, main_action or contact → active, follow_through → recovery, final → end). clear=true removes them.",
            CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Int("startupStart", "Startup start (default 0).", false), S.Int("activeStart", "First active frame.", false),
                 S.Int("activeEnd", "End of the active frames (exclusive) = recovery start.", false), S.Int("recoveryEnd", "End of recovery (exclusive; default clip length).", false),
                 S.Bool("fromMarkers", "Derive from the phase markers.", false), S.Bool("clear", "Remove the phases.", false)),
            a => new SetPhases(S.Id(a, "clipId"), Long(a, "startupStart"), Long(a, "activeStart"), Long(a, "activeEnd"), Long(a, "recoveryEnd"),
                (bool?)a["fromMarkers"] ?? false, (bool?)a["clear"] ?? false)));

        r.Register(new("hitbox.add",
            "Add a hitbox to an attack clip: a sphere/capsule/box attached to a bone of the clip's rig or to a prop, active only on the given frames " +
            "(default: the clip's active phase). Returns hitboxId.",
            CommandEffect.Mutating,
            S.Of(new[] { S.Str("clipId", "Clip id.", true), S.Str("name", "Hitbox name.", true), S.Str("shape", ShapeHelp, false),
                S.Num("radius", "Sphere/capsule radius (m, default 0.1).", false), S.Num("length", "Capsule length between the sphere centres (m).", false),
                S.Num("sizeX", "Box extent X (m).", false), S.Num("sizeY", "Box extent Y (m).", false), S.Num("sizeZ", "Box extent Z (m).", false),
                S.Str("bone", "Attach to this bone of the clip's rig (id or name)…", false), S.Str("propId", "…or to this prop (id or name).", false),
                S.Str("activeFrames", RangesHelp, false), S.Num("damage", "Damage (default 10).", false), S.Num("knockback", "Knockback distance (m, default 0.3).", false),
                S.Int("hitstopFrames", "Hit-stop frames (default 0).", false), S.Str("tags", "Comma-separated tags.", false) }.Concat(OffsetArgs()).ToArray()),
            a => new AddHitbox(S.Id(a, "clipId"), a)));

        r.Register(new("hitbox.update", "Change a hitbox (only the given fields): shape and size, attachment (bone or propId), offset, active frames, damage, knockback, hit-stop, tags, name.",
            CommandEffect.Mutating,
            S.Of(new[] { S.Str("clipId", "Clip id.", true), S.Str("hitboxId", "Hitbox id.", true), S.Str("name", "New name.", false), S.Str("shape", ShapeHelp, false),
                S.Num("radius", "Radius (m).", false), S.Num("length", "Capsule length (m).", false),
                S.Num("sizeX", "Box extent X (m).", false), S.Num("sizeY", "Box extent Y (m).", false), S.Num("sizeZ", "Box extent Z (m).", false),
                S.Str("bone", "Attach to this bone…", false), S.Str("propId", "…or to this prop.", false), S.Str("activeFrames", RangesHelp, false),
                S.Num("damage", "Damage.", false), S.Num("knockback", "Knockback (m).", false), S.Int("hitstopFrames", "Hit-stop frames.", false),
                S.Str("tags", "Comma-separated tags ('' clears).", false) }.Concat(OffsetArgs()).ToArray()),
            a => new UpdateHitbox(S.Id(a, "clipId"), S.Id(a, "hitboxId"), a)));

        r.Register(new("hitbox.remove", "Remove a hitbox from a clip.", CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("hitboxId", "Hitbox id.", true)),
            a => new RemoveHitbox(S.Id(a, "clipId"), S.Id(a, "hitboxId"))));

        r.Register(new("hitbox.list", "A clip's phases and hitboxes (shape, attachment, offset, active frames, damage…). Does not change anything.", CommandEffect.ReadOnly,
            S.Of(S.Str("clipId", "Clip id.", true)),
            a => new ListHitboxes(S.Id(a, "clipId"))));

        // ---- hurtboxes and reactions ---------------------------------------------------------------------------------
        r.Register(new("hurtbox.generate",
            "Generate hurtboxes for a character from its rig: one capsule per body segment, radius from humanoid proportions (unmapped bones: 20% of the segment). " +
            "Replaces the existing hurtboxes unless replace=false. Returns the hurtboxes.",
            CommandEffect.Mutating,
            S.Of(S.Str("characterId", "Character id.", true), S.Num("radiusScale", "Multiply every radius (0.25–4, default 1).", false),
                 S.Bool("replace", "Replace existing hurtboxes (default true).", false)),
            a => new GenerateHurtboxes(S.Id(a, "characterId"), S.OptNumber(a, "radiusScale") ?? 1, (bool?)a["replace"] ?? true)));

        r.Register(new("hurtbox.update", "Change a hurtbox (only the given fields): shape and size, bone, offset, zone (high|mid|low), enabled, name.", CommandEffect.Mutating,
            S.Of(new[] { S.Str("characterId", "Character id.", true), S.Str("hurtboxId", "Hurtbox id.", true), S.Str("name", "New name.", false), S.Str("shape", ShapeHelp, false),
                S.Num("radius", "Radius (m).", false), S.Num("length", "Capsule length (m).", false),
                S.Num("sizeX", "Box extent X (m).", false), S.Num("sizeY", "Box extent Y (m).", false), S.Num("sizeZ", "Box extent Z (m).", false),
                S.Str("bone", "Bone (id or name).", false), S.Str("zone", "high | mid | low.", false), S.Bool("enabled", "Disabled hurtboxes are ignored by hit tests.", false) }
                .Concat(OffsetArgs()).ToArray()),
            a => new UpdateHurtbox(S.Id(a, "characterId"), S.Id(a, "hurtboxId"), a)));

        r.Register(new("hurtbox.remove", "Remove one hurtbox, or all of a character's hurtboxes when hurtboxId is omitted.", CommandEffect.Mutating,
            S.Of(S.Str("characterId", "Character id.", true), S.Str("hurtboxId", "Hurtbox id; omit for all.", false)),
            a => new RemoveHurtbox(S.Id(a, "characterId"), S.OptId(a, "hurtboxId"))));

        r.Register(new("hurtbox.list", "A character's hurtboxes (bone, shape, offset, zone) and reaction rules. Does not change anything.", CommandEffect.ReadOnly,
            S.Of(S.Str("characterId", "Character id.", true)),
            a => new ListHurtboxes(S.Id(a, "characterId"))));

        r.Register(new("combat.shapes",
            "World-space shapes for drawing: the hurtboxes of a character and (with clipId) the clip's hitboxes, at a (fractional) frame of the clip or in the working pose, " +
            "each with 'active'. Optional placement (x/z/facingDegrees). Does not change anything.",
            CommandEffect.ReadOnly,
            S.Of(S.Str("characterId", "Character id.", true), S.Str("clipId", "Clip played on the character (optional).", false), S.Num("frame", "Frame (default 0).", false),
                 S.Num("x", "Placement X (m).", false), S.Num("z", "Placement Z (m).", false), S.Num("facingDegrees", "Placement yaw (degrees).", false)),
            a => new CombatShapes(S.Id(a, "characterId"), S.OptId(a, "clipId"), S.OptNumber(a, "frame") ?? 0, S.OptNumber(a, "x") ?? 0, S.OptNumber(a, "z") ?? 0,
                S.OptNumber(a, "facingDegrees") ?? 0)));

        r.Register(new("reaction.set_rule",
            "Add or replace (ruleId) a hit-reaction rule of a character: which reaction (a clip, or procedural when clipId is omitted) a hit from a direction/height/strength range plays. " +
            "Returns ruleId.",
            CommandEffect.Mutating,
            S.Of(S.Str("characterId", "Character id.", true), S.Str("ruleId", "Rule to replace (omit to add).", false), S.Str("name", "Rule name.", true),
                 S.Str("direction", "any (default) | front | back | left | right.", false), S.Str("height", "any (default) | high | mid | low.", false),
                 S.Num("minStrength", "0–1 (default 0).", false), S.Num("maxStrength", "0–1 (default 1).", false),
                 S.Str("clipId", "Reaction clip (omit for procedural).", false), S.Num("durationSeconds", "Procedural reaction length (default 0.6).", false)),
            a => new SetReactionRule(S.Id(a, "characterId"), S.OptId(a, "ruleId"), a)));

        r.Register(new("reaction.remove_rule", "Remove a hit-reaction rule.", CommandEffect.Mutating,
            S.Of(S.Str("characterId", "Character id.", true), S.Str("ruleId", "Rule id.", true)),
            a => new RemoveReactionRule(S.Id(a, "characterId"), S.Id(a, "ruleId"))));

        r.Register(new("reaction.generate",
            "Generate a procedural hit reaction as a NEW clip (undoable): the spine and head flinch away from the hit with a damped spring and recover to the base pose; " +
            "optional knockback moves the root. Give 'direction' (side the hit comes from) or directionX/directionZ (push direction in the character's space), " +
            "or pass the 'reactionArgs' of combat.hit_test. Base pose: a clip frame (baseClipId/baseFrame) or the working pose. Returns clipId.",
            CommandEffect.Mutating,
            S.Of(S.Str("characterId", "Character id.", true), S.Str("direction", FromHelp, false),
                 S.Num("directionX", "Push direction X (character space, +X = its left).", false), S.Num("directionZ", "Push direction Z (+Z = its forward).", false),
                 S.Str("height", "high | mid (default) | low.", false), S.Num("strength", "0–1 (default 0.5).", false), S.Num("knockback", "Root travel (m, default 0).", false),
                 S.Num("durationSeconds", $"Length ({HitReaction.MinSeconds}–{HitReaction.MaxSeconds} s, default 0.6).", false),
                 S.Str("baseClipId", "Clip whose pose to start from.", false), S.Int("baseFrame", "Frame of baseClipId (default its last frame).", false),
                 S.Str("name", "Clip name (default 'Hit reaction').", false)),
            a => new GenerateReaction(S.Id(a, "characterId"), a)));

        // ---- combos ------------------------------------------------------------------------------------------------
        r.Register(new("combo.create", "Create an empty attack combo for a character. Returns comboId.", CommandEffect.Mutating,
            S.Of(S.Str("characterId", "Character id.", true), S.Str("name", "Combo name.", true)),
            a => new CreateCombo(S.Id(a, "characterId"), S.Name(a, "name"))));

        r.Register(new("combo.add_attack",
            "Append (or insert at 'index') an attack clip to a combo, with its input and cancel window: [cancelStartFrame, cancelEndFrame) of this clip is when the NEXT " +
            "attack may start; linkFrame (default: window start) is where it starts when baked; blendFrames is this attack's fade-in. Returns attackId.",
            CommandEffect.Mutating,
            S.Of(S.Str("comboId", "Combo id.", true), S.Str("clipId", "Attack clip (animates the combo's character).", true), S.Str("input", "Input label, e.g. 'X' or 'light'.", false),
                 S.Int("cancelStartFrame", "Cancel window start.", false), S.Int("cancelEndFrame", "Cancel window end (exclusive).", false),
                 S.Int("linkFrame", "Frame where the next attack starts when baked.", false), S.Int("blendFrames", "Fade-in frames (default 3).", false),
                 S.Int("index", "Insert position (default: end).", false)),
            a => new AddComboAttack(S.Id(a, "comboId"), S.Id(a, "clipId"), a)));

        r.Register(new("combo.set_cancel_window", "Change an attack's cancel window, link frame, blend and/or input. clear=true removes the cancel window and link.",
            CommandEffect.Mutating,
            S.Of(S.Str("comboId", "Combo id.", true), S.Str("attackId", "Attack id.", true), S.Int("cancelStartFrame", "Window start.", false),
                 S.Int("cancelEndFrame", "Window end (exclusive).", false), S.Int("linkFrame", "Link frame.", false), S.Int("blendFrames", "Fade-in frames.", false),
                 S.Str("input", "Input label.", false), S.Bool("clear", "Remove the window and link.", false)),
            a => new UpdateComboAttack(S.Id(a, "comboId"), S.Id(a, "attackId"), a)));

        r.Register(new("combo.remove", "Remove an attack from a combo, or the whole combo when attackId is omitted (clips are not deleted).", CommandEffect.Destructive,
            S.Of(S.Str("comboId", "Combo id.", true), S.Str("attackId", "Attack id; omit to remove the combo.", false)),
            a => new RemoveCombo(S.Id(a, "comboId"), S.OptId(a, "attackId"))));

        r.Register(new("combo.inspect", "A combo's attacks on the combined timeline: start frame, cancel window, link, blend, input, hitbox count, total length. Does not change anything.",
            CommandEffect.ReadOnly,
            S.Of(S.Str("comboId", "Combo id.", true)),
            a => new InspectCombo(S.Id(a, "comboId"))));

        r.Register(new("combo.bake",
            "Bake a combo with the sequence machinery: target 'clip' (default) creates a NEW normal clip (blended, root-aligned, with the attacks' events and hitboxes cut at each cancel); " +
            "target 'sequence' creates a clip sequence you can edit further. Undoable. Returns clipId or sequenceId.",
            CommandEffect.Mutating,
            S.Of(S.Str("comboId", "Combo id.", true), S.Str("target", "clip (default) | sequence.", false), S.Str("name", "Name (default: the combo name).", false),
                 S.Bool("reduceKeys", "Key reduction (clip, default true).", false), S.Bool("footLock", "Lock foot contacts (clip, default false).", false)),
            a => ((string?)a["target"])?.Trim().ToLowerInvariant() switch
            {
                null or "clip" => new BakeCombo(S.Id(a, "comboId"), S.OptName(a, "name"), (bool?)a["reduceKeys"] ?? true, (bool?)a["footLock"] ?? false),
                "sequence" => new BakeComboToSequence(S.Id(a, "comboId"), S.OptName(a, "name")),
                _ => throw new CommandException("invalid_args", "'target' must be clip or sequence."),
            }));

        // ---- hit test -------------------------------------------------------------------------------------------------
        r.Register(new("combat.hit_test",
            "Test an attack clip's hitboxes against a defender's hurtboxes frame by frame, with adaptive swept sub-steps (no tunnelling at fast swings). " +
            "The attacker stands at the origin facing +Z; the defender at defenderX/Y/Z facing defenderFacing degrees (default 1 m in front, facing the attacker), " +
            "playing defenderClipId or its working pose. Reports each hitbox's first contact {frame, time, hitbox, hurtbox, bone, point, normal, direction, from, zone} " +
            "and 'reactionArgs' for reaction.generate. Does not change anything.",
            CommandEffect.ReadOnly,
            S.Of(S.Str("attackerCharacterId", "Attacker character.", true), S.Str("attackClipId", "Attack clip.", true), S.Str("defenderCharacterId", "Defender character.", true),
                 S.Str("defenderClipId", "Clip the defender plays (default: its working pose).", false),
                 S.Num("defenderX", "Defender X (m, default 0).", false), S.Num("defenderY", "Defender Y (m, default 0).", false), S.Num("defenderZ", "Defender Z (m, default 1).", false),
                 S.Num("defenderFacing", "Defender yaw in degrees (default 180 = facing the attacker).", false),
                 S.Int("startFrame", "First frame (default 0).", false), S.Int("endFrame", "Last frame (default: clip end).", false),
                 S.Int("maxSubsteps", $"Most sub-steps between frames (1–{HitDetector.MaxSubstepsLimit}, default 8).", false, 1), S.Str("hitboxId", "Only this hitbox.", false)),
            a => new HitTest(a)));
    }

    // ---- argument helpers ------------------------------------------------------------------------------------------

    internal static IEnumerable<(string, JsonObject, bool)> OffsetArgs() => new[]
    {
        S.Num("x", "Offset X (m).", false), S.Num("y", "Offset Y (m).", false), S.Num("z", "Offset Z (m).", false),
        S.Num("rx", "Offset rotation X (deg).", false), S.Num("ry", "Offset rotation Y (deg).", false), S.Num("rz", "Offset rotation Z (deg).", false),
    };

    internal static long? Long(JsonObject a, string key)
    {
        var v = Args.OptLong(a, key);
        return v is < 0 or > 10_000_000 ? throw new CommandException("invalid_args", $"'{key}' must be between 0 and 10,000,000.") : v;
    }

    static EventKind? Kind(JsonObject a) => ((string?)a["kind"])?.Trim().ToLowerInvariant() switch
    {
        null => null,
        "custom" => EventKind.Custom,
        "hit" => EventKind.Hit,
        "footstep" => EventKind.Footstep,
        "sound" => EventKind.Sound,
        "vfx" => EventKind.Vfx,
        _ => throw new CommandException("invalid_args", "'kind' must be " + KindHelp),
    };

    internal static string KindName(EventKind k) => k.ToString().ToLowerInvariant();

    /// <summary>A flat JSON object of strings/numbers/booleans → payload; size and entry limits as in the archive.</summary>
    static Dictionary<string, string> Payload(JsonObject a)
    {
        var text = (string?)a["payload"];
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return data;
        if (text.Length > 16_384) throw new CommandException("too_large", "'payload' is longer than 16 KB.");
        JsonObject obj;
        try { obj = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 4 }) as JsonObject ?? throw new CommandException("invalid_args", "'payload' must be a JSON object."); }
        catch (JsonException e) { throw new CommandException("invalid_args", $"'payload' is not valid JSON: {e.Message}"); }
        if (obj.Count > CombatLimits.MaxPayloadEntries) throw new CommandException("too_large", $"'payload' has {obj.Count} entries (at most {CombatLimits.MaxPayloadEntries}).");
        foreach (var (k, v) in obj)
        {
            if (v is not JsonValue jv) throw new CommandException("invalid_args", $"payload '{k}' must be a string, number or boolean (no nested objects).");
            data[k] = jv.TryGetValue<string>(out var s) ? s
                : jv.TryGetValue<bool>(out var b) ? (b ? "true" : "false")
                : SchemaValidator.TryGetNumber(jv, out var d) && double.IsFinite(d) ? d.ToString("R", CultureInfo.InvariantCulture)
                : throw new CommandException("invalid_args", $"payload '{k}' must be a string, number or boolean.");
        }
        if (CombatLimits.CheckPayload(data, "'payload'") is { } err) throw new CommandException("invalid_args", err + ".");
        return data;
    }

    internal static JsonObject PayloadJson(Dictionary<string, string> data) =>
        new(data.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));

    /// <summary>Applies shape arguments over <paramref name="current"/> (a new sphere when null) and checks the limits.</summary>
    internal static CollisionShape Shape(JsonObject a, CollisionShape? current)
    {
        var s = current?.Clone() ?? new CollisionShape();
        if ((string?)a["shape"] is { } kind)
            s.Kind = kind.Trim().ToLowerInvariant() switch
            {
                "sphere" => CollisionShapeKind.Sphere,
                "capsule" => CollisionShapeKind.Capsule,
                "box" => CollisionShapeKind.Box,
                _ => throw new CommandException("invalid_args", "'shape' must be " + ShapeHelp),
            };
        if (S.OptNumber(a, "radius") is { } r) s.Radius = r;
        if (S.OptNumber(a, "length") is { } l) s.Length = l;
        if (a["sizeX"] is not null || a["sizeY"] is not null || a["sizeZ"] is not null)
            s.Size = new Vec3(S.OptNumber(a, "sizeX") ?? s.Size.X, S.OptNumber(a, "sizeY") ?? s.Size.Y, S.OptNumber(a, "sizeZ") ?? s.Size.Z);
        if (CombatLimits.CheckShape(s) is { } e) throw new CommandException("invalid_args", "The shape is invalid: " + e + ".");
        return s;
    }

    internal static Transform Offset(JsonObject a, Transform current)
    {
        var t = PropCommands.TransformFrom(a, current) with { Scale = Vec3.One };
        if (CombatLimits.CheckOffset(t) is { } e) throw new CommandException("invalid_args", "The offset is invalid: " + e + ".");
        return t;
    }

    /// <summary>"12-17,20-23" (end exclusive) or "12" (one frame) → sorted, non-overlapping ranges inside the clip.</summary>
    internal static List<ClipRange> Ranges(string text, Clip clip)
    {
        var list = new List<(long S, long E)>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (list.Count >= CombatLimits.MaxActiveRanges) throw new CommandException("too_large", $"At most {CombatLimits.MaxActiveRanges} active ranges.");
            var bits = part.Split('-', StringSplitOptions.TrimEntries);
            if (bits.Length is < 1 or > 2 || !long.TryParse(bits[0], NumberStyles.None, CultureInfo.InvariantCulture, out var s)
                || bits.Length == 2 && !long.TryParse(bits[1], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw new CommandException("invalid_args", $"'{part}' is not a frame range (use 'start-end', end exclusive).");
            var e = bits.Length == 2 ? long.Parse(bits[1], CultureInfo.InvariantCulture) : s + 1;
            if (e <= s || e > clip.DurationFrames)
                throw new CommandException("invalid_args", $"Range '{part}' must satisfy start < end ≤ clip length ({clip.DurationFrames}).");
            list.Add((s, e));
        }
        if (list.Count == 0) throw new CommandException("invalid_args", "'activeFrames' is empty.");
        list.Sort();
        for (var i = 1; i < list.Count; i++)
            if (list[i].S < list[i - 1].E) throw new CommandException("invalid_args", "Active ranges must not overlap.");
        return list.Select(x => new ClipRange { Start = Flicks.FromFrame(x.S, clip.Fps), End = Flicks.FromFrame(x.E, clip.Fps) }).ToList();
    }

    internal static List<string> Tags(string text)
    {
        var tags = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
        if (tags.Count > CombatLimits.MaxTags || tags.Any(t => t.Length > CombatLimits.MaxTagLength))
            throw new CommandException("invalid_args", $"At most {CombatLimits.MaxTags} tags of at most {CombatLimits.MaxTagLength} characters.");
        return tags;
    }

    internal static double Bounded(JsonObject a, string key, double min, double max, double fallback)
    {
        var v = S.OptNumber(a, key) ?? fallback;
        return v >= min && v <= max ? v : throw new CommandException("invalid_args", $"'{key}' must be between {min} and {max}.");
    }

    internal static Rig RigOfClip(Project p, Clip clip) =>
        (clip.Skeleton.IsEmpty ? null : p.FindRig(clip.Skeleton)) ?? throw new CommandException("no_rig", $"Clip '{clip.Name}' does not animate a rig yet (key a character first).");

    internal static Combo RequireCombo(Project p, ObjectId id) =>
        p.FindCombo(id) ?? throw new CommandException("not_found", $"Combo '{id}' does not exist.");

    internal static ClipCombat CombatOf(Clip clip) => clip.Combat ??= new ClipCombat();

    /// <summary>Drops empty combat data so clips without any stay as they were.</summary>
    internal static void Tidy(Clip clip)
    {
        if (clip.Combat is { Phases: null, Hitboxes.Count: 0 }) clip.Combat = null;
    }

    internal static JsonArray V(Vec3 v) => new(Math.Round(v.X, 6) + 0.0, Math.Round(v.Y, 6) + 0.0, Math.Round(v.Z, 6) + 0.0);
    internal static JsonArray Q(Quat q) => new(Math.Round(q.X, 7) + 0.0, Math.Round(q.Y, 7) + 0.0, Math.Round(q.Z, 7) + 0.0, Math.Round(q.W, 7) + 0.0);

    internal static JsonObject ShapeJson(CollisionShape s) => new()
    {
        ["kind"] = s.Kind.ToString().ToLowerInvariant(),
        ["radius"] = s.Kind == CollisionShapeKind.Box ? null : s.Radius,
        ["length"] = s.Kind == CollisionShapeKind.Capsule ? s.Length : null,
        ["size"] = s.Kind == CollisionShapeKind.Box ? V(s.Size) : null,
    };

    internal static JsonObject OffsetJson(Transform t) => new() { ["translation"] = V(t.Translation), ["rotation"] = Q(t.Rotation) };

    /// <summary>A placed shape for drawing: kind, segment a–b (sphere: a = b = centre), radius, box centre/rotation/half extents.</summary>
    internal static JsonObject WorldJson(WorldShape w) => new()
    {
        ["kind"] = w.Kind.ToString().ToLowerInvariant(),
        ["a"] = V(w.A),
        ["b"] = V(w.B),
        ["center"] = V(w.Center),
        ["radius"] = w.IsBox ? null : Math.Round(w.Radius, 6),
        ["rotation"] = Q(w.Rotation),
        ["halfExtents"] = w.IsBox ? V(w.Half) : null,
    };

    internal static JsonArray RangesJson(Hitbox h, FrameRate fps) =>
        new(CombatEvaluator.ActiveFrames(h, fps).Select(r => (JsonNode?)new JsonObject { ["startFrame"] = r.Start, ["endFrame"] = r.End }).ToArray());

    internal static JsonObject HitboxJson(Project p, Clip clip, Hitbox h)
    {
        var rig = clip.Skeleton.IsEmpty ? null : p.FindRig(clip.Skeleton);
        var target = h.Attachment.Kind == HitboxAttachKind.Bone ? rig?.FindBone(h.Attachment.Target)?.Name : p.FindProp(h.Attachment.Target)?.Name;
        return new JsonObject
        {
            ["hitboxId"] = h.Id.ToString(),
            ["name"] = h.Name,
            ["shape"] = ShapeJson(h.Shape),
            ["attachment"] = new JsonObject
            {
                ["kind"] = h.Attachment.Kind.ToString().ToLowerInvariant(),
                ["id"] = h.Attachment.Target.ToString(),
                ["name"] = target,
                ["missing"] = target is null,
                ["offset"] = OffsetJson(h.Attachment.Offset),
            },
            ["active"] = RangesJson(h, clip.Fps),
            ["damage"] = h.Damage,
            ["knockback"] = h.Knockback,
            ["hitstopFrames"] = h.HitstopFrames,
            ["tags"] = new JsonArray(h.Tags.Select(t => (JsonNode?)t).ToArray()),
        };
    }

    internal static JsonObject? PhasesJson(Clip clip)
    {
        if (clip.Combat?.Phases is not { } p) return null;
        JsonObject Span(Flicks s, Flicks e) => new() { ["startFrame"] = s.ToFrame(clip.Fps), ["endFrame"] = e.ToFrame(clip.Fps) };
        return new JsonObject
        {
            ["startup"] = Span(p.StartupStart, p.ActiveStart),
            ["active"] = Span(p.ActiveStart, p.RecoveryStart),
            ["recovery"] = Span(p.RecoveryStart, p.End),
        };
    }

    internal static JsonObject HurtboxJson(Rig? rig, Hurtbox h) => new()
    {
        ["hurtboxId"] = h.Id.ToString(),
        ["name"] = h.Name,
        ["bone"] = rig?.FindBone(h.Bone)?.Name,
        ["boneId"] = h.Bone.ToString(),
        ["shape"] = ShapeJson(h.Shape),
        ["offset"] = OffsetJson(h.Offset),
        ["zone"] = h.Zone.ToString().ToLowerInvariant(),
        ["enabled"] = h.Enabled,
    };

    internal static JsonObject RuleJson(Project p, ReactionRule r) => new()
    {
        ["ruleId"] = r.Id.ToString(),
        ["name"] = r.Name,
        ["direction"] = r.Direction.ToString().ToLowerInvariant(),
        ["height"] = r.Height.ToString().ToLowerInvariant(),
        ["minStrength"] = r.MinStrength,
        ["maxStrength"] = r.MaxStrength,
        ["clipId"] = r.Clip.IsEmpty ? null : r.Clip.ToString(),
        ["clip"] = r.Clip.IsEmpty ? null : p.FindClip(r.Clip)?.Name,
        ["procedural"] = r.Clip.IsEmpty,
        ["durationSeconds"] = r.DurationSeconds,
    };

    internal static T Enum<T>(JsonObject a, string key, T fallback) where T : struct, System.Enum
    {
        var s = ((string?)a[key])?.Trim();
        if (s is null) return fallback;
        return System.Enum.TryParse<T>(s, ignoreCase: true, out var v) && System.Enum.IsDefined(v) && !int.TryParse(s, out _) ? v
            : throw new CommandException("invalid_args", $"'{key}' must be one of: {string.Join(", ", System.Enum.GetNames<T>().Select(n => n.ToLowerInvariant()))}.");
    }

    internal static CommandException Map(CombatException e) => new(e.Code, e.Message);

    internal static Change ClipChanged(ObjectId id) => new(Change.ClipChanged, id.ToString());
    internal static Change ComboChanged(ObjectId id) => new(Change.ComboChanged, id.ToString());
}

// ---- events ------------------------------------------------------------------------------------------------------------

public sealed class AddEvent(ObjectId clipId, long frame, string name, EventKind kind, Dictionary<string, string> payload) : ClipEdit(clipId)
{
    readonly ObjectId _eventId = ObjectId.New();
    public override string Id => "event.add";
    public override string Label => "Add event";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        AnimationEditCommands.CheckFrameInClip(clip, frame);
        if (clip.Events.Count >= CombatLimits.MaxEventsPerClip)
            throw new CommandException("too_large", $"A clip holds at most {CombatLimits.MaxEventsPerClip} events.");
        clip.Events.Add(new AnimationEvent { Id = _eventId, Time = Flicks.FromFrame(frame, clip.Fps), Name = name, Kind = kind, Data = new Dictionary<string, string>(payload) });
        clip.Events = clip.Events.OrderBy(e => e.Time.Value).ToList();
        return new JsonObject { ["eventId"] = _eventId.ToString(), ["frame"] = frame };
    }
}

public sealed class UpdateEvent(ObjectId clipId, ObjectId eventId, long? frame, string? name, EventKind? kind, Dictionary<string, string>? payload) : ClipEdit(clipId)
{
    public override string Id => "event.update";
    public override string Label => "Edit event";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        var e = clip.Events.Find(x => x.Id == eventId) ?? throw new CommandException("not_found", $"Event '{eventId}' does not exist in clip '{clip.Name}'.");
        if (frame is { } f)
        {
            AnimationEditCommands.CheckFrameInClip(clip, f);
            e.Time = Flicks.FromFrame(f, clip.Fps);
        }
        if (name is not null) e.Name = name;
        if (kind is { } k) e.Kind = k;
        if (payload is not null) e.Data = new Dictionary<string, string>(payload);
        clip.Events = clip.Events.OrderBy(x => x.Time.Value).ToList();
        return new JsonObject { ["eventId"] = eventId.ToString() };
    }
}

public sealed class RemoveEvent(ObjectId clipId, ObjectId eventId) : ClipEdit(clipId)
{
    public override string Id => "event.remove";
    public override string Label => "Remove event";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        if (clip.Events.RemoveAll(e => e.Id == eventId) == 0) throw new CommandException("not_found", $"Event '{eventId}' does not exist in clip '{clip.Name}'.");
        return null;
    }
}

public sealed class ListEvents(ObjectId clipId, EventKind? kind) : ICommand
{
    public string Id => "event.list";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "List events";

    public CommandOutcome Execute(Project p)
    {
        var clip = AnimationEditCommands.RequireClip(p, clipId);
        return new(Array.Empty<Change>(), new JsonObject
        {
            ["clipId"] = clip.Id.ToString(),
            ["events"] = new JsonArray(clip.Events.Where(e => kind is null || e.Kind == kind).Select(e => (JsonNode?)new JsonObject
            {
                ["eventId"] = e.Id.ToString(),
                ["frame"] = e.Time.ToFrame(clip.Fps),
                ["name"] = e.Name,
                ["kind"] = CombatCommands.KindName(e.Kind),
                ["payload"] = CombatCommands.PayloadJson(e.Data),
            }).ToArray()),
        });
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}

// ---- phases and hitboxes -------------------------------------------------------------------------------------------------

public sealed class SetPhases(ObjectId clipId, long? startupStart, long? activeStart, long? activeEnd, long? recoveryEnd, bool fromMarkers, bool clear) : ClipEdit(clipId)
{
    public override string Id => "combat.set_phases";
    public override string Label => "Set attack phases";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        if (clear)
        {
            if (clip.Combat is { } c) c.Phases = null;
            CombatCommands.Tidy(clip);
            return new JsonObject { ["phases"] = null };
        }
        var length = clip.DurationFrames;
        long? M(params KeyPhase[] phases)
        {
            foreach (var ph in phases)
                if (clip.Markers.Find(m => m.Phase == ph) is { } m) return m.Time.ToFrame(clip.Fps);
            return null;
        }
        long s0, a0, a1, e;
        if (fromMarkers)
        {
            var act = M(KeyPhase.MainAction, KeyPhase.Contact);
            var rec = M(KeyPhase.FollowThrough, KeyPhase.Recovery);
            if (activeStart is null && act is null || activeEnd is null && rec is null)
                throw new CommandException("missing_markers", "fromMarkers needs a main_action (or contact) marker and a follow_through (or recovery) marker; add them with clip.add_phase_markers or marker.add.");
            s0 = startupStart ?? M(KeyPhase.Anticipation, KeyPhase.Initial) ?? 0;
            a0 = activeStart ?? act!.Value;
            a1 = activeEnd ?? rec!.Value;
            e = recoveryEnd ?? (M(KeyPhase.Final) is { } fin ? Math.Min(fin + 1, length) : length);
        }
        else
        {
            if (activeStart is null || activeEnd is null)
                throw new CommandException("invalid_args", "Give activeStart and activeEnd (or fromMarkers=true, or clear=true).");
            s0 = startupStart ?? 0;
            a0 = activeStart.Value;
            a1 = activeEnd.Value;
            e = recoveryEnd ?? length;
        }
        if (!(s0 >= 0 && s0 <= a0 && a0 < a1 && a1 <= e && e <= length))
            throw new CommandException("invalid_args", $"Phases must satisfy 0 ≤ startupStart ≤ activeStart < activeEnd ≤ recoveryEnd ≤ {length} (got {s0}, {a0}, {a1}, {e}).");
        CombatCommands.CombatOf(clip).Phases = new CombatPhases
        {
            StartupStart = Flicks.FromFrame(s0, clip.Fps), ActiveStart = Flicks.FromFrame(a0, clip.Fps),
            RecoveryStart = Flicks.FromFrame(a1, clip.Fps), End = Flicks.FromFrame(e, clip.Fps),
        };
        return new JsonObject { ["phases"] = CombatCommands.PhasesJson(clip) };
    }
}

public sealed class AddHitbox(ObjectId clipId, JsonObject args) : ClipEdit(clipId)
{
    readonly ObjectId _hitboxId = ObjectId.New();
    readonly JsonObject _args = (JsonObject)args.DeepClone();
    public override string Id => "hitbox.add";
    public override string Label => "Add hitbox";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        var combat = CombatCommands.CombatOf(clip);
        if (combat.Hitboxes.Count >= CombatLimits.MaxHitboxesPerClip)
            throw new CommandException("too_large", $"A clip holds at most {CombatLimits.MaxHitboxesPerClip} hitboxes.");
        var h = new Hitbox { Id = _hitboxId, Name = S.Name(_args, "name") };
        HitboxEdits.Apply(p, clip, h, _args, isNew: true);
        combat.Hitboxes.Add(h);
        return new JsonObject { ["hitboxId"] = _hitboxId.ToString(), ["hitbox"] = CombatCommands.HitboxJson(p, clip, h) };
    }
}

public sealed class UpdateHitbox(ObjectId clipId, ObjectId hitboxId, JsonObject args) : ClipEdit(clipId)
{
    readonly JsonObject _args = (JsonObject)args.DeepClone();
    public override string Id => "hitbox.update";
    public override string Label => "Edit hitbox";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        var h = clip.Combat?.Hitboxes.Find(x => x.Id == hitboxId) ?? throw new CommandException("not_found", $"Hitbox '{hitboxId}' does not exist in clip '{clip.Name}'.");
        if (_args["name"] is not null) h.Name = S.Name(_args, "name");
        HitboxEdits.Apply(p, clip, h, _args, isNew: false);
        return new JsonObject { ["hitboxId"] = hitboxId.ToString(), ["hitbox"] = CombatCommands.HitboxJson(p, clip, h) };
    }
}

static class HitboxEdits
{
    /// <summary>Applies the hitbox arguments (shape, attachment, offset, active frames, values) to <paramref name="h"/> and checks it.</summary>
    public static void Apply(Project p, Clip clip, Hitbox h, JsonObject a, bool isNew)
    {
        h.Shape = CombatCommands.Shape(a, isNew ? null : h.Shape);
        var bone = (string?)a["bone"];
        var prop = (string?)a["propId"];
        if (bone is not null && prop is not null) throw new CommandException("invalid_args", "Give 'bone' or 'propId', not both.");
        if (bone is not null)
        {
            var rig = CombatCommands.RigOfClip(p, clip);
            h.Attachment = new HitboxAttachment { Kind = HitboxAttachKind.Bone, Target = PoseCommands.ResolveBone(rig, bone.Trim()).Id, Offset = h.Attachment.Offset };
        }
        else if (prop is not null)
            h.Attachment = new HitboxAttachment { Kind = HitboxAttachKind.Prop, Target = PropCommands.Resolve(p, new[] { prop.Trim() })[0].Id, Offset = h.Attachment.Offset };
        else if (isNew) throw new CommandException("invalid_args", "Give 'bone' (a bone of the clip's rig) or 'propId'.");
        h.Attachment.Offset = CombatCommands.Offset(a, isNew ? Transform.Identity : h.Attachment.Offset);

        if ((string?)a["activeFrames"] is { } ranges) h.Active = CombatCommands.Ranges(ranges, clip);
        else if (isNew)
        {
            if (clip.Combat?.Phases is not { } ph)
                throw new CommandException("invalid_args", "Give 'activeFrames', or set the clip's phases first (combat.set_phases) to use its active frames.");
            h.Active = new List<ClipRange> { new() { Start = ph.ActiveStart, End = ph.RecoveryStart } };
        }
        h.Damage = CombatCommands.Bounded(a, "damage", 0, CombatLimits.MaxDamage, h.Damage);
        h.Knockback = CombatCommands.Bounded(a, "knockback", 0, CombatLimits.MaxKnockback, h.Knockback);
        h.HitstopFrames = (int)CombatCommands.Bounded(a, "hitstopFrames", 0, CombatLimits.MaxHitstopFrames, h.HitstopFrames);
        if ((string?)a["tags"] is { } tags) h.Tags = CombatCommands.Tags(tags);
        if (CombatLimits.CheckHitbox(h) is { } e) throw new CommandException("invalid_args", e + ".");
    }
}

public sealed class RemoveHitbox(ObjectId clipId, ObjectId hitboxId) : ClipEdit(clipId)
{
    public override string Id => "hitbox.remove";
    public override string Label => "Remove hitbox";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        if (clip.Combat is null || clip.Combat.Hitboxes.RemoveAll(h => h.Id == hitboxId) == 0)
            throw new CommandException("not_found", $"Hitbox '{hitboxId}' does not exist in clip '{clip.Name}'.");
        CombatCommands.Tidy(clip);
        return null;
    }
}

public sealed class ListHitboxes(ObjectId clipId) : ICommand
{
    public string Id => "hitbox.list";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "List hitboxes";

    public CommandOutcome Execute(Project p)
    {
        var clip = AnimationEditCommands.RequireClip(p, clipId);
        return new(Array.Empty<Change>(), new JsonObject
        {
            ["clipId"] = clip.Id.ToString(),
            ["durationFrames"] = clip.DurationFrames,
            ["phases"] = CombatCommands.PhasesJson(clip),
            ["hitboxes"] = new JsonArray((clip.Combat?.Hitboxes ?? new()).Select(h => (JsonNode?)CombatCommands.HitboxJson(p, clip, h)).ToArray()),
        });
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}

// ---- hurtboxes and reaction rules ---------------------------------------------------------------------------------------

/// <summary>
/// Base for edits of a character's combat data (hurtboxes, reaction rules). Works on a copy and swaps it in last, so a failed
/// command never leaves half an edit; undo/redo restore serialized snapshots (exact). The character is looked up by id each time.
/// </summary>
public abstract class CharacterCombatEdit(ObjectId characterId) : ICommand
{
    byte[]? _before;
    byte[]? _after;
    JsonNode? _data;
    protected ObjectId CharacterId { get; } = characterId;

    public abstract string Id { get; }
    public virtual CommandEffect Effect => CommandEffect.Mutating;
    public abstract string Label { get; }

    public CommandOutcome Execute(Project p)
    {
        var (character, rig) = PoseCommands.Resolve(p, CharacterId);
        if (_after is not null)
        {
            character.Combat = Read(_after);
            return new(Changes, _data?.DeepClone());
        }
        var before = JsonSerializer.SerializeToUtf8Bytes(character.Combat, ProjectArchive.JsonOptions);
        var work = Read(before) ?? new CharacterCombat();
        var warnings = new List<Diagnostic>();
        var data = Apply(p, character, rig, work, warnings);
        if (CombatLimits.CheckCharacterCombat(work) is { } e) throw new CommandException("invalid_args", $"Character '{character.Name}': {e}.");
        var result = work.Hurtboxes.Count == 0 && work.Reactions.Count == 0 ? null : work;
        _before = before;
        _after = JsonSerializer.SerializeToUtf8Bytes(result, ProjectArchive.JsonOptions);
        _data = data;
        character.Combat = result;
        return new(Changes, data?.DeepClone(), warnings);
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        var character = p.FindCharacter(CharacterId) ?? throw new InvalidOperationException($"Character '{CharacterId}' disappeared before undo.");
        character.Combat = Read(_before!);
        return Changes;
    }

    IReadOnlyList<Change> Changes => new[] { new Change(Change.CombatChanged, CharacterId.ToString()) };

    static CharacterCombat? Read(byte[] bytes) => JsonSerializer.Deserialize<CharacterCombat?>(bytes, ProjectArchive.JsonOptions);

    /// <summary>Edit <paramref name="combat"/> (a private copy). Throw CommandException to abort.</summary>
    protected abstract JsonNode? Apply(Project project, Character character, Rig rig, CharacterCombat combat, List<Diagnostic> warnings);
}

public sealed class GenerateHurtboxes(ObjectId characterId, double radiusScale, bool replace) : CharacterCombatEdit(characterId)
{
    List<Hurtbox>? _generated;
    public override string Id => "hurtbox.generate";
    public override string Label => "Generate hurtboxes";

    protected override JsonNode? Apply(Project p, Character character, Rig rig, CharacterCombat combat, List<Diagnostic> warnings)
    {
        var notes = new List<string>();
        try { _generated ??= HurtboxGenerator.Generate(rig, radiusScale, notes); }
        catch (CombatException e) { throw CombatCommands.Map(e); }
        warnings.AddRange(notes.Select(n => new Diagnostic("hurtbox_cap", n)));
        if (replace) combat.Hurtboxes.Clear();
        if (combat.Hurtboxes.Count + _generated.Count > CombatLimits.MaxHurtboxesPerCharacter)
            throw new CommandException("too_large", $"A character holds at most {CombatLimits.MaxHurtboxesPerCharacter} hurtboxes (use replace=true).");
        combat.Hurtboxes.AddRange(_generated.Select(h => SceneEdit.Clone(h)));
        return new JsonObject
        {
            ["count"] = _generated.Count,
            ["hurtboxes"] = new JsonArray(combat.Hurtboxes.Select(h => (JsonNode?)CombatCommands.HurtboxJson(rig, h)).ToArray()),
        };
    }
}

public sealed class UpdateHurtbox(ObjectId characterId, ObjectId hurtboxId, JsonObject args) : CharacterCombatEdit(characterId)
{
    readonly JsonObject _args = (JsonObject)args.DeepClone();
    public override string Id => "hurtbox.update";
    public override string Label => "Edit hurtbox";

    protected override JsonNode? Apply(Project p, Character character, Rig rig, CharacterCombat combat, List<Diagnostic> warnings)
    {
        var h = combat.Hurtboxes.Find(x => x.Id == hurtboxId) ?? throw new CommandException("not_found", $"Hurtbox '{hurtboxId}' does not exist on '{character.Name}'.");
        if (_args["name"] is not null) h.Name = S.Name(_args, "name");
        h.Shape = CombatCommands.Shape(_args, h.Shape);
        if ((string?)_args["bone"] is { } bone) h.Bone = PoseCommands.ResolveBone(rig, bone.Trim()).Id;
        h.Offset = CombatCommands.Offset(_args, h.Offset);
        h.Zone = CombatCommands.Enum(_args, "zone", h.Zone);
        if (_args["enabled"] is not null) h.Enabled = (bool)_args["enabled"]!;
        return new JsonObject { ["hurtbox"] = CombatCommands.HurtboxJson(rig, h) };
    }
}

public sealed class RemoveHurtbox(ObjectId characterId, ObjectId? hurtboxId) : CharacterCombatEdit(characterId)
{
    public override string Id => "hurtbox.remove";
    public override string Label => hurtboxId is null ? "Remove hurtboxes" : "Remove hurtbox";

    protected override JsonNode? Apply(Project p, Character character, Rig rig, CharacterCombat combat, List<Diagnostic> warnings)
    {
        var removed = hurtboxId is { } id ? combat.Hurtboxes.RemoveAll(h => h.Id == id) : combat.Hurtboxes.Count;
        if (hurtboxId is null) combat.Hurtboxes.Clear();
        if (removed == 0) throw new CommandException("not_found", hurtboxId is null ? $"'{character.Name}' has no hurtbox." : $"Hurtbox '{hurtboxId}' does not exist on '{character.Name}'.");
        return new JsonObject { ["removed"] = removed };
    }
}

public sealed class ListHurtboxes(ObjectId characterId) : ICommand
{
    public string Id => "hurtbox.list";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "List hurtboxes";

    public CommandOutcome Execute(Project p)
    {
        var (character, rig) = PoseCommands.Resolve(p, characterId);
        var c = character.Combat;
        return new(Array.Empty<Change>(), new JsonObject
        {
            ["characterId"] = character.Id.ToString(),
            ["hurtboxes"] = new JsonArray((c?.Hurtboxes ?? new()).Select(h => (JsonNode?)CombatCommands.HurtboxJson(rig, h)).ToArray()),
            ["reactionRules"] = new JsonArray((c?.Reactions ?? new()).Select(r => (JsonNode?)CombatCommands.RuleJson(p, r)).ToArray()),
        });
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}

public sealed class SetReactionRule(ObjectId characterId, ObjectId? ruleId, JsonObject args) : CharacterCombatEdit(characterId)
{
    readonly JsonObject _args = (JsonObject)args.DeepClone();
    readonly ObjectId _newId = ObjectId.New();
    public override string Id => "reaction.set_rule";
    public override string Label => "Set reaction rule";

    protected override JsonNode? Apply(Project p, Character character, Rig rig, CharacterCombat combat, List<Diagnostic> warnings)
    {
        var rule = new ReactionRule
        {
            Id = ruleId ?? _newId,
            Name = S.Name(_args, "name"),
            Direction = CombatCommands.Enum(_args, "direction", HitDirection.Any),
            Height = CombatCommands.Enum(_args, "height", HitHeight.Any),
            MinStrength = CombatCommands.Bounded(_args, "minStrength", 0, 1, 0),
            MaxStrength = CombatCommands.Bounded(_args, "maxStrength", 0, 1, 1),
            DurationSeconds = CombatCommands.Bounded(_args, "durationSeconds", 0.05, CombatLimits.MaxReactionSeconds, 0.6),
        };
        if (S.OptId(_args, "clipId") is { } clipId)
        {
            var clip = AnimationEditCommands.RequireClip(p, clipId);
            if (!clip.Skeleton.IsEmpty && clip.Skeleton != rig.Id)
                throw new CommandException("rig_mismatch", $"Clip '{clip.Name}' animates another rig than '{character.Name}'.");
            rule.Clip = clipId;
        }
        if (CombatLimits.CheckReaction(rule) is { } e) throw new CommandException("invalid_args", e + ".");
        if (ruleId is { } id)
        {
            var i = combat.Reactions.FindIndex(r => r.Id == id);
            if (i < 0) throw new CommandException("not_found", $"Reaction rule '{id}' does not exist on '{character.Name}'.");
            combat.Reactions[i] = rule;
        }
        else
        {
            if (combat.Reactions.Count >= CombatLimits.MaxReactionRules)
                throw new CommandException("too_large", $"A character holds at most {CombatLimits.MaxReactionRules} reaction rules.");
            combat.Reactions.Add(rule);
        }
        return new JsonObject { ["ruleId"] = rule.Id.ToString(), ["rule"] = CombatCommands.RuleJson(p, rule) };
    }
}

public sealed class RemoveReactionRule(ObjectId characterId, ObjectId ruleId) : CharacterCombatEdit(characterId)
{
    public override string Id => "reaction.remove_rule";
    public override string Label => "Remove reaction rule";

    protected override JsonNode? Apply(Project p, Character character, Rig rig, CharacterCombat combat, List<Diagnostic> warnings)
    {
        if (combat.Reactions.RemoveAll(r => r.Id == ruleId) == 0) throw new CommandException("not_found", $"Reaction rule '{ruleId}' does not exist on '{character.Name}'.");
        return null;
    }
}

public sealed class CombatShapes(ObjectId characterId, ObjectId? clipId, double frame, double x, double z, double facing) : ICommand
{
    public string Id => "combat.shapes";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Combat shapes";

    public CommandOutcome Execute(Project p)
    {
        if (frame is < 0 or > 10_000_000) throw new CommandException("invalid_args", "'frame' must be between 0 and 10,000,000.");
        if (Math.Abs(x) > 1000 || Math.Abs(z) > 1000) throw new CommandException("invalid_args", "'x'/'z' must be within ±1000 m.");
        var (character, rig) = PoseCommands.Resolve(p, characterId);
        Clip? clip = null;
        if (clipId is { } cid)
        {
            clip = AnimationEditCommands.RequireClip(p, cid);
            if (!clip.Skeleton.IsEmpty && clip.Skeleton != rig.Id) throw new CommandException("rig_mismatch", $"Clip '{clip.Name}' animates another rig than '{character.Name}'.");
        }
        var placement = CombatEvaluator.Placement(new Vec3(x, 0, z), facing);
        var t = clip is null ? Flicks.Zero : CombatEvaluator.TimeOf(frame, clip.Fps);
        var model = CombatEvaluator.Model(rig, character, clip, t);
        var hurt = new JsonArray();
        foreach (var h in character.Combat?.Hurtboxes ?? new())
            hurt.Add(new JsonObject
            {
                ["hurtboxId"] = h.Id.ToString(), ["name"] = h.Name, ["bone"] = rig.FindBone(h.Bone)?.Name, ["zone"] = h.Zone.ToString().ToLowerInvariant(),
                ["enabled"] = h.Enabled, ["world"] = CombatEvaluator.HurtboxShape(rig, h, model, placement) is { } w ? CombatCommands.WorldJson(w) : null,
            });
        var hit = new JsonArray();
        if (clip is not null)
            foreach (var h in clip.Combat?.Hitboxes ?? new())
                hit.Add(new JsonObject
                {
                    ["hitboxId"] = h.Id.ToString(), ["name"] = h.Name, ["active"] = CombatEvaluator.IsActive(h, t),
                    ["world"] = CombatEvaluator.HitboxShape(p, h, clip, rig, character.Id, model, t, placement) is { } w ? CombatCommands.WorldJson(w) : null,
                });
        string? phase = null;
        if (clip?.Combat?.Phases is { } ph)
            phase = t < ph.StartupStart ? null : t < ph.ActiveStart ? "startup" : t < ph.RecoveryStart ? "active" : t < ph.End ? "recovery" : null;
        return new(Array.Empty<Change>(), new JsonObject
        {
            ["characterId"] = character.Id.ToString(),
            ["clipId"] = clip?.Id.ToString(),
            ["frame"] = frame,
            ["phase"] = phase,
            ["hurtboxes"] = hurt,
            ["hitboxes"] = hit,
        });
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}

// ---- reactions ---------------------------------------------------------------------------------------------------------

public sealed class GenerateReaction(ObjectId characterId, JsonObject args) : AddClipCommand
{
    readonly JsonObject _args = (JsonObject)args.DeepClone();
    public override string Id => "reaction.generate";
    public override string Label => "Generate hit reaction";

    protected override Clip Build(Project p, List<Diagnostic> warnings, out JsonObject data)
    {
        var a = _args;
        var (character, rig) = PoseCommands.Resolve(p, characterId);
        Vec3 dir;
        var from = (string?)a["direction"];
        if (from is not null && (a["directionX"] is not null || a["directionZ"] is not null))
            throw new CommandException("invalid_args", "Give 'direction' or directionX/directionZ, not both.");
        if (from is not null)
            dir = CombatCommands.Enum(a, "direction", HitDirection.Front) switch
            {
                HitDirection.Back => new Vec3(0, 0, 1),
                HitDirection.Left => new Vec3(-1, 0, 0),
                HitDirection.Right => new Vec3(1, 0, 0),
                HitDirection.Any => throw new CommandException("invalid_args", "'direction' must be front, back, left or right."),
                _ => new Vec3(0, 0, -1),
            };
        else if (a["directionX"] is not null || a["directionZ"] is not null)
            dir = new Vec3(CombatCommands.Bounded(a, "directionX", -1e6, 1e6, 0), 0, CombatCommands.Bounded(a, "directionZ", -1e6, 1e6, 0));
        else dir = new Vec3(0, 0, -1);
        var height = CombatCommands.Enum(a, "height", HitHeight.Mid);
        var strength = CombatCommands.Bounded(a, "strength", 0, 1, 0.5);
        var knockback = CombatCommands.Bounded(a, "knockback", 0, CombatLimits.MaxKnockback, 0);
        var seconds = CombatCommands.Bounded(a, "durationSeconds", HitReaction.MinSeconds, HitReaction.MaxSeconds, 0.6);

        var fps = p.Settings.DefaultFrameRate;
        Transform[] baseLocals;
        var ev = new RigEvaluator(rig);
        if (S.OptId(a, "baseClipId") is { } bid)
        {
            var baseClip = AnimationEditCommands.RequireClip(p, bid);
            if (!baseClip.Skeleton.IsEmpty && baseClip.Skeleton != rig.Id)
                throw new CommandException("rig_mismatch", $"Clip '{baseClip.Name}' animates another rig than '{character.Name}'.");
            var frame = CombatCommands.Long(a, "baseFrame") ?? Math.Max(0, baseClip.DurationFrames - 1);
            if (frame > baseClip.DurationFrames) throw new CommandException("invalid_args", $"'baseFrame' is outside clip '{baseClip.Name}' ({baseClip.DurationFrames} frames).");
            fps = baseClip.Fps;
            baseLocals = ev.Locals(ClipEvaluator.Evaluate(baseClip, rig, Flicks.FromFrame(frame, baseClip.Fps)));
        }
        else baseLocals = ev.Locals(character.Pose);

        ReactionResult result;
        try { result = HitReaction.Build(rig, baseLocals, new ReactionParams(dir, height, strength, knockback, seconds), fps.FramesPerSecond); }
        catch (CombatException e) { throw CombatCommands.Map(e); }
        var last = result.Locals.Length - 1;
        var clip = new Clip
        {
            Name = S.OptName(a, "name") ?? "Hit reaction", Fps = fps, Duration = Flicks.FromFrame(Math.Max(1, last), fps), Skeleton = rig.Id,
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "reaction",
                ["reaction.direction"] = string.Create(CultureInfo.InvariantCulture, $"{dir.Normalized().X:0.####},{dir.Normalized().Z:0.####}"),
                ["reaction.height"] = height.ToString().ToLowerInvariant(),
                ["reaction.strength"] = strength.ToString(CultureInfo.InvariantCulture),
            },
        };
        var moved = new HashSet<int>(result.BoneIndices);
        var root = SequenceEvaluator.RootMotionBoneOf(rig);
        var keys = BakedTracks.AddBoneTracks(clip, rig, result.Locals,
            b => moved.Contains(b) || BakedTracks.RotationMoves(rig, result.Locals, b),
            b => (b == root && knockback > 0) || BakedTracks.TranslationMoves(rig, result.Locals, b), reduce: true, 0.05, 0.0002);
        clip.Markers.Add(new Marker { Name = "Hit", Time = Flicks.Zero, Phase = KeyPhase.Contact });
        if (result.PeakFrame > 0) clip.Markers.Add(new Marker { Name = "Flinch peak", Time = Flicks.FromFrame(result.PeakFrame, fps), Phase = KeyPhase.FollowThrough });
        data = new JsonObject
        {
            ["peakFrame"] = result.PeakFrame,
            ["peakDegrees"] = Math.Round(result.PeakDegrees, 4),
            ["axis"] = CombatCommands.V(result.Axis),
            ["rootDisplacement"] = CombatCommands.V(result.RootDisplacement),
            ["bones"] = new JsonArray(result.Bones.Select(b => (JsonNode?)b).ToArray()),
            ["keyCount"] = keys,
        };
        return clip;
    }
}

// ---- combos ------------------------------------------------------------------------------------------------------------

public sealed class CreateCombo(ObjectId characterId, string name) : SceneEdit
{
    readonly ObjectId _id = ObjectId.New();
    public override string Id => "combo.create";
    public override string Label => "Create combo";
    protected override SceneParts Touches => SceneParts.Combos;
    protected override IReadOnlyList<Change> Changes => new[] { CombatCommands.ComboChanged(_id) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        PoseCommands.Resolve(p, characterId);
        if (p.Combos.Count >= CombatLimits.MaxCombos) throw new CommandException("too_large", $"A project holds at most {CombatLimits.MaxCombos} combos.");
        p.Combos.Add(new Combo { Id = _id, Name = name, Character = characterId });
        return new JsonObject { ["comboId"] = _id.ToString() };
    }
}

static class ComboEdits
{
    /// <summary>Applies window/link/blend/input arguments to <paramref name="a"/> (frames of <paramref name="clip"/>) and checks them.</summary>
    public static void Apply(ComboAttack a, Clip clip, JsonObject args, bool clear)
    {
        var length = clip.DurationFrames;
        long? F(string key)
        {
            var v = CombatCommands.Long(args, key);
            return v is > 0 && v > length ? throw new CommandException("invalid_args", $"'{key}' {v} is outside clip '{clip.Name}' ({length} frames).") : v;
        }
        if (clear) { a.CancelStart = a.CancelEnd = a.Link = null; }
        var cs = F("cancelStartFrame");
        var ce = F("cancelEndFrame");
        var link = F("linkFrame");
        if (cs is not null || ce is not null)
        {
            var s = cs ?? a.CancelStart?.ToFrame(clip.Fps) ?? throw new CommandException("invalid_args", "Give cancelStartFrame with cancelEndFrame.");
            var e = ce ?? a.CancelEnd?.ToFrame(clip.Fps) ?? length;
            if (!(s < e)) throw new CommandException("invalid_args", $"The cancel window must satisfy start < end ≤ {length} (got {s}..{e}).");
            a.CancelStart = Flicks.FromFrame(s, clip.Fps);
            a.CancelEnd = Flicks.FromFrame(e, clip.Fps);
            if (link is null && a.Link is { } old && (old < a.CancelStart || old >= a.CancelEnd)) a.Link = null; // stale link outside the new window
        }
        if (link is { } l)
        {
            if (l < 1) throw new CommandException("invalid_args", "'linkFrame' must be at least 1.");
            a.Link = Flicks.FromFrame(l, clip.Fps);
        }
        if (Args.OptLong(args, "blendFrames") is { } b)
        {
            if (b is < 0 or > CombatLimits.MaxBlendFrames) throw new CommandException("invalid_args", $"'blendFrames' must be between 0 and {CombatLimits.MaxBlendFrames}.");
            a.BlendFrames = b;
        }
        if ((string?)args["input"] is { } input)
        {
            input = input.Trim();
            if (input.Length > CombatLimits.MaxInputLength) throw new CommandException("invalid_args", $"'input' must be at most {CombatLimits.MaxInputLength} characters.");
            a.Input = input;
        }
        if (CombatLimits.CheckAttack(a) is { } err) throw new CommandException("invalid_args", err + ".");
    }

    public static Clip AttackClip(Project p, Combo combo, ObjectId clipId)
    {
        var clip = AnimationEditCommands.RequireClip(p, clipId);
        var (character, rig) = PoseCommands.Resolve(p, combo.Character);
        if (!clip.Skeleton.IsEmpty && clip.Skeleton != rig.Id)
            throw new CommandException("rig_mismatch", $"Clip '{clip.Name}' animates another rig than '{character.Name}'.");
        return clip;
    }
}

public sealed class AddComboAttack(ObjectId comboId, ObjectId clipId, JsonObject args) : SceneEdit
{
    readonly JsonObject _args = (JsonObject)args.DeepClone();
    readonly ObjectId _attackId = ObjectId.New();
    public override string Id => "combo.add_attack";
    public override string Label => "Add combo attack";
    protected override SceneParts Touches => SceneParts.Combos;
    protected override IReadOnlyList<Change> Changes => new[] { CombatCommands.ComboChanged(comboId) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        var combo = CombatCommands.RequireCombo(p, comboId);
        if (combo.Attacks.Count >= CombatLimits.MaxComboAttacks) throw new CommandException("too_large", $"A combo holds at most {CombatLimits.MaxComboAttacks} attacks.");
        var clip = ComboEdits.AttackClip(p, combo, clipId);
        var attack = new ComboAttack { Id = _attackId, Clip = clipId };
        ComboEdits.Apply(attack, clip, _args, clear: false);
        var index = Args.OptLong(_args, "index") ?? combo.Attacks.Count;
        if (index < 0 || index > combo.Attacks.Count) throw new CommandException("invalid_args", $"'index' must be between 0 and {combo.Attacks.Count}.");
        combo.Attacks.Insert((int)index, attack);
        if (clip.Combat?.Hitboxes.Count is null or 0) warnings.Add(new Diagnostic("no_hitboxes", $"Clip '{clip.Name}' has no hitbox yet."));
        return new JsonObject { ["attackId"] = _attackId.ToString(), ["index"] = index };
    }
}

public sealed class UpdateComboAttack(ObjectId comboId, ObjectId attackId, JsonObject args) : SceneEdit
{
    readonly JsonObject _args = (JsonObject)args.DeepClone();
    public override string Id => "combo.set_cancel_window";
    public override string Label => "Set cancel window";
    protected override SceneParts Touches => SceneParts.Combos;
    protected override IReadOnlyList<Change> Changes => new[] { CombatCommands.ComboChanged(comboId) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        var combo = CombatCommands.RequireCombo(p, comboId);
        var attack = combo.Attacks.Find(x => x.Id == attackId) ?? throw new CommandException("not_found", $"Attack '{attackId}' does not exist in combo '{combo.Name}'.");
        var clip = ComboEdits.AttackClip(p, combo, attack.Clip);
        var clear = (bool?)_args["clear"] ?? false;
        if (!clear && new[] { "cancelStartFrame", "cancelEndFrame", "linkFrame", "blendFrames", "input" }.All(k => _args[k] is null))
            throw new CommandException("invalid_args", "Give at least one of cancelStartFrame, cancelEndFrame, linkFrame, blendFrames, input, clear.");
        ComboEdits.Apply(attack, clip, _args, clear);
        return new JsonObject { ["attackId"] = attackId.ToString() };
    }
}

public sealed class RemoveCombo(ObjectId comboId, ObjectId? attackId) : SceneEdit
{
    public override string Id => "combo.remove";
    public override CommandEffect Effect => CommandEffect.Destructive;
    public override string Label => attackId is null ? "Delete combo" : "Remove combo attack";
    protected override SceneParts Touches => SceneParts.Combos;
    protected override IReadOnlyList<Change> Changes => new[] { CombatCommands.ComboChanged(comboId) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        var combo = CombatCommands.RequireCombo(p, comboId);
        if (attackId is { } id)
        {
            if (combo.Attacks.RemoveAll(a => a.Id == id) == 0) throw new CommandException("not_found", $"Attack '{id}' does not exist in combo '{combo.Name}'.");
        }
        else p.Combos.Remove(combo);
        return null;
    }
}

public sealed class InspectCombo(ObjectId comboId) : ICommand
{
    public string Id => "combo.inspect";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Inspect combo";

    public CommandOutcome Execute(Project p)
    {
        var combo = CombatCommands.RequireCombo(p, comboId);
        var data = new JsonObject
        {
            ["comboId"] = combo.Id.ToString(),
            ["name"] = combo.Name,
            ["characterId"] = combo.Character.ToString(),
        };
        var warnings = new List<Diagnostic>();
        ComboPlan? plan = null;
        try { plan = ComboPlanner.Plan(p, combo); }
        catch (CombatException e) { warnings.Add(new Diagnostic(e.Code, e.Message)); }
        if (plan is not null) warnings.AddRange(plan.Warnings.Select(w => new Diagnostic("combo", w)));
        data["fps"] = plan?.Sequence.Fps.ToString();
        data["lastFrame"] = plan?.LastFrame;
        data["attacks"] = new JsonArray(combo.Attacks.Select((a, i) =>
        {
            var clip = p.FindClip(a.Clip);
            var pa = plan?.Attacks.FirstOrDefault(x => x.Attack.Id == a.Id);
            return (JsonNode?)new JsonObject
            {
                ["attackId"] = a.Id.ToString(),
                ["index"] = i,
                ["clipId"] = a.Clip.ToString(),
                ["clip"] = clip?.Name,
                ["input"] = a.Input,
                ["lengthFrames"] = clip?.DurationFrames,
                ["cancelWindow"] = clip is not null && a.CancelStart is { } cs && a.CancelEnd is { } ce
                    ? new JsonObject { ["startFrame"] = cs.ToFrame(clip.Fps), ["endFrame"] = ce.ToFrame(clip.Fps) } : null,
                ["linkFrame"] = pa?.LinkFrame,
                ["blendFrames"] = a.BlendFrames,
                ["startFrame"] = pa?.StartFrame,
                ["endFrame"] = pa?.EndFrame,
                ["hitboxes"] = clip?.Combat?.Hitboxes.Count ?? 0,
                ["playable"] = pa is not null,
            };
        }).ToArray());
        return new(Array.Empty<Change>(), data, warnings);
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}

/// <summary>Bakes a combo through the sequence bake into a new clip, then adds the attacks' hitboxes on the combined timeline.</summary>
public sealed class BakeCombo(ObjectId comboId, string? name, bool reduceKeys, bool footLock) : AddClipCommand
{
    public override string Id => "combo.bake";
    public override string Label => "Bake combo";

    protected override Clip Build(Project p, List<Diagnostic> warnings, out JsonObject data)
    {
        var combo = CombatCommands.RequireCombo(p, comboId);
        ComboPlan plan;
        try { plan = ComboPlanner.Plan(p, combo); }
        catch (CombatException e) { throw CombatCommands.Map(e); }
        warnings.AddRange(plan.Warnings.Select(w => new Diagnostic("combo", w)));
        var ev = SequenceCommands.Evaluator(p, plan.Sequence);
        if (ev.SkippedForBlending.Count > 0)
            throw new CommandException("too_many_blends", $"More than {SequenceEvaluator.MaxBlending} attacks would blend at the same time; lengthen the links or shorten the blends.");
        var clip = BakeSequence.Bake(p, plan.Sequence, ev, name ?? combo.Name, reduceKeys, footLock, warnings, out data);
        clip.Metadata["source"] = "combo";
        clip.Metadata["comboId"] = combo.Id.ToString();
        clip.Metadata.Remove("sequenceId");
        var notes = new List<string>();
        var hitboxes = ComboPlanner.CombinedHitboxes(plan, plan.Sequence.Fps, clip.DurationFrames, notes);
        warnings.AddRange(notes.Select(n => new Diagnostic("combo", n)));
        if (hitboxes.Count > 0) clip.Combat = new ClipCombat { Hitboxes = hitboxes };
        // Cancel windows on the combined timeline, as events games can read ("cancel" with the next input).
        for (var i = 0; i + 1 < plan.Attacks.Count; i++)
        {
            var at = plan.Attacks[i];
            if (at.CancelStartFrame is not { } cs || at.CancelEndFrame is not { } ce) continue;
            var start = at.StartFrame + ComboPlanner.ToSeq(cs, at.Clip, plan.Sequence.Fps);
            if (start > clip.DurationFrames || clip.Events.Count >= CombatLimits.MaxEventsPerClip) continue;
            clip.Events.Add(new AnimationEvent
            {
                Name = "cancel_window", Kind = EventKind.Custom, Time = Flicks.FromFrame(start, plan.Sequence.Fps),
                Data = new Dictionary<string, string>
                {
                    ["next"] = plan.Attacks[i + 1].Attack.Input,
                    ["frames"] = (ComboPlanner.ToSeq(ce, at.Clip, plan.Sequence.Fps) - ComboPlanner.ToSeq(cs, at.Clip, plan.Sequence.Fps)).ToString(CultureInfo.InvariantCulture),
                },
            });
        }
        clip.Events = clip.Events.OrderBy(e => e.Time.Value).ToList();
        data["comboId"] = combo.Id.ToString();
        data.Remove("sequenceId");
        data["hitboxes"] = hitboxes.Count;
        data["attacks"] = new JsonArray(plan.Attacks.Select(a => (JsonNode?)new JsonObject
        {
            ["attackId"] = a.Attack.Id.ToString(), ["clip"] = a.Clip.Name, ["startFrame"] = a.StartFrame, ["endFrame"] = a.EndFrame,
        }).ToArray());
        return clip;
    }
}

/// <summary>Creates the combo's clip sequence in the project (editable with the sequence.* commands).</summary>
public sealed class BakeComboToSequence(ObjectId comboId, string? name) : SceneEdit
{
    readonly ObjectId _sequenceId = ObjectId.New();
    public override string Id => "combo.bake";
    public override string Label => "Combo to sequence";
    protected override SceneParts Touches => SceneParts.Sequences;
    protected override IReadOnlyList<Change> Changes => new[] { SequenceCommands.Changed(_sequenceId) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        var combo = CombatCommands.RequireCombo(p, comboId);
        ComboPlan plan;
        try { plan = ComboPlanner.Plan(p, combo, _sequenceId); }
        catch (CombatException e) { throw CombatCommands.Map(e); }
        warnings.AddRange(plan.Warnings.Select(w => new Diagnostic("combo", w)));
        if (name is not null) plan.Sequence.Name = name;
        p.Sequences.Add(plan.Sequence);
        SequenceCommands.CheckPlayable(p, plan.Sequence);
        return new JsonObject { ["sequenceId"] = _sequenceId.ToString(), ["items"] = plan.Sequence.Items.Count, ["lastFrame"] = plan.LastFrame };
    }
}

// ---- hit test ----------------------------------------------------------------------------------------------------------

public sealed class HitTest(JsonObject args) : ICommand
{
    readonly JsonObject _args = (JsonObject)args.DeepClone();
    public string Id => "combat.hit_test";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Hit test";

    public CommandOutcome Execute(Project p)
    {
        var a = _args;
        var (attacker, _) = PoseCommands.Resolve(p, S.Id(a, "attackerCharacterId"));
        var (defender, rigD) = PoseCommands.Resolve(p, S.Id(a, "defenderCharacterId"));
        var attack = AnimationEditCommands.RequireClip(p, S.Id(a, "attackClipId"));
        var defClip = S.OptId(a, "defenderClipId") is { } dc ? AnimationEditCommands.RequireClip(p, dc) : null;
        var position = new Vec3(CombatCommands.Bounded(a, "defenderX", -1000, 1000, 0), CombatCommands.Bounded(a, "defenderY", -1000, 1000, 0),
            CombatCommands.Bounded(a, "defenderZ", -1000, 1000, 1));
        var facing = CombatCommands.Bounded(a, "defenderFacing", -3600, 3600, 180);
        var maxSub = Args.OptLong(a, "maxSubsteps") ?? 8;
        if (maxSub is < 1 or > HitDetector.MaxSubstepsLimit) throw new CommandException("invalid_args", $"'maxSubsteps' must be between 1 and {HitDetector.MaxSubstepsLimit}.");
        var options = new HitTestOptions
        {
            DefenderPlacement = CombatEvaluator.Placement(position, facing),
            StartFrame = CombatCommands.Long(a, "startFrame"), EndFrame = CombatCommands.Long(a, "endFrame"),
            MaxSubsteps = (int)maxSub, Hitbox = S.OptId(a, "hitboxId"),
        };
        HitTestReport report;
        try { report = HitDetector.Run(p, attacker, attack, defender, defClip, options); }
        catch (CombatException e) { throw CombatCommands.Map(e); }

        JsonObject Hit(HitInfo h) => new()
        {
            ["frame"] = h.Frame,
            ["time"] = Math.Round(h.Time, 4),
            ["hitboxId"] = h.Hitbox.Id.ToString(),
            ["hitbox"] = h.Hitbox.Name,
            ["hurtboxId"] = h.Hurtbox.Id.ToString(),
            ["hurtbox"] = h.Hurtbox.Name,
            ["bone"] = h.BoneName,
            ["point"] = CombatCommands.V(h.Point),
            ["normal"] = CombatCommands.V(h.Normal),
            ["depth"] = Math.Round(h.Depth, 6),
            ["velocity"] = CombatCommands.V(h.Velocity),
            ["direction"] = CombatCommands.V(h.Direction),
            ["localDirection"] = CombatCommands.V(h.LocalDirection),
            ["from"] = h.From.ToString().ToLowerInvariant(),
            ["zone"] = h.Zone.ToString().ToLowerInvariant(),
            ["strength"] = Math.Round(h.Strength, 4),
            ["damage"] = h.Hitbox.Damage,
            ["knockback"] = h.Hitbox.Knockback,
            ["hitstopFrames"] = h.Hitbox.HitstopFrames,
            ["swept"] = h.Swept,
        };
        var first = report.First;
        JsonObject? reactionArgs = null;
        JsonObject? rule = null;
        if (first is not null)
        {
            var height = HitDetector.HeightOf(first.Zone);
            reactionArgs = new JsonObject
            {
                ["characterId"] = defender.Id.ToString(),
                ["directionX"] = Math.Round(first.LocalDirection.X, 6),
                ["directionZ"] = Math.Round(first.LocalDirection.Z, 6),
                ["height"] = height.ToString().ToLowerInvariant(),
                ["strength"] = Math.Round(first.Strength, 4),
                ["knockback"] = first.Hitbox.Knockback,
            };
            if (HitDetector.PickReaction(defender.Combat, first.From, height, first.Strength) is { } r) rule = CombatCommands.RuleJson(p, r);
        }
        return new(Array.Empty<Change>(), new JsonObject
        {
            ["hit"] = first is not null,
            ["firstHit"] = first is null ? null : Hit(first),
            ["hits"] = new JsonArray(report.Hits.Select(h => (JsonNode?)Hit(h)).ToArray()),
            ["framesTested"] = report.FramesTested,
            ["startFrame"] = report.StartFrame,
            ["endFrame"] = report.EndFrame,
            ["evaluations"] = report.Evaluations,
            ["reactionArgs"] = reactionArgs,
            ["reactionRule"] = rule,
        }, report.Warnings.Select(w => new Diagnostic("hit_test", w)).ToList());
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}
