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

    public List<Completion> Complete(string typed, KeyboardLayout layout, string? contextLanguage, int k = 3)
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
                    if (z < MinZipf || w.Length < lower.Length + 2) continue;
                    double score = z + (lang == contextLanguage ? 0.7 : 0) + (lang == layout.Language ? 0.3 : 0) - (p != lower ? 0.3 : 0);
                    if (!cands.TryGetValue(w, out var old) || old.Score < score) cands[w] = (score, z, lang);
                }
        }
        foreach (var (w, c) in cands.OrderByDescending(kv => kv.Value.Score).Take(k))
        {
            // the user probably means the short word itself ("как", not "какой")
            if (result.Count == 0 && prefixZ >= c.Score - 0.3) break;
            result.Add(new Completion(typed, TajikLetters.MatchCase(typed, w), c.Lang));
        }
        return result;
    }
}
