using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Physics;

/// <summary>Which stages of <see cref="PhysicsPolish"/> run, and their parameters.</summary>
public sealed record PhysicsPolishOptions
{
    /// <summary>Feet in contact stay planted on flat ground (two-bone leg IK; the foot's own rotation, so heel/toe roll, is kept).</summary>
    public bool GroundContact { get; init; } = true;

    /// <summary>No body part below the ground; a body resting low (lying, sitting) touches the ground with its lowest points.</summary>
    public bool GroundClamp { get; init; } = true;

    /// <summary>Flight phases: the COM follows a ballistic parabola, with take-off and landing impulses and landing compression.</summary>
    public bool Ballistic { get; init; } = true;

    /// <summary>Momentum-preserving step transitions: the pelvis' horizontal path and heading ease across each step boundary.</summary>
    public bool Transitions { get; init; } = true;

    /// <summary>Falls: the descent keeps accelerating into the first body impact (no easing in) and the body settles with a small damped bounce.</summary>
    public bool Falls { get; init; } = true;

    /// <summary>Spine, head and arms lag behind the motion (damped springs, <see cref="Spring"/>), fading out near the ground.</summary>
    public bool Secondary { get; init; } = true;

    /// <summary>Standing frames: the hips shift so the COM stays inside the support polygon (feet stay planted).</summary>
    public bool Balance { get; init; } = true;

    public double Gravity { get; init; } = 9.81;

    /// <summary>Knee flexion (deg) a landing must reach within 0.3 s (the pelvis dips further when it does not).</summary>
    public double MinLandingKneeFlex { get; init; } = 15;

    /// <summary>Gentle defaults: ≈3 Hz, damping ratio ≈0.75, at most 8° of lag.</summary>
    public SpringSettings Spring { get; init; } = new() { Stiffness = 350, Damping = 28, MaxAngleDegrees = 8 };

    /// <summary>Half-width (s) of the window eased across each step boundary.</summary>
    public double TransitionSeconds { get; init; } = 0.25;

    /// <summary>Step boundaries (frames); null = the clip's markers after frame 0.</summary>
    public IReadOnlyList<int>? Boundaries { get; init; }

    /// <summary>The clip loops (null = the clip's loop setting): only the per-frame ground clamp, foot penetration and interior flights
    /// run (a loop's feet are planted where it was made), and nothing touching either end is changed, so the seam stays as it was.</summary>
    public bool? Cyclic { get; init; }

    /// <summary>Margin (m) the COM keeps inside the support polygon on standing frames.</summary>
    public double BalanceMargin { get; init; } = 0.02;
}

/// <summary>What <see cref="PhysicsPolish.Run"/> did.</summary>
public sealed record PhysicsPolishResult(Transform[][] Locals, MotionPhysicsReport Before, MotionPhysicsReport After,
    IReadOnlyList<string> Stages, IReadOnlyList<string> Warnings, int ChangedFrames, IReadOnlyList<int> ChangedBones);

/// <summary>
/// "Physics polish": a deterministic post-process that makes a clip physically believable without simulating it. It works on the
/// clip's dense per-frame local transforms (model space, ground = the rest pose's sole) and runs these stages in order:
/// <list type="number">
/// <item><b>Falls</b>: from the fastest descent to the first impact of a part other than the feet the COM keeps accelerating
/// (0.6 g: the body topples about its supports) instead of easing into the ground; later frames play earlier and the last pose is
/// held. After the end of the descent the pelvis bounces once, damped (lift only).</item>
/// <item><b>Secondary motion</b>: spine, neck, head and arms lag the primary motion (<see cref="SecondaryMotion"/>, gentle springs),
/// fading out while the body is within 30 cm of the ground so nothing swings through the floor.</item>
/// <item><b>Transitions</b>: across each step boundary the pelvis' horizontal path and heading are replaced by cubic Hermite curves
/// matching position and velocity at both window ends, so the root velocity changes smoothly (no momentum pops).</item>
/// <item><b>Ballistic flight</b>: during each flight phase (not a fall) the pelvis is shifted so the COM follows the parabola (g)
/// through its take-off and landing positions; the shift's velocity is blended in before take-off (a small counter-movement, then
/// the push) and out after landing (compression), and the pelvis dips further when the knees would bend less than the landing minimum.</item>
/// <item><b>Balance</b>: on standing frames the hips shift (smoothed over time) so the COM keeps a margin inside the support polygon.</item>
/// <item><b>Ground clamp</b>: the pelvis is lifted so no body capsule (<see cref="BodyGeometry"/>) is below the ground; while the body
/// rests low on a part other than the feet it is lowered or lifted so its lowest point touches the ground. Smoothed over ±0.1 s,
/// never below the bound.</item>
/// <item><b>Ground contact</b>: a foot within 2 cm of the ground, not lifting off or striking fast, is planted when it is slower than
/// 1 m/s (median over the contact): the pivot that touches (heel, then the ball at the toe joint, then the toe tip, so the roll is
/// kept) stays where it touched down and the lowest sole point sits on the ground; frames at either end still moving fast, and whole
/// contacts that are fast, skim 1.2 cm above the ground instead of skating. The correction eases in and out over 0.1 s; legs are
/// re-solved with two-bone IK (soft reach limit). Feet never end below the ground.</item>
/// </list>
/// Bones and frames no stage touches keep exactly their original local transforms. Work is bounded: a fixed number of passes, each
/// linear in frames × bones; clips longer than <see cref="MaxFrames"/> are refused.
/// </summary>
public static class PhysicsPolish
{
    public const int MaxFrames = 20_000;
    const double ContactHeight = 0.02, ContactSpeed = 1.0, SkimHeight = 0.012;

