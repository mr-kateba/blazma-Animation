using Blazma.Combat;
using Blazma.Core.Mathematics;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Tests.Advanced;

namespace Blazma.Tests.Combat;

/// <summary>Collision math of the combat tools: every shape pair, normals, separating axes and the swept (tunnelling) case.</summary>
public class CollisionTests
{
    static bool Hit(WorldShape a, WorldShape b) => Collision.Test(a, b, out _);

    [Fact]
    public void SphereSphere_TouchAndMiss_NormalPointsFromSecondToFirst()
    {
        Assert.True(Collision.Test(WorldShape.Sphere(new Vec3(1.9, 0, 0), 1), WorldShape.Sphere(Vec3.Zero, 1), out var c));
        Assert.True(c.Normal.ApproxEquals(Vec3.UnitX, 1e-12));
        Assert.Equal(0.1, c.Depth, 12);
        Assert.True(c.Point.ApproxEquals(new Vec3(0.95, 0, 0), 1e-12));
        Assert.False(Hit(WorldShape.Sphere(new Vec3(2.01, 0, 0), 1), WorldShape.Sphere(Vec3.Zero, 1)));
    }

    [Fact]
    public void SphereCapsule_AndCapsuleCapsule_UseSegmentDistances()
    {
        var cap = WorldShape.Capsule(new Vec3(0, -1, 0), new Vec3(0, 1, 0), 0.2);
        Assert.True(Hit(WorldShape.Sphere(new Vec3(0.29, 0.5, 0), 0.1), cap));
        Assert.False(Hit(WorldShape.Sphere(new Vec3(0.31, 0.5, 0), 0.1), cap));
        Assert.True(Hit(WorldShape.Sphere(new Vec3(0, 1.29, 0), 0.1), cap));   // end cap
        Assert.False(Hit(WorldShape.Sphere(new Vec3(0, 1.31, 0), 0.1), cap));
        // Crossing capsules (skew lines 0.35 apart).
        Assert.True(Hit(WorldShape.Capsule(new Vec3(-1, 0, 0.35), new Vec3(1, 0, 0.35), 0.16), cap));
        Assert.False(Hit(WorldShape.Capsule(new Vec3(-1, 0, 0.37), new Vec3(1, 0, 0.37), 0.16), cap));
        // Parallel capsules and degenerate (zero-length) segments.
        Assert.True(Hit(WorldShape.Capsule(new Vec3(0.39, -5, 0), new Vec3(0.39, 5, 0), 0.2), cap));
        Assert.False(Hit(WorldShape.Capsule(new Vec3(0.41, -5, 0), new Vec3(0.41, 5, 0), 0.2), cap));
        Collision.ClosestPtSegmentSegment(Vec3.Zero, Vec3.Zero, new Vec3(1, -1, 0), new Vec3(1, 1, 0), out var p, out var q);
        Assert.True(p.ApproxEquals(Vec3.Zero) && q.ApproxEquals(new Vec3(1, 0, 0)));
    }

    [Fact]
    public void Box_AgainstSphereAndCapsule_OrientedAndInside()
    {
        var box = WorldShape.Box(Vec3.Zero, Quat.FromAxisAngle(Vec3.UnitZ, Math.PI / 4), new Vec3(1, 0.1, 0.5));
        // Along the box's rotated X axis the box reaches 1; perpendicular (its Y) only 0.1.
        var alongX = Quat.FromAxisAngle(Vec3.UnitZ, Math.PI / 4).Rotate(Vec3.UnitX);
        var alongY = Quat.FromAxisAngle(Vec3.UnitZ, Math.PI / 4).Rotate(Vec3.UnitY);
        Assert.True(Hit(WorldShape.Sphere(alongX * 1.15, 0.2), box));
        Assert.False(Hit(WorldShape.Sphere(alongX * 1.25, 0.2), box));
        Assert.True(Hit(WorldShape.Sphere(alongY * 0.29, 0.2), box));
        Assert.False(Hit(WorldShape.Sphere(alongY * 0.31, 0.2), box));
        // A sphere centre inside the box: pushed out through the nearest face (its +Y face), depth = r + distance to it.
        Assert.True(Collision.Test(WorldShape.Sphere(alongY * 0.05, 0.02), box, out var c));
        Assert.True(c.Normal.ApproxEquals(alongY, 1e-9));
        Assert.Equal(0.07, c.Depth, 9);
        // A capsule crossing near the box's end: only its middle is close (golden-section search on the segment).
        Assert.True(Hit(WorldShape.Capsule(alongX * 1.1 + Vec3.UnitZ * -2, alongX * 1.1 + Vec3.UnitZ * 2, 0.11), box));
        Assert.False(Hit(WorldShape.Capsule(alongX * 1.1 + Vec3.UnitZ * -2, alongX * 1.1 + Vec3.UnitZ * 2, 0.09), box));
        // Order does not matter; the normal flips.
        Assert.True(Collision.Test(box, WorldShape.Sphere(alongX * 1.15, 0.2), out var c2));
        Assert.True(Vec3.Dot(c2.Normal, alongX) < 0);
    }

