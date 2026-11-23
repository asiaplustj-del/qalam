using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Qalam.App;

/// <summary>
/// Puts an Avalonia window at an exact place on macOS through Cocoa itself (-[NSWindow setFrame:display:]),
/// without Avalonia's position bookkeeping, which applied a stale window height and lifted the hint a line up.
/// Coordinates in, as everywhere in Qalam: points, top-left origin of the main screen.
/// </summary>
internal static class MacWindowFrame
{
    private const string ObjC = "/usr/lib/libobjc.dylib";
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [StructLayout(LayoutKind.Sequential)] private struct CGRect { public double X, Y, W, H; }

    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr target, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendFrame(IntPtr target, IntPtr sel, CGRect frame, [MarshalAs(UnmanagedType.I1)] bool display);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool Responds(IntPtr target, IntPtr sel, IntPtr query);
    [DllImport(CG)] private static extern uint CGMainDisplayID();
    [DllImport(CG)] private static extern CGRect CGDisplayBounds(uint display);

    private static readonly Lazy<IntPtr> SelWindow = new(() => sel_registerName("window"));
    private static readonly Lazy<IntPtr> SelSetFrame = new(() => sel_registerName("setFrame:display:"));
    private static readonly Lazy<IntPtr> SelResponds = new(() => sel_registerName("respondsToSelector:"));

    /// <returns>False if the native window could not be reached (then Avalonia's own Position stays).</returns>
    public static bool Place(Window w, double xPt, double topPt, double widthPt, double heightPt)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            var h = w.TryGetPlatformHandle();
            if (h == null || h.Handle == IntPtr.Zero) return false;
            // the handle is an NSWindow or an NSView (then take its window); an unknown selector would abort the app,
            // so every call is checked with respondsToSelector: first
            var ns = h.Handle;
            if (!Responds(ns, SelResponds.Value, SelSetFrame.Value))
            {
                if (!Responds(ns, SelResponds.Value, SelWindow.Value)) return false;
                ns = Send(ns, SelWindow.Value);
                if (ns == IntPtr.Zero || !Responds(ns, SelResponds.Value, SelSetFrame.Value)) return false;
            }
            double mainH = CGDisplayBounds(CGMainDisplayID()).H;
            // Cocoa: origin at the bottom-left of the main screen, y grows upwards
            var frame = new CGRect { X = xPt, Y = mainH - (topPt + heightPt), W = widthPt, H = heightPt };
            SendFrame(ns, SelSetFrame.Value, frame, true);
            return true;
        }
        catch { return false; }
    }
}
