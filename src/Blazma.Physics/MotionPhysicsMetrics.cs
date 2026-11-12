using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Physics;

/// <summary>A run of frames [Start, End] (inclusive).</summary>
public readonly record struct FrameSpan(int Start, int End)
{
    public int Length => End - Start + 1;
    public bool Contains(int f) => f >= Start && f <= End;
}

/// <summary>Physical plausibility numbers of a clip (all model space, ground = the rest pose's sole).</summary>
public sealed record MotionPhysicsReport
{
    public int Frames { get; init; }
    public double Fps { get; init; }

    /// <summary>Largest horizontal drift (m) of a sole point (ball points: their toe joint) from where it touched down, while it stays within 5 mm of the ground (as GaitMetrics).</summary>
    public double FootSlide { get; init; }
    public string FootSlideAt { get; init; } = "";

    /// <summary>Lowest body surface point (capsules and soles) relative to the ground over the clip (m; negative = penetration).</summary>
    public double MinBodyHeight { get; init; }
    public string MinBodyAt { get; init; } = "";

    /// <summary>Flight phases (no part within 3 cm of the ground for ≥ 3 frames, contact on both sides).</summary>
    public IReadOnlyList<FrameSpan> Flights { get; init; } = Array.Empty<FrameSpan>();

    /// <summary>Worst RMS distance (m) of the flight COM from the best ballistic fit (g = 9.81 vertical, constant horizontal velocity).</summary>
    public double FlightParabolaRms { get; init; }

    /// <summary>Largest rate of change of the whole-body angular momentum about the COM during flight (N·m; 0 = conserved).</summary>
    public double FlightAngularMomentumRate { get; init; }

    public double ComAccelMax { get; init; }
    public double ComJerkRms { get; init; }
    public double ComJerkMax { get; init; }

    /// <summary>Largest frame-to-frame change of the pelvis horizontal velocity (m/s), anywhere and within ±0.3 s of a step boundary.</summary>
    public double RootVelocityJump { get; init; }
    public double BoundaryVelocityJump { get; init; }

    /// <summary>Largest pelvis yaw angular acceleration (deg/s²) while the pelvis is not lying or bent double.</summary>
    public double YawAccelMax { get; init; }

    /// <summary>Smallest COM margin to the support polygon on standing frames (both feet down, COM slow and high), m; +∞ when none.</summary>
    public double StandingMarginMin { get; init; } = double.PositiveInfinity;
    public int StandingFrames { get; init; }

    /// <summary>Largest knee flexion (deg) within 0.3 s after each landing (end of a flight).</summary>
    public IReadOnlyList<double> LandingKneeFlex { get; init; } = Array.Empty<double>();

    /// <summary>Smallest knee flexion (deg; negative = hyperextended).</summary>
    public double KneeMin { get; init; }

    /// <summary>Deepest hand inside the torso capsules (m, ≥ 0).</summary>
    public double HandTorsoPenetration { get; init; }

    /// <summary>The descent of a fall into a lying pose (COM drop ≥ 0.3 H), if any.</summary>
    public FrameSpan? Fall { get; init; }

    /// <summary>COM downward speed at the impact (first contact of a part other than the feet) relative to the fastest descent speed
    /// before it (1 = still accelerating into the ground, ≪ 1 = easing in as if held up).</summary>
    public double FallImpactRatio { get; init; }

    /// <summary>The clip ends lying (COM below 0.35 H): its lowest point above the ground (m) and the parts within 2 cm.</summary>
    public bool EndsLying { get; init; }
    public double RestGap { get; init; }
    public IReadOnlyList<string> RestContacts { get; init; } = Array.Empty<string>();

