using Blazma.Core.Mathematics;

namespace Blazma.AI;

/// <summary>
/// Walk and run cycles (and the planted idle): a gait model in world space, solved onto the SOMA legs with analytic two-bone IK.
/// <list type="bullet">
/// <item><b>Feet</b> follow footprints that never move while loaded: heel strike (toes up, the foot rolls down about the heel),
/// foot flat, heel off (the foot rolls about the ball, toes stay flat on the ground) and toe off. Swing is a quintic
/// (position, velocity and acceleration matched at toe off and heel strike) plus a clearance bump, so every curve is C².</item>
/// <item><b>Pelvis</b> travels at a constant speed with a vertical bob twice per cycle, lateral sway over the stance foot,
/// transverse rotation (the leading hip forward) and list (the swing side drops).</item>
/// <item><b>Upper body</b>: spine and chest counter-rotate against the pelvis, the head is stabilised (level, facing forward), arms
/// swing opposite to the legs with a small lag and the elbows follow through.</item>
/// <item>Stride length and cadence come from the speed (stride = speed / cycle frequency); looping rounds the frequency to whole
/// cycles over the clip, so the last frame repeats the first pose one or more strides further on.</item>
/// </list>
/// Phases are fractions of the gait cycle of the left leg (0 = left heel strike); the right leg is half a cycle later.
/// </summary>
public sealed partial class ProceduralMotionProvider
{
    /// <param name="Speed">Forward speed (m/s).</param>
    /// <param name="Freq">Nominal gait-cycle frequency (Hz, one left + one right step).</param>
    /// <param name="Duty">Fraction of the cycle a foot is on the ground (walk ≈ 0.62, run &lt; 0.5 = flight).</param>
    /// <param name="HeelStrike">Foot pitch at heel strike (deg, toes up).</param>
    /// <param name="ToeOff">Foot pitch at toe off (deg, negative = heel up).</param>
    /// <param name="FootFlat">Phase at which the foot is flat after heel strike.</param>
    /// <param name="HeelOff">Phase at which the heel starts to rise.</param>
    /// <param name="Clearance">Extra ankle lift in swing (m) and its peak position within the swing (0..1).</param>
    /// <param name="SwingLead">Extra forward ankle offset early in the swing (m): the foot leaves faster, so the knee keeps folding.</param>
    /// <param name="Retract">Ankle held back late in the swing (m): less forward overshoot before the strike (the leg does not lock out).</param>
    /// <param name="LateLift">Extra ankle lift late in the swing (m): the heel does not skim the ground before the strike.</param>
    /// <param name="Bob">Pelvis vertical amplitude (m; range = 2·Bob), lowest at <paramref name="BobLow"/> and half a cycle later.</param>
    /// <param name="KneeTarget">Knee flexion (deg) wanted at phase <paramref name="KneePhase"/> (loading response / mid stance): sets the pelvis height.</param>
    sealed record Gait(double Speed, double Freq, double Duty, double HeelStrike, double ToeOff, double FootFlat, double HeelOff,
        double StrikeRate, double ToeOffRate, double Clearance, double ClearancePeak, double SwingLead, double Retract, double LateLift, double StepWidth, double ToeOut, double StanceCentre,
        double Bob, double BobLow, double Sway, double Yaw, double Roll, double Tilt, double Lean,
        double ArmSwing, double ArmForward, double ArmLag, double Elbow, double ElbowSwing, double ElbowLag, double KneeTarget, double KneePhase);

    static readonly Gait WalkGait = new(Speed: 1.3, Freq: 0.95, Duty: 0.6, HeelStrike: 20, ToeOff: -60, FootFlat: 0.11, HeelOff: 0.27,
        StrikeRate: -1.2, ToeOffRate: 2.2, Clearance: 0.09, ClearancePeak: 0.25, SwingLead: 0.06, Retract: -0.025, LateLift: 0.015, StepWidth: 0.085, ToeOut: 7, StanceCentre: -0.10,
        Bob: 0.016, BobLow: 0.0, Sway: 0.02, Yaw: 4, Roll: 4, Tilt: 4, Lean: 2,
        ArmSwing: 17, ArmForward: 3, ArmLag: 0.04, Elbow: 18, ElbowSwing: 8, ElbowLag: 0.06, KneeTarget: 17, KneePhase: 0.12);

