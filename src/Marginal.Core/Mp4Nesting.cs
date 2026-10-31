// Marginal — a look at an MP4 file's box nesting before TagLib# reads it.
// COPIED to AlbumWall (src/AlbumWall.Domain/Mp4Nesting.cs): a fix in one goes to the other.
//
// TagLib# 2.3.0 follows nested boxes by recursion with no limit, so a 24 KB
// .m4a nested 3,000 deep overflowed the stack and took the app down while the
// folder was being scanned; a stack overflow cannot be caught (security review
// 2026-10-31, A1). This walk is iterative and only reads box headers: any box
// whose contents read as a run of boxes (as they are, or after the few bytes
// of fields some boxes carry first) counts as holding them, so whatever
// TagLib# might follow is counted. A real file nests about eight deep
// (moov/trak/mdia/minf/stbl/stsd/mp4a/esds); a file deeper than MaxDepth is
// not given to TagLib# and shows as unreadable.

namespace Marginal.Core;

public static class Mp4Nesting
{
    public const int MaxDepth = 16;

    /// Box headers read at most before giving up on a file as not plausible.
    private const int MaxHeaders = 200_000;

    /// Where a box's children may start: at once, after a full box's version
    /// and flags (meta), after those and an entry count (stsd), after an audio
    /// sample entry's fields (mp4a, alac).
    private static readonly int[] ChildOffsets = [0, 4, 8, 28];

    /// Boxes never looked into: the audio itself and padding.
    private static readonly HashSet<string> Opaque = new(StringComparer.Ordinal) { "mdat", "free", "skip", "wide" };

    /// True when the file is an MP4 TagLib# may safely read: no deeper than
    /// MaxDepth. Not an MP4, or unreadable, is left to TagLib# (true).
    public static bool IsSafe(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            return IsSafe(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    public static bool IsSafe(Stream file)
    {
        var header = new byte[16];
        var headers = 0;
        var work = new Stack<(long Start, long End, int Depth)>();
        work.Push((0, file.Length, 0));
        while (work.Count > 0)
        {
            var (start, end, depth) = work.Pop();
            var boxes = Tile(file, start, end, header, ref headers);
            if (boxes is null) continue;               // not a run of boxes
            if (depth > MaxDepth) return false;
            if (headers > MaxHeaders) return false;
            foreach (var (type, body, bodyEnd) in boxes)
            {
                if (Opaque.Contains(type)) continue;
                foreach (var skip in ChildOffsets)
                    if (body + skip + 8 <= bodyEnd) work.Push((body + skip, bodyEnd, depth + 1));
            }
        }
        return true;
    }

    /// The boxes at [start, end), as many as read as boxes from the start (a
    /// container with bytes after its boxes is still followed by TagLib#, so
    /// trailing bytes do not stop the count), or null when not even the first
    /// reads as one.
    private static List<(string Type, long Body, long End)>? Tile(Stream file, long start, long end, byte[] header, ref int headers)
    {
        var boxes = new List<(string, long, long)>();
        var at = start;
        while (at + 8 <= end && ++headers <= MaxHeaders)
        {
            file.Position = at;
            if (file.ReadAtLeast(header.AsSpan(0, 8), 8, throwOnEndOfStream: false) < 8) break;
            long size = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);
            var head = 8;
            if (!Printable(header.AsSpan(4, 4))) break;
            if (size == 1)
            {
                if (end - at < 16 || file.ReadAtLeast(header.AsSpan(8, 8), 8, throwOnEndOfStream: false) < 8) break;
                var large = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                size = large > long.MaxValue ? -1 : (long)large;
                head = 16;
            }
            else if (size == 0) size = end - at;
            if (size < head || size > end - at) break;
            boxes.Add((System.Text.Encoding.Latin1.GetString(header, 4, 4), at + head, at + size));
            at += size;
        }
        return boxes.Count > 0 ? boxes : null;
    }

    /// A box type: four printable characters (© starts iTunes names).
    private static bool Printable(ReadOnlySpan<byte> type)
    {
        foreach (var b in type)
            if (b is (< 0x20 or > 0x7E) and not 0xA9) return false;
        return true;
    }
}
