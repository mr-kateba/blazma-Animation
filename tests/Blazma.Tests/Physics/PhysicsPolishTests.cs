using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Blazma.Commands;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Physics;
using Blazma.Rigging;
using Xunit.Abstractions;

namespace Blazma.Tests.Physics;

/// <summary>
/// Physics polish (motion.physics_polish), measured with <see cref="MotionPhysicsMetrics"/> before and after on the saved Kimodo
/// hero clip, the procedural actions, a planned multi-step clip and a baked walk → run sequence blend.
/// </summary>
[Collection(PhysicsPolishCollection.Name)]
public class PhysicsPolishTests(ITestOutputHelper output)
{
    const double MaxSlide = 0.01, MaxPenetration = -0.005;

    PhysicsPolishResult Polish(string name, Clip clip, Rig rig)
    {
        var r = PhysicsPolish.Run(clip, rig);
        output.WriteLine($"== {name} [{string.Join(",", r.Stages)}]\nbefore\n{r.Before}\nafter\n{r.After}");
        return r;
    }

    [Theory]
    [InlineData("walk", 3.0)]
    [InlineData("run", 3.0)]
    [InlineData("jump", 2.0)]
    [InlineData("fall", 2.5)]
    [InlineData("die", 2.5)]
    [InlineData("punch", 1.5)]
    public void ProceduralActions_AfterPolish_ArePlantedAboveGroundAndBallistic(string prompt, double seconds)
    {
        var r = Polish(prompt, PhysicsScenarios.Procedural(prompt, seconds), PhysicsScenarios.Mannequin);
        var a = r.After;
        Assert.True(a.FootSlide < MaxSlide, $"slide {a.FootSlide}");
        Assert.True(a.MinBodyHeight > MaxPenetration, $"lowest {a.MinBodyHeight} {a.MinBodyAt}");
        Assert.True(a.FlightParabolaRms < 0.02, $"parabola {a.FlightParabolaRms}");
        Assert.All(a.LandingKneeFlex, k => Assert.True(k >= 15, $"landing knee {k}"));
        // Polishing does not make the motion rougher (follow-through and planting add at most a little).
        Assert.True(a.ComJerkRms <= 1.2 * r.Before.ComJerkRms + 5, $"jerk {r.Before.ComJerkRms} → {a.ComJerkRms}");
        if (prompt is "fall" or "die")
        {
            Assert.True(a.EndsLying);
            Assert.InRange(a.RestGap, MaxPenetration, 0.02);
            Assert.Contains("pelvis", a.RestContacts);
            Assert.True(a.FallImpactRatio >= 0.8, $"impact ratio {a.FallImpactRatio}");
        }
        if (prompt == "jump") Assert.Single(a.Flights);
    }

    [Fact]
    public void ProceduralJump_IsBallistic_AndTakesOffWithoutAccelerationSpikes()
    {
        var m = MotionPhysicsMetrics.Measure(PhysicsScenarios.Procedural("jump", 2), PhysicsScenarios.Mannequin);
        Assert.Single(m.Flights);
        Assert.True(m.FlightParabolaRms < 0.01, $"raw jump parabola {m.FlightParabolaRms}"); // was 3.5 cm
        Assert.True(m.ComAccelMax < 40, $"COM accel {m.ComAccelMax}");                     // was 144 m/s²
        Assert.True(m.LandingKneeFlex[0] >= 15);
    }

    [Fact]
    public void HeroKimodoClip_NothingBelowGround_RestsOnFloor_LessSkating()
    {
        var (clip, rig) = PhysicsScenarios.Hero();
        var r = Polish("hero", clip, rig);
        Assert.True(r.Before.MinBodyHeight < -0.1, "the saved clip sinks into the floor"); // −162 mm
        Assert.True(r.After.MinBodyHeight > MaxPenetration, $"lowest {r.After.MinBodyHeight} {r.After.MinBodyAt}");
        Assert.True(r.After.EndsLying);
        Assert.InRange(r.After.RestGap, MaxPenetration, 0.02);
        Assert.True(r.After.FootSlide < r.Before.FootSlide / 4, $"slide {r.Before.FootSlide} → {r.After.FootSlide}");
        Assert.True(r.After.BoundaryVelocityJump < r.Before.BoundaryVelocityJump * 0.75, $"Δv {r.Before.BoundaryVelocityJump} → {r.After.BoundaryVelocityJump}");
        Assert.True(r.After.StandingMarginMin >= 0.02, $"margin {r.After.StandingMarginMin}");
        Assert.True(r.After.ComJerkRms <= 1.1 * r.Before.ComJerkRms, $"jerk {r.Before.ComJerkRms} → {r.After.ComJerkRms}");
    }

