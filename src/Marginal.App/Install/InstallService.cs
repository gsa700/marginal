// FROM Deadwax (Deadwax.App/Install/InstallService.cs, 704ed87, 2026-10-31), renamed to Marginal throughout.
// Dates and findings in the comments are Deadwax's history; a fix to the
// install or update logic goes to both (and AlbumWall's, from which Deadwax's came).
//
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Marginal.App.Install;

/// <summary>What an uninstall should take with it besides the program itself.</summary>
/// <param name="RemoveSettings">
/// Delete settings.json and the registration log as well. False at every automatic call site:
/// only a person answering a question gets to throw their preferences away. Never the drive
/// offsets (drives.json), the saved disc notes or the unbox backups: an offset is measured from a
/// well-known disc, and the other two are his records, none of it rebuilt by reinstalling.
/// </param>
public readonly record struct UninstallOptions(bool RemoveSettings);

/// <summary>Outcome of an install.</summary>
/// <param name="ExePath">The installed executable.</param>
/// <param name="Registered">
/// Whether the desktop registration is verifiably in place. The install itself succeeded either
/// way — but when this is false on Windows it will not appear in Settings → Apps → Installed
/// apps, which is the only route most people have to uninstall it. Worth telling the user about.
/// </param>
public readonly record struct InstallResult(string ExePath, bool Registered);

/// <summary>
/// An install could not proceed for a reason the user can act on — almost always because the
/// installed copy is still running. Carries a message meant to be shown as it is.
/// </summary>
public sealed class InstallBlockedException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Installs and removes the per-user copy of the app. Ported from AlbumWall (2026-10-18), which had
/// it from Shack Power and W2 Monitor; keep the two in step. The reasoning behind the non-obvious
/// choices was paid for in the field by those tools and is kept: per-user, because the in-place
/// updater must never need elevation; reg.exe rather than the registry APIs; the whole
/// installed-apps entry in ONE reg import, because eleven spawns are eleven silent failure modes;
/// registration re-asserted at every launch and never check-and-skipped; every attempt leaving a
/// line in an audit log, because an attempt that leaves no trace is indistinguishable from one
/// that never happened.
///
/// What differs here, and why:
/// <list type="bullet">
/// <item>ONLY A PUBLISHED BUILD INSTALLS. Install copies the executable and, for a Native AOT
/// build (0.8), the libraries <see cref="InstallLayout.NativeLibraries(bool)"/> lists beside it,
/// and nothing else: the whole program for a published release, a broken fragment of a
/// development build.</item>
/// <item>Windows shortcuts go through <see cref="WindowsShell"/>, because they must carry the
/// AppUserModelID that groups the taskbar button.</item>
/// <item>The Linux layout is tools/install.sh's: the program in ~/.local/share/marginal and
/// <c>marginal.desktop</c>, whose name is the Wayland app_id (<see cref="WaylandShell.AppId"/>). A
/// copy that script installed is already Installed here.</item>
/// <item>NO ~/.local/bin LINK, unlike AlbumWall. "marginal" on a terminal reads as the command-line
/// tool, and this binary is the window: run with arguments it would still open and read the drive.</item>
/// <item>Never on macOS, where Marginal is a Marginal.app built by tools/make-macos-app.sh.</item>
/// </list>
/// </summary>
public static class InstallService
{
    private const string UninstallKey =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\Marginal";

    public const string DisplayName = App.DisplayName;

    private const string Description = "Notes in the margins of your music library: tags, art and lyrics";

    public const string Repo = "gsa700/marginal";

    public static string ExeFileName => OperatingSystem.IsWindows() ? "Marginal.exe" : "Marginal";

    /// <summary>Full path of the running executable.</summary>
    public static string ExePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine the current executable path.");

    public static string ExeDirectory => Path.GetDirectoryName(ExePath)!;

    /// <summary>
    /// True for a published release: one executable with everything inside it. A bundled assembly
    /// has no location on disk, which is the documented way to tell. Native AOT (0.8) answers the
    /// same way: its program has no assemblies on disk either. The analyzer's warning is for code
    /// that wants a path; this only wants to know that there is none.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "The empty location is the answer, not a path.")]
    public static bool IsSingleFile => string.IsNullOrEmpty(typeof(InstallService).Assembly.Location);

