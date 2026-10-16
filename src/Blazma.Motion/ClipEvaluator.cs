using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Rigging;

namespace Blazma.Motion;

/// <summary>
/// Turns a clip into a pose at a given time. Animated channels overwrite the base pose (the
/// character's working pose, or rest); IK targets are animated too, and pinned chains are
/// re-solved afterwards so planted feet stay planted between keys.
/// </summary>
public static class ClipEvaluator
{
    public static Pose Evaluate(Clip clip, Rig rig, Flicks time, Pose? basePose = null)
    {
        var pose = basePose?.Clone() ?? new Pose();
        foreach (var track in clip.Tracks)
        {
            if (track.Muted) continue;
            var value = TrackSampler.Sample(track, time);
            if (value is null) continue;
            switch (track.Target.Kind)
            {
                case TargetKind.Bone when rig.FindBone(track.Target.Id) is { } bone:
                {
                    var key = bone.Id.ToString();
                    var t = pose.LocalOf(bone);
                    pose.Bones[key] = Apply(t, track.Channel, value);
                    break;
                }
                case TargetKind.Control when rig.FindControl(track.Target.Id) is { } control:
                {
                    var state = PoseSolver.StateOf(rig, pose, control.Id).Clone();
                    if (track.Channel == TrackChannels.Pinned) state.Pinned = value[0] >= 0.5;
                    else state.Transform = Apply(state.Transform, track.Channel, value) with { Scale = Vec3.One };
                    pose.Controls[control.Id.ToString()] = state;
                    break;
                }
            }
        }
        PoseSolver.Update(rig, pose);
        return pose;
    }

    static Transform Apply(Transform t, string channel, double[] v) => channel switch
    {
        TrackChannels.Translation => t with { Translation = new Vec3(v[0], v[1], v[2]) },
        TrackChannels.Rotation => t with { Rotation = new Quat(v[0], v[1], v[2], v[3]).Normalized() },
        TrackChannels.Scale => t with { Scale = new Vec3(v[0], v[1], v[2]) },
        _ => t,
    };

    /// <summary>Playback time for <paramref name="elapsed"/> since the start, honouring the loop range.</summary>
    public static Flicks PlaybackTime(Clip clip, Flicks elapsed)
    {
        if (elapsed.Value < 0) return Flicks.Zero;
        if (!clip.Loop.Enabled) return elapsed > clip.Duration ? clip.Duration : elapsed;
        // End = 0 means "clip end"; both ends are clamped to the clip so a stale range cannot freeze playback.
        var end = clip.Loop.End.Value > 0 && clip.Loop.End < clip.Duration ? clip.Loop.End : clip.Duration;
        var start = clip.Loop.Start < end ? clip.Loop.Start : Flicks.Zero;
        if (elapsed < start) return elapsed;
        var length = (end - start).Value;
        if (length <= 0) return start;
        return new Flicks(start.Value + (elapsed.Value - start.Value) % length);
    }

    /// <summary>Model-space positions of a bone sampled every frame between two times (motion trail).</summary>
    public static Vec3[] SampleBonePath(Clip clip, Rig rig, ObjectId boneId, long fromFrame, long toFrame, Pose? basePose = null)
    {
        var ev = new RigEvaluator(rig);
        var index = ev.IndexOf(boneId);
        if (index < 0 || toFrame < fromFrame) return Array.Empty<Vec3>();
        var path = new Vec3[toFrame - fromFrame + 1];
        for (var f = fromFrame; f <= toFrame; f++)
            path[f - fromFrame] = ev.Model(Evaluate(clip, rig, Flicks.FromFrame(f, clip.Fps), basePose))[index].Translation;
        return path;
    }

    /// <summary>Distinct key frames of the clip (optionally only tracks of some targets), sorted.</summary>
    public static IReadOnlyList<long> KeyFrames(Clip clip, Func<Track, bool>? filter = null) =>
        clip.Tracks.Where(t => filter?.Invoke(t) ?? true)
            .SelectMany(t => t.Keys.Select(k => k.Time.ToFrame(clip.Fps)))
            .Distinct().OrderBy(f => f).ToList();
}
