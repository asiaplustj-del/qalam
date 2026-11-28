using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Qalam.Core;

/// <summary>
/// A small reader of Hunspell dictionaries (the ones LibreOffice and Firefox use): only "is this a
/// real word?", with prefixes/suffixes and their conditions. Case-insensitive, ё = е.
/// Russian: ru_RU by A. Lebedev (BSD); English: en_US from SCOWL — see Data/LICENSES.txt.
/// </summary>
public sealed class HunspellLite
{
    private sealed record Affix(char Flag, bool Cross, string Strip, string Add, Regex? Cond);

    private readonly Dictionary<string, string> _words = new(StringComparer.Ordinal);   // lower → flags
    private readonly Dictionary<string, List<Affix>> _sfxByAdd = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Affix>> _pfxByAdd = new(StringComparer.Ordinal);
    private readonly HashSet<char> _onlyInCompound = new();
    private int _maxSfx, _maxPfx;

    public int WordCount => _words.Count;

    public static string Norm(string s) => s.ToLowerInvariant().Replace('ё', 'е').Replace('’', '\'');

    public static HunspellLite LoadEmbedded(string lang)
    {
        var asm = typeof(HunspellLite).Assembly;
        Stream Open(string ext) =>
            asm.GetManifestResourceStream($"Qalam.Core.Data.hun_{lang}.{ext}.gz") ?? throw new FileNotFoundException(lang);
        using var aff = new GZipStream(Open("aff"), CompressionMode.Decompress);
        using var dic = new GZipStream(Open("dic"), CompressionMode.Decompress);
        return Load(aff, dic);
    }

    public static HunspellLite Load(Stream aff, Stream dic)
    {
        var h = new HunspellLite();
        h.ReadAff(new StreamReader(aff, Encoding.UTF8));
        h.ReadDic(new StreamReader(dic, Encoding.UTF8));
        return h;
    }

    private void ReadAff(TextReader r)
    {
        string? line;
        var headers = new HashSet<string>();
        while ((line = r.ReadLine()) != null)
        {
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0 || p[0].StartsWith('#')) continue;
            if (p[0] == "ONLYINCOMPOUND" && p.Length > 1) { _onlyInCompound.Add(p[1][0]); continue; }
            if (p[0] is not ("SFX" or "PFX") || p.Length < 4) continue;
            bool sfx = p[0] == "SFX";
            var key = p[0] + p[1];
            if (headers.Add(key)) { _cross[key] = p[2] == "Y"; continue; }   // "SFX A Y 4"
            if (p.Length < 4) continue;
            var strip = p[2] == "0" ? "" : Norm(p[2]);
            var add = p[3].Split('/')[0];
            add = add == "0" ? "" : Norm(add);
            var cond = p.Length > 4 ? p[4] : ".";
            Regex? rx = cond == "." ? null
                : new Regex(sfx ? "(" + Norm(cond) + ")$" : "^(" + Norm(cond) + ")", RegexOptions.CultureInvariant);
            var a = new Affix(p[1][0], _cross.GetValueOrDefault(key), strip, add, rx);
            var map = sfx ? _sfxByAdd : _pfxByAdd;
            if (!map.TryGetValue(add, out var list)) map[add] = list = new List<Affix>();
            list.Add(a);
            if (sfx) _maxSfx = Math.Max(_maxSfx, add.Length); else _maxPfx = Math.Max(_maxPfx, add.Length);
        }
    }
    private readonly Dictionary<string, bool> _cross = new();

    private void ReadDic(TextReader r)
    {
        r.ReadLine();   // word count
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            int end = line.IndexOfAny(new[] { '\t', ' ' });
            if (end >= 0) line = line[..end];
            int slash = line.IndexOf('/');
            var word = Norm(slash >= 0 ? line[..slash] : line);
            var flags = slash >= 0 ? line[(slash + 1)..] : "";
            if (word.Length == 0) continue;
            _words[word] = _words.TryGetValue(word, out var old) ? old + flags : flags;
        }
    }

    private bool Stem(string stem, char flag) => _words.TryGetValue(stem, out var f) && f.IndexOf(flag) >= 0;

    /// <summary>True if the word (any case) is a valid spelling.</summary>
    public bool Check(string word)
    {
        var w = Norm(word);
        if (w.Length == 0) return false;
        if (_words.TryGetValue(w, out var flags) && !flags.Any(_onlyInCompound.Contains)) return true;
        if (CheckSuffix(w, null)) return true;
        // prefixes (English: re-, un-, dis-…), alone or combined with a suffix
        for (int n = 0; n <= Math.Min(_maxPfx, w.Length - 1); n++)
        {
            if (!_pfxByAdd.TryGetValue(w[..n], out var list)) continue;
            foreach (var a in list)
            {
                var rest = a.Strip + w[n..];
                if (a.Cond != null && !a.Cond.IsMatch(rest)) continue;
                if (Stem(rest, a.Flag)) return true;
                if (a.Cross && CheckSuffix(rest, a.Flag)) return true;
            }
        }
        return false;
    }

    /// <param name="alsoFlag">For prefix+suffix: the stem must carry this prefix flag too.</param>
    private bool CheckSuffix(string w, char? alsoFlag)
    {
        for (int n = 0; n <= Math.Min(_maxSfx, w.Length - 1); n++)
        {
            if (!_sfxByAdd.TryGetValue(w[(w.Length - n)..], out var list)) continue;
            foreach (var a in list)
            {
                if (alsoFlag != null && !a.Cross) continue;
                var stem = w[..(w.Length - n)] + a.Strip;
                if (stem.Length == 0) continue;
                if (a.Cond != null && !a.Cond.IsMatch(stem)) continue;
                if (Stem(stem, a.Flag) && (alsoFlag is not { } pf || Stem(stem, pf))) return true;
            }
        }
        return false;
    }
}

