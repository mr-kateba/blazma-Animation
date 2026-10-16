using Blazma.Core.Timing;

namespace Blazma.Tests.Timing;

public class FlicksTests
{
    [Fact]
    public void SecondsConversion()
    {
        Assert.Equal(705_600_000, Flicks.FromSeconds(1).Value);
        Assert.Equal(0, Flicks.Zero.Value);
        Assert.Equal(0.5, new Flicks(352_800_000).ToSeconds());
        Assert.Equal(-705_600_000, Flicks.FromSeconds(-1).Value);
    }

    [Fact]
    public void FromFrameExact()
    {
        Assert.Equal(705_600_000 / 30, Flicks.FromFrame(1, FrameRate.Fps30).Value);
        Assert.Equal(705_600_000, Flicks.FromFrame(30, FrameRate.Fps30).Value);
        Assert.Equal(-705_600_000 / 24, Flicks.FromFrame(-1, FrameRate.Fps24).Value);
    }

    [Fact]
    public void FromFrameNtscRounds()
    {
        // 705600000*1001/30000 = 23543.52 * 1000 = 23,543,520 exactly? compute via decimal reference.
        var r = FrameRate.Ntsc2997;
        for (long n = -5; n <= 5; n++)
        {
            decimal exact = (decimal)n * Flicks.PerSecond * r.Denominator / r.Numerator;
            long expected = (long)Math.Round(exact, MidpointRounding.AwayFromZero);
            Assert.Equal(expected, Flicks.FromFrame(n, r).Value);
        }
    }

    [Theory]
    [InlineData(30, 1)]
    [InlineData(24, 1)]
    [InlineData(60, 1)]
    [InlineData(30000, 1001)]
    [InlineData(24000, 1001)]
    [InlineData(48, 1)]
    [InlineData(7, 1)]
    [InlineData(7, 3)]
    [InlineData(11, 1)]
    public void FrameRoundTrip(int num, int den)
    {
        var rate = new FrameRate(num, den);
        for (long n = -1000; n <= 100000; n++)
            Assert.Equal(n, Flicks.FromFrame(n, rate).ToFrame(rate));
    }

    [Fact]
    public void FromFrameRoundsInexactRates()
    {
        var r = new FrameRate(7); // 705600000/7 = 100800000 exact; use 11 for inexact
        var r11 = new FrameRate(11);
        for (long n = -20; n <= 20; n++)
        {
            decimal exact = (decimal)n * Flicks.PerSecond / 11;
            Assert.Equal((long)Math.Round(exact, MidpointRounding.AwayFromZero), Flicks.FromFrame(n, r11).Value);
        }
        Assert.Equal(100_800_000, Flicks.FromFrame(1, r).Value);
    }

    [Fact]
    public void ToFrameRoundsToNearestMidpointAway()
    {
        var r = FrameRate.Fps30; // 23,520,000 flicks per frame
        long f = 23_520_000;
        Assert.Equal(1, new Flicks(f / 2).ToFrame(r));
        Assert.Equal(0, new Flicks(f / 2 - 1).ToFrame(r));
        Assert.Equal(-1, new Flicks(-f / 2).ToFrame(r));
        Assert.Equal(-1, new Flicks(-f).ToFrame(r));
    }

    [Fact]
    public void ToFrameExactIsFractional()
    {
        Assert.Equal(1.5, new Flicks(23_520_000 * 3 / 2).ToFrameExact(FrameRate.Fps30), 1e-12);
    }

    [Fact]
    public void LargeFrameDoesNotOverflowIntermediate()
    {
        long frame = 10_000_000_000; // 3.3e9 s at 30fps; intermediate exceeds long
        var f = Flicks.FromFrame(frame, FrameRate.Fps30);
        Assert.Equal(frame, f.ToFrame(FrameRate.Fps30));
    }

    [Fact]
    public void OperatorsAndComparison()
    {
        var a = new Flicks(10);
        var b = new Flicks(20);
        Assert.Equal(new Flicks(30), a + b);
        Assert.Equal(new Flicks(-10), a - b);
        Assert.True(a < b);
        Assert.True(b > a);
        Assert.True(a <= new Flicks(10));
        Assert.True(a >= new Flicks(10));
        Assert.True(a.CompareTo(b) < 0);
        Assert.Equal(a, Flicks.Min(a, b));
        Assert.Equal(b, Flicks.Max(a, b));
    }
}
