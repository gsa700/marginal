// Marginal — art from the Cover Art Archive (spec §3.4, §5).
//
// What Deadwax learned about the archive, kept here (CoverArt.cs, BackCover.cs):
//
//   * Read a release's image INDEX, never the /front or /back shortcuts: an
//     editor can flag one image Front AND Back (Neil Diamond 50), and the
//     shortcut then serves the cover as the back. An image typed Front is
//     never a back here.
//   * The archive is flaky: one URL answered 200, 500, 500 within seconds,
//     and its image host dropped every connection for an evening. A 5xx or a
//     dropped connection is tried again; only a 404 (or the 400 it answers for
//     a question it cannot serve) means "none"; a failure is reported as one,
//     never as "no art".
//   * Other editions of the album come from MusicBrainz's release browse,
//     which says for each whether the archive holds art for it.
//   * Only the archive's own hosts are asked (WebLimits.IsArchiveUrl), and
//     no download may exceed WebLimits.MaxImageBytes.

using System.Net;
using System.Text.Json;

namespace Marginal.Core;

public enum ArtSlot { Front, Back, Booklet }

/// One image the archive holds. `Edition` describes the release it belongs to
/// ("1990 US CD"); `Ours` marks the release the files are tagged with.
public sealed record ArtCandidate(string ReleaseId, string Edition, bool Ours, ArtSlot Slot, string Thumbnail, string Image, int Page);

public static class CoverArtArchive
{
    /// How many other editions' indexes are asked for, at most.
    public const int MaxEditions = 8;

