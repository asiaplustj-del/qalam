using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Qalam.Core;
using static Qalam.Platform.Windows.Native;

namespace Qalam.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform : IKeyboardPlatform
{
    public string Name => "Windows";
    public Func<KeyInput, bool>? KeyHandler { get; set; }
    public event Action? MouseClicked;

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _kbHook, _mouseHook;
    private HookProc? _kbProc, _mouseProc;          // keep delegates alive
    private readonly BlockingCollection<Action> _work = new();
    private Thread? _worker;
    private const nuint Marker = 0x51414C4D;        // "QALM" in dwExtraInfo of our injected input

    private KeyboardLayout? _pendingLayout;
    private DateTime _pendingUntil;

    // scan code (set 1) → physical key id (US QWERTY char)
    private static readonly Dictionary<uint, char> ScanToKey = BuildScanMap();
    private static Dictionary<uint, char> BuildScanMap()
    {
        var m = new Dictionary<uint, char> { [0x29] = '`', [0x0C] = '-', [0x0D] = '=', [0x1A] = '[', [0x1B] = ']', [0x2B] = '\\',
            [0x27] = ';', [0x28] = '\'', [0x33] = ',', [0x34] = '.', [0x35] = '/' };
        const string digits = "1234567890"; for (int i = 0; i < 10; i++) m[(uint)(0x02 + i)] = digits[i];
        const string r1 = "qwertyuiop"; for (int i = 0; i < r1.Length; i++) m[(uint)(0x10 + i)] = r1[i];
        const string r2 = "asdfghjkl"; for (int i = 0; i < r2.Length; i++) m[(uint)(0x1E + i)] = r2[i];
        const string r3 = "zxcvbnm"; for (int i = 0; i < r3.Length; i++) m[(uint)(0x2C + i)] = r3[i];
        return m;
    }
    private static readonly Dictionary<char, uint> KeyToScan = ScanToKey.ToDictionary(kv => kv.Value, kv => kv.Key);

    public bool HasPermissions() => true;
    public void RequestPermissions() { }

