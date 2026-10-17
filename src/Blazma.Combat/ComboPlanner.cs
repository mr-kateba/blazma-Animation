using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Core.Timing;

namespace Blazma.Combat;

/// <param name="StartFrame">Sequence frame where this attack starts (and starts fading in over the previous one).</param>
/// <param name="LinkFrame">Clip frame of this attack where the next one starts.</param>
/// <param name="CancelStartFrame">Cancel window in this attack's clip frames (null = none: the next starts near the end).</param>
/// <param name="EndFrame">Sequence frame where this attack stops counting (the next attack's start, or its own end for the last).</param>
public sealed record PlannedAttack(ComboAttack Attack, Clip Clip, long StartFrame, long BlendFrames, long LinkFrame, long? CancelStartFrame, long? CancelEndFrame,
    long LengthFrames, long EndFrame);

public sealed record ComboPlan(ClipSequence Sequence, IReadOnlyList<PlannedAttack> Attacks, IReadOnlyList<string> Warnings, long LastFrame);

/// <summary>
/// Turns a combo into a clip sequence (docs/ARCHITECTURE.md §9.5): attack i+1 starts at attack i's link frame (default: the start of
/// its cancel window; without a window, its end minus the next blend) and fades in over <c>BlendFrames</c>, with root alignment, so
/// it plays with the Phase 8 sequence machinery (evaluation, bake). Sequence frames use the first attack clip's frame rate; every
/// item plays its clip once at speed 1. Attacks whose clip is missing or animates another rig are skipped with a warning.
/// </summary>
public static class ComboPlanner
{
    public static ComboPlan Plan(Project project, Combo combo, ObjectId? sequenceId = null)
    {
        var character = project.FindCharacter(combo.Character) ?? throw new CombatException("not_found", $"The character of combo '{combo.Name}' no longer exists.");
        var warnings = new List<string>();
        var usable = new List<(ComboAttack Attack, Clip Clip)>();
        foreach (var a in combo.Attacks)
        {
            var clip = project.FindClip(a.Clip);
            if (clip is null) { warnings.Add($"Attack {a.Id}: its clip no longer exists; skipped."); continue; }
            if (!clip.Skeleton.IsEmpty && clip.Skeleton != character.Rig) { warnings.Add($"Attack {a.Id}: clip '{clip.Name}' animates another rig; skipped."); continue; }
            if (clip.DurationFrames < 1) { warnings.Add($"Attack {a.Id}: clip '{clip.Name}' is empty; skipped."); continue; }
            usable.Add((a, clip));
        }
        if (usable.Count == 0) throw new CombatException("empty_combo", $"Combo '{combo.Name}' has no playable attack.");
        var fps = usable[0].Clip.Fps;
        var seq = new ClipSequence
        {
            Id = sequenceId ?? ObjectId.New(), Name = combo.Name, Character = combo.Character, Fps = fps, AlignRoot = true, AlignYaw = true,
        };
        var planned = new List<PlannedAttack>();
        long start = 0;
        for (var i = 0; i < usable.Count; i++)
        {
            var (a, clip) = usable[i];
            var length = clip.DurationFrames;
            var blend = i == 0 ? 0 : Math.Min(a.BlendFrames, ToSeq(length, clip, fps));
            long? cs = a.CancelStart?.ToFrame(clip.Fps), ce = a.CancelEnd?.ToFrame(clip.Fps);
            var nextBlend = i + 1 < usable.Count ? usable[i + 1].Attack.BlendFrames : 0;
            var link = a.Link?.ToFrame(clip.Fps) ?? cs ?? Math.Max(1, length - FromSeq(nextBlend, clip, fps));
            link = Math.Clamp(link, 1, length);
            seq.Items.Add(new SequenceItem
            {
                Clip = clip.Id, StartFrame = start, InFrame = 0, OutFrame = length, Speed = 1, Loops = 1, BlendInFrames = blend, BlendCurve = BlendCurve.Smooth,
            });
            var next = start + ToSeq(link, clip, fps);
            planned.Add(new PlannedAttack(a, clip, start, blend, link, cs, ce, length, i + 1 < usable.Count ? next : start + ToSeq(length, clip, fps)));
            start = next;
        }
        foreach (var item in seq.Items)
            if (!ClipSequence.ItemInLimits(item)) throw new CombatException("too_large", $"Combo '{combo.Name}' is too long for a sequence.");
        return new ComboPlan(seq, planned, warnings, planned[^1].EndFrame);
    }

    /// <summary>Clip frames → sequence frames (rounded).</summary>
    public static long ToSeq(long clipFrames, Clip clip, FrameRate seqFps) =>
        (long)Math.Round(clipFrames * seqFps.FramesPerSecond / clip.Fps.FramesPerSecond);

    static long FromSeq(long seqFrames, Clip clip, FrameRate seqFps) =>
        (long)Math.Round(seqFrames * clip.Fps.FramesPerSecond / seqFps.FramesPerSecond);

    /// <summary>
    /// The attacks' hitboxes on the combined timeline: active ranges shifted to each attack's start and cut where the next attack
    /// starts (a cancelled attack stops hitting). Ids are new; names get the attack's input (or index) as prefix.
    /// </summary>
    public static List<Hitbox> CombinedHitboxes(ComboPlan plan, FrameRate fps, long lastFrame, List<string> warnings)
    {
        var result = new List<Hitbox>();
        for (var i = 0; i < plan.Attacks.Count; i++)
        {
            var at = plan.Attacks[i];
            if (at.Clip.Combat is not { } c) continue;
            var cut = Math.Min(at.EndFrame, lastFrame + 1);
            foreach (var h in c.Hitboxes)
            {
                var ranges = new List<ClipRange>();
                foreach (var r in h.Active)
                {
                    var s = at.StartFrame + ToSeq(r.Start.ToFrame(at.Clip.Fps), at.Clip, fps);
                    var e = Math.Min(cut, at.StartFrame + ToSeq(r.End.ToFrame(at.Clip.Fps), at.Clip, fps));
                    if (e > s) ranges.Add(new ClipRange { Start = Flicks.FromFrame(s, fps), End = Flicks.FromFrame(e, fps) });
                }
                if (ranges.Count == 0) continue;
                if (result.Count >= CombatLimits.MaxHitboxesPerClip)
                {
                    warnings.Add($"More than {CombatLimits.MaxHitboxesPerClip} hitboxes in the combo; the rest were not copied.");
                    return result;
                }
                var prefix = string.IsNullOrEmpty(at.Attack.Input) ? $"#{i + 1}" : at.Attack.Input;
                var name = $"{prefix} {h.Name}";
                result.Add(new Hitbox
                {
                    Name = name.Length > CombatLimits.MaxNameLength ? name[..CombatLimits.MaxNameLength] : name,
                    Shape = h.Shape.Clone(), Damage = h.Damage, Knockback = h.Knockback, HitstopFrames = h.HitstopFrames, Tags = new List<string>(h.Tags),
                    Attachment = new HitboxAttachment { Kind = h.Attachment.Kind, Target = h.Attachment.Target, Offset = h.Attachment.Offset },
                    Active = ranges,
                });
            }
        }
        return result;
    }
}