/// <summary>
/// A big list of rarer real words (from wordfreq, frequency ≥ ~1 per 100 million words): slang, rare
/// forms, terms. Only "is it a word?", stored compactly (one sorted string + offsets).
/// </summary>
public sealed class WordSet
{
    private readonly string _blob;
    private readonly int[] _start;
    public int Count => _start.Length;

    private WordSet(string blob, int[] start) { _blob = blob; _start = start; }

    public static WordSet? LoadEmbedded(string name)
    {
        using var s = typeof(WordSet).Assembly.GetManifestResourceStream($"Qalam.Core.Data.{name}.txt.gz");
        if (s == null) return null;
        using var gz = new GZipStream(s, CompressionMode.Decompress);
        var text = new StreamReader(gz, Encoding.UTF8).ReadToEnd();
        var starts = new List<int>(text.Length / 9) { 0 };
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        return new WordSet(text, starts.ToArray());
    }

    private ReadOnlySpan<char> At(int i)
    {
        int a = _start[i], b = i + 1 < _start.Length ? _start[i + 1] - 1 : _blob.Length;
        return _blob.AsSpan(a, b - a);
    }

    public bool Contains(string word)
    {
        int lo = 0, hi = _start.Length - 1;
        var w = word.AsSpan();
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int c = At(mid).SequenceCompareTo(w);
            if (c == 0) return true;
            if (c < 0) lo = mid + 1; else hi = mid - 1;
        }
        return false;
    }
}

/// <summary>Neighbouring keys on a physical keyboard (for "a typo, not a different word" decisions).</summary>
public static class KeyNeighbours
{
    private static readonly string[] Rows = { "`1234567890-=", "qwertyuiop[]", "asdfghjkl;'", "zxcvbnm,./" };
    private static readonly int[] Shift = { 0, 1, 1, 2 };   // row stagger in half-keys ≈ simplified

    /// <summary>True if the two characters sit on adjacent physical keys in this layout.</summary>
    public static bool Adjacent(KeyboardLayout layout, char a, char b)
    {
        if (!layout.TryEncode(char.ToLowerInvariant(a).ToString(), out var sa) || !layout.TryEncode(char.ToLowerInvariant(b).ToString(), out var sb)) return false;
        var (ra, ca) = Find(sa[0].Key); var (rb, cb) = Find(sb[0].Key);
        if (ra < 0 || rb < 0) return false;
        if (ra == rb) return Math.Abs(ca - cb) == 1;
        if (Math.Abs(ra - rb) != 1) return false;
        // staggered rows: the key below-left and below-right are neighbours
        double xa = ca + Shift[ra] * 0.5, xb = cb + Shift[rb] * 0.5;
        return Math.Abs(xa - xb) <= 1.0;
    }

