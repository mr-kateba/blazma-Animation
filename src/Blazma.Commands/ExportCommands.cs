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
                 S.Int("startFrame", "First frame to export (default 0, or the loop start when the clip loops over part of its length); it becomes time 0 in the file.", false),
                 S.Int("endFrame", "Last frame to export, inclusive (default: clip length, or the loop end when the clip loops over part of its length).", false),
                 S.Bool("sidecar", "Also write '<name>.blazma.json' next to the .glb (default true).", false),
                 S.Bool("overwrite", "Replace existing files (default false). Allowed for the user (app, blazma-anim CLI); ignored for MCP agents, which can only create new files.", false),
                 S.Bool("validate", "Re-read the written file and validate it (default true).", false)),
            a => new ExportAnimation(S.Id(a, "clipId"), S.OptId(a, "characterId"), S.Text(a, "path"), RootMotion(a),
                Frame(a, "startFrame"), Frame(a, "endFrame"), (bool?)a["sidecar"] ?? true, (bool?)a["overwrite"] ?? false,
                (bool?)a["validate"] ?? true))
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
        var (clip, _, rig) = ResolveWith(p, clipId, characterId);
        return (clip, rig);
    }

    /// <summary>The clip, the character to evaluate it on (given, or the first one using the clip's rig) and its rig.</summary>
    internal static (Clip, Character, Rig) ResolveWith(Project p, ObjectId clipId, ObjectId? characterId)
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
        return (clip, character, rig);
    }

    internal static CommandException Map(ExportException e) => new(e.Code, e.Message);
}

public sealed class ExportAnimation(ObjectId clipId, ObjectId? characterId, string path, RootMotionMode rootMotion,
    long? startFrame, long? endFrame, bool sidecar, bool overwrite, bool validate = true) : ICommand
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public string Id => "animation.export";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "Export animation";

    public CommandOutcome Execute(Project p)
    {
        var (clip, character, rig) = ExportCommands.ResolveWith(p, clipId, characterId);
        var given = Path.GetFullPath(path);
        var name = Path.GetFileName(given);
        if (!FilePaths.IsPortableFileName(name))
            throw new CommandException("invalid_path", $"'{name}' is not a valid file name (no ':', wildcards, control characters or trailing dot/space).");
        if (!name.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            throw new CommandException("unsupported_format", "The export path must end in .glb.");
        var givenDir = Path.GetDirectoryName(given);
        if (givenDir is null || !Directory.Exists(givenDir))
            throw new CommandException("not_found", $"The folder '{givenDir}' does not exist.");
        // Write to the real folder (every link resolved); it is checked again right before writing.
        var dir = RealFolder(givenDir);
        var full = Path.Combine(dir, name);
        var sidecarPath = AnimationExporter.SidecarPathFor(full);
        foreach (var target in sidecar ? new[] { full, sidecarPath } : new[] { full })
        {
            if (Directory.Exists(target)) throw new CommandException("file_exists", $"'{target}' is a folder.");
            if (!overwrite && (File.Exists(target) || new FileInfo(target).LinkTarget is not null))
                throw new CommandException("file_exists", $"'{target}' already exists; pass overwrite=true to replace it.");
        }

        var options = new ExportOptions { StartFrame = startFrame, EndFrame = endFrame, RootMotion = rootMotion, Character = character.Id };
        ExportResult result;
        try { result = AnimationExporter.Export(p, clip, rig, options, name); }
        catch (ExportException e) { throw ExportCommands.Map(e); }

        // Baking can take a while: the folder must still be the one that was checked (no link swapped in).
        if (!string.Equals(RealFolder(givenDir), dir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new CommandException("path_changed", $"The folder '{givenDir}' changed while exporting (it no longer resolves to '{dir}'). Nothing was written.");

        var glbExisted = File.Exists(full);
        Write(full, result.Glb, verify: tmp => GlbContainer.Parse(File.ReadAllBytes(tmp)));
        if (sidecar)
        {
            try { Write(sidecarPath, JsonSerializer.SerializeToUtf8Bytes(result.Sidecar, Indented), verify: null); }
            catch
            {
                // Do not leave half an export behind: remove the GLB, but only if this export created it.
                if (!glbExisted) { try { File.Delete(full); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                throw;
            }
        }

        // The written file is re-read and checked against the bake it was made from (no second bake),
        // plus an independent FK check against the app's evaluation.
        var report = validate ? ExportValidator.Validate(full, p, clip, rig, options, result.Baked) : null;
        if (report is not null && sidecar)
        {
            report.Issues.AddRange(SidecarValidator.Check(result.Sidecar, p, clip, rig, character.Id));
            report.SidecarChecked = true;
        }
        var warnings = result.Warnings.Select(w => new Diagnostic("export", w)).ToList();
        if (report is { Ok: false })
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
            ["validation"] = report?.ToJson(5),
        }, warnings);
    }

    static string RealFolder(string dir)
    {
        try { return FilePaths.Canonicalize(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CommandException("invalid_path", e.Message); }
    }

    void Write(string target, byte[] bytes, Action<string>? verify)
    {
        try { AtomicFile.Write(target, s => s.Write(bytes), verify, keepBackup: false, overwrite: overwrite); }
        catch (FileAlreadyExistsException)
        {
            throw new CommandException("file_exists", $"'{target}' was created by someone else while exporting; it was not replaced.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ExportException)
        {
            throw new CommandException("io_error", $"Could not write the export: {e.Message}");
        }
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
        var (clip, character, rig) = ExportCommands.ResolveWith(p, clipId, characterId);
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new CommandException("not_found", $"File not found: {full}");
        var info = new FileInfo(full);
        if (info.Length == 0) throw new CommandException("not_a_file", "The file is empty or not a regular file.");
        if (info.Length > RigCommands.MaxImportBytes) throw new CommandException("too_large", "The file is larger than 256 MB.");
        var report = ExportValidator.Validate(full, p, clip, rig, character: character.Id);
        var sidecarPath = AnimationExporter.SidecarPathFor(full);
        if (File.Exists(sidecarPath))
        {
            report.Issues.AddRange(SidecarValidator.CheckFile(sidecarPath, p, clip, rig, character.Id));
            report.SidecarChecked = true;
        }
        var data = report.ToJson(50);
        data["path"] = full;
        return new(Array.Empty<Change>(), data);
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}
