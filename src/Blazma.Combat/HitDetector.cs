using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;

namespace Blazma.Combat;

public sealed record HitTestOptions
{
    /// <summary>Defender placement in the world (the attacker stands at the origin facing +Z). Default: 1 m in front, facing it.</summary>
    public Transform DefenderPlacement { get; init; } = CombatEvaluator.Placement(new Vec3(0, 0, 1), 180);

    /// <summary>First/last attack-clip frame tested (default: the whole clip).</summary>
    public long? StartFrame { get; init; }
    public long? EndFrame { get; init; }

    /// <summary>Upper bound of sub-steps between two frames (adaptive below it; 1–<see cref="HitDetector.MaxSubstepsLimit"/>).</summary>
    public int MaxSubsteps { get; init; } = 8;

    /// <summary>Only this hitbox (default: all).</summary>
    public ObjectId? Hitbox { get; init; }
}

/// <param name="Frame">First attack-clip frame whose test (sweep from the previous frame) found the contact.</param>
/// <param name="Time">Exact (fractional) frame of the contact.</param>
/// <param name="Normal">Contact normal (world) pointing out of the hurtbox, towards the hitbox.</param>
/// <param name="Direction">World direction the hit travels (hitbox velocity at the contact, or −normal when it barely moves).</param>
/// <param name="LocalDirection">The same direction in the defender's frame (+Z its forward, +X its left).</param>
/// <param name="From">Side of the defender the hit comes from.</param>
/// <param name="Swept">True when only the swept volume between two sub-steps touched (a fast swing that would tunnel).</param>
public sealed record HitInfo(Hitbox Hitbox, Hurtbox Hurtbox, string BoneName, long Frame, double Time, Vec3 Point, Vec3 Normal, double Depth,
    Vec3 Velocity, Vec3 Direction, Vec3 LocalDirection, HitDirection From, HitZone Zone, double Strength, bool Swept);

public sealed class HitTestReport
{
    public List<HitInfo> Hits { get; } = new();
    public List<string> Warnings { get; } = new();
    public long StartFrame { get; set; }
    public long EndFrame { get; set; }
    public int FramesTested { get; set; }

    /// <summary>Pose evaluations (frames + sub-steps) actually done.</summary>
    public int Evaluations { get; set; }
    public HitInfo? First => Hits.Count == 0 ? null : Hits.MinBy(h => h.Time);
}

/// <summary>
/// Swept hit detection between an attack clip's hitboxes and a defender's hurtboxes (docs/ARCHITECTURE.md §9.3). For each frame
/// where a hitbox is active, the interval since the previous frame is split into adaptive sub-steps (enough that the hitbox moves at
/// most its own thickness plus the thinnest hurtbox radius per step, capped by <see cref="HitTestOptions.MaxSubsteps"/>); both characters are evaluated at
/// each sub-step time (the real animation, not an interpolation), the hitbox is tested against every hurtbox, and — so a fast
/// swing cannot pass through a thin target between two sub-steps — so is the volume it swept since the previous sub-step. Each
/// hitbox reports its first contact only (one hit per hitbox); an instant contact's time is refined by bisection inside its sub-step
/// (the reported frame is the first frame whose test from the previous frame finds it, i.e. ⌈time⌉).
/// </summary>
public static class HitDetector
{
    public const int MaxFrames = 10_000;
    public const int MaxSubstepsLimit = 64;

    /// <summary>Bisection steps that refine a contact time inside its sub-step.</summary>
    public const int RefineSteps = 10;

    /// <summary>Budget of shape pair tests (frames × sub-steps × hitboxes × hurtboxes).</summary>
    public const double MaxPairWork = 5_000_000;

    /// <summary>Budget of bone evaluations (frames × sub-steps × (attacker + defender bones)).</summary>
    public const double MaxPoseWork = 20_000_000;

