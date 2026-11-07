using System.Text.Json;

namespace Blazma.Core.Persistence;

/// <summary>
/// Prevents two processes (e.g. the app and a headless CLI) from writing the same project.
/// Holds "&lt;project&gt;.lock" open with FileShare.None (an OS lock on Windows, flock on Unix),
/// so a lock left behind by a crash is not held by anyone and is simply taken over.
/// </summary>
public sealed class SessionLock : IDisposable
{
    readonly FileStream _stream;
    public string LockPath { get; }

    SessionLock(string lockPath, FileStream stream)
    {
        LockPath = lockPath;
        _stream = stream;
    }

    public static string LockPathFor(string projectPath) => Path.GetFullPath(projectPath) + ".lock";

    /// <summary>
    /// Returns null if another live process holds the lock (code locked_by_other). Any other failure throws
    /// <see cref="FileAccessException"/> with a <see cref="FileErrors"/> code: read_only_folder (no permission / read-only
    /// media), path_too_long, not_found (the folder is missing), disk_full or io_error — so a read-only folder is not misreported
    /// as "open in another window".
    /// </summary>
    public static SessionLock? TryAcquire(string projectPath)
    {
        string lockPath;
        try { lockPath = LockPathFor(projectPath); }
        catch (Exception e) when (e is PathTooLongException or ArgumentException or NotSupportedException)
        {
            throw FileErrors.Wrap(e, projectPath, e is PathTooLongException ? FileErrors.PathTooLong : FileErrors.IoError);
        }
        FileStream stream;
        try
        {
            stream = Open(lockPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var code = FileErrors.Classify(e);
            if (code is FileErrors.SharingViolation) return null; // another process holds the lock file open
            throw FileErrors.Wrap(e, projectPath, code);
        }

        var info = JsonSerializer.SerializeToUtf8Bytes(new
        {
            pid = Environment.ProcessId,
            host = Environment.MachineName,
            startedUtc = DateTimeOffset.UtcNow,
        });
        stream.SetLength(0);
        stream.Write(info);
        stream.Flush(flushToDisk: true);
        return new SessionLock(lockPath, stream);
    }

    /// <summary>Opens the lock file (replaceable in tests to simulate platform errors).</summary>
    internal static Func<string, FileStream> Open { get => OpenOverride.Value ?? DefaultOpen; set => OpenOverride.Value = value; }

    static readonly Func<string, FileStream> DefaultOpen = path => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    static readonly AsyncLocal<Func<string, FileStream>?> OpenOverride = new();

    public void Dispose()
    {
        _stream.Dispose();
        try { File.Delete(LockPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
