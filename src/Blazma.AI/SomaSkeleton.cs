using Blazma.Core.Mathematics;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.AI;

/// <summary>
/// The compact 30-joint SOMA control skeleton that Kimodo SOMA RP/SEED v1.1 predicts (body only: no fingers or
/// face, docs/RISKS.md R5). Names, parents and parent-local offsets are copied from kimodo.cpp
/// <c>src/skeleton.hpp</c> (soma30_spec, commit 5679ff19), which copies them from NVIDIA Kimodo's Apache-2.0
/// skeleton definitions. The hips rest offset (0, 0.988, 0) is the value kimodo.cpp's
/// <c>scripts/export_glb.py</c> writes for the root node. Rest rotations are identity (the GLB exporter writes only
/// translations), so a joint's local rotation is also its rotation relative to rest.
/// Axes: +Y up, +Z forward, "Left*" joints on +X: the same as Blazma, so no axis conversion is needed.
/// </summary>
public static class SomaSkeleton
{
    public const string Key = "soma30";
    public const double NativeFps = 30;

    public static readonly IReadOnlyList<string> Names = new[]
    {
        "Hips", "Spine1", "Spine2", "Chest", "Neck1", "Neck2", "Head", "Jaw",
        "LeftEye", "RightEye", "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
        "LeftHandThumbEnd", "LeftHandMiddleEnd", "RightShoulder", "RightArm", "RightForeArm",
        "RightHand", "RightHandThumbEnd", "RightHandMiddleEnd", "LeftLeg", "LeftShin", "LeftFoot",
        "LeftToeBase", "RightLeg", "RightShin", "RightFoot", "RightToeBase",
    };

    public static readonly IReadOnlyList<int> Parents = new[]
    {
        -1, 0, 1, 2, 3, 4, 5, 6, 6, 6, 3, 10, 11, 12, 13, 13, 3, 16, 17, 18, 19, 19, 0, 22, 23, 24, 0, 26, 27, 28,
    };

    static readonly double[][] Offsets =
    {
        new[] { 0, 0.988, 0 }, new[] { -.00013727, .0500376256, -.00053726669 }, new[] { -1.86574103e-9, .0712530139, -.000298248546 },
        new[] { -5.75188398e-9, .0755006305, -.00815970992 }, new[] { -.00181676517, .263112953, -.00553348292 },
        new[] { -2.85102231e-8, .0770939664, .0230258546 }, new[] { -4.5975437e-8, .0612891595, .0195370861 },
        new[] { 2.63687901e-5, .0047559225, .0309494062 }, new[] { .0320638079, .0538020513, .0758688308 },
        new[] { -.0322244017, .05361869, .0755823359 }, new[] { .0162165175, .232371641, .0511341324 },
        new[] { .149198457, 2.19397873e-8, -.0550232576 }, new[] { .287393078, 2.50268389e-9, -2.58787737e-5 },
        new[] { .270939812, -7.06625108e-9, 2.60897248e-5 }, new[] { .122686267, -.0322017573, .0483306876 },
        new[] { .190119595, -.00312878387, -.000339570373 }, new[] { -.0138011824, .231803086, .0521415786 },
        new[] { -.150371962, 1.17387901e-7, -.0554560437 }, new[] { -.287366393, 1.87628082e-8, -2.59709359e-5 },
        new[] { -.271336198, -1.16767401e-9, 2.61269368e-5 }, new[] { -.122642483, -.0321145448, .0480403904 },
        new[] { -.190005945, -.00306615542, -.0003157343 }, new[] { .10043214, -.0843452671, .0259565473 },
        new[] { -1e-8, -.432217537, -.00802912805 }, new[] { 1e-8, -.421550959, -.0348152298 },
        new[] { 0, -.0505947206, .132315294 }, new[] { -.10047278, -.0829525995, .0262031695 },
        new[] { 1e-8, -.433622059, -.00805555828 }, new[] { 2e-8, -.421173943, -.0347839785 },
        new[] { -3.42907669e-9, -.0507960932, .132841956 },
    };