    public static PhysicsPolishResult Run(Clip clip, Rig rig, PhysicsPolishOptions? options = null)
    {
        var o = options ?? new PhysicsPolishOptions();
        var body = BodyGeometry.For(rig);
        var ev = body.Evaluator;
        var locals = MotionPhysicsMetrics.Locals(clip, rig, ev);
        if (locals.Length > MaxFrames) throw new ArgumentException($"The clip has {locals.Length} frames; the physics polish handles at most {MaxFrames}.");
        var boundaries = o.Boundaries ?? MotionPhysicsMetrics.Boundaries(clip);
        var fps = clip.Fps.FramesPerSecond;
        var before = MotionPhysicsMetrics.Measure(locals, fps, body, boundaries);
        var w = new Work(body, locals.Select(l => (Transform[])l.Clone()).ToArray(), fps, o, o.Cyclic ?? clip.Loop.Enabled);
        var stages = new List<string>();
        if (w.Pelvis < 0) w.Warnings.Add("The rig has no humanoid-mapped pelvis: nothing was changed.");
        else if (locals.Length >= 4)
        {
            if (o.Falls && !w.Cyclic && w.Falls()) stages.Add("falls");
            if (o.Secondary && !w.Cyclic && w.Secondary(clip)) stages.Add("secondary");
            if (o.Transitions && !w.Cyclic && w.Transitions(boundaries)) stages.Add("transitions");
            if (o.Ballistic && w.Ballistic()) stages.Add("ballistic");
            if (o.Balance && w.Balance()) stages.Add("balance");
            for (var pass = 0; pass < 2; pass++)
            {
                var changed = false;
                if (o.GroundClamp && w.Clamp()) changed = true;
                if (pass == 0) w.SettleBounce();
                if (o.GroundContact && !w.Cyclic && w.Contacts()) changed = true;
                if ((o.GroundClamp || o.GroundContact) && w.FeetAboveGround()) changed = true;
                if (changed && pass == 0) stages.AddRange(new[] { o.GroundClamp ? "ground_clamp" : null, o.GroundContact ? "ground_contact" : null }.OfType<string>());
                if (!changed || !o.GroundClamp || w.LowestCapsule() >= -0.002) break;
            }
        }
        var after = MotionPhysicsMetrics.Measure(w.L, fps, body, boundaries);
        var changedFrames = 0;
        var changedBones = new HashSet<int>();
        for (var f = 0; f < locals.Length; f++)
        {
            var any = false;
            for (var b = 0; b < ev.Count; b++)
                if (Differs(locals[f][b], w.L[f][b])) { any = true; changedBones.Add(b); }
            if (any) changedFrames++;
        }
        return new PhysicsPolishResult(w.L, before, after, stages, w.Warnings, changedFrames, changedBones.OrderBy(b => b).ToList());
    }

    public static bool Differs(Transform a, Transform b) =>
        (a.Translation - b.Translation).LengthSquared() > 1e-14 || Quat.AngleBetween(a.Rotation, b.Rotation) > 1e-7;

    /// <summary>Runs the polish and returns a copy of the clip whose changed bones carry new cubic tracks (one key per frame, reduced
    /// within tolerance when <paramref name="reduceKeys"/>); other tracks are kept as they are. Locked tracks are not rewritten.</summary>
    public static Clip Apply(Clip clip, Rig rig, PhysicsPolishOptions? options, bool reduceKeys, out PhysicsPolishResult result)
    {
        result = Run(clip, rig, options);
        var copy = clip.DeepClone();
        if (result.ChangedFrames == 0) return copy;
        var ev = new RigEvaluator(rig);
        var original = MotionPhysicsMetrics.Locals(clip, rig, ev);
        var cyclic = options?.Cyclic ?? clip.Loop.Enabled;
        var warnings = (List<string>)result.Warnings;
        foreach (var b in result.ChangedBones)
        {
            var bone = rig.Bones[b];
            foreach (var channel in new[] { TrackChannels.Rotation, TrackChannels.Translation })
            {
                var rot = channel == TrackChannels.Rotation;
                var moved = false;
                for (var f = 0; f < original.Length && !moved; f++)
                    moved = rot ? Quat.AngleBetween(original[f][b].Rotation, result.Locals[f][b].Rotation) > 1e-7
                        : (original[f][b].Translation - result.Locals[f][b].Translation).LengthSquared() > 1e-14;
                if (!moved) continue;
                var index = copy.Tracks.FindIndex(t => t.Target.Kind == TargetKind.Bone && t.Target.Id == bone.Id && t.Channel == channel);
                var old = index >= 0 ? copy.Tracks[index] : null;
                if (old is { Locked: true }) { warnings.Add($"The {channel} track of '{bone.Name}' is locked and was not changed."); continue; }
                double[][] values;
                if (rot)
                {
                    var q = new Quat[original.Length];
                    for (var f = 0; f < q.Length; f++)
                    {
                        q[f] = result.Locals[f][b].Rotation;
                        if (f > 0 && Quat.Dot(q[f], q[f - 1]) < 0) q[f] = new Quat(-q[f].X, -q[f].Y, -q[f].Z, -q[f].W);
                    }
                    values = q.Select(x => new[] { x.X, x.Y, x.Z, x.W }).ToArray();
                }
                else values = result.Locals.Select(l => new[] { l[b].Translation.X, l[b].Translation.Y, l[b].Translation.Z }).ToArray();
                var track = BakedTracks.CubicTrack(TargetKind.Bone, bone.Id, old?.Group ?? BakedTracks.GroupOf(rig, bone.Id), channel, values, clip.Fps,
                    reduceKeys, rot ? 0.05 * Math.PI / 180 : 0.0002, cyclic);
                if (old is null) copy.Tracks.Add(track);
                else
                {
                    track.Id = old.Id;
                    track.Muted = old.Muted;
                    copy.Tracks[index] = track;
                }
            }
        }
        return copy;
    }

