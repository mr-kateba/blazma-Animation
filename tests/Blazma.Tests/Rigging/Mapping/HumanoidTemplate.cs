using Blazma.Core.Mathematics;
using Blazma.Tests.Rigging.Import;

namespace Blazma.Tests.Rigging.Mapping;

/// <summary>A humanoid rest pose (+Z forward, left at +X) that can be named according to any convention.</summary>
public static class HumanoidTemplate
{
    /// <summary>role (standard name; spine roles are spine_01..spine_04) → bone name.</summary>
    public static Dictionary<string, string> Mixamo() => With("mixamorig:", new()
    {
        ["pelvis"] = "Hips", ["spine_01"] = "Spine", ["spine_02"] = "Spine1", ["spine_03"] = "Spine2",
        ["neck"] = "Neck", ["head"] = "Head",
        ["clavicle_l"] = "LeftShoulder", ["upper_arm_l"] = "LeftArm", ["lower_arm_l"] = "LeftForeArm", ["hand_l"] = "LeftHand",
        ["thigh_l"] = "LeftUpLeg", ["calf_l"] = "LeftLeg", ["foot_l"] = "LeftFoot", ["toe_l"] = "LeftToeBase",
        ["clavicle_r"] = "RightShoulder", ["upper_arm_r"] = "RightArm", ["lower_arm_r"] = "RightForeArm", ["hand_r"] = "RightHand",
        ["thigh_r"] = "RightUpLeg", ["calf_r"] = "RightLeg", ["foot_r"] = "RightFoot", ["toe_r"] = "RightToeBase",
    });

    public static Dictionary<string, string> Unreal() => new()
    {
        ["root"] = "root", ["pelvis"] = "pelvis", ["spine_01"] = "spine_01", ["spine_02"] = "spine_02", ["spine_03"] = "spine_03",
        ["neck"] = "neck_01", ["head"] = "head",
        ["clavicle_l"] = "clavicle_l", ["upper_arm_l"] = "upperarm_l", ["lower_arm_l"] = "lowerarm_l", ["hand_l"] = "hand_l",
        ["thigh_l"] = "thigh_l", ["calf_l"] = "calf_l", ["foot_l"] = "foot_l", ["toe_l"] = "ball_l",
        ["clavicle_r"] = "clavicle_r", ["upper_arm_r"] = "upperarm_r", ["lower_arm_r"] = "lowerarm_r", ["hand_r"] = "hand_r",
        ["thigh_r"] = "thigh_r", ["calf_r"] = "calf_r", ["foot_r"] = "foot_r", ["toe_r"] = "ball_r",
    };

    public static Dictionary<string, string> Unity() => new()
    {
        ["pelvis"] = "Hips", ["spine_01"] = "Spine", ["spine_02"] = "Chest", ["spine_03"] = "UpperChest",
        ["neck"] = "Neck", ["head"] = "Head",
        ["clavicle_l"] = "LeftShoulder", ["upper_arm_l"] = "LeftUpperArm", ["lower_arm_l"] = "LeftLowerArm", ["hand_l"] = "LeftHand",
        ["thigh_l"] = "LeftUpperLeg", ["calf_l"] = "LeftLowerLeg", ["foot_l"] = "LeftFoot", ["toe_l"] = "LeftToes",
        ["clavicle_r"] = "RightShoulder", ["upper_arm_r"] = "RightUpperArm", ["lower_arm_r"] = "RightLowerArm", ["hand_r"] = "RightHand",
        ["thigh_r"] = "RightUpperLeg", ["calf_r"] = "RightLowerLeg", ["foot_r"] = "RightFoot", ["toe_r"] = "RightToes",
    };

