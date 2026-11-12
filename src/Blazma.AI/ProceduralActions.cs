using Blazma.Core.Mathematics;

namespace Blazma.AI;

/// <summary>
/// Single actions for the prompt planner (D45), all with the feet solved by IK on fixed footprints (no foot sliding):
/// <list type="bullet">
/// <item><b>Look back (startled)</b>: a quick flinch, then the hips, spine, chest and head turn to look back over the left shoulder
/// (≈140° for the head) and come back to face forward; the feet stay planted.</item>
/// <item><b>Turn around</b>: three steps (left 0→90°, right 0→180°, left 90→180°) about the body centre; the stance foot never moves,
/// the swing foot lifts on an arc, the pelvis shifts over the stance foot and the head leads the turn.</item>
/// <item><b>Fall / die</b>: the knees buckle with the feet planted and the body rotates back about them, dropping faster and faster
/// (no easing before the impact, arms held forward) until the hips hit the ground, with a small damped bounce; then the torso
/// topples back about the hips, accelerating until the back hits the ground (bounce, then still) while the legs slide out with the
/// heels lifted. It ends lying on the back. "Die" starts with a hit reaction.</item>
/// <item><b>Jump</b>: physically timed: counter-movement crouch, push-off that ends with the take-off velocity v₀ = √(2gh) (cubic
/// Hermite, so the hips accelerate into the take-off), a ballistic flight (hips on the parabola with g = 9.81, legs tucked), a landing
/// that arrives with −v₀ and decelerates over the knee compression, then recovery. Feet roll about the ball at take-off and landing.</item>
/// </list>
/// </summary>
public sealed partial class ProceduralMotionProvider
{
    static double BaseX => Math.Abs(SomaSkeleton.Offset(LLeg).X) + 0.01;
    static double StandY => AnkleHeight + IdleHipDrop;

    /// <summary>Piecewise smoothstep through (u, value) keys.</summary>
    static double Keys(double u, params (double U, double V)[] keys)
    {
        if (u <= keys[0].U) return keys[0].V;
        for (var i = 1; i < keys.Length; i++)
            if (u <= keys[i].U) return keys[i - 1].V + (keys[i].V - keys[i - 1].V) * Smooth((u - keys[i - 1].U) / (keys[i].U - keys[i - 1].U));
        return keys[^1].V;
    }

    /// <summary>Both legs by IK: ankles at fixed world targets, knees towards the feet's forward direction.</summary>
    static void Legs(Frame fr, Vec3 pelvis, Quat hips, Vec3 leftAnkle, Quat leftFoot, Vec3 rightAnkle, Quat rightFoot, Vec3? pole = null)
    {
        fr.R[Hips] = hips;
        foreach (var (leg, shin, foot, ankle, footW) in new[] { (LLeg, LShin, LFoot, leftAnkle, leftFoot), (RLeg, RShin, RFoot, rightAnkle, rightFoot) })
        {
            var hip = pelvis + hips.Rotate(SomaSkeleton.Offset(leg));
            SolveLeg(fr, hips, hip, ankle, pole ?? footW.Rotate(Vec3.UnitZ), footW, leg, shin, foot);
        }
        fr.Root = pelvis;
        fr.Absolute = true;
    }

    static Vec3 StanceAnkle(double side, double yawDeg) => Ry(yawDeg).Rotate(new Vec3(side * BaseX, AnkleHeight, 0));

    static void LookBack(Frame fr, double t, double u, double vary, double phase0)
    {
        var b = Math.Sin(2 * Math.PI * (0.25 * t + phase0));
        var startle = Bump(Math.Clamp(u / 0.3, 0, 1), 0.3);                                     // flinch: crouch, shoulders up
        var look = Smooth((u - 0.1) / 0.22) * (1 - Smooth((u - 0.68) / 0.27)) * vary;          // turn and hold, then face forward
        var pelvis = new Vec3(0.01 * look, StandY - 0.035 * startle + 0.0015 * b, -0.01 * startle);
        var hips = Ry(22 * look) * Rx(2 + 4 * startle);
        Legs(fr, pelvis, hips, StanceAnkle(1, 0), Ry(6), StanceAnkle(-1, 0), Ry(-6));
        fr.R[Spine1] = Ry(22 * look) * Rx(-1 + 4 * startle);
        fr.R[Spine2] = Ry(30 * look);
        fr.R[Chest] = Ry(30 * look) * Rx(-2 * b - 6 * startle);
        StabiliseHead(fr, Ry(140 * look) * Rx(-6 * startle + 3 * look));
        // Arms: pulled in and up with the flinch, the far arm follows the twist a little.
        ArmsDown(fr, -25 * startle + 3 * b, -25 * startle - 10 * look + 3 * b, 12 + 55 * startle, 12 + 55 * startle);
        fr.R[LArm] = Rz(15 * startle) * fr.R[LArm];
        fr.R[RArm] = Rz(-15 * startle) * fr.R[RArm];
    }

