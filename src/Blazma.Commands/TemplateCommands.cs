using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.AI;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging.Retargeting;

namespace Blazma.Commands;

/// <summary>Phase 8: clip templates — built-in (generated procedurally, retargeted) and the project's own library.</summary>
public static class TemplateCommands
{
    public static void Register(CommandRegistry r)
    {
        r.Register(new("template.list", "List clip templates: built-in ones (idle, walk/run cycles, jump, wave, attack swing) and the project's own (saved with template.save_clip).",
            CommandEffect.ReadOnly, S.Of(), _ => new ListTemplates()));

        r.Register(new("template.apply",
            "Create a NEW normal clip (not a draft; undoable) from a template on any humanoid-mapped character: built-in templates are generated and retargeted to its proportions; " +
            "project templates are retargeted from the rig they were saved with (feet locked on contacts). Returns clipId.",
            CommandEffect.Mutating,
            S.Of(S.Str("templateId", "Template id from template.list (e.g. builtin.walk_cycle).", true), S.Str("characterId", "Character to animate.", true),
                 S.Num("durationSeconds", "Length in seconds (default: the template's).", false), S.Str("name", "Clip name (default: the template name).", false),
                 S.Int("fps", "Frame rate for built-in templates (default: the project frame rate).", false, 1)),
            a => new ApplyTemplate(S.Text(a, "templateId"), S.Id(a, "characterId"), S.OptNumber(a, "durationSeconds"), S.OptName(a, "name"), Args.OptLong(a, "fps"))));

        r.Register(new("template.save_clip", "Save a clip as a template in the project's library (with a snapshot of its rig, so it can be applied to other characters later). Returns templateId.",
            CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("name", "Template name.", true), S.Str("description", "Short description.", false),
                 S.Str("characterId", "Character whose rig the clip animates (needed only when the clip is not bound to a rig yet).", false)),
            a => new SaveClipTemplate(S.Id(a, "clipId"), S.Name(a, "name"), ((string?)a["description"])?.Trim() ?? "", S.OptId(a, "characterId"))));

        r.Register(new("template.delete", "Delete a project template (undoable). Built-in templates cannot be deleted.", CommandEffect.Destructive,
            S.Of(S.Str("templateId", "Template id.", true)), a => new DeleteTemplate(S.Text(a, "templateId"))));
    }

    internal static ClipTemplate RequireProject(Project p, string id) =>
        (ObjectId.TryParse(id, out var oid) ? p.FindTemplate(oid) : null)
        ?? throw new CommandException("not_found", $"Template '{id}' does not exist{(id.StartsWith(BuiltinTemplates.Prefix, StringComparison.Ordinal) ? " (built-in templates cannot be changed)" : "")}.");

    internal static Change Changed(string id) => new(Change.TemplateChanged, id);
}

public sealed class ListTemplates : ICommand
{
    public string Id => "template.list";
    public CommandEffect Effect => CommandEffect.ReadOnly;
    public string Label => "List templates";

    public CommandOutcome Execute(Project p)
    {
        var list = new JsonArray();
        foreach (var t in BuiltinTemplates.All)
            list.Add(new JsonObject
            {
                ["templateId"] = t.Id, ["name"] = t.Name, ["description"] = t.Description, ["kind"] = "builtin", ["category"] = t.Category,
                ["defaultSeconds"] = t.DefaultSeconds, ["loop"] = t.Loop,
            });
        foreach (var t in p.Templates)
            list.Add(new JsonObject
            {
                ["templateId"] = t.Id.ToString(), ["name"] = t.Name, ["description"] = t.Description, ["kind"] = "project", ["category"] = "user",
                ["defaultSeconds"] = Math.Round(t.Clip.Duration.ToSeconds(), 4), ["loop"] = t.Clip.Loop.Enabled, ["rig"] = t.Rig.Name,
            });
        return new CommandOutcome(Array.Empty<Change>(), new JsonObject { ["templates"] = list });
    }

    public IReadOnlyList<Change> Undo(Project p) => Array.Empty<Change>();
}

public sealed class ApplyTemplate(string templateId, ObjectId characterId, double? seconds, string? name, long? fps) : AddClipCommand
{
    public override string Id => "template.apply";
    public override string Label => "Apply template";

