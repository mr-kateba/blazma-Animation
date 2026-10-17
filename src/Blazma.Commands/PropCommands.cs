using System.Text.Json.Nodes;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Export;
using Blazma.Motion;
using Blazma.Rigging;

namespace Blazma.Commands;

/// <summary>
/// Phase 8: props (animatable objects). A prop's working value is its world transform, or its offset relative to a bone when
/// attached; key.set {props} records it in a clip (target kind Prop) and animation.export writes props as extra nodes.
/// </summary>
public static class PropCommands
{
    const string TransformHelp = "Position x/y/z (metres) and rotation rx/ry/rz (intrinsic Euler XYZ, degrees): the offset from the bone when attached, else world.";

    public static void Register(CommandRegistry r)
    {
        r.Register(new("prop.add", "Add a primitive prop (box, sphere, cylinder) to the scene, optionally attached to a character's bone (e.g. a sword in hand_r). " + TransformHelp + " Returns propId.",
            CommandEffect.Mutating,
            S.Of(new[] { S.Str("name", "Prop name.", true), S.Str("shape", "box (default) | sphere | cylinder.", false),
                S.Num("sizeX", "Size X in metres (box extent, sphere/cylinder diameter; default 0.1).", false), S.Num("sizeY", "Size Y (cylinder length; default 0.1).", false),
                S.Num("sizeZ", "Size Z (default 0.1).", false),
                S.Str("characterId", "Attach to this character…", false), S.Str("bone", "…at this bone (id or name).", false) }.Concat(TransformArgs()).ToArray()),
            a => new AddProp(S.Name(a, "name"), Shape(a), Size(a), S.OptId(a, "characterId"), S.OptName(a, "bone"), TransformFrom(a, null))));

        r.Register(new("prop.import", "Import a prop mesh from a .glb file (embedded in the project; static meshes, materials and textures are kept, skins/animations are not). " +
            "Optionally attach it to a character's bone. Returns propId.",
            CommandEffect.External,
            S.Of(S.Str("path", "Absolute path of the .glb file.", true), S.Str("name", "Prop name (default: file name).", false),
                 S.Str("characterId", "Attach to this character…", false), S.Str("bone", "…at this bone (id or name).", false)),
            a => new ImportProp(S.Text(a, "path"), S.OptName(a, "name"), S.OptId(a, "characterId"), S.OptName(a, "bone"))));

        r.Register(new("prop.remove", "Remove a prop (undoable). Its keys in clips are kept but ignored.", CommandEffect.Destructive,
            S.Of(S.Str("propId", "Prop id.", true)), a => new RemoveProp(S.Id(a, "propId"))));

        r.Register(new("prop.attach", "Attach a prop to a character's bone. keepWorld (default true) keeps it where it is now (offset computed from the character's current pose).",
            CommandEffect.Mutating,
            S.Of(S.Str("propId", "Prop id.", true), S.Str("characterId", "Character.", true), S.Str("bone", "Bone id or name.", true), S.Bool("keepWorld", "Keep its world placement (default true).", false)),
            a => new AttachProp(S.Id(a, "propId"), S.Id(a, "characterId"), S.Text(a, "bone"), (bool?)a["keepWorld"] ?? true)));

        r.Register(new("prop.detach", "Detach a prop from its bone. keepWorld (default true) keeps it where it is now.", CommandEffect.Mutating,
            S.Of(S.Str("propId", "Prop id.", true), S.Bool("keepWorld", "Keep its world placement (default true).", false)),
            a => new DetachProp(S.Id(a, "propId"), (bool?)a["keepWorld"] ?? true)));

        r.Register(new("prop.set_transform", "Set a prop's working transform (only the given components change). " + TransformHelp + " sx/sy/sz scale. Successive calls in one drag merge into one undo step.",
            CommandEffect.Mutating,
            S.Of(new[] { S.Str("propId", "Prop id.", true) }.Concat(TransformArgs()).Concat(new[]
                { S.Num("sx", "Scale X.", false), S.Num("sy", "Scale Y.", false), S.Num("sz", "Scale Z.", false) }).ToArray()),
            a => new SetPropTransform(S.Id(a, "propId"), a)));

        r.Register(new("prop.evaluate", "World transform of a prop (with a clip at a frame, or its working transform). Does not change anything.", CommandEffect.ReadOnly,
            S.Of(S.Str("propId", "Prop id.", true), S.Str("clipId", "Clip id (optional).", false), S.Num("frame", "Frame (with clipId).", false),
                 S.Str("characterId", "Character the clip is played on (default: the one the prop is attached to).", false)),
            a => new EvaluateProp(S.Id(a, "propId"), S.OptId(a, "clipId"), S.OptNumber(a, "frame") ?? 0, S.OptId(a, "characterId"))));
    }