    /// <summary>Steps of a 180° turn: (foot side, from yaw, to yaw, start u, end u).</summary>
    static readonly (double Side, double From, double To, double U0, double U1)[] TurnSteps =
    {
        (1, 0, 90, 0.06, 0.36), (-1, 0, 180, 0.36, 0.66), (1, 90, 180, 0.66, 0.92),
    };

    static void TurnAround(Frame fr, double t, double u, double vary, double phase0)
    {
        var b = Math.Sin(2 * Math.PI * (0.25 * t + phase0));
        double FootYaw(double side, out double lift, out double phase)
        {
            lift = 0; phase = -1;
            var y = 0.0;
            foreach (var (sd, from, to, u0, u1) in TurnSteps)
            {
                if (sd != side) continue;
                if (u >= u1) { y = to; continue; }
                if (u <= u0) return from;
                phase = (u - u0) / (u1 - u0);
                lift = 0.07 * vary * Bump(phase, 0.45);
                return from + (to - from) * Smooth((phase - 0.15) / 0.7); // lift first, turn in the air, land, then load
            }
            return y;
        }
        var yawL = FootYaw(1, out var liftL, out var phaseL);
        var yawR = FootYaw(-1, out var liftR, out var phaseR);
        // Weight over the stance foot while the other one swings.
        var shift = Vec3.Zero;
        if (phaseL >= 0) shift = StanceAnkle(-1, yawR) * (0.35 * Math.Sin(Math.PI * phaseL));
        if (phaseR >= 0) shift = StanceAnkle(1, yawL) * (0.35 * Math.Sin(Math.PI * phaseR));
        var pelvisYaw = Keys(u, (0.04, 0), (0.94, 180));
        var pelvis = new Vec3(shift.X, StandY - 0.02 * Math.Sin(Math.PI * Math.Clamp((u - 0.05) / 0.9, 0, 1)) + 0.0015 * b, shift.Z);
        var hips = Ry(pelvisYaw) * Rx(2);
        Legs(fr, pelvis, hips,
            StanceAnkle(1, yawL) + new Vec3(0, liftL, 0), Ry(yawL + 6),
            StanceAnkle(-1, yawR) + new Vec3(0, liftR, 0), Ry(yawR - 6));
        fr.R[Spine1] = Rx(-1);
        fr.R[Chest] = Rx(-2 * b);
        var lead = 25 * Math.Sin(Math.PI * Math.Clamp((u - 0.02) / 0.9, 0, 1));
        StabiliseHead(fr, Ry(pelvisYaw + lead) * Rx(2));
        var swing = 12 * Math.Sin(Math.PI * Math.Clamp(u / 0.92, 0, 1));
        ArmsDown(fr, -swing, swing, 15, 15);
    }

    /// <summary>Lying pose of the fall: pelvis position and rotation.</summary>
    static readonly Vec3 LyingPelvis = new(0, 0.12, -0.42);
    static Quat LyingHips => Rx(-86);
    const double SitY = 0.115, SitZ = -0.33;

    /// <summary>Accelerating from rest into an impact: <paramref name="from"/> → <paramref name="to"/> with progress s² on [u0, u1].</summary>
    static double Drop(double u, double u0, double u1, double from, double to)
    {
        var s = Math.Clamp((u - u0) / (u1 - u0), 0, 1);
        return from + (to - from) * s * s;
    }

    /// <summary>A small damped rebound after an impact at <paramref name="u0"/> (≥ 0, gone after <paramref name="width"/>).</summary>
    static double Rebound(double u, double u0, double width)
    {
        if (u <= u0) return 0;
        var x = (u - u0) / width;
        return x >= 1 ? 0 : Math.Exp(-3 * x) * Math.Sin(Math.PI * x);
    }

