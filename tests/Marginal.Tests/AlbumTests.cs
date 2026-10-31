using Marginal.Core;

namespace Marginal.Tests;

/// The fixture album: one track in each format, with faults on purpose.
///   01 First.flac   clean, plain lyrics, 300 px embedded front cover
///   02 Second.mp3   ID3v2.3 + ID3v1, no album artist
///   03 Third.m4a    album "Test Album (Remastered)", synced lyrics
///   cover.jpg       600×600, so every embedded cover differs from it
public class AlbumTests
{
    private static readonly string Folder =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fixture Band - Test Album");

    private static readonly Album Album = AlbumReader.Read(Folder);

    private static TrackFile Track(string name) => Album.Tracks.Single(t => t.FileName.StartsWith(name));

    [Fact]
    public void Reads_all_three_formats_in_track_order()
    {
        Assert.Equal(["01 First.flac", "02 Second.mp3", "03 Third.m4a"], Album.Tracks.Select(t => t.FileName));
        Assert.All(Album.Tracks, t => Assert.Null(t.Problem));
        Assert.Equal([AudioFormat.Flac, AudioFormat.Mp3, AudioFormat.M4a], Album.Tracks.Select(t => t.Format));
    }

    [Fact]
    public void Fields_come_from_each_format_s_own_keys()
    {
        Assert.Equal("Fixture Band", Track("01").Get(FieldMap.AlbumArtist));
        Assert.Equal("Fixture Band", Track("03").Get(FieldMap.AlbumArtist));
        Assert.Null(Track("02").Get(FieldMap.AlbumArtist));
        Assert.Equal("2/3", Track("02").Get(FieldMap.Track));
        Assert.Equal("3/3", Track("03").Get(FieldMap.Track));
        Assert.Equal("1976", Track("02").Get(FieldMap.Date));   // TYER, the file being ID3v2.3
    }

    [Fact]
    public void Raw_frames_carry_the_real_keys()
    {
        Assert.Contains(Track("02").Raw, r => r.Key == "TALB" && r.Value == "Test Album");
        Assert.Contains(Track("03").Raw, r => r.Key == "aART" && r.Value == "Fixture Band");
        Assert.Contains(Track("01").Raw, r => r.Key.Equals("ALBUMARTIST", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Tag_versions_and_leftovers()
    {
        Assert.Equal("v2.3 + v1", Track("02").TagVersions);
        Assert.True(Track("02").HasId3v1);
        Assert.False(Track("01").HasId3v1);
    }

    [Fact]
    public void Lyrics_kinds()
    {
        Assert.Equal(LyricsKind.Plain, Track("01").Lyrics);
        Assert.Equal(LyricsKind.None, Track("02").Lyrics);
        Assert.Equal(LyricsKind.Synced, Track("03").Lyrics);
    }

    [Fact]
    public void Summary_shows_where_values_differ()
    {
        var album = Album.Fields.Single(f => f.Field == FieldMap.Album);
        Assert.True(album.Differs);
        Assert.Equal("Test Album", album.Common);
        Assert.Equal(2, album.Values[0].Count);
        var artist = Album.Fields.Single(f => f.Field == FieldMap.AlbumArtist);
        Assert.Equal(1, artist.Missing);
    }

    [Fact]
    public void Checks_find_the_planted_faults()
    {
        Assert.Contains(Album.Checks, c => c is { Tone: Tone.Missing, Text: "no album artist", Track: "02 Second.mp3" });
        Assert.Contains(Album.Checks, c => c is { Tone: Tone.Fixable, Text: "album differs", Track: "03 Third.m4a" });
        Assert.Contains(Album.Checks, c => c is { Tone: Tone.Fixable, Text: "ID3v1 leftover", Track: "02 Second.mp3" });
        Assert.Contains(Album.Checks, c => c.Text == "embedded art differs from cover.jpg");
        Assert.Equal(Tone.Missing, Album.Status);
    }

    [Fact]
    public void Folder_cover_and_embedded_art_sizes()
    {
        Assert.Equal((600, 600), (Album.Cover!.Width, Album.Cover.Height));
        Assert.Null(Album.Back);
        // ffmpeg types the FLAC's picture "Other"; it is still the cover.
        Assert.Equal((300, 300), (Track("01").EmbeddedCover()!.Width, Track("01").EmbeddedCover()!.Height));
        Assert.Equal(300, Track("02").EmbeddedCover()!.Width);
    }

    [Fact]
    public void Library_finds_the_album_folder()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        Assert.Equal([Folder], Library.AlbumFolders(root));
    }

    [Theory]
    [InlineData(500, 500, true)]
    [InlineData(500, 497, true)]     // a scan a few pixels off: square to the eye
    [InlineData(500, 494, false)]
    [InlineData(500, 393, false)]    // a CD back inlay
    public void Nearly_square_within_one_percent(int w, int h, bool square) =>
        Assert.Equal(square, AlbumReader.NearlySquare(w, h));

    [Fact]
    public void Album_table_leaves_out_each_track_s_own_fields()
    {
        Assert.DoesNotContain(FieldMap.Title, FieldMap.AlbumTable);
        Assert.DoesNotContain(FieldMap.Track, FieldMap.AlbumTable);
        Assert.DoesNotContain(FieldMap.Lyrics, FieldMap.AlbumTable);
        Assert.Contains(FieldMap.AlbumArtist, FieldMap.AlbumTable);
    }

    [Theory]
    [InlineData("", LyricsKind.None)]
    [InlineData("Just words\nand more", LyricsKind.Plain)]
    [InlineData("[00:12.40]A line\n[00:15.00]Another", LyricsKind.Synced)]
    [InlineData("[ar:Someone]\n[01:02]A line", LyricsKind.Synced)]
    public void Lyrics_kind_from_text(string text, LyricsKind kind) =>
        Assert.Equal(kind, TrackReader.KindOf(text));
}
