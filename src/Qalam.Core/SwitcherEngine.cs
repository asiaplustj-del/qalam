namespace Qalam.Core;

/// <summary>What the platform layer must do on screen.</summary>
/// <param name="Erase">How many characters to delete with Backspace.</param>
/// <param name="Insert">Text to type instead.</param>
/// <param name="SwitchTo">Keyboard layout to activate afterwards (null = keep).</param>
/// <param name="Kind">Why it happened (for the tray indicator / sound / log).</param>
public sealed record Correction(int Erase, string Insert, KeyboardLayout? SwitchTo, CorrectionKind Kind, string From = "", string To = "");

public enum CorrectionKind { Auto, Manual, Undo, Selection }

/// <summary>
/// Platform-independent heart of the switcher. The OS layer feeds it key events and
/// executes the <see cref="Correction"/>s it returns.
/// </summary>
public sealed class SwitcherEngine
{
    private readonly Detector _detector;
    private readonly List<KeyStroke> _word = new();
    private List<KeyStroke> _lastWord = new();
    private KeyboardLayout? _lastLayout;
    private string _lastBoundary = "";
    private int _boundaryLeft;              // chars of the last boundary still on screen
    private bool _lastConverted;
    private string? _contextLang;           // language of recent words in this text field

    private Correction? _lastAuto;          // for undo
    private string _lastAutoBoundary = "";
    private KeyboardLayout? _lastAutoOrigLayout;
    private bool _undoAvailable;

    private int _manualStep;                // repeated manual presses cycle through layouts
    private KeyboardLayout? _manualOrigin;
    private bool _manualActive;
    private string? _manualVariant, _manualOnScreen;

    public IReadOnlyList<KeyboardLayout> Layouts { get; set; }
    public KeyboardLayout CurrentLayout { get; set; }
    public bool AutoMode { get; set; } = true;
    public bool Paused { get; set; }
    /// <summary>Learn an exception when the user undoes an automatic correction.</summary>
    public bool LearnFromUndo { get; set; } = true;
    /// <summary>"хамчун" → "ҳамчун" when the word only exists with Tajik letters.</summary>
    public bool RestoreTajik { get; set; } = true;

    private readonly System.Text.StringBuilder _committed = new();   // finished text of the current field
    /// <summary>What the user has typed in the current text field (best effort, max ~2000 chars).</summary>
    public string FieldText => _committed + CurrentWordText;
    public string? ContextLanguage => _contextLang;
    public bool WordInProgress => _word.Count > 0;

    public Detector Detector => _detector;
    public event Action<Correction>? Corrected;

    public SwitcherEngine(Detector detector, IReadOnlyList<KeyboardLayout> layouts, KeyboardLayout current)
    {
        _detector = detector; Layouts = layouts; CurrentLayout = current;
    }

    public string CurrentWordText => CurrentLayout.Render(_word);

    /// <summary>A printable key was pressed (no Ctrl/Alt/Win/Cmd).</summary>
    public void Key(KeyStroke s)
    {
        if (_boundaryLeft == 0 && _word.Count == 0) { /* new word */ }
        _word.Add(s);
        _undoAvailable = false;
        _manualActive = false;
    }

    public void Backspace()
    {
        _undoAvailable = false;
        _manualActive = false;
        if (_word.Count > 0) { _word.RemoveAt(_word.Count - 1); return; }
        if (_committed.Length > 0) _committed.Length--;
        if (_boundaryLeft > 0)
        {
            _boundaryLeft--;
            if (_boundaryLeft == 0 && _lastWord.Count > 0 && _lastLayout?.Id == CurrentLayout.Id)
            {   // user deleted the space → continue editing the previous word
                _word.AddRange(_lastWord);
                _lastWord = new();
            }
        }
    }