    [Fact]
    public void BoxBox_SeparatingAxes_CatchRotatedBoxes()
    {
        var a = WorldShape.Box(Vec3.Zero, Quat.Identity, new Vec3(1, 1, 1));
        var rot = Quat.FromAxisAngle(Vec3.UnitZ, Math.PI / 4);
        // The rotated box reaches √2 along X: at 2.3 it overlaps, at 2.5 it does not (its bounding box would still overlap).
        Assert.True(Collision.Test(WorldShape.Box(new Vec3(2.3, 0, 0), rot, new Vec3(1, 1, 1)), a, out var c));
        Assert.True(c.Normal.ApproxEquals(Vec3.UnitX, 1e-9));
        Assert.Equal(1 + Math.Sqrt(2) - 2.3, c.Depth, 9);
        Assert.False(Hit(WorldShape.Box(new Vec3(2.5, 0, 0), rot, new Vec3(1, 1, 1)), a));
        // Edge–edge axis: two boxes rotated about different axes, separated only along a cross product.
        var b = WorldShape.Box(new Vec3(1.9, 1.9, 0), Quat.FromAxisAngle(Vec3.UnitX, Math.PI / 4), new Vec3(1, 1, 1));
        Assert.Equal(Hit(b, a), Hit(a, b));
    }

    [Fact]
    public void NonFiniteShapes_NeverCollide()
    {
        Assert.False(Hit(WorldShape.Sphere(new Vec3(double.NaN, 0, 0), 1), WorldShape.Sphere(Vec3.Zero, 1)));
        Assert.False(Hit(WorldShape.Sphere(Vec3.Zero, double.PositiveInfinity), WorldShape.Sphere(Vec3.Zero, 1)));
    }

    /// <summary>A small sphere crosses a 2 cm plate between two frames: instant tests at the frames miss; the sweep catches it.</summary>
    [Fact]
    public void FastSwing_DoesNotTunnel_SweptTestFindsTheContact()
    {
        var (s, man, _) = P8.Scene(cesium: false);
        var p = s.Project;
        var rig = P8.RigOf(s, man);
        var clip = new Clip { Name = "Fast", Fps = FrameRate.Fps30, Duration = Flicks.FromFrame(3, FrameRate.Fps30), Skeleton = rig.Id };
        var root = rig.Bones[0];
        clip.Tracks.Add(new Track
        {
            Target = new TrackTarget { Kind = TargetKind.Bone, Id = root.Id }, Channel = TrackChannels.Translation,
            Keys =
            {
                new Keyframe { Time = Flicks.Zero, Value = new[] { 0.0, 0, -2 }, Interp = Interpolation.Linear },
                new Keyframe { Time = Flicks.FromFrame(1, clip.Fps), Value = new[] { 0.0, 0, 2.1 }, Interp = Interpolation.Linear },
            },
        });
        clip.Combat = new ClipCombat
        {
            Hitboxes =
            {
                new Hitbox
                {
                    Name = "Ball", Shape = new CollisionShape { Kind = CollisionShapeKind.Sphere, Radius = 0.02 },
                    Attachment = new HitboxAttachment { Kind = HitboxAttachKind.Bone, Target = root.Id, Offset = new Transform(new Vec3(0, 0.95, 0), Quat.Identity, Vec3.One) },
                    Active = { new ClipRange { Start = Flicks.Zero, End = Flicks.FromFrame(3, clip.Fps) } },
                },
            },
        };
        p.Clips.Add(clip);
        var defender = Blazma.Rigging.MannequinFactory.CreateCharacter(rig, "Plate");
        var pelvis = rig.FindByStandardName("pelvis")!;
        defender.Combat = new CharacterCombat
        {
            Hurtboxes = { new Hurtbox { Name = "plate", Bone = pelvis.Id, Shape = new CollisionShape { Kind = CollisionShapeKind.Box, Size = new Vec3(1, 1, 0.02) } } },
        };
        p.Characters.Add(defender);
        var placement = CombatEvaluator.Placement(Vec3.Zero, 0);
        var attacker = P8.Character(s, man);

        // Instant tests at the frames (and even at 4 sub-steps) never overlap: -2, -0.975, 0.05, 1.075 and 2.1 m from the plate.
        foreach (var f in new[] { 0.0, 0.25, 0.5, 0.75, 1, 2 })
        {
            var t = CombatEvaluator.TimeOf(f, clip.Fps);
            var hb = CombatEvaluator.HitboxShape(p, clip.Combat.Hitboxes[0], clip, rig, attacker.Id, CombatEvaluator.Model(rig, attacker, clip, t), t, Transform.Identity)!.Value;
            var hu = CombatEvaluator.HurtboxShape(rig, defender.Combat.Hurtboxes[0], CombatEvaluator.Model(rig, defender, null, t), placement)!.Value;
            Assert.False(Collision.Test(hb, hu, out _), $"frame {f}");
        }
        foreach (var sub in new[] { 1, 4 })
        {
            var report = HitDetector.Run(p, attacker, clip, defender, null, new HitTestOptions { DefenderPlacement = placement, MaxSubsteps = sub });
            var hit = Assert.Single(report.Hits);
            Assert.True(hit.Swept);
            Assert.Equal(1, hit.Frame);
            Assert.Equal("pelvis", hit.BoneName);
            Assert.True(hit.Direction.ApproxEquals(Vec3.UnitZ, 1e-6), hit.Direction.ToString());
            Assert.Equal(HitDirection.Back, hit.From); // the plate faces +Z, the ball comes from −Z (behind it)
        }
    }
}
