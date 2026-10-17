using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;

namespace Blazma.Export;

/// <summary>Root motion taken out of (or, for Keep, present in) the baked animation.</summary>
public sealed class RootMotionInfo
{
    public RootMotionMode Mode { get; init; }

    /// <summary>Index into <see cref="Rig.Bones"/> of the root-motion bone (the bone mapped to "root", else the first bone).</summary>
    public int BoneIndex { get; init; }
    public string BoneName { get; init; } = "";

    /// <summary>Per exported frame: change of the extracted translation since the previous frame (frame 0: zero).
    /// For Keep it is the horizontal travel an engine could extract; nothing was removed.</summary>
    public Vec3[] Deltas { get; init; } = Array.Empty<Vec3>();

    /// <summary>Sum of <see cref="Deltas"/>: last frame minus first frame.</summary>
    public Vec3 Total { get; init; }

    public bool Removed => Mode != RootMotionMode.Keep;
}

/// <summary>A clip sampled once per frame into local bone transforms (what the file stores).</summary>
public sealed class BakedClip
{
    public required Rig Rig { get; init; }
    public required Clip Clip { get; init; }
    public FrameRate Fps => Clip.Fps;
    public long StartFrame { get; init; }
    public long EndFrame { get; init; }
    public int FrameCount => (int)(EndFrame - StartFrame + 1);

    /// <summary>[frame - StartFrame][bone index] local transform (relative to the parent bone).</summary>
    public required Transform[][] Locals { get; init; }
    public required RootMotionInfo RootMotion { get; init; }

    /// <summary>File time of the i-th baked frame, seconds from the first exported frame.</summary>
    public double SecondsAt(int i) => i * (double)Fps.Denominator / Fps.Numerator;
    public double DurationSeconds => SecondsAt(FrameCount - 1);
}

/// <summary>
/// Samples a clip on its rig every frame, exactly like playback: evaluated from the rest pose
/// (not the character's working pose), pinned IK re-solved, looping clips wrapped through
/// <see cref="ClipEvaluator.PlaybackTime"/> (so the frame at the clip's end repeats the loop start).
/// </summary>
public static class AnimationBaker
{
    public const long MaxFrames = 100_000;

    public static BakedClip Bake(Clip clip, Rig rig, ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        if (rig.Bones.Count == 0) throw new ExportException("empty_rig", $"Rig '{rig.Name}' has no bones.");
        var last = clip.DurationFrames;
        var start = options.StartFrame ?? 0;
        var end = options.EndFrame ?? last;
        if (start < 0 || end < start || end > last)
            throw new ExportException("invalid_range", $"Frame range {start}..{end} is outside the clip (0..{last}).");
        if (end - start + 1 > MaxFrames)
            throw new ExportException("too_large", $"At most {MaxFrames} frames can be exported at once.");

        var ev = new RigEvaluator(rig);
        var frames = new Transform[end - start + 1][];
        for (var f = start; f <= end; f++)
        {
            var time = Flicks.FromFrame(f, clip.Fps);
            if (clip.Loop.Enabled) time = ClipEvaluator.PlaybackTime(clip, time);
            frames[f - start] = ev.Locals(ClipEvaluator.Evaluate(clip, rig, time));
        }

        var rootIndex = RootMotionBone(rig);
        var rm = ExtractRootMotion(frames, rootIndex, options.RootMotion);
        return new BakedClip
        {
            Rig = rig, Clip = clip, StartFrame = start, EndFrame = end, Locals = frames,
            RootMotion = new RootMotionInfo
            {
                Mode = options.RootMotion, BoneIndex = rootIndex, BoneName = rig.Bones[rootIndex].Name,
                Deltas = rm.Deltas, Total = rm.Total,
            },
        };
    }

    /// <summary>The bone mapped to the standard "root", else the first bone.</summary>
    public static int RootMotionBone(Rig rig)
    {
        var mapped = rig.FindByStandardName(HumanoidStandard.Root);
        var i = mapped is null ? -1 : rig.IndexOf(mapped.Id);
        return i < 0 ? 0 : i;
    }

    static (Vec3[] Deltas, Vec3 Total) ExtractRootMotion(Transform[][] frames, int bone, RootMotionMode mode)
    {
        var origin = frames[0][bone].Translation;
        var deltas = new Vec3[frames.Length];
        var previous = Vec3.Zero;
        for (var i = 0; i < frames.Length; i++)
        {
            var t = frames[i][bone].Translation;
            var d = t - origin;
            var extracted = mode == RootMotionMode.InPlaceAll ? d : new Vec3(d.X, 0, d.Z);
            deltas[i] = extracted - previous;
            previous = extracted;
            if (mode != RootMotionMode.Keep)
                frames[i][bone] = frames[i][bone] with { Translation = t - extracted };
        }
        return (deltas, previous);
    }
}