    public override string ToString() => string.Join("\n", new[]
    {
        $"  frames {Frames} @ {Fps:0}; foot slide {FootSlide * 100:0.00} cm ({FootSlideAt}); lowest body point {MinBodyHeight * 1000:0.0} mm ({MinBodyAt})",
        $"  flights {Flights.Count} [{string.Join(", ", Flights.Select(f => $"{f.Start}-{f.End}"))}], parabola RMS {FlightParabolaRms * 100:0.00} cm, flight dL/dt {FlightAngularMomentumRate:0.0} N·m",
        $"  COM accel max {ComAccelMax:0.0} m/s², jerk RMS {ComJerkRms:0} max {ComJerkMax:0} m/s³; root Δv max {RootVelocityJump:0.000} (boundaries {BoundaryVelocityJump:0.000}) m/s; yaw accel max {YawAccelMax:0}°/s²",
        $"  standing margin min {StandingMarginMin * 100:0.0} cm on {StandingFrames} frames; landing knee flex [{string.Join(", ", LandingKneeFlex.Select(k => k.ToString("0.0")))}]°; knee min {KneeMin:0.0}°; hand in torso {HandTorsoPenetration * 100:0.0} cm",
        $"  fall {(Fall is { } fl ? $"{fl.Start}-{fl.End}" : "none")}, impact speed ratio {FallImpactRatio:0.00}",
        $"  ends lying {EndsLying}: rest gap {RestGap * 1000:0.0} mm, contacts [{string.Join(", ", RestContacts)}]",
    });
}

/// <summary>Measures <see cref="MotionPhysicsReport"/> on a clip (or on dense per-frame local transforms).</summary>
public static class MotionPhysicsMetrics
{
    const double Deg = 180 / Math.PI;
    public const double ContactHeight = 0.03, SlideContactHeight = 0.005, Gravity = 9.81;

    /// <summary>Last frame worth evaluating: the last key (clips hold their last key up to their duration), at most the duration.</summary>
    public static int LastFrame(Clip clip)
    {
        var lastKey = clip.Tracks.Where(t => t.Keys.Count > 0).Select(t => t.Keys[^1].Time.ToFrame(clip.Fps)).DefaultIfEmpty(clip.DurationFrames).Max();
        return (int)Math.Max(0, Math.Min(clip.DurationFrames, lastKey));
    }

    /// <summary>Local bone transforms on every frame 0..<see cref="LastFrame"/>.</summary>
    public static Transform[][] Locals(Clip clip, Rig rig, RigEvaluator ev)
    {
        var last = LastFrame(clip);
        var locals = new Transform[last + 1][];
        for (var f = 0; f <= last; f++) locals[f] = ev.Locals(ClipEvaluator.Evaluate(clip, rig, Flicks.FromFrame(f, clip.Fps)));
        return locals;
    }

    /// <summary>Step boundaries of a clip: its markers after frame 0 (planned generation and sequence bakes put one per step).</summary>
    public static IReadOnlyList<int> Boundaries(Clip clip) =>
        clip.Markers.Select(m => (int)m.Time.ToFrame(clip.Fps)).Where(f => f > 0).Distinct().OrderBy(f => f).ToList();

    public static MotionPhysicsReport Measure(Clip clip, Rig rig, IReadOnlyList<int>? boundaries = null)
    {
        var body = BodyGeometry.For(rig);
        return Measure(Locals(clip, rig, body.Evaluator), clip.Fps.FramesPerSecond, body, boundaries ?? Boundaries(clip));
    }

    /// <summary>Feet down per frame (lowest sole point within <paramref name="height"/>) and frames where some other part is that low.</summary>
    internal static (bool[][] FootDown, bool[] Other) Contacts(BodyGeometry body, Transform[][] models, double height)
    {
        var n = models.Length;
        var feet = body.Feet.Feet.Select(_ => new bool[n]).ToArray();
        var other = new bool[n];
        for (var f = 0; f < n; f++)
        {
            for (var i = 0; i < feet.Length; i++) feet[i][f] = body.SoleHeight(body.Feet.Feet[i], models[f]) < height;
            other[f] = body.Lowest(models[f], soles: false).Height < height;
        }
        return (feet, other);
    }