    private static (int Row, int Col) Find(char key)
    {
        for (int r = 0; r < Rows.Length; r++) { int c = Rows[r].IndexOf(char.ToLowerInvariant(key)); if (c >= 0) return (r, c); }
        return (-1, -1);
    }
}

/// <summary>
/// Automatic typo correction ("превет" → "привет", "teh" → "the") for Russian, Tajik and English.
/// Conservative on purpose: only clearly unknown words, only one-letter mistakes, only when one
/// common word clearly wins. Undo with the manual hotkey teaches it the word.
/// </summary>
public sealed class SpellFixer
{
    private readonly Detector _det;
    private readonly Task<HunspellLite?> _ru, _en;
    private readonly Task<WordSet?> _ruMore, _enMore;
    private const string Cyr = "абвгдежзийклмнопрстуфхцчшщъыьэюя";
    private const string Tg = "ҳҷқғӯӣ";
    private const string Lat = "abcdefghijklmnopqrstuvwxyz";

    public SpellFixer(Detector det)
    {
        _det = det;
        _ru = Task.Run(() => TryLoad("ru"));
        _en = Task.Run(() => TryLoad("en"));
        _ruMore = Task.Run(() => TryLoadSet("known_ru"));
        _enMore = Task.Run(() => TryLoadSet("known_en"));
    }

    private static WordSet? TryLoadSet(string name)
    {
        try { return WordSet.LoadEmbedded(name); }
        catch (Exception e) { Log.Error($"word list {name}", e); return null; }
    }

    private bool More(string lang, string lower)
    {
        var t = lang == "ru" ? _ruMore : _enMore;
        if (!t.IsCompletedSuccessfully || t.Result is not { } set) return false;
        return set.Contains(lower) || (lower.Contains('ё') && set.Contains(lower.Replace('ё', 'е')));
    }

    private static HunspellLite? TryLoad(string lang)
    {
        try { return HunspellLite.LoadEmbedded(lang); }
        catch (Exception e) { Log.Error($"spelling dictionary {lang}", e); return null; }
    }

    /// <summary>Waits until the dictionaries are read (tests, warm-up).</summary>
    public void WaitLoaded() { _ru.Wait(); _en.Wait(); _ruMore.Wait(); _enMore.Wait(); }

    private HunspellLite? Hun(string lang) =>
        (lang == "ru" ? _ru : lang == "en" ? _en : null) is { IsCompletedSuccessfully: true } t ? t.Result : null;

    /// <summary>Is this a real word in any language of the group (Cyrillic: Russian + Tajik)?</summary>
    public bool IsKnown(string lower, bool cyrillic)
    {
        if (_det.User.IsKnown(lower) || _det.User.IsException(lower)) return true;
        if (cyrillic)
        {
            if (_det.Model("ru")?.Frequency(lower) != null || _det.Model("tg")?.Frequency(lower) != null || More("ru", lower)) return true;
            if (Hun("ru") is { } h ? h.Check(lower) : true) return true;   // dictionary still loading → never "fix"
            return _det.BestTajikVariant(lower) is { } v && _det.Model("tg")?.Frequency(v) is { } z && z > 1.5f;
        }
        if (_det.Model("en")?.Frequency(lower) != null || More("en", lower)) return true;
        return Hun("en") is not { } he || he.Check(lower);
    }

    /// <summary>A valid spelling in the Russian or English dictionary (false while they are still loading).</summary>
    public bool IsRealWord(string lower) =>
        lower.Length > 1 && (Hun("ru")?.Check(lower) == true || Hun("en")?.Check(lower) == true);

    [ThreadStatic] private static bool TajikAllowed;
    private static readonly string[] VerbEndings =
        { "ю", "ешь", "ет", "ем", "ете", "ут", "ют", "ит", "ишь", "им", "ите", "ат", "ят", "л", "ла", "ло", "ли", "ть", "ться", "тся", "лся", "лась", "лись", "ться", "чь", "й", "йте" };

