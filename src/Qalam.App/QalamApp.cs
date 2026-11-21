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
    public NeuralService? Neural { get; private set; }
    private SuggestionPopup? _popup;
    public event Action? Changed;

    private TrayIcon? _tray;
    private NativeMenuItem? _autoItem, _pauseItem;
    private WindowIcon? _icoNormal, _icoPaused, _icoFlash;
    private SettingsWindow? _window;
    private DispatcherTimer? _flashTimer;

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Resources["SystemAccentColor"] = Ui.OrangeC;
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
        Log.Info($"Qalam {typeof(QalamApp).Assembly.GetName().Version} start on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("unobserved task", e.Exception); e.SetObserved(); };
        Dispatcher.UIThread.UnhandledException += (_, e) => { Log.Error("UI exception", e.Exception); e.Handled = true; };
        StartWatchdog();
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
        var models = await Task.Run(() =>
        {
            var list = new[] { "en", "ru", "tg" }.Select(LanguageModel.LoadEmbedded).ToList();
            foreach (var m in list) m.Warmup();   // prefix index for word completion
            return list;
        });
        Log.Info($"dictionaries loaded: {string.Join(", ", models.Select(m => $"{m.Language}={m.WordCount}"))}");
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

        Log.Mark("reading layouts");
        var layouts = platform.GetLayouts();
        Log.Info("layouts: " + string.Join(", ", layouts.Select(l => $"{l.Language}:{l.Id}")));
        Log.Mark("idle");
        var supported = layouts.Where(l => detector.Model(l.Language) != null).ToList();
        if (supported.Count == 0) supported = new() { KeyboardLayout.English, KeyboardLayout.Russian };
        var engine = new SwitcherEngine(detector, supported, supported[0]);
        Controller = new QalamController(platform, engine, Settings);
        Platform = platform;
        Controller.Corrected += c => Dispatcher.UIThread.Post(() => OnCorrected(c));
        Controller.StateChanged += () => Dispatcher.UIThread.Post(UpdateState);
        Neural = new NeuralService(Settings, platform);
        Neural.Engine.StateChanged += () => Dispatcher.UIThread.Post(() => Changed?.Invoke());
        _ = Neural.RefreshAsync().ContinueWith(_ => Dispatcher.UIThread.Post(() => Changed?.Invoke()));
        Controller.SuggestionChanged += sug => Dispatcher.UIThread.Post(() => OnSuggestion(sug));
        Controller.PhraseOpportunity += (v, text) => Neural.OnPhraseOpportunity(Controller, v, text);

        bool perms = platform.HasPermissions();
        Log.Info($"permissions: {perms}");
        if (perms) platform.Start();
        else
        {
            // macOS: wait until the user grants Accessibility / Input Monitoring
            ShowSettings();
            var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            retry.Tick += (_, _) =>
            {
                if (!platform.HasPermissions()) return;
                retry.Stop();
                Log.Info("permissions granted, starting");
                platform.Start();
                Changed?.Invoke();
            };
            retry.Start();
        }
        platform.SetStartWithSystem(Settings.StartWithSystem);
        UpdateState();
    }

    private void OnSuggestion(QalamController.Suggestion? sug)
    {
        _popup ??= new SuggestionPopup();
        if (sug == null) { _popup.Hide(); return; }
        // The caret position is asked from the OTHER app (accessibility API) — it can be slow,
        // so never on the UI thread, and with a hard time limit.
        _ = Task.Run(async () =>
        {
            await Task.Delay(30);   // let the target app move its caret after the keystroke
            if (Controller?.CurrentSuggestion != sug) return;
            var query = Task.Run(() => { Log.Mark("caret query"); var r = Platform?.GetCaretRect(); Log.Mark("idle"); return r; });
            var done = await Task.WhenAny(query, Task.Delay(400));
            var caret = done == query ? query.Result : null;
            Dispatcher.UIThread.Post(() =>
            {
                if (Controller?.CurrentSuggestion != sug) return;
                if (caret is not { } c) { _popup.Hide(); return; }
                double scale = OperatingSystem.IsMacOS() ? (_popup.Screens.Primary?.Scaling ?? 1) : 1;  // macOS reports points
                _popup.ShowSuggestion(sug, new PixelPoint((int)(c.X * scale), (int)((c.Y + c.H + 4) * scale)));
            });
        });
    }

    /// <summary>Logs when the UI thread stops responding (and what the app was doing).</summary>
    private static void StartWatchdog()
    {
        var t = new Thread(() =>
        {
            bool reported = false;
            while (true)
            {
                Thread.Sleep(2000);
                var ping = Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Send);
                if (!ping.GetTask().Wait(5000))
                {
                    if (!reported) Log.Error($"UI thread not responding; last activity: '{Log.Current}' for {Log.CurrentFor.TotalSeconds:F0}s");
                    reported = true;
                }
                else if (reported) { Log.Info("UI thread responsive again"); reported = false; }
            }
        }) { IsBackground = true, Name = "Qalam watchdog" };
        t.Start();
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
        Neural?.ApplySettings();
        UpdateState();
    }

    public void Quit()
    {
        Controller?.Dispose();
        Neural?.Dispose();
        if (_tray != null) _tray.IsVisible = false;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
