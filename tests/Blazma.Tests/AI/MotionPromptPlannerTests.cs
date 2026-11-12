using Blazma.AI;

namespace Blazma.Tests.AI;

public class MotionPromptPlannerTests
{
    static string[] Actions(MotionPlan p) => p.Segments.Select(s => s.Action ?? "?").ToArray();

    [Theory]
    [InlineData("يضرب لكمة قوية ثم يرجع لوضع الاستعداد", "punch,idle")]
    [InlineData("يضرب ثم يرجع لوضعه", "punch,idle")]
    [InlineData("يضرب ثم يستعد", "punch,idle")]
    [InlineData("punch then returns to a ready stance", "punch,idle")]
    [InlineData("kick then return to guard stance", "kick,idle")]
    [InlineData("يمشي ثم يرجع ورا", "walk,step_back")]
    public void ReturnToReadyStance_IsIdle_NotStepBack(string prompt, string expected)
    {
        var p = MotionPromptPlanner.Plan(prompt, 5);
        Assert.Equal(expected, string.Join(",", Actions(p)));
        Assert.All(p.Segments, s => Assert.True(s.Understood));
    }

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
            ("يركع", "kneel"), ("يزحف", "crawl"), ("يسبح", "swim"), ("يصفق", "clap"), ("يشير", "point"), ("يستلقي", "lie_down"), ("ينام", "sleep"), ("يصرخ", "shout"), ("يغني", "sing"),
            ("يبكي", "cry"), ("يضحك", "laugh"), ("يبتسم", "smile"), ("يفكر", "think"), ("يتلفت حواليه", "look_around"), ("ينظر", "look"), ("يتسلل", "sneak"), ("يعرج", "limp"), ("يترنح", "stagger"),
            ("يتلقى ضربة", "get_hit"), ("يتفاجأ", "startle"), ("يرتجف", "tremble"), ("وضع القتال", "fight_stance"), ("يصوب", "aim"), ("يطلق النار", "shoot"), ("يشرب", "drink"), ("ياكل", "eat"),
            ("يتثاءب", "yawn"), ("يتمطى", "stretch"), ("يومئ", "nod"), ("يرفض", "shake_head"), ("يهز كتفيه", "shrug"), ("يحضن", "hug"), ("يتصل", "phone_call"), ("يتكلم", "talk"), ("يصفع", "slap"),
            ("يطعن", "stab"), ("يضرب بالسيف", "slash"), ("يصد", "block"), ("يمسك", "catch"), ("يدفع", "push"), ("يسحب", "pull"), ("يحمل", "carry"), ("يحتفل", "celebrate"), ("يهتف", "cheer"),
            ("يؤدي التحية العسكرية", "salute"), ("ينحني احتراما", "bow"), ("headbutt", "headbutt"),
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

        var p = MotionPromptPlanner.Plan("يمشي ثم يطبخ الرز", 4);
        Assert.Equal(2, p.Segments.Count);
        Assert.True(p.Segments[0].Understood);
        Assert.False(p.Segments[1].Understood);
        Assert.Null(p.Segments[1].ProceduralPrompt);
        Assert.Contains("يطبخ", p.Segments[1].Unrecognized);
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

    // ---------------------------------------------------------------- Arabic understanding (dialects, typos, diacritics, numbers, negation, simultaneous actions)

