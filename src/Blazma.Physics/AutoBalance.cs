using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Rigging;

namespace Blazma.Physics;

public sealed record AutoBalanceOptions
{
    /// <summary>The corrected COM must be at least this far inside the support polygon (m).</summary>
    public double Margin { get; init; } = 0.02;

    /// <summary>Largest allowed horizontal hip shift (m).</summary>
    public double MaxShift { get; init; } = 0.3;

    public int MaxIterations { get; init; } = 40;

    /// <summary>Stop when the COM is this close (m) to its target.</summary>
    public double Tolerance { get; init; } = 1e-6;
}

/// <summary>Result of balancing one pose.</summary>
public sealed record BalanceCorrection
{
    public long Frame { get; init; }

    /// <summary>False when the pose was already balanced, airborne, or could not be corrected (see <see cref="Note"/>).</summary>
    public bool Changed { get; init; }
    public double MarginBefore { get; init; }
    public double MarginAfter { get; init; }
    public Vec3 ComBefore { get; init; }
    public Vec3 ComAfter { get; init; }

    /// <summary>Model-space horizontal translation applied to the hips.</summary>
    public Vec3 HipShift { get; init; }

    /// <summary>Largest movement of a supporting foot/toe bone (m); ≈ 0 when the legs could reach.</summary>
    public double FootDrift { get; init; }

    /// <summary>Where the COM was moved to (on the ground).</summary>
    public Vec3 Target { get; init; }
    public string? Note { get; init; }

    /// <summary>The corrected pose (supporting feet pinned on their original transforms), null if unchanged.</summary>
    public Pose? Pose { get; init; }

    /// <summary>Bones whose local transforms must be keyed to reproduce <see cref="Pose"/> with FK: the pelvis (translation) first, then leg bones (rotation).</summary>
    public IReadOnlyList<int> ChangedBones { get; init; } = Array.Empty<int>();
}

/// <summary>
/// Moves the projected centre of mass inside the support polygon by translating the hips (pelvis) horizontally
/// while supporting feet stay planted (their legs are re-solved with the two-bone IK through <see cref="PoseSolver"/>).
/// The target is the closest point of the polygon shrunk by the margin (minimal change); when the polygon is too
/// small to shrink, its centroid (or the closest point of a point/segment support). Because planted legs
/// do not follow the hips, the COM moves less than the hips: the shift is found by a damped secant iteration
/// on <c>COM(shift) = target</c>. Deterministic; no spine lean.
/// </summary>
public static class AutoBalance
{
    public static BalanceCorrection CorrectPose(BalanceAnalyzer an, Pose pose, PoseBalance balance, AutoBalanceOptions? options = null)
    {
        var o = options ?? new AutoBalanceOptions();
        var rig = an.Rig;
        var ev = an.Evaluator;
        var model0 = ev.Model(pose);
        var com0 = an.Mass.CenterOfMass(model0);
        var hull = balance.Polygon;
        var c0 = Vec2.Of(com0);
        var before = Geometry2D.SignedDistance(hull, c0);
        var result = new BalanceCorrection { Frame = balance.Frame, MarginBefore = before, MarginAfter = before, ComBefore = com0, ComAfter = com0 };

        if (hull.Count == 0) return result with { Note = "airborne" };
        if (before >= o.Margin - 1e-9) return result with { Note = "balanced" };
        if (an.PelvisBone < 0) return result with { Note = "no_pelvis" };

        var inset = Geometry2D.Inset(hull, o.Margin);
        var target = inset.Count >= 3 ? Geometry2D.ClosestPoint(inset, c0)
            : hull.Count >= 3 ? Geometry2D.Centroid(hull)
            : Geometry2D.ClosestPoint(hull, c0);

        // Pin every supporting foot on its current transform so the legs re-solve instead of sliding.
        var work = pose.Clone();
        var legBones = new List<int>();
        var feetToKeep = new List<int>();
        var unpinnable = false;
        foreach (var fi in balance.ContactFeet)
        {
            var foot = an.Feet.Feet[fi];
            feetToKeep.Add(foot.FootBone);
            if (foot.ToeBone >= 0) feetToKeep.Add(foot.ToeBone);
            if (foot.Chain is not { } chain) { unpinnable = true; continue; }
            var state = PoseSolver.StateOf(rig, work, chain.Target);
            if (!state.Pinned)
            {
                work.Controls[chain.Target.ToString()] = new ControlState { Transform = model0[foot.FootBone] with { Scale = Vec3.One }, Pinned = true };
                legBones.AddRange(new[] { rig.IndexOf(chain.Upper), rig.IndexOf(chain.Lower), rig.IndexOf(chain.End) });
            }
        }

        var p = an.PelvisBone;
        var parent = ev.ParentOf(p);
        var parentModel = parent < 0 ? Transform.Identity : model0[parent];
        var baseLocal = work.LocalOf(rig.Bones[p]);

        Pose Apply(Vec2 shift)
        {
            var w = work.Clone();
            var d = parentModel.Rotation.Inverse().Rotate(new Vec3(shift.X, 0, shift.Z));
            var s = parentModel.Scale;
            w.Bones[rig.Bones[p].Id.ToString()] = baseLocal with
            {
                Translation = baseLocal.Translation + new Vec3(d.X / s.X, d.Y / s.Y, d.Z / s.Z),
            };
            PoseSolver.Update(rig, w);
            return w;
        }

        var shift = new Vec2(0, 0);
        var gain = 1.0;
        var solved = Apply(shift);
        var com = Vec2.Of(an.Mass.CenterOfMass(solved));
        for (var it = 0; it < o.MaxIterations; it++)
        {
            var err = target - com;
            if (err.Length() <= o.Tolerance) break;
            var next = shift + err * (1 / gain);
            if (next.Length() > o.MaxShift) next = next * (o.MaxShift / next.Length());
            var step = next - shift;
            if (step.Length() < 1e-12) break;
            var candidate = Apply(next);
            var newCom = Vec2.Of(an.Mass.CenterOfMass(candidate));
            var moved = Vec2.Dot(newCom - com, step) / Vec2.Dot(step, step);
            gain = Math.Clamp(moved, 0.1, 2.0);
            shift = next;
            com = newCom;
            solved = candidate;
        }

        var model1 = ev.Model(solved);
        var com1 = an.Mass.CenterOfMass(model1);
        var drift = feetToKeep.Count == 0 ? 0 : feetToKeep.Max(b => (model1[b].Translation - model0[b].Translation).Length());
        var after = Geometry2D.SignedDistance(hull, Vec2.Of(com1));
        string? note = null;
        if (unpinnable) note = "foot_without_ik";
        else if (drift > 1e-4) note = "leg_reach";
        else if (shift.Length() >= o.MaxShift - 1e-9) note = "max_shift";
        var changed = new List<int> { p };
        changed.AddRange(legBones.Where(i => i >= 0));
        return result with
        {
            Changed = true, MarginAfter = after, ComAfter = com1, HipShift = new Vec3(shift.X, 0, shift.Z),
            FootDrift = drift, Target = target.At(an.Ground), Note = note, Pose = solved, ChangedBones = changed,
        };
    }
}
