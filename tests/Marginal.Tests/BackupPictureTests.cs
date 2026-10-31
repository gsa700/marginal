// 0.1.2: pictures a write leaves as they were are not copied into its backup
// (a genre fix across Dave's lossy library made a 322 MB backup of covers that
// never changed). Undo takes them back from the file, byte for byte, or refuses.

using System.Security.Cryptography;
using Marginal.Core;

namespace Marginal.Tests;

public sealed class BackupPictureTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("marginal-pics-").FullName;
    private readonly string _album;
    private readonly string _backups;
    private readonly byte[] _cover;

    public BackupPictureTests()
    {
        _album = Path.Combine(_root, "Album");
        _backups = Path.Combine(_root, "backups");
        Directory.CreateDirectory(_album);
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fixture Band - Test Album");
        foreach (var f in Directory.GetFiles(fixture)) File.Copy(f, Path.Combine(_album, Path.GetFileName(f)));
        // A cover big enough to matter, embedded in every file, as a store's are.
        _cover = ArtImage.Process(File.ReadAllBytes(Path.Combine(_album, "cover.jpg")), ArtShape.AsPrinted, null, 100, false)!;
        Assert.True(_cover.Length > 1024);
        Write(Pending.EmbedFront(Read(), _cover));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private Album Read() => AlbumReader.Read(_album);
    private string[] Tracks() => Directory.GetFiles(_album).Where(TrackReader.IsMusic).Order().ToArray();
    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private WriteBatch Write(IReadOnlyList<Change> changes)
    {
        var result = TagWriter.Write(changes, _backups);
        Assert.Empty(result.Problems);
        return result.Batch!;
    }

    [Fact]
    public void A_tag_change_backs_up_no_picture_and_undoes_byte_for_byte()
    {
        var before = Tracks().ToDictionary(t => t, Sha);
        var batch = Write(Pending.SetField(Read(), FieldMap.Label, "Vertigo"));

        foreach (var entry in batch.Files)
        {
            // The cover was left out: listed once, at its own size, not kept.
            Assert.NotNull(entry.Pictures);
            var cut = Assert.Single(File.ReadAllLines(entry.Pictures!));
            Assert.Equal(_cover.Length.ToString(), cut.Split('\t')[2]);
            var kept = WriterTests.Stored(entry.Head).Concat(WriterTests.Stored(entry.Tail)).ToArray();
            Assert.True(kept.AsSpan().IndexOf(_cover) < 0, Path.GetFileName(entry.Original));
        }

        Assert.Empty(TagWriter.Undo(TagWriter.Latest(_backups)!));
        foreach (var (track, sha) in before) Assert.Equal(sha, Sha(track));
    }

    [Fact]
    public void A_new_cover_keeps_the_old_one_in_the_backup()
    {
        var before = Tracks().ToDictionary(t => t, Sha);
        var other = ArtImage.Process(_cover, ArtShape.PadSquare, 300, 80, false)!;
        var batch = Write(Pending.EmbedFront(Read(), other));
        // The old cover is not in the new files, so the backup keeps it.
        Assert.All(batch.Files, e => Assert.True(WriterTests.Stored(e.Head).Concat(WriterTests.Stored(e.Tail)).ToArray().AsSpan().IndexOf(_cover) >= 0));
        Assert.Empty(TagWriter.Undo(TagWriter.Latest(_backups)!));
        foreach (var (track, sha) in before) Assert.Equal(sha, Sha(track));
    }

    [Fact]
    public void A_picture_changed_since_means_that_file_cannot_be_undone()
    {
        var batch = Write(Pending.SetField(Read(), FieldMap.Label, "Vertigo"));
        // Another program swaps the cover in one file afterwards.
        var victim = batch.Files[0].Original;
        using (var f = TagLib.File.Create(victim))
        {
            f.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(ArtImage.Process(_cover, ArtShape.AsPrinted, 200, 70, false)!)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" }];
            f.Save();
        }
        var problems = TagWriter.Undo(batch);
        var only = Assert.Single(problems);
        Assert.Contains("pictures have changed", only.Problem);
        Assert.Single(TagWriter.Latest(_backups)!.Files);   // kept for another try
    }

    [Fact]
    public void An_old_manifest_without_pictures_still_undoes()
    {
        var batch = Write(Pending.SetField(Read(), FieldMap.Label, "Vertigo"));
        var entry = batch.Files[0];
        // As 0.1.1 wrote it: the whole head and tail, five fields.
        var manifest = Path.Combine(batch.BackupFolder, "manifest.tsv");
        var lines = File.ReadAllLines(manifest).Select(l => string.Join('\t', l.Split('\t').Take(5))).ToArray();
        File.WriteAllLines(manifest, lines);
        var latest = TagWriter.Latest(_backups)!;
        Assert.All(latest.Files, e => Assert.Null(e.Pictures));
        // Without the pictures list the stored bytes are not the original's, so it must refuse, not damage.
        Assert.All(TagWriter.Undo(latest), p => Assert.Contains("does not match", p.Problem));
        Assert.Equal("Vertigo", TrackReader.Read(entry.Original).Get(FieldMap.Label));
    }
}

