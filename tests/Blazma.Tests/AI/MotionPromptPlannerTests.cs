using Blazma.AI;

namespace Blazma.Tests.AI;

public class MotionPromptPlannerTests
{
    static string[] Actions(MotionPlan p) => p.Segments.Select(s => s.Action ?? "?").ToArray();

    [Fact]
    public void UsersSentence_IsLookBack_Run_Die_InOrder()
    {
        var p = MotionPromptPlanner.Plan("في واحد وراك اجري وبعدها موت", 6);
        Assert.Equal(new[] { "look_back", "run", "die" }, Actions(p));
        Assert.Equal(new[] { "في واحد وراك", "اجري", "موت" }, p.Segments.Select(s => s.Text));
        Assert.Contains("looks back", p.Segments[0].Caption);
        Assert.Equal("a person runs forward", p.Segments[1].Caption);
        Assert.Equal("a person falls to the ground and dies", p.Segments[2].Caption);
        Assert.All(p.Segments, s => Assert.True(s.Understood));
        Assert.Equal(6, p.TotalSeconds, 2);
        Assert.Equal(6, p.Segments.Sum(s => s.DurationSeconds), 2);
        // Run gets the most time, the look back the least.
        Assert.True(p.Segments[1].DurationSeconds > p.Segments[2].DurationSeconds && p.Segments[2].DurationSeconds > p.Segments[0].DurationSeconds);
        Assert.Equal(new[] { "look back", "run", "die" }, p.Segments.Select(s => s.ProceduralPrompt));
    }

    [Theory]
    [InlineData("someone is behind you, run, and then die", "look_back,run,die")]
    [InlineData("walk forward then jump after that wave", "walk,jump,wave")]
    [InlineData("a person walks and waves", "walk,wave")]
    [InlineData("run; fall. stand up", "run,fall,stand_up")]
    [InlineData("turn around and run away, finally stop", "turn_around,run,stop")]
    [InlineData("يمشي ثم يجري ثمّ يقفز", "walk,run,jump")]
    [InlineData("امشي بعدين اقعد وبعد ذلك قوم", "walk,sit,stand_up")]
    [InlineData("يجري بسرعة ويطيح", "run,fall")]
    [InlineData("يلتفت، يركض، يموت", "look_back,run,die")]
    [InlineData("ينط وبعدين يرقص", "jump,dance")]
    [InlineData("يلوح بيده بعدها يمشي لقدام", "wave,walk")]
    [InlineData("walk ثم يجري", "walk,run")]
    [InlineData("بيجري وبعدين بيقع", "run,fall")]
    [InlineData("اركل ثم الكم ثم ارجع لورا", "kick,punch,step_back")]
    public void Splits_OnConnectors_Punctuation_And_JoinedActions(string prompt, string expected)
    {
        var p = MotionPromptPlanner.Plan(prompt, 8);
        Assert.Equal(expected.Split(','), Actions(p));
        Assert.Equal(8, p.Segments.Sum(s => s.DurationSeconds), 2);
    }

    [Theory]
    [InlineData("a person walks forward")]
    [InlineData("walk")]
    [InlineData("run fast")]
    [InlineData("wave hello")]
    [InlineData("do a backflip")]
    [InlineData("a person walks while waving")]
    [InlineData("يمشي وهو يلوح")]
    [InlineData("walk to the door, looking around")]
    [InlineData("fall down and die")]
    [InlineData("turn around and look back")]
    public void SingleSteps_StayOneSegment_WithTheWholeLength(string prompt)
    {
        var p = MotionPromptPlanner.Plan(prompt, 4);
        Assert.Single(p.Segments);
        Assert.Equal(4, p.Segments[0].DurationSeconds, 3);
    }

    [Fact]
    public void Durations_FromText_AreKept_TheRestIsShared()
    {
        var p = MotionPromptPlanner.Plan("walk for 3 seconds then run then jump", 8);
        Assert.Equal(3, p.Segments[0].DurationSeconds, 3);
        Assert.True(p.Segments[0].DurationExplicit);
        Assert.Equal("a person walks forward", p.Segments[0].Caption);
        Assert.Equal(5, p.Segments[1].DurationSeconds + p.Segments[2].DurationSeconds, 2);

        var ar = MotionPromptPlanner.Plan("امشي لمدة ٣ ثواني ثم اجري ثانيتين", 10);
        Assert.Equal(new[] { 3.0, 2.0 }, ar.Segments.Select(s => s.DurationSeconds));
        Assert.Equal(5, ar.TotalSeconds, 3);
        Assert.Contains(ar.Warnings, w => w.Contains("add up to"));

        var c = MotionPromptPlanner.Plan("jump for 2s, then run for 1.5 sec", 3.5);
        Assert.Equal(new[] { 2.0, 1.5 }, c.Segments.Select(s => s.DurationSeconds));

        // Slow takes longer than fast.
        var s = MotionPromptPlanner.Plan("walk slowly then walk fast", 6);
        Assert.Equal(new[] { "walk", "walk" }, Actions(s));
        Assert.True(s.Segments[0].DurationSeconds > s.Segments[1].DurationSeconds);
        Assert.Equal("a person walks forward slowly", s.Segments[0].Caption);
        Assert.Equal("a person walks forward quickly", s.Segments[1].Caption);
        Assert.Equal("a person walks forward slowly", MotionPromptPlanner.Plan("يمشي ببطء", 3).Segments[0].Caption);
        Assert.Equal("a person runs forward quickly", MotionPromptPlanner.Plan("اجري بسرعة", 3).Segments[0].Caption);

        // Too short a total: every step still gets the minimum.
        var tiny = MotionPromptPlanner.Plan("walk then run then jump then wave", 1);
        Assert.All(tiny.Segments, x => Assert.True(x.DurationSeconds >= MotionPromptPlanner.MinSegmentSeconds - 1e-9));
        Assert.NotEmpty(tiny.Warnings);
    }

