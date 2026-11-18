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
    var cq = new KeyInput(true, SpecialKey.Other, Ctrl: true, Shift: true, KeyName: "Q");   // manual hotkey is the custom combo now
    fp.Type("ки", L["ru"]); fp.KeyHandler!(cq); fp.KeyHandler!(cq with { IsDown = false });
    Check($"manual Tajik letters ки→кӣ: '{fp.Screen}'", fp.Screen.ToString() == "кӣ");
    fp.KeyHandler!(cq); fp.KeyHandler!(cq with { IsDown = false });
    Check($"second press → other layout: '{fp.Screen}'", fp.Screen.ToString() == "rb");

    fp.Screen.Clear(); fp.Click(); fp.Current = L["ru"];
    fp.Type("ҳамчун ", L["tg"]);
    Check($"tajik in RU layout: '{fp.Screen}'", fp.Screen.ToString() == "ҳамчун " && fp.Current.Language == "tg");
}

// ---------- Tajik letters typed with Russian look-alikes ----------
Console.WriteLine("\n=== Tajik letters restoration (ҳ→х, ҷ→ч, қ→к, ғ→г, ӯ→у, ӣ→и) ===");
{
    var back = new Dictionary<char, char> { ['ҳ'] = 'х', ['ҷ'] = 'ч', ['қ'] = 'к', ['ғ'] = 'г', ['ӯ'] = 'у', ['ӣ'] = 'и', ['Ҳ'] = 'Х', ['Ҷ'] = 'Ч', ['Қ'] = 'К', ['Ғ'] = 'Г', ['Ӯ'] = 'У', ['Ӣ'] = 'И' };
    int words = 0, fixedOk = 0, needed = 0, broken = 0; var ex = new List<string>();
    foreach (var line in File.ReadAllLines(Path.Combine(dir, "lines_tg.txt")).Take(400))
    {
        var plain = new string(line.Select(c => back.GetValueOrDefault(c, c)).ToArray());
        var eng = new SwitcherEngine(det, layouts, L["ru"]);
        var screen = new System.Text.StringBuilder();
        foreach (var ch in plain + " ")
        {
            if (ch == ' ') { var c = eng.Boundary(" "); if (c != null) { screen.Length -= c.Erase; screen.Append(c.Insert); } else screen.Append(' '); continue; }
            if (!L["ru"].TryEncode(ch.ToString(), out var st) || st[0].Key == '\0') { screen.Append(ch); continue; }
            screen.Append(eng.CurrentLayout.Render(st[0])); eng.Key(st[0]);
        }
        var orig = line.Split(' '); var typed = plain.Split(' '); var got = screen.ToString().TrimEnd().Split(' ');
        for (int i = 0; i < orig.Length && i < got.Length; i++)
        {
            words++;
            bool need = orig[i] != typed[i];
            if (need) { needed++; if (got[i] == orig[i]) fixedOk++; else if (ex.Count < 14) ex.Add($"{got[i]}({orig[i]})"); }
            else if (got[i] != orig[i]) { broken++; if (ex.Count < 14) ex.Add($"!{got[i]}({orig[i]})"); }
        }
    }
    Console.WriteLine($"  words {words}: needed fix {needed}, fixed {fixedOk} ({100.0 * fixedOk / Math.Max(1, needed):F1}%), broken good words {broken}  {string.Join(" ", ex)}");
    if (broken * 200 > words) failures++;
}

// ---------- word completion through the controller ----------
Console.WriteLine("\n=== Word completion ===");
{
    var fp = new FakePlatform(layouts);
    var settings = new QalamSettings { ExcludedApps = new() };
    var ctl = new QalamController(fp, new SwitcherEngine(new Detector(models), layouts, layouts[1]), settings);
    QalamController.Suggestion? shown = null;
    ctl.SuggestionChanged += s => shown = s;
    fp.Current = L["ru"];
    fp.Type("Здравс", L["ru"]);
    Check($"suggestion shown: {shown?.Text}", shown?.Text == "Здравствуйте");
    fp.PressTab();
    Check($"Tab accepts: '{fp.Screen}'", fp.Screen.ToString() == "Здравствуйте" && shown == null);
    fp.Screen.Clear(); fp.Click();
    fp.Type("точик", L["ru"]);
    Check($"Tajik via look-alikes: {shown?.Text}", shown?.Text?.StartsWith("тоҷик") == true);
    fp.Type("и ", L["ru"]);
    Check($"ignored by typing on: '{fp.Screen}'", shown == null);
    fp.Screen.Clear(); fp.Click();
    fp.Type("как", L["ru"]);
    Check($"no suggestion for a common short word: {shown?.Text ?? "-"}", shown == null);
    fp.PressTab();
    Check("Tab passes through without suggestion", fp.Screen.ToString() == "как\t");
    long ver = 0; string? ctxText = null;
    settings.NeuralEnabled = true;
    ctl.PhraseOpportunity += (v, t) => { ver = v; ctxText = t; };
    fp.Screen.Clear(); fp.Click();
    fp.Type("мы снимаем ", L["ru"]);
    Check($"phrase opportunity with context '{ctxText}'", ctxText == "мы снимаем ");
    ctl.OfferPhrase(ver, "новый фильм");
    Check($"phrase offered: {shown?.Text}", shown?.IsPhrase == true);
    fp.PressTab();
    Check($"phrase accepted: '{fp.Screen}'", fp.Screen.ToString() == "мы снимаем новый фильм");
    ctl.OfferPhrase(ver, "устаревший ответ");
    Check("stale neural answer ignored", shown == null);
}

Console.WriteLine(failures == 0 ? "\nQUALITY OK" : $"\nQUALITY FAILED ({failures})");
return failures == 0 ? 0 : 1;
