using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;

namespace Blazma.Physics;

/// <summary>Which ground point is tested against the support polygon.</summary>
public enum BalanceMetric
{
    /// <summary>Static: the centre of mass projected vertically.</summary>
    Com,

    /// <summary>Zero-moment point of the cart-table model (uses COM acceleration).</summary>
    Zmp,

    /// <summary>Instantaneous capture point of the linear inverted pendulum (uses COM velocity).</summary>
    CapturePoint,
}

public enum BalanceState { Balanced, Unbalanced, Airborne }

public sealed record BalanceOptions
{
    /// <summary>Ground plane height; null = the rig's rest sole height (0 for the mannequin).</summary>
    public double? GroundHeight { get; init; }

    /// <summary>A sole point is in contact when it is at most this high above the ground (m).</summary>
    public double ContactHeight { get; init; } = 0.03;

    /// <summary>…and moves slower than this (m/s; ignored for single poses and for pinned feet).</summary>
    public double ContactSpeed { get; init; } = 0.35;

    /// <summary>Required margin (m) for a frame to count as balanced.</summary>
    public double MinMargin { get; init; }

    public BalanceMetric Metric { get; init; } = BalanceMetric.Com;
    public double Gravity { get; init; } = 9.81;

    /// <summary>Total mass used when the rig has no bone masses (kg).</summary>
    public double FallbackTotalMass { get; init; } = MassModel.DefaultTotalMass;
}

/// <param name="Foot">Index in <see cref="FootModel.Feet"/>.</param>
public sealed record ContactPoint(int Foot, Side Side, string Point, Vec3 Position);

/// <summary>Balance of one pose / frame. Points are model space (metres); <see cref="Polygon"/> is counter-clockwise in XZ.</summary>
public sealed record PoseBalance
{
    public long Frame { get; init; }
    public Vec3 Com { get; init; }
    public Vec3 ComVelocity { get; init; }
    public Vec3 ComAcceleration { get; init; }

    /// <summary>Zero-moment point on the ground, or null when undefined (free fall, single pose).</summary>
    public Vec3? Zmp { get; init; }

    /// <summary>Capture point on the ground, or null when the COM is too low.</summary>
    public Vec3? CapturePoint { get; init; }

    public IReadOnlyList<ContactPoint> Contacts { get; init; } = Array.Empty<ContactPoint>();
    public IReadOnlyList<Vec2> Polygon { get; init; } = Array.Empty<Vec2>();
    public double GroundHeight { get; init; }

    /// <summary>Signed distance of the projected COM to the polygon (inside positive). NaN when airborne.</summary>
    public double ComMargin { get; init; }

    /// <summary>Signed distance of the metric point (COM, ZMP or capture point) to the polygon. NaN when airborne.</summary>
    public double Margin { get; init; }

    public BalanceState State { get; init; }
    public bool Balanced => State == BalanceState.Balanced;

    /// <summary>Feet (indices in <see cref="FootModel.Feet"/>) with at least one contact point.</summary>
    public IReadOnlyList<int> ContactFeet => Contacts.Select(c => c.Foot).Distinct().OrderBy(i => i).ToList();

    /// <summary>The polygon as model-space points on the ground (for drawing).</summary>
    public Vec3[] PolygonPoints => Polygon.Select(p => p.At(GroundHeight)).ToArray();
}

/// <param name="Start">First unbalanced frame.</param>
/// <param name="End">Last unbalanced frame (inclusive).</param>
public sealed record FrameRange(long Start, long End, double MinMargin, long WorstFrame);

public sealed record ClipBalanceReport(
    long StartFrame, long EndFrame, IReadOnlyList<PoseBalance> Frames, IReadOnlyList<FrameRange> UnbalancedRanges)
{
    public int BalancedFrames => Frames.Count(f => f.State == BalanceState.Balanced);
    public int UnbalancedFrames => Frames.Count(f => f.State == BalanceState.Unbalanced);
    public int AirborneFrames => Frames.Count(f => f.State == BalanceState.Airborne);
    /// <summary>The evaluated frames behind this report (frames StartFrame−1 … EndFrame+1 where they exist).</summary>
    public ClipSamples? Samples { get; init; }

    public PoseBalance? Worst => Frames.Where(f => f.State != BalanceState.Airborne && double.IsFinite(f.Margin)).OrderBy(f => f.Margin).ThenBy(f => f.Frame).FirstOrDefault();
}

/// <summary>Per-frame samples of an evaluated clip: poses, model transforms and centre of mass.</summary>
public sealed record ClipSamples(long StartFrame, double FrameSeconds, Pose[] Poses, Transform[][] Model, Vec3[] Com)
{
    public int Count => Poses.Length;
}

