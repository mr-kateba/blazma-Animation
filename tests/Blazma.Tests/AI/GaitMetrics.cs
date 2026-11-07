using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Physics;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.AI;

/// <summary>Gait quality numbers of a locomotion clip on a humanoid rig (sole points from <see cref="FootModel"/>).</summary>
/// <param name="MaxStanceSlide">Largest horizontal drift (m) of a sole point from where it touched down, while it stays in contact (/// <param name="MaxStanceSlide">Largest horizontal drift (m) of a sole point from where it touched down, while it stays in contact (&lt; 1 cm high).</param>lt; 5 mm high).</param>
/// <param name="MaxStanceSpeed">Largest horizontal speed (m/s) of a sole point between two contact frames.</param>
/// <param name="MaxPenetration">Deepest sole point below the ground (m, ≥ 0).</param>
/// <param name="KneeMin">Smallest knee flexion (deg); negative = hyperextended.</param>
/// <param name="KneeMax">Largest knee flexion (deg).</param>
/// <param name="KneeLoading">Knee flexion just after heel strike (max flexion in the first 25 % of a stance), deg.</param>
/// <param name="KneeMaxStep">Largest knee flexion change between sub-samples, deg per frame.</param>
/// <param name="MaxAngAccel">Largest local angular acceleration of any animated bone (deg/s²), sampled 4× per frame.</param>
/// <param name="AccelBone">Bone with that acceleration.</param>
/// <param name="AccelSpikiness">Max / RMS angular acceleration (≈3 for smooth periodic motion; large = pops/kinks).</param>
/// <param name="PelvisBob">Pelvis vertical range (m).</param>
/// <param name="PelvisBobPeaks">Pelvis height maxima per cycle (2 for a natural gait).</param>
/// <param name="PelvisSway">Pelvis lateral range relative to the travel line (m).</param>
/// <param name="PelvisMaxAccel">Largest pelvis linear acceleration (m/s²).</param>
/// <param name="ArmLegCorrelation">Correlation of the left hand's and left ankle's fore-aft offset from the pelvis (−1 = opposite).</param>
/// <param name="SeamPose">Largest local rotation difference between the loop end and start (deg).</param>
/// <param name="SeamVelocity">Angular velocity change across the loop seam relative to the largest frame-to-frame change inside
/// the clip, worst bone (≤ 1: the seam is as smooth as any other frame).</param>
/// <param name="Speed">Average forward speed (m/s).</param>
/// <param name="Contacts">Foot touch-downs (heel or ball) per foot per second.</param>
/// <param name="Alternating">The two feet touch down alternately.</param>
/// <param name="MinSupport">Fewest feet on the ground on any frame (0 = flight phase).</param>
public sealed record GaitReport(double MaxStanceSlide, double MaxStanceSpeed, double MaxPenetration,
    double KneeMin, double KneeMax, double KneeLoading, double KneeMaxStep,
    double MaxAngAccel, string AccelBone, double AccelSpikiness,
    double PelvisBob, int PelvisBobPeaks, double PelvisSway, double PelvisMaxAccel,
    double ArmLegCorrelation, double SeamPose, double SeamVelocity, double Speed, double Contacts, bool Alternating, int MinSupport,
    int Keys, string Interp, string SlideAt, string SeamBone, string PenetrationAt, string KneeAt, string AccelAt)
{
    public override string ToString() => string.Join("\n", new[]
    {
        $"  stance slide max {MaxStanceSlide * 100:0.00} cm ({SlideAt}), stance speed max {MaxStanceSpeed:0.000} m/s, penetration {MaxPenetration * 1000:0.0} mm ({PenetrationAt})",
        $"  knee flex {KneeMin:0.0}..{KneeMax:0.0}° (loading {KneeLoading:0.0}°), max step {KneeMaxStep:0.00}°/frame ({KneeAt})",
        $"  ang. accel max {MaxAngAccel:0} °/s² ({AccelBone} {AccelAt}), spikiness {AccelSpikiness:0.0}",
        $"  pelvis bob {PelvisBob * 100:0.0} cm ({PelvisBobPeaks} peaks/cycle), sway {PelvisSway * 100:0.0} cm, max accel {PelvisMaxAccel:0.0} m/s²",
        $"  arm/leg correlation {ArmLegCorrelation:0.00}; seam pose {SeamPose:0.00}°, seam velocity change {SeamVelocity:0.00}× interior max ({SeamBone})",
        $"  speed {Speed:0.00} m/s, touch-downs {Contacts:0.00}/s per foot, alternating {Alternating}, min support {MinSupport}; keys {Keys} ({Interp})",
    });
}

public static class GaitMetrics
{
    const double Deg = 180 / Math.PI;

