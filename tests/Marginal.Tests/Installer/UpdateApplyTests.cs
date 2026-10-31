using System.Diagnostics;
using System.Reflection;
using Marginal.App;

namespace Marginal.Tests;

/// The Linux and Windows update helpers, run for real against stand-in
/// programs (0.7.6): a package is the program and its native libraries, the
/// libraries go in first, and a new program that falls over at once is taken
/// out again, the old files put back and the old program started.
public class UpdateApplyTests
{
    // ---- Linux: UpdateService.ApplyScript under /bin/sh ------------------------

    private const string Lib = "libSkiaSharp.so";

    /// A stand-in program: notes that it started, then lives `seconds` and exits with `code`.
    private static void Program(string path, string name, int code, double seconds = 0)
    {
        File.WriteAllText(path, $"#!/bin/sh\necho {name} >> \"$(dirname \"$0\")/started\"\nsleep {seconds}\nexit {code}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static (string Root, string From, string To, string Marker) Package(int newCode, double newSeconds, bool withLib = true)
    {
        var root = Directory.CreateTempSubdirectory("marginal-apply-test-").FullName;
        var from = Path.Combine(root, "stage", "ex");
        var to = Path.Combine(root, "installed");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);
        Program(Path.Combine(from, "Marginal"), "new", newCode, newSeconds);
        if (withLib) File.WriteAllText(Path.Combine(from, Lib), "new lib");
        Program(Path.Combine(to, "Marginal"), "old", 0);
        return (root, from, to, Path.Combine(to, ".marginal-update-failed"));
    }

    private static void RunHelper(string root, string from, string to, string marker, params string[] libraries)
    {
        var sh = Path.Combine(root, "apply.sh");
        File.WriteAllText(sh, UpdateService.ApplyScript + "\n");
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = root };
        foreach (var a in new[] { sh, "999999", from, to, "Marginal", marker, Path.Combine(root, "stage"), Path.Combine(root, "helper"), "", "1" })
            start.ArgumentList.Add(a);
        foreach (var l in libraries) start.ArgumentList.Add(l);
        start.ArgumentList.Add("--");
        using var p = Process.Start(start)!;
        Assert.True(p.WaitForExit(30_000), "the helper did not finish");
        Thread.Sleep(500);   // the relaunched stand-in writes its line in the background
    }

    private static string Started(string to) =>
        File.Exists(Path.Combine(to, "started")) ? File.ReadAllText(Path.Combine(to, "started")).Replace("\n", " ").Trim() : "";

