// FROM Deadwax (Deadwax.App/UpdateService.cs, 704ed87, 2026-10-31), renamed to Marginal throughout.
// Dates and findings in the comments are Deadwax's history; a fix to the
// install or update logic goes to both (and AlbumWall's, from which Deadwax's came).
//
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Marginal.Core;

namespace Marginal.App;

/// What a check found. Error is a sentence that can be shown as it is.
public sealed class UpdateInfo
{
    public string CurrentVersion { get; init; } = "";
    public string LatestTag { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public string ReleaseUrl { get; set; } = "";
    public string? AssetUrl { get; set; }
    public string? AssetName { get; set; }
    public string? SumsUrl { get; set; }
    /// The feed answered 404: no full release yet, or the repository is private.
    public bool NothingPublished { get; set; }
    /// The release offered is an Edge build (a GitHub pre-release).
    public bool IsPrerelease { get; set; }
    public string? Error { get; set; }
}

/// The in-app updater, ported from AlbumWall (Install/UpdateService.cs) with the
/// parts Marginal does not have taken out: no kiosk supervision, no install
/// service. Linux, Windows and macOS alike since 0.3.1. What is
/// kept is what matters: ask GitHub for the latest release, download the build
/// for this platform, refuse it unless the release's SHA256SUMS vouches for it,
/// then hand over to a helper that waits for exit, swaps the executable and
/// relaunches it. A program fetching a program and running it is verified or
/// not installed.
///
/// A release, as this reads it (tools/release.sh writes exactly this): a
/// normal, non-pre-release GitHub release tagged vX.Y.Z carrying
/// Marginal-linux-x64.zip (the single-file program) and SHA256SUMS. From 0.8
/// Linux and Windows carry Marginal-<rid>-aot.zip instead: the Native AOT
/// program with its native libraries beside it. Copies before 0.7.6 install
/// only the program and do not look for that name, so they are told the
/// release has no build for them rather than left with one that cannot start.
/// /releases/latest never returns drafts or pre-releases, and answers 404 for a
/// private repository.
///
/// MARGINAL_UPDATE_FEED points the check at another URL serving the same JSON,
/// so the whole path can be run against a local server before anything is
/// public. Logged loudly when set.
public static class UpdateService
{
    // The program's name, path, kind and version are InstallService's: one
    // answer for both, so a change to them is made once (code review 2026-10-25).
    public const string Repo = Install.InstallService.Repo;
    public const string ProjectUrl = "https://github.com/" + Repo;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static string ExeFileName => Install.InstallService.ExeFileName;
    public static string ExePath => Install.InstallService.ExePath;

    /// True for a published build (single file or Native AOT); a development
    /// build is a folder of assemblies and is never offered an update.
    public static bool IsSingleFile => Install.InstallService.IsSingleFile;

    /// On macOS since 0.7.0, by replacing Marginal.app whole (MacUpdate):
    /// the program sits inside it, signed as a whole, and swapping only the
    /// executable would break the seal. Not from the disk image, from
    /// Downloads, or where this account cannot write (MacBlocker says which).
    public static bool CanUpdate => IsSingleFile && MacBlocker is null;

    /// Why a Mac copy cannot update itself, as a sentence; null when it can,
    /// and on every other system.
    public static string? MacBlocker => OperatingSystem.IsMacOS() && IsSingleFile ? MacUpdate.Blocker(ExePath) : null;

    /// The version this copy reports, without the source-revision suffix.
    public static string CurrentVersion => Install.InstallService.CurrentVersion;

    /// The Native AOT package for this system: Marginal-linux-x64-aot.zip.
    public static string AotAssetName => $"Marginal-{Rid()}-aot.zip";

    /// The runtime identifier in the asset name: linux-x64, linux-arm64.
    public static string Rid()
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        if (OperatingSystem.IsMacOS()) return $"osx-{arch}";
        return $"linux-{arch}";
    }

