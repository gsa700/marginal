using System.Runtime.CompilerServices;

namespace Marginal.Tests;

/// The install and update code is Spindle's and is told which app it is: here,
/// Marginal, as Program.Main tells it, before any test runs.
internal static class MarginalAppInfo
{
    [ModuleInitializer]
    internal static void Use() => Spindle.Install.AppInfo.Use(Marginal.App.Program.Identity);
}

public class AppInfoTests
{
    /// The version the update check compares is this app's own (found
    /// 2026-11-02 before release: from Spindle it said 1.0.0, so no copy would
    /// have been offered an update).
    [Fact]
    public void The_update_check_compares_this_apps_version() =>
        Assert.Equal(typeof(Marginal.App.Program).Assembly.GetName().Version!.ToString(3), Spindle.Install.InstallService.CurrentVersion);

    [Fact]
    public void The_identity_follows_the_apps_conventions()
    {
        var app = Spindle.Install.AppInfo.Current;
        Assert.Equal("Marginal", app.Name);
        Assert.Equal("gsa700/marginal", app.Repo);
        Assert.Equal("io.github.gsa700.Marginal", app.BundleId);
        Assert.Equal("gsa700.Marginal", app.WindowsAppId);
        Assert.NotNull(app.AppAssembly.GetManifestResourceStream("app-icon.png"));     // the Linux install's icon
    }
}
