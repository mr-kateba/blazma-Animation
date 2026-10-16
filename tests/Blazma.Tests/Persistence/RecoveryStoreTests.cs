using System.Text.Json.Nodes;
using Blazma.Core.Persistence;

namespace Blazma.Tests.Persistence;

public class RecoveryStoreTests
{
    // A marker written by "another process" that is no longer running.
    static void PretendProcessDied(string folder)
    {
        var markerPath = Path.Combine(folder, "session.json");
        var marker = JsonNode.Parse(File.ReadAllText(markerPath))!.AsObject();
        marker["pid"] = 999_999_999;
        File.WriteAllText(markerPath, marker.ToJsonString());
    }

    [Fact]
    public void LiveSession_IsNotOfferedForRecovery()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.File("recovery"));
        var session = store.BeginSession(ProjectFileTests.Sample(), null);
        session.Autosave(ProjectFileTests.Sample());
        Assert.Empty(store.FindRecoverable());
    }

    [Fact]
    public void CrashedUnsavedProject_IsRecoverable()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.File("recovery"));
        var project = ProjectFileTests.Sample();
        var session = store.BeginSession(project, null);
        project.Name = "Edited after start";
        session.Autosave(project);
        PretendProcessDied(session.Folder);

        var candidate = Assert.Single(store.FindRecoverable());
        Assert.Null(candidate.ProjectPath);
        var recovered = store.Recover(candidate);
        Assert.Equal("Edited after start", recovered.Name);
        Assert.Equal(project.Clips.Count, recovered.Clips.Count);
    }

    [Fact]
    public void CleanClose_LeavesNothing()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.File("recovery"));
        var session = store.BeginSession(ProjectFileTests.Sample(), null);
        session.Autosave(ProjectFileTests.Sample());
        session.EndCleanly();
        Assert.False(Directory.Exists(session.Folder));
    }

    [Fact]
    public void AutosaveOlderThanSavedFile_IsNotOffered()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.File("recovery"));
        var path = dir.File("p.blazanim");
        var project = ProjectFileTests.Sample();
        var session = store.BeginSession(project, path);
        session.Autosave(project);
        Thread.Sleep(20);
        ProjectFile.Save(project, path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        PretendProcessDied(session.Folder);
        Assert.Empty(store.FindRecoverable());
    }

    [Fact]
    public void AfterExplicitSave_AutosavesAreDropped()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.File("recovery"));
        var path = dir.File("p.blazanim");
        var project = ProjectFileTests.Sample();
        var session = store.BeginSession(project, null);
        session.Autosave(project);
        ProjectFile.Save(project, path);
        session.MarkSaved(path, project);
        PretendProcessDied(session.Folder);
        Assert.Empty(store.FindRecoverable());
        Assert.Null(session.LastAutosaveUtc);
    }

    [Fact]
    public void DamagedNewestGeneration_FallsBackToPrevious()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.File("recovery"));
        var project = ProjectFileTests.Sample();
        var session = store.BeginSession(project, null);
        project.Name = "Gen A";
        session.Autosave(project);
        project.Name = "Gen B";
        session.Autosave(project);
        PretendProcessDied(session.Folder);

        var candidate = Assert.Single(store.FindRecoverable());
        File.WriteAllText(candidate.AutosavePath, "truncated"); // crash in the middle of copying, disk error, ...
        Assert.Equal("Gen A", store.Recover(candidate).Name);
    }

    [Fact]
    public void Discard_RemovesCandidate()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.File("recovery"));
        var session = store.BeginSession(ProjectFileTests.Sample(), null);
        session.Autosave(ProjectFileTests.Sample());
        PretendProcessDied(session.Folder);
        store.Discard(Assert.Single(store.FindRecoverable()));
        Assert.Empty(store.FindRecoverable());
    }
}
