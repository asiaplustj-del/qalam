using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Qalam.Core;
using static Qalam.Platform.Mac.Native;

namespace Qalam.Platform.Mac;

[SupportedOSPlatform("macos")]
public sealed class MacPlatform : IKeyboardPlatform
{
    public string Name => "macOS";
    public Func<KeyInput, bool>? KeyHandler { get; set; }
    public event Action? MouseClicked;

    private Thread? _tapThread;
    private IntPtr _tap, _runLoop;
    private CGEventTapCallBack? _callback;            // keep alive
    private readonly BlockingCollection<Action> _work = new();
    private Thread? _worker;
    private const long Marker = 0x51414C4D;
    private ulong _prevFlags;

    private KeyboardLayout? _pendingLayout;
    private DateTime _pendingUntil;
    private volatile string? _currentSourceId;

    /// <summary>
    /// Text Input Sources APIs must run on the main thread on modern macOS.
    /// The app sets this to its UI dispatcher (async post).
    /// </summary>
    public Action<Action> PostToMain { get; set; } = a => a();

    /// <summary>Call periodically on the MAIN thread (e.g. every 150 ms) to track the active layout.</summary>
    public void PollCurrentLayout()
    {
        var cur = TISCopyCurrentKeyboardInputSource();
        if (cur == IntPtr.Zero) return;
        try { _currentSourceId = CFString(TISGetInputSourceProperty(cur, kTISPropertyInputSourceID)); }
        finally { CFRelease(cur); }
    }

    // macOS virtual key codes (ANSI positions) → physical key id
    private static readonly Dictionary<long, char> CodeToKey = new()
    {
        [50] = '`', [18] = '1', [19] = '2', [20] = '3', [21] = '4', [23] = '5', [22] = '6', [26] = '7', [28] = '8', [25] = '9', [29] = '0', [27] = '-', [24] = '=',
        [12] = 'q', [13] = 'w', [14] = 'e', [15] = 'r', [17] = 't', [16] = 'y', [32] = 'u', [34] = 'i', [31] = 'o', [35] = 'p', [33] = '[', [30] = ']', [42] = '\\',
        [0] = 'a', [1] = 's', [2] = 'd', [3] = 'f', [5] = 'g', [4] = 'h', [38] = 'j', [40] = 'k', [37] = 'l', [41] = ';', [39] = '\'',
        [6] = 'z', [7] = 'x', [8] = 'c', [9] = 'v', [11] = 'b', [45] = 'n', [46] = 'm', [43] = ',', [47] = '.', [44] = '/',
    };

    private static readonly Dictionary<long, string> KeyNames = new()
    {
        [122] = "F1", [120] = "F2", [99] = "F3", [118] = "F4", [96] = "F5", [97] = "F6", [98] = "F7", [100] = "F8",
        [101] = "F9", [109] = "F10", [103] = "F11", [111] = "F12", [105] = "F13", [107] = "F14", [113] = "F15",
        [106] = "F16", [64] = "F17", [79] = "F18", [80] = "F19", [90] = "F20",
        [117] = "Delete", [115] = "Home", [119] = "End", [116] = "PageUp", [121] = "PageDown", [114] = "Help",
        [123] = "Left", [124] = "Right", [125] = "Down", [126] = "Up",
        [53] = "Escape", [49] = "Space", [36] = "Enter", [76] = "NumEnter", [48] = "Tab", [51] = "Backspace",
        [82] = "Num0", [83] = "Num1", [84] = "Num2", [85] = "Num3", [86] = "Num4", [87] = "Num5", [88] = "Num6",
        [89] = "Num7", [91] = "Num8", [92] = "Num9", [65] = "Num.", [67] = "Num*", [69] = "Num+", [78] = "Num-", [75] = "Num/",
        [71] = "Clear", [10] = "§",
    };

    public bool HasPermissions()
    {
        try { return AXIsProcessTrusted() && CGPreflightListenEventAccess(); } catch { return false; }
    }

    public void RequestPermissions()
    {
        try { CGRequestListenEventAccess(); } catch { }
        try { CGRequestPostEventAccess(); } catch { }
        if (!AXIsProcessTrusted())
            Process.Start("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility");
    }

