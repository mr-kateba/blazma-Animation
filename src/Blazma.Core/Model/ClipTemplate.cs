using Blazma.Core.Ids;

namespace Blazma.Core.Model;

/// <summary>
/// A user clip template in the project library (docs/DATA_FORMAT.md §10): a copy of a clip together with a snapshot
/// of the rig it was made for, so it can be retargeted onto any humanoid-mapped character later even if the
/// original character or rig is gone. Built-in templates are not stored; they are generated on demand.
/// </summary>
public sealed class ClipTemplate
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public string Name { get; set; } = "Template";
    public string Description { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The clip (its Skeleton is <see cref="Rig"/>'s id).</summary>
    public Clip Clip { get; set; } = new();

    /// <summary>Snapshot of the source rig (bones, rest pose, humanoid map, IK).</summary>
    public Rig Rig { get; set; } = new();
}
