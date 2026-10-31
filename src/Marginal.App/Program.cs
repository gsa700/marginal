using Avalonia;

namespace Marginal.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// Native Wayland wherever a Wayland session exists, as AlbumWall and
    /// Deadwax do. MARGINAL_X11=1 goes back through XWayland.
    public static readonly bool NativeWayland =
        OperatingSystem.IsLinux()
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        && Environment.GetEnvironmentVariable("MARGINAL_X11") != "1";

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
        // The system draws the title bar for now; Deadwax's drawn title bar
        // (and its Wayland ForceDrawnDecorations) comes when this window gets one.
        return NativeWayland ? builder.UseWayland() : builder;
    }
}