    static IEnumerable<(string, JsonObject, bool)> TransformArgs() => new[]
    {
        S.Num("x", "Position X (m).", false), S.Num("y", "Position Y (m).", false), S.Num("z", "Position Z (m).", false),
        S.Num("rx", "Rotation X (deg).", false), S.Num("ry", "Rotation Y (deg).", false), S.Num("rz", "Rotation Z (deg).", false),
    };

    static PropShape Shape(JsonObject a) => ((string?)a["shape"])?.Trim().ToLowerInvariant() switch
    {
        null or "box" => PropShape.Box,
        "sphere" => PropShape.Sphere,
        "cylinder" => PropShape.Cylinder,
        _ => throw new CommandException("invalid_args", "'shape' must be box, sphere or cylinder (use prop.import for meshes)."),
    };

    static Vec3 Size(JsonObject a)
    {
        double C(string k)
        {
            var v = S.OptNumber(a, k) ?? 0.1;
            return !(v > 0 && v <= 100) ? throw new CommandException("invalid_args", $"'{k}' must be between 0 and 100 m.") : v;
        }
        return new Vec3(C("sizeX"), C("sizeY"), C("sizeZ"));
    }

    /// <summary>Overrides the given components of <paramref name="current"/> (identity when null).</summary>
    internal static Transform TransformFrom(JsonObject a, Transform? current)
    {
        var t = current ?? Transform.Identity;
        double? N(string k)
        {
            var v = S.OptNumber(a, k);
            return v is { } d && Math.Abs(d) > 100_000 ? throw new CommandException("invalid_args", $"'{k}' is out of range.") : v;
        }
        var pos = new Vec3(N("x") ?? t.Translation.X, N("y") ?? t.Translation.Y, N("z") ?? t.Translation.Z);
        var rot = t.Rotation;
        if (a["rx"] is not null || a["ry"] is not null || a["rz"] is not null)
        {
            var e = t.Rotation.ToEulerXYZ() * (180 / Math.PI);
            rot = Quat.FromEulerXYZ(new Vec3(N("rx") ?? e.X, N("ry") ?? e.Y, N("rz") ?? e.Z) * (Math.PI / 180)).Normalized();
        }
        var scale = t.Scale;
        if (a.ContainsKey("sx") || a.ContainsKey("sy") || a.ContainsKey("sz"))
        {
            scale = new Vec3(N("sx") ?? t.Scale.X, N("sy") ?? t.Scale.Y, N("sz") ?? t.Scale.Z);
            if (!(scale.X > 0 && scale.Y > 0 && scale.Z > 0)) throw new CommandException("invalid_args", "Scale must be greater than 0.");
        }
        return new Transform(pos, rot, scale);
    }

    internal static Prop Require(Project p, ObjectId id) =>
        p.FindProp(id) ?? throw new CommandException("not_found", $"Prop '{id}' does not exist.");

    /// <summary>Resolve "prop names or ids" (comma list) to props.</summary>
    internal static List<Prop> Resolve(Project p, IEnumerable<string> names) => names.Select(n =>
        (ObjectId.TryParse(n, out var id) ? p.FindProp(id) : null) ?? p.Props.Find(x => x.Name == n)
        ?? throw new CommandException("not_found", $"'{n}' is not a prop of this project.")).ToList();

    /// <summary>The attachment for (character, bone) with the given offset; validates both.</summary>
    internal static PropAttachment Attachment(Project p, ObjectId characterId, string bone, Transform offset)
    {
        var (_, rig) = PoseCommands.Resolve(p, characterId);
        var b = PoseCommands.ResolveBone(rig, bone);
        return new PropAttachment { Character = characterId, Bone = b.Id, Offset = offset };
    }

    /// <summary>Model transform of a character's bone in its current working pose.</summary>
    internal static Transform BoneModel(Project p, PropAttachment a)
    {
        var (character, rig) = PoseCommands.Resolve(p, a.Character);
        var i = rig.IndexOf(a.Bone);
        if (i < 0) throw new CommandException("not_found", "The attachment bone no longer exists.");
        return new RigEvaluator(rig).Model(character.Pose)[i];
    }

