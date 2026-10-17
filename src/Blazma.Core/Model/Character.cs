using Blazma.Core.Ids;
using Blazma.Core.Mathematics;

namespace Blazma.Core.Model;

/// <summary>A rig placed in the scene with its current, editable pose.</summary>
public sealed class Character
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public string Name { get; set; } = "Character";
    public ObjectId Rig { get; set; }
    public Pose Pose { get; set; } = new();

    /// <summary>Hurtboxes and hit-reaction rules (schema v5); null when the character has none.</summary>
    public CharacterCombat? Combat { get; set; }
}

/// <summary>
/// Pose state of one rig. Bones absent from <see cref="Bones"/> are at rest.
/// Bone transforms are local (relative to parent). IK chains whose target is pinned are
/// re-solved after every edit, and their solved rotations are stored here too, so the pose
/// can be evaluated with plain FK.
/// </summary>
public sealed class Pose
{
    /// <summary>Key: bone id. Local transform.</summary>
    public Dictionary<string, Transform> Bones { get; set; } = new();

    /// <summary>Key: control id. Model-space transform and IK state.</summary>
    public Dictionary<string, ControlState> Controls { get; set; } = new();

    public Pose Clone() => new()
    {
        Bones = new Dictionary<string, Transform>(Bones),
        Controls = Controls.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()),
    };

    public Transform LocalOf(Bone bone) => Bones.TryGetValue(bone.Id.ToString(), out var t) ? t : bone.Rest;

    public bool ContentEquals(Pose other, double eps = 1e-12)
    {
        if (Bones.Count != other.Bones.Count || Controls.Count != other.Controls.Count) return false;
        foreach (var (k, t) in Bones)
            if (!other.Bones.TryGetValue(k, out var o) || !Same(t, o, eps)) return false;
        foreach (var (k, c) in Controls)
            if (!other.Controls.TryGetValue(k, out var o) || c.Pinned != o.Pinned || !Same(c.Transform, o.Transform, eps)) return false;
        return true;
    }

    static bool Same(Transform a, Transform b, double eps) =>
        a.Translation.ApproxEquals(b.Translation, eps) && a.Scale.ApproxEquals(b.Scale, eps) && a.Rotation.ApproxEquals(b.Rotation, eps * 10);
}

public sealed class ControlState
{
    /// <summary>Model-space transform of the control.</summary>
    public Transform Transform { get; set; } = Transform.Identity;

    /// <summary>When pinned, the chain follows this control even when parents move.</summary>
    public bool Pinned { get; set; }

    public ControlState Clone() => new() { Transform = Transform, Pinned = Pinned };
}

/// <summary>A named pose saved in the project's pose library.</summary>
public sealed class PoseAsset
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public string Name { get; set; } = "Pose";
    public ObjectId Rig { get; set; }
    public Pose Pose { get; set; } = new();
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
