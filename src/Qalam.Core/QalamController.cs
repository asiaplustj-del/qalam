namespace Qalam.Core;

/// <summary>
/// Glue between the OS keyboard hook and the engine: hotkeys, pause, excluded apps,
/// layout syncing and applying corrections. Platform-independent and unit-testable.
/// </summary>
public sealed class QalamController : IDisposable
{
    private readonly IKeyboardPlatform _platform;
    public SwitcherEngine Engine { get; }
    public QalamSettings Settings { get; }

    public event Action<Correction>? Corrected;

    // ---------- suggestions (word completion + neural phrases) ----------
    public sealed record Suggestion(string Text, int Erase, string Insert, bool IsPhrase, IReadOnlyList<string> Alternatives, long Version);
    /// <summary>Show/hide the suggestion popup (null = hide). Raised on the hook thread.</summary>
    public event Action<Suggestion?>? SuggestionChanged;
    /// <summary>The user finished a word: a neural module may propose a continuation of <c>text</c>.</summary>
    public event Action<long, string>? PhraseOpportunity;
    public Suggestion? CurrentSuggestion { get; private set; }
    public Completer Completer { get; }
    private long _version;
    /// <summary>Raised after <see cref="StartRecording"/>: the combo pressed, or null if cancelled (Esc).</summary>
    public event Action<KeyCombo?>? Recorded;
    public bool Recording { get; private set; }
    private string? _swallowUp;   // key whose release must also be swallowed (hotkey keys like CapsLock)

    public void StartRecording() { lock (_sync) Recording = true; }
    public void CancelRecording() { lock (_sync) Recording = false; }
    public event Action? StateChanged;

    // hotkey tracking
    private SpecialKey _tapKey = SpecialKey.None;
    private bool _tapRight;
    private DateTime _tapDownAt, _lastTapAt;
    private SpecialKey _lastTapKey = SpecialKey.None;
    private bool _otherKeySinceDown;
    private readonly HashSet<SpecialKey> _held = new();
    private const int DoubleTapMs = 400;

    private string? _excludedCacheApp;
    private bool _excludedCache;
    private DateTime _excludedCheckedAt;

    public QalamController(IKeyboardPlatform platform, SwitcherEngine engine, QalamSettings settings)
    {
        _platform = platform; Engine = engine; Settings = settings;
        Completer = new Completer(engine.Detector);
        ApplySettings();
        platform.KeyHandler = OnKey;
        platform.MouseClicked += () => { lock (_sync) { Engine.Reset(); ClearSuggestion(); } };
    }

    public void ApplySettings()
    {
        Engine.AutoMode = Settings.AutoMode;
        Engine.Paused = Settings.Paused;
        Engine.LearnFromUndo = Settings.LearnFromUndo;
        Engine.RestoreTajik = Settings.RestoreTajik;
        Completer.UseTajikLetters = Settings.RestoreTajik;
        var o = Engine.Detector.Options;
        o.NgramMargin = Settings.Sensitivity switch { 1 => 2.8, 3 => 1.7, _ => 2.2 };
        o.FixSingleLetters = Settings.FixSingleLetters;
        RefreshLayouts();
    }

    /// <summary>Re-reads installed layouts (call when the user adds/removes one).</summary>
    public void RefreshLayouts()
    {
        var all = _platform.GetLayouts();
        var used = all.Where(l => Engine.Detector.Model(l.Language) != null
                               && (Settings.Languages.Count == 0 || Settings.Languages.Contains(l.Language)))
                      .GroupBy(l => l.Language).Select(g => g.First()).ToList();
        if (used.Count > 0) Engine.Layouts = used;
        var cur = _platform.GetCurrentLayout(Engine.Layouts);
        if (cur != null) Engine.CurrentLayout = cur;
    }

    public bool Paused
    {
        get => Settings.Paused;
        set { Settings.Paused = value; Engine.Paused = value; Engine.Reset(); Settings.Save(); StateChanged?.Invoke(); }
    }

    public bool AutoMode
    {
        get => Settings.AutoMode;
        set { Settings.AutoMode = value; Engine.AutoMode = value; Settings.Save(); StateChanged?.Invoke(); }
    }

    private readonly object _sync = new();

    private bool OnKey(KeyInput k)
    {
        try { lock (_sync) return HandleWithSuggestions(k); }
        catch { Engine.Reset(); return false; }
    }