    static readonly Gait RunGait = new(Speed: 3.3, Freq: 1.4, Duty: 0.32, HeelStrike: 8, ToeOff: -50, FootFlat: 0.05, HeelOff: 0.14,
        StrikeRate: -1.2, ToeOffRate: 2.2, Clearance: 0.17, ClearancePeak: 0.3, SwingLead: 0.1, Retract: 0.2, LateLift: 0.04, StepWidth: 0.05, ToeOut: 4, StanceCentre: -0.15,
        Bob: 0.026, BobLow: 0.16, Sway: 0.01, Yaw: 7, Roll: 4, Tilt: 8, Lean: 6,
        ArmSwing: 30, ArmForward: 8, ArmLag: -0.06, Elbow: 85, ElbowSwing: 12, ElbowLag: 0.06, KneeTarget: 42, KneePhase: 0.16);

    static readonly int Neck2 = SomaSkeleton.IndexOf("Neck2"), LToe = SomaSkeleton.IndexOf("LeftToeBase"), RToe = SomaSkeleton.IndexOf("RightToeBase");

    /// <summary>Foot pivots relative to the ankle in the foot's rest frame: under the heel and under the ball (toe joint).</summary>
    static Vec3 HeelLocal => new(0, -AnkleHeight, -0.055);
    static Vec3 BallLocal => new(SomaSkeleton.Offset(LToe).X, -AnkleHeight, SomaSkeleton.Offset(LToe).Z);

    /// <summary>Quintic Hermite on u ∈ [0, 1] (derivatives per unit u).</summary>
    static double Quintic(double u, double p0, double v0, double a0, double p1, double v1, double a1)
    {
        double u2 = u * u, u3 = u2 * u, u4 = u3 * u, u5 = u4 * u;
        return (1 - 10 * u3 + 15 * u4 - 6 * u5) * p0 + (u - 6 * u3 + 8 * u4 - 3 * u5) * v0 + (0.5 * u2 - 1.5 * u3 + 1.5 * u4 - 0.5 * u5) * a0
            + (0.5 * u3 - u4 + 0.5 * u5) * a1 + (-4 * u3 + 7 * u4 - 3 * u5) * v1 + (10 * u3 - 15 * u4 + 6 * u5) * p1;
    }

    static double Frac(double x) => x - Math.Floor(x);

    /// <summary>Foot pitch (deg, + = toes up) over the leg's cycle: C² (zero acceleration at every join), exactly flat in foot flat.</summary>
    static double Pitch(Gait g, double p)
    {
        p = Frac(p);
        var hsSlope = g.HeelStrike * g.StrikeRate / g.FootFlat;          // dθ/dp just after heel strike
        var toSlope = g.ToeOff * g.ToeOffRate / (g.Duty - g.HeelOff);    // dθ/dp at toe off
        if (p < g.FootFlat) return Quintic(p / g.FootFlat, g.HeelStrike, hsSlope * g.FootFlat, 0, 0, 0, 0);
        if (p < g.HeelOff) return 0;
        if (p < g.Duty) return Quintic((p - g.HeelOff) / (g.Duty - g.HeelOff), 0, 0, 0, g.ToeOff, toSlope * (g.Duty - g.HeelOff), 0);
        // Swing: the ankle dorsiflexes quickly (toes up by mid swing, so they clear the ground), then eases into the strike.
        var sw = 1 - g.Duty;
        const double mid = 0.4;
        var midValue = 0.6 * g.HeelStrike;
        var midSlope = 1.4 * (g.HeelStrike - g.ToeOff) * 0.5;       // dθ/ds at the mid key
        var s = (p - g.Duty) / sw;
        return s < mid
            ? Quintic(s / mid, g.ToeOff, toSlope * sw * mid, 0, midValue, midSlope * mid, 0)
            : Quintic((s - mid) / (1 - mid), midValue, midSlope * (1 - mid), 0, g.HeelStrike, hsSlope * sw * (1 - mid), 0);
    }

