using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Rigging;

namespace Blazma.Motion;

/// <summary>
/// Evaluates props (docs/DATA_FORMAT.md §9). A prop's animated value is its offset relative to the attachment bone when it
/// is attached, else its world transform. Clip tracks with target kind Prop override the prop's working value per channel;
/// the world transform of an attached prop is <c>boneModel · offset</c> (model space = world space; a character has no
/// separate placement). An attachment whose character or bone no longer exists falls back to the free (world) transform.
/// </summary>
public static class PropEvaluator
{
    /// <summary>The prop's animated value at <paramref name="time"/>: clip tracks where present, else the working value.</summary>
    public static Transform Local(Prop prop, Clip? clip, Flicks time)
    {
        var t = prop.Local;
        if (clip is null) return t;
        foreach (var track in clip.Tracks)
        {
            if (track.Muted || track.Target.Kind != TargetKind.Prop || track.Target.Id != prop.Id) continue;
            if (TrackSampler.Sample(track, time) is not { } v) continue;
            t = track.Channel switch
            {
                TrackChannels.Translation => t with { Translation = new Vec3(v[0], v[1], v[2]) },
                TrackChannels.Rotation => t with { Rotation = new Quat(v[0], v[1], v[2], v[3]).Normalized() },
                TrackChannels.Scale => t with { Scale = new Vec3(v[0], v[1], v[2]) },
                _ => t,
            };
        }
        return t;
    }

    /// <summary>
    /// Index of the attachment bone in <paramref name="rig"/> when <paramref name="rig"/> is the rig of the prop's own character
    /// (the one it is attached to), or −1 when the prop is free or the reference is stale. Use the overload with a character
    /// when the rig may be shared by several characters (two mannequins share one rig): a prop follows only its own character.
    /// </summary>
    public static int AttachedBone(Project project, Prop prop, Rig? rig) =>
        prop.Attachment is { } a ? AttachedBone(project, prop, rig, a.Character) : -1;

    /// <summary>Index of the attachment bone when the prop is attached to <paramref name="character"/> and that character uses
    /// <paramref name="rig"/>; else −1 (free, stale, or attached to another character — even one sharing the rig).</summary>
    public static int AttachedBone(Project project, Prop prop, Rig? rig, ObjectId character)
    {
        if (prop.Attachment is not { } a || rig is null || a.Character != character) return -1;
        var c = project.FindCharacter(character);
        if (c is null || c.Rig != rig.Id) return -1;
        return rig.IndexOf(a.Bone);
    }

    /// <summary>World transform of a prop given its value and the model transforms of its own character (the one it is
    /// attached to), whose rig is <paramref name="rig"/>.</summary>
    public static Transform World(Project project, Prop prop, Transform local, Rig? rig, Transform[]? model) =>
        World(project, prop, local, rig, model, prop.Attachment?.Character ?? default);

    /// <summary>World transform of a prop when <paramref name="character"/> (with <paramref name="rig"/>, posed as
    /// <paramref name="model"/>) is shown: follows the bone when attached to that character, else the free transform.</summary>
    public static Transform World(Project project, Prop prop, Transform local, Rig? rig, Transform[]? model, ObjectId character)
    {
        var bone = AttachedBone(project, prop, rig, character);
        if (bone >= 0 && model is not null && bone < model.Length) return model[bone] * local;
        return prop.Attachment is null ? local : prop.Transform;
    }

    /// <summary>World transform at a clip time, evaluating the clip on <paramref name="rig"/> (rest base, as playback/export),
    /// as shown with the prop's own character.</summary>
    public static Transform WorldAt(Project project, Prop prop, Clip? clip, Rig? rig, Flicks time) =>
        WorldAt(project, prop, clip, rig, time, prop.Attachment?.Character ?? default);

    /// <summary>World transform at a clip time when the clip plays on <paramref name="character"/> (rig <paramref name="rig"/>).</summary>
    public static Transform WorldAt(Project project, Prop prop, Clip? clip, Rig? rig, Flicks time, ObjectId character)
    {
        Transform[]? model = null;
        if (rig is not null && AttachedBone(project, prop, rig, character) >= 0)
            model = new RigEvaluator(rig).Model(clip is null ? new Pose() : ClipEvaluator.Evaluate(clip, rig, time));
        return World(project, prop, Local(prop, clip, time), rig, model, character);
    }

    /// <summary>Props a clip shows with the first character of <paramref name="rig"/> (as export picks by default).</summary>
    public static IReadOnlyList<Prop> PropsFor(Project project, Clip clip, Rig? rig) =>
        PropsFor(project, clip, rig, rig is null ? default : project.Characters.Find(c => c.Rig == rig.Id)?.Id ?? default);

    /// <summary>Props a clip shows when it plays on <paramref name="character"/>: props attached to that character (not to
    /// another character sharing its rig) and props the clip animates.</summary>
    public static IReadOnlyList<Prop> PropsFor(Project project, Clip clip, Rig? rig, ObjectId character) =>
        project.Props.Where(p => (rig is not null && AttachedBone(project, p, rig, character) >= 0)
            || clip.Tracks.Exists(t => t.Target.Kind == TargetKind.Prop && t.Target.Id == p.Id)).ToList();

    /// <summary>True when <paramref name="clip"/> has an unmuted track for <paramref name="prop"/>.</summary>
    public static bool Animates(Clip clip, Prop prop) =>
        clip.Tracks.Exists(t => !t.Muted && t.Target.Kind == TargetKind.Prop && t.Target.Id == prop.Id);
}
