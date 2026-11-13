using System.Diagnostics;
using System.IO.Compression;
using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Tests.Advanced;
using Blazma.Tests.Persistence;
using Xunit.Abstractions;

namespace Blazma.Tests.Commands;

/// <summary>Audit fixes: undo history keeps only what an edit changed and is capped; saves verify by manifest; snapshots for
/// autosave on a worker thread.</summary>
[Collection(HistoryTimingCollection.Name)]
public class HistoryAndSaveTests(ITestOutputHelper output)
{
    static double Median(List<double> v) => v.Order().ElementAt(v.Count / 2);

    static string Rename(ProjectSession s, string name) { P8.Ok(s, "project.rename", new { name }); return name; }

    [Fact]
    public void KeySet_OnLongClip_IsFast_StoresOnlyChangedTracks_AndUndoesExactly()
    {
        var (s, _, ces) = P8.Scene();
        var clipId = P8.ApplyTemplate(s, "builtin.walk_cycle", ces, 20); // 600 frames, every bone keyed
        var clip = P8.Clip(s, clipId);
        Assert.True(clip.DurationFrames >= 590);
        var original = P8.Json(clip);
        var fullBytes = ClipDelta.EstimateBytes(clip);
        var bytes0 = s.HistoryBytes;

        // Timing (robust, not weaker): warm up first, then interleave every key.set with a whole-clip JSON snapshot (what each step
        // used to cost, twice) so background load slows both alike. A regression to whole-clip snapshots fails the relative check on
        // any machine; the absolute check takes the best of three batch medians, so one burst of load cannot fail it alone.
        P8.Ok(s, "key.set", new { clipId, characterId = ces, bones = "Skeleton_torso_joint_1", frame = 0 }); // warm-up (counted in history)
        for (var w = 0; w < 3; w++) _ = P8.Json(clip);
        const int batches = 3, perBatch = 15, n = batches * perBatch;
        var keyTimes = new List<double>(); var snapTimes = new List<double>(); var batchMedians = new List<double>();
        for (var b = 0; b < batches; b++)
        {
            var batch = new List<double>();
            for (var j = 1; j <= perBatch; j++)
            {
                var i = b * perBatch + j;
                var sw = Stopwatch.StartNew();
                P8.Ok(s, "key.set", new { clipId, characterId = ces, bones = "Skeleton_torso_joint_1", frame = i * 3 });
                var t = sw.Elapsed.TotalMilliseconds;
                batch.Add(t); keyTimes.Add(t);
                sw.Restart();
                _ = P8.Json(P8.Clip(s, clipId));
                snapTimes.Add(sw.Elapsed.TotalMilliseconds);
            }
            batchMedians.Add(Median(batch));
        }
        var keyMedian = Median(keyTimes); var snapMedian = Median(snapTimes);
        output.WriteLine($"key.set median {keyMedian:0.00} ms (batches {string.Join(", ", batchMedians.Select(m => m.ToString("0.00")))}), whole-clip snapshot median {snapMedian:0.00} ms");
        Assert.True(keyMedian < snapMedian / 2, $"key.set ({keyMedian:0.00} ms) costs more than half a whole-clip snapshot ({snapMedian:0.00} ms): history went back to whole-clip copies");
        Assert.True(batchMedians.Min() < 10, $"key.set took {batchMedians.Min():0.0} ms per command (best batch median; was ~52 ms with whole-clip JSON snapshots)");

        // Memory: each step holds the changed track(s), far less than the whole clip (was 2 whole-clip copies per step).
        var perStep = (s.HistoryBytes - bytes0) / (n + 1.0);
        Assert.True(perStep < fullBytes / 4.0, $"{perStep:N0} bytes per step vs {fullBytes:N0} for the clip");

        var edited = P8.Json(P8.Clip(s, clipId));
        for (var i = 0; i <= n; i++) Assert.True(s.Undo(CommandOrigin.Ui).Ok);
        Assert.Equal(original, P8.Json(P8.Clip(s, clipId)));
        for (var i = 0; i <= n; i++) Assert.True(s.Redo(CommandOrigin.Ui).Ok);
        Assert.Equal(edited, P8.Json(P8.Clip(s, clipId)));
    }

    [Fact]
    public void History_IsCappedBySteps_OldestDropped_DirtyStateStaysRight()
    {
        var s = new ProjectSession(Project.CreateNew("A")) { MaxHistorySteps = 3 };
        for (var i = 1; i <= 5; i++) Rename(s, $"N{i}");
        Assert.Equal(3, s.UndoHistory.Count);
        while (s.CanUndo) Assert.True(s.Undo(CommandOrigin.Ui).Ok);
        Assert.Equal("N2", s.Project.Name); // steps 1–2 can no longer be undone; their result stays
        Assert.True(s.IsDirty); // the saved state (the new project) is no longer reachable
        Assert.True(s.Redo(CommandOrigin.Ui).Ok);
        Assert.Equal("N3", s.Project.Name);

        // Save in the middle, then push past the cap: back at the saved step it is clean, before it dirty.
        s.MarkSaved("x.blazanim");
        Assert.False(s.IsDirty);
        Rename(s, "N6"); Rename(s, "N7");
        Assert.Equal(3, s.UndoHistory.Count);
        s.Undo(CommandOrigin.Ui); s.Undo(CommandOrigin.Ui);
        Assert.Equal("N3", s.Project.Name);
        Assert.False(s.IsDirty);
        Rename(s, "a"); Rename(s, "b"); Rename(s, "c"); // the saved step becomes the bottom: still reachable
        while (s.CanUndo) s.Undo(CommandOrigin.Ui);
        Assert.Equal("N3", s.Project.Name);
        Assert.False(s.IsDirty);
        Rename(s, "a"); Rename(s, "b"); Rename(s, "c"); Rename(s, "d"); // now it falls off
        while (s.CanUndo) s.Undo(CommandOrigin.Ui);
        Assert.Equal("a", s.Project.Name);
        Assert.True(s.IsDirty);
    }

