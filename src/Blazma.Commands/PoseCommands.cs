using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Rigging;

namespace Blazma.Commands;

/// <summary>Phase 2 commands: mannequin, posing (FK/IK), mirror, reset, paste and the pose library.</summary>
public static class PoseCommands
{
    public static void Register(CommandRegistry r)
    {
        r.Register(new("scene.add_mannequin", "Add the built-in humanoid mannequin to the scene. Returns characterId.", CommandEffect.Mutating,
            S.Of(S.Str("name", "Character name (default 'Mannequin').", false)),
            a => new AddMannequin(S.OptName(a, "name") ?? "Mannequin")));

        r.Register(new("character.remove", "Remove a character from the scene (undoable).", CommandEffect.Destructive,
            S.Of(S.Str("characterId", "Character id.", true)),
            a => new RemoveCharacter(S.Id(a, "characterId"))));

        r.Register(new("rig.inspect", "List a rig's bones (name, parent, standard name, rest), controls and IK chains.", CommandEffect.ReadOnly,
            S.Of(S.Str("rigId", "Rig id (or use characterId).", false), S.Str("characterId", "Character whose rig to inspect.", false)),
            a => new InspectRig(S.OptId(a, "rigId"), S.OptId(a, "characterId"))));

        r.Register(new("pose.get", "Current pose of a character: per-bone local rotation (Euler XYZ degrees) and model-space position, and control states.", CommandEffect.ReadOnly,
            S.Of(S.Str("characterId", "Character id.", true)),
            a => new GetPose(S.Id(a, "characterId"))));

        r.Register(new("pose.set_bone", "FK: set a bone's local rotation (intrinsic Euler XYZ, degrees, relative to its parent) and optionally its local translation (metres). Joint limits apply.", CommandEffect.Mutating,
            S.Of(S.Str("characterId", "Character id.", true), S.Str("bone", "Bone id or name.", true),
                 S.Num("rx", "Rotation X (deg).", true), S.Num("ry", "Rotation Y (deg).", true), S.Num("rz", "Rotation Z (deg).", true),
                 S.Num("tx", "Local translation X (m).", false), S.Num("ty", "Local translation Y (m).", false), S.Num("tz", "Local translation Z (m).", false)),
            a => new SetBone(S.Id(a, "characterId"), S.Text(a, "bone"),
                new Vec3(S.Number(a, "rx"), S.Number(a, "ry"), S.Number(a, "rz")),
                a["tx"] is null && a["ty"] is null && a["tz"] is null ? null
                    : new Vec3(S.OptNumber(a, "tx") ?? double.NaN, S.OptNumber(a, "ty") ?? double.NaN, S.OptNumber(a, "tz") ?? double.NaN))));

        r.Register(new("pose.set_control", "IK: move an IK target or pole control (model space, metres; rotation as Euler XYZ degrees). The chain is solved.", CommandEffect.Mutating,
            S.Of(S.Str("characterId", "Character id.", true), S.Str("control", "Control id or name, e.g. ik_foot_l.", true),
                 S.Num("x", "Position X (m).", true), S.Num("y", "Position Y (m).", true), S.Num("z", "Position Z (m).", true),
                 S.Num("rx", "Rotation X (deg).", false), S.Num("ry", "Rotation Y (deg).", false), S.Num("rz", "Rotation Z (deg).", false),
                 S.Bool("pinned", "Pin the chain to this target.", false)),
            a => new SetControl(S.Id(a, "characterId"), S.Text(a, "control"),
                new Vec3(S.Number(a, "x"), S.Number(a, "y"), S.Number(a, "z")),
                a["rx"] is null && a["ry"] is null && a["rz"] is null ? null
                    : new Vec3(S.OptNumber(a, "rx") ?? 0, S.OptNumber(a, "ry") ?? 0, S.OptNumber(a, "rz") ?? 0),
                a["pinned"]?.GetValue<bool>())));

        r.Register(new("pose.set_pinned", "Pin or unpin an IK target (pinned hands/feet stay in place when the body moves).", CommandEffect.Mutating,
            S.Of(S.Str("characterId", "Character id.", true), S.Str("control", "IK target id or name.", true), S.Bool("pinned", "Pinned state.", true)),
            a => new SetPinned(S.Id(a, "characterId"), S.Text(a, "control"), a["pinned"]!.GetValue<bool>())));

        r.Register(new("pose.reset", "Reset the whole pose, or one bone, to rest.", CommandEffect.Mutating,
            S.Of(S.Str("characterId", "Character id.", true), S.Str("bone", "Only this bone (id or name).", false)),
            a => new ResetPose(S.Id(a, "characterId"), (string?)a["bone"])));

        r.Register(new("pose.mirror", "Mirror the pose left ↔ right.", CommandEffect.Mutating,
            S.Of(S.Str("characterId", "Character id.", true)),
            a => new MirrorPose(S.Id(a, "characterId"))));

        r.Register(new("pose.apply", "Apply a pose given as JSON (from pose.get's 'pose' field or a copied pose).", CommandEffect.Mutating,
            S.Of(S.Str("characterId", "Character id.", true), S.Str("poseJson", "Pose JSON.", true)),
            a => new ApplyPose(S.Id(a, "characterId"), ParsePose((string)a["poseJson"]!), "Paste pose")));

        r.Register(new("poselib.save", "Save the character's current pose into the pose library. Returns poseId.", CommandEffect.Mutating,
            S.Of(S.Str("characterId", "Character id.", true), S.Str("name", "Pose name.", true)),
            a => new SavePoseAsset(S.Id(a, "characterId"), S.Name(a, "name"))));

        r.Register(new("poselib.apply", "Apply a pose from the library to a character.", CommandEffect.Mutating,
            S.Of(S.Str("characterId", "Character id.", true), S.Str("poseId", "Pose id.", true)),
            a => new ApplyPoseAsset(S.Id(a, "characterId"), S.Id(a, "poseId"))));

        r.Register(new("poselib.rename", "Rename a library pose.", CommandEffect.Mutating,
            S.Of(S.Str("poseId", "Pose id.", true), S.Str("name", "New name.", true)),
            a => new RenamePoseAsset(S.Id(a, "poseId"), S.Name(a, "name"))));

        r.Register(new("poselib.delete", "Delete a library pose (undoable).", CommandEffect.Destructive,
            S.Of(S.Str("poseId", "Pose id.", true)),
            a => new DeletePoseAsset(S.Id(a, "poseId"))));
    }

