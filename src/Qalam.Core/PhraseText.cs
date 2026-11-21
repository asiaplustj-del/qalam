namespace Qalam.Core;

/// <summary>Turns a raw model answer into a short, clean continuation of the user's text.</summary>
public static class PhraseText
{
    private static readonly char[] Quotes = { '"', '«', '»', '“', '”', '„', '\'', '`' };

    /// <param name="maxWords">Longest continuation kept.</param>
    /// <param name="sentences">How many sentence ends may be included (1 = stop at the first one).</param>
    public static string? Clean(string? raw, string context, int maxWords = 7, int sentences = 1)
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

        t = CutSentences(t, sentences);
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > maxWords) t = string.Join(' ', words.Take(maxWords));
        t = t.Trim().Trim(Quotes).TrimEnd();
        if (t.Length < 2 || t.Contains('<') || t.Contains('*') || t.Contains('#') || t.Contains("http")) return null;
        return t;
    }

    /// <summary>
    /// The model continued text that ends in the middle of a word ("дорогие дру"). Models answer either
    /// with the ending ("зья, рад вас видеть") or with the whole word ("друзья, рад…"). Returns the
    /// finished word plus a short continuation ("друзья, рад вас видеть"), or null.
    /// </summary>
    public static string? CompleteWord(string? raw, string typed, string contextBefore, Func<string, bool> isWord, int maxMoreWords = 5)
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
        rest = CutSentences(rest, 1);
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > maxMoreWords) rest = (rest.StartsWith(' ') ? " " : "") + string.Join(' ', words.Take(maxMoreWords));
        rest = rest.TrimEnd().TrimEnd(Quotes);
        var result = word + rest;
        if (result.Length <= typed.Length || result.Contains('<') || result.Contains('*') || result.Contains('#') || result.Contains("http")) return null;
        return result;
    }

    private static string CutSentences(string t, int sentences)
    {
        int from = 0;
        for (int n = 0; n < sentences; n++)
        {
            int cut = t.IndexOfAny(new[] { '.', '!', '?', '…' }, from);
            if (cut < 0) return t;
            while (cut + 1 < t.Length && t[cut + 1] is '.' or '!' or '?' or '…') cut++;   // "?!", "..."
            if (n == sentences - 1) return t[..(cut + 1)];
            from = cut + 1;
        }
        return t;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '\'' or '’';

    /// <summary>
    /// What one Tab press inserts: the next word, or the next punctuation mark ("друзья, как дела?" →
    /// "друзья" | ", " … → "," | " как" | " дела" | "?"). Returns the chunk and the rest.
    /// </summary>
    public static (string Chunk, string Tail) FirstChunk(string text)
    {
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i < text.Length && IsWordChar(text[i]))
            while (i < text.Length && IsWordChar(text[i])) i++;
        else
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && !IsWordChar(text[i])) i++;
        return (text[..i], text[i..]);
    }
}
