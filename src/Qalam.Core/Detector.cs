namespace Qalam.Core;

public sealed record Candidate(KeyboardLayout Layout, string Text, double Score, float? Freq, double Ngram);

public sealed class DetectorOptions
{
    /// <summary>Words shorter than this are never auto-converted by the n-gram model alone.</summary>
    public int MinNgramLength { get; set; } = 4;
    /// <summary>Required n-gram advantage (avg log-prob per trigram) to convert an unknown word.</summary>
    public double NgramMargin { get; set; } = 2.2;
    /// <summary>Minimum Zipf frequency for a dictionary hit to count as a real word.</summary>
    public float MinZipf { get; set; } = 1.0f;
    /// <summary>2-letter words are converted only if the target word is at least this common.</summary>
    public float ShortWordZipf { get; set; } = 4.3f;
    /// <summary>Convert one-letter words ("ф"→"a", "d"→"в").</summary>
    public bool FixSingleLetters { get; set; } = true;
}

/// <summary>Decides in which layout a sequence of key strokes makes the most sense.</summary>
public sealed class Detector
{
    private readonly Dictionary<string, LanguageModel> _models;
    public DetectorOptions Options { get; } = new();
    public UserDictionary User { get; }

    public Detector(IEnumerable<LanguageModel> models, UserDictionary? user = null)
    {
        _models = models.ToDictionary(m => m.Language);
        User = user ?? new UserDictionary();
    }

    public LanguageModel? Model(string lang) => _models.GetValueOrDefault(lang);

    public Candidate Evaluate(KeyboardLayout layout, IReadOnlyList<KeyStroke> strokes)
    {
        var text = layout.Render(strokes);
        var core = Core(text);
        var m = Model(layout.Language);
        if (m is null || core.Length == 0) return new Candidate(layout, text, double.NegativeInfinity, null, double.NegativeInfinity);
        float? f = User.IsKnown(core) ? 6f : m.Frequency(core);
        if (f < Options.MinZipf) f = null;
        double ng = m.NgramScore(core) * m.AlphabetFit(core) + (m.AlphabetFit(core) - 1) * 10;
        double score = ng * 0.3 + (f is { } z ? 10 + z : 0);
        return new Candidate(layout, text, score, f, ng);
    }

    /// <summary>
    /// Returns the layout the word should be converted to, or null to leave it as typed.
    /// </summary>
    private static readonly Dictionary<string, string> SingleLetterWords = new()
    {
        ["en"] = "ai", ["ru"] = "авикосуя", ["tg"] = "аиоуӯ"
    };

    /// <param name="contextLanguage">Language of the previous word in the same text, if known.</param>
    public Candidate? Decide(KeyboardLayout current, IReadOnlyList<KeyboardLayout> layouts,
                             IReadOnlyList<KeyStroke> strokes, string? contextLanguage = null)
    {
        var cur = Evaluate(current, strokes);
        var core = Core(cur.Text);
        if (core.Length == 0) return null;
        // The same text may be a valid word of a sibling layout (RU/TG share most keys): "хеле" typed
        // with the Russian layout is still correct Tajik on screen → treat it as a known word.
        foreach (var l in layouts)
        {
            if (l.Id == current.Id) continue;
            var same = Evaluate(l, strokes);
            if (same.Text == cur.Text && same.Freq is { } sf && (cur.Freq is null || sf > cur.Freq))
                cur = cur with { Freq = sf };
        }
        if (User.IsException(cur.Text)) return null;
        if (cur.Text.Any(char.IsDigit) && cur.Text.Any(char.IsLetter)) return null;
        bool technical = LooksTechnical(cur.Text);

        Candidate? best = null;
        foreach (var l in layouts)
        {
            if (l.Id == current.Id) continue;
            var alt = Evaluate(l, strokes);
            if (alt.Text == cur.Text) continue;          // e.g. RU vs TG for words without special letters
            var altCore = Core(alt.Text);
            // edge keys may legitimately become punctuation ("сщдвб" → "cold,"), but not the word itself
            if (altCore.Length == 0 || altCore.Length < core.Length - 2) continue;
            if (LooksTechnical(alt.Text)) continue;
            if (best is null || alt.Score > best.Score) best = alt;
        }
        if (best is null) return null;
        bool ctxAlt = contextLanguage == best.Layout.Language;
        bool ctxCur = contextLanguage == current.Language;

        int len = Math.Max(core.Length, Core(best.Text).Length);
        // 0) single letters: "ф"→"a", "d"→"в", "e"→"у" — only real one-letter words
        if (len == 1)
        {
            var altCore = Core(best.Text);
            if (!Options.FixSingleLetters || altCore.Length != 1 || ctxCur) return null;
            bool curValid = SingleLetterWords.GetValueOrDefault(current.Language, "").Contains(char.ToLowerInvariant(core[0]));
            bool altValid = SingleLetterWords.GetValueOrDefault(best.Layout.Language, "").Contains(char.ToLowerInvariant(altCore[0]));
            return !curValid && altValid && cur.Text.Length == 1 ? best : null;
        }
        // tokens with symbols inside (URLs, code, "к=шидан") only convert to a real dictionary word
        if (technical) return best.Freq != null ? best : null;

        // 1) current word is a real word → keep, unless it's a rare short word vs a much more common one
        //    ("lf"→"да", "рощи"→"роҳи" inside Tajik text)
        // if edge keys of the "real word" are letters in the target ("[kt," → "хлеб,"), the hit is just a fragment
        var curFreq = Core(best.Text).Length > core.Length ? null : cur.Freq;
        if (curFreq is { } fc)
        {
            if (best.Freq is not { } fb || ctxCur) return null;
            if (len <= 4 && ((fb - fc >= 2.5 && fc < 3.5) || fb - fc >= 3.3)) return best;
            if (ctxAlt && fb - fc >= 1.0 && fc < 5.0) return best;
            return null;
        }
        // 2) target is a dictionary word
        if (best.Freq is { } fa)
        {
            if (len == 2 && fa < Options.ShortWordZipf && !ctxAlt) return null;
            if (len == 3 && fa < 2.5 && !ctxAlt) return null;
            return best;
        }
        // 3) neither known: trust the n-gram model only for longer words and clear wins
        double margin = Options.NgramMargin - (ctxAlt ? 0.7 : 0) + (ctxCur ? 0.7 : 0);
        if (len >= Options.MinNgramLength && best.Ngram - cur.Ngram >= margin) return best;
        return null;
    }