    [Theory]
    // MSA
    [InlineData("يركض الرجل ثم يقفز", "run,jump")]
    [InlineData("يسير ببطء ثم يتوقف", "walk,stop")]
    [InlineData("سقط الرجل أرضًا", "fall")]
    [InlineData("مات الرجل", "die")]
    [InlineData("يستدير ثم ينظر إلى الخلف", "turn_around,look_back")]
    [InlineData("ينحني ثم ينهض", "crouch,stand_up")]
    [InlineData("يجلس على الكرسي ثم يقف", "sit,idle")]
    [InlineData("يستلقي على الأرض", "lie_down")]
    [InlineData("يزحف على بطنه", "crawl")]
    [InlineData("يتسلق الجدار", "climb")]
    [InlineData("يسبح في الماء", "swim")]
    [InlineData("يصفق بيديه ويهتف", "clap,cheer")]
    [InlineData("يلوّح بيده ثم ينحني احتراما", "wave,bow")]
    [InlineData("يؤدي التحية العسكرية", "salute")]
    [InlineData("يتثاءب ثم يتمطى", "yawn,stretch")]
    [InlineData("يشير بإصبعه إلى الباب", "point")]
    // Gulf / Saudi / Najdi / Kuwaiti / Emirati / Omani
    [InlineData("يطيح وبعدين يقوم", "fall,stand_up")]
    [InlineData("طاح على الأرض", "fall")]
    [InlineData("يشرد بسرعة", "run")]
    [InlineData("اشرد وبعدها طيح", "run,fall")]
    [InlineData("ينقز ثم يركض", "jump,run")]
    [InlineData("يقعد ثم يتمدد", "sit,lie_down")]
    [InlineData("يبرك على ركبته", "kneel")]
    [InlineData("يطاطي راسه ويختبي", "crouch")]
    [InlineData("يسولف ويضحك", "talk,laugh")]
    [InlineData("يبچي ويعيط", "cry")]
    [InlineData("يلبط الكرة برجله", "kick")]
    [InlineData("يضرب نار", "shoot")]
    [InlineData("يطخ العدو", "shoot")]
    [InlineData("يرفس ثم يلكم", "kick,punch")]
    [InlineData("يتخفى ويتسلل", "sneak")]
    [InlineData("يصقف ويرقص", "clap,dance")]
    [InlineData("يرقص العرضة", "dance")]
    [InlineData("يدز الباب", "push")]
    [InlineData("يشد الحبل", "pull")]
    [InlineData("يعب الماء", "drink")]
    [InlineData("يتلفن لصديقه", "phone_call")]
    [InlineData("يزعق بأعلى صوته", "shout")]
    // Iraqi
    [InlineData("يگعد ويكعد", "sit")]
    [InlineData("يطفر وبعدين يوكع", "jump,fall")]
    [InlineData("يهرب ثم يلحق", "run")]
    [InlineData("اكعد ثم اگوم", "sit,stand_up")]
    [InlineData("يحدف الحجر", "throw")]
    // Levantine
    [InlineData("بيمشي وبعدين بيوقع", "walk,fall")]
    [InlineData("عم يركض ثم يوقف", "run,stop")]
    [InlineData("بيتلفت ورا", "look_back")]
    [InlineData("بيدبك", "dance")]
    [InlineData("بيعيط وبيحضن", "cry,hug")]
    [InlineData("يشيل الصندوق", "pick_up")]
    [InlineData("يدفر الباب", "push")]
    [InlineData("يتفادى الضربة", "dodge")]
    // Egyptian
    [InlineData("بيجري وبعدين بيقع", "run,fall")]
    [InlineData("هيجري وبعد كده هيموت", "run,die")]
    [InlineData("ينط ثم يتشقلب", "jump,roll")]
    [InlineData("اتخض وبعدين جري", "startle,run")]
    [InlineData("يسقف ويهلل", "clap,cheer")]
    [InlineData("يوطي ويشيل حاجة", "crouch,pick_up")]
    [InlineData("يضحك وبعدين يعيط", "laugh,cry")]
    [InlineData("بيزق العربية", "push")]
    [InlineData("يخبط الباب", "punch")]
    [InlineData("يتكعبل ويقع", "fall")]
    // Sudanese
    [InlineData("يجري طوالي ويطيح", "run,fall")]
    [InlineData("الزول يمشي وبعدين يقعد", "walk,sit")]
    [InlineData("يتنطط شوية", "jump")]
    [InlineData("يفطس", "die")]
    // Maghrebi
    [InlineData("كيجري وكيطيح", "run,fall")]
    [InlineData("يهرب دغيا", "run")]
    [InlineData("يمشي وبعدها يتوقف", "walk,stop")]
    [InlineData("يطيّح ثم يقوم", "fall,stand_up")]
    // imperative / past / masdar / feminine / plural forms
    [InlineData("امشي", "walk")]
    [InlineData("اجري", "run")]
    [InlineData("قوم", "stand_up")]
    [InlineData("اقعد ثم قوم", "sit,stand_up")]
    [InlineData("مشى ثم ركض", "walk,run")]
    [InlineData("وقف ثم جلس", "stop,sit")]
    [InlineData("هرب ثم سقط", "run,fall")]
    [InlineData("رقصت ثم جلست", "dance,sit")]
    [InlineData("جري ثم سقوط", "run,fall")]
    [InlineData("ركض فسقوط فموت", "run,die")]
    [InlineData("يمشون ثم يجرون", "walk,run")]
    [InlineData("اركضي ثم اقفزي", "run,jump")]
    // connectors
    [InlineData("اول شي يمشي بعدين يجري وفي النهاية يقعد", "walk,run,sit")]
    [InlineData("أخيرا يموت", "die")]
    [InlineData("يمشي وبعد شوي يقف", "walk,idle")]
    [InlineData("يجري وبعدها على طول يطيح", "run,fall")]
    [InlineData("لما يوصل الباب يقف", "idle")]
    [InlineData("بعد ما يقعد يرقص", "sit,dance")]
    [InlineData("يمشي ثم فجأة يجري", "walk,run")]
    [InlineData("يمشي وفجأة يقفز", "walk,jump")]
    [InlineData("يمشي ومن بعدها يقفز", "walk,jump")]
    [InlineData("يمشي ثم يقفز وفي الختام يسجد", "walk,jump,kneel")]
    [InlineData("يمشي ثم يجري ثم يقفز ثم يطيح ثم يموت", "walk,run,jump,fall,die")]
    // typos, stretched letters, hamza/ta marbuta/ya forms, Persian letters
    [InlineData("يجرررري", "run")]
    [InlineData("يمشششي ببطء", "walk")]
    [InlineData("يقفززز", "jump")]
    [InlineData("يجرى ثم يقع", "run,fall")]
    [InlineData("يمشى", "walk")]
    [InlineData("يمشي وبعدين يجري", "walk,run")]
    [InlineData("يمسي", "walk")]
    [InlineData("يجري ثم يمووت", "run,die")]
    [InlineData("يركظ", "run")]
    [InlineData("يقفظ", "jump")]
    [InlineData("يطيج", "fall")]
    [InlineData("يرقاص", "dance")]
    [InlineData("يتسلّق", "climb")]
    [InlineData("يَمْشِي ثُمَّ يَجْرِي", "walk,run")]
    [InlineData("يـــمـــشـــي", "walk")]
    [InlineData("يضحک ثم يبکي", "laugh,cry")]
    [InlineData("اجرِ ثم اقفِز", "run,jump")]
    [InlineData("يتمشّى", "walk")]
    [InlineData("اقفز ٣ مرات", "jump")]
    [InlineData("آركض ثم أقفز", "run,jump")]
    // prefixes and suffixes
    [InlineData("ويمشي ويجري", "walk,run")]
    [InlineData("فيقفز", "jump")]
    [InlineData("سيجري ثم سيقع", "run,fall")]
    [InlineData("ليجري", "run")]
    [InlineData("وبيجري", "run")]
    [InlineData("يضربه ثم يركله", "punch,kick")]
    [InlineData("يضربها ثم يحضنها", "punch,hug")]
    [InlineData("تركض ثم تقفز", "run,jump")]
    [InlineData("يجريان ثم يقعان", "run,fall")]
    [InlineData("الركض ثم القفز", "run,jump")]
    // context clauses
    [InlineData("في واحد وراك، اجري، وبعدها موت", "look_back,run,die")]
    [InlineData("كأن أحدا خلفه ثم يجري", "look_back,run")]
    [InlineData("حد وراه يجري", "look_back,run")]
    // combat and poses
    [InlineData("يضرب لكمة قوية ثم يرجع لوضع الاستعداد", "punch,idle")]
    [InlineData("ياخذ وضع القتال ثم يلكم", "fight_stance,punch")]
    [InlineData("يصوب البندقية ثم يطلق النار", "aim,shoot")]
    [InlineData("يطعن بالسكين ثم يرجع", "stab,step_back")]
    [InlineData("يضرب بالسيف مرتين", "slash")]
    [InlineData("يصد الضربة ثم يتفادى", "block,dodge")]
    [InlineData("يتلقى لكمة ويقع", "get_hit,fall")]
    [InlineData("يصفع ويرجع خطوة", "slap,step_back")]
    [InlineData("يتراجع ثم يهاجم", "step_back,punch")]
    [InlineData("يرمي القنبلة ويطيح", "throw,fall")]
    [InlineData("يمسك الكرة ثم يرميها", "catch,throw")]
    [InlineData("يشيل الصندوق ويمشي", "pick_up,walk")]
    [InlineData("يحمل جريح ويجري", "carry,run")]
    // everyday
    [InlineData("يأكل ثم يشرب", "eat,drink")]
    [InlineData("يشرب قهوة ثم يتصل", "drink,phone_call")]
    [InlineData("ينام ثم يستيقظ", "sleep,stand_up")]
    [InlineData("يفكر ثم يبتسم", "think,smile")]
    [InlineData("يهز راسه موافقا", "nod")]
    [InlineData("يهز راسه رفضا", "shake_head")]
    [InlineData("يهز كتفيه", "shrug")]
    [InlineData("يحضن صديقه", "hug")]
    [InlineData("يتلفت حواليه", "look_around")]
    [InlineData("يبحث عن شي", "look_around")]
    [InlineData("ينظر لفوق", "look")]
    [InlineData("يطالع يمين وشمال", "look_around")]
    [InlineData("يتكلم في التلفون", "phone_call")]
    [InlineData("يرد على الجوال ويمشي", "phone_call,walk")]
    [InlineData("يغني ويرقص", "sing,dance")]
    [InlineData("يحتفل بالفوز", "celebrate")]
    [InlineData("يرتجف من البرد", "tremble")]
    [InlineData("يتفاجأ ثم يرجع للخلف", "startle,step_back")]
    [InlineData("يصرخ ثم يجري", "shout,run")]
    // gait manners are actions of their own
    [InlineData("يعرج", "limp")]
    [InlineData("يترنح ثم يطيح", "stagger,fall")]
    [InlineData("يتسلل ببطء", "sneak")]
    [InlineData("يمشي مترنح", "stagger")]
    // English still works
    [InlineData("walks forward, jumps twice, then kneels", "walk,jump,kneel")]
    [InlineData("a person sneaks then crawls", "sneak,crawl")]
    [InlineData("he drinks water and then falls asleep", "drink,sleep")]
    [InlineData("claps and cheers", "clap,cheer")]
    [InlineData("aim a gun, fire", "aim,shoot")]
    public void Phrases_MapToActions(string prompt, string expected)
    {
        var p = MotionPromptPlanner.Plan(prompt, 8);
        var got = Actions(p);
        // The expectation lists the distinct actions in order; steps of the same action are collapsed.
        var collapsed = got.Where((a, i) => i == 0 || a != got[i - 1]).ToArray();
        Assert.True(collapsed.SequenceEqual(expected.Split(',')), $"{prompt}: expected [{expected}] got [{string.Join(",", got)}]");
    }

