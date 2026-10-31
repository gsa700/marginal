// Marginal — making an image ready to be a cover.jpg or back.jpg (spec §3.4).
//
// Kept as it came when it can be: "keep as printed" at its own size and
// quality is the archive's bytes untouched, save for stripping camera and
// editor metadata, which is done by dropping JPEG segments, not by decoding
// and encoding again. Anything else (a crop, a pad, a smaller size, a quality,
// a PNG) is decoded once, converted to sRGB, and encoded as JPEG.
//
// Never decoded past MaxPixels: a 1.1 MB PNG of 20000 x 20000 decodes to
// 1.5 GB, and anyone can upload to the archive (Deadwax security review
// 2026-10-24, L12). The size is read from the header first.

using SkiaSharp;

namespace Marginal.Core;

public enum ArtShape { AsPrinted, CropSquare, PadSquare }

public static class ArtImage
{
    public const long MaxPixels = 50_000_000;
    public const int DefaultQuality = 92;

    /// JPEG or PNG by its first bytes.
    public static bool LooksLikeAnImage(byte[] b) =>
        b.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])
        || b.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

    public static bool IsJpeg(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    /// The image ready to save, or null when it cannot be read (or is too big
    /// to decode). `maxSide`: shrink so the longer side is at most this (never
    /// enlarge); null keeps the size. `quality`: JPEG quality; null keeps the
    /// original when nothing else asks for a new encode (92 when one is needed).
    public static byte[]? Process(byte[] image, ArtShape shape, int? maxSide, int? quality, bool strip)
    {
        if (ImageSize.Read(image) is not { } size) return null;
        if ((long)size.Width * size.Height > MaxPixels) return null;
        // Padding to a square makes a bigger picture than the one read: a
        // 20000 x 1000 strip became 400 MP (security review 2026-10-31, A5).
        var side = (long)Math.Max(size.Width, size.Height);
        if (shape == ArtShape.PadSquare && side * side > MaxPixels) return null;
        try { return Make(image, size, shape, maxSide, quality, strip); }
        catch (Exception) { return null; }   // Skia could not allocate: too big after all
    }

    private static byte[]? Make(byte[] image, (int Width, int Height) size, ArtShape shape, int? maxSide, int? quality, bool strip)
    {
        var shrink = maxSide is { } m && Math.Max(size.Width, size.Height) > m;
        var square = shape != ArtShape.AsPrinted && size.Width != size.Height;
        if (!shrink && !square && quality is null && IsJpeg(image))
            return strip ? StripJpeg(image) : image;

        using var data = SKData.CreateCopy(image);
        using var codec = SKCodec.Create(data);
        if (codec is null) return null;
        // Decoded into sRGB: a cover in Adobe RGB or a camera profile shows
        // its colours as meant everywhere after.
        var info = codec.Info.WithColorSpace(SKColorSpace.CreateSrgb()).WithColorType(SKColorType.Rgba8888).WithAlphaType(SKAlphaType.Premul);
        using var decoded = new SKBitmap(info);
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;

        var bitmap = decoded;
        SKBitmap? owned = null;
        try
        {
            if (square)
            {
                owned = shape == ArtShape.CropSquare ? Crop(bitmap) : Pad(bitmap);
                bitmap = owned;
            }
            if (maxSide is { } max && Math.Max(bitmap.Width, bitmap.Height) > max)
            {
                var scale = (double)max / Math.Max(bitmap.Width, bitmap.Height);
                var target = new SKImageInfo(Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale)),
                    SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb());
                var scaled = bitmap.Resize(target, new SKSamplingOptions(SKCubicResampler.Mitchell));
                if (scaled is null) return null;
                owned?.Dispose();
                owned = scaled;
                bitmap = scaled;
            }
            using var picture = SKImage.FromBitmap(bitmap);
            using var jpeg = picture.Encode(SKEncodedImageFormat.Jpeg, quality ?? DefaultQuality);
            return jpeg?.ToArray();
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// The centre square: a cover scanned with a margin, or a back cut to fit
    /// (which loses the spines, said where the choice is made).
    private static SKBitmap Crop(SKBitmap from)
    {
        var side = Math.Min(from.Width, from.Height);
        var square = new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        using var canvas = new SKCanvas(square);
        var x = (from.Width - side) / 2f;
        var y = (from.Height - side) / 2f;
        canvas.DrawBitmap(from, new SKRect(x, y, x + side, y + side), new SKRect(0, 0, side, side));
        return square;
    }

    /// The whole image on a white square: nothing is lost.
    private static SKBitmap Pad(SKBitmap from)
    {
        var side = Math.Max(from.Width, from.Height);
        var square = new SKBitmap(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb()));
        using var canvas = new SKCanvas(square);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(from, (side - from.Width) / 2f, (side - from.Height) / 2f);
        return square;
    }

    /// A JPEG without its camera and editor metadata: EXIF and XMP (APP1),
    /// Photoshop's (APP13), the APP12-15 blocks and comments go; the JFIF
    /// header and the colour profile (APP2) stay. The picture itself is not
    /// touched: segments are dropped, nothing is decoded.
    public static byte[] StripJpeg(byte[] jpeg)
    {
        if (!IsJpeg(jpeg)) return jpeg;
        using var output = new MemoryStream(jpeg.Length);
        output.Write(jpeg, 0, 2);                                  // SOI
        var i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF)
        {
            var marker = jpeg[i + 1];
            if (marker == 0xFF) { i++; continue; }                 // padding
            if (marker == 0xDA) break;                             // start of scan: the rest is the picture
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (length < 2 || i + 2 + length > jpeg.Length) return jpeg;   // not what it seems: leave it alone
            var drop = marker is 0xE1 or 0xED or (>= 0xEC and <= 0xEF) or 0xFE;
            if (!drop) output.Write(jpeg, i, 2 + length);
            i += 2 + length;
        }
        output.Write(jpeg, i, jpeg.Length - i);
        return output.ToArray();
    }
}
