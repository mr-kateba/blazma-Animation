using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Blazma.Agent;
using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Persistence;
using Blazma.Core.Timing;
using Blazma.Export;
using Blazma.Motion;
using Blazma.Rigging;
using Blazma.Tests.Agent;
using TempDir = Blazma.Tests.Persistence.TempDir;
using H = Blazma.Rigging.HumanoidStandard;

namespace Blazma.Tests.Export;

/// <summary>Regression tests for the Phase 4 export review (H1–H3, M1–M5, L1–L4).</summary>
public class ExportPathSecurityTests
{
    static (ProjectSession s, AgentHost host, string sid, string clip) Host(bool userPresent, AgentMode mode = AgentMode.Assisted)
    {
        var (s, _, clip) = ExportFixture.Walk();
        var host = new AgentHost(ExportFixture.Registry, () => s, userPresent) { Mode = mode };
        return (s, host, host.Connect("t").Id, clip);
    }

    static CommandResult Export(AgentHost host, string sid, object args) => host.Call(sid, "animation.export", ExportFixture.A(args));

    static string[] TempFiles(string dir) => Directory.GetFiles(dir, "*.tmp", SearchOption.TopDirectoryOnly);

    // ---- H1: every path component is resolved ------------------------------------------------------

    [Fact]
    public void H1_IntermediateSymlink_CannotEscapeTheWriteFolder()
    {
        if (OperatingSystem.IsWindows()) return;
        var (_, host, sid, clip) = Host(userPresent: false);
        using var allowed = new TempDir();
        using var outside = new TempDir();
        Directory.CreateDirectory(Path.Combine(outside.Path, "sub"));
        Directory.CreateSymbolicLink(Path.Combine(allowed.Path, "out"), outside.Path);
        host.AllowedWriteRoots.Add(allowed.Path);
        var r = Export(host, sid, new { clipId = clip, path = Path.Combine(allowed.Path, "out", "sub", "walk.glb") });
        Assert.Equal("path_not_allowed", r.Error!.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(outside.Path, "sub")));
    }

    [Fact]
    public void H1_Canonicalize_ResolvesEveryComponent_RelativeAndChainedLinks_AndLoops()
    {
        if (OperatingSystem.IsWindows()) return;
        using var d = new TempDir();
        var real = Directory.CreateDirectory(Path.Combine(d.Path, "real", "deep")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(d.Path, "a"), "real");            // relative target
        Directory.CreateSymbolicLink(Path.Combine(d.Path, "b"), Path.Combine(d.Path, "a")); // chain b → a → real
        var root = FilePaths.Canonicalize(d.Path);
        Assert.Equal(Path.Combine(root, "real", "deep", "x.glb"), FilePaths.Canonicalize(Path.Combine(d.Path, "b", "deep", "x.glb")));
        Assert.Equal(Path.Combine(root, "real", "missing", "y"), FilePaths.Canonicalize(Path.Combine(d.Path, "a", "missing", "y")));
        Assert.Equal(FilePaths.Canonicalize(real), Path.Combine(root, "real", "deep"));
        File.CreateSymbolicLink(Path.Combine(d.Path, "loop1"), Path.Combine(d.Path, "loop2"));
        File.CreateSymbolicLink(Path.Combine(d.Path, "loop2"), Path.Combine(d.Path, "loop1"));
        Assert.Throws<IOException>(() => FilePaths.Canonicalize(Path.Combine(d.Path, "loop1", "x")));
    }

    [Fact]
    public void H1_TheCommandWritesToTheCanonicalPathThatWasChecked()
    {
        if (OperatingSystem.IsWindows()) return;
        var (_, host, sid, clip) = Host(userPresent: false);
        using var allowed = new TempDir();
        var real = Directory.CreateDirectory(Path.Combine(allowed.Path, "real")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(allowed.Path, "out"), real);
        host.AllowedWriteRoots.Add(allowed.Path);
        var r = Export(host, sid, new { clipId = clip, path = Path.Combine(allowed.Path, "out", "walk.glb") });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Equal(Path.Combine(FilePaths.Canonicalize(real), "walk.glb"), (string?)r.Data!["path"]);
        Assert.True(File.Exists(Path.Combine(real, "walk.glb")));
    }

    [Fact]
    public void H1_ApprovedExport_IsRechecked_WhenTheFolderWasSwappedForALink()
    {
        if (OperatingSystem.IsWindows()) return;
        var (_, host, sid, clip) = Host(userPresent: true);
        using var dir = new TempDir();
        using var elsewhere = new TempDir();
        var outDir = Directory.CreateDirectory(Path.Combine(dir.Path, "out")).FullName;
        var r = Export(host, sid, new { clipId = clip, path = Path.Combine(outDir, "walk.glb") });
        Assert.Equal("approval_required", r.Error!.Code);
        var pending = Assert.Single(host.PendingApprovals);
        Assert.Equal(Path.Combine(FilePaths.Canonicalize(outDir), "walk.glb"), (string?)pending.Args["path"]);

        // Before the user clicks Approve, the folder becomes a link to somewhere else.
        Directory.Delete(outDir);
        Directory.CreateSymbolicLink(outDir, elsewhere.Path);
        var approved = host.ApprovePending(pending.Id);
        Assert.False(approved.Ok);
        Assert.Equal("path_changed", approved.Error!.Code);
        Assert.Empty(Directory.GetFiles(elsewhere.Path));
    }

    [Fact]
    public void H1_ApprovedExport_IsRechecked_WhenTheFileAppearedMeanwhile()
    {
        var (_, host, sid, clip) = Host(userPresent: true);
        using var dir = new TempDir();
        var path = dir.File("walk.glb");
        Assert.Equal("approval_required", Export(host, sid, new { clipId = clip, path }).Error!.Code);
        File.WriteAllText(path, "user");
        var approved = host.ApprovePending(Assert.Single(host.PendingApprovals).Id);
        Assert.Equal("file_exists", approved.Error!.Code);
        Assert.Equal("user", File.ReadAllText(path));
    }

    [Fact]
    public void H1_Reads_KeepTheGivenPath_SoALinkedModelKeepsItsName()
    {
        if (OperatingSystem.IsWindows()) return;
        var (s, _, _) = ExportFixture.Walk();
        var host = new AgentHost(ExportFixture.Registry, () => s, userPresent: false);
        var sid = host.Connect("t").Id;
        using var dir = new TempDir();
        var blob = Directory.CreateDirectory(Path.Combine(dir.Path, "blobs")).FullName;
        File.Copy(Blazma.Tests.Rigging.Import.RepoPaths.Sample("models/khronos/RiggedSimple.glb"), Path.Combine(blob, "3f9a1c"));
        var link = Path.Combine(dir.Path, "model.glb");
        File.CreateSymbolicLink(link, Path.Combine(blob, "3f9a1c"));
        host.AllowedReadRoots.Add(dir.Path);
        var r = host.Call(sid, "rig.import", ExportFixture.A(new { path = link }));
        Assert.True(r.Ok, r.Error?.Message);
    }

    // ---- H2: temporary files are new, random and exclusive -------------------------------------------

    [Fact]
    public void H2_PlantedTmpSymlink_IsNotFollowed()
    {
        if (OperatingSystem.IsWindows()) return;
        var (_, host, sid, clip) = Host(userPresent: false);
        using var allowed = new TempDir();
        using var outside = new TempDir();
        var victim = Path.Combine(outside.Path, "victim.txt");
        File.WriteAllText(victim, "precious");
        File.CreateSymbolicLink(Path.Combine(allowed.Path, "walk.glb.tmp"), victim);
        File.CreateSymbolicLink(Path.Combine(allowed.Path, "walk.blazma.json.tmp"), victim);
        host.AllowedWriteRoots.Add(allowed.Path);
        var r = Export(host, sid, new { clipId = clip, path = Path.Combine(allowed.Path, "walk.glb") });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Equal("precious", File.ReadAllText(victim));
        Assert.Equal(victim, new FileInfo(Path.Combine(allowed.Path, "walk.glb.tmp")).LinkTarget);
    }

    [Fact]
    public void H2_ExistingTmpFiles_AreLeftAlone_AndNoTemporaryFileRemains()
    {
        var (_, host, sid, clip) = Host(userPresent: false);
        using var allowed = new TempDir();
        File.WriteAllText(allowed.File("walk.blazma.json.tmp"), "user data");
        File.WriteAllText(allowed.File("walk.glb.tmp"), "more user data");
        host.AllowedWriteRoots.Add(allowed.Path);
        Assert.True(Export(host, sid, new { clipId = clip, path = allowed.File("walk.glb") }).Ok);
        Assert.Equal("user data", File.ReadAllText(allowed.File("walk.blazma.json.tmp")));
        Assert.Equal("more user data", File.ReadAllText(allowed.File("walk.glb.tmp")));
        Assert.Equal(2, TempFiles(allowed.Path).Length);
    }

    [Fact]
    public void H2_ProjectSave_KeepsBackups_ButDoesNotFollowAPlantedBakLink()
    {
        using var dir = new TempDir();
        var path = dir.File("p.blazanim");
        var p = Blazma.Tests.Persistence.ProjectFileTests.Sample();
        ProjectFile.Save(p, path);
        p.Name = "Second";
        ProjectFile.Save(p, path);
        Assert.Equal("Demo — عرض", ProjectFile.Load(path + ".bak").Name);
        Assert.Empty(TempFiles(dir.Path));
        if (OperatingSystem.IsWindows()) return;

        using var outside = new TempDir();
        var victim = Path.Combine(outside.Path, "victim.txt");
        File.WriteAllText(victim, "precious");
        File.Delete(path + ".bak");
        File.CreateSymbolicLink(path + ".bak", victim);
        p.Name = "Third";
        ProjectFile.Save(p, path);
        Assert.Equal("precious", File.ReadAllText(victim));
        Assert.Null(new FileInfo(path + ".bak").LinkTarget);
        Assert.Equal("Second", ProjectFile.Load(path + ".bak").Name);
        Assert.Equal("Third", ProjectFile.Load(path).Name);
        Assert.Empty(TempFiles(dir.Path));
    }

    // ---- M1: no-overwrite writes fail atomically when the file appears meanwhile -----------------------

    [Fact]
    public void M1_AtomicFile_NoOverwrite_RefusesAFileCreatedWhileWriting()
    {
        using var dir = new TempDir();
        var path = dir.File("a.glb");
        var e = Assert.Throws<FileAlreadyExistsException>(() =>
            AtomicFile.Write(path, s => { File.WriteAllText(path, "user"); s.WriteByte(1); }, keepBackup: false, overwrite: false));
        Assert.Equal(path, e.Path);
        Assert.Equal("user", File.ReadAllText(path));
        Assert.Empty(TempFiles(dir.Path));

        // Without the race it creates the file; the default (overwrite) still replaces.
        var other = dir.File("b.glb");
        AtomicFile.Write(other, s => s.WriteByte(7), keepBackup: false, overwrite: false);
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(other));
        Assert.Throws<FileAlreadyExistsException>(() => AtomicFile.Write(other, s => s.WriteByte(8), keepBackup: false, overwrite: false));
        AtomicFile.Write(other, s => s.WriteByte(9), keepBackup: false);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(other));
    }

    [Fact]
    public void M1_FileCreatedDuringTheExport_IsNotClobbered()
    {
        var (s, host, sid, _) = Host(userPresent: false);
        var bones = s.Project.Rigs[0].Bones.Count;
        var frames = (int)Math.Min(60_000, AnimationBaker.MaxSamples / bones - 1);
        var clip = (string)ExportFixture.Run(s, "clip.create", new { name = "Long", durationFrames = frames }).Data!["clipId"]!;
        using var allowed = new TempDir();
        host.AllowedWriteRoots.Add(allowed.Path);
        var path = allowed.File("walk.glb");
        var t = new Thread(() => { Thread.Sleep(50); File.WriteAllText(path, "user file created during export"); });
        t.Start();
        var r = Export(host, sid, new { clipId = clip, path });
        t.Join();
        // Either the export saw the file and refused, or it finished first and the user's write came after:
        // the user's file is never replaced by the export.
        Assert.Equal("user file created during export", File.ReadAllText(path));
        if (!r.Ok) Assert.Equal("file_exists", r.Error!.Code);
        Assert.Empty(TempFiles(allowed.Path));
    }

    // ---- M2: read folders do not grant write -------------------------------------------------------------

    [Theory]
    [InlineData(false, "path_not_allowed")]
    [InlineData(true, "approval_required")]
    public void M2_ReadFolder_DoesNotGrantWrite(bool userPresent, string code)
    {
        var (_, host, sid, clip) = Host(userPresent);
        using var dir = new TempDir();
        host.AllowedReadRoots.Add(dir.Path);
        Assert.Equal(code, Export(host, sid, new { clipId = clip, path = dir.File("walk.glb") }).Error!.Code);
        Assert.False(File.Exists(dir.File("walk.glb")));
    }

    [Fact]
    public void M2_WriteFolder_IsAlsoReadable_AndTheProjectFolderIsWritable()
    {
        var (s, host, sid, clip) = Host(userPresent: false);
        using var write = new TempDir();
        host.AllowedWriteRoots.Add(write.Path);
        var path = write.File("walk.glb");
        Assert.True(Export(host, sid, new { clipId = clip, path }).Ok);
        Assert.True(host.Call(sid, "animation.validate", ExportFixture.A(new { clipId = clip, path })).Ok);

        using var project = new TempDir();
        host.ProjectFolder = () => project.Path;
        Assert.True(Export(host, sid, new { clipId = clip, path = project.File("p.glb") }).Ok);
        Assert.True(host.IsUnderWriteRoot(Path.Combine(FilePaths.Canonicalize(project.Path), "x.glb")));
    }

    [Fact]
    public async Task M2_HeadlessCli_AllowRead_DoesNotGrantWrite_AllowWriteDoes()
    {
        var baseDir = Tools.TempDir();
        try
        {
            string D(string name) => Directory.CreateDirectory(Path.Combine(baseDir, name)).FullName;
            var projectDir = D("project");
            var cwd = D("cwd");
            var readDir = D("read");
            var writeDir = D("write");
            var envWriteDir = D("envwrite");
            var file = Path.Combine(projectDir, "p.blazanim");
            Assert.Equal(0, (await Tools.RunCli("new", file)).Exit);
            var created = await Tools.RunCli("exec", file, "clip.create", "--arg", "name=Walk", "--arg", "durationFrames=6", "--json");
            var clipId = (string)JsonNode.Parse(created.Out)!["data"]!["clipId"]!;

            async Task<(int Exit, string Out, string Err)> Export(string target, params string[] extra)
            {
                var psi = Tools.Start(Tools.Cli, new[] { "exec", file, "animation.export", "--arg", $"clipId={clipId}", "--arg", $"path={target}", "--json" }.Concat(extra), false);
                psi.WorkingDirectory = cwd;
                psi.Environment["BLAZMA_ALLOW_READ"] = readDir;
                psi.Environment["BLAZMA_ALLOW_WRITE"] = envWriteDir;
                using var p = Process.Start(psi)!;
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await p.WaitForExitAsync(cts.Token);
                return (p.ExitCode, await o, await e);
            }

            var denied = await Export(Path.Combine(readDir, "a.glb"), "--allow-read", readDir);
            Assert.Equal(1, denied.Exit);
            Assert.Contains("path_not_allowed", denied.Out + denied.Err);
            Assert.False(File.Exists(Path.Combine(readDir, "a.glb")));

            Assert.Equal(0, (await Export(Path.Combine(writeDir, "b.glb"), "--allow-write", writeDir)).Exit);
            Assert.True(File.Exists(Path.Combine(writeDir, "b.glb")));
            Assert.Equal(1, (await Export(Path.Combine(writeDir, "c.glb"))).Exit); // not listed this time
            Assert.Equal(0, (await Export(Path.Combine(envWriteDir, "d.glb"))).Exit);  // BLAZMA_ALLOW_WRITE
            Assert.Equal(0, (await Export(Path.Combine(projectDir, "e.glb"))).Exit);   // the project folder
            Assert.Equal(0, (await Export(Path.Combine(cwd, "f.glb"))).Exit);          // the start folder
        }
        finally { try { Directory.Delete(baseDir, true); } catch (IOException) { } }
    }

    // ---- L2: file names ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("walk.glb:stream.glb")]
    [InlineData("wa?lk.glb")]
    [InlineData("walk.glb.")]
    [InlineData("wa\u0001lk.glb")]
    public void L2_InvalidFileNames_AreRejected_ForAgentsAndTheUi(string name)
    {
        var (s, host, sid, clip) = Host(userPresent: false);
        using var dir = new TempDir();
        host.AllowedWriteRoots.Add(dir.Path);
        Assert.Equal("invalid_path", Export(host, sid, new { clipId = clip, path = Path.Combine(dir.Path, name) }).Error!.Code);
        Assert.Equal("invalid_path", ExportFixture.Run(s, "animation.export", new { clipId = clip, path = Path.Combine(dir.Path, name) }, CommandOrigin.Ui).Error!.Code);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
        Assert.True(FilePaths.IsPortableFileName("walk-2 (final).glb"));
    }

    // ---- L3: a failed sidecar does not leave half an export --------------------------------------------

    [Fact]
    public void L3_SidecarWriteFailure_RemovesTheGlbItCreated()
    {
        if (!OperatingSystem.IsLinux()) return; // relies on the 255-byte file name limit
        var (s, _, clip) = ExportFixture.Walk();
        using var dir = new TempDir();
        // The .glb name fits (250 bytes); "<name>.blazma.json" (258 bytes) does not.
        var path = dir.File(new string('a', 246) + ".glb");
        var r = ExportFixture.Run(s, "animation.export", new { clipId = clip, path });
        Assert.False(r.Ok);
        Assert.Equal("io_error", r.Error!.Code);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));

        // A GLB that existed before (overwrite) is not deleted.
        File.WriteAllText(path, "old");
        r = ExportFixture.Run(s, "animation.export", new { clipId = clip, path, overwrite = true }, CommandOrigin.Ui);
        Assert.False(r.Ok);
        Assert.True(File.Exists(path));
    }
}

