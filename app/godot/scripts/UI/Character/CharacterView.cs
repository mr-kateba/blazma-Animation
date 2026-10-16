using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Rigging;
using Godot;
using CoreTransform = Blazma.Core.Mathematics.Transform;

namespace Blazma.UI.Views;

/// <summary>
/// The 3D representation of one project character: a Godot Skeleton3D driven from the core pose
/// plus either the built-in mannequin mesh (capsule segments on bone attachments) or the imported glTF scene.
/// The core model (RigEvaluator) is the single source of truth: <see cref="ApplyPose"/> sets every
/// Godot bone so that its global transform relative to the character root equals the core model transform.
/// </summary>
public partial class CharacterView : Node3D
{
    public string CharacterId { get; }
    public Character Character { get; private set; }
    public Rig Rig { get; private set; }
    public bool IsImported { get; private set; }
    public string? LoadError { get; private set; }

    Skeleton3D? _skeleton;
    RigEvaluator _ev;
    int[] _coreToGodot = Array.Empty<int>();    // core bone index → Godot bone index (-1 = none)
    int[] _godotOrder = Array.Empty<int>();     // Godot bone indices, parents first
    int[] _godotToCore = Array.Empty<int>();    // Godot bone index → core bone index (-1 = none)
    Transform3D[] _desired = Array.Empty<Transform3D>(); // scratch: desired global (skeleton space) per Godot bone
    Transform3D _skelToChar = Transform3D.Identity;
    readonly List<MeshInstance3D> _meshes = new();
    StandardMaterial3D? _material;
    Node? _importedRoot;

    /// <summary>Core model-space transform of every bone (index = rig bone index), refreshed by <see cref="ApplyPose"/>.</summary>
    public CoreTransform[] Model { get; private set; } = Array.Empty<CoreTransform>();
    /// <summary>Joint positions in world space (character root is at the origin).</summary>
    public Vector3[] Joints { get; private set; } = Array.Empty<Vector3>();
    /// <summary>World position of every rig control (index = rig control index).</summary>
    public Vector3[] ControlPositions { get; private set; } = Array.Empty<Vector3>();
    public bool[] ControlPinned { get; private set; } = Array.Empty<bool>();
    public Skeleton3D? Skeleton => _skeleton;
    public RigEvaluator Evaluator => _ev;
    public ulong PoseVersion { get; private set; }

    public CharacterView(Character character, Rig rig)
    {
        CharacterId = character.Id.ToString();
        Character = character;
        Rig = rig;
        _ev = new RigEvaluator(rig);
        Name = "Character_" + CharacterId;
    }

    /// <summary>Call after the view is in the scene tree.</summary>
    public void Build(Project project, Palette palette)
    {
        _material = new StandardMaterial3D { Roughness = 0.72f, Metallic = 0f };
        ApplyPalette(palette);
        if (Rig.Source.Kind == RigSourceKind.Imported && !Rig.Source.Asset.IsEmpty)
        {
            var asset = project.FindAsset(Rig.Source.Asset);
            if (asset is not null && asset.Data.Length > 0) IsImported = BuildImported(asset.Data);
            else LoadError = "The model file is missing from the project.";
        }
        if (!IsImported) BuildBuiltin();
        MapBones();
        ApplyPose();
    }

    public void SetCharacter(Character character, Rig rig)
    {
        Character = character;
        if (!ReferenceEquals(rig, Rig))
        {
            Rig = rig;
            _ev = new RigEvaluator(rig);
        }
    }

    public void ApplyPalette(Palette p)
    {
        if (_material is null) return;
        // Warm neutral clay, a little lighter than the stage in both themes.
        _material.AlbedoColor = p.Name == "light" ? new Color("#b3a899") : new Color("#b8ab9a");
    }

    // ---- built-in mannequin ----------------------------------------------------------------------

    record Seg(string From, string To, float R, float Sx = 1f, float Sz = 1f, float T1 = 1f);

    static readonly Seg[] MannequinSegments =
    {
        new("pelvis", "spine_01", 0.10f, 1.45f, 0.86f),
        new("spine_01", "spine_02", 0.083f, 1.42f, 0.84f),
        new("spine_02", "spine_03", 0.09f, 1.42f, 0.84f),
        new("spine_03", "neck", 0.108f, 1.42f, 0.82f, 0.62f),
        new("neck", "head", 0.036f),
        new("clavicle_l", "upper_arm_l", 0.041f), new("clavicle_r", "upper_arm_r", 0.041f),
        new("upper_arm_l", "lower_arm_l", 0.043f), new("upper_arm_r", "lower_arm_r", 0.043f),
        new("lower_arm_l", "hand_l", 0.035f), new("lower_arm_r", "hand_r", 0.035f),
        new("thigh_l", "calf_l", 0.075f), new("thigh_r", "calf_r", 0.075f),
        new("calf_l", "foot_l", 0.052f), new("calf_r", "foot_r", 0.052f),
    };