    /// <summary>
    /// Space / Enter / Tab was pressed. If a correction is returned, the platform must
    /// SWALLOW the boundary key, apply the correction, and then type <paramref name="boundary"/>.
    /// (The correction's Insert already ends with the boundary text.)
    /// </summary>
    public Correction? Boundary(string boundary)
    {
        _manualActive = false;
        Correction? result = null;
        bool converted = false;
        _wordRenderLen = CurrentLayout.Render(_word).Length;
        if (_word.Count > 0 && AutoMode && !Paused)
        {
            var d = _detector.Decide(CurrentLayout, Layouts, _word, _contextLang);
            if (d != null)
            {
                var from = CurrentLayout.Render(_word);
                string prefixFrom = "", prefixTo = "";
                // Retro: previous short word typed in the same wrong layout ("vs ujdjhbv" → "мы говорим")
                if (_lastWord.Count > 0 && !_lastConverted && _lastLayout?.Id == CurrentLayout.Id
                    && _boundaryLeft == _lastBoundary.Length && _lastBoundary == " "
                    && _detector.PrefersRetro(CurrentLayout, d.Layout, _lastWord))
                {
                    prefixFrom = CurrentLayout.Render(_lastWord) + _lastBoundary;
                    prefixTo = d.Layout.Render(_lastWord) + _lastBoundary;
                }
                result = new Correction(prefixFrom.Length + from.Length, prefixTo + d.Text + boundary, d.Layout,
                                        CorrectionKind.Auto, prefixFrom + from, prefixTo + d.Text);
                _lastAuto = result; _lastAutoBoundary = boundary; _lastAutoOrigLayout = CurrentLayout;
                _undoAvailable = true;
                CurrentLayout = d.Layout;
                converted = true;
                _contextLang = d.Layout.Language;
                Corrected?.Invoke(result);
            }
            else if (RestoreTajik && CurrentLayout.Language is "ru" or "tg"
                     && _detector.RestoreTajik(CurrentLayout.Render(_word), _contextLang) is { } fixedText)
            {
                var from = CurrentLayout.Render(_word);
                result = new Correction(from.Length, fixedText + boundary, null, CorrectionKind.Auto, from, fixedText);
                _lastAuto = result; _lastAutoBoundary = boundary; _lastAutoOrigLayout = CurrentLayout;
                _undoAvailable = true;
                converted = true;
                _contextLang = "tg";
                Corrected?.Invoke(result);
            }
            else
            {
                _undoAvailable = false;
                // context = language in which this word is most typical among layouts that render it the same way
                var text = CurrentLayout.Render(_word);
                if (Detector.Core(text).Length >= 2)
                {
                    float bestF = float.MinValue; string? lang = null;
                    foreach (var l in Layouts)
                    {
                        if (l.Render(_word) != text) continue;
                        var c = _detector.Evaluate(l, _word);
                        if (c.Freq is { } f && f > bestF) { bestF = f; lang = l.Language; }
                    }
                    if (lang != null) _contextLang = lang;
                }
            }
        }
        else _undoAvailable = false;

        if (_word.Count > 0)
        {
            _lastWord = new List<KeyStroke>(_word);
            _lastLayout = CurrentLayout;
            _lastConverted = converted;
        }
        if (result != null) { Trim(result.From.Length - _wordRenderLen);   // retro: previous word is already committed
 _committed.Append(result.Insert); }
        else _committed.Append(CurrentLayout.Render(_word)).Append(boundary);
        if (_committed.Length > 2000) _committed.Remove(0, _committed.Length - 1500);
        _lastBoundary = boundary;
        _boundaryLeft = boundary.Length;
        _word.Clear();
        return result;
    }

    private int _wordRenderLen;
    private void Trim(int n) { if (n > 0) _committed.Length = Math.Max(0, _committed.Length - n); }

    /// <summary>A completion was accepted: the current word was replaced by <paramref name="text"/>.</summary>
    public void AcceptCompletion(string text)
    {
        _committed.Append(text);
        _word.Clear(); _lastWord = new(); _boundaryLeft = 0;
        _undoAvailable = false; _manualActive = false;
    }