    /// <summary>The working state: per-frame local transforms and the rig's humanoid indices.</summary>
    sealed class Work
    {
        public readonly BodyGeometry Body;
        public readonly RigEvaluator Ev;
        public readonly Transform[][] L;
        public readonly Transform[][] Original;
        public readonly double Fps, Dt;
        public readonly int N, Pelvis;
        public readonly bool Cyclic;
        public readonly PhysicsPolishOptions O;
        public readonly List<string> Warnings = new();
        readonly Leg[] _legs;
        readonly Transform[] _rest;
        double _impactSpeed;
        int _impactFrame = -1;

        sealed record Leg(Foot Foot, int Thigh, int Calf, int Ankle, (int Bone, Vec3 Local) Heel, (int Bone, Vec3 Local) Ball, (int Bone, Vec3 Local) Tip);

        public Work(BodyGeometry body, Transform[][] locals, double fps, PhysicsPolishOptions o, bool cyclic)
        {
            Body = body;
            Ev = body.Evaluator;
            L = locals;
            Original = locals.Select(l => (Transform[])l.Clone()).ToArray();
            N = locals.Length;
            Fps = fps;
            Dt = 1 / fps;
            O = o;
            Cyclic = cyclic;
            Pelvis = Index(H.Pelvis);
            _rest = Ev.RestModel();
            var legs = new List<Leg>();
            foreach (var foot in body.Feet.Feet)
            {
                int t = Index(H.Thigh(foot.Side)), c = Index(H.Calf(foot.Side)), a = foot.FootBone;
                if (t < 0 || c < 0 || Ev.ParentOf(c) != t || Ev.ParentOf(a) != c)
                {
                    Warnings.Add($"The {foot.Side.ToString().ToLowerInvariant()} leg has no thigh → calf → foot chain: that foot is not planted.");
                    continue;
                }
                (int, Vec3) Avg(string a1, string a2)
                {
                    var p = foot.Points.Where(x => x.Name == a1 || x.Name == a2).ToList();
                    return (p[0].Bone, (p[0].Local + p[^1].Local) * 0.5);
                }
                var tip = foot.Points.FirstOrDefault(x => x.Name == "tip");
                var ball = foot.ToeBone >= 0 ? (foot.ToeBone, Vec3.Zero) : Avg("ball_in", "ball_out");
                legs.Add(new Leg(foot, t, c, a, Avg("heel_in", "heel_out"), ball, tip.Name == "tip" ? (tip.Bone, tip.Local) : ball));
            }
            _legs = legs.ToArray();
        }

        int Index(string std) => Ev.Rig.FindByStandardName(std) is { } b ? Ev.IndexOf(b.Id) : -1;

        public Transform[] Model(int f) => Ev.Model(L[f]);

        double Ground => Body.Ground;

        Vec3 Com(int f) => Body.Mass.CenterOfMass(Model(f));

        /// <summary>Moves the pelvis by a model-space offset (everything below it follows).</summary>
        void MovePelvis(int f, Vec3 d)
        {
            if (d.LengthSquared() < 1e-16) return;
            var parent = Ev.ParentOf(Pelvis);
            var local = d;
            if (parent >= 0)
            {
                var pm = Model(f)[parent];
                local = pm.Rotation.Inverse().Rotate(d);
                local = new Vec3(local.X / pm.Scale.X, local.Y / pm.Scale.Y, local.Z / pm.Scale.Z);
            }
            L[f][Pelvis] = L[f][Pelvis] with { Translation = L[f][Pelvis].Translation + local };
        }

        /// <summary>Turns the pelvis about the vertical through its joint.</summary>
        void YawPelvis(int f, double radians)
        {
            if (Math.Abs(radians) < 1e-9) return;
            var m = Model(f);
            var parent = Ev.ParentOf(Pelvis);
            var parentRot = parent < 0 ? Quat.Identity : m[parent].Rotation;
            var world = (Quat.FromAxisAngle(Vec3.UnitY, radians) * m[Pelvis].Rotation).Normalized();
            L[f][Pelvis] = L[f][Pelvis] with { Rotation = (parentRot.Inverse() * world).Normalized() };
        }

        Vec3 PelvisPos(int f) => Model(f)[Pelvis].Translation;

        double PelvisYaw(int f) => MotionPhysicsMetrics.Yaw(Model(f)[Pelvis].Rotation * _rest[Pelvis].Rotation.Inverse());

        // ------------------------------------------------------------------ falls

