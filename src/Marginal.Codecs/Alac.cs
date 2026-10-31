// COPIED from AlbumWall (src/AlbumWall.Codecs/Alac.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — ALAC (Apple Lossless) decoded in C#: the codec core.
//
// PORTED FROM APPLE'S OWN DECODER (github.com/macosforge/alac, at c38887c,
// 2016-05-11), Copyright (c) 2011 Apple Inc., under the Apache License 2.0,
// which GPL v3 can carry. ALAC is the third codec an iTunes library holds,
// after AAC and MP3, and once mpv is gone it has nowhere else to go. Ported
// function by function from codec/: ALACDecoder.cpp (the frame and its
// channel elements), ag_dec.c (the adaptive Golomb decoder, dyn_decomp),
// dp_dec.c (the dynamic predictor, unpc_block), matrix_dec.c (the stereo
// unmix) and ALACBitUtilities.c (the bit reader); the C names are kept in
// PascalCase so that this can be read beside the C. The magic cookie is read
// as ALACMagicCookieDescription.txt describes it.
//
// What differs from the C, and why:
//   * unpc_block has hand-unrolled copies for 4 and 8 coefficients; only its
//     general loop is here. They are the same sums in another order, and in
//     wrapping 32-bit arithmetic the order does not change the result.
//   * Its output is not 16-, 20-, 24- or 32-bit PCM in a byte buffer but
//     floats from -1 to 1, as FlacTrack gives: each sample is what Apple's
//     output routine would store, sign-extended at the stream's bit depth and
//     scaled by 1 / 2^(depth-1), so a 16-bit ALAC file and its FLAC source
//     play the same floats.
//   * Channels past two are put in the WAV order FLAC and FFmpeg use, not left
//     in ALAC's (centre first): the layouts are those of
//     ALACMagicCookieDescription.txt, which FFmpeg's alac_data.c agrees with.
//   * A frame that ends before all its channels, or holds an element Apple's
//     decoder refuses (a coupling channel, a program config), is damaged: it
//     throws, and the track plays silence in its place. Channels a frame
//     leaves out are silent, at every bit depth (Apple's forgets the 20-bit).
//   * Every read is bounds-checked by C#, where the C trusted the packet: a
//     frame is copied into a buffer with a few zero bytes after it, for the
//     reads past its end that the C makes by design, and a read beyond those
//     is a damaged frame.
//
// Each frame is independent of the one before: no state is carried from one
// to the next, which is why a seek needs no pre-roll (Mp4Track). The MP4
// around it (the sample table, the edit list, gapless trimming, tags) is
// Mp4Track's.

using System.Numerics;

namespace Marginal.Codecs;

internal sealed class AlacDecoder
{
    // ALACAudioTypes.h, ALACBitUtilities.h
    private const int IdSce = 0, IdCpe = 1, IdCce = 2, IdLfe = 3, IdDse = 4, IdPce = 5, IdFil = 6, IdEnd = 7;
    private const int MaxChannels = 8;

    // aglib.h
    private const int QbShift = 9;
    private const uint Qb = 1u << QbShift;
    private const int MMulShift = 2;
    private const int MDenShift = QbShift - MMulShift - 1;
    private const uint MOff = 1u << (MDenShift - 2);
    private const int BitOff = 24;
    private const int MaxPrefix16 = 9;
    private const int MaxPrefix32 = 9;
    private const int MaxDatatypeBits16 = 16;

    // ag_dec.c
    private const uint NMaxMeanClamp = 0xffff;
    private const uint NMeanClampVal = 0xffff;

    /// Bytes of zeros after a frame's own, for the C's reads past its end: the
    /// bit reader looks three bytes ahead, the Golomb decoder five.
    private const int Padding = 8;

    /// Where each channel, in the order the elements carry them, goes in the
    /// output: ALAC's layouts (C L R ..., ALACMagicCookieDescription.txt) put
    /// in WAV's (L R C LFE ...).
    private static readonly int[][] ChannelOrder =
    [
        [0],                            // C
        [0, 1],                         // L R
        [2, 0, 1],                      // C L R
        [2, 0, 1, 3],                   // C L R Cs
        [2, 0, 1, 3, 4],                // C L R Ls Rs
        [2, 0, 1, 4, 5, 3],             // C L R Ls Rs LFE
        [2, 0, 1, 4, 5, 6, 3],          // C L R Ls Rs Cs LFE
        [2, 6, 7, 0, 1, 4, 5, 3],       // C Lc Rc L R Ls Rs LFE
    ];

