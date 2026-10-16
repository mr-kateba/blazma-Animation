namespace Blazma.Core.Mathematics;

/// <summary>Translation-rotation-scale transform; points map as T + R * (S ⊙ p).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(Blazma.Core.Serialization.TransformJsonConverter))]
public readonly record struct Transform(Vec3 Translation, Quat Rotation, Vec3 Scale)
{
    /// <summary>No translation, identity rotation, unit scale.</summary>
    public static readonly Transform Identity = new(Vec3.Zero, Quat.Identity, Vec3.One);

    /// <summary>Applies scale (component-wise), then rotation, then translation.</summary>
    public Vec3 TransformPoint(Vec3 p) =>
        Translation + Rotation.Rotate(new Vec3(Scale.X * p.X, Scale.Y * p.Y, Scale.Z * p.Z));

    /// <summary>
    /// Composes parent and child. Exact (equals sequential TransformPoint) when the parent scale is uniform.
    /// For non-uniform parent scale the standard TRS approximation is used and shear is discarded.
    /// </summary>
    public static Transform operator *(Transform parent, Transform child) => new(
        parent.TransformPoint(child.Translation),
        parent.Rotation * child.Rotation,
        new Vec3(parent.Scale.X * child.Scale.X, parent.Scale.Y * child.Scale.Y, parent.Scale.Z * child.Scale.Z));

    /// <summary>Inverse transform. Exact for uniform scale; for non-uniform scale with rotation it is only a TRS approximation (shear is not representable).</summary>
    public Transform Inverse()
    {
        var si = new Vec3(1.0 / Scale.X, 1.0 / Scale.Y, 1.0 / Scale.Z);
        Quat ri = Rotation.Inverse();
        Vec3 t = ri.Rotate(new Vec3(si.X * Translation.X, si.Y * Translation.Y, si.Z * Translation.Z));
        return new Transform(-t, ri, si);
    }

    /// <summary>Lerps translation and scale, slerps rotation.</summary>
    public static Transform Lerp(Transform a, Transform b, double t) => new(
        Vec3.Lerp(a.Translation, b.Translation, t),
        Quat.Slerp(a.Rotation, b.Rotation, t),
        Vec3.Lerp(a.Scale, b.Scale, t));
}
