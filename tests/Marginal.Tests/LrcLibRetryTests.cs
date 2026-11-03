using System.Net;
using System.Text;
using Marginal.App;
using Marginal.Core;

namespace Marginal.Tests;

/// LRCLIB not answering (Dave, 2026-10-31: a 503 twice in a row ended Find
/// lyrics for the album after its first track). No real LRCLIB is asked.
public sealed class LrcLibRetryTests
{
    // The retries are tested in Spindle (LrcLibTests), with the client, since 2026-11-02.

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
