using System.Text.Json.Nodes;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Physics;

namespace Blazma.Commands;

/// <summary>motion.physics_polish and the polish step run after motion generation and template application.</summary>
public static class PhysicsPolishCommands
{
    public static void Register(CommandRegistry r)
    {
        r.Register(new("motion.physics_polish",
            "Make a clip physically believable (deterministic post-process, one undo step): feet in contact planted on the ground with leg IK (heel/toe roll kept), " +
            "no body part below the ground and a lying body resting on it, flight phases on a ballistic COM parabola (g = 9.81) with take-off and landing " +
            "(knees bend ≥ minLandingKneeFlex), smooth root velocity across step boundaries (the clip's markers), falls accelerating into the impact with a " +
            "damped settle, spine/head/arm follow-through, and balance while standing. motion.generate and template.apply already run it unless physicsPolish=false. " +
            "Bones and frames it does not need to change keep their exact values. Returns before/after numbers (footSlide, minBodyHeight, flightParabolaRms, " +
            "boundaryVelocityJump, landingKneeFlex, restGap, comJerkRms…) and the stages that changed something.",
            CommandEffect.Mutating,
            S.Of(S.Str("clipId", "Clip id.", true), S.Str("characterId", "Character to evaluate the clip on (default: the first character using the clip's rig).", false),
                 S.Bool("groundContact", "Plant feet in contact (default true).", false),
                 S.Bool("groundClamp", "Keep every body part above the ground; lying bodies rest on it (default true).", false),
                 S.Bool("ballistic", "Flight phases follow a ballistic COM parabola, with landing compression (default true).", false),
                 S.Bool("transitions", "Ease the root velocity across step boundaries (default true).", false),
                 S.Bool("falls", "Falls accelerate into the impact and settle with a small bounce (default true).", false),
                 S.Bool("secondary", "Spine, head and arm follow-through (default true).", false),
                 S.Bool("balance", "Keep the COM inside the support polygon on standing frames (default true).", false),
                 S.Num("minLandingKneeFlex", "Knee flexion (deg) a landing must reach (default 15, 0–90).", false),
                 S.Bool("reduceKeys", "Reduce the rewritten tracks' keys within tolerance (default false: a key on every frame, exact values).", false)),
            a =>
            {
                bool B(string k) => a[k] is null || (bool)a[k]!;
                var knee = S.OptNumber(a, "minLandingKneeFlex") ?? 15;
                if (knee is < 0 or > 90) throw new CommandException("invalid_args", "'minLandingKneeFlex' must be between 0 and 90.");
                var o = new PhysicsPolishOptions
                {
                    GroundContact = B("groundContact"), GroundClamp = B("groundClamp"), Ballistic = B("ballistic"), Transitions = B("transitions"),
                    Falls = B("falls"), Secondary = B("secondary"), Balance = B("balance"), MinLandingKneeFlex = knee,
                };
                return new PolishClip(S.Id(a, "clipId"), S.OptId(a, "characterId"), o, a["reduceKeys"] is not null && (bool)a["reduceKeys"]!);
            }));
    }

    /// <summary>Polishes a freshly made clip in place (its tracks are replaced); adds the result to its metadata and warnings.</summary>
    public static PhysicsPolishResult? PolishNew(Clip clip, Rig rig, bool cyclic, bool reduceKeys, ICollection<string> warnings)
    {
        if (MotionPhysicsMetrics.LastFrame(clip) + 1 > PhysicsPolish.MaxFrames || clip.DurationFrames < 4) return null;
        var polished = PhysicsPolish.Apply(clip, rig, new PhysicsPolishOptions { Cyclic = cyclic }, reduceKeys, out var result);
        clip.Tracks = polished.Tracks;
        clip.Metadata["physics.polish"] = result.Stages.Count == 0 ? "unchanged" : string.Join(",", result.Stages);
        foreach (var w in result.Warnings) if (!warnings.Contains(w)) warnings.Add(w);
        return result;
    }

    public static JsonObject Report(MotionPhysicsReport r)
    {
        static double? F(double d) => double.IsFinite(d) ? PhysicsCommands.R(d) : null;
        return new JsonObject
        {
            ["footSlide"] = F(r.FootSlide), ["minBodyHeight"] = F(r.MinBodyHeight), ["minBodyAt"] = r.MinBodyAt,
            ["flights"] = new JsonArray(r.Flights.Select(f => (JsonNode?)new JsonArray(f.Start, f.End)).ToArray()),
            ["flightParabolaRms"] = F(r.FlightParabolaRms), ["comJerkRms"] = F(r.ComJerkRms), ["comAccelMax"] = F(r.ComAccelMax),
            ["rootVelocityJump"] = F(r.RootVelocityJump), ["boundaryVelocityJump"] = F(r.BoundaryVelocityJump), ["yawAccelMax"] = F(r.YawAccelMax),
            ["standingMarginMin"] = F(r.StandingMarginMin), ["landingKneeFlex"] = new JsonArray(r.LandingKneeFlex.Select(k => (JsonNode?)F(k)).ToArray()),
            ["kneeMin"] = F(r.KneeMin), ["fallImpactRatio"] = r.Fall is null ? null : F(r.FallImpactRatio),
            ["endsLying"] = r.EndsLying, ["restGap"] = r.EndsLying ? F(r.RestGap) : null,
            ["restContacts"] = new JsonArray(r.RestContacts.Select(c => (JsonNode?)c).ToArray()),
        };
    }
}

public sealed class PolishClip(ObjectId clipId, ObjectId? characterId, PhysicsPolishOptions options, bool reduceKeys) : ClipEdit(clipId)
{
    public override string Id => "motion.physics_polish";
    public override string Label => "Physics polish";

    protected override JsonNode? Apply(Project p, Clip clip, List<Diagnostic> warnings)
    {
        var (_, rig) = ExportCommands.Resolve(p, ClipId, characterId);
        if (rig.FindByStandardName(Blazma.Rigging.HumanoidStandard.Pelvis) is null)
            throw new CommandException("not_humanoid", $"Rig '{rig.Name}' has no bone mapped to 'pelvis'. Map the humanoid bones first (rig.map_bone).");
        if (MotionPhysicsMetrics.LastFrame(clip) + 1 > PhysicsPolish.MaxFrames)
            throw new CommandException("too_large", $"The physics polish handles clips of at most {PhysicsPolish.MaxFrames} frames.");
        if (clip.DurationFrames < 4) throw new CommandException("invalid_args", "The clip needs at least 4 frames.");
        var polished = PhysicsPolish.Apply(clip, rig, options, reduceKeys, out var result);
        clip.Tracks = polished.Tracks;
        if (result.ChangedFrames > 0) clip.Metadata["physics.polish"] = string.Join(",", result.Stages);
        warnings.AddRange(result.Warnings.Select(w => new Diagnostic("physics_polish", w)));
        return new JsonObject
        {
            ["clipId"] = ClipId.ToString(),
            ["stages"] = new JsonArray(result.Stages.Select(s => (JsonNode?)s).ToArray()),
            ["changedFrames"] = result.ChangedFrames,
            ["changedBones"] = result.ChangedBones.Count,
            ["before"] = PhysicsPolishCommands.Report(result.Before),
            ["after"] = PhysicsPolishCommands.Report(result.After),
        };
    }
}
