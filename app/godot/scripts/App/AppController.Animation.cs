using System.Globalization;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Motion;
using Blazma.Rigging;
using Godot;

namespace Blazma.UI.App;

/// <summary>
/// Phase 3 part of the app controller: animation state that is UI state, not project data (active clip, current frame,
/// playing, auto-key) plus the one non-edit way to change the pose: showing another frame (<see cref="PreviewFrame"/>).
/// Scrubbing and playback never go through the undo history; keys, markers, loop and the like always do.
/// </summary>
public partial class AppController
{
    static readonly HashSet<string> PoseEditIds = new()
    {
        "pose.set_bone", "pose.set_control", "pose.set_pinned", "pose.mirror", "pose.reset", "pose.apply", "poselib.apply",
    };

    string? _activeClipId;
    string? _focusChar;
    readonly Dictionary<string, string> _activeByChar = new();
    long _frame;
    double _playFrames;
    bool _playing, _autoKey, _poseDirty, _previewing;

    /// <summary>Raised when the active clip (the clip the timeline shows and keys go into) changed.</summary>
    public event Action? ActiveClipChanged;

    /// <summary>Raised when the current frame, playing state, auto-key or the unkeyed-changes flag changed.</summary>
    public event Action? PlaybackChanged;

    /// <summary>Neutral information for the user (not an error), already translated.</summary>
    public event Action<string>? InfoRaised;

    /// <summary>Incremented whenever any clip's content may have changed (curve and trail caches key on it).</summary>
    public ulong ClipVersion { get; private set; }

    public bool Playing => _playing;
    public bool AutoKey => _autoKey;
    /// <summary>The working pose was edited since the last key or frame change and auto-key is off.</summary>
    public bool PoseDirty => _poseDirty && !_autoKey;
    public long CurrentFrame => _frame;
    public string? FocusCharacterId => _focusChar;
    public string? ActiveClipId => _activeClipId;

    public Clip? ActiveClip => _activeClipId is null ? null : Session.Project.Clips.Find(c => c.Id.ToString() == _activeClipId);
    public Character? FocusCharacter => _focusChar is null ? null : Session.Project.Characters.Find(c => c.Id.ToString() == _focusChar);

    public FrameRate ActiveFps => ActiveClip?.Fps ?? Session.Project.Settings.DefaultFrameRate;

    /// <summary>Number of frames the timeline shows (the clip length, or one second when there is no clip yet).</summary>
    public long FrameCount => ActiveClip?.DurationFrames ?? Math.Max(1, (long)Math.Round(ActiveFps.FramesPerSecond));

    /// <summary>Time of the displayed pose: exact frame time when stopped, fractional while playing.</summary>
    public Flicks CurrentTime => _playing
        ? Flicks.FromSeconds(_playFrames / ActiveFps.FramesPerSecond)
        : Flicks.FromFrame(_frame, ActiveFps);

    /// <summary>Fractional frame position (equals <see cref="CurrentFrame"/> unless playing).</summary>
    public double CurrentFrameExact => _playing ? _playFrames : _frame;

    // ---- active clip -----------------------------------------------------------------------------------------------

    public void SetActiveClip(string? id)
    {
        if (id == _activeClipId) return;
        if (_playing) StopPlaying();
        _activeClipId = id;
        RememberActive();
        ClipVersion++;
        _frame = Math.Clamp(_frame, 0, Math.Max(0, FrameCount - 1));
        _playFrames = _frame;
        ActiveClipChanged?.Invoke();
        if (id is not null) ApplyFrame(); // picking a clip shows its pose at the current frame
        PlaybackChanged?.Invoke();
    }

    /// <summary>Assign without events or previews (selection repair while the session is notifying).</summary>
    public void SetActiveClipSilent(string? id)
    {
        if (id == _activeClipId) return;
        _activeClipId = id;
        RememberActive();
        ClipVersion++;
    }

    void RememberActive()
    {
        if (_focusChar is not null)
        {
            if (_activeClipId is null) _activeByChar.Remove(_focusChar); else _activeByChar[_focusChar] = _activeClipId;
        }
        Settings.Layout[LayoutKeyForProject()] = _activeClipId ?? "";
    }

    string LayoutKeyForProject() => "active_clip_" + Session.Project.Id;

