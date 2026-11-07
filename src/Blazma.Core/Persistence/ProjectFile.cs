using Blazma.Core.Model;

namespace Blazma.Core.Persistence;

/// <summary>How a save checks the temporary file before it replaces the destination.</summary>
public enum SaveVerification
{
    /// <summary>Re-open the archive: structure, file list equal to what was written, SHA-256 of every entry (default; fast).</summary>
    Integrity,

    /// <summary>Re-load the whole project (parse, migrate, validate) like opening it: slow on big projects; for tests and tools.</summary>
    FullReload,

    None,
}

/// <summary>Save/load a project to a .blazanim path with atomic replacement and read-back verification.</summary>
public static class ProjectFile
{
    public const string Extension = ".blazanim";

    public static void Save(Project project, string path, bool keepBackup = true, SaveVerification verification = SaveVerification.Integrity) =>
        WriteSnapshot(ProjectArchive.Capture(project, copy: false), path, keepBackup, verification); // synchronous: no copy needed

    /// <summary>
    /// Writes a snapshot taken with <see cref="ProjectArchive.Snapshot"/> (atomic replace, verification). Thread-safe: call it on a
    /// worker thread so a big save or autosave does not block the UI.
    /// </summary>
    public static void WriteSnapshot(ProjectSnapshot snapshot, string path, bool keepBackup = true, SaveVerification verification = SaveVerification.Integrity)
    {
        IReadOnlyDictionary<string, string>? manifest = null;
        AtomicFile.Write(path,
            stream => manifest = ProjectArchive.WriteWithManifest(snapshot, stream),
            verify: verification switch
            {
                SaveVerification.FullReload => temp => Load(temp),
                SaveVerification.Integrity => temp =>
                {
                    using var fs = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read);
                    ProjectArchive.VerifyIntegrity(fs, manifest);
                },
                _ => null,
            },
            keepBackup: keepBackup);
    }

    public static Project Load(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ProjectArchive.Read(fs);
        }
        catch (FileNotFoundException e)
        {
            throw new ProjectFormatException("not_found", $"File not found: {path}", e);
        }
        catch (DirectoryNotFoundException e)
        {
            throw new ProjectFormatException("not_found", $"File not found: {path}", e);
        }
    }
}