    public int FrameLength { get; }
    public int BitDepth { get; }
    public int Channels { get; }
    public int SampleRate { get; }

    private readonly uint _pb, _mb, _kb, _maxRun;
    private readonly int[] _order;
    private readonly float _scale;

    private readonly int[] _mixBufferU, _mixBufferV, _predictor;
    private readonly ushort[] _shiftBuffer;
    private readonly short[] _coefsU = new short[32], _coefsV = new short[32];
    private byte[] _frame = new byte[4096];

    private AlacDecoder(int frameLength, int bitDepth, int pb, int mb, int kb, int channels, int maxRun, int sampleRate)
    {
        FrameLength = frameLength;
        BitDepth = bitDepth;
        Channels = channels;
        SampleRate = sampleRate;
        _pb = (uint)pb;
        _mb = (uint)mb;
        _kb = (uint)kb;
        _maxRun = (uint)maxRun;
        _order = ChannelOrder[channels - 1];
        _scale = 1f / (1L << (bitDepth - 1));
        // The predictor writes its first coefficients' worth of samples
        // whatever the frame's length, so the buffers hold at least that.
        var size = Math.Max(frameLength, 32);
        _mixBufferU = new int[size];
        _mixBufferV = new int[size];
        _predictor = new int[size];
        _shiftBuffer = new ushort[2 * size];
    }

    /// From the magic cookie (ALACSpecificConfig), as an MP4's 'alac' box
    /// carries it: ALACDecoder::Init, which also takes the older cookies that
    /// wrap it in 'frma' and 'alac' atoms.
    public static AlacDecoder FromCookie(ReadOnlySpan<byte> cookie)
    {
        if (cookie.Length >= 12 && cookie[4] == 'f' && cookie[5] == 'r' && cookie[6] == 'm' && cookie[7] == 'a') cookie = cookie[12..];
        if (cookie.Length >= 12 && cookie[4] == 'a' && cookie[5] == 'l' && cookie[6] == 'a' && cookie[7] == 'c') cookie = cookie[12..];
        if (cookie.Length < 24) throw new FlacDecodeException("The ALAC magic cookie is too short.");

        var frameLength = Be32(cookie, 0);
        int compatibleVersion = cookie[4];
        int bitDepth = cookie[5];
        int pb = cookie[6], mb = cookie[7], kb = cookie[8];
        int channels = cookie[9];
        var maxRun = cookie[10] << 8 | cookie[11];
        var sampleRate = Be32(cookie, 20);

        if (compatibleVersion > 0) throw new UnsupportedAudioException($"ALAC version {compatibleVersion} is not supported.");
        // The only depths Apple's decoder writes out; any other would be
        // decoded and then dropped on the floor.
        if (bitDepth is not (16 or 20 or 24 or 32)) throw new UnsupportedAudioException($"{bitDepth}-bit ALAC is not supported.");
        if (channels is < 1 or > MaxChannels) throw new UnsupportedAudioException($"ALAC with {channels} channels is not supported.");
        if (frameLength is 0 or > 1 << 20) throw new FlacDecodeException($"An ALAC frame length of {frameLength} samples.");
        if (sampleRate is 0 or > 1_000_000) throw new FlacDecodeException($"An ALAC sample rate of {sampleRate}.");
        return new AlacDecoder((int)frameLength, bitDepth, pb, mb, kb, channels, maxRun, (int)sampleRate);
    }

    private static uint Be32(ReadOnlySpan<byte> b, int i) => (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);

    /// One ALAC packet (an MP4 sample) into `pcm`, interleaved, as floats from
    /// -1 to 1. Returns its length in samples: FrameLength, or fewer for a
    /// partial frame (the last).
    public int DecodeFrame(byte[] data, int offset, int length, Span<float> pcm)
    {
        if (_frame.Length < length + Padding) _frame = new byte[length + Padding];
        data.AsSpan(offset, length).CopyTo(_frame);
        _frame.AsSpan(length, Padding).Clear();
        var bits = new BitBuffer(_frame, length);
        try
        {
            return Decode(ref bits, pcm);
        }
        catch (IndexOutOfRangeException)
        {
            throw new FlacDecodeException("A damaged ALAC frame: it reads past its end.");
        }
    }

