using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Motion;

/// <summary>A sequence that cannot be evaluated, with a stable code (not_found, rig_mismatch, empty_sequence).</summary>
public sealed class SequenceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <param name="Weight">Final contribution to the blended pose (all weights sum to 1).</param>
/// <param name="ClipFrame">Clip frame played (fractional, the clip's frame rate).</param>
/// <param name="Cycle">Repeat index (0-based) of the item's [In, Out) range.</param>
public sealed record SequenceContribution(ObjectId ItemId, ObjectId ClipId, double Weight, double ClipFrame, int Cycle);

public sealed record SequenceSample(double Frame, Pose Pose, IReadOnlyList<SequenceContribution> Active);

/// <summary>
/// Evaluates a <see cref="ClipSequence"/> (docs/ARCHITECTURE.md §8.3):
/// <list type="bullet">
/// <item><b>Timing:</b> item i plays clip time <c>In + (t − Start)·Speed</c>, repeated <c>Loops</c> times over [In, Out) (Out defaults to the
/// loop end of a looping clip, else its length). For a looping clip without an explicit Out and In inside the loop range, a pass is
/// the whole loop range and In only sets the starting phase (playback wraps at the loop end, as in the clip's own playback). Before it
/// starts it holds its first pose, after it ends its last pose. Each repeat moves the whole character by the item's cycle
/// travel (horizontal displacement of the root-motion bone from In to Out), so looping walks keep going forward.</item>
/// <item><b>Root alignment:</b> with <see cref="ClipSequence.AlignRoot"/>, item i is moved (and, with AlignYaw, turned about +Y)
/// rigidly so that its root-motion bone starts horizontally where the sequence's root-motion bone is at the item's start
/// (and faces the same way): the second clip continues from where the first one is, it does not jump back to its own origin.
/// Every parentless bone and every IK target is transformed, so pinned feet stay consistent.</item>
/// <item><b>Blending:</b> at time t the item that has fully faded in most recently is the base; every later item that has
/// started is blended over it with weight <c>curve((t − Start)/BlendIn)</c>: local bone transforms are lerped/slerped,
/// IK targets too; a chain stays pinned only when it is pinned in both poses (then it is re-solved), otherwise the solved
/// bone rotations are blended in FK. Weight 0 and 1 return the poses unchanged. With the Smooth curve the weight has zero
/// slope at both ends, so neither pose nor velocity jumps at the transition.</item>
/// </list>
/// </summary>
public sealed class SequenceEvaluator
{
    sealed class Item
    {
        public required SequenceItem Source { get; init; }
        public required Clip Clip { get; init; }
        /// <summary>Clip range one pass plays (seconds): [RangeStart, RangeStart + Pass); playback starts Offset into it.</summary>
        public double Start, RangeStart, Offset, Pass, End, Blend;

        /// <summary>A looping clip played from inside its loop range: passes wrap at the loop end (In only sets the phase).</summary>
        public bool Wrap;
        public int Loops;
        public Vec3 Cycle;
        public Transform Align = Transform.Identity;
    }

    readonly List<Item> _items = new();
    readonly RigEvaluator _ev;
    readonly Transform[] _restModel;

