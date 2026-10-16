using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Keeps one <see cref="CharacterView"/> per project character under the stage's world.
/// Views are rebuilt only when characters/rigs are added or removed; a pose change only re-applies the pose
/// of the affected character.
/// </summary>
public partial class CharacterLayer : Node3D
{
    readonly Dictionary<string, CharacterView> _views = new();
    Palette _palette = Palette.Dark;
    string? _signature;

    /// <summary>Raised after views were added, removed or re-posed (overlay and inspector redraw).</summary>
    public event Action? ViewsChanged;

    static AppController App => AppController.Instance;

    public IReadOnlyDictionary<string, CharacterView> Views => _views;
    public CharacterView? View(string? characterId) => characterId is not null && _views.TryGetValue(characterId, out var v) ? v : null;

    public void SetPalette(Palette p)
    {
        _palette = p;
        foreach (var v in _views.Values) v.ApplyPalette(p);
    }

    /// <summary>Handle a session change. Returns true if anything visible changed.</summary>
    public void OnSessionChanged(SessionChangedArgs args)
    {
        var project = App.Session.Project;
        var replaced = args.Changes.Any(c => c.Kind == Change.ProjectReplaced);
        var structural = replaced || args.Changes.Any(c => c.Kind is Change.CharacterAdded or Change.CharacterRemoved);
        if (structural) { Rebuild(project, replaced); return; }

        var rigChanged = args.Changes.Where(c => c.Kind == Change.RigChanged).Select(c => c.TargetId).ToHashSet();
        var posed = args.Changes.Where(c => c.Kind == Change.PoseChanged).Select(c => c.TargetId).ToHashSet();
        var touched = false;
        foreach (var (id, view) in _views)
        {
            var character = project.Characters.Find(c => c.Id.ToString() == id);
            if (character is null) continue;
            var rig = project.FindRig(character.Rig);
            if (rig is null) continue;
            if (posed.Contains(id) || rigChanged.Contains(rig.Id.ToString()))
            {
                view.SetCharacter(character, rig);
                view.ApplyPose();
                touched = true;
            }
        }
        if (touched) ViewsChanged?.Invoke();
    }

    public void Rebuild(Project project, bool force = false)
    {
        var wanted = project.Characters.Select(c => c.Id.ToString()).ToList();
        // Views whose character vanished, or that belong to a different project object, are dropped.
        foreach (var id in _views.Keys.ToList())
        {
            var character = project.Characters.Find(c => c.Id.ToString() == id);
            if (force || character is null || !ReferenceEquals(_views[id].Character, character) || !ReferenceEquals(_views[id].Rig, project.FindRig(character.Rig)))
            {
                RemoveChild(_views[id]);
                _views[id].QueueFree();
                _views.Remove(id);
            }
        }
        foreach (var character in project.Characters)
        {
            var id = character.Id.ToString();
            if (_views.ContainsKey(id)) continue;
            var rig = project.FindRig(character.Rig);
            if (rig is null) continue;
            var view = new CharacterView(character, rig);
            AddChild(view);
            view.Build(project, _palette);
            _views[id] = view;
        }
        ViewsChanged?.Invoke();
    }

    /// <summary>Bounding box of a character's joints plus a margin for the body, in world space.</summary>
    public Aabb? Bounds(string? characterId)
    {
        var view = View(characterId);
        if (view is null || view.Joints.Length == 0) return null;
        var box = new Aabb(view.Joints[0], Vector3.Zero);
        foreach (var j in view.Joints) box = box.Expand(j);
        // The mesh extends past the joints (head above the last joint, feet below the ankles).
        var margin = Mathf.Max(0.12f, 0.06f * box.Size.Length());
        box = box.Grow(margin);
        return box.Expand(new Vector3(box.GetCenter().X, box.End.Y + 0.1f * box.Size.Y, box.GetCenter().Z));
    }
}
