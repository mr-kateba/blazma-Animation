using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Motion;
using Blazma.Rigging;

namespace Blazma.Combat;

/// <param name="Direction">Direction the hit pushes, in the character's model space (horizontal; +Z forward, +X left). A hit from
/// the front pushes towards −Z.</param>
/// <param name="Strength">0–1: flinch angle 10° + 25°·strength.</param>
/// <param name="Knockback">Root travel along <paramref name="Direction"/> in metres (0 = in place).</param>
public sealed record ReactionParams(Vec3 Direction, HitHeight Height, double Strength, double Knockback, double DurationSeconds);

/// <param name="PeakFrame">Frame of the largest flinch.</param>
/// <param name="Axis">Model-space flinch axis (cross(+Y, direction)).</param>
public sealed record ReactionResult(Transform[][] Locals, long PeakFrame, double PeakDegrees, Vec3 Axis, Vec3 RootDisplacement, IReadOnlyList<string> Bones,
    IReadOnlyList<int> BoneIndices);

/// <summary>
/// Procedural hit reaction (docs/ARCHITECTURE.md §9.4): from a base pose, the spine chain (pelvis → head) bends away from the hit —
/// rotation about cross(+Y, direction) in model space, distributed over the chain by the hit height (high hits move the neck and
/// head most, low hits the pelvis) — following the impulse response of a damped angular spring θ(t) = A·e^(−ζωt)·sin(ω_d t)
/// (the hit gives the spring a velocity; ζ = 0.55 so it overshoots slightly and settles), scaled so the peak is
/// 10° + 25°·strength. The head adds a softer, later spring (it lags the chest). The last 20% fades smoothly to the base pose, so
/// the reaction ends exactly where it started and can be followed by any clip. Knockback moves the root-motion bone along the
/// hit direction with an ease-out over the first half.
/// </summary>
public static class HitReaction
{
    public const double MinSeconds = CombatLimits.MinReactionSeconds;
    public const double MaxSeconds = CombatLimits.MaxReactionSeconds;
    public const int MaxFrames = 2000;

    static readonly string[] Chain = { "pelvis", "spine_01", "spine_02", "spine_03", "neck", "head" };

    static readonly Dictionary<HitHeight, double[]> Weights = new()
    {
        [HitHeight.High] = new[] { 0.05, 0.10, 0.15, 0.20, 0.20, 0.30 },
        [HitHeight.Mid] = new[] { 0.10, 0.25, 0.25, 0.25, 0.07, 0.08 },
        [HitHeight.Low] = new[] { 0.35, 0.25, 0.20, 0.10, 0.05, 0.05 },
    };

