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
    ctl.PhraseOpportunity += r => { ver = r.Version; ctxText = r.Text; };
    fp.Screen.Clear(); fp.Click();
    fp.Type("мы снимаем ", L["ru"]);
    Check($"phrase opportunity with context '{ctxText}'", ctxText == "мы снимаем ");
    ctl.OfferPhrase(ver, "новый фильм");
    long staleVer = ver;
    Check($"phrase offered: {shown?.Text}", shown?.IsPhrase == true);
    fp.PressTab();
    Check($"Tab takes one word: '{fp.Screen}' + {shown?.Text}", fp.Screen.ToString() == "мы снимаем новый" && shown?.Text == " фильм");
    fp.PressTab();
    Check($"phrase accepted: '{fp.Screen}'", fp.Screen.ToString() == "мы снимаем новый фильм" && shown == null);
    ctl.OfferPhrase(staleVer, "устаревший ответ");
    Check("stale neural answer ignored", shown == null);
}

// ---------- typos, context, learned phrases, model word completion ----------
Console.WriteLine("\n=== Typos and context ===");
{
    var fp = new FakePlatform(layouts);
    var settings = new QalamSettings { ManualHotkey = Hotkey.DoubleShift, ExcludedApps = new() };
    var phrases = new UserPhrases();
    var ctl = new QalamController(fp, new SwitcherEngine(new Detector(models), layouts, layouts[1]) { Phrases = phrases }, settings);
    ctl.Engine.Speller!.WaitLoaded();
    QalamController.Suggestion? shown = null;
    ctl.SuggestionChanged += s => shown = s;
    fp.Current = L["ru"];
    fp.Type("мы превет ", L["ru"]);
    Check($"typo fixed: '{fp.Screen}'", fp.Screen.ToString() == "мы привет ");
    fp.Tap(SpecialKey.Shift); fp.Tap(SpecialKey.Shift);
    Check($"typo undone by the manual hotkey: '{fp.Screen}'", fp.Screen.ToString() == "мы превет ");
    fp.Type("превет ", L["ru"]);
    Check($"undone typo is remembered: '{fp.Screen}'", fp.Screen.ToString() == "мы превет превет ");
    fp.Screen.Clear(); fp.Click();
    fp.Type("Приевт как дела ", L["ru"]);
    Check($"swapped letters at sentence start: '{fp.Screen}'", fp.Screen.ToString() == "Привет как дела ");
    fp.Screen.Clear(); fp.Click();
    fp.Type("кто-то пришёл Фируз ", L["ru"]);
    Check($"hyphen words and names untouched: '{fp.Screen}'", fp.Screen.ToString() == "кто-то пришёл Фируз ");
    fp.Screen.Clear(); fp.Click(); fp.Current = L["en"];
    fp.Type("I hvae a dog ", L["en"]);
    Check($"English typo: '{fp.Screen}'", fp.Screen.ToString() == "I have a dog ");
    settings.FixTypos = false; ctl.ApplySettings();
    fp.Screen.Clear(); fp.Click(); fp.Current = L["ru"];
    fp.Type("превед ", L["ru"]);
    Check($"typo fixing can be switched off: '{fp.Screen}'", fp.Screen.ToString() == "превед ");
    settings.FixTypos = true; ctl.ApplySettings();

    // a complete word still gets longer forms: "дороги" → "дорогие"
    fp.Screen.Clear(); fp.Click();
    fp.Type("дороги", L["ru"]);
    Check($"complete word gets longer forms: {shown?.Text} [{string.Join(",", shown?.Alternatives ?? new List<string>())}]", shown != null && shown.Text.StartsWith("дороги") && shown.Text.Length > 6);
    // Shift+Tab → next variant
    var first = shown?.Text;
    fp.Press(new KeyInput(true, SpecialKey.Tab, Shift: true, KeyName: "Tab"));
    Check($"Shift+Tab shows another variant: {first} → {shown?.Text}", shown != null && shown.Text != first && fp.Screen.ToString() == "дороги");

    // the user's own phrases: after "дорогие друзья" twice, "друзья" ranks first
    fp.Screen.Clear(); fp.Click();
    fp.Type("дорогие друзья и дорогие друзья ", L["ru"]);
    Check($"pairs learned: {phrases.Count("дорогие", "друзья")}", phrases.Count("дорогие", "друзья") == 2);
    fp.Type("и дорогие ", L["ru"]);
    Check($"next word after a space: {shown?.Text}", shown?.Text == "друзья" && shown.IsPhrase);
    fp.Type("дру", L["ru"]);
    Check($"learned word ranks first: {shown?.Text}", shown?.Text == "друзья");

    // the model finishes a word in context
    long ver = 0; ctl.PhraseOpportunity += r => ver = r.Version;
    settings.NeuralEnabled = true;
    fp.Screen.Clear(); fp.Click();
    fp.Type("уважаемые колл", L["ru"]);
    ctl.OfferPhrase(ver, "еги, рад вас видеть");
    Check($"model ending: {shown?.Text}", shown?.Text == "коллеги, рад вас видеть");
    fp.Type("е", L["ru"]);
    Check($"model word kept while typing agrees: {shown?.Text}", shown?.Text == "коллеги, рад вас видеть");
    fp.PressTab();
    Check($"Tab → word: '{fp.Screen}' + '{shown?.Text}'", fp.Screen.ToString() == "уважаемые коллеги" && shown?.Text == ", рад вас видеть");
    fp.PressTab();
    Check($"Tab → comma: '{fp.Screen}'", fp.Screen.ToString() == "уважаемые коллеги,");
    ctl.OfferPhrase(ver, "рад вас видеть. Как ваши дела?");
    Check($"model extends the rest: '{shown?.Text}'", shown?.Text == " рад вас видеть.");
    ctl.OfferPhrase(ver, "мы начинаем");
    Check($"a different answer does not replace it: '{shown?.Text}'", shown?.Text == " рад вас видеть.");
    fp.PressTab(); fp.PressTab(); fp.PressTab(); fp.PressTab();
    Check($"word by word to the end: '{fp.Screen}'", fp.Screen.ToString() == "уважаемые коллеги, рад вас видеть." && shown == null);
    ctl.OfferPhrase(ver, "Как ваши дела?");
    Check($"next sentence after the end: '{shown?.Text}'", shown?.Text == " Как ваши дела?");
    settings.TabAcceptsWord = false;
    fp.PressTab();
    Check($"whole suggestion when word-by-word is off: '{fp.Screen}'", fp.Screen.ToString() == "уважаемые коллеги, рад вас видеть. Как ваши дела?");
    settings.TabAcceptsWord = true;
    fp.Screen.Clear(); fp.Click();
    fp.Type("Мы сегодня рады предс", L["ru"]);
    ctl.OfferPhrase(ver, "представить вам новый проект.");
    Check($"model full word: {shown?.Text}", shown?.Text == "представить вам новый проект.");
    ctl.OfferPhrase(ver, "<b>чепуха");
    Check("markup ignored", shown?.Text == "представить вам новый проект.");
    settings.NeuralEnabled = false;
}

