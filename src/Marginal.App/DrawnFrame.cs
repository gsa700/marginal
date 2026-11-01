// MADE FROM Deadwax (src/Deadwax.App/MainWindow.axaml.cs, "the frame": ApplyCorners,
// BuildWindowButtons, the resize band and OnTitleBarPressed), 2026-10-31, itself
// AlbumWall's. Changed: one class for every window rather than the main window's
// own code; the Mac keeps its system title bar here (Deadwax extends its own under
// the top bar); a hairline edge, the windows being light; owned windows get Close
// only. A fix to the shared behaviour goes to both.
//
// Marginal draws its own title bars off the Mac (Dave, 2026-10-31). Under
// XWayland ("Backend": "x11") GNOME's X11 frame has rounded top corners above a
// window with square bottom ones; on native Wayland Avalonia's drawn title bar
// does not maximize on a double click. Drawn here, every window has 12 px
// corners all round, the desktop's own buttons (WindowButtons) and both
// behaviours, as Deadwax and AlbumWall do.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Marginal.App;

internal sealed class DrawnFrame
{
    /// Everywhere but the Mac, which keeps its own window, corners and shadow
    /// and only has its title bar folded into the app's own (UseMacTitleBar).
    public static bool Wanted => !OperatingSystem.IsMacOS();

    /// The bar's height on the Mac, which the traffic lights are centred on.
    private const double MacBarHeight = 44;

    /// How much of the bar's leading end the traffic lights take on the Mac:
    /// the three buttons and the gap the system leaves round them (Deadwax's).
    private const double MacTrafficLightsWidth = 70;

    /// The transparent margin round a normal window that holds its shadow
    /// (Dave, 2026-10-31: with no shadow the light dialogs melted into the
    /// light main window, and the main window's lines showed through their
    /// corners). GNOME draws none for a window it does not frame.
    private const double ShadowMargin = 14;

    /// Soft and close: the blur reaches about 1.5 times its radius, which has
    /// to fit inside the margin or the shadow stops at a hard edge.
    private static readonly BoxShadows Shadow = BoxShadows.Parse("0 1 8 0 #40000000");

    /// How far inside the window's edge a press still resizes, besides the
    /// whole shadow margin, as GNOME's own windows resize from just outside.
    private const double ResizeBand = 5;

    private readonly Window _window;
    private readonly Border _surface;
    private Border _holder = null!;   // the transparent margin
    private Border _shadow = null!;   // casts the shadow, behind the surface
    private Border _outline = null!;  // the hairline edge, over everything
    private StandardCursorType _cursor = StandardCursorType.Arrow;

    private DrawnFrame(Window window, Border surface)
    {
        _window = window;
        _surface = surface;
    }

    /// A window whose XAML has its own frame parts: the Surface around
    /// everything, a bar to drag (its header), and a panel on each side of it
    /// for the window buttons. An owned window (a sheet over the main one) gets
    /// Close only, as GNOME's own dialogs do: "we only need X on prefs".
    public static void Attach(Window window, Border surface, Border bar, StackPanel left, StackPanel right, bool owned)
    {
        var frame = new DrawnFrame(window, surface);
        bar.PointerPressed += frame.OnBarPressed;
        if (!Wanted)
        {
            frame.UseMacTitleBar(bar, left);
            return;
        }
        frame.Undecorate();
        frame.AddButtons(bar, left, right, owned);
    }