    public static ReactionResult Build(Rig rig, Transform[] baseLocals, ReactionParams p, double fps)
    {
        if (!(p.DurationSeconds >= MinSeconds && p.DurationSeconds <= MaxSeconds))
            throw new CombatException("invalid_args", $"The reaction length must be between {MinSeconds} and {MaxSeconds} s.");
        if (!(p.Strength >= 0 && p.Strength <= 1)) throw new CombatException("invalid_args", "'strength' must be between 0 and 1.");
        if (!(p.Knockback >= 0 && p.Knockback <= CombatLimits.MaxKnockback)) throw new CombatException("invalid_args", $"'knockback' must be between 0 and {CombatLimits.MaxKnockback} m.");
        if (!(fps > 0 && fps <= 1000)) throw new CombatException("invalid_args", "Invalid frame rate.");
        var dir = new Vec3(p.Direction.X, 0, p.Direction.Z);
        if (!(dir.Length() > 1e-6) || !double.IsFinite(dir.Length())) throw new CombatException("invalid_args", "The hit direction must have a horizontal component.");
        dir = dir.Normalized();
        var frames = (int)Math.Round(p.DurationSeconds * fps) + 1;
        if (frames > MaxFrames || (double)frames * rig.Bones.Count > 2_000_000)
            throw new CombatException("too_large", $"{frames} frames × {rig.Bones.Count} bones is too many for a reaction.");

        var ev = new RigEvaluator(rig);
        var baseModel = ev.Model(baseLocals);
        var weights = Weights[p.Height == HitHeight.Any ? HitHeight.Mid : p.Height];
        var chain = new List<(int Bone, double Weight, string Name)>();
        for (var i = 0; i < Chain.Length; i++)
            if (rig.FindByStandardName(Chain[i]) is { } b) chain.Add((rig.IndexOf(b.Id), weights[i], b.Name));
        if (chain.Count == 0) throw new CombatException("no_spine", $"Rig '{rig.Name}' has no humanoid-mapped spine or head; map the pelvis/spine/head bones first.");
        var total = chain.Sum(c => c.Weight);
        var head = chain.FindIndex(c => rig.StandardNameOf(rig.Bones[c.Bone].Id) == "head");

        var axis = Vec3.Cross(Vec3.UnitY, dir).Normalized();
        var peak = (10 + 25 * p.Strength) * Math.PI / 180;
        var d = p.DurationSeconds;
        const double zeta = 0.55;
        var omega = 3.0 / (zeta * 0.7 * d);
        var body = Spring(zeta, omega);
        var lag = Spring(0.45, omega * 0.8);
        double Fade(double t)
        {
            var u = Math.Clamp((t - 0.8 * d) / (0.2 * d), 0, 1);
            return 1 - u * u * (3 - 2 * u);
        }

        var rootBone = SequenceEvaluator.RootMotionBoneOf(rig);
        var rootParent = rootBone >= 0 ? ev.ParentOf(rootBone) : -1;
        var locals = new Transform[frames][];
        var peakFrame = 0L;
        var peakAngle = 0.0;
        var rootEnd = Vec3.Zero;
        for (var f = 0; f < frames; f++)
        {
            var t = f / fps;
            var l = (Transform[])baseLocals.Clone();
            var theta = peak * body(t) * Fade(t);
            var thetaHead = 0.4 * peak * lag(t) * Fade(t);
            if (Math.Abs(theta) > peakAngle) { peakAngle = Math.Abs(theta); peakFrame = f; }
            for (var c = 0; c < chain.Count; c++)
            {
                var (bone, w, _) = chain[c];
                var angle = theta * w / total + (c == head ? thetaHead : 0);
                if (angle == 0) continue;
                // A model-space rotation about one fixed axis, expressed in the parent's (base) frame: parent deltas about the same axis
                // commute with it, so the chain's tilts add up.
                var parent = ev.ParentOf(bone);
                var pr = parent >= 0 ? baseModel[parent].Rotation : Quat.Identity;
                var delta = pr.Conjugate() * Quat.FromAxisAngle(axis, angle) * pr;
                l[bone] = l[bone] with { Rotation = (delta * l[bone].Rotation).Normalized() };
            }
            if (rootBone >= 0 && p.Knockback > 0)
            {
                var u = Math.Clamp(t / (0.5 * d), 0, 1);
                var move = dir * (p.Knockback * (1 - Math.Pow(1 - u, 3)));
                var pr = rootParent >= 0 ? baseModel[rootParent].Rotation : Quat.Identity;
                var ps = rootParent >= 0 ? baseModel[rootParent].Scale : Vec3.One;
                var lm = pr.Conjugate().Rotate(move);
                l[rootBone] = l[rootBone] with { Translation = l[rootBone].Translation + new Vec3(lm.X / ps.X, lm.Y / ps.Y, lm.Z / ps.Z) };
                rootEnd = move;
            }
            locals[f] = l;
        }
        return new ReactionResult(locals, peakFrame, peakAngle * 180 / Math.PI, axis, rootEnd, chain.Select(c => c.Name).ToList(), chain.Select(c => c.Bone).ToList());
    }

    /// <summary>Impulse response of a damped spring, normalized so its peak is 1.</summary>
    static Func<double, double> Spring(double zeta, double omega)
    {
        var wd = omega * Math.Sqrt(1 - zeta * zeta);
        var tp = Math.Atan2(Math.Sqrt(1 - zeta * zeta), zeta) / wd;
        double Raw(double t) => Math.Exp(-zeta * omega * t) * Math.Sin(wd * t);
        var norm = Raw(tp);
        return t => t <= 0 ? 0 : Raw(t) / norm;
    }
}
