using System.Globalization;
using System.Text;

namespace Blazma.AI;

/// <summary>One step of a planned motion: what the user wrote, the English motion caption sent to the generator, its length.</summary>
/// <param name="Index">0-based order.</param>
/// <param name="Text">The user's words for this step (original language, without the sequencing connector).</param>
/// <param name="Caption">English Kimodo-style caption ("a person runs forward"). For English text that the dictionary does not fully
/// cover it is the text as written; for Arabic that is not understood it is the original text.</param>
/// <param name="Action">Recognised action id (walk, run, die, look_back…), or null.</param>
/// <param name="DurationSeconds">Allocated length.</param>
/// <param name="Understood">False only for Arabic (or other non-English) text with no recognised action: the user should rephrase it.</param>
/// <param name="DurationExplicit">The length came from the text ("for 3 seconds", "لمدة 3 ثواني") or from an explicit plan.</param>
/// <param name="IsArabic">The text contains Arabic letters.</param>
/// <param name="ProceduralPrompt">Keyword the built-in procedural generator understands for this action, or null.</param>
/// <param name="Unrecognized">Words of the text that the dictionary did not know (Arabic only; shown to the user).</param>
public sealed record MotionSegmentPlan(int Index, string Text, string Caption, string? Action, double DurationSeconds, bool Understood,
    bool DurationExplicit, bool IsArabic, string? ProceduralPrompt, IReadOnlyList<string> Unrecognized);

/// <summary>A prompt split into ordered steps with their lengths (see <see cref="MotionPromptPlanner"/>).</summary>
public sealed record MotionPlan(string Prompt, double TotalSeconds, IReadOnlyList<MotionSegmentPlan> Segments, IReadOnlyList<string> Warnings)
{
    public bool AllUnderstood => Segments.All(s => s.Understood);
}

/// <summary>An explicit step (agents' <c>segments</c>, or the dialog's edited plan). <paramref name="Caption"/> overrides the translation.</summary>
public sealed record SegmentInput(string Text, double? DurationSeconds = null, string? Caption = null);

/// <summary>
/// Offline motion prompt planner (DECISIONS D45). Kimodo generates ONE motion per prompt and its text encoder knows English motion
/// captions, so a prompt like «في واحد وراك اجري وبعدها موت» ("someone is behind you, run, and then die") collapsed into a single
/// action. The planner splits the prompt into ordered steps on Arabic and English sequencing connectors (ثم، بعدين، وبعدها، then,
/// after that…), punctuation, and «و»/"and" when it joins two different actions; reads per-step duration and speed hints; maps
/// context clauses ("someone is behind you") to an action (look back, startled); translates each step to a natural English
/// caption with a curated dictionary (MSA and Gulf/Sudanese/Egyptian colloquial verbs); and allocates the requested total length
/// across the steps with per-action defaults. No network, no language model: deterministic and instant.
/// </summary>
public static class MotionPromptPlanner
{
    public const int MaxSegments = 12;
    public const double MinSegmentSeconds = 0.4;

    // ------------------------------------------------------------------ dictionary

    enum K { Action, Cue, Speed, Dir, Connector, And, Concurrent, Number, Unit, UnitWithCount, Filler }

    sealed record Entry(K Kind, string Value = "", double Number = 0);

    static readonly Dictionary<string, Entry> Words = new(StringComparer.Ordinal);
    static readonly List<(string[] Tokens, Entry Entry)> Phrases = new();

    /// <summary>Action → (group, strength). Steps are split only between actions of different groups; within a group the stronger wins.</summary>
    static readonly Dictionary<string, (string Group, int Strength)> Groups = new()
    {
        ["walk"] = ("walk", 0), ["run"] = ("run", 0), ["sprint"] = ("run", 1), ["jump"] = ("jump", 0),
        ["fall"] = ("fall", 0), ["die"] = ("fall", 1), ["look_back"] = ("turn", 0), ["turn_around"] = ("turn", 1),
        ["crouch"] = ("crouch", 0), ["sit"] = ("sit", 0), ["stand_up"] = ("stand_up", 0), ["kick"] = ("kick", 0),
        ["punch"] = ("punch", 0), ["wave"] = ("wave", 0), ["dance"] = ("dance", 0), ["idle"] = ("idle", 0), ["stop"] = ("idle", 1),
        ["step_back"] = ("step_back", 0), ["dodge"] = ("dodge", 0), ["roll"] = ("roll", 0), ["climb"] = ("climb", 0),
        ["throw"] = ("throw", 0), ["pick_up"] = ("pick_up", 0),
    };

    /// <summary>Default length (s) of each action, used as weights when the total is shared out.</summary>
    static readonly Dictionary<string, double> Defaults = new()
    {
        ["walk"] = 4, ["run"] = 3.5, ["sprint"] = 3, ["jump"] = 1.5, ["fall"] = 2, ["die"] = 2.5, ["look_back"] = 1.2, ["turn_around"] = 1.2,
        ["crouch"] = 1.5, ["sit"] = 2, ["stand_up"] = 1.5, ["kick"] = 1.2, ["punch"] = 1, ["wave"] = 2, ["dance"] = 4, ["idle"] = 1.5,
        ["stop"] = 1.5, ["step_back"] = 1.2, ["dodge"] = 1, ["roll"] = 1.5, ["climb"] = 3, ["throw"] = 1.2, ["pick_up"] = 1.8,
    };