public class ExportRootMotionLoopTests
{
    [Fact]
    public void H3_LoopingWalk_KeepsTravelling_InEveryRootMotionMode()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        var c = ExportFixture.ClipOf(s, clip);
        c.Loop.Enabled = true;
        var rig = ExportFixture.RigOf(s, ch);
        foreach (var mode in new[] { RootMotionMode.Keep, RootMotionMode.InPlace, RootMotionMode.InPlaceAll })
        {
            var b = AnimationBaker.Bake(c, rig, new ExportOptions { RootMotion = mode });
            var ri = b.RootMotion.BoneIndex;
            Assert.Equal(26, b.FrameCount); // full-clip loop: the whole clip, the last frame is the wrapped loop start
            Assert.Equal(0.6, b.RootMotion.Total.Z, 9);
            Assert.All(b.RootMotion.Deltas, d => Assert.True(d.Z >= -1e-9, $"{mode}: root motion jumps back ({d})"));
            if (mode == RootMotionMode.Keep)
                Assert.Equal(0.6, b.Locals[^1][ri].Translation.Z - b.Locals[0][ri].Translation.Z, 9);
            else
                Assert.All(b.Locals, f => Assert.Equal(0, f[ri].Translation.Z, 9));

            var result = AnimationExporter.Export(s.Project, c, rig, new ExportOptions { RootMotion = mode });
            var report = ExportValidator.Validate(result.Glb, s.Project, c, rig);
            Assert.True(report.Ok, mode + ": " + string.Join("\n", report.Issues));
            Assert.Equal(26, report.WorldFramesChecked);
            Assert.Equal(0.6, (double)result.Sidecar["rootMotion"]!["totalDisplacement"]![2]!, 6);
        }
    }

    [Fact]
    public void H3_LoopSubRange_IsTheDefaultExportRange_AndTheFileLoopsCleanly()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        Assert.True(ExportFixture.Run(s, "clip.set_loop", new { clipId = clip, enabled = true, startFrame = 6, endFrame = 18 }).Ok);
        var c = ExportFixture.ClipOf(s, clip);
        var rig = ExportFixture.RigOf(s, ch);
        Assert.Equal((6L, 18L), AnimationBaker.DefaultRange(c));

        var ev = new RigEvaluator(rig);
        var rootIndex = AnimationBaker.RootMotionBone(rig);
        Vec3 RootAt(long f) => ev.Model(ClipEvaluator.Evaluate(c, rig, Flicks.FromFrame(f, c.Fps)))[rootIndex].Translation;
        var cycle = RootAt(18) - RootAt(6);
        Assert.True(cycle.Z > 0.2);

        var keep = AnimationBaker.Bake(c, rig);
        Assert.Equal(6, keep.StartFrame);
        Assert.Equal(18, keep.EndFrame);
        Assert.True(keep.RootMotion.CycleDisplacement.ApproxEquals(cycle, 1e-9));
        Assert.True(keep.RootMotion.Total.ApproxEquals(new Vec3(cycle.X, 0, cycle.Z), 1e-9));
        for (var i = 0; i < rig.Bones.Count; i++)
            if (i != rootIndex) Assert.Equal(keep.Locals[0][i], keep.Locals[^1][i]);
        Assert.True((keep.Locals[^1][rootIndex].Translation - keep.Locals[0][rootIndex].Translation).ApproxEquals(cycle, 1e-9));

        // In place, the last frame is exactly the first: the file loops without a pop.
        var inPlace = AnimationBaker.Bake(c, rig, new ExportOptions { RootMotion = RootMotionMode.InPlace });
        for (var i = 0; i < rig.Bones.Count; i++)
            Assert.True(inPlace.Locals[0][i].Translation.ApproxEquals(inPlace.Locals[^1][i].Translation, 1e-9)
                && Quat.AngleBetween(inPlace.Locals[0][i].Rotation, inPlace.Locals[^1][i].Rotation) < 1e-9, rig.Bones[i].Name);

        var result = AnimationExporter.Export(s.Project, c, rig, new ExportOptions { RootMotion = RootMotionMode.InPlace });
        Assert.Equal(13, (int)result.Sidecar["frameCount"]!);
        Assert.Equal(6, (long)result.Sidecar["startFrame"]!);
        Assert.Equal(0, (long)result.Sidecar["loop"]!["startFrame"]!);
        Assert.Equal(12, (long)result.Sidecar["loop"]!["endFrame"]!);
        Assert.False((bool)result.Sidecar["loop"]!["clampedToExport"]!);
        var report = ExportValidator.Validate(result.Glb, s.Project, c, rig);
        Assert.True(report.Ok, string.Join("\n", report.Issues));

        // An explicit range still wins.
        Assert.Equal(26, AnimationBaker.Bake(c, rig, new ExportOptions { StartFrame = 0, EndFrame = 25 }).FrameCount);
        Assert.Equal(25, AnimationBaker.Bake(c, rig, new ExportOptions { StartFrame = 20 }).EndFrame);
    }

    [Fact]
    public void L4_SidecarLoopRange_IsClampedToTheExport_OrOmitted()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        Assert.True(ExportFixture.Run(s, "clip.set_loop", new { clipId = clip, enabled = true, startFrame = 0, endFrame = 24 }).Ok);
        var c = ExportFixture.ClipOf(s, clip);
        var rig = ExportFixture.RigOf(s, ch);
        var loop = AnimationExporter.Export(s.Project, c, rig, new ExportOptions { StartFrame = 6, EndFrame = 18 }).Sidecar["loop"]!;
        Assert.Equal(0, (long)loop["startFrame"]!);
        Assert.Equal(12, (long)loop["endFrame"]!);
        Assert.True((bool)loop["clampedToExport"]!);

        Assert.True(ExportFixture.Run(s, "clip.set_loop", new { clipId = clip, enabled = true, startFrame = 0, endFrame = 6 }).Ok);
        c = ExportFixture.ClipOf(s, clip);
        loop = AnimationExporter.Export(s.Project, c, rig, new ExportOptions { StartFrame = 12, EndFrame = 20 }).Sidecar["loop"]!;
        Assert.Null(loop["startFrame"]);
        Assert.Null(loop["endFrame"]);
    }

    /// <summary>A two-bone rig whose root-motion bone hangs under a parent turned 90° about X: its parent-space
    /// Z travel is vertical in model space.</summary>
    static (Project p, Rig rig, Clip clip) TiltedRig()
    {
        var p = Project.CreateNew("Tilted");
        var rig = new Rig { Name = "Tilted" };
        var basis = new Bone { Name = "basis", Rest = new Transform(Vec3.Zero, Quat.FromAxisAngle(Vec3.UnitX, Math.PI / 2), Vec3.One) };
        var mover = new Bone { Name = "mover", Parent = basis.Id, Rest = Transform.Identity };
        rig.Bones.AddRange(new[] { basis, mover });
        rig.HumanoidMap.Add(new BoneMapping { Bone = mover.Id, Standard = H.Root });
        p.Rigs.Add(rig);
        var clip = new Clip { Name = "Rise", Fps = FrameRate.Fps30, Duration = Flicks.FromFrame(10, FrameRate.Fps30), Skeleton = rig.Id };
        clip.Tracks.Add(new Track
        {
            Target = new TrackTarget { Kind = TargetKind.Bone, Id = mover.Id },
            Channel = TrackChannels.Translation,
            Keys =
            {
                new Keyframe { Time = Flicks.Zero, Value = new double[] { 0.5, 0, 0 }, Interp = Interpolation.Linear },
                new Keyframe { Time = Flicks.FromFrame(10, FrameRate.Fps30), Value = new double[] { 0.5, 0, 1 }, Interp = Interpolation.Linear },
            },
        });
        p.Clips.Add(clip);
        return (p, rig, clip);
    }

    [Fact]
    public void L1_RootMotion_IsExtractedInModelSpace()
    {
        var (p, rig, clip) = TiltedRig();
        var ev = new RigEvaluator(rig);
        Vec3 ModelRoot(BakedClip b, int f) => ev.Model(b.Locals[f])[1].Translation;

        // Parent-space +Z is model-space −Y (vertical): nothing horizontal to remove for in_place.
        var inPlace = AnimationBaker.Bake(clip, rig, new ExportOptions { RootMotion = RootMotionMode.InPlace });
        Assert.Equal("mover", inPlace.RootMotion.BoneName);
        Assert.True(inPlace.RootMotion.Total.ApproxEquals(Vec3.Zero, 1e-9), inPlace.RootMotion.Total.ToString());
        Assert.Equal(-1, ModelRoot(inPlace, 10).Y - ModelRoot(inPlace, 0).Y, 9);

        // in_place_all removes it, in model space, keeping the bone under its parent.
        var all = AnimationBaker.Bake(clip, rig, new ExportOptions { RootMotion = RootMotionMode.InPlaceAll });
        Assert.True(all.RootMotion.Total.ApproxEquals(new Vec3(0, -1, 0), 1e-9), all.RootMotion.Total.ToString());
        for (var f = 0; f < all.FrameCount; f++)
            Assert.True(ModelRoot(all, f).ApproxEquals(ModelRoot(all, 0), 1e-9));

        foreach (var mode in new[] { RootMotionMode.Keep, RootMotionMode.InPlace, RootMotionMode.InPlaceAll })
        {
            var options = new ExportOptions { RootMotion = mode };
            var report = ExportValidator.Validate(AnimationExporter.Export(p, clip, rig, options).Glb, p, clip, rig, options);
            Assert.True(report.Ok, mode + ": " + string.Join("\n", report.Issues));
        }
    }
}

