using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;

namespace Blazma.Combat;

/// <summary>A combat failure with a stable code (too_large, no_hitboxes, no_hurtboxes, rig_mismatch, invalid_args…).</summary>
public sealed class CombatException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// World-space hitboxes and hurtboxes at a clip time (docs/ARCHITECTURE.md §9). Characters are evaluated like playback (rest base,
/// pinned IK solved, <see cref="ClipEvaluator"/>); a hitbox follows its bone (<c>boneModel · offset</c>) or its prop (the prop's world
/// transform from <see cref="PropEvaluator"/> without scale, then the offset); a placement transform puts a character in the world
/// (the attacker at the origin, the defender wherever the hit test puts it).
/// </summary>
public static class CombatEvaluator
{
    /// <summary>Time of a (possibly fractional) frame.</summary>
    public static Flicks TimeOf(double frame, FrameRate fps) =>
        new((long)Math.Round(frame * Flicks.PerSecond * fps.Denominator / fps.Numerator, MidpointRounding.AwayFromZero));

    public static bool IsActive(Hitbox h, Flicks t)
    {
        foreach (var r in h.Active)
            if (t >= r.Start && t < r.End) return true;
        return false;
    }

    /// <summary>Whether the hitbox is active on frame <paramref name="frame"/> (the frame's start time is inside a range).</summary>
    public static bool IsActiveFrame(Hitbox h, long frame, FrameRate fps) => frame >= 0 && IsActive(h, Flicks.FromFrame(frame, fps));

    /// <summary>Active frames of a hitbox as [start, end) frame pairs (rounded like markers).</summary>
    public static IEnumerable<(long Start, long End)> ActiveFrames(Hitbox h, FrameRate fps) =>
        h.Active.Select(r => (r.Start.ToFrame(fps), r.End.ToFrame(fps)));

    /// <summary>Model-space transform of the hitbox's frame, or null when its bone/prop no longer exists.</summary>
    public static Transform? HitboxTransform(Project project, Hitbox h, Clip clip, Rig rig, ObjectId character, Transform[] model, Flicks time)
    {
        var a = h.Attachment;
        if (a.Kind == HitboxAttachKind.Bone)
        {
            var i = rig.IndexOf(a.Target);
            return i < 0 || i >= model.Length ? null : model[i] * a.Offset;
        }
        var prop = project.FindProp(a.Target);
        if (prop is null) return null;
        var world = PropEvaluator.World(project, prop, PropEvaluator.Local(prop, clip, time), rig, model, character);
        return new Transform(world.Translation, world.Rotation, Vec3.One) * a.Offset;
    }

    public static Transform? HurtboxTransform(Rig rig, Hurtbox h, Transform[] model)
    {
        var i = rig.IndexOf(h.Bone);
        return i < 0 || i >= model.Length ? null : model[i] * h.Offset;
    }

    /// <summary>World shape of a hitbox (placement · its transform), or null when its attachment is broken.</summary>
    public static WorldShape? HitboxShape(Project project, Hitbox h, Clip clip, Rig rig, ObjectId character, Transform[] model, Flicks time, Transform placement) =>
        HitboxTransform(project, h, clip, rig, character, model, time) is { } t ? WorldShape.From(h.Shape, placement * t) : null;

    public static WorldShape? HurtboxShape(Rig rig, Hurtbox h, Transform[] model, Transform placement) =>
        HurtboxTransform(rig, h, model) is { } t ? WorldShape.From(h.Shape, placement * t) : null;

    /// <summary>Model transforms of a character playing <paramref name="clip"/> at <paramref name="time"/> (rest base), or its
    /// working pose when <paramref name="clip"/> is null.</summary>
    public static Transform[] Model(Rig rig, Character character, Clip? clip, Flicks time) =>
        new RigEvaluator(rig).Model(clip is null ? character.Pose : ClipEvaluator.Evaluate(clip, rig, time));

    /// <summary>Defender placement: at <paramref name="position"/> (metres), turned <paramref name="facingDegrees"/> about +Y
    /// (0 = facing +Z like the attacker, 180 = facing the attacker).</summary>
    public static Transform Placement(Vec3 position, double facingDegrees) =>
        new(position, Quat.FromAxisAngle(Vec3.UnitY, facingDegrees * Math.PI / 180), Vec3.One);

