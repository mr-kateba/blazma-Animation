using System.Text.Json.Serialization;
using Blazma.Core.Ids;
using Blazma.Core.Timing;

namespace Blazma.Core.Model;

/// <summary>
/// A clip sequence on one character's track (docs/DATA_FORMAT.md §8): clips placed one after another, each
/// fading in over the previous one. Evaluated by Blazma.Motion.SequenceEvaluator; baked into a normal clip with
/// <c>sequence.bake</c> (export = bake, then export the clip).
/// </summary>
public sealed class ClipSequence
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public string Name { get; set; } = "Sequence";

    /// <summary>The character whose rig every item's clip animates.</summary>
    public ObjectId Character { get; set; }

    /// <summary>Frame rate of the sequence timeline (item start frames and blend lengths are in these frames).</summary>
    public FrameRate Fps { get; set; } = FrameRate.Fps30;

    /// <summary>Move/turn each item horizontally so its root continues from where the previous item is at the item's start
    /// (instead of jumping back to the clip's own origin).</summary>
    public bool AlignRoot { get; set; } = true;

    /// <summary>Also align the facing (yaw about +Y) of the root-motion bone when <see cref="AlignRoot"/> is on.</summary>
    public bool AlignYaw { get; set; } = true;

    /// <summary>Items sorted by <see cref="SequenceItem.StartFrame"/>.</summary>
    public List<SequenceItem> Items { get; set; } = new();
}

public sealed class SequenceItem
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public ObjectId Clip { get; set; }

    /// <summary>Sequence frame where the item starts (and starts fading in).</summary>
    public long StartFrame { get; set; }

    /// <summary>First clip frame played (clip frames, the clip's own frame rate).</summary>
    public long InFrame { get; set; }

    /// <summary>Clip frame where one pass ends (exclusive like a duration); null = the clip's length.</summary>
    public long? OutFrame { get; set; }

    /// <summary>Playback speed (1 = normal, 2 = twice as fast).</summary>
    public double Speed { get; set; } = 1;

    /// <summary>How many times the [In, Out) range plays back to back; the root-motion bone keeps travelling on each repeat.</summary>
    public int Loops { get; set; } = 1;

    /// <summary>Sequence frames over which this item fades in over the items before it (0 = cut).</summary>
    public long BlendInFrames { get; set; }

    public BlendCurve BlendCurve { get; set; } = BlendCurve.Smooth;
}

[JsonConverter(typeof(JsonStringEnumConverter<BlendCurve>))]
public enum BlendCurve
{
    Linear,

    /// <summary>Smoothstep 3w²−2w³: zero slope at both ends, so velocities do not jump.</summary>
    Smooth,
    EaseIn,
    EaseOut,
}