    /// <summary>World transform of a prop with its character's current working pose (no clip).</summary>
    internal static Transform WorkingWorld(Project p, Prop prop)
    {
        if (prop.Attachment is not { } a) return prop.Transform;
        var character = p.FindCharacter(a.Character);
        var rig = character is null ? null : p.FindRig(character.Rig);
        if (character is null || rig is null || rig.IndexOf(a.Bone) < 0) return prop.Transform;
        return new RigEvaluator(rig).Model(character.Pose)[rig.IndexOf(a.Bone)] * a.Offset;
    }

    internal static JsonObject Describe(Project p, Prop prop)
    {
        var w = WorkingWorld(p, prop);
        return new JsonObject
        {
            ["propId"] = prop.Id.ToString(),
            ["name"] = prop.Name,
            ["shape"] = prop.Shape.ToString().ToLowerInvariant(),
            ["size"] = PhysicsCommands.V(prop.Size),
            ["attachedTo"] = prop.Attachment is { } a ? new JsonObject
            {
                ["characterId"] = a.Character.ToString(),
                ["bone"] = p.FindCharacter(a.Character) is { } c && p.FindRig(c.Rig)?.FindBone(a.Bone) is { } b ? b.Name : null,
                ["boneId"] = a.Bone.ToString(),
            } : null,
            ["local"] = TransformJson(prop.Local),
            ["world"] = TransformJson(w),
        };
    }

    internal static JsonObject TransformJson(Transform t) => new()
    {
        ["position"] = PhysicsCommands.V(t.Translation),
        ["rotation"] = InspectRig.Vec(t.Rotation.ToEulerXYZ() * (180 / Math.PI)),
        ["quaternion"] = new JsonArray(t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W),
        ["scale"] = PhysicsCommands.V(t.Scale),
    };

    internal static Change Changed(ObjectId id) => new(Change.PropChanged, id.ToString());
}

public sealed class AddProp(string name, PropShape shape, Vec3 size, ObjectId? characterId, string? bone, Transform transform) : SceneEdit
{
    readonly ObjectId _id = ObjectId.New();
    public override string Id => "prop.add";
    public override string Label => "Add prop";
    protected override SceneParts Touches => SceneParts.Props;
    protected override IReadOnlyList<Change> Changes => new[] { PropCommands.Changed(_id) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        if ((characterId is null) != (bone is null)) throw new CommandException("invalid_args", "Give both 'characterId' and 'bone' to attach, or neither.");
        var prop = new Prop { Id = _id, Name = name, Shape = shape, Size = size };
        if (characterId is { } c) prop.Attachment = PropCommands.Attachment(p, c, bone!, transform);
        else prop.Transform = transform;
        p.Props.Add(prop);
        return PropCommands.Describe(p, prop);
    }
}

public sealed class ImportProp(string path, string? name, ObjectId? characterId, string? bone) : SceneEdit
{
    readonly ObjectId _id = ObjectId.New();
    ProjectAsset? _asset;
    Vec3 _size;
    public override string Id => "prop.import";
    public override CommandEffect Effect => CommandEffect.External;
    public override string Label => "Import prop";
    protected override SceneParts Touches => SceneParts.Props | SceneParts.Assets;
    protected override IReadOnlyList<Change> Changes => new[] { PropCommands.Changed(_id) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        if ((characterId is null) != (bone is null)) throw new CommandException("invalid_args", "Give both 'characterId' and 'bone' to attach, or neither.");
        _asset ??= Load(warnings);
        if (p.Assets.Sum(a => (long)a.Data.Length) + _asset.Data.Length > RigCommands.MaxProjectAssetBytes)
            throw new CommandException("too_large", "The project would exceed 1 GB of embedded files.");
        var prop = new Prop
        {
            Id = _id, Name = name ?? System.IO.Path.GetFileNameWithoutExtension(_asset.FileName), Shape = PropShape.Mesh, Asset = _asset.Id, Size = _size,
        };
        if (characterId is { } c) prop.Attachment = PropCommands.Attachment(p, c, bone!, Transform.Identity);
        p.Assets.Add(_asset);
        p.Props.Add(prop);
        return PropCommands.Describe(p, prop);
    }

