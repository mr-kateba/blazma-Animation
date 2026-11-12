using System.Globalization;
using System.Text;

namespace Blazma.AI;

/// <summary>A spelling correction or hint for one word ("هل تقصد: يجري؟").</summary>
/// <param name="Word">The word as the user wrote it.</param>
/// <param name="Candidates">Dictionary words it may have meant (best first, display spelling).</param>
/// <param name="Applied">True when the planner already read the word as <c>Candidates[0]</c> (a confident typo fix); false when it is only a hint.</param>
/// <param name="Confidence">0..1 confidence of the best candidate.</param>
/// <param name="Message">Ready-to-show text in the word's language («هل تقصد: يجري؟» / "Did you mean: run?").</param>
public sealed record MotionSuggestion(string Word, IReadOnlyList<string> Candidates, bool Applied, double Confidence, string Message);

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
    bool DurationExplicit, bool IsArabic, string? ProceduralPrompt, IReadOnlyList<string> Unrecognized)
{
    /// <summary>0..1: how sure the planner is that the caption says what the user meant (typo fixes, unknown words and pass-through lower it).</summary>
    public double Confidence { get; init; } = 1;

    /// <summary>Typo corrections applied and hints for words that were not understood.</summary>
    public IReadOnlyList<MotionSuggestion> Suggestions { get; init; } = Array.Empty<MotionSuggestion>();

    /// <summary>Actions layered on the main one in the same step ("يمشي وهو يلوّح": Action walk, Layers [wave]).</summary>
    public IReadOnlyList<string> Layers { get; init; } = Array.Empty<string>();
}

/// <summary>A prompt split into ordered steps with their lengths (see <see cref="MotionPromptPlanner"/>).</summary>
public sealed record MotionPlan(string Prompt, double TotalSeconds, IReadOnlyList<MotionSegmentPlan> Segments, IReadOnlyList<string> Warnings)
{
    public bool AllUnderstood => Segments.All(s => s.Understood);

    /// <summary>The lowest step confidence (1 for an empty plan).</summary>
    public double Confidence => Segments.Count == 0 ? 1 : Segments.Min(s => s.Confidence);
}

/// <summary>An explicit step (agents' <c>segments</c>, or the dialog's edited plan). <paramref name="Caption"/> overrides the translation.</summary>
public sealed record SegmentInput(string Text, double? DurationSeconds = null, string? Caption = null);

/// <summary>
/// Offline motion prompt planner (DECISIONS D45). Kimodo generates ONE motion per prompt and its text encoder knows English motion
/// captions, so a prompt like «في واحد وراك اجري وبعدها موت» ("someone is behind you, run, and then die") collapsed into a single
/// action. The planner splits the prompt into ordered steps on Arabic and English sequencing connectors (ثم، بعدين، وبعدها، then,
/// after that…), punctuation, and «و»/"and" when it joins two different actions; reads per-step duration, speed, manner, direction,
/// body-part, count and negation hints; maps context clauses ("someone is behind you") to an action (look back, startled); translates
/// each step to a natural English caption with a curated multi-dialect dictionary; and allocates the requested total length across
/// the steps with per-action defaults. Arabic is normalised (diacritics, tatweel, hamza forms, Persian letters, digits, stretched
/// letters), prefixes and suffixes are stemmed, and typos are repaired by a weighted edit distance with a confidence score. No
/// network, no language model: deterministic and instant.
/// </summary>
public static partial class MotionPromptPlanner
{
    public const int MaxSegments = 12;
    public const double MinSegmentSeconds = 0.4;

    // ------------------------------------------------------------------ dictionary

    enum K { Action, Cue, Speed, Dir, Connector, And, Concurrent, Number, Unit, UnitWithCount, Filler, Manner, Part, Repeat, Times, StepWord, Negation }

    sealed record Entry(K Kind, string Value = "", double Number = 0);

    static readonly Dictionary<string, Entry> Words = new(StringComparer.Ordinal);
    static readonly List<(string[] Tokens, Entry Entry)> Phrases = new();
    static readonly Dictionary<string, string> Display = new(StringComparer.Ordinal);
    static readonly List<string> Conflicts = new();

    /// <summary>Dictionary words registered twice with different meanings (a dictionary self-check; empty when consistent).</summary>
    public static IReadOnlyList<string> LexiconConflicts => Conflicts;

    /// <summary>Number of distinct words and phrases in the dictionary (single words + multi-word phrases).</summary>
    public static int LexiconSize => Words.Count + Phrases.Count;

    /// <summary>Action → (group, strength). Steps are split only between actions of different groups; within a group the stronger wins.</summary>
    static readonly Dictionary<string, (string Group, int Strength)> Groups = new()
    {
        ["walk"] = ("walk", 0), ["sneak"] = ("walk", 1), ["limp"] = ("walk", 1), ["stagger"] = ("walk", 1),
        ["run"] = ("run", 0), ["sprint"] = ("run", 1), ["jump"] = ("jump", 0),
        ["fall"] = ("fall", 0), ["die"] = ("fall", 1),
        ["look_back"] = ("turn", 0), ["turn_around"] = ("turn", 1), ["look"] = ("turn", 0), ["look_around"] = ("look_around", 0),
        ["crouch"] = ("crouch", 0), ["kneel"] = ("kneel", 0), ["sit"] = ("sit", 0), ["stand_up"] = ("stand_up", 0), ["lie_down"] = ("lie_down", 0),
        ["sleep"] = ("lie_down", 1), ["crawl"] = ("crawl", 0),
        ["kick"] = ("kick", 0), ["punch"] = ("punch", 0), ["slap"] = ("punch", 1), ["slash"] = ("punch", 1), ["stab"] = ("punch", 1), ["headbutt"] = ("punch", 1),
        ["wave"] = ("wave", 0), ["point"] = ("point", 0), ["salute"] = ("salute", 0), ["bow"] = ("bow", 0), ["clap"] = ("clap", 0),
        ["dance"] = ("dance", 0), ["idle"] = ("idle", 0), ["stop"] = ("idle", 1),
        ["step_back"] = ("step_back", 0), ["dodge"] = ("dodge", 0), ["roll"] = ("roll", 0), ["climb"] = ("climb", 0), ["swim"] = ("swim", 0),
        ["throw"] = ("throw", 0), ["catch"] = ("catch", 0), ["pick_up"] = ("pick_up", 0), ["carry"] = ("carry", 0),
        ["push"] = ("push", 0), ["pull"] = ("pull", 0), ["block"] = ("block", 0),
        ["cheer"] = ("cheer", 0), ["celebrate"] = ("cheer", 1), ["cry"] = ("cry", 0), ["laugh"] = ("laugh", 1), ["smile"] = ("laugh", 0),
        ["think"] = ("think", 0), ["talk"] = ("talk", 0), ["phone_call"] = ("talk", 1), ["shout"] = ("talk", 1), ["sing"] = ("talk", 1),
        ["get_hit"] = ("hit", 0), ["startle"] = ("startle", 0), ["tremble"] = ("startle", 0),
        ["fight_stance"] = ("stance", 0), ["aim"] = ("aim", 0), ["shoot"] = ("aim", 1),
        ["drink"] = ("drink", 0), ["eat"] = ("eat", 0), ["yawn"] = ("yawn", 0), ["stretch"] = ("stretch", 0),
        ["nod"] = ("nod", 0), ["shake_head"] = ("nod", 1), ["shrug"] = ("shrug", 0), ["hug"] = ("hug", 0),
    };