    /// <summary>The version this copy reports, without the source-revision suffix.</summary>
    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }

    /// <summary>Where settings live: ~/.config/marginal, on every platform.</summary>
    public static string DataDir => Path.GetDirectoryName(Settings.FilePath)!;

    /// <summary>The MusicBrainz answers Marginal keeps (MusicBrainzClient). A cache: it goes with the program.</summary>
    private static string CacheDir => Path.Combine(HomeDirectory, ".cache", "marginal");

    /// <summary>
    /// Per-user programs directory: <c>%LOCALAPPDATA%\Programs</c> on Windows,
    /// <c>~/.local/share</c> on Linux. DoNotVerify: by default the BCL returns "" for a folder
    /// that does not exist yet (a fresh account), and install would land in the current directory.
    /// </summary>
    public static string ProgramsDirectory
    {
        get
        {
            var b = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);
            return OperatingSystem.IsWindows() ? Path.Combine(b, "Programs") : b;
        }
    }

    private static string HomeDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Where the menu entry goes: <c>~/.local/share/applications</c>.</summary>
    private static string DesktopFilePath =>
        Path.Combine(ProgramsDirectory, "applications", DesktopEntry.FileName);

    /// <summary>Icon path in the XDG hicolor theme, at the 256px size the embedded PNG carries.</summary>
    private static string IconFilePath => Path.Combine(
        ProgramsDirectory, "icons", "hicolor", "256x256", "apps", "marginal.png");

    public static string InstallDirectory => InstallLayout.InstallDirectoryUnder(ProgramsDirectory);

    public static string InstalledExePath => Path.Combine(InstallDirectory, ExeFileName);

    /// <summary>Directories accepted as installed — the canonical one plus hand-unzipped ones.</summary>
    public static IEnumerable<string> InstalledDirectories =>
        InstallLayout.InstalledDirectoriesUnder(ProgramsDirectory);

    /// <summary>
    /// The user's desktop directory, or null if there isn't one. On Linux this comes from
    /// <c>XDG_DESKTOP_DIR</c>, never from the BCL's <c>$HOME/Desktop</c> guess — W2 Monitor's
    /// v0.7.0-beta symlink bug is why the BCL is not trusted here.
    /// </summary>
    private static string? DesktopDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                return string.IsNullOrEmpty(d) ? null : d;
            }

            var conf = Path.Combine(HomeDirectory, ".config", "user-dirs.dirs");
            var dir = XdgUserDirs.Resolve(TryReadAllText(conf), XdgUserDirs.DesktopKey, HomeDirectory);

            if (dir is null)
            {
                var guess = Path.Combine(HomeDirectory, "Desktop");
                return Directory.Exists(guess) ? guess : null;
            }
            return dir;
        }
    }

    /// <summary>Desktop shortcut this installer owns.</summary>
    private static string? DesktopShortcutPath => DesktopDirectory is { } d
        ? Path.Combine(d, OperatingSystem.IsWindows() ? DisplayName + ".lnk" : DesktopEntry.FileName)
        : null;

    /// <summary>How this copy is running. Derived from its path every time — never cached or stored.</summary>
    public static InstallMode Mode => InstallLayout.Detect(
        ExeDirectory,
        File.Exists(Path.Combine(ExeDirectory, InstallLayout.PortableMarker)),
        InstallDirectory,
        InstalledDirectories);

    /// <summary>
    /// Whether this copy should offer to install itself: a release build, sitting wherever it was
    /// unzipped, that nobody has pinned as portable.
    /// </summary>
    public static bool ShouldOfferInstall => CanInstall && IsSingleFile && Mode == InstallMode.Loose;

    /// <summary>
    /// False on macOS. The Unix path here is the Linux one: it would copy the program under
    /// Application Support and write .desktop files, none of which a Mac reads (AlbumWall, seen on
    /// the Mac Studio, 2026-10-14). A Mac install is Marginal.app in /Applications.
    /// </summary>
    public static bool CanInstall => !OperatingSystem.IsMacOS();

    /// <summary>
    /// Copy this executable into the install directory and register it. Returns the path of the
    /// installed copy, which the caller should launch before exiting. A single-file build has its
    /// native libraries inside it; a Native AOT build (0.8) has them beside it, and those named in
    /// <see cref="InstallLayout.NativeLibraries(bool)"/> go first, then the program. Nothing else
    /// beside it is copied: a loose copy may be sitting in Downloads. Settings live in
    /// <see cref="DataDir"/> either way.
    /// </summary>
    public static InstallResult Install()
    {
        if (!CanInstall)
            throw new InstallBlockedException(
                "Marginal is not installed this way on macOS. Use Marginal.app.");
        if (!IsSingleFile)
            throw new InstallBlockedException(
                "This is a development build, which is many files, and installing copies only one. "
                + "Publish a release build and install that instead.");

        Directory.CreateDirectory(InstallDirectory);

        var target = InstalledExePath;
        if (!InstallLayout.SamePath(ExeDirectory, InstallDirectory))
        {
            // Not under another copy: the installed one may be running, and its libraries must
            // not change beneath it (code review 2026-10-25: on Linux an overwritten library a
            // program has mapped can crash it).
            if (Process.GetProcessesByName("Marginal").Length > 1)
                throw new InstallBlockedException(
                    $"Another {DisplayName} is running. Close it and try installing again.");
            try
            {
                foreach (var lib in InstallLayout.NativeLibrariesIn(ExeDirectory))
                    CopyInto(Path.Combine(ExeDirectory, lib), Path.Combine(InstallDirectory, lib));
                CopyInto(ExePath, target);
            }
            catch (IOException ex)
            {
                throw new InstallBlockedException(
                    $"{DisplayName} is already running from the install folder. "
                    + "Close it and try installing again.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InstallBlockedException(
                    $"Could not write to {InstallDirectory}. Check the folder's permissions.", ex);
            }
        }

        if (!OperatingSystem.IsWindows()) MakeExecutable(target);

        return new InstallResult(target, Register(target, "install"));
    }

    /// <summary>
    /// Copies a file in under a temporary name and renames it over the old one, so nothing ever
    /// writes into a file a program has open: the old file lives on until it is let go of.
    /// </summary>
    private static void CopyInto(string source, string destination)
    {
        var temp = Path.Combine(Path.GetDirectoryName(destination)!, "." + Path.GetFileName(destination) + ".new");
        try
        {
            File.Copy(source, temp, overwrite: true);
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* gone already, which is the usual case */ }
        }
    }

    /// <summary>
    /// Register, and record what happened — every path writes exactly one log line.
    /// The desktop shortcut is made on the "install" trigger only: the startup re-assert must
    /// not put back a shortcut the user deleted (his report, 2026-09-30).
    /// </summary>
    public static bool Register(string exePath, string trigger)
    {
        var detail = "";
        var ok = false;
        var desktopShortcut = trigger == "install";
        try
        {
            ok = OperatingSystem.IsWindows()
                ? RegisterWindows(exePath, desktopShortcut, out detail)
                : RegisterUnix(exePath, desktopShortcut, out detail);
        }
        catch (Exception ex)
        {
            detail = $"threw {ex.GetType().Name}: {ex.Message}";
        }
        RecordAttempt(trigger, ok, detail);
        return ok;
    }

    /// <summary>The most recent attempt this process made, or the last one on file.</summary>
    public static RegistrationAttempt? LastAttempt
    {
        get
        {
            if (_lastAttempt is not null) return _lastAttempt;
            try
            {
                if (!File.Exists(LogFilePath)) return null;
                return File.ReadAllLines(LogFilePath)
                    .Select(RegistrationLog.Parse)
                    .LastOrDefault(a => a is not null);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    private static RegistrationAttempt? _lastAttempt;

    /// <summary>Audit trail of registration attempts, beside the settings it diagnoses alongside.</summary>
    public static string LogFilePath => Path.Combine(DataDir, "registration.log");

    private static void RecordAttempt(string trigger, bool succeeded, string detail)
    {
        var attempt = new RegistrationAttempt(DateTime.UtcNow, CurrentVersion, trigger, succeeded, detail);
        _lastAttempt = attempt;
        Console.WriteLine($"[install] {trigger}: {(succeeded ? "ok" : "NOT ok")} — {detail}");

        try
        {
            Directory.CreateDirectory(DataDir);
            var existing = File.Exists(LogFilePath) ? File.ReadAllLines(LogFilePath) : [];
            var kept = RegistrationLog.Tail(existing.Append(RegistrationLog.Format(attempt)));
            File.WriteAllLines(LogFilePath, kept);
        }
        catch (IOException) { /* the in-memory copy still reaches the UI */ }
        catch (UnauthorizedAccessException) { }
    }

    private static bool RegisterUnix(string exePath, bool desktopShortcut, out string detail)
    {
        var steps = new List<string>();
        string? icon = null;
        try
        {
            // REWRITTEN WHEN IT DIFFERS, not only when it is missing. The icon has already
            // changed once, and an installed copy that kept the first one forever would make
            // every later icon invisible on the machines that matter most.
            Directory.CreateDirectory(Path.GetDirectoryName(IconFilePath)!);
            using var src = Assembly.GetExecutingAssembly().GetManifestResourceStream("app-icon.png");
            if (src is not null)
            {
                using var mem = new MemoryStream();
                src.CopyTo(mem);
                var wanted = mem.ToArray();
                var current = File.Exists(IconFilePath) ? File.ReadAllBytes(IconFilePath) : null;
                if (current is null || !current.AsSpan().SequenceEqual(wanted))
                {
                    File.WriteAllBytes(IconFilePath, wanted);
                    steps.Add("icon written");
                }
                icon = IconFilePath;
            }
        }
        catch (IOException) { /* an entry without an icon still launches */ }
        catch (UnauthorizedAccessException) { }
        if (icon is null) steps.Add("no icon");

        var entry = DesktopEntry.Build(DisplayName, exePath, icon, Description);
        if (TryReadAllText(DesktopFilePath) != entry)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DesktopFilePath)!);
            File.WriteAllText(DesktopFilePath, entry);
            steps.Add("entry rewritten");
            Run("update-desktop-database", [Path.GetDirectoryName(DesktopFilePath)!]);
        }
        else
        {
            steps.Add("entry already current");
        }

        if (desktopShortcut) steps.Add(EnsureDesktopShortcut(exePath));

        var ok = File.Exists(DesktopFilePath);
        if (!ok) steps.Add("no .desktop entry on disk afterwards");
        detail = string.Join("; ", steps);
        return ok;
    }

    /// <summary>
    /// Put a launcher on the desktop, unless something is already there. <b>Never overwrites</b> —
    /// an existing file at that path is the user's. Runs at install only, never at startup or
    /// after an update: a shortcut the user deleted stays deleted.
    /// </summary>
    private static string EnsureDesktopShortcut(string exePath)
    {
        if (DesktopShortcutPath is not { } shortcut) return "no desktop directory";

        try
        {
            if (File.Exists(shortcut)) return "desktop shortcut already there";

            if (OperatingSystem.IsWindows())
            {
                if (WindowsShell.WriteShortcut(shortcut, exePath) is { } problem)
                    return $"desktop shortcut failed: {problem}";
            }
            else
            {
                var icon = File.Exists(IconFilePath) ? IconFilePath : null;
                File.WriteAllText(shortcut, DesktopEntry.Build(DisplayName, exePath, icon, Description));
                MakeExecutable(shortcut);   // an unexecutable .desktop shows an "untrusted" prompt
            }

            return File.Exists(shortcut) ? "desktop shortcut created" : "desktop shortcut could not be created";
        }
        catch (IOException ex) { return $"desktop shortcut failed: {ex.Message}"; }
        catch (UnauthorizedAccessException ex) { return $"desktop shortcut failed: {ex.Message}"; }
    }

    private static string? TryReadAllText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Write the installed-apps entry and the Start Menu shortcut, verified and retried rather
    /// than written once and assumed (LP-100A's field failure: a reg spawn that silently does
    /// not take looks exactly like success).
    /// </summary>
    private static bool RegisterWindows(string exePath, bool desktopShortcut, out string detail)
    {
        detail = "";
        if (!OperatingSystem.IsWindows()) { detail = "not Windows"; return false; }

        var dir = Path.GetDirectoryName(exePath)!;

        var wrote = WriteUninstallEntry(exePath, dir, out var importExit);
        var verified = wrote && IsRegistered();
        var retried = false;
        if (!verified)
        {
            retried = true;
            Thread.Sleep(250);
            wrote = WriteUninstallEntry(exePath, dir, out importExit);
            verified = wrote && IsRegistered();
        }

        // The Start Menu shortcut carries the AppUserModelID, which is why it goes through
        // WindowsShell and not Windows Script Host. It is REWRITTEN every time: the path it
        // points at is this installer's to keep right.
        var shortcut = WindowsShell.WriteShortcut(WindowsShell.ShortcutPath, exePath);
        var desktop = desktopShortcut ? EnsureDesktopShortcut(exePath) : null;

        detail = $"reg import exit {importExit}{(retried ? ", after retry" : "")}"
               + (verified ? "" : wrote ? ", but the verify query found no entry" : "")
               + (shortcut is null ? "; start menu ok" : $"; start menu: {shortcut}")
               + (desktop is null ? "" : $"; {desktop}");
        return verified;
    }

    /// <summary>
    /// The whole installed-apps entry in one <c>reg import</c> — one action that can fail loudly
    /// instead of eleven that can fail silently, and cheap enough to repeat on every launch.
    /// </summary>
    private static bool WriteUninstallEntry(string exePath, string dir, out int importExit)
    {
        importExit = -1;
        var values = new List<RegValue>
        {
            RegFile.Sz("DisplayName", DisplayName),
            RegFile.Sz("DisplayVersion", CurrentVersion),
            RegFile.Sz("Publisher", "David Erickson (AB0R)"),
            RegFile.Sz("DisplayIcon", exePath),
            RegFile.Sz("InstallLocation", dir),
            RegFile.Sz("URLInfoAbout", $"https://github.com/{Repo}"),

            // Windows gives the user no way to answer a dialog it did not expect, so the entry's
            // own button runs the quiet path — which keeps the settings.
            RegFile.Sz("UninstallString", $"\"{exePath}\" --uninstall"),
            RegFile.Sz("QuietUninstallString", $"\"{exePath}\" --uninstall --quiet"),

            RegFile.Dword("NoModify", 1),
            RegFile.Dword("NoRepair", 1),
        };

        var sizeKb = FileSizeKb(exePath);
        if (sizeKb > 0) values.Add(RegFile.Dword("EstimatedSize", sizeKb));

        var file = Path.Combine(Path.GetTempPath(), "marginal-register.reg");
        try
        {
            File.WriteAllText(file, RegFile.Build(UninstallKey, values), new UnicodeEncoding(false, true));
            importExit = Run(RegExe, ["import", file]);
            return importExit == 0;
        }
        catch (IOException) { importExit = -2; return false; }
        catch (UnauthorizedAccessException) { importExit = -3; return false; }
        finally
        {
            try { File.Delete(file); } catch { /* a leftover in temp is not worth failing over */ }
        }
    }

    /// <summary>
    /// Called at every startup. Re-asserts (never check-and-skip: the early-out is exactly how a
    /// lost entry stayed lost on W2) and adopts hand-unzipped copies where they stand.
    /// </summary>
    public static void EnsureRegistered(string trigger = "startup")
    {
        if (Mode != InstallMode.Installed)
        {
            // Said once, to the console only: a development run is not an event worth a line in
            // the audit log every single time it starts.
            Console.WriteLine($"[install] mode is {Mode}{(IsSingleFile ? "" : " (development build)")}; nothing to register");
            return;
        }
        Register(ExePath, trigger);
    }

    /// <summary>Whether the desktop environment already knows about this copy.</summary>
    public static bool IsRegistered() => OperatingSystem.IsWindows()
        ? Run(RegExe, ["query", UninstallKey, "/v", "DisplayName"]) == 0
        : File.Exists(DesktopFilePath);

    /// <summary>
    /// Where the .NET single-file host unpacks this app's native libraries (Avalonia's graphics libraries; libcdio and paranoia too until 0.7.4). It is
    /// the host's choice, not ours, and it differs by platform: <c>%TEMP%\.net\Marginal</c> on
    /// Windows, <c>~/.net/Marginal</c> on Linux, or under <c>DOTNET_BUNDLE_EXTRACT_BASE_DIR</c> if
    /// that is set. This used to be the Linux path on both, so on Windows the uninstall looked in
    /// the home folder, found nothing, and left the cache behind. (LP-100A, 2026-09-09.)
    /// </summary>
    /// <summary>
    /// The ONE folder under <see cref="ExtractionRoot"/> that belongs to this running copy, or null
    /// for a build that unpacks nothing (a development build). The host names the folder for the
    /// bundle, so two different builds never share one - and two copies CAN be running at once: an
    /// installed one and a loose one, or his player and a test. The updater removes this folder and
    /// no other. AlbumWall's first draft removed the whole root, and was one test run away from
    /// deleting its audio library - which loads only when something is first played - out from
    /// under the copy he was listening to. The host publishes where it unpacked in NATIVE_DLL_SEARCH_DIRECTORIES.
    /// </summary>
    internal static string? OwnExtractionDir
    {
        get
        {
            try
            {
                var root = Path.GetFullPath(ExtractionRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var dirs = AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "";
                foreach (var d in dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var full = Path.GetFullPath(d);
                    if (full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    {
                        // The first segment under the root: <root>/<bundle id>/...
                        var id = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)[0];
                        return Path.Combine(root, id);
                    }
                }
            }
            catch { /* then there is nothing to clean, which is safe */ }
            return null;
        }
    }

    /// <summary>
    /// Remove the unpacked folders of builds that are no longer running. The host unpacks each
    /// build once and never removes it, and the updater removes only the build it replaces; every
    /// build that arrived another way - a copy tried from Downloads, a test kit, a local publish -
    /// left its 30 MB behind. Hambench had 13 of them (2026-10-10) and Techbench 46, 1.1 GB.
    ///
    /// Only when this is the ONLY copy running. A folder is unsafe to touch while a copy runs
    /// from it, and the guard is deliberately blunter than asking which folder each copy uses:
    /// a native library may load only on first use (libcdio did, until 0.7.4), so a copy
    /// that has not read a disc yet may have none of them mapped, Windows would let them go, Linux
    /// always would, and that copy breaks the first time it reads.
    /// A lone copy can only be running from its own folder; the next lone start sweeps what a
    /// second copy kept alive. A Native AOT build (0.8) unpacks nothing, so everything there is
    /// an earlier build's and a lone AOT copy sweeps it all (code review 2026-10-25: it returned
    /// early instead, and the folders stayed for good). Nothing for a development build.
    /// </summary>
    public static void SweepStaleExtractions()
    {
        try
        {
            var mine = OwnExtractionDir;
            if (mine is null && !IsSingleFile) return;
            var root = mine is null ? ExtractionRoot : Path.GetDirectoryName(mine)!;
            if (!Directory.Exists(root)) return;
            if (Process.GetProcessesByName("Marginal").Length > 1)
            {
                Console.WriteLine("[install] another copy is running; leaving the unpacked folders alone");
                return;
            }
            var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var mineName = mine is null ? null : Path.GetFileName(mine.TrimEnd(Path.DirectorySeparatorChar));
            int removed = 0, kept = 0;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (mineName is not null && cmp.Equals(Path.GetFileName(dir), mineName)) continue;
                try { Directory.Delete(dir, recursive: true); removed++; }
                catch { kept++; }   // in use after all, or not ours to remove: next time
            }
            if (removed + kept > 0)
                Console.WriteLine($"[install] unpacked folders of older builds: {removed} removed"
                                + (kept > 0 ? $", {kept} could not be" : "") + $" ({root})");
        }
        catch { /* a cache; nothing depends on it being tidy */ }
    }

    internal static string ExtractionRoot
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
            if (string.IsNullOrEmpty(root))
                root = OperatingSystem.IsWindows()
                    ? Path.Combine(Path.GetTempPath(), ".net")
                    : Path.Combine(HomeDirectory, ".net");
            return Path.Combine(root, "Marginal");
        }
    }

    /// <summary>
    /// Remove the registrations, then hand off to a detached helper that deletes the install
    /// directory once this process has exited. The caller must exit immediately after.
    /// </summary>
    public static void Uninstall(UninstallOptions options)
    {
        // Where there is no installer (the Mac: Marginal.app, dragged to Applications) there is
        // nothing of its to undo, and --uninstall deleted the settings and the cache all the same
        // while saying it removed the program (security review 2026-10-24, L17).
        if (!CanInstall)
            throw new InstallBlockedException("On a Mac, Marginal is removed by dragging Marginal.app from Applications to the Bin.");
        Unregister();

        var toDelete = new List<string>();

        // Only ever remove a directory the app owns: a Loose copy's directory might be Downloads
        // itself. Installed directories are private to the app; shared directories are removed
        // one named file at a time in Unregister.
        if (Mode == InstallMode.Installed) toDelete.Add(ExeDirectory);
        toDelete.AddRange(DataFilesToRemove(options));

        // The single-file build unpacks its native libraries here. It is a cache and would be
        // recreated, so it goes with the program whatever was decided about the settings. All of
        // it only when no other copy is running: another copy's folder holds the libraries it
        // has loaded or will load on its first disc (security review 2026-10-24, L17); otherwise
        // this copy's own folder, and the next lone start sweeps the rest.
        if (Process.GetProcessesByName("Marginal").Length <= 1)
        {
            if (Directory.Exists(ExtractionRoot)) toDelete.Add(ExtractionRoot);
        }
        else if (OwnExtractionDir is { } own && Directory.Exists(own)) toDelete.Add(own);

        var pid = Environment.ProcessId;

        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(Path.GetTempPath(), "marginal-uninstall.ps1");
            var lines = new List<string>
            {
                $"while (Get-Process -Id {pid} -ErrorAction SilentlyContinue) {{ Start-Sleep -Milliseconds 300 }}",
            };
            // Retried for up to ten seconds rather than attempted once. The wait loop above sees
            // the process id vanish a moment before the executable's mapping is released, and a
            // single Remove-Item in that gap fails on the locked exe — silently, because the helper
            // has no window and no one to tell.
            //
            // THIS IS NOT THEORY. The first uninstall on Windows (test3, 2026-09-20) did exactly
            // that: shortcuts and the Installed-apps entry went, the 137 MB exe stayed, and when he
            // clicked his pinned taskbar icon it started — and, because registration is re-asserted
            // at every launch, put everything back. "I uninstalled ... then I clicked on the pinned
            // icon and it opened up?" The family already knew: LP-100A learned it on 2026-09-04 and
            // FlexPad carries the fix. This installer was ported from Shack Power, which does not.
            lines.AddRange(toDelete.Select(p =>
            {
                var q = PowerShell.Quote(p);
                return $"for ($i = 0; $i -lt 40 -and (Test-Path -LiteralPath {q}); $i++) {{ " +
                       $"Remove-Item -LiteralPath {q} -Recurse -Force -ErrorAction SilentlyContinue; " +
                       $"if (Test-Path -LiteralPath {q}) {{ Start-Sleep -Milliseconds 250 }} }}";
            }));
            lines.Add($"Remove-Item -LiteralPath {PowerShell.Quote(script)} -Force -ErrorAction SilentlyContinue");
            File.WriteAllText(script, string.Join("\n", lines) + "\n", PowerShell.FileEncoding);

            // PowerShell.StartInfo starts it by its full path, and in %TEMP%: the helper must not
            // inherit this process's working directory. An installed copy runs with its own folder
            // as the working directory (the shortcut sets it), and Windows will not remove a
            // directory that is any live process's current directory — including the one doing the
            // removing. Without this the helper deletes the files and then fails on the folder
            // itself, every time, from inside it. (LP-100A, 2026-09-04, by way of FlexPad.)
            Process.Start(PowerShell.StartInfo(script));
        }
        else
        {
            // A private folder of its own, never a fixed name in the shared /tmp (security review 2026-10-10).
            var script = Path.Combine(Directory.CreateTempSubdirectory("marginal-uninstall-").FullName, "uninstall.sh");
            var lines = new List<string>
            {
                "#!/bin/sh",
                $"while kill -0 {pid} 2>/dev/null; do sleep 0.3; done",
            };
            lines.AddRange(toDelete.Select(p => $"rm -rf {ShellQuote(p)}"));
            lines.Add($"rm -f {ShellQuote(script)}");
            File.WriteAllText(script, string.Join("\n", lines) + "\n");
            MakeExecutable(script);

            Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList = { script },
                UseShellExecute = false,
            });
        }
    }

    private static string ShellQuote(string path) => "'" + path.Replace("'", "'\\''") + "'";

    /// <summary>
    /// What besides the program an uninstall should take. NAMED FILES ONLY under the data
    /// directory, never the directory itself: it also holds drives.json and the disc notes, which
    /// stay whatever is decided about the settings.
    /// </summary>
    public static IEnumerable<string> DataFilesToRemove(UninstallOptions options)
    {
        // MusicBrainz answers, refetched on demand: a cache, so it goes with the program.
        if (Directory.Exists(CacheDir)) yield return CacheDir;

        if (!options.RemoveSettings) yield break;

        foreach (var name in new[] { "settings.json", "registration.log" })
        {
            var path = Path.Combine(DataDir, name);
            if (File.Exists(path)) yield return path;
        }
    }

    private static void Unregister()
    {
        if (DesktopShortcutPath is { } shortcut) TryDelete(shortcut);

        if (OperatingSystem.IsWindows())
        {
            Run(RegExe, ["delete", UninstallKey, "/f"]);
            TryDelete(WindowsShell.ShortcutPath);
            return;
        }

        // Each removed as a single file. ~/.local/bin and the icon theme are shared directories:
        // nothing here may delete a directory it does not own.
        TryDelete(DesktopFilePath);
        TryDelete(IconFilePath);
        // tools/install.sh used to put an SVG beside the PNG; a copy it installed leaves one.
        TryDelete(Path.Combine(ProgramsDirectory, "icons", "hicolor", "scalable", "apps", "marginal.svg"));
        Run("update-desktop-database", [Path.GetDirectoryName(DesktopFilePath)!]);
    }

    private static void TryDelete(string path)
    {
        try
        {
            // Ask the link itself as well as File.Exists: whether File.Exists follows a dangling
            // symlink varies by runtime.
            if (File.Exists(path) || Symlink.ResolveTarget(path) is not null)
                File.Delete(path);
        }
        catch (IOException) { /* a locked or vanished file is not worth failing an uninstall over */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Launch a copy of the app detached from this process. The working directory is set
    /// explicitly — inheriting this one's would pin the folder the user installed FROM.</summary>
    /// <remarks>On Unix, UseShellExecute is NOT detached: the child inherits this process's stdin,
    /// stdout and stderr and its controlling terminal. Found by AlbumWall, 2026-09-26 on Pop!_OS: `./AlbumWall`
    /// from a terminal, accept the install, and the installed copy kept logging into that terminal
    /// — and closing the terminal would have hung it up. So on Unix the child gets a new session
    /// (setsid, or FreeBSD's daemon(8); nohup as the POSIX floor) and /dev/null for all three
    /// streams.</remarks>
    public static void LaunchDetached(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath)!;
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = dir,
                UseShellExecute = true,
            });
            return;
        }

        const string detach =
            "if command -v setsid >/dev/null 2>&1; then setsid -f \"$0\"; " +
            "elif command -v daemon >/dev/null 2>&1; then daemon -f \"$0\"; " +
            "else nohup \"$0\" & fi";
        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            ArgumentList = { "-c", detach + " </dev/null >/dev/null 2>&1", exePath },
            WorkingDirectory = dir,
            UseShellExecute = false,
        });
    }

    private static int FileSizeKb(string path)
    {
        try { return (int)(new FileInfo(path).Length / 1024); }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static string RegExe => Path.Combine(Environment.SystemDirectory, "reg.exe");

    /// <summary>Run a console tool with no window and return its exit code (-1 if it wouldn't start).</summary>
    private static int Run(string fileName, IEnumerable<string> arguments)
    {
        // A bare name is looked up on PATH only (ToolPath; security review 2026-10-24, L16).
        if (!Path.IsPathFullyQualified(fileName))
        {
            if (Marginal.Core.ToolPath.Find(fileName) is not { } found) return -1;
            fileName = found;
        }
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return -1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception) { return -1; }
    }
}
