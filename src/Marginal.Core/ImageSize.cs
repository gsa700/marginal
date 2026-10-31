// COPIED from AlbumWall (src/AlbumWall.Domain/ImageSize.cs, 2026-10-31), the same
// code in both: a fix in one goes to the other. Namespace changed only.
//
// AlbumWall — image dimensions from the header, without decoding.
//
// The wall needs to know how big a cover REALLY is, for two reasons that both
// came out of the MP3/M4A library on Hambench, where 88 of 1,034 covers are
// under 300 px and the smallest is 70x70:
//
//   * the art cache decodes to a fixed width, which silently enlarges a small
//     cover before the view ever sees it — so by then nothing can tell a
//     thumbnail from a proper scan;
//   * finding the albums whose art wants replacing should not mean decoding a
//     thousand images. The header is a few dozen bytes the scanner has in hand
//     anyway.
//
// Returns null for anything it does not recognize, and callers treat null as
// "unknown, behave as before" — never as "small".

namespace Marginal.Core;

public static class ImageSize
{
    /// The cover among a file's picture frames, repaired if need be, or null.
    ///
    /// ONE rule, used by the scanner to decide an album has art and by the art
    /// cache to show it, so the two cannot disagree. The front cover if there
    /// is one that reads as an image; otherwise the first frame that does;
    /// otherwise the first frame with anything in it, on the chance it is a
    /// format this file does not know and the decoder does.
    ///
    /// "The first frame" was not good enough: A Momentary Lapse Of Reason opens
    /// with a 1,272-byte RealJukebox metadata blob typed NotAPicture, with the
    /// real 500 KB cover behind it, and showed as an album with no art.
    public static byte[]? Cover(TagLib.IPicture[]? pictures)
    {
        if (pictures is not { Length: > 0 }) return null;

        byte[]? readable = null, anything = null;
        foreach (var p in pictures)
        {
            if (p.Data is not { Count: > 0 }) continue;

            var data = Repair(p.Data.Data);
            var isImage = Read(data) is not null;

            if (isImage && p.Type == TagLib.PictureType.FrontCover) return data;
            if (isImage) readable ??= data;
            if (p.Type != TagLib.PictureType.NotAPicture) anything ??= data;
        }
        return readable ?? anything;
    }

    /// Puts back the front of an image that a malformed tag has eaten.
    ///
    /// Found in the Hambench library, in 13 albums: an ID3 picture frame with no
    /// MIME type and no description field at all. The reader still expects a
    /// description, reads up to the first zero byte as one, and hands over the
    /// picture from the byte after — so the image arrives missing everything up
    /// to and including its own first 00. Nothing will decode that, yet every
    /// byte that matters is still there, and what was lost is fixed text:
    ///
    ///   PNG   89 'PNG' 0D 0A 1A 0A 00 | 00 00 0D 'IHDR' ...   loses 9 bytes
    ///   JPEG  FF D8 FF E0 00          | 10 'JFIF' 00 ...      loses 5 bytes
    ///   JPEG  FF D8 FF DB 00          | 43 00 <64 values> FF  loses 5 bytes
    ///
    /// The third is a JPEG with no JFIF header, which opens straight into a
    /// quantization table: length 0x43, table 0, 64 values, then the next
    /// marker — checked, since "43 00" alone proves nothing.
    ///
    /// Only these shapes are repaired, because only these can be restored with
    /// certainty. A JPEG that opens with EXIF rather than JFIF loses its
    /// segment length as well, and guessing that is not a repair. Anything else
    /// is returned untouched.
    public static byte[] Repair(byte[] d)
    {
        if (d.Length < 16) return d;

        if (d[0] == 0x00 && d[1] == 0x00 && d[2] == 0x0D
            && d[3] == 0x49 && d[4] == 0x48 && d[5] == 0x44 && d[6] == 0x52)
            return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, .. d];

        if (d[0] == 0x10 && d[1] == 0x4A && d[2] == 0x46 && d[3] == 0x49 && d[4] == 0x46 && d[5] == 0x00)
            return [0xFF, 0xD8, 0xFF, 0xE0, 0x00, .. d];

        if (d.Length > 68 && d[0] == 0x43 && d[1] == 0x00 && d[66] == 0xFF)
            return [0xFF, 0xD8, 0xFF, 0xDB, 0x00, .. d];

        return d;
    }

    public static (int Width, int Height)? Read(ReadOnlySpan<byte> d)
    {
        // PNG: signature, then IHDR with big-endian width and height.
        if (d.Length >= 24 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
            return Sane(Be32(d, 16), Be32(d, 20));

        // GIF: "GIF8", little-endian logical screen size.
        if (d.Length >= 10 && d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46)
            return Sane(d[6] | (d[7] << 8), d[8] | (d[9] << 8));

        // BMP: "BM", little-endian width and (possibly negative) height.
        if (d.Length >= 26 && d[0] == 0x42 && d[1] == 0x4D)
            return Sane(Le32(d, 18), Math.Abs(Le32(d, 22)));

        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
            return Jpeg(d);

        return null;
    }

    /// Walks the segments to the first start-of-frame. It can be a long way in:
    /// a cover exported from a photo tool carries an EXIF block with its own
    /// embedded thumbnail ahead of the frame header, so the first few hundred
    /// bytes are not enough and callers should hand over what they have.
    private static (int, int)? Jpeg(ReadOnlySpan<byte> d)
    {
        var i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }

            var marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }                        // padding

            // SOF0..SOF15, less the three in that range that are not frames.
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                return Sane((d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6]);

            // Markers with no length: restarts, SOI, TEM.
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }

            i += 2 + ((d[i + 2] << 8) | d[i + 3]);
        }
        return null;
    }

    private static (int, int)? Sane(int w, int h) =>
        w is > 0 and < 65536 && h is > 0 and < 65536 ? (w, h) : null;

    private static int Be32(ReadOnlySpan<byte> d, int i) =>
        (d[i] << 24) | (d[i + 1] << 16) | (d[i + 2] << 8) | d[i + 3];

    private static int Le32(ReadOnlySpan<byte> d, int i) =>
        d[i] | (d[i + 1] << 8) | (d[i + 2] << 16) | (d[i + 3] << 24);
}