        public bool Falls()
        {
            var models = Enumerable.Range(0, N).Select(Model).ToArray();
            var com = models.Select(Body.Mass.CenterOfMass).ToArray();
            if (MotionPhysicsMetrics.FallDescent(com, Body, Fps, MotionPhysicsMetrics.Flights(Body, models)) is not { } fall) return false;
            var a = fall.Start;
            var c = MotionPhysicsMetrics.FirstBodyContact(Body, models, fall);
            var y = com.Select(p => p.Y - Ground).ToArray();
            _impactFrame = fall.End; // the settle bounce follows the end of the descent
            _impactSpeed = Math.Max(0, (y[fall.End - 1] - y[fall.End]) * Fps);
            // Fastest descent before the first body contact; after it the original eases into the ground. Nothing but the feet holds
            // the body up until that impact, so from there the COM keeps accelerating (a fraction of g: the body topples about its
            // supports) until it reaches the impact height.
            var m = a + 1;
            for (var f = a + 1; f <= c; f++) if (y[f - 1] - y[f] > y[m - 1] - y[m]) m = f;
            var vm = (y[m - 1] - y[m]) * Fps;
            var drop = y[m] - y[c];
            if (c - a < 3 || vm < 0.2 || drop < 0.01) return false;
            var acc = 0.6 * O.Gravity;
            var u = (-vm + Math.Sqrt(vm * vm + 2 * acc * drop)) / acc;
            var impact = m + u * Fps;
            if (impact >= c - 1) return false; // already falling at least that fast
            var e = new double[c - m + 1];
            e[0] = y[m];
            for (var k = 1; k < e.Length; k++) e[k] = Math.Min(e[k - 1] - 1e-6, y[m + k]);
            var scale = (e[0] - e[^1]) / drop;
            for (var f = m + 1; f < N; f++)
            {
                double tau;
                if (f < impact)
                {
                    var t = (f - m) / Fps;
                    var target = e[0] - (vm * t + 0.5 * acc * t * t) * scale;
                    var j = 0;
                    while (j < e.Length - 2 && e[j + 1] > target) j++;
                    tau = m + j + Math.Clamp((e[j] - target) / (e[j] - e[j + 1]), 0, 1);
                }
                else tau = c + (f - impact); // after the impact: the original, earlier; the last pose is held at the end
                L[f] = Sample(Math.Min(N - 1, tau));
            }
            _impactFrame = Math.Min(N - 1, (int)Math.Round(fall.End - (c - impact)));
            return true;
        }

        /// <summary>Original pose at fractional frame <paramref name="t"/> (slerp / lerp per bone).</summary>
        Transform[] Sample(double t)
        {
            var i0 = Math.Clamp((int)Math.Floor(t), 0, N - 1);
            var i1 = Math.Min(N - 1, i0 + 1);
            var u = t - i0;
            var r = new Transform[Ev.Count];
            for (var b = 0; b < r.Length; b++)
            {
                Transform x = Original[i0][b], z = Original[i1][b];
                r[b] = new Transform(Vec3.Lerp(x.Translation, z.Translation, u), Quat.Slerp(x.Rotation, z.Rotation, u).Normalized(), Vec3.Lerp(x.Scale, z.Scale, u));
            }
            return r;
        }

        /// <summary>After a fall's descent: one small damped bounce of the pelvis (lift only).</summary>
        public void SettleBounce()
        {
            if (!O.Falls || Cyclic || _impactFrame < 0 || _impactFrame >= N - 2 || _impactSpeed < 0.3) return;
            var amp = Math.Clamp(0.006 * _impactSpeed, 0.003, 0.015);
            for (var f = _impactFrame + 1; f < N; f++)
            {
                var u = (f - _impactFrame) / Fps;
                if (u > 0.5) break;
                MovePelvis(f, new Vec3(0, amp * Math.Exp(-u / 0.1) * Math.Abs(Math.Sin(Math.PI * u / 0.11)), 0));
            }
        }

        // ------------------------------------------------------------------ secondary motion

        public bool Secondary(Clip source)
        {
            var bones = new[] { H.Spine1, H.Spine2, H.Chest, H.Neck, H.Head }
                .Concat(new[] { Side.Left, Side.Right }.SelectMany(s => new[] { H.Clavicle(s), H.UpperArm(s), H.LowerArm(s), H.Hand(s) }))
                .Select(Index).Where(i => i >= 0).ToList();
            if (bones.Count == 0) return false;
            // Simulate on the current state (a temporary clip with a key on every frame).
            var temp = source.CloneWithoutTracks();
            temp.Duration = Flicks.FromFrame(N - 1, source.Fps);
            BakedTracks.AddBoneTracks(temp, Ev.Rig, L, _ => true, _ => true, false);
            var res = SecondaryMotion.Simulate(Ev.Rig, temp, bones, O.Spring, 0, N - 1);
            // Contact with the ground dominates: the lag fades out while the body (not the feet) is within 30 cm of it, so a
            // lagging arm or head never swings through the floor.
            var near = Enumerable.Range(0, N).Select(f => Smooth((Body.Lowest(Model(f), soles: false).Height - 0.05) / 0.25)).ToArray();
            var k = Math.Max(1, (int)Math.Round(0.15 * Fps));
            var weight = Enumerable.Range(0, N).Select(f => Enumerable.Range(Math.Max(0, f - k), Math.Min(N - 1, f + k) - Math.Max(0, f - k) + 1).Min(j => near[j])).ToArray();
            for (var i = 0; i < res.Bones.Count; i++)
                for (var f = 0; f < N; f++)
                {
                    var b = res.Bones[i];
                    L[f][b] = L[f][b] with { Rotation = Quat.Slerp(L[f][b].Rotation, res.Rotations[i][f], weight[f]).Normalized() };
                }
            return true;
        }

