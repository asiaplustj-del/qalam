namespace Qalam.Core;

/// <summary>
/// Restores Tajik letters for text typed with Russian look-alikes:
/// х→ҳ, ч→ҷ, к→қ, г→ғ, у→ӯ, и→ӣ ("хамчун" → "ҳамчун", "чахон" → "ҷаҳон").
/// </summary>
public static class TajikLetters
{
    private static readonly Dictionary<char, char> Pairs = new()
    {
        ['х'] = 'ҳ', ['ч'] = 'ҷ', ['к'] = 'қ', ['г'] = 'ғ', ['у'] = 'ӯ', ['и'] = 'ӣ',
    };

    public static bool HasCandidates(string lower) => lower.Any(Pairs.ContainsKey);

    /// <summary>All spellings with some look-alikes replaced (the original excluded), fewest replacements first.</summary>
    public static IEnumerable<string> Variants(string lower, int max = 1024)
    {
        var positions = new List<int>();
        for (int i = 0; i < lower.Length; i++) if (Pairs.ContainsKey(lower[i])) positions.Add(i);
        if (positions.Count == 0) yield break;
        if (positions.Count > 10) positions = positions.Take(10).ToList();   // 2^10 variants max
        int n = positions.Count;
        var masks = Enumerable.Range(1, (1 << n) - 1).OrderBy(PopCount).Take(max);
        var chars = lower.ToCharArray();
        foreach (var m in masks)
        {
            var c = (char[])chars.Clone();
            for (int b = 0; b < n; b++)
                if ((m & (1 << b)) != 0) c[positions[b]] = Pairs[c[positions[b]]];
            yield return new string(c);
        }
    }

    private static int PopCount(int x) { int c = 0; while (x != 0) { c += x & 1; x >>= 1; } return c; }

    /// <summary>Copies the letter case of <paramref name="pattern"/> onto <paramref name="word"/>.</summary>
    public static string MatchCase(string pattern, string word)
    {
        if (pattern.Length == 0 || word.Length == 0) return word;
        bool allUpper = pattern.Length > 1 && pattern.All(c => !char.IsLetter(c) || char.IsUpper(c));
        if (allUpper) return word.ToUpperInvariant();
        var arr = word.ToCharArray();
        for (int i = 0; i < arr.Length && i < pattern.Length; i++)
            if (char.IsUpper(pattern[i])) arr[i] = char.ToUpperInvariant(arr[i]);
        return new string(arr);
    }
}
