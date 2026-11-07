using Blazma.AI;
using Blazma.Core.Ids;
using Blazma.Core.Model;
using Blazma.Core.Timing;
using Blazma.Rigging;

namespace Blazma.Tests.AI;

/// <summary>
/// Quantitative gait quality of the built-in walk/run/idle cycles on the mannequin (templates: procedural cycle → retarget →
/// foot planting → cubic key reduction), measured with <see cref="GaitMetrics"/> on every frame (4 samples per frame for
/// knees and accelerations).
/// </summary>
public class GaitQualityTests
{
    static readonly Rig Mannequin = MannequinFactory.Create();

    static Clip Template(string id, bool reduce = true)
    {
        if (reduce) return BuiltinTemplates.Apply(BuiltinTemplates.Find(id)!, Mannequin, FrameRate.Fps30, null, ObjectId.New(), id).Clip;
        var t = BuiltinTemplates.Find(id)!;
        var motion = new ProceduralMotionProvider().GenerateAsync(new MotionRequest(t.Prompt, t.DefaultSeconds, 30, 1, new MotionConstraints { Loop = true }),
            null, CancellationToken.None).GetAwaiter().GetResult();
        return MotionRetargeter.Retarget(motion, Mannequin, new RetargetOptions { FootCleanup = true, ReduceKeys = false, Cyclic = true }).Clip;
    }

    static readonly Lazy<GaitReport> Walk = new(() => GaitMetrics.Measure(Template("builtin.walk_cycle"), Mannequin));
    static readonly Lazy<GaitReport> Run = new(() => GaitMetrics.Measure(Template("builtin.run_cycle"), Mannequin));

    [Fact]
    public void Walk_PlantedFeet_DoNotSlide_OrSink()
    {
        var r = Walk.Value;
        Assert.True(r.MaxStanceSlide < 0.01, r.ToString());
        Assert.True(r.MaxPenetration < 0.005, r.ToString());
        Assert.True(r.Alternating, r.ToString());
        Assert.True(r.MinSupport >= 1, "walking always has a foot on the ground\n" + r);
    }

    [Fact]
    public void Run_PlantedFeet_DoNotSlide_OrSink_AndHasAFlightPhase()
    {
        var r = Run.Value;
        Assert.True(r.MaxStanceSlide < 0.01, r.ToString());
        Assert.True(r.MaxPenetration < 0.005, r.ToString());
        Assert.True(r.Alternating, r.ToString());
        Assert.Equal(0, r.MinSupport); // flight
    }

    [Fact]
    public void Walk_SpeedCadence_Bob_Sway_AndKnees_AreNatural()
    {
        var r = Walk.Value;
        Assert.InRange(r.Speed, 1.15, 1.45);           // ≈1.3 m/s
        Assert.InRange(r.Contacts, 0.85, 1.05);        // ≈1 gait cycle per second
        Assert.InRange(r.PelvisBob, 0.02, 0.04);       // 2–4 cm …
        Assert.Equal(2, r.PelvisBobPeaks);             // … twice per cycle
        Assert.InRange(r.PelvisSway, 0.02, 0.06);
        Assert.True(r.KneeMin > -1, "no hyperextension\n" + r);
        Assert.InRange(r.KneeLoading, 12, 26);         // loading response ≈15–20°
        Assert.InRange(r.KneeMax, 55, 70);             // swing ≈60°
        Assert.True(r.ArmLegCorrelation < -0.8, "arms swing opposite to the legs\n" + r);
    }

    [Fact]
    public void Run_SpeedAndKnees_AreNatural()
    {
        var r = Run.Value;
        Assert.InRange(r.Speed, 3.0, 4.0);
        Assert.True(r.KneeMin > -1, r.ToString());
        Assert.InRange(r.KneeMax, 80, 120);
        Assert.InRange(r.PelvisBob, 0.03, 0.08);
        Assert.Equal(2, r.PelvisBobPeaks);
        Assert.True(r.ArmLegCorrelation < -0.6, r.ToString());
    }

    [Theory]
    [InlineData("builtin.walk_cycle")]
    [InlineData("builtin.run_cycle")]
    [InlineData("builtin.idle_breathe")]
    public void Cycles_AreSmooth_LoopSeamlessly_AndUseCubicKeys(string id)
    {
        var clip = Template(id);
        var r = GaitMetrics.Measure(clip, Mannequin);
        // No pops: the old linear-key cycles reached 73 000–82 000 °/s² with max/RMS ≈ 27 (walk).
        Assert.True(r.MaxAngAccel < 65000, r.ToString());
        Assert.True(r.AccelSpikiness < 18, r.ToString());
        Assert.True(r.KneeMaxStep < 32, r.ToString());
        Assert.True(r.SeamPose < 0.05, r.ToString());
        Assert.True(r.SeamVelocity <= 1.05, "the seam is as smooth as any other frame\n" + r);
        Assert.True(r.MaxPenetration < 0.005, r.ToString());
        Assert.True(r.MaxStanceSlide < 0.01, r.ToString());
        Assert.All(clip.Tracks.Where(t => t.Channel != TrackChannels.Pinned).SelectMany(t => t.Keys), k => Assert.Equal(Interpolation.Cubic, k.Interp));
    }

    [Theory]
    [InlineData("builtin.walk_cycle")]
    [InlineData("builtin.run_cycle")]
    public void KeyReduction_StaysWithinHalfADegree_AndTwoMillimetres_OfTheDenseBake(string id)
    {
        var reduced = Template(id);
        var dense = Template(id, reduce: false);
        var (position, rotation) = GaitMetrics.Difference(dense, reduced, Mannequin);
        Assert.True(position < 0.002, $"{position * 1000:0.00} mm");
        Assert.True(rotation < 0.5, $"{rotation:0.000}°");
        Assert.True(reduced.Tracks.Sum(t => t.Keys.Count) < 0.8 * dense.Tracks.Sum(t => t.Keys.Count), "keys are reduced");
    }
}
