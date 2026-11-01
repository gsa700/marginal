// MADE FROM Deadwax (src/Deadwax.App/MainWindow.axaml.cs, "the frame": ApplyCorners,
// BuildWindowButtons, the resize band and OnTitleBarPressed), 2026-10-31, itself
// AlbumWall's. Changed: the Mac keeps its system title bar here (Deadwax extends
// its own under the top bar), and the window has a hairline edge, being light.
// A fix to the shared behaviour goes to both.
//
// Marginal draws its own title bar off the Mac (Dave, 2026-10-31). Under
// XWayland ("Backend": "x11") GNOME's X11 frame has rounded top corners above a
// window with square bottom ones; on native Wayland Avalonia's drawn title bar
// does not maximize on a double click. Drawn here, the window has 12 px corners
// all round, the desktop's own buttons (WindowButtons) and both behaviours, as
// Deadwax and AlbumWall do.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Marginal.App;

public sealed partial class MainWindow
{
    private const double ResizeBand = 7;
    private StandardCursorType _cursor = StandardCursorType.Arrow;

    /// Off the Mac: no system decorations, a transparent window, and the
    /// mode-tab bar as the title bar. On the Mac nothing changes.
    private void UseDrawnTitleBar()
    {
        if (OperatingSystem.IsMacOS()) return;

        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        TitleText.IsVisible = true;

        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) ApplyCorners(); };
        ApplyCorners();
        BuildWindowButtons();
        AddHandler(PointerPressedEvent, OnPointerPressedForResize, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMovedForResize, RoutingStrategies.Tunnel);
        TopBar.PointerPressed += OnTitleBarPressed;
    }

    /// 12 px corners and a hairline edge on a normal window; square and edgeless
    /// when maximized (or minimized, full screen): the window is transparent,
    /// so a rounded corner on a maximized one shows the desktop through it.
    private void ApplyCorners()
    {
        var normal = WindowState == WindowState.Normal;
        Surface.CornerRadius = new CornerRadius(normal ? 12 : 0);
        Surface.BorderThickness = new Thickness(normal ? 1 : 0);
        Surface.BorderBrush = normal && this.TryFindResource("Edge", ActualThemeVariant, out var edge) && edge is IBrush b
            ? b : null;
    }

    private void BuildWindowButtons()
    {
        var (left, right) = WindowButtons.Layout();
        foreach (var k in left) LeftButtons.Children.Add(WindowButtons.Create(k, () => Invoke(k)));
        foreach (var k in right) RightButtons.Children.Add(WindowButtons.Create(k, () => Invoke(k)));
        if (left.Length > 0) LeftButtons.Margin = new Thickness(0, 0, 14, 0);
        if (right.Length > 0) RightButtons.Margin = new Thickness(14, 0, 0, 0);
        // Square buttons (Windows): flush to each other and to the window's
        // edge, the close button's red filling the corner (the Surface clip
        // rounds it), as Windows' own caption buttons do. Round ones (Linux)
        // keep their gaps. -16 undoes the bar's 16 px side padding.
        if (!OperatingSystem.IsLinux())
        {
            LeftButtons.Spacing = RightButtons.Spacing = 0;
            if (right.Length > 0) RightButtons.Margin = new Thickness(14, 0, -16, 0);
            if (left.Length > 0) LeftButtons.Margin = new Thickness(-16, 0, 14, 0);
        }

        void Invoke(WindowButtons.Kind k)
        {
            switch (k)
            {
                case WindowButtons.Kind.Minimize: WindowState = WindowState.Minimized; break;
                case WindowButtons.Kind.Maximize: ToggleMaximized(); break;
                default: Close(); break;
            }
        }
    }

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private WindowEdge? EdgeAt(Point p)
    {
        if (WindowState != WindowState.Normal) return null;
        double w = Bounds.Width, h = Bounds.Height;
        bool l = p.X <= ResizeBand, r = p.X >= w - ResizeBand, t = p.Y <= ResizeBand, b = p.Y >= h - ResizeBand;
        return (l, r, t, b) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (_, true, true, _) => WindowEdge.NorthEast,
            (true, _, _, true) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.West,
            (_, true, _, _) => WindowEdge.East,
            (_, _, true, _) => WindowEdge.North,
            (_, _, _, true) => WindowEdge.South,
            _ => null,
        };
    }

    private void OnPointerMovedForResize(object? sender, PointerEventArgs e)
    {
        var cursor = EdgeAt(e.GetPosition(this)) switch
        {
            WindowEdge.North or WindowEdge.South => StandardCursorType.SizeNorthSouth,
            WindowEdge.West or WindowEdge.East => StandardCursorType.SizeWestEast,
            WindowEdge.NorthWest => StandardCursorType.TopLeftCorner,
            WindowEdge.NorthEast => StandardCursorType.TopRightCorner,
            WindowEdge.SouthWest => StandardCursorType.BottomLeftCorner,
            WindowEdge.SouthEast => StandardCursorType.BottomRightCorner,
            _ => StandardCursorType.Arrow,
        };
        if (_cursor == cursor) return;
        _cursor = cursor;
        Cursor = new Cursor(cursor);
    }

    private void OnPointerPressedForResize(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var edge = EdgeAt(e.GetPosition(this));
        if (edge is null) return;
        BeginResizeDrag(edge.Value, e);
        e.Handled = true;
    }

    /// A press on the bar's own background moves the window; a double click
    /// maximizes, handled here where the click count is known (AlbumWall,
    /// 2026-09-20: a separate DoubleTapped handler cannot stop the move drag
    /// already begun by the second press). The tabs and the chip are left alone.
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button || e.Source is Visual v && v.FindAncestorOfType<Button>() is not null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { ToggleMaximized(); e.Handled = true; return; }
        if (e.ClickCount > 2) return;
        BeginMoveDrag(e);
    }
}
