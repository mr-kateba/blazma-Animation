using System.Text.Json.Serialization;
using Blazma.Core.Serialization;

namespace Blazma.Core.Mathematics;

/// <summary>A 3D vector of doubles (meters unless stated otherwise). Right-handed, +Y up.</summary>
[JsonConverter(typeof(Vec3JsonConverter))]
public readonly record struct Vec3(double X, double Y, double Z)
{
    /// <summary>(0,0,0).</summary>
    public static readonly Vec3 Zero = new(0, 0, 0);
    /// <summary>(1,1,1).</summary>
    public static readonly Vec3 One = new(1, 1, 1);
    /// <summary>(1,0,0).</summary>
    public static readonly Vec3 UnitX = new(1, 0, 0);
    /// <summary>(0,1,0).</summary>
    public static readonly Vec3 UnitY = new(0, 1, 0);
    /// <summary>(0,0,1).</summary>
    public static readonly Vec3 UnitZ = new(0, 0, 1);

    /// <summary>Component-wise sum.</summary>
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    /// <summary>Component-wise difference.</summary>
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    /// <summary>Negation.</summary>
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    /// <summary>Scalar multiplication.</summary>
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    /// <summary>Scalar multiplication.</summary>
    public static Vec3 operator *(double s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);
    /// <summary>Scalar division.</summary>
    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    /// <summary>Dot product.</summary>
    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>Cross product (right-handed).</summary>
    public static Vec3 Cross(Vec3 a, Vec3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>Euclidean length.</summary>
    public double Length() => Math.Sqrt(LengthSquared());

    /// <summary>Squared Euclidean length.</summary>
    public double LengthSquared() => X * X + Y * Y + Z * Z;

    /// <summary>Unit-length copy, or <see cref="Zero"/> for a zero-length vector.</summary>
    public Vec3 Normalized()
    {
        double len = Length();
        return len > 0 ? this / len : Zero;
    }

    /// <summary>Linear interpolation: a + (b - a) * t.</summary>
    public static Vec3 Lerp(Vec3 a, Vec3 b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    /// <summary>True when every component differs by at most <paramref name="epsilon"/>.</summary>
    public bool ApproxEquals(Vec3 other, double epsilon = 1e-9) =>
        Math.Abs(X - other.X) <= epsilon && Math.Abs(Y - other.Y) <= epsilon && Math.Abs(Z - other.Z) <= epsilon;
}
