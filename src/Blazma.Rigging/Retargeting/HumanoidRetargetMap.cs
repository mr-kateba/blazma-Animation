using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Rigging.Retargeting;

/// <summary>A retarget failure with a stable code (not_humanoid).</summary>
public sealed class RetargetException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The source side of a retarget: any skeleton with parents, a model-space rest pose and a humanoid map
/// (a <see cref="Rig"/>, or a generator skeleton such as SOMA).
/// </summary>
public sealed class RetargetSkeleton
{
    public RetargetSkeleton(IReadOnlyList<string> names, IReadOnlyList<int> parents, IReadOnlyList<Transform> restModel, IReadOnlyDictionary<string, int> byStandard)
    {
        if (names.Count != parents.Count || names.Count != restModel.Count) throw new ArgumentException("Names, parents and rest transforms differ in length.");
        Names = names;
        Parents = parents;
        RestModel = restModel.ToArray();
        ByStandard = byStandard;
    }

    public IReadOnlyList<string> Names { get; }
    public IReadOnlyList<int> Parents { get; }

    /// <summary>Model-space rest transforms.</summary>
    public Transform[] RestModel { get; }

    /// <summary>Standard humanoid name → joint index.</summary>
    public IReadOnlyDictionary<string, int> ByStandard { get; }

    public int Count => Names.Count;

    public static RetargetSkeleton FromRig(Rig rig)
    {
        var ev = new RigEvaluator(rig);
        var byStd = new Dictionary<string, int>();
        foreach (var m in rig.HumanoidMap)
            if (ev.IndexOf(m.Bone) is var i and >= 0) byStd[m.Standard] = i;
        return new RetargetSkeleton(rig.Bones.Select(b => b.Name).ToArray(), Enumerable.Range(0, rig.Bones.Count).Select(ev.ParentOf).ToArray(), ev.RestModel(), byStd);
    }
}

/// <summary>
/// Humanoid retargeting between any two humanoid-mapped skeletons (DECISIONS D25, D31), shared by the SOMA → rig
/// path of generated motion and the rig → rig path of <c>clip.retarget</c> and templates.
/// <list type="bullet">
/// <item><b>Rotations:</b> rest-relative world-space delta transfer per mapped bone. For source joint s and target bone b:
/// <c>G_b(t) = Δ_s(t) · C_b · Rest_b</c> with <c>Δ_s(t) = G_s(t) · Rest_s⁻¹</c> (model-space rotations) and <c>C_b</c> the
/// shortest rotation taking b's rest bone direction (towards its first standard child mapped on both sides) onto s's, so each
/// bone points the same way in the world whatever the two rest poses (T/A-pose) and local bone axes are. Feet only align their
/// heading (horizontal ankle→toe direction), so the soles stay parallel to the ground as in the source. Leaves (hand, head,
/// toe) reuse the nearest driven ancestor's correction. Unmapped target bones keep their rest local rotation.</item>
/// <item><b>Travel:</b> the pelvis (and the root, when both sides map one) moves by the source's model-space displacement from
/// rest scaled by the leg-length ratio (hip-height ratio without mapped legs).</item>
/// </list>
/// </summary>
public sealed class HumanoidRetargetMap
{
    /// <summary>Standard child used to define a bone's direction (first one mapped on both sides wins).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> AimChildren = BuildAim();

    static Dictionary<string, string[]> BuildAim()
    {
        var d = new Dictionary<string, string[]>
        {
            [H.Pelvis] = new[] { H.Spine1, H.Spine2, H.Chest, H.Neck },
            [H.Spine1] = new[] { H.Spine2, H.Chest, H.Neck },
            [H.Spine2] = new[] { H.Chest, H.Neck },
            [H.Chest] = new[] { H.Neck, H.Head },
            [H.Neck] = new[] { H.Head },
        };
        foreach (var s in new[] { Side.Left, Side.Right })
        {
            d[H.Clavicle(s)] = new[] { H.UpperArm(s) };
            d[H.UpperArm(s)] = new[] { H.LowerArm(s), H.Hand(s) };
            d[H.LowerArm(s)] = new[] { H.Hand(s) };
            d[H.Thigh(s)] = new[] { H.Calf(s), H.Foot(s) };
            d[H.Calf(s)] = new[] { H.Foot(s) };
            d[H.Foot(s)] = new[] { H.Toe(s) };
        }
        return d;
    }

