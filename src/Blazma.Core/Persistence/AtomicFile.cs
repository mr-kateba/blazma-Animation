namespace Blazma.Core.Persistence;

/// <summary>
/// Crash-safe file replacement (docs/DATA_FORMAT.md §2):
/// write "&lt;path&gt;.tmp" in the same directory → flush to disk → verify → swap in atomically,
/// keeping the previous version as "&lt;path&gt;.bak". The destination is never left half-written.
/// </summary>
public static class AtomicFile
{
    const int MaxAttempts = 6;

    public static void Write(string path, Action<Stream> write, Action<string>? verify = null, bool keepBackup = true)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        var backup = path + ".bak";

        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                write(fs);
                fs.Flush(flushToDisk: true);
            }
            verify?.Invoke(temp);
            Swap(temp, path, keepBackup ? backup : null);
        }
        catch
        {
            TryDelete(temp);
            throw;
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
                    File.Move(temp, path); // first save: nothing to replace
                else if (OperatingSystem.IsWindows())
                    File.Replace(temp, path, backup, ignoreMetadataErrors: true);
                else
                {
                    if (backup is not null) File.Copy(path, backup, overwrite: true);
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

    static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
