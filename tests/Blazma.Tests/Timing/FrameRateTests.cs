using Blazma.Core.Timing;

namespace Blazma.Tests.Timing;

public class FrameRateTests
{
    [Fact]
    public void ReducesFraction()
    {
        var r = new FrameRate(60, 2);
        Assert.Equal(30, r.Numerator);
        Assert.Equal(1, r.Denominator);
        Assert.Equal(new FrameRate(30), r);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(30, 0)]
    [InlineData(30, -1)]
    public void RejectsNonPositive(int n, int d) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameRate(n, d));

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(30)]
    [InlineData(48)]
    [InlineData(50)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(120)]
    public void CommonRatesAreExactInFlicks(int fps) => Assert.True(new FrameRate(fps).IsExactInFlicks);

    [Fact]
    public void NtscIsExactInFlicksButElevenIsNot()
    {
        // 705,600,000 * 1001 is divisible by 24000 and 30000, so the NTSC rates are whole flicks per frame.
        Assert.True(FrameRate.Ntsc23976.IsExactInFlicks);
        Assert.True(FrameRate.Ntsc2997.IsExactInFlicks);
        Assert.False(new FrameRate(13).IsExactInFlicks);
        Assert.False(new FrameRate(11).IsExactInFlicks);
    }

    [Fact]
    public void FramesPerSecondAndConstants()
    {
        Assert.Equal(24000.0 / 1001, FrameRate.Ntsc23976.FramesPerSecond);
        Assert.Equal(30000.0 / 1001, FrameRate.Ntsc2997.FramesPerSecond);
        Assert.Equal(24, FrameRate.Fps24.FramesPerSecond);
        Assert.Equal(25, FrameRate.Fps25.FramesPerSecond);
        Assert.Equal(30, FrameRate.Fps30.FramesPerSecond);
        Assert.Equal(60, FrameRate.Fps60.FramesPerSecond);
    }

    [Fact]
    public void ToStringAndParse()
    {
        Assert.Equal("30", FrameRate.Fps30.ToString());
        Assert.Equal("30000/1001", FrameRate.Ntsc2997.ToString());
        Assert.Equal(FrameRate.Ntsc2997, FrameRate.Parse("30000/1001"));
        Assert.Equal(FrameRate.Fps30, FrameRate.Parse("30"));
        Assert.Equal(FrameRate.Fps30, FrameRate.Parse("60/2"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("29.97")]
    [InlineData("abc")]
    [InlineData("30/")]
    [InlineData("/30")]
    [InlineData("1/2/3")]
    [InlineData("0")]
    [InlineData("-30")]
    [InlineData("30/0")]
    [InlineData(" 30")]
    public void ParseRejectsBadInput(string s) => Assert.Throws<FormatException>(() => FrameRate.Parse(s));
}
