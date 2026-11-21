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
    private GhostText? _ghost;
    public Updater.Release? AvailableUpdate { get; private set; }
    private NativeMenuItem? _updateItem;
    private NativeMenu? _menu;
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
            StartUpdateTimer();
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

    private DateTime _lastNoCaretLog;

    private void OnSuggestion(QalamController.Suggestion? sug)
    {
        _popup ??= new SuggestionPopup();
        _ghost ??= new GhostText();
        if (sug == null) { _popup.Hide(); _ghost.Hide(); return; }
        _ = Task.Run(async () =>
        {
            await Task.Delay(30);   // let the target app move its caret after the keystroke
            if (Controller?.CurrentSuggestion != sug) return;

            // 1) our own window (the "Проверить" field): measured by Avalonia itself
            var own = await Dispatcher.UIThread.InvokeAsync(() => _window?.OwnCaret());
            if (own is { } o)
            {
                Dispatcher.UIThread.Post(() => { if (Controller?.CurrentSuggestion == sug) Show(sug, o.Top, o.Height, exact: true); });
                return;
            }

            // 2) another app: ask its accessibility API — slow apps must not stall us, so with a hard time limit
            var query = Task.Run(() =>
            {
                Log.Mark("caret query");
                var c = Platform?.GetCaretRect();
                bool exact = c != null;
                if (c == null)
                {
                    // 3) caret unknown (Telegram, some Electron apps): a small single-line field still gives
                    //    a useful place — just under its left edge, shown as the box
                    var f = Platform?.GetFocusRect();
                    if (f is { } fr && fr.H <= 90) c = (fr.X + 6, fr.Y + 4, 1, fr.H - 8);
                }
                Log.Mark("idle");
                return (c, exact);
            });
            var done = await Task.WhenAny(query, Task.Delay(400));
            var (caret, isExact) = done == query ? query.Result : (null, false);
            if (caret == null || !isExact)
            {
                if (DateTime.UtcNow - _lastNoCaretLog > TimeSpan.FromSeconds(30))
                {
                    _lastNoCaretLog = DateTime.UtcNow;
                    Log.Info($"suggestion '{sug.Text}': caret {(caret == null ? (done == query ? "unknown" : "timeout") : "≈ field")} in {Platform?.ForegroundApp()}");
                }
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (Controller?.CurrentSuggestion != sug) return;
                if (caret is not { } c) { _popup.Hide(); _ghost.Hide(); return; }
                double scale = OperatingSystem.IsMacOS() ? (_popup.Screens.Primary?.Scaling ?? 1) : 1;  // macOS reports points
                Show(sug, new PixelPoint((int)(c.X * scale), (int)(c.Y * scale)), (int)(c.H * scale), isExact);
            });
        });
    }

    private void Show(QalamController.Suggestion sug, PixelPoint top, int h, bool exact)
    {
        if (Settings.InlineSuggestions && exact) { _popup!.Hide(); _ghost!.ShowAt(sug, sug.Typed, top, h); }
        else { _ghost!.Hide(); _popup!.ShowSuggestion(sug, top, h, exact && Settings.SuggestionAbove); }
    }

    // ------------------------------------------------------------------ updates
    public event Action? UpdateStateChanged;

    /// <summary>Checks GitHub for a newer Qalam. Quiet: errors only go to the log.</summary>
    public async Task<Updater.Release?> CheckForUpdatesAsync()
    {
        try
        {
            var r = await Updater.CheckAsync();
            Settings.LastUpdateCheck = DateTime.UtcNow;
            Settings.Save();
            AvailableUpdate = r != null && Updater.IsNewer(r.Version) ? r : null;
            Log.Info($"update check: current {Updater.Current}, latest {r?.Version}");
            Dispatcher.UIThread.Post(() =>
            {
                if (_updateItem != null && _menu != null)
                {
                    bool shown = _menu.Items.Contains(_updateItem);
                    if (AvailableUpdate != null)
                    {
                        _updateItem.Header = $"Доступна версия {AvailableUpdate.Version} — обновить…";
                        if (!shown) _menu.Items.Insert(0, _updateItem);
                    }
                    else if (shown) _menu.Items.Remove(_updateItem);
                }
                UpdateStateChanged?.Invoke();
            });
            return r;
        }
        catch (Exception e) { Log.Error("update check", e); throw; }
    }

    private void StartUpdateTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        async void Tick()
        {
            if (!Settings.AutoCheckUpdates || DateTime.UtcNow - Settings.LastUpdateCheck < TimeSpan.FromHours(20)) return;
            try { await CheckForUpdatesAsync(); } catch { }
        }
        t.Tick += (_, _) => Tick();
        t.Start();
        DispatcherTimer.RunOnce(Tick, TimeSpan.FromSeconds(15));
    }

    /// <summary>Downloads and installs <paramref name="r"/>, then quits so the installer can replace Qalam.</summary>
    public async Task InstallUpdateAsync(Updater.Release r, IProgress<double> progress, CancellationToken ct)
    {
        var file = await Updater.DownloadAsync(r, progress, ct);
        Log.Info($"update {r.Version} downloaded: {file}");
        Updater.StartInstall(file);
        await Task.Delay(800);
        Quit();
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
        _menu = menu;
        _updateItem = new NativeMenuItem("Обновление");
        _updateItem.Click += (_, _) => ShowSettings();
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
        _ghost?.Close();
        _popup?.Close();
        if (_tray != null) _tray.IsVisible = false;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
