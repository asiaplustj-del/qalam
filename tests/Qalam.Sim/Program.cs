using Qalam.Core;
var L = new Dictionary<string, KeyboardLayout> { ["en"] = KeyboardLayout.English, ["ru"] = KeyboardLayout.Russian, ["tg"] = KeyboardLayout.Tajik };
var layouts = L.Values.ToList();
var sw = System.Diagnostics.Stopwatch.StartNew();
var models = new[] { "en", "ru", "tg" }.Select(LanguageModel.LoadEmbedded).ToList();
Console.WriteLine($"load {sw.ElapsedMilliseconds} ms; words: " + string.Join(", ", models.Select(m => $"{m.Language}={m.WordCount}")));
var det = new Detector(models);
int failures = 0;
string dir = args.Length > 0 ? args[0] : ".";
foreach (var lang in new[] { "en", "ru", "tg" })
{
    var words = File.ReadAllText(Path.Combine(dir, $"test_{lang}.txt")).Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    var src = L[lang];
    int fp = 0, n = 0;
    var fps = new List<string>();
    foreach (var w in words) { src.TryEncode(w, out var st); var eng = new SwitcherEngine(det, layouts, src); foreach (var s in st) eng.Key(s); var c = eng.Boundary(" "); n++; if (c != null) { fp++; if (fps.Count < 25) fps.Add($"{w}->{c.To}"); } }
    if (fp * 200 > n) failures++;   // > 0.5 % false conversions
    Console.WriteLine($"[{lang}] typed correctly: {n} words, false conversions {fp} ({100.0 * fp / n:F2}%)  e.g. {string.Join(" ", fps)}");
    foreach (var wrong in layouts.Where(x => x.Id != src.Id))
    {
        int ok = 0, miss = 0, bad = 0, same = 0; var misses = new List<string>(); var bads = new List<string>();
        foreach (var w in words)
        {
            src.TryEncode(w, out var st);
            var typed = wrong.Render(st);
            if (typed == w) { same++; continue; }
            var eng = new SwitcherEngine(det, layouts, wrong); foreach (var s in st) eng.Key(s);
            var c = eng.Boundary(" ");
            if (c == null) { miss++; if (misses.Count < 20) misses.Add($"{typed}({w})"); }
            else if (c.To == w) ok++; else { bad++; if (bads.Count < 10) bads.Add($"{typed}->{c.To}({w})"); }
        }
        int tot = ok + miss + bad;
        Console.WriteLine($"   typed in {wrong.Language}: fixed {ok}/{tot} ({100.0 * ok / Math.Max(1, tot):F1}%), missed {miss}, wrong {bad}, identical {same}\n      miss: {string.Join(" ", misses)}\n      wrong: {string.Join(" ", bads)}");
    }
}
// manual + selection demo
var e2 = new SwitcherEngine(det, layouts, L["en"]);
foreach (var t in new[] { "ghbdtn vbh", "lkz dbyljec b ecnfyjdbnm tuj&", "ысовуд", "Hello world", "Руддщ цщкдв", "Ассалому алейкум", "щамчун" })
{ var r = e2.ConvertText(t); Console.WriteLine($"select: {t} -> {r?.Text} [{r?.Target.Language}]"); }

// ---------- sentence-level simulation: user keeps typing, layout switches after corrections ----------
Console.WriteLine("\n=== Continuous typing ===");
foreach (var (lang, file) in new[] { ("en", "test_en.txt"), ("ru", "test_ru.txt"), ("tg", "lines_tg.txt") })
{
    var src = L[lang];
    var lines = File.ReadAllLines(Path.Combine(dir, file)).Where(l => l.Trim().Length > 0).ToList();
    foreach (var start in layouts)
    {
        int words = 0, wrongWords = 0; var examples = new List<string>();
        foreach (var line in lines)
        {
            var eng = new SwitcherEngine(det, layouts, start);
            var screen = new System.Text.StringBuilder();
            foreach (var ch in line + " ")
            {
                if (ch == ' ')
                {
                    var c = eng.Boundary(" ");
                    if (c != null) { screen.Length -= c.Erase; screen.Append(c.Insert); } else screen.Append(' ');
                    continue;
                }
                if (!src.TryEncode(ch.ToString(), out var st)) { screen.Append(ch); continue; }
                screen.Append(eng.CurrentLayout.Render(st[0])); eng.Key(st[0]);
            }
            var a = line.Split(' '); var b = screen.ToString().TrimEnd().Split(' ');
            for (int i = 0; i < a.Length; i++) { words++; if (i >= b.Length || a[i] != b[i]) { wrongWords++; if (examples.Count < 12) examples.Add($"{(i < b.Length ? b[i] : "?")}({a[i]})"); } }
        }
        if (wrongWords * 50 > words) failures++;   // > 2 % wrong words
        Console.WriteLine($"[{lang}] start in {start.Language}: words wrong on screen {wrongWords}/{words} ({100.0 * wrongWords / words:F1}%)  {string.Join(" ", examples)}");
    }
}