    /// <summary>Ankle position while the foot is on its footprint (<paramref name="flat"/> = ankle position when the foot is flat):
    /// it rolls about the heel while the toes are up and about the ball while the heel is up.</summary>
    static Vec3 StanceAnkle(Gait g, double p, Vec3 flat, Quat yaw)
    {
        var th = Pitch(g, p);
        var r = yaw * Rx(-th);
        var pivot = th >= 0 ? HeelLocal : BallLocal;
        return flat + yaw.Rotate(pivot) - r.Rotate(pivot);
    }

    /// <summary>Ankle position, foot world rotation and toe bend (deg) of one leg at leg phase <paramref name="p"/>, time <paramref name="t"/>.</summary>
    static (Vec3 Ankle, Quat Foot, double Toe) FootAt(Gait g, double t, double p, double f, double stride, double side, double startZ)
    {
        var yaw = Ry(side * g.ToeOut);
        var strikeTime = t - p / f;
        var flat = new Vec3(side * g.StepWidth, AnkleHeight, startZ + g.Speed * (strikeTime + g.Duty / (2 * f)) + g.StanceCentre);
        var th = Pitch(g, p);
        if (p < g.Duty) return (StanceAnkle(g, p, flat, yaw), yaw * Rx(-th), Math.Min(0, th));

        // Swing: quintic from the toe-off state to the next heel-strike state (one stride further), plus a clearance bump.
        const double h = 1e-3;
        var sw = 1 - g.Duty;
        var next = flat + new Vec3(0, 0, stride);
        Vec3 x0 = StanceAnkle(g, g.Duty, flat, yaw), x1 = StanceAnkle(g, g.Duty - h, flat, yaw), x2 = StanceAnkle(g, g.Duty - 2 * h, flat, yaw);
        Vec3 y0 = StanceAnkle(g, 0, next, yaw), y1 = StanceAnkle(g, h, next, yaw), y2 = StanceAnkle(g, 2 * h, next, yaw);
        var v0 = (x0 * 3 - x1 * 4 + x2) / (2 * h) * sw;
        var a0 = (x0 - x1 * 2 + x2) / (h * h) * sw * sw;
        var v1 = (y0 * -3 + y1 * 4 - y2) / (2 * h) * sw;
        var a1 = (y0 - y1 * 2 + y2) / (h * h) * sw * sw;
        var s = (p - g.Duty) / sw;
        var ankle = new Vec3(
            Quintic(s, x0.X, v0.X, a0.X, y0.X, v1.X, a1.X),
            Quintic(s, x0.Y, v0.Y, a0.Y, y0.Y, v1.Y, a1.Y) + g.Clearance * Bump(s, g.ClearancePeak) + g.LateLift * Bump(1 - s, 0.25),
            Quintic(s, x0.Z, v0.Z, a0.Z, y0.Z, v1.Z, a1.Z) + g.SwingLead * Bump(s, 0.35) - g.Retract * Bump(1 - s, 0.3));
        // Toes: they keep counter-rotating the foot pitch (toes level, the tip lifts with the ball) and relax to straight by mid swing.
        var u = Math.Clamp(s / 0.5, 0, 1);
        var toe = th * (1 - u * u * u * (10 - 15 * u + 6 * u * u));
        return (ankle, yaw * Rx(-th), toe);
    }

    /// <summary>Smooth bump on [0, 1]: 0 with zero first and second derivatives at both ends, 1 at <paramref name="peak"/>.</summary>
    static double Bump(double s, double peak)
    {
        if (s <= 0 || s >= 1) return 0;
        var c = 3 / peak - 3;
        double B(double x) => Math.Pow(x, 3) * Math.Pow(1 - x, c);
        return B(s) / B(peak);
    }