    /// The Mac: a REAL title bar, extended under the app's bar, as Deadwax and
    /// AlbumWall have (Dave, 2026-10-31: "why isn't the titlebar integrated on
    /// macOS?"). macOS keeps drawing the traffic lights, the corners and the
    /// shadow, and resizes at its own edges; the bar drags and zooms on a
    /// double click (OnBarPressed). The green button zooms instead of entering
    /// full screen, as in the other two (MacWindow).
    private void UseMacTitleBar(Border bar, StackPanel left)
    {
        _window.ExtendClientAreaToDecorationsHint = true;
        bar.Height = MacBarHeight;
        // The bar is inside the window's text-size scale (TextSize.Follow), so
        // at 130% it is 130% as tall: the title-bar height and the lights'
        // centre follow it, and the lights' own room, which does not scale, is
        // kept in the bar's smaller units (Accessibility, 2026-11-01).
        void Fit()
        {
            var f = TextSize.Factor;
            // Avalonia's own layout reads this as the title bar's height; the
            // lights are centred by CenterTrafficLights, which the hint does not do.
            _window.ExtendClientAreaTitleBarHeightHint = MacBarHeight * f;
            left.Width = MacTrafficLightsWidth / f;   // their place kept clear
            left.Margin = new Thickness(-8 / f, 0, 6, 0);
        }
        Fit();
        TextSize.Changed += Fit;
        TextSize.Changed += RecenterTrafficLights;
        _window.Closed += (_, _) => { TextSize.Changed -= Fit; TextSize.Changed -= RecenterTrafficLights; };
        _window.Opened += (_, _) =>
        {
            var h = _window.TryGetPlatformHandle();
            MacWindow.ZoomInsteadOfFullScreen(h?.Handle ?? IntPtr.Zero, h?.HandleDescriptor);
            CenterTrafficLights();
        };
        // AppKit lays its title bar out again after each of these and puts the
        // lights back where it keeps them, so they are moved again once it has.
        _window.SizeChanged += (_, _) => RecenterTrafficLights();
        _window.Activated += (_, _) => RecenterTrafficLights();
        _window.Deactivated += (_, _) => RecenterTrafficLights();
        _window.PropertyChanged += (_, e) => { if (e.Property == Window.WindowStateProperty) RecenterTrafficLights(); };
    }

    private void CenterTrafficLights()
    {
        var h = _window.TryGetPlatformHandle();
        MacWindow.CenterTrafficLights(h?.Handle ?? IntPtr.Zero, h?.HandleDescriptor, MacBarHeight * TextSize.Factor);
    }

    /// At once and again once AppKit is done: during a live resize it lays the
    /// title bar out on every frame, and moved only afterwards the lights
    /// jumped about until the drag stopped (AlbumWall, 2026-10-18).
    private void RecenterTrafficLights()
    {
        CenterTrafficLights();
        Dispatcher.UIThread.Post(CenterTrafficLights, DispatcherPriority.Render);
    }

