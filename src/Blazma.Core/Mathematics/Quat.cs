using System.Text.Json.Serialization;
using Blazma.Core.Serialization;

namespace Blazma.Core.Mathematics;

/// <summary>
/// Rotation quaternion (x, y, z, w), Hamilton convention. q and -q represent the same rotation.
/// </summary>
[JsonConverter(typeof(QuatJsonConverter))]
public readonly record struct Quat(double X, double Y, double Z, double W)
{
    /// <summary>The identity rotation.</summary>
    public static readonly Quat Identity = new(0, 0, 0, 1);

    /// <summary>Rotation of <paramref name="radians"/> about <paramref name="axis"/> (normalized internally).</summary>
    public static Quat FromAxisAngle(Vec3 axis, double radians)
    {
        Vec3 n = axis.Normalized();
        if (n == Vec3.Zero) return Identity;
        double h = radians * 0.5;
        double s = Math.Sin(h);
        return new Quat(n.X * s, n.Y * s, n.Z * s, Math.Cos(h));
    }

    /// <summary>Intrinsic Euler XYZ (rotate about X, then new Y, then new Z): q = qx * qy * qz.</summary>
    public static Quat FromEulerXYZ(Vec3 radians)
    {
        Quat qx = FromAxisAngle(Vec3.UnitX, radians.X);
        Quat qy = FromAxisAngle(Vec3.UnitY, radians.Y);
        Quat qz = FromAxisAngle(Vec3.UnitZ, radians.Z);
        return qx * qy * qz;
    }

    /// <summary>
    /// Inverse of <see cref="FromEulerXYZ"/>. Y is returned in [-pi/2, pi/2]. At gimbal lock
    /// (|Y| ~ pi/2) the Z angle is set to 0 and all remaining rotation is assigned to X.
    /// </summary>
    public Vec3 ToEulerXYZ()
    {
        Quat q = Normalized();
        double r02 = 2 * (q.X * q.Z + q.Y * q.W);
        double r12 = 2 * (q.Y * q.Z - q.X * q.W);
        double r22 = 1 - 2 * (q.X * q.X + q.Y * q.Y);
        double r01 = 2 * (q.X * q.Y - q.Z * q.W);
        double r00 = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
        double y = Math.Asin(Math.Clamp(r02, -1.0, 1.0));
        if (Math.Abs(r02) < 0.9999999)
        {
            return new Vec3(Math.Atan2(-r12, r22), y, Math.Atan2(-r01, r00));
        }
        double r21 = 2 * (q.Y * q.Z + q.X * q.W);
        double r11 = 1 - 2 * (q.X * q.X + q.Z * q.Z);
        return new Vec3(Math.Atan2(r21, r11), y, 0);
    }

    /// <summary>Hamilton product; <c>(a*b).Rotate(v) == a.Rotate(b.Rotate(v))</c>.</summary>
    public static Quat operator *(Quat a, Quat b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    /// <summary>Rotates a vector (assumes a unit quaternion).</summary>
    public Vec3 Rotate(Vec3 v)
    {
        var u = new Vec3(X, Y, Z);
        Vec3 t = Vec3.Cross(u, v) * 2.0;
        return v + t * W + Vec3.Cross(u, t);
    }

    /// <summary>Conjugate (-x,-y,-z,w).</summary>
    public Quat Conjugate() => new(-X, -Y, -Z, W);

    /// <summary>Multiplicative inverse (conjugate / length squared).</summary>
    public Quat Inverse()
    {
        double n = X * X + Y * Y + Z * Z + W * W;
        return n > 0 ? new Quat(-X / n, -Y / n, -Z / n, W / n) : Identity;
    }

    /// <summary>Unit-length copy; identity for a zero quaternion.</summary>
    public Quat Normalized()
    {
        double len = Length();
        return len > 0 ? new Quat(X / len, Y / len, Z / len, W / len) : Identity;
    }

    /// <summary>4D length.</summary>
    public double Length() => Math.Sqrt(X * X + Y * Y + Z * Z + W * W);

    /// <summary>4D dot product.</summary>
    public static double Dot(Quat a, Quat b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

    /// <summary>True when the length is within <paramref name="eps"/> of 1.</summary>
    public bool IsNormalized(double eps = 1e-9) => Math.Abs(Length() - 1.0) <= eps;

    /// <summary>Spherical interpolation along the shortest path; normalized lerp when nearly parallel.</summary>
    public static Quat Slerp(Quat a, Quat b, double t)
    {
        double dot = Dot(a, b);
        if (dot < 0)
        {
            b = new Quat(-b.X, -b.Y, -b.Z, -b.W);
            dot = -dot;
        }
        double ka, kb;
        if (dot > 0.9995)
        {
            ka = 1 - t;
            kb = t;
        }
        else
        {
            double theta = Math.Acos(Math.Min(dot, 1.0));
            double sin = Math.Sin(theta);
            ka = Math.Sin((1 - t) * theta) / sin;
            kb = Math.Sin(t * theta) / sin;
        }
        return new Quat(ka * a.X + kb * b.X, ka * a.Y + kb * b.Y, ka * a.Z + kb * b.Z, ka * a.W + kb * b.W).Normalized();
    }

    /// <summary>Rotation angle in radians (0..pi) from <paramref name="a"/> to <paramref name="b"/>; unit inputs assumed.</summary>
    public static double AngleBetween(Quat a, Quat b)
    {
        double s = Dot(a, b) < 0 ? -1.0 : 1.0;
        var sb = new Quat(s * b.X, s * b.Y, s * b.Z, s * b.W);
        var d = new Quat(a.X - sb.X, a.Y - sb.Y, a.Z - sb.Z, a.W - sb.W);
        var p = new Quat(a.X + sb.X, a.Y + sb.Y, a.Z + sb.Z, a.W + sb.W);
        // For unit inputs |a - s b| = 2 sin(theta/4) and |a + s b| = 2 cos(theta/4), so theta = 4 * atan2.
        return 4.0 * Math.Atan2(d.Length(), p.Length());
    }

    /// <summary>True when the rotation angle between the two quaternions is at most <paramref name="epsilonRadians"/>.</summary>
    public bool ApproxEquals(Quat other, double epsilonRadians = 1e-9) =>
        AngleBetween(this, other) <= epsilonRadians;
}