    /// <summary>Zone of a bone from its humanoid name (head/neck high, legs low, the rest mid); null when unmapped.</summary>
    public static HitZone? ZoneOf(string? standard) => standard switch
    {
        null => null,
        "head" or "neck" => HitZone.High,
        _ when standard.StartsWith("thigh", StringComparison.Ordinal) || standard.StartsWith("calf", StringComparison.Ordinal)
            || standard.StartsWith("foot", StringComparison.Ordinal) || standard.StartsWith("toe", StringComparison.Ordinal) => HitZone.Low,
        _ => HitZone.Mid,
    };
}

/// <summary>
/// Builds hurtboxes for a character from its rig (docs/ARCHITECTURE.md §9.2): one capsule per body segment from the joint to the
/// next joint, radius from a table of humanoid segment radii (fraction of the estimated body height, like MassModel's segment
/// lengths); unmapped bones get a capsule to the mean of their children with a radius of 20% of the segment (1–15 cm).
/// Terminal segments (head, hand, toe) extend along their parent's direction. Hurtboxes are editable afterwards.
/// </summary>
public static class HurtboxGenerator
{
    /// <summary>Radius as a fraction of the estimated body height, and the standard child the segment ends at.</summary>
    static readonly Dictionary<string, (double Radius, string[] Next, double Extend)> Table = new()
    {
        ["pelvis"] = (0.085, new[] { "spine_01", "spine_02" }, 0),
        ["spine_01"] = (0.075, new[] { "spine_02", "spine_03" }, 0),
        ["spine_02"] = (0.08, new[] { "spine_03", "neck" }, 0),
        ["spine_03"] = (0.085, new[] { "neck", "head" }, 0),
        ["neck"] = (0.03, new[] { "head" }, 0),
        ["head"] = (0.055, Array.Empty<string>(), 0.11),
        ["upper_arm_l"] = (0.03, new[] { "lower_arm_l" }, 0), ["upper_arm_r"] = (0.03, new[] { "lower_arm_r" }, 0),
        ["lower_arm_l"] = (0.025, new[] { "hand_l" }, 0), ["lower_arm_r"] = (0.025, new[] { "hand_r" }, 0),
        ["hand_l"] = (0.028, Array.Empty<string>(), 0.08), ["hand_r"] = (0.028, Array.Empty<string>(), 0.08),
        ["thigh_l"] = (0.045, new[] { "calf_l" }, 0), ["thigh_r"] = (0.045, new[] { "calf_r" }, 0),
        ["calf_l"] = (0.033, new[] { "foot_l" }, 0), ["calf_r"] = (0.033, new[] { "foot_r" }, 0),
        ["foot_l"] = (0.028, new[] { "toe_l" }, 0.072), ["foot_r"] = (0.028, new[] { "toe_r" }, 0.072),
        ["toe_l"] = (0.02, Array.Empty<string>(), 0.03), ["toe_r"] = (0.02, Array.Empty<string>(), 0.03),
    };

    /// <param name="radiusScale">Multiplies every radius (0.25–4).</param>
    public static List<Hurtbox> Generate(Rig rig, double radiusScale, List<string> warnings)
    {
        if (!(radiusScale >= 0.25 && radiusScale <= 4)) throw new CombatException("invalid_args", "'radiusScale' must be between 0.25 and 4.");
        var ev = new RigEvaluator(rig);
        var rest = ev.RestModel();
        if (rest.Length == 0) throw new CombatException("no_bones", $"Rig '{rig.Name}' has no bones.");
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var t in rest)
        {
            if (!double.IsFinite(t.Translation.Y)) continue;
            minY = Math.Min(minY, t.Translation.Y);
            maxY = Math.Max(maxY, t.Translation.Y);
        }
        var ground = minY is >= 0 and <= 0.2 ? 0 : minY;
        var humanoid = rig.FindByStandardName("pelvis") is not null;
        var height = (maxY - ground) * (humanoid ? 1.14 : 1);
        if (!(height > 0.05) || !double.IsFinite(height)) height = 1;

