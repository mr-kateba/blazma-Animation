namespace Blazma.Export;

/// <summary>What happens to the root-motion bone's translation in the exported animation.</summary>
public enum RootMotionMode
{
    /// <summary>Unchanged: the character travels in the file.</summary>
    Keep,

    /// <summary>Horizontal (X/Z) travel removed relative to the first exported frame; height (Y) kept.</summary>
    InPlace,

    /// <summary>All translation (X/Y/Z) of the root-motion bone removed relative to the first exported frame.</summary>
    InPlaceAll,
}

public sealed record ExportOptions
{
    /// <summary>First exported frame (default 0). It becomes time 0 in the file.</summary>
    public long? StartFrame { get; init; }

    /// <summary>Last exported frame, inclusive (default: the clip's duration in frames).</summary>
    public long? EndFrame { get; init; }

    public RootMotionMode RootMotion { get; init; } = RootMotionMode.Keep;

    /// <summary>
    /// Channels whose value never changes are written with two keys (first and last frame) instead of
    /// one per frame. Two, not one, so every channel spans the whole clip and engines that derive the
    /// length from the keys (or that blend channel by channel) see the same duration everywhere.
    /// </summary>
    public bool CompactConstantChannels { get; init; } = true;

    /// <summary>Animation name in the file (default: the clip name; a suffix is added on collision).</summary>
    public string? AnimationName { get; init; }

    /// <summary>Write props attached to the character or animated by the clip as extra nodes with channels (default true).</summary>
    public bool IncludeProps { get; init; } = true;
}

/// <summary>An export failure with a stable code (e.g. unsupported_source, missing_asset).</summary>
public sealed class ExportException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class RootMotionModes
{
    public static string Name(RootMotionMode m) => m switch
    {
        RootMotionMode.InPlace => "in_place",
        RootMotionMode.InPlaceAll => "in_place_all",
        _ => "keep",
    };

    public static bool TryParse(string? s, out RootMotionMode mode)
    {
        mode = RootMotionMode.Keep;
        switch (s?.Trim().ToLowerInvariant().Replace("-", "_"))
        {
            case null or "" or "keep": return true;
            case "in_place" or "inplace": mode = RootMotionMode.InPlace; return true;
            case "in_place_all" or "inplaceall": mode = RootMotionMode.InPlaceAll; return true;
            default: return false;
        }
    }
}
