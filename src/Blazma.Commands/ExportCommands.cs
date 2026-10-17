using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Export;

namespace Blazma.Commands;

/// <summary>
/// Phase 4: export a clip to glTF binary (+ JSON sidecar) and validate exported files. Both are
/// ReadOnly for the project (nothing to undo); the files they touch are declared through
/// <see cref="CommandDescriptor.FileArg"/>, so agents get the allowed-folder and new-file-only rules.
/// </summary>
public static class ExportCommands
{
    public static void Register(CommandRegistry r)
    {
        r.Register(new("animation.export",
            "Export a clip as a .glb animation (glTF 2.0 binary) plus a '<name>.blazma.json' sidecar (fps, loop, markers, events, root motion, bone map). " +
            "The built-in mannequin gets a new file with a simple mesh; an imported character's original file is kept and the animation is appended. " +
            "Writes files outside the project (the project itself is not changed); agents may only create new files. The written file is re-read and validated.",
            CommandEffect.ReadOnly,
            S.Of(S.Str("clipId", "Clip id.", true),
                 S.Str("characterId", "Character to export with (default: the first character using the clip's rig).", false),
                 S.Str("path", "Absolute path of the .glb to create; its folder must exist.", true),
                 S.Str("rootMotion", "keep (default) | in_place (remove the root's horizontal X/Z travel, keep height) | in_place_all (remove all root translation).", false),
                 S.Int("startFrame", "First frame to export (default 0); it becomes time 0 in the file.", false),
                 S.Int("endFrame", "Last frame to export, inclusive (default: clip length).", false),
                 S.Bool("sidecar", "Also write '<name>.blazma.json' next to the .glb (default true).", false),
                 S.Bool("overwrite", "Replace existing files (default false). Ignored for agents, which can only create new files.", false)),
            a => new ExportAnimation(S.Id(a, "clipId"), S.OptId(a, "characterId"), S.Text(a, "path"), RootMotion(a),
                Frame(a, "startFrame"), Frame(a, "endFrame"), (bool?)a["sidecar"] ?? true, (bool?)a["overwrite"] ?? false))
        {
            FileArg = new FileArgument("path", Write: true, Extension: ".glb", OverwriteArgument: "overwrite"),
        });

        r.Register(new("animation.validate",
            "Re-read an exported .glb and compare it with the clip frame by frame (position 1e-4 m, rotation 0.01°, scale 1e-4) and check its glTF structure, hierarchy and duration. Returns ok, max errors and issues.",
            CommandEffect.ReadOnly,
            S.Of(S.Str("path", "Absolute path of the .glb.", true), S.Str("clipId", "Clip id.", true),
                 S.Str("characterId", "Character the file was exported with (default: as for animation.export).", false)),
            a => new ValidateAnimation(S.Text(a, "path"), S.Id(a, "clipId"), S.OptId(a, "characterId")))
        {
            FileArg = new FileArgument("path"),
        });
    }

    static RootMotionMode RootMotion(JsonObject a) =>
        RootMotionModes.TryParse((string?)a["rootMotion"], out var m) ? m
            : throw new CommandException("invalid_args", "'rootMotion' must be keep, in_place or in_place_all.");

    static long? Frame(JsonObject a, string key)
    {
        var v = Args.OptLong(a, key);
        return v is < 0 or > 10_000_000 ? throw new CommandException("invalid_args", $"'{key}' must be between 0 and 10,000,000.") : v;
    }

    /// <summary>The clip, and the character (and its rig) to evaluate it on.</summary>
    internal static (Clip, Rig) Resolve(Project p, ObjectId clipId, ObjectId? characterId)
    {
        var clip = p.FindClip(clipId) ?? throw new CommandException("not_found", $"Clip '{clipId}' does not exist.");
        Character? character;
        if (characterId is { } cid)
            character = p.FindCharacter(cid) ?? throw new CommandException("not_found", $"Character '{cid}' does not exist.");
        else
            character = (clip.Skeleton.IsEmpty ? null : p.Characters.Find(c => c.Rig == clip.Skeleton)) ?? p.Characters.FirstOrDefault()
                ?? throw new CommandException("not_found", "The project has no character to export the clip with.");
        var rig = p.FindRig(character.Rig) ?? throw new CommandException("not_found", $"Rig of character '{character.Name}' does not exist.");
        if (!clip.Skeleton.IsEmpty && clip.Skeleton != rig.Id)
            throw new CommandException("rig_mismatch", $"Clip '{clip.Name}' animates another rig than character '{character.Name}'.");
        return (clip, rig);
    }

