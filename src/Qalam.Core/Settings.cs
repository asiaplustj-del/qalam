using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qalam.Core;

public enum Hotkey
{
    None,
    Pause,          // Pause/Break
    ShiftPause,
    AltPause,
    DoubleShift,    // tap Shift twice quickly
    DoubleCtrl,
    DoubleAlt,      // Alt / Option
    RightCtrl,      // single tap of the right Ctrl
    Custom,         // user-recorded combination (see *Combo properties)
}

public enum HotkeyAction { Manual, Selection, Pause }

/// <summary>A user-recorded shortcut, e.g. Ctrl+Shift+Q or F9.</summary>
public sealed record KeyCombo(bool Ctrl, bool Alt, bool Shift, bool Meta, string Key)
{
    private static readonly HashSet<string> TypingKeys = new() { "Space", "Enter", "Tab", "Backspace" };

    public bool Matches(KeyInput k) => k.KeyName == Key && k.Ctrl == Ctrl && k.Alt == Alt && k.Shift == Shift && k.Meta == Meta;

    /// <summary>Plain letters/digits (or with Shift only) would break normal typing.</summary>
    public bool IsSafe
    {
        get
        {
            if (string.IsNullOrEmpty(Key)) return false;
            if (Ctrl || Alt || Meta) return true;
            bool printable = Key.Length == 1 || TypingKeys.Contains(Key);
            return !printable;
        }
    }

    public override string ToString()
    {
        var parts = new List<string>();
        bool mac = OperatingSystem.IsMacOS();
        if (Ctrl) parts.Add(mac ? "⌃" : "Ctrl");
        if (Alt) parts.Add(mac ? "⌥" : "Alt");
        if (Shift) parts.Add(mac ? "⇧" : "Shift");
        if (Meta) parts.Add(mac ? "⌘" : "Win");
        parts.Add(Key);
        return string.Join(mac ? " " : " + ", parts);
    }
}

public sealed class QalamSettings
{
    public bool AutoMode { get; set; } = true;
    public bool Paused { get; set; }
    public bool StartWithSystem { get; set; } = true;
    public bool LearnFromUndo { get; set; } = true;
    public bool Sound { get; set; }
    public bool FixSingleLetters { get; set; } = true;
    /// <summary>"хамчун" → "ҳамчун".</summary>
    public bool RestoreTajik { get; set; } = true;
    /// <summary>Dictionary word completion, accepted with Tab.</summary>
    public bool WordCompletion { get; set; } = true;
    /// <summary>Show suggestions as semi-transparent text right in the line (true) or in a separate box (false).</summary>
    public bool InlineSuggestions { get; set; } = true;
    /// <summary>Check GitHub for a new version once a day.</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }
    /// <summary>Show the suggestion above the text line (macOS shows its own predictions below it).</summary>
    public bool SuggestionAbove { get; set; } = OperatingSystem.IsMacOS();

    // ---- optional neural module (local model via Ollama) ----
    public bool NeuralEnabled { get; set; }
    public string NeuralUrl { get; set; } = "http://localhost:11434";
    public string NeuralModel { get; set; } = "gemma3:1b";
    /// <summary>Pause after a space before asking the model, ms.</summary>
    public int NeuralDelayMs { get; set; } = 400;
    /// <summary>"builtin" — llama.cpp inside Qalam with a GGUF file; "ollama" — external Ollama server.</summary>
    public string NeuralBackend { get; set; } = "builtin";
    /// <summary>GGUF model file for the built-in engine.</summary>
    public string? NeuralModelPath { get; set; }
    /// <summary>Unload the model from memory after this many idle minutes (0 = keep loaded).</summary>
    public int NeuralUnloadMinutes { get; set; } = 10;
    /// <summary>Don't run the model while a laptop is on battery.</summary>
    public bool NeuralSkipOnBattery { get; set; }
    /// <summary>1 = careful … 3 = aggressive (n-gram margin for unknown words).</summary>
    public int Sensitivity { get; set; } = 2;

