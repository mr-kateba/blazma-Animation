using System.Text.Json.Serialization;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Timing;

namespace Blazma.Core.Model;

// Combat data (schema v5, docs/DATA_FORMAT.md §11). Times are flicks of the owning clip (D4); commands and the sidecar speak frames.

/// <summary>What an <see cref="AnimationEvent"/> means to a game (schema v5). Older events are <see cref="Custom"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EventKind>))]
public enum EventKind { Custom, Hit, Footstep, Sound, Vfx }

[JsonConverter(typeof(JsonStringEnumConverter<CollisionShapeKind>))]
public enum CollisionShapeKind { Sphere, Capsule, Box }

/// <summary>
/// A collision volume in its own local frame: a sphere (<see cref="Radius"/>) at the origin, a capsule whose two sphere centres are
/// <see cref="Length"/> apart along local +Y (centred on the origin), or a box with full extents <see cref="Size"/>.
/// </summary>
public sealed class CollisionShape
{
    public CollisionShapeKind Kind { get; set; } = CollisionShapeKind.Sphere;
    public double Radius { get; set; } = 0.1;
    public double Length { get; set; }
    public Vec3 Size { get; set; } = new(0.2, 0.2, 0.2);

    public CollisionShape Clone() => new() { Kind = Kind, Radius = Radius, Length = Length, Size = Size };
}

/// <summary>A frame range of a clip, [Start, End) in flicks.</summary>
public sealed class ClipRange
{
    public Flicks Start { get; set; }
    public Flicks End { get; set; }
}

/// <summary>Startup [StartupStart, ActiveStart), active [ActiveStart, RecoveryStart), recovery [RecoveryStart, End).</summary>
public sealed class CombatPhases
{
    public Flicks StartupStart { get; set; }
    public Flicks ActiveStart { get; set; }
    public Flicks RecoveryStart { get; set; }
    public Flicks End { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<HitboxAttachKind>))]
public enum HitboxAttachKind { Bone, Prop }

/// <summary>What a hitbox follows: a bone of the clip's rig, or a prop (its world transform without scale), plus an offset in
/// that frame.</summary>
public sealed class HitboxAttachment
{
    public HitboxAttachKind Kind { get; set; } = HitboxAttachKind.Bone;
    public ObjectId Target { get; set; }
    public Transform Offset { get; set; } = Transform.Identity;
}

/// <summary>An attack volume of a clip, active only on <see cref="Active"/> frames.</summary>
public sealed class Hitbox
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public string Name { get; set; } = "Hitbox";
    public CollisionShape Shape { get; set; } = new();
    public HitboxAttachment Attachment { get; set; } = new();
    public List<ClipRange> Active { get; set; } = new();
    public double Damage { get; set; } = 10;

    /// <summary>Push-back distance in metres given to the target.</summary>
    public double Knockback { get; set; } = 0.3;
    public int HitstopFrames { get; set; }
    public List<string> Tags { get; set; } = new();
}

/// <summary>Combat data of one clip: its phases and hitboxes (null when the clip has none).</summary>
public sealed class ClipCombat
{
    public CombatPhases? Phases { get; set; }
    public List<Hitbox> Hitboxes { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<HitZone>))]
public enum HitZone { High, Mid, Low }

/// <summary>A damageable volume following one bone of a character.</summary>
public sealed class Hurtbox
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public string Name { get; set; } = "Hurtbox";
    public ObjectId Bone { get; set; }
    public CollisionShape Shape { get; set; } = new();

    /// <summary>Placement in the bone's local frame.</summary>
    public Transform Offset { get; set; } = Transform.Identity;
    public HitZone Zone { get; set; } = HitZone.Mid;
    public bool Enabled { get; set; } = true;
}

[JsonConverter(typeof(JsonStringEnumConverter<HitDirection>))]
public enum HitDirection { Any, Front, Back, Left, Right }

[JsonConverter(typeof(JsonStringEnumConverter<HitHeight>))]
public enum HitHeight { Any, High, Mid, Low }

/// <summary>Maps a hit (where it comes from, its height and strength) to a reaction: a clip, or a procedural reaction when
/// <see cref="Clip"/> is empty.</summary>
public sealed class ReactionRule
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public string Name { get; set; } = "Reaction";
    public HitDirection Direction { get; set; } = HitDirection.Any;
    public HitHeight Height { get; set; } = HitHeight.Any;
    public double MinStrength { get; set; }
    public double MaxStrength { get; set; } = 1;
    public ObjectId Clip { get; set; }

    /// <summary>Length of the procedural reaction (when <see cref="Clip"/> is empty).</summary>
    public double DurationSeconds { get; set; } = 0.6;
}