    /// <summary>Set by the main window when the selected character changes; each character remembers its own active clip.</summary>
    public void SetFocusCharacter(string? characterId)
    {
        if (characterId == _focusChar) return;
        _focusChar = characterId;
        if (characterId is not null && _activeByChar.TryGetValue(characterId, out var remembered)
            && remembered != _activeClipId && Session.Project.Clips.Any(c => c.Id.ToString() == remembered))
        {
            if (_playing) StopPlaying();
            _activeClipId = remembered;
            ClipVersion++;
            ActiveClipChanged?.Invoke();
        }
        PlaybackChanged?.Invoke();
    }

    void ResetAnimationForProject()
    {
        _playing = false;
        _frame = 0;
        _playFrames = 0;
        _poseDirty = false;
        _activeByChar.Clear();
        var clips = Session.Project.Clips;
        string? id = null;
        if (Settings.Layout.TryGetValue(LayoutKeyForProject(), out var v) && v.VariantType == Variant.Type.String)
        {
            var saved = v.AsString();
            if (clips.Any(c => c.Id.ToString() == saved)) id = saved;
        }
        id ??= clips.Count > 0 ? clips[0].Id.ToString() : null;
        _activeClipId = id;
        ClipVersion++;
    }

    // ---- frames ------------------------------------------------------------------------------------------------------

    /// <summary>Go to a frame (scrubbing). Stops playback; shows the clip's pose there when the clip has keys.</summary>
    public void GoToFrame(long frame)
    {
        if (_seqMode) { SetSequenceFrame(frame); return; }
        if (_playing) _playing = false;
        _frame = Math.Clamp(frame, 0, Math.Max(0, FrameCount - 1));
        _playFrames = _frame;
        ApplyFrame();
        PlaybackChanged?.Invoke();
    }

    public void StepFrames(int delta) => GoToFrame((_seqMode ? (long)_seqFrame : _frame) + delta);

    public void GoToStart() => GoToFrame(0);
    public void GoToEnd() => GoToFrame(_seqMode ? SequenceLastFrame : FrameCount - 1);

    /// <summary>Distinct key frames of the active clip, sorted.</summary>
    public IReadOnlyList<long> ActiveKeyFrames() => ActiveClip is { } c ? ClipEvaluator.KeyFrames(c) : Array.Empty<long>();

    public void GoToNextKey()
    {
        var next = ActiveKeyFrames().Where(f => f > _frame).Select(f => (long?)f).FirstOrDefault();
        if (next is { } n) GoToFrame(n); else InfoRaised?.Invoke(Tr("No later key."));
    }

    public void GoToPreviousKey()
    {
        var prev = ActiveKeyFrames().Where(f => f < _frame).Select(f => (long?)f).LastOrDefault();
        if (prev is { } n) GoToFrame(n); else InfoRaised?.Invoke(Tr("No earlier key."));
    }

    /// <summary>The clip has something to show (at least one key).</summary>
    public static bool HasKeys(Clip? clip) => clip is not null && clip.Tracks.Any(t => t.Keys.Count > 0);

    /// <summary>
    /// Evaluates the active clip at the current time and shows it on the focused character.
    /// Returns false when there is nothing to show (no clip, no keys, or the clip belongs to another rig).
    /// </summary>
    public bool ApplyFrame()
    {
        if (_seqMode) return false; // the sequence editor owns the pose while it is shown
        var clip = ActiveClip;
        var character = FocusCharacter;
        if (clip is null || character is null || !HasKeys(clip) || clip.Skeleton != character.Rig) return false;
        var rig = Session.Project.FindRig(character.Rig);
        if (rig is null) return false;
        var pose = ClipEvaluator.Evaluate(clip, rig, CurrentTime);
        if (!PreviewFrame(character.Id.ToString(), pose)) return false;
        _poseDirty = false;
        return true;
    }

    /// <summary>
    /// Shows <paramref name="pose"/> on a character without recording history or dirtying the project
    /// (scrub / playback). Refused while a transaction is open.
    /// </summary>
    public bool PreviewFrame(string characterId, Pose pose)
    {
        if (Session.InTransaction || !ObjectId.TryParse(characterId, out var id)) return false;
        _previewing = true;
        try
        {
            Session.ApplyPreview(p => p.GetCharacter(id).Pose = pose, new[] { new Change(Change.PoseChanged, characterId) }, CommandOrigin.Ui);
        }
        finally { _previewing = false; }
        return true;
    }