    // ------------------------------------------------------------ ALACDecoder.cpp

    /// ALACDecoder::Decode: the frame's elements, one after another, until
    /// every channel has been decoded or the frame ends.
    private int Decode(ref BitBuffer bits, Span<float> pcm)
    {
        var numChannels = Channels;
        var numSamples = FrameLength;
        var channelIndex = 0;
        var shiftBits = default(BitBuffer);

        while (channelIndex < numChannels)
        {
            if (bits.Cur >= bits.End) throw new FlacDecodeException("A damaged ALAC frame: it ends before its channels.");

            var pb = _pb;
            int tag = BitBufferReadSmall(ref bits, 3);
            switch (tag)
            {
                case IdSce:
                case IdLfe:
                {
                    // mono/LFE channel
                    BitBufferReadSmall(ref bits, 4);                // element instance tag
                    if (BitBufferRead(ref bits, 12) != 0) throw new FlacDecodeException("A damaged ALAC frame: unused header bits set.");

                    // the 1-bit "partial frame" flag, 2-bit "shift-off" flag & 1-bit "escape" flag
                    var headerByte = BitBufferRead(ref bits, 4);
                    var partialFrame = headerByte >> 3;
                    var bytesShifted = (int)(headerByte >> 1) & 0x3;
                    if (bytesShifted == 3) throw new FlacDecodeException("A damaged ALAC frame: 3 bytes shifted.");
                    var shift = bytesShifted * 8;
                    var escapeFlag = headerByte & 0x1;
                    var chanBits = BitDepth - bytesShifted * 8;

                    if (partialFrame != 0) numSamples = PartialFrame(ref bits);

                    if (escapeFlag == 0)
                    {
                        // compressed frame, read rest of parameters (no mixing for mono)
                        BitBufferRead(ref bits, 8);                 // mixBits
                        BitBufferRead(ref bits, 8);                 // mixRes

                        headerByte = BitBufferRead(ref bits, 8);
                        var modeU = headerByte >> 4;
                        var denShiftU = headerByte & 0xf;

                        headerByte = BitBufferRead(ref bits, 8);
                        var pbFactorU = headerByte >> 5;
                        var numU = (int)(headerByte & 0x1f);
                        for (var i = 0; i < numU; i++) _coefsU[i] = (short)BitBufferRead(ref bits, 16);

                        // if shift active, skip the shift buffer but remember where it starts
                        if (bytesShifted != 0)
                        {
                            shiftBits = bits;
                            BitBufferAdvance(ref bits, (uint)(bytesShifted * 8 * numSamples));
                        }

                        var agParams = new AgParams(_mb, pb * pbFactorU / 4, _kb, _maxRun);
                        DynDecomp(agParams, ref bits, _predictor, numSamples, chanBits);

                        if (modeU != 0)
                        {
                            // the special "numActive == 31" mode, in place
                            UnpcBlock(_predictor, _predictor, numSamples, null, 31, chanBits, 0);
                        }
                        UnpcBlock(_predictor, _mixBufferU, numSamples, _coefsU, numU, chanBits, (int)denShiftU);
                    }
                    else
                    {
                        // uncompressed frame, copied into the mix buffer to use the common output code
                        Uncompressed(ref bits, _mixBufferU, null, numSamples, chanBits);
                        bytesShifted = 0;
                    }

                    // now read the shifted values into the shift buffer
                    if (bytesShifted != 0)
                    {
                        for (var i = 0; i < numSamples; i++) _shiftBuffer[i] = (ushort)BitBufferRead(ref shiftBits, (byte)shift);
                    }

                    // the samples out, as the copyPredictorTo* routines would store them
                    var o = _order[channelIndex];
                    var shifted = BitDepth >= 24 && bytesShifted != 0;
                    for (var i = 0; i < numSamples; i++)
                    {
                        var val = _mixBufferU[i];
                        if (shifted) val = val << shift | _shiftBuffer[i];
                        pcm[i * numChannels + o] = Output(val);
                    }

                    channelIndex += 1;
                    break;
                }

                case IdCpe:
                {
                    // if decoding this pair would take us over the max channels limit, bail
                    if (channelIndex + 2 > numChannels) goto NoMoreChannels;

                    // stereo channel pair
                    BitBufferReadSmall(ref bits, 4);                // element instance tag
                    if (BitBufferRead(ref bits, 12) != 0) throw new FlacDecodeException("A damaged ALAC frame: unused header bits set.");

                    var headerByte = BitBufferRead(ref bits, 4);
                    var partialFrame = headerByte >> 3;
                    var bytesShifted = (int)(headerByte >> 1) & 0x3;
                    if (bytesShifted == 3) throw new FlacDecodeException("A damaged ALAC frame: 3 bytes shifted.");
                    var shift = bytesShifted * 8;
                    var escapeFlag = headerByte & 0x1;
                    var chanBits = BitDepth - bytesShifted * 8 + 1;

                    if (partialFrame != 0) numSamples = PartialFrame(ref bits);

                    int mixBits, mixRes;
                    if (escapeFlag == 0)
                    {
                        // compressed frame, read rest of parameters
                        mixBits = (int)BitBufferRead(ref bits, 8);
                        mixRes = (sbyte)BitBufferRead(ref bits, 8);

                        headerByte = BitBufferRead(ref bits, 8);
                        var modeU = headerByte >> 4;
                        var denShiftU = headerByte & 0xf;

                        headerByte = BitBufferRead(ref bits, 8);
                        var pbFactorU = headerByte >> 5;
                        var numU = (int)(headerByte & 0x1f);
                        for (var i = 0; i < numU; i++) _coefsU[i] = (short)BitBufferRead(ref bits, 16);

                        headerByte = BitBufferRead(ref bits, 8);
                        var modeV = headerByte >> 4;
                        var denShiftV = headerByte & 0xf;

                        headerByte = BitBufferRead(ref bits, 8);
                        var pbFactorV = headerByte >> 5;
                        var numV = (int)(headerByte & 0x1f);
                        for (var i = 0; i < numV; i++) _coefsV[i] = (short)BitBufferRead(ref bits, 16);

                        // if shift active, skip the interleaved shifted values but remember where they start
                        if (bytesShifted != 0)
                        {
                            shiftBits = bits;
                            BitBufferAdvance(ref bits, (uint)(bytesShifted * 8 * 2 * numSamples));
                        }

                        // decompress and run predictor for "left" channel
                        DynDecomp(new AgParams(_mb, pb * pbFactorU / 4, _kb, _maxRun), ref bits, _predictor, numSamples, chanBits);
                        if (modeU != 0) UnpcBlock(_predictor, _predictor, numSamples, null, 31, chanBits, 0);
                        UnpcBlock(_predictor, _mixBufferU, numSamples, _coefsU, numU, chanBits, (int)denShiftU);

                        // decompress and run predictor for "right" channel
                        DynDecomp(new AgParams(_mb, pb * pbFactorV / 4, _kb, _maxRun), ref bits, _predictor, numSamples, chanBits);
                        if (modeV != 0) UnpcBlock(_predictor, _predictor, numSamples, null, 31, chanBits, 0);
                        UnpcBlock(_predictor, _mixBufferV, numSamples, _coefsV, numV, chanBits, (int)denShiftV);
                    }
                    else
                    {
                        // uncompressed frame, copied into the mix buffers to use the common output code
                        Uncompressed(ref bits, _mixBufferU, _mixBufferV, numSamples, BitDepth);
                        mixBits = mixRes = 0;
                        bytesShifted = 0;
                    }

                    // now read the shifted values into the shift buffer
                    if (bytesShifted != 0)
                    {
                        for (var i = 0; i < numSamples * 2; i += 2)
                        {
                            _shiftBuffer[i + 0] = (ushort)BitBufferRead(ref shiftBits, (byte)shift);
                            _shiftBuffer[i + 1] = (ushort)BitBufferRead(ref shiftBits, (byte)shift);
                        }
                    }

                    // un-mix the data and convert to output format (unmix16/20/24/32);
                    // mixRes = 0 means just interleave, which uncompressed frames use
                    var ol = _order[channelIndex];
                    var or = _order[channelIndex + 1];
                    var shifted = BitDepth >= 24 && bytesShifted != 0;
                    for (int j = 0, k = 0; j < numSamples; j++, k += 2)
                    {
                        int l, r;
                        if (mixRes != 0)
                        {
                            // matrixed stereo
                            l = _mixBufferU[j] + _mixBufferV[j] - ((mixRes * _mixBufferV[j]) >> mixBits);
                            r = l - _mixBufferV[j];
                        }
                        else
                        {
                            // conventional separated stereo
                            l = _mixBufferU[j];
                            r = _mixBufferV[j];
                        }
                        if (shifted)
                        {
                            l = l << shift | _shiftBuffer[k + 0];
                            r = r << shift | _shiftBuffer[k + 1];
                        }
                        pcm[j * numChannels + ol] = Output(l);
                        pcm[j * numChannels + or] = Output(r);
                    }

                    channelIndex += 2;
                    break;
                }

                case IdCce:
                case IdPce:
                    // unsupported element: Apple's decoder bails
                    throw new FlacDecodeException($"An ALAC frame with a {(tag == IdCce ? "coupling channel" : "program config")} element.");

                case IdDse:
                    // data stream element: parse but ignore
                    DataStreamElement(ref bits);
                    break;

                case IdFil:
                    // fill element: parse but ignore
                    FillElement(ref bits);
                    break;

                case IdEnd:
                    // the frame's end, before all the channels it should have
                    BitBufferByteAlign(ref bits);
                    goto NoMoreChannels;
            }
        }

    NoMoreChannels:
        // channels the frame did not have are silent
        for (; channelIndex < numChannels; channelIndex++)
        {
            var o = _order[channelIndex];
            for (var i = 0; i < numSamples; i++) pcm[i * numChannels + o] = 0;
        }
        return numSamples;
    }