public class ExportValidatorIndependenceTests
{
    static void SetFloats(GlbContainer glb, int accessor, int index, float[] values)
    {
        var acc = glb.Json["accessors"]![accessor]!;
        var view = glb.Json["bufferViews"]![(int)acc["bufferView"]!]!;
        var offset = (int)(view["byteOffset"] ?? 0) + (int)(acc["byteOffset"] ?? 0) + index * values.Length * 4;
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(glb.Bin.AsSpan(offset + i * 4), values[i]);
    }

    [Fact]
    public void M4_TamperedRotationInTheFile_IsCaughtByTheWorldCheck()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        var rig = ExportFixture.RigOf(s, ch);
        var c = ExportFixture.ClipOf(s, clip);
        var glb = GlbContainer.Parse(AnimationExporter.Export(s.Project, c, rig).Glb);
        var chest = rig.IndexOf(rig.FindBoneByName(H.Chest)!.Id);
        var anim = glb.Json["animations"]![0]!;
        var channel = anim["channels"]!.AsArray().First(x => (int)x!["target"]!["node"]! == chest && (string?)x["target"]!["path"] == "rotation")!;
        var output = (int)anim["samplers"]![(int)channel["sampler"]!]!["output"]!;
        var q = Quat.FromAxisAngle(Vec3.UnitY, 0.5);
        var count = (int)glb.Json["accessors"]![output]!["count"]!;
        for (var k = 0; k < count; k++) SetFloats(glb, output, k, new[] { (float)q.X, (float)q.Y, (float)q.Z, (float)q.W });