        // Children sum/count in one pass (O(n); rigs are parent-before-child).
        var kidSum = new Vec3[rig.Bones.Count];
        var kidCount = new int[rig.Bones.Count];
        for (var k = 0; k < rig.Bones.Count; k++)
            if (ev.ParentOf(k) is var pk and >= 0) { kidSum[pk] += rest[k].Translation; kidCount[pk]++; }
        var candidates = new List<(Hurtbox Box, bool Mapped, double Length)>();
        for (var i = 0; i < rig.Bones.Count; i++)
        {
            var bone = rig.Bones[i];
            var std = rig.StandardNameOf(bone.Id);
            var origin = rest[i].Translation;
            Vec3? far = null;
            double radius;
            if (std is not null && Table.TryGetValue(std, out var row))
            {
                foreach (var next in row.Next)
                    if (rig.FindByStandardName(next) is { } nb) { far = rest[rig.IndexOf(nb.Id)].Translation; break; }
                if (far is null && row.Extend > 0)
                {
                    var parent = ev.ParentOf(i);
                    var dir = parent >= 0 ? origin - rest[parent].Translation : Vec3.UnitY;
                    if (std.StartsWith("foot", StringComparison.Ordinal)) dir = new Vec3(0, 0, 1); // no toe: the foot points forward (+Z)
                    if (dir.LengthSquared() < 1e-12) dir = Vec3.UnitY;
                    far = origin + dir.Normalized() * (row.Extend * height);
                }
                radius = row.Radius * height;
            }
            else
            {
                if (std is "root" || std is not null && std.StartsWith("clavicle", StringComparison.Ordinal)) continue;
                if (kidCount[i] == 0) continue;
                far = kidSum[i] / kidCount[i];
                radius = Math.Clamp(0.2 * (far.Value - origin).Length(), 0.01, 0.15);
            }
            if (far is not { } end) continue;
            var seg = end - origin;
            var len = seg.Length();
            if (!double.IsFinite(len) || len < 0.02 && std != "head") continue;
            radius = Math.Clamp(radius * radiusScale, 0.005, CombatLimits.MaxShapeSize);
            var mid = (origin + end) * 0.5;
            var rot = len > 1e-9 ? Quat.FromTo(Vec3.UnitY, seg) : Quat.Identity;
            var offset = RigEvaluator.LocalFromModel(rest[i], new Transform(mid, rot, Vec3.One)) with { Scale = Vec3.One };
            if (CombatLimits.CheckOffset(offset) is not null) continue;
            var rel = (mid.Y - ground) / height;
            var zone = CombatEvaluator.ZoneOf(std) ?? (rel > 0.8 ? HitZone.High : rel < 0.45 ? HitZone.Low : HitZone.Mid);
            candidates.Add((new Hurtbox
            {
                Name = bone.Name.Length > CombatLimits.MaxNameLength ? bone.Name[..CombatLimits.MaxNameLength] : bone.Name.Length == 0 ? "bone" : bone.Name,
                Bone = bone.Id, Offset = offset, Zone = zone,
                Shape = new CollisionShape { Kind = CollisionShapeKind.Capsule, Radius = radius, Length = Math.Min(len, CombatLimits.MaxShapeSize) },
            }, std is not null, len));
        }
        if (candidates.Count == 0) throw new CombatException("no_segments", $"Rig '{rig.Name}' has no bone segment long enough for a hurtbox.");
        if (candidates.Count > CombatLimits.MaxHurtboxesPerCharacter)
        {
            warnings.Add($"{candidates.Count} segments found; kept the {CombatLimits.MaxHurtboxesPerCharacter} largest (humanoid-mapped first).");
            var keep = candidates.OrderByDescending(c => c.Mapped).ThenByDescending(c => c.Length).Take(CombatLimits.MaxHurtboxesPerCharacter).Select(c => c.Box).ToHashSet();
            candidates = candidates.Where(c => keep.Contains(c.Box)).ToList();
        }
        return candidates.Select(c => c.Box).ToList();
    }
}
