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

    /// <summary>Pose evaluations (distinct sample times: frames, sub-steps, refinement and velocity samples) actually done.</summary>
    public int Evaluations { get; set; }

    /// <summary>Bone evaluations actually done (attacker + moving defender, per sample time).</summary>
    public double BoneWork { get; set; }

    /// <summary>Most sub-steps any frame interval needed for the guaranteed step bound (may exceed <see cref="HitTestOptions.MaxSubsteps"/>).</summary>
    public int SubstepsRequired { get; set; }

    /// <summary>Most sub-steps actually used in one frame interval (the shared grid).</summary>
    public int SubstepsUsed { get; set; }

    /// <summary>True when the sub-step cap was below what the step bound needed (or a refinement was cut short): a contact
    /// could have been missed where the motion between sub-steps is not a rigid swing (warning code may_have_missed_contacts).</summary>
    public bool MayHaveMissedContacts { get; set; }

    public HitInfo? First => Hits.Count == 0 ? null : Hits.MinBy(h => h.Time);
}

/// <summary>
/// Swept hit detection between an attack clip's hitboxes and a defender's hurtboxes (docs/ARCHITECTURE.md §9.3).
/// <para>For each frame interval (f−1, f] where a hitbox stays active, ONE sub-step grid is shared by all hitboxes and both
/// characters: n = max over (hitbox, hurtbox) pairs of the steps needed so that no point of the hitbox moves more than half the
/// thinner shape of the pair RELATIVE to the hurtbox per step (centre chord with an arc-length correction for the rotation, plus
/// the rotation of the farthest extreme point: capsule end, box corner), capped by <see cref="HitTestOptions.MaxSubsteps"/>. Each
/// sample time is evaluated once per character.</para>
/// <para>Each sub-step is tested in the hurtbox's frame (so defender motion counts): the hitbox's relative pose is interpolated
/// (centre lerp, rotation slerp) at enough geometric samples, each inflated by a conservative margin = sampling gap + the arc
/// sagitta of a rigid swing (chord/2 · tan(θ/4)) + a slack for non-rigid relative motion. A candidate is resolved on the REAL
/// animation by time bisection down to 1/1024 frame (earliest contact first); the reported time is that of a real overlap, or —
/// when a tiny interval still moves farther than the shapes are thick — the swept contact. When the required sub-steps exceed the
/// cap, the report says so (<see cref="HitTestReport.MayHaveMissedContacts"/>).</para>
/// <para>Work is bounded: frames × MaxSubsteps × (attacker + defender bones) plus the refinement allowance is checked before any
/// work, and the bone evaluations actually done are enforced during the test (too_large).</para>
/// </summary>
public static class HitDetector
{
    public const int MaxFrames = 10_000;
    public const int MaxSubstepsLimit = 64;

    /// <summary>Bisection depth of the refinement inside a sub-step (contact time to ~1/1024 frame).</summary>
    public const int RefineSteps = 10;

    /// <summary>Smallest refinement interval (frames).</summary>
    public const double MinInterval = 1.0 / 1024;

    /// <summary>Refinement nodes allowed per (hitbox, sub-step) before giving up on it (flagged as may_have_missed_contacts).</summary>
    public const int MaxResolveNodes = 256;

    /// <summary>Most interpolated poses per swept pair test.</summary>
    public const int MaxGeometricSamples = 32;

    /// <summary>Budget of shape pair tests (frames × sub-steps × hitboxes × hurtboxes).</summary>
    public const double MaxPairWork = 5_000_000;

    /// <summary>Budget of bone evaluations (frames × sub-steps × (attacker + defender bones) + refinement), checked before and during.</summary>
    public const double MaxPoseWork = 20_000_000;

    /// <summary>Code of the warning raised when the sub-step cap was too low for the step bound.</summary>
    public const string MissedWarningCode = "may_have_missed_contacts";