    /// <summary>Default length (s) of each action, used as weights when the total is shared out.</summary>
    static readonly Dictionary<string, double> Defaults = new()
    {
        ["walk"] = 4, ["sneak"] = 4, ["limp"] = 4, ["stagger"] = 4, ["run"] = 3.5, ["sprint"] = 3, ["jump"] = 1.5, ["fall"] = 2, ["die"] = 2.5,
        ["look_back"] = 1.2, ["turn_around"] = 1.2, ["look"] = 1.2, ["look_around"] = 2.5,
        ["crouch"] = 1.5, ["kneel"] = 1.8, ["sit"] = 2, ["stand_up"] = 1.5, ["lie_down"] = 2.5, ["sleep"] = 3, ["crawl"] = 3.5,
        ["kick"] = 1.2, ["punch"] = 1, ["slap"] = 1, ["slash"] = 1.2, ["stab"] = 1.2, ["headbutt"] = 1.2,
        ["wave"] = 2, ["point"] = 1.5, ["salute"] = 1.5, ["bow"] = 2, ["clap"] = 2.5, ["dance"] = 4, ["idle"] = 1.5, ["stop"] = 1.5,
        ["step_back"] = 1.2, ["dodge"] = 1, ["roll"] = 1.5, ["climb"] = 3, ["swim"] = 4, ["throw"] = 1.2, ["catch"] = 1.2, ["pick_up"] = 1.8, ["carry"] = 4,
        ["push"] = 2, ["pull"] = 2, ["block"] = 1.2, ["cheer"] = 3, ["celebrate"] = 3.5, ["cry"] = 3.5, ["laugh"] = 3, ["smile"] = 2, ["think"] = 3,
        ["talk"] = 3, ["phone_call"] = 4, ["shout"] = 2.5, ["sing"] = 4, ["get_hit"] = 1.5, ["startle"] = 1.2, ["tremble"] = 3,
        ["fight_stance"] = 1.8, ["aim"] = 2, ["shoot"] = 1.5, ["drink"] = 2.5, ["eat"] = 3, ["yawn"] = 2.5, ["stretch"] = 3, ["nod"] = 1.2,
        ["shake_head"] = 1.5, ["shrug"] = 1.2, ["hug"] = 2.5,
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

    static void Put(string norm, Entry entry, string original, bool derived)
    {
        if (Words.TryGetValue(norm, out var old))
        {
            if (!derived && (old.Kind != entry.Kind || old.Value != entry.Value || old.Number != entry.Number)) Conflicts.Add($"{norm}: {old.Kind}/{old.Value} vs {entry.Kind}/{entry.Value}");
            if (derived) return;
        }
        Words[norm] = entry;
        if (!derived && !Display.ContainsKey(norm)) Display[norm] = original;
    }

    static void W(K kind, string value, params string[] forms)
    {
        foreach (var f in forms)
        {
            var toks = f.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Normalize).ToArray();
            if (toks.Length == 1) Put(toks[0], new Entry(kind, value), f, false);
            else if (toks.Length > 1) Phrases.Add((toks, new Entry(kind, value)));
        }
    }

    static void A(string action, params string[] forms) => W(K.Action, action, forms);

    /// <summary>Arabic forms separated by '|'. Third-person present forms (يفعل, 4+ letters) also register the imperative (افعل), the
    /// second/third-feminine (تفعل) and the "we" (نفعل) forms, unless those words are already taken.</summary>
    static void AR(string action, string forms)
    {
        foreach (var f in forms.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // "A and B" compounds are never phrases: the planner splits them into two steps.
            if (System.Text.RegularExpressions.Regex.IsMatch(f, @"\sو[يتان]")) continue;
            A(action, f);
            if (f.Length >= 4 && f[0] == 'ي' && !f.Contains(' '))
                foreach (var p in new[] { "ا", "ت", "ن" })
                    Put(Normalize(p + f[1..]), new Entry(K.Action, action), f, true);
        }
    }

    static void Cross(string action, string[] first, string[] second)
    {
        foreach (var a in first) foreach (var b in second) A(action, a + " " + b);
    }

    static MotionPromptPlanner()
    {
        LoadEnglishLexicon();
        LoadArabicLexicon();
        LoadFunctionWords();
        BuildFuzzyIndex();
    }

    static readonly Dictionary<string, double> NumberWords = new(StringComparer.Ordinal);

    // ------------------------------------------------------------------ text