    /// The length of a partial frame, which overrides the cookie's.
    private int PartialFrame(ref BitBuffer bits)
    {
        var n = BitBufferRead(ref bits, 16) << 16;
        n |= BitBufferRead(ref bits, 16);
        if (n > FrameLength) throw new FlacDecodeException($"A damaged ALAC frame: {n} samples, more than {FrameLength}.");
        return (int)n;
    }

    /// An escaped (uncompressed) element's samples, one channel or a pair
    /// interleaved, `chanBits` each: BitBufferRead reads at most 16 bits at a
    /// time, so a wider sample is read in two.
    private static void Uncompressed(ref BitBuffer bits, int[] u, int[]? v, int numSamples, int chanBits)
    {
        var shift = 32 - chanBits;
        if (chanBits <= 16)
        {
            for (var i = 0; i < numSamples; i++)
            {
                var val = (int)BitBufferRead(ref bits, (byte)chanBits);
                u[i] = (val << shift) >> shift;
                if (v is null) continue;
                val = (int)BitBufferRead(ref bits, (byte)chanBits);
                v[i] = (val << shift) >> shift;
            }
        }
        else
        {
            var extraBits = (byte)(chanBits - 16);
            for (var i = 0; i < numSamples; i++)
            {
                var val = (int)BitBufferRead(ref bits, 16);
                val = (val << 16) >> shift;
                u[i] = val | (int)BitBufferRead(ref bits, extraBits);
                if (v is null) continue;
                val = (int)BitBufferRead(ref bits, 16);
                val = (val << 16) >> shift;
                v[i] = val | (int)BitBufferRead(ref bits, extraBits);
            }
        }
    }