        var report = ExportValidator.Validate(GlbContainer.Write(glb.Json, glb.Bin), s.Project, c, rig);
        Assert.False(report.Ok);
        Assert.Contains(report.Issues, i => i.StartsWith("World check", StringComparison.Ordinal));
        Assert.True(report.MaxWorldRotationErrorDegrees > 1);
    }

    [Fact]
    public void M4_CorruptedNonBoneAncestor_OfAnImportedCharacter_IsOnlyVisibleToTheWorldCheck()
    {
        var (s, ch, clip) = ExportFixture.Imported("CesiumMan");
        var rig = ExportFixture.RigOf(s, ch);
        var c = ExportFixture.ClipOf(s, clip);
        var result = AnimationExporter.Export(s.Project, c, rig);
        Assert.True(ExportValidator.Validate(result.Glb, s.Project, c, rig).Ok);

        var glb = GlbContainer.Parse(result.Glb);
        var nodes = GltfAnimationReader.ReadNodes(glb.Json);
        var rootNode = rig.Source.NodeIndex[rig.Bones[0].Id.ToString()];
        var boneNodes = rig.Source.NodeIndex.Values.ToHashSet();
        var ancestor = nodes[rootNode].Parent is int pa && !boneNodes.Contains(pa) ? pa : throw new InvalidOperationException("CesiumMan's root bone has no non-bone parent");
        var node = glb.Json["nodes"]![ancestor]!.AsObject();
        var l = nodes[ancestor].Local;
        node.Remove("matrix");
        node["translation"] = new JsonArray(l.Translation.X, l.Translation.Y, l.Translation.Z);
        var q = (Quat.FromAxisAngle(Vec3.UnitZ, 0.3) * l.Rotation).Normalized();
        node["rotation"] = new JsonArray(q.X, q.Y, q.Z, q.W);
        node["scale"] = new JsonArray(l.Scale.X, l.Scale.Y, l.Scale.Z);

        // The bone → node binding (and so the per-channel check) is unaffected; the joints are not where the app has them.
        var report = ExportValidator.Validate(GlbContainer.Write(glb.Json, glb.Bin), s.Project, c, rig);
        Assert.False(report.Ok);
        Assert.All(report.Issues, i => Assert.StartsWith("World check", i));
    }

    [Fact]
    public void M3_PostExportValidation_ReusesTheGivenBake()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        var rig = ExportFixture.RigOf(s, ch);
        var c = ExportFixture.ClipOf(s, clip);
        var result = AnimationExporter.Export(s.Project, c, rig);
        // A deliberately wrong "bake" is used as is for the per-channel check (no second bake), while the
        // independent world check still finds the file correct.
        var head = rig.IndexOf(rig.FindBoneByName(H.Head)!.Id);
        foreach (var f in result.Baked.Locals)
            f[head] = f[head] with { Rotation = (Quat.FromAxisAngle(Vec3.UnitX, 0.4) * f[head].Rotation).Normalized() };
        var report = ExportValidator.Validate(result.Glb, s.Project, c, rig, expected: result.Baked);
        Assert.Contains(report.Issues, i => i.Contains($"Bone '{H.Head}' rotation"));
        Assert.DoesNotContain(report.Issues, i => i.StartsWith("World check", StringComparison.Ordinal));
    }
}