    /// A window with no header of its own (Preferences, the confirm box): its
    /// content goes under a slim bar with the window's title and Close.
    public static void Wrap(Window window)
    {
        if (!Wanted || window.Content is not Control content) return;
        window.Content = null;

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        var title = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
        };
        title.Bind(TextBlock.TextProperty, window.GetObservable(Window.TitleProperty));   // set after, by some
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("Soft"));
        var bar = new Border
        {
            MinHeight = 40,
            Padding = new Thickness(16, 0),
            Background = Brushes.Transparent,   // the ground shows; hit-testable for the drag
            Child = new DockPanel { Children = { left, right, title } },
        };
        DockPanel.SetDock(bar, Dock.Top);

        var surface = new Border { ClipToBounds = true, Child = new DockPanel { Children = { bar, content } } };
        surface.Bind(Border.BackgroundProperty, surface.GetResourceObservable("Ground"));
        window.Content = surface;

        Attach(window, surface, bar, left, right, owned: true);
    }

    private void Undecorate()
    {
        _window.WindowDecorations = WindowDecorations.None;
        _window.ExtendClientAreaToDecorationsHint = true;
        _window.Background = Brushes.Transparent;
        _window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        // Window > margin > shadow > the surface and everything in it. The
        // surface clips to its corners, which would clip its own shadow too,
        // so a border of its own casts it.
        var root = (Control)_window.Content!;
        _window.Content = null;
        // The hairline is a layer of its own on top: drawn by the surface, the
        // square content inside (a white header) painted over it where the
        // corners curve, and the line broke at every corner (Dave, 2026-10-31).
        _outline = new Border { IsHitTestVisible = false };
        _shadow = new Border { Child = new Panel { Children = { root, _outline } } };
        _shadow.Bind(Border.BackgroundProperty, _window.GetResourceObservable("Ground"));
        _holder = new Border { Background = Brushes.Transparent, Child = _shadow };   // hit-testable: it is the resize band
        _window.Content = _holder;

        _window.PropertyChanged += (_, e) => { if (e.Property == Window.WindowStateProperty) ApplyCorners(); };
        ApplyCorners();
        _window.AddHandler(InputElement.PointerPressedEvent, OnPointerPressedForResize, RoutingStrategies.Tunnel);
        _window.AddHandler(InputElement.PointerMovedEvent, OnPointerMovedForResize, RoutingStrategies.Tunnel);
    }

    /// 12 px corners, a hairline edge and the shadow on a normal window;
    /// square, edgeless and filling the window when maximized (or minimized,
    /// full screen): the window is transparent, so a rounded corner on a
    /// maximized one shows the desktop through it.
    private void ApplyCorners()
    {
        var normal = _window.WindowState == WindowState.Normal;
        _holder.Padding = new Thickness(normal ? ShadowMargin : 0);
        _shadow.CornerRadius = _surface.CornerRadius = _outline.CornerRadius = new CornerRadius(normal ? 12 : 0);
        _shadow.BoxShadow = normal ? Shadow : default;
        _outline.BorderThickness = new Thickness(normal ? 1 : 0);
        _outline.BorderBrush = normal && _window.TryFindResource("Edge", _window.ActualThemeVariant, out var edge) && edge is IBrush b
            ? b : null;
    }

    private void AddButtons(Border bar, StackPanel left, StackPanel right, bool owned)
    {
        var (l, r) = WindowButtons.Layout();
        if (owned)
        {
            // Close alone, on the side the desktop keeps it.
            var closeLeft = l.Contains(WindowButtons.Kind.Close);
            WindowButtons.Kind[] close = [WindowButtons.Kind.Close], none = [];
            (l, r) = closeLeft ? (close, none) : (none, close);
        }
        foreach (var k in l) left.Children.Add(WindowButtons.Create(k, () => Invoke(k)));
        foreach (var k in r) right.Children.Add(WindowButtons.Create(k, () => Invoke(k)));
        if (l.Length > 0) left.Margin = new Thickness(0, 0, 14, 0);
        if (r.Length > 0) right.Margin = new Thickness(14, 0, 0, 0);

        // Square buttons (Windows): full height, flush to each other and to
        // the window's edge, the close button's red filling the corner (the
        // Surface clip rounds it), as Windows' own caption buttons do. The
        // negative margins undo the bar's padding on that side. Round ones
        // (Linux) keep their gaps.
        if (!OperatingSystem.IsLinux())
        {
            var pad = bar.Padding;
            foreach (var panel in new[] { left, right })
            {
                panel.Spacing = 0;
                panel.VerticalAlignment = VerticalAlignment.Stretch;
                foreach (var b in panel.Children.OfType<Button>()) { b.Height = double.NaN; b.VerticalAlignment = VerticalAlignment.Stretch; }
            }
            if (r.Length > 0) right.Margin = new Thickness(14, -pad.Top, -pad.Right, -pad.Bottom);
            if (l.Length > 0) left.Margin = new Thickness(-pad.Left, -pad.Top, 14, -pad.Bottom);
        }

        void Invoke(WindowButtons.Kind k)
        {
            switch (k)
            {
                case WindowButtons.Kind.Minimize: _window.WindowState = WindowState.Minimized; break;
                case WindowButtons.Kind.Maximize: ToggleMaximized(); break;
                default: _window.Close(); break;
            }
        }
    }

    private void ToggleMaximized()
    {
        if (!_window.CanResize) return;
        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private WindowEdge? EdgeAt(Point p)
    {
        if (_window.WindowState != WindowState.Normal || !_window.CanResize) return null;
        double w = _window.Bounds.Width, h = _window.Bounds.Height;
        const double band = ShadowMargin + ResizeBand;
        bool l = p.X <= band, r = p.X >= w - band, t = p.Y <= band, b = p.Y >= h - band;
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
        var cursor = EdgeAt(e.GetPosition(_window)) switch
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
        _window.Cursor = new Cursor(cursor);
    }

    private void OnPointerPressedForResize(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_window).Properties.IsLeftButtonPressed) return;
        var edge = EdgeAt(e.GetPosition(_window));
        if (edge is null) return;
        _window.BeginResizeDrag(edge.Value, e);
        e.Handled = true;
    }

    /// A press on the bar's own background moves the window; a double click
    /// maximizes, handled here where the click count is known (AlbumWall,
    /// 2026-09-20: a separate DoubleTapped handler cannot stop the move drag
    /// already begun by the second press). Buttons on the bar are left alone.
    private void OnBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button || e.Source is Visual v && v.FindAncestorOfType<Button>() is not null) return;
        if (!e.GetCurrentPoint(_window).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) { ToggleMaximized(); e.Handled = true; return; }
        if (e.ClickCount > 2) return;
        _window.BeginMoveDrag(e);
    }
}
