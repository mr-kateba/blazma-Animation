using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Physics;

/// <summary>Where segment masses came from.</summary>
public enum MassSource
{
    /// <summary><see cref="Bone.Mass"/> values of the rig (the mannequin: Winter's fractions).</summary>
    BoneMasses,

    /// <summary>The rig has no masses: a total mass is distributed over bones in proportion to segment length.</summary>
    LengthDistributed,
}

/// <param name="Bone">Bone index in <see cref="Rig.Bones"/>.</param>
/// <param name="Mass">Segment mass, kg.</param>
/// <param name="LocalCom">Segment centre of mass in the bone's local space (rigidly attached to the bone).</param>
/// <param name="RestCom">Segment centre of mass in model space at rest.</param>
/// <param name="ComRatio">Fraction of the way from the joint (proximal) to the distal point.</param>
public readonly record struct MassSegment(int Bone, double Mass, Vec3 LocalCom, Vec3 RestCom, double ComRatio);

/// <summary>
/// Per-bone segment masses and centres of mass, and the whole-body centre of mass of a pose.
/// <para>A segment runs from its bone's origin (proximal joint) to a distal point; its centre of mass lies at
/// <c>proximal + ratio·(distal − proximal)</c>, fixed in the bone's local frame. Humanoid-mapped bones use the
/// anatomical next joint as the distal point and Winter's COM fractions (Biomechanics and Motor Control of Human
/// Movement, table 4.1): thigh 0.433, shank 0.433, foot 0.5, upper arm 0.436, forearm 0.430, hand 0.506 of
/// wrist→knuckle (≈0.35 of the hand), trunk segments 0.5, head ≈0.45 of chin-height. Leaf segments (head, hand, toe)
/// extend along the parent bone with a length that is a fraction of the estimated body height H.
/// Unmapped bones: distal = the single child (average of children), leaf = half the parent's length, ratio 0.5.</para>
/// <para>Masses: <see cref="Bone.Mass"/> if any bone has one; otherwise the fallback total mass is distributed in
/// proportion to segment length (documented approximation for imported rigs).</para>
/// </summary>
public sealed class MassModel
{
    public const double DefaultTotalMass = 75;

    readonly MassSegment[] _segments;

    MassModel(Rig rig, RigEvaluator ev, MassSegment[] segments, MassSource source, double height)
    {
        Rig = rig;
        Evaluator = ev;
        _segments = segments;
        Source = source;
        EstimatedHeight = height;
        TotalMass = segments.Sum(s => s.Mass);
    }

    public Rig Rig { get; }
    public RigEvaluator Evaluator { get; }
    public IReadOnlyList<MassSegment> Segments => _segments;
    public double TotalMass { get; }
    public MassSource Source { get; }

    /// <summary>Estimated standing height used for leaf segment lengths (metres).</summary>
    public double EstimatedHeight { get; }

    /// <summary>Segment spec for humanoid bones: next standard joint (null = leaf), COM ratio, leaf length as a fraction of H.</summary>
    static readonly Dictionary<string, (string? Distal, double Ratio, double LeafLength)> Humanoid = BuildHumanoid();

    static Dictionary<string, (string?, double, double)> BuildHumanoid()
    {
        var d = new Dictionary<string, (string?, double, double)>
        {
            [H.Pelvis] = (H.Spine1, 0.5, 0),
            [H.Spine1] = (H.Spine2, 0.5, 0),
            [H.Spine2] = (H.Chest, 0.5, 0),
            [H.Chest] = (H.Neck, 0.5, 0),
            [H.Neck] = (H.Head, 0.5, 0),
            [H.Head] = (null, 0.45, 0.13),
        };
        foreach (var s in new[] { Side.Left, Side.Right })
        {
            d[H.Clavicle(s)] = (H.UpperArm(s), 0.5, 0);
            d[H.UpperArm(s)] = (H.LowerArm(s), 0.436, 0);
            d[H.LowerArm(s)] = (H.Hand(s), 0.430, 0);
            d[H.Hand(s)] = (null, 0.35, 0.108);
            d[H.Thigh(s)] = (H.Calf(s), 0.433, 0);
            d[H.Calf(s)] = (H.Foot(s), 0.433, 0);
            d[H.Foot(s)] = (H.Toe(s), 0.5, 0);
            d[H.Toe(s)] = (null, 0.5, 0.03);
        }
        return d;
    }

