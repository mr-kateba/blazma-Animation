using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;

namespace Blazma.Physics;

/// <summary>
/// Damped angular spring, mass-independent: α = k·e − c·ω, where e is the rotation vector from the bone's current
/// model-space orientation to its target. Natural frequency √k rad/s, damping ratio c / (2√k).
/// </summary>
public sealed record SpringSettings
{
    /// <summary>k (1/s²). Default 150 ≈ 1.9 Hz.</summary>
    public double Stiffness { get; init; } = 150;

    /// <summary>c (1/s). Default 12 → damping ratio ≈ 0.49.</summary>
    public double Damping { get; init; } = 12;

    /// <summary>Largest lag behind the primary motion (degrees).</summary>
    public double MaxAngleDegrees { get; init; } = 30;

    public double DampingRatio => Damping / (2 * Math.Sqrt(Stiffness));

    public const double MaxStiffness = 10_000;
    public const double MaxDamping = 1_000;
}

/// <summary>Simulated local rotations: <c>Rotations[b][i]</c> for bone <c>Bones[b]</c> at frame <c>StartFrame + i</c>.</summary>
public sealed record SecondaryResult(long StartFrame, IReadOnlyList<int> Bones, Quat[][] Rotations, double MaxLagDegrees)
{
    /// <summary>Simulated model-space rotations, indexed like <see cref="Rotations"/>: what playback gives once the local rotations are keyed.</summary>
    public Quat[][] World { get; init; } = Array.Empty<Quat[]>();
}

/// <summary>
/// Follow-through / overlapping action: each chosen bone's model-space orientation chases the orientation the
/// primary motion gives it under its <em>playback</em> parent: its parent's simulated orientation, or, for a parent that
/// is not simulated (a gap in the chain, or a locked bone left out by the caller), that parent's primary local rotation
/// composed onto its own playback parent — so lag accumulates down a chain and the baked local rotations reproduce the
/// simulated world orientations exactly when played back. Integration: fixed sub-steps of at most 1/240 s whatever the
/// clip's frame rate (primary targets interpolated between frames), implicit damping
/// <c>ω ← (ω + h·k·e) / (1 + h·c)</c> then <c>q ← exp(h·ω)·q</c>, so it is stable for any fps and converges to the
/// primary pose once it stops moving. The lag is clamped to <see cref="SpringSettings.MaxAngleDegrees"/>.
/// Bones are simulated parent before child; the state starts on the primary motion at the first frame, moving with it
/// (ω = the primary angular velocity there: central difference, or the second-order forward difference at frame 0), so starting mid-motion does
/// not kink.
/// </summary>
public static class SecondaryMotion
{
    public const double MaxSubstep = 1.0 / 240;

    /// <summary>Integration sub-steps per frame at a frame rate.</summary>
    public static int Substeps(double framesPerSecond) => Math.Max(1, (int)Math.Ceiling(1.0 / framesPerSecond / MaxSubstep - 1e-9));

