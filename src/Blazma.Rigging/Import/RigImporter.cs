using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Rigging.Mapping;

namespace Blazma.Rigging.Import;

public sealed class RigImportResult
{
    public required Rig Rig { get; init; }
    public required MappingReport Mapping { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Bone id → glTF node name before uniquifying ("node_&lt;index&gt;" when the node had no name).</summary>
    public required IReadOnlyDictionary<ObjectId, string> OriginalNodeNames { get; init; }

    /// <summary>Bone id → glTF node index (to match the rendered Skeleton3D).</summary>
    public required IReadOnlyDictionary<ObjectId, int> NodeIndexOf { get; init; }

    /// <summary>Index of the glTF skin that was imported.</summary>
    public int SkinIndex { get; init; }
}

/// <summary>Builds a <see cref="Rig"/> from the skeleton of a glTF/GLB file and auto-maps it to the Blazma humanoid.</summary>
public static class RigImporter
{
    /// <summary>
    /// Imports the skin with the most joints. Bones are the skin's joints plus any non-joint nodes between
    /// joints (so the hierarchy is closed), ordered parent-before-child and otherwise by node index.
    /// Each bone's Rest is its node's local TRS, except for root bones, whose Rest is the product of all
    /// non-joint ancestor transforms and their own local transform so that model space stays correct.
    /// Controls and IK chains are not created.
    /// </summary>
    public static RigImportResult Import(string path, string? relativeAssetPath = null)
    {
        var doc = GltfReader.Read(path);
        var warnings = new List<string>(doc.Warnings);

        if (doc.Skins.Count == 0)
            throw new GltfImportException("no_skin", $"'{Path.GetFileName(path)}' contains no skin, so there is no skeleton to import.");

        int skinIndex = 0;
        for (int i = 1; i < doc.Skins.Count; i++)
            if (doc.Skins[i].Joints.Length > doc.Skins[skinIndex].Joints.Length) skinIndex = i;
        if (doc.Skins.Count > 1)
        {
            var others = Enumerable.Range(0, doc.Skins.Count).Where(i => i != skinIndex)
                .Select(i => $"skin {i} ('{doc.Skins[i].Name ?? "unnamed"}', {doc.Skins[i].Joints.Length} joints)");
            warnings.Add($"File has {doc.Skins.Count} skins; imported skin {skinIndex} ('{doc.Skins[skinIndex].Name ?? "unnamed"}', {doc.Skins[skinIndex].Joints.Length} joints). Ignored: {string.Join(", ", others)}.");
        }
        var skin = doc.Skins[skinIndex];
        var nodes = doc.Nodes;

        // Close the hierarchy: add non-joint nodes lying between two joints.
        var jointSet = new HashSet<int>(skin.Joints);
        var boneSet = new HashSet<int>(jointSet);
        int intermediates = 0;
        foreach (int j in jointSet)
        {
            var chain = new List<int>();
            for (int? p = nodes[j].Parent; p is not null; p = nodes[p.Value].Parent)
            {
                if (jointSet.Contains(p.Value))
                {
                    foreach (int c in chain) if (boneSet.Add(c)) intermediates++;
                    break;
                }
                chain.Add(p.Value);
            }
        }
        if (intermediates > 0)
            warnings.Add($"{intermediates} non-joint node(s) between joints were added as bones.");

        // Parent-before-child, stable by node index.
        var order = new List<int>();
        var emitted = new HashSet<int>();
        void Emit(int n)
        {
            if (emitted.Contains(n)) return;
            if (nodes[n].Parent is int p && boneSet.Contains(p)) Emit(p);
            if (emitted.Add(n)) order.Add(n);
        }
        foreach (int n in boneSet.OrderBy(x => x)) Emit(n);

        var rig = new Rig
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Source = new RigSource { Kind = RigSourceKind.Imported, AssetPath = relativeAssetPath ?? Path.GetFileName(path) },
        };
        var boneOf = new Dictionary<int, Bone>();
        var originals = new Dictionary<ObjectId, string>();
        var nodeIndexOf = new Dictionary<ObjectId, int>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        int rootCount = 0;

        foreach (int n in order)
        {
            var node = nodes[n];
            string original = string.IsNullOrEmpty(node.Name) ? $"node_{n}" : node.Name;
            string unique = original;
            for (int k = 2; !usedNames.Add(unique); k++) unique = $"{original}_{k}";
            if (unique != original) warnings.Add($"Duplicate bone name '{original}' (node {n}) renamed to '{unique}'.");

            var bone = new Bone { Name = unique, Rest = node.Local };
            if (node.Parent is int p && boneSet.Contains(p))
            {
                bone.Parent = boneOf[p].Id;
            }
            else
            {
                rootCount++;
                if (node.Parent is int top) bone.Rest = WorldOf(nodes, top) * node.Local;
            }
            boneOf[n] = bone;
            originals[bone.Id] = original;
            nodeIndexOf[bone.Id] = n;
            rig.Bones.Add(bone);
        }
        if (rootCount > 1)
            warnings.Add($"The skin has {rootCount} root bones (no single common root joint).");

        var report = HumanoidMapper.AutoMap(rig);
        HumanoidMapper.Apply(rig, report);

        return new RigImportResult
        {
            Rig = rig,
            Mapping = report,
            Warnings = warnings,
            OriginalNodeNames = originals,
            NodeIndexOf = nodeIndexOf,
            SkinIndex = skinIndex,
        };
    }

    /// <summary>Model-space transform of a node (product of local transforms from the scene root down).</summary>
    static Transform WorldOf(IReadOnlyList<GltfNode> nodes, int index)
    {
        var chain = new List<int>();
        for (int? p = index; p is not null; p = nodes[p.Value].Parent) chain.Add(p.Value);
        var t = Transform.Identity;
        for (int i = chain.Count - 1; i >= 0; i--) t = t * nodes[chain[i]].Local;
        return t;
    }
}