    /// A sample as Apple's output routines store it (the low BitDepth bits:
    /// a 16-bit sample is cast to int16_t, a 24-bit one written as 3 bytes)
    /// and as a float.
    private float Output(int val)
    {
        var s = 32 - BitDepth;
        return ((val << s) >> s) * _scale;
    }

    /// ALACDecoder::FillElement: filler, skipped.
    private static void FillElement(ref BitBuffer bits)
    {
        // 4-bit count or (4-bit + 8-bit count) if 4-bit count == 15
        int count = BitBufferReadSmall(ref bits, 4);
        if (count == 15) count += BitBufferReadSmall(ref bits, 8) - 1;
        BitBufferAdvance(ref bits, (uint)(count * 8));
        if (bits.Cur > bits.End) throw new FlacDecodeException("A damaged ALAC frame: a fill element past its end.");
    }

    /// ALACDecoder::DataStreamElement: not wanted, skipped.
    private static void DataStreamElement(ref BitBuffer bits)
    {
        BitBufferReadSmall(ref bits, 4);                            // element instance tag
        var dataByteAlignFlag = BitBufferReadOne(ref bits);
        // 8-bit count or (8-bit + 8-bit count) if 8-bit count == 255
        int count = BitBufferReadSmall(ref bits, 8);
        if (count == 255) count += BitBufferReadSmall(ref bits, 8);
        if (dataByteAlignFlag != 0) BitBufferByteAlign(ref bits);
        BitBufferAdvance(ref bits, (uint)(count * 8));
        if (bits.Cur > bits.End) throw new FlacDecodeException("A damaged ALAC frame: a data element past its end.");
    }