    /// <summary>Flight phases: no part within <see cref="ContactHeight"/> for at least <paramref name="minFrames"/> frames, with contact before and after.</summary>
    public static List<FrameSpan> Flights(BodyGeometry body, Transform[][] models, int minFrames = 3)
    {
        var (feet, other) = Contacts(body, models, ContactHeight);
        var n = models.Length;
        var air = Enumerable.Range(0, n).Select(f => !other[f] && feet.All(x => !x[f])).ToArray();
        return Runs(air).Where(s => s.Length >= minFrames && s.Start > 0 && s.End < n - 1).ToList();
    }

    public static IEnumerable<FrameSpan> Runs(bool[] on)
    {
        for (var i = 0; i < on.Length;)
        {
            if (!on[i]) { i++; continue; }
            var s = i;
            while (i < on.Length && on[i]) i++;
            yield return new FrameSpan(s, i - 1);
        }
    }

    /// <summary>RMS residual (m) of points against the best ballistic fit: vertical y = a + b·t − g/2·t², horizontal x = a + b·t.</summary>
    public static double ParabolaRms(IReadOnlyList<Vec3> p, double dt)
    {
        if (p.Count < 3) return 0;
        double Fit(Func<int, double> y)
        {
            int n = p.Count;
            double st = 0, sy = 0, stt = 0, sty = 0;
            for (var i = 0; i < n; i++) { var t = i * dt; st += t; sy += y(i); stt += t * t; sty += t * y(i); }
            var b = (n * sty - st * sy) / (n * stt - st * st);
            var a = (sy - b * st) / n;
            var e = 0.0;
            for (var i = 0; i < n; i++) e += Math.Pow(y(i) - (a + b * i * dt), 2);
            return e;
        }
        var ex = Fit(i => p[i].X);
        var ez = Fit(i => p[i].Z);
        var ey = Fit(i => p[i].Y + 0.5 * Gravity * (i * dt) * (i * dt));
        return Math.Sqrt((ex + ey + ez) / p.Count);
    }

    /// <summary>Knee flexion (deg) of a leg: angle between thigh and shin, negative when bent backwards (about the pelvis' lateral axis).</summary>
    internal static double KneeFlex(Transform[] m, int thigh, int calf, int foot, Vec3 lateral)
    {
        var u = (m[calf].Translation - m[thigh].Translation).Normalized();
        var l = (m[foot].Translation - m[calf].Translation).Normalized();
        var ang = Math.Acos(Math.Clamp(Vec3.Dot(u, l), -1, 1)) * Deg;
        return Vec3.Dot(Vec3.Cross(u, l), lateral) >= -1e-9 ? ang : -ang;
    }

