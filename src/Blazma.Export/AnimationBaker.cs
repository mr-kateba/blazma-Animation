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

    /// <summary>Per exported frame: change of the extracted model-space translation since the previous frame
    /// (frame 0: zero). For Keep it is the horizontal travel an engine could extract; nothing was removed.</summary>
    public Vec3[] Deltas { get; init; } = Array.Empty<Vec3>();

    /// <summary>Sum of <see cref="Deltas"/>: last frame minus first frame (looping clips include the travel of every repeated cycle).</summary>
    public Vec3 Total { get; init; }

    /// <summary>Model-space travel of the root-motion bone over one loop cycle (zero for clips that do not loop).
    /// Frames after a wrap carry it once per completed cycle, so a looping walk keeps moving forward.</summary>
    public Vec3 CycleDisplacement { get; init; }

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
/// <see cref="ClipEvaluator.PlaybackTime"/> (so the frame at the loop end repeats the loop start's pose),
/// with the root-motion bone carried forward by one cycle of travel per wrap.
/// </summary>
public static class AnimationBaker
{
    public const long MaxFrames = 100_000;

    /// <summary>Upper bound of frames × bones per export (memory and time; applies to every caller).</summary>
    public const long MaxSamples = 2_000_000;

    /// <summary>Default export range: the whole clip (0..duration), or for a looping clip whose loop is a
    /// sub-range of the clip, the loop range (loop start..loop end), so the file loops cleanly.</summary>
    public static (long Start, long End) DefaultRange(Clip clip)
    {
        if (clip.Loop.Enabled && LoopRange(clip) is var (ls, le) && (ls.Value > 0 || le < clip.Duration))
            return (ls.ToFrame(clip.Fps), le.ToFrame(clip.Fps));
        return (0, clip.DurationFrames);
    }

    /// <summary>The effective loop range, as <see cref="ClipEvaluator.PlaybackTime"/> uses it.</summary>
    public static (Flicks Start, Flicks End) LoopRange(Clip clip)
    {
        var end = clip.Loop.End.Value > 0 && clip.Loop.End < clip.Duration ? clip.Loop.End : clip.Duration;
        var start = clip.Loop.Start < end ? clip.Loop.Start : Flicks.Zero;
        return (start, end);
    }

    /// <summary>Completed loop cycles at <paramref name="elapsed"/> (0 for clips that do not loop).</summary>
    public static long CyclesAt(Clip clip, Flicks elapsed)
    {
        if (!clip.Loop.Enabled || elapsed.Value < 0) return 0;
        var (start, end) = LoopRange(clip);
        var length = (end - start).Value;
        if (elapsed < start || length <= 0) return 0;
        return (elapsed.Value - start.Value) / length;
    }

    public static BakedClip Bake(Clip clip, Rig rig, ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        if (rig.Bones.Count == 0) throw new ExportException("empty_rig", $"Rig '{rig.Name}' has no bones.");
        var last = clip.DurationFrames;
        var (defaultStart, defaultEnd) = DefaultRange(clip);
        var start = options.StartFrame ?? defaultStart;
        var end = options.EndFrame ?? (options.StartFrame is null || start <= defaultEnd ? defaultEnd : last);
        if (start < 0 || end < start || end > last)
            throw new ExportException("invalid_range", $"Frame range {start}..{end} is outside the clip (0..{last}).");
        if (end - start + 1 > MaxFrames)
            throw new ExportException("too_large", $"At most {MaxFrames} frames can be exported at once.");
        if ((end - start + 1) * rig.Bones.Count > MaxSamples)
            throw new ExportException("too_large",
                $"{end - start + 1} frames × {rig.Bones.Count} bones is more than {MaxSamples:N0} bone samples; export a shorter frame range (at most {MaxSamples / rig.Bones.Count} frames for this rig).");

        var ev = new RigEvaluator(rig);
        var rootIndex = RootMotionBone(rig);
        var parent = ev.ParentOf(rootIndex);

        // One loop cycle of root travel, in model space: loop end (evaluated, not wrapped) minus loop start.
        var cycle = Vec3.Zero;
        if (clip.Loop.Enabled)
        {
            var (ls, le) = LoopRange(clip);
            cycle = ev.Model(ClipEvaluator.Evaluate(clip, rig, le))[rootIndex].Translation
                  - ev.Model(ClipEvaluator.Evaluate(clip, rig, ls))[rootIndex].Translation;
        }

        var frames = new Transform[end - start + 1][];
        var parentModels = new Transform[frames.Length];
        var rootModels = new Vec3[frames.Length];
        for (var f = start; f <= end; f++)
        {
            var elapsed = Flicks.FromFrame(f, clip.Fps);
            var time = clip.Loop.Enabled ? ClipEvaluator.PlaybackTime(clip, elapsed) : elapsed;
            var locals = ev.Locals(ClipEvaluator.Evaluate(clip, rig, time));
            var model = ev.Model(locals);
            var i = f - start;
            parentModels[i] = parent < 0 ? Transform.Identity : model[parent];
            var root = model[rootIndex];
            var cycles = CyclesAt(clip, elapsed);
            if (cycles > 0)
            {
                root = root with { Translation = root.Translation + cycle * cycles };
                locals[rootIndex] = parent < 0 ? root : RigEvaluator.LocalFromModel(parentModels[i], root);
            }
            rootModels[i] = root.Translation;
            frames[i] = locals;
        }

        var rm = ExtractRootMotion(frames, parentModels, rootModels, rootIndex, parent, options.RootMotion);
        return new BakedClip
        {
            Rig = rig, Clip = clip, StartFrame = start, EndFrame = end, Locals = frames,
            RootMotion = new RootMotionInfo
            {
                Mode = options.RootMotion, BoneIndex = rootIndex, BoneName = rig.Bones[rootIndex].Name,
                Deltas = rm.Deltas, Total = rm.Total, CycleDisplacement = cycle,
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

    /// <summary>Model-space travel of the root-motion bone relative to the first exported frame that a
    /// mode removes: X/Z for InPlace (model +Y is up), X/Y/Z for InPlaceAll.</summary>
    public static Vec3 Extracted(Vec3 travel, RootMotionMode mode) =>
        mode == RootMotionMode.InPlaceAll ? travel : new Vec3(travel.X, 0, travel.Z);

    static (Vec3[] Deltas, Vec3 Total) ExtractRootMotion(Transform[][] frames, Transform[] parentModels, Vec3[] rootModels,
        int bone, int parent, RootMotionMode mode)
    {
        var origin = rootModels[0];
        var deltas = new Vec3[frames.Length];
        var previous = Vec3.Zero;
        for (var i = 0; i < frames.Length; i++)
        {
            var extracted = Extracted(rootModels[i] - origin, mode);
            deltas[i] = extracted - previous;
            previous = extracted;
            if (mode == RootMotionMode.Keep) continue;
            // Remove the travel in model space, then express the bone in its (unchanged) parent's space again.
            var local = frames[i][bone];
            var model = parent < 0 ? local : parentModels[i] * local;
            model = model with { Translation = model.Translation - extracted };
            frames[i][bone] = parent < 0 ? model : RigEvaluator.LocalFromModel(parentModels[i], model);
        }
        return (deltas, previous);
    }
}
