// FROM Deadwax (Deadwax.App/Install/InstallLayout.cs, 704ed87, 2026-10-31), renamed to Marginal throughout.
// Dates and findings in the comments are Deadwax's history; a fix to the
// install or update logic goes to both (and AlbumWall's, from which Deadwax's came).
//
namespace Marginal.App.Install;

/// <summary>How this copy of the app is running, decided entirely by where its executable sits.</summary>
public enum InstallMode
{
    /// <summary>
    /// Running from the per-user install directory. Listed in the OS's installed-apps list and
    /// updatable in place.
    /// </summary>
    Installed,

    /// <summary>
    /// A portable marker file sits beside the executable. Runs where it stands and registers
    /// nothing with the OS, so a copy on a USB stick leaves no trace on the machine.
    /// </summary>
    Portable,

    /// <summary>
    /// Anywhere else — typically a freshly unzipped download in Downloads. Offers to install
    /// itself on first run.
    /// </summary>
    Loose,
}

/// <summary>
/// Works out how a copy of the app is installed from nothing but its path. Deliberately pure: it
/// takes the directories and the marker's presence as arguments rather than reading the disk, so
/// every combination is testable without creating files or touching the registry. The App layer
/// supplies the real paths and performs the side effects.
///
/// Location IS the mode — there is no "installed" flag written anywhere to disagree with reality.
/// Copy an installed exe to the desktop and it becomes <see cref="InstallMode.Loose"/>; that is the
/// intended behaviour, not an edge case.
///
/// Ported from Shack Power, which had it from LP-100A Monitor — the shape all the family's
/// tools share.
/// </summary>
public static class InstallLayout
{
    /// <summary>Marker filename that pins a copy to <see cref="InstallMode.Portable"/>.</summary>
    public const string PortableMarker = "portable.txt";

    /// <summary>
    /// Folder name under the per-user programs directory, e.g.
    /// <c>%LOCALAPPDATA%\Programs\Marginal</c>. Human-readable rather than RID-suffixed, matching
    /// the convention the other station tools use.
    /// </summary>
    public const string ProductFolder = "Marginal";

    /// <summary>
    /// Folder name on Linux, under <c>~/.local/share</c>. Lower-case and hyphenated to match XDG
    /// convention, and — not incidentally — free of the space that would otherwise have to survive
    /// quoting in the <c>.desktop</c> entry's <c>Exec</c> line.
    /// </summary>
    public const string ProductFolderUnix = "marginal";

    /// <summary>
    /// Directory names that earlier hand-installs left behind, relative to the per-user programs
    /// directory. Unzipping a release in Explorer creates a folder named after the zip, which is how
    /// the station tools' copies ended up in folders like <c>ShackPower-win-x64</c> before they had
    /// installers. Marginal releases before 0.4.6 had no installer, so a copy may already sit in
    /// one of these after being unzipped straight into Programs by hand. These are treated as installed <em>in place</em> rather than as strays to
    /// be re-installed elsewhere, so upgrading doesn't leave an orphaned second copy behind.
    /// </summary>
    public static readonly string[] LegacyFolders =
    [
        "Marginal-win-x64",
        "Marginal-linux-x64",
    ];

    /// <summary>
    /// Path comparison appropriate to the running OS. Windows paths are case-insensitive; Linux
    /// paths are not, and treating them as if they were would let <c>~/Apps</c> masquerade as
    /// <c>~/apps</c>.
    /// </summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Compare two directory paths for equality, ignoring a trailing separator and (on Windows)
    /// case. Does not touch the filesystem, so it works for paths that don't exist.
    /// </summary>
    public static bool SamePath(string? a, string? b, StringComparison? comparison = null)
    {
        if (a is null || b is null) return false;
        return string.Equals(Normalize(a), Normalize(b), comparison ?? PathComparison);
    }