public sealed class BackupCompactTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("marginal-compact-").FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    [Fact]
    public void An_old_style_backup_is_made_compact_and_still_undoes_byte_for_byte()
    {
        var album = Path.Combine(_root, "Album");
        var backups = Path.Combine(_root, "backups");
        Directory.CreateDirectory(album);
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fixture Band - Test Album");
        foreach (var f in Directory.GetFiles(fixture)) File.Copy(f, Path.Combine(album, Path.GetFileName(f)));
        var cover = ArtImage.Process(File.ReadAllBytes(Path.Combine(album, "cover.jpg")), ArtShape.AsPrinted, null, 100, false)!;
        Assert.Empty(TagWriter.Write(Pending.EmbedFront(AlbumReader.Read(album), cover), backups).Problems);
        Assert.Empty(TagWriter.Undo(TagWriter.Latest(backups)!));   // a clean start: no batch to undo
        Assert.Empty(TagWriter.Write(Pending.EmbedFront(AlbumReader.Read(album), cover), backups).Problems);
        var before = Directory.GetFiles(album).Where(TrackReader.IsMusic).ToDictionary(t => t, t => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(t))));

        var batch = TagWriter.Write(Pending.SetField(AlbumReader.Read(album), FieldMap.Label, "Vertigo"), backups).Batch!;
        // Turn it into what 0.1.1 wrote: plain .head and .tail with the pictures in, five fields.
        var lines = new List<string>();
        foreach (var e in batch.Files)
        {
            var plainHead = e.Head[..^3];
            var plainTail = e.Tail[..^3];
            File.WriteAllBytes(plainHead, Rebuilt(e).Head);
            File.WriteAllBytes(plainTail, Rebuilt(e).Tail);
            File.Delete(e.Head); File.Delete(e.Tail); File.Delete(e.Pictures!);
            lines.Add(string.Join('\t', e.Original, Path.GetFileName(plainHead), Path.GetFileName(plainTail), e.Sha256, e.AudioSha256));
        }
        File.WriteAllLines(Path.Combine(batch.BackupFolder, "manifest.tsv"), lines);
        var oldSize = Directory.GetFiles(batch.BackupFolder).Sum(f => new FileInfo(f).Length);

        Assert.True(TagWriter.Compact(batch.BackupFolder) > 0);
        Assert.True(Directory.GetFiles(batch.BackupFolder).Sum(f => new FileInfo(f).Length) < oldSize);
        Assert.DoesNotContain(Directory.GetFiles(batch.BackupFolder), f => f.EndsWith(".head") || f.EndsWith(".tail"));

        Assert.Empty(TagWriter.Undo(TagWriter.Latest(backups)!));
        foreach (var (track, sha) in before) Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(track))));
    }

    /// The plain head and tail a compact entry stands for, pictures back in.
    private static (byte[] Head, byte[] Tail) Rebuilt(BackupEntry e)
    {
        var pictures = new List<byte[]>();
        using (var f = TagLib.File.Create(e.Original)) pictures.AddRange(f.Tag.Pictures.Select(p => p.Data.Data));
        var head = WriterTests.Stored(e.Head).ToList();
        var tail = WriterTests.Stored(e.Tail).ToList();
        foreach (var cut in File.ReadAllLines(e.Pictures!).Select(l => l.Split('\t')).OrderBy(c => long.Parse(c[1])))
        {
            var bytes = pictures.First(p => Convert.ToHexString(SHA256.HashData(p)) == cut[3]);
            (cut[0] == "head" ? head : tail).InsertRange(int.Parse(cut[1]), bytes);
        }
        return (head.ToArray(), tail.ToArray());
    }
}
