// FROM Deadwax (Deadwax.App/Install/RegistrationLog.cs, 704ed87, 2026-10-31), renamed to Marginal throughout.
// Dates and findings in the comments are Deadwax's history; a fix to the
// install or update logic goes to both (and AlbumWall's, from which Deadwax's came).
//
using System.Globalization;

namespace Marginal.App.Install;

/// <summary>What one registration attempt did. <see cref="Detail"/> is meant to be read by a person.</summary>
/// <param name="WhenUtc">When the attempt finished.</param>
/// <param name="Version">The app version that attempted it — the value that goes stale when this fails.</param>
/// <param name="Trigger">What prompted it: <c>startup</c>, <c>install</c>, or <c>update</c>.</param>
/// <param name="Succeeded">Whether the registration was verifiably in place afterwards.</param>
/// <param name="Detail">
/// What actually happened, specific enough to tell the failure modes apart: which step ran, what
/// <c>reg.exe</c> returned, or the exception type if one escaped.
/// </param>
public readonly record struct RegistrationAttempt(
    DateTime WhenUtc, string Version, string Trigger, bool Succeeded, string Detail);

/// <summary>
/// Formats and parses the registration audit trail — one line per attempt, appended to a file the app
/// keeps beside its config.
///
/// This exists because the registration failure it was written for is invisible from the outside.
/// After an in-place update the installed-apps entry keeps the *previous* version, and nothing on the
/// machine distinguishes "the call was skipped" from "reg.exe refused" from "it threw before getting
/// there": <c>WriteUninstallEntry</c> returns false rather than throwing, <c>EnsureRegistered</c>
/// discarded the result, and the startup call site swallows exceptions. Diagnosing it took reading a
/// registry key's last-write timestamp and inferring backwards (BACKLOG, 2026-07-31). A line per
/// attempt turns the next occurrence into evidence.
///
/// Pure string work, so the format survives a round trip under test rather than being eyeballed once.
/// </summary>
public static class RegistrationLog
{
    /// <summary>Attempts to keep. Small: this is a diagnostic tail, not a history.</summary>
    public const int Keep = 20;

    private const string Separator = "\t";

    /// <summary>
    /// One tab-separated line, ending in the free-text detail. Tabs are stripped from the fields so a
    /// detail carrying one can't shift the columns; the detail is last so it is the only field that
    /// could ever contain a separator anyway.
    /// </summary>
    public static string Format(RegistrationAttempt a) => string.Join(Separator,
        a.WhenUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        Clean(a.Version),
        Clean(a.Trigger),
        a.Succeeded ? "ok" : "FAILED",
        Clean(a.Detail));

    /// <summary>Read a line back. Null for anything that doesn't parse — a truncated tail is expected.</summary>
    public static RegistrationAttempt? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var f = line.Split(Separator);
        if (f.Length < 5) return null;
        if (!DateTime.TryParse(f[0], CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
            return null;
        return new RegistrationAttempt(when, f[1], f[2], f[3] == "ok", string.Join(Separator, f[4..]));
    }

    /// <summary>Keep the last <paramref name="keep"/> lines, dropping blanks.</summary>
    public static IReadOnlyList<string> Tail(IEnumerable<string> lines, int keep = Keep)
    {
        var kept = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return kept.Count <= keep ? kept : kept[^keep..];
    }

    /// <summary>
    /// One-line summary for the UI. Deliberately says something even when all is well: a person
    /// checking on this needs to see that the check ran, not an empty space that could equally mean
    /// "fine" or "never happened".
    /// </summary>
    public static string Describe(RegistrationAttempt? last, string currentVersion)
    {
        if (last is not { } a) return "Installed-apps entry: not yet checked this session.";

        var when = a.WhenUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        if (!a.Succeeded) return $"Installed-apps entry FAILED at {when} — {a.Detail}";

        // Registered under an older version means the entry is stale even though this attempt passed:
        // the attempt that should have refreshed it never ran. That is exactly the reported fault, so
        // name it rather than reporting a plain success.
        return a.Version == currentVersion
            ? $"Installed-apps entry: up to date, checked {when}."
            : $"Installed-apps entry: last written by {a.Version} at {when}, now running {currentVersion}.";
    }

    private static string Clean(string? s) =>
        (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
