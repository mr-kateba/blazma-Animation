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

    /// <summary>Index of the attachment bone in <paramref name="rig"/>, or −1 when the prop is free or the reference is stale.</summary>
    public static int AttachedBone(Project project, Prop prop, Rig? rig)
    {
        if (prop.Attachment is not { } a || rig is null) return -1;
        var character = project.FindCharacter(a.Character);
        if (character is null || character.Rig != rig.Id) return -1;
        return rig.IndexOf(a.Bone);
    }

    /// <summary>World transform of a prop given its value and the model transforms of the rig it may be attached to.</summary>
    public static Transform World(Project project, Prop prop, Transform local, Rig? rig, Transform[]? model)
    {
        var bone = AttachedBone(project, prop, rig);
        if (bone >= 0 && model is not null && bone < model.Length) return model[bone] * local;
        return prop.Attachment is null ? local : prop.Transform;
    }

    /// <summary>World transform at a clip time, evaluating the clip on <paramref name="rig"/> (rest base, as playback/export).</summary>
    public static Transform WorldAt(Project project, Prop prop, Clip? clip, Rig? rig, Flicks time)
    {
        Transform[]? model = null;
        if (rig is not null && AttachedBone(project, prop, rig) >= 0)
            model = new RigEvaluator(rig).Model(clip is null ? new Pose() : ClipEvaluator.Evaluate(clip, rig, time));
        return World(project, prop, Local(prop, clip, time), rig, model);
    }

    /// <summary>Props a clip shows with a character of <paramref name="rig"/>: props attached to a character of that rig, and
    /// props the clip animates.</summary>
    public static IReadOnlyList<Prop> PropsFor(Project project, Clip clip, Rig? rig) =>
        project.Props.Where(p => (rig is not null && AttachedBone(project, p, rig) >= 0)
            || clip.Tracks.Exists(t => t.Target.Kind == TargetKind.Prop && t.Target.Id == p.Id)).ToList();
}