/// <summary>
/// Centre of mass, ground contacts, support polygon and balance for poses and clips.
/// <list type="bullet">
/// <item>Contacts: a sole point touches when <c>y − ground ≤ ContactHeight</c> and its speed ≤ ContactSpeed
/// (central differences); a foot whose leg IK target is pinned counts as stationary.</item>
/// <item>Support polygon: 2D convex hull (XZ) of the contact points.</item>
/// <item>Margin: signed distance of the tested point to the polygon, inside positive.</item>
/// <item>ZMP (cart-table): <c>p = c − (c_y − g)·a / (a_y + G)</c> for the horizontal components (undefined when a_y + G ≤ 0.1·G).</item>
/// <item>Capture point (LIPM): <c>ξ = c + v/ω₀</c>, <c>ω₀ = √(G / (c_y − g))</c>.</item>
/// <item>Velocity/acceleration: central finite differences over frames (one-sided at the clip ends).</item>
/// </list>
/// </summary>
public sealed class BalanceAnalyzer
{
    public BalanceAnalyzer(Rig rig, BalanceOptions? options = null)
    {
        Options = options ?? new BalanceOptions();
        Rig = rig;
        Mass = MassModel.For(rig, Options.FallbackTotalMass);
        Feet = FootModel.For(rig, Mass.Evaluator, Mass.EstimatedHeight);
        Ground = Options.GroundHeight ?? Feet.RestSoleHeight;
        var pelvis = rig.FindByStandardName(HumanoidStandard.Pelvis);
        PelvisBone = pelvis is null ? -1 : rig.IndexOf(pelvis.Id);
    }

    public Rig Rig { get; }
    public BalanceOptions Options { get; }
    public MassModel Mass { get; }
    public FootModel Feet { get; }
    public RigEvaluator Evaluator => Mass.Evaluator;
    public double Ground { get; }

    /// <summary>Index of the humanoid pelvis (hips) bone, or −1.</summary>
    public int PelvisBone { get; }

    /// <summary>Static balance of one pose: no velocities, every low sole point counts as a contact.</summary>
    public PoseBalance AnalyzePose(Pose pose, long frame = 0)
    {
        var model = Evaluator.Model(pose);
        var soles = Feet.SolePositions(model);
        return Build(frame, Mass.CenterOfMass(model), Vec3.Zero, Vec3.Zero, soles, null, null, staticPose: true);
    }

    /// <summary>Evaluates frames [start−1, end+1] (clamped to the clip) so derivatives exist at both ends.</summary>
    public ClipSamples Sample(Clip clip, long start, long end)
    {
        var last = LastFrame(clip);
        var from = Math.Max(0, start - 1);
        var to = Math.Min(last, end + 1);
        var n = (int)Math.Max(0, to - from + 1);
        var poses = new Pose[n];
        var model = new Transform[n][];
        var com = new Vec3[n];
        for (var i = 0; i < n; i++)
        {
            poses[i] = ClipEvaluator.Evaluate(clip, Rig, Flicks.FromFrame(from + i, clip.Fps));
            model[i] = Evaluator.Model(poses[i]);
            com[i] = Mass.CenterOfMass(model[i]);
        }
        return new ClipSamples(from, 1.0 / clip.Fps.FramesPerSecond, poses, model, com);
    }

    public static long LastFrame(Clip clip) => Math.Max(0, clip.DurationFrames - 1);

    public ClipBalanceReport AnalyzeClip(Clip clip, long? startFrame = null, long? endFrame = null)
    {
        var last = LastFrame(clip);
        var start = Math.Clamp(startFrame ?? 0, 0, last);
        var end = Math.Clamp(endFrame ?? last, start, last);
        var samples = Sample(clip, start, end);
        var soles = samples.Model.Select(Feet.SolePositions).ToArray();
        var frames = new List<PoseBalance>();
        for (var f = start; f <= end; f++)
            frames.Add(AnalyzeSample(samples, soles, (int)(f - samples.StartFrame)));
        return new ClipBalanceReport(start, end, frames, Ranges(frames)) { Samples = samples };
    }

    /// <summary>Balance at sample index <paramref name="i"/> with velocities from neighbouring samples.</summary>
    public PoseBalance AnalyzeSample(ClipSamples s, Vec3[][][] soles, int i)
    {
        var dt = s.FrameSeconds;
        var vel = Derivative(s.Com, i, dt);
        var acc = SecondDerivative(s.Com, i, dt);
        var speeds = new double[soles[i].Length][];
        for (var f = 0; f < soles[i].Length; f++)
        {
            speeds[f] = new double[soles[i][f].Length];
            for (var p = 0; p < soles[i][f].Length; p++)
                {
                    if (s.Count < 2) continue;
                    int a = Math.Max(0, i - 1), b = Math.Min(s.Count - 1, i + 1);
                    speeds[f][p] = ((soles[b][f][p] - soles[a][f][p]) / ((b - a) * dt)).Length();
                }
        }
        var pinned = Feet.Feet.Select(ft => ft.Chain is { } c && PoseSolver.StateOf(Rig, s.Poses[i], c.Target).Pinned).ToArray();
        return Build(s.StartFrame + i, s.Com[i], vel, acc, soles[i], speeds, pinned, staticPose: s.Count < 2);
    }

