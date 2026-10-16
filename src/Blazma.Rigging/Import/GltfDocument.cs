using Blazma.Core.Mathematics;

namespace Blazma.Rigging.Import;

/// <summary>The skeleton-relevant subset of a glTF 2.0 file.</summary>
public sealed class GltfDocument
{
    public string Generator { get; init; } = "";
    public List<GltfNode> Nodes { get; init; } = new();
    public List<GltfSkin> Skins { get; init; } = new();
    public int MeshCount { get; init; }
    public int? DefaultScene { get; init; }

    /// <summary>Root node indices of every scene, in scene order.</summary>
    public List<int[]> SceneRoots { get; init; } = new();

    public List<string> Warnings { get; init; } = new();
}

public sealed record GltfNode
{
    public int Index { get; init; }
    public string? Name { get; init; }
    public int[] Children { get; init; } = Array.Empty<int>();

    /// <summary>Local transform (from TRS properties, or decomposed from "matrix"; shear is dropped).</summary>
    public Transform Local { get; init; } = Transform.Identity;

    /// <summary>Computed from the children lists; null for scene roots.</summary>
    public int? Parent { get; set; }

    public int? Mesh { get; init; }
    public int? Skin { get; init; }
}

public sealed record GltfSkin
{
    public string? Name { get; init; }
    public int[] Joints { get; init; } = Array.Empty<int>();
    public int? Skeleton { get; init; }

    /// <summary>One column-major 16-element matrix per joint, or null if absent or unreadable.</summary>
    public double[][]? InverseBindMatrices { get; init; }
}