    /// <summary>Rigify-style: the "spine" bone is the pelvis, neck/head are spine.004 and spine.006 with spine.005 in between.</summary>
    public static Dictionary<string, string> Blender(string prefix = "") => With(prefix, new()
    {
        ["pelvis"] = "spine", ["spine_01"] = "spine.001", ["spine_02"] = "spine.002", ["spine_03"] = "spine.003",
        ["neck"] = "spine.004", ["neck_extra"] = "spine.005", ["head"] = "spine.006",
        ["clavicle_l"] = "shoulder.L", ["upper_arm_l"] = "upper_arm.L", ["lower_arm_l"] = "forearm.L", ["hand_l"] = "hand.L",
        ["thigh_l"] = "thigh.L", ["calf_l"] = "shin.L", ["foot_l"] = "foot.L", ["toe_l"] = "toe.L",
        ["clavicle_r"] = "shoulder.R", ["upper_arm_r"] = "upper_arm.R", ["lower_arm_r"] = "forearm.R", ["hand_r"] = "hand.R",
        ["thigh_r"] = "thigh.R", ["calf_r"] = "shin.R", ["foot_r"] = "foot.R", ["toe_r"] = "toe.R",
    });

    public static Dictionary<string, string> Biped() => new()
    {
        ["root"] = "Bip01", ["pelvis"] = "Bip01 Pelvis", ["spine_01"] = "Bip01 Spine", ["spine_02"] = "Bip01 Spine1", ["spine_03"] = "Bip01 Spine2",
        ["neck"] = "Bip01 Neck", ["head"] = "Bip01 Head",
        ["clavicle_l"] = "Bip01 L Clavicle", ["upper_arm_l"] = "Bip01 L UpperArm", ["lower_arm_l"] = "Bip01 L Forearm", ["hand_l"] = "Bip01 L Hand",
        ["thigh_l"] = "Bip01 L Thigh", ["calf_l"] = "Bip01 L Calf", ["foot_l"] = "Bip01 L Foot", ["toe_l"] = "Bip01 L Toe0",
        ["clavicle_r"] = "Bip01 R Clavicle", ["upper_arm_r"] = "Bip01 R UpperArm", ["lower_arm_r"] = "Bip01 R Forearm", ["hand_r"] = "Bip01 R Hand",
        ["thigh_r"] = "Bip01 R Thigh", ["calf_r"] = "Bip01 R Calf", ["foot_r"] = "Bip01 R Foot", ["toe_r"] = "Bip01 R Toe0",
    };

    public static Dictionary<string, string> Vrm() => new()
    {
        ["pelvis"] = "J_Bip_C_Hips", ["spine_01"] = "J_Bip_C_Spine", ["spine_02"] = "J_Bip_C_Chest", ["spine_03"] = "J_Bip_C_UpperChest",
        ["neck"] = "J_Bip_C_Neck", ["head"] = "J_Bip_C_Head",
        ["clavicle_l"] = "J_Bip_L_Shoulder", ["upper_arm_l"] = "J_Bip_L_UpperArm", ["lower_arm_l"] = "J_Bip_L_LowerArm", ["hand_l"] = "J_Bip_L_Hand",
        ["thigh_l"] = "J_Bip_L_UpperLeg", ["calf_l"] = "J_Bip_L_LowerLeg", ["foot_l"] = "J_Bip_L_Foot", ["toe_l"] = "J_Bip_L_ToeBase",
        ["clavicle_r"] = "J_Bip_R_Shoulder", ["upper_arm_r"] = "J_Bip_R_UpperArm", ["lower_arm_r"] = "J_Bip_R_LowerArm", ["hand_r"] = "J_Bip_R_Hand",
        ["thigh_r"] = "J_Bip_R_UpperLeg", ["calf_r"] = "J_Bip_R_LowerLeg", ["foot_r"] = "J_Bip_R_Foot", ["toe_r"] = "J_Bip_R_ToeBase",
    };

