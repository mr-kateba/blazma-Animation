using System.Text.RegularExpressions;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;

namespace Blazma.Rigging.Mapping;

/// <summary>
/// Maps an arbitrary rig onto the Blazma standard humanoid names (<see cref="HumanoidStandard"/>) using
/// bone-name synonyms from common conventions (Mixamo, Unreal, Unity, Rigify, 3ds Max Biped, VRM, Blazma),
/// validated and completed with the skeleton hierarchy and rest positions.
/// </summary>
public static partial class HumanoidMapper
{
    /// <summary>Computes a mapping without modifying the rig.</summary>
    public static MappingReport AutoMap(Rig rig)
    {
        var g = new Graph(rig);
        var issues = new List<MappingIssue>();
        var cands = BuildCandidates(g);

        var assigned = new Dictionary<string, Assigned>(); // standard → bone
        var used = new HashSet<int>();
        int? Mapped(string std) => assigned.TryGetValue(std, out var a) ? a.Bone : null;

        void Assign(string std, int bone, MappingConfidence conf, string reason)
        {
            assigned[std] = new Assigned(bone, conf, reason);
            used.Add(bone);
        }

        bool PickAndAssign(string std)
        {
            if (!cands.TryGetValue(std, out var list)) return false;
            foreach (var c in list.OrderByDescending(x => x.Prio).ThenBy(x => g.Depth[x.Bone]).ThenBy(x => x.Bone))
            {
                if (used.Contains(c.Bone)) continue;
                string? bad = CheckRule(g, std, c.Bone, Mapped);
                if (bad is not null)
                {
                    issues.Add(new MappingIssue("hierarchy_violation",
                        $"Bone '{g.Names[c.Bone]}' matches '{std}' by name but {bad}; skipped.", g.Names[c.Bone]));
                    continue;
                }
                Assign(std, c.Bone, c.Confidence, c.Reason);
                return true;
            }
            return false;
        }

        // Pelvis: prefer a named candidate that is an ancestor of the thighs, else the thighs' common parent.
        int? rawThighL = RawBest(g, cands, HumanoidStandard.Thigh(Side.Left));
        int? rawThighR = RawBest(g, cands, HumanoidStandard.Thigh(Side.Right));
        PickPelvis(g, cands, rawThighL, rawThighR, issues, Assign);

        foreach (var side in new[] { Side.Left, Side.Right })
        {
            PickAndAssign(HumanoidStandard.Thigh(side));
            PickAndAssign(HumanoidStandard.Calf(side));
            PickAndAssign(HumanoidStandard.Foot(side));
            PickAndAssign(HumanoidStandard.Toe(side));
            PickAndAssign(HumanoidStandard.UpperArm(side));
            PickAndAssign(HumanoidStandard.LowerArm(side));
            PickAndAssign(HumanoidStandard.Hand(side));
        }
        PickAndAssign(HumanoidStandard.Head);
        PickAndAssign(HumanoidStandard.Neck);
        foreach (var side in new[] { Side.Left, Side.Right }) PickAndAssign(HumanoidStandard.Clavicle(side));
        PickAndAssign(HumanoidStandard.Root);

        AssignSpine(g, assigned, used, issues);

        return Finish(g, assigned.Select(kv => ToAssignment(g, kv.Key, kv.Value)).ToList(), issues);
    }

    /// <summary>Writes <paramref name="report"/>'s assignments into <c>rig.HumanoidMap</c>, replacing the previous map.</summary>
    public static void Apply(Rig rig, MappingReport report)
    {
        rig.HumanoidMap.Clear();
        foreach (var a in report.Assignments)
            rig.HumanoidMap.Add(new BoneMapping { Bone = a.BoneId, Standard = a.Standard });
    }

    /// <summary>
    /// Manual correction: gives <paramref name="boneId"/> the standard name <paramref name="standard"/>
    /// (null = unmap it). Any previous holder of that standard name loses it. Missing lists and issues are recomputed.
    /// </summary>
    public static MappingReport Reassign(Rig rig, MappingReport current, ObjectId boneId, string? standard)
    {
        if (standard is not null && !HumanoidStandard.IsStandard(standard))
            throw new ArgumentException($"'{standard}' is not a Blazma standard bone name.", nameof(standard));
        var bone = rig.FindBone(boneId) ?? throw new ArgumentException($"Bone '{boneId}' does not exist in rig '{rig.Name}'.", nameof(boneId));

        var list = current.Assignments
            .Where(a => a.BoneId != boneId && (standard is null || a.Standard != standard) && rig.FindBone(a.BoneId) is not null)
            .ToList();
        if (standard is not null)
            list.Add(new BoneAssignment(bone.Id, bone.Name, standard, MappingConfidence.Manual, "assigned manually"));
        return Finish(new Graph(rig), list, new List<MappingIssue>());
    }