    /// <summary>Lower-case; removes Arabic diacritics, tatweel, Quranic marks and zero-width marks; unifies alef/hamza forms (أإآٱ→ا, ؤ→و, ئ→ي),
    /// ى/ی→ي, ة/ہ→ه, Persian/Urdu letters (ک گ چ→ك, پ→ب, ڤ→ف…); Arabic-Indic and Eastern digits → ASCII; collapses a letter repeated three or
    /// more times to one ("يجرررري" → "يجري").</summary>
    public static string Normalize(string s)
    {
        s = s.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(s.Length);
        var prev = '\0';
        var run = 0;
        foreach (var ch in s)
        {
            if (ch is >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ' or 'ء' or >= 'ۖ' and <= 'ۭ' or >= '​' and <= '‏' or >= '‪' and <= '‮' or '؜' or '﻿') continue;
            var m = ch switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' or 'ٲ' or 'ٳ' => 'ا',
                'ى' or 'ی' or 'ے' or 'ێ' or 'ۍ' => 'ي',
                'ة' or 'ہ' or 'ھ' or 'ۀ' or 'ۃ' => 'ه',
                'ؤ' or 'ۆ' or 'ۇ' => 'و',
                'ئ' => 'ي',
                'ک' or 'گ' or 'چ' or 'ڪ' or 'ݣ' => 'ك',
                'پ' => 'ب',
                'ڤ' => 'ف',
                'ڨ' => 'ق',
                'ٹ' => 'ت',
                'ڈ' => 'د',
                'ڑ' => 'ر',
                'ں' => 'ن',
                '’' or '‘' => '\'',
                >= '٠' and <= '٩' => (char)('0' + (ch - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (ch - '۰')),
                _ => char.ToLowerInvariant(ch),
            };
            if (char.IsLetter(m) && m == prev)
            {
                run++;
                if (run == 3) sb.Length--;
                if (run >= 3) continue;
            }
            else run = 1;
            prev = m;
            sb.Append(m);
        }
        return sb.ToString();
    }

    public static bool HasArabic(string s) => s.Any(c => c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ');

    sealed record Token(string Orig, string Norm, int Start, int End, bool Punct);

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format || c is '\'' or '’' or 'ـ' or '&';

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
                var norm = Normalize(orig).Trim('\'');
                if (norm.Length > 0) tokens.Add(new Token(orig, norm, start, i, false));
                continue;
            }
            if (c is '،' or ',' or ';' or '؛' or '.' or '!' or '?' or '؟' or '\n' or '…' or ':' or '-' or '—' or '–')
                tokens.Add(new Token(c.ToString(), c.ToString(), i, i + 1, true));
            i++;
        }
        return tokens;
    }

    /// <summary>A dictionary hit over tokens [Start, End).</summary>
    sealed record Item(int Start, int End, Entry? Entry, bool AndPrefix, bool Punct, double? Seconds = null)
    {
        public int? Repeat { get; init; }
        public int? Steps { get; init; }
        public bool Negated { get; init; }
        public double Conf { get; init; } = 1;
        public string? Canonical { get; init; }
        public IReadOnlyList<string>? Suggest { get; init; }
    }

    static bool Unknown(Item x) => x.Entry is null && x.Seconds is null && x.Repeat is null && x.Steps is null;

    // ------------------------------------------------------------------ resolving words: stemming + typo repair

    sealed record Res(Entry? Entry, double Conf, bool AndPrefix, string? Canonical, IReadOnlyList<string>? Suggest);

    static readonly string[] Conj = { "", "و", "ف" };
    static readonly string[] Pre = { "", "ب", "ل", "س", "ح", "ه", "ك", "ال", "بال", "لل", "كال", "كي" };
    static readonly string[] Suf = { "", "ها", "هم", "هن", "ه", "كم", "ك", "نا", "ني", "وا", "ون", "ين", "ان", "ات", "ت", "تا" };

    static readonly (string Conj, string Pre, string Suf)[] Affixes =
        (from c in Conj from p in Pre from s in Suf select (c, p, s)).OrderBy(x => x.c.Length + x.p.Length + x.s.Length).ToArray();

    static bool Allowed(Entry e, string conj, string pre, string suf, string stem)
    {
        var verb = e.Kind is K.Action or K.Cue;
        var noun = verb || e.Kind is K.Dir or K.Speed or K.Manner or K.Part or K.Filler or K.Negation;
        if (suf.Length > 0) return (e.Kind is K.Action or K.Part) && stem.Length >= 3 && pre is "" or "ب" or "ل" or "س" or "ح" or "ه" or "كي" or "ال" or "بال" or "لل";
        if (pre is "س" or "ح" or "ه" or "كي") return e.Kind == K.Action && stem.Length >= 3 && stem[0] is 'ي' or 'ت' or 'ن' or 'ا';
        if (pre is "ب" or "ل" or "ك") return e.Kind == K.Action ? stem.Length >= 3 && stem[0] is 'ي' or 'ت' or 'ن' or 'ا' : noun && !verb && stem.Length >= 3;
        if (pre is "ال" or "بال" or "لل" or "كال") return noun && stem.Length >= 3;
        return noun && (e.Kind != K.Filler || conj.Length > 0);
    }

    static Res? Resolve(string norm)
    {
        if (Words.TryGetValue(norm, out var e)) return new Res(e, 1, false, null, null);
        var arabic = HasArabic(norm);
        var stems = new List<string>();
        if (arabic)
        {
            foreach (var (conj, pre, suf) in Affixes)
            {
                var head = conj.Length + pre.Length;
                if (head + suf.Length == 0 || head + suf.Length + 2 > norm.Length) continue;
                if (!norm.StartsWith(conj + pre, StringComparison.Ordinal) || !norm.EndsWith(suf, StringComparison.Ordinal)) continue;
                var stem = norm.Substring(head, norm.Length - head - suf.Length);
                if (Words.TryGetValue(stem, out e) && Allowed(e, conj, pre, suf, stem))
                    return new Res(e, 0.95, conj.Length > 0 && e.Kind is K.Action or K.Cue, null, null);
                if (stem.Length >= 4 && !stems.Contains(stem)) stems.Add(stem);
            }
        }
        else if (norm.Length >= 5 && norm.All(c => c is >= 'a' and <= 'z')) stems.Add(norm);
        if (arabic && norm.Length >= 4 && !stems.Contains(norm)) stems.Insert(0, norm);
        return stems.Count == 0 ? null : Fuzzy(norm, stems, arabic);
    }

    sealed record FuzzyKey(string Key, Entry Entry, string Display);

    static readonly Dictionary<int, List<FuzzyKey>> ArKeys = new(), EnKeys = new();

    static void BuildFuzzyIndex()
    {
        foreach (var (key, e) in Words)
        {
            if (e.Kind is not (K.Action or K.Dir or K.Speed or K.Manner or K.Part)) continue;
            var ar = HasArabic(key);
            if (key.Length < (ar ? 4 : 5) || (!ar && !key.All(c => c is >= 'a' and <= 'z'))) continue;
            var map = ar ? ArKeys : EnKeys;
            if (!map.TryGetValue(key.Length, out var list)) map[key.Length] = list = new List<FuzzyKey>();
            list.Add(new FuzzyKey(key, e, Display.TryGetValue(key, out var d) ? d : key));
        }
    }

    static double MaxApply(int len, bool arabic) => !arabic ? 1.0 : len <= 4 ? 0.5 : len <= 5 ? 1.0 : len <= 7 ? 1.5 : 2.0;

    static bool SameStart(char a, char b) => a == b || (a is 'ي' or 'ا' or 'ت' or 'ن' && b is 'ي' or 'ا' or 'ت' or 'ن');

    static Res? Fuzzy(string original, List<string> stems, bool arabic)
    {
        var map = arabic ? ArKeys : EnKeys;
        var found = new List<(FuzzyKey Key, double Dist, int Len)>();
        foreach (var stem in stems)
        {
            var limit = MaxApply(stem.Length, arabic) + (arabic ? 0.5 : 0);
            if (stem.Length <= 3) continue;
            for (var len = stem.Length - 2; len <= stem.Length + 2; len++)
            {
                if (!map.TryGetValue(len, out var list)) continue;
                foreach (var k in list)
                {
                    if (arabic && !SameStart(stem[0], k.Key[0])) continue;
                    var d = Distance(stem, k.Key, arabic);
                    if (d <= limit) found.Add((k, d, stem.Length));
                }
            }
        }
        if (found.Count == 0) return null;
        var best = found.Min(f => f.Dist);
        var top = found.Where(f => f.Dist <= best + 1e-9).ToList();
        var distinct = top.Select(f => (f.Key.Entry.Kind, f.Key.Entry.Value)).Distinct().Count();
        var first = top[0];
        var conf = Math.Round(Math.Max(0.3, 1 - first.Dist / first.Len), 2);
        var names = found.OrderBy(f => f.Dist).Select(f => f.Key.Display).Distinct().Take(3).ToList();
        if (distinct == 1 && first.Dist <= MaxApply(first.Len, arabic) && conf >= 0.6)
            return new Res(first.Key.Entry, conf, false, first.Key.Display, new[] { first.Key.Display });
        return new Res(null, 0, false, null, names);
    }

    static bool Soft(char c) => c is 'ا' or 'و' or 'ي' or 'ه';

    static readonly HashSet<(char, char)> Confusable = new()
    {
        ('ض', 'ظ'), ('ص', 'س'), ('ذ', 'ز'), ('ذ', 'د'), ('ث', 'س'), ('ث', 'ت'), ('ت', 'ط'), ('ق', 'ك'), ('ق', 'غ'), ('ج', 'ش'), ('ح', 'خ'), ('ع', 'غ'), ('ز', 'ظ'), ('س', 'ش'), ('ج', 'ح'), ('ه', 'ة'), ('ك', 'ق'), ('ص', 'ض'), ('ق', 'ف'), ('ف', 'غ'), ('ب', 'ي'), ('ب', 'ل'), ('ت', 'ن'), ('ن', 'م'), ('م', 'ك'), ('ك', 'ط'), ('ز', 'و'), ('ر', 'ز'), ('د', 'ر'), ('ط', 'ظ'), ('ث', 'ص'),
    };

    static double SubCost(char a, char b)
    {
        if (a == b) return 0;
        if (Soft(a) && Soft(b)) return 0.5;
        return Confusable.Contains((a, b)) || Confusable.Contains((b, a)) ? 0.5 : 1;
    }

    static double Indel(char c, bool arabic) => arabic && Soft(c) ? 0.5 : 1;

    static double Distance(string a, string b, bool arabic)
    {
        var n = a.Length;
        var m = b.Length;
        var d = new double[n + 1, m + 1];
        for (var i = 1; i <= n; i++) d[i, 0] = d[i - 1, 0] + Indel(a[i - 1], arabic);
        for (var j = 1; j <= m; j++) d[0, j] = d[0, j - 1] + Indel(b[j - 1], arabic);
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
            {
                var v = Math.Min(d[i - 1, j - 1] + SubCost(a[i - 1], b[j - 1]), Math.Min(d[i - 1, j] + Indel(a[i - 1], arabic), d[i, j - 1] + Indel(b[j - 1], arabic)));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) v = Math.Min(v, d[i - 2, j - 2] + 1);
                d[i, j] = v;
            }
        return d[n, m];
    }

    static string SuggestionMessage(string display, bool arabic) => arabic ? $"هل تقصد: {display}؟" : $"Did you mean: {display}?";

    // ------------------------------------------------------------------ annotating tokens

    static readonly System.Text.RegularExpressions.Regex Compact = new(@"^(\d+(?:[.,٫]\d+)?)(s|sec|secs|ث)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    static double? Number(string norm)
    {
        if (norm.Length > 0 && char.IsDigit(norm[0]) && double.TryParse(norm.Replace(',', '.').Replace('٫', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return NumberWords.TryGetValue(norm, out var n) ? n : null;
    }

    static double UnitSeconds(Entry u) => u.Value == "60" ? 60 : 1;

    static List<Item> Annotate(List<Token> t)
    {
        var items = new List<Item>();
        var i = 0;
        while (i < t.Count)
        {
            if (t[i].Punct) { items.Add(new Item(i, i + 1, null, false, true)); i++; continue; }
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
                if (j < t.Count && !t[j].Punct && Words.TryGetValue(t[j].Norm, out var u))
                {
                    if (u.Kind == K.Unit && num > 0)
                    {
                        items.Add(new Item(i, j + 1, null, false, false, num * UnitSeconds(u)));
                        i = j + 1;
                        continue;
                    }
                    if (u.Kind == K.Times && num is >= 1 and <= 99 && j == i + 1)
                    {
                        items.Add(new Item(i, j + 1, null, false, false) { Repeat = (int)num });
                        i = j + 1;
                        continue;
                    }
                    if (u.Kind == K.StepWord && num is >= 1 and <= 99 && j == i + 1)
                    {
                        items.Add(new Item(i, j + 1, null, false, false) { Steps = (int)num });
                        i = j + 1;
                        continue;
                    }
                }
                if (!Words.ContainsKey(t[i].Norm) || Words[t[i].Norm].Kind == K.Filler)
                {
                    items.Add(new Item(i, i + 1, new Entry(K.Filler), false, false));
                    i++;
                    continue;
                }
            }
            if (Words.TryGetValue(t[i].Norm, out var uc))
            {
                if (uc.Kind == K.UnitWithCount)
                {
                    items.Add(new Item(i, i + 1, null, false, false, uc.Number));
                    i++;
                    continue;
                }
                if (uc.Kind == K.Unit && HasArabic(t[i].Norm))
                {
                    // A bare Arabic unit: «ثانية» = 1 s, «ثانية ونص» = 1.5 s.
                    var secs = UnitSeconds(uc);
                    var end = i + 1;
                    if (end < t.Count && t[end].Norm is "ونص" or "ونصف") { secs *= 1.5; end++; }
                    else if (end < t.Count && t[end].Norm == "وربع") { secs *= 1.25; end++; }
                    items.Add(new Item(i, end, null, false, false, secs));
                    i = end;
                    continue;
                }
                if (uc.Kind == K.Repeat)
                {
                    items.Add(new Item(i, i + 1, uc, false, false) { Repeat = (int)uc.Number });
                    i++;
                    continue;
                }
                if (uc.Kind == K.StepWord && uc.Number > 0)
                {
                    items.Add(new Item(i, i + 1, uc, false, false) { Steps = (int)uc.Number });
                    i++;
                    continue;
                }
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
            var r = Resolve(t[i].Norm);
            items.Add(r?.Entry is null
                ? new Item(i, i + 1, null, false, false) { Suggest = r?.Suggest }
                : new Item(i, i + 1, r.Entry, r.AndPrefix, false) { Conf = r.Conf, Canonical = r.Canonical, Suggest = r.Suggest });
            i++;
        }
        return ApplyNegation(items);
    }

    /// <summary>«بدون ما يطيح» / "without falling": the action right after a negation word is marked negated (it is not performed).</summary>
    static List<Item> ApplyNegation(List<Item> items)
    {
        for (var n = 0; n < items.Count; n++)
        {
            if (items[n].Entry?.Kind != K.Negation) continue;
            for (var k = n + 1; k < items.Count && k <= n + 4; k++)
            {
                var it = items[k];
                if (it.Punct || it.Entry?.Kind is K.Connector or K.Negation) break;
                if (it.Entry?.Kind == K.Action) { items[k] = it with { Negated = true }; break; }
            }
        }
        return items;
    }

    static bool IsHit(Item it) => it.Entry is { Kind: K.Action or K.Cue } && !it.Negated;
    static string GroupOf(Item it) => Groups[it.Entry!.Value].Group;

    // ------------------------------------------------------------------ planning

    /// <summary>Splits <paramref name="prompt"/> into steps and shares <paramref name="totalSeconds"/> among them. A prompt with a
    /// single step gets the whole length (as before the planner existed).</summary>
    public static MotionPlan Plan(string prompt, double totalSeconds)
    {
        var warnings = new List<string>();
        var tokens = Tokenize(prompt ?? "");
        var items = Annotate(tokens);

        // 1. Hard boundaries: sequencing connectors and punctuation. A comma before an English "-ing" action ("walk, looking around")
        //    is not a boundary: the two happen together.
        var chunks = new List<(List<Item> Items, bool AfterConnector)>();
        var cur = new List<Item>();
        var afterConnector = false;
        for (var idx = 0; idx < items.Count; idx++)
        {
            var it = items[idx];
            if (it.Punct && cur.Count > 0 && LeadsGerund(items, tokens, idx + 1))
            {
                cur.Add(new Item(it.Start, it.End, new Entry(K.Concurrent, "punct"), false, false));
                continue;
            }
            if (it.Punct || it.Entry?.Kind == K.Connector)
            {
                var ended = cur.Count > 0;
                if (ended) chunks.Add((cur, afterConnector));
                cur = new List<Item>();
                if (it.Entry?.Kind == K.Connector) afterConnector = true;
                else if (ended) afterConnector = false;
                if (it.Entry is { Kind: K.Connector, Value: "sudden" }) cur.Add(new Item(it.Start, it.End, new Entry(K.Manner, "sudden"), false, false));
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
            var lead = false;
            var first = true;
            for (var k = 0; k < chunk.Count; k++)
            {
                var it = chunk[k];
                if (it.Entry?.Kind == K.Concurrent) { concurrent = true; if (prevHit is null) lead = true; }
                if (!IsHit(it)) continue;
                if (prevHit is not null && !concurrent && GroupOf(prevHit) != GroupOf(it) && it.Entry!.Kind != K.Cue)
                {
                    // A standalone «و»/"and" just before the action is the joint: drop it.
                    var andAt = -1;
                    for (var m = k - 1; m >= start && !IsHit(chunk[m]); m--) if (chunk[m].Entry?.Kind == K.And) { andAt = m; break; }
                    var cut = andAt >= 0 ? andAt : k;
                    // "suddenly" belongs to the step it introduces.
                    while (andAt < 0 && cut - 1 > start && chunk[cut - 1].Entry is { Kind: K.Manner, Value: "sudden" }) cut--;
                    var left = chunk.GetRange(start, cut - start);
                    if (left.Count > 0) { parts.Add((left, first && hard)); first = false; }
                    start = andAt >= 0 ? andAt + 1 : cut;
                }
                // "while he walks, waves": a lead "while" also covers the second action.
                concurrent = prevHit is null && lead;
                prevHit = it;
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
            var hasHit = part.Any(p => IsHit(p) || (p.Entry is { Kind: K.Manner } && ImpliesWalk(p.Entry.Value)));
            var substantive = part.Where(Unknown).ToList();
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

    static bool LeadsGerund(List<Item> items, List<Token> tokens, int from)
    {
        for (var k = from; k < items.Count; k++)
        {
            var it = items[k];
            if (it.Punct) return false;
            if (it.Entry?.Kind == K.Filler) continue;
            if (it.Entry?.Kind == K.Concurrent) return true;
            var norm = tokens[it.Start].Norm;
            return it.Entry?.Kind == K.Action && !it.Negated && norm.EndsWith("ing", StringComparison.Ordinal) && !HasArabic(norm);
        }
        return false;
    }

    static bool ImpliesWalk(string manner) => manner is "zombie" or "old" or "drunk";

    /// <summary>An explicit plan: each step is translated (unless it carries a caption); missing lengths share the rest of the total.</summary>
    public static MotionPlan FromSegments(IReadOnlyList<SegmentInput> segments, double totalSeconds)
    {
        var warnings = new List<string>();
        var readings = new List<Reading>();
        foreach (var s in segments.Take(MaxSegments))
        {
            var r = ReadText(s.Text);
            if (s.Caption is { } c && c.Trim().Length > 0)
                r = r with { Caption = c.Trim(), Understood = true, Action = r.Action ?? ReadText(c).Action, Confidence = 1 };
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
            r.IsArabic, ProceduralKeyword(r.Action), r.Unrecognized) { Confidence = r.Confidence, Suggestions = r.Suggestions, Layers = r.Layers };
    }

    sealed record Reading(string Text, string Caption, string? Action, double? Seconds, double Weight, bool Understood, bool IsArabic, IReadOnlyList<string> Unrecognized)
    {
        public double Confidence { get; init; } = 1;
        public IReadOnlyList<MotionSuggestion> Suggestions { get; init; } = Array.Empty<MotionSuggestion>();
        public IReadOnlyList<string> Layers { get; init; } = Array.Empty<string>();
    }

    static Reading ReadText(string text)
    {
        var tokens = Tokenize(text);
        var items = Annotate(tokens)
            .Select(i => i.Entry is { Kind: K.Connector, Value: "sudden" } ? i with { Entry = new Entry(K.Manner, "sudden") } : i)
            .Where(i => !i.Punct && i.Entry?.Kind != K.Connector).ToList();
        return Read(text, tokens, items);
    }

    static Reading Read(string source, List<Token> tokens, List<Item> items)
    {
        var words = items.Where(i => !i.Punct && i.Entry?.Kind is not K.Connector).ToList();
        // Drop a leading joint «و»/"and" left by the split.
        while (words.Count > 0 && words[0].Entry?.Kind == K.And) words.RemoveAt(0);
        var text = words.Count == 0 ? "" : source[tokens[words[0].Start].Start..tokens[words[^1].End - 1].End].Trim();
        var arabic = HasArabic(text);

        // ---- actions: one representative per group (the strongest), in order of appearance; context clauses (cues) only count alone.
        var order = new List<string>();
        var rep = new Dictionary<string, (string Action, int Strength, bool Cue)>();
        foreach (var hit in words.Where(IsHit))
        {
            var a = hit.Entry!.Value;
            var (group, strength) = Groups[a];
            var cue = hit.Entry.Kind == K.Cue;
            if (!rep.TryGetValue(group, out var r)) { order.Add(group); rep[group] = (a, strength, cue); }
            else if (strength > r.Strength) rep[group] = (a, strength, cue);
            else if (!cue && r.Cue) rep[group] = (a, strength, false);
        }
        string? action = null;
        var cueOnly = false;
        var layers = new List<string>();
        var real = order.Where(g => !rep[g].Cue).ToList();
        if (real.Count > 0)
        {
            action = rep[real[0]].Action;
            layers = real.Skip(1).Select(g => rep[g].Action).ToList();
        }
        else if (order.Count > 0) { action = rep[order[0]].Action; cueOnly = true; }

        // ---- modifiers
        var consumed = new HashSet<Item>();
        string? part = null, side = null;
        for (var k = 0; k < words.Count; k++)
        {
            if (words[k].Entry is not { Kind: K.Part } pe) continue;
            part = pe.Value;
            if (pe.Number > 0) side = "both";
            foreach (var step in new[] { 1, -1, 2, -2 })
            {
                var n = k;
                var skipped = 0;
                Item? found = null;
                while (skipped < 3)
                {
                    n += Math.Sign(step);
                    if (n < 0 || n >= words.Count) break;
                    var w = words[n];
                    if (w.Entry?.Kind == K.Filler) { skipped++; continue; }
                    if (w.Entry is { Kind: K.Dir, Value: "left" or "right" }) found = w;
                    break;
                }
                if (found is not null) { side = found.Entry!.Value; consumed.Add(found); break; }
            }
        }
        var speed = words.LastOrDefault(w => w.Entry?.Kind == K.Speed)?.Entry!.Value;
        var dir = words.LastOrDefault(w => w.Entry?.Kind == K.Dir && !consumed.Contains(w))?.Entry!.Value;
        var manners = words.Where(w => w.Entry?.Kind == K.Manner).Select(w => w.Entry!.Value).Distinct().ToList();
        var seconds = words.LastOrDefault(w => w.Seconds is not null)?.Seconds;
        if (seconds is double s0) seconds = Math.Clamp(s0, MotionRequest.MinDurationSeconds, MotionRequest.MaxDurationSeconds);
        var repeat = words.LastOrDefault(w => w.Repeat is > 1)?.Repeat;
        var steps = words.LastOrDefault(w => w.Steps is > 0)?.Steps;
        var negs = words.Where(w => w.Negated).Select(w => w.Entry!.Value).Distinct().ToList();
        var unknown = words.Where(Unknown).SelectMany(w => Enumerable.Range(w.Start, w.End - w.Start)).Select(k => tokens[k].Orig).ToList();

        // ---- refinements
        if (side is null && dir is "left" or "right" && action is "wave" or "punch" or "kick" or "point" or "salute" or "slap") { side = dir; dir = null; }
        if (action == "punch" && part == "leg") action = "kick";
        else if (action == "punch" && part == "head") action = "headbutt";
        if (action == "look" && dir == "backward") action = "look_back";
        var emotionOnly = false;
        if (action is null)
        {
            if (steps is not null) action = dir == "backward" ? "step_back" : "walk";
            else if (manners.Any(ImpliesWalk)) action = "walk";
            else if (manners.Any(m => m is not "sudden")) { action = "idle"; emotionOnly = true; }
            else if (negs.Count > 0) action = "idle";
        }
        layers.Remove(action ?? "");

        var groups = order.Count(g => !rep[g].Cue);
        string caption;
        bool understood;
        double confidence;
        if (action is not null && (arabic || unknown.Count == 0))
        {
            caption = BuildCaption(new Ctx(action, speed, dir, cueOnly, side, part, manners, repeat, steps, layers, negs, emotionOnly));
            understood = true;
            var known = words.Where(w => !Unknown(w) && w.Entry?.Kind is not (K.Filler or K.And or K.Concurrent)).ToList();
            var actionConf = known.Count == 0 ? 1 : known.Min(w => w.Conf);
            var coverage = known.Count + unknown.Count == 0 ? 1 : (double)known.Count / (known.Count + unknown.Count);
            confidence = Math.Round(Math.Min(1, actionConf * (0.55 + 0.45 * coverage)), 2);
        }
        else if (!arabic)
        {
            // English the dictionary does not fully cover: Kimodo reads English, so it is sent as written (minus timing words).
            var kept = words.Where((w, k) => w.Seconds is null && w.Entry is not { Kind: K.Concurrent, Value: "punct" }
                && !(k + 1 < words.Count && words[k + 1].Seconds is not null && tokens[w.Start].Norm is "for" or "during" or "لمده")).ToList();
            caption = kept.Count == 0 ? text : string.Join(' ', kept.SelectMany(w => Enumerable.Range(w.Start, w.End - w.Start)).Select(k => tokens[k].Orig));
            understood = caption.Length > 0;
            confidence = action is not null ? 0.6 : 0.5;
        }
        else
        {
            caption = text;
            understood = false;
            confidence = 0;
        }
        var layered = layers.Count;
        var weight = (layered > 0 ? new[] { action }.Concat(layers).Max(DefaultSeconds) : DefaultSeconds(action)) * (speed == "slow" ? 1.3 : speed == "fast" ? 0.85 : 1);
        if (steps is not null) weight = Math.Max(1, steps.Value * 0.7);
        if (repeat is int rp) weight *= 1 + 0.6 * (Math.Min(rp, 6) - 1);

        var sugg = new List<MotionSuggestion>();
        foreach (var w in words.Where(w => w.Suggest is { Count: > 0 }))
        {
            var orig = string.Join(' ', Enumerable.Range(w.Start, w.End - w.Start).Select(k => tokens[k].Orig));
            var isAr = HasArabic(orig);
            sugg.Add(new MotionSuggestion(orig, w.Suggest!, w.Canonical is not null, w.Entry is null ? 0 : w.Conf, SuggestionMessage(string.Join(isAr ? "، " : ", ", w.Suggest!), isAr)));
        }
        return new Reading(text, caption, action, seconds, weight, understood, arabic, arabic ? unknown : Array.Empty<string>())
        {
            Confidence = confidence, Suggestions = sugg, Layers = layers,
        };
    }

    // ------------------------------------------------------------------ captions

    sealed record Ctx(string Action, string? Speed, string? Dir, bool Cue, string? Side, string? Part, IReadOnlyList<string> Manners, int? Repeat, int? Steps,
        IReadOnlyList<string> Layers, IReadOnlyList<string> Negs, bool EmotionOnly);

    static string DirText(string? dir, string fallback) => dir switch
    {
        "forward" => "forward",
        "backward" => "backward",
        "left" => "to the left",
        "right" => "to the right",
        "in_place" => "in place",
        "side" => "sideways",
        "circle" => "in a circle",
        "up" => fallback == "forward" ? "forward" : "up",
        "down" => fallback == "forward" ? "forward" : "down",
        _ => fallback,
    };

    static readonly string[] NumberNames = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten" };

    static string NumberName(int n) => n < NumberNames.Length ? NumberNames[n] : n.ToString(CultureInfo.InvariantCulture);

    static string Limb(Ctx c, string part, string defaultSide = "right") => c.Side == "both" ? $"both {part}s" : $"the {c.Side ?? defaultSide} {part}";

    static readonly Dictionary<string, string> Gerunds = new()
    {
        ["walk"] = "walking", ["sneak"] = "sneaking", ["limp"] = "limping", ["stagger"] = "staggering", ["run"] = "running", ["sprint"] = "sprinting",
        ["jump"] = "jumping", ["fall"] = "falling", ["die"] = "dying", ["look_back"] = "looking back", ["turn_around"] = "turning around", ["look"] = "looking",
        ["look_around"] = "looking around", ["crouch"] = "crouching", ["kneel"] = "kneeling", ["sit"] = "sitting", ["stand_up"] = "standing up",
        ["lie_down"] = "lying down", ["sleep"] = "sleeping", ["crawl"] = "crawling", ["kick"] = "kicking", ["punch"] = "punching", ["slap"] = "slapping",
        ["slash"] = "slashing with a sword", ["stab"] = "stabbing", ["headbutt"] = "headbutting", ["wave"] = "waving", ["point"] = "pointing",
        ["salute"] = "saluting", ["bow"] = "bowing", ["clap"] = "clapping", ["dance"] = "dancing", ["idle"] = "standing still", ["stop"] = "stopping",
        ["step_back"] = "stepping back", ["dodge"] = "dodging", ["roll"] = "rolling", ["climb"] = "climbing", ["swim"] = "swimming", ["throw"] = "throwing something",
        ["catch"] = "catching something", ["pick_up"] = "picking something up", ["carry"] = "carrying something heavy", ["push"] = "pushing something",
        ["pull"] = "pulling something", ["block"] = "blocking", ["cheer"] = "cheering", ["celebrate"] = "celebrating", ["cry"] = "crying", ["laugh"] = "laughing",
        ["smile"] = "smiling", ["think"] = "thinking", ["talk"] = "talking", ["phone_call"] = "talking on the phone", ["shout"] = "shouting", ["sing"] = "singing",
        ["get_hit"] = "getting hit", ["startle"] = "being startled", ["tremble"] = "trembling", ["fight_stance"] = "holding a fighting stance",
        ["aim"] = "aiming a gun", ["shoot"] = "shooting", ["drink"] = "drinking", ["eat"] = "eating", ["yawn"] = "yawning", ["stretch"] = "stretching",
        ["nod"] = "nodding", ["shake_head"] = "shaking their head", ["shrug"] = "shrugging", ["hug"] = "hugging someone",
    };

    static string Gerund(string action) => Gerunds.TryGetValue(action, out var g) ? g : action.Replace('_', ' ');

    static string MannerPhrase(string manner, string action)
    {
        var moving = action is "walk" or "run" or "sprint" or "crawl" or "sneak" or "limp" or "stagger";
        return manner switch
        {
            "zombie" => moving ? "like a zombie, with stiff arms stretched out" : "like a zombie",
            "old" => moving ? "like an old person, hunched over" : "like an old person",
            "drunk" => moving ? "unsteadily, like a drunk person" : "like a drunk person",
            "tired" => "wearily, as if exhausted",
            "scared" => "fearfully",
            "angry" => "angrily",
            "happy" => "happily",
            "sad" => "sadly, with drooping shoulders",
            "nervous" => "nervously",
            "careful" => "carefully",
            "forceful" => "forcefully",
            "confident" => "confidently",
            _ => "",
        };
    }

    static string EmotionCaption(string manner) => manner switch
    {
        "scared" => "a person looks scared and tense",
        "angry" => "a person stands angrily with clenched fists",
        "happy" => "a person stands smiling happily",
        "sad" => "a person stands sadly with their head down",
        "tired" => "a person stands tired and exhausted",
        "nervous" => "a person stands nervously, fidgeting",
        "confident" => "a person stands confidently with a straight back",
        "careful" => "a person stands alert and cautious",
        "forceful" => "a person stands with a strong, firm posture",
        _ => "a person stands still",
    };

    static string BuildCaption(Ctx c)
    {
        string core;
        if (c.EmotionOnly && c.Action == "idle" && c.Manners.FirstOrDefault(m => m != "sudden") is { } em) core = EmotionCaption(em);
        else core = Core(c);
        var sb = new StringBuilder(core);
        if (!c.EmotionOnly)
            foreach (var m in c.Manners.Where(m => m != "sudden"))
            {
                var p = MannerPhrase(m, c.Action);
                if (p.Length > 0 && !core.Contains(p, StringComparison.Ordinal) && !(m == "forceful" && c.Action == "punch")) sb.Append(' ').Append(p);
            }
        if (c.Repeat is int r && r > 1) sb.Append(r == 2 ? " twice" : $" {NumberName(r)} times");
        if (c.Negs.Count > 0) sb.Append(" without ").Append(string.Join(" or ", c.Negs.Select(Gerund)));
        if (c.Layers.Count > 0) sb.Append(" while ").Append(string.Join(" and ", c.Layers.Select(Gerund)));
        var s = sb.ToString();
        if (c.Manners.Contains("sudden") && s.StartsWith("a person ", StringComparison.Ordinal)) s = "a person suddenly " + s["a person ".Length..];
        return s;
    }

    static string Core(Ctx c)
    {
        var speed = c.Speed;
        var dir = c.Dir;
        var adv = speed switch { "fast" => " quickly", "slow" => " slowly", _ => "" };
        var steps = c.Steps is int st && st > 0 ? (st == 1 ? "one step" : NumberName(st) + " steps") : null;
        return c.Action switch
        {
            "walk" => steps is not null ? "a person takes " + steps + " " + DirText(dir, "forward") + adv : "a person walks " + DirText(dir, "forward") + adv,
            "sneak" => "a person sneaks " + DirText(dir, "forward") + " stealthily, crouched low" + adv,
            "limp" => "a person limps " + DirText(dir, "forward") + adv,
            "stagger" => "a person staggers " + DirText(dir, "forward") + " unsteadily" + adv,
            "run" => speed == "slow" ? "a person jogs " + DirText(dir, "forward") : "a person runs " + DirText(dir, "forward") + adv,
            "sprint" => "a person sprints " + DirText(dir, "forward"),
            "jump" => "a person jumps " + DirText(dir, "up") + adv,
            "fall" => dir == "backward" ? "a person falls backward to the ground" : dir == "forward" ? "a person falls forward to the ground" : "a person falls to the ground" + adv,
            "die" => "a person falls to the ground and dies",
            "look_back" => c.Cue ? "a person is startled and looks back over their shoulder" : dir is "left" or "right" ? "a person looks to the " + dir : "a person looks back over their shoulder" + adv,
            "look" => dir switch { "left" => "a person looks to the left", "right" => "a person looks to the right", "up" => "a person looks up", "down" => "a person looks down", _ => "a person looks ahead" } + adv,
            "look_around" => "a person looks around" + adv,
            "turn_around" => dir is "left" or "right" ? "a person turns " + DirText(dir, "") + adv : dir == "circle" ? "a person spins around in a circle" + adv : "a person turns around" + adv,
            "crouch" => "a person crouches down" + adv,
            "kneel" => "a person kneels down on one knee" + adv,
            "sit" => "a person sits down" + adv,
            "stand_up" => "a person stands up" + adv,
            "lie_down" => "a person lies down on the ground" + adv,
            "sleep" => "a person lies down and sleeps",
            "crawl" => "a person crawls " + DirText(dir, "forward") + " on the ground" + adv,
            "kick" => "a person kicks forward with " + Limb(c, "leg") + adv,
            "punch" => (c.Manners.Contains("forceful") ? "a person throws a powerful punch with " : "a person punches forward with ") + Limb(c, "hand") + adv,
            "slap" => "a person slaps forward with " + Limb(c, "hand") + adv,
            "slash" => "a person slashes with a sword" + adv,
            "stab" => "a person stabs forward with a knife" + adv,
            "headbutt" => "a person headbutts forward" + adv,
            "wave" => "a person waves with " + Limb(c, "hand") + adv,
            "point" => "a person points " + DirText(dir, "forward") + " with " + Limb(c, "hand") + adv,
            "salute" => "a person salutes with " + Limb(c, "hand") + adv,
            "bow" => "a person bows politely" + adv,
            "clap" => "a person claps their hands" + adv,
            "dance" => "a person dances" + adv,
            "idle" => "a person stands still",
            "stop" => "a person stops and stands still",
            "step_back" => steps is not null ? "a person takes " + steps + " backward" + adv : "a person steps backward" + adv,
            "dodge" => "a person dodges " + (dir is "left" or "right" ? DirText(dir, "") : "to the side") + adv,
            "roll" => "a person rolls forward on the ground",
            "climb" => "a person climbs up" + adv,
            "swim" => "a person swims in place with freestyle arm strokes" + adv,
            "throw" => "a person throws something forward" + adv,
            "catch" => "a person catches something with both hands" + adv,
            "pick_up" => "a person bends down and picks something up from the ground" + adv,
            "carry" => "a person carries something heavy in both arms" + adv,
            "push" => "a person pushes something forward with both hands" + adv,
            "pull" => "a person pulls something toward themselves with both hands" + adv,
            "block" => "a person blocks with both arms raised in front of the face" + adv,
            "cheer" => "a person cheers with both arms raised" + adv,
            "celebrate" => "a person celebrates happily, raising both fists" + adv,
            "cry" => "a person cries, wiping their eyes" + adv,
            "laugh" => "a person laughs out loud" + adv,
            "smile" => "a person smiles warmly",
            "think" => "a person stands thinking, with a hand on their chin",
            "talk" => "a person talks, gesturing with their hands" + adv,
            "phone_call" => "a person talks on the phone, holding it to their ear",
            "shout" => "a person shouts loudly, hands cupped around the mouth",
            "sing" => "a person sings with their arms open",
            "get_hit" => "a person gets hit and reacts, staggering backward",
            "startle" => "a person is startled and flinches back",
            "tremble" => "a person trembles with fear",
            "fight_stance" => "a person takes a fighting stance with fists raised",
            "aim" => "a person aims a gun forward with both hands",
            "shoot" => "a person shoots a gun forward",
            "drink" => "a person drinks from a cup" + adv,
            "eat" => "a person eats, bringing food to their mouth" + adv,
            "yawn" => "a person yawns and covers their mouth",
            "stretch" => "a person stretches their arms above their head",
            "nod" => "a person nods their head",
            "shake_head" => "a person shakes their head",
            "shrug" => "a person shrugs their shoulders",
            "hug" => "a person hugs someone",
            _ => "a person " + c.Action.Replace('_', ' '),
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
            r.Understood ? ProceduralKeyword(r.Action) : null, r.Unrecognized)
        { Confidence = r.Confidence, Suggestions = r.Suggestions, Layers = r.Layers }).ToList();
        foreach (var s in segments.Where(s => !s.Understood))
        {
            var hint = s.Suggestions.FirstOrDefault(x => !x.Applied);
            warnings.Add($"Step {s.Index + 1} was not understood: \"{s.Text}\". Rephrase it (for example «يمشي»، «يجري»، «يقع») or write it in English."
                + (hint is null ? "" : " " + hint.Message));
        }
        foreach (var s in segments)
            foreach (var x in s.Suggestions.Where(x => x.Applied && x.Confidence < 0.85))
                warnings.Add($"Step {s.Index + 1}: \"{x.Word}\" was read as \"{x.Candidates[0]}\" ({x.Confidence:P0}). {x.Message}");
        return new MotionPlan(prompt, Math.Round(d.Sum(), 2), segments, warnings);
    }
}