    public static SecondaryResult Simulate(Rig rig, Clip clip, IEnumerable<int> boneIndices, SpringSettings? settings, long startFrame, long endFrame)
    {
        var st = settings ?? new SpringSettings();
        var k = Math.Clamp(st.Stiffness, 1e-3, SpringSettings.MaxStiffness);
        var c = Math.Clamp(st.Damping, 0, SpringSettings.MaxDamping);
        var maxAngle = Math.Clamp(st.MaxAngleDegrees, 0, 180) * Math.PI / 180;
        var ev = new RigEvaluator(rig);
        var bones = boneIndices.Distinct().Where(i => i >= 0 && i < rig.Bones.Count).OrderBy(i => i).ToArray();
        var n = (int)Math.Max(0, endFrame - startFrame + 1);
        var output = bones.Select(_ => new Quat[n]).ToArray();
        var world = bones.Select(_ => new Quat[n]).ToArray();
        if (n == 0 || bones.Length == 0) return new SecondaryResult(startFrame, bones, output, 0) { World = world };

        var count = rig.Bones.Count;
        var slot = new int[count];
        Array.Fill(slot, -1);
        for (var b = 0; b < bones.Length; b++) slot[bones[b]] = b;
        // Bones below a simulated ancestor follow it at playback: they need their primary local rotations.
        var underSim = new bool[count];
        for (var i = 0; i < count; i++)
        {
            var p = ev.ParentOf(i);
            underSim[i] = p >= 0 && (slot[p] >= 0 || underSim[p]);
        }

        // Primary motion, sampled per frame: model rotations of every bone, local rotations where needed.
        var locals = new Quat[n][];
        var model = new Quat[n][];
        for (var i = 0; i < n; i++)
        {
            var pose = ClipEvaluator.Evaluate(clip, rig, Flicks.FromFrame(startFrame + i, clip.Fps));
            var m = ev.Model(pose);
            var l = new Quat[count];
            for (var j = 0; j < count; j++) if (slot[j] >= 0 || underSim[j]) l[j] = pose.LocalOf(rig.Bones[j]).Rotation;
            locals[i] = l;
            model[i] = m.Select(t => t.Rotation).ToArray();
        }

        var dt = 1.0 / clip.Fps.FramesPerSecond;
        var q = new Quat[bones.Length];
        var w = new Vec3[bones.Length];
        Quat[]? before = startFrame > 0 && n >= 2
            ? ev.Model(ClipEvaluator.Evaluate(clip, rig, Flicks.FromFrame(startFrame - 1, clip.Fps))).Select(t => t.Rotation).ToArray()
            : null;
        for (var b = 0; b < bones.Length; b++)
        {
            q[b] = model[0][bones[b]];
            var j = bones[b];
            var q0Inv = model[0][j].Inverse();
            w[b] = n < 2 ? Vec3.Zero
                : before is not null ? RotationVector((model[1][j] * before[j].Inverse()).Normalized()) / (2 * dt)
                : n >= 3 ? (RotationVector((model[1][j] * q0Inv).Normalized()) * 4 - RotationVector((model[2][j] * q0Inv).Normalized())) / (2 * dt)
                : RotationVector((model[1][j] * q0Inv).Normalized()) / dt;
            if (!Finite(w[b])) w[b] = Vec3.Zero;
            output[b][0] = locals[0][bones[b]];
            world[b][0] = q[b];
        }

        var steps = Substeps(clip.Fps.FramesPerSecond);
        var h = dt / steps;
        double maxLag = 0;
        for (var i = 1; i < n; i++)
        {
            for (var s = 1; s <= steps; s++)
            {
                var a = (double)s / steps;
                for (var b = 0; b < bones.Length; b++)
                {
                    var bone = bones[b];
                    var target = (Playback(ev.ParentOf(bone), i, a) * Quat.Slerp(locals[i - 1][bone], locals[i][bone], a)).Normalized();
                    var e = RotationVector((target * q[b].Inverse()).Normalized());
                    w[b] = (w[b] + e * (h * k)) / (1 + h * c);
                    q[b] = (Exp(w[b] * h) * q[b]).Normalized();
                    if (!double.IsFinite(q[b].X + q[b].Y + q[b].Z + q[b].W) || !Finite(w[b])) { q[b] = target; w[b] = Vec3.Zero; }
                    var lag = RotationVector((target * q[b].Inverse()).Normalized());
                    var angle = lag.Length();
                    if (angle > maxAngle)
                    {
                        var axis = lag / angle;
                        q[b] = (Exp(axis * -maxAngle) * target).Normalized();
                        var away = Vec3.Dot(w[b], axis);
                        if (away < 0) w[b] -= axis * away;
                        angle = maxAngle;
                    }
                    if (s == steps) maxLag = Math.Max(maxLag, angle);
                }
            }
            for (var b = 0; b < bones.Length; b++)
            {
                output[b][i] = (Playback(ev.ParentOf(bones[b]), i, 1).Inverse() * q[b]).Normalized();
                world[b][i] = q[b];
            }
        }
        return new SecondaryResult(startFrame, bones, output, maxLag * 180 / Math.PI) { World = world };

        // World rotation bone j will have at playback (between frames i−1 and i, fraction a): simulated if it is
        // simulated, else its primary local rotation under its own playback parent, else (no simulated ancestor) primary.
        Quat Playback(int j, int frame, double a)
        {
            if (j < 0) return Quat.Identity;
            if (slot[j] >= 0) return q[slot[j]];
            if (!underSim[j]) return a >= 1 ? model[frame][j] : Quat.Slerp(model[frame - 1][j], model[frame][j], a);
            var local = a >= 1 ? locals[frame][j] : Quat.Slerp(locals[frame - 1][j], locals[frame][j], a);
            return (Playback(ev.ParentOf(j), frame, a) * local).Normalized();
        }
    }

    static bool Finite(Vec3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

    /// <summary>Axis × angle of a unit quaternion, shortest way (angle ≤ π).</summary>
    public static Vec3 RotationVector(Quat q)
    {
        if (q.W < 0) q = new Quat(-q.X, -q.Y, -q.Z, -q.W);
        var v = new Vec3(q.X, q.Y, q.Z);
        var s = v.Length();
        if (s < 1e-15) return v * 2;
        return v * (2 * Math.Atan2(s, q.W) / s);
    }

    public static Quat Exp(Vec3 r)
    {
        var angle = r.Length();
        return angle < 1e-15 ? Quat.Identity : Quat.FromAxisAngle(r, angle);
    }
}