    /// <summary>After a clip edit made inside a transaction: show the clip again, unless unkeyed pose edits would be lost.</summary>
    public void ApplyFrameIfClean()
    {
        if (!_poseDirty || _autoKey) ApplyFrame();
        PlaybackChanged?.Invoke();
    }

    public void RaiseInfo(string message) => InfoRaised?.Invoke(message);

    /// <summary>Make sure there is an active clip inside the currently open transaction ("Clip N" is created if needed).</summary>
    public string? EnsureClip(out bool created) => EnsureClipInTransaction(out created);

    public void NotifyClipCreated() { ActiveClipChanged?.Invoke(); PlaybackChanged?.Invoke(); }

    /// <summary>The control plus the bones of the IK chain it drives (for keying only that control).</summary>
    public string[]? ControlKeyTargets(string characterId, string controlId) => ControlTargets(characterId, controlId);

    /// <summary>True while the controller itself is showing a frame (so listeners can skip history work).</summary>
    public bool IsPreviewing => _previewing;

    // ---- playback ----------------------------------------------------------------------------------------------------

    public void TogglePlay()
    {
        if (_seqMode) { ToggleSequencePlay(); return; }
        if (_playing) Pause(); else Play();
    }

    public void Play()
    {
        if (_playing) return;
        var clip = ActiveClip;
        if (!HasKeys(clip))
        {
            InfoRaised?.Invoke(Tr("Nothing to play yet. Pose the character and press Key at two different frames first."));
            return;
        }
        var last = Math.Max(0, clip!.DurationFrames - 1);
        var (loopStart, loopEnd) = LoopFrames(clip);
        if (clip.Loop.Enabled && (_frame < loopStart || _frame >= loopEnd)) _frame = loopStart;
        else if (!clip.Loop.Enabled && _frame >= last) _frame = 0;
        _playFrames = _frame;
        _playing = true;
        PlaybackChanged?.Invoke();
    }

    public void Pause()
    {
        if (!_playing) return;
        StopPlaying();
        ApplyFrame();
        PlaybackChanged?.Invoke();
    }

    void StopPlaying()
    {
        _playing = false;
        _frame = Math.Clamp((long)Math.Floor(_playFrames + 1e-6), 0, Math.Max(0, FrameCount - 1));
        _playFrames = _frame;
    }

    /// <summary>[start, end) of the loop range in frames (end defaults to the clip length).</summary>
    public static (long start, long end) LoopFrames(Clip clip)
    {
        var length = clip.DurationFrames;
        var start = clip.Loop.Start.ToFrame(clip.Fps);
        var end = clip.Loop.End.Value > 0 ? clip.Loop.End.ToFrame(clip.Fps) : length;
        if (end <= start) { start = 0; end = length; }
        return (Math.Max(0, start), Math.Min(length, end));
    }

    /// <summary>Advance playback by real elapsed time. Called from _Process; the self-test calls it with fixed steps.</summary>
    public void AdvancePlayback(double delta)
    {
        if (_seqPlaying) { AdvanceSequence(delta); return; }
        if (!_playing) return;
        var clip = ActiveClip;
        if (clip is null || !HasKeys(clip)) { StopPlaying(); PlaybackChanged?.Invoke(); return; }
        _playFrames += delta * clip.Fps.FramesPerSecond;
        var last = Math.Max(0, clip.DurationFrames - 1);
        var (start, end) = LoopFrames(clip);
        if (clip.Loop.Enabled && end > start)
        {
            if (_playFrames >= end) _playFrames = start + (_playFrames - start) % (end - start);
        }
        else if (_playFrames >= last)
        {
            _playing = false;
            _frame = last;
            _playFrames = last;
            ApplyFrame();
            PlaybackChanged?.Invoke();
            return;
        }
        _frame = (long)Math.Floor(_playFrames + 1e-6);
        ApplyFrame();
        PlaybackChanged?.Invoke();
    }

    // ---- keying ---------------------------------------------------------------------------------------------------------

    public void SetAutoKey(bool on)
    {
        if (_autoKey == on) return;
        _autoKey = on;
        PlaybackChanged?.Invoke();
    }