    [Theory]
    [InlineData("يَمْشِي", "يمشي")]
    [InlineData("يــــمــــشــــي", "يمشي")]
    [InlineData("أَجْرِي", "اجري")]
    [InlineData("إِلَى", "الي")]
    [InlineData("آمن", "امن")]
    [InlineData("ٱقعد", "اقعد")]
    [InlineData("مشى", "مشي")]
    [InlineData("مدرسة", "مدرسه")]
    [InlineData("يجرررري", "يجري")]
    [InlineData("جدااااا", "جدا")]
    [InlineData("يضحک", "يضحك")]
    [InlineData("می‌روم", "ميروم")]
    [InlineData("پاک", "باك")]
    [InlineData("٣٢١", "321")]
    [InlineData("۴۵۶", "456")]
    [InlineData("YES", "yes")]
    [InlineData("شيء", "شي")]
    public void Normalize_Rules(string input, string expected) => Assert.Equal(expected, MotionPromptPlanner.Normalize(input));

    [Fact]
    public void Typos_AreRepaired_WithConfidence_AndSuggestions()
    {
        var t = MotionPromptPlanner.Translate("يركظ");
        Assert.Equal("run", t.Action);
        var s = Assert.Single(t.Suggestions);
        Assert.True(s.Applied);
        Assert.Equal("يركظ", s.Word);
        Assert.Equal("يركض", s.Candidates[0]);
        Assert.Equal("هل تقصد: يركض؟", s.Message);
        Assert.InRange(t.Confidence, 0.5, 0.99);
        Assert.Contains(MotionPromptPlanner.Plan("يركظ", 3).Segments[0].Suggestions, x => x.Applied);

        // Exact words carry full confidence and no suggestions.
        var exact = MotionPromptPlanner.Translate("يركض");
        Assert.Equal(1, exact.Confidence);
        Assert.Empty(exact.Suggestions);

        // English typos are repaired too.
        var e = MotionPromptPlanner.Translate("runing");
        Assert.Equal("run", e.Action);
        Assert.True(e.Confidence < 1);

        // A nearly-known word that is NOT repaired still produces a hint.
        var p = MotionPromptPlanner.Plan("يمشي ثم يطبخ الرز", 4);
        Assert.False(p.Segments[1].Understood);
        Assert.Equal(0, p.Segments[1].Confidence);
        Assert.Contains("يطبخ", p.Segments[1].Unrecognized);
        Assert.DoesNotContain(p.Segments[0].Suggestions, x => !x.Applied);
        Assert.True(p.Confidence <= p.Segments[1].Confidence);
    }

