using System.Globalization;
using System.IO.Compression;

namespace Qalam.Core;

/// <summary>
/// Built-in word pairs from large text corpora ("может" → "быть", "покажешь"…), so completion knows the
/// usual next word before the user has taught Qalam anything. Built by tools/bigrams/build.py.
/// File: "prev\tnext zc next zc …", zc = log10 P(next | prev) + 9 (the Zipf scale of the word lists).
/// Lines are kept as text and parsed on lookup: a few lookups per keystroke, little memory.
/// </summary>
public sealed class Bigrams
{
    public string Language { get; }
    private readonly Dictionary<string, string> _lines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, float>> _parsed = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public int Count => _lines.Count;

    public Bigrams(string language) => Language = language;

    /// <summary>Embedded bigram_xx.txt.gz, or null if this build has none for the language.</summary>
    public static Bigrams? LoadEmbedded(string language)
    {
        var asm = typeof(Bigrams).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($".bigram_{language}.txt.gz", StringComparison.Ordinal));
        if (name == null) return null;
        using var s = asm.GetManifestResourceStream(name)!;
        return Load(language, s);
    }

    public static Bigrams Load(string language, Stream gz)
    {
        var b = new Bigrams(language);
        using var z = new GZipStream(gz, CompressionMode.Decompress);
        using var r = new StreamReader(z);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            int t = line.IndexOf('\t');
            if (t <= 0 || line.Length <= t + 1) continue;
            b._lines[line[..t]] = line[(t + 1)..];
        }
        return b;
    }

    /// <summary>log10 P(next | prev) + 9, or null if the pair is not among the usual ones.</summary>
    public float? Score(string prev, string next)
    {
        var d = Followers(prev);
        return d != null && d.TryGetValue(next, out var zc) ? zc : null;
    }

    /// <summary>Usual next words after <paramref name="prev"/> (lower case) with their scores.</summary>
    public IReadOnlyDictionary<string, float>? Followers(string prev)
    {
        lock (_lock)
        {
            if (_parsed.TryGetValue(prev, out var d)) return d;
            if (!_lines.TryGetValue(prev, out var raw)) return null;
            d = new Dictionary<string, float>(StringComparer.Ordinal);
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < parts.Length; i += 2)
                if (float.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var zc))
                    d[parts[i]] = zc;
            if (_parsed.Count > 5000) _parsed.Clear();
            _parsed[prev] = d;
            return d;
        }
    }
}
