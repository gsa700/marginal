using Avalonia;

namespace Marginal.App;

internal static class Program
{
    /// Set when this run exists only to uninstall: ask, act, and go (InstallFlow).
    public static bool PendingUninstall { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // System libraries from the system's places, before anything loads one (NativeLoading).
        NativeLoading.Install();

        // The log and the crash catcher (Diagnostics). Errors escaping a click
        // are shown and survived (App), and go to the log through Console.Error.
        LogFile.Start();
        Diagnostics.CatchCrashes();

        // Who this program is to Windows, before any window exists (WindowsShell).
        WindowsShell.ClaimIdentity();

        // --install and --uninstall, the family's switches (Deadwax's, from
        // AlbumWall's), handled before Avalonia: an install or a quiet
        // uninstall never opens a window. `--uninstall --quiet` is what winget
        // and the Installed-apps entry run; plain `--uninstall` opens the
        // window only to ask.
        var request = Install.InstallCommandLine.Parse(args);
        try
        {
            switch (request.Action)
            {
                case Install.InstallAction.Install:
                    var installed = Install.InstallService.Install();
                    Console.WriteLine($"Installed to {installed.ExePath}");
                    if (!request.Quiet) Install.InstallService.LaunchDetached(installed.ExePath);
                    return installed.Registered ? 0 : 2;

                case Install.InstallAction.Uninstall when request.Quiet:
                    Install.InstallService.Uninstall(new Install.UninstallOptions(RemoveSettings: false));
                    return 0;

                case Install.InstallAction.Uninstall:
                    PendingUninstall = true;
                    break;
            }
        }
        catch (Install.InstallBlockedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// Native Wayland wherever a Wayland session exists, as AlbumWall and
    /// Deadwax do. MARGINAL_X11=1 goes back through XWayland.
    ///
    /// "Backend": "x11" in settings.json does the same for good, on one
    /// machine, on GNOME only (AlbumWall's setting, and the same reasons): a
    /// Wayland client cannot place its window, so it never reopens where it
    /// was, and mutter maximizes any window that opens over 80% of the screen
    /// both ways, so a large remembered size came back maximized (Techbench,
    /// 2026-10-31; Dave: "use Xwayland"). Hand-edited, not in Preferences.
    public static readonly bool NativeWayland =
        OperatingSystem.IsLinux()
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        && Environment.GetEnvironmentVariable("MARGINAL_X11") != "1"
        && !(OnGnome && string.Equals(Settings.Load().Backend, "x11", StringComparison.OrdinalIgnoreCase));

    /// XDG_CURRENT_DESKTOP is a colon-separated list ("GNOME", "ubuntu:GNOME").
    private static bool OnGnome =>
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "")
            .Split(':').Contains("GNOME", StringComparer.OrdinalIgnoreCase);

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
            // Avalonia's own warnings (bindings, layout) into the log, not nowhere.
            .AfterSetup(_ => Avalonia.Logging.Logger.Sink = new ConsoleLogSink(Avalonia.Logging.LogEventLevel.Warning));
        // The system draws the title bar for now; Deadwax's drawn title bar
        // (and its Wayland ForceDrawnDecorations) comes when this window gets one.
        return NativeWayland ? builder.UseWayland() : builder;
    }
}