    readonly Quat[] _srcRestInv;
    readonly Transform[] _restLocals;

    public HumanoidRetargetMap(RetargetSkeleton source, Rig target)
    {
        Source = source;
        Target = target;
        Evaluator = new RigEvaluator(target);
        TargetRest = Evaluator.RestModel();
        var tByStd = new Dictionary<string, int>();
        foreach (var m in target.HumanoidMap)
            if (Evaluator.IndexOf(m.Bone) is var i and >= 0) tByStd[m.Standard] = i;
        TargetByStandard = tByStd;
        var srcByStd = source.ByStandard;
        if (!tByStd.TryGetValue(H.Pelvis, out var tPelvis))
            throw new RetargetException("not_humanoid", $"Rig '{target.Name}' has no bone mapped to '{H.Pelvis}'; map the humanoid bones first (rig.map_bone).");
        if (!srcByStd.TryGetValue(H.Pelvis, out var sPelvis))
            throw new RetargetException("not_humanoid", $"The source skeleton has no bone mapped to '{H.Pelvis}'; map the humanoid bones first (rig.map_bone).");
        TargetPelvis = tPelvis;
        SourcePelvis = sPelvis;
        var srcRest = source.RestModel;

        // Pairs and rest corrections.
        var n = target.Bones.Count;
        PairOf = new int[n];
        Array.Fill(PairOf, -1);
        foreach (var (std, ti) in tByStd)
            if (srcByStd.TryGetValue(std, out var si)) PairOf[ti] = si;
        Correction = new Quat[n];
        for (var b = 0; b < n; b++)
        {
            Correction[b] = Quat.Identity;
            if (PairOf[b] < 0) continue;
            var std = target.StandardNameOf(target.Bones[b].Id)!;
            Quat? c = null;
            if (AimChildren.TryGetValue(std, out var kids))
                foreach (var kid in kids)
                    if (tByStd.TryGetValue(kid, out var tk) && srcByStd.TryGetValue(kid, out var sk))
                    {
                        var dt = TargetRest[tk].Translation - TargetRest[b].Translation;
                        var ds = srcRest[sk].Translation - srcRest[PairOf[b]].Translation;
                        if (std == H.Foot(Side.Left) || std == H.Foot(Side.Right))
                        {
                            // Feet stand flat in both rest poses: align only the heading, so a flat source foot gives a flat
                            // target sole (matching the ankle→toe slope would tilt it when the ankle heights differ).
                            dt = dt with { Y = 0 };
                            ds = ds with { Y = 0 };
                        }
                        if (dt.Length() > 1e-6 && ds.Length() > 1e-6) { c = Quat.FromTo(dt, ds); break; }
                    }
            if (c is null)
            {
                // Leaf (hand, head, toe): reuse the nearest driven ancestor's correction.
                c = Quat.Identity;
                for (var p = Evaluator.ParentOf(b); p >= 0; p = Evaluator.ParentOf(p))
                    if (PairOf[p] >= 0) { c = Correction[p]; break; }
            }
            Correction[b] = c.Value;
        }

        // Root bone: transferred only when both sides map a "root" above the pelvis (rig → rig).
        if (tByStd.TryGetValue(H.Root, out var tr) && srcByStd.TryGetValue(H.Root, out var sr) && tr != tPelvis && sr != sPelvis
            && Evaluator.IsAncestorOrSelf(tr, tPelvis) && IsAncestor(source, sr, sPelvis))
        {
            TargetRoot = tr;
            SourceRoot = sr;
        }

        for (var b = 0; b < n; b++) (PairOf[b] >= 0 ? Mapped : Unmapped).Add(target.Bones[b].Name);
        if (Unmapped.Count > 0)
            Warnings.Add($"{Unmapped.Count} bone(s) are not driven by the source motion and stay at rest: {string.Join(", ", Unmapped.Take(12))}{(Unmapped.Count > 12 ? ", …" : "")}.");
        foreach (var std in H.Required)
            if (!tByStd.ContainsKey(std)) Warnings.Add($"The rig has no bone mapped to '{std}'; that part of the motion is lost.");
            else if (!srcByStd.ContainsKey(std)) Warnings.Add($"The source has no bone mapped to '{std}'; that bone of '{target.Name}' stays at rest.");

        // Root scale: leg-length ratio, else hip-height ratio.
        var scale = LegLength(TargetRest, tByStd) is { } tl && LegLength(srcRest, srcByStd) is { } sl && sl > 1e-6 ? tl / sl
            : srcRest[sPelvis].Translation.Y > 1e-6 && TargetRest[tPelvis].Translation.Y > 1e-6 ? TargetRest[tPelvis].Translation.Y / srcRest[sPelvis].Translation.Y : 1.0;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        Scale = scale;

        _srcRestInv = srcRest.Select(t => t.Rotation.Inverse()).ToArray();
        _restLocals = target.Bones.Select(b => b.Rest).ToArray();
    }

