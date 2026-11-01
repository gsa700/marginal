// Marginal — text size: how large the windows are drawn (Preferences,
// Accessibility, 2026-11-01).
//
// Made from AlbumWall's TextSize.cs, then changed. As there, a larger size is
// not a font setting: the type sizes are written into the windows (11, 12, 13
// where the design wants them), so each window is drawn through a scale, text,
// spacing and buttons together, the way a browser's zoom works.
//
// Changed from AlbumWall's:
// - EVERY window follows it, the main one too. AlbumWall leaves its main window
//   out because a scale would make the covers bigger and fewer to a row; here
//   the main window is the tables, and that is where larger text is wanted.
// - The scale goes INSIDE the drawn frame: Follow is called before
//   DrawnFrame.Attach or Wrap, so the window's content is wrapped once and the
//   frame (shadow margin, resize band, window buttons) stays its own size.
// - Sizes that are kept (Settings) are kept at 100%: a window is opened at the
//   kept size times Factor, and its size is divided by Factor to be kept.
//
// Applies at once to every window that follows it, open or not.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Marginal.App;

public static class TextSize
{
    /// The choices, as percentages of the designed size (AlbumWall's).
    public static readonly int[] Choices = [100, 115, 130, 150];

    public static int Percent { get; private set; } = 100;
    public static double Factor => Percent / 100.0;

    /// Raised after a new size is chosen, once the windows have followed it.
    public static event Action? Changed;

    public static void Choose(int? percent)
    {
        var p = percent is { } v && Choices.Contains(v) ? v : 100;
        if (p == Percent) return;
        Percent = p;
        Console.WriteLine($"[text] {Percent}%");
        Changed?.Invoke();
    }

    /// Right after InitializeComponent, before the drawn frame. The window's
    /// XAML size and smallest size are scaled now; a size set after this from
    /// Settings is multiplied by Factor where it is set.
    public static void Follow(Window window)
    {
        if (window.Content is not Control content) return;
        var host = new LayoutTransformControl();
        window.Content = null;
        host.Child = content;
        window.Content = host;

        double minWidth = window.MinWidth, minHeight = window.MinHeight;
        var applied = 1.0;

        void Apply()
        {
            var f = Factor;
            host.LayoutTransform = f == 1 ? null : new ScaleTransform(f, f);
            var area = (window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is { } screen
                ? screen.WorkingArea.Size.ToSize(screen.Scaling) : new Size(double.MaxValue, double.MaxValue);
            window.MinWidth = Math.Min(minWidth * f, area.Width - 48);
            window.MinHeight = Math.Min(minHeight * f, area.Height - 48);
            // A window as tall as what it holds needs nothing more; one with a
            // size grows or shrinks with the text, from the size it has now
            // (dragged or kept), but never past the screen it is on.
            if (window.SizeToContent == SizeToContent.Manual && window.WindowState == WindowState.Normal)
            {
                if (!double.IsNaN(window.Width)) window.Width = Math.Min(Math.Ceiling(window.Width / applied * f), area.Width - 48);
                if (!double.IsNaN(window.Height)) window.Height = Math.Min(Math.Ceiling(window.Height / applied * f), area.Height - 48);
            }
            applied = f;
        }

        Apply();
        Changed += Apply;
        window.Closed += (_, _) => Changed -= Apply;
    }
}
