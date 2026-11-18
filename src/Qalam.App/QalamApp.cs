using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Qalam.Core;

namespace Qalam.App;

public sealed class QalamApp : Application
{
    public static QalamApp Instance => (QalamApp)Current!;
    public QalamController? Controller { get; private set; }
    public IKeyboardPlatform? Platform { get; private set; }
    public QalamSettings Settings { get; private set; } = QalamSettings.Load();
    public int CorrectionsCount { get; private set; }
    public event Action? Changed;

    private TrayIcon? _tray;
    private NativeMenuItem? _autoItem, _pauseItem;
    private WindowIcon? _icoNormal, _icoPaused, _icoFlash;
    private SettingsWindow? _window;
    private DispatcherTimer? _flashTimer;

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Resources["SystemAccentColor"] = Theme.OrangeC;
        Resources["SystemAccentColorLight1"] = Color.Parse("#F57A42");
        Resources["SystemAccentColorLight2"] = Color.Parse("#F7966A");
        Resources["SystemAccentColorLight3"] = Color.Parse("#FAB492");
        Resources["SystemAccentColorDark1"] = Color.Parse("#D94E12");
        Resources["SystemAccentColorDark2"] = Color.Parse("#B8410E");
        Resources["SystemAccentColorDark3"] = Color.Parse("#96340B");
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            bool startHidden = desktop.Args?.Contains("--tray") == true;
            SetupTray();
            if (!startHidden) ShowSettings();
            _ = InitEngineAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitEngineAsync()
    {
        // dictionaries load in ~1 s; keep the UI responsive meanwhile
        var models = await Task.Run(() => new[] { "en", "ru", "tg" }.Select(LanguageModel.LoadEmbedded).ToList());
        var user = UserDictionary.Load(Path.Combine(QalamSettings.DataDir, "user.json"));
        var detector = new Detector(models, user);

        IKeyboardPlatform platform;
        if (OperatingSystem.IsWindows()) platform = new Qalam.Platform.Windows.WindowsPlatform();
        else if (OperatingSystem.IsMacOS())
        {
            var mac = new Qalam.Platform.Mac.MacPlatform { PostToMain = a => Dispatcher.UIThread.Post(a) };
            mac.PollCurrentLayout();
            var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            poll.Tick += (_, _) => mac.PollCurrentLayout();
            poll.Start();
            platform = mac;
        }
        else return;

        var layouts = platform.GetLayouts();
        var supported = layouts.Where(l => detector.Model(l.Language) != null).ToList();
        if (supported.Count == 0) supported = new() { KeyboardLayout.English, KeyboardLayout.Russian };
        var engine = new SwitcherEngine(detector, supported, supported[0]);
        Controller = new QalamController(platform, engine, Settings);
        Platform = platform;
        Controller.Corrected += c => Dispatcher.UIThread.Post(() => OnCorrected(c));
        Controller.StateChanged += () => Dispatcher.UIThread.Post(UpdateState);

        if (platform.HasPermissions()) platform.Start();
        else
        {
            // macOS: wait until the user grants Accessibility / Input Monitoring
            ShowSettings();
            var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            retry.Tick += (_, _) =>
            {
                if (!platform.HasPermissions()) return;
                retry.Stop();
                platform.Start();
                Changed?.Invoke();
            };
            retry.Start();
        }
        platform.SetStartWithSystem(Settings.StartWithSystem);
        UpdateState();
    }

    private static WindowIcon LoadIcon(string name)
    {
        using var s = typeof(QalamApp).Assembly.GetManifestResourceStream($"Qalam.App.Assets.{name}")!;
        return new WindowIcon(new Bitmap(s));
    }

    public static Bitmap LoadBitmap(string name)
    {
        using var s = typeof(QalamApp).Assembly.GetManifestResourceStream($"Qalam.App.Assets.{name}")!;
        return new Bitmap(s);
    }

    private void SetupTray()
    {
        _icoNormal = LoadIcon("tray.png");
        _icoPaused = LoadIcon("tray-paused.png");
        _icoFlash = LoadIcon("tray-flash.png");

        var menu = new NativeMenu();
        _autoItem = new NativeMenuItem("Автоисправление") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = Settings.AutoMode };
        _autoItem.Click += (_, _) => { if (Controller != null) Controller.AutoMode = !Controller.AutoMode; };
        _pauseItem = new NativeMenuItem("Пауза") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = Settings.Paused };
        _pauseItem.Click += (_, _) => TogglePause();
        var settings = new NativeMenuItem("Настройки…");
        settings.Click += (_, _) => ShowSettings();
        var quit = new NativeMenuItem("Выйти");
        quit.Click += (_, _) => Quit();
        menu.Items.Add(_autoItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(settings);
        menu.Items.Add(quit);

        _tray = new TrayIcon { Icon = _icoNormal, ToolTipText = "Qalam — переключатель раскладки", Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) => ShowSettings();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    public void TogglePause()
    {
        if (Controller != null) Controller.Paused = !Controller.Paused;
        else { Settings.Paused = !Settings.Paused; Settings.Save(); UpdateState(); }
    }

    public void UpdateState()
    {
        if (_autoItem != null) _autoItem.IsChecked = Settings.AutoMode;
        if (_pauseItem != null) _pauseItem.IsChecked = Settings.Paused;
        if (_tray != null)
        {
            _tray.Icon = Settings.Paused ? _icoPaused : _icoNormal;
            _tray.ToolTipText = Settings.Paused ? "Qalam — на паузе" : $"Qalam — работает · {CurrentLayoutName()}";
        }
        Changed?.Invoke();
    }

    public string CurrentLayoutName() => Controller?.Engine.CurrentLayout.Language.ToUpperInvariant() ?? "…";

    private void OnCorrected(Correction c)
    {
        CorrectionsCount++;
        if (_tray != null && !Settings.Paused)
        {
            _tray.Icon = _icoFlash;
            _flashTimer?.Stop();
            _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _flashTimer.Tick += (_, _) => { _flashTimer!.Stop(); UpdateState(); };
            _flashTimer.Start();
        }
        _window?.OnCorrected(c);
        Changed?.Invoke();
    }

    public void ShowSettings()
    {
        _window ??= new SettingsWindow(this);
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void SaveSettings()
    {
        Settings.Save();
        Controller?.ApplySettings();
        Platform?.SetStartWithSystem(Settings.StartWithSystem);
        UpdateState();
    }

    public void Quit()
    {
        Controller?.Dispose();
        if (_tray != null) _tray.IsVisible = false;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
