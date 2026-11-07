namespace Blazma.Core.Persistence;

/// <summary>A file operation failed for a reason the user can act on. <see cref="Code"/> is one of the <see cref="FileErrors"/>
/// codes; it derives from IOException so existing "catch (IOException)" handlers still see it.</summary>
public sealed class FileAccessException(string code, string path, string message, Exception? inner = null) : IOException(message, inner)
{
    public string Code { get; } = code;
    public string Path { get; } = path;
}

/// <summary>
/// Stable codes for file errors (Windows and Unix), so the app, CLI and MCP tools can show the same advice:
/// read_only_folder, path_too_long, sharing_violation, not_found, locked_by_other, disk_full, io_error.
/// </summary>
public static class FileErrors
{
    public const string ReadOnlyFolder = "read_only_folder";
    public const string PathTooLong = "path_too_long";
    public const string SharingViolation = "sharing_violation";
    public const string NotFound = "not_found";
    public const string LockedByOther = "locked_by_other";
    public const string DiskFull = "disk_full";
    public const string IoError = "io_error";

    /// <summary>The code for any exception from a file operation ("" when it is not a file error).
    /// <see cref="ProjectFormatException"/> and <see cref="FileAccessException"/> keep their own code.</summary>
    public static string Classify(Exception e) => e switch
    {
        FileAccessException f => f.Code,
        ProjectFormatException f => f.Code,
        UnauthorizedAccessException => ReadOnlyFolder,
        PathTooLongException => PathTooLong,
        FileNotFoundException or DirectoryNotFoundException => NotFound,
        IOException io => ClassifyHResult(io.HResult),
        _ => "",
    };

    /// <summary>Windows HRESULTs (0x8007xxxx, Win32 code in the low word) or a raw Unix errno, as .NET reports them.</summary>
    internal static string ClassifyHResult(int hr)
    {
        if ((hr & 0xFFFF0000) == 0x80070000)
        {
            return (hr & 0xFFFF) switch
            {
                32 or 33 => SharingViolation,               // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
                5 or 19 => ReadOnlyFolder,                   // ERROR_ACCESS_DENIED, ERROR_WRITE_PROTECT
                2 or 3 or 15 or 53 or 67 => NotFound,        // file/path/drive/network path/name not found
                206 or 111 => PathTooLong,                   // ERROR_FILENAME_EXCED_RANGE, ERROR_BUFFER_OVERFLOW
                112 or 39 => DiskFull,                       // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL
                _ => IoError,
            };
        }
        return hr switch
        {
            11 or 35 => SharingViolation,                    // EWOULDBLOCK (Linux, macOS): flock held by another process
            13 or 1 or 30 => ReadOnlyFolder,                 // EACCES, EPERM, EROFS
            2 => NotFound,                                   // ENOENT
            36 or 63 => PathTooLong,                         // ENAMETOOLONG (Linux, macOS)
            28 or 122 or 69 => DiskFull,                     // ENOSPC, EDQUOT (Linux, macOS)
            _ => IoError,
        };
    }

    /// <summary>Plain-English advice for a code (the app shows its own translated text for the same codes).</summary>
    public static string Message(string code, string path) => code switch
    {
        ReadOnlyFolder => $"Blazma cannot write to the folder of '{path}' (read-only or no permission). Choose another folder or check its permissions.",
        PathTooLong => $"The path '{path}' is too long. Choose a shorter folder or file name.",
        SharingViolation => $"'{path}' is being used by another program (for example an antivirus scan or a sync client). Close it there and try again.",
        NotFound => $"'{path}' or its folder could not be found.",
        LockedByOther => $"'{path}' is open in another Blazma window or tool.",
        DiskFull => $"There is not enough disk space to write '{path}'.",
        _ => $"'{path}' could not be accessed.",
    };

    /// <summary>Wrap a file exception into a <see cref="FileAccessException"/> with its code and advice (keeps the original as inner).</summary>
    public static FileAccessException Wrap(Exception e, string path, string? code = null)
    {
        code ??= Classify(e);
        if (code is "" ) code = IoError;
        return new FileAccessException(code, path, $"{Message(code, path)} ({e.Message})", e);
    }
}
