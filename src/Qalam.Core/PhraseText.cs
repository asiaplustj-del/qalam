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

    /// <summary>
    /// The model continued text that ends in the middle of a word ("дорогие дру"). Models answer either
    /// with the ending ("зья, рад вас видеть") or with the whole word ("друзья, рад…"). Returns the
    /// finished word plus a short continuation ("друзья, рад вас видеть"), or null.
    /// </summary>
    public static string? CompleteWord(string? raw, string typed, string contextBefore, Func<string, bool> isWord)
    {
        if (string.IsNullOrWhiteSpace(raw) || typed.Length == 0) return null;
        // a leading space: the model treats the typed word as finished ("дороги" + " и улицы")
        bool startsNewWord = char.IsWhiteSpace(raw[0]);
        var t = raw.Replace('\n', ' ').Trim().Trim(Quotes).Trim();
        if (t.StartsWith("...") || t.StartsWith("…")) t = t.TrimStart('.', '…', ' ');
        // echo of the words before the unfinished one
        var ctxWords = contextBefore.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int n = Math.Min(8, ctxWords.Length); n >= 1; n--)
        {
            var tail = string.Join(' ', ctxWords.Skip(ctxWords.Length - n));
            if (tail.Length >= 3 && t.StartsWith(tail + " ", StringComparison.OrdinalIgnoreCase)) { t = t[(tail.Length + 1)..].TrimStart(); break; }
        }
        if (t.Length == 0) return null;

        if (startsNewWord && isWord(typed.ToLowerInvariant()) && !Completer.SamePrefix(t, typed.ToLowerInvariant()))
            t = " " + t;
        int fwLen = 0;
        while (fwLen < t.Length && (char.IsLetter(t[fwLen]) || t[fwLen] == '-')) fwLen++;
        var fw = t[..fwLen];
        string word, rest;
        bool fullForm = fwLen > 0 && Completer.SamePrefix(fw, typed.ToLowerInvariant());
        bool endingForm = isWord((typed + fw).ToLowerInvariant());
        if (fullForm && fw.Length > typed.Length && (isWord(fw.ToLowerInvariant()) || !endingForm)) { word = fw; rest = t[fwLen..]; }
        else if (fullForm && fw.Length == typed.Length) { word = typed; rest = t[fwLen..]; }   // the word is already complete
        else if (fwLen > 0 && endingForm) { word = typed + fw; rest = t[fwLen..]; }
        else if (fwLen == 0 && isWord(typed.ToLowerInvariant())) { word = typed; rest = t; }   // ", и …" right after the word
        else return null;

        // keep what the user typed exactly, take only the new letters from the model
        word = char.IsUpper(typed[0]) ? char.ToUpperInvariant(word[0]) + word[1..] : char.ToLowerInvariant(word[0]) + word[1..];
        if (word.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) word = typed + word[typed.Length..];

        // a short continuation: up to the end of the sentence, at most 5 more words
        int cut = rest.IndexOfAny(new[] { '.', '!', '?', '…' });
        if (cut >= 0) rest = rest[..(cut + 1)];
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 5) rest = (rest.StartsWith(' ') ? " " : "") + string.Join(' ', words.Take(5));
        rest = rest.TrimEnd().TrimEnd(Quotes);
        var result = word + rest;
        if (result.Length <= typed.Length || result.Contains('<') || result.Contains('*') || result.Contains('#') || result.Contains("http")) return null;
        return result;
    }
}