    /// <summary>SOMA joint → Blazma standard bone. Jaw, eyes and the hand end joints have no standard bone;
    /// Neck2 is folded into neck/head (rotations are transferred in world space, so nothing is lost).</summary>
    public static readonly IReadOnlyDictionary<string, string> ToStandard = new Dictionary<string, string>
    {
        ["Hips"] = H.Pelvis, ["Spine1"] = H.Spine1, ["Spine2"] = H.Spine2, ["Chest"] = H.Chest,
        ["Neck1"] = H.Neck, ["Head"] = H.Head,
        ["LeftShoulder"] = H.Clavicle(Side.Left), ["LeftArm"] = H.UpperArm(Side.Left), ["LeftForeArm"] = H.LowerArm(Side.Left), ["LeftHand"] = H.Hand(Side.Left),
        ["RightShoulder"] = H.Clavicle(Side.Right), ["RightArm"] = H.UpperArm(Side.Right), ["RightForeArm"] = H.LowerArm(Side.Right), ["RightHand"] = H.Hand(Side.Right),
        ["LeftLeg"] = H.Thigh(Side.Left), ["LeftShin"] = H.Calf(Side.Left), ["LeftFoot"] = H.Foot(Side.Left), ["LeftToeBase"] = H.Toe(Side.Left),
        ["RightLeg"] = H.Thigh(Side.Right), ["RightShin"] = H.Calf(Side.Right), ["RightFoot"] = H.Foot(Side.Right), ["RightToeBase"] = H.Toe(Side.Right),
    };

    public static Vec3 Offset(int joint) => new(Offsets[joint][0], Offsets[joint][1], Offsets[joint][2]);

    public static int IndexOf(string name)
    {
        for (var i = 0; i < Names.Count; i++) if (Names[i] == name) return i;
        return -1;
    }

    public static readonly SkeletonDefinition Definition = new(
        Key, Names, Parents,
        Enumerable.Range(0, Names.Count).Select(i => new Transform(Offset(i), Quat.Identity, Vec3.One)).ToArray(),
        "+Y", "+Z", ToStandard);

    /// <summary>
    /// Builds a definition from what a runner sent, but only accepts the SOMA layout this build knows (names and
    /// parents must match exactly, offsets within 1 mm), so a mismatched runner cannot silently produce wrong
    /// retargeting. The runner's offsets are used.
    /// </summary>
    public static SkeletonDefinition FromRunner(string key, IReadOnlyList<string> names, IReadOnlyList<int> parents, IReadOnlyList<Vec3> offsets)
    {
        if (key != Key) throw new MotionGenerationException("unsupported_skeleton", $"Skeleton '{key}' is not supported (only {Key}).");
        if (names.Count != Names.Count || !names.SequenceEqual(Names) || !parents.SequenceEqual(Parents))
            throw new MotionGenerationException("unsupported_skeleton", "The runner's SOMA joint names or parents differ from this build's definition.");
        if (offsets.Count != Names.Count) throw new MotionGenerationException("invalid_motion", "Offset count does not match the joints.");
        for (var i = 0; i < offsets.Count; i++)
            if (!offsets[i].ApproxEquals(Offset(i), 1e-3)) throw new MotionGenerationException("unsupported_skeleton", $"Rest offset of '{Names[i]}' differs from this build's SOMA definition.");
        return Definition with { Rest = offsets.Select(o => new Transform(o, Quat.Identity, Vec3.One)).ToArray() };
    }

    /// <summary>Model-space rest positions.</summary>
    public static Vec3[] RestPositions()
    {
        var p = new Vec3[Names.Count];
        for (var i = 0; i < p.Length; i++) p[i] = Parents[i] < 0 ? Offset(i) : p[Parents[i]] + Offset(i);
        return p;
    }
}
