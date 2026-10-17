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
public sealed record SecondaryResult(long StartFrame, IReadOnlyList<int> Bones, Quat[][] Rotations, double MaxLagDegrees);

/// <summary>
/// Follow-through / overlapping action: each chosen bone's model-space orientation chases the orientation the
/// primary motion gives it (its parent's <em>simulated</em> orientation × its primary local rotation), so lag
/// accumulates down a chain. Integration: fixed sub-steps of at most 1/240 s whatever the clip's frame rate
/// (primary targets interpolated between frames), implicit damping
/// <c>ω ← (ω + h·k·e) / (1 + h·c)</c> then <c>q ← exp(h·ω)·q</c>, so it is stable for any fps and converges to the
/// primary pose once it stops moving. The lag is clamped to <see cref="SpringSettings.MaxAngleDegrees"/>.
/// Bones are simulated parent before child; the state starts at rest relative to the primary motion at the first frame.
/// </summary>
public static class SecondaryMotion
{
    public const double MaxSubstep = 1.0 / 240;

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
        if (n == 0 || bones.Length == 0) return new SecondaryResult(startFrame, bones, output, 0);

        var slot = new Dictionary<int, int>();
        for (var b = 0; b < bones.Length; b++) slot[bones[b]] = b;

        // Primary motion, sampled per frame.
        var locals = new Quat[n][];
        var model = new Quat[n][];
        for (var i = 0; i < n; i++)
        {
            var pose = ClipEvaluator.Evaluate(clip, rig, Flicks.FromFrame(startFrame + i, clip.Fps));
            var m = ev.Model(pose);
            locals[i] = bones.Select(b => pose.LocalOf(rig.Bones[b]).Rotation).ToArray();
            model[i] = m.Select(t => t.Rotation).ToArray();
        }

        var q = new Quat[bones.Length];
        var w = new Vec3[bones.Length];
        for (var b = 0; b < bones.Length; b++)
        {
            q[b] = model[0][bones[b]];
            w[b] = Vec3.Zero;
            output[b][0] = locals[0][b];
        }

        var dt = 1.0 / clip.Fps.FramesPerSecond;
        var steps = Math.Max(1, (int)Math.Ceiling(dt / MaxSubstep - 1e-9));
        var h = dt / steps;
        double maxLag = 0;
        for (var i = 1; i < n; i++)
        {
            for (var s = 1; s <= steps; s++)
            {
                var a = (double)s / steps;
                for (var b = 0; b < bones.Length; b++)
                {
                    var target = (ParentRotation(b, i, a) * Quat.Slerp(locals[i - 1][b], locals[i][b], a)).Normalized();
                    var e = RotationVector((target * q[b].Inverse()).Normalized());
                    w[b] = (w[b] + e * (h * k)) / (1 + h * c);
                    q[b] = (Exp(w[b] * h) * q[b]).Normalized();
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
                output[b][i] = (ParentRotation(b, i, 1).Inverse() * q[b]).Normalized();
        }
        return new SecondaryResult(startFrame, bones, output, maxLag * 180 / Math.PI);

        Quat ParentRotation(int b, int frame, double a)
        {
            var parent = ev.ParentOf(bones[b]);
            if (parent < 0) return Quat.Identity;
            if (slot.TryGetValue(parent, out var ps)) return q[ps];
            return a >= 1 ? model[frame][parent] : Quat.Slerp(model[frame - 1][parent], model[frame][parent], a);
        }
    }

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
