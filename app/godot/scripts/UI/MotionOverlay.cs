using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;
using Blazma.UI.App;
using Godot;

namespace Blazma.UI.Views;

/// <summary>
/// Viewport extras drawn in screen space under the skeleton overlay: the motion trail of the selected bone over the clip
/// (frame ticks, keyed frames highlighted) and onion-skin ghosts at -N / +N frames. Both are recomputed only when the
/// clip, the selection or the frame changes, never per camera move.
/// </summary>
public partial class MotionOverlay : Control
{
    readonly SkeletonOverlay _proj;
    readonly CharacterLayer _layer;
    readonly SelectionState _sel;
    Transform3D _lastCam;
    Vector2 _lastSize;

    // trail cache
    ulong _trailVersion = ulong.MaxValue;
    string? _trailKey;
    Vec3[] _trail = Array.Empty<Vec3>();
    SortedSet<long> _trailKeys = new();

    // onion cache
    string? _onionKey;
    ulong _onionVersion = ulong.MaxValue;
    Vector3[]? _ghostPast, _ghostFuture;
    int[] _parents = Array.Empty<int>();

    static AppController App => AppController.Instance;
    Palette P => _proj.Pal;

    /// <summary>Trail and onion points of the last draw (for tests): trail point count, ghost joint counts.</summary>
    public int TrailPointCount => _trail.Length;
    public bool HasPastGhost => _ghostPast is not null;
    public bool HasFutureGhost => _ghostFuture is not null;
    public Vector3[]? PastGhost => _ghostPast;
    public Vector3[]? FutureGhost => _ghostFuture;
    public IReadOnlyCollection<long> TrailKeyFrames => _trailKeys;

    public MotionOverlay(SkeletonOverlay projector, CharacterLayer layer, SelectionState sel)
    {
        _proj = projector; _layer = layer; _sel = sel;
        MouseFilter = MouseFilterEnum.Ignore;
        LayoutDirection = LayoutDirectionEnum.Ltr;
        ClipContents = true;
        App.PlaybackChanged += QueueRedraw;
        App.SettingsChanged += QueueRedraw;
        App.SessionChanged += _ => QueueRedraw();
        sel.Changed += QueueRedraw;
    }

    public override void _Process(double delta)
    {
        var cam = _proj.Cam.GlobalTransform;
        if (!cam.IsEqualApprox(_lastCam) || Size != _lastSize) { _lastCam = cam; _lastSize = Size; QueueRedraw(); }
    }

    ObjectId? TrailBone(Rig rig)
    {
        if (_sel.BoneId is { } b && ObjectId.TryParse(b, out var id)) return id;
        if (_sel.ControlId is { } c && ObjectId.TryParse(c, out var cid))
            return rig.IkChains.Find(ch => ch.Target == cid || ch.Pole == cid)?.End;
        return null;
    }

    public override void _Draw()
    {
        var s = App.Settings;
        if (!s.ShowTrail && !s.ShowOnion) return;
        var clip = App.ActiveClip;
        var view = _layer.View(_sel.CharacterId);
        if (clip is null || view is null || clip.Skeleton != view.Rig.Id || !AppController.HasKeys(clip)) return;
        var rig = view.Rig;
        if (s.ShowOnion) DrawOnion(clip, rig, view);
        if (s.ShowTrail) DrawTrail(clip, rig, view);
    }

    // ---- trail ----------------------------------------------------------------------------------------------------------

    void DrawTrail(Clip clip, Rig rig, CharacterView view)
    {
        if (TrailBone(rig) is not { } bone) return;
        var last = Math.Max(0, clip.DurationFrames - 1);
        var key = $"{clip.Id}|{bone}|{view.CharacterId}";
        if (key != _trailKey || _trailVersion != App.ClipVersion)
        {
            _trailKey = key; _trailVersion = App.ClipVersion;
            _trail = ClipEvaluator.SampleBonePath(clip, rig, bone, 0, last);
            _trailKeys = ClipKeys.Frames(clip, clip.Tracks.Where(t => t.Target.Id == bone || IkInvolves(rig, t.Target.Id, bone)));
        }
        if (_trail.Length < 2) return;
        var p = P;
        var pts = new List<Vector2>(_trail.Length);
        var screen = new Vector2?[_trail.Length];
        for (var i = 0; i < _trail.Length; i++)
        {
            if (_proj.Project(CharacterView.ToV3(_trail[i]), out var sp)) { screen[i] = sp; pts.Add(sp); }
        }
        if (pts.Count >= 2)
        {
            DrawPolyline(pts.ToArray(), p.Bg with { A = 0.6f }, 4.2f, true);
            DrawPolyline(pts.ToArray(), p.Accent with { A = 0.9f }, 2.2f, true);
        }
        for (var i = 0; i < screen.Length; i++)
        {
            if (screen[i] is not { } sp) continue;
            if (_trailKeys.Contains(i))
            {
                DrawCircle(sp, 5.2f, p.Bg);
                DrawCircle(sp, 3.8f, p.Accent.Lightened(0.15f));
            }
            else DrawCircle(sp, 1.7f, p.Accent with { A = 0.9f });
        }
        var cur = Math.Clamp(App.CurrentFrame, 0, _trail.Length - 1);
        if (screen[cur] is { } c)
        {
            DrawArc(c, 7.5f, 0, Mathf.Tau, 24, p.Text, 1.8f, true);
        }
    }

    static bool IkInvolves(Rig rig, ObjectId target, ObjectId bone) =>
        rig.IkChains.Any(ch => ch.End == bone && (ch.Target == target || ch.Pole == target));

    // ---- onion skin ------------------------------------------------------------------------------------------------------

    void DrawOnion(Clip clip, Rig rig, CharacterView view)
    {
        var n = App.Settings.OnionFrames;
        var frame = App.CurrentFrame;
        var last = Math.Max(0, clip.DurationFrames - 1);
        var key = $"{clip.Id}|{view.CharacterId}|{frame}|{n}";
        if (key != _onionKey || _onionVersion != App.ClipVersion)
        {
            _onionKey = key; _onionVersion = App.ClipVersion;
            var ev = view.Evaluator;
            _parents = Enumerable.Range(0, rig.Bones.Count).Select(ev.ParentOf).ToArray();
            _ghostPast = frame - n >= 0 ? Joints(clip, rig, ev, frame - n) : null;
            _ghostFuture = frame + n <= last ? Joints(clip, rig, ev, frame + n) : null;
        }
        var p = P;
        if (_ghostPast is not null) DrawGhost(_ghostPast, p.Z);
        if (_ghostFuture is not null) DrawGhost(_ghostFuture, p.Y);
    }

    static Vector3[] Joints(Clip clip, Rig rig, RigEvaluator ev, long frame)
    {
        var pose = ClipEvaluator.Evaluate(clip, rig, Flicks.FromFrame(frame, clip.Fps));
        var model = ev.Model(pose);
        return model.Select(t => CharacterView.ToV3(t.Translation)).ToArray();
    }

    void DrawGhost(Vector3[] joints, Color color)
    {
        var line = color with { A = 0.85f };
        for (var i = 0; i < joints.Length && i < _parents.Length; i++)
        {
            var parent = _parents[i];
            if (parent < 0) continue;
            if (!_proj.Project(joints[parent], out var a) || !_proj.Project(joints[i], out var b)) continue;
            DrawLine(a, b, color with { A = 0.22f }, 8f, true);
            DrawLine(a, b, line, 3f, true);
        }
        for (var i = 0; i < joints.Length; i++)
            if (_proj.Project(joints[i], out var s)) DrawCircle(s, 2.6f, color with { A = 0.75f });
    }
}
