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
    public sealed record Suggestion(string Text, int Erase, string Insert, bool IsPhrase, IReadOnlyList<string> Alternatives, long Version, string Typed = "");
    /// <summary>Show/hide the suggestion popup (null = hide). Raised on the hook thread.</summary>
    public event Action<Suggestion?>? SuggestionChanged;
    /// <summary>A moment to ask the model: after a space, on a pause inside a word, after Tab.</summary>
    /// <param name="Urgent">Ask right away (the user just accepted a word with Tab) instead of after a pause.</param>
    public sealed record PhraseRequest(long Version, string Text, bool MidWord, bool Urgent);
    /// <summary>A neural module may propose a continuation of the text.</summary>
    public event Action<PhraseRequest>? PhraseOpportunity;
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
        Completer = new Completer(engine.Detector) { Phrases = engine.Phrases };
        engine.Speller ??= new SpellFixer(engine.Detector);
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
        Engine.FixTypos = Settings.FixTypos;
        Engine.LearnPhrases = Settings.LearnPhrases;
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

        // Tab accepts the visible suggestion — one word (or punctuation mark) per press, or all of it
        if (k.Special == SpecialKey.Tab && !k.Ctrl && !k.Alt && !k.Meta && !k.Shift && CurrentSuggestion is { } sug && !Recording)
        {
            var (accept, rest) = Settings.TabAcceptsWord ? PhraseText.FirstChunk(sug.Insert) : (sug.Insert, "");
            if (accept.Length == 0) (accept, rest) = (sug.Insert, "");
            _platform.Replace(sug.Erase, accept);
            Engine.AcceptCompletion(accept);
            _modelWord = null;
            _swallowUp = "Tab";
            _version++;
            if (rest.Trim().Length > 0)
            {
                _remainder = true;
                SetSuggestion(new Suggestion(rest, 0, rest, true, Array.Empty<string>(), _version));
            }
            else { _remainder = false; ClearSuggestion(); }
            // the model continues from the new text: the phrase keeps growing as you accept it
            if (Settings.NeuralEnabled && !_excludedCache) PhraseOpportunity?.Invoke(new PhraseRequest(_version, Engine.FieldText, false, true));
            return true;
        }
        _remainder = false;

        // Shift+Tab: next variant of the word (only while a suggestion with variants is visible)
        if (k.Special == SpecialKey.Tab && k.Shift && !k.Ctrl && !k.Alt && !k.Meta && CurrentSuggestion is { Alternatives.Count: > 0 } cur && !Recording)
        {
            var alts = cur.Alternatives.Skip(1).Append(cur.Text).ToList();
            var next = cur.Alternatives[0];
            _swallowUp = "Tab";
            SetSuggestion(cur with { Text = next, Insert = next, Alternatives = alts });
            return true;
        }

        bool swallowed = Handle(k);
        _version++;
        if (Settings.Paused || Recording) { ClearSuggestion(); return swallowed; }
        switch (k.Special)
        {
            case SpecialKey.None when !(k.Ctrl || k.Alt || k.Meta):
                UpdateWordSuggestion();
                // a pause in the middle of a word → the model may finish it in context ("дорогие дру" → "друзья")
                if (Settings.NeuralEnabled && !_excludedCache && Engine.WordInProgress && Engine.CurrentWordText.Length >= 2)
                    PhraseOpportunity?.Invoke(new PhraseRequest(_version, Engine.FieldText, true, false));
                break;
            case SpecialKey.Backspace:
                UpdateWordSuggestion();
                break;
            case SpecialKey.Space:
                _modelWord = null;
                ShowLearnedNextWord();
                if (Settings.NeuralEnabled && !_excludedCache) PhraseOpportunity?.Invoke(new PhraseRequest(_version, Engine.FieldText, false, false));
                break;
            default:
                _modelWord = null;
                ClearSuggestion();
                break;
        }
        return swallowed;
    }

    // the model's word for the current position: kept while the user types letters that agree with it
    private (string Context, string Text)? _modelWord;
    // the visible suggestion is what is left of a phrase after Tab
    private bool _remainder;

    private void UpdateWordSuggestion()
    {
        if (!Settings.WordCompletion || _excludedCache || !Engine.WordInProgress) { ClearSuggestion(); return; }
        var typed = Engine.CurrentWordText;
        var list = Completer.Complete(typed, Engine.CurrentLayout, Engine.ContextLanguage, 4, Engine.PreviousWord);
        var words = list.Select(c => c.Word).ToList();
        // the model's guess still fits what is typed → keep it first
        var before = Engine.FieldText[..^typed.Length];
        if (_modelWord is { } m && m.Context == before && m.Text.Length > typed.Length
            && Completer.SamePrefix(m.Text, typed.ToLowerInvariant()))
        {
            var text = m.Text.StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? typed + m.Text[typed.Length..] : m.Text;
            words.RemoveAll(w => text.StartsWith(w + " ", StringComparison.OrdinalIgnoreCase) || text.Equals(w, StringComparison.OrdinalIgnoreCase));
            SetSuggestion(new Suggestion(text, typed.Length, text, false, words.Take(3).ToList(), _version, typed));
            return;
        }
        if (words.Count == 0) { ClearSuggestion(); return; }
        SetSuggestion(new Suggestion(words[0], typed.Length, words[0], false, words.Skip(1).ToList(), _version, typed));
    }

    /// <summary>After a space: the word this user usually writes next ("дорогие " → "друзья").</summary>
    private void ShowLearnedNextWord()
    {
        ClearSuggestion();
        if (!Settings.WordCompletion || _excludedCache || Engine.PreviousWord is not { } prev || Engine.Phrases == null) return;
        var next = Engine.Phrases.After(prev, 4).Where(x => x.Count >= 2).Select(x => x.Word).ToList();
        if (next.Count == 0) return;
        SetSuggestion(new Suggestion(next[0], 0, next[0], true, next.Skip(1).ToList(), _version));
    }

    /// <summary>
    /// Called by the neural module when its answer arrives; ignored if the user kept typing.
    /// <paramref name="text"/> is the model's raw continuation of the field text.
    /// </summary>
    public void OfferPhrase(long version, string text)
    {
        lock (_sync)
        {
            if (version != _version || Settings.Paused || string.IsNullOrWhiteSpace(text)) return;
            if (!Engine.WordInProgress)
            {
                var field = Engine.FieldText;
                var phrase = PhraseText.Clean(text, field, Settings.PhraseMaxWords, Settings.NeuralLength >= 3 ? 2 : 1);
                if (phrase == null) return;
                // the text ends right after a word or a mark ("друзья", "дела?") → a word needs a space first
                if (field.Length > 0 && !char.IsWhiteSpace(field[^1]) && char.IsLetterOrDigit(phrase[0])) phrase = " " + phrase;
                if (_remainder && CurrentSuggestion is { IsPhrase: true } shown)
                {
                    // keep what is already shown unless the new answer continues it and is longer
                    var head = PhraseText.FirstChunk(shown.Text).Chunk.Trim();
                    if (!phrase.TrimStart().StartsWith(head, StringComparison.OrdinalIgnoreCase) || phrase.Length <= shown.Text.Length) return;
                }
                var alts = CurrentSuggestion is { IsPhrase: true } learned && !_remainder && !phrase.TrimStart().StartsWith(learned.Text + " ", StringComparison.OrdinalIgnoreCase)
                    ? new List<string> { learned.Text } : new List<string>();
                SetSuggestion(new Suggestion(phrase, 0, phrase, true, alts, version));
                return;
            }
            var typed = Engine.CurrentWordText;
            var before = Engine.FieldText[..^typed.Length];
            var full = PhraseText.CompleteWord(text, typed, before, IsWord, Math.Max(2, Settings.PhraseMaxWords - 1));
            if (full == null) return;
            _modelWord = (before, full);
            UpdateWordSuggestion();
        }
    }

    private bool IsWord(string lower)
    {
        foreach (var lang in new[] { "ru", "tg", "en" })
            if (Engine.Detector.Model(lang)?.Frequency(lower) != null) return true;
        return Engine.Speller?.IsRealWord(lower) == true;
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