    /// Stable: the latest full release. Edge: the release list, newest taken
    /// (ReleaseFeed). MARGINAL_UPDATE_FEED replaces either; it must serve the
    /// matching shape (one release for Stable, a list for Edge), from a place
    /// MayFetch allows: anything else is ignored.
    private static string FeedUrl(bool edge)
    {
        var github = edge ? $"https://api.github.com/repos/{Repo}/releases?per_page=30"
                          : $"https://api.github.com/repos/{Repo}/releases/latest";
        var other = Environment.GetEnvironmentVariable("MARGINAL_UPDATE_FEED");
        if (string.IsNullOrWhiteSpace(other)) return github;
        if (!Uri.TryCreate(other, UriKind.Absolute, out var u) || !MayFetch(u))
        {
            Console.WriteLine($"[update] MARGINAL_UPDATE_FEED ignored (GitHub over https, or this computer, only): {other}");
            return github;
        }
        Console.WriteLine($"[update] FEED OVERRIDDEN by MARGINAL_UPDATE_FEED: {other}");
        return other;
    }

    /// Where the updater may fetch from: GitHub over https (the API, the
    /// release pages, and githubusercontent.com, where a download is
    /// redirected), or this computer, over http or https, for a test feed.
    /// MARGINAL_UPDATE_FEED worked in every build and took any address, and the
    /// asset links in a feed were followed wherever they pointed: an
    /// environment that set it could point a copy at any server, which then
    /// only had to agree with itself (AlbumWall security review 2026-10-26,
    /// INFO; AlbumWall 6e6f6c5). SHA256SUMS comes from the same release, so
    /// GitHub stays the root of trust.
    internal static bool MayFetch(Uri u)
    {
        if (u.IsLoopback) return u.Scheme is "http" or "https";
        if (u.Scheme != "https") return false;
        var host = u.IdnHost.ToLowerInvariant();
        return host is "github.com" or "api.github.com"
            || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
    }

    /// Refuses a reply that a redirect brought from somewhere MayFetch does not allow.
    private static void CheckLanded(HttpResponseMessage resp)
    {
        if (resp.RequestMessage?.RequestUri is { } u && !MayFetch(u))
            throw new InvalidOperationException($"The download was redirected to {u.Host}, not GitHub. Nothing was installed.");
    }

    /// On Linux .NET does TLS and hashing through the system's OpenSSL, loaded
    /// by name the first time either is used. If no version it knows is there
    /// the runtime prints "No usable version of libssl was found" and ABORTS
    /// THE PROCESS, uncatchable: Fedora 45 did that to AlbumWall mid-song
    /// (2026-09-21). So look first, with the names the runtime itself tries
    /// (dotnet/runtime opensslshim.c), and treat "none" as a reason not to
    /// update rather than a crash. CLR_OPENSSL_VERSION_OVERRIDE is honoured.
    private static readonly Lazy<string?> MissingTls = new(() =>
    {
        if (!OperatingSystem.IsLinux()) return null;
        var names = new List<string>();
        if (Environment.GetEnvironmentVariable("CLR_OPENSSL_VERSION_OVERRIDE") is { Length: > 0 } v)
            names.Add($"libssl.so.{v}");
        names.AddRange(["libssl.so.3", "libssl.so.1.1", "libssl.so.1.0.2", "libssl.so.1.0.0", "libssl.so.10"]);
        foreach (var name in names)
            if (NativeLibrary.TryLoad(name, out var handle))
            {
                NativeLibrary.Free(handle);
                return null;
            }
        Console.WriteLine($"[update] no libssl .NET can use (tried {string.Join(", ", names)}); updates are off");
        return "This computer has no version of OpenSSL that .NET can use, so Marginal cannot check for "
             + "updates. On Fedora, installing openssl3-libs fixes it.";
    });

    public static async Task<UpdateInfo> CheckAsync(bool edge = false)
    {
        var info = new UpdateInfo { CurrentVersion = CurrentVersion, ReleaseUrl = ProjectUrl + "/releases/latest" };
        if (MissingTls.Value is { } why)
        {
            info.Error = why;
            return info;
        }
        try
        {
            using var req = Request(FeedUrl(edge), "Marginal-UpdateCheck");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                info.NothingPublished = true;
                Console.WriteLine("[update] nothing published (404)");
                return info;
            }
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            JsonElement root;
            if (!edge) root = doc.RootElement;
            else if (ReleaseFeed.Newest(doc.RootElement) is { } newest) root = newest;
            else
            {
                info.NothingPublished = true;
                Console.WriteLine("[update] edge: no releases listed");
                return info;
            }
            if (edge) info.IsPrerelease = root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
            info.LatestTag = root.GetProperty("tag_name").GetString() ?? "";
            if (root.TryGetProperty("html_url", out var hu) && hu.GetString() is { Length: > 0 } url) info.ReleaseUrl = url;

            // The Native AOT package (0.8 on) before the single file: it is the
            // program with its native libraries beside it, which copies before
            // 0.7.6 cannot install, so it has a name they do not look for.
            string[] wanted = [AotAssetName, $"Marginal-{Rid()}.zip"];
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString();
                    var link = a.GetProperty("browser_download_url").GetString();
                    var rank = Array.IndexOf(wanted, name);
                    if (rank >= 0 && (info.AssetName is null || rank < Array.IndexOf(wanted, info.AssetName)))
                    { info.AssetUrl = link; info.AssetName = name; }
                    else if (name == "SHA256SUMS") info.SumsUrl = link;
                }