        // ------------------------------------------------------------------ transitions

        public bool Transitions(IReadOnlyList<int> boundaries)
        {
            var w = Math.Max(2, (int)Math.Round(O.TransitionSeconds * Fps));
            var any = false;
            foreach (var b in boundaries)
            {
                int s = Math.Max(1, b - w), e = Math.Min(N - 2, b + w);
                if (e - s < 3) continue;
                var pos = Enumerable.Range(s - 1, e - s + 3).Select(PelvisPos).ToArray(); // frames s-1 .. e+1
                var yaw = MotionPhysicsMetrics.Unwrap(Enumerable.Range(s - 1, e - s + 3).Select(PelvisYaw).ToArray());
                Vec3 P(int f) => pos[f - s + 1];
                double Y(int f) => yaw[f - s + 1];
                var T = (e - s) * Dt;
                var upright = Enumerable.Range(s - 1, e - s + 3).All(f => Math.Abs((Model(f)[Pelvis].Rotation * _rest[Pelvis].Rotation.Inverse()).Rotate(Vec3.UnitZ).Y) < 0.7);
                Vec3 p0 = P(s), p1 = P(e), v0 = (P(s + 1) - P(s - 1)) * (Fps / 2), v1 = (P(e + 1) - P(e - 1)) * (Fps / 2);
                double y0 = Y(s), y1 = Y(e), w0 = (Y(s + 1) - Y(s - 1)) * (Fps / 2), w1 = (Y(e + 1) - Y(e - 1)) * (Fps / 2);
                for (var f = s + 1; f < e; f++)
                {
                    var u = (f - s) / (double)(e - s);
                    var h = Hermite(u, p0, v0 * T, p1, v1 * T);
                    var d = h - P(f);
                    MovePelvis(f, new Vec3(d.X, 0, d.Z));
                    if (upright) YawPelvis(f, Hermite(u, y0, w0 * T, y1, w1 * T) - Y(f));
                }
                any = true;
            }
            return any;
        }

        static Vec3 Hermite(double u, Vec3 p0, Vec3 m0, Vec3 p1, Vec3 m1)
        {
            double u2 = u * u, u3 = u2 * u;
            return p0 * (2 * u3 - 3 * u2 + 1) + m0 * (u3 - 2 * u2 + u) + p1 * (-2 * u3 + 3 * u2) + m1 * (u3 - u2);
        }

        static double Hermite(double u, double p0, double m0, double p1, double m1)
        {
            double u2 = u * u, u3 = u2 * u;
            return p0 * (2 * u3 - 3 * u2 + 1) + m0 * (u3 - 2 * u2 + u) + p1 * (-2 * u3 + 3 * u2) + m1 * (u3 - u2);
        }

        // ------------------------------------------------------------------ ballistic flight