    PoseBalance Build(long frame, Vec3 com, Vec3 vel, Vec3 acc, Vec3[][] soles, double[][]? speeds, bool[]? pinned, bool staticPose)
    {
        var contacts = new List<ContactPoint>();
        for (var f = 0; f < soles.Length; f++)
        {
            var foot = Feet.Feet[f];
            for (var p = 0; p < soles[f].Length; p++)
            {
                var pos = soles[f][p];
                if (!(pos.Y - Ground <= Options.ContactHeight) || !double.IsFinite(pos.X) || !double.IsFinite(pos.Z)) continue; // also drops NaN
                var stationary = staticPose || (pinned?[f] ?? false) || speeds is null || speeds[f][p] <= Options.ContactSpeed;
                if (stationary) contacts.Add(new ContactPoint(f, foot.Side, foot.Points[p].Name, pos));
            }
        }
        var hull = Geometry2D.ConvexHull(contacts.Select(c => Vec2.Of(c.Position)));

        var g = Options.Gravity;
        var h = com.Y - Ground;
        Vec3? zmp = null;
        if (!staticPose && acc.Y + g > 0.1 * g)
        {
            var k = h / (acc.Y + g);
            zmp = new Vec3(com.X - k * acc.X, Ground, com.Z - k * acc.Z);
        }
        else if (staticPose) zmp = new Vec3(com.X, Ground, com.Z); // at rest the ZMP is the projected COM
        Vec3? capture = null;
        if (h > 0.05)
        {
            var w = Math.Sqrt(g / h);
            capture = new Vec3(com.X + vel.X / w, Ground, com.Z + vel.Z / w);
        }

        var comMargin = Geometry2D.SignedDistance(hull, Vec2.Of(com));
        var point = Options.Metric switch
        {
            BalanceMetric.Zmp => zmp ?? com,
            BalanceMetric.CapturePoint => capture ?? com,
            _ => com,
        };
        var margin = Options.Metric == BalanceMetric.Com ? comMargin : Geometry2D.SignedDistance(hull, Vec2.Of(point));
        var state = hull.Count == 0 ? BalanceState.Airborne
            : margin >= Options.MinMargin - 1e-9 ? BalanceState.Balanced : BalanceState.Unbalanced;
        return new PoseBalance
        {
            Frame = frame, Com = com, ComVelocity = vel, ComAcceleration = acc, Zmp = zmp, CapturePoint = capture,
            Contacts = contacts, Polygon = hull, GroundHeight = Ground, ComMargin = comMargin, Margin = margin, State = state,
        };
    }

    static List<FrameRange> Ranges(IReadOnlyList<PoseBalance> frames)
    {
        var ranges = new List<FrameRange>();
        PoseBalance? first = null, worst = null, prev = null;
        foreach (var f in frames)
        {
            if (f.State == BalanceState.Unbalanced)
            {
                if (first is null) { first = f; worst = f; }
                else if (f.Margin < worst!.Margin || !double.IsFinite(worst.Margin)) worst = f;
            }
            else if (first is not null)
            {
                ranges.Add(new FrameRange(first.Frame, prev!.Frame, worst!.Margin, worst.Frame));
                first = null;
            }
            prev = f;
        }
        if (first is not null) ranges.Add(new FrameRange(first.Frame, prev!.Frame, worst!.Margin, worst.Frame));
        return ranges;
    }

    /// <summary>First derivative by central differences (one-sided at the ends).</summary>
    public static Vec3 Derivative(IReadOnlyList<Vec3> x, int i, double dt)
    {
        if (x.Count < 2) return Vec3.Zero;
        if (i == 0) return (x[1] - x[0]) / dt;
        if (i == x.Count - 1) return (x[i] - x[i - 1]) / dt;
        return (x[i + 1] - x[i - 1]) / (2 * dt);
    }

    /// <summary>Second derivative by central differences (the nearest interior value at the ends).</summary>
    public static Vec3 SecondDerivative(IReadOnlyList<Vec3> x, int i, double dt)
    {
        if (x.Count < 3) return Vec3.Zero;
        var j = Math.Clamp(i, 1, x.Count - 2);
        return (x[j + 1] - x[j] * 2 + x[j - 1]) / (dt * dt);
    }
}
