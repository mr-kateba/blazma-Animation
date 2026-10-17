using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Motion;
using Blazma.Physics;
using Godot;

namespace Blazma.UI.App;

/// <summary>Foot contacts of the active clip as the timeline strip shows them.</summary>
public sealed record ContactInfo(IReadOnlyList<FootContactSpan> Spans, long FirstFrame, long LastFrame);

/// <summary>
/// Phase 8 part of the app controller: UI state that is not project data. Sequence playback (the sequence editor plays the blended
/// sequence in the viewport instead of the clip), the cached foot contacts behind the timeline strip, and keying props.
/// The project data itself (sequences, props, templates) is only ever changed through commands; undo and redo replace those
/// lists, so everything here looks objects up by id whenever it needs them.
/// </summary>
public partial class AppController
{
    // ---- foot contacts ---------------------------------------------------------------------------------------------------------

    (string key, ContactInfo? info)? _contactCache;

    /// <summary>Options the contact strip (and the Foot lock dialog) detect with.</summary>
    public FootLockOptions ContactOptions { get; private set; } = new();

    /// <summary>Raised when the contact options or the visibility of the strip changed.</summary>
    public event Action? ContactsChanged;

    public void SetContactOptions(FootLockOptions options)
    {
        ContactOptions = options;
        _contactCache = null;
        ContactsChanged?.Invoke();
    }

    public void SetShowContacts(bool show)
    {
        Settings.ShowContacts = show;
        Settings.Save();
        SettingsChanged?.Invoke();
        ContactsChanged?.Invoke();
    }

    public void SetPropsPanel(bool open)
    {
        Settings.PropsPanel = open;
        Settings.Save();
        SettingsChanged?.Invoke();
    }

    /// <summary>Contact spans of the active clip on the focused character (null when the clip cannot be analysed). Cached until the clip, rig or options change.</summary>
    public ContactInfo? Contacts()
    {
        if (!Physics.TryResolve(out var clip, out var character, out var rig) || !Physics.HasFeet) return null;
        var o = ContactOptions;
        var key = string.Join("|", clip.Id, ClipVersion, character.Id, rig.Id, o.ContactHeight.ToString("R", CultureInfo.InvariantCulture),
            o.ContactSpeed.ToString("R", CultureInfo.InvariantCulture), o.MinFrames, o.MergeGap);
        if (_contactCache is { } c && c.key == key) return c.info;
        ContactInfo? info = null;
        try
        {
            var spans = FootLocker.DetectContacts(clip, rig, o);
            info = new ContactInfo(spans, 0, Math.Max(0, clip.DurationFrames - 1));
        }
        catch (Exception e) when (e is not OutOfMemoryException) { GD.PushWarning($"Contact detection failed: {e.Message}"); }
        _contactCache = (key, info);
        return info;
    }

    // ---- sequences ---------------------------------------------------------------------------------------------------------------------

    string? _seqId;
    bool _seqMode, _seqPlaying;
    double _seqFrame;
    ulong _seqVersion;
    SequenceEvaluator? _seqEval;
    ulong _seqEvalVersion = ulong.MaxValue;
    string? _seqEvalFor;
    string? _seqError;

    /// <summary>Raised when the active sequence, or any sequence / clip it depends on, changed (the editor rebuilds).</summary>
    public event Action? SequenceStateChanged;

    /// <summary>Raised when the sequence playhead moved or sequence playback started or stopped (the editor redraws).</summary>
    public event Action? SequenceFrameChanged;

    /// <summary>True while the sequence editor tab is shown: transport keys and Play drive the sequence, not the clip.</summary>
    public bool SequenceMode => _seqMode;
    public bool SequencePlaying => _seqPlaying;
    public double SequenceFrame => _seqFrame;

    /// <summary>The sequence being edited: the chosen one, else the first of the project.</summary>
    public ClipSequence? ActiveSequence
    {
        get
        {
            var all = Session.Project.Sequences;
            return (_seqId is null ? null : all.Find(s => s.Id.ToString() == _seqId)) ?? (all.Count > 0 ? all[0] : null);
        }
    }

    public string? ActiveSequenceId => ActiveSequence?.Id.ToString();

    public void SetActiveSequence(string? id)
    {
        if (id == _seqId) return;
        StopSequence();
        _seqId = id;
        _seqFrame = 0;
        _seqVersion++;
        if (_seqMode) ShowSequenceFrame();
        SequenceStateChanged?.Invoke();
        SequenceFrameChanged?.Invoke();
    }

    /// <summary>The evaluator for the active sequence, built once per change (null with the reason in <see cref="SequenceError"/>).</summary>
    public SequenceEvaluator? SequenceEval()
    {
        var seq = ActiveSequence;
        if (seq is null) { _seqError = null; _seqEval = null; return null; }
        var key = seq.Id.ToString();
        if (_seqEval is not null && _seqEvalVersion == _seqVersion && _seqEvalFor == key) return _seqEval;
        _seqEvalVersion = _seqVersion;
        _seqEvalFor = key;
        try { _seqEval = new SequenceEvaluator(Session.Project, seq); _seqError = null; }
        catch (SequenceException e) { _seqEval = null; _seqError = e.Code; }
        return _seqEval;
    }

    /// <summary>Error code of the last failed evaluator build ("empty_sequence", "not_found"...), or null.</summary>
    public string? SequenceError { get { SequenceEval(); return _seqError; } }

    public long SequenceLastFrame => SequenceEval()?.LastFrame ?? 0;

