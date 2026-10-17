using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Rigging;

namespace Blazma.Physics;

public sealed record FootLockOptions
{
    /// <summary>A sole point touches when it is at most this high above the ground (m). Same rule as balance analysis.</summary>
    public double ContactHeight { get; init; } = 0.03;

    /// <summary>…and moves slower than this (m/s).</summary>
    public double ContactSpeed { get; init; } = 0.35;

    /// <summary>Shortest contact that is locked (frames).</summary>
    public int MinFrames { get; init; } = 3;

    /// <summary>Contacts separated by at most this many frames are merged (detection flicker).</summary>
    public int MergeGap { get; init; } = 1;

    /// <summary>Frames before and after each contact over which the foot eases onto / off its anchor.</summary>
    public int BlendFrames { get; init; } = 3;
}

/// <param name="Foot">Index in <see cref="FootModel.Feet"/>.</param>
/// <param name="Start">First contact frame.</param>
/// <param name="End">Last contact frame (inclusive).</param>
public sealed record FootContactSpan(int Foot, Side Side, long Start, long End);

/// <summary>What one foot does on one frame of a lock.</summary>
/// <param name="Weight">1 inside the contact, easing to 0 over the blend frames.</param>
/// <param name="Target">Model-space IK target (ankle/foot bone origin and rotation).</param>
/// <param name="Pole">Model-space knee pole from the evaluated knee, or null when the leg is straight.</param>
/// <param name="PoleStored">The pole as the pole control stores it (relative to the first bone), or null.</param>
public sealed record FootLockFrame(int Foot, long Frame, double Weight, Transform Target, Vec3? Pole, Vec3? PoleStored, Vec3 Anchor, bool InContact);

/// <summary>
/// Foot locking (docs/ARCHITECTURE.md §8.2). Contacts come from the same sole model and rules as balance analysis
/// (<see cref="FootModel"/>, <see cref="BalanceAnalyzer"/>): a foot is in contact on a frame when one of its sole points is
/// within <see cref="FootLockOptions.ContactHeight"/> of the ground and slower than <see cref="FootLockOptions.ContactSpeed"/>.
/// Each contact span gets one anchor: the mean model-space position of the foot (ankle) bone over the span. Inside the span
/// the leg IK target is the anchor (with the foot's evaluated rotation); over <see cref="FootLockOptions.BlendFrames"/> on each
/// side it eases (smoothstep) from the evaluated foot position to the anchor and back. The knee pole follows the evaluated
/// knee (pole = knee + (knee − midpoint(hip, ankle))), so the bend plane does not jump. The command keys these as pinned
/// IK targets and poles; frames outside the planned frames keep exactly their original values (KeyBake).
/// </summary>
public static class FootLocker
{
    /// <summary>Contact spans per foot in [start, end] (clamped to the clip).</summary>
    public static IReadOnlyList<FootContactSpan> DetectContacts(Clip clip, Rig rig, FootLockOptions options, long? start = null, long? end = null)
    {
        var an = new BalanceAnalyzer(rig, new BalanceOptions { ContactHeight = options.ContactHeight, ContactSpeed = options.ContactSpeed });
        var report = an.AnalyzeClip(clip, start, end);
        var spans = new List<FootContactSpan>();
        for (var foot = 0; foot < an.Feet.Feet.Count; foot++)
        {
            var side = an.Feet.Feet[foot].Side;
            var frames = report.Frames.Where(b => b.Contacts.Any(c => c.Foot == foot)).Select(b => b.Frame).ToList();
            spans.AddRange(Spans(foot, side, frames, options));
        }
        return spans.OrderBy(s => s.Start).ThenBy(s => s.Foot).ToList();
    }

    /// <summary>Groups contact frames into spans (merging gaps of at most MergeGap frames, dropping spans shorter than MinFrames).</summary>
    public static IEnumerable<FootContactSpan> Spans(int foot, Side side, IReadOnlyList<long> contactFrames, FootLockOptions options)
    {
        var result = new List<FootContactSpan>();
        long? s = null, e = null;
        foreach (var f in contactFrames.OrderBy(x => x))
        {
            if (s is null) { s = e = f; continue; }
            if (f - e!.Value <= options.MergeGap + 1) { e = f; continue; }
            Add();
            s = e = f;
        }
        Add();
        return result;

        void Add()
        {
            if (s is long a && e is long b && b - a + 1 >= options.MinFrames) result.Add(new FootContactSpan(foot, side, a, b));
        }
    }

