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
    void SetStartWithSystem(bool enabled);
}
