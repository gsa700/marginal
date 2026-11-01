// COPIED from Deadwax (src/Deadwax.App/MacWindow.cs, 704ed87), 2026-10-31: namespace changed only;
// a fix in one goes to the other (and to AlbumWall's, from which Deadwax's came).
//
// Deadwax — the one thing about a macOS window that Avalonia does not offer.
// AlbumWall's MacWindow.cs; keep the two in step.
//
// The green traffic light is FULL SCREEN on macOS unless the window says
// otherwise, and full screen is switched off on the Mac, as in AlbumWall
// (MainWindow.UseMacTitleBar). Avalonia's only switch for it,
// CanMaximize = false, takes zoom with it and leaves the green button dead. The
// window's collection behavior is the system's own answer: FullScreenNone makes
// the green button zoom, which is what Option-click on it does anyway.
//
// Raw Objective-C runtime calls: nothing to
// bundle, and libobjc is on every Mac.

using System.Runtime.InteropServices;

namespace Marginal.App;

internal static class MacWindow
{
    private const ulong FullScreenPrimary = 1 << 7;
    private const ulong FullScreenAuxiliary = 1 << 8;
    private const ulong FullScreenNone = 1 << 9;

    /// Makes the green button zoom instead of entering full screen. The handle is
    /// Avalonia's platform handle for the window: an NSWindow, or an NSView whose
    /// window is the one meant. Returns false, and changes nothing, when it is
    /// neither.
    public static bool ZoomInsteadOfFullScreen(IntPtr handle, string? descriptor)
    {
        if (!OperatingSystem.IsMacOS() || handle == IntPtr.Zero) return false;
        var window = descriptor == "NSView" ? Send(handle, Sel("window")) : handle;
        if (window == IntPtr.Zero) return false;

        var behavior = (ulong)Send(window, Sel("collectionBehavior"));
        behavior = behavior & ~(FullScreenPrimary | FullScreenAuxiliary) | FullScreenNone;
        SendULong(window, Sel("setCollectionBehavior:"), behavior);
        return ((ulong)Send(window, Sel("collectionBehavior")) & FullScreenNone) != 0;
    }

    /// Centres the traffic lights on a bar `barHeight` points tall across the
    /// top of the window. Returns where the close button's centre ended up, in
    /// points from the top of the window, or null when the handle is no window.
    ///
    /// Avalonia's ExtendClientAreaTitleBarHeightHint was meant to do this, and
    /// on macOS 27 with Avalonia 12.1.2 it does not: Avalonia reports the title
    /// bar the hint asks for while AppKit keeps its standard 28 pt one, so the
    /// lights sat 14 pt from the top of the bar, well above its middle ("there
    /// are too high", 2026-10-18, measured in AlbumWall, which has the same
    /// code). The buttons live in AppKit's title bar container, so that is made
    /// the bar's height and each button is centred in it, the same thing
    /// Electron does for its trafficLightPosition. AppKit lays the title bar out
    /// again on a resize, a zoom and a change of focus, so the window calls this
    /// again after each. AlbumWall's MacWindow has the same; the two stay alike.
    public static double? CenterTrafficLights(IntPtr handle, string? descriptor, double barHeight)
    {
        if (!OperatingSystem.IsMacOS() || handle == IntPtr.Zero) return null;
        var window = descriptor == "NSView" ? Send(handle, Sel("window")) : handle;
        if (window == IntPtr.Zero) return null;
        var close = SendNInt(window, Sel("standardWindowButton:"), 0);
        var titleBar = close == IntPtr.Zero ? IntPtr.Zero : Send(close, Sel("superview"));
        var container = titleBar == IntPtr.Zero ? IntPtr.Zero : Send(titleBar, Sel("superview"));
        if (container == IntPtr.Zero) return null;

        var bounds = SendRect(container, Sel("frame"));
        var windowFrame = SendRect(window, Sel("frame"));
        bounds.Y = windowFrame.H - barHeight;
        bounds.H = barHeight;
        SendSetRect(container, Sel("setFrame:"), bounds);
        SendSetRect(titleBar, Sel("setFrame:"), new CGRect { W = bounds.W, H = barHeight });
        for (nint kind = 0; kind <= 2; kind++)     // close, minimize, zoom
        {
            var button = SendNInt(window, Sel("standardWindowButton:"), kind);
            if (button == IntPtr.Zero) continue;
            var r = SendRect(button, Sel("frame"));
            SendSetPoint(button, Sel("setFrameOrigin:"), r.X, Math.Round((barHeight - r.H) / 2));
        }
        return TrafficLightCenter(window, close);
    }

    /// Where the close button's centre is now, without moving anything: for
    /// checking that AppKit has not put the lights back on its own.
    public static double? TrafficLightCenter(IntPtr handle, string? descriptor)
    {
        if (!OperatingSystem.IsMacOS() || handle == IntPtr.Zero) return null;
        var window = descriptor == "NSView" ? Send(handle, Sel("window")) : handle;
        var close = window == IntPtr.Zero ? IntPtr.Zero : SendNInt(window, Sel("standardWindowButton:"), 0);
        return close == IntPtr.Zero ? null : TrafficLightCenter(window, close);
    }

    /// The close button's centre in points from the top of the window: what the
    /// eye judges, read back from AppKit rather than assumed.
    private static double TrafficLightCenter(IntPtr window, IntPtr close)
    {
        var inWindow = SendConvert(close, Sel("convertRect:toView:"), SendRect(close, Sel("bounds")), IntPtr.Zero);
        var content = SendRect(Send(window, Sel("contentView")), Sel("frame"));
        return content.H - (inWindow.Y + inWindow.H / 2);
    }

    /// Sends an NSApplication action with no sender: hide:, hideOtherApplications:,
    /// unhideAllApplications:. For the application menu, whose standard items
    /// Avalonia's defaults would have supplied (MainWindow.MacMenu.cs).
    public static void AppAction(string selector)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
        SendPtr(app, Sel(selector), IntPtr.Zero);
    }

    private static IntPtr Sel(string name) => sel_registerName(name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendPtr(IntPtr receiver, IntPtr selector, IntPtr value);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendULong(IntPtr receiver, IntPtr selector, ulong value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendNInt(IntPtr receiver, IntPtr selector, nint value);

    // An NSRect is four doubles, which arm64 passes and returns in registers, so
    // plain objc_msgSend carries it both ways (no _stret variant there).
    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X, Y, W, H;
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern CGRect SendRect(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendSetRect(IntPtr receiver, IntPtr selector, CGRect value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendSetPoint(IntPtr receiver, IntPtr selector, double x, double y);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern CGRect SendConvert(IntPtr receiver, IntPtr selector, CGRect rect, IntPtr view);
}
