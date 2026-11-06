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
                ["kind"] = e.Kind.ToString().ToLowerInvariant(),
                ["id"] = e.Id.ToString(),
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

    /// <summary>
    /// The "combat" object (schema v5 data, docs/DATA_FORMAT.md §7.9): the clip's phases and hitboxes (active ranges relative to the
    /// export and clipped to it; attachment = bone or prop node in the GLB), the export character's hurtboxes and reaction rules, and
    /// the combos that use the clip. Written only when there is something to write.
    /// </summary>
    public static JsonObject? Combat(Project project, BakedClip baked, Rig rig, NodeBinding[] bindings, IReadOnlyList<ExportedProp> props, Core.Ids.ObjectId character,
        List<string>? warnings = null)
    {
        var clip = baked.Clip;
        var fps = clip.Fps;
        long Rel(Flicks t) => t.ToFrame(fps) - baked.StartFrame;
        long Clamp(long f) => Math.Clamp(f, 0, baked.FrameCount);
        var ch = project.FindCharacter(character);
        var combos = project.Combos.Where(c => c.Attacks.Exists(a => a.Clip == clip.Id)).ToList();
        if (clip.Combat is null && ch?.Combat is null && combos.Count == 0) return null;

        JsonObject Range(long s, long e) => new() { ["startFrame"] = s, ["endFrame"] = e };
        JsonObject? phases = null;
        if (clip.Combat?.Phases is { } ph)
            phases = new JsonObject
            {
                ["startup"] = Range(Clamp(Rel(ph.StartupStart)), Clamp(Rel(ph.ActiveStart))),
                ["active"] = Range(Clamp(Rel(ph.ActiveStart)), Clamp(Rel(ph.RecoveryStart))),
                ["recovery"] = Range(Clamp(Rel(ph.RecoveryStart)), Clamp(Rel(ph.End))),
            };
        var hitboxes = new JsonArray();
        foreach (var h in clip.Combat?.Hitboxes ?? new())
        {
            var ranges = new JsonArray();
            foreach (var r in h.Active)
            {
                var s = Clamp(Rel(r.Start));
                var e = Clamp(Rel(r.End));
                if (e > s) ranges.Add(Range(s, e));
            }
            if (ranges.Count == 0) continue; // never active inside the exported frames
            string? name = null;
            int? node = null;
            if (h.Attachment.Kind == HitboxAttachKind.Bone)
            {
                var i = rig.IndexOf(h.Attachment.Target);
                if (i >= 0) { name = rig.Bones[i].Name; node = bindings[i].Node; }
                else warnings?.Add($"Hitbox '{h.Name}': its bone no longer exists; exported without an attachment.");
            }
            else
            {
                var prop = props.FirstOrDefault(x => x.Prop.Id == h.Attachment.Target);
                name = prop?.Prop.Name ?? project.FindProp(h.Attachment.Target)?.Name;
                node = prop?.Node;
            }
            hitboxes.Add(new JsonObject
            {
                ["id"] = h.Id.ToString(),
                ["name"] = h.Name,
                ["shape"] = ShapeJson(h.Shape),
                ["attachment"] = new JsonObject
                {
                    ["kind"] = h.Attachment.Kind == HitboxAttachKind.Bone ? "bone" : "prop",
                    ["name"] = name,
                    ["node"] = node,
                    ["offset"] = TransformJson(h.Attachment.Offset),
                },
                ["active"] = ranges,
                ["damage"] = h.Damage,
                ["knockback"] = h.Knockback,
                ["hitstopFrames"] = h.HitstopFrames,
                ["tags"] = new JsonArray(h.Tags.Select(t => (JsonNode?)t).ToArray()),
            });
        }
        var hurtboxes = new JsonArray();
        foreach (var h in ch?.Combat?.Hurtboxes ?? new())
        {
            var i = rig.IndexOf(h.Bone);
            if (i < 0) { warnings?.Add($"Hurtbox '{h.Name}': its bone no longer exists; not exported."); continue; }
            hurtboxes.Add(new JsonObject
            {
                ["id"] = h.Id.ToString(), ["name"] = h.Name, ["bone"] = rig.Bones[i].Name, ["node"] = bindings[i].Node,
                ["shape"] = ShapeJson(h.Shape), ["offset"] = TransformJson(h.Offset), ["zone"] = h.Zone.ToString().ToLowerInvariant(), ["enabled"] = h.Enabled,
            });
        }
        var reactions = new JsonArray((ch?.Combat?.Reactions ?? new()).Select(r => (JsonNode?)new JsonObject
        {
            ["id"] = r.Id.ToString(), ["name"] = r.Name, ["direction"] = r.Direction.ToString().ToLowerInvariant(), ["height"] = r.Height.ToString().ToLowerInvariant(),
            ["minStrength"] = r.MinStrength, ["maxStrength"] = r.MaxStrength,
            ["clip"] = r.Clip.IsEmpty ? null : project.FindClip(r.Clip)?.Name, ["clipId"] = r.Clip.IsEmpty ? null : r.Clip.ToString(),
            ["durationSeconds"] = r.DurationSeconds,
        }).ToArray());
        var comboJson = new JsonArray(combos.Select(c => (JsonNode?)new JsonObject
        {
            ["id"] = c.Id.ToString(),
            ["name"] = c.Name,
            ["attacks"] = new JsonArray(c.Attacks.Select(a =>
            {
                var ac = project.FindClip(a.Clip);
                var afps = ac?.Fps ?? fps;
                return (JsonNode?)new JsonObject
                {
                    ["clipId"] = a.Clip.ToString(),
                    ["clip"] = ac?.Name,
                    ["isThisClip"] = a.Clip == clip.Id,
                    ["input"] = a.Input,
                    // Frames of the attack's own clip (not relative to this export).
                    ["cancelWindow"] = a.CancelStart is { } cs && a.CancelEnd is { } ce ? Range(cs.ToFrame(afps), ce.ToFrame(afps)) : null,
                    ["linkFrame"] = a.Link?.ToFrame(afps),
                    ["blendFrames"] = a.BlendFrames,
                };
            }).ToArray()),
        }).ToArray());
        return new JsonObject
        {
            ["phases"] = phases,
            ["hitboxes"] = hitboxes,
            ["hurtboxes"] = hurtboxes,
            ["reactions"] = reactions,
            ["combos"] = comboJson,
        };
    }

    static JsonObject ShapeJson(CollisionShape s) => new()
    {
        ["kind"] = s.Kind.ToString().ToLowerInvariant(),
        ["radius"] = s.Kind == CollisionShapeKind.Box ? null : s.Radius,
        ["length"] = s.Kind == CollisionShapeKind.Capsule ? s.Length : null,
        ["size"] = s.Kind == CollisionShapeKind.Box ? Vec(s.Size) : null,
    };

    static JsonObject TransformJson(Transform t) => new()
    {
        ["translation"] = Vec(t.Translation),
        ["rotation"] = new JsonArray(Round(t.Rotation.X), Round(t.Rotation.Y), Round(t.Rotation.Z), Round(t.Rotation.W)),
    };

    static JsonArray Vec(Vec3 v) => new(Round(v.X), Round(v.Y), Round(v.Z));
    static double Round(double x) => Math.Round(x, 7);

    static string PhaseName(KeyPhase p) => p switch
    {
        KeyPhase.MainAction => "main_action",
        KeyPhase.FollowThrough => "follow_through",
        _ => p.ToString().ToLowerInvariant(),
    };
}
