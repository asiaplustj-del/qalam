using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Qalam.Core;

namespace Qalam.App;

public sealed class SettingsWindow : Window
{
    private readonly QalamApp _app;
    private QalamSettings S => _app.Settings;

    private readonly TextBlock _statusTitle = new() { FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Ui.Text };
    private readonly TextBlock _statusHint = Ui.Hint("");
    private readonly ToggleSwitch _autoSwitch;
    private readonly Button _pauseButton;
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _lastFix = new() { FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _exceptionsList = new() { Spacing = 6 };
    private readonly StackPanel _appsList = new() { Spacing = 6 };
    private readonly StackPanel _layoutsPanel = new() { Spacing = 4 };
    private readonly Border _permissionCard;
    private bool _loading = true;
    private bool _layoutsBuilt;

    public SettingsWindow(QalamApp app)
    {
        _app = app;
        Title = "Qalam";
        Width = 600; Height = 780; MinWidth = 480; MinHeight = 500;
        Background = Ui.Bg;
        Icon = new WindowIcon(QalamApp.LoadBitmap("qalam.png"));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Closing += (_, e) => { e.Cancel = true; Hide(); };   // closing = hide to tray

        _autoSwitch = Ui.Switch(S.AutoMode);
        _autoSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            S.AutoMode = _autoSwitch.IsChecked == true;
            _app.SaveSettings();
        };
        _pauseButton = Ui.PrimaryButton("Пауза", yellow: true);
        _pauseButton.Click += (_, _) => _app.TogglePause();

        _permissionCard = BuildPermissionCard();

        var root = new StackPanel { Spacing = 12, Margin = new Thickness(22, 18, 22, 24) };
        root.Children.Add(BuildHeader());
        root.Children.Add(_permissionCard);
        root.Children.Add(BuildStatusCard());
        root.Children.Add(BuildHotkeysCard());
        root.Children.Add(BuildBehaviourCard());
        root.Children.Add(BuildLayoutsCard());
        root.Children.Add(BuildExceptionsCard());
        root.Children.Add(BuildAppsCard());
        root.Children.Add(BuildTryCard());
        root.Children.Add(new TextBlock
        {
            Text = "Qalam 0.1 · студия видеопродакшена «Азия-Плюс» · всё работает на вашем компьютере, без интернета",
            FontSize = 11, Foreground = Ui.Dim, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 0)
        });

        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _app.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
        _loading = false;
    }

    // ------------------------------------------------------------------ header
    private Control BuildHeader()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 4) };
        var logo = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        logo.Children.Add(new TextBlock { Text = "Qalam", FontSize = 24, FontWeight = FontWeight.ExtraBold, Foreground = Ui.Text });
        logo.Children.Add(new TextBlock { Text = ".", FontSize = 24, FontWeight = FontWeight.ExtraBold, Foreground = Ui.Orange });
        g.Children.Add(logo);
        _chips.Margin = new Thickness(14, 4, 0, 0);
        _chips.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_chips, 1); g.Children.Add(_chips);
        var hide = Ui.GhostButton("Свернуть в трей");
        hide.Click += (_, _) => Hide();
        Grid.SetColumn(hide, 2); g.Children.Add(hide);
        return g;
    }

    private Border BuildPermissionCard()
    {
        var p = new StackPanel { Spacing = 8 };
        p.Children.Add(Ui.Title("Нужно разрешение macOS"));
        p.Children.Add(Ui.Hint("Чтобы видеть нажатия и исправлять слова, разрешите Qalam доступ в «Системные настройки → Конфиденциальность и безопасность»: «Универсальный доступ» и «Мониторинг ввода». Qalam включится сам через пару секунд после разрешения."));
        var b = Ui.PrimaryButton("Открыть настройки доступа", yellow: true);
        b.HorizontalAlignment = HorizontalAlignment.Left;
        b.Click += (_, _) => _app.Platform?.RequestPermissions();
        p.Children.Add(b);
        var card = Ui.CardBox(p);
        card.BorderBrush = Ui.Yellow;
        card.IsVisible = false;
        return card;
    }

    private Control BuildStatusCard()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        var badge = Ui.Badge("⇄", yellow: false);
        g.Children.Add(badge);
        var mid = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(_statusTitle);
        mid.Children.Add(_statusHint);
        Grid.SetColumn(mid, 1); g.Children.Add(mid);
        var autoLbl = new TextBlock { Text = "Авто", Foreground = Ui.Muted, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
        var autoBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        autoBox.Children.Add(autoLbl); autoBox.Children.Add(_autoSwitch);
        Grid.SetColumn(autoBox, 2); g.Children.Add(autoBox);
        _pauseButton.Margin = new Thickness(10, 0, 0, 0);
        _pauseButton.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_pauseButton, 3); g.Children.Add(_pauseButton);

        var p = new StackPanel { Spacing = 10 };
        p.Children.Add(g);
        p.Children.Add(_lastFix);
        return Ui.CardBox(p);
    }

    // ------------------------------------------------------------------ hotkeys
    private static readonly Hotkey[] HotkeyChoices =
        { Hotkey.Pause, Hotkey.ShiftPause, Hotkey.AltPause, Hotkey.DoubleShift, Hotkey.DoubleCtrl, Hotkey.DoubleAlt, Hotkey.RightCtrl, Hotkey.None };

    private ComboBox HotkeyBox(Hotkey current, Action<Hotkey> set)
    {
        var items = HotkeyChoices.Select(QalamSettings.Describe).ToList();
        var cb = new ComboBox { ItemsSource = items, SelectedIndex = Array.IndexOf(HotkeyChoices, current), MinWidth = 190 };
        cb.SelectionChanged += (_, _) =>
        {
            if (_loading || cb.SelectedIndex < 0) return;
            set(HotkeyChoices[cb.SelectedIndex]);
            _app.SaveSettings();
            Refresh();
        };
        return cb;
    }

    private Control BuildHotkeysCard()
    {
        var p = new StackPanel();
        p.Children.Add(Ui.Caption("Горячие клавиши"));
        p.Children.Add(Ui.Row("Исправить слово / отменить",
            "Пока печатаете — меняет раскладку текущего слова (повтор перебирает EN → RU → TJ). Сразу после автоисправления — отменяет его и запоминает слово.",
            HotkeyBox(S.ManualHotkey, h => S.ManualHotkey = h)));
        p.Children.Add(Ui.Row("Исправить выделенный текст", "Выделите фразу, набранную не в той раскладке, и нажмите.",
            HotkeyBox(S.SelectionHotkey, h => S.SelectionHotkey = h)));
        p.Children.Add(Ui.Row("Пауза / продолжить", "Быстро выключить Qalam, например для паролей или кода.",
            HotkeyBox(S.PauseHotkey, h => S.PauseHotkey = h)));
        return Ui.CardBox(p);
    }

    // ------------------------------------------------------------------ behaviour
    private Control BuildBehaviourCard()
    {
        var p = new StackPanel();
        p.Children.Add(Ui.Caption("Поведение"));

        var seg = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var names = new[] { "Осторожно", "Обычно", "Смело" };
        var buttons = new List<ToggleButton>();
        for (int i = 0; i < 3; i++)
        {
            int level = i + 1;
            var tb = new ToggleButton
            {
                Content = names[i], IsChecked = S.Sensitivity == level, CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 6), FontSize = 13
            };
            tb.Click += (_, _) =>
            {
                S.Sensitivity = level;
                foreach (var b in buttons) b.IsChecked = b == tb;
                _app.SaveSettings();
            };
            buttons.Add(tb); seg.Children.Add(tb);
        }
        p.Children.Add(Ui.Row("Чувствительность", "Как смело исправлять незнакомые слова (имена, сленг). Словарные слова исправляются всегда.", seg));

        p.Children.Add(BoolRow("Однобуквенные слова", "«ф» → «a», «d» → «в», «e» → «у»", S.FixSingleLetters, v => S.FixSingleLetters = v));
        p.Children.Add(BoolRow("Учиться на отменах", "Если вы отменили исправление, слово попадает в исключения.", S.LearnFromUndo, v => S.LearnFromUndo = v));
        p.Children.Add(BoolRow("Запускать вместе с системой", null, S.StartWithSystem, v => S.StartWithSystem = v));
        return Ui.CardBox(p);
    }

    private Grid BoolRow(string label, string? hint, bool value, Action<bool> set)
    {
        var sw = Ui.Switch(value);
        sw.IsCheckedChanged += (_, _) => { if (_loading) return; set(sw.IsChecked == true); _app.SaveSettings(); };
        return Ui.Row(label, hint, sw);
    }

    // ------------------------------------------------------------------ layouts
    private Control BuildLayoutsCard()
    {
        var p = new StackPanel();
        p.Children.Add(Ui.Caption("Языки"));
        p.Children.Add(Ui.Hint("Qalam переключает только между раскладками, установленными в системе. Таджикская раскладка добавляется в настройках языка Windows/macOS (язык «Тоҷикӣ»)."));
        _layoutsPanel.Margin = new Thickness(0, 10, 0, 0);
        p.Children.Add(_layoutsPanel);
        return Ui.CardBox(p);
    }

    private void RefreshLayouts()
    {
        _layoutsPanel.Children.Clear();
        var installed = _app.Platform?.GetLayouts() ?? Array.Empty<KeyboardLayout>();
        foreach (var (lang, name) in new[] { ("en", "English"), ("ru", "Русский"), ("tg", "Тоҷикӣ") })
        {
            bool has = installed.Any(l => l.Language == lang);
            var cb = new CheckBox
            {
                IsChecked = S.Languages.Contains(lang), IsEnabled = has,
                Content = new TextBlock
                {
                    Text = has ? name : $"{name} — раскладка не установлена",
                    Foreground = has ? Ui.Text : Ui.Dim, FontSize = 14
                }
            };
            cb.IsCheckedChanged += (_, _) =>
            {
                if (_loading) return;
                if (cb.IsChecked == true) { if (!S.Languages.Contains(lang)) S.Languages.Add(lang); }
                else S.Languages.Remove(lang);
                _app.SaveSettings();
            };
            _layoutsPanel.Children.Add(cb);
        }
    }

    // ------------------------------------------------------------------ exceptions
    private Control BuildExceptionsCard()
    {
        var p = new StackPanel();
        p.Children.Add(Ui.Caption("Слова-исключения"));
        p.Children.Add(Ui.Hint("Эти слова Qalam никогда не исправляет. Сюда же попадают слова, исправление которых вы отменили."));
        p.Children.Add(AddRow("Слово, например qalam или ghbdtn", w => _app.Controller?.Engine.Detector.User.AddException(w)));
        _exceptionsList.Margin = new Thickness(0, 10, 0, 0);
        p.Children.Add(_exceptionsList);
        return Ui.CardBox(p);
    }

    private Control BuildAppsCard()
    {
        var p = new StackPanel();
        p.Children.Add(Ui.Caption("Программы-исключения"));
        p.Children.Add(Ui.Hint("В этих программах Qalam молчит: менеджеры паролей, игры, терминалы. Достаточно части названия процесса."));
        p.Children.Add(AddRow("Название программы, например Code или Photoshop", w =>
        {
            if (!S.ExcludedApps.Contains(w, StringComparer.OrdinalIgnoreCase)) S.ExcludedApps.Add(w);
            _app.SaveSettings();
        }));
        _appsList.Margin = new Thickness(0, 10, 0, 0);
        p.Children.Add(_appsList);
        return Ui.CardBox(p);
    }

    private Grid AddRow(string placeholder, Action<string> add)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 0) };
        var tb = new TextBox { Watermark = placeholder, Background = Ui.Field, BorderBrush = Ui.Border, CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 8) };
        var btn = Ui.GhostButton("+ Добавить");
        btn.Margin = new Thickness(8, 0, 0, 0);
        void Do()
        {
            var w = tb.Text?.Trim();
            if (string.IsNullOrEmpty(w)) return;
            add(w); tb.Text = ""; Refresh();
        }
        btn.Click += (_, _) => Do();
        tb.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Do(); };
        g.Children.Add(tb);
        Grid.SetColumn(btn, 1); g.Children.Add(btn);
        return g;
    }

    private static Control Pill(string text, Action remove)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        g.Children.Add(new TextBlock { Text = text, FontFamily = Ui.Mono, FontSize = 13, Foreground = Ui.Text, VerticalAlignment = VerticalAlignment.Center });
        var x = new Button { Content = "×", Background = Brushes.Transparent, Foreground = Ui.Muted, Padding = new Thickness(8, 0), FontSize = 16, BorderThickness = new Thickness(0) };
        x.Click += (_, _) => remove();
        Grid.SetColumn(x, 1); g.Children.Add(x);
        return new Border { Background = Ui.Field, BorderBrush = Ui.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4), Child = g };
    }

    // ------------------------------------------------------------------ try it
    private Control BuildTryCard()
    {
        var p = new StackPanel();
        p.Children.Add(Ui.Caption("Проверить"));
        p.Children.Add(Ui.Hint("Напечатайте здесь что-нибудь не в той раскладке, например ghbdtn или руддщ, и нажмите пробел."));
        p.Children.Add(new TextBox
        {
            Watermark = "ghbdtn vbh…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, Margin = new Thickness(0, 10, 0, 0),
            Background = Ui.Field, BorderBrush = Ui.Border, CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 8), FontSize = 14
        });
        return Ui.CardBox(p);
    }

    // ------------------------------------------------------------------ state
    public void OnCorrected(Correction c)
    {
        string kind = c.Kind switch { CorrectionKind.Undo => "отменено", CorrectionKind.Manual => "вручную", CorrectionKind.Selection => "выделение", _ => "авто" };
        _lastFix.Text = $"последнее: {Short(c.From)} → {Short(c.To)}  ·  {kind}";
        if (c.Kind == CorrectionKind.Undo) Refresh();
    }

    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;

    private void Refresh()
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            var ctl = _app.Controller;
            bool perms = _app.Platform?.HasPermissions() ?? true;
            _permissionCard.IsVisible = !perms;
            _autoSwitch.IsChecked = S.AutoMode;

            if (ctl == null) { _statusTitle.Text = "Загружаю словари…"; _statusHint.Text = "Секунду"; }
            else if (!perms) { _statusTitle.Text = "Ждёт разрешения"; _statusHint.Text = "Без доступа к клавиатуре Qalam не видит нажатия"; }
            else if (S.Paused) { _statusTitle.Text = "На паузе"; _statusHint.Text = $"Продолжить: {QalamSettings.Describe(S.PauseHotkey)}"; }
            else
            {
                _statusTitle.Text = S.AutoMode ? "Работает" : "Только вручную";
                _statusHint.Text = S.AutoMode
                    ? $"Исправляет при пробеле · вручную: {QalamSettings.Describe(S.ManualHotkey)}"
                    : $"Исправляйте клавишей {QalamSettings.Describe(S.ManualHotkey)}";
            }
            ((TextBlock)_pauseButton.Content!).Text = S.Paused ? "Продолжить" : "Пауза";
            _pauseButton.Background = S.Paused ? Ui.Orange : Ui.Yellow;
            _pauseButton.Foreground = S.Paused ? Brushes.White : Ui.Bg;

            _chips.Children.Clear();
            if (ctl != null)
            {
                foreach (var l in ctl.Engine.Layouts)
                {
                    bool cur = l.Id == ctl.Engine.CurrentLayout.Id;
                    _chips.Children.Add(Ui.Chip(l.Language == "tg" ? "TJ" : l.Language.ToUpperInvariant(), cur ? Ui.Yellow : null));
                }
                _chips.Children.Add(Ui.Chip($"исправлено {_app.CorrectionsCount}"));
            }

            _exceptionsList.Children.Clear();
            var user = ctl?.Engine.Detector.User;
            if (user != null)
                foreach (var w in user.Exceptions.Order())
                    _exceptionsList.Children.Add(Pill(w, () => { user.RemoveException(w); Refresh(); }));

            _appsList.Children.Clear();
            foreach (var a in S.ExcludedApps.ToList())
                _appsList.Children.Add(Pill(a, () => { S.ExcludedApps.Remove(a); _app.SaveSettings(); Refresh(); }));

            if (!_layoutsBuilt && _app.Platform != null) { RefreshLayouts(); _layoutsBuilt = true; }
        }
        finally { _loading = wasLoading; }
    }
}
