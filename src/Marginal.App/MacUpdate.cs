// FROM Deadwax (Deadwax.App/MacUpdate.cs, 704ed87, 2026-10-31), renamed to Marginal throughout.
// Dates and findings in the comments are Deadwax's history; a fix to the
// install or update logic goes to both (and AlbumWall's, from which Deadwax's came).
//
using System.Diagnostics;
using System.Runtime.InteropServices;
using Marginal.Core;

namespace Marginal.App;

/// Updating the Mac copy, which is a signed and notarized bundle, not one
/// executable: the release's Marginal-osx-arm64.zip holds Marginal.app whole
/// (tools/release.sh, made with ditto), and the whole bundle is what gets
/// replaced. Swapping only the program inside it would break the seal, which
/// Apple silicon refuses to run.
///
/// The checks are the security review's (2026-10-24, "Requirements for the
/// Mac self-update"): the zip must match SHA256SUMS (UpdateService, before
/// this), and the app in it must be signed by our team with our identifier,
/// notarized, and be the version the release says, newer than this one.
/// Since 0.7.0 (9700b18) the bundle carries its libraries inside it with
/// library validation on, so what was verified is all that runs.
public static class MacUpdate
{
    public const string BundleId = "io.github.gsa700.Marginal";
    public const string TeamId = "4QX5GG2276";

    /// What codesign must find: Apple's chain, a Developer ID Application
    /// certificate (the two Apple marker fields), our team and our identifier.
    /// The same thing macOS keys the app's permissions to, so an update that
    /// passes keeps them.
    public const string Requirement =
        "anchor apple generic" +
        " and certificate 1[field.1.2.840.113635.100.6.2.6] exists" +
        " and certificate leaf[field.1.2.840.113635.100.6.1.13] exists" +
        $" and certificate leaf[subject.OU] = \"{TeamId}\"" +
        $" and identifier \"{BundleId}\"";

    /// The Marginal.app this program runs from (…/Marginal.app/Contents/MacOS/Marginal), or null.
    public static string? AppBundle(string exePath)
    {
        var macos = Path.GetDirectoryName(exePath);
        var contents = Path.GetDirectoryName(macos ?? "");
        var app = Path.GetDirectoryName(contents ?? "");
        return Path.GetFileName(macos) == "MacOS" && Path.GetFileName(contents) == "Contents"
               && app is not null && app.EndsWith(".app", StringComparison.Ordinal) ? app : null;
    }

    /// Why this copy cannot replace itself, as a sentence to show; null when it can.
    public static string? Blocker(string exePath)
    {
        if (AppBundle(exePath) is not { } app)
            return "This copy is not inside Marginal.app, so it cannot update itself.";
        // Opened from the disk image, or straight from Downloads: macOS runs it
        // from a read-only copy of its own (App Translocation).
        if (app.Contains("/AppTranslocation/", StringComparison.Ordinal) || app.StartsWith("/Volumes/", StringComparison.Ordinal))
            return "Marginal is running from the disk image or from Downloads. Drag it to Applications to get updates.";
        var folder = Path.GetDirectoryName(app)!;
        if (access(folder, WOk) != 0 || access(app, WOk) != 0)
            return $"This account cannot replace Marginal in {folder}, so it cannot update itself.";
        return null;
    }

    private const int WOk = 2;
    [DllImport("libc", SetLastError = true)]
    private static extern int access(string path, int mode);

    /// Unpacks the checked zip with ditto (as it was made, so the signature
    /// and the stapled ticket survive) into unpacked, which must be empty,
    /// and returns the app in it once every check has passed. Throws with a
    /// sentence that can be shown when one fails.
    public static string UnpackAndVerify(string zip, string unpacked, string tag, string currentVersion)
    {
        Directory.CreateDirectory(unpacked);
        if (Run("/usr/bin/ditto", ["-x", "-k", zip, unpacked]).Code != 0)
            throw new InvalidOperationException("The download could not be unpacked. Nothing was installed.");
        var entries = Directory.GetFileSystemEntries(unpacked);
        var app = Path.Combine(unpacked, "Marginal.app");
        if (entries.Length != 1 || entries[0] != app || !Directory.Exists(app))
            throw new InvalidOperationException("The download does not hold Marginal.app alone. Nothing was installed.");
        Verify(app, tag, currentVersion);
        return app;
    }

