using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Motion;
using Blazma.Rigging;
using Blazma.UI.App;
using Godot;
using CoreTransform = Blazma.Core.Mathematics.Transform;

namespace Blazma.UI.Views;

/// <summary>
/// Phase 8: one Godot node per project prop under the stage's world (box, sphere and cylinder meshes sized by <see cref="Prop.Size"/>, or the
/// embedded .glb of an imported prop). A prop's world transform is computed by <see cref="PropEvaluator"/> from the active clip at the
/// current time and the bone transforms the character view shows, so an attached prop sticks to its bone exactly as in the export.
/// Undo and redo replace the project's prop list, so props are always looked up by id and nodes are rebuilt when a prop's shape changes.
/// </summary>
public partial class PropLayer : Node3D
{
    sealed class Entry
    {
        public required Node3D Root;
        public required string Signature;
    }

    readonly CharacterLayer _characters;
    readonly Dictionary<string, Entry> _nodes = new();
    readonly Dictionary<string, CoreTransform> _world = new();
    readonly HashSet<string> _edited = new();
    Palette _palette = Palette.Dark;
    long _lastFrame = long.MinValue;
    string? _lastClip;

    static AppController App => AppController.Instance;

    /// <summary>Raised after props were rebuilt or moved (the overlay and the panel redraw).</summary>
    public event Action? PropsChanged;

    public PropLayer(CharacterLayer characters)
    {
        _characters = characters;
        Name = "Props";
        _characters.ViewsChanged += Refresh;
        App.PlaybackChanged += OnPlayback;
        App.SessionChanged += OnSession;
    }

    public override void _ExitTree()
    {
        _characters.ViewsChanged -= Refresh;
        App.PlaybackChanged -= OnPlayback;
        App.SessionChanged -= OnSession;
    }

    public void SetPalette(Palette p)
    {
        _palette = p;
        foreach (var e in _nodes.Values) ApplyColor(e.Root);
    }

    public int NodeCount => _nodes.Count;
    public Node3D? NodeOf(string propId) => _nodes.TryGetValue(propId, out var e) ? e.Root : null;

    /// <summary>World transform of a prop as drawn (null when the prop does not exist).</summary>
    public CoreTransform? WorldOf(string? propId) => propId is not null && _world.TryGetValue(propId, out var t) ? t : null;

    public Prop? Find(string? propId) => propId is null ? null : App.Session.Project.Props.Find(p => p.Id.ToString() == propId);

    /// <summary>The prop shows its working value (what the user just dragged) until the frame or clip changes or a key is set.</summary>
    public void MarkEdited(string propId) => _edited.Add(propId);

    /// <summary>The character view the prop is attached to (null for a free prop or a stale attachment).</summary>
    public CharacterView? ViewOf(Prop prop) => prop.Attachment is { } a ? _characters.View(a.Character.ToString()) : null;

    void OnPlayback()
    {
        var frame = App.Playing ? long.MinValue + 1 : App.CurrentFrame;
        var clip = App.ActiveClipId;
        if (frame != _lastFrame || clip != _lastClip || App.Playing)
        {
            _lastFrame = frame;
            _lastClip = clip;
            _edited.Clear();
            Refresh();
        }
    }

    void OnSession(SessionChangedArgs args)
    {
        var propsTouched = false;
        foreach (var c in args.Changes)
        {
            switch (c.Kind)
            {
                case Change.ProjectReplaced:
                    _edited.Clear();
                    propsTouched = true;
                    break;
                case Change.PropChanged:
                    if (c.TargetId is { } id) _edited.Add(id);
                    propsTouched = true;
                    break;
                case Change.ClipChanged or Change.ClipAdded or Change.ClipRemoved:
                    _edited.Clear();
                    propsTouched = true;
                    break;
                case Change.CharacterAdded or Change.CharacterRemoved or Change.RigChanged:
                    propsTouched = true;
                    break;
            }
        }
        if (propsTouched) Rebuild();
    }

    // ---- building -----------------------------------------------------------------------------------------------------------

    static string SignatureOf(Prop p) => $"{p.Shape}|{p.Size.X:R}|{p.Size.Y:R}|{p.Size.Z:R}|{p.Asset}";

