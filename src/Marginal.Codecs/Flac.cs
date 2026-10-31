// COPIED from AlbumWall (src/AlbumWall.Codecs/Flac.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — FLAC, read for playing.
//
// COPIED FROM DEADWAX (src/Deadwax.Output/FlacDecoder.cs and FlacBits.cs at
// 1ba7623, 2026-10-20), his call on 2026-10-21: a copy, not a library shared
// between the two repositories. Both are GPLv3. The decoder there was written
// from RFC 9639 and decoded his whole FLAC library the same as FFmpeg did
// (2026-10-20). DecodeFrame, Subframe, Residual, BitReader and FlacCrc are as
// they were, so a fix there can be carried over line for line; what is new
// here is what playing needs and checking a rip does not: the tags (for
// ReplayGain) and finding the frame that holds a given sample (for seeking).

namespace Marginal.Codecs;

/// STREAMINFO (RFC 9639 §8.2): what every FLAC states about itself first.
public sealed record FlacStreamInfo(
    int MinBlockSize, int MaxBlockSize, int MinFrameSize, int MaxFrameSize,
    int SampleRate, int Channels, int BitsPerSample, long TotalSamples, byte[] Md5);

public static class FlacDecoder
{
    /// STREAMINFO, the Vorbis comments (keys upper-cased; the first of a
    /// repeated key wins), and where the first frame starts.
    public static FlacStreamInfo ReadMetadata(byte[] data, out int framesStart, out Dictionary<string, string> tags)
    {
        tags = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = 0;
        if (data.Length >= 10 && data[0] == 'I' && data[1] == 'D' && data[2] == '3')
            at = 10 + ((data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F)) + ((data[5] & 0x10) != 0 ? 10 : 0);
        if (data.Length < at + 4 + 4 + 34 || data[at] != 'f' || data[at + 1] != 'L' || data[at + 2] != 'a' || data[at + 3] != 'C')
            throw new FlacDecodeException("Not a FLAC stream.");
        at += 4;
        FlacStreamInfo? info = null;
        bool last;
        do
        {
            if (at + 4 > data.Length) throw new FlacDecodeException("The metadata ends early.");
            last = (data[at] & 0x80) != 0;
            var type = data[at] & 0x7F;
            var size = data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3];
            at += 4;
            if (at + size > data.Length) throw new FlacDecodeException("A metadata block runs past the end.");
            if (type == 0)
            {
                // 34 bytes, the MD5 its last 16: a shorter block had them read
                // from past its end, or past the file's (security review
                // 2026-10-26, FLAC STREAMINFO).
                if (size < 34) throw new FlacDecodeException("STREAMINFO is too short.");
                var r = new BitReader(data, at, at + size);
                info = new FlacStreamInfo(
                    (int)r.Read(16), (int)r.Read(16), (int)r.Read(24), (int)r.Read(24),
                    (int)r.Read(20), (int)r.Read(3) + 1, (int)r.Read(5) + 1,
                    (long)r.Read(4) << 32 | r.Read(32), data.AsSpan(at + 18, 16).ToArray());
            }
            else if (type == 4)
                ReadComments(data.AsSpan(at, size), tags);
            at += size;
        } while (!last);
        framesStart = at;
        return info ?? throw new FlacDecodeException("No STREAMINFO.");
    }

    /// A VORBIS_COMMENT block (RFC 9639 §8.6): little-endian lengths, a vendor
    /// string, then "KEY=value" entries. A damaged block gives what it can.
    private static void ReadComments(ReadOnlySpan<byte> b, Dictionary<string, string> tags)
    {
        var at = 0;
        if (!Take(b, ref at, out var vendor)) return;
        at += vendor;
        if (!Take(b, ref at, out var count)) return;
        // A count, not a length, but Take's range check still holds for any real
        // one: every entry takes at least its own four length bytes.
        for (var i = 0; i < count; i++)
        {
            if (!Take(b, ref at, out var len)) return;
            var entry = System.Text.Encoding.UTF8.GetString(b.Slice(at, len));
            at += len;
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            tags.TryAdd(entry[..eq].ToUpperInvariant(), entry[(eq + 1)..]);
        }
    }

    /// A little-endian 32-bit length, and whether that many bytes follow it.
    private static bool Take(ReadOnlySpan<byte> b, ref int at, out int n)
    {
        n = 0;
        if (at + 4 > b.Length) return false;
        n = b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24;
        at += 4;
        return n >= 0 && at + n <= b.Length;
    }

    /// Whether a frame header starts at data[at] (sync, no reserved values, a
    /// matching CRC-8), and if so the number of its first sample and its block
    /// size. For seeking: a header can be found without decoding the frames
    /// before it. A fixed-blocksize stream numbers its frames, so the first
    /// sample is the frame number times the stream's block size.
    public static bool TryReadHeader(byte[] data, int at, FlacStreamInfo info, out long firstSample, out int blockSize)
    {
        firstSample = 0; blockSize = 0;
        if (at + 6 > data.Length || data[at] != 0xFF || (data[at + 1] & 0xFE) != 0xF8) return false;
        try
        {
            var r = new BitReader(data, at, Math.Min(data.Length, at + 16));
            r.Read(15);
            var variable = r.Read(1) == 1;
            var sizeCode = (int)r.Read(4);
            var rateCode = (int)r.Read(4);
            var channelCode = (int)r.Read(4);
            var depthCode = (int)r.Read(3);
            if (r.Read(1) != 0 || sizeCode == 0 || rateCode == 15 || channelCode > 10 || depthCode == 3) return false;

            var first = r.Read(8);
            var extra = first < 0x80 ? 0 : first < 0xC0 ? -1 : first < 0xE0 ? 1 : first < 0xF0 ? 2 : first < 0xF8 ? 3 : first < 0xFC ? 4 : first < 0xFE ? 5 : first == 0xFE ? 6 : -1;
            if (extra < 0) return false;
            long number = extra == 0 ? first : first & (0x3F >> extra);
            for (var i = 0; i < extra; i++)
            {
                var b = r.Read(8);
                if ((b & 0xC0) != 0x80) return false;
                number = number << 6 | (b & 0x3F);
            }

            blockSize = sizeCode switch
            {
                1 => 192,
                >= 2 and <= 5 => 576 << (sizeCode - 2),
                6 => (int)r.Read(8) + 1,
                7 => (int)r.Read(16) + 1,
                _ => 256 << (sizeCode - 8),
            };
            switch (rateCode)
            {
                case 12: r.Read(8); break;
                case 13 or 14: r.Read(16); break;
            }
            var headerEnd = r.BytePosition;
            if (r.Read(8) != FlacCrc.Crc8(data.AsSpan(at, headerEnd - at))) return false;
            firstSample = variable ? number : number * info.MaxBlockSize;
            return true;
        }
        catch (FlacDecodeException)
        {
            return false;
        }
    }


    /// One frame at data[start..]: its samples into channels[c][0..blockSize),
    /// growing them when a frame is longer. Returns the frame's length in bytes.
    public static int DecodeFrame(byte[] data, int start, int end, FlacStreamInfo? info, ref long[][] channels, out int blockSize)
    {
        var r = new BitReader(data, start, end);
        if (r.Read(14) != 0b11111111111110 || r.Read(1) != 0) throw new FlacDecodeException($"No frame sync at byte {start}.");
        r.Read(1);   // blocking strategy: fixed or variable, it reads the same
        var sizeCode = (int)r.Read(4);
        var rateCode = (int)r.Read(4);
        var channelCode = (int)r.Read(4);
        var depthCode = (int)r.Read(3);
        if (r.Read(1) != 0) throw new FlacDecodeException("A reserved frame-header bit is set.");

        // The frame or sample number, coded as UTF-8 extends it (up to 7 bytes).
        var first = r.Read(8);
        var extra = first < 0x80 ? 0 : first < 0xC0 ? -1 : first < 0xE0 ? 1 : first < 0xF0 ? 2 : first < 0xF8 ? 3 : first < 0xFC ? 4 : first < 0xFE ? 5 : first == 0xFE ? 6 : -1;
        if (extra < 0) throw new FlacDecodeException("A malformed frame number.");
        for (var i = 0; i < extra; i++) if ((r.Read(8) & 0xC0) != 0x80) throw new FlacDecodeException("A malformed frame number.");

        blockSize = sizeCode switch
        {
            1 => 192,
            >= 2 and <= 5 => 576 << (sizeCode - 2),
            6 => (int)r.Read(8) + 1,
            7 => (int)r.Read(16) + 1,
            >= 8 => 256 << (sizeCode - 8),
            _ => throw new FlacDecodeException("A reserved block size code."),
        };
        switch (rateCode)
        {
            case 12: r.Read(8); break;
            case 13 or 14: r.Read(16); break;
            case 15: throw new FlacDecodeException("A forbidden sample rate code.");
        }
        var bits = depthCode switch
        {
            0 => info?.BitsPerSample ?? throw new FlacDecodeException("The sample size is in STREAMINFO, which is missing."),
            1 => 8, 2 => 12, 4 => 16, 5 => 20, 6 => 24, 7 => 32,
            _ => throw new FlacDecodeException("A reserved sample size code."),
        };
        var headerEnd = r.BytePosition;
        if (r.Read(8) != FlacCrc.Crc8(data.AsSpan(start, headerEnd - start))) throw new FlacDecodeException($"Frame header CRC-8 mismatch at byte {start}.");

        var count = channelCode < 8 ? channelCode + 1 : channelCode <= 10 ? 2 : throw new FlacDecodeException("A reserved channel assignment.");
        if (channels.Length < count) Array.Resize(ref channels, count);
        for (var c = 0; c < count; c++)
            if (channels[c] is null || channels[c].Length < blockSize) channels[c] = new long[blockSize];

        for (var c = 0; c < count; c++)
        {
            // The side channel carries one bit more (RFC 9639 §9.2).
            var side = (channelCode == 8 && c == 1) || (channelCode == 9 && c == 0) || (channelCode == 10 && c == 1);
            Subframe(r, channels[c], blockSize, bits + (side ? 1 : 0));
        }

        switch (channelCode)
        {
            case 8:   // left, side
                for (var i = 0; i < blockSize; i++) channels[1][i] = channels[0][i] - channels[1][i];
                break;
            case 9:   // side, right
                for (var i = 0; i < blockSize; i++) channels[0][i] += channels[1][i];
                break;
            case 10:  // mid, side
                for (var i = 0; i < blockSize; i++)
                {
                    var mid = (channels[0][i] << 1) | (channels[1][i] & 1);
                    var s = channels[1][i];
                    channels[0][i] = (mid + s) >> 1;
                    channels[1][i] = (mid - s) >> 1;
                }
                break;
        }

        r.AlignToByte();
        var crcAt = r.BytePosition;
        var crc = r.Read(16);
        if (crc != FlacCrc.Crc16(data.AsSpan(start, crcAt - start))) throw new FlacDecodeException($"Frame CRC-16 mismatch at byte {start}.");
        return r.BytePosition - start;
    }

    private static void Subframe(BitReader r, long[] s, int n, int bits)
    {
        if (r.Read(1) != 0) throw new FlacDecodeException("A subframe's first bit is not zero.");
        var type = (int)r.Read(6);
        var wasted = r.Read(1) == 1 ? (int)r.ReadUnary() + 1 : 0;
        bits -= wasted;
        if (bits <= 0) throw new FlacDecodeException("More wasted bits than the sample has.");

        if (type == 0)
        {
            var v = r.ReadSigned(bits);
            Array.Fill(s, v, 0, n);
        }
        else if (type == 1)
        {
            for (var i = 0; i < n; i++) s[i] = r.ReadSigned(bits);
        }
        else if (type is >= 8 and <= 12)
        {
            var order = type - 8;
            if (order > n) throw new FlacDecodeException("A predictor order longer than the block.");
            for (var i = 0; i < order; i++) s[i] = r.ReadSigned(bits);
            Residual(r, s, n, order);
            for (var i = order; i < n; i++)
                s[i] += order switch
                {
                    0 => 0,
                    1 => s[i - 1],
                    2 => 2 * s[i - 1] - s[i - 2],
                    3 => 3 * s[i - 1] - 3 * s[i - 2] + s[i - 3],
                    _ => 4 * s[i - 1] - 6 * s[i - 2] + 4 * s[i - 3] - s[i - 4],
                };
        }
        else if (type >= 32)
        {
            var order = type - 31;
            if (order > n) throw new FlacDecodeException("A predictor order longer than the block.");
            for (var i = 0; i < order; i++) s[i] = r.ReadSigned(bits);
            var precision = (int)r.Read(4) + 1;
            if (precision == 16) throw new FlacDecodeException("An invalid coefficient precision.");
            var shift = (int)r.ReadSigned(5);
            if (shift < 0) throw new FlacDecodeException("A negative predictor shift.");
            var coef = new long[order];
            for (var j = 0; j < order; j++) coef[j] = r.ReadSigned(precision);
            Residual(r, s, n, order);
            for (var i = order; i < n; i++)
            {
                long sum = 0;
                for (var j = 0; j < order; j++) sum += coef[j] * s[i - 1 - j];
                s[i] += sum >> shift;
            }
        }
        else throw new FlacDecodeException($"A reserved subframe type ({type}).");

        if (wasted > 0)
            for (var i = 0; i < n; i++) s[i] <<= wasted;
    }

    /// The residual into s[order..n), to which the prediction is added after.
    private static void Residual(BitReader r, long[] s, int n, int order)
    {
        var method = (int)r.Read(2);
        if (method > 1) throw new FlacDecodeException("A reserved residual coding method.");
        var paramBits = method == 0 ? 4 : 5;
        var escape = (1 << paramBits) - 1;
        var partitionOrder = (int)r.Read(4);
        var parts = 1 << partitionOrder;
        if ((n >> partitionOrder) < order || (n & (parts - 1)) != 0 && partitionOrder > 0)
            throw new FlacDecodeException("A partition order the block cannot have.");
        var i = order;
        for (var p = 0; p < parts; p++)
        {
            var count = (n >> partitionOrder) - (p == 0 ? order : 0);
            var k = (int)r.Read(paramBits);
            if (k == escape)
            {
                var raw = (int)r.Read(5);
                for (var j = 0; j < count; j++) s[i++] = r.ReadSigned(raw);
            }
            else
                for (var j = 0; j < count; j++) s[i++] = r.ReadRice(k);
        }
    }
}