    public static GaitReport Measure(Clip clip, Rig rig, int sub = 4)
    {
        var ev = new RigEvaluator(rig);
        var feet = FootModel.For(rig, ev);
        var ground = feet.RestSoleHeight;
        var n = (int)clip.DurationFrames;
        var fps = clip.Fps.FramesPerSecond;
        var cyclic = clip.Loop is { Enabled: true };
        int B(string std) => ev.IndexOf(rig.FindByStandardName(std)!.Id);
        var pelvis = B(H.Pelvis);
        var locals = new Transform[n][];
        var models = new Transform[n][];
        for (var f = 0; f < n; f++)
        {
            locals[f] = ev.Locals(ClipEvaluator.Evaluate(clip, rig, Flicks.FromFrame(f, clip.Fps)));
            models[f] = ev.Model(locals[f]);
        }

        // ---- sole contacts
        double slide = 0, slideSpeed = 0, pen = 0;
        var slideAt = "";
        var penAt = "";
        var touchDowns = new List<(int Frame, Side Side)>();
        var supportCount = new int[n];
        foreach (var foot in feet.Feet)
        {
            var footDown = new bool[n];
            foreach (var pt in foot.Points)
            {
                var p = models.Select(m => m[pt.Bone].TransformPoint(pt.Local)).ToArray();
                var contact = p.Select(x => x.Y - ground < 0.005).ToArray();
                // The ball pad rolls about the toe joint while the heel rises: its contact slides with the joint, not with a point
                // rigidly fixed under it.
                var track = pt.Name.StartsWith("ball") && foot.ToeBone >= 0 ? models.Select(m => m[foot.ToeBone].Translation).ToArray() : p;
                Vec3? start = null;
                for (var f = 0; f < n; f++)
                {
                    if (ground - p[f].Y > pen) { pen = ground - p[f].Y; penAt = $"{foot.Side} {pt.Name} @{f}"; }
                    if (!contact[f]) { start = null; continue; }
                    footDown[f] = true;
                    start ??= track[f];
                    if (Horizontal(track[f] - start.Value) > slide) { slide = Horizontal(track[f] - start.Value); slideAt = $"{foot.Side} {pt.Name} @{f}"; }
                    if (f > 0 && contact[f - 1]) slideSpeed = Math.Max(slideSpeed, Horizontal(track[f] - track[f - 1]) * fps);
                }
            }
            for (var f = 0; f < n; f++)
            {
                if (footDown[f]) supportCount[f]++;
                if (footDown[f] && f > 0 && !footDown[f - 1]) touchDowns.Add((f, foot.Side));
            }
        }
        var order = touchDowns.OrderBy(t => t.Frame).Select(t => t.Side).ToList();
        var alternating = order.Count >= 2 && order.Zip(order.Skip(1)).All(p => p.First != p.Second);

        // ---- knees (sub-sampled), loading flexion
        double kMin = double.MaxValue, kMax = double.MinValue, kStep = 0, loading = 0, kMinSeen = double.MaxValue;
        string kneeAt = "", kneeMinAt = "", accelAt = "";
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            int th = B(H.Thigh(side)), ca = B(H.Calf(side)), fo = B(H.Foot(side));
            var flex = new List<double>();
            for (var s = 0; s <= (n - 1) * sub; s++)
            {
                var m = ev.Model(ClipEvaluator.Evaluate(clip, rig, Flicks.FromSeconds(s / (fps * sub))));
                var u = (m[ca].Translation - m[th].Translation).Normalized();
                var l = (m[fo].Translation - m[ca].Translation).Normalized();
                var lateral = m[pelvis].Rotation.Rotate(Vec3.UnitX);
                var ang = Math.Acos(Math.Clamp(Vec3.Dot(u, l), -1, 1)) * Deg;
                flex.Add(Vec3.Dot(Vec3.Cross(u, l), lateral) >= 0 ? ang : -ang);
            }
            kMin = Math.Min(kMin, flex.Min());
            kMax = Math.Max(kMax, flex.Max());
            for (var i = 1; i < flex.Count; i++)
                if (Math.Abs(flex[i] - flex[i - 1]) * sub > kStep) { kStep = Math.Abs(flex[i] - flex[i - 1]) * sub; kneeAt = $"{side} @{(double)i / sub:0.00}"; }
            var iMin = flex.IndexOf(flex.Min());
            if (flex[iMin] < kMinSeen) { kMinSeen = flex[iMin]; kneeMinAt = $"{side} @{(double)iMin / sub:0.00}"; }
            foreach (var td in touchDowns.Where(t => t.Side == side))
            {
                var stanceLen = Math.Max(2, (int)Math.Round(0.15 * fps));
                for (var f = td.Frame; f < Math.Min(n, td.Frame + stanceLen); f++) loading = Math.Max(loading, flex[f * sub]);
            }
        }