    /// <summary>Keyword of the procedural (not AI) generator for each action it can make.</summary>
    static readonly Dictionary<string, string> ProceduralKeywords = new()
    {
        ["walk"] = "walk", ["run"] = "run", ["sprint"] = "run", ["jump"] = "jump", ["fall"] = "fall", ["die"] = "die",
        ["look_back"] = "look back", ["turn_around"] = "turn around", ["crouch"] = "squat", ["sit"] = "squat", ["pick_up"] = "squat",
        ["stand_up"] = "idle", ["idle"] = "idle", ["stop"] = "stop", ["wave"] = "wave", ["punch"] = "punch",
    };

    /// <summary>All actions the dictionary knows.</summary>
    public static IReadOnlyCollection<string> Actions => Groups.Keys;

    /// <summary>Default length of an action (2 s for an unknown one).</summary>
    public static double DefaultSeconds(string? action) => action is not null && Defaults.TryGetValue(action, out var d) ? d : 2;

    /// <summary>The procedural generator's keyword for an action, or null when it cannot make it.</summary>
    public static string? ProceduralKeyword(string? action) => action is not null && ProceduralKeywords.TryGetValue(action, out var k) ? k : null;

    static void W(K kind, string value, params string[] forms)
    {
        foreach (var f in forms)
        {
            var toks = f.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Normalize).ToArray();
            if (toks.Length == 1) Words[toks[0]] = new Entry(kind, value);
            else Phrases.Add((toks, new Entry(kind, value)));
        }
    }

    static void A(string action, params string[] forms) => W(K.Action, action, forms);

    static MotionPromptPlanner()
    {
        // ---- English actions (all inflections listed: no stemming surprises).
        A("walk", "walk", "walks", "walking", "walked", "stroll", "strolls", "strolling", "strolled", "march", "marches", "marching");
        A("run", "run", "runs", "running", "ran", "jog", "jogs", "jogging", "jogged", "run away", "runs away", "running away", "flee", "flees", "fleeing", "fled");
        A("sprint", "sprint", "sprints", "sprinting", "sprinted", "dash", "dashes", "dashing", "dashed");
        A("jump", "jump", "jumps", "jumping", "jumped", "hop", "hops", "hopping", "hopped", "leap", "leaps", "leaping", "leaped", "leapt");
        A("fall", "fall", "falls", "falling", "fell", "collapse", "collapses", "collapsing", "collapsed", "fall down", "falls down", "trip", "trips", "tripping", "tripped");
        A("die", "die", "dies", "dying", "died", "dead", "drop dead", "drops dead", "gets killed", "get killed", "is killed");
        A("turn_around", "turn", "turns", "turning", "turned", "turn around", "turns around", "turning around", "turned around", "turn round",
            "spin around", "spins around", "spin", "spins", "spinning", "turn back", "turns back");
        A("look_back", "look back", "looks back", "looking back", "looked back", "look behind", "looks behind", "glance back", "glances back",
            "look over shoulder", "looks over shoulder", "look over his shoulder", "looks over his shoulder", "looks over her shoulder");
        A("crouch", "crouch", "crouches", "crouching", "crouched", "squat", "squats", "squatting", "squatted", "duck", "ducks", "ducking", "ducked",
            "kneel", "kneels", "kneeling", "knelt", "bend down", "bends down");
        A("sit", "sit", "sits", "sitting", "sat", "sit down", "sits down", "sitting down", "sat down");
        A("stand_up", "stand up", "stands up", "standing up", "stood up", "get up", "gets up", "getting up", "got up", "rise", "rises", "rising", "rose");
        A("kick", "kick", "kicks", "kicking", "kicked");
        A("punch", "punch", "punches", "punching", "punched", "hit", "hits", "hitting", "strike", "strikes", "striking", "struck",
            "attack", "attacks", "attacking", "attacked", "swing", "swings", "swinging", "slash", "slashes");
        A("wave", "wave", "waves", "waving", "waved", "greet", "greets", "greeting");
        A("dance", "dance", "dances", "dancing", "danced");
        A("idle", "idle", "idles", "idling", "stand", "stands", "standing", "stood", "wait", "waits", "waiting", "waited", "stand still",
            "stands still", "standing still", "breathe", "breathes", "breathing", "rest", "rests");
        A("stop", "stop", "stops", "stopping", "stopped", "halt", "halts", "freeze", "freezes", "froze");
        A("step_back", "step back", "steps back", "stepping back", "stepped back", "step backward", "steps backward", "step backwards", "steps backwards",
            "back away", "backs away", "backing away", "back up", "backs up", "retreat", "retreats");
        A("dodge", "dodge", "dodges", "dodging", "dodged", "evade", "evades", "evading", "sidestep", "sidesteps");
        A("roll", "roll", "rolls", "rolling", "rolled", "tumble", "tumbles");
        A("climb", "climb", "climbs", "climbing", "climbed");
        A("throw", "throw", "throws", "throwing", "threw", "toss", "tosses", "tossing", "tossed");
        A("pick_up", "pick up", "picks up", "picking up", "picked up", "pick it up", "picks it up", "grab", "grabs", "grabbing", "grabbed",
            "lift", "lifts", "lifting", "lifted");

        // ---- Arabic actions (normalised: no diacritics, أإآ→ا, ى→ي, ة→ه). MSA + Gulf / Sudanese / Egyptian colloquial.
        A("walk", "يمشي", "امشي", "مشي", "تمشي", "ماشي", "يتمشي", "اتمشي", "تتمشي", "يسير", "سير", "تسير", "سار", "يمشى");
        A("run", "يجري", "اجري", "جري", "تجري", "جاري", "يجروا", "يركض", "اركض", "ركض", "تركض", "راكض", "اركضي", "يهرب", "اهرب", "هرب", "تهرب", "اشرد", "يشرد", "شرد");
        A("sprint", "ينطلق", "انطلق", "تنطلق");
        A("jump", "ينط", "نط", "انط", "تنط", "نطه", "يتنطط", "يقفز", "اقفز", "قفز", "تقفز", "قفزه", "ينقز", "نقز", "انقز", "تنقز");
        A("fall", "يقع", "وقع", "اقع", "تقع", "يطيح", "طاح", "طيح", "اطيح", "تطيح", "يسقط", "سقط", "اسقط", "تسقط", "ينهار", "انهار", "تنهار",
            "يتعثر", "تعثر", "يندفر", "يقع علي الارض", "يطيح علي الارض");
        A("die", "يموت", "موت", "مات", "تموت", "اموت", "ميت", "ميته", "يفطس", "فطس", "افطس", "ينقتل", "انقتل", "يتوفي", "مقتول");
        A("turn_around", "يلف", "لف", "تلف", "يدور", "دور", "تدور", "يستدير", "استدير", "استدار", "تستدير", "لف ورا", "دور ورا", "لف لورا", "دور لورا", "يلف ورا", "يدور ورا");
        foreach (var look in new[] { "يطالع", "طالع", "يشوف", "شوف", "بص", "يبص", "عاين", "يعاين", "يناظر", "ناظر", "ينظر", "انظر", "يتلفت", "اتلفت" })
            foreach (var back in new[] { "ورا", "وراء", "وراه", "وراك", "لورا", "خلف", "خلفه", "خلفك", "للخلف" })
                A("look_back", look + " " + back);
        A("look_back", "يلتفت", "التفت", "تلتفت", "اتلفت", "يتلفت", "تتلفت");
        A("crouch", "ينحني", "انحني", "تنحني", "يوطي", "وطي", "يقرفص", "قرفص", "قرفصاء", "يدنقر", "دنقر", "يجثو", "اجثو", "ينخفض", "انخفض", "يركع", "اركع", "ركع");
        A("sit", "يقعد", "اقعد", "قعد", "تقعد", "يجلس", "اجلس", "جلس", "تجلس", "قاعد", "جالس");
        A("stand_up", "يقوم", "قوم", "قام", "تقوم", "ينهض", "انهض", "نهض", "تنهض", "يقوم من الارض");
        A("kick", "يركل", "اركل", "ركل", "تركل", "ركله", "يرفس", "رفس", "ارفس", "رفسه", "يشوت", "شوت", "يشلت", "شلوت");
        A("punch", "يضرب", "اضرب", "ضرب", "تضرب", "ضربه", "يلكم", "الكم", "لكم", "لكمه", "بوكس", "يبوكس", "يهاجم", "هاجم", "هجوم");
        A("wave", "يلوح", "لوح", "الوح", "تلوح", "تلويح", "ياشر", "اشر", "يشاور", "شاور", "باي", "تحيه", "يحيي");
        A("dance", "يرقص", "ارقص", "رقص", "ترقص", "يتمايل");
        A("idle", "يقف", "واقف", "وقوف", "ثابت", "ثبات", "يستني", "استني", "ينتظر", "انتظر", "انتظار", "يرتاح", "ارتاح", "اثبت", "يثبت");
        A("stop", "وقف", "قف", "اقف", "يوقف", "توقف", "يتوقف", "تقف");
        A("step_back", "يرجع", "ارجع", "رجع", "ترجع", "يتراجع", "تراجع", "اتراجع", "يرجع لورا", "ارجع لورا", "يرجع ورا", "ارجع ورا",
            "خطوه لورا", "خطوه للخلف", "خطوتين لورا");
        A("dodge", "يتفادي", "تفادي", "اتفادي", "يراوغ", "راوغ", "يزوغ", "زوغ", "يتهرب");
        A("roll", "يتدحرج", "تدحرج", "اتدحرج", "يتقلب", "تقلب", "يتشقلب", "شقلب", "شقلبه");
        A("climb", "يتسلق", "تسلق", "اتسلق", "يتشعبط", "اتشعبط");
        A("throw", "يرمي", "ارمي", "رمي", "ترمي", "يقذف", "قذف", "اقذف", "يحدف", "حدف", "احدف");
        A("pick_up", "يلقط", "لقط", "القط", "التقط", "يلتقط", "يشيل", "شيل", "اشيل", "يرفع", "ارفع", "رفع");

        // ---- context clauses: "someone is behind you" → look back, startled (not dropped).
        W(K.Cue, "look_back", "behind you", "behind me", "behind him", "behind her", "behind them", "behind us",
            "وراك", "وراه", "وراها", "ورايا", "وراكم", "وراكي", "خلفك", "خلفه", "خلفها", "خلفي", "خلفكم");

        // ---- speed and direction
        W(K.Speed, "fast", "fast", "quickly", "quick", "rapidly", "swiftly", "hurriedly", "بسرعه", "سريع", "سريعا", "بسرعه كبيره", "بقوه", "طوالي");
        W(K.Speed, "slow", "slow", "slowly", "gently", "carefully", "ببطء", "ببطئ", "بطيء", "بطئ", "بشويش", "براحه", "بالراحه", "بهدوء", "شويه شويه", "علي مهله", "علي مهلك");
        W(K.Dir, "forward", "forward", "forwards", "ahead", "straight", "لقدام", "قدام", "للقدام", "للامام", "الي الامام", "لي قدام", "امام");
        W(K.Dir, "backward", "backward", "backwards", "back", "ورا", "وراء", "لورا", "للورا", "للخلف", "الي الخلف", "لي ورا");
        W(K.Dir, "left", "left", "to the left", "يسار", "لليسار", "شمال", "للشمال", "علي اليسار");
        W(K.Dir, "right", "right", "to the right", "يمين", "لليمين", "علي اليمين");
        W(K.Dir, "up", "up", "upward", "upwards", "لفوق", "فوق");
        W(K.Dir, "in_place", "in place", "on the spot", "في مكانه", "في مكانك", "مكانه", "مكانك");

        // ---- sequencing connectors
        W(K.Connector, "", "then", "afterwards", "afterward", "next", "finally", "lastly", "later", "and then", "and after that", "after that",
            "after this", "and finally", "and next", "then finally",
            "ثم", "وثم", "فثم", "ومن ثم", "بعدين", "وبعدين", "فبعدين", "بعدها", "وبعدها", "فبعدها", "بعده", "وبعده", "بعد ذلك", "وبعد ذلك", "بعد كده", "وبعد كده",
            "بعد كدا", "وبعد كدا", "بعد كذا", "وبعد كذا", "بعد داك", "وبعد داك", "بعد دا", "وبعد دا", "واخيرا", "اخيرا", "وفي الاخير", "في الاخير", "وبالاخير", "بعدما", "وبعدما");
        W(K.And, "", "and", "و", "&", "plus");
        W(K.Concurrent, "", "while", "whilst", "وهو", "وهي", "بينما", "اثناء", "وانت", "وانا");

        // ---- durations
        foreach (var (n, forms) in new (double, string[])[]
        {
            (0.5, new[] { "half", "نص", "نصف" }), (1, new[] { "one", "a", "an", "واحد", "واحده" }), (2, new[] { "two", "اثنين", "اتنين", "اثنان" }),
            (3, new[] { "three", "ثلاث", "ثلاثه", "تلات", "تلاته" }), (4, new[] { "four", "اربع", "اربعه" }), (5, new[] { "five", "خمس", "خمسه" }),
            (6, new[] { "six", "ست", "سته" }), (7, new[] { "seven", "سبع", "سبعه" }), (8, new[] { "eight", "ثمان", "ثماني", "ثمانيه", "تمن", "تمانيه" }),
            (9, new[] { "nine", "تسع", "تسعه" }), (10, new[] { "ten", "عشر", "عشره" }),
        })
            foreach (var f in forms) NumberWords[Normalize(f)] = n;
        W(K.Unit, "", "s", "sec", "secs", "second", "seconds", "ث", "ثانيه", "ثواني", "ثوان", "ثوانٍ");
        Words[Normalize("ثانيتين")] = new Entry(K.UnitWithCount, "", 2);
        Words[Normalize("ثانيتان")] = new Entry(K.UnitWithCount, "", 2);

        // ---- filler words (subjects, articles, prepositions): understood, carry no action.
        W(K.Filler, "", "a", "an", "the", "person", "man", "woman", "guy", "girl", "boy", "character", "he", "she", "they", "it", "you", "i", "his", "her",
            "their", "your", "my", "to", "on", "of", "at", "in", "into", "onto", "with", "is", "are", "be", "very", "really", "suddenly", "there", "ground",
            "floor", "down", "around", "over", "from", "away", "for", "during", "about", "some", "someone", "somebody", "something", "please", "now", "again",
            "shoulder", "someone's", "there's", "startled", "scared", "himself", "herself",
            "شخص", "الشخص", "رجل", "الرجل", "راجل", "زول", "الزول", "ولد", "بنت", "هو", "هي", "انت", "انتا", "انتي", "انا", "يا", "في", "فيه", "علي", "الي", "لي",
            "من", "عن", "مع", "حتي", "لما", "لين", "عشان", "كده", "كدا", "كذا", "خلاص", "الارض", "ارض", "الارضيه", "لمده", "مده", "مرتين", "مره", "تاني",
            "حد", "احد", "واحد", "شي", "شيء", "حاجه", "جاي", "جا", "بسرعته", "فجاه", "فجأه", "زي", "مثل", "طيب", "يلا", "هسه", "هسع", "الحين", "دلوقتي", "توا");
    }

    static readonly Dictionary<string, double> NumberWords = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ text

    /// <summary>Lower-case; removes Arabic diacritics and tatweel; unifies alef forms, ى→ي, ة→ه; Arabic-Indic digits → ASCII.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ') continue;
            sb.Append(ch switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                'ؤ' => 'و',
                'ئ' => 'ي',
                '’' or '‘' => '\'',
                >= '٠' and <= '٩' => (char)('0' + (ch - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (ch - '۰')),
                _ => char.ToLowerInvariant(ch),
            });
        }
        return sb.ToString();
    }

    public static bool HasArabic(string s) => s.Any(c => c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ');

    sealed record Token(string Orig, string Norm, int Start, int End, bool Punct);

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c is '\'' or '’' or 'ـ' or '&';

    static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (IsWordChar(c))
            {
                var start = i;
                while (i < text.Length && (IsWordChar(text[i]) || (text[i] is '.' or ',' or '٫' && i > start && char.IsDigit(text[i - 1]) && i + 1 < text.Length && char.IsDigit(text[i + 1]))))
                    i++;
                var orig = text[start..i];
                tokens.Add(new Token(orig, Normalize(orig).Trim('\''), start, i, false));
                continue;
            }
            if (c is '،' or ',' or ';' or '؛' or '.' or '!' or '?' or '؟' or '\n' or '…' or ':' or '-' or '—' or '–')
                tokens.Add(new Token(c.ToString(), c.ToString(), i, i + 1, true));
            i++;
        }
        return tokens;
    }

    /// <summary>A dictionary hit over tokens [Start, End).</summary>
    sealed record Item(int Start, int End, Entry? Entry, bool AndPrefix, bool Punct, double? Seconds = null);

    static Entry? Lookup(string norm, out bool andPrefix)
    {
        andPrefix = false;
        if (Words.TryGetValue(norm, out var e)) return e;
        if (norm.Length > 2 && (norm[0] == 'و' || norm[0] == 'ف') && Words.TryGetValue(norm[1..], out e) && e.Kind is K.Action or K.Cue or K.Dir or K.Speed)
        {
            andPrefix = e.Kind is K.Action or K.Cue;
            return e;
        }
        // Colloquial progressive: بيجري، بيمشي، بترقص.
        if (norm.Length > 3 && norm[0] == 'ب' && norm[1] is 'ي' or 'ت' or 'ن' && Words.TryGetValue(norm[1..], out e) && e.Kind == K.Action) return e;
        if (norm.Length > 4 && norm.StartsWith("وب", StringComparison.Ordinal) && norm[2] is 'ي' or 'ت' && Words.TryGetValue(norm[2..], out e) && e.Kind == K.Action)
        {
            andPrefix = true;
            return e;
        }
        if (norm.StartsWith("وال", StringComparison.Ordinal) && Words.TryGetValue(norm[1..], out e) && e.Kind == K.Filler) return e;
        if (norm.StartsWith("ال", StringComparison.Ordinal) && norm.Length > 3 && Words.TryGetValue(norm[2..], out e) && e.Kind == K.Filler) return e;
        return null;
    }

    static readonly System.Text.RegularExpressions.Regex Compact = new(@"^(\d+(?:[.,٫]\d+)?)(s|sec|secs|ث)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    static double? Number(string norm)
    {
        if (double.TryParse(norm.Replace(',', '.').Replace('٫', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return NumberWords.TryGetValue(norm, out var n) ? n : null;
    }

    static List<Item> Annotate(List<Token> t)
    {
        var items = new List<Item>();
        var i = 0;
        while (i < t.Count)
        {
            if (t[i].Punct) { items.Add(new Item(i, i + 1, null, false, true)); i++; continue; }
            // Durations: "3 seconds", "for 2s", "لمدة ثلاث ثواني", "ثانيتين", "half a second".
            var compact = Compact.Match(t[i].Norm);
            if (compact.Success)
            {
                items.Add(new Item(i, i + 1, null, false, false, Number(compact.Groups[1].Value)));
                i++;
                continue;
            }
            if (Number(t[i].Norm) is double num)
            {
                var j = i + 1;
                if (j < t.Count && !t[j].Punct && t[j].Norm is "a" or "an") j++;
                if (j < t.Count && !t[j].Punct && Words.TryGetValue(t[j].Norm, out var u) && u.Kind == K.Unit && num > 0)
                {
                    items.Add(new Item(i, j + 1, null, false, false, num));
                    i = j + 1;
                    continue;
                }
            }
            if (Words.TryGetValue(t[i].Norm, out var uc) && uc.Kind == K.UnitWithCount)
            {
                items.Add(new Item(i, i + 1, null, false, false, uc.Number));
                i++;
                continue;
            }
            // Longest phrase first.
            Item? best = null;
            foreach (var (toks, entry) in Phrases)
            {
                if (i + toks.Length > t.Count || (best is not null && toks.Length <= best.End - best.Start)) continue;
                var ok = true;
                var and = false;
                for (var k = 0; k < toks.Length && ok; k++)
                {
                    if (t[i + k].Punct) { ok = false; break; }
                    var n = t[i + k].Norm;
                    if (n == toks[k]) continue;
                    // The first word may carry «و» / «ف» ("وبعد ذلك" is listed; "ويطالع ورا" is not).
                    if (k == 0 && n.Length > 2 && n[0] is 'و' or 'ف' && n[1..] == toks[0]) { and = true; continue; }
                    ok = false;
                }
                if (ok) best = new Item(i, i + toks.Length, entry, and && entry.Kind is K.Action or K.Cue, false);
            }
            if (best is not null) { items.Add(best); i = best.End; continue; }
            var e = Lookup(t[i].Norm, out var andPrefix);
            items.Add(new Item(i, i + 1, e, andPrefix, false));
            i++;
        }
        return items;
    }

    static bool IsHit(Item it) => it.Entry is { Kind: K.Action or K.Cue };
    static string GroupOf(Item it) => Groups[it.Entry!.Value].Group;

    // ------------------------------------------------------------------ planning

    /// <summary>Splits <paramref name="prompt"/> into steps and shares <paramref name="totalSeconds"/> among them. A prompt with a
    /// single step gets the whole length (as before the planner existed).</summary>
    public static MotionPlan Plan(string prompt, double totalSeconds)
    {
        var warnings = new List<string>();
        var tokens = Tokenize(prompt ?? "");
        var items = Annotate(tokens);

        // 1. Hard boundaries: sequencing connectors and punctuation.
        var chunks = new List<(List<Item> Items, bool AfterConnector)>();
        var cur = new List<Item>();
        var afterConnector = false;
        foreach (var it in items)
        {
            if (it.Punct || it.Entry?.Kind == K.Connector)
            {
                var ended = cur.Count > 0;
                if (ended) chunks.Add((cur, afterConnector));
                cur = new List<Item>();
                if (it.Entry?.Kind == K.Connector) afterConnector = true;
                else if (ended) afterConnector = false;
                continue;
            }
            cur.Add(it);
        }
        if (cur.Count > 0) chunks.Add((cur, afterConnector));

        // 2. Soft boundaries inside a chunk: «و»/"and" or no word at all between two actions of different groups
        //    (not across "while"/«وهو», and never before a context clause that follows its action).
        var parts = new List<(List<Item> Items, bool Hard)>();
        foreach (var (chunk, hard) in chunks)
        {
            var start = 0;
            Item? prevHit = null;
            var concurrent = false;
            var first = true;
            for (var k = 0; k < chunk.Count; k++)
            {
                var it = chunk[k];
                if (it.Entry?.Kind == K.Concurrent) concurrent = true;
                if (!IsHit(it)) continue;
                if (prevHit is not null && !concurrent && GroupOf(prevHit) != GroupOf(it) && it.Entry!.Kind != K.Cue)
                {
                    // A standalone «و»/"and" just before the action is the joint: drop it.
                    var andAt = -1;
                    for (var m = k - 1; m >= start && !IsHit(chunk[m]); m--) if (chunk[m].Entry?.Kind == K.And) { andAt = m; break; }
                    var left = chunk.GetRange(start, (andAt >= 0 ? andAt : k) - start);
                    if (left.Count > 0) { parts.Add((left, first && hard)); first = false; }
                    start = andAt >= 0 ? andAt + 1 : k;
                }
                prevHit = it;
                concurrent = false;
            }
            var rest = chunk.GetRange(start, chunk.Count - start);
            if (rest.Count > 0) parts.Add((rest, first && hard));
        }

        // 3. Parts without an action: modifiers merge into a neighbour; English after a comma joins the previous step; other text stays
        //    a step of its own (Arabic: flagged "not understood").
        var steps = new List<List<Item>>();
        var pendingLead = new List<Item>();
        foreach (var (part, hard) in parts)
        {
            var hasHit = part.Any(IsHit);
            var substantive = part.Where(x => x.Entry is null && x.Seconds is null).ToList();
            if (!hasHit && substantive.Count == 0)
            {
                if (steps.Count > 0) steps[^1].AddRange(part); else pendingLead.AddRange(part);
                continue;
            }
            if (!hasHit && !hard && steps.Count > 0 && !substantive.Any(x => HasArabic(tokens[x.Start].Orig)))
            {
                steps[^1].AddRange(part);
                continue;
            }
            var step = new List<Item>(pendingLead);
            pendingLead.Clear();
            step.AddRange(part);
            steps.Add(step);
        }
        if (pendingLead.Count > 0) steps.Add(pendingLead);
        if (steps.Count > MaxSegments)
        {
            warnings.Add($"Only the first {MaxSegments} steps are used.");
            steps = steps.Take(MaxSegments).ToList();
        }

        var readings = steps.Select(s => Read(prompt ?? "", tokens, s)).ToList();
        if (readings.Count == 0) return new MotionPlan(prompt ?? "", totalSeconds, Array.Empty<MotionSegmentPlan>(), warnings);
        if (readings.Count == 1) readings[0] = readings[0] with { Seconds = null };
        return Allocate(prompt ?? "", readings, totalSeconds, warnings);
    }

    /// <summary>An explicit plan: each step is translated (unless it carries a caption); missing lengths share the rest of the total.</summary>
    public static MotionPlan FromSegments(IReadOnlyList<SegmentInput> segments, double totalSeconds)
    {
        var warnings = new List<string>();
        var readings = new List<Reading>();
        foreach (var s in segments.Take(MaxSegments))
        {
            var r = ReadText(s.Text);
            if (s.Caption is { } c && c.Trim().Length > 0)
                r = r with { Caption = c.Trim(), Understood = true, Action = r.Action ?? ReadText(c).Action };
            r = r with { Seconds = s.DurationSeconds ?? r.Seconds };
            readings.Add(r);
        }
        if (segments.Count > MaxSegments) warnings.Add($"Only the first {MaxSegments} steps are used.");
        var text = string.Join(" → ", segments.Select(s => s.Text));
        if (readings.Count == 1 && segments[0].DurationSeconds is null) readings[0] = readings[0] with { Seconds = null };
        return Allocate(text, readings, totalSeconds, warnings);
    }

    /// <summary>Reads one step of text (translation only, no splitting).</summary>
    public static MotionSegmentPlan Translate(string text)
    {
        var r = ReadText(text);
        return new MotionSegmentPlan(0, r.Text, r.Caption, r.Action, r.Seconds ?? DefaultSeconds(r.Action), r.Understood, r.Seconds is not null,
            r.IsArabic, ProceduralKeyword(r.Action), r.Unrecognized);
    }

    sealed record Reading(string Text, string Caption, string? Action, double? Seconds, double Weight, bool Understood, bool IsArabic, IReadOnlyList<string> Unrecognized);

    static Reading ReadText(string text)
    {
        var tokens = Tokenize(text);
        var items = Annotate(tokens).Where(i => !i.Punct && i.Entry?.Kind != K.Connector).ToList();
        return Read(text, tokens, items);
    }

    static Reading Read(string source, List<Token> tokens, List<Item> items)
    {
        var words = items.Where(i => !i.Punct && i.Entry?.Kind is not K.Connector).ToList();
        var text = words.Count == 0 ? "" : source[tokens[words[0].Start].Start..tokens[words[^1].End - 1].End].Trim();
        // Drop a leading joint «و»/"and" left by the split.
        while (words.Count > 0 && words[0].Entry?.Kind == K.And) words.RemoveAt(0);
        if (words.Count > 0) text = source[tokens[words[0].Start].Start..tokens[words[^1].End - 1].End].Trim();
        var arabic = HasArabic(text);

        string? action = null;
        var cueOnly = false;
        foreach (var hit in words.Where(IsHit))
        {
            var a = hit.Entry!.Value;
            if (action is null) { action = a; cueOnly = hit.Entry.Kind == K.Cue; continue; }
            if (Groups[a].Group == Groups[action].Group && Groups[a].Strength > Groups[action].Strength) { action = a; cueOnly = false; }
            else if (hit.Entry.Kind == K.Action && cueOnly && Groups[a].Group == Groups[action].Group) cueOnly = false;
        }
        var speed = words.LastOrDefault(w => w.Entry?.Kind == K.Speed)?.Entry!.Value;
        var dir = words.LastOrDefault(w => w.Entry?.Kind == K.Dir)?.Entry!.Value;
        var seconds = words.LastOrDefault(w => w.Seconds is not null)?.Seconds;
        if (seconds is double s0) seconds = Math.Clamp(s0, MotionRequest.MinDurationSeconds, MotionRequest.MaxDurationSeconds);
        var unknown = words.Where(w => w.Entry is null && w.Seconds is null).SelectMany(w => Enumerable.Range(w.Start, w.End - w.Start)).Select(k => tokens[k].Orig).ToList();

        var groups = words.Where(w => IsHit(w) && w.Entry!.Kind == K.Action).Select(GroupOf).Distinct().Count();
        string caption;
        bool understood;
        if (action is not null && (arabic || (unknown.Count == 0 && groups <= 1)))
        {
            caption = Caption(action, speed, dir, cueOnly);
            understood = true;
        }
        else if (!arabic)
        {
            // English the dictionary does not fully cover: Kimodo reads English, so it is sent as written (minus timing words).
            var kept = words.Where((w, k) => w.Seconds is null
                && !(k + 1 < words.Count && words[k + 1].Seconds is not null && tokens[w.Start].Norm is "for" or "during" or "لمده")).ToList();
            caption = kept.Count == 0 ? text : string.Join(' ', kept.SelectMany(w => Enumerable.Range(w.Start, w.End - w.Start)).Select(k => tokens[k].Orig));
            understood = caption.Length > 0;
        }
        else
        {
            caption = text;
            understood = false;
        }
        var weight = DefaultSeconds(action) * (speed == "slow" ? 1.3 : speed == "fast" ? 0.85 : 1);
        return new Reading(text, caption, action, seconds, weight, understood, arabic, arabic ? unknown : Array.Empty<string>());
    }

    static string DirText(string? dir, string fallback) => dir switch
    {
        "forward" => "forward",
        "backward" => "backward",
        "left" => "to the left",
        "right" => "to the right",
        "in_place" => "in place",
        "up" => fallback == "forward" ? "forward" : "up",
        _ => fallback,
    };

    static string Caption(string action, string? speed, string? dir, bool cue)
    {
        var adv = speed switch { "fast" => " quickly", "slow" => " slowly", _ => "" };
        return action switch
        {
            "walk" => "a person walks " + DirText(dir, "forward") + adv,
            "run" => speed == "slow" ? "a person jogs " + DirText(dir, "forward") : "a person runs " + DirText(dir, "forward") + adv,
            "sprint" => "a person sprints " + DirText(dir, "forward"),
            "jump" => "a person jumps " + DirText(dir, "up") + adv,
            "fall" => dir == "backward" ? "a person falls backward to the ground" : dir == "forward" ? "a person falls forward to the ground" : "a person falls to the ground" + adv,
            "die" => "a person falls to the ground and dies",
            "look_back" => cue ? "a person is startled and looks back over their shoulder" : "a person looks back over their shoulder" + adv,
            "turn_around" => dir is "left" or "right" ? "a person turns " + DirText(dir, "") + adv : "a person turns around" + adv,
            "crouch" => "a person crouches down" + adv,
            "sit" => "a person sits down" + adv,
            "stand_up" => "a person stands up" + adv,
            "kick" => "a person kicks forward with the right leg" + adv,
            "punch" => "a person punches forward with the right hand" + adv,
            "wave" => "a person waves with the right hand" + adv,
            "dance" => "a person dances" + adv,
            "idle" => "a person stands still",
            "stop" => "a person stops and stands still",
            "step_back" => "a person steps backward" + adv,
            "dodge" => "a person dodges " + (dir is "left" or "right" ? DirText(dir, "") : "to the side") + adv,
            "roll" => "a person rolls forward on the ground",
            "climb" => "a person climbs up" + adv,
            "throw" => "a person throws something forward" + adv,
            "pick_up" => "a person bends down and picks something up from the ground" + adv,
            _ => "a person " + action.Replace('_', ' '),
        };
    }

    static MotionPlan Allocate(string prompt, List<Reading> readings, double total, List<string> warnings)
    {
        var n = readings.Count;
        var d = new double[n];
        var fixedSum = readings.Where(r => r.Seconds is not null).Sum(r => r.Seconds!.Value);
        var free = Enumerable.Range(0, n).Where(i => readings[i].Seconds is null).ToList();
        if (free.Count == 0)
        {
            for (var i = 0; i < n; i++) d[i] = readings[i].Seconds!.Value;
            if (Math.Abs(fixedSum - total) > 0.05 && n > 1)
                warnings.Add(string.Format(CultureInfo.InvariantCulture, "The steps' own lengths add up to {0:0.##} s (requested {1:0.##} s).", fixedSum, total));
        }
        else
        {
            for (var i = 0; i < n; i++) if (readings[i].Seconds is double s) d[i] = s;
            var remaining = total - fixedSum;
            var min = MinSegmentSeconds * free.Count;
            if (remaining < min - 1e-9)
            {
                remaining = min;
                warnings.Add(string.Format(CultureInfo.InvariantCulture, "The length was raised to {0:0.##} s so every step gets at least {1:0.##} s.", fixedSum + min, MinSegmentSeconds));
            }
            // Share by weight; steps below the minimum get the minimum and the rest is shared again.
            var open = new List<int>(free);
            var left = remaining;
            while (open.Count > 0)
            {
                var w = open.Sum(i => readings[i].Weight);
                var low = open.Where(i => left * readings[i].Weight / w < MinSegmentSeconds).ToList();
                if (low.Count == 0)
                {
                    foreach (var i in open) d[i] = left * readings[i].Weight / w;
                    break;
                }
                foreach (var i in low) { d[i] = MinSegmentSeconds; left -= MinSegmentSeconds; open.Remove(i); }
            }
        }
        var sum = d.Sum();
        if (sum > MotionRequest.MaxDurationSeconds)
        {
            var k = MotionRequest.MaxDurationSeconds / sum;
            for (var i = 0; i < n; i++) d[i] *= k;
            warnings.Add(string.Format(CultureInfo.InvariantCulture, "The steps were shortened to fit the {0:0} s limit.", MotionRequest.MaxDurationSeconds));
        }
        // Two decimals; the last free step absorbs the rounding so the total is exact.
        var target = Math.Round(d.Sum(), 2);
        for (var i = 0; i < n; i++) d[i] = Math.Round(d[i], 2);
        var absorb = free.Count > 0 ? free[^1] : n - 1;
        d[absorb] = Math.Round(d[absorb] + target - d.Sum(), 2);
        var segments = readings.Select((r, i) => new MotionSegmentPlan(i, r.Text, r.Caption, r.Action, d[i], r.Understood, r.Seconds is not null, r.IsArabic,
            r.Understood ? ProceduralKeyword(r.Action) : null, r.Unrecognized)).ToList();
        foreach (var s in segments.Where(s => !s.Understood))
            warnings.Add($"Step {s.Index + 1} was not understood: \"{s.Text}\". Rephrase it (for example «يمشي»، «يجري»، «يقع») or write it in English.");
        return new MotionPlan(prompt, Math.Round(d.Sum(), 2), segments, warnings);
    }
}