    public SequenceEvaluator(Project project, ClipSequence sequence)
    {
        Sequence = sequence;
        var character = project.FindCharacter(sequence.Character)
            ?? throw new SequenceException("not_found", $"The character of sequence '{sequence.Name}' no longer exists.");
        Rig = project.FindRig(character.Rig) ?? throw new SequenceException("not_found", $"The rig of '{character.Name}' does not exist.");
        _ev = new RigEvaluator(Rig);
        _restModel = _ev.RestModel();
        RootMotionBone = RootMotionBoneOf(Rig);
        var fps = sequence.Fps.FramesPerSecond;

        foreach (var src in sequence.Items.Select((it, i) => (it, i)).OrderBy(x => x.it.StartFrame).ThenBy(x => x.i).Select(x => x.it))
        {
            var clip = project.FindClip(src.Clip);
            if (clip is null) { Warnings.Add($"Item {src.Id}: its clip no longer exists; skipped."); continue; }
            if (!clip.Skeleton.IsEmpty && clip.Skeleton != Rig.Id) { Warnings.Add($"Item {src.Id}: clip '{clip.Name}' animates another rig; skipped."); continue; }
            var inT = Flicks.FromFrame(src.InFrame, clip.Fps).ToSeconds();
            var outT = Math.Min(src.OutFrame is long o ? Flicks.FromFrame(o, clip.Fps).ToSeconds() : DefaultOut(clip).ToSeconds(), clip.Duration.ToSeconds());
            if (outT <= inT) { Warnings.Add($"Item {src.Id}: its in/out range is empty for clip '{clip.Name}'; skipped."); continue; }
            var loopStart = clip.Loop.Start.ToSeconds();
            var wrap = clip.Loop.Enabled && src.OutFrame is null && inT >= loopStart && loopStart < outT;
            var item = new Item
            {
                Source = src, Clip = clip, Wrap = wrap,
                Start = src.StartFrame / fps,
                RangeStart = wrap ? loopStart : inT,
                Offset = wrap ? inT - loopStart : 0,
                Pass = wrap ? outT - loopStart : outT - inT,
                Loops = Math.Max(1, src.Loops), Blend = Math.Max(0, src.BlendInFrames) / fps,
            };
            item.End = item.Start + item.Loops * item.Pass / src.Speed;
            if (item.Loops > 1 || item.Wrap)
            {
                var a = Model(ClipEvaluator.Evaluate(clip, Rig, Flicks.FromSeconds(item.RangeStart)))[RootMotionBone].Translation;
                var b = Model(ClipEvaluator.Evaluate(clip, Rig, Flicks.FromSeconds(item.RangeStart + item.Pass)))[RootMotionBone].Translation;
                item.Cycle = new Vec3(b.X - a.X, 0, b.Z - a.Z);
            }
            _items.Add(item);
        }
        if (_items.Count == 0) throw new SequenceException("empty_sequence", $"Sequence '{sequence.Name}' has no playable item.");

        // Alignment, item by item: each continues from the sequence made of the items before it.
        if (sequence.AlignRoot)
            for (var i = 1; i < _items.Count; i++)
            {
                var it = _items[i];
                var prev = Model(EvaluateUpTo(i - 1, it.Start, null));
                var own = Model(ItemPose(it, it.Start, applyAlign: false, out _, out _));
                var yaw = sequence.AlignYaw ? Yaw(prev) - Yaw(own) : 0;
                var r = Quat.FromAxisAngle(Vec3.UnitY, yaw);
                var p = prev[RootMotionBone].Translation;
                var q = r.Rotate(own[RootMotionBone].Translation);
                it.Align = new Transform(new Vec3(p.X - q.X, 0, p.Z - q.Z), r, Vec3.One);
            }
        EndSeconds = _items.Max(i => i.End);
    }

    public ClipSequence Sequence { get; }
    public Rig Rig { get; }
    public int RootMotionBone { get; }
    public List<string> Warnings { get; } = new();

    /// <summary>End of the last item, in seconds.</summary>
    public double EndSeconds { get; }

    /// <summary>Last sequence frame (inclusive) covering every item.</summary>
    public long LastFrame => (long)Math.Ceiling(EndSeconds * Sequence.Fps.FramesPerSecond - 1e-6);

    /// <summary>Alignment transform of an item (identity when not aligned or not playable).</summary>
    public Transform AlignmentOf(ObjectId itemId) => _items.Find(i => i.Source.Id == itemId)?.Align ?? Transform.Identity;

    /// <summary>Start and end of an item in sequence frames (end may be fractional).</summary>
    public (double Start, double End)? RangeOf(ObjectId itemId) =>
        _items.Find(i => i.Source.Id == itemId) is { } it ? (it.Start * Sequence.Fps.FramesPerSecond, it.End * Sequence.Fps.FramesPerSecond) : null;

    public SequenceSample Evaluate(double frame)
    {
        var active = new List<SequenceContribution>();
        var pose = EvaluateUpTo(_items.Count - 1, frame / Sequence.Fps.FramesPerSecond, active);
        return new SequenceSample(frame, pose, active);
    }

    Transform[] Model(Pose pose) => _ev.Model(pose);

