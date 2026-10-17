using Blazma.Core.Mathematics;
using Blazma.Core.Model;

namespace Blazma.Combat;

/// <summary>
/// A collision shape placed in the world. Sphere: <see cref="A"/> = <see cref="B"/> = centre. Capsule: the segment A–B swept by
/// <see cref="Radius"/>. Box: centre A (= B), orientation <see cref="Rotation"/>, half extents <see cref="Half"/> (Radius 0).
/// Spheres and capsules are both "swept segments", which keeps every pair test a distance query.
/// </summary>
public readonly record struct WorldShape(CollisionShapeKind Kind, Vec3 A, Vec3 B, double Radius, Quat Rotation, Vec3 Half)
{
    public Vec3 Center => (A + B) * 0.5;

    public bool IsBox => Kind == CollisionShapeKind.Box;

    /// <summary>The smallest "thickness" of the shape: the radius, or the smallest half extent of a box.</summary>
    public double Thickness => IsBox ? Math.Min(Half.X, Math.Min(Half.Y, Half.Z)) : Radius;

    public bool IsFinite => Finite(A) && Finite(B) && double.IsFinite(Radius) && double.IsFinite(Rotation.X) && double.IsFinite(Rotation.Y)
        && double.IsFinite(Rotation.Z) && double.IsFinite(Rotation.W) && Finite(Half);

    static bool Finite(Vec3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

    /// <summary>The shape <paramref name="s"/> (defined in its local frame) placed by <paramref name="t"/> (scale ignored).</summary>
    public static WorldShape From(CollisionShape s, Transform t)
    {
        var c = t.Translation;
        var r = t.Rotation.Normalized();
        return s.Kind switch
        {
            CollisionShapeKind.Capsule => new WorldShape(s.Kind, c - r.Rotate(Vec3.UnitY * (s.Length / 2)), c + r.Rotate(Vec3.UnitY * (s.Length / 2)), s.Radius, r, Vec3.Zero),
            CollisionShapeKind.Box => new WorldShape(s.Kind, c, c, 0, r, s.Size * 0.5),
            _ => new WorldShape(CollisionShapeKind.Sphere, c, c, s.Radius, r, Vec3.Zero),
        };
    }

    public static WorldShape Sphere(Vec3 c, double r) => new(CollisionShapeKind.Sphere, c, c, r, Quat.Identity, Vec3.Zero);
    public static WorldShape Capsule(Vec3 a, Vec3 b, double r) => new(CollisionShapeKind.Capsule, a, b, r, Quat.Identity, Vec3.Zero);
    public static WorldShape Box(Vec3 c, Quat rotation, Vec3 half) => new(CollisionShapeKind.Box, c, c, 0, rotation.Normalized(), half);
}

/// <param name="Point">Contact point (middle of the overlap).</param>
/// <param name="Normal">Unit normal pointing from the second shape (the hurtbox) towards the first (the hitbox).</param>
/// <param name="Depth">Penetration depth (≥ 0).</param>
public readonly record struct Contact(Vec3 Point, Vec3 Normal, double Depth);

/// <summary>
/// Pair tests between spheres, capsules and oriented boxes (Ericson, Real-Time Collision Detection: 5.1.9 closest points of two
/// segments, 5.1.4 closest point on an OBB, 4.4 separating axes). All iterative, no allocation.
/// </summary>
public static class Collision
{
    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> touch or overlap; the contact normal points from b to a.</summary>
    public static bool Test(in WorldShape a, in WorldShape b, out Contact contact)
    {
        contact = default;
        if (!a.IsFinite || !b.IsFinite) return false;
        if (!a.IsBox && !b.IsBox) return SegmentSegment(a, b, out contact);
        if (a.IsBox && b.IsBox) return BoxBox(a, b, out contact);
        if (b.IsBox) return SegmentBox(a, b, out contact);
        if (!SegmentBox(b, a, out var c)) return false;
        contact = c with { Normal = -c.Normal };
        return true;
    }

    static bool SegmentSegment(in WorldShape a, in WorldShape b, out Contact contact)
    {
        ClosestPtSegmentSegment(a.A, a.B, b.A, b.B, out var p, out var q);
        var d = p - q;
        var dist = d.Length();
        var r = a.Radius + b.Radius;
        contact = default;
        if (dist > r) return false;
        var n = dist > 1e-12 ? d / dist : FallbackNormal(a, b);
        var depth = r - dist;
        contact = new Contact(q + n * (b.Radius - depth / 2), n, depth);
        return true;
    }

    /// <summary>Segment (sphere/capsule <paramref name="s"/>) against box <paramref name="box"/>; normal from the box to the segment.</summary>
    static bool SegmentBox(in WorldShape s, in WorldShape box, out Contact contact)
    {
        contact = default;
        var (p, q, dist) = ClosestSegmentBox(s.A, s.B, box);
        if (dist > s.Radius) return false;
        Vec3 n;
        double depth;
        if (dist > 1e-12)
        {
            n = (p - q) / dist;
            depth = s.Radius - dist;
        }
        else
        {
            // The segment point is inside the box: push out through the nearest face.
            var local = box.Rotation.Conjugate().Rotate(p - box.A);
            var dx = box.Half.X - Math.Abs(local.X);
            var dy = box.Half.Y - Math.Abs(local.Y);
            var dz = box.Half.Z - Math.Abs(local.Z);
            Vec3 axis;
            double inside;
            if (dx <= dy && dx <= dz) { axis = new Vec3(Math.Sign(local.X) >= 0 ? 1 : -1, 0, 0); inside = dx; }
            else if (dy <= dz) { axis = new Vec3(0, Math.Sign(local.Y) >= 0 ? 1 : -1, 0); inside = dy; }
            else { axis = new Vec3(0, 0, Math.Sign(local.Z) >= 0 ? 1 : -1); inside = dz; }
            n = box.Rotation.Rotate(axis);
            depth = s.Radius + inside;
        }
        contact = new Contact(q + n * Math.Min(depth / 2, s.Radius), n, depth);
        return true;
    }

    static bool BoxBox(in WorldShape a, in WorldShape b, out Contact contact)
    {
        contact = default;
        var ax = new[] { a.Rotation.Rotate(Vec3.UnitX), a.Rotation.Rotate(Vec3.UnitY), a.Rotation.Rotate(Vec3.UnitZ) };
        var bx = new[] { b.Rotation.Rotate(Vec3.UnitX), b.Rotation.Rotate(Vec3.UnitY), b.Rotation.Rotate(Vec3.UnitZ) };
        var ha = new[] { a.Half.X, a.Half.Y, a.Half.Z };
        var hb = new[] { b.Half.X, b.Half.Y, b.Half.Z };
        var t = a.A - b.A; // from b to a
        var best = double.PositiveInfinity;
        var bestAxis = Vec3.UnitY;
        bool Axis(Vec3 l)
        {
            var len = l.Length();
            if (len < 1e-9) return true; // parallel edges: covered by the face axes
            l /= len;
            double ra = 0, rb = 0;
            for (var i = 0; i < 3; i++) { ra += ha[i] * Math.Abs(Vec3.Dot(ax[i], l)); rb += hb[i] * Math.Abs(Vec3.Dot(bx[i], l)); }
            var dist = Vec3.Dot(t, l);
            var overlap = ra + rb - Math.Abs(dist);
            if (overlap < 0) return false;
            if (overlap < best) { best = overlap; bestAxis = dist >= 0 ? l : -l; }
            return true;
        }
        for (var i = 0; i < 3; i++) if (!Axis(ax[i]) || !Axis(bx[i])) return false;
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
                if (!Axis(Vec3.Cross(ax[i], bx[j]))) return false;
        var pa = ClosestPtPointBox(b.A, a);
        var pb = ClosestPtPointBox(a.A, b);
        contact = new Contact((pa + pb) * 0.5, bestAxis, best);
        return true;
    }

    static Vec3 FallbackNormal(in WorldShape a, in WorldShape b)
    {
        var axis = b.B - b.A;
        if (axis.LengthSquared() < 1e-18) axis = a.B - a.A;
        if (axis.LengthSquared() < 1e-18) return Vec3.UnitY;
        var n = Vec3.Cross(axis, Vec3.UnitY);
        if (n.LengthSquared() < 1e-18) n = Vec3.Cross(axis, Vec3.UnitX);
        return n.Normalized();
    }

    /// <summary>Closest points <paramref name="c1"/> on p1–q1 and <paramref name="c2"/> on p2–q2 (degenerate segments allowed).</summary>
    public static void ClosestPtSegmentSegment(Vec3 p1, Vec3 q1, Vec3 p2, Vec3 q2, out Vec3 c1, out Vec3 c2)
    {
        const double eps = 1e-18;
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        var a = Vec3.Dot(d1, d1);
        var e = Vec3.Dot(d2, d2);
        var f = Vec3.Dot(d2, r);
        double s, t;
        if (a <= eps && e <= eps) { c1 = p1; c2 = p2; return; }
        if (a <= eps)
        {
            s = 0;
            t = Math.Clamp(f / e, 0, 1);
        }
        else
        {
            var c = Vec3.Dot(d1, r);
            if (e <= eps)
            {
                t = 0;
                s = Math.Clamp(-c / a, 0, 1);
            }
            else
            {
                var b = Vec3.Dot(d1, d2);
                var denom = a * e - b * b;
                s = denom > eps * Math.Max(1, a * e) ? Math.Clamp((b * f - c * e) / denom, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
                else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        c1 = p1 + d1 * s;
        c2 = p2 + d2 * t;
    }

    /// <summary>Closest point of an oriented box to <paramref name="p"/> (p itself when inside).</summary>
    public static Vec3 ClosestPtPointBox(Vec3 p, in WorldShape box)
    {
        var inv = box.Rotation.Conjugate();
        var l = inv.Rotate(p - box.A);
        var c = new Vec3(Math.Clamp(l.X, -box.Half.X, box.Half.X), Math.Clamp(l.Y, -box.Half.Y, box.Half.Y), Math.Clamp(l.Z, -box.Half.Z, box.Half.Z));
        return box.A + box.Rotation.Rotate(c);
    }

    /// <summary>
    /// Closest points between segment a–b and a box: the distance from a point moving along the segment to a convex set is convex,
    /// so a golden-section search (exact to ~1e-12 of the segment length) finds its minimum.
    /// </summary>
    public static (Vec3 SegmentPoint, Vec3 BoxPoint, double Distance) ClosestSegmentBox(Vec3 a, Vec3 b, in WorldShape box)
    {
        var bx = box;
        double D(double u)
        {
            var p = a + (b - a) * u;
            return (p - ClosestPtPointBox(p, bx)).Length();
        }
        double lo = 0, hi = 1;
        if ((b - a).LengthSquared() > 1e-24)
        {
            const double g = 0.6180339887498949;
            var x1 = hi - g * (hi - lo);
            var x2 = lo + g * (hi - lo);
            double f1 = D(x1), f2 = D(x2);
            for (var i = 0; i < 80 && hi - lo > 1e-12; i++)
            {
                if (f1 <= f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = D(x1); }
                else { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = D(x2); }
            }
        }
        var u = (lo + hi) / 2;
        // The minimum may sit on an end point (flat region): keep the best of the three.
        foreach (var cand in new[] { 0.0, 1.0 })
            if (D(cand) < D(u)) u = cand;
        var sp = a + (b - a) * u;
        var bp = ClosestPtPointBox(sp, box);
        return (sp, bp, (sp - bp).Length());
    }
}
