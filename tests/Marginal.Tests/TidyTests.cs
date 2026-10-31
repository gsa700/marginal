// 0.1.2: what a survey of a real lossy library turned up (2026-10-31): dates in
// two forms, numeric genres, compilations, purchase info, duplicate and missing
// tracks, and iTunes' gapless data, which must never count as junk. Every write
// test works on a copy of the fixture album in a temporary folder.

using Marginal.Core;

namespace Marginal.Tests;

public sealed class TidyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("marginal-tidy-").FullName;
    private readonly string _album;
    private readonly string _backups;

    public TidyTests()
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
    private string Track(string start) => Directory.GetFiles(_album).Single(f => Path.GetFileName(f).StartsWith(start));

    private void Write(IReadOnlyList<Change> changes)
    {
        var result = TagWriter.Write(changes, _backups);
        Assert.Empty(result.Problems);
    }

    /// Sets the same raw value on every track, by format, as other software wrote it.
    private void Tag(Func<TagLib.File, bool> set)
    {
        foreach (var path in Directory.GetFiles(_album).Where(TrackReader.IsMusic))
        {
            using var f = TagLib.File.Create(path);
            if (set(f)) f.Save();
        }
    }

    // ---- pure rules ------------------------------------------------------------

    [Theory]
    [InlineData(new[] { "2005-08-23T07:00:00Z", "2005" }, "2005")]
    [InlineData(new[] { "2008", "2008-10-20" }, "2008")]
    [InlineData(new[] { "2005-08-23T07:00:00Z", "2005-08-23" }, "2005-08-23")]
    [InlineData(new[] { "1975", "1977" }, null)]
    [InlineData(new[] { "2005", "2005" }, null)]
    public void One_year_in_two_forms_becomes_one(string[] dates, string? expected) =>
        Assert.Equal(expected, Tidy.AlbumDate(dates));

    [Theory]
    [InlineData("17", "Rock")]
    [InlineData("(13)", "Pop")]
    [InlineData("(17)Rock", "Rock")]
    [InlineData("Rock", null)]
    [InlineData("255", null)]
    [InlineData(null, null)]
    public void Numeric_genres_get_their_names(string? genre, string? name) => Assert.Equal(name, Tidy.GenreName(genre));

    // ---- gapless data is not junk ---------------------------------------------------

    [Fact]
    public void ITunSMPB_and_ITunNORM_are_never_junk()
    {
        var m4a = Track("03");
        using (var f = TagLib.File.Create(m4a))
        {
            var apple = (TagLib.Mpeg4.AppleTag)f.GetTag(TagLib.TagTypes.Apple, true);
            apple.SetDashBox("com.apple.iTunes", "iTunSMPB", " 00000000 00000840 000001C0 0000000000ABCDEF");
            apple.SetDashBox("com.apple.iTunes", "iTunNORM", " 00000000 00000000");
            f.Save();
        }
        var t = TrackReader.Read(m4a);
        Assert.Contains(t.Raw, r => r.Key.EndsWith("iTunSMPB") && !r.Junk);
        Assert.Contains(t.Raw, r => r.Key.EndsWith("iTunNORM") && !r.Junk);
        Assert.DoesNotContain(Pending.CleanJunk(Read()), c => c.Key.Contains("iTunSMPB") || c.Key.Contains("iTunNORM"));
    }

    // ---- compilations ---------------------------------------------------------------

    [Fact]
    public void Marking_a_compilation_writes_all_three_formats_and_quiets_years()
    {
        // Each track its own year, as a best-of from the iTunes Store has.
        var years = new Queue<string>(["1975", "1981", "1993"]);
        Write(Pending.SetField(Read(), FieldMap.Date, _ => years.Dequeue()));
        Assert.Contains(Read().Checks, c => c.Text == "date differs");

        Write(Pending.MarkCompilation(Read()));
        var album = Read();
        Assert.True(album.IsCompilation);
        Assert.All(album.Tracks, t => Assert.Equal("1", t.Get(FieldMap.Compilation)));
        Assert.DoesNotContain(album.Checks, c => c.Text is "date differs" or "genre differs");
        Assert.Equal(3, album.Tracks.Select(t => t.Get(FieldMap.Date)).Distinct().Count());   // left as they are
    }

    [Fact]
    public void A_spread_of_years_looks_like_a_compilation_until_marked()
    {
        var years = new Queue<string>(["1975", "1981", "1993"]);
        Write(Pending.SetField(Read(), FieldMap.Date, _ => years.Dequeue()));
        var album = Read();
        Assert.True(album.LooksLikeCompilation);
        Assert.Contains(album.Checks, c => c.Text == "looks like a compilation");
    }

    [Fact]
    public void An_itunes_cpil_of_0_reads_as_0_not_bytes()
    {
        Tag(f => f.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag a && (a.IsCompilation = false) == false);
        var m4a = TrackReader.Read(Track("03"));
        Assert.Equal("0", m4a.Get(FieldMap.Compilation));
        Assert.False(Read().IsCompilation);
    }

    // ---- dates and genres -------------------------------------------------------------

    [Fact]
    public void Dates_in_two_forms_are_one_check_and_one_fix()
    {
        var forms = new Queue<string>(["2005-08-23T07:00:00Z", "2005", "2005"]);
        Write(Pending.SetField(Read(), FieldMap.Date, _ => forms.Dequeue()));
        var album = Read();
        Assert.Contains(album.Checks, c => c.Text == "dates written in different forms");
        Assert.DoesNotContain(album.Checks, c => c.Text == "date differs");

        Write(Pending.TidyDates(album));
        album = Read();
        Assert.All(album.Tracks, t => Assert.Equal("2005", t.Get(FieldMap.Date)));
        Assert.DoesNotContain(album.Checks, c => c.Text.StartsWith("date"));
    }

    [Fact]
    public void Numeric_genres_are_named()
    {
        Write(Pending.SetField(Read(), FieldMap.Genre, "17"));
        Assert.Contains(Read().Checks, c => c.Text == "genre is a number");
        Write(Pending.NameGenres(Read()));
        Assert.All(Read().Tracks, t => Assert.Equal("Rock", t.Get(FieldMap.Genre)));
        Assert.DoesNotContain(Read().Checks, c => c.Text == "genre is a number");
    }

    // ---- purchase info -----------------------------------------------------------------

    [Fact]
    public void Purchase_info_is_found_and_removed_and_nothing_else()
    {
        var m4a = Track("03");
        using (var f = TagLib.File.Create(m4a))
        {
            var apple = (TagLib.Mpeg4.AppleTag)f.GetTag(TagLib.TagTypes.Apple, true);
            apple.SetText(TagLib.ByteVector.FromString("apID", TagLib.StringType.Latin1), "someone@example.com");
            apple.SetText(TagLib.ByteVector.FromString("ownr", TagLib.StringType.Latin1), "Some One");
            apple.SetText(TagLib.ByteVector.FromString("purd", TagLib.StringType.Latin1), "2010-01-01 00:00:00");
            f.Save();
        }
        Assert.Contains(Read().Checks, c => c.Text.StartsWith("purchase info"));
        var changes = Pending.RemovePurchaseInfo(Read());
        Assert.Equal(["apID", "ownr"], changes.Select(c => c.Key).Order());
        Write(changes);
        var t = TrackReader.Read(m4a);
        Assert.DoesNotContain(t.Raw, r => r.Key is "apID" or "ownr");
        Assert.Contains(t.Raw, r => r.Key == "purd");
        Assert.DoesNotContain(Read().Checks, c => c.Text.StartsWith("purchase info"));
    }

    // ---- track numbers --------------------------------------------------------------------

    [Fact]
    public void Two_tracks_with_one_number_and_missing_tracks_are_said()
    {
        Write(Pending.SetField(Read(), FieldMap.Track, t => Path.GetFileName(t.Path).StartsWith("03") ? "2/5" : Path.GetFileName(t.Path)[..2].TrimStart('0') + "/5"));
        var album = Read();
        Assert.Equal(2, album.Checks.Count(c => c.Text == "track 2 twice"));
        Assert.Contains(album.Checks, c => c.Text == "3 of 5 tracks");
    }
}

