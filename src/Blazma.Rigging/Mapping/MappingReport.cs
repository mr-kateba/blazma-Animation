using Blazma.Core.Ids;

namespace Blazma.Rigging.Mapping;

/// <summary>How an assignment was decided.</summary>
public enum MappingConfidence
{
    /// <summary>The bone name is the Blazma standard name (ignoring case and separators).</summary>
    Exact,

    /// <summary>The (normalized) bone name matches a known synonym from another rig convention.</summary>
    Synonym,

    /// <summary>Decided from the skeleton hierarchy or rest positions rather than the name.</summary>
    Hierarchy,

    /// <summary>Assigned by the user via <see cref="HumanoidMapper.Reassign"/>.</summary>
    Manual,
}

public sealed record BoneAssignment(ObjectId BoneId, string BoneName, string Standard, MappingConfidence Confidence, string Reason);

/// <summary>
/// A problem or notice found while mapping. Codes: "hierarchy_violation", "spine_path_not_found",
/// "spine_extra_bones_unmapped", "faces_negative_z", "side_mismatch".
/// </summary>
public sealed record MappingIssue(string Code, string Message, string? BoneName = null);

public sealed class MappingReport
{
    public List<BoneAssignment> Assignments { get; init; } = new();

    /// <summary>Required standard bones (<see cref="HumanoidStandard.Required"/>) without an assignment.</summary>
    public List<string> MissingRequired { get; init; } = new();

    /// <summary>Non-required standard bones without an assignment.</summary>
    public List<string> MissingOptional { get; init; } = new();

    public List<MappingIssue> Issues { get; init; } = new();

    /// <summary>True when every required bone is mapped.</summary>
    public bool IsUsable => MissingRequired.Count == 0;

    /// <summary>Assigned standard bones divided by all standard bones, 0..1.</summary>
    public double Coverage => HumanoidStandard.Bones.Count == 0 ? 0 : (double)Assignments.Count / HumanoidStandard.Bones.Count;
}
