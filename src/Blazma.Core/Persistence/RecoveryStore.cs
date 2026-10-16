using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Blazma.Core.Ids;
using Blazma.Core.Model;

namespace Blazma.Core.Persistence;

/// <summary>
/// Autosave and crash recovery (docs/DATA_FORMAT.md §2).
/// Each open project gets a folder under <see cref="Root"/> with a session marker and two
/// rotating autosave generations, so an autosave interrupted mid-write never destroys the
/// previous one. A clean close deletes the folder; anything left behind after the owning
/// process died is offered for recovery.
/// </summary>
public sealed class RecoveryStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    const string MarkerFile = "session.json";
    static readonly string[] Generations = { "autosave-a.blazanim", "autosave-b.blazanim" };

    public RecoverySession BeginSession(Project project, string? projectPath)
    {
        var session = new RecoverySession(this, project.Id, project.Name, projectPath);
        session.WriteMarker();
        return session;
    }

    /// <summary>Sessions whose process is gone and whose autosave is newer than the saved file.</summary>
    public IReadOnlyList<RecoveryCandidate> FindRecoverable()
    {
        var result = new List<RecoveryCandidate>();
        if (!Directory.Exists(Root)) return result;

        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var marker = ReadMarker(dir);
            if (marker is null || IsAlive(marker)) continue;
            if (marker.LastAutosave is null || marker.LastAutosaveUtc is null) continue;

            var autosave = Path.Combine(dir, marker.LastAutosave);
            if (!File.Exists(autosave)) continue;

            var newer = marker.ProjectPath is null || !File.Exists(marker.ProjectPath)
                || File.GetLastWriteTimeUtc(marker.ProjectPath) < marker.LastAutosaveUtc.Value.UtcDateTime;
            if (newer)
                result.Add(new RecoveryCandidate(dir, autosave, marker.ProjectPath, marker.ProjectName, marker.LastAutosaveUtc.Value));
        }
        return result;
    }

    /// <summary>Loads the newest readable generation; falls back to the older one if the newest is damaged.</summary>
    public Project Recover(RecoveryCandidate candidate)
    {
        var tried = new List<string> { candidate.AutosavePath };
        tried.AddRange(Generations.Select(g => Path.Combine(candidate.Folder, g)).Where(p => p != candidate.AutosavePath));
        ProjectFormatException? last = null;
        foreach (var path in tried.Where(File.Exists))
        {
            try { return ProjectFile.Load(path); }
            catch (ProjectFormatException e) { last = e; }
        }
        throw last ?? new ProjectFormatException("not_found", "No readable autosave was found.");
    }

    public void Discard(RecoveryCandidate candidate) => DeleteFolder(candidate.Folder);

    internal string FolderFor(ObjectId projectId, string? projectPath)
    {
        string key;
        if (projectPath is null)
            key = "untitled-" + projectId;
        else
        {
            var full = Path.GetFullPath(projectPath);
            if (OperatingSystem.IsWindows()) full = full.ToLowerInvariant();
            key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16].ToLowerInvariant();
        }
        return Path.Combine(Root, key);
    }

    internal static SessionMarker? ReadMarker(string dir)
    {
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(dir, MarkerFile));
            return JsonSerializer.Deserialize<SessionMarker>(bytes, ProjectArchive.JsonOptions);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void WriteMarker(string dir, SessionMarker marker)
    {
        AtomicFile.Write(Path.Combine(dir, MarkerFile),
            s => JsonSerializer.Serialize(s, marker, ProjectArchive.JsonOptions), keepBackup: false);
    }

    internal static string NextGeneration(string? current) =>
        current == Generations[0] ? Generations[1] : Generations[0];

    internal static void DeleteFolder(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static bool IsAlive(SessionMarker m)
    {
        if (m.Host != Environment.MachineName) return false;
        try
        {
            using var p = Process.GetProcessById(m.Pid);
            // Guard against PID reuse: the process must have started around the recorded time.
            return Math.Abs((p.StartTime.ToUniversalTime() - m.ProcessStartUtc.UtcDateTime).TotalSeconds) < 5;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

public sealed record RecoveryCandidate(
    string Folder, string AutosavePath, string? ProjectPath, string ProjectName, DateTimeOffset AutosaveUtc);

internal sealed class SessionMarker
{
    public int Pid { get; set; }
    public string Host { get; set; } = "";
    public DateTimeOffset ProcessStartUtc { get; set; }
    public ObjectId ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public string? ProjectPath { get; set; }
    public string? LastAutosave { get; set; }
    public DateTimeOffset? LastAutosaveUtc { get; set; }
}

/// <summary>Recovery bookkeeping for one open project. Call from the thread that owns the project.</summary>
public sealed class RecoverySession
{
    readonly RecoveryStore _store;
    readonly SessionMarker _marker;
    string _folder;

    internal RecoverySession(RecoveryStore store, ObjectId projectId, string name, string? path)
    {
        _store = store;
        using var proc = Process.GetCurrentProcess();
        _marker = new SessionMarker
        {
            Pid = Environment.ProcessId,
            Host = Environment.MachineName,
            ProcessStartUtc = proc.StartTime.ToUniversalTime(),
            ProjectId = projectId,
            ProjectName = name,
            ProjectPath = path is null ? null : Path.GetFullPath(path),
        };
        _folder = store.FolderFor(projectId, path);
    }

    public string Folder => _folder;
    public DateTimeOffset? LastAutosaveUtc => _marker.LastAutosaveUtc;

    internal void WriteMarker()
    {
        Directory.CreateDirectory(_folder);
        RecoveryStore.WriteMarker(_folder, _marker);
    }

    /// <summary>Writes an autosave to the older generation, then points the marker at it.</summary>
    public void Autosave(Project project)
    {
        Directory.CreateDirectory(_folder);
        var gen = RecoveryStore.NextGeneration(_marker.LastAutosave);
        ProjectFile.Save(project, Path.Combine(_folder, gen), keepBackup: false);
        _marker.LastAutosave = gen;
        _marker.LastAutosaveUtc = DateTimeOffset.UtcNow;
        _marker.ProjectName = project.Name;
        RecoveryStore.WriteMarker(_folder, _marker);
    }

    /// <summary>After an explicit save the file on disk is newest: drop autosaves, keep the marker.</summary>
    public void MarkSaved(string projectPath, Project project)
    {
        var newFolder = _store.FolderFor(project.Id, projectPath);
        if (newFolder != _folder)
        {
            RecoveryStore.DeleteFolder(_folder);
            _folder = newFolder;
        }
        _marker.ProjectPath = Path.GetFullPath(projectPath);
        _marker.ProjectName = project.Name;
        _marker.LastAutosave = null;
        _marker.LastAutosaveUtc = null;
        RecoveryStore.DeleteFolder(_folder);
        WriteMarker();
    }

    /// <summary>Clean close: nothing to recover.</summary>
    public void EndCleanly() => RecoveryStore.DeleteFolder(_folder);
}