    public void Start()
    {
        _worker = new Thread(() => { foreach (var a in _work.GetConsumingEnumerable()) { try { a(); } catch { } } })
        { IsBackground = true, Name = "Qalam input worker" };
        _worker.Start();

        var ready = new ManualResetEventSlim();
        _tapThread = new Thread(() =>
        {
            _callback = TapCallback;
            ulong mask = (1UL << kCGEventKeyDown) | (1UL << kCGEventKeyUp) | (1UL << kCGEventFlagsChanged)
                       | (1UL << kCGEventLeftMouseDown) | (1UL << kCGEventRightMouseDown) | (1UL << kCGEventOtherMouseDown);
            _tap = CGEventTapCreate(kCGSessionEventTap, kCGHeadInsertEventTap, kCGEventTapOptionDefault, mask, _callback, IntPtr.Zero);
            if (_tap == IntPtr.Zero) { ready.Set(); return; }   // no permission
            var src = CFMachPortCreateRunLoopSource(IntPtr.Zero, _tap, 0);
            _runLoop = CFRunLoopGetCurrent();
            CFRunLoopAddSource(_runLoop, src, CommonModes);
            CGEventTapEnable(_tap, true);
            ready.Set();
            CFRunLoopRun();
        }) { IsBackground = true, Name = "Qalam event tap" };
        _tapThread.Start();
        ready.Wait(3000);
    }

    public bool TapActive => _tap != IntPtr.Zero;

    private IntPtr TapCallback(IntPtr proxy, uint type, IntPtr evt, IntPtr refcon)
    {
        try
        {
            if (type == kCGEventTapDisabledByTimeout || type == kCGEventTapDisabledByUserInput)
            {
                if (_tap != IntPtr.Zero) CGEventTapEnable(_tap, true);
                return evt;
            }
            if (type is kCGEventLeftMouseDown or kCGEventRightMouseDown or kCGEventOtherMouseDown)
            {
                MouseClicked?.Invoke();
                return evt;
            }
            if (CGEventGetIntegerValueField(evt, kCGEventSourceUserData) == Marker) return evt; // ours
            var handler = KeyHandler;
            if (handler == null) return evt;

            long code = CGEventGetIntegerValueField(evt, kCGKeyboardEventKeycode);
            ulong flags = CGEventGetFlags(evt);
            bool shift = (flags & kCGEventFlagMaskShift) != 0, ctrl = (flags & kCGEventFlagMaskControl) != 0,
                 alt = (flags & kCGEventFlagMaskAlternate) != 0, cmd = (flags & kCGEventFlagMaskCommand) != 0,
                 caps = (flags & kCGEventFlagMaskAlphaShift) != 0;

            if (type == kCGEventFlagsChanged)
            {
                // which modifier changed and was it pressed or released?
                SpecialKey sp; ulong bit; bool right = false;
                switch (code)
                {
                    case 56: sp = SpecialKey.Shift; bit = kCGEventFlagMaskShift; break;
                    case 60: sp = SpecialKey.Shift; bit = kCGEventFlagMaskShift; right = true; break;
                    case 59: sp = SpecialKey.Ctrl; bit = kCGEventFlagMaskControl; break;
                    case 62: sp = SpecialKey.Ctrl; bit = kCGEventFlagMaskControl; right = true; break;
                    case 58: sp = SpecialKey.Alt; bit = kCGEventFlagMaskAlternate; break;
                    case 61: sp = SpecialKey.Alt; bit = kCGEventFlagMaskAlternate; right = true; break;
                    case 55: sp = SpecialKey.Meta; bit = kCGEventFlagMaskCommand; break;
                    case 54: sp = SpecialKey.Meta; bit = kCGEventFlagMaskCommand; right = true; break;
                    default: _prevFlags = flags; return evt;   // caps lock, fn…
                }
                bool down = (flags & bit) != 0;
                _prevFlags = flags;
                handler(new KeyInput(down, sp, '\0', RightSide: right));
                return evt;
            }

            bool isDown = type == kCGEventKeyDown;
            SpecialKey special = code switch
            {
                49 => SpecialKey.Space,
                36 => SpecialKey.Enter,
                76 => SpecialKey.Navigation,    // keypad Enter
                48 => SpecialKey.Tab,
                51 => SpecialKey.Backspace,
                113 => SpecialKey.Pause,        // F15 on Apple keyboards ≈ Pause on PC keyboards
                53 or 117 or 115 or 119 or 116 or 121 or (>= 123 and <= 126) => SpecialKey.Navigation,
                122 or 120 or 99 or 118 or 96 or 97 or 98 or 100 or 101 or 109 or 103 or 111 => SpecialKey.Navigation, // F1..F12
                _ => SpecialKey.Other
            };
            char key = '\0';
            if (special == SpecialKey.Other && CodeToKey.TryGetValue(code, out var k)) { key = k; special = SpecialKey.None; }
            // Option on Mac types special characters; we treat Option+key as a shortcut (context reset)
            string? name = key != '\0' ? char.ToUpperInvariant(key).ToString() : KeyNames.GetValueOrDefault(code, $"Key{code}");
            var input = new KeyInput(isDown, special, key, shift, caps, ctrl, alt, cmd, KeyName: name);
            return handler(input) ? IntPtr.Zero : evt;
        }
        catch { return evt; }
    }

