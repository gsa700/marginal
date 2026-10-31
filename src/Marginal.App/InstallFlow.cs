// FROM Deadwax (Deadwax.App/InstallFlow.cs, 704ed87, 2026-10-31), renamed to Marginal throughout.
// Dates and findings in the comments are Deadwax's history; a fix to the
// install or update logic goes to both (and AlbumWall's, from which Deadwax's came).
//
using Avalonia.Controls;
using Marginal.App.Install;

namespace Marginal.App;

/// The installer's conversations with the person: the offer a loose copy makes,
/// and the uninstall. AlbumWall's App.OfferInstallAsync and RunUninstallAsync
/// (2026-10-18), worded for Marginal.
internal static class InstallFlow
{
    /// Runs when the main window has opened and BEFORE the drive is first
    /// touched. True when this run is over (it was only an uninstall, or it
    /// handed over to the installed copy), so the caller must not start
    /// watching the drive: a read left behind by a closing window is what the
    /// BDR-209D answers with a USB reset.
    public static async Task<bool> BeforeStartAsync(Window window)
    {
        // This run exists only to uninstall: ask, act, and go.
        if (Program.PendingUninstall)
        {
            await RunUninstallAsync(window);
            return true;
        }

        // Re-asserted at EVERY start, never check-and-skip: the family's rule,
        // learned when a lost registration stayed lost. A no-op unless this
        // copy is the installed one.
        InstallService.EnsureRegistered();

        // The unpacked native libraries of builds that are no longer running.
        _ = Task.Run(InstallService.SweepStaleExtractions);

        // The Mac: the installer's disk image left mounted, or this copy run from it.
        await MacInstaller.OfferAsync(window);

        // A release sitting wherever it was unzipped offers to install itself.
        // Never a development build (see InstallService).
        return InstallService.ShouldOfferInstall && await OfferInstallAsync(window);
    }

    /// Offer to install a loose copy. True when it was installed and the
    /// installed copy has been started, in which case this window is closing.
    public static async Task<bool> OfferInstallAsync(Window owner)
    {
        var where = InstallService.InstallDirectory;
        var lists = OperatingSystem.IsWindows()
            ? "lists it in Settings → Apps → Installed apps, with a Start Menu shortcut"
            : "adds it to your applications menu";

        var accepted = await new ConfirmWindow(
            $"Install {App.DisplayName}",
            $"Install {App.DisplayName} on this computer?",
            affirmative: "Install", negative: "Not now",
            detail: $"Copies the program to {where} and {lists}. Your music, your settings and "
                  + "the backups of past writes are untouched either way.\n\n"
                  + "To run from here permanently without being asked again, put a file named "
                  + $"{InstallLayout.PortableMarker} beside the program.")
            .ShowDialog<bool>(owner);
        if (!accepted) return false;

        try
        {
            var installed = InstallService.Install();

            // Installed but not listed is a real outcome, not a detail: the program
            // works, yet the usual way to remove it is missing. Say so here rather
            // than leave it to be discovered.
            if (!installed.Registered)
            {
                await new ConfirmWindow("Installed, with one problem",
                    $"{App.DisplayName} is installed in {where} and will run normally, but it could "
                    + "not register itself with the desktop.",
                    affirmative: "OK", negative: null,
                    detail: "Starting the installed copy again usually fixes it. Failing that, run "
                          + "it once with --install from a terminal.")
                    .ShowDialog<bool>(owner);
            }

            InstallService.LaunchDetached(installed.ExePath);
            owner.Close();
            return true;
        }
        catch (Exception ex)
        {
            await new ConfirmWindow("Could not install", ex.Message,
                affirmative: "OK", negative: null).ShowDialog<bool>(owner);
            return false;
        }
    }

    /// The interactive uninstall: say what goes, ask about the settings, then go.
    /// <param name="asked">
    /// True when a person chose Uninstall from inside the running app, false when
    /// the whole run was started only to uninstall. The difference is what a
    /// "Cancel" means: from the app it means carry on; from --uninstall the run
    /// has nothing else to do and ends.
    /// </param>
    public static async Task RunUninstallAsync(Window owner, bool asked = false)
    {
        // No installer here (the Mac): say how this system removes an app, and remove nothing.
        if (!InstallService.CanInstall)
        {
            await new ConfirmWindow($"Uninstall {App.DisplayName}",
                $"Drag {App.DisplayName} from Applications to the Bin.",
                affirmative: "OK", negative: null,
                detail: "That removes the program. Your music, your settings and the backups of past writes stay.")
                .ShowDialog<bool>(owner);
            if (!asked) owner.Close();
            return;
        }

        var dialog = new ConfirmWindow(
            $"Uninstall {App.DisplayName}",
            $"Remove {App.DisplayName} from this computer?",
            affirmative: "Uninstall", negative: "Cancel",
            detail: "Removes the program and its menu entries. Your music and the backups of past "
                  + "writes (which Undo uses) are never touched.",
            option: "Also remove my settings");

        var accepted = await dialog.ShowDialog<bool>(owner);
        if (accepted)
        {
            InstallService.Uninstall(new UninstallOptions(dialog.OptionChecked));

            // A backstop, from LP-100A: the helper is now waiting on this process id, and nothing
            // in this process is worth preserving. If closing the window below does not end it,
            // this does, and the uninstall completes either way.
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => Environment.Exit(0));
        }

        if (accepted || !asked) owner.Close();
    }
}