/// <summary>Combat data of one character: hurtboxes and reaction rules (null when it has none).</summary>
public sealed class CharacterCombat
{
    public List<Hurtbox> Hurtboxes { get; set; } = new();
    public List<ReactionRule> Reactions { get; set; } = new();
}

/// <summary>A chain of attack clips on one character (schema v5, archive entry combos/&lt;id&gt;.json).</summary>
public sealed class Combo
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public string Name { get; set; } = "Combo";
    public ObjectId Character { get; set; }
    public List<ComboAttack> Attacks { get; set; } = new();
}

/// <summary>
/// One attack of a combo. The cancel window [CancelStart, CancelEnd) (flicks of this attack's clip) is when the NEXT attack may
/// start; <see cref="Link"/> is where it starts when the combo is baked (default: the window start; no window = the clip end).
/// <see cref="BlendFrames"/> is this attack's fade-in over the previous one.
/// </summary>
public sealed class ComboAttack
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public ObjectId Clip { get; set; }
    public string Input { get; set; } = "";
    public Flicks? CancelStart { get; set; }
    public Flicks? CancelEnd { get; set; }
    public Flicks? Link { get; set; }
    public long BlendFrames { get; set; } = 3;
}

/// <summary>
/// Limits of the combat data, the same in the commands and when an archive is read (so a project edited through commands always
/// loads). Each check returns null when valid, else a message (callers prefix the object's name).
/// </summary>
public static class CombatLimits
{
    public const int MaxEventsPerClip = 1024;
    public const int MaxPayloadEntries = 16;
    public const int MaxPayloadKey = 64;
    public const int MaxPayloadValue = 256;
    public const int MaxNameLength = 200;
    public const int MaxHitboxesPerClip = 32;
    public const int MaxActiveRanges = 16;
    public const int MaxTags = 8;
    public const int MaxTagLength = 32;
    public const int MaxHurtboxesPerCharacter = 64;
    public const int MaxReactionRules = 64;
    public const int MaxCombos = 256;
    public const int MaxComboAttacks = 32;
    public const int MaxInputLength = 32;
    public const long MaxBlendFrames = 600;

    /// <summary>Largest radius, capsule length or box extent (m).</summary>
    public const double MaxShapeSize = 10;

    /// <summary>Largest offset translation component (m).</summary>
    public const double MaxOffset = 100;
    public const double MaxDamage = 1_000_000;
    public const double MaxKnockback = 100;
    public const int MaxHitstopFrames = 600;
    public const double MaxReactionSeconds = 10;

    /// <summary>Latest time any combat range or event may have (100,000 s).</summary>
    public static readonly Flicks MaxTime = new(100_000 * Flicks.PerSecond);

    public static string? CheckName(string? name, string what = "name") =>
        string.IsNullOrWhiteSpace(name) ? $"the {what} is empty" : name.Length > MaxNameLength ? $"the {what} is longer than {MaxNameLength} characters" : null;

    public static bool InTime(Flicks t) => t.Value >= 0 && t <= MaxTime;

    public static string? CheckEvent(AnimationEvent? e)
    {
        if (e is null) return "an event entry is empty";
        if (CheckName(e.Name, "event name") is { } n) return n;
        if (!Enum.IsDefined(e.Kind)) return $"event '{e.Name}' has an unknown kind";
        if (!InTime(e.Time)) return $"event '{e.Name}' has a time outside 0–{MaxTime.ToSeconds():N0} s";
        return CheckPayload(e.Data, $"event '{e.Name}'");
    }

    public static string? CheckPayload(Dictionary<string, string>? data, string what)
    {
        if (data is null) return $"{what} has no payload object";
        if (data.Count > MaxPayloadEntries) return $"{what} has {data.Count} payload entries (at most {MaxPayloadEntries})";
        foreach (var (k, v) in data)
        {
            if (string.IsNullOrEmpty(k) || k.Length > MaxPayloadKey) return $"{what} has a payload key that is empty or longer than {MaxPayloadKey} characters";
            if (v is null || v.Length > MaxPayloadValue) return $"{what}: payload value of '{k}' is missing or longer than {MaxPayloadValue} characters";
        }
        return null;
    }

    static bool Size(double v) => v > 0 && v <= MaxShapeSize;