    static bool IsAncestor(RetargetSkeleton s, int ancestor, int joint)
    {
        for (var i = joint; i >= 0; i = s.Parents[i])
            if (i == ancestor) return true;
        return false;
    }

    public RetargetSkeleton Source { get; }
    public Rig Target { get; }
    public RigEvaluator Evaluator { get; }

    /// <summary>Model-space rest pose of the target.</summary>
    public Transform[] TargetRest { get; }
    public IReadOnlyDictionary<string, int> TargetByStandard { get; }

    /// <summary>Target bone → source joint (−1 = not driven).</summary>
    public int[] PairOf { get; }
    public Quat[] Correction { get; }
    public int TargetPelvis { get; }
    public int SourcePelvis { get; }

    /// <summary>Root bones transferred on both sides, or −1.</summary>
    public int TargetRoot { get; } = -1;
    public int SourceRoot { get; } = -1;

    /// <summary>Travel scale (target leg length / source leg length).</summary>
    public double Scale { get; }
    public List<string> Warnings { get; } = new();
    public List<string> Mapped { get; } = new();
    public List<string> Unmapped { get; } = new();

    /// <summary>Target local transforms for one source frame given as model-space joint rotations and positions.</summary>
    public Transform[] Transfer(IReadOnlyList<Quat> srcWorldRot, IReadOnlyList<Vec3> srcWorldPos)
    {
        var n = Target.Bones.Count;
        var srcRest = Source.RestModel;
        var loc = new Transform[n];
        var world = new Transform[n];
        for (var b = 0; b < n; b++)
        {
            var p = Evaluator.ParentOf(b);
            var parentWorld = p < 0 ? Transform.Identity : world[p];
            var l = _restLocals[b];
            if (PairOf[b] is var sj and >= 0)
            {
                var g = (srcWorldRot[sj] * _srcRestInv[sj] * Correction[b] * TargetRest[b].Rotation).Normalized();
                l = l with { Rotation = (parentWorld.Rotation.Inverse() * g).Normalized() };
            }
            var travelFrom = b == TargetPelvis ? SourcePelvis : b == TargetRoot ? SourceRoot : -1;
            if (travelFrom >= 0)
            {
                var d = (srcWorldPos[travelFrom] - srcRest[travelFrom].Translation) * Scale;
                var desired = TargetRest[b].Translation + d;
                var w = new Transform(desired, (parentWorld.Rotation * l.Rotation).Normalized(), parentWorld.Scale);
                l = l with { Translation = RigEvaluator.LocalFromModel(parentWorld, w).Translation };
            }
            loc[b] = l;
            world[b] = p < 0 ? l : parentWorld * l;
        }
        return loc;
    }