    private bool HandleWithSuggestions(KeyInput k)
    {
        bool isModifier = k.Special is SpecialKey.Shift or SpecialKey.Ctrl or SpecialKey.Alt or SpecialKey.Meta;
        if (!k.IsDown || isModifier) return Handle(k);

        // Tab accepts the visible suggestion
        if (k.Special == SpecialKey.Tab && !k.Ctrl && !k.Alt && !k.Meta && !k.Shift && CurrentSuggestion is { } sug && !Recording)
        {
            _platform.Replace(sug.Erase, sug.Insert);
            Engine.AcceptCompletion(sug.Insert);
            _swallowUp = "Tab";
            _version++;
            ClearSuggestion();
            return true;
        }

        bool swallowed = Handle(k);
        _version++;
        if (Settings.Paused || Recording) { ClearSuggestion(); return swallowed; }
        switch (k.Special)
        {
            case SpecialKey.None when !(k.Ctrl || k.Alt || k.Meta):
            case SpecialKey.Backspace:
                UpdateWordSuggestion();
                break;
            case SpecialKey.Space:
                ClearSuggestion();
                if (Settings.NeuralEnabled && !_excludedCache) PhraseOpportunity?.Invoke(_version, Engine.FieldText);
                break;
            default:
                ClearSuggestion();
                break;
        }
        return swallowed;
    }

    private void UpdateWordSuggestion()
    {
        if (!Settings.WordCompletion || _excludedCache || !Engine.WordInProgress) { ClearSuggestion(); return; }
        var typed = Engine.CurrentWordText;
        var list = Completer.Complete(typed, Engine.CurrentLayout, Engine.ContextLanguage);
        if (list.Count == 0) { ClearSuggestion(); return; }
        SetSuggestion(new Suggestion(list[0].Word, typed.Length, list[0].Word, false,
            list.Skip(1).Select(c => c.Word).ToList(), _version));
    }

    /// <summary>Called by the neural module when its answer arrives; ignored if the user kept typing.</summary>
    public void OfferPhrase(long version, string text)
    {
        lock (_sync)
        {
            if (version != _version || Engine.WordInProgress || Settings.Paused || string.IsNullOrWhiteSpace(text)) return;
            SetSuggestion(new Suggestion(text, 0, text, true, Array.Empty<string>(), version));
        }
    }

    private void SetSuggestion(Suggestion s)
    {
        CurrentSuggestion = s;
        SuggestionChanged?.Invoke(s);
    }

    private void ClearSuggestion()
    {
        if (CurrentSuggestion == null) return;
        CurrentSuggestion = null;
        SuggestionChanged?.Invoke(null);
    }

    private bool Handle(KeyInput k)
    {
        // ---------- modifiers: double-tap detection ----------
        if (k.Special is SpecialKey.Shift or SpecialKey.Ctrl or SpecialKey.Alt or SpecialKey.Meta)
        {
            if (k.IsDown)
            {
                if (_held.Add(k.Special)) { _tapKey = k.Special; _tapRight = k.RightSide; _tapDownAt = DateTime.UtcNow; _otherKeySinceDown = false; }
            }
            else
            {
                _held.Remove(k.Special);
                if (_tapKey == k.Special && !_otherKeySinceDown && (DateTime.UtcNow - _tapDownAt).TotalMilliseconds < DoubleTapMs)
                    OnModifierTap(k.Special, _tapRight);
                _tapKey = SpecialKey.None;
            }
            return false;
        }
        if (!k.IsDown)
        {
            if (_swallowUp != null && k.KeyName == _swallowUp) { _swallowUp = null; return true; }
            return false;
        }
        _otherKeySinceDown = true;
        _lastTapKey = SpecialKey.None;

        // ---------- recording a custom shortcut ----------
        if (Recording && k.KeyName != null)
        {
            Recording = false;
            _swallowUp = k.KeyName;
            bool cancel = k.KeyName == "Escape" && !k.Ctrl && !k.Alt && !k.Shift && !k.Meta;
            Recorded?.Invoke(cancel ? null : new KeyCombo(k.Ctrl, k.Alt, k.Shift, k.Meta, k.KeyName));
            return true;
        }

        // ---------- custom shortcuts ----------
        foreach (var a in Enum.GetValues<HotkeyAction>())
        {
            if (Settings.GetHotkey(a) != Hotkey.Custom || Settings.GetCombo(a) is not { } combo || !combo.Matches(k)) continue;
            if (a != HotkeyAction.Pause && Settings.Paused) break;
            _swallowUp = k.KeyName;
            FireAction(a);
            return true;
        }

        // ---------- Pause-based hotkeys ----------
        if (k.Special == SpecialKey.Pause)
        {
            var h = k.Shift ? Hotkey.ShiftPause : k.Alt ? Hotkey.AltPause : Hotkey.Pause;
            if (Fire(h)) return true;
            return false;
        }

        // shortcuts (Ctrl+C, Cmd+V, Alt+Tab…) → context lost
        if (k.Ctrl || k.Alt || k.Meta)
        {
            // AltGr on Windows = Ctrl+Alt and types characters; treat like printable but don't track
            Engine.Reset();
            return false;
        }

        if (Settings.Paused || IsExcludedApp())
        {
            if (k.Special == SpecialKey.None) Engine.Reset();
            return false;
        }

        switch (k.Special)
        {
            case SpecialKey.None:
                SyncLayout();
                Engine.Key(new KeyStroke(k.PhysicalKey, k.Shift, k.Caps));
                return false;
            case SpecialKey.Backspace:
                Engine.Backspace();
                return false;
            case SpecialKey.Space:
            case SpecialKey.Enter:
            case SpecialKey.Tab:
            {
                SyncLayout();
                string boundary = k.Special switch { SpecialKey.Space => " ", SpecialKey.Enter => "\n", _ => "\t" };
                var c = Engine.Boundary(boundary);
                if (c == null) return false;
                // swallow the key, rewrite the word, then replay the original key
                var text = c.Insert[..^boundary.Length];
                _platform.Replace(c.Erase, text);
                if (c.SwitchTo != null) _platform.SwitchLayout(c.SwitchTo);
                _platform.ReplayKey(k.Special);
                Corrected?.Invoke(c);
                return true;
            }
            default:
                Engine.Reset();
                return false;
        }
    }

