using Blazma.Core.Mathematics;

namespace Blazma.Tests.Mathematics;

public class TransformTests
{
    private static Transform RandomTransform(Random r, bool uniform)
    {
        var q = new Quat(r.NextDouble() - 0.5, r.NextDouble() - 0.5, r.NextDouble() - 0.5, r.NextDouble() - 0.5).Normalized();
        double s = 0.5 + r.NextDouble();
        Vec3 scale = uniform ? new Vec3(s, s, s) : new Vec3(0.5 + r.NextDouble(), 0.5 + r.NextDouble(), 0.5 + r.NextDouble());
        return new Transform(new Vec3(r.NextDouble() * 10, r.NextDouble() * 10, r.NextDouble() * 10), q, scale);
    }

    [Fact]
    public void IdentityLeavesPointsAlone()
    {
        var p = new Vec3(1, 2, 3);
        Assert.Equal(p, Transform.Identity.TransformPoint(p));
    }

    [Fact]
    public void TransformPointOrderIsScaleRotateTranslate()
    {
        var t = new Transform(new Vec3(1, 0, 0), Quat.FromAxisAngle(Vec3.UnitY, Math.PI / 2), new Vec3(2, 2, 2));
        Assert.True(t.TransformPoint(Vec3.UnitZ).ApproxEquals(new Vec3(3, 0, 0)));
    }

    [Fact]
    public void ComposeMatchesSequentialForUniformParentScale()
    {
        var r = new Random(7);
        for (int i = 0; i < 100; i++)
        {
            var parent = RandomTransform(r, true);
            var child = RandomTransform(r, false);
            var p = new Vec3(r.NextDouble(), r.NextDouble(), r.NextDouble());
            Assert.True((parent * child).TransformPoint(p).ApproxEquals(parent.TransformPoint(child.TransformPoint(p)), 1e-9));
        }
    }

    [Fact]
    public void ComposeNonUniformParentUsesTrsApproximation()
    {
        var parent = new Transform(new Vec3(1, 2, 3), Quat.Identity, new Vec3(2, 1, 1));
        var child = new Transform(new Vec3(1, 0, 0), Quat.Identity, new Vec3(1, 3, 1));
        var c = parent * child;
        Assert.Equal(new Vec3(3, 2, 3), c.Translation);
        Assert.Equal(new Vec3(2, 3, 1), c.Scale);
    }

    [Fact]
    public void InverseRoundTrip()
    {
        var r = new Random(9);
        for (int i = 0; i < 100; i++)
        {
            var t = RandomTransform(r, true);
            var p = new Vec3(r.NextDouble(), r.NextDouble(), r.NextDouble());
            Assert.True(t.Inverse().TransformPoint(t.TransformPoint(p)).ApproxEquals(p, 1e-9));
            Assert.True((t * t.Inverse()).TransformPoint(p).ApproxEquals(p, 1e-9));
        }
    }

    [Fact]
    public void LerpEndpointsAndMidpoint()
    {
        var a = Transform.Identity;
        var b = new Transform(new Vec3(2, 0, 0), Quat.FromAxisAngle(Vec3.UnitY, Math.PI / 2), new Vec3(3, 3, 3));
        Assert.True(Transform.Lerp(a, b, 0).Rotation.ApproxEquals(a.Rotation));
        Assert.True(Transform.Lerp(a, b, 1).Rotation.ApproxEquals(b.Rotation, 1e-12));
        var m = Transform.Lerp(a, b, 0.5);
        Assert.Equal(new Vec3(1, 0, 0), m.Translation);
        Assert.Equal(new Vec3(2, 2, 2), m.Scale);
        Assert.True(m.Rotation.ApproxEquals(Quat.FromAxisAngle(Vec3.UnitY, Math.PI / 4), 1e-12));
    }
}
