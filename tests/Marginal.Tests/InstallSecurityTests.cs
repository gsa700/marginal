// Security review 2026-10-31, part C: what Marginal added to Deadwax's
// install code. (Deadwax's own protections are covered by Installer/.)

using Marginal.Core;

namespace Marginal.Tests;

public sealed class InstallSecurityTests
{
    // ---- C1: uninstalling must not take the backups with it ----------------------

    [Fact]
    public void Linux_backups_are_not_in_the_install_folder()
    {
        var install = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify), "marginal");
        var backups = TagWriter.BackupRootFor(linux: true);
        Assert.False(backups.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.Ordinal), backups);
        Assert.EndsWith(Path.Combine("marginal", "backups"), backups);
    }

    [Fact]
    public void Backups_are_not_in_the_install_folder_here_either()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;   // the Mac installs no folder
        var install = InstallService.InstallDirectory;
        Assert.False(TagWriter.DefaultBackupRoot.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            $"{TagWriter.DefaultBackupRoot} is inside {install}");
    }

    [Fact]
    public void Old_backups_move_once_and_never_over_new_ones()
    {
        var root = Directory.CreateTempSubdirectory("marginal-move-").FullName;
        try
        {
            var from = Path.Combine(root, "share", "marginal", "backups");
            var to = Path.Combine(root, "state", "marginal", "backups");
            Directory.CreateDirectory(Path.Combine(from, "20261031-120000-000"));
            File.WriteAllText(Path.Combine(from, "20261031-120000-000", "manifest.tsv"), "x");
            Backups.MoveOnce(from, to);
            Assert.True(File.Exists(Path.Combine(to, "20261031-120000-000", "manifest.tsv")));
            Assert.False(Directory.Exists(from));

            Directory.CreateDirectory(from);
            File.WriteAllText(Path.Combine(from, "other"), "y");
            Backups.MoveOnce(from, to);
            Assert.True(File.Exists(Path.Combine(from, "other")));     // the new place has backups: left alone
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // C5 (only dashed words are switches) and the staged program's version are
    // tested in Spindle (InstallCommandLineTests), with the code, since 2026-11-02.
}

// C2 (system libraries from the system's places) is tested in Spindle
// (NativeLoadingTests), with the code, since 2026-11-05.
