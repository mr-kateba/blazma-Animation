using System.Globalization;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Core.Timing;

namespace Blazma.AI;

/// <param name="Id">Stable id ("builtin.walk_cycle").</param>
/// <param name="Prompt">Keyword given to the procedural generator.</param>
/// <param name="DefaultSeconds">Length when the caller does not ask for one.</param>
/// <param name="Loop">Cycle templates loop seamlessly (whole cycles; the clip's loop range ends on the frame equal to frame 0).</param>
public sealed record BuiltinTemplate(string Id, string Name, string Description, string Category, string Prompt, double DefaultSeconds, bool Loop);

/// <summary>
/// Built-in clip templates (docs/ARCHITECTURE.md §8.5): idle breathing, walk and run cycles, jump, wave and an attack swing.
/// They are generated on demand by <see cref="ProceduralMotionProvider"/> (deterministic, seed 1, not AI) on the SOMA skeleton
/// and retargeted onto any humanoid-mapped rig with <see cref="MotionRetargeter"/> (foot cleanup on), so the same template gives
/// the same motion on the mannequin and on an imported character, scaled to its proportions. The result is a normal clip
/// (not a draft).
/// </summary>
public static class BuiltinTemplates
{
    public const string Prefix = "builtin.";

    public static readonly IReadOnlyList<BuiltinTemplate> All = new[]
    {
        new BuiltinTemplate(Prefix + "idle_breathe", "Idle (breathing)", "Standing idle with breathing and a slight sway; loops.", "idle", "idle", 4, true),
        new BuiltinTemplate(Prefix + "walk_cycle", "Walk cycle", "Walking forward at about 1.3 m/s; loops (root travels).", "locomotion", "walk", 2.2, true),
        new BuiltinTemplate(Prefix + "run_cycle", "Run cycle", "Running forward with a short flight phase; loops (root travels).", "locomotion", "run", 1.5, true),
        new BuiltinTemplate(Prefix + "jump", "Jump", "Crouch, take off, land and recover in place.", "action", "jump", 1.6, false),
        new BuiltinTemplate(Prefix + "wave", "Wave", "Raise the right hand and wave.", "gesture", "wave", 2.5, false),
        new BuiltinTemplate(Prefix + "attack_swing", "Attack swing", "One-handed overhead swing with the right hand: wind-up, strike, follow-through, recovery.", "combat", "attack", 1.2, false),
    };

    public static BuiltinTemplate? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>Generates the template on <paramref name="rig"/>. Throws <see cref="MotionGenerationException"/> (not_humanoid, invalid_request).</summary>
    public static RetargetResult Apply(BuiltinTemplate template, Rig rig, FrameRate fps, double? seconds, ObjectId clipId, string name)
    {
        var duration = seconds ?? template.DefaultSeconds;
        var request = new MotionRequest(template.Prompt, duration, fps.FramesPerSecond, 1, template.Loop ? new MotionConstraints { Loop = true } : null);
        var motion = new ProceduralMotionProvider().GenerateAsync(request, null, CancellationToken.None).GetAwaiter().GetResult();
        var result = MotionRetargeter.Retarget(motion, rig, new RetargetOptions
        {
            Fps = fps, Name = name, ClipId = clipId, FootCleanup = true, ReduceKeys = true, Cyclic = template.Loop,
            Metadata = new Dictionary<string, string>
            {
                ["template"] = template.Id,
                ["durationSeconds"] = duration.ToString(CultureInfo.InvariantCulture),
            },
        });
        var clip = result.Clip;
        clip.IsDraft = false;
        if (template.Loop)
        {
            // Keys run 0..n−1 and frame n−1 repeats frame 0's pose: loop over [0, n−1) so playback wraps without a hitch.
            var last = clip.DurationFrames - 1;
            clip.Loop = new LoopSettings { Enabled = true, Start = Flicks.Zero, End = Flicks.FromFrame(last, clip.Fps) };
        }
        return result;
    }
}
