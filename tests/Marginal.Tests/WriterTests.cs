using System.Security.Cryptography;
using Marginal.Core;

namespace Marginal.Tests;

/// Every write test works on a fresh copy of the fixture album in a temporary
/// folder, with its own backup folder: nothing outside it is touched.
public sealed class WriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "marginal-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _album;
    private readonly string _backups;

    public WriterTests()
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

    /// A backup's head or tail as the bytes it stands for (compressed since 0.1.2).
    internal static byte[] Stored(string path)
    {
        if (!path.EndsWith(".gz")) return File.ReadAllBytes(path);
        using var gz = new System.IO.Compression.GZipStream(File.OpenRead(path), System.IO.Compression.CompressionMode.Decompress);
        using var m = new MemoryStream();
        gz.CopyTo(m);
        return m.ToArray();
    }
    private string FilePath(string start) => Directory.GetFiles(_album).Single(f => Path.GetFileName(f).StartsWith(start));
    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private WriteResult Write(IReadOnlyList<Change> changes)
    {
        var result = TagWriter.Write(changes, _backups);
        Assert.Empty(result.Problems);
        return result;
    }

    [Fact]
    public void Fills_a_missing_album_artist_and_touches_nothing_else()
    {
        var changes = Pending.SetField(Read(), FieldMap.AlbumArtist, "Fixture Band");
        var only = Assert.Single(changes);                    // only the MP3 lacks it
        Assert.Equal(ChangeKind.Add, only.Kind);
        Assert.Equal("TPE2", only.Key);

        var audio = TagWriter.AudioHash(FilePath("02"));
        Write(changes);

        var after = Read();
        Assert.All(after.Tracks, t => Assert.Equal("Fixture Band", t.Get(FieldMap.AlbumArtist)));
        Assert.DoesNotContain(after.Checks, c => c.Text == "no album artist");
        Assert.Equal(audio, TagWriter.AudioHash(FilePath("02")));
    }

    [Fact]
    public void Sets_a_field_in_all_three_formats()
    {
        Write(Pending.SetField(Read(), FieldMap.Label, "Vertigo"));
        var after = Read();
        Assert.All(after.Tracks, t => Assert.Equal("Vertigo", t.Get(FieldMap.Label)));
        Assert.Contains(after.Tracks.Single(t => t.Format == AudioFormat.M4a).Raw, r => r.Key == "----:com.apple.iTunes:LABEL");
        Assert.Contains(after.Tracks.Single(t => t.Format == AudioFormat.Mp3).Raw, r => r.Key == "TPUB");
    }

    [Fact]
    public void Makes_differing_album_names_agree()
    {
        var changes = Pending.SetField(Read(), FieldMap.Album, "Test Album");
        var only = Assert.Single(changes);
        Assert.Equal("03 Third.m4a", only.FileName);
        Assert.Equal(ChangeKind.Change, only.Kind);
        Write(changes);
        Assert.False(Read().Fields.Single(f => f.Field == FieldMap.Album).Differs);
    }

    [Fact]
    public void Changes_the_year_on_an_id3v23_file()
    {
        Write(Pending.SetField(Read(), FieldMap.Date, "1977"));
        var mp3 = Read().Tracks.Single(t => t.Format == AudioFormat.Mp3);
        Assert.Equal("1977", mp3.Get(FieldMap.Date));
        Assert.StartsWith("v2.3", mp3.TagVersions);           // still a v2.3 tag
    }

    [Fact]
    public void Disc_number_and_total()
    {
        Write(Pending.SetField(Read(), FieldMap.Disc, "1/2"));
        Assert.All(Read().Tracks, t => Assert.Equal("1/2", t.Get(FieldMap.Disc)));
    }

    [Fact]
    public void Track_number_and_total_in_all_three_formats()
    {
        Write(Pending.SetField(Read(), FieldMap.Track, t => t.FileName[..2].TrimStart('0') + "/9"));
        Assert.Equal(["1/9", "2/9", "3/9"], Read().Tracks.Select(t => t.Get(FieldMap.Track)));
    }

    [Fact]
    public void Removes_a_field()
    {
        var changes = Pending.SetField(Read(), FieldMap.Genre, "");
        Assert.All(changes, c => Assert.Equal(ChangeKind.Remove, c.Kind));
        Write(changes);
        Assert.All(Read().Tracks, t => Assert.Null(t.Get(FieldMap.Genre)));
    }

    [Fact]
    public void Cleans_the_id3v1_leftover()
    {
        var changes = Pending.CleanJunk(Read());
        Assert.Contains(changes, c => c is { Key: "ID3v1", FileName: "02 Second.mp3" });
        Write(changes);
        var mp3 = Read().Tracks.Single(t => t.Format == AudioFormat.Mp3);
        Assert.False(mp3.HasId3v1);
        Assert.Equal("Fixture Band", mp3.Get(FieldMap.Artist));  // the ID3v2 tag is untouched
    }

    [Fact]
    public void Backs_up_only_what_is_not_audio_and_undo_rebuilds_it_byte_for_byte()
    {
        var path = FilePath("02");
        var original = Sha(path);
        var size = new FileInfo(path).Length;
        var result = Write(Pending.SetField(Read(), FieldMap.AlbumArtist, "Fixture Band"));
        Assert.NotEqual(original, Sha(path));

        var batch = TagWriter.Latest(_backups);
        Assert.NotNull(batch);
        var entry = Assert.Single(batch!.Files);
        Assert.Equal(original, entry.Sha256);
        // The ID3v2 tag in front and the ID3v1 tag behind: no audio at all.
        var (start, end) = (Stored(entry.Head).Length, Stored(entry.Tail).Length);
        Assert.Equal(128, end);
        Assert.True(start + end < size / 4, $"backup {start + end} bytes of a {size}-byte file");

        Assert.Empty(TagWriter.Undo(batch));
        Assert.Equal(original, Sha(path));
        Assert.Null(TagWriter.Latest(_backups));              // nothing left to undo
        Assert.NotNull(result.Batch);
    }

    [Fact]
    public void Undo_refuses_a_file_whose_audio_changed_since()
    {
        var path = FilePath("01");
        Write(Pending.SetField(Read(), FieldMap.Genre, "Blues"));
        // Something else rewrites the audio after the write (a re-rip, say).
        var (start, end) = TagWriter.AudioRange(path);
        var bytes = File.ReadAllBytes(path);
        bytes[(start + end) / 2] ^= 0x01;
        File.WriteAllBytes(path, bytes);
        var changed = Sha(path);

        var problems = TagWriter.Undo(TagWriter.Latest(_backups)!);
        Assert.Contains(problems, p => p.File == "01 First.flac" && p.Problem.Contains("audio has changed"));
        Assert.Equal(changed, Sha(path));                     // left exactly as it was
    }

    [Fact]
    public void Several_writes_undo_in_turn_back_to_the_start()
    {
        var shas = Directory.GetFiles(_album).ToDictionary(f => f, Sha);
        Write(Pending.SetField(Read(), FieldMap.Genre, "Blues"));
        Write(Pending.SetField(Read(), FieldMap.Label, "Vertigo"));
        Write(Pending.CleanJunk(Read()));
        while (TagWriter.Latest(_backups) is { } batch) Assert.Empty(TagWriter.Undo(batch));
        foreach (var (file, sha) in shas) Assert.Equal(sha, Sha(file));
    }

    [Fact]
    public void Leaves_no_working_copy_behind()
    {
        Write(Pending.SetField(Read(), FieldMap.Genre, "Hard Rock"));
        Assert.DoesNotContain(Directory.GetFiles(_album), f => Path.GetFileName(f).StartsWith(TagWriter.TempPrefix));
        Assert.DoesNotContain(Directory.GetFiles(_album), f => f.EndsWith(".marginal-old"));
        Assert.Equal(3, Read().Tracks.Count);
    }

    [Fact]
    public void Merge_keeps_the_last_edit_and_drops_a_change_back()
    {
        var album = Read();
        var first = Pending.SetField(album, FieldMap.Genre, "Hard Rock");
        var list = Pending.Merge([], first);
        list = Pending.Merge(list, Pending.SetField(album, FieldMap.Genre, "Blues"));
        Assert.Equal(3, list.Count);
        Assert.All(list, c => Assert.Equal("Blues", c.To));
        Assert.All(list, c => Assert.Equal("Rock", c.From));  // From stays what the file holds
        list = Pending.Merge(list, list.Select(c => c with { To = "Rock" }));
        Assert.Empty(list);                                   // back to what is on disk
    }

    [Fact]
    public void A_local_folder_is_not_a_share() => Assert.False(Library.IsOnNetworkShare(_album));

    [Fact]
    public void Removes_store_junk_atoms_from_an_m4a_but_keeps_gapless_data()
    {
        // Amazon's leftovers are junk; iTunes' gapless (iTunSMPB) and Sound
        // Check (iTunNORM) data are not, since 0.1.2.
        var m4a = FilePath("03");
        using (var f = TagLib.File.Create(m4a))
        {
            var apple = (TagLib.Mpeg4.AppleTag)f.GetTag(TagLib.TagTypes.Apple, true);
            apple.SetDashBox("com.amazon", "AmazonOrderId", "123-4567890");
            apple.SetDashBox("com.apple.iTunes", "iTunSMPB", " 00000000 00000840 000001C0 0000000000ABCDEF");
            f.Save();
        }
        var changes = Pending.CleanJunk(Read());
        Assert.Contains(changes, c => c.Key == "----:com.amazon:AmazonOrderId");
        Assert.DoesNotContain(changes, c => c.Key.Contains("iTunSMPB"));
        Write(changes);
        var after = Read().Tracks.Single(t => t.Format == AudioFormat.M4a);
        Assert.DoesNotContain(after.Raw, r => r.Key.Contains("Amazon"));
        Assert.Contains(after.Raw, r => r.Key.Contains("iTunSMPB"));
        Assert.Equal("Third", after.Get(FieldMap.Title));
    }

    [Fact]
    public void One_write_spans_albums_and_one_undo_takes_it_all_back()
    {
        var second = Path.Combine(_root, "Album 2");
        Directory.CreateDirectory(second);
        foreach (var f in Directory.GetFiles(_album)) File.Copy(f, Path.Combine(second, Path.GetFileName(f)));
        var shas = Directory.GetFiles(_album).Concat(Directory.GetFiles(second)).ToDictionary(f => f, Sha);

        var changes = Pending.CleanJunk(Read()).Concat(Pending.CleanJunk(AlbumReader.Read(second))).ToList();
        var result = Write(changes);
        Assert.Equal(2, result.Batch!.Folders.Count);
        Assert.False(AlbumReader.Read(second).Tracks.Single(t => t.Format == AudioFormat.Mp3).HasId3v1);

        Assert.Empty(TagWriter.Undo(TagWriter.Latest(_backups)!));
        foreach (var (file, sha) in shas) Assert.Equal(sha, Sha(file));
    }

    [Fact]
    public void Backups_are_listed_sized_pruned_and_emptied()
    {
        Write(Pending.SetField(Read(), FieldMap.Genre, "Blues"));
        Write(Pending.SetField(Read(), FieldMap.Genre, "Jazz"));
        var list = Backups.List(_backups);
        Assert.Equal(2, list.Count);
        Assert.All(list, b => Assert.True(b.Bytes > 0));
        Assert.Equal(list.Sum(b => b.Bytes), Backups.Size(_backups));

        // Nothing is older than a year yet; a year and a day on, both are.
        Assert.Equal(0, Backups.Prune(Backups.Keep, _backups).Writes);
        Assert.Equal(0, Backups.Prune(Backups.Keep, _backups, DateTime.Now.AddDays(364)).Writes);
        Assert.Equal(2, Backups.Prune(Backups.Keep, _backups, DateTime.Now.AddDays(366)).Writes);
        Assert.Empty(Backups.List(_backups));

        Write(Pending.SetField(Read(), FieldMap.Genre, "Rock"));
        Assert.Equal(1, Backups.Empty(_backups).Writes);
        Assert.Null(TagWriter.Latest(_backups));
    }

    [Fact]
    public void Flac_audio_starts_after_the_metadata_blocks()
    {
        // Measured from the fixture's own blocks: STREAMINFO, PICTURE,
        // VORBIS_COMMENT, PADDING, then frames at byte 9250.
        var (start, end) = TagWriter.AudioRange(FilePath("01"));
        Assert.Equal(9250, start);
        Assert.Equal(new FileInfo(FilePath("01")).Length, end);
    }

    [Fact]
    public void One_changed_audio_byte_is_caught()
    {
        foreach (var name in new[] { "01", "02", "03" })
        {
            var path = FilePath(name);
            var before = TagWriter.AudioHash(path);
            var (start, end) = TagWriter.AudioRange(path);
            var bytes = File.ReadAllBytes(path);
            bytes[(start + end) / 2] ^= 0x01;
            File.WriteAllBytes(path, bytes);
            Assert.NotEqual(before, TagWriter.AudioHash(path));
        }
    }

    /// A real album, copied to the temporary folder first: set
    /// MARGINAL_REAL_ALBUM to a folder to run it. Never writes to that folder.
    [Fact]
    public void Real_album_copy_round_trip()
    {
        var source = Environment.GetEnvironmentVariable("MARGINAL_REAL_ALBUM");
        if (string.IsNullOrEmpty(source) || !Directory.Exists(source)) return;

        var copy = Path.Combine(_root, "Real");
        Directory.CreateDirectory(copy);
        foreach (var f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(copy, Path.GetFileName(f)));
        var shas = Directory.GetFiles(copy).ToDictionary(f => f, Sha);

        var album = AlbumReader.Read(copy);
        var changes = Pending.Merge(Pending.SetField(album, FieldMap.Genre, "Marginal test"),
                                    Pending.SetField(album, FieldMap.Label, "Marginal test label"));
        var result = TagWriter.Write(changes, _backups);
        Assert.Empty(result.Problems);
        Assert.All(AlbumReader.Read(copy).Tracks, t => Assert.Equal("Marginal test", t.Get(FieldMap.Genre)));
        var bytes = shas.Keys.Sum(f => new FileInfo(f).Length);
        Assert.True(Backups.Size(_backups) * 100 < bytes, $"backups {Backups.Size(_backups)} bytes for a {bytes}-byte album");

        Assert.Empty(TagWriter.Undo(TagWriter.Latest(_backups)!));
        foreach (var (file, sha) in shas) Assert.Equal(sha, Sha(file));
        // The source is never written: it is only read to make the copy.
    }
}
