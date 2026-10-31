// Security review 2026-10-31, part C: what Marginal added to Deadwax's
// install code. (Deadwax's own protections are covered by Installer/.)

using Marginal.App.Install;
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

    // ---- C5: only dashed words are switches ----------------------------------------

    [Theory]
    [InlineData(new[] { "install" }, InstallAction.None, false)]
    [InlineData(new[] { "Uninstall", "quiet" }, InstallAction.None, false)]
    [InlineData(new[] { "/Users/me/Music/install" }, InstallAction.None, false)]
    [InlineData(new[] { "--install" }, InstallAction.Install, false)]
    [InlineData(new[] { "-Install", "--quiet" }, InstallAction.Install, true)]
    [InlineData(new[] { "--uninstall", "--quiet" }, InstallAction.Uninstall, true)]
    public void Only_dashed_words_are_switches(string[] args, InstallAction action, bool quiet)
    {
        var request = InstallCommandLine.Parse(args);
        Assert.Equal(action, request.Action);
        Assert.Equal(quiet, request.Quiet);
    }

    [Fact]
    public void A_slash_is_a_switch_only_on_windows() =>
        Assert.Equal(OperatingSystem.IsWindows() ? InstallAction.Uninstall : InstallAction.None,
            InstallCommandLine.Parse(["/uninstall"]).Action);
}

public sealed class NativeLoadingTests
{
    // ---- C2: system libraries from the system's places ---------------------------

    [Theory]
    [InlineData("libSkiaSharp", true)]
    [InlineData("libHarfBuzzSharp.so", true)]
    [InlineData("av_libglesv2.dll", true)]
    [InlineData("libpulse-simple.so.0", false)]
    [InlineData("libX11.so.6", false)]
    [InlineData("dwmapi.dll", false)]
    public void Only_the_shipped_libraries_come_from_beside_the_program(string library, bool beside) =>
        Assert.Equal(beside, Marginal.App.NativeLoading.IsBeside(library));

    [Fact]
    public void A_library_the_system_lacks_falls_back_to_the_default_search() =>
        Assert.Equal(IntPtr.Zero, Marginal.App.NativeLoading.Resolve("libmarginal-no-such-library.so.9", typeof(NativeLoadingTests).Assembly, null));

    [Fact]
    public void A_staged_program_must_say_its_version()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("marginal-ver-").FullName, "Marginal");
        File.WriteAllBytes(file, [.. new byte[100], .. "0.2.0+abc123"u8, .. new byte[100]]);
        Assert.True(Marginal.App.UpdateService.SaysVersion(file, "v0.2.0"));
        Assert.False(Marginal.App.UpdateService.SaysVersion(file, "v0.3.0"));
        Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
    }
}
