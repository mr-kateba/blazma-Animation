using Blazma.Core.Mathematics;

namespace Blazma.Tests.Mathematics;

public class QuatTests
{
    private static Quat RandomQuat(Random r) =>
        new Quat(r.NextDouble() - 0.5, r.NextDouble() - 0.5, r.NextDouble() - 0.5, r.NextDouble() - 0.5).Normalized();

    [Fact]
    public void Rotate90AboutYMapsZToX()
    {
        var q = Quat.FromAxisAngle(Vec3.UnitY, Math.PI / 2);
        Assert.True(q.Rotate(Vec3.UnitZ).ApproxEquals(Vec3.UnitX));
        Assert.True(q.Rotate(Vec3.UnitX).ApproxEquals(-Vec3.UnitZ));
    }

    [Fact]
    public void AxisIsNormalizedInternally()
    {
        var q = Quat.FromAxisAngle(new Vec3(0, 5, 0), Math.PI / 2);
        Assert.True(q.IsNormalized());
        Assert.True(q.Rotate(Vec3.UnitZ).ApproxEquals(Vec3.UnitX));
    }

    [Fact]
    public void CompositionOrder()
    {
        var r = new Random(1);
        for (int i = 0; i < 50; i++)
        {
            var a = RandomQuat(r);
            var b = RandomQuat(r);
            var v = new Vec3(r.NextDouble(), r.NextDouble(), r.NextDouble());
            Assert.True((a * b).Rotate(v).ApproxEquals(a.Rotate(b.Rotate(v)), 1e-12));
        }
    }

    [Fact]
    public void InverseAndConjugate()
    {
        var q = RandomQuat(new Random(2));
        Assert.True((q * q.Inverse()).ApproxEquals(Quat.Identity, 1e-12));
        Assert.True((q * q.Conjugate()).ApproxEquals(Quat.Identity, 1e-12));
        Assert.True(q.Length() > 0.999999);
    }

    [Fact]
    public void EulerIntrinsicOrder()
    {
        var e = new Vec3(0.3, 0.4, 0.5);
        var q = Quat.FromEulerXYZ(e);
        var expected = Quat.FromAxisAngle(Vec3.UnitX, 0.3) * Quat.FromAxisAngle(Vec3.UnitY, 0.4) * Quat.FromAxisAngle(Vec3.UnitZ, 0.5);
        Assert.True(q.ApproxEquals(expected, 1e-12));
        // Intrinsic: X-only rotation applied last in world terms is wrong; verify via Z-first-on-vector.
        var v = Vec3.UnitX;
        var viaSteps = Quat.FromAxisAngle(Vec3.UnitX, 0.3).Rotate(Quat.FromAxisAngle(Vec3.UnitY, 0.4).Rotate(Quat.FromAxisAngle(Vec3.UnitZ, 0.5).Rotate(v)));
        Assert.True(q.Rotate(v).ApproxEquals(viaSteps, 1e-12));
    }

    [Fact]
    public void EulerRoundTripRandom()
    {
        var r = new Random(42);
        for (int i = 0; i < 500; i++)
        {
            var e = new Vec3((r.NextDouble() * 2 - 1) * 3.0, (r.NextDouble() * 2 - 1) * 1.4, (r.NextDouble() * 2 - 1) * 3.0);
            var q = Quat.FromEulerXYZ(e);
            Assert.True(e.ApproxEquals(q.ToEulerXYZ(), 1e-9), $"{e} -> {q.ToEulerXYZ()}");
        }
    }

    [Theory]
    [InlineData(0.7, 1.5707963267948966, 0.0)]
    [InlineData(-0.4, -1.5707963267948966, 0.0)]
    public void EulerGimbalLockIsGraceful(double x, double y, double z)
    {
        var q = Quat.FromEulerXYZ(new Vec3(x, y, z));
        var e = q.ToEulerXYZ();
        Assert.False(double.IsNaN(e.X) || double.IsNaN(e.Y) || double.IsNaN(e.Z));
        Assert.True(Quat.FromEulerXYZ(e).ApproxEquals(q, 1e-6));
    }

    [Fact]
    public void GimbalLockWithZRotationStillReproducesRotation()
    {
        var q = Quat.FromEulerXYZ(new Vec3(0.2, Math.PI / 2, 0.9));
        Assert.True(Quat.FromEulerXYZ(q.ToEulerXYZ()).ApproxEquals(q, 1e-6));
    }

    [Fact]
    public void SlerpEndpointsAndMidpoint()
    {
        var a = Quat.Identity;
        var b = Quat.FromAxisAngle(Vec3.UnitY, Math.PI / 2);
        Assert.True(Quat.Slerp(a, b, 0).ApproxEquals(a, 1e-12));
        Assert.True(Quat.Slerp(a, b, 1).ApproxEquals(b, 1e-12));
        Assert.True(Quat.Slerp(a, b, 0.5).ApproxEquals(Quat.FromAxisAngle(Vec3.UnitY, Math.PI / 4), 1e-12));
    }

    [Fact]
    public void SlerpTakesShortestPath()
    {
        var a = Quat.Identity;
        var b = Quat.FromAxisAngle(Vec3.UnitY, 0.4);
        var nb = new Quat(-b.X, -b.Y, -b.Z, -b.W);
        var mid = Quat.Slerp(a, nb, 0.5);
        Assert.True(mid.ApproxEquals(Quat.FromAxisAngle(Vec3.UnitY, 0.2), 1e-12));
    }

    [Fact]
    public void SlerpNearlyParallelIsNormalized()
    {
        var a = Quat.Identity;
        var b = Quat.FromAxisAngle(Vec3.UnitX, 1e-6);
        var m = Quat.Slerp(a, b, 0.5);
        Assert.True(m.IsNormalized());
        Assert.True(m.ApproxEquals(Quat.FromAxisAngle(Vec3.UnitX, 5e-7), 1e-12));
    }

    [Fact]
    public void AngleBetweenSignInvariant()
    {
        var q = Quat.FromAxisAngle(new Vec3(1, 2, 3), 1.1);
        var nq = new Quat(-q.X, -q.Y, -q.Z, -q.W);
        Assert.Equal(0.0, Quat.AngleBetween(q, nq));
        Assert.Equal(0.0, Quat.AngleBetween(q, q));
        Assert.True(q.ApproxEquals(nq));
    }

    [Fact]
    public void AngleBetweenSmallAnglePrecision()
    {
        var a = Quat.FromAxisAngle(new Vec3(1, 1, 0), 0.3);
        var d = Quat.FromAxisAngle(new Vec3(0, 0, 1), 1e-6);
        var b = d * a;
        Assert.Equal(1e-6, Quat.AngleBetween(a, b), 1e-12);
        var nb = new Quat(-b.X, -b.Y, -b.Z, -b.W);
        Assert.Equal(1e-6, Quat.AngleBetween(a, nb), 1e-12);
    }

    [Fact]
    public void AngleBetweenLargeAngle()
    {
        var b = Quat.FromAxisAngle(Vec3.UnitZ, 2.5);
        Assert.Equal(2.5, Quat.AngleBetween(Quat.Identity, b), 1e-12);
    }
}
