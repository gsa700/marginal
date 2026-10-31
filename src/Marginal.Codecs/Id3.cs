// COPIED from AlbumWall (src/AlbumWall.Codecs/Id3.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — ID3v2, read for playing: how long the tags at the front of an
// MP3 are, and the TXXX frames in them, which is where ReplayGain is kept.
// Everything else in a tag (titles, pictures) is the library's, not the
// player's, and is passed over.
//
// As FFmpeg reads it (libavformat/id3v2.c): versions 2.2 to 2.4, one tag
// after another, unsynchronisation undone, compressed and encrypted frames
// left alone, a TXXX's description its key, matched without regard to case.

using System.Text;

namespace Marginal.Codecs;

internal static class Id3
{
    /// How many bytes at `at` are ID3v2 tags (several may follow one another),
    /// with the TXXX frames found in them. `available` is how much is there to
    /// look at: a tag that runs past it is measured but not read.
    public static int Measure(byte[] data, int at, int available, Dictionary<string, string> txxx)
    {
        var start = at;
        while (at + 10 <= available && data[at] == 'I' && data[at + 1] == 'D' && data[at + 2] == '3'
               && data[at + 3] is >= 2 and <= 4)
        {
            var size = Synchsafe(data, at + 6);
            if (size < 0) break;
            var total = 10 + size + ((data[at + 5] & 0x10) != 0 ? 10 : 0);
            if (at + total <= available) Read(data.AsSpan(at, 10 + size), txxx);
            at += total;
        }
        return at - start;
    }

    /// The size of the tag starting at `at`, from its first ten bytes alone,
    /// or 0 if no tag starts there.
    public static int SizeAt(byte[] data, int at, int available)
    {
        if (at + 10 > available || data[at] != 'I' || data[at + 1] != 'D' || data[at + 2] != '3') return 0;
        var size = Synchsafe(data, at + 6);
        return size < 0 ? 0 : 10 + size + ((data[at + 5] & 0x10) != 0 ? 10 : 0);
    }

    private static int Synchsafe(byte[] d, int at)
    {
        if ((d[at] | d[at + 1] | d[at + 2] | d[at + 3]) >= 0x80) return -1;
        return d[at] << 21 | d[at + 1] << 14 | d[at + 2] << 7 | d[at + 3];
    }

    private static void Read(ReadOnlySpan<byte> tag, Dictionary<string, string> txxx)
    {
        int version = tag[3], flags = tag[5];
        var body = tag[10..];
        byte[]? undone = null;
        if ((flags & 0x80) != 0 && version < 4)
        {
            undone = Unsync(body);
            body = undone;
        }
        var at = 0;
        if ((flags & 0x40) != 0 && version >= 3 && body.Length >= 4)
        {
            // The extended header: its size, which in 2.3 leaves itself out.
            var ext = version == 3
                ? (body[0] << 24 | body[1] << 16 | body[2] << 8 | body[3]) + 4
                : (body[0] << 21 | body[1] << 14 | body[2] << 7 | body[3]);
            at = ext;
        }

        var idLength = version == 2 ? 3 : 4;
        var headerLength = version == 2 ? 6 : 10;
        while (at + headerLength <= body.Length && body[at] != 0)
        {
            var id = Encoding.ASCII.GetString(body.Slice(at, idLength));
            int size = version switch
            {
                2 => body[at + 3] << 16 | body[at + 4] << 8 | body[at + 5],
                3 => body[at + 4] << 24 | body[at + 5] << 16 | body[at + 6] << 8 | body[at + 7],
                _ => body[at + 4] << 21 | body[at + 5] << 14 | body[at + 6] << 7 | body[at + 7],
            };
            var formatFlags = version == 2 ? 0 : body[at + 9];
            at += headerLength;
            if (size <= 0 || at + size > body.Length) break;
            var frame = body.Slice(at, size);
            at += size;

            if (id is not ("TXXX" or "TXX")) continue;
            if (version == 3 && (formatFlags & 0xC0) != 0) continue;            // compressed or encrypted
            if (version == 4)
            {
                if ((formatFlags & 0x0C) != 0) continue;                          // compressed or encrypted
                if ((formatFlags & 0x01) != 0) { if (frame.Length < 4) continue; frame = frame[4..]; }  // data length indicator
                if ((formatFlags & 0x02) != 0 || (flags & 0x80) != 0) frame = Unsync(frame);
            }
            Txxx(frame, txxx);
        }
    }

    /// A TXXX: an encoding, a description, a value.
    private static void Txxx(ReadOnlySpan<byte> frame, Dictionary<string, string> txxx)
    {
        if (frame.Length < 2) return;
        int encoding = frame[0];
        var rest = frame[1..];
        var wide = encoding is 1 or 2;
        var end = Terminator(rest, wide);
        var key = Text(rest[..end], encoding);
        var after = Math.Min(rest.Length, end + (wide ? 2 : 1));
        var valueBytes = rest[after..];
        var value = Text(valueBytes[..Terminator(valueBytes, wide)], encoding);   // the first of several, as FFmpeg keeps
        if (key.Length > 0) txxx.TryAdd(key.ToUpperInvariant(), value);
    }

    private static int Terminator(ReadOnlySpan<byte> s, bool wide)
    {
        if (!wide) { var i = s.IndexOf((byte)0); return i < 0 ? s.Length : i; }
        for (var i = 0; i + 1 < s.Length; i += 2)
            if (s[i] == 0 && s[i + 1] == 0) return i;
        return s.Length & ~1;
    }

    private static string Text(ReadOnlySpan<byte> s, int encoding) => encoding switch
    {
        0 => Encoding.Latin1.GetString(s),
        1 when s.Length >= 2 && s[0] == 0xFE && s[1] == 0xFF => Encoding.BigEndianUnicode.GetString(s[2..]),
        1 when s.Length >= 2 && s[0] == 0xFF && s[1] == 0xFE => Encoding.Unicode.GetString(s[2..]),
        1 => Encoding.Unicode.GetString(s),
        2 => Encoding.BigEndianUnicode.GetString(s),
        _ => Encoding.UTF8.GetString(s),
    };

    /// Unsynchronisation undone: every FF 00 back to FF.
    private static byte[] Unsync(ReadOnlySpan<byte> s)
    {
        var o = new byte[s.Length];
        var n = 0;
        for (var i = 0; i < s.Length; i++)
        {
            o[n++] = s[i];
            if (s[i] == 0xFF && i + 1 < s.Length && s[i + 1] == 0) i++;
        }
        return o[..n];
    }
}
