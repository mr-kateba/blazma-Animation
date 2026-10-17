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
        // Antivirus scanners and indexers briefly hold files on Windows: retry with backoff.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                    File.Move(temp, path, overwrite: true); // first save: nothing to replace (rename replaces a dangling link, never follows it)
                else if (OperatingSystem.IsWindows())
                    File.Replace(temp, path, backup, ignoreMetadataErrors: true);
                else
                {
                    if (backup is not null) Backup(path, backup);
                    File.Move(temp, path, overwrite: true); // rename(2): atomic on the same filesystem
                }
                return;
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                Thread.Sleep(50 * (1 << attempt));
            }
            catch (UnauthorizedAccessException) when (attempt < MaxAttempts && OperatingSystem.IsWindows())
            {
                Thread.Sleep(50 * (1 << attempt));
            }
        }
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