    [Fact]
    public void Confidence_DropsWithUnknownWords_AndPassThrough()
    {
        var full = MotionPromptPlanner.Translate("يمشي بسرعة");
        var partial = MotionPromptPlanner.Translate("يمشي مثل الدب الابيض");
        Assert.Equal(1, full.Confidence);
        Assert.True(partial.Understood);
        Assert.InRange(partial.Confidence, 0.4, 0.95);
        Assert.NotEmpty(partial.Unrecognized);
        var english = MotionPromptPlanner.Plan("do a backflip", 3).Segments[0];
        Assert.InRange(english.Confidence, 0.1, 0.7);
    }

    [Theory]
    [InlineData("يمشي ببطء", "a person walks forward slowly")]
    [InlineData("يمشي بسرعة", "a person walks forward quickly")]
    [InlineData("اجري لورا", "a person runs backward")]
    [InlineData("امشي لليمين", "a person walks to the right")]
    [InlineData("يلف يسار", "a person turns to the left")]
    [InlineData("يمشي كأنه خايف", "a person walks forward fearfully")]
    [InlineData("يمشي مثل الزومبي", "a person walks forward like a zombie, with stiff arms stretched out")]
    [InlineData("يمشي مثل العجوز", "a person walks forward like an old person, hunched over")]
    [InlineData("يمشي سكران", "a person walks forward unsteadily, like a drunk person")]
    [InlineData("يمشي متعب", "a person walks forward wearily, as if exhausted")]
    [InlineData("يجري وهو غاضب", "a person runs forward angrily")]
    [InlineData("يمشي فرحان", "a person walks forward happily")]
    [InlineData("يمشي بحذر", "a person walks forward carefully")]
    [InlineData("يمشي بثقة", "a person walks forward confidently")]
    [InlineData("يمشي حزين", "a person walks forward sadly, with drooping shoulders")]
    [InlineData("فجأة يطيح", "a person suddenly falls to the ground")]
    [InlineData("يقفز مرتين", "a person jumps up twice")]
    [InlineData("يقفز ثلاث مرات", "a person jumps up three times")]
    [InlineData("يقفز ٣ مرات", "a person jumps up three times")]
    [InlineData("امشي ٣ خطوات", "a person takes three steps forward")]
    [InlineData("امشي خطوتين لقدام", "a person takes two steps forward")]
    [InlineData("يرجع ٤ خطوات", "a person takes four steps backward")]
    [InlineData("يلكم بيده اليسار", "a person punches forward with the left hand")]
    [InlineData("يضرب بيده اليمين", "a person punches forward with the right hand")]
    [InlineData("يركل برجله اليسار", "a person kicks forward with the left leg")]
    [InlineData("يلوح بيده اليسار", "a person waves with the left hand")]
    [InlineData("يضرب برجله", "a person kicks forward with the right leg")]
    [InlineData("يضرب لكمة قوية", "a person throws a powerful punch with the right hand")]
    [InlineData("يضرب برأسه", "a person headbutts forward")]
    [InlineData("يشير بيده اليسار", "a person points forward with the left hand")]
    [InlineData("يلف حول نفسه", "a person spins around in a circle")]
    [InlineData("يعرج", "a person limps forward")]
    [InlineData("يتسلل", "a person sneaks forward stealthily, crouched low")]
    [InlineData("خايف", "a person looks scared and tense")]
    [InlineData("يطالع لفوق", "a person looks up")]
    [InlineData("يطالع شمال", "a person looks to the left")]
    [InlineData("يمشي بدون ما يطيح", "a person walks forward without falling")]
    [InlineData("يجري من غير ما يوقف", "a person runs forward without stopping")]
    [InlineData("يمشي ولا يطيح", "a person walks forward without falling")]
    [InlineData("walks without falling", "a person walks forward without falling")]
    [InlineData("jumps three times", "a person jumps up three times")]
    public void Captions_AreNaturalKimodoStyle(string prompt, string caption) => Assert.Equal(caption, MotionPromptPlanner.Translate(prompt).Caption);

