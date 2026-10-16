using System.IO.Compression;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Core.Timing;

namespace Blazma.Tests.Persistence;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "blazma-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class ProjectFileTests
{
    internal static Project Sample()
    {
        var p = Project.CreateNew("Demo — عرض", FrameRate.Fps30);
        var clip = new Clip { Name = "Walk", Fps = FrameRate.Fps30, Duration = Flicks.FromFrame(48, FrameRate.Fps30) };
        clip.Tracks.Add(new Track
        {
            Target = new TrackTarget { Kind = TargetKind.Bone, Id = Blazma.Core.Ids.ObjectId.New() },
            Channel = TrackChannels.Rotation,
            Group = "Legs",
            Keys =
            {
                new Keyframe { Time = Flicks.Zero, Value = new[] { 0.0, 0.0, 0.0, 1.0 }, Interp = Interpolation.Cubic,
                               InTangent = new[] { 0.0, 0, 0, 0 }, OutTangent = new[] { 0.1, 0, 0, 0 } },
                new Keyframe { Time = Flicks.FromFrame(24, FrameRate.Fps30), Value = new[] { 0.1, 0.2, 0.3, 0.9273618495495703 }, Interp = Interpolation.Linear },
            },
        });
        clip.Events.Add(new AnimationEvent { Name = "foot_contact_l", Time = Flicks.FromFrame(12, FrameRate.Fps30), Data = { ["side"] = "l" } });
        p.Clips.Add(clip);
        p.Clips.Add(new Clip { Name = "Jump", Fps = FrameRate.Ntsc2997, Duration = Flicks.FromFrame(90, FrameRate.Ntsc2997), IsDraft = true });
        p.Metadata["author"] = "test";
        return p;
    }

    [Fact]
    public void SaveThenLoad_PreservesEverything()
    {
        using var dir = new TempDir();
        var path = dir.File("demo.blazanim");
        var original = Sample();

        ProjectFile.Save(original, path);
        var loaded = ProjectFile.Load(path);

        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal(original.Name, loaded.Name);
        Assert.Equal(original.CreatedUtc, loaded.CreatedUtc);
        Assert.Equal(original.Settings.DefaultFrameRate, loaded.Settings.DefaultFrameRate);
        Assert.Equal(original.Metadata, loaded.Metadata);
        Assert.Equal(original.Clips.Count, loaded.Clips.Count);
        for (var i = 0; i < original.Clips.Count; i++)
        {
            var a = original.Clips[i];
            var b = loaded.Clips[i];
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.Fps, b.Fps);
            Assert.Equal(a.Duration, b.Duration); // exact: 0 flick tolerance after save/load
            Assert.Equal(a.IsDraft, b.IsDraft);
            Assert.Equal(a.Tracks.Count, b.Tracks.Count);
            for (var t = 0; t < a.Tracks.Count; t++)
            {
                Assert.Equal(a.Tracks[t].Target.Id, b.Tracks[t].Target.Id);
                Assert.Equal(a.Tracks[t].Group, b.Tracks[t].Group);
                for (var k = 0; k < a.Tracks[t].Keys.Count; k++)
                {
                    var ka = a.Tracks[t].Keys[k];
                    var kb = b.Tracks[t].Keys[k];
                    Assert.Equal(ka.Time, kb.Time);
                    Assert.Equal(ka.Value, kb.Value); // doubles round-trip bit-exactly
                    Assert.Equal(ka.Interp, kb.Interp);
                    Assert.Equal(ka.OutTangent, kb.OutTangent);
                }
            }
            Assert.Equal(a.Events.Select(e => (e.Name, e.Time)), b.Events.Select(e => (e.Name, e.Time)));
        }
    }

    [Fact]
    public void SecondSave_KeepsPreviousVersionAsBackup_AndLeavesNoTemp()
    {
        using var dir = new TempDir();
        var path = dir.File("p.blazanim");
        var p = Sample();
        ProjectFile.Save(p, path);
        p.Name = "Renamed";
        ProjectFile.Save(p, path);

        Assert.Equal("Renamed", ProjectFile.Load(path).Name);
        Assert.Equal("Demo — عرض", ProjectFile.Load(path + ".bak").Name);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void FailedWrite_LeavesExistingFileUntouched()
    {
        using var dir = new TempDir();
        var path = dir.File("p.blazanim");
        ProjectFile.Save(Sample(), path);
        var before = File.ReadAllBytes(path);

        Assert.Throws<InvalidOperationException>(() =>
            AtomicFile.Write(path, s => { s.WriteByte(1); throw new InvalidOperationException("simulated crash"); }));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void CorruptedEntry_IsDetectedByChecksum()
    {
        using var dir = new TempDir();
        var path = dir.File("p.blazanim");
        var p = Sample();
        ProjectFile.Save(p, path);

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry($"clips/{p.Clips[0].Id}.json")!;
            string text;
            using (var r = new StreamReader(entry.Open())) text = r.ReadToEnd();
            entry.Delete();
            using var w = new StreamWriter(zip.CreateEntry($"clips/{p.Clips[0].Id}.json").Open());
            w.Write(text.Replace("Walk", "Wall"));
        }

        var e = Assert.Throws<ProjectFormatException>(() => ProjectFile.Load(path));
        Assert.Equal("integrity", e.Code);
    }

    [Fact]
    public void NotAZip_GivesClearError()
    {
        using var dir = new TempDir();
        var path = dir.File("bad.blazanim");
        File.WriteAllText(path, "hello");
        Assert.Equal("not_a_project", Assert.Throws<ProjectFormatException>(() => ProjectFile.Load(path)).Code);
    }

    [Fact]
    public void MissingFile_GivesNotFound()
    {
        Assert.Equal("not_found", Assert.Throws<ProjectFormatException>(() => ProjectFile.Load("/nonexistent/x.blazanim")).Code);
    }

    [Fact]
    public void NewerSchema_IsRefused()
    {
        using var dir = new TempDir();
        var path = dir.File("future.blazanim");
        ProjectFile.Save(Sample(), path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("project.json")!;
            string text;
            using (var r = new StreamReader(entry.Open())) text = r.ReadToEnd();
            entry.Delete();
            using var w = new StreamWriter(zip.CreateEntry("project.json").Open());
            w.Write(text.Replace("\"schemaVersion\": 3", "\"schemaVersion\": 99"));
        }
        Assert.Equal("schema_too_new", Assert.Throws<ProjectFormatException>(() => ProjectFile.Load(path)).Code);
    }

    [Fact]
    public void Archive_IsReproducible()
    {
        var p = Sample();
        using var a = new MemoryStream();
        using var b = new MemoryStream();
        ProjectArchive.Write(p, a);
        ProjectArchive.Write(p, b);
        Assert.Equal(a.ToArray(), b.ToArray());
    }

    [Fact]
    public void SessionLock_IsExclusive_AndReleasable()
    {
        using var dir = new TempDir();
        var path = dir.File("p.blazanim");
        using (var first = SessionLock.TryAcquire(path))
        {
            Assert.NotNull(first);
            Assert.Null(SessionLock.TryAcquire(path));
        }
        using var again = SessionLock.TryAcquire(path);
        Assert.NotNull(again);
    }

    [Fact]
    public void StaleLockFile_FromCrashedProcess_IsTakenOver()
    {
        using var dir = new TempDir();
        var path = dir.File("p.blazanim");
        File.WriteAllText(SessionLock.LockPathFor(path), "{\"pid\":999999}");
        using var l = SessionLock.TryAcquire(path);
        Assert.NotNull(l);
    }
}