        public bool Ballistic()
        {
            var models = Enumerable.Range(0, N).Select(Model).ToArray();
            var flights = MotionPhysicsMetrics.Flights(Body, models);
            var fallStart = MotionPhysicsMetrics.FallDescent(models.Select(Body.Mass.CenterOfMass).ToArray(), Body, Fps, flights)?.Start ?? int.MaxValue;
            var any = false;
            var g = O.Gravity;
            var thighLen = _legs.Length > 0 ? (_rest[_legs[0].Calf].Translation - _rest[_legs[0].Thigh].Translation).Length() : 0;
            var shinLen = _legs.Length > 0 ? (_rest[_legs[0].Ankle].Translation - _rest[_legs[0].Calf].Translation).Length() : 0;
            for (var i = 0; i < flights.Count; i++)
            {
                var fl = flights[i];
                int t0 = fl.Start - 1, t1 = fl.End + 1;
                if (Cyclic && (t0 <= 0 || t1 >= N - 1)) continue;
                if (fl.End >= fallStart) continue; // a falling body is handled by the fall stage
                var com = Enumerable.Range(t0, t1 - t0 + 1).Select(f => Body.Mass.CenterOfMass(models[f])).ToArray();
                var T = (t1 - t0) * Dt;
                var d = new Vec3[com.Length];
                for (var k = 0; k < com.Length; k++)
                {
                    var s = (double)k / (com.Length - 1);
                    var target = Vec3.Lerp(com[0], com[^1], s) + new Vec3(0, 0.5 * g * T * T * s * (1 - s), 0);
                    d[k] = target - com[k];
                }
                if (d.Max(x => x.Length()) > 0.5) { Warnings.Add($"Flight at frames {fl.Start}–{fl.End} is far from ballistic (> 0.5 m); left as it is."); continue; }
                var vTake = (d[1] - d[0]) * Fps;
                var vLand = (d[^1] - d[^2]) * Fps;
                // Push-off: the correction's velocity blends in before take-off (h(u) = u³ − u²: a small counter-movement, then the push).
                var prevEnd = i > 0 ? flights[i - 1].End + 2 : 1;
                var wp = Math.Min((int)Math.Round(0.15 * Fps), t0 - prevEnd);
                for (var f = Math.Max(0, t0 - wp); f < t0 && wp > 0; f++)
                {
                    var u = (f - (t0 - wp)) / (double)wp;
                    MovePelvis(f, vTake * (wp * Dt * (u * u * u - u * u)));
                }
                for (var k = 1; k < com.Length - 1; k++) MovePelvis(t0 + k, d[k]);
                // Landing: the correction's velocity carries on and decays (g(u) = u(1 − u)²), then the knees must bend enough.
                var nextStart = i + 1 < flights.Count ? flights[i + 1].Start - 2 : N - 1;
                var wl = Math.Min((int)Math.Round(0.25 * Fps), nextStart - t1);
                for (var k = 1; k < wl; k++)
                {
                    var u = (double)k / wl;
                    MovePelvis(t1 + k, vLand * (wl * Dt * u * (1 - u) * (1 - u)));
                }
                if (_legs.Length > 0 && wl > 2 && O.MinLandingKneeFlex > 0)
                {
                    // Hip–ankle distance for the minimum knee flexion; the ankles stay where they land (planted later).
                    var dMax = Math.Sqrt(thighLen * thighLen + shinLen * shinLen + 2 * thighLen * shinLen * Math.Cos(O.MinLandingKneeFlex * Math.PI / 180));
                    var peak = (int)Math.Round(0.4 * wl);
                    var f = t1 + Math.Max(1, peak);
                    var m = Model(f);
                    var anklesAt = Model(t1);
                    var need = double.MaxValue;
                    foreach (var leg in _legs)
                    {
                        var v = m[leg.Thigh].Translation - anklesAt[leg.Ankle].Translation;
                        var horiz2 = v.X * v.X + v.Z * v.Z;
                        var yNeed = horiz2 >= dMax * dMax ? 0 : Math.Sqrt(dMax * dMax - horiz2);
                        need = Math.Min(need, Math.Max(0, v.Y - yNeed)); // the most bent leg reaching the minimum is enough
                    }
                    if (need > 1e-4 && need < 0.3)
                        for (var k = 1; k < wl; k++)
                        {
                            var u = (double)k / wl;
                            MovePelvis(t1 + k, new Vec3(0, -need * Bump(u, (double)Math.Max(1, peak) / wl), 0));
                        }
                }
                any = true;
            }
            return any;
        }

        /// <summary>Smooth bump on [0, 1]: 0 with zero slope at both ends, 1 at <paramref name="peak"/>.</summary>
        static double Bump(double s, double peak)
        {
            if (s <= 0 || s >= 1) return 0;
            peak = Math.Clamp(peak, 0.05, 0.95);
            var c = 2 / peak - 2;
            double B(double x) => x * x * Math.Pow(1 - x, c);
            return B(s) / B(peak);
        }

        // ------------------------------------------------------------------ balance

        public bool Balance()
        {
            if (_legs.Length < 2) return false;
            var shift = new Vec3[N];
            var any = false;
            var com = Enumerable.Range(0, N).Select(Com).ToArray();
            for (var f = 0; f < N; f++)
            {
                if (Cyclic && (f == 0 || f == N - 1)) continue;
                var m = Model(f);
                if (_legs.Any(l => Body.SoleHeight(l.Foot, m) > MotionPhysicsMetrics.ContactHeight)) continue;
                var v = (com[Math.Min(N - 1, f + 1)] - com[Math.Max(0, f - 1)]) * (Fps / Math.Max(1, Math.Min(N - 1, f + 1) - Math.Max(0, f - 1)));
                if (MotionPhysicsMetrics.Horizontal(v) > 0.15 || com[f].Y - Ground < 0.45 * Body.Height) continue;
                var pts = _legs.SelectMany(l => l.Foot.Points.Select(p => m[p.Bone].TransformPoint(p.Local))).Where(p => p.Y - Ground < MotionPhysicsMetrics.ContactHeight).Select(Vec2.Of).ToList();
                var hull = Geometry2D.ConvexHull(pts);
                if (hull.Count < 3 || Geometry2D.SignedDistance(hull, Vec2.Of(com[f])) >= O.BalanceMargin) continue;
                var inset = Geometry2D.Inset(hull, O.BalanceMargin);
                var target = inset.Count >= 3 ? Geometry2D.ClosestPoint(inset, Vec2.Of(com[f])) : Geometry2D.Centroid(hull);
                var d = (target - Vec2.Of(com[f])) * (1 / 0.85); // the pinned legs do not follow the hips
                var len = Math.Sqrt(d.X * d.X + d.Z * d.Z);
                if (len > 0.1) d = d * (0.1 / len);
                shift[f] = new Vec3(d.X, 0, d.Z);
                any = true;
            }
            if (!any) return false;
            var smooth = Smoothed(shift, (int)Math.Round(0.15 * Fps));
            for (var f = 0; f < N; f++)
            {
                // Never less than the frame's own need along its direction (the average would leave the COM just outside).
                var s = smooth[f];
                var need = shift[f];
                if (need.LengthSquared() > 0 && Vec3.Dot(s, need) < need.LengthSquared()) s = need;
                MovePelvis(f, s);
            }
            return true;
        }