    /// <summary>Shortest rotation taking <paramref name="restDir"/> to <paramref name="dir"/>, twisted so that the rest reference
    /// <paramref name="restRef"/> (perpendicular part) ends on the perpendicular part of <paramref name="reference"/>.</summary>
    static Quat Aim(Vec3 restDir, Vec3 restRef, Vec3 dir, Vec3 reference)
    {
        var q = Quat.FromTo(restDir, dir);
        var d = dir.Normalized();
        var from = q.Rotate(restRef);
        from = (from - d * Vec3.Dot(from, d)).Normalized();
        var to = (reference - d * Vec3.Dot(reference, d)).Normalized();
        var angle = Math.Atan2(Vec3.Dot(Vec3.Cross(from, to), d), Vec3.Dot(from, to));
        return (Quat.FromAxisAngle(d, angle) * q).Normalized();
    }

    static double ThighLen => SomaSkeleton.Offset(LShin).Length();
    static double ShinLen => SomaSkeleton.Offset(LFoot).Length();

    /// <summary>Two-bone IK of one SOMA leg: sets thigh, shin and foot local rotations so the ankle reaches <paramref name="ankle"/>
    /// with the knee bending towards <paramref name="pole"/> and the foot at world rotation <paramref name="footWorld"/>.</summary>
    static void SolveLeg(Frame fr, Quat hipsWorld, Vec3 hip, Vec3 ankle, Vec3 pole, Quat footWorld, int leg, int shin, int foot)
    {
        double l1 = ThighLen, l2 = ShinLen;
        var toAnkle = ankle - hip;
        var d = Math.Clamp(toAnkle.Length(), Math.Abs(l1 - l2) + 1e-4, l1 + l2 - 1e-6);
        var axis = toAnkle.Normalized();
        var bend = (pole - axis * Vec3.Dot(pole, axis)).Normalized();
        var cosA = Math.Clamp((l1 * l1 + d * d - l2 * l2) / (2 * l1 * d), -1, 1);
        var knee = hip + (axis * cosA + bend * Math.Sqrt(1 - cosA * cosA)) * l1;
        var end = hip + axis * d;
        var thighW = Aim(SomaSkeleton.Offset(shin), Vec3.UnitZ, knee - hip, bend);
        var shinW = Aim(SomaSkeleton.Offset(foot), Vec3.UnitZ, end - knee, bend);
        fr.R[leg] = (hipsWorld.Inverse() * thighW).Normalized();
        fr.R[shin] = (thighW.Inverse() * shinW).Normalized();
        fr.R[foot] = (shinW.Inverse() * footWorld).Normalized();
    }

    /// <summary>Knee flexion (deg) for a hip–ankle distance.</summary>
    static double KneeFlex(double d)
    {
        double l1 = ThighLen, l2 = ShinLen;
        var c = Math.Clamp((l1 * l1 + l2 * l2 - d * d) / (2 * l1 * l2), -1, 1);
        return 180 - Math.Acos(c) / Deg;
    }

    sealed record GaitPlan(Gait G, double F, double Stride, double Y0, double Phase0);

    static GaitPlan PlanGait(Gait g, double duration, bool loop, double phase0, double vary)
    {
        g = g with { Speed = g.Speed * (0.95 + 0.5 * (vary - 0.9) ), ArmSwing = g.ArmSwing * vary };
        var f = loop && duration > 0 ? Math.Max(1, Math.Round(duration * g.Freq)) / duration : g.Freq;
        var stride = g.Speed / f;
        // Pelvis height: the knee flexion wanted at the loading/mid-stance phase, never over-reaching anywhere in the cycle.
        double Dist(double y0, double phi)
        {
            var plan = new GaitPlan(g, f, stride, y0, 0);
            var t = phi / f;
            var (pelvis, hips) = Pelvis(plan, t, phi);
            var hip = pelvis + hips.Rotate(SomaSkeleton.Offset(LLeg));
            return (FootAt(g, t, phi, f, stride, 1, 0).Ankle - hip).Length();
        }
        double lo = 0.6, hi = 1.1;
        for (var i = 0; i < 50; i++) { var mid = (lo + hi) / 2; if (KneeFlex(Dist(mid, g.KneePhase)) > g.KneeTarget) lo = mid; else hi = mid; }
        var y = lo;
        var reach = (ThighLen + ShinLen) * 0.999;
        for (var k = 0; k < 400; k++)
        {
            var max = 0.0;
            for (var i = 0; i < 200; i++) max = Math.Max(max, Dist(y, i / 200.0));
            if (max <= reach) break;
            y -= 0.001;
        }
        return new GaitPlan(g, f, stride, y, phase0);
    }