public class SchemaMigrationTests
{
    [Fact]
    public void V2File_FromPhase2Release_OpensWithPosesRigsAndEmbeddedModel()
    {
        var p = ProjectFile.Load(Fixture("schema-v2/project-v2.blazanim"));
        Assert.Equal("Schema v2 fixture", p.Name);
        Assert.Equal(2, p.Rigs.Count);
        Assert.Equal(2, p.Characters.Count);
        Assert.Single(p.PoseLibrary);
        Assert.Single(p.Assets);
        Assert.True(p.Characters[0].Pose.Bones.Count > 0);
        Assert.Empty(p.Clips[0].Markers);

        using var dir = new TempDir();
        var path = dir.File("v3.blazanim");
        ProjectFile.Save(p, path);
        var again = ProjectFile.Load(path);
        Assert.True(p.Characters[0].Pose.ContentEquals(again.Characters[0].Pose, 0));
    }

    static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", name);

    [Fact]
    public void V1File_OpensAndUpgrades_WithoutLosingData()
    {
        var p = ProjectFile.Load(Fixture("schema-v1/project-v1.blazanim"));
        Assert.Equal("Schema v1 fixture", p.Name);
        Assert.Equal(new[] { "Walk", "Jump" }, p.Clips.Select(c => c.Name));
        Assert.Equal(48, p.Clips[0].DurationFrames);
        Assert.Equal(new FrameRate(30000, 1001), p.Clips[1].Fps);
        Assert.Empty(p.Rigs);
        Assert.Empty(p.Characters);
        Assert.Empty(p.PoseLibrary);

        // Re-saving writes the current schema and still round-trips.
        using var dir = new TempDir();
        var path = dir.File("upgraded.blazanim");
        ProjectFile.Save(p, path);
        var again = ProjectFile.Load(path);
        Assert.Equal(p.Id, again.Id);
        Assert.Equal(p.Clips.Select(c => c.Duration), again.Clips.Select(c => c.Duration));
    }
}