    public void Start()
    {
        _worker = new Thread(() => { foreach (var a in _work.GetConsumingEnumerable()) { try { a(); } catch { } } })
        { IsBackground = true, Name = "Qalam input worker" };
        _worker.Start();

        var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            _kbProc = KeyboardProc; _mouseProc = MouseProc;
            var hMod = GetModuleHandle(null);
            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
            ready.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg); DispatchMessage(ref msg);
            }
            if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        }) { IsBackground = true, Name = "Qalam keyboard hook" };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        ready.Wait(3000);
    }

    // ------------------------------------------------------------------ hooks
    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (info.flags & LLKHF_INJECTED) != 0;
            if (!injected && KeyHandler is { } handler)
            {
                int msg = (int)wParam;
                bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
                var input = Translate(info, down);
                if (handler(input)) return (IntPtr)1;
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            MouseClicked?.Invoke();
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static KeyInput Translate(KBDLLHOOKSTRUCT info, bool down)
    {
        uint vk = info.vkCode;
        bool extended = (info.flags & LLKHF_EXTENDED) != 0;
        bool shift = Down(VK_SHIFT), ctrl = Down(VK_CONTROL), alt = Down(VK_MENU), win = Down(VK_LWIN) || Down(VK_RWIN);
        bool caps = (GetKeyState(VK_CAPITAL) & 1) != 0;

        SpecialKey sp = vk switch
        {
            0x20 => SpecialKey.Space,
            0x0D => extended ? SpecialKey.Navigation : SpecialKey.Enter, // numpad Enter: treat as navigation
            0x09 => SpecialKey.Tab,
            0x08 => SpecialKey.Backspace,
            0x13 or 0x03 => SpecialKey.Pause,                       // Pause, Ctrl+Pause (VK_CANCEL)
            0x10 or 0xA0 or 0xA1 => SpecialKey.Shift,
            0x11 or 0xA2 or 0xA3 => SpecialKey.Ctrl,
            0x12 or 0xA4 or 0xA5 => SpecialKey.Alt,
            0x5B or 0x5C => SpecialKey.Meta,
            0x1B or (>= 0x21 and <= 0x28) or 0x2D or 0x2E or (>= 0x70 and <= 0x87) => SpecialKey.Navigation,
            _ => SpecialKey.Other
        };
        char key = '\0';
        if (sp == SpecialKey.Other && !extended && ScanToKey.TryGetValue(info.scanCode, out var k)) { key = k; sp = SpecialKey.None; }
        bool right = vk is 0xA1 or 0xA3 or 0xA5 || (extended && vk is 0x11 or 0x12);
        // modifier flags exclude the key itself
        return new KeyInput(down, sp, key,
            Shift: shift && sp != SpecialKey.Shift, Caps: caps,
            Ctrl: ctrl && sp != SpecialKey.Ctrl, Alt: alt && sp != SpecialKey.Alt, Meta: win && sp != SpecialKey.Meta,
            RightSide: right);
    }

    // ------------------------------------------------------------------ layouts
    private List<KeyboardLayout>? _layoutsCache;

    public IReadOnlyList<KeyboardLayout> GetLayouts()
    {
        int n = GetKeyboardLayoutList(0, null);
        var hkls = new IntPtr[Math.Max(n, 1)];
        n = GetKeyboardLayoutList(hkls.Length, hkls);
        var list = new List<KeyboardLayout>();
        foreach (var hkl in hkls.Take(n))
        {
            int langId = (int)((long)hkl & 0xFFFF);
            string lang;
            try { lang = CultureInfo.GetCultureInfo(langId).TwoLetterISOLanguageName; } catch { lang = "xx"; }
            string display;
            try { display = CultureInfo.GetCultureInfo(langId).NativeName; } catch { display = lang.ToUpperInvariant(); }
            var map = new Dictionary<char, (char, char)>();
            var state = new byte[256];
            var buf = new char[8];
            foreach (var (scan, physical) in ScanToKey)
            {
                uint vk = MapVirtualKeyEx(scan, MAPVK_VSC_TO_VK, hkl);
                state[VK_SHIFT] = 0;
                char normal = ToChar(vk, scan, state, buf, hkl);
                state[VK_SHIFT] = 0x80;
                char shifted = ToChar(vk, scan, state, buf, hkl);
                state[VK_SHIFT] = 0;
                map[physical] = (normal, shifted);
            }
            list.Add(new KeyboardLayout($"{(long)hkl:X}", lang, display, map) { NativeHandle = hkl });
        }
        _layoutsCache = list;
        return list;
    }

    private static char ToChar(uint vk, uint scan, byte[] state, char[] buf, IntPtr hkl)
    {
        int r = ToUnicodeEx(vk, scan, state, buf, buf.Length, 4 /* don't change keyboard state */, hkl);
        if (r == 1 && !char.IsControl(buf[0])) return buf[0];
        if (r < 0) ToUnicodeEx(vk, scan, state, buf, buf.Length, 4, hkl); // flush dead key just in case
        return '\0';
    }

    public KeyboardLayout? GetCurrentLayout(IReadOnlyList<KeyboardLayout> known)
    {
        if (_pendingLayout != null && DateTime.UtcNow < _pendingUntil) return _pendingLayout;
        _pendingLayout = null;
        var hwnd = GetForegroundWindow();
        uint tid = GetWindowThreadProcessId(hwnd, out _);
        var hkl = GetKeyboardLayout(tid);
        var exact = known.FirstOrDefault(l => l.NativeHandle is IntPtr h && h == hkl);
        if (exact != null) return exact;
        int langId = (int)((long)hkl & 0xFFFF);
        string lang;
        try { lang = CultureInfo.GetCultureInfo(langId).TwoLetterISOLanguageName; } catch { return null; }
        return known.FirstOrDefault(l => l.Language == lang);
    }

    public void SwitchLayout(KeyboardLayout layout)
    {
        if (layout.NativeHandle is not IntPtr hkl) return;
        _pendingLayout = layout; _pendingUntil = DateTime.UtcNow.AddMilliseconds(400);
        _work.Add(() =>
        {
            var hwnd = GetForegroundWindow();
            PostMessage(hwnd, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
        });
    }

    // ------------------------------------------------------------------ typing
    public void Replace(int erase, string text, Action? then = null)
    {
        _work.Add(() =>
        {
            var inputs = new List<INPUT>();
            for (int i = 0; i < erase; i++) AddVk(inputs, VK_BACK);
            foreach (var ch in text)
            {
                if (ch == '\n') AddVk(inputs, VK_RETURN);
                else if (ch == '\t') AddVk(inputs, VK_TAB);
                else AddUnicode(inputs, ch);
            }
            Send(inputs);
            then?.Invoke();
        });
    }

    public void ReplayKey(SpecialKey key)
    {
        ushort vk = key switch { SpecialKey.Space => VK_SPACE, SpecialKey.Enter => VK_RETURN, SpecialKey.Tab => VK_TAB, _ => 0 };
        if (vk == 0) return;
        _work.Add(() => { var l = new List<INPUT>(); AddVk(l, vk); Send(l); });
    }

    private static void AddVk(List<INPUT> l, ushort vk, bool? onlyDown = null)
    {
        if (onlyDown != false) l.Add(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwExtraInfo = (IntPtr)Marker } } });
        if (onlyDown != true) l.Add(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP, dwExtraInfo = (IntPtr)Marker } } });
    }

    private static void AddUnicode(List<INPUT> l, char ch)
    {
        l.Add(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE, dwExtraInfo = (IntPtr)Marker } } });
        l.Add(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, dwExtraInfo = (IntPtr)Marker } } });
    }

    private static void Send(List<INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        var arr = inputs.ToArray();
        SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
    }

    // ------------------------------------------------------------------ selection
    public void ConvertSelection(Func<string, string?> convert)
    {
        _work.Add(() =>
        {
            // let the user release the hotkey, then make sure no modifier is logically pressed
            Thread.Sleep(60);
            var up = new List<INPUT>();
            foreach (var vk in new ushort[] { VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN })
                if (Down(vk)) AddVk(up, vk, onlyDown: false);
            Send(up);

            var saved = Clipboard.GetText();
            uint seq = GetClipboardSequenceNumber();
            var copy = new List<INPUT>();
            AddVk(copy, VK_CONTROL, true); AddVk(copy, (ushort)'C'); AddVk(copy, VK_CONTROL, false);
            Send(copy);
            var sw = Stopwatch.StartNew();
            while (GetClipboardSequenceNumber() == seq && sw.ElapsedMilliseconds < 400) Thread.Sleep(10);
            if (GetClipboardSequenceNumber() == seq) return;     // nothing selected
            var text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text)) return;
            var result = convert(text);
            if (result == null) { if (saved != null) Clipboard.SetText(saved); return; }
            Clipboard.SetText(result);
            var paste = new List<INPUT>();
            AddVk(paste, VK_CONTROL, true); AddVk(paste, (ushort)'V'); AddVk(paste, VK_CONTROL, false);
            Send(paste);
            Thread.Sleep(250);
            if (saved != null) Clipboard.SetText(saved);
        });
    }

    public string? ForegroundApp()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    public void SetStartWithSystem(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (enabled) key.SetValue("Qalam", $"\"{Environment.ProcessPath}\" --tray");
            else key.DeleteValue("Qalam", false);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _work.CompleteAdding();
    }
}

[SupportedOSPlatform("windows")]
internal static class Clipboard
{
    public static string? GetText()
    {
        if (!Open()) return null;
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            try { return Marshal.PtrToStringUni(p); } finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    public static void SetText(string text)
    {
        if (!Open()) return;
        try
        {
            EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            var p = GlobalLock(h);
            var chars = (text + "\0").ToCharArray();
            Marshal.Copy(chars, 0, p, chars.Length);
            GlobalUnlock(h);
            SetClipboardData(CF_UNICODETEXT, h);
        }
        finally { CloseClipboard(); }
    }

    private static bool Open()
    {
        for (int i = 0; i < 10; i++) { if (OpenClipboard(IntPtr.Zero)) return true; Thread.Sleep(15); }
        return false;
    }
}
