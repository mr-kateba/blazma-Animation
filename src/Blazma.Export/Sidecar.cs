using System.Text.Json.Nodes;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;

namespace Blazma.Export;

/// <summary>
/// "&lt;name&gt;.blazma.json" written next to an exported GLB: what glTF cannot carry (rational fps,
/// loop range, markers/phases, events, root motion, the humanoid bone map). See docs/DATA_FORMAT.md §7.
/// Frames are relative to the first exported frame, like the times in the GLB.
/// </summary>
public static class Sidecar
{
    public const string Schema = "blazma.animation-export";
    public const int SchemaVersion = 1;

    public static JsonObject Build(Project project, BakedClip baked, Rig rig, NodeBinding[] bindings, string animationName, string glbFileName)
    {
        var clip = baked.Clip;
        var fps = clip.Fps;
        long Rel(Flicks t) => t.ToFrame(fps) - baked.StartFrame;
        bool InRange(Flicks t) => Rel(t) is var f && f >= 0 && f < baked.FrameCount;

        // Loop range relative to the export, clamped to the exported frames (never negative); null when the
        // exported frames do not overlap the loop at all.
        var (ls, le) = AnimationBaker.LoopRange(clip);
        long? loopStart = Rel(ls), loopEnd = Rel(le);
        var clamped = loopStart < 0 || loopEnd > baked.FrameCount - 1;
        if (loopEnd < 0 || loopStart > baked.FrameCount - 1) loopStart = loopEnd = null;
        else { loopStart = Math.Max(0, loopStart!.Value); loopEnd = Math.Min(baked.FrameCount - 1, loopEnd!.Value); }
        var rm = baked.RootMotion;
        var asset = rig.Source.Kind == RigSourceKind.Imported ? project.FindAsset(rig.Source.Asset) : null;

        return new JsonObject
        {
            ["schema"] = Schema,
            ["schemaVersion"] = SchemaVersion,
            ["generator"] = AnimationExporter.Generator,
            ["glb"] = glbFileName,
            ["animation"] = animationName,
            ["clip"] = new JsonObject { ["id"] = clip.Id.ToString(), ["name"] = clip.Name },
            ["fps"] = new JsonObject { ["numerator"] = fps.Numerator, ["denominator"] = fps.Denominator },
            ["startFrame"] = baked.StartFrame,
            ["endFrame"] = baked.EndFrame,
            ["frameCount"] = baked.FrameCount,
            ["durationSeconds"] = baked.DurationSeconds,
            ["loop"] = new JsonObject
            {
                ["enabled"] = clip.Loop.Enabled,
                ["startFrame"] = loopStart,
                ["endFrame"] = loopEnd,
                ["clampedToExport"] = clamped,
            },
            ["markers"] = new JsonArray(clip.Markers.Where(m => InRange(m.Time)).OrderBy(m => m.Time.Value).Select(m => (JsonNode?)new JsonObject
            {
                ["frame"] = Rel(m.Time),
                ["name"] = m.Name,
                ["kind"] = m.Phase == KeyPhase.None ? "marker" : PhaseName(m.Phase),
            }).ToArray()),
            ["events"] = new JsonArray(clip.Events.Where(e => InRange(e.Time)).OrderBy(e => e.Time.Value).Select(e => (JsonNode?)new JsonObject
            {
                ["frame"] = Rel(e.Time),
                ["time"] = Rel(e.Time) * (double)fps.Denominator / fps.Numerator,
                ["name"] = e.Name,
                ["payload"] = new JsonObject(e.Data.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            }).ToArray()),
            ["rootMotion"] = new JsonObject
            {
                ["mode"] = RootMotionModes.Name(rm.Mode),
                ["removed"] = rm.Removed,
                ["bone"] = rm.BoneName,
                ["axes"] = rm.Mode == RootMotionMode.InPlaceAll ? "xyz" : "xz",
                ["totalDisplacement"] = Vec(rm.Total),
                ["cycleDisplacement"] = Vec(rm.CycleDisplacement),
                ["perFrameDelta"] = new JsonArray(rm.Deltas.Select(d => (JsonNode?)Vec(d)).ToArray()),
            },
            ["bones"] = new JsonArray(rig.Bones.Select((b, i) => (JsonNode?)new JsonObject
            {
                ["name"] = b.Name,
                ["standard"] = rig.StandardNameOf(b.Id),
                ["parent"] = b.Parent.IsEmpty ? null : rig.FindBone(b.Parent)?.Name,
                ["node"] = bindings[i].Node,
            }).ToArray()),
            ["source"] = new JsonObject
            {
                ["kind"] = rig.Source.Kind == RigSourceKind.Imported ? "imported" : "builtin",
                ["rig"] = rig.Name,
                ["file"] = asset?.FileName ?? rig.Source.AssetPath,
                ["project"] = project.Name,
            },
        };
    }

    static JsonArray Vec(Vec3 v) => new(Round(v.X), Round(v.Y), Round(v.Z));
    static double Round(double x) => Math.Round(x, 7);

    static string PhaseName(KeyPhase p) => p switch
    {
        KeyPhase.MainAction => "main_action",
        KeyPhase.FollowThrough => "follow_through",
        _ => p.ToString().ToLowerInvariant(),
    };
}
