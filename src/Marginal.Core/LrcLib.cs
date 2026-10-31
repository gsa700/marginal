// Marginal — lyrics from LRCLIB (lrclib.net), free and community-run (spec §5).
//
// Asked only when Find lyrics is pressed, with only what the lookup needs:
// artist, title, album, length. Lyrics are copyrighted; Marginal fetches them
// for files their owner has, on request, and says where they came from.
//
// "Not found" (404) is not "not answering": a 5xx or a dropped connection is
// reported as LRCLIB failing, never as "no lyrics" (its API answered 502 to
// everything for a while on 2026-10-31, while its front page was up).
//
// A match is taken without asking only when it agrees on length within 2 s
// and on artist and title (spec §3.2); a live version is the usual misfit.

using System.Net;
using System.Text.Json;

namespace Marginal.Core;

public sealed class LrcLibException(string message) : Exception(message);

public sealed record LrcLibResult(long Id, string Track, string Artist, string Album, double Duration, bool Instrumental, string? Plain, string? Synced)
{
    public bool HasSynced => !string.IsNullOrWhiteSpace(Synced);
    public bool HasPlain => !string.IsNullOrWhiteSpace(Plain);
}

/// How a result fits a track: the length difference, whether artist and
/// title agree, and whether it may be used without asking.
public sealed record LyricsFit(LrcLibResult Result, int Seconds, bool Names, bool Automatic)
{
    public string Text => (Seconds == 0 ? "length ±0 s" : $"length {Seconds:+0;−0} s") + (Names ? "" : " · other title or artist");
}

public sealed class LrcLibClient : IDisposable
{
    /// The most seconds a match may differ in length and still go in unasked.
    public const int AutoSeconds = 2;

    private readonly HttpClient _http;

    public LrcLibClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri("https://lrclib.net/api/");
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Marginal/{AppVersion.Current} ( https://github.com/gsa700/marginal )");
    }

    /// LRCLIB's own best match for exactly this track, or null for none.
    public async Task<LrcLibResult?> GetAsync(string artist, string title, string album, TimeSpan length, CancellationToken ct = default)
    {
        var query = $"get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}"
                    + $"&album_name={Uri.EscapeDataString(album)}&duration={(int)Math.Round(length.TotalSeconds)}";
        var body = await AskAsync(query, ct);
        return body is null ? null : Result(JsonDocument.Parse(body).RootElement);
    }

    /// Every result for an artist and title, any album and length.
    public async Task<IReadOnlyList<LrcLibResult>> SearchAsync(string artist, string title, CancellationToken ct = default)
    {
        var body = await AskAsync($"search?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}", ct);
        if (body is null) return [];
        var root = JsonDocument.Parse(body).RootElement;
        return root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().Select(Result).ToList() : [];
    }

    private async Task<byte[]?> AskAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await _http.GetAsync(path, ct);
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                if (response.IsSuccessStatusCode) return await response.Content.ReadAsByteArrayAsync(ct);
                if ((int)response.StatusCode < 500 || attempt >= 2)
                    throw new LrcLibException($"LRCLIB answered {(int)response.StatusCode}");
            }
            catch (HttpRequestException e) when (attempt >= 2) { throw new LrcLibException($"LRCLIB is not answering ({e.HttpRequestError})"); }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt >= 2) throw new LrcLibException("LRCLIB is not answering (timed out)");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    public static LrcLibResult Result(JsonElement e) => new(
        e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0,
        Str(e, "trackName"), Str(e, "artistName"), Str(e, "albumName"),
        e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0,
        e.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True,
        NullIfBlank(Str(e, "plainLyrics")), NullIfBlank(Str(e, "syncedLyrics")));

    /// How `result` fits a track of `length` called `title` by `artist`.
    public static LyricsFit Fit(LrcLibResult result, string artist, string title, TimeSpan length)
    {
        var seconds = (int)Math.Round(result.Duration - length.TotalSeconds);
        var names = Same(result.Artist, artist) && Same(result.Track, title);
        return new LyricsFit(result, seconds, names, Math.Abs(seconds) <= AutoSeconds && names && (result.HasSynced || result.HasPlain || result.Instrumental));
    }

    /// Results for a track, best first: automatic fits, then synced, then the
    /// nearest length.
    public static IReadOnlyList<LyricsFit> Rank(IEnumerable<LrcLibResult> results, string artist, string title, TimeSpan length) =>
        results.Select(r => Fit(r, artist, title, length))
            .OrderByDescending(f => f.Automatic)
            .ThenByDescending(f => f.Result.HasSynced)
            .ThenBy(f => Math.Abs(f.Seconds))
            .ToList();

    private static bool Same(string a, string b) =>
        string.Equals(Letters(a), Letters(b), StringComparison.OrdinalIgnoreCase);

    private static string Letters(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public void Dispose() => _http.Dispose();
}