    /// The images of the files' release, then (when `otherEditions`) those of
    /// other editions of the album: same country first, then the nearest year.
    /// Each release's images go to `found` as soon as its index is in, so the
    /// window fills as the archive answers: an index takes 1.5 to 5 s (two
    /// redirects to archive.org, measured 2026-10-31), and nine asked in turn
    /// was half a minute of nothing on screen. The edition list is asked of
    /// MusicBrainz while the files' own index is on its way, and the editions'
    /// indexes four at a time. The error, if any, says what failed.
    public static async Task<string?> CandidatesAsync(
        HttpClient http, MusicBrainzClient mb, string? releaseId, Task<string?> releaseGroupId, bool otherEditions,
        Action<IReadOnlyList<ArtCandidate>> found, CancellationToken ct = default)
    {
        string? error = null;
        // The release group may still be on its way (asked of MusicBrainz when
        // the tags lack it): the browse waits for it, the own index does not.
        Task<JsonDocument?>? browsing = otherEditions ? BrowseAsync(mb, releaseGroupId, ct) : null;

        if (releaseId is not null && WebLimits.IsMbid(releaseId))
        {
            var (own, ownError) = await IndexAsync(http, releaseId, ct);
            if (own is not null) found(Parse(own.Value, releaseId, "your release", ours: true));
            error = ownError is null ? null : $"Cover Art Archive: {ownError}";
        }
        if (browsing is null) return error;

        JsonDocument? browse;
        try { browse = await browsing; }
        catch (Exception e) when (e is HttpRequestException or MusicBrainzException) { return error ?? $"MusicBrainz: {e.Message}"; }
        if (browse is null) return error;
        using (browse)
        {
            var failures = 0;
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(Editions(browse.RootElement, releaseId).Take(MaxEditions).Select(async edition =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (Volatile.Read(ref failures) >= 3) return;      // the archive is not answering
                    var (index, indexError) = await IndexAsync(http, edition.Id, ct);
                    if (index is not null) found(Parse(index.Value, edition.Id, edition.Label, ours: false));
                    else if (indexError is not null)
                    {
                        Interlocked.Increment(ref failures);
                        error ??= $"Cover Art Archive: {indexError}";
                    }
                }
                finally { gate.Release(); }
            }));
        }
        return error;
    }

    private static async Task<JsonDocument?> BrowseAsync(MusicBrainzClient mb, Task<string?> releaseGroupId, CancellationToken ct)
    {
        var group = await releaseGroupId;
        return group is not null && WebLimits.IsMbid(group) ? await mb.BrowseReleasesAsync(group, ct) : null;
    }

    /// The images in a release's index, by slot. Booklet pages keep the
    /// archive's order and are numbered from 1.
    public static IReadOnlyList<ArtCandidate> Parse(JsonElement index, string releaseId, string edition, bool ours)
    {
        var list = new List<ArtCandidate>();
        if (!index.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return list;
        var page = 0;
        foreach (var image in images.EnumerateArray())
        {
            var types = image.TryGetProperty("types", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
            var front = types.Contains("Front") || Flag(image, "front");
            var back = !front && (Flag(image, "back") || types.Contains("Back"));
            var booklet = !front && !back && types.Contains("Booklet");
            if (!front && !back && !booklet) continue;
            var full = Https(Str(image, "image"));
            if (full is null || !WebLimits.IsArchiveUrl(full)) continue;
            var thumb = image.TryGetProperty("thumbnails", out var th) && th.ValueKind == JsonValueKind.Object
                ? Https(Str(th, "250") ?? Str(th, "small")) : null;
            list.Add(new ArtCandidate(releaseId, edition, ours,
                front ? ArtSlot.Front : back ? ArtSlot.Back : ArtSlot.Booklet,
                thumb is not null && WebLimits.IsArchiveUrl(thumb) ? thumb : full, full, booklet ? ++page : 0));
        }
        return list;
    }

    /// The editions with art in a release browse, nearest the files' first.
    public static IEnumerable<(string Id, string Label)> Editions(JsonElement browse, string? ours)
    {
        if (!browse.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) yield break;
        var all = releases.EnumerateArray().Select(r => (
            Id: Str(r, "id") ?? "", Country: Str(r, "country"), Date: Str(r, "date"),
            Art: r.TryGetProperty("cover-art-archive", out var caa) && caa.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number && c.GetInt32() > 0,
            Label: Label(r))).ToList();
        var me = all.FirstOrDefault(r => r.Id == ours);
        var myYear = Year(me.Date);
        foreach (var r in all
                     .Where(r => r.Art && r.Id.Length > 0 && r.Id != ours)
                     .OrderBy(r => me.Country is { } mc && r.Country == mc ? 0 : 1)
                     .ThenBy(r => Year(r.Date) is { } y && myYear is { } m ? Math.Abs(y - m) : 999)
                     .ThenBy(r => r.Date ?? "9999", StringComparer.Ordinal))
            yield return (r.Id, r.Label);
    }

    /// "1990 US CD" for a release in a browse.
    public static string EditionOf(JsonElement browse, string id)
    {
        if (browse.TryGetProperty("releases", out var releases) && releases.ValueKind == JsonValueKind.Array)
            foreach (var r in releases.EnumerateArray())
                if (Str(r, "id") == id) return Label(r);
        return "your release";
    }

    private static string Label(JsonElement r)
    {
        var date = Str(r, "date") is { Length: >= 4 } d ? d[..4] : "";
        var formats = r.TryGetProperty("media", out var m) && m.ValueKind == JsonValueKind.Array
            ? string.Join("+", m.EnumerateArray().Select(x => Str(x, "format") ?? "").Where(f => f.Length > 0).Distinct()) : "";
        var label = string.Join(" ", new[] { date, Str(r, "country") ?? "", formats }.Where(s => s.Length > 0));
        return label.Length > 0 ? label : "another edition";
    }

    /// An image's bytes, or (null, error) when the archive failed to answer,
    /// or (null, null) for none. A 5xx or a dropped connection is tried once
    /// more; anything that is not a JPEG or PNG (a CDN error page) is none.
    public static async Task<(byte[]? Image, string? Error)> ImageAsync(HttpClient http, string url, CancellationToken ct = default)
    {
        if (!WebLimits.IsArchiveUrl(url)) return (null, "not an archive address");
        var (body, error) = await GetAsync(http, url, ct);
        if (body is null) return (null, error);
        return ArtImage.LooksLikeAnImage(body) ? (body, null) : (null, null);
    }

    private static async Task<(JsonElement? Index, string? Error)> IndexAsync(HttpClient http, string releaseId, CancellationToken ct)
    {
        var (body, error) = await GetAsync(http, $"https://coverartarchive.org/release/{releaseId}/", ct);
        if (body is null) return (null, error);
        try
        {
            using var doc = JsonDocument.Parse(body);
            return (doc.RootElement.Clone(), null);
        }
        catch (JsonException) { return (null, "an unreadable index"); }
    }

    private static async Task<(byte[]?, string?)> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        var error = "gave up";
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return (null, null);
                if (response.IsSuccessStatusCode)
                    return await WebLimits.ReadCappedAsync(response.Content, WebLimits.MaxImageBytes, ct) is { } body
                        ? (body, null) : (null, "too large");
                error = $"answered {(int)response.StatusCode}";
                if ((int)response.StatusCode < 500) return (null, error);
            }
            catch (HttpRequestException e) { error = e.HttpRequestError.ToString(); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { error = "timed out"; }
            if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        return (null, error);
    }

    /// An HttpClient for the archive: its User-Agent, and redirects followed
    /// (coverartarchive.org sends images on to archive.org).
    public static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Marginal/{AppVersion.Current} ( https://github.com/gsa700/marginal )");
        return http;
    }

    private static string? Https(string? url) => url?.Replace("http://", "https://");

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Year(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;
}
