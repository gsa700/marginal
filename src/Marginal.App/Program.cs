using Avalonia;

namespace Marginal.App;

public static class Program
{
    /// Set when this run exists only to uninstall: ask, act, and go (InstallFlow).
    public static bool PendingUninstall { get; private set; }

    /// Which app this is, for Spindle's install and update code: everything
    /// else (folders, files, repository, bundle id) follows from the name.
    public static AppInfo Identity => new()
    {
        Name = App.DisplayName,
        Description = "Notes in the margins of your music library: tags, art and lyrics",
        DataDir = Path.GetDirectoryName(Settings.FilePath)!,
        AppAssembly = typeof(Program).Assembly,
    };

    [STAThread]
    public static int Main(string[] args)
    {
        AppInfo.Use(Identity);
        WindowSystem.NativeWayland = NativeWayland;     // for Spindle.Ui (WaylandShell, WindowPlacement, DrawnCaptions)
        // System libraries from the system's places, before anything loads one
        // (Spindle's NativeLoading, which replaced Marginal's own on 2026-11-05:
        // that one missed NWayland, so a planted libwayland-client.so.0 was loaded).
        Spindle.NativeLoading.Install([.. Identity.NativeLibrariesUnix, .. Identity.NativeLibrariesWindows], typeof(Program).Assembly.GetName().Name!);

        // The log and the crash catcher (Diagnostics). Errors escaping a click
        // are shown and survived (App), and go to the log through Console.Error.
        LogFile.Start(Path.GetDirectoryName(Settings.FilePath)!, "marginal.log");
        Diagnostics.CatchCrashes(App.DisplayName, InstallService.CurrentVersion,
                                 () => NativeWayland ? "native Wayland" : "X11");
        if (!NativeWayland && ScreenReader.Running)
            Console.WriteLine("[marginal] backend: X11 through XWayland (a screen reader is running)");

        // Who this program is to Windows, before any window exists (WindowsShell).
        WindowsShell.ClaimIdentity();

        // --install and --uninstall, the family's switches (Deadwax's, from
        // AlbumWall's), handled before Avalonia: an install or a quiet
        // uninstall never opens a window. `--uninstall --quiet` is what winget
        // and the Installed-apps entry run; plain `--uninstall` opens the
        // window only to ask.
        var request = InstallCommandLine.Parse(args);
        try
        {
            switch (request.Action)
            {
                case InstallAction.Install:
                    var installed = InstallService.Install();
                    Console.WriteLine($"Installed to {installed.ExePath}");
                    if (!request.Quiet) InstallService.LaunchDetached(installed.ExePath);
                    return installed.Registered ? 0 : 2;

                case InstallAction.Uninstall when request.Quiet:
                    InstallService.Uninstall(new UninstallOptions(RemoveSettings: false));
                    return 0;

                case InstallAction.Uninstall:
                    PendingUninstall = true;
                    break;
            }
        }
        catch (InstallBlockedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// Native Wayland wherever a Wayland session exists, as AlbumWall and
    /// Deadwax do, on Linux. Not FreeBSD yet (Avalonia's Wayland backend fails
    /// there at start; see AlbumWall).
    ///
    /// DROPPED 2026-11-11 (Dave): MARGINAL_X11=1 and the "Backend": "x11"
    /// setting (2026-10-31, GNOME only: a Wayland client cannot place its
    /// window, and mutter maximized a large remembered size). A workaround for
    /// window memory on GNOME; X11 is only the fallback now.
    public static readonly bool NativeWayland =
        OperatingSystem.IsLinux()
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        && !ScreenReader.Running;

    // ScreenReader.Running, last above, is Spindle's (AlbumWall's check): with
    // a screen reader running the app goes through XWayland. Avalonia 12.1
    // speaks to screen readers (AT-SPI) only from its X11 backend, and on
    // native Wayland Orca could not see Marginal at all (Techbench, Fedora 45,
    // 2026-11-03: 8 of 8 starts off the bus native, 8 of 8 on it with x11).

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UseSystemPlatform().WithInterFont()   // Spindle.Ui: UsePlatformDetect, plus FreeBSD
            // Avalonia's own warnings (bindings, layout) into the log, not nowhere.
            .AfterSetup(_ => Avalonia.Logging.Logger.Sink = new ConsoleLogSink(Avalonia.Logging.LogEventLevel.Warning));
        // The system draws the title bar for now; Deadwax's drawn title bar
        // (and its Wayland ForceDrawnDecorations) comes when this window gets one.
        return NativeWayland ? builder.UseWayland() : builder;
    }
}
