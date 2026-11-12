using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Physics;

/// <summary>A body segment as a capsule rigidly attached to a bone: from the bone origin to <paramref name="Distal"/> (bone-local), radius in metres.</summary>
public readonly record struct BodyCapsule(int Bone, string Part, Vec3 Distal, double Radius);

/// <summary>
/// Coarse body surface for ground contact and penetration checks: one capsule per weighted segment of the <see cref="MassModel"/>
/// (proximal joint → distal point, the same segments as the centre of mass), with radii as fractions of the estimated height H
/// (pelvis 0.055H, spine/chest 0.05H, neck 0.03H, head 0.055H, upper arm 0.025H, forearm 0.02H, hand 0.015H, thigh 0.04H, calf 0.02H,
/// others 0.02H), plus the sole points of the <see cref="FootModel"/> for the feet (foot and toe segments are not capsules).
/// The lowest point of a capsule is <c>min(y_a, y_b) − r</c>.
/// </summary>
public sealed class BodyGeometry
{
    BodyGeometry(MassModel mass, FootModel feet, BodyCapsule[] capsules)
    {
        Mass = mass;
        Feet = feet;
        Capsules = capsules;
    }

    public MassModel Mass { get; }
    public FootModel Feet { get; }
    public IReadOnlyList<BodyCapsule> Capsules { get; }
    public RigEvaluator Evaluator => Mass.Evaluator;

    /// <summary>Model-space height of the ground (the rest pose's sole).</summary>
    public double Ground => Feet.RestSoleHeight;

    public double Height => Mass.EstimatedHeight;

    static readonly Dictionary<string, double> Radii = new()
    {
        [H.Pelvis] = 0.055, [H.Spine1] = 0.05, [H.Spine2] = 0.05, [H.Chest] = 0.05, [H.Neck] = 0.03, [H.Head] = 0.055,
        [H.Clavicle(Side.Left)] = 0.025, [H.Clavicle(Side.Right)] = 0.025,
        [H.UpperArm(Side.Left)] = 0.025, [H.UpperArm(Side.Right)] = 0.025, [H.LowerArm(Side.Left)] = 0.02, [H.LowerArm(Side.Right)] = 0.02,
        [H.Hand(Side.Left)] = 0.015, [H.Hand(Side.Right)] = 0.015, [H.Thigh(Side.Left)] = 0.04, [H.Thigh(Side.Right)] = 0.04,
        [H.Calf(Side.Left)] = 0.02, [H.Calf(Side.Right)] = 0.02,
    };

    public static BodyGeometry For(Rig rig, MassModel? mass = null)
    {
        mass ??= MassModel.For(rig);
        var feet = FootModel.For(rig, mass.Evaluator, mass.EstimatedHeight);
        var footBones = feet.Feet.SelectMany(f => new[] { f.FootBone, f.ToeBone }).Where(b => b >= 0).ToHashSet();
        var capsules = new List<BodyCapsule>();
        foreach (var s in mass.Segments)
        {
            if (s.Mass <= 0 || footBones.Contains(s.Bone)) continue;
            var std = rig.StandardNameOf(rig.Bones[s.Bone].Id);
            if (std is not null && (std.StartsWith("foot", StringComparison.Ordinal) || std.StartsWith("toe", StringComparison.Ordinal))) continue;
            var distal = s.ComRatio > 1e-6 ? s.LocalCom / s.ComRatio : s.LocalCom;
            var r = (std is not null && Radii.TryGetValue(std, out var k) ? k : 0.02) * mass.EstimatedHeight;
            capsules.Add(new BodyCapsule(s.Bone, std ?? rig.Bones[s.Bone].Name, distal, r));
        }
        return new BodyGeometry(mass, feet, capsules.ToArray());
    }

    /// <summary>Lowest surface point of the body (capsules, and the soles unless <paramref name="soles"/> is false):
    /// height relative to the ground (negative = below it) and the part.</summary>
    public (double Height, string Part) Lowest(Transform[] model, bool soles = true, bool capsules = true)
    {
        var best = double.MaxValue;
        var part = "";
        if (capsules)
            foreach (var c in Capsules)
            {
                var m = model[c.Bone];
                var y = Math.Min(m.Translation.Y, m.TransformPoint(c.Distal).Y) - c.Radius;
                if (y < best) { best = y; part = c.Part; }
            }
        if (soles)
            foreach (var f in Feet.Feet)
                foreach (var p in f.Points)
                {
                    var y = model[p.Bone].TransformPoint(p.Local).Y;
                    if (y < best) { best = y; part = $"{f.Side.ToString().ToLowerInvariant()} {p.Name}"; }
                }
        return (best == double.MaxValue ? double.PositiveInfinity : best - Ground, part);
    }

    /// <summary>Parts whose lowest point is within <paramref name="within"/> of the ground (resting contacts).</summary>
    public IReadOnlyList<string> Touching(Transform[] model, double within)
    {
        var list = new List<string>();
        foreach (var c in Capsules)
        {
            var m = model[c.Bone];
            if (Math.Min(m.Translation.Y, m.TransformPoint(c.Distal).Y) - c.Radius - Ground <= within) list.Add(c.Part);
        }
        foreach (var f in Feet.Feet)
            if (f.Points.Any(p => model[p.Bone].TransformPoint(p.Local).Y - Ground <= within)) list.Add("foot" + (f.Side == Side.Left ? "_l" : "_r"));
        return list;
    }

    /// <summary>Lowest sole point height of a foot above the ground.</summary>
    public double SoleHeight(Foot foot, Transform[] model) => foot.Points.Min(p => model[p.Bone].TransformPoint(p.Local).Y) - Ground;
}
