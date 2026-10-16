using System.Text.Json.Serialization;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;

namespace Blazma.Core.Model;

/// <summary>
/// A skeleton definition (docs/DATA_FORMAT.md §6). Bones are stored parent-before-child.
/// All references are ids, never names, so renaming a bone breaks nothing.
/// Model space: metres, +Y up, +Z character forward, +X character left.
/// </summary>
public sealed class Rig
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public string Name { get; set; } = "Rig";
    public RigSource Source { get; set; } = new();
    public List<Bone> Bones { get; set; } = new();
    public List<RigControl> Controls { get; set; } = new();
    public List<IkChain> IkChains { get; set; } = new();

    /// <summary>User bone → Blazma standard humanoid bone name (see Blazma.Rigging.HumanoidStandard).</summary>
    public List<BoneMapping> HumanoidMap { get; set; } = new();

    public Bone? FindBone(ObjectId id) => Bones.Find(b => b.Id == id);
    public Bone GetBone(ObjectId id) => FindBone(id) ?? throw new KeyNotFoundException($"Bone '{id}' does not exist in rig '{Name}'.");
    public Bone? FindBoneByName(string name) => Bones.Find(b => b.Name == name);
    public RigControl? FindControl(ObjectId id) => Controls.Find(c => c.Id == id);
    public RigControl GetControl(ObjectId id) => FindControl(id) ?? throw new KeyNotFoundException($"Control '{id}' does not exist in rig '{Name}'.");
    public int IndexOf(ObjectId boneId) => Bones.FindIndex(b => b.Id == boneId);

    /// <summary>Standard name for a bone, or null if unmapped.</summary>
    public string? StandardNameOf(ObjectId boneId) => HumanoidMap.Find(m => m.Bone == boneId)?.Standard;

    public Bone? FindByStandardName(string standard)
    {
        var m = HumanoidMap.Find(x => x.Standard == standard);
        return m is null ? null : FindBone(m.Bone);
    }
}

public sealed class RigSource
{
    public RigSourceKind Kind { get; set; } = RigSourceKind.Builtin;

    /// <summary>Original file name/path the rig was imported from (informational).</summary>
    public string? AssetPath { get; set; }

    /// <summary>Embedded model file (see <see cref="Project.Assets"/>); empty for built-in rigs.</summary>
    public ObjectId Asset { get; set; }

    /// <summary>Bone id → glTF node index in the embedded file, used to drive the rendered skeleton.</summary>
    public Dictionary<string, int> NodeIndex { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<RigSourceKind>))]
public enum RigSourceKind { Builtin, Imported }

public sealed class Bone
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public string Name { get; set; } = "";

    /// <summary>Parent bone id; empty for a root bone.</summary>
    public ObjectId Parent { get; set; }

    /// <summary>Rest transform relative to the parent bone (or model space for roots).</summary>
    public Transform Rest { get; set; } = Transform.Identity;

    /// <summary>Approximate segment mass in kg (used by physics assistance; 0 = unknown).</summary>
    public double Mass { get; set; }

    public JointLimits? Limits { get; set; }
}

/// <summary>
/// Limits on a bone's local rotation relative to rest. If <see cref="Hinge"/> is set it is used
/// (swing-twist, robust for knees/elbows beyond 90°); otherwise per-axis intrinsic Euler XYZ limits.
/// </summary>
public sealed class JointLimits
{
    public Vec3 MinDegrees { get; set; }
    public Vec3 MaxDegrees { get; set; }
    public HingeLimit? Hinge { get; set; }
}

/// <summary>Rotation about <see cref="Axis"/> (bone-local, relative to rest) limited to [Min, Max] degrees; any other rotation limited to a cone of <see cref="MaxSwingDegrees"/>.</summary>
public sealed class HingeLimit
{
    public Vec3 Axis { get; set; } = Vec3.UnitX;
    public double MinDegrees { get; set; }
    public double MaxDegrees { get; set; }
    public double MaxSwingDegrees { get; set; }
}

public sealed class RigControl
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public string Name { get; set; } = "";
    public ControlKind Kind { get; set; } = ControlKind.IkTarget;

    /// <summary>Default model-space transform (where the control sits in the rest pose).</summary>
    public Transform Rest { get; set; } = Transform.Identity;
}

[JsonConverter(typeof(JsonStringEnumConverter<ControlKind>))]
public enum ControlKind { IkTarget, Pole }

/// <summary>Analytic two-bone chain: Upper → Lower → End (e.g. thigh → calf → foot).</summary>
public sealed class IkChain
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public string Name { get; set; } = "";
    public ObjectId Upper { get; set; }
    public ObjectId Lower { get; set; }

    /// <summary>The effector bone; its origin is placed on the target and it takes the target's rotation.</summary>
    public ObjectId End { get; set; }

    public ObjectId Target { get; set; }

    /// <summary>Optional pole control that sets the bend direction; empty = keep current bend plane.</summary>
    public ObjectId Pole { get; set; }

    /// <summary>Model-space bend direction used when no pole is set and the chain is straight.</summary>
    public Vec3 DefaultBend { get; set; } = Vec3.UnitZ;
}

public sealed class BoneMapping
{
    public ObjectId Bone { get; set; }
    public string Standard { get; set; } = "";
}