    /// <summary>Create, replace and remove nodes so they match the project's props, then place them.</summary>
    public void Rebuild()
    {
        var props = App.Session.Project.Props;
        var wanted = props.Select(p => p.Id.ToString()).ToHashSet();
        foreach (var id in _nodes.Keys.ToList())
            if (!wanted.Contains(id)) { Drop(id); }
        foreach (var prop in props)
        {
            var id = prop.Id.ToString();
            var sig = SignatureOf(prop);
            if (_nodes.TryGetValue(id, out var existing))
            {
                if (existing.Signature == sig) continue;
                Drop(id);
            }
            var root = Create(prop);
            AddChild(root);
            _nodes[id] = new Entry { Root = root, Signature = sig };
            ApplyColor(root);
        }
        Refresh();
    }

    void Drop(string id)
    {
        if (!_nodes.Remove(id, out var e)) return;
        RemoveChild(e.Root);
        e.Root.QueueFree();
        _world.Remove(id);
    }

    Node3D Create(Prop prop)
    {
        var root = new Node3D { Name = "Prop_" + prop.Id };
        MeshInstance3D Mesh(Mesh mesh) => new() { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        var size = new Vector3((float)prop.Size.X, (float)prop.Size.Y, (float)prop.Size.Z);
        switch (prop.Shape)
        {
            case PropShape.Box:
                root.AddChild(Mesh(new BoxMesh { Size = size }));
                break;
            case PropShape.Sphere:
                root.AddChild(Mesh(new SphereMesh { Radius = size.X / 2, Height = size.X, RadialSegments = 32, Rings = 16 }));
                break;
            case PropShape.Cylinder:
                root.AddChild(Mesh(new CylinderMesh { TopRadius = size.X / 2, BottomRadius = size.X / 2, Height = size.Y, RadialSegments = 32 }));
                break;
            default:
                if (!AddImported(root, prop)) root.AddChild(Mesh(new BoxMesh { Size = size })); // a box stands in for a model that cannot be shown
                break;
        }
        return root;
    }

    bool AddImported(Node3D root, Prop prop)
    {
        var asset = App.Session.Project.FindAsset(prop.Asset);
        if (asset is null || asset.Data.Length == 0) return false;
        try
        {
            var doc = new GltfDocument();
            var state = new GltfState();
            if (doc.AppendFromBuffer(asset.Data, "", state) != Error.Ok) return false;
            if (doc.GenerateScene(state) is not Node3D scene) return false;
            foreach (var ap in scene.FindChildren("*", "AnimationPlayer", true, false)) ap.QueueFree();
            foreach (var mi in scene.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
                mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            root.AddChild(scene);
            root.SetMeta("imported", true);
            return true;
        }
        catch (Exception) { return false; }
    }

    void ApplyColor(Node3D root)
    {
        if (root.HasMeta("imported")) return; // keep the model's own materials
        var color = _palette.Name == "light" ? new Color("#b8862b") : new Color("#d9a441");
        foreach (var mi in root.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
            mi.MaterialOverride = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.55f };
    }

    // ---- placing ---------------------------------------------------------------------------------------------------------------

    /// <summary>Recompute every prop's world transform (PropEvaluator) and move the nodes. Cheap enough to run on every pose step.</summary>
    public void Refresh()
    {
        var project = App.Session.Project;
        var clip = App.ActiveClip;
        var time = App.CurrentTime;
        foreach (var prop in project.Props)
        {
            var id = prop.Id.ToString();
            if (!_nodes.TryGetValue(id, out var e)) continue;
            Rig? rig = null;
            CoreTransform[]? model = null;
            var owner = prop.Attachment?.Character ?? default;
            if (prop.Attachment is { } a && _characters.View(a.Character.ToString()) is { } view)
            {
                rig = view.Rig;
                model = view.Model;
            }
            var local = _edited.Contains(id) ? prop.Local : PropEvaluator.Local(prop, clip, time);
            var world = PropEvaluator.World(project, prop, local, rig, model, owner);
            _world[id] = world;
            e.Root.Transform = CharacterView.ToT3(world);
        }
        PropsChanged?.Invoke();
    }

    /// <summary>The offset (or world transform) value that puts a free-or-attached prop at <paramref name="world"/>.</summary>
    public CoreTransform LocalFromWorld(Prop prop, CoreTransform world)
    {
        var view = ViewOf(prop);
        if (prop.Attachment is { } a && view is not null)
        {
            var bone = view.Rig.Bones.FindIndex(b => b.Id == a.Bone);
            if (bone >= 0 && bone < view.Model.Length) return view.Model[bone].Inverse() * world;
        }
        return world;
    }
}
