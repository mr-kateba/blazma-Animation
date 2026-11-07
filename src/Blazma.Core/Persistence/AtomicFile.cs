namespace Blazma.Core.Persistence;

/// <summary>The destination of a no-overwrite <see cref="AtomicFile.Write"/> already exists.</summary>
public sealed class FileAlreadyExistsException(string path)
    : IOException($"'{path}' already exists.")
{
    public string Path { get; } = path;
}

/// <summary>
/// Crash-safe file replacement (docs/DATA_FORMAT.md §2):
/// write a new temporary file with a random name (".blz-&lt;random&gt;.tmp") in the same directory → flush to disk → verify →
/// swap in atomically, keeping the previous version as "&lt;path&gt;.bak". The destination is never
/// left half-written. The temporary file is created exclusively (O_EXCL): an existing file or a
/// planted symbolic link with that name is never followed or truncated.
/// </summary>
public static class AtomicFile
{
    const int MaxAttempts = 6;

    /// <summary>How long a save keeps retrying while another program (antivirus, indexer, sync client) holds the file.</summary>
    internal static readonly TimeSpan SharingRetryBudget = TimeSpan.FromSeconds(10);

    /// <summary>The OS calls a swap uses (replaceable in tests to simulate Windows and network-drive failures).</summary>
    internal sealed class FileOps
    {
        public Func<bool> UseReplace = OperatingSystem.IsWindows;
        public Action<string, string, string?> Replace = (src, dst, backup) => File.Replace(src, dst, backup, ignoreMetadataErrors: true);
        public Action<string, string, bool> Move = File.Move;
        public Action<int> Sleep = Thread.Sleep;
    }

    static readonly FileOps DefaultOps = new();
    static readonly AsyncLocal<FileOps?> OpsOverride = new();

    /// <summary>Per async flow, so a test's fake never leaks into other tests running in parallel.</summary>
    internal static FileOps Ops { get => OpsOverride.Value ?? DefaultOps; set => OpsOverride.Value = value; }

    /// <param name="overwrite">False: the final rename fails if <paramref name="path"/> exists by then
    /// (even if it appeared while writing), with <see cref="FileAlreadyExistsException"/>; nothing is replaced.</param>
    public static void Write(string path, Action<Stream> write, Action<string>? verify = null, bool keepBackup = true, bool overwrite = true)
    {
        path = System.IO.Path.GetFullPath(path);
        var dir = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var backup = path + ".bak";

        var (temp, fs) = CreateTemp(dir);
        try
        {
            using (fs)
            {
                write(fs);
                fs.Flush(flushToDisk: true);
            }
            verify?.Invoke(temp);
            if (overwrite) Swap(temp, path, keepBackup ? backup : null);
            else MoveNew(temp, path);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>A new, exclusively created file ".blz-&lt;random&gt;.tmp" next to the destination (short, so any
    /// destination name that fits the file system also has a temporary file that fits).</summary>
    static (string Path, FileStream Stream) CreateTemp(string dir)
    {
        for (var attempt = 0; ; attempt++)
        {
            var temp = System.IO.Path.Combine(dir, $".blz-{Guid.NewGuid():N}.tmp");
            try { return (temp, new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)); }
            catch (IOException) when (attempt < 4 && Exists(temp)) { } // name taken (astronomically unlikely): pick another
        }
    }

    static void MoveNew(string temp, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (Exists(path)) throw new FileAlreadyExistsException(path);
            try
            {
                File.Move(temp, path, overwrite: false); // link(2)/MoveFileEx without replace: fails if the target exists
                return;
            }
            catch (IOException) when (Exists(path)) { throw new FileAlreadyExistsException(path); }
            catch (IOException) when (attempt < MaxAttempts) { Thread.Sleep(50 * (1 << attempt)); }
            catch (UnauthorizedAccessException) when (attempt < MaxAttempts && OperatingSystem.IsWindows()) { Thread.Sleep(50 * (1 << attempt)); }
        }
    }

    static void Swap(string temp, string path, string? backup)
    {
        var ops = Ops;
        long slept = 0;
        var replaceUnsupported = false;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                    ops.Move(temp, path, true); // first save: nothing to replace (rename replaces a dangling link, never follows it)
                else if (ops.UseReplace() && !replaceUnsupported)
                {
                    try { ops.Replace(temp, path, backup); }
                    catch (Exception e) when (IsReplaceUnsupported(e))
                    {
                        // File.Replace (ReplaceFile) fails on many network shares and sync folders (SMB, OneDrive, Dropbox):
                        // 1175/1176/1177 or "not supported". Fall back to backup copy + rename-over, which is still atomic for the
                        // destination (MoveFileEx with MOVEFILE_REPLACE_EXISTING). After 1176 the original may already have been
                        // moved to the backup name: then the destination is simply missing and the move below creates it.
                        replaceUnsupported = true;
                        CopyFallback(temp, path, backup, ops);
                    }
                }
                else CopyFallback(temp, path, backup, ops);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && ShouldRetry(e, attempt, slept))
            {
                var delay = Math.Min(1000, 50 * (1 << Math.Min(attempt, 5)));
                ops.Sleep(delay);
                slept += delay;
            }
        }
    }

    static void CopyFallback(string temp, string path, string? backup, FileOps ops)
    {
        if (backup is not null && File.Exists(path)) Backup(path, backup);
        ops.Move(temp, path, true); // rename(2) / MoveFileEx(REPLACE_EXISTING): atomic on the same volume
    }

    /// <summary>Win32 1175 (unable to remove replaced), 1176 (unable to move replacement), 1177 (unable to move replacement 2),
    /// or a share that does not implement ReplaceFile at all (ERROR_NOT_SUPPORTED 50, ERROR_INVALID_FUNCTION 1).</summary>
    internal static bool IsReplaceUnsupported(Exception e) =>
        e is PlatformNotSupportedException or NotSupportedException
        || e is IOException io && (io.HResult & 0xFFFF0000) == 0x80070000 && (io.HResult & 0xFFFF) is 1175 or 1176 or 1177 or 50 or 1;

    /// <summary>A sharing violation (antivirus, indexer, sync client; or "access denied" while a delete is pending on Windows)
    /// is retried with backoff for up to <see cref="SharingRetryBudget"/>; other I/O errors a few times.</summary>
    static bool ShouldRetry(Exception e, int attempt, long sleptMs)
    {
        var sharing = FileErrors.Classify(e) == FileErrors.SharingViolation
            || (e is UnauthorizedAccessException && Ops.UseReplace());
        if (sharing) return sleptMs < SharingRetryBudget.TotalMilliseconds;
        return e is IOException && attempt < MaxAttempts && FileErrors.Classify(e) is FileErrors.IoError;
    }

    /// <summary>Copy to a fresh temporary file, then rename over the backup (a link named like the backup is replaced, not followed).</summary>
    static void Backup(string path, string backup)
    {
        var (temp, fs) = CreateTemp(System.IO.Path.GetDirectoryName(backup)!);
        try
        {
            using (fs)
            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                src.CopyTo(fs);
            File.Move(temp, backup, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    static bool Exists(string p) => File.Exists(p) || Directory.Exists(p) || IsLink(p);

    static bool IsLink(string p)
    {
        try { return new FileInfo(p).LinkTarget is not null; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static void TryDelete(string p)
    {
        try { if (File.Exists(p) || IsLink(p)) File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
