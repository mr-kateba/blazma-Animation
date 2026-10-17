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

    /// <summary>The rig has no masses and no humanoid pelvis: a total mass is distributed over bones in proportion to segment length.</summary>
    LengthDistributed,

    /// <summary>
    /// The rig has no masses but is humanoid-mapped: Winter's segment fractions of a total mass on the mapped bones
    /// (fractions of unmapped standard segments go to their nearest mapped standard ancestor); unmapped bones weigh nothing.
    /// </summary>
    HumanoidFractions,
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
/// <para>Masses: <see cref="Bone.Mass"/> if any bone has one. Otherwise, for a rig with a humanoid-mapped pelvis, Winter's
/// segment fractions (<see cref="WinterFractions"/>, the mannequin's) of the fallback total mass on the mapped bones: a
/// standard segment that is not mapped (e.g. no neck or clavicle bone) adds its fraction to its nearest mapped standard
/// ancestor, and unmapped bones (twist, finger, helper, a floor-level root) get no mass, so they cannot pull the COM.
/// A mapped segment whose next standard joint is missing extends to the next mapped one down the chain.
/// Only rigs with neither masses nor a pelvis fall back to distributing the total in proportion to segment length.</para>
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

    /// <summary>Winter's mass fractions of total body mass per standard segment (sum 1; the mannequin's bone masses).</summary>
    public static readonly IReadOnlyDictionary<string, double> WinterFractions = BuildFractions();

    /// <summary>Anatomical parent of each standard segment (where an unmapped segment's mass goes).</summary>
    static readonly Dictionary<string, string?> StandardParent = BuildParents();

    static Dictionary<string, double> BuildFractions()
    {
        var d = new Dictionary<string, double>
        {
            [H.Pelvis] = 0.142, [H.Spine1] = 0.100, [H.Spine2] = 0.100, [H.Chest] = 0.147, [H.Neck] = 0.013, [H.Head] = 0.068,
        };
        foreach (var s in new[] { Side.Left, Side.Right })
        {
            d[H.Clavicle(s)] = 0.004; d[H.UpperArm(s)] = 0.028; d[H.LowerArm(s)] = 0.016; d[H.Hand(s)] = 0.006;
            d[H.Thigh(s)] = 0.100; d[H.Calf(s)] = 0.0465; d[H.Foot(s)] = 0.0120; d[H.Toe(s)] = 0.0025;
        }
        return d;
    }

    static Dictionary<string, string?> BuildParents()
    {
        var d = new Dictionary<string, string?>
        {
            [H.Pelvis] = null, [H.Spine1] = H.Pelvis, [H.Spine2] = H.Spine1, [H.Chest] = H.Spine2, [H.Neck] = H.Chest, [H.Head] = H.Neck,
        };
        foreach (var s in new[] { Side.Left, Side.Right })
        {
            d[H.Clavicle(s)] = H.Chest; d[H.UpperArm(s)] = H.Clavicle(s); d[H.LowerArm(s)] = H.UpperArm(s); d[H.Hand(s)] = H.LowerArm(s);
            d[H.Thigh(s)] = H.Pelvis; d[H.Calf(s)] = H.Thigh(s); d[H.Foot(s)] = H.Calf(s); d[H.Toe(s)] = H.Foot(s);
        }
        return d;
    }

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

        var height = EstimateHeight(rest);

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
                var ni = -1;
                // The next mapped standard joint down the chain (skipping unmapped ones, e.g. no spine_02 or neck).
                for (var d = spec.Distal; d is not null && ni < 0; d = Humanoid[d].Distal)
                    if (rig.FindByStandardName(d) is { } next && rig.IndexOf(next.Id) is var k && k >= 0 && k != i && ev.IsAncestorOrSelf(i, k)) ni = k;
                if (ni >= 0) distal = rest[ni].Translation;
                else if (spec.LeafLength > 0) distal = p + ParentDirection(i) * (spec.LeafLength * height);
                else if (children[i].Count == 0 && (std == H.Foot(Side.Left) || std == H.Foot(Side.Right)))
                    distal = p + Vec3.UnitZ * (0.072 * height); // a foot without toe or child bone: forward (+Z), ≈ ankle→ball
                else distal = Generic(i);
            }
            else distal = Generic(i);
            proximal[i] = p;
            ratio[i] = r;
            com[i] = Vec3.Lerp(p, distal, r);
            length[i] = (distal - p).Length();
        }

        var pelvis = rig.FindByStandardName(H.Pelvis);
        var source = rig.Bones.Any(b => b.Mass > 0) ? MassSource.BoneMasses
            : pelvis is not null && rig.IndexOf(pelvis.Id) >= 0 ? MassSource.HumanoidFractions
            : MassSource.LengthDistributed;
        var fraction = source == MassSource.HumanoidFractions ? HumanoidFractions(rig) : null;
        var totalLength = length.Sum();
        var segments = new List<MassSegment>();
        for (var i = 0; i < n; i++)
        {
            var mass = source switch
            {
                MassSource.BoneMasses => double.IsFinite(rig.Bones[i].Mass) ? Math.Max(0, rig.Bones[i].Mass) : 0,
                MassSource.HumanoidFractions => fallbackTotalMass * fraction![i],
                _ => totalLength > 1e-9 ? fallbackTotalMass * length[i] / totalLength : fallbackTotalMass / n,
            };
            if (!(mass > 0) || !double.IsFinite(com[i].X + com[i].Y + com[i].Z)) continue;
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

    /// <summary>
    /// Per bone index: its share of the body mass for a humanoid-mapped rig without masses. Each standard segment's
    /// Winter fraction goes to its bone, or, when it is not mapped, to the nearest mapped standard ancestor; whatever
    /// is still left (no mapped ancestor) is spread over the mapped segments in proportion to their shares.
    /// </summary>
    static double[] HumanoidFractions(Rig rig)
    {
        var share = new double[rig.Bones.Count];
        var leftover = 0.0;
        foreach (var (std, f) in WinterFractions)
        {
            var target = -1;
            for (var s = (string?)std; s is not null && target < 0; s = StandardParent[s])
                if (rig.FindByStandardName(s) is { } b) target = rig.IndexOf(b.Id);
            if (target >= 0) share[target] += f; else leftover += f;
        }
        var mapped = share.Sum();
        if (leftover > 0 && mapped > 0)
            for (var i = 0; i < share.Length; i++) share[i] += leftover * share[i] / mapped;
        return share;
    }

    /// <summary>
    /// Standing height from rest joints: the highest joint (head base) sits at ≈ 0.88 of it. Like the foot model, a
    /// lowest joint between 0 and 0.2 m is taken to stand on the ground plane y = 0.
    /// </summary>
    public static double EstimateHeight(Transform[] rest)
    {
        if (rest.Length == 0) return 0.1;
        var minY = rest.Min(t => t.Translation.Y);
        var maxY = rest.Max(t => t.Translation.Y);
        var floor = minY is >= 0 and <= 0.2 ? 0 : minY;
        return Math.Max(0.1, 1.14 * (maxY - floor));
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
