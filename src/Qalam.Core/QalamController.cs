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
        ApplySettings();
        platform.KeyHandler = OnKey;
        platform.MouseClicked += () => Engine.Reset();
    }

    public void ApplySettings()
    {
        Engine.AutoMode = Settings.AutoMode;
        Engine.Paused = Settings.Paused;
        Engine.LearnFromUndo = Settings.LearnFromUndo;
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
        try { lock (_sync) return Handle(k); }
        catch { Engine.Reset(); return false; }
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
        if (!k.IsDown) return false;
        _otherKeySinceDown = true;
        _lastTapKey = SpecialKey.None;

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
        if (h == Hotkey.None) return false;
        if (h == Settings.PauseHotkey) { Paused = !Paused; return true; }
        if (Settings.Paused) return false;
        if (h == Settings.ManualHotkey) { ManualConvertCore(); return true; }
        if (h == Settings.SelectionHotkey) { ConvertSelection(); return true; }
        return false;
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