    /// <summary>Would this (already typed) word also read better in <paramref name="target"/>? Used to fix the previous short word retroactively.</summary>
    public bool PrefersRetro(KeyboardLayout current, KeyboardLayout target, IReadOnlyList<KeyStroke> strokes)
    {
        var cur = Evaluate(current, strokes);
        var alt = Evaluate(target, strokes);
        if (alt.Text == cur.Text || User.IsException(cur.Text) || LooksTechnical(alt.Text)) return false;
        if (alt.Freq is not { } fa) return false;
        if (cur.Freq is not { } fc) return Core(alt.Text).Length >= 1;
        return Core(cur.Text).Length <= 4 && fa >= fc - 0.5;
    }

    /// <summary>
    /// "хамчун" → "ҳамчун": returns the corrected token, or null. A word that is already a valid
    /// Tajik spelling ("ки") is never touched; a valid Russian word only inside Tajik text.
    /// </summary>
    public string? RestoreTajik(string text, string? contextLanguage)
    {
        var tg = Model("tg");
        if (tg == null) return null;
        var core = Core(text);
        if (core.Length < 2 || User.IsException(text) || LooksTechnical(text)) return null;
        var lower = core.ToLowerInvariant();
        if (!TajikLetters.HasCandidates(lower) || lower.Any(c => c is >= 'a' and <= 'z')) return null;
        if (User.IsKnown(core)) return null;
        // "ки", "чой" are already Tajik; but the dictionary also has rare look-alike spellings
        // ("хамчун" from old texts) — those lose to a much more common Tajik spelling
        var own = tg.Exact(lower) ?? tg.Frequency(lower);
        string? best = null; float bestZ = float.MinValue;
        foreach (var v in TajikLetters.Variants(lower))
        {
            var z = tg.Exact(v) ?? tg.Frequency(v);
            if (z is { } f && f > bestZ) { bestZ = f; best = v; }
        }
        if (best == null || bestZ < 2.2f) return null;
        if (own is { } o && bestZ - o < 1.5f) return null;
        var ru = Model("ru")?.Frequency(lower);
        if (ru is { } fr)
        {
            // "хам", "чаи"… are Russian too — only fix them inside Tajik text
            bool ok = contextLanguage == "tg" ? bestZ - fr >= 1.0f
                    : contextLanguage == null && bestZ - fr >= 2.5f;   // "хар" → "ҳар" even at the start
            if (!ok) return null;
        }
        else if (contextLanguage is "ru" && bestZ < 3.5f) return null;
        int a = text.IndexOf(core, StringComparison.Ordinal);
        return text[..a] + TajikLetters.MatchCase(core, best) + text[(a + core.Length)..];
    }

    /// <summary>Most common Tajik spelling with restored letters, ignoring thresholds (for the manual hotkey: "ки" → "кӣ").</summary>
    public string? BestTajikVariant(string text)
    {
        var tg = Model("tg");
        var core = Core(text);
        if (tg == null || core.Length == 0) return null;
        var lower = core.ToLowerInvariant();
        string? best = null; float bestZ = float.MinValue;
        foreach (var v in TajikLetters.Variants(lower))
        {
            var z = tg.Exact(v) ?? tg.Frequency(v);
            if (z is { } f && f > bestZ) { bestZ = f; best = v; }
        }
        if (best == null) return null;
        int a = text.IndexOf(core, StringComparison.Ordinal);
        return text[..a] + TajikLetters.MatchCase(core, best) + text[(a + core.Length)..];
    }

    /// <summary>Letters of the token without surrounding punctuation.</summary>
    public static string Core(string text)
    {
        int a = 0, b = text.Length;
        while (a < b && !char.IsLetter(text[a])) a++;
        while (b > a && !char.IsLetter(text[b - 1])) b--;
        return text[a..b];
    }

    private static bool LooksTechnical(string t)
    {
        bool hasDigit = false, hasLetter = false;
        foreach (var c in t)
        {
            if (char.IsDigit(c)) hasDigit = true;
            else if (char.IsLetter(c)) hasLetter = true;
            if (c is '@' or '/' or '\\' or '_' or '=' or '#' or '<' or '>' or '{' or '}' or '|' or '~' or '`' or '$') return true;
        }
        if (hasDigit && hasLetter) return true;
        var core = Core(t);
        // internal punctuation like "a.b" (domains, file names) — except hyphen/apostrophe
        foreach (var c in core) if (!char.IsLetter(c) && c != '-' && c != '\'' && c != '’') return true;
        return false;
    }
}