Console.WriteLine("\n=== Typo correction on correct texts (must stay silent) ===");
{
    var sp = new SpellFixer(det); sp.WaitLoaded();
    foreach (var (file, lang) in new[] { ("test_ru.txt", "ru"), ("test_en.txt", "en"), ("test_tg.txt", "tg"), ("lines_tg.txt", "tg") })
    {
        int n = 0, fixes = 0; var ex = new List<string>();
        foreach (var line in File.ReadLines(Path.Combine(dir, file)))
        {
            bool start = true;
            foreach (var tok in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                n++;
                if (sp.Fix(tok, L[lang], lang, start) is { } r) { fixes++; ex.Add($"{tok}→{r}"); }
                start = tok[^1] is '.' or '!' or '?';
            }
        }
        Console.WriteLine($"  [{lang}] {file}: {n} words, corrected {fixes} {string.Join(" ", ex.Take(10))}");
        if (fixes * 300 > n) failures++;   // > 0.33 %
    }
    int ok = 0; var typos = new[] { ("превет", "привет"), ("сдесь", "здесь"), ("незнаю", "не знаю"), ("пожалуйсто", "пожалуйста"), ("teh", "the"), ("recieve", "receive"), ("dont", "don't"), ("untill", "until"), ("ҳамчунн", "ҳамчун") };
    foreach (var (bad, good) in typos)
    {
        var lang = bad[0] < 128 ? "en" : bad.Any(c => "ҳҷқғӯӣ".Contains(c)) ? "tg" : "ru";
        var r = sp.Fix(bad, L[lang], lang, false);
        if (r == good) ok++; else Console.WriteLine($"  miss: {bad} → {r ?? "-"} (expected {good})");
    }
    Check($"common typos fixed: {ok}/{typos.Length}", ok >= typos.Length - 1);
}