/// Reading bits back, most significant first, through a 64-bit cache: the
/// decoder reads every residual of every file in the library this way.
internal sealed class BitReader(byte[] data, int start, int end)
{
    private int _next = start;      // the next byte not yet in the cache
    private ulong _cache;           // left-aligned: the top bits are the next to read
    private int _cacheBits;

    /// The byte the next unread bit is in (or the next byte, at a boundary).
    public int BytePosition => _next - _cacheBits / 8;
    public bool AtByteBoundary => (_cacheBits & 7) == 0;

    private void Refill()
    {
        while (_cacheBits <= 56 && _next < end)
        {
            _cache |= (ulong)data[_next++] << (56 - _cacheBits);
            _cacheBits += 8;
        }
    }

    private void Drop(int bits)
    {
        _cache = bits >= 64 ? 0 : _cache << bits;
        _cacheBits -= bits;
    }

    /// 0 to 32 bits as an unsigned number.
    public uint Read(int bits)
    {
        if (bits == 0) return 0;
        if (_cacheBits < bits) Refill();
        if (_cacheBits < bits) throw new FlacDecodeException("The frame ends early.");
        var value = (uint)(_cache >> (64 - bits));
        Drop(bits);
        return value;
    }

    public long ReadSigned(int bits)
    {
        if (bits == 0) return 0;
        // A side channel of 32-bit audio is 33 bits wide.
        var v = bits > 32 ? (long)Read(bits - 32) << 32 | Read(32) : (long)Read(bits);
        return (v & (1L << (bits - 1))) != 0 ? v - (1L << bits) : v;
    }

