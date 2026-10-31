using System.Security.Cryptography;
using System.Text.Json;
using Marginal.Core;

namespace Marginal.Tests;

/// Lyrics without the network. The LRCLIB answers here are MADE UP in the
/// shape its API documents (its API answered 502 to everything on 2026-10-31,
/// so none could be saved), with placeholder words: no real song's lyrics.
public sealed class LyricsTests : IDisposable
{
    private const string Synced = "[00:01.00] line one\n[00:02.50] line two\n[00:04.25] line three";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "marginal-lyrics-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _album;
    private readonly string _backups;

    public LyricsTests()
    {
        _album = Path.Combine(_root, "Album");
        _backups = Path.Combine(_root, "backups");
        Directory.CreateDirectory(_album);
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fixture Band - Test Album");
        foreach (var f in Directory.GetFiles(fixture)) File.Copy(f, Path.Combine(_album, Path.GetFileName(f)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private Album Read() => AlbumReader.Read(_album);
    private TrackFile Track(string start) => Read().Tracks.Single(t => t.FileName.StartsWith(start));
    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ---- LRC -----------------------------------------------------------------------

    [Fact]
    public void Parses_times_in_order()
    {
        var lines = Lrc.Parse("[ar:Somebody]\n[00:02.50] second\n[00:01.00] first\n\n[01:05] third");
        Assert.Equal(["first", "second", "third"], lines.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromMilliseconds(2500), lines[1].Time);
        Assert.Equal(TimeSpan.FromSeconds(65), lines[2].Time);
    }

    [Fact]
    public void A_line_with_two_times_is_sung_twice_and_offset_moves_them()
    {
        var lines = Lrc.Parse("[offset:+500]\n[00:10.00][00:30.00] chorus");
        Assert.Equal([TimeSpan.FromSeconds(9.5), TimeSpan.FromSeconds(29.5)], lines.Select(l => l.Time));
        Assert.All(lines, l => Assert.Equal("chorus", l.Text));
    }

    [Fact]
    public void Formats_and_reads_back()
    {
        var text = Lrc.Format(Lrc.Parse(Synced));
        Assert.Equal(Synced, text);
    }

    [Fact]
    public void Plain_takes_the_times_out()
    {
        Assert.Equal("line one\nline two\nline three", Lrc.Plain(Synced));
        Assert.Equal("just words", Lrc.Plain("just words  \r\n"));
    }

    // ---- LRCLIB's answers and the 2 s rule ---------------------------------------------

    private static LrcLibResult Result(string track, double duration, bool synced = true, string artist = "Fixture Band", string album = "Test Album") =>
        LrcLibClient.Result(JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = 1, ["trackName"] = track, ["artistName"] = artist, ["albumName"] = album, ["duration"] = duration,
            ["instrumental"] = false, ["plainLyrics"] = "line one\nline two", ["syncedLyrics"] = synced ? Synced : null,
        })).RootElement);

    [Fact]
    public void Reads_an_answer_in_lrclibs_shape()
    {
        var r = Result("Second", 61.2);
        Assert.Equal(("Second", "Fixture Band", "Test Album", 61.2), (r.Track, r.Artist, r.Album, r.Duration));
        Assert.True(r.HasSynced && r.HasPlain);
        Assert.False(Result("Second", 61, synced: false).HasSynced);
    }

    [Theory]
    [InlineData(61.0, true)]       // the same length
    [InlineData(62.4, true)]       // within 2 s
    [InlineData(64.0, false)]      // 3 s: asked first
    [InlineData(92.0, false)]      // a live version
    public void Automatic_only_within_two_seconds(double duration, bool automatic) =>
        Assert.Equal(automatic, LrcLibClient.Fit(Result("Second", duration), "Fixture Band", "Second", TimeSpan.FromSeconds(61)).Automatic);