Console.WriteLine("\n=== Mid-word answers ===");
{
    Check("chunks", PhraseText.FirstChunk("друзья, как дела?") == ("друзья", ", как дела?") && PhraseText.FirstChunk(", как дела?") == (",", " как дела?")
                    && PhraseText.FirstChunk(" как дела?") == (" как", " дела?") && PhraseText.FirstChunk("?") == ("?", "") && PhraseText.FirstChunk(" кто-то пришёл") == (" кто-то", " пришёл"));
    Func<string, bool> known = w => models.Any(m => m.Frequency(w) != null);
    Check("ending", PhraseText.CompleteWord("зья, мы рады", "дру", "дорогие ", known) == "друзья, мы рады");
    Check("whole word", PhraseText.CompleteWord("друзья! Сегодня", "дру", "дорогие ", known) == "друзья!");
    Check("echo of context", PhraseText.CompleteWord("дорогие друзья", "дру", "дорогие ", known) == "друзья");
    Check("word already complete", PhraseText.CompleteWord(" и улицы", "дороги", "все ", known) == "дороги и улицы");
    Check("capital kept", PhraseText.CompleteWord("зья", "Дру", "", known) == "Друзья");
    Check("Tajik letters", PhraseText.CompleteWord("тоҷикистон", "точ", "", known) == "тоҷикистон");
    Check("nonsense rejected", PhraseText.CompleteWord("ыыыы", "дру", "", known) == null);
}

// ---------- model answer cleaning ----------
Console.WriteLine("\n=== Phrase cleaning ===");
{
    var ctx = "Завтра утром мы поедем на съёмки и ";
    Check("plain", PhraseText.Clean("снимем репортаж о заводе.", ctx) == "снимем репортаж о заводе.");
    Check("quotes stripped", PhraseText.Clean("«снимем репортаж»", ctx) == "снимем репортаж");
    Check("echo removed", PhraseText.Clean("съёмки и снимем репортаж", ctx) == "снимем репортаж");
    Check("long cut to 7 words", PhraseText.Clean("а б в г д е ж з и к", ctx)!.Split(' ').Length == 7);
    Check("markup rejected", PhraseText.Clean("**Конечно!** вот", ctx) == null);
    Check("empty rejected", PhraseText.Clean("\n", ctx) == null);
}

Console.WriteLine("\n=== Built-in word pairs ===");
{
    // ranking logic, independent of the shipped data
    var ms = new MemoryStream();
    using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
    using (var w = new StreamWriter(gz)) w.Write("может\tбыть 8.4 покажешь 7.6\n");
    ms.Position = 0;
    var comp = new Completer(det);
    var plain = comp.Complete("пок", L["ru"], "ru", 4, "может").Select(c => c.Word).ToList();
    comp.Builtin["ru"] = Bigrams.Load("ru", ms);
    var withPairs = comp.Complete("пок", L["ru"], "ru", 4, "может").Select(c => c.Word).ToList();
    Check($"pair lifts the usual next word: [{string.Join(",", plain)}] → [{string.Join(",", withPairs)}]", withPairs.FirstOrDefault() == "покажешь");
    var other = comp.Complete("пок", L["ru"], "ru", 4, "мы").Select(c => c.Word).ToList();
    Check($"no pair → dictionary order: [{string.Join(",", other)}]", other.SequenceEqual(comp.Complete("пок", L["ru"], "ru", 4, null).Select(c => c.Word)));

    // the shipped data (if this build has it)
    foreach (var lang in new[] { "ru", "en", "tg" })
    {
        var bg = Bigrams.LoadEmbedded(lang);
        if (bg == null) { Console.WriteLine($"  [{lang}] no built-in pairs in this build"); continue; }
        var c2 = new Completer(det); c2.Builtin[lang] = bg;
        var probes = lang switch
        {
            "ru" => new[] { ("может", "пок"), ("добрый", "дне"), ("большое", "спа"), ("как", "дел") },
            "en" => new[] { ("thank", "yo"), ("good", "mor"), ("let", "kno"), ("in", "ord") },
            _ => new[] { ("ҷумҳурии", "тоҷ"), ("рӯзи", "хуш"), ("дар", "бор"), ("ба", "ман") },
        };
        var lay = L[lang];
        Console.WriteLine($"  [{lang}] {bg.Count} words with pairs: " + string.Join("  ", probes.Select(p =>
            $"{p.Item1} {p.Item2}… → {string.Join("/", c2.Complete(p.Item2, lay, lang, 3, p.Item1).Select(x => x.Word))}")));
        Check($"[{lang}] pairs loaded", bg.Count > 1000);
    }
}

Console.WriteLine(failures == 0 ? "\nQUALITY OK" : $"\nQUALITY FAILED ({failures})");
return failures == 0 ? 0 : 1;