    static void Fall(Frame fr, double t, double u, bool die, double vary)
    {
        u = Math.Clamp(u, 0, 1);
        var hit = die ? Bump(Math.Clamp(u / 0.2, 0, 1), 0.3) : 0;
        const double sit = 0.45, topple = 0.52, back = 0.8;
        // Hips: knees give, then the body drops and rotates back about the planted feet, faster and faster, into the sitting impact;
        // after a rebound the torso topples back about the hips into the lying impact.
        var sink = Smooth(u / 0.12);
        var y = u < 0.12 ? StandY - 0.03 * sink : Drop(u, 0.12, sit, StandY - 0.03, SitY);
        y += 0.018 * Rebound(u, sit, 0.12) + Drop(u, topple, back, 0, LyingPelvis.Y - SitY) + 0.008 * Rebound(u, back, 0.1);
        var z = u < 0.12 ? 0.02 * sink : Drop(u, 0.12, sit, 0.02, SitZ);
        z += Drop(u, topple, back, 0, LyingPelvis.Z - SitZ);
        var pitch = u < 0.12 ? 2 + (die ? -6 : 6) * sink : Drop(u, 0.12, sit, die ? -4 : 8, 22);
        pitch += Drop(u, topple, back, 0, -86 - 22) + 3 * Rebound(u, back, 0.1) - 4 * Rebound(u, sit, 0.12);
        var pelvis = new Vec3(0, y, z);
        var hips = Rx(pitch);

        // Feet: planted until the body sits, then the legs slide out with the heels lifted (no dragging), heels down, toes up and out.
        var extend = Smooth((u - topple) / (back + 0.06 - topple));
        Vec3 Ankle(double side)
        {
            var planted = StanceAnkle(side, 0);
            var footRot = Ry(side * 14) * Rx(-78);
            var ankleY = -footRot.Rotate(HeelLocal).Y + 0.004;
            var hip = LyingPelvis + LyingHips.Rotate(SomaSkeleton.Offset(side > 0 ? LLeg : RLeg));
            var reach = (ThighLen + ShinLen) * 0.97;
            var dy = hip.Y - ankleY;
            var horizontal = Math.Sqrt(Math.Max(0, reach * reach - dy * dy - 0.06 * 0.06));
            var lying = new Vec3(hip.X + side * 0.06, ankleY, hip.Z + horizontal);
            return planted + (lying - planted) * extend + new Vec3(0, 0.08 * Math.Pow(Math.Sin(Math.PI * extend), 0.6), 0);
        }
        Quat Foot(double side) => Quat.Slerp(Ry(side * 6), Ry(side * 14) * Rx(-78), extend).Normalized();
        Legs(fr, pelvis, hips, Ankle(1), Foot(1), Ankle(-1), Foot(-1), new Vec3(0, 1, 0.6)); // knees up and forward (a deep squat)

        // Trunk: braces forward while dropping, whips back as it topples; a hit jolts the chest back first (die).
        var lie = Math.Clamp((u - topple) / (back - topple), 0, 1);
        lie *= lie;
        var brace = Smooth(u / 0.4) * (1 - lie);
        fr.R[Spine1] = Rx(8 * brace - 12 * hit);
        fr.R[Spine2] = Rx(6 * brace - 8 * hit);
        fr.R[Chest] = Rx(-10 * hit + 4 * lie);
        fr.R[Neck1] = Rx(14 * brace - 10 * hit - 6 * lie + 6 * Rebound(u, back, 0.1));
        fr.R[Head] = Ry((die ? 28 : 12) * Smooth((u - back) / 0.12) * vary) * Rx(8 * brace - 12 * hit);
        // Arms: thrown forward as the knees give and held out in front (balance) until the torso is well on its way back, then
        // flopping out onto the ground.
        var forward = Smooth((u - 0.1) / 0.15) * (1 - Smooth((u - topple - 0.6 * (back - topple)) / (0.4 * (back - topple) + 0.04)));
        ArmsDown(fr, -78 * forward, -70 * forward, 20 + 30 * forward, 20 + 25 * forward);
        var flop = Smooth((u - topple) / (back - topple));
        fr.R[LArm] = Rz(48 * flop) * fr.R[LArm];
        fr.R[RArm] = Rz(-40 * flop) * fr.R[RArm];
    }

