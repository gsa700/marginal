// FROM Deadwax (src/Deadwax.App/WindowPlacement.cs, 2026-10-31), namespace changed only;
// a fix in one goes to the other.
//
using Avalonia;
using Avalonia.Controls;

namespace Marginal.App;

/// Putting a window back where it was (Windows, X11, macOS; native Wayland
/// does not let a window place itself, and its position reads 0,0 there).
/// AlbumWall's rules: the screen is the one under the saved corner, with ITS
/// scaling (RenderScaling is not settled at Opened); the window is shrunk to
/// fit that screen's working area and then moved fully inside it, so a monitor
/// unplugged or a resolution changed since cannot leave it off-screen.
public static class WindowPlacement
{
    /// Moves the window to the saved corner, kept on a screen. False when there
    /// was nothing to restore.
    public static bool Restore(Window window, int? savedX, int? savedY)
    {
        if (Program.NativeWayland || savedX is not { } x || savedY is not { } y) return false;
        var screen = window.Screens.ScreenFromPoint(new PixelPoint(x, y)) ?? window.Screens.Primary;
        if (screen is null) return false;
        var scale = screen.Scaling > 0 ? screen.Scaling : window.RenderScaling;
        var area = screen.WorkingArea;
        var w = (int)(window.Width * scale);
        var h = (int)(window.Height * scale);
        if (window.CanResize)
        {
            if (w > area.Width) { w = area.Width; window.Width = Math.Max(w / scale, window.MinWidth); }
            if (h > area.Height) { h = area.Height; window.Height = Math.Max(h / scale, window.MinHeight); }
        }
        var cx = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - w));
        var cy = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - h));
        window.Position = new PixelPoint(cx, cy);
        return true;
    }

    /// First launch, nothing saved: shrink to fit the screen it opens on and
    /// centre it there (Deadwax's XAML size was taller than a 1080p screen's
    /// working area, 2026-10-17).
    public static void FitNew(Window window)
    {
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen is null) return;
        var scale = screen.Scaling > 0 ? screen.Scaling : window.RenderScaling;
        var area = screen.WorkingArea;
        var w = (int)(window.Width * scale);
        var h = (int)(window.Height * scale);
        if (window.CanResize)
        {
            if (w > area.Width) { w = area.Width; window.Width = Math.Max(w / scale, window.MinWidth); }
            if (h > area.Height) { h = area.Height; window.Height = Math.Max(h / scale, window.MinHeight); }
        }
        if (Program.NativeWayland) return;   // the compositor places it
        window.Position = new PixelPoint(area.X + Math.Max(0, (area.Width - w) / 2),
                                         area.Y + Math.Max(0, (area.Height - h) / 2));
    }

    /// The position worth saving, or null on native Wayland, where it is unknowable.
    public static PixelPoint? Current(Window window) => Program.NativeWayland ? null : window.Position;
}