    public static string? CheckShape(CollisionShape? s)
    {
        if (s is null) return "it has no shape";
        return s.Kind switch
        {
            CollisionShapeKind.Sphere => Size(s.Radius) ? null : $"its radius must be > 0 and ≤ {MaxShapeSize} m",
            CollisionShapeKind.Capsule => !Size(s.Radius) ? $"its radius must be > 0 and ≤ {MaxShapeSize} m"
                : !(s.Length >= 0 && s.Length <= MaxShapeSize) ? $"its capsule length must be between 0 and {MaxShapeSize} m" : null,
            CollisionShapeKind.Box => Size(s.Size.X) && Size(s.Size.Y) && Size(s.Size.Z) ? null : $"each box extent must be > 0 and ≤ {MaxShapeSize} m",
            _ => "its shape kind is unknown",
        };
    }

    public static string? CheckOffset(Transform t)
    {
        var p = t.Translation;
        var q = t.Rotation;
        if (!(Math.Abs(p.X) <= MaxOffset && Math.Abs(p.Y) <= MaxOffset && Math.Abs(p.Z) <= MaxOffset))
            return $"its offset must be finite and within ±{MaxOffset} m";
        if (!(double.IsFinite(q.X) && double.IsFinite(q.Y) && double.IsFinite(q.Z) && double.IsFinite(q.W)) || q.Length() < 1e-9)
            return "its offset rotation is invalid";
        return null;
    }

    public static string? CheckRange(ClipRange? r)
    {
        if (r is null) return "an active range is empty";
        if (!InTime(r.Start) || !InTime(r.End) || r.End <= r.Start) return "an active range must have 0 ≤ start < end (within the time limit)";
        return null;
    }

    public static string? CheckHitbox(Hitbox? h)
    {
        if (h is null) return "a hitbox entry is empty";
        if (CheckName(h.Name, "hitbox name") is { } n) return n;
        var what = $"hitbox '{h.Name}'";
        if (CheckShape(h.Shape) is { } s) return $"{what}: {s}";
        if (h.Attachment is null || !Enum.IsDefined(h.Attachment.Kind) || h.Attachment.Target.IsEmpty) return $"{what} has no attachment";
        if (CheckOffset(h.Attachment.Offset) is { } o) return $"{what}: {o}";
        if (h.Active is null || h.Active.Count == 0 || h.Active.Count > MaxActiveRanges) return $"{what} needs 1–{MaxActiveRanges} active ranges";
        foreach (var r in h.Active)
            if (CheckRange(r) is { } e) return $"{what}: {e}";
        for (var i = 1; i < h.Active.Count; i++)
            if (h.Active[i].Start < h.Active[i - 1].End) return $"{what}: active ranges must be sorted and must not overlap";
        if (!(h.Damage >= 0 && h.Damage <= MaxDamage)) return $"{what}: damage must be between 0 and {MaxDamage:N0}";
        if (!(h.Knockback >= 0 && h.Knockback <= MaxKnockback)) return $"{what}: knockback must be between 0 and {MaxKnockback} m";
        if (h.HitstopFrames is < 0 or > MaxHitstopFrames) return $"{what}: hitstop must be between 0 and {MaxHitstopFrames} frames";
        if (h.Tags is null || h.Tags.Count > MaxTags) return $"{what}: at most {MaxTags} tags";
        foreach (var t in h.Tags)
            if (string.IsNullOrWhiteSpace(t) || t.Length > MaxTagLength) return $"{what}: tags must be 1–{MaxTagLength} characters";
        return null;
    }

    public static string? CheckPhases(CombatPhases p) =>
        InTime(p.StartupStart) && InTime(p.End) && p.StartupStart <= p.ActiveStart && p.ActiveStart < p.RecoveryStart && p.RecoveryStart <= p.End
            ? null : "its phases must satisfy 0 ≤ startup ≤ active start < recovery start ≤ end";

    public static string? CheckClipCombat(ClipCombat c)
    {
        if (c.Hitboxes is null) return "its combat data has no hitbox list";
        if (c.Phases is { } p && CheckPhases(p) is { } e) return e;
        if (c.Hitboxes.Count > MaxHitboxesPerClip) return $"it has {c.Hitboxes.Count} hitboxes (at most {MaxHitboxesPerClip})";
        var ids = new HashSet<ObjectId>();
        foreach (var h in c.Hitboxes)
        {
            if (CheckHitbox(h) is { } he) return he;
            if (!ids.Add(h!.Id)) return $"duplicate hitbox id {h.Id}";
        }
        return null;
    }