    void BuildBuiltin()
    {
        _skeleton = new Skeleton3D { Name = "Skeleton" };
        AddChild(_skeleton);
        var index = new Dictionary<string, int>();
        foreach (var b in Rig.Bones)
        {
            var gi = _skeleton.AddBone(b.Name);
            index[b.Id.ToString()] = gi;
            if (!b.Parent.IsEmpty && index.TryGetValue(b.Parent.ToString(), out var pi)) _skeleton.SetBoneParent(gi, pi);
            _skeleton.SetBoneRest(gi, ToT3(b.Rest));
        }
        _skeleton.ResetBonePoses();

        var byStd = new Dictionary<string, Bone>();
        foreach (var m in Rig.HumanoidMap)
            if (Rig.FindBone(m.Bone) is { } bone) byStd[m.Standard] = bone;

        foreach (var seg in MannequinSegments)
        {
            if (!byStd.TryGetValue(seg.From, out var from) || !byStd.TryGetValue(seg.To, out var to)) continue;
            // Child joint expressed in the parent bone's frame (the child may be an intermediate bone's child in odd rigs).
            var end = ModelOffset(from, to);
            AddCapsule(from, Vector3.Zero, end * seg.T1, seg.R, seg.Sx, seg.Sz);
        }

        // Head, hands, feet.
        if (byStd.TryGetValue("head", out var head))
            AddSphere(head, new Vector3(0, 0.082f, 0.005f), 0.095f, new Vector3(0.92f, 1.14f, 1.02f));
        foreach (var side in new[] { "l", "r" })
        {
            if (byStd.TryGetValue("hand_" + side, out var hand) && byStd.TryGetValue("lower_arm_" + side, out var lower))
            {
                var dir = ModelOffset(lower, hand).Normalized();
                AddCapsule(hand, Vector3.Zero, dir * 0.085f, 0.03f, 1f, 1f);
            }
            if (byStd.TryGetValue("foot_" + side, out var foot))
            {
                // Rounded foot lying along +Z from just behind the ankle to the toes.
                var z0 = new Vector3(0, -0.032f, -0.035f);
                var z1 = new Vector3(0, -0.032f, 0.135f);
                AddCapsule(foot, z0, z1, 0.042f, 1f, 1f);
            }
        }
    }

    Vector3 ModelOffset(Bone from, Bone to)
    {
        // Rest offset between two bones in the frame of `from` (walks the hierarchy between them).
        var chain = new List<Bone>();
        for (var b = to; b is not null && b.Id != from.Id; b = Rig.FindBone(b.Parent)) chain.Add(b);
        var t = CoreTransform.Identity;
        for (var i = chain.Count - 1; i >= 0; i--) t = t * chain[i].Rest;
        return ToV3(t.Translation);
    }

    BoneAttachment3D Attach(Bone bone)
    {
        var att = new BoneAttachment3D { Name = "Att_" + bone.Name };
        _skeleton!.AddChild(att);
        att.BoneName = bone.Name;
        return att;
    }

    readonly Dictionary<string, BoneAttachment3D> _attachments = new();

    BoneAttachment3D AttachmentFor(Bone bone)
    {
        if (!_attachments.TryGetValue(bone.Id.ToString(), out var att)) _attachments[bone.Id.ToString()] = att = Attach(bone);
        return att;
    }

    void AddCapsule(Bone bone, Vector3 a, Vector3 b, float radius, float sx, float sz)
    {
        var len = (b - a).Length();
        if (len < 1e-4f) return;
        var mesh = new CapsuleMesh { Radius = radius, Height = len + 2 * radius, RadialSegments = 24, Rings = 8 };
        var dir = (b - a) / len;
        var basis = new Basis(FromTo(Vector3.Up, dir)) * Basis.FromScale(new Vector3(sx, 1, sz));
        var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = _material, Transform = new Transform3D(basis, (a + b) * 0.5f), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AttachmentFor(bone).AddChild(mi);
        _meshes.Add(mi);
    }

