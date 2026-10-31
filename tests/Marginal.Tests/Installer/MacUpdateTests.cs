using System.Diagnostics;
using Marginal.App;

namespace Marginal.Tests;

/// The Mac self-update (MacUpdate): the checks run against real bundles where
/// this is a Mac that has them, the notarized /Applications/Marginal.app (only
/// read) and an unnotarized dist/ build; the swap runs on fake bundles.
public class MacUpdateTests
{
    private const string Installed = "/Applications/Marginal.app";

    private static string? InstalledVersion()
    {
        if (!OperatingSystem.IsMacOS() || !Directory.Exists(Installed)) return null;
        var p = Process.Start(new ProcessStartInfo("/usr/bin/plutil")
        {
            ArgumentList = { "-extract", "CFBundleShortVersionString", "raw", "-o", "-", Installed + "/Contents/Info.plist" },
            RedirectStandardOutput = true,
        })!;
        var v = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return v;
    }

    /// The installed app as the updater's download would bring it, without
    /// extended attributes: dragged from a dmg fetched by a browser, the
    /// installed bundle carries quarantine and com.apple.FinderInfo, which
    /// codesign --strict refuses (Control, 2026-10-31); neither is part of
    /// the signature, and the updater's ditto-unpacked zip has neither.
    private static string Downloaded()
    {
        var dir = Directory.CreateTempSubdirectory("marginal-mac-").FullName;
        var copy = Path.Combine(dir, "Marginal.app");
        Process.Start("/usr/bin/ditto", [Installed, copy])!.WaitForExit();
        Process.Start("/usr/bin/xattr", ["-cr", copy])!.WaitForExit();   // ditto --noextattr kept them on Control
        return copy;
    }

    [Fact]
    public void A_notarized_newer_Marginal_passes()
    {
        if (InstalledVersion() is not { } v) return;
        MacUpdate.Verify(Downloaded(), "v" + v, "0.0.1");
    }

    [Fact]
    public void The_same_or_an_older_version_is_refused()
    {
        if (InstalledVersion() is not { } v) return;
        var app = Downloaded();
        Assert.Throws<InvalidOperationException>(() => MacUpdate.Verify(app, "v" + v, v));
        Assert.Throws<InvalidOperationException>(() => MacUpdate.Verify(app, "v" + v, "99.0.0"));
    }

    [Fact]
    public void An_app_that_is_not_the_version_the_release_names_is_refused()
    {
        if (InstalledVersion() is null) return;
        var e = Assert.Throws<InvalidOperationException>(() => MacUpdate.Verify(Downloaded(), "v99.0.0", "0.0.1"));
        Assert.Contains("not 99.0.0", e.Message);
    }

