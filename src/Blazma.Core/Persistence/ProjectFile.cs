using Blazma.Core.Model;

namespace Blazma.Core.Persistence;

/// <summary>Save/load a project to a .blazanim path with atomic replacement and read-back verification.</summary>
public static class ProjectFile
{
    public const string Extension = ".blazanim";

    public static void Save(Project project, string path, bool keepBackup = true)
    {
        AtomicFile.Write(path,
            stream => ProjectArchive.Write(project, stream),
            verify: temp => Load(temp),
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