    // ---- report assembly -----------------------------------------------------------------------

    static BoneAssignment ToAssignment(Graph g, string std, Assigned a) =>
        new(g.Ids[a.Bone], g.Names[a.Bone], std, a.Confidence, a.Reason);

    static MappingReport Finish(Graph g, List<BoneAssignment> assignments, List<MappingIssue> issues)
    {
        var order = new Dictionary<string, int>();
        for (int i = 0; i < HumanoidStandard.Bones.Count; i++) order[HumanoidStandard.Bones[i].Name] = i;
        assignments = assignments.OrderBy(a => order.GetValueOrDefault(a.Standard, int.MaxValue)).ToList();

        var have = new HashSet<string>(assignments.Select(a => a.Standard));
        var missingReq = new List<string>();
        var missingOpt = new List<string>();
        foreach (var (name, _) in HumanoidStandard.Bones)
        {
            if (have.Contains(name)) continue;
            (HumanoidStandard.Required.Contains(name) ? missingReq : missingOpt).Add(name);
        }

        issues.AddRange(Validate(g, assignments));
        return new MappingReport { Assignments = assignments, MissingRequired = missingReq, MissingOptional = missingOpt, Issues = issues };
    }

    /// <summary>Hierarchy and left/right sanity checks on a finished assignment set.</summary>
    static IEnumerable<MappingIssue> Validate(Graph g, List<BoneAssignment> assignments)
    {
        var byStd = new Dictionary<string, int>();
        foreach (var a in assignments)
        {
            int i = g.IndexOf(a.BoneId);
            if (i >= 0) byStd[a.Standard] = i;
        }
        int? Mapped(string std) => byStd.TryGetValue(std, out int i) ? i : null;

        foreach (var (std, bone) in byStd.OrderBy(kv => kv.Value))
        {
            string? bad = CheckRule(g, std, bone, Mapped);
            if (bad is not null)
                yield return new MappingIssue("hierarchy_violation", $"Bone '{g.Names[bone]}' is mapped to '{std}' but {bad}.", g.Names[bone]);
        }

        // Left bones should be at +X, right bones at −X in the rest pose.
        const double tol = 0.005;
        var lefts = new List<(string Std, int Bone)>();
        var rights = new List<(string Std, int Bone)>();
        foreach (var (std, bone) in byStd)
        {
            if (std.EndsWith("_l", StringComparison.Ordinal)) lefts.Add((std, bone));
            else if (std.EndsWith("_r", StringComparison.Ordinal)) rights.Add((std, bone));
        }
        if (lefts.Count > 0 && rights.Count > 0
            && lefts.Average(x => g.World[x.Bone].X) < -tol && rights.Average(x => g.World[x.Bone].X) > tol)
        {
            yield return new MappingIssue("faces_negative_z",
                "Bones mapped as left lie at −X and right bones at +X: the rig appears to face −Z (or its sides are named the other way round). It was not flipped.");
            yield break;
        }
        foreach (var (std, bone) in lefts.Concat(rights).OrderBy(x => x.Bone))
        {
            double x = g.World[bone].X;
            bool left = std.EndsWith("_l", StringComparison.Ordinal);
            if (left ? x < -tol : x > tol)
                yield return new MappingIssue("side_mismatch",
                    $"Bone '{g.Names[bone]}' is mapped to '{std}' but lies at X={x:0.###} (left should be +X, right −X).", g.Names[bone]);
        }
    }

    // ---- hierarchy rules -----------------------------------------------------------------------