    public Hotkey ManualHotkey { get; set; } = OperatingSystem.IsMacOS() ? Hotkey.DoubleShift : Hotkey.Pause;
    public Hotkey SelectionHotkey { get; set; } = OperatingSystem.IsMacOS() ? Hotkey.DoubleCtrl : Hotkey.ShiftPause;
    public Hotkey PauseHotkey { get; set; } = OperatingSystem.IsMacOS() ? Hotkey.DoubleAlt : Hotkey.AltPause;

    public KeyCombo? ManualCombo { get; set; }
    public KeyCombo? SelectionCombo { get; set; }
    public KeyCombo? PauseCombo { get; set; }

    public Hotkey GetHotkey(HotkeyAction a) => a switch { HotkeyAction.Manual => ManualHotkey, HotkeyAction.Selection => SelectionHotkey, _ => PauseHotkey };
    public KeyCombo? GetCombo(HotkeyAction a) => a switch { HotkeyAction.Manual => ManualCombo, HotkeyAction.Selection => SelectionCombo, _ => PauseCombo };
    public void SetCustom(HotkeyAction a, KeyCombo c)
    {
        // a combination can belong to one action only
        foreach (var other in Enum.GetValues<HotkeyAction>())
            if (other != a && GetHotkey(other) == Hotkey.Custom && GetCombo(other) == c) SetPreset(other, Hotkey.None);
        switch (a)
        {
            case HotkeyAction.Manual: ManualHotkey = Hotkey.Custom; ManualCombo = c; break;
            case HotkeyAction.Selection: SelectionHotkey = Hotkey.Custom; SelectionCombo = c; break;
            default: PauseHotkey = Hotkey.Custom; PauseCombo = c; break;
        }
    }
    public void SetPreset(HotkeyAction a, Hotkey h)
    {
        switch (a) { case HotkeyAction.Manual: ManualHotkey = h; break; case HotkeyAction.Selection: SelectionHotkey = h; break; default: PauseHotkey = h; break; }
    }
    public string DescribeAction(HotkeyAction a) => GetHotkey(a) == Hotkey.Custom ? GetCombo(a)?.ToString() ?? "не задана" : Describe(GetHotkey(a));

    /// <summary>Process / app names where Qalam stays silent (password managers, games, terminals…).</summary>
    public List<string> ExcludedApps { get; set; } = new() { "KeePass", "KeePassXC", "1Password", "Bitwarden" };
    /// <summary>Layout languages taking part in switching (empty = all supported).</summary>
    public List<string> Languages { get; set; } = new() { "en", "ru", "tg" };

    [JsonIgnore] public string? FilePath { get; set; }

    public static string DataDir
    {
        get
        {
            var baseDir = OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(baseDir, "Qalam");
        }
    }

    public static QalamSettings Load()
    {
        var path = Path.Combine(DataDir, "settings.json");
        QalamSettings? s = null;
        try { if (File.Exists(path)) s = JsonSerializer.Deserialize<QalamSettings>(File.ReadAllText(path), Json); } catch { }
        s ??= new QalamSettings();
        s.FilePath = path;
        return s;
    }

    public void Save()
    {
        if (FilePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Describe(Hotkey h) => h switch
    {
        Hotkey.None => "Выключено",
        Hotkey.Pause => "Pause/Break",
        Hotkey.ShiftPause => "Shift + Pause",
        Hotkey.AltPause => OperatingSystem.IsMacOS() ? "⌥ + Pause" : "Alt + Pause",
        Hotkey.DoubleShift => "Shift дважды",
        Hotkey.DoubleCtrl => OperatingSystem.IsMacOS() ? "⌃ Control дважды" : "Ctrl дважды",
        Hotkey.DoubleAlt => OperatingSystem.IsMacOS() ? "⌥ Option дважды" : "Alt дважды",
        Hotkey.RightCtrl => "Правый Ctrl",
        Hotkey.Custom => "Своя комбинация",
        _ => h.ToString()
    };
}
