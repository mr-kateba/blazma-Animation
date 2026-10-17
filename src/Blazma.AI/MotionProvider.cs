using Blazma.Core.Mathematics;

namespace Blazma.AI;

/// <summary>
/// A source of generated motion (docs/AI_INTEGRATION.md §4). Implementations never download anything and never
/// send data over the network. <see cref="GenerateAsync"/> may run for minutes; it honours the token (Kimodo:
/// cancelling kills the runner process) and reports progress.
/// </summary>
public interface IMotionGenerationProvider
{
    /// <summary>Stable id used by commands: "kimodo", "procedural".</summary>
    string Id { get; }

    /// <summary>Cheap description (file checks only, no process started): name, licence notes, availability.</summary>
    ProviderInfo Describe();

    /// <summary>
    /// Diagnostics: what is installed and what the machine has (CPU/RAM/GPU best effort). With
    /// <paramref name="probe"/> the provider may also start its runtime briefly (Kimodo: runner hello/diagnose).
    /// Never uses the network.
    /// </summary>
    Task<ProviderDiagnostics> DiagnoseAsync(bool probe, CancellationToken ct);

    Task<MotionResult> GenerateAsync(MotionRequest request, IProgress<GenerationProgress>? progress, CancellationToken ct);
}

/// <param name="Prompt">UTF-8 text prompt.</param>
/// <param name="DurationSeconds">Requested length.</param>
/// <param name="Fps">Frame rate wanted by the caller. Providers may generate at their native rate (Kimodo: 30)
/// and say so in <see cref="MotionResult.Fps"/>; the retargeter resamples.</param>
/// <param name="Seed">Random seed; the same request and seed give the same motion.</param>
/// <param name="Constraints">Optional constraints. Kimodo.cpp does not implement constraint input yet, so
/// providers that cannot honour them return a warning instead of failing.</param>
public sealed record MotionRequest(string Prompt, double DurationSeconds, double Fps, ulong Seed, MotionConstraints? Constraints = null)
{
    public const int MaxPromptLength = 1000;
    public const double MaxDurationSeconds = 30;
    public const double MinDurationSeconds = 0.2;

    /// <summary>Throws <see cref="MotionGenerationException"/> (invalid_request) for out-of-range values.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Prompt)) throw new MotionGenerationException("invalid_request", "The prompt is empty.");
        if (Prompt.Length > MaxPromptLength) throw new MotionGenerationException("invalid_request", $"The prompt is too long (max {MaxPromptLength} characters).");
        if (!double.IsFinite(DurationSeconds) || DurationSeconds < MinDurationSeconds || DurationSeconds > MaxDurationSeconds)
            throw new MotionGenerationException("invalid_request", $"durationSeconds must be between {MinDurationSeconds} and {MaxDurationSeconds}.");
        if (!double.IsFinite(Fps) || Fps < 1 || Fps > 240) throw new MotionGenerationException("invalid_request", "fps must be between 1 and 240.");
    }

    /// <summary>Frames at <paramref name="fps"/> covering the duration (at least 2).</summary>
    public int FramesAt(double fps) => Math.Max(2, (int)Math.Round(DurationSeconds * fps));
}

/// <summary>Optional constraints. None are implemented by any provider yet (kimodo.cpp: "General constraint input … not implemented yet").</summary>
public sealed record MotionConstraints
{
    /// <summary>Ask for a motion whose last pose matches the first (best effort).</summary>
    public bool Loop { get; init; }

    /// <summary>Keep the root in place (no travel).</summary>
    public bool InPlace { get; init; }

    public bool IsEmpty => !Loop && !InPlace;
}

public sealed record GenerationProgress(string Stage, double Fraction, string? Message = null);

public sealed record ProviderInfo(
    string Id,
    string Name,
    string Kind,
    bool Available,
    string? UnavailableReason,
    string Licence,
    IReadOnlyList<string> Skeletons,
    double NativeFps,
    IReadOnlyList<string> Notes);

