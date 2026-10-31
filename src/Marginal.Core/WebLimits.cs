// COPIED from Deadwax (src/Deadwax.Metadata/WebLimits.cs, 2026-10-31), the same
// code in both: a fix in one goes to the other. Namespace changed only.
//
using System.Net.Http;

namespace Marginal.Core;

/// What Deadwax accepts from MusicBrainz and the Cover Art Archive before it
/// is used in a file name, a URL or memory. Both answer over HTTPS and are
/// trusted to be themselves; these keep a wrong answer from going further than
/// it should. Security review 2026-10-10.
public static class WebLimits
{
    /// The largest image kept: the archive's 500 px renderings are well under 1 MB.
    public const long MaxImageBytes = 20 * 1024 * 1024;

    /// A MusicBrainz ID (MBID): a UUID in its 36-character form. Release and
    /// release-group IDs go into cache file names and URL paths.
    public static bool IsMbid(string? s) =>
        s is { Length: 36 } && s[8] == '-' && s[13] == '-' && s[18] == '-' && s[23] == '-'
        && s.Where((c, i) => i is not (8 or 13 or 18 or 23)).All(char.IsAsciiHexDigit);

    /// True for an https URL on the archive's own hosts: coverartarchive.org,
    /// which redirects to archive.org, where the images live.
    public static bool IsArchiveUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && (u.Host is "coverartarchive.org" or "archive.org" || u.Host.EndsWith(".archive.org", StringComparison.OrdinalIgnoreCase));

    /// The body, or null when it is larger than max (by its header or as it arrives).
    public static async Task<byte[]?> ReadCappedAsync(HttpContent content, long max, CancellationToken ct)
    {
        if (content.Headers.ContentLength > max) return null;
        await using var from = await content.ReadAsStreamAsync(ct);
        using var to = new MemoryStream();
        var buffer = new byte[1 << 16];
        int n;
        while ((n = await from.ReadAsync(buffer, ct)) > 0)
        {
            if (to.Length + n > max) return null;
            to.Write(buffer, 0, n);
        }
        return to.ToArray();
    }
}