    public static MotionPhysicsReport Measure(Transform[][] locals, double fps, BodyGeometry body, IReadOnlyList<int> boundaries)
    {
        var ev = body.Evaluator;
        var rig = ev.Rig;
        var n = locals.Length;
        var dt = 1 / fps;
        var models = locals.Select(ev.Model).ToArray();
        var rest = ev.RestModel();
        int B(string std) => rig.FindByStandardName(std) is { } b ? ev.IndexOf(b.Id) : -1;
        var pelvis = B(H.Pelvis);
        var com = models.Select(body.Mass.CenterOfMass).ToArray();
        var ground = body.Ground;

        // ---- foot slide in contact (as GaitMetrics: ball points are tracked at the toe joint they roll about)
        double slide = 0;
        var slideAt = "";
        foreach (var foot in body.Feet.Feet)
            foreach (var pt in foot.Points)
            {
                Vec3? start = null;
                for (var f = 0; f < n; f++)
                {
                    var p = models[f][pt.Bone].TransformPoint(pt.Local);
                    if (p.Y - ground >= SlideContactHeight) { start = null; continue; }
                    var at = pt.Name.StartsWith("ball", StringComparison.Ordinal) && foot.ToeBone >= 0 ? models[f][foot.ToeBone].Translation : p;
                    start ??= at;
                    var d = Horizontal(at - start.Value);
                    if (d > slide) { slide = d; slideAt = $"{foot.Side} {pt.Name} @{f}"; }
                }
            }

        // ---- lowest body point
        var minH = double.MaxValue;
        var minAt = "";
        for (var f = 0; f < n; f++)
        {
            var (h, part) = body.Lowest(models[f]);
            if (h < minH) { minH = h; minAt = $"{part} @{f}"; }
        }

        // ---- flights: parabola fit and angular momentum
        var flights = Flights(body, models);
        double rms = 0, dL = 0;
        var segs = body.Mass.Segments;
        Vec3 AngularMomentum(int f, int g)
        {
            // About the COM, from segment centre velocities between frames f and g (= f + 1).
            Vec3[] a = body.Mass.SegmentCentres(models[f]), b = body.Mass.SegmentCentres(models[g]);
            var c = (com[f] + com[g]) * 0.5;
            var vc = (com[g] - com[f]) * fps;
            var l = Vec3.Zero;
            for (var i = 0; i < segs.Count; i++)
                l += Vec3.Cross((a[i] + b[i]) * 0.5 - c, (b[i] - a[i]) * fps - vc) * segs[i].Mass;
            return l;
        }
        foreach (var s in flights)
        {
            rms = Math.Max(rms, ParabolaRms(com[s.Start..(s.End + 1)], dt));
            for (var f = s.Start; f < s.End - 1; f++) dL = Math.Max(dL, (AngularMomentum(f + 1, f + 2) - AngularMomentum(f, f + 1)).Length() * fps);
        }

        // ---- COM acceleration and jerk
        double accMax = 0, jerkMax = 0, jerkSum = 0;
        var jerkCount = 0;
        for (var f = 1; f < n - 1; f++) accMax = Math.Max(accMax, (com[f + 1] - com[f] * 2 + com[f - 1]).Length() * fps * fps);
        for (var f = 1; f < n - 2; f++)
        {
            var j = (com[f + 2] - com[f + 1] * 3 + com[f] * 3 - com[f - 1]).Length() * fps * fps * fps;
            jerkMax = Math.Max(jerkMax, j);
            jerkSum += j * j;
            jerkCount++;
        }

        // ---- root velocity jumps and yaw pops
        double vJump = 0, vJumpB = 0, yawAcc = 0;
        var near = (int)Math.Round(0.3 * fps);
        if (pelvis >= 0 && n > 3)
        {
            var pp = models.Select(m => m[pelvis].Translation).ToArray();
            for (var f = 1; f < n - 1; f++)
            {
                var d = pp[f + 1] - pp[f] * 2 + pp[f - 1];
                var jump = Math.Sqrt(d.X * d.X + d.Z * d.Z) * fps;
                vJump = Math.Max(vJump, jump);
                if (boundaries.Any(b => Math.Abs(f - b) <= near)) vJumpB = Math.Max(vJumpB, jump);
            }
            var rel = models.Select(m => m[pelvis].Rotation * rest[pelvis].Rotation.Inverse()).ToArray();
            var yaw = Unwrap(rel.Select(Yaw).ToArray());
            // Heading is only defined while the pelvis' forward axis is not near vertical (not lying or bent double).
            bool Level(int f) => Math.Abs(rel[f].Rotate(Vec3.UnitZ).Y) < 0.7;
            for (var f = 1; f < n - 1; f++)
                if (Level(f - 1) && Level(f) && Level(f + 1)) yawAcc = Math.Max(yawAcc, Math.Abs(yaw[f + 1] - 2 * yaw[f] + yaw[f - 1]) * fps * fps * Deg);
        }

        // ---- standing balance margin
        var (down, other) = Contacts(body, models, ContactHeight);
        double margin = double.PositiveInfinity;
        var standing = 0;
        for (var f = 0; f < n; f++)
        {
            if (body.Feet.Feet.Count < 2 || down.Any(x => !x[f]) || other[f]) continue;
            var v = (com[Math.Min(n - 1, f + 1)] - com[Math.Max(0, f - 1)]) * (fps / Math.Max(1, Math.Min(n - 1, f + 1) - Math.Max(0, f - 1)));
            if (Horizontal(v) > 0.15 || com[f].Y - ground < 0.45 * body.Height) continue;
            var pts = body.Feet.Feet.SelectMany(ft => ft.Points.Select(p => models[f][p.Bone].TransformPoint(p.Local)))
                .Where(p => p.Y - ground < ContactHeight).Select(Vec2.Of).ToList();
            var hull = Geometry2D.ConvexHull(pts);
            if (hull.Count < 3) continue;
            standing++;
            margin = Math.Min(margin, Geometry2D.SignedDistance(hull, Vec2.Of(com[f])));
        }

        // ---- knees, landings
        var legs = new[] { Side.Left, Side.Right }.Select(s => (T: B(H.Thigh(s)), C: B(H.Calf(s)), F: B(H.Foot(s)))).Where(l => l.T >= 0 && l.C >= 0 && l.F >= 0).ToArray();
        Vec3 Lateral(int f) => pelvis >= 0 ? (models[f][pelvis].Rotation * rest[pelvis].Rotation.Inverse()).Rotate(Vec3.UnitX) : Vec3.UnitX;
        double MaxFlex(int f) => legs.Length == 0 ? 0 : legs.Max(l => KneeFlex(models[f], l.T, l.C, l.F, Lateral(f)));
        var kneeMin = legs.Length == 0 ? 0 : Enumerable.Range(0, n).Min(f => legs.Min(l => KneeFlex(models[f], l.T, l.C, l.F, Lateral(f))));
        var window = (int)Math.Round(0.3 * fps);
        var landing = flights.Select(s => Enumerable.Range(s.End + 1, Math.Max(1, Math.Min(window, n - s.End - 1))).Max(MaxFlex)).ToList();

        // ---- hands in the torso
        var torso = body.Capsules.Where(c => c.Part is H.Pelvis or H.Spine1 or H.Spine2 or H.Chest).ToArray();
        var hands = new[] { B(H.Hand(Side.Left)), B(H.Hand(Side.Right)) }.Where(h => h >= 0).ToArray();
        var handPen = 0.0;
        for (var f = 0; f < n; f++)
            foreach (var h in hands)
                foreach (var c in torso)
                {
                    var a = models[f][c.Bone].Translation;
                    var b = models[f][c.Bone].TransformPoint(c.Distal);
                    handPen = Math.Max(handPen, c.Radius - DistanceToSegment(models[f][h].Translation, a, b));
                }

        // ---- fall
        var fall = FallDescent(com, body, fps, flights);
        var impact = fall is { } fd ? ImpactRatio(com.Select(c => c.Y - ground).ToArray(), fd.Start, FirstBodyContact(body, models, fd), fps) : 0;

        // ---- rest at the end
        var lying = n > 0 && com[^1].Y - ground < 0.35 * body.Height;
        return new MotionPhysicsReport
        {
            Frames = n, Fps = fps, FootSlide = slide, FootSlideAt = slideAt, MinBodyHeight = minH, MinBodyAt = minAt,
            Flights = flights, FlightParabolaRms = rms, FlightAngularMomentumRate = dL,
            ComAccelMax = accMax, ComJerkMax = jerkMax, ComJerkRms = Math.Sqrt(jerkSum / Math.Max(1, jerkCount)),
            RootVelocityJump = vJump, BoundaryVelocityJump = vJumpB, YawAccelMax = yawAcc,
            StandingMarginMin = margin, StandingFrames = standing, LandingKneeFlex = landing, KneeMin = kneeMin, HandTorsoPenetration = handPen,
            Fall = fall, FallImpactRatio = impact, EndsLying = lying, RestGap = lying ? body.Lowest(models[^1]).Height : 0,
            RestContacts = lying ? body.Touching(models[^1], 0.02) : Array.Empty<string>(),
        };
    }

