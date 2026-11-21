using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
        root.Children.Add(BuildSuggestCard());
        root.Children.Add(BuildNeuralCard());
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

        var logBtn = Ui.GhostButton("Папка с журналом");
        logBtn.HorizontalAlignment = HorizontalAlignment.Center;
        logBtn.Click += (_, _) => { Directory.CreateDirectory(QalamSettings.DataDir); NeuralService.OpenUrl(QalamSettings.DataDir); };
        root.Children.Add(logBtn);

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
        { Hotkey.Pause, Hotkey.ShiftPause, Hotkey.AltPause, Hotkey.DoubleShift, Hotkey.DoubleCtrl, Hotkey.DoubleAlt, Hotkey.RightCtrl, Hotkey.Custom, Hotkey.None };

    private readonly Dictionary<HotkeyAction, (ComboBox Box, Button Rec)> _hotkeyRows = new();
    private HotkeyAction? _recordingFor;
    private readonly TextBlock _hotkeyNote = new() { FontSize = 12, Foreground = Ui.Yellow, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 6, 0, 0) };

    private Control HotkeyEditor(HotkeyAction action)
    {
        var cb = new ComboBox { MinWidth = 200 };
        var rec = Ui.GhostButton("Записать");
        rec.Margin = new Thickness(8, 0, 0, 0);
        cb.SelectionChanged += (_, _) =>
        {
            if (_loading || cb.SelectedIndex < 0) return;
            var h = HotkeyChoices[cb.SelectedIndex];
            if (h == Hotkey.Custom)
            {
                if (S.GetCombo(action) is { } c) S.SetCustom(action, c);
                else { StartRecording(action); return; }
            }
            else S.SetPreset(action, h);
            _app.SaveSettings();
            Refresh();
        };
        rec.Click += (_, _) => { if (_recordingFor == action) StopRecording(null); else StartRecording(action); };
        _hotkeyRows[action] = (cb, rec);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(cb); row.Children.Add(rec);
        return row;
    }

    private void StartRecording(HotkeyAction action)
    {
        var ctl = _app.Controller;
        if (ctl == null) return;
        if (_recordingFor != null) StopRecording(null);
        _recordingFor = action;
        ((TextBlock)_hotkeyRows[action].Rec.Content!).Text = "Нажмите комбинацию… (Esc — отмена)";
        _hotkeyRows[action].Rec.BorderBrush = Ui.Orange;
        _hotkeyNote.IsVisible = false;
        ctl.Recorded -= OnRecorded;
        ctl.Recorded += OnRecorded;
        ctl.StartRecording();
    }

    private void OnRecorded(KeyCombo? combo) => Dispatcher.UIThread.Post(() => StopRecording(combo));

    private void StopRecording(KeyCombo? combo)
    {
        var ctl = _app.Controller;
        ctl?.CancelRecording();
        if (_recordingFor is not { } action) return;
        _recordingFor = null;
        ((TextBlock)_hotkeyRows[action].Rec.Content!).Text = "Записать";
        _hotkeyRows[action].Rec.BorderBrush = Ui.Border;
        if (combo != null)
        {
            if (!combo.IsSafe)
            {
                _hotkeyNote.Text = $"«{combo}» мешала бы обычному набору. Добавьте Ctrl, Alt или {(OperatingSystem.IsMacOS() ? "⌘" : "Win")} либо выберите F1–F24, Pause, Insert, ScrollLock и т. п.";
                _hotkeyNote.IsVisible = true;
            }
            else
            {
                S.SetCustom(action, combo);
                _app.SaveSettings();
                _hotkeyNote.IsVisible = false;
            }
        }
        Refresh();
    }

    private string? _hotkeySig;

    private void RefreshHotkeys()
    {
        var sig = string.Join("|", Enum.GetValues<HotkeyAction>().Select(a => $"{S.GetHotkey(a)}:{S.GetCombo(a)}"));
        if (sig == _hotkeySig) return;
        _hotkeySig = sig;
        foreach (var (action, (cb, _)) in _hotkeyRows)
        {
            var combo = S.GetCombo(action);
            cb.ItemsSource = HotkeyChoices.Select(h => h == Hotkey.Custom
                ? (combo != null ? $"Своя: {combo}" : "Своя комбинация…")
                : QalamSettings.Describe(h)).ToList();
            cb.SelectedIndex = Array.IndexOf(HotkeyChoices, S.GetHotkey(action));
        }
    }

    private Control BuildHotkeysCard()
    {
        var p = new StackPanel();
        p.Children.Add(Ui.Caption("Горячие клавиши"));
        p.Children.Add(Ui.Row("Исправить слово / отменить",
            "Пока печатаете — меняет раскладку текущего слова (повтор перебирает EN → RU → TJ). Сразу после автоисправления — отменяет его и запоминает слово.",
            HotkeyEditor(HotkeyAction.Manual)));
        p.Children.Add(Ui.Row("Исправить выделенный текст", "Выделите фразу, набранную не в той раскладке, и нажмите.",
            HotkeyEditor(HotkeyAction.Selection)));
        p.Children.Add(Ui.Row("Пауза / продолжить", "Быстро выключить Qalam, например для паролей или кода.",
            HotkeyEditor(HotkeyAction.Pause)));
        p.Children.Add(Ui.Hint("Выберите вариант из списка или нажмите «Записать» и затем любую комбинацию: Ctrl + Shift + Q, F9, CapsLock и т. д."));
        p.Children.Add(_hotkeyNote);
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

    // ------------------------------------------------------------------ suggestions
    private Control BuildSuggestCard()
    {
        var p = new StackPanel();
        p.Children.Add(Ui.Caption("Подсказки и таджикские буквы"));
        p.Children.Add(BoolRow("Дописывать слова", "Под курсором появляется подсказка: «точик» → «тоҷикистон». Tab — принять, просто печатайте дальше — игнорировать. Работает по словарю, без интернета и нейросети.",
            S.WordCompletion, v => S.WordCompletion = v));
        p.Children.Add(BoolRow("Подсказка над строкой", "Иначе — под строкой. На Mac снизу показывает свои подсказки сама система, поэтому по умолчанию — сверху.",
            S.SuggestionAbove, v => S.SuggestionAbove = v));
        p.Children.Add(BoolRow("Восстанавливать таджикские буквы", "«хамчун» → «ҳамчун», «чахон» → «ҷаҳон», «точикистон» → «тоҷикистон». Слова, которые и так правильные («ки», «кор»), не трогаются. Отменить — той же клавишей, что и исправление.",
            S.RestoreTajik, v => S.RestoreTajik = v));
        return Ui.CardBox(p);
    }

    // ------------------------------------------------------------------ neural module
    private ToggleSwitch? _neuralSwitch;
    private readonly ToggleButton _btnBuiltin = new() { Content = "Встроенный движок", CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 6), FontSize = 13 };
    private readonly ToggleButton _btnOllama = new() { Content = "Ollama", CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 6), FontSize = 13 };
    private readonly StackPanel _builtinPanel = new() { Spacing = 6 };
    private readonly StackPanel _ollamaPanel = new() { Spacing = 6 };

    // built-in engine
    private readonly TextBlock _engineStatus = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _modelLabel = new() { FontSize = 13, Foreground = Ui.Text, TextWrapping = TextWrapping.Wrap, FontFamily = Ui.Mono };
    private readonly ComboBox _foundModels = new() { MinWidth = 300, PlaceholderText = "Модели на этом компьютере" };
    private readonly Button _copyButton = Ui.GhostButton("Скопировать к себе");
    private readonly TextBlock _copyStatus = new() { FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _unloadBox = new() { MinWidth = 150 };
    private List<ModelFiles.Found> _found = new();
    private bool _scanning;
    private CancellationTokenSource? _copyCts;
    private static readonly int[] UnloadChoices = { 5, 10, 30, 60, 0 };

    // Ollama
    private readonly TextBlock _neuralStatus = new() { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _neuralModels = new() { MinWidth = 260 };
    private readonly Button _pullButton = Ui.GhostButton("Скачать модель");
    private readonly Button _installOllama = Ui.PrimaryButton("Установить Ollama", yellow: true);
    private CancellationTokenSource? _pullCts;
    private string _neuralSig = "";

    private Control BuildNeuralCard()
    {
        var p = new StackPanel { Spacing = 4 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        head.Children.Add(Ui.Badge("✦", yellow: true));
        var mid = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(Ui.Title("Нейро-подсказки · модуль"));
        mid.Children.Add(Ui.Hint("Продолжение фразы после пробела от локальной языковой модели. Tab — вставить. Выключено по умолчанию."));
        Grid.SetColumn(mid, 1); head.Children.Add(mid);
        _neuralSwitch = Ui.Switch(S.NeuralEnabled);
        _neuralSwitch.VerticalAlignment = VerticalAlignment.Center;
        _neuralSwitch.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            S.NeuralEnabled = _neuralSwitch.IsChecked == true;
            _app.SaveSettings();
            _app.Neural?.ApplySettings(userAction: true);
            Refresh();
        };
        Grid.SetColumn(_neuralSwitch, 2); head.Children.Add(_neuralSwitch);
        p.Children.Add(head);

        var seg = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 12, 0, 6) };
        _btnBuiltin.Click += (_, _) => SetBackend("builtin");
        _btnOllama.Click += (_, _) => SetBackend("ollama");
        seg.Children.Add(_btnBuiltin); seg.Children.Add(_btnOllama);
        p.Children.Add(seg);

        BuildBuiltinPanel();
        BuildOllamaPanel();
        p.Children.Add(_builtinPanel);
        p.Children.Add(_ollamaPanel);
        _ = ScanModels();
        return Ui.CardBox(p);
    }

    private void SetBackend(string backend)
    {
        S.NeuralBackend = backend;
        _app.SaveSettings();
        _app.Neural?.ApplySettings(userAction: true);
        if (backend == "ollama" && _app.Neural != null) _ = _app.Neural.RefreshAsync().ContinueWith(_ => Dispatcher.UIThread.Post(Refresh));
        Refresh();
    }

    private void BuildBuiltinPanel()
    {
        _engineStatus.Margin = new Thickness(0, 2, 0, 2);
        _builtinPanel.Children.Add(_engineStatus);
        _builtinPanel.Children.Add(_modelLabel);

        _foundModels.SelectionChanged += (_, _) =>
        {
            if (_loading || _foundModels.SelectedIndex < 0 || _foundModels.SelectedIndex >= _found.Count) return;
            UseModel(_found[_foundModels.SelectedIndex].Path);
        };
        var pick = Ui.GhostButton("Выбрать файл…");
        pick.Click += async (_, _) => await PickModelFile();
        var scan = Ui.GhostButton("Найти");
        scan.Click += async (_, _) => await ScanModels();
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _foundModels.Margin = new Thickness(0, 0, 8, 6);
        pick.Margin = new Thickness(0, 0, 8, 6);
        scan.Margin = new Thickness(0, 0, 8, 6);
        row.Children.Add(_foundModels); row.Children.Add(pick); row.Children.Add(scan);
        _builtinPanel.Children.Add(row);

        _copyButton.HorizontalAlignment = HorizontalAlignment.Left;
        _copyButton.Click += async (_, _) => await CopyModel();
        var copyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        copyRow.Children.Add(_copyButton);
        _copyStatus.VerticalAlignment = VerticalAlignment.Center;
        copyRow.Children.Add(_copyStatus);
        _builtinPanel.Children.Add(copyRow);

        _unloadBox.ItemsSource = UnloadChoices.Select(m => m == 0 ? "никогда" : m < 60 ? $"{m} мин" : "1 час").ToList();
        _unloadBox.SelectedIndex = Math.Max(0, Array.IndexOf(UnloadChoices, S.NeuralUnloadMinutes));
        _unloadBox.SelectionChanged += (_, _) =>
        {
            if (_loading || _unloadBox.SelectedIndex < 0) return;
            S.NeuralUnloadMinutes = UnloadChoices[_unloadBox.SelectedIndex];
            _app.SaveSettings();
        };
        _builtinPanel.Children.Add(Ui.Row("Выгружать модель из памяти", "Если долго не печатаете — память освобождается, при следующей подсказке модель загрузится снова.", _unloadBox));
        _builtinPanel.Children.Add(BoolRow("Не использовать на батарее", "На ноутбуке без зарядки подсказки от модели отключаются, чтобы беречь батарею.",
            S.NeuralSkipOnBattery, v => S.NeuralSkipOnBattery = v));

        var test = Ui.GhostButton("Проверить подсказку");
        test.HorizontalAlignment = HorizontalAlignment.Left;
        test.Click += async (_, _) => await TestModel();
        _builtinPanel.Children.Add(test);
        _testResult.Margin = new Thickness(0, 2, 0, 6);
        _builtinPanel.Children.Add(_testResult);

        var where = Ui.GhostButton("Где взять модель");
        where.HorizontalAlignment = HorizontalAlignment.Left;
        where.Click += (_, _) => NeuralService.OpenUrl(NeuralService.ModelsPage);
        _builtinPanel.Children.Add(Ui.Hint("Подходит любой файл модели в формате GGUF: например, уже скачанный для Cotypist или LM Studio — «Найти» покажет их. Если модели нет — скачайте файл Gemma 4 E2B (вариант Q4_K_M) и выберите его. Модель работает на вашем компьютере, текст никуда не отправляется. По-таджикски маленькие модели пишут заметно слабее, чем по-русски и по-английски."));
        _builtinPanel.Children.Add(where);
    }

    private readonly TextBlock _testResult = new() { FontSize = 13, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, FontFamily = Ui.Mono };

    /// <summary>Runs one real request so the user sees what the model answers and how fast.</summary>
    private async Task TestModel()
    {
        var n = _app.Neural;
        var path = S.NeuralModelPath;
        if (n == null || !ModelFiles.IsUsable(path)) { _testResult.Text = "Сначала выберите файл модели."; return; }
        _testResult.Text = "Загружаю модель…";
        if (!await n.Engine.WaitReadyAsync(path!, TimeSpan.FromMinutes(3)))
        {
            _testResult.Text = "Модель не загрузилась: " + (n.Engine.Error ?? "нет ответа");
            return;
        }
        var sample = "Завтра утром мы поедем на съёмки и ";
        _testResult.Text = $"Спрашиваю: «{sample}…»";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var raw = await n.Engine.CompleteAsync(path!, sample, 16, default);
        var clean = PhraseText.Clean(raw, sample);
        _testResult.Text = clean != null
            ? $"{sw.Elapsed.TotalSeconds:0.0} с · «{sample}{clean}»"
            : $"{sw.Elapsed.TotalSeconds:0.0} с · модель ответила неподходяще: «{raw}»";
    }

    private async Task ScanModels()
    {
        if (_scanning) return;
        _scanning = true;
        _copyStatus.Text = "Ищу модели…";
        try
        {
            _found = await Task.Run(() => ModelFiles.Scan());
            bool was = _loading; _loading = true;
            _foundModels.ItemsSource = _found.Select(f => f.ToString()).ToList();
            _foundModels.SelectedIndex = _found.FindIndex(f => string.Equals(f.Path, S.NeuralModelPath, StringComparison.OrdinalIgnoreCase));
            _foundModels.PlaceholderText = _found.Count == 0 ? "Модели не найдены" : $"Найдено моделей: {_found.Count}";
            _loading = was;
            _copyStatus.Text = "";
        }
        catch (Exception e) { _copyStatus.Text = "Не удалось поискать: " + e.Message; }
        finally { _scanning = false; Refresh(); }
    }

    private async Task PickModelFile()
    {
        var options = new FilePickerOpenOptions { Title = "Файл модели (.gguf)", AllowMultiple = false };
        if (!OperatingSystem.IsMacOS())   // macOS doesn't know the .gguf type — allow any file and check the extension
            options.FileTypeFilter = new[] { new FilePickerFileType("Модель GGUF") { Patterns = new[] { "*.gguf" } } };
        var files = await StorageProvider.OpenFilePickerAsync(options);
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (path == null || !path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            _copyStatus.Text = "Это не файл модели GGUF.";
            return;
        }
        UseModel(path);
        if (!_found.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            try { _found.Insert(0, new ModelFiles.Found(path, new FileInfo(path).Length, ModelFiles.SourceOf(path))); } catch { }
            bool was = _loading; _loading = true;
            _foundModels.ItemsSource = _found.Select(f => f.ToString()).ToList();
            _foundModels.SelectedIndex = 0;
            _loading = was;
        }
    }

    private void UseModel(string path)
    {
        S.NeuralModelPath = path;
        _app.SaveSettings();
        _app.Neural?.ApplySettings(userAction: true);
        _copyStatus.Text = "";
        Refresh();
    }

    private async Task CopyModel()
    {
        if (_copyCts != null) { _copyCts.Cancel(); return; }
        var src = S.NeuralModelPath;
        if (!ModelFiles.IsUsable(src)) return;
        _copyCts = new CancellationTokenSource();
        ((TextBlock)_copyButton.Content!).Text = "Отменить";
        var progress = new Progress<double>(f => _copyStatus.Text = $"Копирую… {f:P0}");
        try
        {
            var dest = await ModelFiles.CopyToQalamAsync(src!, progress, _copyCts.Token);
            UseModel(dest);
            _copyStatus.Text = "Скопировано в папку Qalam — модель останется, даже если удалить программу, из которой она взята.";
            await ScanModels();
        }
        catch (OperationCanceledException) { _copyStatus.Text = "Копирование отменено."; }
        catch (Exception e) { _copyStatus.Text = "Не удалось скопировать: " + e.Message; }
        finally
        {
            _copyCts = null;
            ((TextBlock)_copyButton.Content!).Text = "Скопировать к себе";
        }
    }

    private void BuildOllamaPanel()
    {
        _neuralStatus.Margin = new Thickness(0, 2, 0, 4);
        _ollamaPanel.Children.Add(_neuralStatus);
        _neuralModels.SelectionChanged += (_, _) =>
        {
            if (_loading || _neuralModels.SelectedIndex < 0) return;
            S.NeuralModel = ModelNames()[_neuralModels.SelectedIndex];
            _app.SaveSettings();
            Refresh();
        };
        _pullButton.Margin = new Thickness(8, 0, 0, 0);
        _pullButton.Click += async (_, _) => await PullModel();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(_neuralModels); row.Children.Add(_pullButton);
        _ollamaPanel.Children.Add(row);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        _installOllama.Click += (_, _) => NeuralService.OpenUrl("https://ollama.com/download");
        var recheck = Ui.GhostButton("Проверить снова");
        recheck.Click += async (_, _) => { if (_app.Neural != null) { await _app.Neural.RefreshAsync(); _neuralSig = ""; Refresh(); } };
        buttons.Children.Add(_installOllama); buttons.Children.Add(recheck);
        _ollamaPanel.Children.Add(buttons);
        _ollamaPanel.Children.Add(Ui.Hint("Для тех, у кого уже стоит Ollama: Qalam подключится к ней и сможет сам скачать модель."));
    }

    private List<string> ModelNames()
    {
        var names = NeuralService.Recommended.Select(m => m.Name).ToList();
        foreach (var m in _app.Neural?.InstalledModels ?? new()) if (!names.Contains(m) && !names.Contains(m.Replace(":latest", ""))) names.Add(m);
        if (!names.Contains(S.NeuralModel)) names.Add(S.NeuralModel);
        return names;
    }

    private async Task PullModel()
    {
        var n = _app.Neural;
        if (n == null) return;
        if (_pullCts != null) { _pullCts.Cancel(); return; }
        _pullCts = new CancellationTokenSource();
        ((TextBlock)_pullButton.Content!).Text = "Отменить";
        var progress = new Progress<(double Fraction, string Status)>(p =>
            _neuralStatus.Text = p.Fraction > 0 ? $"Скачиваю {S.NeuralModel}: {p.Fraction:P0}" : $"Скачиваю {S.NeuralModel}: {p.Status}");
        bool ok = await n.PullAsync(S.NeuralModel, progress, _pullCts.Token);
        _pullCts = null;
        ((TextBlock)_pullButton.Content!).Text = "Скачать модель";
        _neuralSig = "";
        Refresh();
        if (!ok) _neuralStatus.Text = $"Не удалось скачать: {n.LastError}";
    }

    private void RefreshNeural()
    {
        var n = _app.Neural;
        _neuralSwitch!.IsChecked = S.NeuralEnabled;
        bool builtin = S.NeuralBackend != "ollama";
        _btnBuiltin.IsChecked = builtin;
        _btnOllama.IsChecked = !builtin;
        _builtinPanel.IsVisible = builtin;
        _ollamaPanel.IsVisible = !builtin;
        if (builtin) RefreshBuiltin(n); else RefreshOllama(n);
    }

    private void RefreshBuiltin(NeuralService? n)
    {
        var path = S.NeuralModelPath;
        bool usable = ModelFiles.IsUsable(path);
        if (usable)
        {
            long size = 0; try { size = new FileInfo(path!).Length; } catch { }
            _modelLabel.Text = $"Модель: {Path.GetFileName(path)} · {ModelFiles.Size(size)} · {ModelFiles.SourceOf(path!)}";
        }
        else _modelLabel.Text = string.IsNullOrEmpty(path) ? "Модель не выбрана" : $"Файл модели не найден: {path}";
        _copyButton.IsVisible = usable && ModelFiles.SourceOf(path!) != "Qalam";
        if (_copyCts == null && _copyButton.IsVisible && _copyStatus.Text is null or "")
            _copyStatus.Text = "Совет: если модель взята из другой программы, скопируйте её к себе — иначе она пропадёт при удалении той программы.";

        var e = n?.Engine;
        string status; IBrush color = Ui.Muted;
        if (!LlamaEngine.Available) status = "Встроенный движок не найден в этой сборке Qalam.";
        else if (!S.NeuralEnabled) status = "Модуль выключен — модель не загружена и память не занимает.";
        else if (!usable) status = "Выберите файл модели, чтобы включить подсказки.";
        else if (S.NeuralSkipOnBattery && _app.Platform?.IsOnBattery() == true) status = "Ноутбук на батарее — подсказки от модели на паузе.";
        else
        {
            switch (e?.State)
            {
                case LlamaEngine.EngineState.Loading: status = "Загружаю модель в память…"; break;
                case LlamaEngine.EngineState.Ready:
                    var mem = e!.MemoryBytes;
                    status = mem > 0 ? $"Работает · модель в памяти (~{ModelFiles.Size(mem)})" : "Работает · модель в памяти";
                    color = Ui.Yellow; break;
                case LlamaEngine.EngineState.Failed: status = "Ошибка: " + e!.Error; color = Ui.Orange; break;
                default: status = "Готово · модель загрузится при первой подсказке"; color = Ui.Yellow; break;
            }
        }
        _engineStatus.Text = status;
        _engineStatus.Foreground = color;
    }

    private void RefreshOllama(NeuralService? n)
    {
        if (_pullCts != null) return;   // progress text in use
        var names = ModelNames();
        var sig = $"{n?.OllamaFound}|{string.Join(",", n?.InstalledModels ?? new())}|{S.NeuralModel}";
        if (sig == _neuralSig) return;
        _neuralSig = sig;
        _neuralModels.ItemsSource = names.Select(m =>
        {
            var rec = NeuralService.Recommended.FirstOrDefault(r => r.Name == m);
            var mark = n?.ModelInstalled(m) == true ? "✓ " : "";
            return rec != null ? $"{mark}{m} — {rec.Description}" : $"{mark}{m}";
        }).ToList();
        _neuralModels.SelectedIndex = names.IndexOf(S.NeuralModel);
        bool found = n?.OllamaFound == true;
        bool installed = n?.ModelInstalled(S.NeuralModel) == true;
        _installOllama.IsVisible = !found;
        _pullButton.IsEnabled = found;
        _pullButton.IsVisible = !installed;
        _neuralStatus.Text = n?.OllamaFound switch
        {
            null => "Проверяю Ollama…",
            false => "Ollama не найдена на этом компьютере.",
            true when installed => S.NeuralEnabled ? $"Работает · модель {S.NeuralModel}" : $"Готово к включению · модель {S.NeuralModel} скачана",
            _ => $"Ollama найдена. Модель {S.NeuralModel} ещё не скачана."
        };
        _neuralStatus.Foreground = found && installed ? Ui.Yellow : Ui.Muted;
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
            else if (S.Paused) { _statusTitle.Text = "На паузе"; _statusHint.Text = $"Продолжить: {S.DescribeAction(HotkeyAction.Pause)}"; }
            else
            {
                _statusTitle.Text = S.AutoMode ? "Работает" : "Только вручную";
                _statusHint.Text = S.AutoMode
                    ? $"Исправляет при пробеле · вручную: {S.DescribeAction(HotkeyAction.Manual)}"
                    : $"Исправляйте клавишей {S.DescribeAction(HotkeyAction.Manual)}";
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

            RefreshHotkeys();
            RefreshNeural();
            if (!_layoutsBuilt && _app.Platform != null) { RefreshLayouts(); _layoutsBuilt = true; }
        }
        finally { _loading = wasLoading; }
    }
}