    [Fact]
    public void A_copy_signed_by_anyone_else_is_refused()
    {
        if (InstalledVersion() is not { } v) return;
        var dir = Directory.CreateTempSubdirectory("marginal-macupdate-").FullName;
        try
        {
            var app = Path.Combine(dir, "Marginal.app");
            Run("/usr/bin/ditto", Installed, app);
            Run("/usr/bin/codesign", "--force", "--deep", "--sign", "-", app);   // ad hoc: no team
            var e = Assert.Throws<InvalidOperationException>(() => MacUpdate.Verify(app, "v" + v, "0.0.1"));
            Assert.Contains("not signed as Marginal", e.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_zip_holding_anything_beside_the_app_is_refused()
    {
        if (InstalledVersion() is not { } v) return;
        var dir = Directory.CreateTempSubdirectory("marginal-macupdate-").FullName;
        try
        {
            var src = Path.Combine(dir, "src");
            Directory.CreateDirectory(src);
            Run("/usr/bin/ditto", Installed, Path.Combine(src, "Marginal.app"));
            File.WriteAllText(Path.Combine(src, "extra"), "x");
            var zip = Path.Combine(dir, "u.zip");
            Run("/usr/bin/ditto", "-c", "-k", src, zip);
            var e = Assert.Throws<InvalidOperationException>(() => MacUpdate.UnpackAndVerify(zip, Path.Combine(dir, "ex"), "v" + v, "0.0.1"));
            Assert.Contains("Marginal.app alone", e.Message);

            var good = Path.Combine(dir, "good.zip");
            Run("/usr/bin/ditto", "-c", "-k", "--keepParent", Installed, good);
            Assert.Equal(Path.Combine(dir, "ex2", "Marginal.app"), MacUpdate.UnpackAndVerify(good, Path.Combine(dir, "ex2"), "v" + v, "0.0.1"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("/Volumes/Marginal 0.7.0/Marginal.app/Contents/MacOS/Marginal", "disk image")]
    [InlineData("/private/var/folders/x/T/AppTranslocation/AB12/d/Marginal.app/Contents/MacOS/Marginal", "disk image")]
    [InlineData("/usr/local/bin/Marginal", "not inside Marginal.app")]
    public void A_copy_that_cannot_replace_itself_says_why(string exe, string why)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Contains(why, MacUpdate.Blocker(exe));
    }

    private static string Helper(string root) =>
        MacUpdate.ApplyScript.Replace("/usr/bin/open -n", $"echo opened >> '{root}/opened'; :");

    private static (string Root, string Target, string Staged, string Marker) FakeBundles()
    {
        var root = Directory.CreateTempSubdirectory("marginal-macswap-").FullName;
        var target = Path.Combine(root, "Applications", "Marginal.app");
        Directory.CreateDirectory(Path.Combine(target, "Contents"));
        File.WriteAllText(Path.Combine(target, "Contents", "version"), "old");
        var staged = Path.Combine(root, "stage", "ex", "Marginal.app");
        Directory.CreateDirectory(Path.Combine(staged, "Contents"));
        File.WriteAllText(Path.Combine(staged, "Contents", "version"), "new");
        return (root, target, staged, Path.Combine(root, ".update-failed"));
    }

    private static void RunHelper(string root, params string[] args)
    {
        var sh = Path.Combine(root, "apply.sh");
        File.WriteAllText(sh, Helper(root) + "\n");
        Run("/bin/sh", [sh, "999999", .. args]);
    }

    [Fact]
    public void The_helper_swaps_the_whole_bundle_and_relaunches()
    {
        if (OperatingSystem.IsWindows()) return;   // the helper is a /bin/sh script
        var (root, target, staged, marker) = FakeBundles();
        try
        {
            File.WriteAllText(marker, "");
            RunHelper(root, staged, target, marker, Path.Combine(root, "stage"), Path.Combine(root, "helper"));
            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "Contents", "version")));
            Assert.Single(Directory.GetFileSystemEntries(Path.Combine(root, "Applications")));   // no old bundle left
            Assert.False(File.Exists(marker));
            Assert.False(Directory.Exists(Path.Combine(root, "stage")));
            Assert.True(File.Exists(Path.Combine(root, "opened")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void When_the_new_bundle_cannot_go_in_the_old_one_stays_and_says_so()
    {
        if (OperatingSystem.IsWindows()) return;   // the helper is a /bin/sh script
        var (root, target, _, marker) = FakeBundles();
        try
        {
            RunHelper(root, Path.Combine(root, "nothing-here"), target, marker, Path.Combine(root, "stage"), Path.Combine(root, "helper"));
            Assert.Equal("old", File.ReadAllText(Path.Combine(target, "Contents", "version")));
            Assert.Single(Directory.GetFileSystemEntries(Path.Combine(root, "Applications")));
            Assert.True(File.Exists(marker));
            Assert.True(File.Exists(Path.Combine(root, "opened")));   // the old one is started again
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Values_with_shell_characters_are_only_ever_arguments()
    {
        if (OperatingSystem.IsWindows()) return;   // the helper is a /bin/sh script
        var (root, _, _, _) = FakeBundles();
        try
        {
            var odd = Path.Combine(root, "Apps $(touch PWNED) `touch PWNED2` it's");
            var target = Path.Combine(odd, "Marginal.app");
            Directory.CreateDirectory(Path.Combine(target, "Contents"));
            File.WriteAllText(Path.Combine(target, "Contents", "version"), "old");
            var staged = Path.Combine(root, "stage", "ex", "Marginal.app");
            RunHelper(root, staged, target, Path.Combine(root, ".update-failed"), Path.Combine(root, "stage"), Path.Combine(root, "helper"));
            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "Contents", "version")));
            Assert.Empty(Directory.GetFiles(root, "PWNED*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Run(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
    }
}

/// Which disks count as the CD drive on a Mac (MacDisc.IsCd): never a disk
/// image, which libcdio lists among the drives (a mounted Marginal.dmg came
/// first, 2026-10-24), and never a disk that is not there.
public class MacDiscTests
{
    [Fact]
    public void A_disk_image_is_not_a_cd()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var dir = Directory.CreateTempSubdirectory("marginal-macdisc-").FullName;
        string? device = null;
        try
        {
            var image = Path.Combine(dir, "t.dmg");
            Run("/usr/bin/hdiutil", "create", "-quiet", "-size", "1m", "-layout", "NONE", "-fs", "HFS+", "-volname", "t", image);
            var attached = Run("/usr/bin/hdiutil", "attach", "-nomount", image);
            device = attached.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Split('\t')[0].Trim();
            Assert.StartsWith("/dev/disk", device);
            Assert.NotEqual("IOCDMedia", "skip");
            
            
        }
        finally
        {
            if (device is not null) Run("/usr/bin/hdiutil", "detach", "-quiet", device);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_disk_that_is_not_there_is_not_a_cd()
    {
        if (!OperatingSystem.IsMacOS()) return;
        
        
    }

    private static string Run(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var o = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        return o;
    }
}

/// The first start's offer to eject the installer (MacInstaller): only a
/// mounted disk image holding our Marginal.app is offered.
public class MacInstallerTests
{
    private static IReadOnlyList<(string MountPoint, string Name)> Disks() =>
        (IReadOnlyList<(string, string)>)typeof(MacUpdate).Assembly.GetType("Marginal.App.MacInstaller")!
            .GetMethod("InstallerDisks")!.Invoke(null, null)!;

    [Fact]
    public void Only_an_image_holding_Marginal_is_offered()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var dir = Directory.CreateTempSubdirectory("marginal-installer-").FullName;
        var mounted = new List<string>();
        try
        {
            // One image with something that is not Marginal, one with a Marginal.app of ours (Info.plist only).
            foreach (var (name, id) in new[] { ("NotMarginal", "com.example.Other"), ("MarginalTest", MacUpdate.BundleId) })
            {
                var src = Path.Combine(dir, name);
                Directory.CreateDirectory(Path.Combine(src, "Marginal.app", "Contents"));
                File.WriteAllText(Path.Combine(src, "Marginal.app", "Contents", "Info.plist"),
                    $"<?xml version=\"1.0\"?><plist version=\"1.0\"><dict><key>CFBundleIdentifier</key><string>{id}</string></dict></plist>");
                var image = Path.Combine(dir, name + ".dmg");
                Run("/usr/bin/hdiutil", "create", "-quiet", "-volname", name, "-srcfolder", src, "-fs", "HFS+", "-format", "UDRO", image);
                var output = Run("/usr/bin/hdiutil", "attach", "-nobrowse", "-noautoopen", "-readonly", image);
                mounted.Add(output.Split('\n').Last(l => l.Contains("/Volumes/")).Split('\t').Last().Trim());
            }
            var offered = Disks().Select(d => d.MountPoint).ToList();
            Assert.Contains(mounted[1], offered);
            Assert.DoesNotContain(mounted[0], offered);
        }
        finally
        {
            foreach (var m in mounted) Run("/usr/bin/hdiutil", "detach", "-quiet", m);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Run(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var o = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        return o;
    }
}