    public static HitTestReport Run(Project project, Character attacker, Clip attack, Character defender, Clip? defenderClip, HitTestOptions o)
    {
        var rigA = project.FindRig(attacker.Rig) ?? throw new CombatException("not_found", $"The rig of '{attacker.Name}' does not exist.");
        var rigD = project.FindRig(defender.Rig) ?? throw new CombatException("not_found", $"The rig of '{defender.Name}' does not exist.");
        if (!attack.Skeleton.IsEmpty && attack.Skeleton != rigA.Id)
            throw new CombatException("rig_mismatch", $"Clip '{attack.Name}' animates another rig than '{attacker.Name}'.");
        if (defenderClip is not null && !defenderClip.Skeleton.IsEmpty && defenderClip.Skeleton != rigD.Id)
            throw new CombatException("rig_mismatch", $"Clip '{defenderClip.Name}' animates another rig than '{defender.Name}'.");
        if (o.MaxSubsteps is < 1 or > MaxSubstepsLimit) throw new CombatException("invalid_args", $"'maxSubsteps' must be between 1 and {MaxSubstepsLimit}.");
        var placement = o.DefenderPlacement;
        if (!Finite(placement.Translation) || Math.Abs(placement.Translation.X) > 1000 || Math.Abs(placement.Translation.Y) > 1000 || Math.Abs(placement.Translation.Z) > 1000)
            throw new CombatException("invalid_args", "The defender position must be finite and within ±1000 m.");

        var hitboxes = (attack.Combat?.Hitboxes ?? new List<Hitbox>()).Where(h => o.Hitbox is not { } only || h.Id == only).ToList();
        if (hitboxes.Count == 0)
            throw new CombatException(o.Hitbox is null ? "no_hitboxes" : "not_found", o.Hitbox is null ? $"Clip '{attack.Name}' has no hitbox." : $"Hitbox '{o.Hitbox}' is not in clip '{attack.Name}'.");
        var hurtboxes = (defender.Combat?.Hurtboxes ?? new List<Hurtbox>()).Where(h => h.Enabled && rigD.IndexOf(h.Bone) >= 0).ToList();
        if (hurtboxes.Count == 0) throw new CombatException("no_hurtboxes", $"'{defender.Name}' has no enabled hurtbox (run hurtbox.generate).");

        var fps = attack.Fps;
        var last = Math.Max(0, attack.DurationFrames - 1);
        var start = o.StartFrame ?? 0;
        var end = Math.Min(o.EndFrame ?? last, last);
        if (start < 0 || start > end) throw new CombatException("invalid_args", $"The frame range must satisfy 0 ≤ startFrame ≤ endFrame ≤ {last}.");
        var frames = end - start + 1;
        if (frames > MaxFrames) throw new CombatException("too_large", $"{frames} frames to test (at most {MaxFrames:N0}).");
        var steps = (double)frames * o.MaxSubsteps;
        if (steps * hitboxes.Count * hurtboxes.Count > MaxPairWork)
            throw new CombatException("too_large", $"{frames} frames × {o.MaxSubsteps} sub-steps × {hitboxes.Count} hitboxes × {hurtboxes.Count} hurtboxes is more than {MaxPairWork:N0} tests.");
        if (steps * (rigA.Bones.Count + rigD.Bones.Count) > MaxPoseWork)
            throw new CombatException("too_large", $"{frames} frames × {o.MaxSubsteps} sub-steps × {rigA.Bones.Count + rigD.Bones.Count} bones is more than {MaxPoseWork:N0} bone evaluations.");

        var report = new HitTestReport { StartFrame = start, EndFrame = end, FramesTested = (int)frames };
        var evA = new RigEvaluator(rigA);
        var evD = new RigEvaluator(rigD);
        var staticDefender = defenderClip is null ? evD.Model(defender.Pose) : null;
        var cache = new Dictionary<double, (Transform[] A, Transform[] D, Flicks T)>();
        (Transform[] A, Transform[] D, Flicks T) At(double frame)
        {
            if (cache.TryGetValue(frame, out var hit)) return hit;
            var t = CombatEvaluator.TimeOf(frame, fps);
            var a = evA.Model(ClipEvaluator.Evaluate(attack, rigA, t));
            var d = staticDefender ?? evD.Model(ClipEvaluator.Evaluate(defenderClip!, rigD, ClipEvaluator.PlaybackTime(defenderClip!, t)));
            report.Evaluations++;
            if (cache.Count > 4 * MaxSubstepsLimit) cache.Clear(); // only the current and previous frame are needed
            return cache[frame] = (a, d, t);
        }
        WorldShape? HitShape(Hitbox h, double frame)
        {
            var s = At(frame);
            return CombatEvaluator.HitboxShape(project, h, attack, rigA, attacker.Id, s.A, s.T, Transform.Identity);
        }
        List<(Hurtbox Box, WorldShape Shape)> HurtShapes(double frame)
        {
            var s = At(frame);
            var list = new List<(Hurtbox, WorldShape)>(hurtboxes.Count);
            foreach (var h in hurtboxes)
                if (CombatEvaluator.HurtboxShape(rigD, h, s.D, placement) is { } w) list.Add((h, w));
            return list;
        }

        var minHurt = hurtboxes.Min(h => Thickness(h.Shape));
        var done = new HashSet<ObjectId>();
        var broken = new HashSet<ObjectId>();
        var capped = false;
        for (var f = start; f <= end; f++)
        {
            foreach (var h in hitboxes)
            {
                if (done.Contains(h.Id) || broken.Contains(h.Id) || !CombatEvaluator.IsActiveFrame(h, f, fps)) continue;
                if (HitShape(h, f) is not { } cur)
                {
                    broken.Add(h.Id);
                    report.Warnings.Add($"Hitbox '{h.Name}': its bone or prop no longer exists; skipped.");
                    continue;
                }
                var sweep = f > start && CombatEvaluator.IsActiveFrame(h, f - 1, fps);
                if (!sweep)
                {
                    if (TestAt(h, f, cur, null) is { } instant) Record(instant);
                    continue;
                }
                var prev = HitShape(h, f - 1)!.Value;
                var moved = Math.Max((cur.A - prev.A).Length(), (cur.B - prev.B).Length());
                var needed = Math.Ceiling(moved / Math.Max(1e-4, Thickness(h.Shape) + minHurt));
                if (!double.IsFinite(needed)) needed = o.MaxSubsteps;
                if (needed > o.MaxSubsteps) capped = true;
                var n = (int)Math.Clamp(needed, 1, o.MaxSubsteps);
                for (var s = 1; s <= n; s++)
                {
                    var t = f - 1 + (double)s / n;
                    var shape = s == n ? cur : HitShape(h, t)!.Value;
                    if (TestAt(h, t, shape, prev) is { } contact)
                    {
                        Record(contact.Swept ? contact : Refine(h, f - 1 + (double)(s - 1) / n, t, contact));
                        break;
                    }
                    prev = shape;
                }
            }
        }
        if (capped) report.Warnings.Add($"A hitbox moved more than its thickness plus the thinnest hurtbox radius per sub-step even with {o.MaxSubsteps} sub-steps; the swept-volume test still catches pass-through contacts, but the contact time is less precise (raise maxSubsteps).");
        report.Hits.Sort((a, b) => a.Time.CompareTo(b.Time));
        return report;

        // The contact is inside (lo, hi]: bisect on the real animation (lo has no overlap, hi has) to ~1/1000 of a frame.
        HitInfo Refine(Hitbox h, double lo, double hi, HitInfo best)
        {
            for (var i = 0; i < RefineSteps && hi - lo > 1e-4; i++)
            {
                var mid = (lo + hi) / 2;
                if (TestAt(h, mid, HitShape(h, mid)!.Value, null) is { } m) { hi = mid; best = m; }
                else lo = mid;
            }
            return best;
        }

        void Record(HitInfo hit)
        {
            done.Add(hit.Hitbox.Id);
            report.Hits.Add(hit);
        }

        HitInfo? TestAt(Hitbox h, double t, WorldShape shape, WorldShape? previous)
        {
            var hurts = HurtShapes(t);
            (Hurtbox Box, Contact C)? best = null;
            var swept = false;
            foreach (var (box, hs) in hurts)
                if (Collision.Test(shape, hs, out var c) && (best is null || c.Depth > best.Value.C.Depth)) best = (box, c);
            if (best is null && previous is { } p)
            {
                foreach (var sweepShape in SweptVolume(p, shape))
                    foreach (var (box, hs) in hurts)
                        if (Collision.Test(sweepShape, hs, out var c) && (best is null || c.Depth > best.Value.C.Depth)) best = (box, c);
                swept = best is not null;
            }
            if (best is not { } b) return null;
            return Describe(h, b.Box, b.C, t, shape, swept);
        }

        HitInfo Describe(Hitbox h, Hurtbox box, Contact c, double t, WorldShape shape, bool swept)
        {
            // Velocity of the hitbox point nearest the contact, by a backward difference over a small step.
            var dt = 1.0 / Math.Max(1, o.MaxSubsteps);
            var u = SegmentParam(shape, c.Point);
            Vec3 velocity = Vec3.Zero;
            if (t - dt >= 0 && HitShape(h, t - dt) is { } before)
                velocity = (Lerp(shape, u) - Lerp(before, u)) / (dt / fps.FramesPerSecond);
            else if (HitShape(h, t + dt) is { } after)
                velocity = (Lerp(after, u) - Lerp(shape, u)) / (dt / fps.FramesPerSecond);
            var dir = velocity.Length() > 0.05 ? velocity.Normalized() : -c.Normal;
            var local = placement.Rotation.Conjugate().Rotate(dir);
            var horizontal = new Vec3(local.X, 0, local.Z);
            if (horizontal.Length() < 0.05)
            {
                var n = placement.Rotation.Conjugate().Rotate(-c.Normal);
                horizontal = new Vec3(n.X, 0, n.Z);
            }
            if (horizontal.Length() < 1e-6) horizontal = new Vec3(0, 0, -1); // straight down: treat as pushed back (hit from the front)
            horizontal = horizontal.Normalized();
            var from = -horizontal;
            var side = from.Z >= Math.Abs(from.X) ? HitDirection.Front
                : -from.Z >= Math.Abs(from.X) ? HitDirection.Back
                : from.X > 0 ? HitDirection.Left : HitDirection.Right;
            var bone = rigD.FindBone(box.Bone)?.Name ?? "";
            return new HitInfo(h, box, bone, (long)Math.Ceiling(t - 1e-9), t, c.Point, c.Normal, c.Depth, velocity, dir, horizontal, side, box.Zone, StrengthOf(h), swept);
        }
    }