    // ------------------------------------------------------------------ layouts
    public IReadOnlyList<KeyboardLayout> GetLayouts()
    {
        var list = new List<KeyboardLayout>();
        var arr = TISCreateInputSourceList(IntPtr.Zero, false);   // enabled sources only
        if (arr == IntPtr.Zero) return list;
        long n = CFArrayGetCount(arr);
        for (long i = 0; i < n; i++)
        {
            var src = CFArrayGetValueAtIndex(arr, i);
            var cat = CFString(TISGetInputSourceProperty(src, kTISPropertyInputSourceCategory));
            if (cat != "TISCategoryKeyboardInputSource") continue;
            var selectable = TISGetInputSourceProperty(src, kTISPropertyInputSourceIsSelectCapable);
            if (selectable != IntPtr.Zero && !CFBooleanGetValue(selectable)) continue;
            var layoutData = TISGetInputSourceProperty(src, kTISPropertyUnicodeKeyLayoutData);
            if (layoutData == IntPtr.Zero) continue;   // input methods (CJK) have no key layout
            var id = CFString(TISGetInputSourceProperty(src, kTISPropertyInputSourceID)) ?? $"src{i}";
            var name = CFString(TISGetInputSourceProperty(src, kTISPropertyLocalizedName)) ?? id;
            string lang = "xx";
            var langs = TISGetInputSourceProperty(src, kTISPropertyInputSourceLanguages);
            if (langs != IntPtr.Zero && CFArrayGetCount(langs) > 0) lang = CFString(CFArrayGetValueAtIndex(langs, 0)) ?? "xx";
            if (lang.Contains('-')) lang = lang[..lang.IndexOf('-')];
            var map = new Dictionary<char, (char, char)>();
            var ptr = CFDataGetBytePtr(layoutData);
            foreach (var (code, physical) in CodeToKey)
                map[physical] = (Translate(ptr, (ushort)code, 0), Translate(ptr, (ushort)code, 2 /* shift */));
            CFRetain(src);
            list.Add(new KeyboardLayout(id, lang, name, map) { NativeHandle = src });
        }
        CFRelease(arr);
        return list;
    }

    private static char Translate(IntPtr layout, ushort code, uint mods)
    {
        uint dead = 0;
        var buf = new char[4];
        int r = UCKeyTranslate(layout, code, kUCKeyActionDisplay, mods, LMGetKbdType(), kUCKeyTranslateNoDeadKeysMask, ref dead, 4, out var len, buf);
        return r == 0 && len == 1 && !char.IsControl(buf[0]) ? buf[0] : '\0';
    }

    public KeyboardLayout? GetCurrentLayout(IReadOnlyList<KeyboardLayout> known)
    {
        if (_pendingLayout != null && DateTime.UtcNow < _pendingUntil) return _pendingLayout;
        _pendingLayout = null;
        var id = _currentSourceId;
        return id == null ? null : known.FirstOrDefault(l => l.Id == id);
    }

