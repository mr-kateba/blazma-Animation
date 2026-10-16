using System.Text.Json.Nodes;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Rigging;
using Blazma.Rigging.Import;
using Blazma.Rigging.Mapping;

namespace Blazma.Commands;

/// <summary>Phase 2 commands: character import (glTF/GLB) and manual bone mapping.</summary>
public static class RigCommands
{
    public const long MaxImportBytes = 256L * 1024 * 1024;

    public static void Register(CommandRegistry r)
    {
        r.Register(new("rig.import", "Import a rigged character from a .glb/.gltf file. The file is embedded in the project. Returns characterId, rigId and the bone-mapping report.", CommandEffect.External,
            S.Of(S.Str("path", "Absolute path of the .glb or .gltf file.", true), S.Str("name", "Character name (default: file name).", false)),
            a => new ImportRig(S.Text(a, "path"), S.OptName(a, "name"))));

        r.Register(new("rig.map_bone", "Correct the humanoid mapping: give a bone a Blazma standard name (e.g. thigh_l), or omit 'standard' to unmap it. IK chains are rebuilt.", CommandEffect.Mutating,
            S.Of(S.Str("rigId", "Rig id.", true), S.Str("bone", "Bone id or name.", true), S.Str("standard", "Standard bone name; omit to unmap.", false)),
            a => new MapBone(S.Id(a, "rigId"), S.Text(a, "bone"), (string?)a["standard"])));

        r.Register(new("rig.mapping_report", "Bone-mapping report for a rig: assignments, missing bones and issues.", CommandEffect.ReadOnly,
            S.Of(S.Str("rigId", "Rig id.", true)),
            a => new MappingReportCommand(S.Id(a, "rigId"))));
    }

    /// <summary>The report that matches the rig's current map (checks re-run).</summary>
    public static MappingReport CurrentReport(Rig rig)
    {
        var current = new MappingReport
        {
            Assignments = rig.HumanoidMap
                .Where(m => rig.FindBone(m.Bone) is not null)
                .Select(m => new BoneAssignment(m.Bone, rig.GetBone(m.Bone).Name, m.Standard, MappingConfidence.Manual, "current"))
                .ToList(),
        };
        // Re-assigning a bone to its own name re-runs the validation over the whole map.
        var first = current.Assignments.FirstOrDefault();
        return first is null ? HumanoidMapper.Reassign(rig, current, rig.Bones[0].Id, null)
            : HumanoidMapper.Reassign(rig, current, first.BoneId, first.Standard);
    }

    public static JsonObject ReportToJson(MappingReport r) => new()
    {
        ["usable"] = r.IsUsable,
        ["coverage"] = Math.Round(r.Coverage, 3),
        ["missingRequired"] = new JsonArray(r.MissingRequired.Select(x => (JsonNode?)x).ToArray()),
        ["missingOptional"] = new JsonArray(r.MissingOptional.Select(x => (JsonNode?)x).ToArray()),
        ["assignments"] = new JsonArray(r.Assignments.Select(a => (JsonNode?)new JsonObject
        {
            ["bone"] = a.BoneName, ["boneId"] = a.BoneId.ToString(), ["standard"] = a.Standard,
            ["confidence"] = a.Confidence.ToString(), ["reason"] = a.Reason,
        }).ToArray()),
        ["issues"] = new JsonArray(r.Issues.Select(i => (JsonNode?)new JsonObject
        {
            ["code"] = i.Code, ["message"] = i.Message, ["bone"] = i.BoneName,
        }).ToArray()),
    };

    /// <summary>Rebuild the standard leg/arm chains after the mapping changed; returns warnings.</summary>
    internal static IReadOnlyList<string> RebuildStandardIk(Project p, Rig rig)
    {
        var standard = new[] { "leg_l", "leg_r", "arm_l", "arm_r" };
        var removed = new HashSet<ObjectId>();
        foreach (var chain in rig.IkChains.Where(c => standard.Contains(c.Name)).ToList())
        {
            removed.Add(chain.Target);
            if (!chain.Pole.IsEmpty) removed.Add(chain.Pole);
            rig.IkChains.Remove(chain);
        }
        rig.Controls.RemoveAll(c => removed.Contains(c.Id));
        var warnings = RigSetup.AddStandardIk(rig);
        foreach (var character in p.Characters.Where(c => c.Rig == rig.Id))
        {
            foreach (var id in removed) character.Pose.Controls.Remove(id.ToString());
            PoseSolver.Update(rig, character.Pose);
        }
        return warnings;
    }
}

public sealed class ImportRig(string path, string? name) : ICommand
{
    // Imported once; Redo re-adds the very same objects.
    Rig? _rig;
    Character? _character;
    ProjectAsset? _asset;
    JsonObject? _data;

    public string Id => "rig.import";
    public CommandEffect Effect => CommandEffect.External;
    public string Label => "Import character";

    public CommandOutcome Execute(Project p)
    {
        if (_rig is null) Load();
        p.Assets.Add(_asset!);
        p.Rigs.Add(_rig!);
        p.Characters.Add(_character!);
        return new(new[] { new Change(Change.RigChanged, _rig!.Id.ToString()), new Change(Change.CharacterAdded, _character!.Id.ToString()) },
            _data!.DeepClone());
    }

