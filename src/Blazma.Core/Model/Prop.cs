using System.Text.Json.Serialization;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;

namespace Blazma.Core.Model;

/// <summary>
/// An animatable object in the scene (sword, ball, box…), docs/DATA_FORMAT.md §9. Its current (working) transform is
/// <see cref="Transform"/> in world (model) space when it is free, or <see cref="PropAttachment.Offset"/> relative to the
/// bone when it is attached. Clip tracks with target kind <see cref="TargetKind.Prop"/> animate that same value.
/// </summary>
public sealed class Prop
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public string Name { get; set; } = "Prop";
    public PropShape Shape { get; set; } = PropShape.Box;

    /// <summary>Size in metres: box = full extents; sphere = diameter in X; cylinder = diameter (X) × length (Y).</summary>
    public Vec3 Size { get; set; } = new(0.1, 0.1, 0.1);

    /// <summary>For <see cref="PropShape.Mesh"/>: the embedded .glb (see <see cref="Project.Assets"/>).</summary>
    public ObjectId Asset { get; set; }

    /// <summary>World transform when the prop is not attached.</summary>
    public Transform Transform { get; set; } = Transform.Identity;

    public PropAttachment? Attachment { get; set; }

    /// <summary>The value prop tracks animate: the offset when attached, else the world transform.</summary>
    [JsonIgnore]
    public Transform Local => Attachment?.Offset ?? Transform;
}

public sealed class PropAttachment
{
    public ObjectId Character { get; set; }
    public ObjectId Bone { get; set; }

    /// <summary>Transform relative to the bone (bone-local space).</summary>
    public Transform Offset { get; set; } = Transform.Identity;
}

[JsonConverter(typeof(JsonStringEnumConverter<PropShape>))]
public enum PropShape { Box, Sphere, Cylinder, Mesh }