    public static Pose ParsePose(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Pose>(json, ProjectArchive.JsonOptions)
                ?? throw new CommandException("invalid_args", "poseJson is empty.");
        }
        catch (JsonException e)
        {
            throw new CommandException("invalid_args", $"poseJson is not a valid pose: {e.Message}");
        }
    }

    public static string SerializePose(Pose pose) => JsonSerializer.Serialize(pose, ProjectArchive.JsonOptions);

    internal static Bone ResolveBone(Rig rig, string idOrName) =>
        (ObjectId.TryParse(idOrName, out var id) ? rig.FindBone(id) : null) ?? rig.FindBoneByName(idOrName)
        ?? throw new CommandException("not_found", $"Bone '{idOrName}' does not exist in rig '{rig.Name}'.");

    internal static RigControl ResolveControl(Rig rig, string idOrName) =>
        (ObjectId.TryParse(idOrName, out var id) ? rig.FindControl(id) : null) ?? rig.Controls.Find(c => c.Name == idOrName)
        ?? throw new CommandException("not_found", $"Control '{idOrName}' does not exist in rig '{rig.Name}'.");

    internal static (Character, Rig) Resolve(Project p, ObjectId characterId)
    {
        var c = p.FindCharacter(characterId) ?? throw new CommandException("not_found", $"Character '{characterId}' does not exist.");
        return (c, p.GetRig(c.Rig));
    }

    internal static Change PoseChanged(ObjectId characterId) => new(Change.PoseChanged, characterId.ToString());
}