public sealed class ArtSearchTests
{
    private static Album Fixture() =>
        AlbumReader.Read(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fixture Band - Test Album"));

    private static ReleaseCandidate Candidate(string title, string artist, int tracks) =>
        new("b84ee12a-09ef-421b-82de-0441a926375b", title, artist, "1976", "US", "CD", tracks, "", "", "", 100, false);

    [Fact]
    public void A_search_result_is_a_clear_match_only_with_title_artist_and_track_count()
    {
        var album = Fixture();
        Assert.True(Lookup.IsClearMatch(Candidate("Test Album", "Fixture Band", 3), album));
        Assert.True(Lookup.IsClearMatch(Candidate("test album!", "FIXTURE BAND", 3), album));   // letters and digits, case aside
        Assert.False(Lookup.IsClearMatch(Candidate("Test Album", "Fixture Band", 12), album));
        Assert.False(Lookup.IsClearMatch(Candidate("Test Album (Deluxe)", "Fixture Band", 3), album));
        Assert.False(Lookup.IsClearMatch(Candidate("Test Album", "Someone Else", 3), album));
        Assert.True(Lookup.IsClearMatch(Candidate("Fixture Band's Test Album", "Fixture Band", 3), album));
        Assert.False(Lookup.IsClearMatch(Candidate("Fixture Band's Other Album", "Fixture Band", 3), album));
    }
}

public sealed class TagKindTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("marginal-v1-").FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    [Fact]
    public void A_write_never_adds_an_ID3v1_tag_the_file_did_not_have()
    {
        // TagLib# gives every MP3 an ID3v1 tag in memory; a Label change wrote
        // it to 1,212 of Dave's files (0.1.2's Debug build, 2026-10-31).
        var album = Path.Combine(_root, "Album");
        Directory.CreateDirectory(album);
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fixture Band - Test Album");
        foreach (var f in Directory.GetFiles(fixture)) File.Copy(f, Path.Combine(album, Path.GetFileName(f)));
        var backups = Path.Combine(_root, "backups");
        Assert.Empty(TagWriter.Write(Pending.CleanJunk(AlbumReader.Read(album)), backups).Problems);
        Assert.DoesNotContain(AlbumReader.Read(album).Tracks, t => t.HasId3v1);

        Assert.Empty(TagWriter.Write(Pending.SetField(AlbumReader.Read(album), FieldMap.Label, "Vertigo"), backups).Problems);
        Assert.Empty(TagWriter.Write(Pending.SetField(AlbumReader.Read(album), FieldMap.Date, "1976"), backups).Problems);
        var cover = File.ReadAllBytes(Path.Combine(album, "cover.jpg"));
        Assert.Empty(TagWriter.Write(Pending.EmbedFront(AlbumReader.Read(album), cover), backups).Problems);
        Assert.DoesNotContain(AlbumReader.Read(album).Tracks, t => t.HasId3v1 || t.HasApe);
    }
}

