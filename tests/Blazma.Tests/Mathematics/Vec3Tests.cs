using Blazma.Core.Mathematics;

namespace Blazma.Tests.Mathematics;

public class Vec3Tests
{
    [Fact]
    public void Arithmetic()
    {
        var a = new Vec3(1, 2, 3);
        var b = new Vec3(4, 5, 6);
        Assert.Equal(new Vec3(5, 7, 9), a + b);
        Assert.Equal(new Vec3(-3, -3, -3), a - b);
        Assert.Equal(new Vec3(-1, -2, -3), -a);
        Assert.Equal(new Vec3(2, 4, 6), a * 2);
        Assert.Equal(new Vec3(2, 4, 6), 2 * a);
        Assert.Equal(new Vec3(0.5, 1, 1.5), a / 2);
    }

    [Fact]
    public void DotCross()
    {
        Assert.Equal(32, Vec3.Dot(new Vec3(1, 2, 3), new Vec3(4, 5, 6)));
        Assert.Equal(Vec3.UnitZ, Vec3.Cross(Vec3.UnitX, Vec3.UnitY));
        Assert.Equal(Vec3.UnitX, Vec3.Cross(Vec3.UnitY, Vec3.UnitZ));
    }

    [Fact]
    public void LengthAndNormalize()
    {
        var v = new Vec3(3, 4, 0);
        Assert.Equal(5, v.Length());
        Assert.Equal(25, v.LengthSquared());
        Assert.True(v.Normalized().ApproxEquals(new Vec3(0.6, 0.8, 0)));
        Assert.Equal(Vec3.Zero, Vec3.Zero.Normalized());
    }

    [Fact]
    public void LerpAndApprox()
    {
        Assert.Equal(new Vec3(5, 5, 5), Vec3.Lerp(Vec3.Zero, new Vec3(10, 10, 10), 0.5));
        Assert.True(Vec3.One.ApproxEquals(new Vec3(1 + 1e-10, 1, 1)));
        Assert.False(Vec3.One.ApproxEquals(new Vec3(1.001, 1, 1)));
        Assert.True(Vec3.One.ApproxEquals(new Vec3(1.001, 1, 1), 0.01));
    }
}
