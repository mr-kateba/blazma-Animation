using Blazma.Core.Mathematics;

namespace Blazma.AI;

/// <summary>
/// Single actions for the prompt planner (D45), all with the feet solved by IK on fixed footprints (no foot sliding):
/// <list type="bullet">
/// <item><b>Look back (startled)</b>: a quick flinch, then the hips, spine, chest and head turn to look back over the left shoulder
/// (≈140° for the head) and come back to face forward; the feet stay planted.</item>
/// <item><b>Turn around</b>: three steps (left 0→90°, right 0→180°, left 90→180°) about the body centre; the stance foot never moves,
/// the swing foot lifts on an arc, the pelvis shifts over the stance foot and the head leads the turn.</item>
/// <item><b>Fall / die</b>: the knees buckle with the feet planted until the body sits back to the ground, then the torso falls back
/// and the legs extend along the ground; it ends lying on the back, still. "Die" starts with a hit reaction.</item>
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
    static void Legs(Frame fr, Vec3 pelvis, Quat hips, Vec3 leftAnkle, Quat leftFoot, Vec3 rightAnkle, Quat rightFoot)
    {
        fr.R[Hips] = hips;
        foreach (var (leg, shin, foot, ankle, footW) in new[] { (LLeg, LShin, LFoot, leftAnkle, leftFoot), (RLeg, RShin, RFoot, rightAnkle, rightFoot) })
        {
            var hip = pelvis + hips.Rotate(SomaSkeleton.Offset(leg));
            SolveLeg(fr, hips, hip, ankle, footW.Rotate(Vec3.UnitZ), footW, leg, shin, foot);
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

    static void Fall(Frame fr, double t, double u, bool die, double vary)
    {
        u = Math.Clamp(u, 0, 1);
        var hit = die ? Bump(Math.Clamp(u / 0.2, 0, 1), 0.3) : 0;
        var y = Keys(u, (0, StandY), (0.12, StandY - 0.03), (0.48, 0.38), (0.66, 0.2), (0.84, LyingPelvis.Y));
        var z = Keys(u, (0, 0), (0.12, 0.02), (0.48, -0.26), (0.66, -0.36), (0.84, LyingPelvis.Z));
        var pitch = Keys(u, (0, 2), (0.12, die ? -4 : 8), (0.48, 22), (0.66, -25), (0.84, -86));
        var pelvis = new Vec3(0, y, z);
        var hips = Rx(pitch);

        // Feet: planted until the body sits on the ground, then the legs extend along the ground (heels down, toes up and out).
        var extend = Smooth((u - 0.5) / 0.34);
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
            return planted + (lying - planted) * extend + new Vec3(0, 0.03 * Math.Sin(Math.PI * extend), 0);
        }
        Quat Foot(double side) => Quat.Slerp(Ry(side * 6), Ry(side * 14) * Rx(-78), extend).Normalized();
        Legs(fr, pelvis, hips, Ankle(1), Foot(1), Ankle(-1), Foot(-1));

        // Trunk: braces forward while sinking, whips back on impact; a hit jolts the chest back first (die).
        var lie = Smooth((u - 0.6) / 0.25);
        fr.R[Spine1] = Rx(8 * Smooth(u / 0.4) * (1 - lie) - 12 * hit);
        fr.R[Spine2] = Rx(6 * Smooth(u / 0.4) * (1 - lie) - 8 * hit);
        fr.R[Chest] = Rx(-10 * hit + 4 * lie);
        fr.R[Neck1] = Rx(14 * Smooth(u / 0.5) * (1 - lie) - 10 * hit - 6 * lie);
        fr.R[Head] = Ry((die ? 28 : 12) * lie * vary) * Rx(8 * Smooth(u / 0.5) * (1 - lie) - 12 * hit);
        // Arms: thrown up and forward as the body drops, then flop out onto the ground.
        var reachOut = Bump(Math.Clamp((u - 0.15) / 0.6, 0, 1), 0.5);
        ArmsDown(fr, -45 * reachOut, -35 * reachOut, 20 + 30 * reachOut, 20 + 25 * reachOut);
        fr.R[LArm] = Rz(48 * lie) * fr.R[LArm];
        fr.R[RArm] = Rz(-40 * lie) * fr.R[RArm];
    }
}
