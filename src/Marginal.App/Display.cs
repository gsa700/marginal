// Marginal — pictures decoded for the screen, held to the same limit as the
// pictures Marginal saves (ArtImage.MaxPixels).
//
// Only ArtImage.Process checked the size before 0.1.0's review: every cover
// shown on screen was decoded whole, so a 1.2 MB PNG of 20000 x 20000 (in a
// file, as cover.jpg, or from the archive) took 1.7 to 2.2 GB on the UI thread
// (security review 2026-10-31, A5). Here the size is read from the header
// first, anything over the limit is not decoded, and a picture is decoded no
// wider than it will be shown.

using Avalonia.Media.Imaging;
using Marginal.Core;

namespace Marginal.App;

internal static class Display
{
    /// The picture no wider than `width`, or null when it is not one, or
    /// larger than ArtImage.MaxPixels by its header.
    public static Bitmap? Decode(byte[]? bytes, int width)
    {
        if (bytes is null || !ArtImage.LooksLikeAnImage(bytes)) return null;
        if (ImageSize.Read(bytes) is not { } size || (long)size.Width * size.Height > ArtImage.MaxPixels) return null;
        try
        {
            using var stream = new MemoryStream(bytes);
            return size.Width > width ? Bitmap.DecodeToWidth(stream, width) : new Bitmap(stream);
        }
        catch (Exception) { return null; }
    }

    /// A picture file in an album folder: a plain file (SafeFiles), no larger
    /// than a cover may be.
    public static Bitmap? Decode(string path, int width) =>
        Decode(SafeFiles.Read(path, WebLimits.MaxImageBytes), width);
}