    ProjectAsset Load(List<Diagnostic> warnings)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (!System.IO.Path.GetExtension(full).Equals(".glb", StringComparison.OrdinalIgnoreCase))
            throw new CommandException("unsupported_format", "Props can be imported from .glb files only.");
        if (!File.Exists(full)) throw new CommandException("not_found", $"File not found: {full}");
        var info = new FileInfo(full);
        if (info.Length == 0) throw new CommandException("not_a_file", "The file is empty or not a regular file.");
        if (info.Length > RigCommands.MaxImportBytes) throw new CommandException("too_large", "The file is larger than 256 MB.");
        var bytes = File.ReadAllBytes(full);
        try
        {
            var glb = GlbContainer.Parse(bytes);
            // Copy once into a scratch builder: proves the model can be exported, and gives its bounds.
            var scratch = new GltfBuilder(new JsonObject { ["asset"] = new JsonObject { ["version"] = "2.0" } });
            var notes = new List<string>();
            PropExport.AppendGlbScene(scratch, glb, notes, name ?? "prop");
            warnings.AddRange(notes.Select(n => new Diagnostic("import", n)));
            _size = Bounds(scratch.Json);
        }
        catch (ExportException e) { throw new CommandException(e.Code, e.Message); }
        return new ProjectAsset { FileName = System.IO.Path.GetFileName(full), MediaType = "model/gltf-binary", Data = bytes };
    }

    /// <summary>
    /// Extent of the POSITION bounds of all meshes (ignores node transforms); 0.1 m when unknown. Bounds must be finite numbers within
    /// ±<see cref="Prop.MaxSize"/> m (and the extent at most that), else the import is refused (invalid_gltf), so a prop can never get
    /// a size the archive refuses.
    /// </summary>
    internal static Vec3 Bounds(JsonObject json)
    {
        var acc = json["accessors"] as JsonArray ?? new JsonArray();
        Vec3? min = null, max = null;
        foreach (var mesh in json["meshes"] as JsonArray ?? new JsonArray())
            foreach (var prim in mesh?["primitives"] as JsonArray ?? new JsonArray())
                if (Export.GltfAnimationReader.Int(prim?["attributes"]?["POSITION"]) is int i && i >= 0 && i < acc.Count
                    && acc[i]?["min"] is JsonArray mn && acc[i]?["max"] is JsonArray mx && mn.Count == 3 && mx.Count == 3)
                {
                    var a = new Vec3(Coord(mn[0]), Coord(mn[1]), Coord(mn[2]));
                    var b = new Vec3(Coord(mx[0]), Coord(mx[1]), Coord(mx[2]));
                    min = min is { } m0 ? new Vec3(Math.Min(m0.X, a.X), Math.Min(m0.Y, a.Y), Math.Min(m0.Z, a.Z)) : a;
                    max = max is { } m1 ? new Vec3(Math.Max(m1.X, b.X), Math.Max(m1.Y, b.Y), Math.Max(m1.Z, b.Z)) : b;
                }
        if (min is not { } lo || max is not { } hi) return new Vec3(0.1, 0.1, 0.1);
        var d = hi - lo;
        if (!(d.X <= Prop.MaxSize && d.Y <= Prop.MaxSize && d.Z <= Prop.MaxSize))
            throw new CommandException("invalid_gltf", $"The model is larger than {Prop.MaxSize:N0} m (or its bounds are inverted); check its units.");
        return new Vec3(Math.Max(1e-3, d.X), Math.Max(1e-3, d.Y), Math.Max(1e-3, d.Z));

        static double Coord(JsonNode? n) =>
            n is JsonValue v && v.TryGetValue<double>(out var x) && double.IsFinite(x) && Math.Abs(x) <= Prop.MaxSize ? x
                : throw new CommandException("invalid_gltf", $"The model's POSITION bounds (accessor min/max) must be finite numbers within ±{Prop.MaxSize:N0} m.");
    }
}

public sealed class RemoveProp(ObjectId propId) : SceneEdit
{
    public override string Id => "prop.remove";
    public override CommandEffect Effect => CommandEffect.Destructive;
    public override string Label => "Remove prop";
    protected override SceneParts Touches => SceneParts.Props | SceneParts.Assets;
    protected override IReadOnlyList<Change> Changes => new[] { PropCommands.Changed(propId) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        var prop = PropCommands.Require(p, propId);
        p.Props.Remove(prop);
        // Drop the embedded model when no other prop uses it (rigs keep their own assets).
        if (prop.Shape == PropShape.Mesh && !p.Props.Exists(x => x.Asset == prop.Asset) && !p.Rigs.Exists(r => r.Source.Asset == prop.Asset))
            p.Assets.RemoveAll(a => a.Id == prop.Asset);
        if (p.Clips.Exists(c => c.Tracks.Exists(t => t.Target.Kind == TargetKind.Prop && t.Target.Id == propId)))
            warnings.Add(new Diagnostic("prop_keys_kept", "Clips still contain keys for this prop; they are ignored (undo brings the prop back)."));
        return null;
    }
}