    /// The checks on an unpacked Marginal.app, in order.
    public static void Verify(string app, string tag, string currentVersion)
    {
        var sign = Run("/usr/bin/codesign", ["--verify", "--deep", "--strict", "-R", "=" + Requirement, app]);
        if (sign.Code != 0)
            throw new InvalidOperationException($"The downloaded app is not signed as Marginal ({TeamId}). Nothing was installed.");

        var gatekeeper = Run("/usr/sbin/spctl", ["--assess", "--type", "execute", "-vv", app]);
        if (gatekeeper.Code != 0 || !gatekeeper.Output.Contains("source=Notarized Developer ID", StringComparison.Ordinal))
            throw new InvalidOperationException("The downloaded app is not notarized by Apple. Nothing was installed.");

        var plist = Path.Combine(app, "Contents", "Info.plist");
        if (PlistString(plist, "CFBundleIdentifier") != BundleId)
            throw new InvalidOperationException("The downloaded app is not Marginal. Nothing was installed.");
        var version = PlistString(plist, "CFBundleShortVersionString");
        // The version the release claims, and newer than this one: an older
        // build, genuinely signed, must never come back as an "update".
        if (version is null || version != tag.TrimStart('v', 'V'))
            throw new InvalidOperationException($"The downloaded app is version {version ?? "(none)"}, not {tag.TrimStart('v', 'V')}. Nothing was installed.");
        if (!VersionOrder.IsNewer(tag, currentVersion))
            throw new InvalidOperationException($"The downloaded app is version {version}, which is not newer than this one ({currentVersion}). Nothing was installed.");
    }

    private static string? PlistString(string plist, string key)
    {
        var r = Run("/usr/bin/plutil", ["-extract", key, "raw", "-o", "-", plist]);
        return r.Code == 0 ? r.Output.Trim() : null;
    }

    /// The helper, a script that takes every value as an argument, never as
    /// script text: $1 the process to wait for, $2 the verified app, $3 the
    /// installed app, $4 the failure marker, $5 the staging folder, $6 its
    /// own folder. The old bundle is moved aside, the new one renamed into
    /// its place, the old one deleted; if the new one cannot go in, the old
    /// one goes back and the marker says so. Relaunched with open -n, as
    /// Finder would.
    public const string ApplyScript = """
        #!/bin/sh
        pid=$1 staged=$2 target=$3 marker=$4 stage=$5 helper=$6
        while kill -0 "$pid" 2>/dev/null; do sleep 0.3; done
        old="$target.marginal-old-$$"
        if mv "$target" "$old"; then
          if mv "$staged" "$target"; then
            rm -rf "$old"
            rm -f "$marker"
          else
            rm -rf "$target"
            mv "$old" "$target"
            : > "$marker"
          fi
        else
          : > "$marker"
        fi
        /usr/bin/open -n "$target"
        rm -rf "$stage" "$helper"
        """;

    /// Starts the helper; THE CALLER MUST THEN EXIT THE APP.
    public static void Start(int pid, string stagedApp, string targetApp, string failedMarker, string stageRoot, string helperDir)
    {
        var sh = Path.Combine(helperDir, "apply-update.sh");
        File.WriteAllText(sh, ApplyScript + "\n");
        Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { sh, pid.ToString(System.Globalization.CultureInfo.InvariantCulture), stagedApp, targetApp, failedMarker, stageRoot, helperDir },
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    private static (int Code, string Output) Run(string file, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start) ?? throw new InvalidOperationException($"{file} would not start.");
        var stderr = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output + stderr.Result);
    }
}
