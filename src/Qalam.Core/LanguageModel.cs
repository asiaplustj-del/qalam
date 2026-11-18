using System.IO.Compression;
using System.Reflection;

namespace Qalam.Core;

/// <summary>
/// Word list with Zipf frequencies (log10 of occurrences per billion words)
/// plus a character-trigram model trained from the same list, used for words
/// that are not in the dictionary (names, rare inflections, slang).
/// </summary>
public sealed class LanguageModel
{
    public string Language { get; }
    private readonly Dictionary<string, float> _words = new(StringComparer.Ordinal);
    private readonly Dictionary<int, float> _tri = new();   // hashed trigram -> log prob
    private float _unseen;
    private readonly HashSet<char> _alphabet = new();
    private readonly string[] _suffixes;
    private readonly string[] _prefixes;

    public int WordCount => _words.Count;

    /// <summary>Exact dictionary lookup (no morphology), Zipf or null.</summary>
    public float? Exact(string lower) => _words.TryGetValue(lower, out var z) ? z : null;

    /// <summary>Builds the prefix index ahead of time (call from a background thread).</summary>
    public void Warmup() { foreach (var _ in WithPrefix("\uFFFF", 1)) { } }

    private string[]? _sorted;
    private readonly object _sortLock = new();

    /// <summary>Words starting with <paramref name="prefix"/> (lower case), scanned in alphabetical order.</summary>
    public IEnumerable<(string Word, float Zipf)> WithPrefix(string prefix, int maxScan = 30000)
    {
        if (_sorted == null) lock (_sortLock) _sorted ??= _words.Keys.OrderBy(w => w, StringComparer.Ordinal).ToArray();
        var arr = _sorted;
        int lo = 0, hi = arr.Length;
        while (lo < hi) { int mid = (lo + hi) / 2; if (string.CompareOrdinal(arr[mid], prefix) < 0) lo = mid + 1; else hi = mid; }
        for (int i = lo, n = 0; i < arr.Length && n < maxScan && arr[i].StartsWith(prefix, StringComparison.Ordinal); i++, n++)
            yield return (arr[i], _words[arr[i]]);
    }

    public LanguageModel(string language, IEnumerable<(string Word, float Zipf)> words)
    {
        Language = language;
        (_prefixes, _suffixes) = Morphology(language);
        foreach (var (w, z) in words)
        {
            if (!_words.TryGetValue(w, out var old) || old < z) _words[w] = z;
            foreach (var c in w) _alphabet.Add(c);
        }
        Train();
    }