public sealed class Id3v1PolicyTests
{
    private static Album Fixture() =>
        AlbumReader.Read(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fixture Band - Test Album"));

    [Fact]
    public void Changing_an_MP3_with_ID3v1_takes_it_off_and_says_so()
    {
        var album = Fixture();
        var mp3 = album.Tracks.Single(t => t.Format == AudioFormat.Mp3);
        Assert.True(mp3.HasId3v1);
        var list = Pending.DropId3v1(album, Pending.SetField(album, FieldMap.Label, "Vertigo"));
        var v1 = Assert.Single(list, c => c.Op == ChangeOp.RemoveTag);
        Assert.Equal(mp3.Path, v1.Path);
        Assert.Equal(Pending.Id3v1Along, v1.From);

        // The label taken back out: the ID3v1 line goes with it.
        Assert.Empty(Pending.DropId3v1(album, list.Where(c => c.Op != ChangeOp.SetField).ToList()));
    }

    [Fact]
    public void An_asked_for_ID3v1_removal_stays_on_its_own()
    {
        var album = Fixture();
        var junk = Pending.CleanJunk(album);
        Assert.Equal(junk.Count, Pending.DropId3v1(album, junk).Count);
    }
}

public sealed class MergeTests
{
    [Fact]
    public void Removals_of_frames_sharing_a_key_all_stay()
    {
        Change Priv(string v) => new("/a/01.mp3", ChangeOp.RemoveFrame, ChangeKind.Remove, "PRIV", v, null);
        var merged = Pending.Merge([], [Priv("WM/WMContentID (16 bytes)"), Priv("WM/UniqueFileIdentifier (114 bytes)"), Priv("WM/Provider (8 bytes)")]);
        Assert.Equal(3, merged.Count);
        Assert.Equal(3, Pending.Merge(merged, [Priv("WM/Provider (8 bytes)")]).Count);   // the same one again is one
    }
}
