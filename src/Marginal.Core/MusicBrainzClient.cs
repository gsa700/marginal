// Marginal — the MusicBrainz web service. FROM Deadwax's MusicBrainzClient
// (src/Deadwax.Metadata/MusicBrainzClient.cs, 2026-10-31): the same pacing and
// retry rules (a fix to those goes to both); its own User-Agent, a release
// search, and no disk cache (a lookup is one sitting).
//
// Used the way MusicBrainz asks: a real User-Agent, at most one request a
// second, 503 ("slow down") retried with a growing pause. Only a 404 means
// "no such thing". Asked only when he presses Search or picks a release
// (spec §5), never in the background.

using System.Net;
using System.Text.Json;

namespace Marginal.Core;

public sealed class MusicBrainzException(string message) : Exception(message);

public sealed class MusicBrainzClient : IDisposable
{
    /// What a release lookup brings: the tracks with their lengths and
    /// artists, labels, the release group (for the first release date).
    public const string ReleaseIncludes = "recordings+artist-credits+labels+release-groups+media";

    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(1100);

    private readonly HttpClient _http;
    private DateTime _last = DateTime.MinValue;

    public MusicBrainzClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri("https://musicbrainz.org/ws/2/");
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Marginal/{AppVersion.Current} ( https://github.com/gsa700/marginal )");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// A release with its tracks, or null if MusicBrainz has no such release.
    public async Task<JsonDocument?> GetReleaseAsync(string releaseId, CancellationToken ct = default)
    {
        if (!WebLimits.IsMbid(releaseId)) throw new MusicBrainzException($"\"{releaseId}\" is not a MusicBrainz release ID.");
        var body = await GetAsync($"release/{releaseId}?inc={ReleaseIncludes}&fmt=json", ct);
        return body is null ? null : JsonDocument.Parse(body);
    }

    /// A release search. `query` is a Lucene query, encoded here as a whole:
    /// encoding the parts separately matched nothing (Deadwax, 2026-09-20).
    public async Task<JsonDocument?> SearchReleasesAsync(string query, int limit = 25, CancellationToken ct = default)
    {
        var body = await GetAsync($"release/?query={Uri.EscapeDataString(query)}&fmt=json&limit={limit}", ct);
        return body is null ? null : JsonDocument.Parse(body);
    }

    private async Task<byte[]?> GetAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var wait = _last + Pace - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _last = DateTime.UtcNow;

            using var response = await _http.GetAsync(path, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests && attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new MusicBrainzException($"MusicBrainz answered {(int)response.StatusCode}");
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
    }

    public void Dispose() => _http.Dispose();
}