    public static string? CheckHurtbox(Hurtbox? h)
    {
        if (h is null) return "a hurtbox entry is empty";
        if (CheckName(h.Name, "hurtbox name") is { } n) return n;
        var what = $"hurtbox '{h.Name}'";
        if (h.Bone.IsEmpty) return $"{what} has no bone";
        if (CheckShape(h.Shape) is { } s) return $"{what}: {s}";
        if (CheckOffset(h.Offset) is { } o) return $"{what}: {o}";
        if (!Enum.IsDefined(h.Zone)) return $"{what} has an unknown zone";
        return null;
    }

    public static string? CheckReaction(ReactionRule? r)
    {
        if (r is null) return "a reaction rule is empty";
        if (CheckName(r.Name, "reaction name") is { } n) return n;
        var what = $"reaction '{r.Name}'";
        if (!Enum.IsDefined(r.Direction) || !Enum.IsDefined(r.Height)) return $"{what} has an unknown direction or height";
        if (!(r.MinStrength >= 0 && r.MinStrength <= r.MaxStrength && r.MaxStrength <= 1)) return $"{what}: strengths must satisfy 0 ≤ min ≤ max ≤ 1";
        if (!(r.DurationSeconds >= 0.05 && r.DurationSeconds <= MaxReactionSeconds)) return $"{what}: duration must be between 0.05 and {MaxReactionSeconds} s";
        return null;
    }

    public static string? CheckCharacterCombat(CharacterCombat c)
    {
        if (c.Hurtboxes is null || c.Reactions is null) return "its combat data has no hurtbox or reaction list";
        if (c.Hurtboxes.Count > MaxHurtboxesPerCharacter) return $"it has {c.Hurtboxes.Count} hurtboxes (at most {MaxHurtboxesPerCharacter})";
        if (c.Reactions.Count > MaxReactionRules) return $"it has {c.Reactions.Count} reaction rules (at most {MaxReactionRules})";
        var ids = new HashSet<ObjectId>();
        foreach (var h in c.Hurtboxes)
        {
            if (CheckHurtbox(h) is { } e) return e;
            if (!ids.Add(h!.Id)) return $"duplicate hurtbox id {h.Id}";
        }
        foreach (var r in c.Reactions)
        {
            if (CheckReaction(r) is { } e) return e;
            if (!ids.Add(r!.Id)) return $"duplicate reaction id {r.Id}";
        }
        return null;
    }

    public static string? CheckAttack(ComboAttack? a)
    {
        if (a is null) return "an attack entry is empty";
        if (a.Clip.IsEmpty) return $"attack {a.Id} has no clip";
        if (a.Input is null || a.Input.Length > MaxInputLength) return $"attack {a.Id}: input must be at most {MaxInputLength} characters";
        if (a.BlendFrames < 0 || a.BlendFrames > MaxBlendFrames) return $"attack {a.Id}: blend must be between 0 and {MaxBlendFrames} frames";
        if (a.CancelStart is null != a.CancelEnd is null) return $"attack {a.Id}: the cancel window needs both a start and an end";
        if (a.CancelStart is { } cs && (!InTime(cs) || !InTime(a.CancelEnd!.Value) || a.CancelEnd!.Value <= cs))
            return $"attack {a.Id}: the cancel window must have 0 ≤ start < end";
        if (a.Link is { } l)
        {
            if (!InTime(l)) return $"attack {a.Id}: the link time is out of range";
            if (a.CancelStart is { } s && (l < s || l >= a.CancelEnd!.Value)) return $"attack {a.Id}: the link must be inside the cancel window";
        }
        return null;
    }

    public static string? CheckCombo(Combo? c)
    {
        if (c is null) return "a combo entry is empty";
        if (CheckName(c.Name, "combo name") is { } n) return n;
        var what = $"combo '{c.Name}'";
        if (c.Character.IsEmpty) return $"{what} has no character";
        if (c.Attacks is null || c.Attacks.Count > MaxComboAttacks) return $"{what} must have at most {MaxComboAttacks} attacks";
        var ids = new HashSet<ObjectId>();
        foreach (var a in c.Attacks)
        {
            if (CheckAttack(a) is { } e) return $"{what}: {e}";
            if (!ids.Add(a!.Id)) return $"{what}: duplicate attack id {a.Id}";
        }
        return null;
    }
}