    Pose EvaluateUpTo(int last, double t, List<SequenceContribution>? active)
    {
        const double eps = 1e-9;
        var candidates = new List<int>();
        for (var i = 0; i <= last; i++) if (_items[i].Start <= t + eps) candidates.Add(i);
        if (candidates.Count == 0) candidates.Add(0);
        var baseIndex = candidates[0];
        foreach (var i in candidates) if (t + eps >= _items[i].Start + _items[i].Blend) baseIndex = i;
        var used = candidates.Where(i => i >= baseIndex).ToList();
        var weights = new double[used.Count];
        var clipFrames = new double[used.Count];
        var cycles = new int[used.Count];
        var pose = ItemPose(_items[used[0]], t, true, out clipFrames[0], out cycles[0]);
        weights[0] = 1;
        for (var k = 1; k < used.Count; k++)
        {
            var it = _items[used[k]];
            var w = it.Blend <= 0 ? 1 : Weight(it.Source.BlendCurve, (t - it.Start) / it.Blend);
            var next = ItemPose(it, t, true, out clipFrames[k], out cycles[k]);
            pose = Blend(Rig, pose, next, w);
            for (var j = 0; j < k; j++) weights[j] *= 1 - w;
            weights[k] = w;
        }
        if (active is not null)
            for (var k = 0; k < used.Count; k++)
                if (weights[k] > 0)
                    active.Add(new SequenceContribution(_items[used[k]].Source.Id, _items[used[k]].Clip.Id, weights[k], clipFrames[k], cycles[k]));
        return pose;
    }

    Pose ItemPose(Item it, double t, bool applyAlign, out double clipFrame, out int cycle)
    {
        var u = Math.Clamp((t - it.Start) * it.Source.Speed, 0, it.Loops * it.Pass);
        var pos = it.Offset + u;
        cycle = (int)Math.Floor(pos / it.Pass);
        var within = pos - cycle * it.Pass;
        if (!it.Wrap && cycle >= it.Loops) { cycle = it.Loops - 1; within = it.Pass; }
        var clipTime = Flicks.FromSeconds(Math.Min(it.RangeStart + within, it.Clip.Duration.ToSeconds()));
        clipFrame = clipTime.ToFrameExact(it.Clip.Fps);
        var pose = ClipEvaluator.Evaluate(it.Clip, Rig, clipTime);
        var m = (applyAlign ? it.Align : Transform.Identity) * new Transform(it.Cycle * cycle, Quat.Identity, Vec3.One);
        if (m.Translation != Vec3.Zero || !m.Rotation.ApproxEquals(Quat.Identity, 0)) MoveRigidly(Rig, pose, m);
        return pose;
    }

    /// <summary>Moves a whole pose rigidly in model space: parentless bones and IK targets (poles follow the first bone).</summary>
    public static void MoveRigidly(Rig rig, Pose pose, Transform m)
    {
        foreach (var bone in rig.Bones)
            if (bone.Parent.IsEmpty) pose.Bones[bone.Id.ToString()] = m * pose.LocalOf(bone);
        foreach (var control in rig.Controls)
        {
            if (control.Kind != ControlKind.IkTarget) continue;
            var s = PoseSolver.StateOf(rig, pose, control.Id).Clone();
            s.Transform = (m * s.Transform) with { Scale = Vec3.One };
            pose.Controls[control.Id.ToString()] = s;
        }
    }

    /// <summary>Blends two poses of one rig (see the class remarks). w = 0 → a, w = 1 → b (copies).</summary>
    public static Pose Blend(Rig rig, Pose a, Pose b, double w)
    {
        if (w <= 0) return a.Clone();
        if (w >= 1) return b.Clone();
        var pose = new Pose();
        foreach (var bone in rig.Bones)
        {
            var key = bone.Id.ToString();
            if (!a.Bones.ContainsKey(key) && !b.Bones.ContainsKey(key)) continue;
            pose.Bones[key] = Transform.Lerp(a.LocalOf(bone), b.LocalOf(bone), w);
        }
        foreach (var control in rig.Controls)
        {
            var sa = PoseSolver.StateOf(rig, a, control.Id);
            var sb = PoseSolver.StateOf(rig, b, control.Id);
            pose.Controls[control.Id.ToString()] = new ControlState
            {
                Transform = Transform.Lerp(sa.Transform, sb.Transform, w) with { Scale = Vec3.One },
                Pinned = sa.Pinned && sb.Pinned,
            };
        }
        PoseSolver.Update(rig, pose);
        return pose;
    }

    /// <summary>Blend weight for normalized time x ∈ [0, 1].</summary>
    public static double Weight(BlendCurve curve, double x)
    {
        x = Math.Clamp(x, 0, 1);
        return curve switch
        {
            BlendCurve.Linear => x,
            BlendCurve.EaseIn => x * x,
            BlendCurve.EaseOut => 1 - (1 - x) * (1 - x),
            _ => x * x * (3 - 2 * x),
        };
    }