        static Vec3[] Smoothed(Vec3[] x, int k)
        {
            var r = new Vec3[x.Length];
            for (var f = 0; f < x.Length; f++)
            {
                var sum = Vec3.Zero;
                var wsum = 0.0;
                for (var d = -k; d <= k; d++)
                {
                    var j = f + d;
                    if (j < 0 || j >= x.Length) continue;
                    var w = 1 - Math.Abs(d) / (k + 1.0);
                    sum += x[j] * w;
                    wsum += w;
                }
                r[f] = sum / wsum;
            }
            return r;
        }

        // ------------------------------------------------------------------ ground clamp

        public double LowestCapsule() => Enumerable.Range(0, N).Min(f => Body.Lowest(Model(f), soles: false).Height);

        public bool Clamp()
        {
            var bound = new double[N];
            var target = new double?[N];
            var com = Enumerable.Range(0, N).Select(Com).ToArray();
            for (var f = 0; f < N; f++)
            {
                var m = Model(f);
                var lowCaps = Body.Lowest(m, soles: false).Height;
                bound[f] = Math.Max(0, -lowCaps);
                var v = (com[Math.Min(N - 1, f + 1)] - com[Math.Max(0, f - 1)]).Length() * Fps / Math.Max(1, Math.Min(N - 1, f + 1) - Math.Max(0, f - 1));
                // Resting on the ground (lying, sitting): low, slow and held up by a part other than the feet.
                if (com[f].Y - Ground < 0.25 * Body.Height && v < 0.3 && lowCaps < 0.06)
                {
                    var t = -Body.Lowest(m).Height;
                    target[f] = t;
                    bound[f] = Math.Max(bound[f], t);
                }
            }
            if (Cyclic) { bound[0] = bound[^1] = 0; target[0] = target[^1] = null; }
            if (bound.All(b => b < 1e-5) && target.All(t => t is null || Math.Abs(t.Value) < 1e-5)) return false;
            var k = Math.Max(1, (int)Math.Round(0.1 * Fps));
            // Dilate the bound, then average: lifts start before and end after the frames that need them.
            var dil = new double[N];
            for (var f = 0; f < N; f++)
            {
                var mx = target[f] ?? 0.0;
                for (var d = -k; d <= k; d++) if (f + d >= 0 && f + d < N && target[f + d] is null) mx = Math.Max(mx, bound[f + d]);
                dil[f] = target[f] is { } t0 ? Math.Max(t0, mx) : mx;
            }
            var lift = new double[N];
            for (var f = 0; f < N; f++)
            {
                double sum = 0, wsum = 0;
                for (var d = -k; d <= k; d++)
                {
                    if (f + d < 0 || f + d >= N) continue;
                    var w = 1 - Math.Abs(d) / (k + 1.0);
                    sum += dil[f + d] * w;
                    wsum += w;
                }
                lift[f] = target[f] is { } t ? Math.Max(t, Math.Min(sum / wsum, t + 0.005)) : Math.Max(bound[f], sum / wsum);
            }
            if (Cyclic) lift[0] = lift[^1] = 0;
            var changed = false;
            for (var f = 0; f < N; f++)
                if (Math.Abs(lift[f]) > 1e-6) { MovePelvis(f, new Vec3(0, lift[f], 0)); changed = true; }
            return changed;
        }

        // ------------------------------------------------------------------ ground contact