    public void SwitchLayout(KeyboardLayout layout)
    {
        if (layout.NativeHandle is not IntPtr src) return;
        _pendingLayout = layout; _pendingUntil = DateTime.UtcNow.AddMilliseconds(400);
        _currentSourceId = layout.Id;
        PostToMain(() => { TISSelectInputSource(src); PollCurrentLayout(); });
    }

    // ------------------------------------------------------------------ typing
    public void Replace(int erase, string text, Action? then = null)
    {
        _work.Add(() =>
        {
            for (int i = 0; i < erase; i++) PostKey(51);
            var chunk = new System.Text.StringBuilder();
            void Flush() { if (chunk.Length > 0) { PostText(chunk.ToString()); chunk.Clear(); } }
            foreach (var ch in text)
            {
                if (ch == '\n') { Flush(); PostKey(36); }
                else if (ch == '\t') { Flush(); PostKey(48); }
                else { chunk.Append(ch); if (chunk.Length >= 16) Flush(); }
            }
            Flush();
            then?.Invoke();
        });
    }

    public void ReplayKey(SpecialKey key)
    {
        ushort code = key switch { SpecialKey.Space => 49, SpecialKey.Enter => 36, SpecialKey.Tab => 48, _ => ushort.MaxValue };
        if (code == ushort.MaxValue) return;
        _work.Add(() => PostKey(code));
    }

    private static void PostKey(ushort code, ulong flags = 0)
    {
        foreach (var down in new[] { true, false })
        {
            var e = CGEventCreateKeyboardEvent(IntPtr.Zero, code, down);
            CGEventSetIntegerValueField(e, kCGEventSourceUserData, Marker);
            CGEventSetFlags(e, flags);
            CGEventPost(kCGHIDEventTap, e);
            CFRelease(e);
        }
        Thread.Sleep(2);
    }

    private static void PostText(string s)
    {
        var chars = s.ToCharArray();
        foreach (var down in new[] { true, false })
        {
            var e = CGEventCreateKeyboardEvent(IntPtr.Zero, 0, down);
            CGEventSetIntegerValueField(e, kCGEventSourceUserData, Marker);
            CGEventSetFlags(e, 0);
            CGEventKeyboardSetUnicodeString(e, (nuint)chars.Length, chars);
            CGEventPost(kCGHIDEventTap, e);
            CFRelease(e);
        }
        Thread.Sleep(2);
    }