    protected override Clip Build(Project p, List<Diagnostic> warnings, out JsonObject data)
    {
        var (character, rig) = PoseCommands.Resolve(p, characterId);
        if (seconds is { } s && (s < MotionRequest.MinDurationSeconds || s > MotionRequest.MaxDurationSeconds))
            throw new CommandException("invalid_args", $"'durationSeconds' must be between {MotionRequest.MinDurationSeconds} and {MotionRequest.MaxDurationSeconds}.");
        if (fps is > 240) throw new CommandException("invalid_args", "'fps' must be between 1 and 240.");
        data = new JsonObject { ["templateId"] = templateId, ["characterId"] = characterId.ToString() };
        if (BuiltinTemplates.Find(templateId) is { } builtin)
        {
            var rate = fps is long f ? new FrameRate((int)f) : p.Settings.DefaultFrameRate;
            RetargetResult result;
            try { result = BuiltinTemplates.Apply(builtin, rig, rate, seconds, NewClipId, name ?? builtin.Name); }
            catch (MotionGenerationException e) { throw new CommandException(e.Code, e.Message); }
            warnings.AddRange(result.Warnings.Where(w => !w.Contains("not AI", StringComparison.Ordinal)).Select(w => new Diagnostic("template", w)));
            data["kind"] = "builtin";
            data["scale"] = PhysicsCommands.R(result.Scale);
            data["pinnedFrames"] = result.PinnedFrames;
            data["unmappedBones"] = new JsonArray(result.UnmappedBones.Select(b => (JsonNode?)b).ToArray());
            return result.Clip;
        }

        var template = TemplateCommands.RequireProject(p, templateId);
        var duration = template.Clip.Duration.ToSeconds();
        var timeScale = seconds is { } want && duration > 0 ? want / duration : 1;
        ClipRetargetResult r;
        try
        {
            r = ClipRetargeter.Retarget(template.Clip, template.Rig, rig, new ClipRetargetOptions
            {
                Name = name ?? template.Name, TimeScale = timeScale,
            });
        }
        catch (RetargetException e) { throw RetargetCommands.Map(e); }
        warnings.AddRange(r.Warnings.Select(w => new Diagnostic("template", w)));
        var clip = r.Clip;
        clip.Metadata["template"] = template.Id.ToString();
        clip.Metadata["character"] = character.Name;
        if (timeScale != 1) clip.Metadata["durationSeconds"] = (duration * timeScale).ToString(CultureInfo.InvariantCulture);
        data["kind"] = "project";
        data["scale"] = PhysicsCommands.R(r.Scale);
        data["unmappedBones"] = new JsonArray(r.UnmappedBones.Select(b => (JsonNode?)b).ToArray());
        data["footLock"] = RetargetClip.LockSourceContacts(template.Clip, template.Rig, clip, rig, timeScale, warnings);
        return clip;
    }
}

public sealed class SaveClipTemplate(ObjectId clipId, string name, string description, ObjectId? characterId) : SceneEdit
{
    readonly ObjectId _id = ObjectId.New();
    public override string Id => "template.save_clip";
    public override string Label => "Save clip as template";
    protected override IReadOnlyList<Change> Changes => new[] { TemplateCommands.Changed(_id.ToString()) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        var clip = AnimationEditCommands.RequireClip(p, clipId);
        Rig rig;
        if (!clip.Skeleton.IsEmpty)
        {
            rig = p.FindRig(clip.Skeleton) ?? throw new CommandException("not_found", $"The rig of clip '{clip.Name}' no longer exists.");
            if (characterId is { } cid && PoseCommands.Resolve(p, cid).Item2.Id != rig.Id)
                throw new CommandException("rig_mismatch", $"Clip '{clip.Name}' animates another rig than that character.");
        }
        else if (characterId is { } cid) rig = PoseCommands.Resolve(p, cid).Item2;
        else throw new CommandException("invalid_args", $"Clip '{clip.Name}' is not bound to a rig yet; pass 'characterId'.");
        if (rig.FindByStandardName(Rigging.HumanoidStandard.Pelvis) is null)
            throw new CommandException("not_humanoid", $"Rig '{rig.Name}' has no bone mapped to 'pelvis'; templates are retargeted through the humanoid map (rig.map_bone).");
        if (clip.IsDraft) warnings.Add(new Diagnostic("draft", "The clip is a draft; the template keeps its current motion."));
        var copy = ClipEdit.CloneClip(clip);
        copy.Id = ObjectId.New();
        copy.Skeleton = rig.Id;
        copy.IsDraft = false;
        p.Templates.Add(new ClipTemplate { Id = _id, Name = name, Description = description, Clip = copy, Rig = SceneEdit.Clone(rig) });
        return new JsonObject { ["templateId"] = _id.ToString(), ["name"] = name };
    }
}

public sealed class DeleteTemplate(string templateId) : SceneEdit
{
    public override string Id => "template.delete";
    public override CommandEffect Effect => CommandEffect.Destructive;
    public override string Label => "Delete template";
    protected override IReadOnlyList<Change> Changes => new[] { TemplateCommands.Changed(templateId) };

    protected override JsonNode? Apply(Project p, List<Diagnostic> warnings)
    {
        p.Templates.Remove(TemplateCommands.RequireProject(p, templateId));
        return null;
    }
}