    [Theory]
    [InlineData("يمشي وهو يلوح", "walk", "wave", "a person walks forward while waving")]
    [InlineData("يمشي وهو يلوّح بيده", "walk", "wave", "a person walks forward while waving")]
    [InlineData("يجري وهو يصرخ", "run", "shout", "a person runs forward while shouting")]
    [InlineData("يمشي وهو يتكلم بالتلفون", "walk", "phone_call", "a person walks forward while talking on the phone")]
    [InlineData("يمشي وهو يشرب", "walk", "drink", "a person walks forward while drinking")]
    [InlineData("يرقص وهو يغني", "dance", "sing", "a person dances while singing")]
    [InlineData("يمشي وهو يتلفت حواليه", "walk", "look_around", "a person walks forward while looking around")]
    [InlineData("يمشي، وهو يلوح", "walk", "wave", "a person walks forward while waving")]
    [InlineData("بينما يمشي يلوح", "walk", "wave", "a person walks forward while waving")]
    [InlineData("walk, looking around", "walk", "look_around", "a person walks forward while looking around")]
    [InlineData("a person walks while waving", "walk", "wave", "a person walks forward while waving")]
    [InlineData("runs while shouting", "run", "shout", "a person runs forward while shouting")]
    public void SimultaneousActions_AreLayered_InOneStep(string prompt, string action, string layer, string caption)
    {
        var p = MotionPromptPlanner.Plan(prompt, 5);
        var s = Assert.Single(p.Segments);
        Assert.Equal(action, s.Action);
        Assert.Equal(new[] { layer }, s.Layers);
        Assert.Equal(caption, s.Caption);
        Assert.Equal(5, s.DurationSeconds, 2);
    }