    public static MassModel For(Rig rig, double fallbackTotalMass = DefaultTotalMass)
    {
        var ev = new RigEvaluator(rig);
        var rest = ev.RestModel();
        var n = rig.Bones.Count;
        var children = new List<int>[n];
        for (var i = 0; i < n; i++) children[i] = new List<int>();
        for (var i = 0; i < n; i++) if (ev.ParentOf(i) >= 0) children[ev.ParentOf(i)].Add(i);

        var minY = n == 0 ? 0 : rest.Min(t => t.Translation.Y);
        var maxY = n == 0 ? 0 : rest.Max(t => t.Translation.Y);
        // The highest joint (head base) sits at ≈ 0.88 of standing height.
        var height = Math.Max(0.1, 1.14 * (maxY - minY));

        var proximal = new Vec3[n];
        var com = new Vec3[n];
        var ratio = new double[n];
        var length = new double[n];
        for (var i = 0; i < n; i++)
        {
            var p = rest[i].Translation;
            Vec3 distal;
            var r = 0.5;
            var std = rig.StandardNameOf(rig.Bones[i].Id);
            if (std is not null && Humanoid.TryGetValue(std, out var spec))
            {
                r = spec.Ratio;
                var next = spec.Distal is null ? null : rig.FindByStandardName(spec.Distal);
                var ni = next is null ? -1 : rig.IndexOf(next.Id);
                if (ni >= 0 && ev.IsAncestorOrSelf(i, ni) && ni != i) distal = rest[ni].Translation;
                else if (spec.LeafLength > 0) distal = p + ParentDirection(i) * (spec.LeafLength * height);
                else distal = Generic(i);
            }
            else distal = Generic(i);
            proximal[i] = p;
            ratio[i] = r;
            com[i] = Vec3.Lerp(p, distal, r);
            length[i] = (distal - p).Length();
        }

        var source = rig.Bones.Any(b => b.Mass > 0) ? MassSource.BoneMasses : MassSource.LengthDistributed;
        var totalLength = length.Sum();
        var segments = new List<MassSegment>();
        for (var i = 0; i < n; i++)
        {
            var mass = source == MassSource.BoneMasses ? Math.Max(0, rig.Bones[i].Mass)
                : totalLength > 1e-9 ? fallbackTotalMass * length[i] / totalLength : fallbackTotalMass / n;
            if (mass <= 0) continue;
            segments.Add(new MassSegment(i, mass, rest[i].Inverse().TransformPoint(com[i]), com[i], ratio[i]));
        }
        return new MassModel(rig, ev, segments.ToArray(), source, height);

        Vec3 ParentDirection(int i)
        {
            var parent = ev.ParentOf(i);
            var d = parent < 0 ? Vec3.Zero : rest[i].Translation - rest[parent].Translation;
            return d.LengthSquared() < 1e-12 ? Vec3.UnitY : d.Normalized();
        }

        Vec3 Generic(int i)
        {
            if (children[i].Count > 0)
            {
                var sum = Vec3.Zero;
                foreach (var c in children[i]) sum += rest[c].Translation;
                return sum / children[i].Count;
            }
            var parent = ev.ParentOf(i);
            if (parent < 0) return rest[i].Translation;
            return rest[i].Translation + (rest[i].Translation - rest[parent].Translation) * 0.5;
        }
    }

    /// <summary>Model-space centre of mass of each segment for a pose's model transforms.</summary>
    public Vec3[] SegmentCentres(Transform[] model)
    {
        var r = new Vec3[_segments.Length];
        for (var i = 0; i < _segments.Length; i++) r[i] = model[_segments[i].Bone].TransformPoint(_segments[i].LocalCom);
        return r;
    }

    /// <summary>Whole-body centre of mass: Σ mᵢ·cᵢ / Σ mᵢ.</summary>
    public Vec3 CenterOfMass(Transform[] model)
    {
        if (TotalMass <= 0) return model.Length > 0 ? model[0].Translation : Vec3.Zero;
        var sum = Vec3.Zero;
        foreach (var s in _segments) sum += model[s.Bone].TransformPoint(s.LocalCom) * s.Mass;
        return sum / TotalMass;
    }

    public Vec3 CenterOfMass(Pose pose) => CenterOfMass(Evaluator.Model(pose));
}