            // Unparseable on either side is "not newer", never "newer" (VersionOrder).
            info.UpdateAvailable = VersionOrder.IsNewer(info.LatestTag, CurrentVersion);
            Console.WriteLine($"[update] {(edge ? "edge" : "stable")}: latest {info.LatestTag}{(info.IsPrerelease ? " (edge build)" : "")}, have {CurrentVersion}, newer={info.UpdateAvailable}, "
                            + $"asset={info.AssetName ?? "none for " + Rid()}");
        }
        catch (Exception ex)
        {
            info.Error = ex is HttpRequestException or TaskCanceledException
                ? "Could not reach GitHub. Check the network and try again."
                : ex.Message;
            Console.WriteLine($"[update] check failed: {ex.Message}");
        }
        return info;
    }

    /// Where the update was downloaded and unpacked: a new private folder each
    /// time (mode 0700, unique name), never a fixed name in the shared temp
    /// directory, where another account could make the folder first and swap
    /// the program between the hash check and the copy (security review
    /// 2026-10-10). The relaunched program must never have this as its working
    /// directory: a directory in use as one cannot be deleted.
    private static string? _stageRoot;

    /// Downloads the release's zip, checks it against SHA256SUMS, unpacks it
    /// and returns the staged executable. progress is the fraction downloaded.
    public static async Task<string> DownloadAndStageAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        if (MissingTls.Value is { } why) throw new InvalidOperationException(why);
        if (info.AssetUrl is null || info.AssetName is null)
            throw new InvalidOperationException($"This release has no build for {Rid()}.");
        if (info.SumsUrl is null)
            throw new InvalidOperationException("This release publishes no SHA256SUMS, so its download cannot be checked. It has not been installed.");
        // Both links checked before anything is made on disk.
        Request(info.SumsUrl, "Marginal-UpdateInstall").Dispose();
        Request(info.AssetUrl, "Marginal-UpdateInstall").Dispose();

        // One staging folder at a time, and none left by a download that
        // failed (code review 2026-10-25: every failure but a bad hash left
        // its zip in temp, one more per retry).
        DeleteStage();
        var tmp = Directory.CreateTempSubdirectory("marginal-update-").FullName;
        _stageRoot = tmp;
        try
        {
            return await StageAsync(info, tmp, progress);
        }
        catch
        {
            DeleteStage();
            throw;
        }
    }

    private static void DeleteStage()
    {
        if (_stageRoot is not { } old) return;
        _stageRoot = null;
        try { Directory.Delete(old, recursive: true); } catch { /* gone already, or temp's to tidy */ }
    }

    /// The download proper; AssetUrl, AssetName and SumsUrl are checked above.
    private static async Task<string> StageAsync(UpdateInfo info, string tmp, IProgress<double>? progress)
    {
        // The list first: it is 100 bytes, and without the zip's line there is
        // no point fetching forty megabytes.
        string sums;
        using (var req = Request(info.SumsUrl!, "Marginal-UpdateInstall"))
        using (var resp = await Http.SendAsync(req))
        {
            resp.EnsureSuccessStatusCode();
            CheckLanded(resp);
            sums = await resp.Content.ReadAsStringAsync();
        }
        var expected = sums.Split('\n')
            .Select(l => l.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f.Length == 2 && f[1].TrimStart('*', '.', '/') == info.AssetName)
            .Select(f => f[0].ToLowerInvariant())
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"SHA256SUMS does not list {info.AssetName}. Not installed.");

        var zip = Path.Combine(tmp, "update.zip");
        using (var req = Request(info.AssetUrl!, "Marginal-UpdateInstall"))
        using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            CheckLanded(resp);
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var from = await resp.Content.ReadAsStreamAsync();
            await using var to = File.Create(zip);
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await from.ReadAsync(buffer)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n));
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        string actual;
        await using (var fs = File.OpenRead(zip))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
        if (actual != expected)
        {
            Directory.Delete(tmp, recursive: true);
            throw new InvalidOperationException("The download does not match the release's SHA-256 and has been thrown away. Nothing was installed.");
        }
        Console.WriteLine($"[update] {info.AssetName} matches SHA256SUMS ({actual[..12]}...)");

        var unpacked = Path.Combine(tmp, "ex");
        if (OperatingSystem.IsMacOS())
        {
            // The whole bundle, checked for our signature, Apple's notarization and its version.
            var app = MacUpdate.UnpackAndVerify(zip, unpacked, info.LatestTag, CurrentVersion);
            File.Delete(zip);
            Console.WriteLine($"[update] {app}: signed by {MacUpdate.TeamId}, notarized, version {info.LatestTag}");
            return app;
        }
        ZipFile.ExtractToDirectory(zip, unpacked, overwriteFiles: true);
        File.Delete(zip);
        var staged = FindProgram(unpacked, ExeFileName)
            ?? throw new FileNotFoundException($"{ExeFileName} is not in the downloaded package.");

        // A last look before it is given the keys: an executable for this
        // system (ELF, PE or Mach-O by its first bytes), and not a stub; and
        // the same for each native library a Native AOT build has beside it.
        if (!await IsBinaryForThisSystemAsync(staged) || new FileInfo(staged).Length < 1_000_000)
            throw new InvalidOperationException("The downloaded program is not an executable for this system.");
        foreach (var lib in Install.InstallLayout.NativeLibrariesIn(Path.GetDirectoryName(staged)!))
            if (!await IsBinaryForThisSystemAsync(Path.Combine(Path.GetDirectoryName(staged)!, lib)))
                throw new InvalidOperationException($"The downloaded {lib} is not a library for this system.");
        return staged;
    }

    /// The program in an unpacked package: the one at its top, where
    /// release.sh puts it; else, in a folder below, only a file named exactly
    /// so. A search through every folder ignores case on Windows and macOS,
    /// and would take a cli\marginal.exe for Marginal.exe (Mobilenode,
    /// 2026-10-25: CI's artifacts carry the CLI in cli\).
    public static string? FindProgram(string unpacked, string exeName)
    {
        bool Named(string f) => Path.GetFileName(f) == exeName;   // the name as it is on disk
        return Directory.GetFiles(unpacked, exeName).FirstOrDefault(Named)
            ?? Directory.GetFiles(unpacked, exeName, SearchOption.AllDirectories).FirstOrDefault(Named);
    }

    private static async Task<bool> IsBinaryForThisSystemAsync(string path)
    {
        var head = new byte[4];
        await using (var fs = File.OpenRead(path)) _ = await fs.ReadAsync(head);
        return OperatingSystem.IsWindows() ? head[0] == 'M' && head[1] == 'Z'
            : OperatingSystem.IsMacOS() ? (head[0] == 0xCF && head[1] == 0xFA && head[2] == 0xED && head[3] == 0xFE) || (head[0] == 0xCA && head[1] == 0xFE && head[2] == 0xBA && head[3] == 0xBE)
            : head[0] == 0x7F && head[1] == 'E' && head[2] == 'L' && head[3] == 'F';
    }

    private static HttpRequestMessage Request(string url, string agent)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !MayFetch(u))
            throw new InvalidOperationException($"The release points its download at {url}, not at GitHub. Nothing was installed.");
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue(agent, CurrentVersion));
        return req;
    }

    /// Launches the detached helper that waits for this process to exit,
    /// replaces the program (on the Mac, the bundle) with the staged one and
    /// relaunches it. THE CALLER MUST THEN EXIT THE APP.
    ///
    /// On Linux and Windows since 0.7.6 the helper installs a package, not
    /// just a program: a Native AOT build (0.8) has its native libraries
    /// beside it (InstallLayout.NativeLibraries), and a copy that took only
    /// the program would not start. Whatever it replaces is backed up first;
    /// the libraries go in before the program; and the new program must still
    /// be running after a few seconds (or have ended cleanly), or the old
    /// files go back, the old program is started, and the marker says so.
    public static void ApplyAndRestart(string stagedExe)
    {
        var target = ExePath;
        var marker = FailedMarkerPath(target);
        var pid = Environment.ProcessId;
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();   // relaunch as launched
        var stageRoot = _stageRoot ?? throw new InvalidOperationException("Nothing has been staged.");

        // The helper has a private folder of its own, not the staging folder:
        // it deletes that one first, and its own last.
        var helperDir = Directory.CreateTempSubdirectory("marginal-apply-").FullName;
        if (OperatingSystem.IsMacOS())
        {
            var app = MacUpdate.AppBundle(target) ?? throw new InvalidOperationException("This copy is not inside Marginal.app.");
            MacUpdate.Start(pid, stagedExe, app, marker, stageRoot, helperDir);
            Console.WriteLine($"[update] helper started; replacing {app} once pid {pid} has gone");
            return;
        }
        var from = Path.GetDirectoryName(stagedExe)!;
        var to = Path.GetDirectoryName(target)!;
        var libraries = Install.InstallLayout.NativeLibrariesIn(from);
        if (OperatingSystem.IsWindows())
        {
            var ps1 = Path.Combine(helperDir, "apply-update.ps1");
            File.WriteAllText(ps1, WindowsApplyScript(pid, from, to, ExeFileName, libraries, marker, stageRoot, OwnExtractionDir, helperDir, args, StartCheckSeconds), PowerShell.FileEncoding);
            Process.Start(PowerShell.StartInfo(ps1));
        }
        else
        {
            var sh = Path.Combine(helperDir, "apply-update.sh");
            File.WriteAllText(sh, ApplyScript + "\n");
            var start = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),   // not ours: see _stageRoot
            };
            foreach (var a in ApplyScriptArguments(sh, pid, from, to, ExeFileName, libraries, marker, stageRoot, OwnExtractionDir, helperDir, args))
                start.ArgumentList.Add(a);
            Process.Start(start);
        }
        Console.WriteLine($"[update] helper started; installing {ExeFileName}{(libraries.Count > 0 ? " and " + string.Join(", ", libraries) : "")} "
                        + $"into {to} once pid {pid} has gone; then removing {OwnExtractionDir ?? "(nothing unpacked)"}");
    }

    /// How long the new program must stay up (or end with 0) to count as started.
    private const int StartCheckSeconds = 5;

    /// The PowerShell helper (AlbumWall's UpdateApplyScript.Windows, grown in
    /// 0.7.6): wait for this process to end; back up what will be replaced;
    /// copy the libraries, then the program (forty tries each, since Windows
    /// keeps a just-exited exe locked a moment); drop the old build's unpacked
    /// libraries unless another copy still has them loaded; start the new
    /// program and, if it falls over within startCheckSeconds, put the old
    /// files back and start the old one; tidy up.
    private static string WindowsApplyScript(int pid, string from, string to, string exeName, IReadOnlyList<string> libraries,
        string failedMarker, string stageRoot, string? ownExtractionDir, string helperDir, IReadOnlyList<string> relaunchArgs,
        int startCheckSeconds)
    {
        static string Q(string value) => PowerShell.Quote(value);
        var argList = relaunchArgs.Count > 0 ? " -ArgumentList " + Q(PowerShell.CommandLine(relaunchArgs)) : "";
        var names = "@(" + string.Join(", ", libraries.Append(exeName).Select(Q)) + ")";   // the program last
        var exe = Path.Combine(to, exeName);
        return
            $"$from = {Q(from)}; $to = {Q(to)}; $back = {Q(Path.Combine(helperDir, "old"))}; $names = {names}\n" +
            $"while (Get-Process -Id {pid} -ErrorAction SilentlyContinue) {{ Start-Sleep -Milliseconds 300 }}\n" +
            "New-Item -ItemType Directory -Path $back -Force | Out-Null\n" +
            "$ok = $true\n" +
            "foreach ($n in $names) {\n" +
            "  $t = Join-Path $to $n\n" +
            "  if (Test-Path -LiteralPath $t) {\n" +
            "    try { Copy-Item -LiteralPath $t -Destination (Join-Path $back $n) -Force -ErrorAction Stop } catch { $ok = $false; break }\n" +
            "  } else { New-Item -ItemType File -Path (Join-Path $back \"$n.absent\") -Force | Out-Null }\n" +
            "}\n" +
            "function Restore {\n" +
            "  foreach ($n in $names) {\n" +
            "    $t = Join-Path $to $n\n" +
            "    if (Test-Path -LiteralPath (Join-Path $back \"$n.absent\")) { Remove-Item -LiteralPath $t -Force -ErrorAction SilentlyContinue }\n" +
            "    elseif (Test-Path -LiteralPath (Join-Path $back $n)) {\n" +
            "      for ($i = 0; $i -lt 40; $i++) { try { Copy-Item -LiteralPath (Join-Path $back $n) -Destination $t -Force -ErrorAction Stop; break } catch { Start-Sleep -Milliseconds 250 } }\n" +
            "    }\n" +
            "  }\n" +
            "}\n" +
            "if ($ok) {\n" +
            "  foreach ($n in $names) {\n" +
            "    $copied = $false\n" +
            "    for ($i = 0; $i -lt 40 -and -not $copied; $i++) {\n" +
            "      try { Copy-Item -LiteralPath (Join-Path $from $n) -Destination (Join-Path $to $n) -Force -ErrorAction Stop; $copied = $true }\n" +
            "      catch { Start-Sleep -Milliseconds 250 }\n" +
            "    }\n" +
            "    if (-not $copied) { $ok = $false; break }\n" +
            "  }\n" +
            "  if (-not $ok) { Restore }\n" +
            "}\n" +
            "if ($ok) {\n" +
            (ownExtractionDir is null ? "" :
            $"  $mine = {Q("*\\" + ownExtractionDir.TrimEnd('\\', '/').Split('\\', '/')[^1] + "\\*")}\n" +
            "  $inUse = Get-Process -Name Marginal -ErrorAction SilentlyContinue | Where-Object { $_.Modules | Where-Object { $_.FileName -like $mine } }\n" +
            "  if (-not $inUse) {\n" +
            $"    Remove-Item -LiteralPath {Q(ownExtractionDir)} -Recurse -Force -ErrorAction SilentlyContinue\n" +
            "  }\n") +
            $"  $p = Start-Process -FilePath {Q(exe)}{argList} -WorkingDirectory $to -PassThru\n" +
            "  $null = $p.Handle   # keeps the exit code readable once it has gone\n" +
            $"  for ($i = 0; $i -lt {startCheckSeconds * 2} -and -not $p.HasExited; $i++) {{ Start-Sleep -Milliseconds 500 }}\n" +
            "  if ($p.HasExited -and $p.ExitCode -ne 0) { $ok = $false; Restore }\n" +
            "}\n" +
            "if ($ok) {\n" +
            $"  Remove-Item -LiteralPath {Q(failedMarker)} -ErrorAction SilentlyContinue\n" +
            "} else {\n" +
            $"  New-Item -ItemType File -Path {Q(failedMarker)} -Force | Out-Null\n" +
            $"  Start-Process -FilePath {Q(exe)}{argList} -WorkingDirectory $to\n" +
            "}\n" +
            $"Remove-Item -LiteralPath {Q(stageRoot)} -Recurse -Force -ErrorAction SilentlyContinue\n" +
            $"Remove-Item -LiteralPath {Q(helperDir)} -Recurse -Force -ErrorAction SilentlyContinue\n";
    }

    /// The POSIX shell helper for Linux (the Mac has MacUpdate.ApplyScript):
    /// the same steps as the Windows one. Every value is an argument, never
    /// script text (ApplyScriptArguments). Each file goes in under a
    /// temporary name and is renamed over the old one, never written in
    /// place: another copy running from the folder keeps the files it has
    /// mapped, and a running program could not be overwritten at all (code
    /// review 2026-10-25). The retry is for a busy filesystem. The program is
    /// started in a session of its own with no terminal, as
    /// InstallService.LaunchDetached does, so closing the terminal the old one
    /// was started from does not take the new one with it.
    public const string ApplyScript = """
        #!/bin/sh
        # $1 pid  $2 staged folder  $3 install folder  $4 program  $5 failure marker
        # $6 staging folder  $7 own folder  $8 old build's unpacked libraries, or ""
        # $9 seconds the new program must last; then the libraries, "--", the relaunch arguments
        pid=$1 from=$2 to=$3 exe=$4 marker=$5 stage=$6 helper=$7 oldx=$8 secs=$9
        shift 9
        names=""
        while [ $# -gt 0 ] && [ "$1" != "--" ]; do names="$names $1"; shift; done
        [ "$1" = "--" ] && shift
        names="$names $exe"
        while kill -0 "$pid" 2>/dev/null; do sleep 0.3; done
        back="$helper/old"
        mkdir -p "$back"
        put() { cp -p "$1" "$to/.$2.new" && mv -f "$to/.$2.new" "$to/$2"; }
        restore() {
          for n in $names; do
            if [ -e "$back/$n.absent" ]; then rm -f "$to/$n"
            elif [ -e "$back/$n" ]; then put "$back/$n" "$n"; fi
            rm -f "$to/.$n.new"
          done
        }
        launch() {
          if command -v setsid >/dev/null 2>&1; then
            (cd "$to" && exec setsid -w "$to/$exe" "$@" </dev/null >/dev/null 2>&1) &   # -w: its exit code, for the start check
          else
            (cd "$to" && exec "$to/$exe" "$@" </dev/null >/dev/null 2>&1) &
          fi
        }
        ok=1
        for n in $names; do
          if [ -e "$to/$n" ]; then cp -p "$to/$n" "$back/$n" || { ok=0; break; }
          else : > "$back/$n.absent"; fi
        done
        if [ "$ok" = 1 ]; then
          for n in $names; do
            copied=0
            for i in 1 2 3 4 5; do
              if put "$from/$n" "$n"; then copied=1; break; fi
              sleep 0.5
            done
            [ "$copied" = 1 ] || { ok=0; break; }
          done
          if [ "$ok" = 1 ]; then chmod +x "$to/$exe"; else restore; fi
        fi
        if [ "$ok" = 1 ]; then
          # The OLD build's unpacked native libraries; a new single file unpacks into a folder of its own.
          if [ -n "$oldx" ]; then pgrep -x "$exe" >/dev/null 2>&1 || rm -rf "$oldx"; fi
          launch "$@"
          child=$!
          i=0
          while [ "$i" -lt $((secs * 2)) ] && kill -0 "$child" 2>/dev/null; do sleep 0.5; i=$((i + 1)); done
          if ! kill -0 "$child" 2>/dev/null; then
            wait "$child" || { ok=0; restore; }
          fi
        fi
        if [ "$ok" = 1 ]; then
          rm -f "$marker"
        else
          : > "$marker"
          launch "$@"
        fi
        rm -rf "$stage" "$helper"
        """;

    /// The arguments ApplyScript reads, in its order, after the script's own path.
    private static IEnumerable<string> ApplyScriptArguments(string script, int pid, string from, string to, string exeName,
        IReadOnlyList<string> libraries, string failedMarker, string stageRoot, string? ownExtractionDir, string helperDir,
        IReadOnlyList<string> relaunchArgs) =>
        [script, pid.ToString(System.Globalization.CultureInfo.InvariantCulture), from, to, exeName, failedMarker, stageRoot,
         helperDir, ownExtractionDir ?? "", StartCheckSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
         .. libraries, "--", .. relaunchArgs];

    /// Beside the program; on the Mac beside the settings instead, since a
    /// file put inside the signed bundle would break its seal.
    private static string FailedMarkerPath(string targetExe) => OperatingSystem.IsMacOS()
        ? Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, ".update-failed")
        : Path.Combine(Path.GetDirectoryName(targetExe) ?? ".", ".marginal-update-failed");

    /// True, once, if the last helper could not put the new build in place,
    /// or the new program fell over at once (it put the old one back and
    /// relaunched that). Clears the marker, so the warning shows on the
    /// next start only.
    public static bool ConsumeUpdateFailed()
    {
        try
        {
            var p = FailedMarkerPath(Environment.ProcessPath ?? "");
            if (File.Exists(p)) { File.Delete(p); return true; }
        }
        catch { /* a marker we cannot read is a warning we do not show */ }
        return false;
    }

    /// Where this single-file build unpacked its native libraries: the
    /// installer's answer, the one folder directly under ~/.net/Marginal (or
    /// DOTNET_BUNDLE_EXTRACT_BASE_DIR/Marginal) named for this build. This had
    /// a copy of its own that took any two folders under the extraction
    /// base, and with that base set to the home folder named ~/.local/share
    /// for rm -rf (security review 2026-10-24, L14).
    private static string? OwnExtractionDir => Install.InstallService.OwnExtractionDir;
}
