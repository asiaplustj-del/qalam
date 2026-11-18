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
}

public sealed class QalamSettings
{
    public bool AutoMode { get; set; } = true;
    public bool Paused { get; set; }
    public bool StartWithSystem { get; set; } = true;
    public bool LearnFromUndo { get; set; } = true;
    public bool Sound { get; set; }
    public bool FixSingleLetters { get; set; } = true;
    /// <summary>1 = careful … 3 = aggressive (n-gram margin for unknown words).</summary>
    public int Sensitivity { get; set; } = 2;

    public Hotkey ManualHotkey { get; set; } = OperatingSystem.IsMacOS() ? Hotkey.DoubleShift : Hotkey.Pause;
    public Hotkey SelectionHotkey { get; set; } = OperatingSystem.IsMacOS() ? Hotkey.DoubleCtrl : Hotkey.ShiftPause;
    public Hotkey PauseHotkey { get; set; } = OperatingSystem.IsMacOS() ? Hotkey.DoubleAlt : Hotkey.AltPause;

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
        _ => h.ToString()
    };
}
