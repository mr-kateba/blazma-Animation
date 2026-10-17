using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Rigging;

namespace Blazma.Physics;

/// <summary>Per-frame model-space paths for drawing (index i = frame StartFrame + i).</summary>
public sealed record TrajectorySet(long StartFrame, Vec3[] Com, Vec3[] Hips, Vec3[] LeftFoot, Vec3[] RightFoot);

public static class Trajectories
{
    /// <summary>
    /// Centre of mass, hips (pelvis bone, or the first bone) and foot (ankle bone) paths over [start, end] of a clip.
    /// A missing foot gives an empty array.
    /// </summary>
    public static TrajectorySet Compute(BalanceAnalyzer an, Clip clip, long? startFrame = null, long? endFrame = null)
    {
        var last = BalanceAnalyzer.LastFrame(clip);
        var start = Math.Clamp(startFrame ?? 0, 0, last);
        var end = Math.Clamp(endFrame ?? last, start, last);
        var s = an.Sample(clip, start, end);
        var offset = (int)(start - s.StartFrame);
        var n = (int)(end - start + 1);
        var hips = an.PelvisBone >= 0 ? an.PelvisBone : 0;
        int FootIndex(Side side) => an.Feet.Feet.FirstOrDefault(f => f.Side == side)?.FootBone ?? -1;
        var lf = FootIndex(Side.Left);
        var rf = FootIndex(Side.Right);
        var com = new Vec3[n];
        var hip = new Vec3[an.Rig.Bones.Count == 0 ? 0 : n];
        var left = new Vec3[lf < 0 ? 0 : n];
        var right = new Vec3[rf < 0 ? 0 : n];
        for (var i = 0; i < n; i++)
        {
            var m = s.Model[i + offset];
            com[i] = s.Com[i + offset];
            if (hip.Length > 0) hip[i] = m[hips].Translation;
            if (lf >= 0) left[i] = m[lf].Translation;
            if (rf >= 0) right[i] = m[rf].Translation;
        }
        return new TrajectorySet(start, com, hip, left, right);
    }
}