    static (Vec3 Pos, Quat Rot) Pelvis(GaitPlan plan, double t, double phi)
    {
        var g = plan.G;
        var tau = 2 * Math.PI;
        var bob = g.Duty < 0.5 ? SpringMassBob(g, plan.F, phi) : -g.Bob * Math.Cos(2 * tau * (phi - g.BobLow));
        var pos = new Vec3(g.Sway * Math.Cos(tau * (phi - g.Duty / 2)), plan.Y0 + bob, g.Speed * t);
        var yaw = -g.Yaw * Math.Cos(tau * phi);
        var roll = g.Roll * Math.Cos(tau * (phi - 0.15));
        var tilt = g.Tilt + 1 * Math.Cos(2 * tau * (phi - g.BobLow));
        return (pos, (Ry(yaw) * Rz(roll) * Rx(tilt)).Normalized());
    }

    /// <summary>Pelvis height offset of a run (spring-mass model): during each flight the body is ballistic (−g, rising at take-off
    /// and falling at touch-down with v = g·T_flight/2), during each stance the leg spring compresses and returns
    /// (−C·sin(π·t/T_stance), C = v·T_stance/π), so height and vertical velocity are continuous at every touch-down and take-off.</summary>
    static double SpringMassBob(Gait g, double f, double phi)
    {
        const double gravity = 9.81;
        var q = Frac(phi * 2) / 2; // phase within the half cycle of one step
        double ts = g.Duty / f, tf = (0.5 - g.Duty) / f, v = gravity * tf / 2, c = v * ts / Math.PI;
        var t = q / f;
        if (t < ts) return -c * Math.Sin(Math.PI * t / ts);
        var tt = t - ts;
        return v * tt - 0.5 * gravity * tt * tt;
    }

    /// <summary>One frame of a walk or run cycle (world space; the hips position is the root).</summary>
    static void GaitFrame(Frame fr, GaitPlan plan, double t)
    {
        var g = plan.G;
        var phi = Frac(plan.F * t + plan.Phase0);
        var tShift = t + plan.Phase0 / plan.F; // time on the gait clock (phase0 shifts the cycle, the body still starts at Z = 0)
        var (pelvis, hips) = Pelvis(plan, tShift, phi);
        var z0 = -g.Speed * plan.Phase0 / plan.F;
        pelvis += new Vec3(0, 0, z0);
        fr.R[Hips] = hips;
        foreach (var (side, leg, shin, foot, toe, p) in new[] { (1.0, LLeg, LShin, LFoot, LToe, phi), (-1.0, RLeg, RShin, RFoot, RToe, Frac(phi + 0.5)) })
        {
            var (ankle, footW, toeBend) = FootAt(g, tShift, p, plan.F, plan.Stride, side, z0);
            var hip = pelvis + hips.Rotate(SomaSkeleton.Offset(leg));
            var pole = Ry(side * g.ToeOut * 0.5).Rotate(Vec3.UnitZ);
            SolveLeg(fr, hips, hip, ankle, pole, footW, leg, shin, foot);
            fr.R[toe] = Rx(toeBend);
        }

        // Upper body: counter-rotation, level shoulders, lean; head stabilised.
        var tau = 2 * Math.PI;
        var yaw = -g.Yaw * Math.Cos(tau * phi);
        var roll = g.Roll * Math.Cos(tau * (phi - 0.15));
        var tilt = g.Tilt + 1 * Math.Cos(2 * tau * (phi - g.BobLow));
        var lean = g.Lean - tilt;
        fr.R[Spine1] = Ry(-0.6 * yaw) * Rz(-0.5 * roll) * Rx(0.4 * lean);
        fr.R[Spine2] = Ry(-0.6 * yaw) * Rz(-0.3 * roll) * Rx(0.3 * lean);
        fr.R[Chest] = Ry(-0.6 * yaw) * Rz(-0.2 * roll) * Rx(0.3 * lean);
        StabiliseHead(fr, Rx(3));

        var swing = g.ArmSwing * Math.Cos(tau * (phi - g.ArmLag));
        var elbow = g.ElbowSwing * Math.Cos(tau * (phi - g.ArmLag - g.ElbowLag));
        ArmsDown(f: fr, swingL: -g.ArmForward + swing, swingR: -g.ArmForward - swing, elbowL: g.Elbow - elbow, elbowR: g.Elbow + elbow);
        fr.Root = pelvis;
        fr.Absolute = true;
    }