    /// <summary>Returns a reason when assigning <paramref name="std"/> to <paramref name="bone"/> violates the hierarchy, else null.</summary>
    static string? CheckRule(Graph g, string std, int bone, Func<string, int?> mapped)
    {
        string b = std, suffix = "";
        if (std.EndsWith("_l", StringComparison.Ordinal) || std.EndsWith("_r", StringComparison.Ordinal))
        {
            b = std[..^2];
            suffix = std[^2..];
        }

        string? Descendant(params string[] prefs)
        {
            foreach (string p in prefs)
            {
                if (mapped(p) is not int anc) continue;
                return g.IsDescendant(bone, anc) ? null : $"is not a descendant of '{g.Names[anc]}' ({p})";
            }
            return null;
        }

        switch (b)
        {
            case "thigh": return Descendant(HumanoidStandard.Pelvis);
            case "calf": return Descendant("thigh" + suffix);
            case "foot": return Descendant("calf" + suffix, "thigh" + suffix);
            case "toe": return Descendant("foot" + suffix, "calf" + suffix);
            case "lower_arm": return Descendant("upper_arm" + suffix);
            case "hand": return Descendant("lower_arm" + suffix, "upper_arm" + suffix);
            case "neck":
                if (mapped(HumanoidStandard.Head) is int head && !g.IsDescendant(head, bone))
                    return $"is not an ancestor of '{g.Names[head]}' (head)";
                return null;
            case "clavicle":
                if (mapped("upper_arm" + suffix) is int ua && !g.IsDescendant(ua, bone))
                    return $"is not an ancestor of '{g.Names[ua]}' (upper_arm{suffix})";
                return null;
            case "root":
                if (mapped(HumanoidStandard.Pelvis) is int pel && !g.IsDescendant(pel, bone))
                    return $"is not an ancestor of '{g.Names[pel]}' (pelvis)";
                return null;
            default: return null;
        }
    }

    // ---- pelvis & spine ------------------------------------------------------------------------

    static int? RawBest(Graph g, Dictionary<string, List<Cand>> cands, string std) =>
        cands.TryGetValue(std, out var l)
            ? l.OrderByDescending(x => x.Prio).ThenBy(x => g.Depth[x.Bone]).ThenBy(x => x.Bone).Select(x => (int?)x.Bone).FirstOrDefault()
            : null;

    static void PickPelvis(Graph g, Dictionary<string, List<Cand>> cands, int? thighL, int? thighR,
        List<MappingIssue> issues, Action<string, int, MappingConfidence, string> assign)
    {
        string std = HumanoidStandard.Pelvis;
        var thighs = new[] { thighL, thighR }.Where(x => x is not null).Select(x => x!.Value).ToList();

        if (cands.TryGetValue(std, out var list))
        {
            foreach (var c in list.OrderByDescending(x => x.Prio).ThenBy(x => g.Depth[x.Bone]).ThenBy(x => x.Bone))
            {
                if (thighs.All(t => g.IsDescendant(t, c.Bone)))
                {
                    assign(std, c.Bone, c.Confidence, c.Reason);
                    return;
                }
                issues.Add(new MappingIssue("hierarchy_violation",
                    $"Bone '{g.Names[c.Bone]}' matches 'pelvis' by name but the thighs are not its descendants; skipped.", g.Names[c.Bone]));
            }
        }

        if (thighL is int l && thighR is int r)
        {
            int p = g.Parent[l];
            while (p >= 0 && !g.IsDescendant(r, p)) p = g.Parent[p];
            if (p >= 0)
                assign(std, p, MappingConfidence.Hierarchy, "common parent of both thighs");
        }
    }

    [GeneratedRegex(@"^(spine\d*|chest|upperchest|torso|abdomen|waist|belly)$")]
    private static partial Regex SpineKeyRegex();

    static void AssignSpine(Graph g, Dictionary<string, Assigned> assigned, HashSet<int> used, List<MappingIssue> issues)
    {
        if (!assigned.TryGetValue(HumanoidStandard.Pelvis, out var pelvis)) return;
        int top;
        string topLabel;
        if (assigned.TryGetValue(HumanoidStandard.Neck, out var neck)) { top = neck.Bone; topLabel = "neck"; }
        else if (assigned.TryGetValue(HumanoidStandard.Head, out var head)) { top = head.Bone; topLabel = "head"; }
        else return;

        // Walk from the neck (or head) down to the pelvis, or to the pelvis's ancestor if the spine is a sibling branch.
        var pelvisUp = new HashSet<int>();
        for (int p = pelvis.Bone; p >= 0; p = g.Parent[p]) pelvisUp.Add(p);

        var chain = new List<int>();
        int cur = g.Parent[top];
        while (cur >= 0 && !pelvisUp.Contains(cur))
        {
            chain.Add(cur);
            cur = g.Parent[cur];
        }
        if (cur < 0)
        {
            issues.Add(new MappingIssue("spine_path_not_found",
                $"No path from pelvis '{g.Names[pelvis.Bone]}' to '{g.Names[top]}' through the hierarchy; spine bones were not mapped."));
            return;
        }
        chain.Reverse();
        chain.RemoveAll(used.Contains);
        int n = chain.Count;
        if (n == 0) return;

        var targets = new Dictionary<int, string>();
        if (n == 1) targets[0] = HumanoidStandard.Spine1;
        else if (n == 2) { targets[0] = HumanoidStandard.Spine1; targets[1] = HumanoidStandard.Chest; }
        else
        {
            targets[0] = HumanoidStandard.Spine1;
            targets[n / 2] = HumanoidStandard.Spine2;
            targets[n - 1] = HumanoidStandard.Chest;
        }

        foreach (var (idx, std) in targets)
        {
            int bone = chain[idx];
            var conf = g.Condensed[bone] == BoneNameNormalizer.Condense(std) ? MappingConfidence.Exact
                : SpineKeyRegex().IsMatch(g.Parsed[bone].Key) ? MappingConfidence.Synonym
                : MappingConfidence.Hierarchy;
            assigned[std] = new Assigned(bone, conf, $"spine bone {idx + 1} of {n} between pelvis and {topLabel}");
            used.Add(bone);
        }
        if (n > 3)
            issues.Add(new MappingIssue("spine_extra_bones_unmapped",
                $"The spine has {n} bones; only the first, middle and last were mapped to spine_01/02/03."));
    }