    internal static CommandException Map(ExportException e) => new(e.Code, e.Message);
}

public sealed class ExportAnimation(ObjectId clipId, ObjectId? characterId, string path, RootMotionMode rootMotion,
    long? startFrame, long? endFrame, bool sidecar, bool overwrite) : ICommand
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public string Id => "animation.export";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Export animation";

    public CommandOutcome Execute(Project p)
    {
        var (clip, rig) = ExportCommands.Resolve(p, clipId, characterId);
        var full = Path.GetFullPath(path);
        if (!full.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            throw new CommandException("unsupported_format", "The export path must end in .glb.");
        var dir = Path.GetDirectoryName(full);
        if (dir is null || !Directory.Exists(dir))
            throw new CommandException("not_found", $"The folder '{dir}' does not exist.");
        var sidecarPath = AnimationExporter.SidecarPathFor(full);
        foreach (var target in sidecar ? new[] { full, sidecarPath } : new[] { full })
        {
            if (Directory.Exists(target)) throw new CommandException("file_exists", $"'{target}' is a folder.");
            if (!overwrite && File.Exists(target))
                throw new CommandException("file_exists", $"'{target}' already exists; pass overwrite=true to replace it.");
        }

        var options = new ExportOptions { StartFrame = startFrame, EndFrame = endFrame, RootMotion = rootMotion };
        ExportResult result;
        try { result = AnimationExporter.Export(p, clip, rig, options, Path.GetFileName(full)); }
        catch (ExportException e) { throw ExportCommands.Map(e); }

        try
        {
            AtomicFile.Write(full, s => s.Write(result.Glb), tmp => GlbContainer.Parse(File.ReadAllBytes(tmp)), keepBackup: false);
            if (sidecar)
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(result.Sidecar, Indented);
                AtomicFile.Write(sidecarPath, s => s.Write(json), keepBackup: false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ExportException)
        {
            throw new CommandException("io_error", $"Could not write the export: {e.Message}");
        }

        var report = ExportValidator.Validate(full, p, clip, rig, options);
        var warnings = result.Warnings.Select(w => new Diagnostic("export", w)).ToList();
        if (!report.Ok)
            warnings.Add(new Diagnostic("validation_failed", $"The exported file differs from the clip: {report.Issues[0]}"));
        var baked = result.Baked;
        return new(Array.Empty<Change>(), new JsonObject
        {
            ["path"] = full,
            ["bytes"] = result.Glb.Length,
            ["animation"] = result.AnimationName,
            ["frames"] = baked.FrameCount,
            ["startFrame"] = baked.StartFrame,
            ["endFrame"] = baked.EndFrame,
            ["durationSeconds"] = baked.DurationSeconds,
            ["fps"] = clip.Fps.ToString(),
            ["source"] = rig.Source.Kind == RigSourceKind.Imported ? "imported" : "builtin",
            ["rootMotion"] = result.Sidecar["rootMotion"] is JsonObject rm
                ? new JsonObject { ["mode"] = rm["mode"]!.DeepClone(), ["bone"] = rm["bone"]!.DeepClone(), ["totalDisplacement"] = rm["totalDisplacement"]!.DeepClone() }
                : null,
            ["sidecarPath"] = sidecar ? sidecarPath : null,
            ["validation"] = report.ToJson(5),
        }, warnings);
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}

public sealed class ValidateAnimation(string path, ObjectId clipId, ObjectId? characterId) : ICommand
{
    public string Id => "animation.validate";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Validate export";

    public CommandOutcome Execute(Project p)
    {
        var (clip, rig) = ExportCommands.Resolve(p, clipId, characterId);
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new CommandException("not_found", $"File not found: {full}");
        var info = new FileInfo(full);
        if (info.Length == 0) throw new CommandException("not_a_file", "The file is empty or not a regular file.");
        if (info.Length > RigCommands.MaxImportBytes) throw new CommandException("too_large", "The file is larger than 256 MB.");
        var report = ExportValidator.Validate(full, p, clip, rig);
        var data = report.ToJson(50);
        data["path"] = full;
        return new(Array.Empty<Change>(), data);
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}