    string NextClipNameForNew()
    {
        var clips = Session.Project.Clips;
        var n = clips.Count + 1;
        while (clips.Any(c => c.Name == $"Clip {n}")) n++;
        return $"Clip {n}";
    }

    /// <summary>Makes sure there is an active clip, creating "Clip N" inside the open transaction if not.</summary>
    string? EnsureClipInTransaction(out bool created)
    {
        created = false;
        if (ActiveClip is { } existing) return existing.Id.ToString();
        var r = Execute("clip.create", new JsonObject { ["name"] = NextClipNameForNew() });
        if (!r.Ok || (string?)r.Data?["clipId"] is not { } id) return null;
        _activeClipId = id;
        RememberActive();
        ClipVersion++;
        created = true;
        return id;
    }

    /// <summary>
    /// Key the working pose at the current frame (all bones and controls, or only <paramref name="targets"/>: names or ids).
    /// With no clip yet, "Clip N" is created first, in the same undo step.
    /// </summary>
    public bool KeyAtCurrent(IReadOnlyList<string>? targets = null, long? atFrame = null)
    {
        if (FocusCharacter is not { } character)
        {
            InfoRaised?.Invoke(Tr("There is no character to key. Use Rigging > Add Mannequin."));
            return false;
        }
        if (_playing) Pause();
        var previousClip = _activeClipId;
        bool created;
        bool ok;
        using (var tx = BeginTransaction("Set key"))
        {
            var clipId = EnsureClipInTransaction(out created);
            ok = clipId is not null && RunKeySet(clipId, character.Id.ToString(), targets, atFrame);
            if (!ok)
            {
                tx.Rollback();
                if (created) { _activeClipId = previousClip; RememberActive(); }
            }
            else tx.Commit();
        }
        if (!ok) return false;
        _poseDirty = false;
        if (created) ActiveClipChanged?.Invoke();
        PlaybackChanged?.Invoke();
        return true;
    }

    bool RunKeySet(string clipId, string characterId, IReadOnlyList<string>? targets, long? atFrame = null)
    {
        var args = new JsonObject { ["clipId"] = clipId, ["characterId"] = characterId, ["frame"] = atFrame ?? _frame };
        if (targets is { Count: > 0 }) args["bones"] = string.Join(",", targets);
        var r = Execute("key.set", args);
        if (r.Warnings.Any(w => w.Code == "track_locked")) InfoRaised?.Invoke(Tr("Some locked tracks were skipped."));
        return r.Ok;
    }

    /// <summary>Delete all keys at the current frame.</summary>
    public bool DeleteKeysAtCurrent()
    {
        var clip = ActiveClip;
        if (clip is null || !ActiveKeyFrames().Contains(_frame))
        {
            InfoRaised?.Invoke(Tr("There are no keys at this frame."));
            return false;
        }
        var r = Execute("key.delete", new JsonObject { ["clipId"] = clip.Id.ToString(), ["frames"] = _frame.ToString(CultureInfo.InvariantCulture) });
        if (r.Ok) { ApplyFrame(); PlaybackChanged?.Invoke(); }
        return r.Ok;
    }

    /// <summary>Loop on/off for the active clip (keeps the range).</summary>
    public bool SetLoop(bool enabled, long? startFrame = null, long? endFrame = null)
    {
        if (ActiveClip is not { } clip) return false;
        var args = new JsonObject { ["clipId"] = clip.Id.ToString(), ["enabled"] = enabled };
        if (startFrame is { } s) args["startFrame"] = s;
        if (endFrame is { } e) args["endFrame"] = e;
        return Execute("clip.set_loop", args).Ok;
    }

    // ---- auto-key ------------------------------------------------------------------------------------------------------