    /// <summary>The last fall in the clip: from the last high frame (within 3 cm of the top of the 3 s before, after any flight) to the
    /// first frame within 2 cm of the lowest COM height reached afterwards; the COM must drop ≥ 0.3 H and end below 0.4 H.</summary>
    public static FrameSpan? FallDescent(IReadOnlyList<Vec3> com, BodyGeometry body, double fps, IReadOnlyList<FrameSpan> flights)
    {
        var n = com.Count;
        if (n < 4) return null;
        var y = com.Select(c => c.Y - body.Ground).ToArray();
        var suffixMin = new double[n];
        suffixMin[^1] = y[^1];
        for (var f = n - 2; f >= 0; f--) suffixMin[f] = Math.Min(y[f], suffixMin[f + 1]);
        var c = -1;
        for (var f = 0; f < n; f++) if (y[f] < 0.4 * body.Height && y[f] <= suffixMin[f] + 0.02) { c = f; break; }
        if (c < 3) return null;
        var from = Math.Max(0, c - (int)Math.Round(3 * fps));
        foreach (var fl in flights) if (fl.End < c) from = Math.Max(from, fl.End + 1); // a jump's descent is not a fall
        if (from >= c) return null;
        var top = Enumerable.Range(from, c - from + 1).Max(f => y[f]);
        var a = c;
        while (a > from && y[a] < top - 0.03) a--;
        return top - y[c] >= 0.3 * body.Height && c - a >= 3 ? new FrameSpan(a, c) : null;
    }

