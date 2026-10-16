using Blazma.Core.Mathematics;
using Blazma.Core.Model;

namespace Blazma.Rigging;

/// <summary>
/// Forward kinematics: local bone transforms → model-space transforms.
/// Arrays are indexed like <see cref="Rig.Bones"/> (parent before child), so one pass suffices.
/// </summary>
public sealed class RigEvaluator
{
    readonly Rig _rig;
    readonly int[] _parent;
    readonly Dictionary<string, int> _index;

    public RigEvaluator(Rig rig)
    {
        _rig = rig;
        _parent = new int[rig.Bones.Count];
        _index = new Dictionary<string, int>(rig.Bones.Count);
        for (var i = 0; i < rig.Bones.Count; i++)
        {
            var b = rig.Bones[i];
            _index[b.Id.ToString()] = i;
            _parent[i] = b.Parent.IsEmpty ? -1 : _index.TryGetValue(b.Parent.ToString(), out var p) ? p
                : throw new InvalidOperationException($"Bone '{b.Name}' appears before its parent.");
        }
    }

    public Rig Rig => _rig;
    public int Count => _rig.Bones.Count;
    public int ParentOf(int i) => _parent[i];
    public int IndexOf(Core.Ids.ObjectId id) => _index.TryGetValue(id.ToString(), out var i) ? i : -1;

    public Transform[] RestModel()
    {
        var m = new Transform[Count];
        for (var i = 0; i < Count; i++)
            m[i] = _parent[i] < 0 ? _rig.Bones[i].Rest : m[_parent[i]] * _rig.Bones[i].Rest;
        return m;
    }

    public Transform[] Locals(Pose pose)
    {
        var l = new Transform[Count];
        for (var i = 0; i < Count; i++) l[i] = pose.LocalOf(_rig.Bones[i]);
        return l;
    }

    public Transform[] Model(Transform[] locals)
    {
        var m = new Transform[Count];
        for (var i = 0; i < Count; i++)
            m[i] = _parent[i] < 0 ? locals[i] : m[_parent[i]] * locals[i];
        return m;
    }

    public Transform[] Model(Pose pose) => Model(Locals(pose));

    /// <summary>Local transform that yields model-space <paramref name="model"/> under the given parent.</summary>
    public static Transform LocalFromModel(Transform parentModel, Transform model)
    {
        var inv = parentModel.Rotation.Inverse();
        var s = parentModel.Scale;
        var dp = inv.Rotate(model.Translation - parentModel.Translation);
        return new Transform(
            new Vec3(dp.X / s.X, dp.Y / s.Y, dp.Z / s.Z),
            (inv * model.Rotation).Normalized(),
            new Vec3(model.Scale.X / s.X, model.Scale.Y / s.Y, model.Scale.Z / s.Z));
    }

    /// <summary>True if <paramref name="ancestor"/> is <paramref name="bone"/> or one of its parents.</summary>
    public bool IsAncestorOrSelf(int ancestor, int bone)
    {
        for (var i = bone; i >= 0; i = _parent[i])
            if (i == ancestor) return true;
        return false;
    }
}