    // ---- name → candidates ---------------------------------------------------------------------

    sealed record Cand(int Bone, int Prio, MappingConfidence Confidence, string Reason);

    sealed record Assigned(int Bone, MappingConfidence Confidence, string Reason);

    // key → (standard, priority). Spine bones are intentionally absent: they are resolved from the hierarchy.
    static readonly Dictionary<string, (string Std, int Prio)> CentreSynonyms = new()
    {
        ["root"] = (HumanoidStandard.Root, 10),
        ["bip01"] = (HumanoidStandard.Root, 6),
        ["pelvis"] = (HumanoidStandard.Pelvis, 10),
        ["hips"] = (HumanoidStandard.Pelvis, 9),
        ["hip"] = (HumanoidStandard.Pelvis, 6),
        ["neck"] = (HumanoidStandard.Neck, 10),
        ["neck1"] = (HumanoidStandard.Neck, 9),
        ["neckbase"] = (HumanoidStandard.Neck, 8),
        ["necktwist1"] = (HumanoidStandard.Neck, 6),
        ["neck2"] = (HumanoidStandard.Neck, 5),
        ["necktwist2"] = (HumanoidStandard.Neck, 4),
        ["head"] = (HumanoidStandard.Head, 10),
    };

    // Rigify names the neck/head "spine.004..006"; only recognised when the rig has a "spine.006".
    static readonly Dictionary<string, (string Std, int Prio)> RigifyNeckHead = new()
    {
        ["spine4"] = (HumanoidStandard.Neck, 3),
        ["spine5"] = (HumanoidStandard.Neck, 3),
        ["spine6"] = (HumanoidStandard.Head, 3),
    };

    // key → (side-less standard base, priority).
    static readonly Dictionary<string, (string Base, int Prio)> LimbSynonyms = new()
    {
        ["clavicle"] = ("clavicle", 10), ["shoulder"] = ("clavicle", 9), ["collar"] = ("clavicle", 8),
        ["collarbone"] = ("clavicle", 8), ["clav"] = ("clavicle", 7),
        ["upperarm"] = ("upper_arm", 10), ["uparm"] = ("upper_arm", 8), ["arm"] = ("upper_arm", 5),
        ["lowerarm"] = ("lower_arm", 10), ["forearm"] = ("lower_arm", 10), ["lowarm"] = ("lower_arm", 8),
        ["hand"] = ("hand", 10),
        ["thigh"] = ("thigh", 10), ["upperleg"] = ("thigh", 10), ["upleg"] = ("thigh", 10),
        ["calf"] = ("calf", 10), ["shin"] = ("calf", 10), ["lowerleg"] = ("calf", 10), ["lowleg"] = ("calf", 8), ["leg"] = ("calf", 4),
        ["foot"] = ("foot", 10), ["ankle"] = ("foot", 5),
        ["toe"] = ("toe", 10), ["toes"] = ("toe", 10), ["toebase"] = ("toe", 10), ["ball"] = ("toe", 9), ["toe0"] = ("toe", 9),
    };