    // ------------------------------------------------------------ ag_dec.c

    private readonly struct AgParams(uint m, uint p, uint k, uint maxrun)
    {
        // set_ag_params, with fullwidth and sectorwidth, which decoding never reads, left out
        public readonly uint Mb0 = m;
        public readonly uint Pb = p;
        public readonly uint Kb = k;
        public readonly uint Wb = (1u << (int)k) - 1;
        public readonly uint MaxRun = maxrun;
    }

    /// The leading zeros of a 32-bit word (32 for none): lead, the loop in the
    /// C that its own comment says a count-leading-zeros instruction should be.
    private static int Lead(uint m) => BitOperations.LeadingZeroCount(m);

    private static int Lg3a(uint x) => 31 - Lead(x + 3);

    private static uint Read32Bit(byte[] buffer, int at) =>
        (uint)buffer[at] << 24 | (uint)buffer[at + 1] << 16 | (uint)buffer[at + 2] << 8 | buffer[at + 3];

    private static uint GetStreamBits(byte[] input, int inAt, uint bitOffset, int numBits)
    {
        var byteOffset = inAt + (int)(bitOffset / 8);
        var load1 = Read32Bit(input, byteOffset);
        var off = (int)(bitOffset & 7);
        uint result;
        if (numBits + off > 32)
        {
            result = load1 << off;
            uint load2 = input[byteOffset + 4];
            var load2Shift = 8 - (numBits + off - 32);
            load2 >>= load2Shift;
            result >>= 32 - numBits;
            result |= load2;
        }
        else
        {
            result = load1 >> (32 - numBits - off);
        }
        // a shift by 32 does nothing in C# (as on x86), so the C's guard stays
        if (numBits != 32) result &= ~(0xffffffffu << numBits);
        return result;
    }

    /// dyn_get: a run length's Golomb code.
    private static uint DynGet(byte[] input, int inAt, ref uint bitPos, uint m, int k)
    {
        var tempBits = bitPos;
        uint result;
        var streamLong = Read32Bit(input, inAt + (int)(tempBits >> 3));
        streamLong <<= (int)(tempBits & 7);

        // the number of bits in the prefix
        var pre = (uint)Lead(~streamLong);
        if (pre >= MaxPrefix16)
        {
            pre = MaxPrefix16;
            tempBits += pre;
            streamLong <<= (int)pre;
            result = streamLong >> (32 - MaxDatatypeBits16);
            tempBits += MaxDatatypeBits16;
        }
        else
        {
            // all of the bits fit within the word loaded
            tempBits += pre;
            tempBits += 1;
            streamLong <<= (int)(pre + 1);
            var v = streamLong >> (32 - k);
            tempBits += (uint)k;
            result = pre * m + v - 1;
            if (v < 2)
            {
                result -= v - 1;
                tempBits -= 1;
            }
        }
        bitPos = tempBits;
        return result;
    }