    [Theory]
    [InlineData("بدون ما يطيح يمشي", "walk")]
    [InlineData("يمشي من غير ما يقع", "walk")]
    [InlineData("يجري ولا يطيح", "run")]
    [InlineData("لا تقع", "idle")]
    [InlineData("يمشي وما يموت", "walk")]
    [InlineData("يقفز بدون ما يسقط", "jump")]
    [InlineData("يمشي بدون ما يلتفت", "walk")]
    public void Negation_RemovesTheAction_AndSaysWithout(string prompt, string action)
    {
        var p = MotionPromptPlanner.Plan(prompt, 4);
        var s = Assert.Single(p.Segments);
        Assert.Equal(action, s.Action);
        Assert.Contains("without", s.Caption.Split(" while ")[0]);
        Assert.DoesNotContain("fall", s.Caption.Replace("falling", ""));
    }

    [Theory]
    [InlineData("امشي ثانيتين", 2.0)]
    [InlineData("امشي لمدة ثانيتين", 2.0)]
    [InlineData("امشي نص ثانية", 0.5)]
    [InlineData("امشي ربع ثانية", 0.25)]
    [InlineData("امشي ثانية ونص", 1.5)]
    [InlineData("امشي ثانية", 1.0)]
    [InlineData("امشي ثلاث ثواني", 3.0)]
    [InlineData("امشي ٥ ثواني", 5.0)]
    [InlineData("امشي ٢.٥ ثانية", 2.5)]
    [InlineData("امشي خمس ثوان", 5.0)]
    [InlineData("امشي نص دقيقة", 30.0)]
    [InlineData("امشي دقيقة", 30.0)]
    [InlineData("walk for half a second", 0.5)]
    [InlineData("walk for 2 seconds", 2.0)]
    [InlineData("walk for a quarter second", 0.25)]
    [InlineData("walk for 10s", 10.0)]
    [InlineData("امشي لمدة عشر ثواني", 10.0)]
    [InlineData("امشي ثمان ثواني", 8.0)]
    public void DurationsInWords_AreRead(string prompt, double seconds)
    {
        var s = MotionPromptPlanner.Plan(prompt + " ثم اجري", 12).Segments[0];
        if (prompt.StartsWith("walk")) s = MotionPromptPlanner.Plan(prompt + " then run", 12).Segments[0];
        Assert.True(s.DurationExplicit, prompt);
        // Lengths above the 30 s limit are scaled down together with the other steps.
        if (seconds >= 30) Assert.InRange(s.DurationSeconds, 28, 30);
        else Assert.Equal(seconds, s.DurationSeconds, 2);
    }