    /// <summary>
    /// Per frame and foot, the IK target/pole to key for locking <paramref name="spans"/> on <paramref name="clip"/> evaluated
    /// on <paramref name="rig"/>. Frames are limited to [<paramref name="firstFrame"/>, <paramref name="lastFrame"/>]: the ease
    /// onto/off an anchor is fitted inside that range (shortened when the span is close to its edge). A span that touches a
    /// range edge flagged as cut (<paramref name="cutStart"/>/<paramref name="cutEnd"/>: the contact may continue outside the
    /// range) is partial: its anchor is the mean of its visible part and it has no ease at that edge (the foot is already
    /// planted there). Feet without a leg IK chain are skipped (warning). When the blend of one span overlaps another span of
    /// the same foot, the larger weight wins.
    /// </summary>
    public static IReadOnlyList<FootLockFrame> Plan(Clip clip, Rig rig, IReadOnlyList<FootContactSpan> spans, FootLockOptions options, long lastFrame,
        List<string> warnings, long firstFrame = 0, bool cutStart = false, bool cutEnd = false)
    {
        firstFrame = Math.Max(0, firstFrame);
        var ev = new RigEvaluator(rig);
        var feet = FootModel.For(rig, ev);
        var result = new Dictionary<(int, long), FootLockFrame>();
        var cache = new Dictionary<long, (Pose Pose, Transform[] Model)>();
        (Pose, Transform[]) At(long f)
        {
            if (!cache.TryGetValue(f, out var v))
            {
                var pose = Motion.ClipEvaluator.Evaluate(clip, rig, Core.Timing.Flicks.FromFrame(f, clip.Fps));
                v = (pose, ev.Model(pose));
                cache[f] = v;
            }
            return v;
        }
        var b = Math.Max(0, options.BlendFrames);
        foreach (var span in spans)
        {
            if (span.Foot < 0 || span.Foot >= feet.Feet.Count) continue;
            var foot = feet.Feet[span.Foot];
            if (foot.Chain is not { } chain)
            {
                warnings.Add($"The {foot.Side.ToString().ToLowerInvariant()} foot has no leg IK chain; it cannot be locked.");
                continue;
            }
            int iu = ev.IndexOf(chain.Upper), il = ev.IndexOf(chain.Lower), ie = ev.IndexOf(chain.End);
            var s0 = Math.Max(firstFrame, span.Start);
            var s1 = Math.Min(lastFrame, span.End);
            if (s1 < s0) continue;
            var anchor = Vec3.Zero;
            for (var f = s0; f <= s1; f++) anchor += At(f).Item2[ie].Translation;
            anchor /= s1 - s0 + 1;
            // Ease lengths fitted inside the range; none at a cut edge.
            var bIn = cutStart && s0 == firstFrame ? 0 : (int)Math.Min(b, s0 - firstFrame);
            var bOut = cutEnd && s1 == lastFrame ? 0 : (int)Math.Min(b, lastFrame - s1);
            for (var f = s0 - bIn; f <= s1 + bOut; f++)
            {
                double w = f < s0 ? Smooth((double)(f - (s0 - bIn - 1)) / (bIn + 1)) : f > s1 ? Smooth((double)(s1 + bOut + 1 - f) / (bOut + 1)) : 1;
                if (w <= 0) continue;
                var (_, m) = At(f);
                var pos = Vec3.Lerp(m[ie].Translation, anchor, w);
                var target = new Transform(pos, m[ie].Rotation.Normalized(), Vec3.One);
                var hip = m[iu].Translation;
                var knee = m[il].Translation;
                var side = knee - (hip + m[ie].Translation) * 0.5;
                var reach = (knee - hip).Length() + (m[ie].Translation - knee).Length();
                Vec3? pole = side.Length() > 1e-3 * reach ? knee + side.Normalized() * (0.5 * reach) : null;
                Vec3? stored = pole is { } pv && !chain.Pole.IsEmpty && rig.FindControl(chain.Pole) is { } pc
                    ? PoseSolver.ToControlSpace(pc, new Transform(pv, Quat.Identity, Vec3.One), m).Translation : null;
                var key = (span.Foot, f);
                if (result.TryGetValue(key, out var existing) && existing.Weight >= w) continue;
                result[key] = new FootLockFrame(span.Foot, f, w, target, pole, stored, anchor, f >= s0 && f <= s1);
            }
        }
        return result.Values.OrderBy(x => x.Frame).ThenBy(x => x.Foot).ToList();
    }

    static double Smooth(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }
}