    /// <summary>First frame of a fall's descent where a part other than the feet comes within <see cref="ContactHeight"/> of the
    /// ground (hands, knees, hips…: the impact), or the end of the descent.</summary>
    public static int FirstBodyContact(BodyGeometry body, IReadOnlyList<Transform[]> models, FrameSpan fall)
    {
        for (var f = fall.Start + 2; f <= fall.End; f++)
            if (body.Lowest(models[f], soles: false).Height < ContactHeight) return f;
        return fall.End;
    }

    /// <summary>Downward COM speed into frame <paramref name="end"/> relative to the fastest descent over (start, end].</summary>
    public static double ImpactRatio(double[] y, int start, int end, double fps)
    {
        var max = 0.0;
        for (var f = start + 1; f <= end; f++) max = Math.Max(max, (y[f - 1] - y[f]) * fps);
        return max <= 1e-9 ? 0 : Math.Max(0, (y[end - 1] - y[end]) * fps) / max;
    }

    internal static double Horizontal(Vec3 v) => Math.Sqrt(v.X * v.X + v.Z * v.Z);

    /// <summary>Heading (rad) of the rotated +Z axis about +Y.</summary>
    internal static double Yaw(Quat q)
    {
        var z = q.Rotate(Vec3.UnitZ);
        return Math.Atan2(z.X, z.Z);
    }

    internal static double[] Unwrap(double[] a)
    {
        var r = (double[])a.Clone();
        for (var i = 1; i < r.Length; i++)
        {
            var d = r[i] - r[i - 1];
            r[i] -= Math.Round(d / (2 * Math.PI)) * 2 * Math.PI;
        }
        return r;
    }

    internal static double DistanceToSegment(Vec3 p, Vec3 a, Vec3 b)
    {
        var ab = b - a;
        var t = ab.LengthSquared() < 1e-12 ? 0 : Math.Clamp(Vec3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return (a + ab * t - p).Length();
    }
}