public class ExportLimitsAndMalformedFileTests
{
    [Fact]
    public void M3_TooManyBoneSamples_IsRefused_ForEveryCaller()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        var rig = ExportFixture.RigOf(s, ch);
        var c = ExportFixture.ClipOf(s, clip);
        var frames = AnimationBaker.MaxSamples / rig.Bones.Count + 1;
        Assert.True(frames < AnimationBaker.MaxFrames);
        c.Duration = Flicks.FromFrame(frames, c.Fps);
        var e = Assert.Throws<ExportException>(() => AnimationBaker.Bake(c, rig));
        Assert.Equal("too_large", e.Code);
        Assert.Contains("bone samples", e.Message);
        using var dir = new TempDir();
        var r = ExportFixture.Run(s, "animation.export", new { clipId = clip, path = dir.File("long.glb") }, CommandOrigin.Ui);
        Assert.Equal("too_large", r.Error!.Code);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public void M3_ValidationCanBeSkipped()
    {
        var (s, _, clip) = ExportFixture.Walk();
        using var dir = new TempDir();
        var r = ExportFixture.Run(s, "animation.export", new { clipId = clip, path = dir.File("w.glb"), validate = false });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.Null(r.Data!["validation"]);
        r = ExportFixture.Run(s, "animation.export", new { clipId = clip, path = dir.File("v.glb") });
        Assert.True((bool)r.Data!["validation"]!["ok"]!);
        Assert.True((int)r.Data["validation"]!["worldFramesChecked"]! > 0);
    }

