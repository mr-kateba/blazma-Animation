using Blazma.Core.Persistence;

namespace Blazma.Tests.Persistence;

/// <summary>Windows/network-drive file errors, simulated through the AtomicFile/SessionLock seams.</summary>
public class FileErrorTests : IDisposable
{
    public void Dispose()
    {
        AtomicFile.Ops = null!; // back to the real OS calls (the seams are per async flow anyway)
        SessionLock.Open = null!;
    }

    static IOException Win32(int code) => new($"win32 {code}", unchecked((int)0x80070000) | code);

    [Theory]
    [InlineData("unauthorized", "read_only_folder")]
    [InlineData("toolong", "path_too_long")]
    [InlineData("nodir", "not_found")]
    [InlineData("diskfull", "disk_full")]
    public void SessionLock_MapsErrorsToDistinctCodes(string kind, string code)
    {
        using var dir = new TempDir();
        SessionLock.Open = _ => throw (kind switch
        {
            "unauthorized" => new UnauthorizedAccessException("denied"),
            "toolong" => new PathTooLongException("long"),
            "nodir" => new DirectoryNotFoundException("missing"),
            _ => (Exception)Win32(112),
        });
        var e = Assert.Throws<FileAccessException>(() => SessionLock.TryAcquire(dir.File("a.blazanim")));
        Assert.Equal(code, e.Code);
        Assert.Equal(code, FileErrors.Classify(e));
        Assert.DoesNotContain("another Blazma", e.Message);
    }

    [Fact]
    public void SessionLock_SharingViolation_MeansLockedByAnotherProcess()
    {
        using var dir = new TempDir();
        SessionLock.Open = _ => throw Win32(32);
        Assert.Null(SessionLock.TryAcquire(dir.File("a.blazanim")));
    }

    [Fact]
    public void SessionLock_RealSecondHolder_IsLockedByOther()
    {
        using var dir = new TempDir();
        var path = dir.File("a.blazanim");
        using var first = SessionLock.TryAcquire(path);
        Assert.NotNull(first);
        Assert.Null(SessionLock.TryAcquire(path));
    }

    [Theory]
    [InlineData(1175)]
    [InlineData(1176)]
    [InlineData(1177)]
    public void Swap_FallsBackToCopyAndMove_WhenReplaceFileIsUnsupported(int win32)
    {
        using var dir = new TempDir();
        var path = dir.File("p.txt");
        File.WriteAllText(path, "old");
        var replaceCalls = 0;
        AtomicFile.Ops = new AtomicFile.FileOps
        {
            UseReplace = () => true,
            Replace = (src, dst, bak) =>
            {
                replaceCalls++;
                if (win32 == 1176 && bak is not null) File.Move(dst, bak, true); // ReplaceFile moved the original away, then failed
                throw Win32(win32);
            },
        };
        AtomicFile.Write(path, s => s.Write("new"u8));
        Assert.Equal(1, replaceCalls);
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal("old", File.ReadAllText(path + ".bak"));
        Assert.Empty(Directory.GetFiles(dir.Path, ".blz-*.tmp")); // no temp files left
    }

    [Fact]
    public void Swap_RetriesSharingViolations_WithBackoff_ForAboutTenSeconds()
    {
        using var dir = new TempDir();
        var path = dir.File("p.txt");
        File.WriteAllText(path, "old");
        var slept = new List<int>();
        var failures = 4;
        AtomicFile.Ops = new AtomicFile.FileOps
        {
            UseReplace = () => true,
            Replace = (src, dst, bak) => { if (failures-- > 0) throw Win32(32); File.Move(src, dst, true); },
            Sleep = slept.Add,
        };
        AtomicFile.Write(path, s => s.Write("new"u8));
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Equal(4, slept.Count);
        Assert.True(slept.Zip(slept.Skip(1)).All(p => p.Second >= p.First), "backoff grows");

        // Held for good: gives up after ~10 s of waiting, with the sharing-violation code; the destination is untouched.
        slept.Clear();
        AtomicFile.Ops = new AtomicFile.FileOps { UseReplace = () => true, Replace = (_, _, _) => throw Win32(32), Sleep = slept.Add };
        var e = Assert.ThrowsAny<IOException>(() => AtomicFile.Write(path, s => s.Write("newer"u8)));
        Assert.Equal(FileErrors.SharingViolation, FileErrors.Classify(e));
        Assert.InRange(slept.Sum(), 10_000, 11_000);
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(dir.Path, ".blz-*.tmp"));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), "sharing_violation")]
    [InlineData(unchecked((int)0x80070005), "read_only_folder")]
    [InlineData(unchecked((int)0x800700CE), "path_too_long")]
    [InlineData(unchecked((int)0x80070070), "disk_full")]
    [InlineData(11, "sharing_violation")]
    [InlineData(30, "read_only_folder")]
    [InlineData(28, "disk_full")]
    public void HResults_AreClassified(int hr, string code) => Assert.Equal(code, FileErrors.Classify(new IOException("x", hr)));
}