    /// <summary>Neck and head local rotations that put the head at world rotation <paramref name="headWorld"/> (half in the neck).</summary>
    static void StabiliseHead(Frame fr, Quat headWorld)
    {
        var chest = fr.R[Hips] * fr.R[Spine1] * fr.R[Spine2] * fr.R[Chest];
        fr.R[Neck1] = Quat.Slerp(Quat.Identity, (chest.Inverse() * headWorld).Normalized(), 0.5);
        fr.R[Neck2] = Quat.Identity;
        fr.R[Head] = ((chest * fr.R[Neck1] * fr.R[Neck2]).Inverse() * headWorld).Normalized();
    }

    /// <summary>Standing idle with both feet planted (IK): the pelvis sways and breathes over fixed feet.</summary>
    static void PlantedIdle(Frame fr, double t, double vary, double phase0, double loopDuration)
    {
        double Freq(double hz) => loopDuration > 0 ? Math.Max(1, Math.Round(loopDuration * hz)) / loopDuration : hz;
        var b = Math.Sin(2 * Math.PI * (Freq(0.25) * t + phase0));
        var sway = Math.Sin(2 * Math.PI * (Freq(0.1) * t + phase0));
        // Pelvis height for ≈15° knee flexion (soft knees: the sway never locks a leg out), lowered slightly with the breath.
        var standY = AnkleHeight + IdleHipDrop + 0.0015 * b;
        var pelvis = new Vec3(0.015 * sway, standY, 0);
        var hips = Rz(-1.0 * sway) * Rx(2);
        fr.R[Hips] = hips;
        foreach (var (side, leg, shin, foot) in new[] { (1.0, LLeg, LShin, LFoot), (-1.0, RLeg, RShin, RFoot) })
        {
            var restAnkle = SomaSkeleton.Offset(leg);
            var ankle = new Vec3(restAnkle.X + side * 0.01, AnkleHeight, 0.0);
            var hip = pelvis + hips.Rotate(SomaSkeleton.Offset(leg));
            SolveLeg(fr, hips, hip, ankle, Ry(side * 5).Rotate(Vec3.UnitZ), Ry(side * 6), leg, shin, foot);
        }
        fr.R[Spine1] = Rz(1.0 * sway) * Rx(-1);
        fr.R[Chest] = Rx(-2 * b * vary) * Rz(0.5 * sway);
        StabiliseHead(fr, Rx(1.5 * b));
        ArmsDown(fr, 3 * b, 3 * b, 12, 12);
        fr.Root = pelvis;
        fr.Absolute = true;
    }

    /// <summary>Hips height above the ankles for a ≈15° knee bend (straight-down legs).</summary>
    static double IdleHipDrop
    {
        get
        {
            double l1 = ThighLen, l2 = ShinLen, k = 15 * Deg;
            var d = Math.Sqrt(l1 * l1 + l2 * l2 + 2 * l1 * l2 * Math.Cos(k));
            return d - SomaSkeleton.Offset(LLeg).Y; // hips joint above the hip joint
        }
    }
}