public sealed record ProviderDiagnostics(
    ProviderInfo Info,
    IReadOnlyDictionary<string, string?> Details,
    HardwareReport Hardware,
    IReadOnlyList<string> Warnings);

/// <summary>Source skeleton of a generated motion. Rest pose is given as parent-local transforms.</summary>
public sealed record SkeletonDefinition(
    string Key,
    IReadOnlyList<string> Names,
    IReadOnlyList<int> Parents,
    IReadOnlyList<Transform> Rest,
    string UpAxis,
    string ForwardAxis,
    IReadOnlyDictionary<string, string> StandardNames)
{
    public int Count => Names.Count;

    public int IndexOf(string name)
    {
        for (var i = 0; i < Names.Count; i++) if (Names[i] == name) return i;
        return -1;
    }

    /// <summary>Checks counts, parent order (parent before child) and finite values.</summary>
    public void Validate()
    {
        if (Names.Count == 0) throw new MotionGenerationException("invalid_motion", "The skeleton has no joints.");
        if (Parents.Count != Names.Count || Rest.Count != Names.Count)
            throw new MotionGenerationException("invalid_motion", "Skeleton names, parents and rest transforms differ in length.");
        for (var i = 0; i < Parents.Count; i++)
            if (Parents[i] >= i || Parents[i] < -1) throw new MotionGenerationException("invalid_motion", $"Joint '{Names[i]}' has an invalid parent index {Parents[i]}.");
        if (Names.Distinct().Count() != Names.Count) throw new MotionGenerationException("invalid_motion", "Joint names are not unique.");
        if (UpAxis != "+Y" || ForwardAxis != "+Z")
            throw new MotionGenerationException("invalid_motion", $"Unsupported axes up={UpAxis} forward={ForwardAxis} (expected +Y up, +Z forward).");
    }
}

/// <summary>
/// Generated motion in the provider's own skeleton: per frame, every joint's local rotation (relative to its parent,
/// replacing the rest rotation) and the root joint's translation (parent space = model space, metres).
/// </summary>
public sealed record MotionResult(
    string Provider,
    SkeletonDefinition Skeleton,
    double Fps,
    int FrameCount,
    Quat[] LocalRotations,
    Vec3[] RootTranslations,
    IReadOnlyList<string> Warnings)
{
    public int JointCount => Skeleton.Count;
    public Quat Rotation(int frame, int joint) => LocalRotations[frame * JointCount + joint];
    public double DurationSeconds => (FrameCount - 1) / Fps;

    /// <summary>Extra facts recorded in the clip metadata (model file, steps…).</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    /// <summary>Checks sizes and values; renormalizes rotations. Throws invalid_motion.</summary>
    public void Validate()
    {
        Skeleton.Validate();
        if (!double.IsFinite(Fps) || Fps <= 0 || Fps > 1000) throw new MotionGenerationException("invalid_motion", $"Invalid fps {Fps}.");
        if (FrameCount < 1 || FrameCount > 100_000) throw new MotionGenerationException("invalid_motion", $"Invalid frame count {FrameCount}.");
        if (LocalRotations.Length != FrameCount * JointCount) throw new MotionGenerationException("invalid_motion", "Rotation data does not match frames × joints.");
        if (RootTranslations.Length != FrameCount) throw new MotionGenerationException("invalid_motion", "Root translation data does not match the frame count.");
        for (var i = 0; i < LocalRotations.Length; i++)
        {
            var q = LocalRotations[i];
            var len = q.Length();
            if (!double.IsFinite(len) || len < 1e-6) throw new MotionGenerationException("invalid_motion", "A rotation is zero or not finite.");
            LocalRotations[i] = q.Normalized();
        }
        foreach (var p in RootTranslations)
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z) || p.Length() > 10_000)
                throw new MotionGenerationException("invalid_motion", "A root position is not finite or out of range.");
    }
}

/// <summary>A failure with a stable code (provider_unavailable, runner_crashed, runner_timeout, …).</summary>
public sealed class MotionGenerationException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