    public ulong ReadUnary()
    {
        ulong zeros = 0;
        while (true)
        {
            if (_cacheBits == 0) { Refill(); if (_cacheBits == 0) throw new FlacDecodeException("The frame ends early."); }
            var lead = _cache == 0 ? 64 : System.Numerics.BitOperations.LeadingZeroCount(_cache);
            if (lead >= _cacheBits) { zeros += (ulong)_cacheBits; _cache = 0; _cacheBits = 0; continue; }
            zeros += (ulong)lead;
            Drop(lead + 1);
            return zeros;
        }
    }

    public long ReadRice(int k)
    {
        var q = ReadUnary();
        var folded = (q << k) | Read(k);
        return (folded & 1) == 0 ? (long)(folded >> 1) : -(long)(folded >> 1) - 1;
    }

    public void AlignToByte() => Drop(_cacheBits & 7);
}

/// The two checksums of a frame (RFC 9639 §9.1.8 and §9.3): CRC-8 over the
/// header, polynomial x^8 + x^2 + x + 1; CRC-16 over the whole frame,
/// x^16 + x^15 + x^2 + 1. Both start at zero, most significant bit first.
internal static class FlacCrc
{
    private static readonly byte[] Table8 = Build8();
    private static readonly ushort[] Table16 = Build16();

    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var b in data) crc = Table8[crc ^ b];
        return crc;
    }

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data) crc = (ushort)((crc << 8) ^ Table16[(crc >> 8) ^ b]);
        return crc;
    }

    private static byte[] Build8()
    {
        var t = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var c = (byte)i;
            for (var j = 0; j < 8; j++) c = (byte)((c & 0x80) != 0 ? (c << 1) ^ 0x07 : c << 1);
            t[i] = c;
        }
        return t;
    }

    private static ushort[] Build16()
    {
        var t = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var c = (ushort)(i << 8);
            for (var j = 0; j < 8; j++) c = (ushort)((c & 0x8000) != 0 ? (c << 1) ^ 0x8005 : c << 1);
            t[i] = c;
        }
        return t;
    }
}

public sealed class FlacDecodeException(string message) : IOException(message);