    /// <summary>Jump phases (s): counter-movement crouch, push-off, landing compression, recovery (flight from the apex height).</summary>
    const double JumpCrouch = 0.35, JumpPush = 0.22, JumpLand = 0.28, JumpRecover = 0.45, JumpDip = 0.2, JumpLandDip = 0.16, JumpExtend = 0.06;

    static double Hermite(double u, double p0, double m0, double p1, double m1)
    {
        double u2 = u * u, u3 = u2 * u;
        return p0 * (2 * u3 - 3 * u2 + 1) + m0 * (u3 - 2 * u2 + u) + p1 * (-2 * u3 + 3 * u2) + m1 * (u3 - u2);
    }

    static void Jump(Frame fr, double t, double duration, double vary)
    {
        const double g = 9.81;
        var height = 0.3 * vary;
        var flight = 2 * Math.Sqrt(2 * height / g);
        var fixedPart = JumpCrouch + JumpPush + JumpLand + JumpRecover;
        var k = Math.Min(1, duration / (fixedPart + flight + 0.1));
        // A short clip scales every phase (and the flight with it: h = g·T²/8).
        double tc = JumpCrouch * k, tp = JumpPush * k, tl = JumpLand * k, tr = JumpRecover * k;
        flight *= k;
        var v0 = g * flight / 2;
        var lead = 0.35 * Math.Max(0, duration - (tc + tp + flight + tl + tr));
        double t0 = lead, t1 = t0 + tc, t2 = t1 + tp, t3 = t2 + flight, t4 = t3 + tl, t5 = t4 + tr;

        double z, pitch = 0, arms;
        if (t < t0) { z = 0; arms = 0; }
        else if (t < t1) { var s = Smooth((t - t0) / tc); z = -JumpDip * s; arms = 50 * s; }                                   // crouch, arms back
        else if (t < t2) { var s = (t - t1) / tp; z = Hermite(s, -JumpDip, 0, JumpExtend, v0 * tp); arms = 50 - 200 * Smooth(s); pitch = -35 * Smooth((s - 0.4) / 0.6); } // push-off
        else if (t < t3) { var s = t - t2; z = JumpExtend + v0 * s - 0.5 * g * s * s; arms = -150 + 60 * (s / flight); pitch = -35 + 27 * Smooth(s / flight); } // flight
        else if (t < t4) { var s = (t - t3) / tl; z = Hermite(s, JumpExtend, -v0 * tl, -JumpLandDip, 0); arms = -90 + 120 * Smooth(s); pitch = -8 * (1 - Smooth(s / 0.35)); } // landing
        else if (t < t5) { var s = Smooth((t - t4) / tr); z = -JumpLandDip * (1 - s); arms = 30 * (1 - s); }                   // recover
        else { z = 0; arms = 0; }
        var crouch = Math.Max(0, -z) / JumpDip;
        // In the air the feet leave with the body (minus a small tuck of the knees).
        var air = t >= t2 && t < t3 ? z - JumpExtend - 0.1 * Math.Sin(Math.PI * (t - t2) / flight) : 0;
        var pelvis = new Vec3(0, StandY + z, -0.06 * crouch);
        var hips = Rx(2 + 22 * crouch);
        Vec3 AnkleAt(double side)
        {
            var yaw = Ry(side * 6);
            var flat = StanceAnkle(side, 0) + new Vec3(0, Math.Max(0, air), 0);
            return pitch < 0 ? flat + yaw.Rotate(BallLocal) - (yaw * Rx(-pitch)).Rotate(BallLocal) : flat;
        }
        Legs(fr, pelvis, hips, AnkleAt(1), Ry(6) * Rx(-pitch), AnkleAt(-1), Ry(-6) * Rx(-pitch));
        fr.R[LToe] = fr.R[RToe] = Rx(Math.Min(0, pitch)); // toes stay flat on the ground while the heels rise
        fr.R[Spine1] = Rx(10 * crouch);
        fr.R[Spine2] = Rx(6 * crouch);
        StabiliseHead(fr, Rx(4));
        ArmsDown(fr, arms, arms, 20);
    }
}
