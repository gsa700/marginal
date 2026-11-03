using System.Security.Cryptography;
using System.Text.Json;
using Marginal.App;
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

    // ---- Find lyrics for the album: what is already pending ---------------------------

    [Fact]
    public void Lyrics_waiting_in_Pending_count_as_found()
    {
        var album = Read();
        var first = album.Tracks.First(t => t.Problem is null);
        var other = album.Tracks.First(t => t.Problem is null && t.Path != first.Path);
        Assert.False(Pending.HasLyrics([], first));

        // In the tag only, in a .lrc only, or both: each is lyrics on the way.
        Assert.True(Pending.HasLyrics(Pending.SetLyrics(first, Synced, tag: true, lrc: false, timesInTag: false), first));
        Assert.True(Pending.HasLyrics(Pending.SetLyrics(first, Synced, tag: false, lrc: true, timesInTag: false), first));
        var both = Pending.SetLyrics(first, Synced, tag: true, lrc: true, timesInTag: false);
        Assert.True(Pending.HasLyrics(both, first));
        Assert.False(Pending.HasLyrics(both, other));
    }

    [Fact]
    public void The_album_note_says_how_many_were_already_pending()
    {
        Assert.Equal("Every track has synced lyrics or lyrics waiting in Pending.",
            LyricsPanel.FindAllNote(0, 0, 0, 0, false, null, alreadyPending: 12));
        Assert.Equal("1 of 1 tracks found and added to Pending (12 already in Pending, not asked again); the rest are marked to look at.",
            LyricsPanel.FindAllNote(1, 1, 1, 0, false, null, alreadyPending: 12));
    }
}