    private float? Zipf(string lower, bool cyrillic, string? contextLang)
    {
        float? best = null;
        void Take(float? z, float bonus) { if (z is { } v && (best is null || v + bonus > best)) best = v + bonus; }
        if (cyrillic)
        {
            Take(_det.Model("ru")?.Exact(lower) ?? _det.Model("ru")?.Frequency(lower), contextLang == "ru" ? 0.3f : 0);
            if (TajikAllowed) Take(_det.Model("tg")?.Exact(lower) ?? _det.Model("tg")?.Frequency(lower), contextLang == "tg" ? 0.3f : -0.3f);
            if (best == null && (Hun("ru")?.Check(lower) == true || More("ru", lower))) best = 1.5f;   // valid but rare
        }
        else
        {
            Take(_det.Model("en")?.Exact(lower) ?? _det.Model("en")?.Frequency(lower), 0);
            if (best == null && (Hun("en")?.Check(lower) == true || More("en", lower))) best = 1.5f;
        }
        return best;
    }

    /// <summary>Returns the corrected token (punctuation kept), or null to leave it alone.</summary>
    public string? Fix(string token, KeyboardLayout layout, string? contextLang, bool sentenceStart)
    {
        var core = Detector.Core(token);
        if (core.Length < 3 || core.Length > 30 || Detector.LooksTechnical(token)) return null;
        // compound and shortened words ("кто-то", "из-за", "e-mail", "don't") are left as typed
        if (core.Any(c => !char.IsLetter(c))) return null;
        if (_det.User.IsException(token)) return null;
        // proper names, abbreviations: "Фируз", "НБТ", "iPhone"
        if (core.Skip(1).Any(char.IsUpper)) return null;
        if (char.IsUpper(core[0]) && !sentenceStart) return null;
        bool cyrillic = layout.Language is "ru" or "tg";
        var lower = core.ToLowerInvariant();
        bool cyrWord = lower.All(c => c == '-' || Cyr.Contains(c) || c == 'ё' || Tg.Contains(c));
        bool latWord = lower.All(c => c == '-' || c == '\'' || Lat.Contains(c));
        if (cyrillic ? !cyrWord : !latWord) return null;
        if (cyrillic ? Hun("ru") == null : Hun("en") == null) return null;
        if (_det.User.IsKnown(lower)) return null;
        // a correct spelling → leave it
        if (Hun(cyrillic ? "ru" : "en")!.Check(lower)) return null;
        if (cyrillic && (_det.Model("tg")?.Frequency(lower) != null
                         || _det.BestTajikVariant(lower) is { } tv && _det.Model("tg")?.Frequency(Detector.Core(tv).ToLowerInvariant()) is > 1.5f))
            return null;   // Tajik
        // Seen in real texts but not in the spelling dictionary: slang, names, rare forms — or a
        // common misspelling ("сдесь", "teh", "recieve"). Only fixed when the right word is far more common.
        float? own = cyrillic ? (_det.Model("ru")?.Exact(lower) ?? _det.Model("ru")?.Frequency(lower)) : _det.Model("en")?.Exact(lower);
        if (own == null && More(cyrillic ? "ru" : "en", lower)) own = 1.5f;

        TajikAllowed = layout.Language == "tg" || contextLang == "tg";
        var alphabet = cyrillic ? Cyr + (layout.Language == "tg" || contextLang == "tg" ? Tg : "") : Lat;
        var cands = new Dictionary<string, double>();
        void Consider(string cand, double bonus)
        {
            if (cand.Length < 2 || cand == lower) return;
            if (Zipf(cand, cyrillic, contextLang) is not { } z) return;
            var s = z + bonus;
            if (!cands.TryGetValue(cand, out var old) || old < s) cands[cand] = s;
        }
        for (int i = 0; i <= lower.Length; i++)
        {
            // a letter is missing: "привт" → "привет" (never the first one: "атар" is more likely the end of a
            // word whose start we did not see than "хатар" with a forgotten "х")
            if (i > 0)
            foreach (var c in alphabet)
                Consider(lower[..i] + c + lower[i..], (i > 0 && lower[i - 1] == c) || (i < lower.Length && lower[i] == c) ? 0.4 : 0.1);
            if (i == lower.Length) break;

            // the ending of a Russian/Tajik word is often a valid form the dictionaries lack
            // ("установлениях", "невозвраты") — be careful with changes in the last letters
            double tail = cyrillic && i >= lower.Length - 2 ? -0.7 : 0;
            // an extra letter: "приввет" → "привет"
            bool dup = (i > 0 && lower[i - 1] == lower[i]) || (i + 1 < lower.Length && lower[i + 1] == lower[i]);
            double first = i == 0 && !dup ? -0.8 : 0;   // an extra first letter is a rare slip ("tmobile", "унас")
            bool nearby = (i > 0 && KeyNeighbours.Adjacent(layout, lower[i], lower[i - 1])) || (i + 1 < lower.Length && KeyNeighbours.Adjacent(layout, lower[i], lower[i + 1]));
            Consider(lower[..i] + lower[(i + 1)..], dup ? 0.5 : (nearby ? 0.4 : first) + tail);
            // a wrong letter: "превет" → "привет" (a neighbouring key is the most likely slip)
            foreach (var c in alphabet)
                if (c != lower[i])
                {
                    bool near = KeyNeighbours.Adjacent(layout, lower[i], c);
                    // the first letter is rarely the slip (and may belong to a word we only saw the end of)
                    Consider(lower[..i] + c + lower[(i + 1)..], (near ? 0.6 : tail) - (i == 0 ? 0.8 : 0));
                }
            // two letters swapped: "приевт" → "привет"
            if (i + 1 < lower.Length && lower[i] != lower[i + 1])
                Consider(lower[..i] + lower[i + 1] + lower[i] + lower[(i + 2)..], 0.6);
        }
        // two words written together: "незнаю" → "не знаю", "alot" → "a lot", "thetime" → "the time"
        var glueLeft = cyrillic ? new[] { "не", "ни", "в", "во", "у" } : new[] { "a" };
        for (int i = 1; i < lower.Length; i++)
        {
            var (l, r) = (lower[..i], lower[i..]);
            if (Zipf(l, cyrillic, contextLang) is not { } zl || Zipf(r, cyrillic, contextLang) is not { } zr) continue;
            // "не"/"ни" + verb ("незнаю"); otherwise "не-" is just a prefix ("непубличный")
            bool particle = glueLeft.Contains(l) && zr >= 4.5 && r.Length >= 3
                            && (l is not ("не" or "ни") || VerbEndings.Any(r.EndsWith));
            // two full words glued; Russian compounds with -о-/-е- ("радиокомплекс", "киноверсия") stay
            bool twoWords = zl >= 5.0 && zr >= 5.0 && l.Length >= 3 && r.Length >= 3 && !(cyrillic && l[^1] is 'о' or 'е');
            if (!particle && !twoWords) continue;
            var sc = Math.Min(zl, zr) + (particle ? 0.3 : -0.3);
            var key = l + " " + r;
            if (!cands.TryGetValue(key, out var old) || old < sc) cands[key] = sc;
        }
        // English contractions written without the apostrophe: "thats" → "that's", "dont" → "don't"
        if (!cyrillic)
            for (int i = 1; i < lower.Length; i++)
            {
                var c = lower[..i] + "'" + lower[i..];
                if (_det.Model("en")?.Exact(c) is { } zc && zc >= 3.5f && (!cands.TryGetValue(c, out var old) || old < zc + 2.5)) cands[c] = zc + 2.5;
            }
        if (cands.Count == 0) return null;
        var ranked = cands.OrderByDescending(kv => kv.Value).ToList();
        var (best, score) = (ranked[0].Key, ranked[0].Value);
        double second = ranked.Count > 1 ? ranked[1].Value : double.MinValue;
        double need = lower.Length switch { 3 => 5.8, 4 => 3.6, _ => 3.0 };
        if (layout.Language == "tg" || contextLang == "tg") need += 0.5;   // Tajik forms are less covered by the dictionary
        if (char.IsUpper(core[0]))
        {
            if (own != null) return null;   // a capitalised word seen in real texts is most likely a name
            need += 1.5;
        }
        if (score < need || score - second < 0.8) return null;
        if (own is { } oz && score - oz < 2.3) return null;
        // three-letter words: only swapped letters of very common words ("чот" → "что", "teh" → "the")
        if (lower.Length == 3 && !(best.Length == 3 && best.OrderBy(c => c).SequenceEqual(lower.OrderBy(c => c)))) return null;
        int a = token.IndexOf(core, StringComparison.Ordinal);
        return token[..a] + TajikLetters.MatchCase(core, best) + token[(a + core.Length)..];
    }
}
