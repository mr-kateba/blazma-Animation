using Blazma.Core.Mathematics;

namespace Blazma.Physics;

/// <summary>A point on the ground plane: model X and Z (metres).</summary>
public readonly record struct Vec2(double X, double Z)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Z + b.Z);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Z - b.Z);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Z * s);
    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Z * b.Z;

    /// <summary>2D cross product (a.X·b.Z − a.Z·b.X); positive when b is counter-clockwise from a in the (X, Z) plane.</summary>
    public static double Cross(Vec2 a, Vec2 b) => a.X * b.Z - a.Z * b.X;
    public double Length() => Math.Sqrt(X * X + Z * Z);
    public static Vec2 Of(Vec3 v) => new(v.X, v.Z);
    public Vec3 At(double y) => new(X, y, Z);
}

/// <summary>Convex-polygon helpers for support polygons on the XZ ground plane.</summary>
public static class Geometry2D
{
    const double Eps = 1e-12;

    /// <summary>
    /// Convex hull (Andrew's monotone chain), counter-clockwise in (X, Z), without duplicate or
    /// collinear points. 0, 1 or 2 points are returned for degenerate input (none, a point, a segment).
    /// Non-finite points are ignored.
    /// </summary>
    public static IReadOnlyList<Vec2> ConvexHull(IEnumerable<Vec2> points)
    {
        var p = points.Where(v => double.IsFinite(v.X) && double.IsFinite(v.Z)).Distinct().OrderBy(v => v.X).ThenBy(v => v.Z).ToList();
        // Merge near-duplicates so float noise does not create slivers.
        var unique = new List<Vec2>();
        foreach (var v in p)
            if (!unique.Exists(u => (u - v).Length() < 1e-9)) unique.Add(v);
        p = unique;
        if (p.Count <= 1) return p;

        var hull = new List<Vec2>(2 * p.Count);
        foreach (var v in p)
        {
            while (hull.Count >= 2 && Vec2.Cross(hull[^1] - hull[^2], v - hull[^2]) <= Eps) hull.RemoveAt(hull.Count - 1);
            hull.Add(v);
        }
        var lower = hull.Count + 1;
        for (var i = p.Count - 2; i >= 0; i--)
        {
            var v = p[i];
            while (hull.Count >= lower && Vec2.Cross(hull[^1] - hull[^2], v - hull[^2]) <= Eps) hull.RemoveAt(hull.Count - 1);
            hull.Add(v);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    /// <summary>
    /// Signed horizontal distance from <paramref name="p"/> to the polygon: positive inside (distance to the
    /// nearest edge, i.e. the stability margin), negative outside. A point or segment "polygon" has no inside,
    /// so the result is ≤ 0. NaN for an empty polygon.
    /// </summary>
    public static double SignedDistance(IReadOnlyList<Vec2> hull, Vec2 p)
    {
        if (hull.Count == 0) return double.NaN;
        if (hull.Count == 1) return -(p - hull[0]).Length();
        if (hull.Count == 2) return -DistanceToSegment(p, hull[0], hull[1]);
        var inside = true;
        var min = double.PositiveInfinity;
        for (var i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            if (Vec2.Cross(b - a, p - a) < 0) inside = false;
            min = Math.Min(min, DistanceToSegment(p, a, b));
        }
        return inside ? min : -min;
    }

    /// <summary>Closest point of the (filled) polygon to <paramref name="p"/>; p itself when inside.</summary>
    public static Vec2 ClosestPoint(IReadOnlyList<Vec2> hull, Vec2 p)
    {
        if (hull.Count == 0) return p;
        if (hull.Count == 1) return hull[0];
        if (hull.Count >= 3 && SignedDistance(hull, p) >= 0) return p;
        var best = hull[0];
        var bestD = double.PositiveInfinity;
        var n = hull.Count == 2 ? 1 : hull.Count;
        for (var i = 0; i < n; i++)
        {
            var c = ClosestOnSegment(p, hull[i], hull[(i + 1) % hull.Count]);
            var d = (c - p).Length();
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    /// <summary>
    /// The polygon shrunk inwards by <paramref name="margin"/> (intersection of the edges' inward half-planes).
    /// Empty when the polygon is thinner than 2·margin, or has fewer than 3 vertices.
    /// </summary>
    public static IReadOnlyList<Vec2> Inset(IReadOnlyList<Vec2> hull, double margin)
    {
        if (hull.Count < 3) return Array.Empty<Vec2>();
        if (margin <= 0) return hull;
        var poly = hull.ToList();
        for (var i = 0; i < hull.Count && poly.Count > 0; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            var e = b - a;
            var len = e.Length();
            if (len < Eps) continue;
            var inward = new Vec2(-e.Z / len, e.X / len); // left of a CCW edge is inside
            var a2 = a + inward * margin;
            poly = ClipHalfPlane(poly, a2, e);
        }
        return poly.Count >= 3 ? ConvexHull(poly) : Array.Empty<Vec2>();
    }

    /// <summary>Area centroid (vertex average for degenerate polygons).</summary>
    public static Vec2 Centroid(IReadOnlyList<Vec2> hull)
    {
        if (hull.Count == 0) return default;
        double a = 0, cx = 0, cz = 0;
        for (var i = 0; i < hull.Count; i++)
        {
            var p = hull[i];
            var q = hull[(i + 1) % hull.Count];
            var c = Vec2.Cross(p, q);
            a += c;
            cx += (p.X + q.X) * c;
            cz += (p.Z + q.Z) * c;
        }
        if (Math.Abs(a) < 1e-15)
            return new Vec2(hull.Average(v => v.X), hull.Average(v => v.Z));
        return new Vec2(cx / (3 * a), cz / (3 * a));
    }

    /// <summary>Polygon area (positive for counter-clockwise).</summary>
    public static double Area(IReadOnlyList<Vec2> hull)
    {
        double a = 0;
        for (var i = 0; i < hull.Count; i++) a += Vec2.Cross(hull[i], hull[(i + 1) % hull.Count]);
        return a / 2;
    }

    public static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b) => (ClosestOnSegment(p, a, b) - p).Length();

    public static Vec2 ClosestOnSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        var l2 = Vec2.Dot(ab, ab);
        if (l2 < Eps) return a;
        var t = Math.Clamp(Vec2.Dot(p - a, ab) / l2, 0, 1);
        return a + ab * t;
    }

    /// <summary>Sutherland–Hodgman: keep the part of <paramref name="poly"/> left of the line through <paramref name="a"/> along <paramref name="dir"/>.</summary>
    static List<Vec2> ClipHalfPlane(List<Vec2> poly, Vec2 a, Vec2 dir)
    {
        var result = new List<Vec2>(poly.Count + 1);
        for (var i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            var sp = Vec2.Cross(dir, p - a);
            var sq = Vec2.Cross(dir, q - a);
            if (sp >= 0) result.Add(p);
            if ((sp >= 0) != (sq >= 0))
            {
                var t = sp / (sp - sq);
                result.Add(p + (q - p) * t);
            }
        }
        return result;
    }
}
