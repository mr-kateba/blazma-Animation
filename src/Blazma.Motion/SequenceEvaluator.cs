using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Rigging;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Motion;

/// <summary>A sequence that cannot be evaluated, with a stable code (not_found, rig_mismatch, empty_sequence, too_large).</summary>
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
/// The heading is the facing of the pelvis in model space (root · pelvis, so a turn carried on the pelvis counts as well as one
/// on the root); the root-motion bone when no pelvis is mapped. Every parentless bone and every IK target is transformed, so
/// pinned feet stay consistent.</item>
/// <item><b>Blending:</b> at time t the item that has fully faded in most recently is the base; every later item that has
/// started is blended over it with weight <c>curve((t − Start)/BlendIn)</c>: local bone transforms are lerped/slerped,
/// IK targets too; a chain stays pinned only when it is pinned in both poses (then it is re-solved), otherwise the solved
/// bone rotations are blended in FK. With the Smooth curve the weight has zero slope at both ends, so neither pose nor
/// velocity jumps at the transition. At most <see cref="MaxBlending"/> items contribute at once; an item that would exceed
/// it is skipped (warning).</item>
/// <item><b>Root velocity in blends:</b> lerping two diverging root positions adds <c>w′·(pB − pA)</c> to the speed (a walk
/// blended into a run would briefly go faster than the run). Per sequence frame the blended root-motion bone's horizontal step
/// is clamped to [min, max] of the two clips' steps; the difference is a horizontal correction accumulated over the blend and
/// kept afterwards (it moves the whole pose rigidly, so feet and IK targets stay consistent). <see cref="AlignmentOf"/>
/// includes it.</item>
/// </list>
/// The constructor only computes timing and limits (cheap; refuses with too_large when a bake or the blend preparation would
/// exceed the work budget); alignment and blend corrections are computed on the first evaluation.
/// </summary>
public sealed class SequenceEvaluator
{
    /// <summary>Most items blending at the same time (the base and the items fading in over it).</summary>
    public const int MaxBlending = 8;

    /// <summary>Budget for preparing alignment and blend corrections: Σ blend frames × contributors × bones.</summary>
    public const double MaxPrepareWork = 20_000_000;

    /// <summary>Budget for baking (evaluating every frame): Σ frames × contributors × bones.</summary>
    public const double MaxBakeWork = 40_000_000;

    /// <summary>Largest sequence length in frames the evaluator accepts (a bake has its own, lower limit).</summary>
    public const double MaxFrames = 1e12;

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

