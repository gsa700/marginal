// COPIED from AlbumWall (src/AlbumWall.App/DrawnCaptions.cs), 2026-10-31: names changed only; a fix in one
// goes to the other.
//
// AlbumWall — the buttons in a title bar the app draws itself, cut to what the desktop asked for.
//
// On native Wayland (since 2b3f43a) GNOME draws no title bars: a client that
// wants one draws its own, and for Preferences, the notices and the confirm
// box, Avalonia 12 does. Its title bar carries four buttons whatever the
// desktop says: full screen, minimize, maximize and close. It greys out the
// ones a window cannot use rather than leaving them off, so Preferences, which
// cannot be resized, showed two dead buttons (full screen and maximize) beside
// the two GNOME asked for. Spotted by him on 2026-09-22, and settled the same
// day: an owned window like Preferences gets Close and nothing else.
//
// The main window has no title bar of Avalonia's (decorations=None) and draws
// its own buttons from the same WindowButtons.Layout this uses, so both kinds
// of window now obey one rule.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Marginal.App;

internal static class DrawnCaptions
{
    public static void FollowTheDesktop()
    {
        // Only where Avalonia draws the title bar. Windows and X11 get the
        // system's own, which already follows the system's settings.
        if (!Program.NativeWayland) return;

        var (left, right) = WindowButtons.Layout();
        var wanted = left.Concat(right).ToHashSet();

        // Every Button as it joins a window, filtered to the four caption parts
        // by name: the decorations are built from a theme template outside the
        // window's own tree, so this is the one place they can all be caught.
        Control.LoadedEvent.AddClassHandler<Button>((button, _) =>
        {
            var keep = button.Name switch
            {
                "PART_FullScreenButton" => false,
                "PART_MinimizeButton" => wanted.Contains(WindowButtons.Kind.Minimize) && !Owned(button),
                "PART_MaximizeButton" => wanted.Contains(WindowButtons.Kind.Maximize) && !Owned(button)
                                         && WindowOf(button) is { CanResize: true },
                _ => (bool?)null,
            };
            if (keep is not false) return;
            button.IsVisible = false;
            Console.WriteLine($"[captions] {WindowOf(button)?.Title ?? "?"}: {button.Name![5..^6]} hidden");
        }, RoutingStrategies.Direct);
    }

    /// A window that belongs to another (Preferences, a confirm box) gets only
    /// Close: "we only need X on prefs". It is also what GNOME gives its own
    /// dialogs. Minimizing one on its own leaves the main window without it,
    /// and maximizing a fixed-size sheet means nothing.
    private static bool Owned(Visual part) => WindowOf(part) is { Owner: not null };

    /// The window a caption button serves. Not an ancestor: Avalonia puts the
    /// drawn title bar in a TopLevelHost ABOVE the window (TopLevelHost >
    /// LayerWrapper > Panel > StackPanel > button, measured), beside the window
    /// rather than inside it, so TopLevel.GetTopLevel on the button is null. The
    /// host holds the window, so it is found from the host down.
    private static Window? WindowOf(Visual part)
    {
        var host = part.GetVisualAncestors().LastOrDefault() ?? part;
        return host as Window ?? host.GetVisualDescendants().OfType<Window>().FirstOrDefault();
    }
}