/// <summary>Argument schema helpers (shared with built-ins).</summary>
static class S
{
    public static (string, JsonObject, bool) Str(string n, string d, bool req) => (n, new JsonObject { ["type"] = "string", ["description"] = d, ["minLength"] = 1 }, req);
    public static (string, JsonObject, bool) Num(string n, string d, bool req) => (n, new JsonObject { ["type"] = "number", ["description"] = d }, req);
    public static (string, JsonObject, bool) Bool(string n, string d, bool req) => (n, new JsonObject { ["type"] = "boolean", ["description"] = d }, req);

    public static JsonObject Of(params (string Name, JsonObject Schema, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in props)
        {
            properties[p.Name] = p.Schema;
            if (p.Required) required.Add(p.Name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
    }

    public static ObjectId Id(JsonObject a, string key) => Args.Id(a, key);
    public static ObjectId? OptId(JsonObject a, string key) => a[key] is null ? null : Args.Id(a, key);
    public static string Name(JsonObject a, string key) => Args.Name(a, key);
    public static string? OptName(JsonObject a, string key) => a[key] is null ? null : Args.Name(a, key);
    public static string Text(JsonObject a, string key) => ((string?)a[key])?.Trim() ?? throw new CommandException("invalid_args", $"'{key}' is required.");

    public static double Number(JsonObject a, string key) =>
        OptNumber(a, key) ?? throw new CommandException("invalid_args", $"'{key}' is required.");

    public static double? OptNumber(JsonObject a, string key)
    {
        if (a[key] is not JsonValue v || !SchemaValidator.TryGetNumber(v, out var d)) return null;
        if (!double.IsFinite(d)) throw new CommandException("invalid_args", $"'{key}' must be a finite number.");
        return d;
    }
}

/// <summary>
/// Base for edits that change one character's pose. Works on a copy and swaps it in only on
/// success, so a failed command never leaves a half-edited pose. Undo restores the snapshot;
/// Redo recomputes from it (all parameters are absolute, so this is deterministic).
/// </summary>
public abstract class PoseEdit(ObjectId characterId) : ICommand
{
    Pose? _before;
    protected ObjectId CharacterId { get; } = characterId;

    public abstract string Id { get; }
    public virtual CommandEffect Effect => CommandEffect.Mutating;
    public abstract string Label { get; }

    public CommandOutcome Execute(Project project)
    {
        var (character, rig) = PoseCommands.Resolve(project, CharacterId);
        var before = _before ?? character.Pose.Clone();
        var work = before.Clone();
        var warnings = new List<Diagnostic>();
        var data = Apply(project, rig, work, warnings);
        _before = before;
        character.Pose = work;
        return new CommandOutcome(new[] { PoseCommands.PoseChanged(CharacterId) }, data, warnings);
    }

    public IReadOnlyList<Change> Undo(Project project)
    {
        project.GetCharacter(CharacterId).Pose = _before!.Clone();
        return new[] { PoseCommands.PoseChanged(CharacterId) };
    }

    /// <summary>Mutate <paramref name="pose"/> (a private copy). Throw CommandException to abort.</summary>
    protected abstract JsonNode? Apply(Project project, Rig rig, Pose pose, List<Diagnostic> warnings);

    public virtual bool TryMerge(ICommand next) => false;
}

public sealed class SetBone(ObjectId characterId, string bone, Vec3 eulerDegrees, Vec3? translation) : PoseEdit(characterId)
{
    string _bone = bone;
    Vec3 _euler = eulerDegrees;
    Vec3? _translation = translation;

    public override string Id => "pose.set_bone";
    public override string Label => "Rotate bone";

    protected override JsonNode? Apply(Project project, Rig rig, Pose pose, List<Diagnostic> warnings)
    {
        var b = PoseCommands.ResolveBone(rig, _bone);
        if (PoseSolver.DrivingChain(rig, pose, b.Id) is { } chain)
            throw new CommandException("driven_by_ik", $"'{b.Name}' is driven by the pinned IK chain '{chain.Name}'. Move its target or unpin it.");

        const double d = Math.PI / 180;
        var rotation = Quat.FromEulerXYZ(_euler * d).Normalized();
        rotation = JointLimiter.Clamp(b, rotation, out var clamped);
        if (clamped) warnings.Add(new Diagnostic("clamped_by_limits", $"'{b.Name}' was limited to its joint range."));

        var current = pose.LocalOf(b);
        var t = current.Translation;
        if (_translation is { } tr)
            t = new Vec3(double.IsNaN(tr.X) ? t.X : tr.X, double.IsNaN(tr.Y) ? t.Y : tr.Y, double.IsNaN(tr.Z) ? t.Z : tr.Z);
        pose.Bones[b.Id.ToString()] = new Transform(t, rotation, current.Scale);
        PoseSolver.Update(rig, pose);
        return new JsonObject { ["boneId"] = b.Id.ToString(), ["clamped"] = clamped };
    }

    public override bool TryMerge(ICommand next)
    {
        if (next is not SetBone n || n.CharacterId != CharacterId || n._bone != _bone) return false;
        _euler = n._euler;
        _translation = n._translation ?? _translation;
        return true;
    }
}

public sealed class SetControl(ObjectId characterId, string control, Vec3 position, Vec3? eulerDegrees, bool? pinned) : PoseEdit(characterId)
{
    string _control = control;
    Vec3 _position = position;
    Vec3? _euler = eulerDegrees;
    bool? _pinned = pinned;

    public override string Id => "pose.set_control";
    public override string Label => "Move IK control";

    protected override JsonNode? Apply(Project project, Rig rig, Pose pose, List<Diagnostic> warnings)
    {
        var c = PoseCommands.ResolveControl(rig, _control);
        var state = PoseSolver.StateOf(rig, pose, c.Id).Clone();
        var rotation = _euler is { } e ? Quat.FromEulerXYZ(e * (Math.PI / 180)).Normalized() : state.Transform.Rotation;
        state.Transform = new Transform(_position, rotation, Vec3.One);
        if (_pinned is { } p) state.Pinned = p;
        pose.Controls[c.Id.ToString()] = state;
        var chain = PoseSolver.ChainForControl(rig, c.Id);
        PoseSolver.Update(rig, pose, chain?.Id);
        return new JsonObject { ["controlId"] = c.Id.ToString(), ["chain"] = chain?.Name };
    }

    public override bool TryMerge(ICommand next)
    {
        if (next is not SetControl n || n.CharacterId != CharacterId || n._control != _control) return false;
        _position = n._position;
        _euler = n._euler ?? _euler;
        _pinned = n._pinned ?? _pinned;
        return true;
    }
}

public sealed class SetPinned(ObjectId characterId, string control, bool pinned) : PoseEdit(characterId)
{
    public override string Id => "pose.set_pinned";
    public override string Label => pinned ? "Pin IK target" : "Unpin IK target";

    protected override JsonNode? Apply(Project project, Rig rig, Pose pose, List<Diagnostic> warnings)
    {
        var c = PoseCommands.ResolveControl(rig, control);
        if (c.Kind != ControlKind.IkTarget) throw new CommandException("invalid_args", $"'{c.Name}' is not an IK target.");
        var state = PoseSolver.StateOf(rig, pose, c.Id).Clone();
        state.Pinned = pinned;
        pose.Controls[c.Id.ToString()] = state;
        PoseSolver.Update(rig, pose);
        return null;
    }
}

public sealed class ResetPose(ObjectId characterId, string? bone) : PoseEdit(characterId)
{
    public override string Id => "pose.reset";
    public override string Label => bone is null ? "Reset pose" : "Reset bone";

    protected override JsonNode? Apply(Project project, Rig rig, Pose pose, List<Diagnostic> warnings)
    {
        if (bone is null)
        {
            pose.Bones.Clear();
            pose.Controls.Clear();
        }
        else
        {
            var b = PoseCommands.ResolveBone(rig, bone);
            if (PoseSolver.DrivingChain(rig, pose, b.Id) is { } chain)
                throw new CommandException("driven_by_ik", $"'{b.Name}' is driven by the pinned IK chain '{chain.Name}'.");
            pose.Bones.Remove(b.Id.ToString());
        }
        PoseSolver.Update(rig, pose);
        return null;
    }
}

public sealed class MirrorPose(ObjectId characterId) : PoseEdit(characterId)
{
    public override string Id => "pose.mirror";
    public override string Label => "Mirror pose";

    protected override JsonNode? Apply(Project project, Rig rig, Pose pose, List<Diagnostic> warnings)
    {
        var mirrored = PoseMirror.Mirror(rig, pose);
        pose.Bones = mirrored.Bones;
        pose.Controls = mirrored.Controls;
        PoseSolver.Update(rig, pose);
        return null;
    }
}

public sealed class ApplyPose(ObjectId characterId, Pose source, string label) : PoseEdit(characterId)
{
    public override string Id => "pose.apply";
    public override string Label => label;

    protected override JsonNode? Apply(Project project, Rig rig, Pose pose, List<Diagnostic> warnings)
    {
        var ignored = 0;
        pose.Bones.Clear();
        pose.Controls.Clear();
        foreach (var (k, t) in source.Bones)
            if (ObjectId.TryParse(k, out var id) && rig.FindBone(id) is not null) pose.Bones[k] = t;
            else ignored++;
        foreach (var (k, c) in source.Controls)
            if (ObjectId.TryParse(k, out var id) && rig.FindControl(id) is not null) pose.Controls[k] = c.Clone();
            else ignored++;
        if (ignored > 0) warnings.Add(new Diagnostic("ignored_entries", $"{ignored} entries do not belong to this rig and were ignored."));
        PoseSolver.Update(rig, pose);
        return null;
    }
}

public sealed class ApplyPoseAsset(ObjectId characterId, ObjectId poseId) : PoseEdit(characterId)
{
    public override string Id => "poselib.apply";
    public override string Label => "Apply library pose";

    protected override JsonNode? Apply(Project project, Rig rig, Pose pose, List<Diagnostic> warnings)
    {
        var asset = project.FindPose(poseId) ?? throw new CommandException("not_found", $"Pose '{poseId}' does not exist.");
        if (asset.Rig != rig.Id) throw new CommandException("rig_mismatch", $"Pose '{asset.Name}' was made for a different rig.");
        pose.Bones = new Dictionary<string, Transform>(asset.Pose.Bones);
        pose.Controls = asset.Pose.Controls.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        PoseSolver.Update(rig, pose);
        return null;
    }
}

public sealed class AddMannequin(string name) : ICommand
{
    // Fixed at construction so Redo recreates the same objects.
    readonly Rig _rig = MannequinFactory.Create();
    Character? _character;
    bool _addedRig;

    public string Id => "scene.add_mannequin";
    public CommandEffect Effect => CommandEffect.Mutating;
    public string Label => "Add mannequin";

    public CommandOutcome Execute(Project p)
    {
        // Reuse the project's mannequin rig if there is one, so poses are shareable.
        var rig = p.Rigs.Find(r => r.Source.Kind == RigSourceKind.Builtin && r.Name == MannequinFactory.RigName);
        _addedRig = rig is null;
        rig ??= _rig;
        if (_addedRig) p.Rigs.Add(rig);
        _character ??= MannequinFactory.CreateCharacter(rig, name);
        _character.Rig = rig.Id;
        p.Characters.Add(_character);
        return new(new[] { new Change(Change.CharacterAdded, _character.Id.ToString()) },
            new JsonObject { ["characterId"] = _character.Id.ToString(), ["rigId"] = rig.Id.ToString() });
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        p.Characters.RemoveAll(c => c.Id == _character!.Id);
        if (_addedRig) p.Rigs.RemoveAll(r => r.Id == _rig.Id);
        return new[] { new Change(Change.CharacterRemoved, _character!.Id.ToString()) };
    }
}

public sealed class RemoveCharacter(ObjectId characterId) : ICommand
{
    Character? _removed;
    int _index;
    public string Id => "character.remove";
    public CommandEffect Effect => CommandEffect.Destructive;
    public string Label => "Remove character";

    public CommandOutcome Execute(Project p)
    {
        _index = p.Characters.FindIndex(c => c.Id == characterId);
        if (_index < 0) throw new CommandException("not_found", $"Character '{characterId}' does not exist.");
        _removed = p.Characters[_index];
        p.Characters.RemoveAt(_index);
        return new(new[] { new Change(Change.CharacterRemoved, characterId.ToString()) });
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        p.Characters.Insert(Math.Min(_index, p.Characters.Count), _removed!);
        return new[] { new Change(Change.CharacterAdded, characterId.ToString()) };
    }
}

public sealed class SavePoseAsset(ObjectId characterId, string name) : ICommand
{
    readonly ObjectId _id = ObjectId.New();
    public string Id => "poselib.save";
    public CommandEffect Effect => CommandEffect.Mutating;
    public string Label => "Save pose";

    public CommandOutcome Execute(Project p)
    {
        var (character, rig) = PoseCommands.Resolve(p, characterId);
        p.PoseLibrary.Add(new PoseAsset { Id = _id, Name = name, Rig = rig.Id, Pose = character.Pose.Clone() });
        return new(new[] { new Change(Change.PoseLibraryChanged, _id.ToString()) }, new JsonObject { ["poseId"] = _id.ToString() });
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        p.PoseLibrary.RemoveAll(x => x.Id == _id);
        return new[] { new Change(Change.PoseLibraryChanged, _id.ToString()) };
    }
}

public sealed class RenamePoseAsset(ObjectId poseId, string name) : ICommand
{
    string? _old;
    public string Id => "poselib.rename";
    public CommandEffect Effect => CommandEffect.Mutating;
    public string Label => "Rename pose";

    public CommandOutcome Execute(Project p)
    {
        var asset = p.FindPose(poseId) ?? throw new CommandException("not_found", $"Pose '{poseId}' does not exist.");
        _old ??= asset.Name;
        asset.Name = name;
        return new(new[] { new Change(Change.PoseLibraryChanged, poseId.ToString()) });
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        p.FindPose(poseId)!.Name = _old!;
        return new[] { new Change(Change.PoseLibraryChanged, poseId.ToString()) };
    }
}

public sealed class DeletePoseAsset(ObjectId poseId) : ICommand
{
    PoseAsset? _removed;
    int _index;
    public string Id => "poselib.delete";
    public CommandEffect Effect => CommandEffect.Destructive;
    public string Label => "Delete pose";

    public CommandOutcome Execute(Project p)
    {
        _index = p.PoseLibrary.FindIndex(x => x.Id == poseId);
        if (_index < 0) throw new CommandException("not_found", $"Pose '{poseId}' does not exist.");
        _removed = p.PoseLibrary[_index];
        p.PoseLibrary.RemoveAt(_index);
        return new(new[] { new Change(Change.PoseLibraryChanged, poseId.ToString()) });
    }

    public IReadOnlyList<Change> Undo(Project p)
    {
        p.PoseLibrary.Insert(Math.Min(_index, p.PoseLibrary.Count), _removed!);
        return new[] { new Change(Change.PoseLibraryChanged, poseId.ToString()) };
    }
}

public sealed class InspectRig(ObjectId? rigId, ObjectId? characterId) : ICommand
{
    public string Id => "rig.inspect";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Inspect rig";

    public CommandOutcome Execute(Project p)
    {
        var rig = rigId is { } r ? p.FindRig(r)
            : characterId is { } c ? p.FindRig(p.FindCharacter(c)?.Rig ?? default)
            : throw new CommandException("invalid_args", "Pass rigId or characterId.");
        if (rig is null) throw new CommandException("not_found", "Rig or character does not exist.");

        var ev = new RigEvaluator(rig);
        var rest = ev.RestModel();
        var bones = new JsonArray();
        for (var i = 0; i < rig.Bones.Count; i++)
        {
            var b = rig.Bones[i];
            bones.Add(new JsonObject
            {
                ["boneId"] = b.Id.ToString(),
                ["name"] = b.Name,
                ["parent"] = b.Parent.IsEmpty ? null : rig.FindBone(b.Parent)?.Name,
                ["standard"] = rig.StandardNameOf(b.Id),
                ["restPosition"] = Vec(rest[i].Translation),
                ["mass"] = b.Mass,
                ["hasLimits"] = b.Limits is not null,
            });
        }
        var chains = new JsonArray();
        foreach (var ch in rig.IkChains)
            chains.Add(new JsonObject
            {
                ["name"] = ch.Name,
                ["upper"] = rig.FindBone(ch.Upper)?.Name,
                ["lower"] = rig.FindBone(ch.Lower)?.Name,
                ["end"] = rig.FindBone(ch.End)?.Name,
                ["target"] = rig.FindControl(ch.Target)?.Name,
                ["pole"] = ch.Pole.IsEmpty ? null : rig.FindControl(ch.Pole)?.Name,
            });
        var missing = new JsonArray(HumanoidStandard.Required.Where(s => rig.FindByStandardName(s) is null).Select(s => (JsonNode?)s).ToArray());
        return new(Array.Empty<Change>(), new JsonObject
        {
            ["rigId"] = rig.Id.ToString(),
            ["name"] = rig.Name,
            ["source"] = rig.Source.Kind.ToString(),
            ["boneCount"] = rig.Bones.Count,
            ["bones"] = bones,
            ["controls"] = new JsonArray(rig.Controls.Select(c => (JsonNode?)new JsonObject { ["controlId"] = c.Id.ToString(), ["name"] = c.Name, ["kind"] = c.Kind.ToString() }).ToArray()),
            ["ikChains"] = chains,
            ["missingRequired"] = missing,
        });
    }

    internal static JsonArray Vec(Vec3 v) => new(Math.Round(v.X, 6), Math.Round(v.Y, 6), Math.Round(v.Z, 6));

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}

public sealed class GetPose(ObjectId characterId) : ICommand
{
    public string Id => "pose.get";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Get pose";

    public CommandOutcome Execute(Project p)
    {
        var (character, rig) = PoseCommands.Resolve(p, characterId);
        var ev = new RigEvaluator(rig);
        var locals = ev.Locals(character.Pose);
        var model = ev.Model(locals);
        var bones = new JsonArray();
        for (var i = 0; i < rig.Bones.Count; i++)
        {
            var e = locals[i].Rotation.ToEulerXYZ() * (180 / Math.PI);
            bones.Add(new JsonObject
            {
                ["name"] = rig.Bones[i].Name,
                ["rotation"] = InspectRig.Vec(e),
                ["position"] = InspectRig.Vec(model[i].Translation),
                ["posed"] = character.Pose.Bones.ContainsKey(rig.Bones[i].Id.ToString()),
            });
        }
        var controls = new JsonArray();
        foreach (var c in rig.Controls)
        {
            var s = PoseSolver.StateOf(rig, character.Pose, c.Id);
            controls.Add(new JsonObject { ["name"] = c.Name, ["position"] = InspectRig.Vec(s.Transform.Translation), ["pinned"] = s.Pinned });
        }
        return new(Array.Empty<Change>(), new JsonObject
        {
            ["characterId"] = character.Id.ToString(),
            ["bones"] = bones,
            ["controls"] = controls,
            ["pose"] = PoseCommands.SerializePose(character.Pose),
        });
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}
