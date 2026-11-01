// COPIED from AlbumWall (src/AlbumWall.App/WindowButtons.cs), 2026-10-31: names changed only; a fix in one
// goes to the other.
//
// AlbumWall — window controls, built to the window manager's own preferences.
//
// Removing the system title bar means taking on the job it was doing, and the
// easy version of that job is wrong: hard-coding minimize/maximize/close on the
// right is the Windows convention asserted over everyone else's desktop. This
// machine's GNOME is set to `appmenu:minimize,close` — no maximize button at
// all, because maximizing is a double-click on the title bar — and an app that
// draws one anyway is ignoring a preference its owner deliberately set.
//
// So: ask the desktop what it wants, honor the side and the order, and draw
// only the buttons it asked for. Every platform gets its own convention, and
// nobody gets a button they turned off.
//
// The glyphs are vector geometry rather than a font glyph. `Segoe Fluent Icons`
// does not exist on Linux, so the first version of this fell back to whatever
// the fontconfig chain offered and looked like Windows with the graphics
// missing, which is precisely what it was.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Shapes = Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Marginal.App;

public static class WindowButtons
{
    public enum Kind { Minimize, Maximize, Close }

    /// Left-of-title and right-of-title button sets, in the desktop's own order.
    public static (Kind[] Left, Kind[] Right) Layout()
    {
        if (OperatingSystem.IsLinux())
        {
            var raw = ReadGnomeLayout();
            if (raw is not null) return ParseGnome(raw);
        }

        // Windows and macOS have fixed, opposite conventions.
        return OperatingSystem.IsMacOS()
            ? ([Kind.Close, Kind.Minimize, Kind.Maximize], [])
            : ([], [Kind.Minimize, Kind.Maximize, Kind.Close]);
    }

    private static string? ReadGnomeLayout()
    {
        try
        {
            if (Marginal.Core.ToolPath.Find("gsettings") is not { } gsettings) return null;
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = gsettings,
                Arguments = "get org.gnome.desktop.wm.preferences button-layout",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (p is null) return null;
            var outp = p.StandardOutput.ReadToEnd();
            // A desktop without gsettings, or a hung one, must not delay startup.
            if (!p.WaitForExit(1500)) return null;
            return p.ExitCode == 0 ? outp.Trim().Trim('\'', '"') : null;
        }
        catch
        {
            // No gsettings, not GNOME, sandboxed — fall through to the default.
            return null;
        }
    }

    /// "appmenu:minimize,close" — everything before the colon sits left of the
    /// title, everything after sits right. Entries we do not draw (appmenu,
    /// spacer, icon, menu) are skipped rather than guessed at.
    private static (Kind[], Kind[]) ParseGnome(string layout)
    {
        var halves = layout.Split(':');
        var left = Parse(halves.ElementAtOrDefault(0));
        var right = Parse(halves.ElementAtOrDefault(1));

        // A layout naming none of our buttons is almost certainly one we failed
        // to understand; a window with no way to close it is not an acceptable
        // outcome of a parse.
        if (left.Length == 0 && right.Length == 0)
            return ([], [Kind.Minimize, Kind.Close]);

        return (left, right);

        static Kind[] Parse(string? half) =>
            (half ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().ToLowerInvariant() switch
                {
                    "minimize" => (Kind?)Kind.Minimize,
                    "maximize" => Kind.Maximize,
                    "close" => Kind.Close,
                    _ => null
                })
                .Where(k => k is not null)
                .Select(k => k!.Value)
                .ToArray();
    }

    /// Builds one button. Circular on Linux, where GNOME's own controls are
    /// circles; full-height squares on Windows, where they are not.
    public static Button Create(Kind kind, Action onClick)
    {
        var round = OperatingSystem.IsLinux();

        var btn = new Button
        {
            Classes = { "wbtn", round ? "round" : "square", kind.ToString().ToLowerInvariant() },
            Content = new Shapes.Path
            {
                Data = Geometry.Parse(kind switch
                {
                    Kind.Minimize => "M 0 5 H 10",
                    Kind.Maximize => "M 0.5 0.5 H 9.5 V 9.5 H 0.5 Z",
                    _ => "M 0 0 L 10 10 M 10 0 L 0 10"
                }),
                StrokeThickness = 1.2,
                Stroke = Brushes.Transparent,   // replaced by the style's Foreground binding
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        // Stroke follows the button's Foreground so hover styling works on the
        // glyph without the style needing to know it is a Path.
        if (btn.Content is Shapes.Path path)
            path.Bind(Shapes.Shape.StrokeProperty, btn.GetObservable(TemplatedControl.ForegroundProperty));

        // A glyph is no name: Orca read these as "Avalonia.Controls.Shapes.Path".
        Avalonia.Automation.AutomationProperties.SetName(btn, kind switch
        {
            Kind.Minimize => "Minimize",
            Kind.Maximize => "Maximize",
            _ => "Close"
        });
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
