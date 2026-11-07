using System.Text.Json.Serialization;
using Blazma.Core.Ids;
using Blazma.Core.Timing;

namespace Blazma.Core.Model;

/// <summary>An animation clip. Times are in flicks; see docs/DATA_FORMAT.md §5.</summary>
public sealed class Clip
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public string Name { get; set; } = "Clip";
    public FrameRate Fps { get; set; } = FrameRate.Fps30;

    /// <summary>Clip length; exclusive end time.</summary>
    public Flicks Duration { get; set; } = Flicks.FromSeconds(1);

    /// <summary>Rig this clip animates; empty for object-only clips.</summary>
    public ObjectId Skeleton { get; set; }

    public LoopSettings Loop { get; set; } = new();
    public List<Track> Tracks { get; set; } = new();
    public List<AnimationEvent> Events { get; set; } = new();

    /// <summary>Authoring markers on the timeline, including the key-pose phases (Anticipation, Contact, …).</summary>
    public List<Marker> Markers { get; set; } = new();

    /// <summary>Combat phases and hitboxes (schema v5); null when the clip has none.</summary>
    public ClipCombat? Combat { get; set; }

    /// <summary>True for AI or tool drafts that have not been approved yet.</summary>
    public bool IsDraft { get; set; }

    public Dictionary<string, string> Metadata { get; set; } = new();

    public long DurationFrames => Duration.ToFrame(Fps);

    /// <summary>Exact deep copy (typed, no JSON round trip for the tracks).</summary>
    public Clip DeepClone()
    {
        var copy = CloneWithoutTracks();
        copy.Tracks = new List<Track>(Tracks.Count);
        foreach (var t in Tracks) copy.Tracks.Add(t.DeepClone());
        return copy;
    }

    /// <summary>Deep copy of everything but the tracks (the copy has an empty track list).</summary>
    public Clip CloneWithoutTracks() => new()
    {
        Id = Id,
        Name = Name,
        Fps = Fps,
        Duration = Duration,
        Skeleton = Skeleton,
        Loop = new LoopSettings { Enabled = Loop.Enabled, Start = Loop.Start, End = Loop.End },
        Tracks = new List<Track>(),
        Events = Events.Select(e => new AnimationEvent { Id = e.Id, Time = e.Time, Name = e.Name, Kind = e.Kind, Data = new Dictionary<string, string>(e.Data) }).ToList(),
        Markers = Markers.Select(m => new Marker { Id = m.Id, Time = m.Time, Name = m.Name, Phase = m.Phase }).ToList(),
        Combat = Combat is null ? null
            : System.Text.Json.JsonSerializer.Deserialize<ClipCombat>(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Combat, Persistence.ProjectArchive.CompactJsonOptions), Persistence.ProjectArchive.CompactJsonOptions),
        IsDraft = IsDraft,
        Metadata = new Dictionary<string, string>(Metadata),
    };
}

public sealed class LoopSettings
{
    public bool Enabled { get; set; }
    public Flicks Start { get; set; }

    /// <summary>Exclusive loop end. Zero means "clip duration".</summary>
    public Flicks End { get; set; }
}

public sealed class Track
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public TrackTarget Target { get; set; } = new();

    /// <summary>"translation", "rotation", "scale" or "custom:&lt;name&gt;".</summary>
    public string Channel { get; set; } = TrackChannels.Rotation;

    /// <summary>Keys sorted by time, unique times.</summary>
    public List<Keyframe> Keys { get; set; } = new();
    public bool Locked { get; set; }
    public string? Group { get; set; }

    /// <summary>Muted tracks are kept but not evaluated.</summary>
    public bool Muted { get; set; }

    /// <summary>Exact deep copy.</summary>
    public Track DeepClone()
    {
        var keys = new List<Keyframe>(Keys.Count);
        foreach (var k in Keys)
            keys.Add(new Keyframe
            {
                Time = k.Time,
                Interp = k.Interp,
                Value = (double[])k.Value.Clone(),
                InTangent = (double[]?)k.InTangent?.Clone(),
                OutTangent = (double[]?)k.OutTangent?.Clone(),
            });
        return new Track
        {
            Id = Id,
            Target = new TrackTarget { Kind = Target.Kind, Id = Target.Id },
            Channel = Channel,
            Keys = keys,
            Locked = Locked,
            Group = Group,
            Muted = Muted,
        };
    }
}

public static class TrackChannels
{
    public const string Translation = "translation";
    public const string Rotation = "rotation";
    public const string Scale = "scale";

    /// <summary>IK target pinned state (1 = pinned), always stepped.</summary>
    public const string Pinned = "pinned";
    public const string CustomPrefix = "custom:";

    /// <summary>Number of components for a channel value, or 0 if the channel name is invalid.</summary>
    public static int Arity(string channel) => channel switch
    {
        Translation or Scale => 3,
        Rotation => 4,
        Pinned => 1,
        _ when channel.StartsWith(CustomPrefix, StringComparison.Ordinal) && channel.Length > CustomPrefix.Length => 1,
        _ => 0,
    };
}

public sealed class TrackTarget
{
    public TargetKind Kind { get; set; } = TargetKind.Bone;
    public ObjectId Id { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<TargetKind>))]
public enum TargetKind
{
    Bone,
    Control,

    /// <summary>Reserved (never written by this build).</summary>
    Object,

    /// <summary>A <see cref="Prop"/> (schema v4): translation/rotation/scale of its offset (attached) or world transform.</summary>
    Prop,
}

[JsonConverter(typeof(JsonStringEnumConverter<Interpolation>))]
public enum Interpolation { Step, Linear, Cubic }

public sealed class Keyframe
{
    public Flicks Time { get; set; }
    public double[] Value { get; set; } = Array.Empty<double>();
    public Interpolation Interp { get; set; } = Interpolation.Cubic;

    /// <summary>glTF CUBICSPLINE-compatible tangents (value per second, per component).</summary>
    public double[]? InTangent { get; set; }
    public double[]? OutTangent { get; set; }
}

public sealed class AnimationEvent
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public Flicks Time { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Typed meaning for games (schema v5); older events are Custom.</summary>
    public EventKind Kind { get; set; } = EventKind.Custom;

    /// <summary>Payload (limits: <see cref="CombatLimits"/>).</summary>
    public Dictionary<string, string> Data { get; set; } = new();
}

/// <summary>A named point on a clip's timeline.</summary>
public sealed class Marker
{
    public ObjectId Id { get; set; } = ObjectId.New();
    public Flicks Time { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Which key-pose phase this marker represents, if any.</summary>
    public KeyPhase Phase { get; set; } = KeyPhase.None;
}

/// <summary>The pose-to-pose phases of an action (docs: product philosophy §2.1).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KeyPhase>))]
public enum KeyPhase { None, Initial, Anticipation, MainAction, Contact, FollowThrough, Recovery, Final }
