namespace Blazma.Rigging;

/// <summary>
/// The Blazma standard humanoid skeleton. Imported rigs are mapped onto these names so that
/// poses, mirroring, retargeting and AI motion (e.g. Kimodo SOMA) share one vocabulary.
/// Side suffix: "_l" = character left (+X), "_r" = character right (−X).
/// </summary>
public static class HumanoidStandard
{
    public const string Root = "root";
    public const string Pelvis = "pelvis";
    public const string Spine1 = "spine_01";
    public const string Spine2 = "spine_02";
    public const string Chest = "spine_03";
    public const string Neck = "neck";
    public const string Head = "head";

    public static string Clavicle(Side s) => "clavicle" + Suffix(s);
    public static string UpperArm(Side s) => "upper_arm" + Suffix(s);
    public static string LowerArm(Side s) => "lower_arm" + Suffix(s);
    public static string Hand(Side s) => "hand" + Suffix(s);
    public static string Thigh(Side s) => "thigh" + Suffix(s);
    public static string Calf(Side s) => "calf" + Suffix(s);
    public static string Foot(Side s) => "foot" + Suffix(s);
    public static string Toe(Side s) => "toe" + Suffix(s);

    static string Suffix(Side s) => s == Side.Left ? "_l" : "_r";

    /// <summary>All standard bones, parent before child, with their standard parent.</summary>
    public static readonly IReadOnlyList<(string Name, string? Parent)> Bones = BuildBones();

    /// <summary>Bones a rig must map for posing tools (IK, mirror, balance) to work.</summary>
    public static readonly IReadOnlySet<string> Required = new HashSet<string>
    {
        Pelvis, Spine1, Head,
        UpperArm(Side.Left), LowerArm(Side.Left), Hand(Side.Left),
        UpperArm(Side.Right), LowerArm(Side.Right), Hand(Side.Right),
        Thigh(Side.Left), Calf(Side.Left), Foot(Side.Left),
        Thigh(Side.Right), Calf(Side.Right), Foot(Side.Right),
    };

    public static bool IsStandard(string name) => Bones.Any(b => b.Name == name);

    /// <summary>"thigh_l" ↔ "thigh_r"; centre bones map to themselves.</summary>
    public static string Counterpart(string standard) =>
        standard.EndsWith("_l", StringComparison.Ordinal) ? standard[..^2] + "_r"
        : standard.EndsWith("_r", StringComparison.Ordinal) ? standard[..^2] + "_l"
        : standard;

    static List<(string, string?)> BuildBones()
    {
        var list = new List<(string, string?)>
        {
            (Root, null), (Pelvis, Root), (Spine1, Pelvis), (Spine2, Spine1), (Chest, Spine2), (Neck, Chest), (Head, Neck),
        };
        foreach (var s in new[] { Side.Left, Side.Right })
        {
            list.Add((Clavicle(s), Chest));
            list.Add((UpperArm(s), Clavicle(s)));
            list.Add((LowerArm(s), UpperArm(s)));
            list.Add((Hand(s), LowerArm(s)));
        }
        foreach (var s in new[] { Side.Left, Side.Right })
        {
            list.Add((Thigh(s), Pelvis));
            list.Add((Calf(s), Thigh(s)));
            list.Add((Foot(s), Calf(s)));
            list.Add((Toe(s), Foot(s)));
        }
        return list;
    }
}

public enum Side { Left, Right }
