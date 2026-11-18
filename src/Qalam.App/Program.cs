using Avalonia;
using Qalam.Core;

namespace Qalam.App;

internal static class Program
{
    private static Mutex? _mutex;
    private static FileStream? _lock;

    [STAThread]
    public static int Main(string[] args)
    {
        if (!AcquireSingleInstance()) return 0;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<QalamApp>()
        .UsePlatformDetect()
        .WithInterFont()
        .With(new MacOSPlatformOptions { ShowInDock = false })
        .LogToTrace();

    private static bool AcquireSingleInstance()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _mutex = new Mutex(true, @"Local\QalamKeyboardSwitcher", out bool created);
                return created;
            }
            Directory.CreateDirectory(QalamSettings.DataDir);
            _lock = new FileStream(Path.Combine(QalamSettings.DataDir, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch { return false; }
    }
}