    [Fact]
    public void Translation_Covers_TheDictionary()
    {
        var cases = new (string Text, string Action)[]
        {
            ("يمشي", "walk"), ("امشي", "walk"), ("يجري", "run"), ("اجري", "run"), ("يركض", "run"), ("ينط", "jump"), ("يقفز", "jump"), ("ينقز", "jump"),
            ("يقع", "fall"), ("يطيح", "fall"), ("يسقط", "fall"), ("يموت", "die"), ("موت", "die"), ("مات", "die"), ("يلتفت", "look_back"),
            ("يلف", "turn_around"), ("يستدير", "turn_around"), ("يطالع ورا", "look_back"), ("عاين وراك", "look_back"), ("ينحني", "crouch"), ("يقرفص", "crouch"),
            ("يقعد", "sit"), ("يجلس", "sit"), ("يقوم", "stand_up"), ("يركل", "kick"), ("شوت", "kick"), ("يضرب", "punch"), ("لكمة", "punch"),
            ("ركلة", "kick"), ("يلوح", "wave"), ("يلوّح", "wave"), ("يرقص", "dance"), ("وقوف", "idle"), ("وقف", "stop"), ("يرجع لورا", "step_back"),
            ("يتفادى", "dodge"), ("يتدحرج", "roll"), ("يتسلق", "climb"), ("يرمي", "throw"), ("يحدف", "throw"), ("يلقط", "pick_up"), ("يشيل", "pick_up"),
            ("walk", "walk"), ("run", "run"), ("sprint", "sprint"), ("jump", "jump"), ("fall", "fall"), ("die", "die"), ("turn around", "turn_around"),
            ("look back", "look_back"), ("crouch", "crouch"), ("sit down", "sit"), ("stand up", "stand_up"), ("kick", "kick"), ("punch", "punch"),
            ("wave", "wave"), ("dance", "dance"), ("idle", "idle"), ("stop", "stop"), ("step back", "step_back"), ("dodge", "dodge"), ("roll", "roll"),
            ("climb", "climb"), ("throw", "throw"), ("pick up", "pick_up"),
        };
        foreach (var (text, action) in cases)
        {
            var t = MotionPromptPlanner.Translate(text);
            Assert.True(t.Action == action, $"{text}: {t.Action} (expected {action})");
            Assert.True(t.Understood, text);
            Assert.StartsWith("a person ", t.Caption);
        }
        Assert.All(MotionPromptPlanner.Actions, a => Assert.Contains(cases, c => c.Action == a));
        Assert.Equal("a person turns to the left", MotionPromptPlanner.Translate("يلف يسار").Caption);
        Assert.Equal("a person walks backward", MotionPromptPlanner.Translate("امشي لورا").Caption);
        Assert.Equal("a person jumps forward", MotionPromptPlanner.Translate("jump forward").Caption);
    }

    [Fact]
    public void Unknown_English_IsKeptAsWritten_UnknownArabic_IsFlagged()
    {
        var e = MotionPromptPlanner.Plan("do a backflip", 3).Segments[0];
        Assert.True(e.Understood);
        Assert.Null(e.Action);
        Assert.Equal("do a backflip", e.Caption);
        Assert.Equal("a person walks to the door", MotionPromptPlanner.Plan("a person walks to the door for 3 seconds", 3).Segments[0].Caption);

        var p = MotionPromptPlanner.Plan("يمشي ثم يتكلم في التلفون", 4);
        Assert.Equal(2, p.Segments.Count);
        Assert.True(p.Segments[0].Understood);
        Assert.False(p.Segments[1].Understood);
        Assert.Null(p.Segments[1].ProceduralPrompt);
        Assert.Contains("يتكلم", p.Segments[1].Unrecognized);
        Assert.False(p.AllUnderstood);
        Assert.Contains(p.Warnings, w => w.Contains("not understood"));
    }

    [Fact]
    public void ExplicitSegments_AreTranslated_AndShareTheRest()
    {
        var p = MotionPromptPlanner.FromSegments(new[]
        {
            new SegmentInput("اجري", 2), new SegmentInput("a person falls to the ground and dies"), new SegmentInput("يمشي", null, "a person limps forward"),
        }, 6);
        Assert.Equal(new[] { "run", "die", "walk" }, Actions(p));
        Assert.Equal(2, p.Segments[0].DurationSeconds, 3);
        Assert.Equal(6, p.TotalSeconds, 2);
        Assert.Equal("a person runs forward", p.Segments[0].Caption);
        Assert.Equal("a person falls to the ground and dies", p.Segments[1].Caption);
        Assert.Equal("a person limps forward", p.Segments[2].Caption);
    }
}
