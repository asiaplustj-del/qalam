using System.Runtime.InteropServices;

namespace Qalam.Platform.Mac;

internal static class Native
{
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const string AS = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string ObjC = "/usr/lib/libobjc.dylib";

    public const uint kCGSessionEventTap = 1, kCGHIDEventTap = 0, kCGHeadInsertEventTap = 0, kCGEventTapOptionDefault = 0;
    public const int kCGEventLeftMouseDown = 1, kCGEventRightMouseDown = 3, kCGEventKeyDown = 10, kCGEventKeyUp = 11,
        kCGEventFlagsChanged = 12, kCGEventOtherMouseDown = 25;
    public const uint kCGEventTapDisabledByTimeout = 0xFFFFFFFE, kCGEventTapDisabledByUserInput = 0xFFFFFFFF;
    public const int kCGKeyboardEventKeycode = 9, kCGEventSourceUserData = 42;
    public const ulong kCGEventFlagMaskAlphaShift = 0x10000, kCGEventFlagMaskShift = 0x20000, kCGEventFlagMaskControl = 0x40000,
        kCGEventFlagMaskAlternate = 0x80000, kCGEventFlagMaskCommand = 0x100000;
    public const ushort kUCKeyActionDisplay = 3;
    public const uint kUCKeyTranslateNoDeadKeysMask = 1;

    public delegate IntPtr CGEventTapCallBack(IntPtr proxy, uint type, IntPtr evt, IntPtr refcon);

    [StructLayout(LayoutKind.Sequential)] public struct CFRange { public long location, length; }

    [DllImport(CG)] public static extern IntPtr CGEventTapCreate(uint tap, uint place, uint options, ulong mask, CGEventTapCallBack cb, IntPtr refcon);
    [DllImport(CG)] public static extern void CGEventTapEnable(IntPtr tap, bool enable);
    [DllImport(CG)] public static extern long CGEventGetIntegerValueField(IntPtr evt, int field);
    [DllImport(CG)] public static extern void CGEventSetIntegerValueField(IntPtr evt, int field, long value);
    [DllImport(CG)] public static extern ulong CGEventGetFlags(IntPtr evt);
    [DllImport(CG)] public static extern void CGEventSetFlags(IntPtr evt, ulong flags);
    [DllImport(CG)] public static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort keycode, bool down);
    [DllImport(CG, CharSet = CharSet.Unicode)] public static extern void CGEventKeyboardSetUnicodeString(IntPtr evt, nuint len, char[] chars);
    [DllImport(CG)] public static extern void CGEventPost(uint tap, IntPtr evt);
    [DllImport(CG)] public static extern bool CGPreflightListenEventAccess();
    [DllImport(CG)] public static extern bool CGRequestListenEventAccess();
    [DllImport(CG)] public static extern bool CGRequestPostEventAccess();

    [DllImport(CF)] public static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr alloc, IntPtr port, long order);
    [DllImport(CF)] public static extern IntPtr CFRunLoopGetCurrent();
    [DllImport(CF)] public static extern void CFRunLoopAddSource(IntPtr rl, IntPtr src, IntPtr mode);
    [DllImport(CF)] public static extern void CFRunLoopRun();
    [DllImport(CF)] public static extern void CFRunLoopStop(IntPtr rl);
    [DllImport(CF)] public static extern void CFRelease(IntPtr obj);
    [DllImport(CF)] public static extern IntPtr CFRetain(IntPtr obj);
    [DllImport(CF)] public static extern long CFArrayGetCount(IntPtr arr);
    [DllImport(CF)] public static extern IntPtr CFArrayGetValueAtIndex(IntPtr arr, long i);
    [DllImport(CF)] public static extern long CFStringGetLength(IntPtr s);
    [DllImport(CF, CharSet = CharSet.Unicode)] public static extern void CFStringGetCharacters(IntPtr s, CFRange range, [Out] char[] buf);
    [DllImport(CF)] public static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CF)] public static extern bool CFBooleanGetValue(IntPtr b);

    [DllImport(Carbon)] public static extern IntPtr TISCreateInputSourceList(IntPtr props, bool includeAll);
    [DllImport(Carbon)] public static extern IntPtr TISCopyCurrentKeyboardInputSource();
    [DllImport(Carbon)] public static extern IntPtr TISGetInputSourceProperty(IntPtr src, IntPtr key);
    [DllImport(Carbon)] public static extern int TISSelectInputSource(IntPtr src);
    [DllImport(Carbon)] public static extern byte LMGetKbdType();
    [DllImport(Carbon, CharSet = CharSet.Unicode)]
    public static extern int UCKeyTranslate(IntPtr layout, ushort code, ushort action, uint mods, uint kbdType, uint options,
                                            ref uint deadKeyState, nuint maxLen, out nuint actualLen, [Out] char[] buf);

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")] public static extern IntPtr IOPSCopyPowerSourcesInfo();
    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")] public static extern IntPtr IOPSGetProvidingPowerSourceType(IntPtr snapshot);

    [DllImport(AS)] public static extern bool AXIsProcessTrusted();
    [DllImport(AS)] public static extern IntPtr AXUIElementCreateSystemWide();
    [DllImport(AS)] public static extern IntPtr AXUIElementCreateApplication(int pid);
    [DllImport(AS)] public static extern int AXUIElementSetMessagingTimeout(IntPtr element, float seconds);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] public static extern int objc_msgSend_int(IntPtr target, IntPtr selector);
    [DllImport(AS)] public static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);
    [DllImport(AS)] public static extern int AXUIElementCopyParameterizedAttributeValue(IntPtr element, IntPtr attribute, IntPtr parameter, out IntPtr value);
    [DllImport(AS)] public static extern bool AXValueGetValue(IntPtr value, int type, out CGRect rect);
    [DllImport(AS)] public static extern bool AXValueGetValue(IntPtr value, int type, out CFRange range);
    [DllImport(AS)] public static extern IntPtr AXValueCreate(int type, ref CFRange range);
    public const int kAXValueCGRectType = 3, kAXValueCFRangeType = 4;
    [StructLayout(LayoutKind.Sequential)] public struct CGRect { public double X, Y, W, H; }
    [DllImport(CF)] public static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string s, uint encoding);
    public static IntPtr CFStr(string s) => CFStringCreateWithCString(IntPtr.Zero, s, 0x08000100);

    [DllImport(ObjC)] public static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] public static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] public static extern IntPtr objc_msgSend(IntPtr target, IntPtr selector);

    // ---- exported CFStringRef constants (global variables, read via dlsym) ----
    private static IntPtr Sym(string lib, string name)
    {
        var h = NativeLibrary.Load(lib);
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(h, name));
    }
    public static readonly IntPtr CommonModes = Sym(CF, "kCFRunLoopCommonModes");
    public static readonly IntPtr kTISPropertyInputSourceCategory = Sym(Carbon, "kTISPropertyInputSourceCategory");
    public static readonly IntPtr kTISPropertyInputSourceIsSelectCapable = Sym(Carbon, "kTISPropertyInputSourceIsSelectCapable");
    public static readonly IntPtr kTISPropertyUnicodeKeyLayoutData = Sym(Carbon, "kTISPropertyUnicodeKeyLayoutData");
    public static readonly IntPtr kTISPropertyInputSourceID = Sym(Carbon, "kTISPropertyInputSourceID");
    public static readonly IntPtr kTISPropertyLocalizedName = Sym(Carbon, "kTISPropertyLocalizedName");
    public static readonly IntPtr kTISPropertyInputSourceLanguages = Sym(Carbon, "kTISPropertyInputSourceLanguages");
}