    sealed class Sample(double frame)
    {
        public double Frame { get; } = frame;
        public Flicks T;
        public Transform[] A = Array.Empty<Transform>();
        public Transform[] D = Array.Empty<Transform>();
        public WorldShape?[] Hit = Array.Empty<WorldShape?>();
        public bool[] HitKnown = Array.Empty<bool>();
        public WorldShape[]? Hurt;
    }

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
        var report = new HitTestReport();
        var hurtboxes = new List<Hurtbox>();
        foreach (var h in defender.Combat?.Hurtboxes ?? new List<Hurtbox>())
        {
            if (!h.Enabled) continue;
            if (rigD.IndexOf(h.Bone) < 0) { report.Warnings.Add($"Hurtbox '{h.Name}': its bone no longer exists; skipped."); continue; }
            hurtboxes.Add(h);
        }
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
        var planned = PlannedPoseWork(frames, o.MaxSubsteps, hitboxes.Count, rigA.Bones.Count + rigD.Bones.Count);
        if (planned > MaxPoseWork)
            throw new CombatException("too_large", $"{frames} frames × {o.MaxSubsteps} sub-steps (+ refinement) × {rigA.Bones.Count + rigD.Bones.Count} bones is more than {MaxPoseWork:N0} bone evaluations.");
        report.StartFrame = start;
        report.EndFrame = end;
        report.FramesTested = (int)frames;

        var evA = new RigEvaluator(rigA);
        var evD = new RigEvaluator(rigD);
        var staticDefender = defenderClip is null ? evD.Model(defender.Pose) : null;
        var costPerSample = rigA.Bones.Count + (staticDefender is null ? rigD.Bones.Count : 0);
        var restD = evD.RestModel();
        var headingBone = SequenceEvaluator.HeadingBoneOf(rigD);
        var hurtLocal = hurtboxes.Select(h => WorldShape.From(h.Shape, Transform.Identity)).ToArray();
        var hurtThick = hurtboxes.Select(h => Thickness(h.Shape)).ToArray();
        var hurtBound = hurtboxes.Select(h => Bound(h.Shape)).ToArray();

        // Samples of the current frame interval only (each time evaluated once per character); older ones are dropped per frame.
        var cache = new Dictionary<double, Sample>();
        Sample At(double frame)
        {
            if (cache.TryGetValue(frame, out var hit)) return hit;
            report.BoneWork += costPerSample;
            if (report.BoneWork > MaxPoseWork)
                throw new CombatException("too_large", $"The hit test was stopped after {MaxPoseWork:N0} bone evaluations (the refinement needed more than planned); test fewer frames or hitboxes.");
            var t = CombatEvaluator.TimeOf(frame, fps);
            var s = new Sample(frame)
            {
                T = t,
                A = evA.Model(ClipEvaluator.Evaluate(attack, rigA, t)),
                D = staticDefender ?? evD.Model(ClipEvaluator.Evaluate(defenderClip!, rigD, ClipEvaluator.PlaybackTime(defenderClip!, t))),
                Hit = new WorldShape?[hitboxes.Count],
                HitKnown = new bool[hitboxes.Count],
            };
            report.Evaluations++;
            return cache[frame] = s;
        }
        WorldShape? HitOf(Sample s, int i)
        {
            if (!s.HitKnown[i])
            {
                s.Hit[i] = CombatEvaluator.HitboxShape(project, hitboxes[i], attack, rigA, attacker.Id, s.A, s.T, Transform.Identity);
                s.HitKnown[i] = true;
            }
            return s.Hit[i];
        }
        WorldShape[] HurtOf(Sample s)
        {
            if (s.Hurt is null)
            {
                s.Hurt = new WorldShape[hurtboxes.Count];
                for (var k = 0; k < hurtboxes.Count; k++) s.Hurt[k] = CombatEvaluator.HurtboxShape(rigD, hurtboxes[k], s.D, placement)!.Value;
            }
            return s.Hurt;
        }