    void AddSphere(Bone bone, Vector3 center, float radius, Vector3 scale)
    {
        var mesh = new SphereMesh { Radius = radius, Height = radius * 2, RadialSegments = 32, Rings = 16 };
        var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = _material, Transform = new Transform3D(Basis.FromScale(scale), center), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AttachmentFor(bone).AddChild(mi);
        _meshes.Add(mi);
    }

    static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        var d = from.Dot(to);
        if (d >= 0.999999f) return Quaternion.Identity;
        if (d <= -0.999999f)
        {
            var axis = from.Cross(Vector3.Right);
            if (axis.LengthSquared() < 1e-6f) axis = from.Cross(Vector3.Forward);
            return new Quaternion(axis.Normalized(), Mathf.Pi);
        }
        var c = from.Cross(to);
        return new Quaternion(c.X, c.Y, c.Z, 1 + d).Normalized();
    }

    // ---- imported glTF -------------------------------------------------------------------------------

    bool BuildImported(byte[] data)
    {
        try
        {
            var doc = new GltfDocument();
            var state = new GltfState();
            var err = doc.AppendFromBuffer(data, "", state);
            if (err != Error.Ok) { LoadError = "The model could not be loaded (" + err + ")."; return false; }
            var scene = doc.GenerateScene(state);
            if (scene is not Node3D root) { scene?.QueueFree(); LoadError = "The model contains no 3D scene."; return false; }

            var skeleton = FindSkeleton(root);
            if (skeleton is null) { root.QueueFree(); LoadError = "The model has no skeleton."; return false; }

            // glTF node index → Godot bone index, from the importer's own skeleton description.
            var nodeToBone = new Dictionary<int, int>();
            var skeletons = state.GetSkeletons();
            for (var si = 0; si < skeletons.Count; si++)
            {
                var gs = skeletons[si];
                if (!ReferenceEquals(gs.GetGodotSkeleton(), skeleton) && gs.GetGodotSkeleton() != skeleton) continue;
                var joints = gs.Joints;
                for (var k = 0; k < joints.Length; k++) nodeToBone[joints[k]] = k;
            }
            // Cross-check by name; fall back to names when the order assumption does not hold.
            var nodes = state.GetNodes();
            foreach (var (node, bone) in nodeToBone.ToList())
            {
                if (bone >= skeleton.GetBoneCount()) { nodeToBone.Remove(node); continue; }
                var n = nodes[node];
                var orig = n.OriginalName;
                var bn = skeleton.GetBoneName(bone);
                if (!string.IsNullOrEmpty(orig) && orig != bn && SanitizeName(orig) != bn)
                {
                    var byName = skeleton.FindBone(orig);
                    if (byName < 0) byName = skeleton.FindBone(SanitizeName(orig));
                    if (byName >= 0) nodeToBone[node] = byName;
                }
            }
            _nodeToBone = nodeToBone;

            foreach (var ap in root.FindChildren("*", "AnimationPlayer", true, false)) ap.QueueFree();
            AddChild(root);
            _importedRoot = root;
            _skeleton = skeleton;
            foreach (var mi in root.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
            {
                mi.ExtraCullMargin = 2f;
                mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                _meshes.Add(mi);
            }
            return true;
        }
        catch (Exception e)
        {
            LoadError = e.Message;
            return false;
        }
    }

    Dictionary<int, int>? _nodeToBone;

    static string SanitizeName(string s) => s.Replace(':', '_').Replace('/', '_').Replace('.', '_').Replace(',', '_');

    static Skeleton3D? FindSkeleton(Node n)
    {
        if (n is Skeleton3D s) return s;
        foreach (var c in n.GetChildren())
            if (FindSkeleton(c) is { } r) return r;
        return null;
    }

    // ---- bone mapping core ↔ Godot -----------------------------------------------------------------------

    void MapBones()
    {
        var sk = _skeleton!;
        var n = Rig.Bones.Count;
        _coreToGodot = new int[n];
        Array.Fill(_coreToGodot, -1);
        _godotToCore = new int[sk.GetBoneCount()];
        Array.Fill(_godotToCore, -1);
        for (var i = 0; i < n; i++)
        {
            var b = Rig.Bones[i];
            int g = -1;
            if (IsImported && _nodeToBone is not null && Rig.Source.NodeIndex.TryGetValue(b.Id.ToString(), out var node) && _nodeToBone.TryGetValue(node, out var gi)) g = gi;
            else if (!IsImported) g = sk.FindBone(b.Name);
            if (g >= 0 && _godotToCore[g] < 0) { _coreToGodot[i] = g; _godotToCore[g] = i; }
        }

        // Parents first.
        var order = new List<int>(sk.GetBoneCount());
        var done = new bool[sk.GetBoneCount()];
        void Visit(int g)
        {
            if (done[g]) return;
            var p = sk.GetBoneParent(g);
            if (p >= 0) Visit(p);
            done[g] = true;
            order.Add(g);
        }
        for (var g = 0; g < sk.GetBoneCount(); g++) Visit(g);
        _godotOrder = order.ToArray();
        _desired = new Transform3D[sk.GetBoneCount()];

        // Skeleton node relative to the character root (non-joint glTF ancestors such as Z_UP live here).
        _skelToChar = GlobalTransform.AffineInverse() * sk.GlobalTransform;
        // Core roots already contain these ancestors, so they are removed again when posing root bones.
    }

    // ---- posing ----------------------------------------------------------------------------------------------

    /// <summary>Re-evaluate the core pose and drive the Godot skeleton from it. Call only when the pose changed.</summary>
    public void ApplyPose()
    {
        PoseVersion++;
        var model = _ev.Model(Character.Pose);
        Model = model;
        if (Joints.Length != model.Length) Joints = new Vector3[model.Length];
        for (var i = 0; i < model.Length; i++) Joints[i] = ToV3(model[i].Translation);

        var controls = Rig.Controls;
        if (ControlPositions.Length != controls.Count) { ControlPositions = new Vector3[controls.Count]; ControlPinned = new bool[controls.Count]; }
        for (var i = 0; i < controls.Count; i++)
        {
            ControlPositions[i] = ToV3(PoseSolver.ControlModel(Rig, Character.Pose, controls[i], model).Translation);
            ControlPinned[i] = controls[i].Kind == ControlKind.IkTarget && PoseSolver.StateOf(Rig, Character.Pose, controls[i].Id).Pinned;
        }

        var sk = _skeleton;
        if (sk is null) return;
        var skelInv = _skelToChar.AffineInverse();
        foreach (var g in _godotOrder)
        {
            var parent = sk.GetBoneParent(g);
            var core = _godotToCore[g];
            if (core >= 0) _desired[g] = skelInv * ToT3(model[core]);
            else _desired[g] = parent >= 0 ? _desired[parent] * sk.GetBoneRest(g) : sk.GetBoneRest(g);
            var local = parent >= 0 ? _desired[parent].AffineInverse() * _desired[g] : _desired[g];
            sk.SetBonePose(g, local);
        }
    }

    /// <summary>
    /// Largest distance (metres) between a mapped bone's Godot global position (in character root space) and the
    /// core model position. Used by the self-test to prove the rendered skeleton matches the core evaluation.
    /// </summary>
    public double MaxSkeletonError(out int checkedBones)
    {
        checkedBones = 0;
        var sk = _skeleton;
        if (sk is null) return double.NaN;
        var skelToChar = GlobalTransform.AffineInverse() * sk.GlobalTransform;
        var worst = 0.0;
        for (var i = 0; i < _coreToGodot.Length; i++)
        {
            var g = _coreToGodot[i];
            if (g < 0) continue;
            var pos = skelToChar * sk.GetBoneGlobalPose(g).Origin;
            var core = Model[i].Translation;
            var err = Math.Sqrt(Sq(pos.X - core.X) + Sq(pos.Y - core.Y) + Sq(pos.Z - core.Z));
            worst = Math.Max(worst, err);
            checkedBones++;
        }
        return worst;
    }

    public int MappedBoneCount => _coreToGodot.Count(g => g >= 0);
    public int GodotBoneCount => _skeleton?.GetBoneCount() ?? 0;
    public int MeshCount => _meshes.Count;

    static double Sq(double x) => x * x;

    // ---- conversion ----------------------------------------------------------------------------------------------

    public static Vector3 ToV3(Vec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    public static Transform3D ToT3(CoreTransform t)
    {
        var q = new Quaternion((float)t.Rotation.X, (float)t.Rotation.Y, (float)t.Rotation.Z, (float)t.Rotation.W).Normalized();
        var basis = new Basis(q);
        if (t.Scale != Vec3.One) basis = basis * Basis.FromScale(ToV3(t.Scale));
        return new Transform3D(basis, ToV3(t.Translation));
    }
}