    [Fact]
    public void Another_title_or_artist_is_never_automatic()
    {
        Assert.False(LrcLibClient.Fit(Result("Second (Live)", 61), "Fixture Band", "Second", TimeSpan.FromSeconds(61)).Automatic);
        Assert.False(LrcLibClient.Fit(Result("Second", 61, artist: "Someone Else"), "Fixture Band", "Second", TimeSpan.FromSeconds(61)).Automatic);
        // Case and punctuation do not count as different.
        Assert.True(LrcLibClient.Fit(Result("second!", 61, artist: "fixture band"), "Fixture Band", "Second", TimeSpan.FromSeconds(61)).Automatic);
    }

    [Fact]
    public void Ranks_automatic_then_synced_then_nearest()
    {
        var ranked = LrcLibClient.Rank([Result("Second", 92), Result("Second", 61, synced: false), Result("Second", 61.5)],
            "Fixture Band", "Second", TimeSpan.FromSeconds(61));
        Assert.Equal([61.5, 61.0, 92.0], ranked.Select(f => f.Result.Duration));
    }

    // ---- writing them --------------------------------------------------------------------

    [Fact]
    public void Synced_lyrics_go_to_the_tag_as_words_and_to_a_lrc_with_times()
    {
        var mp3 = Track("02");
        var changes = Pending.SetLyrics(mp3, Synced, tag: true, lrc: true, timesInTag: false);
        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c is { Key: "USLT", Kind: ChangeKind.Add });
        Assert.Contains(changes, c => c is { Key: ".lrc", Kind: ChangeKind.Add, Op: ChangeOp.WriteText });
        Assert.Empty(TagWriter.Write(changes, _backups).Problems);

        var after = Track("02");
        Assert.Equal("line one\nline two\nline three", Lrc.Tidy(after.Get(FieldMap.Lyrics)));
        Assert.Equal(LyricsKind.Synced, after.Lyrics);            // the .lrc counts
        Assert.Equal(Synced, Lrc.Tidy(File.ReadAllText(after.LrcPath)));
    }

    [Fact]
    public void Times_in_the_tag_when_asked()
    {
        var flac = Track("01");
        Assert.Empty(TagWriter.Write(Pending.SetLyrics(flac, Synced, tag: true, lrc: false, timesInTag: true), _backups).Problems);
        Assert.Equal(Synced, Lrc.Tidy(Track("01").Get(FieldMap.Lyrics)));
        Assert.False(File.Exists(Track("01").LrcPath));
    }

    [Fact]
    public void Lyrics_in_all_three_formats_and_undo_is_exact()
    {
        var shas = Directory.GetFiles(_album).ToDictionary(f => f, Sha);
        var changes = Read().Tracks.SelectMany(t => Pending.SetLyrics(t, Synced, tag: true, lrc: true, timesInTag: false)).ToList();
        Assert.Empty(TagWriter.Write(changes, _backups).Problems);
        Assert.All(Read().Tracks, t => Assert.Equal("line one\nline two\nline three", Lrc.Tidy(t.Get(FieldMap.Lyrics))));
        Assert.Equal(3, Directory.GetFiles(_album, "*.lrc").Length);

        Assert.Empty(TagWriter.Undo(TagWriter.Latest(_backups)!));
        Assert.Empty(Directory.GetFiles(_album, "*.lrc"));           // new .lrc files go
        foreach (var (file, sha) in shas) Assert.Equal(sha, Sha(file));
    }

    [Fact]
    public void Same_lyrics_make_no_change_and_empty_removes()
    {
        var flac = Track("01");                                    // plain "A plain line"
        Assert.Empty(Pending.SetLyrics(flac, "A plain line", tag: true, lrc: true, timesInTag: false));
        var remove = Assert.Single(Pending.SetLyrics(flac, "", tag: true, lrc: true, timesInTag: false));
        Assert.Equal(ChangeKind.Remove, remove.Kind);
        Assert.Empty(TagWriter.Write([remove], _backups).Problems);
        Assert.Equal(LyricsKind.None, Track("01").Lyrics);
    }
}