    // ------------------------------------------------------------------ selection
    public void ConvertSelection(Func<string, string?> convert)
    {
        _work.Add(() =>
        {
            Thread.Sleep(80);
            var saved = Pasteboard("pbpaste", null);
            Pasteboard("pbcopy", "");
            PostKey(8 /* c */, kCGEventFlagMaskCommand);
            string? text = null;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 500)
            {
                Thread.Sleep(40);
                text = Pasteboard("pbpaste", null);
                if (!string.IsNullOrEmpty(text)) break;
            }
            if (string.IsNullOrEmpty(text)) { if (saved != null) Pasteboard("pbcopy", saved); return; }
            var result = convert(text);
            if (result == null) { if (saved != null) Pasteboard("pbcopy", saved); return; }
            Pasteboard("pbcopy", result);
            PostKey(9 /* v */, kCGEventFlagMaskCommand);
            Thread.Sleep(300);
            if (saved != null) Pasteboard("pbcopy", saved);
        });
    }

    private static string? Pasteboard(string tool, string? input)
    {
        try
        {
            var psi = new ProcessStartInfo(tool) { RedirectStandardInput = input != null, RedirectStandardOutput = input == null, UseShellExecute = false };
            psi.Environment["LANG"] = "en_US.UTF-8";
            if (input == null) psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
            else psi.StandardInputEncoding = System.Text.Encoding.UTF8;
            using var p = Process.Start(psi)!;
            if (input != null) { p.StandardInput.Write(input); p.StandardInput.Close(); p.WaitForExit(1000); return null; }
            var s = p.StandardOutput.ReadToEnd(); p.WaitForExit(1000); return s;
        }
        catch { return null; }
    }

    public string? ForegroundApp()
    {
        try
        {
            var ws = objc_msgSend(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
            var app = objc_msgSend(ws, sel_registerName("frontmostApplication"));
            var name = objc_msgSend(app, sel_registerName("localizedName"));
            var utf8 = objc_msgSend(name, sel_registerName("UTF8String"));
            return Marshal.PtrToStringUTF8(utf8);
        }
        catch { return null; }
    }

    private static readonly IntPtr AXFocused = CFStr("AXFocusedUIElement");
    private static readonly IntPtr AXSelRange = CFStr("AXSelectedTextRange");
    private static readonly IntPtr AXBoundsForRange = CFStr("AXBoundsForRange");

    /// <summary>Caret rectangle in POINTS (macOS global coordinates, top-left origin).</summary>
    public (int X, int Y, int W, int H)? GetCaretRect()
    {
        IntPtr sys = IntPtr.Zero, focused = IntPtr.Zero, rangeVal = IntPtr.Zero, bounds = IntPtr.Zero;
        try
        {
            sys = AXUIElementCreateSystemWide();
            if (AXUIElementCopyAttributeValue(sys, AXFocused, out focused) != 0 || focused == IntPtr.Zero) return null;
            if (AXUIElementCopyAttributeValue(focused, AXSelRange, out rangeVal) != 0 || rangeVal == IntPtr.Zero) return null;
            if (AXUIElementCopyParameterizedAttributeValue(focused, AXBoundsForRange, rangeVal, out bounds) == 0
                && bounds != IntPtr.Zero && AXValueGetValue(bounds, kAXValueCGRectType, out CGRect r) && r.H > 0)
                return ((int)r.X, (int)r.Y, Math.Max((int)r.W, 1), (int)r.H);
            // empty selection: some apps only give bounds for a real character → take the previous one
            if (AXValueGetValue(rangeVal, kAXValueCFRangeType, out CFRange range) && range.location > 0)
            {
                var prev = new CFRange { location = range.location - 1, length = 1 };
                var pv = AXValueCreate(kAXValueCFRangeType, ref prev);
                if (bounds != IntPtr.Zero) { CFRelease(bounds); bounds = IntPtr.Zero; }
                CGRect r2 = default;
                bool ok = AXUIElementCopyParameterizedAttributeValue(focused, AXBoundsForRange, pv, out bounds) == 0
                          && bounds != IntPtr.Zero && AXValueGetValue(bounds, kAXValueCGRectType, out r2) && r2.H > 0;
                CFRelease(pv);
                if (ok) return ((int)(r2.X + r2.W), (int)r2.Y, 1, (int)r2.H);
            }
            return null;
        }
        catch { return null; }
        finally
        {
            if (bounds != IntPtr.Zero) CFRelease(bounds);
            if (rangeVal != IntPtr.Zero) CFRelease(rangeVal);
            if (focused != IntPtr.Zero) CFRelease(focused);
            if (sys != IntPtr.Zero) CFRelease(sys);
        }
    }

    public void SetStartWithSystem(bool enabled)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "LaunchAgents");
            var plist = Path.Combine(dir, "tj.asiaplus.qalam.plist");
            if (!enabled) { if (File.Exists(plist)) File.Delete(plist); return; }
            Directory.CreateDirectory(dir);
            // inside an .app bundle ProcessPath is .../Qalam.app/Contents/MacOS/Qalam
            var exe = Environment.ProcessPath ?? "";
            File.WriteAllText(plist, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0"><dict>
                  <key>Label</key><string>tj.asiaplus.qalam</string>
                  <key>ProgramArguments</key><array><string>{System.Security.SecurityElement.Escape(exe)}</string><string>--tray</string></array>
                  <key>RunAtLoad</key><true/>
                </dict></plist>
                """);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_runLoop != IntPtr.Zero) CFRunLoopStop(_runLoop);
        _work.CompleteAdding();
    }

    private static string? CFString(IntPtr s)
    {
        if (s == IntPtr.Zero) return null;
        long len = CFStringGetLength(s);
        var buf = new char[len];
        CFStringGetCharacters(s, new CFRange { location = 0, length = len }, buf);
        return new string(buf);
    }
}
