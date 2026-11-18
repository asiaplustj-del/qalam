using System.Text;

namespace Qalam.Core;

/// <summary>
/// Physical key = its character on the US QWERTY layout (unshifted), e.g. 'q', '[', ';'.
/// Platform layers translate scan codes into these ids, so the engine never depends on the OS.
/// </summary>
public readonly record struct KeyStroke(char Key, bool Shift, bool Caps = false);

public sealed class KeyboardLayout
{
    public const string PhysicalKeys = "`1234567890-=qwertyuiop[]\\asdfghjkl;'zxcvbnm,./";

    public string Id { get; }          // e.g. "en-US", "ru-RU", "tg-TJ"
    public string Language { get; }    // "en", "ru", "tg"
    public string DisplayName { get; }
    /// <summary>Opaque platform handle (HKL on Windows, input source id on macOS).</summary>
    public object? NativeHandle { get; set; }

    private readonly Dictionary<char, (char Normal, char Shifted)> _map = new();
    private readonly Dictionary<char, KeyStroke> _reverse = new();

    public KeyboardLayout(string id, string language, string displayName, IDictionary<char, (char, char)> map)
    {
        Id = id; Language = language; DisplayName = displayName;
        foreach (var (k, v) in map)
        {
            _map[k] = v;
            if (v.Item1 != '\0') _reverse.TryAdd(v.Item1, new KeyStroke(k, false));
            if (v.Item2 != '\0') _reverse.TryAdd(v.Item2, new KeyStroke(k, true));
        }
    }

    /// <summary>Builds a layout from two 47-char strings aligned with <see cref="PhysicalKeys"/>.</summary>
    public static KeyboardLayout FromRows(string id, string lang, string name, string normal, string shifted)
    {
        if (normal.Length != PhysicalKeys.Length || shifted.Length != PhysicalKeys.Length)
            throw new ArgumentException($"Layout {id}: rows must have {PhysicalKeys.Length} chars");
        var map = new Dictionary<char, (char, char)>();
        for (int i = 0; i < PhysicalKeys.Length; i++) map[PhysicalKeys[i]] = (normal[i], shifted[i]);
        return new KeyboardLayout(id, lang, name, map);
    }

    public char Render(KeyStroke s)
    {
        if (!_map.TryGetValue(s.Key, out var v)) return '\0';
        char c = s.Shift ? v.Shifted : v.Normal;
        if (s.Caps && char.IsLetter(c))
            c = char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c);
        return c;
    }

    public string Render(IEnumerable<KeyStroke> strokes)
    {
        var sb = new StringBuilder();
        foreach (var s in strokes) { var c = Render(s); if (c != '\0') sb.Append(c); }
        return sb.ToString();
    }

    /// <summary>Text → key strokes typed on this layout. Returns false if some char can't be typed.</summary>
    public bool TryEncode(string text, out List<KeyStroke> strokes)
    {
        strokes = new List<KeyStroke>(text.Length);
        bool ok = true;
        foreach (var ch in text)
        {
            if (_reverse.TryGetValue(ch, out var s)) { strokes.Add(s); continue; }
            // letter with case we don't have directly (e.g. via caps)
            var lower = char.ToLowerInvariant(ch); var upper = char.ToUpperInvariant(ch);
            if (_reverse.TryGetValue(lower, out s)) { strokes.Add(s with { Shift = !s.Shift }); continue; }
            if (_reverse.TryGetValue(upper, out s)) { strokes.Add(s with { Shift = !s.Shift }); continue; }
            strokes.Add(new KeyStroke('\0', false)); // passthrough marker
            ok = false;
        }
        return ok;
    }

    public bool CanType(char c) => _reverse.ContainsKey(c) || _reverse.ContainsKey(char.ToLowerInvariant(c));

    public override string ToString() => DisplayName;

    // ---------- Built-in defaults (used when the OS can't be queried, and in tests) ----------
    //                                         `1234567890-=qwertyuiop[]\asdfghjkl;'zxcvbnm,./
    public static KeyboardLayout English => FromRows("en-US", "en", "English",
                                              "`1234567890-=qwertyuiop[]\\asdfghjkl;'zxcvbnm,./",
                                              "~!@#$%^&*()_+QWERTYUIOP{}|ASDFGHJKL:\"ZXCVBNM<>?");
    public static KeyboardLayout Russian => FromRows("ru-RU", "ru", "Русский",
                                              "ё1234567890-=йцукенгшщзхъ\\фывапролджэячсмитьбю.",
                                              "Ё!\"№;%:?*()_+ЙЦУКЕНГШЩЗХЪ/ФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,");
    /// <summary>Official Tajik standard layout (Windows KLID 00000428, xkb "tj").</summary>
    public static KeyboardLayout Tajik => FromRows("tg-TJ", "tg", "Тоҷикӣ",
                                              "ё1234567890ғӯйқукенгшҳзхъ\\фҷвапролджэячсмитӣбю.",
                                              "Ё!\"№;%:?*()ҒӮЙҚУКЕНГШҲЗХЪ/ФҶВАПРОЛДЖЭЯЧСМИТӢБЮ,");
}