    public void SetSequenceMode(bool on)
    {
        if (_seqMode == on) return;
        if (_playing) StopPlaying();
        _seqMode = on;
        if (on) { _seqVersion++; ShowSequenceFrame(); }
        else
        {
            _seqPlaying = false;
            ApplyFrame();
        }
        PlaybackChanged?.Invoke();
        SequenceFrameChanged?.Invoke();
    }

    /// <summary>Show the blended pose of the sequence at the playhead on its character (a preview: no history, not dirty).</summary>
    public bool ShowSequenceFrame()
    {
        var seq = ActiveSequence;
        var ev = SequenceEval();
        if (seq is null || ev is null) return false;
        var sample = ev.Evaluate(Math.Clamp(_seqFrame, 0, ev.LastFrame));
        return PreviewFrame(seq.Character.ToString(), sample.Pose);
    }

    public void SetSequenceFrame(double frame)
    {
        _seqPlaying = false;
        var last = SequenceLastFrame;
        _seqFrame = Math.Clamp(Math.Round(frame), 0, Math.Max(0, last));
        ShowSequenceFrame();
        SequenceFrameChanged?.Invoke();
    }

    public void PlaySequence()
    {
        if (_seqPlaying) return;
        var ev = SequenceEval();
        if (ev is null)
        {
            InfoRaised?.Invoke(Tr("Add a clip to the sequence first."));
            return;
        }
        if (_playing) StopPlaying();
        if (_seqFrame >= ev.LastFrame) _seqFrame = 0;
        _seqPlaying = true;
        SequenceFrameChanged?.Invoke();
    }

    public void StopSequence()
    {
        if (!_seqPlaying) return;
        _seqPlaying = false;
        _seqFrame = Math.Round(_seqFrame);
        ShowSequenceFrame();
        SequenceFrameChanged?.Invoke();
    }

    public void ToggleSequencePlay()
    {
        if (_seqPlaying) StopSequence(); else PlaySequence();
    }

    /// <summary>Advance sequence playback by real elapsed time (called from AdvancePlayback; the self-test calls it with fixed steps).</summary>
    void AdvanceSequence(double delta)
    {
        var seq = ActiveSequence;
        var ev = SequenceEval();
        if (seq is null || ev is null) { _seqPlaying = false; SequenceFrameChanged?.Invoke(); return; }
        _seqFrame += delta * seq.Fps.FramesPerSecond;
        if (_seqFrame >= ev.LastFrame)
        {
            _seqFrame = ev.LastFrame;
            _seqPlaying = false;
        }
        ShowSequenceFrame();
        SequenceFrameChanged?.Invoke();
    }

    void Phase8Before(SessionChangedArgs args)
    {
        var replaced = false;
        foreach (var c in args.Changes)
        {
            switch (c.Kind)
            {
                case Change.ProjectReplaced:
                    replaced = true;
                    _seqVersion++;
                    _contactCache = null;
                    break;
                case Change.SequenceChanged or Change.ClipChanged or Change.ClipAdded or Change.ClipRemoved or Change.CharacterAdded or Change.CharacterRemoved:
                    _seqVersion++;
                    break;
                case Change.RigChanged:
                    _seqVersion++;
                    _contactCache = null;
                    break;
            }
        }
        if (replaced) { _seqPlaying = false; _seqFrame = 0; _seqId = null; }
    }

    void Phase8After(SessionChangedArgs args)
    {
        if (_previewing) return;
        if (!args.Changes.Any(c => c.Kind is Change.SequenceChanged or Change.ClipChanged or Change.ClipAdded or Change.ClipRemoved
            or Change.RigChanged or Change.CharacterAdded or Change.CharacterRemoved or Change.ProjectReplaced)) return;
        var last = SequenceLastFrame;
        if (_seqFrame > last) _seqFrame = Math.Max(0, last);
        if (_seqMode && !_seqPlaying && !Session.InTransaction) ShowSequenceFrame();
        SequenceStateChanged?.Invoke();
        SequenceFrameChanged?.Invoke();
    }

    // ---- props ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Key the selected prop's current transform at the current frame (key.set with props only; the character is not keyed).
    /// With no clip yet, "Clip N" is created first, in the same undo step.
    /// </summary>
    public bool KeyPropAtCurrent(string propId, long? atFrame = null)
    {
        if (_playing) Pause();
        var previousClip = _activeClipId;
        bool created;
        bool ok;
        using (var tx = BeginTransaction("Set key"))
        {
            var clipId = EnsureClipInTransaction(out created);
            ok = clipId is not null && Execute("key.set", new JsonObject { ["clipId"] = clipId, ["frame"] = atFrame ?? _frame, ["props"] = propId }).Ok;
            if (!ok)
            {
                tx.Rollback();
                if (created) { _activeClipId = previousClip; RememberActive(); }
            }
            else tx.Commit();
        }
        if (!ok) return false;
        if (created) ActiveClipChanged?.Invoke();
        PlaybackChanged?.Invoke();
        return true;
    }

    /// <summary>Called by a prop gizmo drag just before its transaction commits, so the drag and its key are one undo step (auto-key).</summary>
    public void AutoKeyPropInOpenTransaction(string propId)
    {
        if (!_autoKey || !Session.InTransaction) return;
        var clipId = EnsureClipInTransaction(out var created);
        if (clipId is null) return;
        Execute("key.set", new JsonObject { ["clipId"] = clipId, ["frame"] = _frame, ["props"] = propId });
        if (created) CallDeferred(nameof(NotifyActiveClipChanged));
    }
}