    public static Dictionary<string, string> Blazma() => new()
    {
        ["root"] = "root", ["pelvis"] = "pelvis", ["spine_01"] = "spine_01", ["spine_02"] = "spine_02", ["spine_03"] = "spine_03",
        ["neck"] = "neck", ["head"] = "head",
        ["clavicle_l"] = "clavicle_l", ["upper_arm_l"] = "upper_arm_l", ["lower_arm_l"] = "lower_arm_l", ["hand_l"] = "hand_l",
        ["thigh_l"] = "thigh_l", ["calf_l"] = "calf_l", ["foot_l"] = "foot_l", ["toe_l"] = "toe_l",
        ["clavicle_r"] = "clavicle_r", ["upper_arm_r"] = "upper_arm_r", ["lower_arm_r"] = "lower_arm_r", ["hand_r"] = "hand_r",
        ["thigh_r"] = "thigh_r", ["calf_r"] = "calf_r", ["foot_r"] = "foot_r", ["toe_r"] = "toe_r",
    };

    static Dictionary<string, string> With(string prefix, Dictionary<string, string> d) =>
        d.ToDictionary(kv => kv.Key, kv => prefix + kv.Value);

    /// <param name="names">Roles to create and their names; missing roles are skipped (e.g. remove "spine_03").</param>
    /// <param name="flipX">Mirror the character so that "left" bones lie at −X (a rig facing −Z).</param>
    /// <param name="spineSiblingOfPelvis">Biped-like layout where the spine hangs off the root, not the pelvis.</param>
    public static GltfTestBuilder Build(Dictionary<string, string> names, bool flipX = false, bool spineSiblingOfPelvis = false,
        Dictionary<string, string>? extras = null)
    {
        var b = new GltfTestBuilder();
        var idx = new Dictionary<string, int>();
        double fx = flipX ? -1 : 1;

        int Add(string role, string? parentRole, Vec3 off)
        {
            int parent = parentRole is not null && idx.TryGetValue(parentRole, out int p) ? p : -1;
            if (!names.TryGetValue(role, out var nm)) return -1;
            return idx[role] = b.Add(nm, parent, off);
        }

        Add("root", null, Vec3.Zero);
        Add("pelvis", "root", new Vec3(0, 1.0, 0));
        string spineParent = spineSiblingOfPelvis && idx.ContainsKey("root") ? "root" : "pelvis";
        double spineBase = spineSiblingOfPelvis ? 1.0 : 0.0;
        string last = spineParent;
        foreach (var (role, dy) in new[] { ("spine_01", 0.1), ("spine_02", 0.12), ("spine_03", 0.12), ("spine_04", 0.1) })
        {
            if (Add(role, last, new Vec3(0, dy + (last == "root" ? spineBase : 0), 0)) >= 0) last = role;
        }
        Add("neck", last, new Vec3(0, 0.15, 0));
        if (names.TryGetValue("neck_extra", out var extra) && idx.TryGetValue("neck", out int neck))
            idx["neck_extra"] = b.Add(extra, neck, new Vec3(0, 0.05, 0));
        Add("head", idx.ContainsKey("neck_extra") ? "neck_extra" : "neck", new Vec3(0, 0.1, 0));

        foreach (var (side, sign) in new[] { ("l", fx), ("r", -fx) })
        {
            Add("clavicle_" + side, last, new Vec3(sign * 0.04, 0.12, 0));
            string armParent = idx.ContainsKey("clavicle_" + side) ? "clavicle_" + side : last;
            Add("upper_arm_" + side, armParent, new Vec3(sign * 0.12, 0, 0));
            Add("lower_arm_" + side, "upper_arm_" + side, new Vec3(sign * 0.28, 0, 0));
            Add("hand_" + side, "lower_arm_" + side, new Vec3(sign * 0.26, 0, 0));
            Add("thigh_" + side, "pelvis", new Vec3(sign * 0.09, -0.05, 0));
            Add("calf_" + side, "thigh_" + side, new Vec3(0, -0.45, 0));
            Add("foot_" + side, "calf_" + side, new Vec3(0, -0.42, 0));
            Add("toe_" + side, "foot_" + side, new Vec3(0, -0.05, 0.12));
        }
        if (extras is not null)
            foreach (var (role, name) in extras)
            {
                // extras: "<parentRole>" → bone name, placed 5 cm above the parent
                b.Add(name, idx[role], new Vec3(0, 0.05, 0));
            }
        return b;
    }
}