    private static string Normalize(string path) =>
        path.Replace(System.IO.Path.AltDirectorySeparatorChar, System.IO.Path.DirectorySeparatorChar)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar);

    /// <summary>Decide the mode for an executable living in <paramref name="exeDirectory"/>.</summary>
    /// <param name="exeDirectory">Directory holding the running executable.</param>
    /// <param name="portableMarkerPresent">
    /// Whether <see cref="PortableMarker"/> sits in that directory. Checked by the caller so this
    /// stays filesystem-free.
    /// </param>
    /// <param name="installDirectory">The canonical per-user install directory.</param>
    /// <param name="alsoInstalled">
    /// Additional directories to accept as installed — see <see cref="LegacyFolders"/>. Null or
    /// empty is fine.
    /// </param>
    /// <param name="comparison">Override the path comparison; defaults to <see cref="PathComparison"/>.</param>
    /// <remarks>
    /// The portable marker wins over everything, including the install directory. That is
    /// deliberate: it gives one unambiguous way to say "don't touch this machine" that works no
    /// matter where the copy happens to sit, and it can't be defeated by putting the file somewhere
    /// the app also recognises.
    /// </remarks>
    public static InstallMode Detect(
        string exeDirectory,
        bool portableMarkerPresent,
        string installDirectory,
        IEnumerable<string>? alsoInstalled = null,
        StringComparison? comparison = null)
    {
        if (portableMarkerPresent) return InstallMode.Portable;

        if (SamePath(exeDirectory, installDirectory, comparison)) return InstallMode.Installed;

        if (alsoInstalled is not null)
        {
            foreach (var dir in alsoInstalled)
            {
                if (SamePath(exeDirectory, dir, comparison)) return InstallMode.Installed;
            }
        }

        return InstallMode.Loose;
    }

    /// <summary>
    /// The canonical install directory beneath a per-user programs directory, e.g. passing
    /// <c>%LOCALAPPDATA%\Programs</c> yields <c>%LOCALAPPDATA%\Programs\Marginal</c>.
    /// </summary>
    public static string InstallDirectoryUnder(string programsDirectory) =>
        InstallDirectoryUnder(programsDirectory, OperatingSystem.IsWindows());

    /// <param name="windows">
    /// Which naming convention to use. Passed explicitly rather than sniffed so the choice is
    /// testable from either platform — cross-publishing means the build box and the target are
    /// routinely not the same machine.
    /// </param>
    public static string InstallDirectoryUnder(string programsDirectory, bool windows) =>
        System.IO.Path.Combine(programsDirectory, windows ? ProductFolder : ProductFolderUnix);

    /// <summary>
    /// Directories to accept as already-installed beneath a per-user programs directory — the
    /// canonical one plus every <see cref="LegacyFolders"/> entry.
    /// </summary>
    public static IEnumerable<string> InstalledDirectoriesUnder(string programsDirectory) =>
        InstalledDirectoriesUnder(programsDirectory, OperatingSystem.IsWindows());

    public static IEnumerable<string> InstalledDirectoriesUnder(string programsDirectory, bool windows)
    {
        yield return InstallDirectoryUnder(programsDirectory, windows);
        foreach (var legacy in LegacyFolders)
        {
            yield return System.IO.Path.Combine(programsDirectory, legacy);
        }
    }

    /// <summary>
    /// The native libraries a Native AOT build (0.8) has beside its program on Linux and Windows;
    /// a single-file build keeps them inside itself, so none of these are beside it. Install and
    /// update copy these and nothing else of what sits beside the program, since a loose copy may
    /// be sitting in Downloads. tools/release.sh checks a release carries exactly these.
    /// </summary>
    public static IReadOnlyList<string> NativeLibraries(bool windows) => windows
        ? ["libSkiaSharp.dll", "libHarfBuzzSharp.dll", "av_libglesv2.dll"]
        : ["libSkiaSharp.so", "libHarfBuzzSharp.so"];

    /// <summary>Those of <see cref="NativeLibraries(bool)"/> present in <paramref name="directory"/>.</summary>
    public static IReadOnlyList<string> NativeLibrariesIn(string directory) =>
        NativeLibraries(OperatingSystem.IsWindows()).Where(n => File.Exists(System.IO.Path.Combine(directory, n))).ToList();
}