        // ---- angular acceleration (sub-sampled, all rotation-keyed bones)
        var rotBones = clip.Tracks.Where(t => t.Channel == TrackChannels.Rotation && t.Target.Kind == TargetKind.Bone)
            .Select(t => ev.IndexOf(t.Target.Id)).Where(i => i >= 0).Distinct().ToArray();
        var total = (n - 1) * sub;
        var subLocals = new Transform[total + 1][];
        for (var s = 0; s <= total; s++) subLocals[s] = ev.Locals(ClipEvaluator.Evaluate(clip, rig, Flicks.FromSeconds(s / (fps * sub))));
        double maxA = 0, sumA2 = 0;
        var cntA = 0;
        var accelBone = "";
        var dt = 1 / (fps * sub);
        foreach (var b in rotBones)
        {
            // cyclic: sample index wraps over [0, total) since sample total == sample 0
            Vec3 W(int s)
            {
                int Wrap(int k) => cyclic ? ((k % total) + total) % total : Math.Clamp(k, 0, total);
                return AngVel(subLocals[Wrap(s)][b].Rotation, subLocals[Wrap(s + 1)][b].Rotation) / dt;
            }
            var lo = cyclic ? 0 : 1;
            var hi = cyclic ? total : total - 1;
            for (var s = lo; s < hi; s++)
            {
                var a = (W(s) - W(s - 1)).Length() / dt * Deg;
                sumA2 += a * a;
                cntA++;
                if (a > maxA) { maxA = a; accelBone = rig.Bones[b].Name; accelAt = $"@{(double)s / sub:0.00}"; }
            }
        }
        var rmsA = Math.Sqrt(sumA2 / Math.Max(1, cntA));

        // ---- seam
        double seamPose = 0, seamVel = 0;
        var seamBone = "";
        if (cyclic && n > 3)
        {
            foreach (var b in rotBones)
            {
                if (b == pelvis) continue; // pelvis yaw follows travel; compared below via its rotation anyway
                seamPose = Math.Max(seamPose, Quat.AngleBetween(locals[0][b].Rotation, locals[n - 1][b].Rotation) * Deg);
                // Velocity change across the seam relative to the largest frame-to-frame velocity change inside the clip.
                var w = Enumerable.Range(0, n - 1).Select(f => AngVel(locals[f][b].Rotation, locals[f + 1][b].Rotation) * fps).ToArray();
                var inner = 1e-9;
                for (var f = 1; f < n - 1; f++) inner = Math.Max(inner, (w[f] - w[f - 1]).Length());
                var ratio = (w[0] - w[^1]).Length() / inner;
                if ((w[0] - w[^1]).Length() * Deg > 1 && ratio > seamVel) { seamVel = ratio; seamBone = rig.Bones[b].Name; }
            }
        }

        // ---- pelvis
        var pp = models.Select(m => m[pelvis].Translation).ToArray();
        var bob = pp.Max(p => p.Y) - pp.Min(p => p.Y);
        var line = pp[^1] - pp[0];
        var dir = Horizontal(line) > 1e-6 ? new Vec3(line.X, 0, line.Z).Normalized() : Vec3.UnitZ;
        var side3 = Vec3.Cross(Vec3.UnitY, dir);
        var lat = pp.Select(p => Vec3.Dot(p - pp[0], side3)).ToArray();
        var sway = lat.Max() - lat.Min();
        var peaks = 0;
        var m1 = cyclic ? n - 1 : n;
        for (var f = 0; f < m1; f++)
        {
            int Prev(int k) => cyclic ? (k - 1 + m1) % m1 : Math.Max(0, k - 1);
            int Next(int k) => cyclic ? (k + 1) % m1 : Math.Min(n - 1, k + 1);
            if ((cyclic || (f > 0 && f < n - 1)) && pp[f].Y > pp[Prev(f)].Y && pp[f].Y >= pp[Next(f)].Y) peaks++;
        }
        var speed = Vec3.Dot(line, dir) / ((n - 1) / fps);
        var cycles = cyclic ? Math.Max(1, Math.Round((double)touchDowns.Count / 2)) : 1;
        var tdCount = touchDowns.Count(t => !cyclic || t.Frame < n - 1);
        var pelAcc = 0.0;
        for (var f = 1; f < n - 1; f++) pelAcc = Math.Max(pelAcc, (pp[f + 1] - pp[f] * 2 + pp[f - 1]).Length() * fps * fps);