        /// <summary>Horizontal root correction on each sequence frame of the blend (index 0 = Start); empty = none.</summary>
        public Vec3[] Correction = Array.Empty<Vec3>();
        public Vec3 FinalCorrection => Correction.Length == 0 ? Vec3.Zero : Correction[^1];
    }

    /// <summary>One item's part in an evaluation: its blend weight over what is below it, timing and rigid move.</summary>
    readonly record struct Part(Item Item, double W, Flicks ClipTime, double ClipFrame, int Cycle, Transform Move, Vec3 Correction);

    readonly List<Item> _items = new();
    readonly RigEvaluator _ev;
    readonly Transform[] _restModel;
    readonly int _headingBone;
    bool _prepared;

    public SequenceEvaluator(Project project, ClipSequence sequence)
    {
        Sequence = sequence;
        var character = project.FindCharacter(sequence.Character)
            ?? throw new SequenceException("not_found", $"The character of sequence '{sequence.Name}' no longer exists.");
        Rig = project.FindRig(character.Rig) ?? throw new SequenceException("not_found", $"The rig of '{character.Name}' does not exist.");
        if (sequence.Items.Count > ClipSequence.MaxItems)
            throw new SequenceException("too_large", $"Sequence '{sequence.Name}' has {sequence.Items.Count} items (at most {ClipSequence.MaxItems}).");
        _ev = new RigEvaluator(Rig);
        _restModel = _ev.RestModel();
        RootMotionBone = RootMotionBoneOf(Rig);
        _headingBone = HeadingBoneOf(Rig);
        var fps = sequence.Fps.FramesPerSecond;
        if (!(fps > 0)) throw new SequenceException("invalid", $"Sequence '{sequence.Name}' has an invalid frame rate.");

        foreach (var src in sequence.Items.Select((it, i) => (it, i)).OrderBy(x => x.it.StartFrame).ThenBy(x => x.i).Select(x => x.it))
        {
            var clip = project.FindClip(src.Clip);
            if (clip is null) { Warnings.Add($"Item {src.Id}: its clip no longer exists; skipped."); continue; }
            if (!clip.Skeleton.IsEmpty && clip.Skeleton != Rig.Id) { Warnings.Add($"Item {src.Id}: clip '{clip.Name}' animates another rig; skipped."); continue; }
            if (!ClipSequence.ItemInLimits(src)) { Warnings.Add($"Item {src.Id}: its timing is outside the limits; skipped."); continue; }
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
            if (_items.Count > 0 && ActiveAt(_items, item.Start, item) > MaxBlending)
            {
                Warnings.Add($"Item {src.Id}: more than {MaxBlending} items would blend at frame {src.StartFrame}; skipped.");
                SkippedForBlending.Add(src.Id);
                continue;
            }
            _items.Add(item);
        }
        if (_items.Count == 0) throw new SequenceException("empty_sequence", $"Sequence '{sequence.Name}' has no playable item.");
        EndSeconds = _items.Max(i => i.End);
        if (!(EndSeconds * fps <= MaxFrames)) throw new SequenceException("too_large", $"Sequence '{sequence.Name}' is too long to evaluate.");

        // Work estimates (counted in bone evaluations), so a hostile or careless sequence is refused before any heavy work.
        var bones = Math.Max(1, Rig.Bones.Count);
        double prepare = 0, bake = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            var active = ActiveAt(_items.Take(i).ToList(), it.Start, it);
            if (i > 0 && sequence.AlignRoot) prepare += (active + 1) * bones;
            if (i > 0 && it.Blend > 0) prepare += (BlendSamples(it) + 1) * (double)active * bones;
            // Item i is evaluated from its start until a later item has fully faded in.
            var until = EndSeconds;
            for (var j = i + 1; j < _items.Count; j++) until = Math.Min(until, _items[j].Start + _items[j].Blend);
            bake += Math.Max(0, until - it.Start) * fps * bones + bones;
        }
        PrepareWork = prepare;
        BakeWork = bake;
        if (prepare > MaxPrepareWork)
            throw new SequenceException("too_large", $"Sequence '{sequence.Name}': its blends are too long to prepare ({prepare:0.#e0} bone evaluations, at most {MaxPrepareWork:0.#e0}); shorten blendFrames.");
    }

    public ClipSequence Sequence { get; }
    public Rig Rig { get; }
    public int RootMotionBone { get; }
    public List<string> Warnings { get; } = new();

    /// <summary>Items left out because more than <see cref="MaxBlending"/> items would blend at once.</summary>
    public List<ObjectId> SkippedForBlending { get; } = new();

    /// <summary>End of the last item, in seconds.</summary>
    public double EndSeconds { get; }

    /// <summary>Last sequence frame (inclusive) covering every item.</summary>
    public long LastFrame => (long)Math.Ceiling(EndSeconds * Sequence.Fps.FramesPerSecond - 1e-6);

    /// <summary>Estimated bone evaluations of the blend preparation (see <see cref="MaxPrepareWork"/>).</summary>
    public double PrepareWork { get; }

    /// <summary>Estimated bone evaluations of evaluating every frame 0 … LastFrame (see <see cref="MaxBakeWork"/>).</summary>
    public double BakeWork { get; }

    /// <summary>Rigid transform of an item in the sequence: its alignment plus the root correction its blend accumulated
    /// (identity when not aligned or not playable). Item pose · this = the sequence's pose once the item has faded in.</summary>
    public Transform AlignmentOf(ObjectId itemId)
    {
        if (_items.Find(i => i.Source.Id == itemId) is not { } it) return Transform.Identity;
        Prepare();
        return new Transform(it.FinalCorrection, Quat.Identity, Vec3.One) * it.Align;
    }

    /// <summary>Start and end of an item in sequence frames (end may be fractional).</summary>
    public (double Start, double End)? RangeOf(ObjectId itemId) =>
        _items.Find(i => i.Source.Id == itemId) is { } it ? (it.Start * Sequence.Fps.FramesPerSecond, it.End * Sequence.Fps.FramesPerSecond) : null;

    public SequenceSample Evaluate(double frame)
    {
        Prepare();
        var parts = Parts(_items.Count - 1, frame / Sequence.Fps.FramesPerSecond);
        var pose = Compose(parts);
        var weights = FinalWeights(parts);
        var active = new List<SequenceContribution>();
        for (var k = 0; k < parts.Count; k++)
            if (weights[k] > 0)
                active.Add(new SequenceContribution(parts[k].Item.Source.Id, parts[k].Item.Clip.Id, weights[k], parts[k].ClipFrame, parts[k].Cycle));
        return new SequenceSample(frame, pose, active);
    }

    /// <summary>
    /// The value of <paramref name="prop"/> (its offset when attached, else its world transform) at a sequence frame: each
    /// contributing item gives what its clip plays (<see cref="PropEvaluator.Local"/>), a free prop the clip animates is moved
    /// with the item (alignment, cycle travel, blend correction), and the values are blended with the items' weights.
    /// Null when no contributing clip animates the prop.
    /// </summary>
    public Transform? PropLocal(Prop prop, double frame)
    {
        Prepare();
        var parts = Parts(_items.Count - 1, frame / Sequence.Fps.FramesPerSecond);
        if (!parts.Any(p => PropEvaluator.Animates(p.Item.Clip, prop))) return null;
        var free = prop.Attachment is null;
        Transform Value(Part p)
        {
            var local = PropEvaluator.Local(prop, p.Item.Clip, p.ClipTime);
            return free && PropEvaluator.Animates(p.Item.Clip, prop) ? p.Move * local : local;
        }
        var value = Shift(Value(parts[0]), parts[0].Correction, free);
        for (var k = 1; k < parts.Count; k++)
            value = Shift(Transform.Lerp(value, Value(parts[k]), parts[k].W), parts[k].Correction, free);
        return value;

        static Transform Shift(Transform t, Vec3 c, bool free) => free ? t with { Translation = t.Translation + c } : t;
    }

    /// <summary>
    /// Sequence frames at which item <paramref name="itemId"/> plays clip time <paramref name="clipTime"/> (every pass, in order),
    /// with the evaluator's exact timing (speed, loops, and the phase/wrap of a looping clip). Empty when the item is not
    /// playable or never plays that time.
    /// </summary>
    public IReadOnlyList<double> FramesPlaying(ObjectId itemId, Flicks clipTime)
    {
        var result = new List<double>();
        if (_items.Find(i => i.Source.Id == itemId) is not { } it) return result;
        var e = clipTime.ToSeconds();
        if (e < it.RangeStart - 1e-9 || e >= it.RangeStart + it.Pass - 1e-9) return result;
        // Elapsed clip time u (0 ≤ u < Loops·Pass) whose position Offset + u wraps to e.
        var first = e - it.RangeStart - it.Offset;
        if (it.Wrap) first = ((first % it.Pass) + it.Pass) % it.Pass;
        else if (first < -1e-9) return result;
        var fps = Sequence.Fps.FramesPerSecond;
        for (var k = 0; k < it.Loops; k++)
        {
            var u = first + k * it.Pass;
            if (u >= it.Loops * it.Pass - 1e-9) break;
            result.Add((it.Start + u / it.Source.Speed) * fps);
        }
        return result;
    }

    Transform[] Model(Pose pose) => _ev.Model(pose);

    /// <summary>Alignment and blend corrections, item by item (each continues from the sequence made of the items before it).</summary>
    void Prepare()
    {
        if (_prepared) return;
        _prepared = true;
        for (var i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            if (it.Loops > 1 || it.Wrap)
            {
                var a = Model(ClipEvaluator.Evaluate(it.Clip, Rig, Flicks.FromSeconds(it.RangeStart)))[RootMotionBone].Translation;
                var b = Model(ClipEvaluator.Evaluate(it.Clip, Rig, Flicks.FromSeconds(it.RangeStart + it.Pass)))[RootMotionBone].Translation;
                it.Cycle = new Vec3(b.X - a.X, 0, b.Z - a.Z);
            }
            if (i == 0) continue;
            if (Sequence.AlignRoot)
            {
                var prev = Model(Compose(Parts(i - 1, it.Start)));
                var own = Model(ItemPose(it, it.Start, Transform.Identity, out _, out _, out _));
                var yaw = Sequence.AlignYaw ? Heading(prev) - Heading(own) : 0;
                var r = Quat.FromAxisAngle(Vec3.UnitY, yaw);
                var p = prev[RootMotionBone].Translation;
                var q = r.Rotate(own[RootMotionBone].Translation);
                it.Align = new Transform(new Vec3(p.X - q.X, 0, p.Z - q.Z), r, Vec3.One);
            }
            if (it.Blend > 0) it.Correction = BlendCorrection(i);
        }
    }

    long BlendSamples(Item it) => (long)Math.Ceiling(it.Blend * Sequence.Fps.FramesPerSecond - 1e-9);

    /// <summary>
    /// Root correction of item <paramref name="k"/>'s blend on each sequence frame of it. The lerp of two diverging root positions
    /// moves by <c>(1 − w)·ΔpA + w·ΔpB + Δw·(pB − pA)</c>; the last term can make the blend faster than either clip. Per frame the
    /// blended root's horizontal step keeps its direction but its length is clamped to [min, max] of the two clips' steps; the
    /// difference accumulates as a rigid horizontal shift. Clamping (instead of replacing the step by the velocity blend) changes
    /// the lerped pose only where it overshoots, so feet the lerp keeps planted (phase-matched transitions) stay planted.
    /// </summary>
    Vec3[] BlendCorrection(int k)
    {
        var it = _items[k];
        var fps = Sequence.Fps.FramesPerSecond;
        var end = Math.Min(it.Start + it.Blend, Math.Max(EndSeconds, it.Start));
        var n = (int)Math.Ceiling((end - it.Start) * fps - 1e-9);
        if (n <= 0) return Array.Empty<Vec3>();
        var c = new Vec3[n + 1];
        Vec3 pb = default, pi = default, pl = default;
        for (var j = 0; j <= n; j++)
        {
            var t = Math.Min(it.Start + j / fps, end);
            var below = Compose(Parts(k - 1, t));
            var own = ItemPose(it, t, it.Align, out _, out _, out _);
            var wj = Weight(it.Source.BlendCurve, (t - it.Start) / it.Blend);
            var blended = Blend(Rig, below, own, wj);
            var b = Flat(Model(below)[RootMotionBone].Translation);
            var o = Flat(Model(own)[RootMotionBone].Translation);
            var l = Flat(Model(blended)[RootMotionBone].Translation);
            if (j > 0)
            {
                // The step the lerp gives (with the corrections so far), its length clamped to the two clips' steps.
                var step = l - pl;
                double lo = Math.Min((b - pb).Length(), (o - pi).Length()), hi = Math.Max((b - pb).Length(), (o - pi).Length());
                var len = step.Length();
                var want = len > hi ? step * (hi / len) : len < lo && len > 1e-12 ? step * (lo / len) : step;
                c[j] = c[j - 1] + (want - step);
            }
            (pb, pi, pl) = (b, o, l);
        }
        return c;

        static Vec3 Flat(Vec3 v) => new(v.X, 0, v.Z);
    }

    /// <summary>Correction of an item at time t (interpolated between sequence frames; the final value after the blend).</summary>
    Vec3 CorrectionAt(Item it, double t)
    {
        var c = it.Correction;
        if (c.Length == 0) return Vec3.Zero;
        var x = (t - it.Start) * Sequence.Fps.FramesPerSecond;
        if (x <= 0) return Vec3.Zero;
        if (x >= c.Length - 1) return c[^1];
        var i = (int)Math.Floor(x);
        return Vec3.Lerp(c[i], c[i + 1], x - i);
    }

    /// <summary>Number of items of <paramref name="items"/> (sorted by start) plus <paramref name="extra"/> active at time t.</summary>
    static int ActiveAt(List<Item> items, double t, Item extra)
    {
        const double eps = 1e-9;
        var baseIndex = 0;
        var started = 0;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Start > t + eps) break;
            started = i + 1;
            if (t + eps >= items[i].Start + items[i].Blend) baseIndex = i;
        }
        if (items.Count == 0) return 1;
        // The new item itself becomes the base when it does not blend.
        if (extra.Blend <= 0) return 1;
        return started - baseIndex + 1;
    }

    /// <summary>The items contributing at time t among items 0 … <paramref name="last"/>: the base first.</summary>
    List<Part> Parts(int last, double t)
    {
        const double eps = 1e-9;
        var baseIndex = 0;
        var lastStarted = 0;
        for (var i = 0; i <= last; i++)
        {
            if (_items[i].Start > t + eps) break;
            lastStarted = i;
            if (t + eps >= _items[i].Start + _items[i].Blend) baseIndex = i;
        }
        var parts = new List<Part>(lastStarted - baseIndex + 1);
        for (var i = baseIndex; i <= lastStarted; i++)
        {
            var it = _items[i];
            var w = i == baseIndex || it.Blend <= 0 ? 1 : Weight(it.Source.BlendCurve, (t - it.Start) / it.Blend);
            var (time, frame, cycle) = ItemTime(it, t);
            var move = it.Align * new Transform(it.Cycle * cycle, Quat.Identity, Vec3.One);
            parts.Add(new Part(it, w, time, frame, cycle, move, CorrectionAt(it, t)));
        }
        return parts;
    }

    Pose Compose(List<Part> parts)
    {
        // The base carries its own (final) correction; every item blended over it adds its correction to the blended pose.
        var pose = PoseOf(parts[0], new Transform(parts[0].Correction, Quat.Identity, Vec3.One) * parts[0].Move);
        for (var k = 1; k < parts.Count; k++)
        {
            pose = Blend(Rig, pose, PoseOf(parts[k], parts[k].Move), parts[k].W);
            if (parts[k].Correction != Vec3.Zero) MoveRigidly(Rig, pose, new Transform(parts[k].Correction, Quat.Identity, Vec3.One));
        }
        return pose;

        Pose PoseOf(Part p, Transform m)
        {
            var pose = ClipEvaluator.Evaluate(p.Item.Clip, Rig, p.ClipTime);
            if (m.Translation != Vec3.Zero || !m.Rotation.ApproxEquals(Quat.Identity, 0)) MoveRigidly(Rig, pose, m);
            return pose;
        }
    }

    static double[] FinalWeights(List<Part> parts)
    {
        var weights = new double[parts.Count];
        weights[0] = 1;
        for (var k = 1; k < parts.Count; k++)
        {
            for (var j = 0; j < k; j++) weights[j] *= 1 - parts[k].W;
            weights[k] = parts[k].W;
        }
        return weights;
    }

    (Flicks Time, double Frame, int Cycle) ItemTime(Item it, double t)
    {
        var u = Math.Clamp((t - it.Start) * it.Source.Speed, 0, it.Loops * it.Pass);
        var pos = it.Offset + u;
        var cycle = (int)Math.Floor(pos / it.Pass);
        var within = pos - cycle * it.Pass;
        if (!it.Wrap && cycle >= it.Loops) { cycle = it.Loops - 1; within = it.Pass; }
        var clipTime = Flicks.FromSeconds(Math.Min(it.RangeStart + within, it.Clip.Duration.ToSeconds()));
        return (clipTime, clipTime.ToFrameExact(it.Clip.Fps), cycle);
    }

    Pose ItemPose(Item it, double t, Transform align, out Flicks clipTime, out double clipFrame, out int cycle)
    {
        (clipTime, clipFrame, cycle) = ItemTime(it, t);
        var pose = ClipEvaluator.Evaluate(it.Clip, Rig, clipTime);
        var m = align * new Transform(it.Cycle * cycle, Quat.Identity, Vec3.One);
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

    /// <summary>The bone whose model-space facing is the character's heading: the pelvis (its model rotation includes the
    /// root's), else the root-motion bone.</summary>
    public static int HeadingBoneOf(Rig rig)
    {
        var pelvis = rig.FindByStandardName(H.Pelvis);
        var i = pelvis is null ? -1 : rig.IndexOf(pelvis.Id);
        return i < 0 ? RootMotionBoneOf(rig) : i;
    }

    /// <summary>Heading (radians about +Y, 0 = +Z) of <paramref name="bone"/> relative to its rest orientation, in model space.</summary>
    public static double HeadingOf(Transform[] model, Transform[] restModel, int bone)
    {
        var q = model[bone].Rotation * restModel[bone].Rotation.Inverse();
        var f = q.Rotate(Vec3.UnitZ);
        return Math.Abs(f.X) + Math.Abs(f.Z) < 1e-9 ? 0 : Math.Atan2(f.X, f.Z);
    }

    double Heading(Transform[] model) => HeadingOf(model, _restModel, _headingBone);

    // ------------------------------------------------------------------ foot phase

    /// <summary>
    /// Foot-phase signal of a pose: (left ankle − right ankle) along the character's heading (<see cref="HeadingBoneOf"/>),
    /// divided by the leg length; null when the feet are not mapped.
    /// </summary>
    public static double? FootPhase(Rig rig, Transform[] model, Transform[] restModel)
    {
        var l = rig.FindByStandardName(H.Foot(Side.Left));
        var r = rig.FindByStandardName(H.Foot(Side.Right));
        var th = rig.FindByStandardName(H.Thigh(Side.Left));
        if (l is null || r is null || th is null) return null;
        var yaw = HeadingOf(model, restModel, HeadingBoneOf(rig));
        var fwd = new Vec3(Math.Sin(yaw), 0, Math.Cos(yaw));
        var leg = (restModel[rig.IndexOf(th.Id)].Translation - restModel[rig.IndexOf(l.Id)].Translation).Length();
        if (leg < 1e-6) leg = 1;
        return Vec3.Dot(model[rig.IndexOf(l.Id)].Translation - model[rig.IndexOf(r.Id)].Translation, fwd) / leg;
    }

    /// <summary>
    /// The clip frame in [<paramref name="from"/>, <paramref name="to"/>] whose foot phase (and its rate of change) best matches
    /// the sequence at sequence frame <paramref name="atFrame"/>. <paramref name="speed"/> is the item's playback speed (clip
    /// seconds per sequence second); both rates are per second, so clip and sequence frame rates may differ. Returns null when
    /// the feet are not mapped.
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
