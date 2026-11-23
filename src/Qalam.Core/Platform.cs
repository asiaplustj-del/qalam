namespace Qalam.Core;

public enum SpecialKey
{
    None,            // printable key → PhysicalKey is set
    Space, Enter, Tab, Backspace,
    Pause,
    Shift, Ctrl, Alt, Meta,   // modifier keys themselves (down/up)
    Navigation,      // arrows, Home/End, PgUp/PgDn, Delete, Esc, F-keys, Insert…
    Other
}

/// <summary>A raw keyboard event from the OS, already mapped to a physical key.</summary>
public readonly record struct KeyInput(
    bool IsDown,
    SpecialKey Special,
    char PhysicalKey = '\0',
    bool Shift = false, bool Caps = false, bool Ctrl = false, bool Alt = false, bool Meta = false,
    bool RightSide = false,
    /// <summary>Stable key name for custom hotkeys: "Q", "1", "F9", "Pause", "Insert", "CapsLock", "Num5"…</summary>
    string? KeyName = null);

/// <summary>Font of the text in another app's field, as its accessibility API reports it.</summary>
/// <param name="Family">Family name ("Helvetica Neue", "Segoe UI"); names starting with "." are hidden system fonts.</param>
/// <param name="Size">Font size in the same units as <see cref="IKeyboardPlatform.GetCaretRect"/> (macOS: points, Windows: pixels).</param>
/// <param name="Weight">100…900 (400 regular, 700 bold).</param>
public sealed record CaretFont(string Family, double Size, int Weight, bool Italic)
{
    /// <summary>Weight guessed from a font face name: "HelveticaNeue-Bold" → 700, ".SFNS-Semibold" → 600.</summary>
    public static int WeightFromName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return 400;
        var n = name.ToLowerInvariant().Replace(" ", "").Replace("-", "");
        if (n.Contains("black") || n.Contains("heavy")) return 900;
        if (n.Contains("extrabold") || n.Contains("ultrabold")) return 800;
        if (n.Contains("semibold") || n.Contains("demibold")) return 600;
        if (n.Contains("bold")) return 700;
        if (n.Contains("medium")) return 500;
        if (n.Contains("extralight") || n.Contains("ultralight")) return 200;
        if (n.Contains("thin") || n.Contains("hairline")) return 100;
        if (n.Contains("light")) return 300;
        return 400;
    }

    public static bool ItalicFromName(string? name) =>
        name != null && (name.Contains("Italic", StringComparison.OrdinalIgnoreCase) || name.Contains("Oblique", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Everything the controller needs from Windows / macOS.</summary>
public interface IKeyboardPlatform : IDisposable
{
    string Name { get; }
    /// <summary>Key events. Return true to swallow the key (it won't reach the app).</summary>
    Func<KeyInput, bool>? KeyHandler { get; set; }
    /// <summary>Mouse click anywhere → the cursor may have moved.</summary>
    event Action? MouseClicked;

    bool HasPermissions();
    void RequestPermissions();
    void Start();

    IReadOnlyList<KeyboardLayout> GetLayouts();
    KeyboardLayout? GetCurrentLayout(IReadOnlyList<KeyboardLayout> known);
    void SwitchLayout(KeyboardLayout layout);

    /// <summary>Delete <paramref name="erase"/> chars with Backspace and type <paramref name="text"/>. Executed asynchronously, in order.</summary>
    void Replace(int erase, string text, Action? then = null);
    /// <summary>Re-send a key that was swallowed (Space/Enter/Tab), after queued replacements.</summary>
    void ReplayKey(SpecialKey key);
    /// <summary>Copy the selected text (Ctrl/Cmd+C) and return it, restoring the clipboard later.</summary>
    void ConvertSelection(Func<string, string?> convert);

    string? ForegroundApp();
    /// <summary>Text caret rectangle on screen in physical pixels (top-left origin), or null if unknown.</summary>
    (int X, int Y, int W, int H)? GetCaretRect();
    /// <summary>Frame of the focused text field (when the exact caret is unknown), physical pixels; null if unknown.</summary>
    (int X, int Y, int W, int H)? GetFocusRect() => null;
    /// <summary>Font of the text right before the caret in the focused field (for inline suggestions); null if unknown.</summary>
    CaretFont? GetCaretFont() => null;
    void SetStartWithSystem(bool enabled);
    /// <summary>True when a laptop runs on battery (no charger).</summary>
    bool IsOnBattery();
}