    /// <summary>Where a pass ends when the item gives no out frame: the loop end for a looping clip (so repeats are seamless),
    /// else the clip's length.</summary>
    public static Flicks DefaultOut(Clip clip) =>
        clip.Loop.Enabled && clip.Loop.End.Value > 0 && clip.Loop.End < clip.Duration ? clip.Loop.End : clip.Duration;

    /// <summary>The bone mapped to "root", else the first bone (as export).</summary>
    public static int RootMotionBoneOf(Rig rig)
    {
        var mapped = rig.FindByStandardName(H.Root);
        var i = mapped is null ? -1 : rig.IndexOf(mapped.Id);
        return i < 0 ? 0 : i;
    }

    /// <summary>Heading (radians about +Y, 0 = +Z) of the root-motion bone relative to its rest orientation.</summary>
    double Yaw(Transform[] model)
    {
        var q = model[RootMotionBone].Rotation * _restModel[RootMotionBone].Rotation.Inverse();
        var f = q.Rotate(Vec3.UnitZ);
        return Math.Abs(f.X) + Math.Abs(f.Z) < 1e-9 ? 0 : Math.Atan2(f.X, f.Z);
    }

    // ------------------------------------------------------------------ foot phase

    /// <summary>
    /// Foot-phase signal of a pose: (left ankle − right ankle) along the heading of the root-motion bone, divided by the leg
    /// length; null when the feet are not mapped.
    /// </summary>
    public static double? FootPhase(Rig rig, Transform[] model, Transform[] restModel)
    {
        var l = rig.FindByStandardName(H.Foot(Side.Left));
        var r = rig.FindByStandardName(H.Foot(Side.Right));
        var th = rig.FindByStandardName(H.Thigh(Side.Left));
        if (l is null || r is null || th is null) return null;
        var rm = RootMotionBoneOf(rig);
        var q = model[rm].Rotation * restModel[rm].Rotation.Inverse();
        var fwd = q.Rotate(Vec3.UnitZ);
        fwd = new Vec3(fwd.X, 0, fwd.Z);
        if (fwd.Length() < 1e-9) fwd = Vec3.UnitZ;
        fwd = fwd.Normalized();
        var leg = (restModel[rig.IndexOf(th.Id)].Translation - restModel[rig.IndexOf(l.Id)].Translation).Length();
        if (leg < 1e-6) leg = 1;
        return Vec3.Dot(model[rig.IndexOf(l.Id)].Translation - model[rig.IndexOf(r.Id)].Translation, fwd) / leg;
    }

    /// <summary>
    /// The clip frame in [<paramref name="from"/>, <paramref name="to"/>] whose foot phase (and its rate of change, at
    /// <paramref name="speed"/>) best matches the sequence at sequence frame <paramref name="atFrame"/>. Returns null when the
    /// feet are not mapped.
    /// </summary>
    public long? MatchFootPhase(double atFrame, Clip clip, long from, long to, double speed)
    {
        var rest = _restModel;
        var dt = 1 / Sequence.Fps.FramesPerSecond;
        var a0 = FootPhase(Rig, Model(Evaluate(atFrame).Pose), rest);
        var a1 = FootPhase(Rig, Model(Evaluate(atFrame + 1).Pose), rest);
        if (a0 is null || a1 is null) return null;
        var rateA = (a1.Value - a0.Value) / dt;
        long? best = null;
        var bestScore = double.MaxValue;
        var clipDt = 1 / clip.Fps.FramesPerSecond;
        var last = Math.Min(to, clip.DurationFrames);
        for (var f = from; f <= last; f++)
        {
            var b0 = FootPhase(Rig, Model(ClipEvaluator.Evaluate(clip, Rig, Flicks.FromFrame(f, clip.Fps))), rest)!.Value;
            var b1 = FootPhase(Rig, Model(ClipEvaluator.Evaluate(clip, Rig, Flicks.FromFrame(Math.Min(f + 1, clip.DurationFrames), clip.Fps))), rest)!.Value;
            var rateB = (b1 - b0) / clipDt * speed;
            var score = Math.Abs(a0.Value - b0) + 0.1 * Math.Abs(rateA - rateB);
            if (score < bestScore - 1e-12) { bestScore = score; best = f; }
        }
        return best;
    }
}