        var done = new bool[hitboxes.Count];
        var broken = new bool[hitboxes.Count];
        var gridSteps = 1;
        for (var f = start; f <= end; f++)
        {
            foreach (var key in cache.Keys.Where(k => k < f - 1).ToList()) cache.Remove(key);
            var active = Enumerable.Range(0, hitboxes.Count).Where(i => !done[i] && !broken[i] && CombatEvaluator.IsActiveFrame(hitboxes[i], f, fps)).ToList();
            if (active.Count == 0) continue;
            var cur = At(f);
            foreach (var i in active)
                if (HitOf(cur, i) is null)
                {
                    broken[i] = true;
                    report.Warnings.Add($"Hitbox '{hitboxes[i].Name}': its bone or prop no longer exists; skipped.");
                }
            var sweepers = new List<int>();
            foreach (var i in active)
            {
                if (broken[i]) continue;
                if (f > start && CombatEvaluator.IsActiveFrame(hitboxes[i], f - 1, fps) && HitOf(At(f - 1), i) is not null) sweepers.Add(i);
                else if (Instant(cur, i) is { } c) Record(i, c.K, c.C, cur, false);
            }
            if (sweepers.Count == 0) continue;

            // One grid for every hitbox and both characters: the largest need over the hitbox/hurtbox pairs.
            var prev = At(f - 1);
            var need = 1.0;
            foreach (var i in sweepers)
                for (var k = 0; k < hurtboxes.Count; k++)
                {
                    var (dC, theta, rho) = Relative(HitOf(prev, i)!.Value, HitOf(cur, i)!.Value, HurtOf(prev)[k], HurtOf(cur)[k], hitboxes[i].Shape);
                    var arc = theta > 1e-9 ? dC * (theta / 2) / Math.Sin(theta / 2) : dC;
                    var tol = Math.Max(1e-4, Math.Min(Thickness(hitboxes[i].Shape), hurtThick[k]) / 2);
                    var r = (arc + rho * theta) / tol;
                    need = Math.Max(need, double.IsFinite(r) ? r : double.MaxValue);
                }
            var required = need >= MaxSubstepsLimit * 1e6 ? int.MaxValue : (int)Math.Ceiling(need - 1e-9);
            report.SubstepsRequired = Math.Max(report.SubstepsRequired, required);
            if (required > o.MaxSubsteps) report.MayHaveMissedContacts = true;
            var n = Math.Clamp(required, 1, o.MaxSubsteps);
            gridSteps = n;
            report.SubstepsUsed = Math.Max(report.SubstepsUsed, n);
            var a = prev;
            for (var s = 1; s <= n; s++)
            {
                if (sweepers.All(i => done[i])) break;
                var b = s == n ? cur : At(f - 1 + (double)s / n);
                foreach (var i in sweepers)
                {
                    if (done[i]) continue;
                    var nodes = 0;
                    if (Resolve(i, a, b, 0, ref nodes) is { } r)
                        Record(i, r.K, r.C, r.At, Instant(b, i) is null); // swept: the sub-step sample itself does not overlap
                }
                a = b;
            }
        }
        if (report.MayHaveMissedContacts)
            report.Warnings.Add($"{MissedWarningCode}: the motion needed {(report.SubstepsRequired == int.MaxValue ? "too many" : report.SubstepsRequired.ToString(System.Globalization.CultureInfo.InvariantCulture))} sub-steps per frame but at most {o.MaxSubsteps} were used. Rigid swings are still covered by the swept test, but a contact made by non-rigid motion between sub-steps may have been missed; raise maxSubsteps.");
        report.Hits.Sort((x, y) => x.Time.CompareTo(y.Time));
        return report;

        (int K, Contact C)? Instant(Sample s, int i)
        {
            if (HitOf(s, i) is not { } shape) return null;
            var hurts = HurtOf(s);
            (int K, Contact C)? best = null;
            for (var k = 0; k < hurts.Length; k++)
                if (Collision.Test(shape, hurts[k], out var c) && (best is null || c.Depth > best.Value.C.Depth)) best = (k, c);
            return best;
        }