    /// <summary>Maps a model-space point near a source joint onto the target: the offset from the joint's rest position is
    /// scaled and added to the paired target bone's rest position (used for IK targets and poles).</summary>
    public Vec3 MapPoint(string standard, Vec3 sourcePoint)
    {
        if (!Source.ByStandard.TryGetValue(standard, out var s) || !TargetByStandard.TryGetValue(standard, out var t))
            return sourcePoint * Scale;
        return TargetRest[t].Translation + (sourcePoint - Source.RestModel[s].Translation) * Scale;
    }

    public static double? LegLength(Transform[] rest, IReadOnlyDictionary<string, int> byStd)
    {
        double? total = null;
        foreach (var s in new[] { Side.Left, Side.Right })
            if (byStd.TryGetValue(H.Thigh(s), out var t) && byStd.TryGetValue(H.Calf(s), out var c) && byStd.TryGetValue(H.Foot(s), out var f))
            {
                var len = (rest[c].Translation - rest[t].Translation).Length() + (rest[f].Translation - rest[c].Translation).Length();
                total = total is null ? len : (total + len) / 2;
            }
        return total;
    }

    /// <summary>
    /// When the rig has a bone mapped to "root" above the pelvis but the source drove only the pelvis, moves the horizontal
    /// travel onto the root: it follows the ground projection of the pelvis, smoothed over ±0.25 s (symmetric window, narrowed
    /// near the ends), so the pelvis keeps its height, sway and rest offset relative to it. Every bone below the pelvis keeps
    /// exactly the same model-space transform. Returns the root bone index, or −1 when the rig has no such bone (DECISIONS D30).
    /// </summary>
    /// <param name="periodic">The motion loops (last frame = first frame + one cycle of travel): the window wraps around the cycle,
    /// so the travel speed is the same at both ends and repeats seamlessly.</param>
    public static int MoveTravelToRoot(RigEvaluator ev, IReadOnlyDictionary<string, int> byStd, int pelvis, Transform[] rest, Transform[][] locals, double fps,
        bool periodic = false)
    {
        if (!byStd.TryGetValue(H.Root, out var root) || root == pelvis || !ev.IsAncestorOrSelf(root, pelvis)) return -1;
        var frames = locals.Length;
        var models = locals.Select(ev.Model).ToArray();
        var restOffset = rest[pelvis].Translation - rest[root].Translation;
        var travel = models.Select(m =>
        {
            var d = m[pelvis].Translation - m[root].Translation - restOffset;
            return new Vec3(d.X, 0, d.Z);
        }).ToArray();
        var half = Math.Max(0, (int)Math.Round(0.25 * fps));
        var cycle = frames - 1;
        var cycleTravel = cycle > 0 ? travel[cycle] - travel[0] : Vec3.Zero;
        periodic &= cycle >= 2;
        Vec3 At(int k)
        {
            if (!periodic) return travel[k];
            var wraps = (int)Math.Floor((double)k / cycle);
            return travel[k - wraps * cycle] + cycleTravel * wraps;
        }
        for (var f = 0; f < frames; f++)
        {
            var sum = Vec3.Zero;
            var n = 0;
            var h = periodic ? half : Math.Min(half, Math.Min(f, frames - 1 - f));
            for (var k = f - h; k <= f + h; k++) { sum += At(k); n++; }
            var shift = sum / n;
            var m = models[f];
            var loc = locals[f];
            var rp = ev.ParentOf(root);
            var newRoot = m[root] with { Translation = m[root].Translation + shift };
            loc[root] = rp < 0 ? newRoot : RigEvaluator.LocalFromModel(m[rp], newRoot);
            var after = ev.Model(loc);
            var pp = ev.ParentOf(pelvis);
            loc[pelvis] = RigEvaluator.LocalFromModel(after[pp], m[pelvis]);
        }
        return root;
    }
}
