using Blazma.Commands;
using Blazma.Core.Model;
using Blazma.Physics;
using Blazma.Rigging;

namespace Blazma.UI.App;

/// <summary>
/// Phase 6: balance analysis of the active clip and of the working pose, cached for the viewport overlay, the timeline balance
/// strip and the Physics panel. Nothing is computed until a view asks, and a result is reused until the clip (keys, length),
/// the rig, the character or the active clip changes; scrubbing and playback only look frames up. Calls the Blazma.Physics
/// library directly (the same code the physics.* commands run) because the views need per-frame polygons and contact points.
/// </summary>
public sealed class PhysicsAnalysis
{
    readonly AppController _app;
    ulong _rigVersion, _poseVersion;

    BalanceAnalyzer? _an;
    Rig? _anRig;
    ulong _anVersion = ulong.MaxValue;

    ClipBalanceReport? _report;
    string? _reportKey;
    TrajectorySet? _paths;
    string? _pathsKey;
    PoseBalance? _pose;
    string? _poseKey;

    public PhysicsAnalysis(AppController app)
    {
        _app = app;
        app.Session.Changed += OnChanged;
    }

    /// <summary>Raised when cached results were dropped (views redraw).</summary>
    public event Action? Invalidated;

    /// <summary>How many times the whole clip was analysed / the working pose was analysed (tests check that scrubbing does not recompute).</summary>
    public int ClipComputations { get; private set; }
    public int PoseComputations { get; private set; }
    public int PathComputations { get; private set; }

    void OnChanged(SessionChangedArgs args)
    {
        var rig = false;
        var pose = false;
        foreach (var c in args.Changes)
        {
            switch (c.Kind)
            {
                case Change.RigChanged or Change.ProjectReplaced or Change.CharacterAdded or Change.CharacterRemoved:
                    rig = true; pose = true; break;
                case Change.PoseChanged:
                    pose = true; break;
            }
        }
        if (rig) _rigVersion++;
        if (pose) _poseVersion++;
        // Clip edits are noticed through AppController.ClipVersion when the next result is asked for.
        if (rig || pose || args.Changes.Any(c => c.Kind is Change.ClipChanged or Change.ClipAdded or Change.ClipRemoved)) Invalidated?.Invoke();
    }

    // ---- what is analysed ---------------------------------------------------------------------------------------------

    /// <summary>The clip being analysed: the active clip when it has keys and belongs to the focused character's rig.</summary>
    public bool TryResolve(out Clip clip, out Character character, out Rig rig)
    {
        clip = null!; character = null!; rig = null!;
        var c = _app.ActiveClip;
        var ch = _app.FocusCharacter;
        if (c is null || ch is null || !AppController.HasKeys(c) || c.Skeleton != ch.Rig) return false;
        var r = _app.Session.Project.FindRig(ch.Rig);
        if (r is null) return false;
        clip = c; character = ch; rig = r;
        return true;
    }

    /// <summary>Analyser for the focused character's rig (null without a character).</summary>
    public BalanceAnalyzer? Analyzer
    {
        get
        {
            var ch = _app.FocusCharacter;
            var rig = ch is null ? null : _app.Session.Project.FindRig(ch.Rig);
            if (rig is null) return null;
            if (_an is null || !ReferenceEquals(_anRig, rig) || _anVersion != _rigVersion)
            {
                _an = new BalanceAnalyzer(rig);
                _anRig = rig;
                _anVersion = _rigVersion;
            }
            return _an;
        }
    }

    /// <summary>The rig has feet the analysis can use (humanoid-mapped foot bones).</summary>
    public bool HasFeet => Analyzer is { } a && a.Feet.Feet.Count > 0;

    /// <summary>Balance report of the whole active clip, or null when there is nothing to analyse.</summary>
    public ClipBalanceReport? Report
    {
        get
        {
            if (!TryResolve(out var clip, out var character, out var rig) || Analyzer is not { } an || an.Feet.Feet.Count == 0) return null;
            var key = $"{clip.Id}|{character.Id}|{_app.ClipVersion}|{_rigVersion}|{clip.DurationFrames}";
            if (key != _reportKey || _report is null)
            {
                _report = an.AnalyzeClip(clip);
                _reportKey = key;
                ClipComputations++;
            }
            return _report;
        }
    }

    /// <summary>COM, hips and feet paths over the active clip.</summary>
    public TrajectorySet? Paths
    {
        get
        {
            if (!TryResolve(out var clip, out var character, out _) || Analyzer is not { } an || an.Feet.Feet.Count == 0) return null;
            var key = $"{clip.Id}|{character.Id}|{_app.ClipVersion}|{_rigVersion}|{clip.DurationFrames}";
            if (key != _pathsKey || _paths is null)
            {
                _paths = Trajectories.Compute(an, clip);
                _pathsKey = key;
                PathComputations++;
            }
            return _paths;
        }
    }

    /// <summary>The balance of one frame of the active clip (null outside the clip).</summary>
    public PoseBalance? FrameAt(long frame)
    {
        var r = Report;
        if (r is null) return null;
        var i = frame - r.StartFrame;
        return i >= 0 && i < r.Frames.Count ? r.Frames[(int)i] : null;
    }

    /// <summary>Static balance of the focused character's working pose.</summary>
    public PoseBalance? WorkingPose
    {
        get
        {
            var ch = _app.FocusCharacter;
            if (ch is null || Analyzer is not { } an || an.Feet.Feet.Count == 0) return null;
            var key = $"{ch.Id}|{_poseVersion}|{_rigVersion}";
            if (key != _poseKey || _pose is null)
            {
                _pose = an.AnalyzePose(ch.Pose, _app.CurrentFrame);
                _poseKey = key;
                PoseComputations++;
            }
            return _pose;
        }
    }

    /// <summary>
    /// What the viewport and the panel show right now: the evaluated clip frame while playing or while the shown pose is the
    /// clip's own frame, the working pose as soon as it was edited (and when there is no usable clip).
    /// </summary>
    public PoseBalance? Current
    {
        get
        {
            if (_app.Playing || !_app.PoseDirty)
                if (FrameAt(_app.CurrentFrame) is { } f) return f;
            return WorkingPose;
        }
    }

    /// <summary>True when <see cref="Current"/> comes from the clip (velocity-aware contacts, ZMP) and not from the working pose.</summary>
    public bool CurrentFromClip => (_app.Playing || !_app.PoseDirty) && FrameAt(_app.CurrentFrame) is not null;

    // ---- text helpers --------------------------------------------------------------------------------------------------

    /// <summary>Margin in centimetres, one decimal; null for an airborne frame.</summary>
    public static double? MarginCm(PoseBalance b) => double.IsFinite(b.Margin) ? Math.Round(b.Margin * 100, 1) : null;
}