    private void OnModifierTap(SpecialKey key, bool right)
    {
        var now = DateTime.UtcNow;
        if (key == SpecialKey.Ctrl && right && Fire(Hotkey.RightCtrl)) { _lastTapKey = SpecialKey.None; return; }
        if (_lastTapKey == key && (now - _lastTapAt).TotalMilliseconds < DoubleTapMs)
        {
            _lastTapKey = SpecialKey.None;
            var h = key switch
            {
                SpecialKey.Shift => Hotkey.DoubleShift,
                SpecialKey.Ctrl => Hotkey.DoubleCtrl,
                SpecialKey.Alt => Hotkey.DoubleAlt,
                _ => Hotkey.None
            };
            Fire(h);
            return;
        }
        _lastTapKey = key; _lastTapAt = now;
    }

    private bool Fire(Hotkey h)
    {
        if (h is Hotkey.None or Hotkey.Custom || Recording) return false;
        if (h == Settings.PauseHotkey) { FireAction(HotkeyAction.Pause); return true; }
        if (Settings.Paused) return false;
        if (h == Settings.ManualHotkey) { FireAction(HotkeyAction.Manual); return true; }
        if (h == Settings.SelectionHotkey) { FireAction(HotkeyAction.Selection); return true; }
        return false;
    }

    private void FireAction(HotkeyAction a)
    {
        switch (a)
        {
            case HotkeyAction.Pause: Paused = !Paused; break;
            case HotkeyAction.Manual: ManualConvertCore(); break;
            default: ConvertSelection(); break;
        }
    }

    public void ManualConvert()
    {
        lock (_sync) ManualConvertCore();
    }

    private void ManualConvertCore()
    {
        SyncLayout();
        var c = Engine.Manual();
        if (c == null) return;
        _platform.Replace(c.Erase, c.Insert);
        if (c.SwitchTo != null) _platform.SwitchLayout(c.SwitchTo);
        Corrected?.Invoke(c);
    }

    public void ConvertSelection()
    {
        Engine.Reset();
        _platform.ConvertSelection(text =>
        {
            (string Text, KeyboardLayout Target)? r;
            lock (_sync) r = Engine.ConvertText(text);
            if (r is not { } res) return null;
            _platform.SwitchLayout(res.Target);
            lock (_sync) Engine.CurrentLayout = res.Target;
            Corrected?.Invoke(new Correction(text.Length, res.Text, res.Target, CorrectionKind.Selection, text, res.Text));
            return res.Text;
        });
    }

    private void SyncLayout()
    {
        var cur = _platform.GetCurrentLayout(Engine.Layouts);
        if (cur != null && cur.Id != Engine.CurrentLayout.Id)
        {
            // the user switched the layout by hand: the current word is now mixed, start fresh
            Engine.Reset();
            Engine.CurrentLayout = cur;
        }
    }

    private bool IsExcludedApp()
    {
        if (Settings.ExcludedApps.Count == 0) return false;
        var now = DateTime.UtcNow;
        if ((now - _excludedCheckedAt).TotalMilliseconds < 700) return _excludedCache;
        _excludedCheckedAt = now;
        var app = _platform.ForegroundApp();
        if (app != _excludedCacheApp)
        {
            _excludedCacheApp = app;
            _excludedCache = app != null && Settings.ExcludedApps.Any(x =>
                app.Contains(x, StringComparison.OrdinalIgnoreCase));
            Engine.Reset();
        }
        return _excludedCache;
    }

    public void Dispose() => _platform.Dispose();
}
