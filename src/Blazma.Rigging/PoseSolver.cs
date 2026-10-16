using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;

namespace Blazma.Rigging;

/// <summary>
/// Keeps IK chains and their controls consistent with a pose.
/// <list type="bullet">
/// <item>Pinned chain: the end bone stays on its target control, whatever the parents do.</item>
/// <item>Unpinned chain: the control follows the end bone (so it is ready when the user grabs it).</item>
/// </list>
/// Requires Upper → Lower → End to be direct parent/child bones (validated by <see cref="RigSetup"/>).
/// </summary>
public static class PoseSolver
{
    /// <summary>Solve every pinned chain (plus <paramref name="alsoSolve"/>), then sync unpinned controls.</summary>
    public static void Update(Rig rig, Pose pose, ObjectId? alsoSolve = null)
    {
        var ev = new RigEvaluator(rig);
        foreach (var chain in rig.IkChains)
        {
            var state = StateOf(rig, pose, chain.Target);
            if (state.Pinned || chain.Id == alsoSolve) SolveChain(ev, pose, chain);
        }
        SyncUnpinnedControls(ev, pose);
    }

    public static ControlState StateOf(Rig rig, Pose pose, ObjectId controlId)
    {
        if (pose.Controls.TryGetValue(controlId.ToString(), out var s)) return s;
        var control = rig.GetControl(controlId);
        return new ControlState { Transform = control.Rest };
    }

    /// <summary>True if <paramref name="boneId"/> is currently driven by a pinned IK chain.</summary>
    public static IkChain? DrivingChain(Rig rig, Pose pose, ObjectId boneId)
    {
        foreach (var chain in rig.IkChains)
            if ((chain.Upper == boneId || chain.Lower == boneId || chain.End == boneId) && StateOf(rig, pose, chain.Target).Pinned)
                return chain;
        return null;
    }

    public static IkChain? ChainForControl(Rig rig, ObjectId controlId) =>
        rig.IkChains.Find(c => c.Target == controlId || c.Pole == controlId);

    static void SolveChain(RigEvaluator ev, Pose pose, IkChain chain)
    {
        var rig = ev.Rig;
        int iu = ev.IndexOf(chain.Upper), il = ev.IndexOf(chain.Lower), ie = ev.IndexOf(chain.End);
        if (iu < 0 || il < 0 || ie < 0 || ev.ParentOf(il) != iu || ev.ParentOf(ie) != il) return;

        var locals = ev.Locals(pose);
        var model = ev.Model(locals);
        var target = StateOf(rig, pose, chain.Target).Transform;
        // Poles live in the space of the rig's top bone, so they travel with the character;
        // IK targets live in model space, so pinned feet stay planted when the body moves.
        Vec3? pole = chain.Pole.IsEmpty ? null
            : model[0].TransformPoint(StateOf(rig, pose, chain.Pole).Transform.Translation);

        var r = TwoBoneIk.Solve(model[iu].Translation, model[il].Translation, model[ie].Translation,
            target.Translation, pole, chain.DefaultBend);

        var parentOfUpper = ev.ParentOf(iu) < 0 ? Transform.Identity : model[ev.ParentOf(iu)];
        var upperRot = (r.UpperDelta * model[iu].Rotation).Normalized();
        var lowerRot = (r.LowerDelta * r.UpperDelta * model[il].Rotation).Normalized();

        locals[iu] = locals[iu] with { Rotation = (parentOfUpper.Rotation.Inverse() * upperRot).Normalized() };
        locals[il] = locals[il] with { Rotation = (upperRot.Inverse() * lowerRot).Normalized() };
        locals[ie] = locals[ie] with { Rotation = (lowerRot.Inverse() * target.Rotation).Normalized() };

        pose.Bones[rig.Bones[iu].Id.ToString()] = locals[iu];
        pose.Bones[rig.Bones[il].Id.ToString()] = locals[il];
        pose.Bones[rig.Bones[ie].Id.ToString()] = locals[ie];
    }

    static void SyncUnpinnedControls(RigEvaluator ev, Pose pose)
    {
        var rig = ev.Rig;
        var model = ev.Model(pose);
        foreach (var chain in rig.IkChains)
        {
            var key = chain.Target.ToString();
            var state = StateOf(rig, pose, chain.Target);
            if (state.Pinned) continue;
            var ie = ev.IndexOf(chain.End);
            if (ie < 0) continue;
            pose.Controls[key] = new ControlState { Transform = model[ie] with { Scale = Vec3.One }, Pinned = false };
        }
    }
}