    [Fact]
    public void PlannedGeneration_PolishedByDefault_SmoothTransitions_PlantedFeet()
    {
        const string prompt = "walk then run then jump then fall";
        var (raw, rig) = PhysicsScenarios.Planned(prompt, 8, false);
        var (polished, rig2) = PhysicsScenarios.Planned(prompt, 8, true);
        var b = MotionPhysicsMetrics.Measure(raw, rig);
        var a = MotionPhysicsMetrics.Measure(polished, rig2);
        output.WriteLine($"before\n{b}\nafter\n{a}");
        Assert.Contains("ground_contact", polished.Metadata["physics.polish"]);
        Assert.False(raw.Metadata.ContainsKey("physics.polish"));
        Assert.True(a.FootSlide < MaxSlide, $"slide {b.FootSlide} → {a.FootSlide}");
        Assert.True(a.MinBodyHeight > MaxPenetration, $"lowest {a.MinBodyHeight}");
        Assert.True(a.FlightParabolaRms < 0.02);
        // Step transitions: the root's velocity changes by less than 1 m/s from one frame to the next (was 2.3 m/s).
        Assert.True(a.BoundaryVelocityJump < 1.0 && a.BoundaryVelocityJump < 0.6 * b.BoundaryVelocityJump, $"Δv {b.BoundaryVelocityJump} → {a.BoundaryVelocityJump}");
        Assert.True(a.ComJerkRms < b.ComJerkRms, $"jerk {b.ComJerkRms} → {a.ComJerkRms}");
        Assert.True(a.EndsLying);
        Assert.InRange(a.RestGap, MaxPenetration, 0.02);
    }

    [Fact]
    public void SequenceBlend_Polish_EasesTheRootAcrossTheBlend()
    {
        var (clip, rig) = PhysicsScenarios.SequenceBlend();
        var r = Polish("sequence", clip, rig);
        Assert.True(r.After.FootSlide < MaxSlide);
        Assert.True(r.After.MinBodyHeight > MaxPenetration);
        Assert.True(r.After.BoundaryVelocityJump < 0.5 * r.Before.BoundaryVelocityJump, $"Δv {r.Before.BoundaryVelocityJump} → {r.After.BoundaryVelocityJump}");
        Assert.True(r.After.ComJerkRms < r.Before.ComJerkRms);
    }

    [Fact]
    public void Polish_IsDeterministic_AndKeepsUntouchedBonesExactly()
    {
        var clip = PhysicsScenarios.Procedural("jump", 2);
        var rig = PhysicsScenarios.Mannequin;
        var a = PhysicsPolish.Apply(clip, rig, null, false, out var ra);
        var b = PhysicsPolish.Apply(clip, rig, null, false, out _);
        Assert.Equal(P(a), P(b));
        var changed = ra.ChangedBones.Select(i => rig.Bones[i].Id).ToHashSet();
        foreach (var t in clip.Tracks.Where(t => !changed.Contains(t.Target.Id)))
            Assert.Equal(P1(t), P1(a.Tracks.Single(x => x.Id == t.Id)));
        // Disabling every stage changes nothing.
        var none = PhysicsPolish.Run(clip, rig, new PhysicsPolishOptions
        {
            GroundContact = false, GroundClamp = false, Ballistic = false, Transitions = false, Falls = false, Secondary = false, Balance = false,
        });
        Assert.Equal(0, none.ChangedFrames);
    }

    static string P(Clip c) => string.Join("|", c.Tracks.Select(P1));
    static string P1(Track t) => t.Id + ":" + string.Join(";", t.Keys.Select(k => string.Join(",", k.Value.Select(v => v.ToString("R", CultureInfo.InvariantCulture)))));

    [Fact]
    public void Loops_KeepTheirSeam()
    {
        var clip = PhysicsScenarios.Procedural("walk", 2);
        var rig = PhysicsScenarios.Mannequin;
        var r = PhysicsPolish.Run(clip, rig, new PhysicsPolishOptions { Cyclic = true });
        var ev = new RigEvaluator(rig);
        var orig = MotionPhysicsMetrics.Locals(clip, rig, ev);
        foreach (var f in new[] { 0, orig.Length - 1 })
            for (var b = 0; b < ev.Count; b++) Assert.False(PhysicsPolish.Differs(orig[f][b], r.Locals[f][b]), $"frame {f} bone {rig.Bones[b].Name}");
    }