public sealed class AttachProp(ObjectId propId, ObjectId characterId, string bone, bool keepWorld) : SceneEdit
{
    public override string Id => "prop.attach";
    public override string Label => "Attach prop";
    protected override SceneParts Touches => SceneParts.Props;
    protected override IReadOnlyList<Change> Changes => new[] { PropCommands.Changed(propId) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        var prop = PropCommands.Require(p, propId);
        var world = PropCommands.WorkingWorld(p, prop);
        var attachment = PropCommands.Attachment(p, characterId, bone, Transform.Identity);
        if (keepWorld) attachment.Offset = PropCommands.BoneModel(p, attachment).Inverse() * world;
        prop.Attachment = attachment;
        if (p.Clips.Exists(c => c.Tracks.Exists(t => t.Target.Kind == TargetKind.Prop && t.Target.Id == propId)))
            warnings.Add(new Diagnostic("prop_keys_space", "Existing keys of this prop are now read as offsets from the bone."));
        return PropCommands.Describe(p, prop);
    }
}

public sealed class DetachProp(ObjectId propId, bool keepWorld) : SceneEdit
{
    public override string Id => "prop.detach";
    public override string Label => "Detach prop";
    protected override SceneParts Touches => SceneParts.Props;
    protected override IReadOnlyList<Change> Changes => new[] { PropCommands.Changed(propId) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        var prop = PropCommands.Require(p, propId);
        if (prop.Attachment is null) throw new CommandException("not_attached", $"Prop '{prop.Name}' is not attached.");
        var world = PropCommands.WorkingWorld(p, prop);
        prop.Transform = keepWorld ? world : prop.Attachment.Offset;
        prop.Attachment = null;
        if (p.Clips.Exists(c => c.Tracks.Exists(t => t.Target.Kind == TargetKind.Prop && t.Target.Id == propId)))
            warnings.Add(new Diagnostic("prop_keys_space", "Existing keys of this prop are now read as world transforms."));
        return PropCommands.Describe(p, prop);
    }
}

public sealed class SetPropTransform(ObjectId propId, JsonObject args) : SceneEdit
{
    public override string Id => "prop.set_transform";
    public override string Label => "Move prop";
    protected override SceneParts Touches => SceneParts.Props;
    protected override IReadOnlyList<Change> Changes => new[] { PropCommands.Changed(propId) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        var prop = PropCommands.Require(p, propId);
        var t = PropCommands.TransformFrom(args, prop.Local);
        if (prop.Attachment is { } a) a.Offset = t;
        else prop.Transform = t;
        return PropCommands.Describe(p, prop);
    }

    public override bool TryMerge(ICommand next) => next is SetPropTransform s && s.PropId == propId && AbsorbAfterState(s);

    internal ObjectId PropId => propId;
}

public sealed class EvaluateProp(ObjectId propId, ObjectId? clipId, double frame, ObjectId? characterId) : ICommand
{
    public string Id => "prop.evaluate";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Evaluate prop";

    public CommandOutcome Execute(Project p)
    {
        var prop = PropCommands.Require(p, propId);
        if (clipId is null) return new CommandOutcome(Array.Empty<Change>(), PropCommands.Describe(p, prop));
        if (frame is < 0 or > 10_000_000) throw new CommandException("invalid_args", "'frame' must be between 0 and 10,000,000.");
        var clip = AnimationEditCommands.RequireClip(p, clipId.Value);
        Rig? rig = null;
        var who = characterId ?? prop.Attachment?.Character ?? default;
        if (characterId is { } given && p.FindCharacter(given) is null) throw new CommandException("not_found", $"Character '{given}' does not exist.");
        if (p.FindCharacter(who) is { } ch) rig = p.FindRig(ch.Rig);
        var time = Flicks.FromSeconds(frame / clip.Fps.FramesPerSecond);
        var data = PropCommands.Describe(p, prop);
        data["frame"] = frame;
        data["local"] = PropCommands.TransformJson(PropEvaluator.Local(prop, clip, time));
        data["world"] = PropCommands.TransformJson(PropEvaluator.WorldAt(p, prop, clip, rig, time, who));
        return new CommandOutcome(Array.Empty<Change>(), data);
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}
