using System.Text.Json;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;
using Blazma.Tests.Rigging.Import;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.Advanced;

/// <summary>Shared helpers for the Phase 8 tests: a project with the mannequin and a humanoid-mapped CesiumMan.</summary>
static class P8
{
    public static readonly CommandRegistry Registry = CommandRegistry.CreateDefault();

    public static CommandResult Run(ProjectSession s, string id, object args)
    {
        try { return s.Execute(Registry.Create(id, JsonNode.Parse(JsonSerializer.Serialize(args))!.AsObject()), CommandOrigin.Test); }
        catch (CommandException e) { return CommandResult.Fail(e.Code, e.Message); }
    }

    public static CommandResult Ok(ProjectSession s, string id, object args)
    {
        var r = Run(s, id, args);
        Assert.True(r.Ok, $"{id}: {r.Error?.Code} {r.Error?.Message}");
        return r;
    }

    public static string Err(CommandResult r) => r.Error?.Code ?? "ok";

    public static string Json<T>(T value) => JsonSerializer.Serialize(value, ProjectArchive.JsonOptions);

    /// <summary>Manual humanoid map of CesiumMan (its names follow no known convention), as in the Phase 7 tests.</summary>
    public static readonly (string Bone, string Std)[] CesiumMap =
    {
        ("Skeleton_torso_joint_1", "pelvis"), ("Skeleton_torso_joint_2", "spine_01"), ("torso_joint_3", "spine_03"), ("Skeleton_neck_joint_2", "head"),
        ("Skeleton_arm_joint_L__4_", "upper_arm_l"), ("Skeleton_arm_joint_L__3_", "lower_arm_l"), ("Skeleton_arm_joint_L__2_", "hand_l"),
        ("Skeleton_arm_joint_R", "upper_arm_r"), ("Skeleton_arm_joint_R__2_", "lower_arm_r"), ("Skeleton_arm_joint_R__3_", "hand_r"),
        ("leg_joint_L_1", "thigh_l"), ("leg_joint_L_2", "calf_l"), ("leg_joint_L_3", "foot_l"),
        ("leg_joint_R_1", "thigh_r"), ("leg_joint_R_2", "calf_r"), ("leg_joint_R_3", "foot_r"),
    };

    /// <summary>Project with the mannequin (from Project.CreateNew + scene.add_mannequin) and CesiumMan imported and mapped.</summary>
    public static (ProjectSession S, string Mannequin, string Cesium) Scene(bool cesium = true)
    {
        var s = new ProjectSession(Project.CreateNew("Phase 8"));
        var man = (string)Ok(s, "scene.add_mannequin", new { }).Data!["characterId"]!;
        if (!cesium) return (s, man, "");
        var r = Ok(s, "rig.import", new { path = RepoPaths.Sample("models/khronos/CesiumMan.glb"), name = "Cesium" });
        var ch = (string)r.Data!["characterId"]!;
        var rigId = (string)r.Data!["rigId"]!;
        foreach (var (bone, std) in CesiumMap) Ok(s, "rig.map_bone", new { rigId, bone, standard = std });
        return (s, man, ch);
    }

    public static Character Character(ProjectSession s, string id) => s.Project.GetCharacter(ObjectId.Parse(id));
    public static Rig RigOf(ProjectSession s, string ch) => s.Project.GetRig(Character(s, ch).Rig);
    public static Clip Clip(ProjectSession s, string id) => s.Project.GetClip(ObjectId.Parse(id));

    public static string ApplyTemplate(ProjectSession s, string template, string ch, double? seconds = null) =>
        (string)Ok(s, "template.apply", seconds is null ? new { templateId = template, characterId = ch } : (object)new { templateId = template, characterId = ch, durationSeconds = seconds })
            .Data!["clipId"]!;

    public static Transform[] Model(Rig rig, Clip clip, long frame) =>
        new RigEvaluator(rig).Model(ClipEvaluator.Evaluate(clip, rig, Flicks.FromFrame(frame, clip.Fps)));

    public static int Bone(Rig rig, string std) => rig.IndexOf(rig.FindByStandardName(std)!.Id);

    /// <summary>Mapped bones with a direction (bone → first standard child mapped on both rigs).</summary>
    public static List<(string Std, string Child)> Directions(Rig a, Rig b)
    {
        var list = new List<(string, string)>();
        foreach (var (std, kids) in Blazma.Rigging.Retargeting.HumanoidRetargetMap.AimChildren)
        {
            if (a.FindByStandardName(std) is null || b.FindByStandardName(std) is null) continue;
            var i = Array.FindIndex(kids, k => a.FindByStandardName(k) is not null && b.FindByStandardName(k) is not null);
            // Skip when a joint between them is mapped on one rig only: that rig bends there and the other cannot.
            if (i >= 0 && kids.Take(i).All(k => a.FindByStandardName(k) is null && b.FindByStandardName(k) is null)) list.Add((std, kids[i]));
        }
        return list;
    }

    public static double AngleDeg(Vec3 a, Vec3 b) =>
        Math.Acos(Math.Clamp(Vec3.Dot(a.Normalized(), b.Normalized()), -1, 1)) * 180 / Math.PI;

    public static Vec3 Dir(Rig rig, Transform[] m, string std, string child) =>
        m[Bone(rig, child)].Translation - m[Bone(rig, std)].Translation;

    public static Side[] Sides => new[] { Side.Left, Side.Right };
    public static string Foot(Side s) => H.Foot(s);
}