        public bool Contacts()
        {
            if (_legs.Length == 0) return false;
            var models = Enumerable.Range(0, N).Select(Model).ToArray();
            var ease = Math.Max(2, (int)Math.Round(0.1 * Fps));
            var changed = false;
            foreach (var leg in _legs)
            {
                Vec3 At((int Bone, Vec3 Local) p, int f) => models[f][p.Bone].TransformPoint(p.Local);
                double Low(int f, string prefix) => leg.Foot.Points.Where(x => x.Name.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(x => models[f][x.Bone].TransformPoint(x.Local).Y - Ground).DefaultIfEmpty(double.MaxValue).Min();
                var sole = Enumerable.Range(0, N).Select(f => Body.SoleHeight(leg.Foot, models[f])).ToArray();
                // Pivot: heel while it touches, then the ball (toe joint), then the tip.
                var pivot = new int[N];
                for (var f = 0; f < N; f++) pivot[f] = Low(f, "heel") < ContactHeight ? 0 : Low(f, "ball") < ContactHeight ? 1 : 2;
                (int, Vec3) Pivot(int k) => k == 0 ? leg.Heel : k == 1 ? leg.Ball : leg.Tip;
                var low = new bool[N];
                var speed = new double[N];
                for (var f = 0; f < N; f++)
                {
                    int f0 = Math.Max(0, f - 1), f1 = Math.Min(N - 1, f + 1);
                    // Near the ground and not lifting off or coming down fast (a toe-off or a strike in progress is not a contact yet),
                    // unless it already touches.
                    low[f] = sole[f] < ContactHeight && (sole[f] < 0.006 || Math.Abs(sole[f1] - sole[f0]) * Fps / Math.Max(1, f1 - f0) < 0.3);
                    speed[f] = MotionPhysicsMetrics.Horizontal(At(Pivot(pivot[f]), f1) - At(Pivot(pivot[f]), f0)) * Fps / Math.Max(1, f1 - f0);
                }
                for (var f = 1; f < N - 1; f++) if (!low[f] && low[f - 1] && low[f + 1] && sole[f] < 2 * ContactHeight) low[f] = true;

                var corr = new Vec3[N];
                var inContact = new bool[N];
                foreach (var span in MotionPhysicsMetrics.Runs(low))
                {
                    if (Cyclic && (span.Start == 0 || span.End == N - 1)) continue;
                    var sorted = Enumerable.Range(span.Start, span.Length).Select(f => speed[f]).OrderBy(v => v).ToArray();
                    if (span.Length < 2 || sorted[sorted.Length / 2] >= ContactSpeed)
                    {
                        // Skimming (moving fast near the ground): a foot that touches must be still, so this one stays just above it.
                        for (var f = span.Start; f <= span.End; f++)
                        {
                            corr[f] = new Vec3(0, Math.Max(0, SkimHeight - sole[f]), 0);
                            inContact[f] = true;
                        }
                        continue;
                    }
                    // A foot still moving fast as it touches (or as it leaves) skims until it slows: the plant starts where it is still.
                    int s0 = span.Start, s1 = span.End;
                    while (s0 < s1 && speed[s0] >= ContactSpeed) s0++;
                    while (s1 > s0 && speed[s1] >= ContactSpeed) s1--;
                    for (var f = span.Start; f <= span.End; f++)
                        if (f < s0 || f > s1)
                        {
                            corr[f] = new Vec3(0, Math.Max(0, SkimHeight - sole[f]), 0);
                            inContact[f] = true;
                        }
                    var c = Vec3.Zero;
                    var current = -1;
                    var anchor = Vec3.Zero;
                    for (var f = s0; f <= s1; f++)
                    {
                        var p = At(Pivot(pivot[f]), f);
                        if (pivot[f] != current)
                        {
                            // A new pivot is anchored where it is now, corrected like the frame before (no jump).
                            anchor = p + new Vec3(c.X, 0, c.Z);
                            current = pivot[f];
                        }
                        c = new Vec3(anchor.X - p.X, -sole[f], anchor.Z - p.Z);
                        corr[f] = c;
                        inContact[f] = true;
                    }
                }
                // Ease the correction in and out around each contact (never pushing a foot below the ground).
                var final = new Vec3[N];
                for (var f = 0; f < N; f++)
                {
                    if (inContact[f]) { final[f] = corr[f]; continue; }
                    var sum = Vec3.Zero;
                    var wsum = 0.0;
                    foreach (var dir in new[] { -1, 1 })
                        for (var d = 1; d <= ease; d++)
                        {
                            var k = f + dir * d;
                            if (k < 0 || k >= N) break;
                            if (!inContact[k]) continue;
                            var w = Smooth(1 - (double)d / (ease + 1));
                            sum += corr[k] * w;
                            wsum += w;
                            break;
                        }
                    var r = wsum > 1 ? sum / wsum : sum;
                    final[f] = r with { Y = Math.Max(r.Y, -sole[f]) };
                }
                if (Cyclic) final[0] = final[^1] = Vec3.Zero;
                for (var f = 0; f < N; f++)
                {
                    if (final[f].LengthSquared() < 1e-14) continue;
                    SolveLeg(f, leg, models[f][leg.Ankle].Translation + final[f]);
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>Lifts any foot whose sole is below the ground (leg IK).</summary>
        public bool FeetAboveGround()
        {
            var changed = false;
            foreach (var leg in _legs)
                for (var f = 0; f < N; f++)
                {
                    if (Cyclic && (f == 0 || f == N - 1)) continue;
                    var m = Model(f);
                    var h = Body.SoleHeight(leg.Foot, m);
                    if (h >= -1e-4) continue;
                    SolveLeg(f, leg, m[leg.Ankle].Translation + new Vec3(0, -h, 0));
                    changed = true;
                }
            return changed;
        }

        /// <summary>Two-bone IK of a leg so its ankle reaches <paramref name="target"/>; the foot keeps its model rotation.</summary>
        void SolveLeg(int f, Leg leg, Vec3 target)
        {
            var loc = L[f];
            var m = Ev.Model(loc);
            int iu = leg.Thigh, il = leg.Calf, ie = leg.Ankle;
            var hip = m[iu].Translation;
            var reach = (m[il].Translation - hip).Length() + (m[ie].Translation - m[il].Translation).Length();
            var toTarget = target - hip;
            var dist = toTarget.Length();
            var own = (m[ie].Translation - hip).Length();
            double limit = 0.9995 * reach, knee = 0.015 * reach;
            if (dist > own && dist > limit - knee)
                target = hip + toTarget * (Math.Max(own, limit - knee * Math.Exp(-(dist - (limit - knee)) / knee)) / dist);
            var r = TwoBoneIk.Solve(hip, m[il].Translation, m[ie].Translation, target, null, Vec3.UnitZ);
            var parent = Ev.ParentOf(iu);
            var parentRot = parent < 0 ? Quat.Identity : m[parent].Rotation;
            var upper = (r.UpperDelta * m[iu].Rotation).Normalized();
            var lower = (r.LowerDelta * r.UpperDelta * m[il].Rotation).Normalized();
            loc[iu] = loc[iu] with { Rotation = (parentRot.Inverse() * upper).Normalized() };
            loc[il] = loc[il] with { Rotation = (upper.Inverse() * lower).Normalized() };
            loc[ie] = loc[ie] with { Rotation = (lower.Inverse() * m[ie].Rotation).Normalized() };
        }
    }

    static double Smooth(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }
}