    public static LanguageModel LoadEmbedded(string language)
    {
        var asm = typeof(LanguageModel).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith($".{language}.txt.gz"));
        using var s = asm.GetManifestResourceStream(name)!;
        return Load(language, s);
    }

    public static LanguageModel Load(string language, Stream gz)
    {
        using var z = new GZipStream(gz, CompressionMode.Decompress);
        using var r = new StreamReader(z);
        var list = new List<(string, float)>();
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            int t = line.IndexOf('\t');
            if (t <= 0) continue;
            list.Add((line[..t], float.Parse(line[(t + 1)..], System.Globalization.CultureInfo.InvariantCulture)));
        }
        return new LanguageModel(language, list);
    }

    private void Train()
    {
        var counts = new Dictionary<int, double>();
        double total = 0;
        foreach (var (w, z) in _words)
        {
            // weight: dampened frequency so that common words matter more, but rare words still count
            double weight = Math.Max(0.2, z - 1.0);
            var s = "^" + w + "$";
            for (int i = 0; i + 2 < s.Length + 1 && i + 3 <= s.Length; i++)
            {
                int h = Hash(s[i], s[i + 1], s[i + 2]);
                counts[h] = counts.GetValueOrDefault(h) + weight;
                total += weight;
            }
        }
        foreach (var (h, c) in counts) _tri[h] = (float)Math.Log(c / total);
        _unseen = (float)Math.Log(0.05 / total);
    }

    private static int Hash(char a, char b, char c) => (a * 65599 + b) * 65599 + c;

    /// <summary>Average log-probability per trigram; higher = more like this language.</summary>
    public double NgramScore(string word)
    {
        var s = "^" + word.ToLowerInvariant() + "$";
        double sum = 0; int n = 0;
        for (int i = 0; i + 3 <= s.Length; i++)
        {
            sum += _tri.TryGetValue(Hash(s[i], s[i + 1], s[i + 2]), out var lp) ? lp : _unseen;
            n++;
        }
        return n == 0 ? _unseen : sum / n;
    }

    /// <summary>Share of characters that belong to this language's alphabet.</summary>
    public double AlphabetFit(string word)
    {
        if (word.Length == 0) return 0;
        int ok = 0;
        foreach (var c in word.ToLowerInvariant()) if (_alphabet.Contains(c)) ok++;
        return (double)ok / word.Length;
    }

    /// <summary>Zipf frequency of the word or of its stem (morphology-aware); null if unknown.</summary>
    public float? Frequency(string word)
    {
        var w = word.ToLowerInvariant().Replace('ё', 'е');
        if (_words.TryGetValue(w, out var z)) return z;
        if (w != word.ToLowerInvariant() && _words.TryGetValue(word.ToLowerInvariant(), out z)) return z;
        if (Language == "en" && w.EndsWith("'s") && _words.TryGetValue(w[..^2], out z)) return z - 0.5f;
        // Strip affixes (mostly for Tajik, which is agglutinative: китоб-ҳо-и-ам-ро)
        if (_suffixes.Length == 0 && _prefixes.Length == 0) return null;
        return StripAndLookup(w, 0);
    }

    private float? StripAndLookup(string w, int depth)
    {
        if (depth > 3 || w.Length < 4) return null;
        float? best = null;
        foreach (var p in _prefixes)
            if (w.Length >= p.Length + 3 && w.StartsWith(p, StringComparison.Ordinal))
            {
                var rest = w[p.Length..];
                var f = _words.TryGetValue(rest, out var z) ? z : StripAndLookup(rest, depth + 1);
                if (f is { } v && (best is null || v > best)) best = v;
            }
        foreach (var s in _suffixes)
            if (w.Length >= s.Length + 3 && w.EndsWith(s, StringComparison.Ordinal))
            {
                var rest = w[..^s.Length];
                var f = _words.TryGetValue(rest, out var z) ? z : StripAndLookup(rest, depth + 1);
                if (f is { } v && (best is null || v > best)) best = v;
            }
        return best is { } b ? b - 1.2f : null; // derived forms are slightly less certain
    }

    private static (string[] prefixes, string[] suffixes) Morphology(string lang) => lang switch
    {
        "tg" => (
            new[] { "нами", "наме", "ме", "на", "би", "ҳам", "бе", "бо" },
            new[] { "ҳоямон", "ҳоятон", "ҳояшон", "ҳоям", "ҳоят", "ҳояш", "ҳоро", "ҳои", "ҳое", "ҳо",
                    "онро", "они", "он", "ён", "гон", "вон",
                    "аму", "ашро", "амро", "атро",
                    "ам", "ӣ", "ат", "аш", "ем", "ед", "анд", "ему", "иму", "имон", "итон", "ишон",
                    "ро", "и", "е", "ву", "у", "ям", "ят", "яш", "ӣ", "гӣ", "гиаш", "гиам",
                    "тар", "тарин", "она", "иҳо", "ист", "аст", "ман", "мон", "тон", "шон",
                    "ед", "ам", "ем", "ӣ", "дан", "тан", "да", "та", "гӣ", "ӣ", "ҳ", "анд", "ист", "ҳом" }),
        _ => (Array.Empty<string>(), Array.Empty<string>())
    };
}
