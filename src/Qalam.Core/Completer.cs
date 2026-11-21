namespace Qalam.Core;

public sealed record Completion(string Typed, string Word, string Language);

/// <summary>Dictionary-based word completion ("тоҷик" → "тоҷикистон"), no neural network.</summary>
public sealed class Completer
{
    private readonly Detector _detector;
    public int MinPrefix { get; set; } = 3;
    public float MinZipf { get; set; } = 3.0f;
    public bool UseTajikLetters { get; set; } = true;

    public Completer(Detector detector) => _detector = detector;

    /// <summary>Learned word pairs of this user (context: what usually follows the previous word).</summary>
    public UserPhrases? Phrases { get; set; }

    /// <param name="previous">The word before (lowercase) — what the user usually writes after it ranks first.</param>
    public List<Completion> Complete(string typed, KeyboardLayout layout, string? contextLanguage, int k = 4, string? previous = null)
    {
        var result = new List<Completion>();
        if (typed.Length < MinPrefix || Detector.Core(typed) != typed || typed.Any(c => !char.IsLetter(c) && c != '-')) return result;
        var lower = typed.ToLowerInvariant();
        bool cyr = layout.Language is "ru" or "tg";
        var langs = cyr ? new[] { "ru", "tg" } : new[] { layout.Language };

        var cands = new Dictionary<string, (double Score, float Z, string Lang)>();
        double prefixZ = double.MinValue;
        foreach (var lang in langs)
        {
            var m = _detector.Model(lang);
            if (m == null) continue;
            if (m.Exact(lower) is { } pz) prefixZ = Math.Max(prefixZ, pz + (lang == contextLanguage ? 0.7 : 0));
            var prefixes = new List<string> { lower };
            if (lang == "tg" && UseTajikLetters) prefixes.AddRange(TajikLetters.Variants(lower, 64));
            foreach (var p in prefixes)
                foreach (var (w, z) in m.WithPrefix(p, 8000))
                {
                    if (z < MinZipf || w.Length <= lower.Length) continue;
                    double score = z + (lang == contextLanguage ? 0.7 : 0) + (lang == layout.Language ? 0.3 : 0) - (p != lower ? 0.3 : 0)
                                   + PairBonus(previous, w);
                    if (!cands.TryGetValue(w, out var old) || old.Score < score) cands[w] = (score, z, lang);
                }
        }
        // the user's own phrases: "дорогие" → "друзья" even if the dictionary prefers "другие"
        if (previous != null && Phrases != null)
            foreach (var (w, n) in Phrases.After(previous, 30))
            {
                if (w.Length <= lower.Length || !SamePrefix(w, lower) || cands.ContainsKey(w)) continue;
                cands[w] = (MinZipf + PairBonus(previous, w), MinZipf, contextLanguage ?? layout.Language);
            }
        var ranked = cands.OrderByDescending(kv => kv.Value.Score).Take(k).ToList();
        // a short complete word ("как", "это") is what the user means — no "какой" hint over it
        if (ranked.Count > 0 && prefixZ > double.MinValue && lower.Length <= 4 && prefixZ >= ranked[0].Value.Score - 0.3
            && PairBonus(previous, ranked[0].Key) == 0)
            return result;
        foreach (var (w, c) in ranked)
            result.Add(new Completion(typed, TajikLetters.MatchCase(typed, w), c.Lang));
        return result;
    }

    private double PairBonus(string? previous, string word)
    {
        if (previous == null || Phrases == null) return 0;
        int n = Phrases.Count(previous, word);
        return n == 0 ? 0 : 2.0 + 1.2 * Math.Log(n);
    }

    /// <summary>Prefix match that treats Tajik letters as their Russian look-alikes (ҳ = х, ҷ = ч…).</summary>
    public static bool SamePrefix(string word, string typedLower) =>
        Fold(word).StartsWith(Fold(typedLower), StringComparison.Ordinal);

    public static string Fold(string s)
    {
        var a = s.ToLowerInvariant().ToCharArray();
        for (int i = 0; i < a.Length; i++)
            a[i] = a[i] switch { 'ҳ' => 'х', 'ҷ' => 'ч', 'қ' => 'к', 'ғ' => 'г', 'ӯ' => 'у', 'ӣ' => 'и', 'ё' => 'е', _ => a[i] };
        return new string(a);
    }
}