    [Fact]
    public void Command_PhysicsPolish_ReportsAndUndoes()
    {
        var reg = CommandRegistry.CreateDefault();
        var p = Project.CreateNew("Polish");
        var ch = MannequinFactory.AddTo(p);
        var s = new ProjectSession(p);
        var clip = PhysicsScenarios.Procedural("jump", 2, rig: p.FindRig(ch.Rig));
        p.Clips.Add(clip);
        var before = P(clip);
        var r = PhysicsScenarios.Ok(reg, s, "motion.physics_polish", new { clipId = clip.Id.ToString(), characterId = ch.Id.ToString() }).Data!;
        Assert.Contains("ballistic", r["stages"]!.AsArray().Select(x => (string?)x));
        Assert.True((double)r["after"]!["flightParabolaRms"]! < 0.02);
        Assert.True((int)r["changedFrames"]! > 0);
        Assert.NotEqual(before, P(p.Clips.Single(c => c.Id == clip.Id)));
        s.Undo(CommandOrigin.Test);
        Assert.Equal(before, P(p.Clips.Single(c => c.Id == clip.Id)));
    }

    [Fact]
    public void Template_PolishedByDefault_OptOut()
    {
        var reg = CommandRegistry.CreateDefault();
        var p = Project.CreateNew("T");
        var ch = MannequinFactory.AddTo(p);
        var s = new ProjectSession(p);
        var on = (string)PhysicsScenarios.Ok(reg, s, "template.apply", new { templateId = "builtin.jump", characterId = ch.Id.ToString() }).Data!["clipId"]!;
        var off = (string)PhysicsScenarios.Ok(reg, s, "template.apply", new { templateId = "builtin.jump", characterId = ch.Id.ToString(), physicsPolish = false }).Data!["clipId"]!;
        Assert.True(p.Clips.Single(c => c.Id == ObjectId.Parse(on)).Metadata.ContainsKey("physics.polish"));
        Assert.False(p.Clips.Single(c => c.Id == ObjectId.Parse(off)).Metadata.ContainsKey("physics.polish"));
    }

    /// <summary>Writes the before/after curves for docs/screenshots/physics-before-after.png (set BLAZMA_PHYSICS_PLOT to an output JSON path).</summary>
    [Fact]
    public void ExportPlotData()
    {
        var path = Environment.GetEnvironmentVariable("BLAZMA_PHYSICS_PLOT");
        if (string.IsNullOrEmpty(path)) return;
        var root = new JsonObject();
        void Add(string name, Clip clip, Rig rig)
        {
            var body = BodyGeometry.For(rig);
            var res = PhysicsPolish.Run(clip, rig);
            var before = MotionPhysicsMetrics.Locals(clip, rig, body.Evaluator);
            JsonObject Series(Blazma.Core.Mathematics.Transform[][] l) => new()
            {
                ["com"] = new JsonArray(l.Select(x => (JsonNode?)Math.Round(body.Mass.CenterOfMass(body.Evaluator.Model(x)).Y - body.Ground, 4)).ToArray()),
                ["lowest"] = new JsonArray(l.Select(x => (JsonNode?)Math.Round(body.Lowest(body.Evaluator.Model(x)).Height, 4)).ToArray()),
            };
            root[name] = new JsonObject
            {
                ["fps"] = clip.Fps.FramesPerSecond, ["before"] = Series(before), ["after"] = Series(res.Locals),
                ["metrics"] = new JsonObject { ["before"] = PhysicsPolishCommands.Report(res.Before), ["after"] = PhysicsPolishCommands.Report(res.After) },
            };
        }
        var (hero, heroRig) = PhysicsScenarios.Hero();
        Add("hero", hero, heroRig);
        var (planned, rig) = PhysicsScenarios.Planned("walk then run then jump then fall", 8, false);
        Add("planned", planned, rig);
        foreach (var (n, sec) in new[] { ("walk", 3.0), ("run", 3.0), ("jump", 2.0), ("fall", 2.5) })
            Add(n, PhysicsScenarios.Procedural(n, sec), PhysicsScenarios.Mannequin);
        var (seq, seqRig) = PhysicsScenarios.SequenceBlend();
        Add("sequence", seq, seqRig);
        File.WriteAllText(path, root.ToJsonString());
    }
}

/// <summary>The polish tests are CPU-heavy (whole clips, every frame): they run on their own, after the parallel tests, so they do
/// not skew the wall-clock checks of other tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PhysicsPolishCollection
{
    public const string Name = "Physics polish (serial)";
}
