using System.Net;
using System.Text;
using Marginal.App;
using Marginal.Core;

namespace Marginal.Tests;

/// LRCLIB not answering (Dave, 2026-10-31: a 503 twice in a row ended Find
/// lyrics for the album after its first track). No real LRCLIB is asked.
public sealed class LrcLibRetryTests
{
    private static readonly TimeSpan[] NoWait = [TimeSpan.Zero, TimeSpan.Zero];

    /// Answers with each status in turn, then the last one forever; counts the questions.
    private sealed class Script(params HttpStatusCode[] codes) : HttpMessageHandler
    {
        public int Asked;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var code = codes[Math.Min(Asked, codes.Length - 1)];
            Asked++;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task A_503_is_asked_three_times_before_giving_up()
    {
        var script = new Script(HttpStatusCode.ServiceUnavailable);
        using var lrc = new LrcLibClient(script, NoWait);
        var e = await Assert.ThrowsAsync<LrcLibException>(() => lrc.SearchAsync("A", "T"));
        Assert.Equal(3, script.Asked);
        Assert.Contains("503", e.Message);
    }

    [Fact]
    public async Task An_answer_on_the_third_try_is_taken()
    {
        var script = new Script(HttpStatusCode.ServiceUnavailable, HttpStatusCode.BadGateway, HttpStatusCode.OK);
        using var lrc = new LrcLibClient(script, NoWait);
        Assert.Empty(await lrc.SearchAsync("A", "T"));
        Assert.Equal(3, script.Asked);
    }

    [Fact]
    public async Task Not_found_and_other_4xx_are_not_asked_again()
    {
        var notFound = new Script(HttpStatusCode.NotFound);
        using (var lrc = new LrcLibClient(notFound, NoWait)) Assert.Null(await lrc.GetAsync("A", "T", "", TimeSpan.Zero));
        Assert.Equal(1, notFound.Asked);

        var tooMany = new Script(HttpStatusCode.TooManyRequests);
        using (var lrc = new LrcLibClient(tooMany, NoWait)) await Assert.ThrowsAsync<LrcLibException>(() => lrc.SearchAsync("A", "T"));
        Assert.Equal(1, tooMany.Asked);
    }

    [Fact]
    public void The_default_waits_are_2_then_6_seconds() =>
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)], LrcLibClient.DefaultRetryWaits);

    [Fact]
    public void The_album_note_says_what_was_not_asked()
    {
        Assert.Equal("Every track already has synced lyrics.", LyricsPanel.FindAllNote(0, 0, 0, 0, false, null));
        Assert.Equal("6 of 10 tracks found and added to Pending; the rest are marked to look at.",
            LyricsPanel.FindAllNote(10, 10, 6, 0, false, null));
        Assert.Equal("6 of 10 tracks found and added to Pending. 2 couldn't be asked (LRCLIB answered 503): press Find lyrics for the album again for those.",
            LyricsPanel.FindAllNote(10, 10, 6, 2, false, "LRCLIB answered 503"));
        // Stopped after three failures at track 4: those 3 and the 6 never asked.
        Assert.Equal("1 of 10 tracks found and added to Pending. LRCLIB stopped answering (LRCLIB answered 503): 9 not asked. Press Find lyrics for the album again later for those.",
            LyricsPanel.FindAllNote(10, 4, 1, 3, true, "LRCLIB answered 503"));
    }
}