        // Earliest contact of hitbox i in (a, b], on the real animation: a conservative swept test prunes, bisection resolves.
        (int K, Contact C, Sample At)? Resolve(int i, Sample a, Sample b, int depth, ref int nodes)
        {
            if (Sweep(i, a, b) is not { } cand) return null;
            if (++nodes > MaxResolveNodes) { report.MayHaveMissedContacts = true; return null; }
            if (b.Frame - a.Frame <= MinInterval * 1.0000001 || depth >= 2 * RefineSteps)
            {
                if (Instant(b, i) is { } c) return (c.K, c.C, b);
                // A tiny interval that still moves farther than the shapes are thick: a pass-through, reported as the swept contact.
                return cand.Disp > cand.Thick ? (cand.K, cand.C, b) : null;
            }
            var m = At((a.Frame + b.Frame) / 2);
            return Resolve(i, a, m, depth + 1, ref nodes) ?? Resolve(i, m, b, depth + 1, ref nodes);
        }

        (int K, Contact C, double Disp, double Thick)? Sweep(int i, Sample a, Sample b)
        {
            var h0 = HitOf(a, i)!.Value;
            var h1 = HitOf(b, i)!.Value;
            var u0 = HurtOf(a);
            var u1 = HurtOf(b);
            var shape = hitboxes[i].Shape;
            var bound = Bound(shape);
            var thick = Thickness(shape);
            (int, Contact, double, double)? best = null;
            for (var k = 0; k < hurtboxes.Count; k++)
            {
                var q0 = u0[k].Rotation.Conjugate();
                var q1 = u1[k].Rotation.Conjugate();
                var c0 = q0.Rotate(h0.Center - u0[k].Center);
                var c1 = q1.Rotate(h1.Center - u1[k].Center);
                var r0 = (q0 * h0.Rotation).Normalized();
                var r1 = (q1 * h1.Rotation).Normalized();
                var dC = (c1 - c0).Length();
                var theta = Quat.AngleBetween(r0, r1);
                var rho = Extent(shape);
                var disp = dC + rho * theta;
                if (!double.IsFinite(disp)) continue;
                var pairThick = Math.Max(1e-4, Math.Min(thick, hurtThick[k]));
                var samples = (int)Math.Clamp(Math.Ceiling(disp / (pairThick / 2)), 1, MaxGeometricSamples);
                // Rigid-swing sagitta of the centre path + slack for non-rigid relative motion + the gap between geometric samples.
                var margin = dC / 2 * Math.Tan(theta / 4) + disp * theta / 4 + disp / (2 * samples);
                if (DistancePointSegment(Vec3.Zero, c0, c1) > bound + margin + hurtBound[k]) continue;
                for (var j = 0; j <= samples; j++)
                {
                    var u = (double)j / samples;
                    var pose = new Transform(Vec3.Lerp(c0, c1, u), Quat.Slerp(r0, r1, u), Vec3.One);
                    if (!Collision.Test(Inflate(WorldShape.From(shape, pose), margin), hurtLocal[k], out var c)) continue;
                    var world = new Contact(u1[k].Center + u1[k].Rotation.Rotate(c.Point), u1[k].Rotation.Rotate(c.Normal), c.Depth);
                    if (best is null) best = (k, world, disp, pairThick);
                    break;
                }
                if (best is not null) break;
            }
            return best;
        }

        void Record(int i, int k, Contact c, Sample s, bool swept)
        {
            done[i] = true;
            report.Hits.Add(Describe(i, k, c, s, swept));
        }