    [Fact]
    public void Linux_a_package_goes_in_libraries_and_all_and_the_new_program_stays()
    {
        if (OperatingSystem.IsWindows()) return;
        var (root, from, to, marker) = Package(newCode: 0, newSeconds: 3);
        try
        {
            File.WriteAllText(marker, "");
            RunHelper(root, from, to, marker, Lib);
            Assert.Contains("echo new", File.ReadAllText(Path.Combine(to, "Marginal")));
            Assert.Equal("new lib", File.ReadAllText(Path.Combine(to, Lib)));
            Assert.Equal("new", Started(to));
            Assert.False(File.Exists(marker));
            Assert.False(Directory.Exists(Path.Combine(root, "stage")));
            Assert.False(Directory.Exists(Path.Combine(root, "helper")));
        }
        finally { Thread.Sleep(3000); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Linux_files_go_in_by_rename_so_a_running_copy_keeps_what_it_has_open()
    {
        if (OperatingSystem.IsWindows()) return;
        var (root, from, to, marker) = Package(newCode: 0, newSeconds: 3);
        try
        {
            File.WriteAllText(Path.Combine(to, Lib), "old lib");
            using var held = new FileStream(Path.Combine(to, Lib), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            RunHelper(root, from, to, marker, Lib);
            Assert.Equal("new lib", File.ReadAllText(Path.Combine(to, Lib)));
            Assert.Equal("old lib", new StreamReader(held).ReadToEnd());   // written in place, this would read "new lib"
            Assert.Empty(Directory.GetFiles(to, ".*.new"));
        }
        finally { Thread.Sleep(3000); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Linux_a_new_program_that_falls_over_is_taken_out_and_the_old_one_started()
    {
        if (OperatingSystem.IsWindows()) return;
        var (root, from, to, marker) = Package(newCode: 3, newSeconds: 0);
        try
        {
            RunHelper(root, from, to, marker, Lib);
            Assert.Contains("echo old", File.ReadAllText(Path.Combine(to, "Marginal")));
            Assert.False(File.Exists(Path.Combine(to, Lib)));   // it was not there before
            Assert.Equal("new old", Started(to));
            Assert.True(File.Exists(marker));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Linux_a_package_that_cannot_be_copied_leaves_the_old_files()
    {
        if (OperatingSystem.IsWindows()) return;
        var (root, from, to, marker) = Package(newCode: 0, newSeconds: 3, withLib: false);
        try
        {
            File.WriteAllText(Path.Combine(to, Lib), "old lib");
            RunHelper(root, from, to, marker, Lib);   // the library is named but missing: the copy fails
            Assert.Contains("echo old", File.ReadAllText(Path.Combine(to, "Marginal")));
            Assert.Equal("old lib", File.ReadAllText(Path.Combine(to, Lib)));
            Assert.Equal("old", Started(to));
            Assert.True(File.Exists(marker));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_single_file_package_has_no_libraries_beside_it()
    {
        var dir = Directory.CreateTempSubdirectory("marginal-libs-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, UpdateService.ExeFileName), "x");
            File.WriteAllText(Path.Combine(dir, "LICENSE"), "x");
            Assert.Empty(Marginal.App.Install.InstallLayout.NativeLibrariesIn(dir));
            var lib = Marginal.App.Install.InstallLayout.NativeLibraries(OperatingSystem.IsWindows())[0];
            File.WriteAllText(Path.Combine(dir, lib), "x");
            Assert.Equal([lib], Marginal.App.Install.InstallLayout.NativeLibrariesIn(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_program_is_the_one_at_the_top_of_the_package_not_the_cli()
    {
        var dir = Directory.CreateTempSubdirectory("marginal-find-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "cli"));
            File.WriteAllText(Path.Combine(dir, "cli", "marginal.exe"), "cli");      // sorts first, and matches when case is ignored
            Assert.Null(UpdateService.FindProgram(dir, "Marginal.exe"));
            File.WriteAllText(Path.Combine(dir, "Marginal.exe"), "app");
            Assert.Equal("app", File.ReadAllText(UpdateService.FindProgram(dir, "Marginal.exe")!));
            Directory.CreateDirectory(Path.Combine(dir, "a"));
            File.Move(Path.Combine(dir, "Marginal.exe"), Path.Combine(dir, "a", "Marginal.exe"));
            Assert.Equal("app", File.ReadAllText(UpdateService.FindProgram(dir, "Marginal.exe")!));   // a folder below, named exactly
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- Windows: the PowerShell helper, on Windows only -------------------------

    private static string WindowsScript(string from, string to, string marker, string root, string[] libraries, string[] args) =>
        (string)typeof(UpdateService).GetMethod("WindowsApplyScript", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [999999, from, to, "Marginal.exe", libraries, marker, Path.Combine(root, "stage"), null, Path.Combine(root, "helper"), args, 1])!;

    /// The helper's own output, for the failure messages.
    private static string RunWindows(string root, string script)
    {
        var ps1 = Path.Combine(root, "apply.ps1");
        File.WriteAllText(ps1, script);
        var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var log = Path.Combine(root, "helper.log");
        using var p = Process.Start(new ProcessStartInfo(exe)
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", $"& '{ps1}' *> '{log}'" },
            UseShellExecute = false,
        })!;
        Assert.True(p.WaitForExit(60_000), "the helper did not finish");
        return File.Exists(log) ? File.ReadAllText(log) : "(no output)";
    }

    /// Real programs stand in: cmd.exe as the new one (its arguments decide how
    /// it ends) and whoami.exe as the old one, told apart by their bytes.
    private static (string Root, string From, string To, string Marker) WindowsPackage()
    {
        var root = Directory.CreateTempSubdirectory("marginal-apply-test-").FullName;
        var from = Path.Combine(root, "stage", "ex");
        var to = Path.Combine(root, "installed");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(from, "Marginal.exe"));
        File.WriteAllText(Path.Combine(from, "libSkiaSharp.dll"), "new lib");
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), Path.Combine(to, "Marginal.exe"));
        return (root, from, to, Path.Combine(to, ".marginal-update-failed"));
    }

    /// A stand-in still holding its file must not fail the test; temp is cleaned eventually.
    private static void TryDelete(string root)
    {
        for (var i = 0; i < 40; i++)
            try { Directory.Delete(root, recursive: true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
    }

    [Fact]
    public void Windows_a_package_goes_in_libraries_and_all_and_the_new_program_stays()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (root, from, to, marker) = WindowsPackage();
        try
        {
            File.WriteAllText(marker, "");
            var fresh = File.ReadAllBytes(Path.Combine(from, "Marginal.exe"));   // the helper deletes the staging folder
            var output = RunWindows(root, WindowsScript(from, to, marker, root, ["libSkiaSharp.dll"], ["/c", "ping", "-n", "4", "127.0.0.1"]));
            Assert.True(fresh.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(to, "Marginal.exe"))), "program not replaced: " + output);
            Assert.True(File.Exists(Path.Combine(to, "libSkiaSharp.dll")), "library not copied: " + output);
            Assert.False(File.Exists(marker), "marked as failed: " + output);
        }
        finally { Thread.Sleep(4000); TryDelete(root); }
    }

    [Fact]
    public void Windows_a_new_program_that_falls_over_is_taken_out_and_the_old_one_started()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (root, from, to, marker) = WindowsPackage();
        var old = File.ReadAllBytes(Path.Combine(to, "Marginal.exe"));
        try
        {
            var output = RunWindows(root, WindowsScript(from, to, marker, root, ["libSkiaSharp.dll"], ["/c", "exit", "3"]));
            Assert.True(old.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(to, "Marginal.exe"))), "old program not put back: " + output);
            Assert.False(File.Exists(Path.Combine(to, "libSkiaSharp.dll")), "new library left behind: " + output);
            Assert.True(File.Exists(marker), "not marked as failed: " + output);
        }
        finally { Thread.Sleep(2000); TryDelete(root); }
    }
}