        // ---- arm vs leg phase
        var hand = B(H.Hand(Side.Left));
        var ankle = B(H.Foot(Side.Left));
        var hz = models.Select(m => Vec3.Dot(m[hand].Translation - m[pelvis].Translation, dir)).ToArray();
        var az = models.Select(m => Vec3.Dot(m[ankle].Translation - m[pelvis].Translation, dir)).ToArray();
        var corr = Correlation(hz, az);

        var keys = clip.Tracks.Sum(t => t.Keys.Count);
        var interp = string.Join("/", clip.Tracks.SelectMany(t => t.Keys).GroupBy(k => k.Interp).Select(g => $"{g.Key} {g.Count()}"));
        var seconds = (n - 1) / fps;
        return new GaitReport(slide, slideSpeed, pen, kMin, kMax, loading, kStep, maxA, accelBone, maxA / Math.Max(1e-9, rmsA),
            bob, (int)Math.Round(peaks / (cyclic ? cycles : Math.Max(1, seconds))), sway, pelAcc, corr, seamPose, seamVel, speed,
            tdCount / 2.0 / seconds, alternating, supportCount.Min(), keys, interp, slideAt, seamBone, penAt, kneeAt + " min " + kneeMinAt, accelAt);
    }

    /// <summary>Largest horizontal drift (m) of a touching sole point (&lt; 5 mm high) of <paramref name="side"/> over frames
    /// [<paramref name="from"/>, <paramref name="to"/>], from where it touched down. Ball points are measured at the toe joint they
    /// roll about (as in <see cref="Measure"/>).</summary>
    public static double ContactDrift(Clip clip, Rig rig, Side side, long from, long to)
    {
        var ev = new RigEvaluator(rig);
        var feet = FootModel.For(rig, ev);
        var foot = feet.Feet.Single(x => x.Side == side);
        var models = new List<Transform[]>();
        for (var f = from; f <= to; f++) models.Add(ev.Model(ClipEvaluator.Evaluate(clip, rig, Flicks.FromFrame(f, clip.Fps))));
        var worst = 0.0;
        foreach (var pt in foot.Points)
        {
            Vec3? first = null;
            foreach (var m in models)
            {
                var p = m[pt.Bone].TransformPoint(pt.Local);
                if (p.Y - feet.RestSoleHeight > 0.005) { first = null; continue; }
                var at = pt.Name.StartsWith("ball") && foot.ToeBone >= 0 ? m[foot.ToeBone].Translation : p;
                first ??= at;
                worst = Math.Max(worst, Horizontal(at - first.Value));
            }
        }
        return worst;
    }

    /// <summary>Rotation vector (axis·angle, radians) from <paramref name="a"/> to <paramref name="b"/> (b = d·a).</summary>
    public static Vec3 AngVel(Quat a, Quat b)
    {
        var d = (b * a.Inverse()).Normalized();
        if (d.W < 0) d = new Quat(-d.X, -d.Y, -d.Z, -d.W);
        var s = Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
        if (s < 1e-12) return Vec3.Zero;
        var angle = 2 * Math.Atan2(s, d.W);
        return new Vec3(d.X, d.Y, d.Z) * (angle / s);
    }

    static double Horizontal(Vec3 v) => Math.Sqrt(v.X * v.X + v.Z * v.Z);

    static double Correlation(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
        for (var i = 0; i < a.Length; i++) { sab += (a[i] - ma) * (b[i] - mb); saa += (a[i] - ma) * (a[i] - ma); sbb += (b[i] - mb) * (b[i] - mb); }
        return sab / Math.Sqrt(Math.Max(1e-18, saa * sbb));
    }

    /// <summary>Largest model-space bone position (m) and local rotation (deg) difference between two clips, sampled
    /// <paramref name="sub"/> times per frame.</summary>
    public static (double Position, double RotationDeg) Difference(Clip a, Clip b, Rig rig, int sub = 4)
    {
        var ev = new RigEvaluator(rig);
        double dp = 0, dr = 0;
        var fps = a.Fps.FramesPerSecond;
        for (var s = 0; s <= (a.DurationFrames - 1) * sub; s++)
        {
            var t = Flicks.FromSeconds(s / (fps * sub));
            var la = ev.Locals(ClipEvaluator.Evaluate(a, rig, t));
            var lb = ev.Locals(ClipEvaluator.Evaluate(b, rig, t));
            var ma = ev.Model(la);
            var mb = ev.Model(lb);
            for (var i = 0; i < ma.Length; i++)
            {
                dp = Math.Max(dp, (ma[i].Translation - mb[i].Translation).Length());
                dr = Math.Max(dr, Quat.AngleBetween(la[i].Rotation, lb[i].Rotation) * Deg);
            }
        }
        return (dp, dr);
    }
}