    static Dictionary<string, List<Cand>> BuildCandidates(Graph g)
    {
        var result = new Dictionary<string, List<Cand>>();
        bool rigify = g.Parsed.Any(p => p.Key == "spine6");

        void Add(string std, int bone, int prio, MappingConfidence conf, string reason)
        {
            if (g.Condensed[bone] == BoneNameNormalizer.Condense(std))
            {
                conf = MappingConfidence.Exact;
                prio += 1;
                reason = $"name '{g.Names[bone]}' is the standard name";
            }
            if (!result.TryGetValue(std, out var l)) result[std] = l = new List<Cand>();
            l.Add(new Cand(bone, prio, conf, reason));
        }

        for (int i = 0; i < g.Count; i++)
        {
            var (side, key, penalty) = g.Parsed[i];
            string name = g.Names[i];

            if (side is null)
            {
                if (CentreSynonyms.TryGetValue(key, out var c))
                    Add(c.Std, i, c.Prio - penalty, MappingConfidence.Synonym, $"name '{name}' matches synonym '{key}'");
                else if (rigify && RigifyNeckHead.TryGetValue(key, out var rf))
                    Add(rf.Std, i, rf.Prio - penalty, MappingConfidence.Synonym, $"name '{name}' matches Rigify spine chain '{key}'");
            }

            if (LimbSynonyms.TryGetValue(key, out var limb))
            {
                if (side is Side s)
                {
                    Add(Name(limb.Base, s), i, limb.Prio - penalty, MappingConfidence.Synonym, $"name '{name}' matches synonym '{key}' ({s})");
                }
                else if (Math.Abs(g.World[i].X) > 0.005)
                {
                    var inferred = g.World[i].X > 0 ? Side.Left : Side.Right;
                    Add(Name(limb.Base, inferred), i, limb.Prio - penalty - 1, MappingConfidence.Hierarchy,
                        $"name '{name}' matches '{key}'; side inferred from rest position (X={g.World[i].X:0.###})");
                }
            }
        }
        return result;
    }

    static string Name(string b, Side s) => b + (s == Side.Left ? "_l" : "_r");

    // ---- rig graph -----------------------------------------------------------------------------

    sealed class Graph
    {
        public readonly int Count;
        public readonly ObjectId[] Ids;
        public readonly string[] Names;
        public readonly string[] Condensed;
        public readonly BoneNameNormalizer.Parsed[] Parsed;
        public readonly int[] Parent;
        public readonly int[] Depth;
        public readonly Vec3[] World;
        readonly Dictionary<ObjectId, int> _index = new();

        public Graph(Rig rig)
        {
            Count = rig.Bones.Count;
            Ids = new ObjectId[Count];
            Names = new string[Count];
            Condensed = new string[Count];
            Parsed = new BoneNameNormalizer.Parsed[Count];
            Parent = new int[Count];
            Depth = new int[Count];
            World = new Vec3[Count];
            for (int i = 0; i < Count; i++)
            {
                var b = rig.Bones[i];
                Ids[i] = b.Id;
                Names[i] = b.Name;
                Condensed[i] = BoneNameNormalizer.Condense(b.Name);
                Parsed[i] = BoneNameNormalizer.ParseFull(b.Name);
                _index[b.Id] = i;
            }
            for (int i = 0; i < Count; i++)
            {
                var pid = rig.Bones[i].Parent;
                Parent[i] = !pid.IsEmpty && _index.TryGetValue(pid, out int p) && p != i ? p : -1;
            }

            var world = new Transform?[Count];
            var state = new byte[Count]; // 1 = in progress (cycle guard)
            Transform WorldOf(int i)
            {
                if (world[i] is Transform done) return done;
                if (state[i] == 1) { Parent[i] = -1; return rig.Bones[i].Rest; }
                state[i] = 1;
                var local = rig.Bones[i].Rest;
                var t = Parent[i] >= 0 ? WorldOf(Parent[i]) * local : local;
                world[i] = t;
                return t;
            }
            for (int i = 0; i < Count; i++) World[i] = WorldOf(i).Translation;
            for (int i = 0; i < Count; i++)
            {
                int d = 0;
                for (int p = Parent[i]; p >= 0 && d <= Count; p = Parent[p]) d++;
                Depth[i] = d;
            }
        }

        public int IndexOf(ObjectId id) => _index.TryGetValue(id, out int i) ? i : -1;

        /// <summary>True when <paramref name="bone"/> lies strictly below <paramref name="ancestor"/>.</summary>
        public bool IsDescendant(int bone, int ancestor)
        {
            int steps = 0;
            for (int p = Parent[bone]; p >= 0 && steps <= Count; p = Parent[p], steps++)
                if (p == ancestor) return true;
            return false;
        }
    }
}
