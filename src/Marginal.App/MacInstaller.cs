// FROM Deadwax (Deadwax.App/MacInstaller.cs, 704ed87, 2026-10-31), renamed to Marginal throughout.
// Dates and findings in the comments are Deadwax's history; a fix to the
// install or update logic goes to both (and AlbumWall's, from which Deadwax's came).
//
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;

namespace Marginal.App;

/// The Mac's install is Marginal.dmg: drag the app to Applications, start it
/// from there. Its window cannot hold a button of its own (a Finder window
/// runs no code), so the app does the rest the first time it starts: it
/// offers to eject the installer's disk, which is otherwise left mounted,
/// and says so when it is being run from that disk instead of from
/// Applications. A mounted Marginal.dmg was also what the first install took
/// for the CD drive (2026-10-24; CdDrive.FoundDevice no longer does).
internal static class MacInstaller
{
    /// Before the drive is first touched (InstallFlow.BeforeStartAsync).
    public static async Task OfferAsync(Window owner)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var exe = Environment.ProcessPath ?? "";
        if (MacUpdate.AppBundle(exe) is not { } app) return;   // a development build

        if (app.StartsWith("/Volumes/", StringComparison.Ordinal) || app.Contains("/AppTranslocation/", StringComparison.Ordinal))
        {
            await new ConfirmWindow("Marginal",
                "Marginal is running from its disk image.",
                affirmative: "OK", negative: null,
                detail: "Drag Marginal to Applications and start it from there: from here it cannot update itself, "
                      + "and the disk image can't be ejected while it runs.")
                .ShowDialog<bool>(owner);
            return;
        }

        foreach (var (mountPoint, name) in InstallerDisks())
        {
            var eject = await new ConfirmWindow("Eject the installer",
                $"Eject “{name}”?",
                affirmative: "Eject", negative: "Not now",
                detail: "Marginal is in Applications now, so its installer disk is no longer needed.")
                .ShowDialog<bool>(owner);
            if (!eject) continue;
            if (!Eject(mountPoint))
                await new ConfirmWindow("Eject the installer", $"“{name}” could not be ejected.",
                    affirmative: "OK", negative: null,
                    detail: "Something may still be using it. Eject it from the Finder's sidebar.")
                    .ShowDialog<bool>(owner);
        }
    }

    /// Mounted disk images with Marginal.app (our bundle id) at the top: the
    /// mount point and the disk's name, from hdiutil's own list. Never the
    /// disk this copy runs from, never a disk that is not an image.
    public static IReadOnlyList<(string MountPoint, string Name)> InstallerDisks()
    {
        var found = new List<(string, string)>();
        try
        {
            var plist = Run("/usr/bin/hdiutil", ["info", "-plist"], null);
            if (plist is null) return found;
            var json = Run("/usr/bin/plutil", ["-convert", "json", "-o", "-", "-"], plist);
            if (json is null) return found;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("images", out var images)) return found;
            foreach (var image in images.EnumerateArray())
            {
                if (!image.TryGetProperty("system-entities", out var entities)) continue;
                foreach (var e in entities.EnumerateArray())
                {
                    if (!e.TryGetProperty("mount-point", out var mp) || mp.GetString() is not { Length: > 0 } mountPoint) continue;
                    var app = Path.Combine(mountPoint, "Marginal.app");
                    var plistPath = Path.Combine(app, "Contents", "Info.plist");
                    if (!File.Exists(plistPath)) continue;
                    if ((Environment.ProcessPath ?? "").StartsWith(mountPoint + "/", StringComparison.Ordinal)) continue;
                    if (Run("/usr/bin/plutil", ["-extract", "CFBundleIdentifier", "raw", "-o", "-", plistPath], null)?.Trim() != MacUpdate.BundleId) continue;
                    found.Add((mountPoint, Path.GetFileName(mountPoint)));
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"[install] could not list disk images: {e.Message}");
        }
        return found;
    }

    private static bool Eject(string mountPoint)
    {
        var ok = Run("/usr/bin/hdiutil", ["detach", mountPoint], null) is not null;
        Console.WriteLine($"[install] eject {mountPoint}: {(ok ? "done" : "refused")}");
        return ok;
    }

    /// The tool's standard output, or null when it failed.
    private static string? Run(string file, IEnumerable<string> args, string? input)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, UseShellExecute = false,
        };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start);
        if (p is null) return null;
        if (input is not null) { p.StandardInput.Write(input); p.StandardInput.Close(); }
        var stderr = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        _ = stderr.Result;
        return p.ExitCode == 0 ? output : null;
    }
}