    /// <summary>Hit strength 0–1 from a hitbox: knockback 1 m or damage 100 is a full-strength hit.</summary>
    public static double StrengthOf(Hitbox h) => Math.Clamp(Math.Max(h.Knockback / 1.0, h.Damage / 100.0), 0, 1);

    /// <summary>Capsules covering the volume a sphere/capsule/box swept between two placements (conservative for spheres and capsules;
    /// a box sweeps its centre with its smallest half extent).</summary>
    static IEnumerable<WorldShape> SweptVolume(WorldShape from, WorldShape to)
    {
        if (to.IsBox)
        {
            yield return WorldShape.Capsule(from.A, to.A, to.Thickness);
            yield break;
        }
        yield return WorldShape.Capsule(from.A, to.A, to.Radius);
        if (to.Kind == CollisionShapeKind.Capsule)
        {
            yield return WorldShape.Capsule(from.B, to.B, to.Radius);
            yield return WorldShape.Capsule(from.Center, to.Center, to.Radius);
        }
    }

    static double Thickness(CollisionShape s) => s.Kind == CollisionShapeKind.Box ? Math.Min(s.Size.X, Math.Min(s.Size.Y, s.Size.Z)) / 2 : s.Radius;

    static double SegmentParam(WorldShape s, Vec3 p)
    {
        var d = s.B - s.A;
        var l2 = d.LengthSquared();
        return l2 < 1e-18 ? 0 : Math.Clamp(Vec3.Dot(p - s.A, d) / l2, 0, 1);
    }

    static Vec3 Lerp(WorldShape s, double u) => s.A + (s.B - s.A) * u;
    static bool Finite(Vec3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

    /// <summary>The reaction rule of <paramref name="combat"/> that fits a hit best (most specific; earliest on ties), or null.</summary>
    public static ReactionRule? PickReaction(CharacterCombat? combat, HitDirection from, HitHeight height, double strength)
    {
        if (combat is null) return null;
        ReactionRule? best = null;
        var bestScore = -1;
        foreach (var r in combat.Reactions)
        {
            if (r.Direction != HitDirection.Any && r.Direction != from) continue;
            if (r.Height != HitHeight.Any && r.Height != height) continue;
            if (strength < r.MinStrength || strength > r.MaxStrength) continue;
            var score = (r.Direction != HitDirection.Any ? 2 : 0) + (r.Height != HitHeight.Any ? 1 : 0);
            if (score > bestScore) { best = r; bestScore = score; }
        }
        return best;
    }

    public static HitHeight HeightOf(HitZone zone) => zone switch { HitZone.High => HitHeight.High, HitZone.Low => HitHeight.Low, _ => HitHeight.Mid };
}
