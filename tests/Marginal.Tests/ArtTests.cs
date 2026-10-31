using System.Security.Cryptography;
using System.Text.Json;
using Marginal.Core;
using SkiaSharp;

namespace Marginal.Tests;

/// The art picker's pieces, without the network: the Cover Art Archive index
/// and edition browse for Facelift saved on 2026-10-31 (Fixtures/mb), images
/// drawn here, writes to a copy of the fixture album.
public sealed class ArtTests : IDisposable
{
    private static readonly string Mb = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mb");
    private static JsonElement Json(string name) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Mb, name))).RootElement;
    private const string Facelift = "c63ad92b-19d7-41d9-83e6-607202ceba07";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "marginal-art-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _album;
    private readonly string _backups;

    public ArtTests()
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

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// A picture of the given size, as JPEG or PNG.
    private static byte[] Draw(int w, int h, SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg)
    {
        using var bitmap = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0x40, 0x60, 0x80));
            using var paint = new SKPaint { Color = SKColors.Orange };
            canvas.DrawCircle(w / 2f, h / 2f, Math.Min(w, h) / 3f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(format, 90).ToArray();
    }

    // ---- the archive's index and editions --------------------------------------

    [Fact]
    public void Reads_the_index_by_slot_and_skips_the_disc_photo()
    {
        var art = CoverArtArchive.Parse(Json("caa-facelift.json"), Facelift, "1990 US CD", ours: true);
        Assert.Single(art, a => a.Slot == ArtSlot.Front);
        Assert.Single(art, a => a.Slot == ArtSlot.Back);       // typed Back and Spine
        Assert.Equal([1, 2], art.Where(a => a.Slot == ArtSlot.Booklet).Select(a => a.Page));
        Assert.Equal(4, art.Count);                           // the Medium photo is not offered
        Assert.All(art, a => Assert.True(WebLimits.IsArchiveUrl(a.Image) && WebLimits.IsArchiveUrl(a.Thumbnail)));
        Assert.All(art, a => Assert.StartsWith("https://", a.Image));
    }

    [Fact]
    public void Editions_with_art_other_than_ours()
    {
        var browse = Json("browse-facelift.json");
        var editions = CoverArtArchive.Editions(browse, Facelift).ToList();
        Assert.DoesNotContain(editions, e => e.Id == Facelift);
        Assert.Equal(12, editions.Count);                     // 13 of 18 have art; ours is one of them
        Assert.All(editions, e => Assert.False(string.IsNullOrWhiteSpace(e.Label)));
    }

    // ---- making the image ----------------------------------------------------------

    [Fact]
    public void Kept_as_printed_at_its_own_size_is_the_same_bytes()
    {
        var jpeg = Draw(1400, 1100);
        Assert.Same(jpeg, ArtImage.Process(jpeg, ArtShape.AsPrinted, null, null, strip: false));
    }

    [Fact]
    public void Stripping_metadata_drops_exif_and_keeps_the_picture()
    {
        var jpeg = Draw(600, 600);
        // An EXIF block (APP1) after the JFIF header, as a camera or editor writes one.
        var exif = new byte[] { 0xFF, 0xE1, 0x00, 0x10, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        var withExif = jpeg[..2].Concat(exif).Concat(jpeg[2..]).ToArray();
        var stripped = ArtImage.Process(withExif, ArtShape.AsPrinted, null, null, strip: true)!;
        Assert.Equal(jpeg.Length, stripped.Length);
        Assert.Equal(jpeg, stripped);                         // only the block went
    }

    [Theory]
    [InlineData(ArtShape.CropSquare, 1100, 1100)]
    [InlineData(ArtShape.PadSquare, 1400, 1400)]
    [InlineData(ArtShape.AsPrinted, 1400, 1100)]
    public void Shapes(ArtShape shape, int w, int h)
    {
        var made = ArtImage.Process(Draw(1400, 1100), shape, null, 85, strip: true)!;
        Assert.Equal((w, h), ImageSize.Read(made));
    }

    [Fact]
    public void Shrinks_to_the_longest_side_and_never_enlarges()
    {
        Assert.Equal((1000, 786), ImageSize.Read(ArtImage.Process(Draw(1400, 1100), ArtShape.AsPrinted, 1000, null, true)!));
        var small = Draw(300, 300);
        Assert.Equal((300, 300), ImageSize.Read(ArtImage.Process(small, ArtShape.AsPrinted, 1000, null, true)!));
    }

    [Fact]
    public void A_png_becomes_a_jpeg()
    {
        var made = ArtImage.Process(Draw(500, 500, SKEncodedImageFormat.Png), ArtShape.AsPrinted, null, null, true)!;
        Assert.True(ArtImage.IsJpeg(made));
        Assert.Equal((500, 500), ImageSize.Read(made));
    }

    [Fact]
    public void An_image_too_big_to_decode_is_refused_from_its_header()
    {
        // A PNG header claiming 20000 x 20000: refused before any decoding.
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
                               0x00, 0x00, 0x4E, 0x20, 0x00, 0x00, 0x4E, 0x20, 8, 2, 0, 0, 0 };
        Assert.Null(ArtImage.Process(png, ArtShape.CropSquare, 1000, null, true));
    }

    // ---- writing it --------------------------------------------------------------------

    [Fact]
    public void A_new_back_jpg_is_written_and_undo_removes_it()
    {
        var album = AlbumReader.Read(_album);
        var image = Draw(1400, 1100);
        var change = Pending.SaveImage(album, "back.jpg", image);
        Assert.Equal(ChangeKind.Add, change.Kind);
        var result = TagWriter.Write([change], _backups);
        Assert.Empty(result.Problems);
        Assert.Equal(image, File.ReadAllBytes(Path.Combine(_album, "back.jpg")));
        Assert.NotNull(AlbumReader.Read(_album).Back);

        Assert.Empty(TagWriter.Undo(TagWriter.Latest(_backups)!));
        Assert.False(File.Exists(Path.Combine(_album, "back.jpg")));
    }

    [Fact]
    public void A_replaced_cover_jpg_comes_back_on_undo()
    {
        var cover = Path.Combine(_album, "cover.jpg");
        var old = Sha(cover);
        var change = Pending.SaveImage(AlbumReader.Read(_album), "cover.jpg", Draw(1200, 1200));
        Assert.Equal(ChangeKind.Change, change.Kind);
        Assert.Empty(TagWriter.Write([change], _backups).Problems);
        Assert.NotEqual(old, Sha(cover));
        Assert.Empty(TagWriter.Undo(TagWriter.Latest(_backups)!));
        Assert.Equal(old, Sha(cover));
    }

    [Fact]
    public void Undo_leaves_a_new_image_that_was_changed_since()
    {
        var album = AlbumReader.Read(_album);
        Assert.Empty(TagWriter.Write([Pending.SaveImage(album, "back.jpg", Draw(800, 600))], _backups).Problems);
        var back = Path.Combine(_album, "back.jpg");
        File.WriteAllBytes(back, Draw(900, 700));             // replaced by hand afterwards
        var problems = TagWriter.Undo(TagWriter.Latest(_backups)!);
        Assert.Contains(problems, p => p.File == "back.jpg");
        Assert.True(File.Exists(back));
    }

    [Fact]
    public void Embeds_a_front_in_all_three_formats_and_undo_is_exact()
    {
        var shas = Directory.GetFiles(_album).ToDictionary(f => f, Sha);
        var image = ArtImage.Process(Draw(1200, 1200), ArtShape.AsPrinted, null, 90, true)!;
        var changes = Pending.EmbedFront(AlbumReader.Read(_album), image);
        Assert.Equal(3, changes.Count);
        Assert.All(changes, c => Assert.Equal(ChangeKind.Change, c.Kind));   // each had a 300 px cover
        var result = TagWriter.Write(changes, _backups);
        Assert.Empty(result.Problems);

        var after = AlbumReader.Read(_album);
        Assert.All(after.Tracks, t => Assert.Equal((1200, 1200, (long)image.Length),
            (t.EmbeddedCover()!.Width!.Value, t.EmbeddedCover()!.Height!.Value, t.EmbeddedCover()!.Bytes)));

        Assert.Empty(TagWriter.Undo(TagWriter.Latest(_backups)!));
        foreach (var (file, sha) in shas) Assert.Equal(sha, Sha(file));
    }
}
