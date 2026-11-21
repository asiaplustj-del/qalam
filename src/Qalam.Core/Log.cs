namespace Qalam.Core;

/// <summary>
/// Tiny diagnostic log: %APPDATA%\Qalam\qalam.log or ~/Library/Application Support/Qalam/qalam.log.
/// Keeps ~1 MB. <see cref="Mark"/> records what the app is doing right now, so a hang can be traced.
/// </summary>
public static class Log
{
    private static readonly object Sync = new();
    private static readonly string FilePath = Path.Combine(QalamSettings.DataDir, "qalam.log");
    private static volatile string _current = "start";
    private static DateTime _currentAt = DateTime.UtcNow;

    public static string Current => _current;
    public static TimeSpan CurrentFor => DateTime.UtcNow - _currentAt;

    public static void Mark(string what) { _current = what; _currentAt = DateTime.UtcNow; }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg, Exception? e = null) => Write("ERROR", e == null ? msg : $"{msg}: {e}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(QalamSettings.DataDir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000)
                {
                    var old = FilePath + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(FilePath, old);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