    /// dyn_get_32bit: a sample's Golomb code, escaped to `maxBits` raw bits.
    private static uint DynGet32Bit(byte[] input, int inAt, ref uint bitPos, uint m, int k, int maxBits)
    {
        var tempBits = bitPos;
        var streamLong = Read32Bit(input, inAt + (int)(tempBits >> 3));
        streamLong <<= (int)(tempBits & 7);

        // the number of bits in the prefix
        var result = (uint)Lead(~streamLong);
        if (result >= MaxPrefix32)
        {
            result = GetStreamBits(input, inAt, tempBits + MaxPrefix32, maxBits);
            tempBits += MaxPrefix32 + (uint)maxBits;
        }
        else
        {
            // all of the bits fit within the word loaded
            tempBits += result;
            tempBits += 1;
            if (k != 1)
            {
                streamLong <<= (int)(result + 1);
                var v = streamLong >> (32 - k);
                tempBits += (uint)k;
                tempBits -= 1;
                result *= m;
                if (v >= 2)
                {
                    result += v - 1;
                    tempBits += 1;
                }
            }
        }
        bitPos = tempBits;
        return result;
    }

    /// dyn_decomp: one channel's prediction residuals, adaptive Golomb coded,
    /// with runs of zeros.
    private static void DynDecomp(in AgParams p, ref BitBuffer bitstream, int[] pc, int numSamples, int maxSize)
    {
        var input = bitstream.Buffer;
        var inAt = bitstream.Cur;
        var startPos = (uint)bitstream.BitIndex;
        var maxPos = (uint)bitstream.ByteSize * 8;
        var bitPos = startPos;

        var mb = p.Mb0;
        var pb = p.Pb;
        var kb = p.Kb;
        var wb = p.Wb;
        uint zmode = 0;
        var c = 0;
        var outAt = 0;

        while (c < numSamples)
        {
            // bail if we've run off the end of the buffer
            if (bitPos >= maxPos) throw new FlacDecodeException("A damaged ALAC frame: its residuals run past its end.");

            var m = mb >> QbShift;
            var k = (uint)Lg3a(m);
            k = Math.Min(k, kb);
            m = (1u << (int)k) - 1;

            var n = DynGet32Bit(input, inAt, ref bitPos, m, (int)k, maxSize);

            // least significant bit is sign bit
            {
                var ndecode = n + zmode;
                var multiplier = -(int)(ndecode & 1);
                multiplier |= 1;
                pc[outAt++] = (int)(((ndecode + 1) >> 1) * (uint)multiplier);
            }

            c++;

            mb = pb * (n + zmode) + mb - ((pb * mb) >> QbShift);

            // update mean tracking
            if (n > NMaxMeanClamp) mb = NMeanClampVal;

            zmode = 0;

            if ((mb << MMulShift) < Qb && c < numSamples)
            {
                zmode = 1;
                k = (uint)Lead(mb) - BitOff + ((mb + MOff) >> MDenShift);
                var mz = ((1u << (int)k) - 1) & wb;

                n = DynGet(input, inAt, ref bitPos, mz, (int)k);

                if (c + n > numSamples) throw new FlacDecodeException("A damaged ALAC frame: a run of zeros past its end.");

                for (var j = 0; j < n; j++)
                {
                    pc[outAt++] = 0;
                    ++c;
                }

                if (n >= 65535) zmode = 0;

                mb = 0;
            }
        }

        BitBufferAdvance(ref bitstream, bitPos - startPos);
        if (bitstream.Cur > bitstream.End) throw new FlacDecodeException("A damaged ALAC frame: its residuals run past its end.");
    }

    // ------------------------------------------------------------ dp_dec.c

    private static int SignOfInt(int i)
    {
        var negishift = (int)((uint)-i >> 31);
        return negishift | (i >> 31);
    }