    /// <summary>
    /// Manual hotkey (Pause/Break, double Shift…):
    /// • right after an auto-correction → undo it (and remember the word as an exception);
    /// • while typing a word → convert it, repeated presses cycle through layouts;
    /// • right after a word + space → convert that last word.
    /// </summary>
    public Correction? Manual()
    {
        if (_undoAvailable && _lastAuto is { } a && _lastAutoOrigLayout is { } orig)
        {
            _undoAvailable = false;
            var undo = new Correction(a.Insert.Length, a.From + _lastAutoBoundary, orig, CorrectionKind.Undo, a.To, a.From);
            if (LearnFromUndo) _detector.User.AddException(a.From);
            Trim(a.Insert.Length); _committed.Append(a.From).Append(_lastAutoBoundary);
            CurrentLayout = orig;
            _lastLayout = orig;
            Corrected?.Invoke(undo);
            return undo;
        }

        List<KeyStroke> strokes; string tail;
        if (_word.Count > 0) { strokes = _word; tail = ""; }
        else if (_lastWord.Count > 0 && _boundaryLeft == _lastBoundary.Length && _lastLayout != null)
        { strokes = _lastWord; tail = _lastBoundary; CurrentLayout = _lastLayout; }
        else return null;

        if (!_manualActive)
        {
            _manualActive = true; _manualStep = 0; _manualOrigin = CurrentLayout;
            _manualVariant = RestoreTajik && CurrentLayout.Language is "ru" or "tg"
                ? _detector.BestTajikVariant(CurrentLayout.Render(strokes)) : null;
            _manualOnScreen = CurrentLayout.Render(strokes);
        }
        var order = ManualOrder(strokes, _manualOrigin!);
        // step 0: Tajik letters ("ки" → "кӣ") if possible, then layouts EN → RU → TJ → back
        var steps = new List<(KeyboardLayout Layout, string Text, bool Variant)>();
        if (_manualVariant != null) steps.Add((_manualOrigin!, _manualVariant, true));
        foreach (var l in order) steps.Add((l, l.Render(strokes), false));
        if (_manualVariant != null && order.Count == 0) steps.Add((_manualOrigin!, _manualOrigin!.Render(strokes), false));
        if (steps.Count == 0) return null;
        var (target, toText, isVariant) = steps[_manualStep % steps.Count];
        _manualStep++;

        var fromText = _manualOnScreen ?? CurrentLayout.Render(strokes);
        var c = new Correction(fromText.Length + tail.Length, toText + tail, isVariant ? null : target, CorrectionKind.Manual, fromText, toText);
        _manualOnScreen = toText;
        CurrentLayout = target;
        if (tail.Length > 0) _lastLayout = target;
        _undoAvailable = false;
        Corrected?.Invoke(c);
        return c;
    }

    /// <summary>Other layouts, most plausible first, then back to where we started.</summary>
    private List<KeyboardLayout> ManualOrder(IReadOnlyList<KeyStroke> strokes, KeyboardLayout origin)
    {
        var originText = origin.Render(strokes);
        var others = Layouts.Where(l => l.Id != origin.Id)
            .Select(l => _detector.Evaluate(l, strokes))
            .Where(c => c.Text != originText)
            .OrderByDescending(c => c.Score)
            .Select(c => c.Layout).ToList();
        if (others.Count > 0) others.Add(origin);
        return others;
    }

    /// <summary>Converts arbitrary (selected) text typed in the wrong layout.</summary>
    public (string Text, KeyboardLayout Target)? ConvertText(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        (string, KeyboardLayout, double)? best = null;
        foreach (var src in Layouts)
        {
            if (!src.TryEncode(text, out var strokes) && strokes.Count(s => s.Key != '\0') < text.Count(char.IsLetter)) continue;
            foreach (var dst in Layouts)
            {
                if (dst.Id == src.Id) continue;
                var outText = RenderWithPassthrough(text, strokes, dst);
                if (outText == text) continue;
                double score = ScoreText(outText, dst) - ScoreText(text, src);
                if (best is null || score > best.Value.Item3) best = (outText, dst, score);
            }
        }
        return best is { } b ? (b.Item1, b.Item2) : null;
    }

    private static string RenderWithPassthrough(string original, List<KeyStroke> strokes, KeyboardLayout dst)
    {
        var sb = new System.Text.StringBuilder(original.Length);
        for (int i = 0; i < strokes.Count; i++)
        {
            var s = strokes[i];
            if (s.Key == '\0' || char.IsWhiteSpace(original[i])) { sb.Append(original[i]); continue; }
            var c = dst.Render(s);
            sb.Append(c == '\0' ? original[i] : c);
        }
        return sb.ToString();
    }

    private double ScoreText(string text, KeyboardLayout layout)
    {
        double sum = 0; int n = 0;
        foreach (var w in text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!layout.TryEncode(w, out var st)) { sum -= 20; n++; continue; }
            var c = _detector.Evaluate(layout, st);
            sum += double.IsNegativeInfinity(c.Score) ? -20 : c.Score; n++;
        }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>Mouse click, focus change, arrows, Ctrl+… — context is lost.</summary>
    public void Reset()
    {
        _word.Clear(); _lastWord = new(); _boundaryLeft = 0; _contextLang = null; _lastConverted = false; _committed.Clear();
        _undoAvailable = false; _manualActive = false;
    }
}