    static (ProjectSession s, Clip c, Rig rig, byte[] glb, int rotationOutput) Exported()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        var c = ExportFixture.ClipOf(s, clip);
        var rig = ExportFixture.RigOf(s, ch);
        var glb = AnimationExporter.Export(s.Project, c, rig).Glb;
        var json = GlbContainer.Parse(glb).Json;
        var anim = json["animations"]![0]!;
        var ch1 = anim["channels"]!.AsArray().First(x => (string?)x!["target"]!["path"] == "rotation")!;
        return (s, c, rig, glb, (int)anim["samplers"]![(int)ch1["sampler"]!]!["output"]!);
    }

    static ExportValidationReport ValidateEdited(Action<JsonObject> edit)
    {
        var (s, c, rig, bytes, _) = Exported();
        var glb = GlbContainer.Parse(bytes);
        edit(glb.Json);
        return ExportValidator.Validate(GlbContainer.Write(glb.Json, glb.Bin), s.Project, c, rig);
    }

    [Fact]
    public void M5_RotationOutputDeclaredVec3_IsAnIssue_NotACrash()
    {
        var output = Exported().rotationOutput;
        var report = ValidateEdited(j => j["accessors"]![output]!["type"] = "VEC3");
        Assert.False(report.Ok);
        Assert.Contains(report.Issues, i => i.Contains("needs 4"));
    }

    [Fact]
    public void M5_NullInNodeTranslation_IsAnIssue_NotACrash()
    {
        var report = ValidateEdited(j => j["nodes"]![0]!["translation"] = new JsonArray(null, 0, 0));
        Assert.False(report.Ok);
        Assert.Contains(report.Issues, i => i.Contains("nodes[0].translation"));
        report = ValidateEdited(j => j["nodes"]![0]!["rotation"] = new JsonArray("x", 0, 0, 1));
        Assert.Contains(report.Issues, i => i.Contains("nodes[0].rotation"));
        report = ValidateEdited(j => j["nodes"]![0]!["matrix"] = new JsonArray(1, 2));
        Assert.False(report.Ok);
    }

    [Fact]
    public void M5_HugeAccessorCount_IsRejected_BeforeAllocating()
    {
        var output = Exported().rotationOutput;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var report = ValidateEdited(j => j["accessors"]![output]!["count"] = 600_000_000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.False(report.Ok);
        Assert.Contains(report.Issues, i => i.Contains("past the end"));
        Assert.True(allocated < 200_000_000, $"allocated {allocated / 1e6:0} MB");

        // No buffer view and a huge count: refused too (would be all zeros).
        report = ValidateEdited(j => { j["accessors"]![output]!.AsObject().Remove("bufferView"); j["accessors"]![output]!["count"] = 600_000_000; });
        Assert.False(report.Ok);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 400_000_000);
    }

    [Fact]
    public void M5_OutputShorterThanTheKeys_AndGarbage_AreIssues()
    {
        var output = Exported().rotationOutput;
        var report = ValidateEdited(j => j["accessors"]![output]!["count"] = 1);
        Assert.False(report.Ok);
        report = ValidateEdited(j => j["animations"]![0]!["samplers"]![0]!["interpolation"] = 5);
        Assert.False(report.Ok);
        report = ValidateEdited(j => j["nodes"] = new JsonObject());
        Assert.False(report.Ok);
        var (s, c, rig, _, _) = Exported();
        Assert.False(ExportValidator.Validate(new byte[] { 1, 2, 3 }, s.Project, c, rig).Ok);
    }
}

