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

    /// <summary>Returns null if another live process holds the lock.</summary>
    public static SessionLock? TryAcquire(string projectPath)
    {
        var lockPath = LockPathFor(projectPath);
        FileStream stream;
        try
        {
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
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

    public void Dispose()
    {
        _stream.Dispose();
        try { File.Delete(LockPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