    void Load()
    {
        var full = System.IO.Path.GetFullPath(path);
        var ext = System.IO.Path.GetExtension(full).ToLowerInvariant();
        if (ext is not (".glb" or ".gltf"))
            throw new CommandException("unsupported_format", "Only .glb and .gltf files can be imported for now.");
        if (!File.Exists(full)) throw new CommandException("not_found", $"File not found: {full}");
        if (new FileInfo(full).Length > RigCommands.MaxImportBytes)
            throw new CommandException("too_large", "The file is larger than 256 MB.");
        if (ext == ".gltf")
            throw new CommandException("unsupported_format",
                "Embedding .gltf files with external buffers is not supported yet; export as .glb (binary glTF).");

        RigImportResult result;
        try { result = RigImporter.Import(full, System.IO.Path.GetFileName(full)); }
        catch (GltfImportException e) { throw new CommandException("import_" + e.Code, e.Message); }

        var rig = result.Rig;
        HumanoidMapper.Apply(rig, result.Mapping);
        var warnings = new List<string>(result.Warnings);
        warnings.AddRange(RigSetup.AddStandardIk(rig));

        _asset = new ProjectAsset
        {
            FileName = System.IO.Path.GetFileName(full),
            MediaType = "model/gltf-binary",
            Data = File.ReadAllBytes(full),
        };
        rig.Source.Asset = _asset.Id;
        rig.Source.AssetPath = full;
        rig.Source.NodeIndex = result.NodeIndexOf.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
        _rig = rig;
        _character = MannequinFactory.CreateCharacter(rig, name ?? rig.Name);

        _data = new JsonObject
        {
            ["characterId"] = _character.Id.ToString(),
            ["rigId"] = rig.Id.ToString(),
            ["boneCount"] = rig.Bones.Count,
            ["ikChains"] = new JsonArray(rig.IkChains.Select(c => (JsonNode?)c.Name).ToArray()),
            ["mapping"] = RigCommands.ReportToJson(result.Mapping),
            ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode?)w).ToArray()),
        };
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        p.Characters.RemoveAll(c => c.Id == _character!.Id);
        p.Rigs.RemoveAll(r => r.Id == _rig!.Id);
        p.Assets.RemoveAll(a => a.Id == _asset!.Id);
        return new[] { new Change(Change.CharacterRemoved, _character!.Id.ToString()), new Change(Change.RigChanged, _rig!.Id.ToString()) };
    }
}

/// <summary>Manual mapping correction. Snapshots the rig's map, controls, chains and affected poses for undo.</summary>
public sealed class MapBone(ObjectId rigId, string bone, string? standard) : ICommand
{
    List<BoneMapping>? _map;
    List<RigControl>? _controls;
    List<IkChain>? _chains;
    Dictionary<ObjectId, Pose>? _poses;

    public string Id => "rig.map_bone";
    public CommandEffect Effect => CommandEffect.Mutating;
    public string Label => "Map bone";

    public CommandOutcome Execute(Project p)
    {
        var rig = p.FindRig(rigId) ?? throw new CommandException("not_found", $"Rig '{rigId}' does not exist.");
        if (rig.Source.Kind == RigSourceKind.Builtin)
            throw new CommandException("builtin_rig", "The built-in mannequin's mapping cannot be changed.");
        var b = PoseCommands.ResolveBone(rig, bone);
        var std = string.IsNullOrWhiteSpace(standard) ? null : standard.Trim();
        if (std is not null && !HumanoidStandard.IsStandard(std))
            throw new CommandException("invalid_args", $"'{std}' is not a Blazma standard bone name.");

        var report = HumanoidMapper.Reassign(rig, RigCommands.CurrentReport(rig), b.Id, std);

        _map = rig.HumanoidMap.Select(m => new BoneMapping { Bone = m.Bone, Standard = m.Standard }).ToList();
        _controls = rig.Controls.ToList();
        _chains = rig.IkChains.ToList();
        _poses = p.Characters.Where(c => c.Rig == rig.Id).ToDictionary(c => c.Id, c => c.Pose.Clone());

        HumanoidMapper.Apply(rig, report);
        var warnings = RigCommands.RebuildStandardIk(p, rig);

        var data = RigCommands.ReportToJson(report);
        data["warnings"] = new JsonArray(warnings.Select(w => (JsonNode?)w).ToArray());
        var changes = new List<Change> { new(Change.RigChanged, rig.Id.ToString()) };
        changes.AddRange(_poses.Keys.Select(id => new Change(Change.PoseChanged, id.ToString())));
        return new(changes, data);
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        var rig = p.GetRig(rigId);
        rig.HumanoidMap = _map!;
        rig.Controls = _controls!;
        rig.IkChains = _chains!;
        foreach (var (id, pose) in _poses!) p.GetCharacter(id).Pose = pose.Clone();
        var changes = new List<Change> { new(Change.RigChanged, rig.Id.ToString()) };
        changes.AddRange(_poses.Keys.Select(id => new Change(Change.PoseChanged, id.ToString())));
        return changes;
    }
}

public sealed class MappingReportCommand(ObjectId rigId) : ICommand
{
    public string Id => "rig.mapping_report";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Mapping report";

    public CommandOutcome Execute(Project p)
    {
        var rig = p.FindRig(rigId) ?? throw new CommandException("not_found", $"Rig '{rigId}' does not exist.");
        return new(Array.Empty<Change>(), RigCommands.ReportToJson(RigCommands.CurrentReport(rig)));
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}
