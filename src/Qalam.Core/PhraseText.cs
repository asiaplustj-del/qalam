namespace Qalam.Core;

/// <summary>Turns a raw model answer into a short, clean continuation of the user's text.</summary>
public static class PhraseText
{
    private static readonly char[] Quotes = { '"', '«', '»', '“', '”', '„', '\'', '`' };

    public static string? Clean(string? raw, string context)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = raw.Replace('\n', ' ').Trim().Trim(Quotes).Trim();
        if (t.StartsWith("...") || t.StartsWith("…")) t = t.TrimStart('.', '…', ' ');

        // models sometimes repeat the end of what was already typed — drop the echo
        var ctxWords = context.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int n = Math.Min(8, ctxWords.Length); n >= 1; n--)
        {
            var tail = string.Join(' ', ctxWords.Skip(ctxWords.Length - n));
            if (tail.Length >= 3 && t.StartsWith(tail, StringComparison.OrdinalIgnoreCase))
            {
                t = t[tail.Length..].TrimStart();
                break;
            }
        }

        int cut = t.IndexOfAny(new[] { '.', '!', '?', '…' });
        if (cut >= 0) t = t[..(cut + 1)];
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 7) t = string.Join(' ', words.Take(7));
        t = t.Trim().Trim(Quotes).TrimEnd();
        if (t.Length < 2 || t.Contains('<') || t.Contains('*') || t.Contains('#') || t.Contains("http")) return null;
        return t;
    }
}