    CommandResult ExecutePoseEdit(ICommand command, string commandId, JsonObject? args)
    {
        if (_playing) Pause();
        var characterId = (string?)args?["characterId"];
        if (!_autoKey || Session.InTransaction || characterId is null)
            return Report(Session.Execute(command, CommandOrigin.Ui));

        var previousClip = _activeClipId;
        CommandResult result;
        bool created = false;
        using (var tx = BeginTransaction(command.Label))
        {
            result = Session.Execute(command, CommandOrigin.Ui);
            if (!result.Ok) tx.Rollback();
            else
            {
                var clipId = EnsureClipInTransaction(out created);
                if (clipId is not null) RunKeySet(clipId, characterId, AutoKeyTargets(commandId, characterId, args));
                tx.Commit();
            }
        }
        if (result.Ok)
        {
            _poseDirty = false;
            if (created) ActiveClipChanged?.Invoke();
            PlaybackChanged?.Invoke();
        }
        else if (created) { _activeClipId = previousClip; }
        return Report(result);
    }

    /// <summary>Called by a gizmo drag just before its transaction commits, so the drag and its key are one undo step.</summary>
    public void AutoKeyInOpenTransaction(string characterId, string? boneId, string? controlId)
    {
        if (!_autoKey || !Session.InTransaction) return;
        var clipId = EnsureClipInTransaction(out var created);
        if (clipId is null) return;
        var targets = boneId is not null
            ? new[] { boneId }
            : controlId is not null ? ControlTargets(characterId, controlId) : null;
        RunKeySet(clipId, characterId, targets);
        _poseDirty = false;
        if (created) CallDeferred(nameof(NotifyActiveClipChanged));
    }

    void NotifyActiveClipChanged() { ActiveClipChanged?.Invoke(); PlaybackChanged?.Invoke(); }

    string[]? AutoKeyTargets(string commandId, string characterId, JsonObject? args)
    {
        if (commandId == "pose.set_bone" && (string?)args?["bone"] is { } bone) return new[] { bone };
        if (commandId is "pose.set_control" or "pose.set_pinned" && (string?)args?["control"] is { } control)
            return ControlTargets(characterId, control);
        return null; // mirror, reset, apply: the whole pose changed
    }

    /// <summary>The control plus the bones of the IK chain it drives.</summary>
    string[]? ControlTargets(string characterId, string controlIdOrName)
    {
        var character = Session.Project.Characters.Find(c => c.Id.ToString() == characterId);
        var rig = character is null ? null : Session.Project.FindRig(character.Rig);
        var control = rig?.Controls.Find(c => c.Id.ToString() == controlIdOrName || c.Name == controlIdOrName);
        if (rig is null || control is null) return null;
        var list = new List<string> { control.Id.ToString() };
        foreach (var chain in rig.IkChains.Where(ch => ch.Target == control.Id || ch.Pole == control.Id))
        {
            list.Add(chain.Upper.ToString());
            list.Add(chain.Lower.ToString());
            list.Add(chain.End.ToString());
        }
        return list.ToArray();
    }

    // ---- session hooks ------------------------------------------------------------------------------------------------------

    void BeforeSessionNotify(SessionChangedArgs args)
    {
        if (args.Changes.Any(c => c.Kind == Change.ProjectReplaced)) ResetAnimationForProject();
        if (_previewing) return;
        if (args.Changes.Any(c => c.Kind == Change.PoseChanged) && !_poseDirty)
        {
            _poseDirty = true;
            PlaybackChanged?.Invoke(); // the Key button shows its unkeyed-changes dot
        }
        if (args.Changes.Any(c => c.Kind is Change.ClipChanged or Change.ClipAdded or Change.ClipRemoved)) ClipVersion++;
    }

    void AfterSessionNotify(SessionChangedArgs args)
    {
        if (_previewing || Session.InTransaction) return;
        if (args.Changes.Any(c => c.Kind == Change.ProjectReplaced))
        {
            PlaybackChanged?.Invoke();
            return;
        }
        var touchesActive = args.Changes.Any(c => c.Kind == Change.ClipChanged && c.TargetId == _activeClipId)
            || args.Changes.Any(c => c.Kind == Change.ClipRemoved);
        if (touchesActive)
        {
            var clamped = Math.Clamp(_frame, 0, Math.Max(0, FrameCount - 1));
            if (clamped != _frame) { _frame = clamped; _playFrames = clamped; }
            // Re-show the clip so edits (move, delete, undo, tangents) are visible. Unkeyed pose edits are never overwritten by this.
            if (!_poseDirty || _autoKey) ApplyFrame();
        }
        if (touchesActive || args.Changes.Any(c => c.Kind == Change.PoseChanged)) PlaybackChanged?.Invoke();
    }
}
