using Blazma.Core.Persistence;

namespace Blazma.Agent;

/// <summary>
/// File-system rules for paths that agents supply. Agents never get to touch network shares or
/// device files, never overwrite an existing file other than the project they have open, and only
/// write .blazanim projects.
/// </summary>
public static class AgentPaths
{
    /// <summary>Full path, or an <see cref="AgentRpcException"/> for empty, UNC, device or malformed paths.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new AgentRpcException("invalid_path", "A path is required.");
        var raw = path.Trim();
        if (raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal))
            throw new AgentRpcException("invalid_path", "Network and device paths are not allowed.");
        string full;
        try { full = Path.GetFullPath(raw); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AgentRpcException("invalid_path", "The path is not valid.");
        }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)
            || (!OperatingSystem.IsWindows() && (full.StartsWith("/dev/", StringComparison.Ordinal) || full.StartsWith("/proc/", StringComparison.Ordinal) || full.StartsWith("/sys/", StringComparison.Ordinal))))
            throw new AgentRpcException("invalid_path", "Network and device paths are not allowed.");
        return full;
    }

    /// <summary>
    /// Where an agent may save: the open project's own file, or a NEW .blazanim file. Existing files
    /// (other projects or anything else) are never overwritten by an agent.
    /// </summary>
    public static string ValidateSaveTarget(string? path, string? currentPath)
    {
        var full = Normalize(path);
        if (!full.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)) full += ProjectFile.Extension;
        var isCurrent = currentPath is not null && SamePath(full, Path.GetFullPath(currentPath));
        if (!isCurrent && (File.Exists(full) || Directory.Exists(full)))
            throw new AgentRpcException("file_exists", $"'{full}' already exists. Agents only save to the open project or to a new file; ask the user to overwrite it.");
        return full;
    }

    /// <summary>
    /// Where an agent may write an exported file: a NEW file with the required extension, in a folder that
    /// exists, never the open project. The folder's links are resolved, so the returned path is the
    /// real location (the caller checks it against the allowed folders).
    /// </summary>
    public static string ValidateNewFileTarget(string? path, string? extension, string? currentProjectPath)
    {
        var full = Normalize(path);
        if (extension is not null && !full.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            throw new AgentRpcException("unsupported_format", $"The path must end in {extension}.");
        var dir = Path.GetDirectoryName(full);
        if (dir is null || !Directory.Exists(dir))
            throw new AgentRpcException("not_found", $"The folder '{dir}' does not exist.");
        full = Path.Combine(ResolveLinks(dir), Path.GetFileName(full));
        if (currentProjectPath is not null && SamePath(full, Path.GetFullPath(currentProjectPath)))
            throw new AgentRpcException("file_exists", "Agents cannot overwrite the open project with an export.");
        if (File.Exists(full) || Directory.Exists(full) || new FileInfo(full).LinkTarget is not null)
            throw new AgentRpcException("file_exists", $"'{full}' already exists. Agents only create new files; ask the user to overwrite it.");
        return full;
    }

    /// <summary>Path with symbolic links resolved (so a link inside an allowed folder cannot point outside it).</summary>
    public static string ResolveLinks(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            var target = info.LinkTarget is null ? null : info.ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? fullPath;
        }
        catch (IOException) { return fullPath; }
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