    /// unpc_block: the dynamic predictor run backwards, from residuals `pc1`
    /// to samples `output` (which may be the same array), adapting its
    /// coefficients as it goes. Apple's unrolled cases for 4 and 8
    /// coefficients are this loop, written out.
    private static void UnpcBlock(int[] pc1, int[] output, int num, short[]? coefs, int numActive, int chanBits, int denShift)
    {
        var chanShift = 32 - chanBits;
        var denHalf = 1 << (denShift - 1);

        output[0] = pc1[0];
        if (numActive == 0)
        {
            // just copy if numActive == 0 (but don't bother if in/out are the same)
            if (num > 1 && pc1 != output) Array.Copy(pc1, 1, output, 1, num - 1);
            return;
        }
        if (numActive == 31)
        {
            // short-circuit if numActive == 31, written so that in and out can be the same
            var prev = output[0];
            for (var j = 1; j < num; j++)
            {
                var d = pc1[j] + prev;
                prev = (d << chanShift) >> chanShift;
                output[j] = prev;
            }
            return;
        }

        for (var j = 1; j <= numActive; j++)
        {
            var d = pc1[j] + output[j - 1];
            output[j] = (d << chanShift) >> chanShift;
        }

        var lim = numActive + 1;

        // general case
        for (var j = lim; j < num; j++)
        {
            var sum1 = 0;
            var pout = j - 1;
            var top = output[j - lim];

            for (var k = 0; k < numActive; k++) sum1 += coefs![k] * (output[pout - k] - top);

            var del = pc1[j];
            var del0 = del;
            var sg = SignOfInt(del);
            del += top + ((sum1 + denHalf) >> denShift);
            output[j] = (del << chanShift) >> chanShift;

            if (sg > 0)
            {
                for (var k = numActive - 1; k >= 0; k--)
                {
                    var dd = top - output[pout - k];
                    var sgn = SignOfInt(dd);
                    coefs![k] -= (short)sgn;
                    del0 -= (numActive - k) * ((sgn * dd) >> denShift);
                    if (del0 <= 0) break;
                }
            }
            else if (sg < 0)
            {
                for (var k = numActive - 1; k >= 0; k--)
                {
                    var dd = top - output[pout - k];
                    var sgn = SignOfInt(dd);
                    coefs![k] += (short)sgn;
                    del0 -= (numActive - k) * ((-sgn * dd) >> denShift);
                    if (del0 >= 0) break;
                }
            }
        }
    }

    // ------------------------------------------------------------ ALACBitUtilities.c

    /// A big-endian bit reader over one frame: a byte and a bit within it.
    private struct BitBuffer(byte[] buffer, int byteSize)
    {
        public readonly byte[] Buffer = buffer;
        public readonly int End = byteSize;
        public readonly int ByteSize = byteSize;
        public int Cur;
        public int BitIndex;
    }

    /// Up to 16 bits.
    private static uint BitBufferRead(ref BitBuffer bits, byte numBits)
    {
        var b = bits.Buffer;
        var returnBits = (uint)b[bits.Cur] << 16 | (uint)b[bits.Cur + 1] << 8 | b[bits.Cur + 2];
        returnBits <<= bits.BitIndex;
        returnBits &= 0x00FFFFFF;
        bits.BitIndex += numBits;
        returnBits >>= 24 - numBits;
        bits.Cur += bits.BitIndex >> 3;
        bits.BitIndex &= 7;
        return returnBits;
    }

    /// Up to 8 bits.
    private static byte BitBufferReadSmall(ref BitBuffer bits, byte numBits)
    {
        var b = bits.Buffer;
        var returnBits = (ushort)(b[bits.Cur] << 8 | b[bits.Cur + 1]);
        returnBits = (ushort)(returnBits << bits.BitIndex);
        bits.BitIndex += numBits;
        returnBits = (ushort)(returnBits >> (16 - numBits));
        bits.Cur += bits.BitIndex >> 3;
        bits.BitIndex &= 7;
        return (byte)returnBits;
    }

    /// One bit.
    private static byte BitBufferReadOne(ref BitBuffer bits)
    {
        var returnBits = (byte)((bits.Buffer[bits.Cur] >> (7 - bits.BitIndex)) & 1);
        bits.BitIndex++;
        bits.Cur += bits.BitIndex >> 3;
        bits.BitIndex &= 7;
        return returnBits;
    }

    private static void BitBufferByteAlign(ref BitBuffer bits)
    {
        if (bits.BitIndex == 0) return;
        BitBufferAdvance(ref bits, (uint)(8 - bits.BitIndex));
    }

    private static void BitBufferAdvance(ref BitBuffer bits, uint numBits)
    {
        if (numBits == 0) return;
        // a damaged count would wrap an int: past the end either way
        if (numBits > (uint)(bits.ByteSize + Padding) * 8) throw new FlacDecodeException("A damaged ALAC frame: a skip past its end.");
        bits.BitIndex += (int)numBits;
        bits.Cur += bits.BitIndex >> 3;
        bits.BitIndex &= 7;
    }
}
