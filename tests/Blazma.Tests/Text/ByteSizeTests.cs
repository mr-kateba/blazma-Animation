using Blazma.Core.Text;

namespace Blazma.Tests.Text;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(148L, "148 B")]
    [InlineData(7_801L, "7.8 KB")]
    [InlineData(7_295_054L, "7.3 MB")]
    [InlineData(999_600L, "1.0 MB")]          // never "1000 KB"
    [InlineData(553_400_000L, "553 MB")]
    [InlineData(999_700_000L, "1.0 GB")]      // never "1000 MB"
    [InlineData(1L << 30, "1.1 GB")]           // 1 GiB is 1.07 GB
    [InlineData(1_133_166_784L, "1.1 GB")]
    [InlineData(15_177_675_936L, "15.2 GB")]
    [InlineData(150_000_000_000L, "150 GB")]
    [InlineData(-5L, "0 B")]
    public void Sizes_Use_Decimal_Units(long bytes, string expected) => Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void Split_Gives_The_Number_And_The_Unit_For_Translated_Templates()
    {
        Assert.Equal(("6.2", "GB"), ByteSize.Split(6_198_797_854));
        Assert.Equal(("55.3", "MB"), ByteSize.Split(55_300_000));
    }

    [Theory]
    [InlineData(16L << 30, "16")]
    [InlineData(8L << 30, "8")]
    [InlineData((long)(7.5 * (1L << 30)), "7.5")]
    [InlineData(24L << 30, "24")]
    public void Memory_Is_Counted_Like_Windows_Labels_Ram(long bytes, string expected) => Assert.Equal(expected, ByteSize.MemoryGb(bytes));
}
