namespace Blazma.Core.Persistence;

/// <summary>Path helpers shared by commands that write files outside the project and by the agent permission checks.</summary>
public static class FilePaths
{
    const int MaxLinkHops = 40;

    /// <summary>
    /// Full path with every symbolic link (or junction) in every component resolved: the real location
    /// the OS would use. Components that do not exist yet are kept as they are (they cannot be links).
    /// Throws <see cref="IOException"/> on a link loop.
    /// </summary>
    public static string Canonicalize(string path)
    {
        var hops = 0;
        return Resolve(Path.GetFullPath(path), ref hops);
    }

    static string Resolve(string full, ref int hops)
    {
        var root = Path.GetPathRoot(full) ?? "";
        var segments = full[root.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var next = Path.Combine(current, segments[i]);
            string? target;
            try { target = new FileInfo(next).LinkTarget; }
            catch (IOException) { target = null; }
            catch (UnauthorizedAccessException) { target = null; }
            if (target is null)
            {
                current = next;
                continue;
            }
            if (++hops > MaxLinkHops) throw new IOException($"Too many levels of symbolic links in '{full}'.");
            // The link's target is relative to the folder holding the link (already canonical), then the rest follows.
            var rest = segments.Length > i + 1 ? Path.Combine(segments[(i + 1)..]) : "";
            var resolved = Path.GetFullPath(rest.Length > 0 ? Path.Combine(Path.GetFullPath(target, current), rest) : Path.GetFullPath(target, current));
            return Resolve(resolved, ref hops);
        }
        return current;
    }

    static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars()
        .Concat(new[] { '<', '>', ':', '"', '|', '?', '*', '\\', '/' }).Distinct().ToArray();

    /// <summary>
    /// A plain file name that is valid on every platform: no separators, no ':' (NTFS alternate data
    /// streams, drive letters), no wildcard/control characters and no trailing dot or space.
    /// </summary>
    public static bool IsPortableFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(InvalidNameChars) < 0
        && !name.Any(char.IsControl)
        && !name.EndsWith('.') && !name.EndsWith(' ')
        && name is not ("." or "..");
}