// ---------- controller end-to-end ----------
Console.WriteLine("\n=== Controller ===");
void Check(string name, bool ok) { Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {name}"); if (!ok) failures++; }
{
    var fp = new FakePlatform(layouts);
    var settings = new QalamSettings { ManualHotkey = Hotkey.DoubleShift, SelectionHotkey = Hotkey.DoubleCtrl, PauseHotkey = Hotkey.DoubleAlt, ExcludedApps = new() };
    var ctl = new QalamController(fp, new SwitcherEngine(new Detector(models), layouts, layouts[0]), settings);
    fp.Current = L["en"];
    fp.Type("привет мир ", L["ru"]);
    Check($"auto: '{fp.Screen}'", fp.Screen.ToString() == "привет мир ");
    Check("layout switched to RU", fp.Current.Language == "ru");
    fp.Screen.Clear(); fp.Click(); fp.Current = L["ru"];
    fp.Type("hello ", L["en"]);
    Check($"auto EN: '{fp.Screen}'", fp.Screen.ToString() == "hello ");
    fp.Tap(SpecialKey.Shift); fp.Tap(SpecialKey.Shift);   // undo
    Check($"undo by double Shift: '{fp.Screen}'", fp.Screen.ToString() == "руддщ ");
    Check("undo learned exception", ctl.Engine.Detector.User.IsException("руддщ"));
    fp.Screen.Clear(); fp.Click(); fp.Current = L["en"];
    fp.Type("кошка", L["ru"]); fp.Tap(SpecialKey.Shift); fp.Tap(SpecialKey.Shift);
    Check($"manual mid-word: '{fp.Screen}'", fp.Screen.ToString() == "кошка");
    fp.Screen.Clear(); fp.Click(); fp.Current = L["en"];
    fp.Tap(SpecialKey.Alt); fp.Tap(SpecialKey.Alt);
    fp.Type("привет ", L["ru"]);
    Check($"paused: '{fp.Screen}'", fp.Screen.ToString() == "ghbdtn " && settings.Paused);
    fp.Tap(SpecialKey.Alt); fp.Tap(SpecialKey.Alt);
    fp.Screen.Clear(); fp.Screen.Append("lkz dbyljec");
    fp.Tap(SpecialKey.Ctrl); fp.Tap(SpecialKey.Ctrl);
    Check($"selection: '{fp.Screen}'", fp.Screen.ToString() == "для виндоус");
    // custom shortcut: record Ctrl+Shift+Q, then use it for manual conversion
    KeyCombo? got = null;
    ctl.Recorded += c => got = c;
    ctl.StartRecording();
    bool sw1 = fp.KeyHandler!(new KeyInput(true, SpecialKey.Other, Ctrl: true, Shift: true, KeyName: "Q"));
    bool sw2 = fp.KeyHandler!(new KeyInput(false, SpecialKey.Other, Ctrl: true, Shift: true, KeyName: "Q"));
    Check($"recorded combo: {got}", got?.Key == "Q" && got.Ctrl && got.Shift && sw1 && sw2 && got.IsSafe);
    settings.SetCustom(HotkeyAction.Manual, got!);
    fp.Screen.Clear(); fp.Click(); fp.Current = L["en"];
    fp.Type("кошка", L["ru"]);
    fp.KeyHandler!(new KeyInput(true, SpecialKey.Other, Ctrl: true, Shift: true, KeyName: "Q"));
    Check($"custom Ctrl+Shift+Q converts: '{fp.Screen}'", fp.Screen.ToString() == "кошка");
    Check("plain letter combo is unsafe", !new KeyCombo(false, false, true, false, "Q").IsSafe && new KeyCombo(false, false, false, false, "F9").IsSafe);
    ctl.StartRecording();
    fp.KeyHandler!(new KeyInput(true, SpecialKey.Navigation, KeyName: "Escape"));
    Check("Esc cancels recording", got == null && !ctl.Recording);

    fp.Screen.Clear(); fp.Click(); fp.Current = L["ru"];
    fp.Type("ҳамчун ", L["tg"]);
    Check($"tajik in RU layout: '{fp.Screen}'", fp.Screen.ToString() == "ҳамчун " && fp.Current.Language == "tg");
}

Console.WriteLine(failures == 0 ? "\nQUALITY OK" : $"\nQUALITY FAILED ({failures})");
return failures == 0 ? 0 : 1;
