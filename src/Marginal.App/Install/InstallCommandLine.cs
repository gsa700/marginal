// FROM Deadwax (Deadwax.App/Install/InstallCommandLine.cs, 704ed87, 2026-10-31), renamed to Marginal throughout.
// Dates and findings in the comments are Deadwax's history; a fix to the
// install or update logic goes to both (and AlbumWall's, from which Deadwax's came).
//
namespace Marginal.App.Install;

/// <summary>What the command line asked the app to do about installation.</summary>
public enum InstallAction
{
    /// <summary>Nothing install-related was asked; run the app normally.</summary>
    None,

    /// <summary>Install this copy into the per-user install directory, then exit.</summary>
    Install,

    /// <summary>Remove the installed copy and its OS registrations, then exit.</summary>
    Uninstall,
}

/// <summary>An install request parsed off the command line.</summary>
/// <param name="Action">What was asked.</param>
/// <param name="Quiet">
/// Suppress every prompt and answer conservatively. Drives unattended installs, and it is what the
/// installed-apps entry passes on removal, since Windows gives the user no way to answer a dialog
/// it didn't expect. A quiet uninstall therefore keeps settings.
/// </param>
public readonly record struct InstallRequest(InstallAction Action, bool Quiet);

/// <summary>
/// Parses the install-related switches. Pure and separate from the app's startup so the precedence
/// rules are testable without launching a UI.
///
/// Ported from LP-100A Monitor, minus its <c>--keep-data</c> switch: that exists there to protect a
/// transmission log, which is irreplaceable operating history. Marginal's records that cannot be
/// rebuilt (drives.json, the disc notes, the unbox backups) are never removed by an uninstall at
/// all, and settings.json goes only when the person ticks the box, so one prompt covers it and the
/// extra switch would be ceremony.
/// </summary>
public static class InstallCommandLine
{
    /// <summary>
    /// Read an <see cref="InstallRequest"/> from raw args. Unknown arguments are ignored — Avalonia
    /// and the OS both pass switches of their own, and an unrecognised one must never be mistaken
    /// for an instruction to modify the machine.
    /// </summary>
    /// <remarks>
    /// <c>--uninstall</c> beats <c>--install</c> if somebody passes both. Between two contradictory
    /// instructions, the one that ends up doing less to the machine is the safer reading.
    /// </remarks>
    public static InstallRequest Parse(IEnumerable<string>? args)
    {
        if (args is null) return new InstallRequest(InstallAction.None, false);

        var install = false;
        var uninstall = false;
        var quiet = false;

        foreach (var raw in args)
        {
            switch (Canonical(raw))
            {
                case "install": install = true; break;
                case "uninstall": uninstall = true; break;
                case "quiet": quiet = true; break;
            }
        }

        var action = uninstall ? InstallAction.Uninstall
                   : install ? InstallAction.Install
                   : InstallAction.None;

        return new InstallRequest(action, quiet);
    }

    /// <summary>
    /// Strip a leading <c>--</c>, <c>-</c> or <c>/</c> and lower-case, so <c>--Quiet</c>,
    /// <c>/quiet</c> and <c>-quiet</c> all read the same. Windows shortcuts and the installed-apps
    /// entry each have their own habits about switch prefixes.
    /// </summary>
    /// A word with no prefix is no switch: Marginal takes a folder on its
    /// command line, so `marginal install` (a folder called install) installed
    /// and `marginal uninstall quiet` uninstalled without a word (security
    /// review 2026-10-31, C5). `/quiet` on Linux and macOS is a path, not a
    /// switch, so `/` counts only on Windows.
    private static string Canonical(string arg)
    {
        var s = arg.Trim();
        if (s.StartsWith("--", StringComparison.Ordinal)) s = s[2..];
        else if (s.StartsWith('-') || (OperatingSystem.IsWindows() && s.StartsWith('/'))) s = s[1..];
        else return "";
        return s.ToLowerInvariant();
    }
}