public class ExportTimingTests
{
    [Fact]
    public void SingleFrame_Export_Validates()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        var c = ExportFixture.ClipOf(s, clip);
        var rig = ExportFixture.RigOf(s, ch);
        var result = AnimationExporter.Export(s.Project, c, rig, new ExportOptions { StartFrame = 24, EndFrame = 24, RootMotion = RootMotionMode.InPlace });
        Assert.Equal(1, result.Baked.FrameCount);
        var report = ExportValidator.Validate(result.Glb, s.Project, c, rig);
        Assert.True(report.Ok, string.Join("\n", report.Issues));
        Assert.Equal(0, report.DurationSeconds);
        Assert.Equal(1, report.WorldFramesChecked);
    }

    [Fact]
    public void FractionalFps_30000_1001_RoundTrips()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        var c = ExportFixture.ClipOf(s, clip);
        var rig = ExportFixture.RigOf(s, ch);
        c.Fps = FrameRate.Ntsc2997;
        using var dir = new TempDir();
        var r = ExportFixture.Run(s, "animation.export", new { clipId = clip, path = dir.File("ntsc.glb") });
        Assert.True(r.Ok, r.Error?.Message);
        Assert.True((bool)r.Data!["validation"]!["ok"]!, r.Data["validation"]!.ToJsonString());
        var frames = (int)r.Data["frames"]!;
        Assert.Equal(c.DurationFrames + 1, frames);
        Assert.Equal((frames - 1) * 1001 / 30000.0, (double)r.Data["durationSeconds"]!, 9);

        var glb = GlbContainer.Parse(File.ReadAllBytes(dir.File("ntsc.glb")));
        var anim = GltfAnimationReader.ReadAnimations(glb).Single();
        var root = anim.Channels.Single(x => x.Node == 0 && x.Path == "translation");
        Assert.Equal(frames, root.Times.Length);
        for (var i = 0; i < frames; i++) Assert.Equal(i * 1001 / 30000.0, root.Times[i], 5);
        var sidecar = JsonNode.Parse(File.ReadAllText(dir.File("ntsc.blazma.json")))!;
        Assert.Equal(30000, (int)sidecar["fps"]!["numerator"]!);
        Assert.Equal(1001, (int)sidecar["fps"]!["denominator"]!);
        Assert.Equal("30000/1001", (string?)glb.Json["animations"]![0]!["extras"]!["blazma"]!["fps"]);
    }

    [Fact]
    public void ClipDuration_NotAWholeNumberOfFrames_Exports_AndLoops()
    {
        var (s, ch, clip) = ExportFixture.Walk();
        var c = ExportFixture.ClipOf(s, clip);
        var rig = ExportFixture.RigOf(s, ch);
        c.Duration = Flicks.FromFrame(24, c.Fps) + new Flicks(Flicks.FromFrame(1, c.Fps).Value * 2 / 5); // 24.4 frames
        Assert.Equal(24, c.DurationFrames);
        var result = AnimationExporter.Export(s.Project, c, rig);
        Assert.Equal(25, result.Baked.FrameCount);
        Assert.True(ExportValidator.Validate(result.Glb, s.Project, c, rig).Ok);

        c.Loop.Enabled = true;
        foreach (var mode in new[] { RootMotionMode.Keep, RootMotionMode.InPlace })
        {
            var looped = AnimationExporter.Export(s.Project, c, rig, new ExportOptions { RootMotion = mode });
            var report = ExportValidator.Validate(looped.Glb, s.Project, c, rig);
            Assert.True(report.Ok, mode + ": " + string.Join("\n", report.Issues));
        }
    }
}