    [Fact]
    public void History_IsCappedByEstimatedMemory_NewestStepAlwaysKept()
    {
        var (s, _, ces) = P8.Scene(cesium: true);
        var clipId = P8.ApplyTemplate(s, "builtin.walk_cycle", ces, 5);
        s.MaxHistoryBytes = 1; // everything is over budget: only the newest step stays
        P8.Ok(s, "key.set", new { clipId, characterId = ces, frame = 1 });
        P8.Ok(s, "key.set", new { clipId, characterId = ces, frame = 2 });
        Assert.Single(s.UndoHistory);
        Assert.True(s.HistoryBytes > 0);
        Assert.True(s.Undo(CommandOrigin.Ui).Ok);
        Assert.False(s.CanUndo);
        Assert.True(s.IsDirty);
    }

    [Fact]
    public async Task Snapshot_IsIsolatedFromLaterEdits_AndWritesOnAWorkerThread()
    {
        using var dir = new TempDir();
        var (s, _, ces) = P8.Scene();
        var clipId = P8.ApplyTemplate(s, "builtin.walk_cycle", ces, 2);
        var before = P8.Json(P8.Clip(s, clipId));
        var snapshot = ProjectArchive.Snapshot(s.Project);
        P8.Ok(s, "key.set", new { clipId, characterId = ces, frame = 3 });
        P8.Ok(s, "clip.rename", new { clipId, name = "Changed" });
        Rename(s, "Renamed");

        var path = dir.File("snap.blazanim");
        await Task.Run(() => ProjectFile.WriteSnapshot(snapshot, path));
        var loaded = ProjectFile.Load(path);
        Assert.Equal("Phase 8", loaded.Name);
        Assert.Equal(before, P8.Json(loaded.GetClip(ObjectId.Parse(clipId))));
    }

    [Fact]
    public void Save_WritesCompactEntries_VerifiesByManifest_AndFullReloadStillAvailable()
    {
        using var dir = new TempDir();
        var path = dir.File("a.blazanim");
        var p = ProjectFileTests.Sample();
        ProjectFile.Save(p, path);
        ProjectFile.Save(p, path, verification: SaveVerification.FullReload);
        ProjectFile.Save(p, path, verification: SaveVerification.None);
        using (var zip = ZipFile.OpenRead(path))
        {
            using var r = new StreamReader(zip.GetEntry($"clips/{p.Clips[0].Id}.json")!.Open());
            Assert.DoesNotContain('\n', r.ReadToEnd());
        }
        Assert.Equal(P8.Json(p.Clips[0]), P8.Json(ProjectFile.Load(path).Clips[0]));

        // A damaged entry fails the integrity check.
        var bad = dir.File("bad.blazanim");
        File.Copy(path, bad);
        using (var zip = ZipFile.Open(bad, ZipArchiveMode.Update))
        {
            var e = zip.GetEntry($"clips/{p.Clips[0].Id}.json")!;
            e.Delete();
            using var w = new StreamWriter(zip.CreateEntry($"clips/{p.Clips[0].Id}.json").Open());
            w.Write("{}");
        }
        using var fs = File.OpenRead(bad);
        Assert.Equal("integrity", Assert.Throws<ProjectFormatException>(() => ProjectArchive.VerifyIntegrity(fs)).Code);
    }

    [Fact]
    public void AutosaveSnapshot_OlderThanLastSave_OrAfterClose_IsSkipped()
    {
        using var dir = new TempDir();
        var store = new RecoveryStore(dir.File("recovery"));
        var project = ProjectFileTests.Sample();
        var session = store.BeginSession(project, null);
        Assert.True(session.AutosaveSnapshot(ProjectArchive.Snapshot(project)));
        var old = ProjectArchive.Snapshot(project);
        Thread.Sleep(20);
        var saved = dir.File("p.blazanim");
        ProjectFile.Save(project, saved);
        session.MarkSaved(saved, project);
        Assert.False(session.AutosaveSnapshot(old));
        Assert.True(session.AutosaveSnapshot(ProjectArchive.Snapshot(project)));
        session.EndCleanly();
        Assert.False(session.AutosaveSnapshot(ProjectArchive.Snapshot(project)));
        Assert.False(Directory.Exists(session.Folder));
    }
}

/// <summary>The history tests measure wall-clock time: they run on their own, after the parallel tests, so CPU-heavy tests running
/// alongside cannot skew them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HistoryTimingCollection
{
    public const string Name = "History timing (serial)";
}