        HitInfo Describe(int i, int k, Contact c, Sample s, bool swept)
        {
            var h = hitboxes[i];
            var box = hurtboxes[k];
            var t = s.Frame;
            var shape = HitOf(s, i)!.Value;
            // Velocity of the hitbox point nearest the contact RELATIVE to the hurtbox (defender motion removed), by a short difference.
            var dt = 1.0 / Math.Max(64, gridSteps);
            var u = SegmentParam(shape, c.Point);
            var other = At(t - dt >= 0 ? t - dt : t + dt);
            var sign = t - dt >= 0 ? 1 : -1;
            Vec3 velocity = Vec3.Zero;
            if (HitOf(other, i) is { } os)
            {
                var hs = HurtOf(s)[k];
                var ho = HurtOf(other)[k];
                var relS = hs.Rotation.Conjugate().Rotate(Lerp(shape, u) - hs.Center);
                var relO = ho.Rotation.Conjugate().Rotate(Lerp(os, u) - ho.Center);
                velocity = hs.Rotation.Rotate(relS - relO) * sign / (dt / fps.FramesPerSecond);
            }
            var dir = velocity.Length() > 0.05 ? velocity.Normalized() : -c.Normal;
            // The defender's actual facing at the hit: its placement yaw × the heading of its pelvis/root in the pose it plays.
            var heading = SequenceEvaluator.HeadingOf(s.D, restD, headingBone);
            var facing = (placement.Rotation * Quat.FromAxisAngle(Vec3.UnitY, heading)).Normalized();
            var local = facing.Conjugate().Rotate(dir);
            var horizontal = new Vec3(local.X, 0, local.Z);
            if (horizontal.Length() < 0.05)
            {
                var nn = facing.Conjugate().Rotate(-c.Normal);
                horizontal = new Vec3(nn.X, 0, nn.Z);
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

    /// <summary>Bone evaluations a test is planned to need: one shared grid of <paramref name="maxSubsteps"/> sub-steps per frame
    /// (each sample time evaluated once per character) plus a refinement allowance per hitbox (a bisection path and a velocity sample).</summary>
    public static double PlannedPoseWork(long frames, int maxSubsteps, int hitboxes, int bones) =>
        ((double)frames * maxSubsteps + 1 + hitboxes * (2.0 * RefineSteps + 4)) * bones;

    /// <summary>Hit strength 0–1 from a hitbox: knockback 1 m or damage 100 is a full-strength hit.</summary>
    public static double StrengthOf(Hitbox h) => Math.Clamp(Math.Max(h.Knockback / 1.0, h.Damage / 100.0), 0, 1);

    /// <summary>Relative motion of a hitbox in a hurtbox's frame between two samples: centre chord, rotation angle and the
    /// distance of the farthest extreme point (capsule end, box corner) from the centre.</summary>
    static (double CentreChord, double Angle, double Extent) Relative(WorldShape h0, WorldShape h1, WorldShape u0, WorldShape u1, CollisionShape shape)
    {
        var q0 = u0.Rotation.Conjugate();
        var q1 = u1.Rotation.Conjugate();
        var c0 = q0.Rotate(h0.Center - u0.Center);
        var c1 = q1.Rotate(h1.Center - u1.Center);
        return ((c1 - c0).Length(), Quat.AngleBetween((q0 * h0.Rotation).Normalized(), (q1 * h1.Rotation).Normalized()), Extent(shape));
    }

    /// <summary>Farthest point of the shape's core (segment or box) from its centre.</summary>
    static double Extent(CollisionShape s) => s.Kind switch
    {
        CollisionShapeKind.Capsule => s.Length / 2,
        CollisionShapeKind.Box => (s.Size * 0.5).Length(),
        _ => 0,
    };

    /// <summary>Radius of a sphere around the centre that contains the whole shape.</summary>
    static double Bound(CollisionShape s) => s.Kind == CollisionShapeKind.Box ? (s.Size * 0.5).Length() : Extent(s) + s.Radius;

    static WorldShape Inflate(WorldShape s, double m) =>
        s.IsBox ? s with { Half = s.Half + new Vec3(m, m, m) } : s with { Radius = s.Radius + m };

    static double DistancePointSegment(Vec3 p, Vec3 a, Vec3 b)
    {
        var d = b - a;
        var l2 = d.LengthSquared();
        var u = l2 < 1e-18 ? 0 : Math.Clamp(Vec3.Dot(p - a, d) / l2, 0, 1);
        return (a + d * u - p).Length();
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