    [Fact]
    public void Repetition_LengthensTheStep()
    {
        var plain = MotionPromptPlanner.Plan("ينط ثم يطيح", 8).Segments[0].DurationSeconds;
        var twice = MotionPromptPlanner.Plan("ينط مرتين ثم يطيح", 8).Segments[0].DurationSeconds;
        Assert.True(twice > plain);
        var steps = MotionPromptPlanner.Plan("امشي ٣ خطوات ثم اقف", 8);
        Assert.Equal(new[] { "walk", "stop" }, Actions(steps));
    }

    [Fact]
    public void Suddenly_BelongsToTheFollowingStep()
    {
        var p = MotionPromptPlanner.Plan("يمشي فجأة يطيح", 6);
        Assert.Equal(new[] { "walk", "fall" }, Actions(p));
        Assert.Equal("a person walks forward", p.Segments[0].Caption);
        Assert.Equal("a person suddenly falls to the ground", p.Segments[1].Caption);
        var q = MotionPromptPlanner.Plan("يمشي ثم فجأة يجري", 6);
        Assert.Equal("a person suddenly runs forward", q.Segments[1].Caption);
        Assert.Equal("a person suddenly falls to the ground", MotionPromptPlanner.Plan("suddenly falls", 3).Segments[0].Caption);
    }

    [Fact]
    public void UsersExamples_RemainSplitCorrectly_WithTypos()
    {
        foreach (var prompt in new[] { "في واحد وراك، اجري، وبعدها موت", "فى واحد وراااك اجرى وبعدها موووت", "في واحد ورآك اجري وبعدها موت" })
            Assert.Equal(new[] { "look_back", "run", "die" }, Actions(MotionPromptPlanner.Plan(prompt, 6)));
        var p = MotionPromptPlanner.Plan("يضرب لكمة قوية ثم يرجع لوضع الاستعداد", 4);
        Assert.Equal(new[] { "punch", "idle" }, Actions(p));
        Assert.Equal("a person throws a powerful punch with the right hand", p.Segments[0].Caption);
        Assert.Equal("a person stands still", p.Segments[1].Caption);
    }

    [Fact]
    public void ExistingJsonShape_IsKept_AndNewFieldsAreOptional()
    {
        // MotionSegmentPlan keeps its ten positional members; Confidence, Suggestions and Layers are extra init properties with defaults.
        var s = new MotionSegmentPlan(0, "x", "y", null, 1, true, false, false, null, Array.Empty<string>());
        Assert.Equal(1, s.Confidence);
        Assert.Empty(s.Suggestions);
        Assert.Empty(s.Layers);
        Assert.Equal(1, new MotionPlan("", 0, Array.Empty<MotionSegmentPlan>(), Array.Empty<string>()).Confidence);
    }

    [Fact]
    public void Lexicon_IsLargeAndConsistent()
    {
        Assert.Empty(MotionPromptPlanner.LexiconConflicts);
        Assert.True(MotionPromptPlanner.LexiconSize > 5000, MotionPromptPlanner.LexiconSize.ToString());
        Assert.True(MotionPromptPlanner.Actions.Count >= 70);
    }

    [Fact]
    public void UnknownButEnglishWords_PassThrough_And_ArabicUnknownsAreListed()
    {
        var e = MotionPromptPlanner.Plan("a person does a moonwalk", 3).Segments[0];
        Assert.True(e.Understood);
        Assert.Equal("a person does a moonwalk", e.Caption);
        var a = MotionPromptPlanner.Plan("يمشي مثل الدب الابيض", 3).Segments[0];
        Assert.Equal("walk", a.Action);
        Assert.Contains("الدب", a.Unrecognized);
        Assert.Contains("الابيض", a.Unrecognized);
    }
}
