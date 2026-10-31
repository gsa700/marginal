// COPIED from AlbumWall (src/AlbumWall.Codecs/Mp3.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — MP3 (MPEG-1, 2 and 2.5 Layer III) decoded in C#: the codec core.
//
// PORTED FROM minimp3 (github.com/lieff/minimp3, minimp3.h at ea99364,
// 2026-07-27) by lieff, dedicated to the public domain (CC0 1.0), his call on
// 2026-10-22: the top three formats get native C# decoders so that mpv can go.
// It is ported line for line, pointers and all, so that it can be read beside
// the C and a fix there carried over: the functions keep their C names in
// PascalCase, and the macros became the small methods they stand for. What is
// left out: Layers I and II (MP2, which a .mp3 almost never is), the SIMD
// paths (the plain C ones are the same arithmetic, four at a time), and the
// 16-bit output (this one gives floats, MINIMP3_FLOAT_OUTPUT). The tables are
// in Mp3Tables.cs, generated from the C by a script.
//
// What a track needs around it (the Xing/LAME tag, gapless trimming, ID3,
// seeking) is Mp3Track's; this decodes one frame at a time and keeps what
// the next frame needs: the bit reservoir, the IMDCT overlap and the
// synthesis filter's history.

namespace Marginal.Codecs;

internal sealed unsafe partial class Mp3Decoder
{
    public const int MaxSamplesPerFrame = 1152 * 2;

    private const int MaxFreeFormatFrameSize = 2304;    // more than ISO spec's
    private const int MaxFrameSyncMatches = 10;
    private const int MaxL3FramePayloadBytes = MaxFreeFormatFrameSize;
    private const int MaxBitReservoirBytes = 511;
    private const int ShortBlockType = 2;
    private const int StopBlockType = 3;
    private const int ModeMono = 3;
    private const int ModeJointStereo = 1;
    private const int HdrSize = 4;
    private const int BitsDequantizerOut = -1;
    private const int MaxScf = 255 + BitsDequantizerOut * 4 - 210;
    private const int MaxScfi = (MaxScf + 3) & ~3;

    public struct FrameInfo
    {
        public int FrameBytes, FrameOffset, Channels, Hz, Layer, BitrateKbps;
    }

    private struct Bs
    {
        public byte* Buf;
        public int Pos, Limit;
    }

    private struct GrInfo
    {
        public byte* Sfbtab;
        public ushort Part23Length, BigValues, ScalefacCompress;
        public byte GlobalGain, BlockType, MixedBlockFlag, NLongSfb, NShortSfb;
        public fixed byte TableSelect[3];
        public fixed byte RegionCount[3];
        public fixed byte SubblockGain[3];
        public byte Preflag, ScalefacScale, Count1Table, Scfsi;
    }

    // mp3dec_t: what carries from one frame to the next.
    private readonly float* _mdctOverlap;          // [2][9*32]
    private readonly float* _qmfState;             // [15*2*32]
    private int _reserv, _freeFormatBytes;
    private readonly byte* _header;                // [4]
    private readonly byte* _reservBuf;             // [511]

    // mp3dec_scratch_t: for one frame only.
    private Bs _bs;
    private readonly byte* _maindata;              // [511 + 2304], and slack the Huffman reader may look into
    private readonly GrInfo* _grInfo;              // [4]
    private readonly float* _grbuf;                // [2][576]
    private readonly float* _scf;                  // [40]
    private readonly float* _syn;                  // [18 + 15][2*32]
    private readonly byte* _istPos;                // [2][39]

    private readonly Array[] _owners;              // what keeps the pinned arrays above alive

    public Mp3Decoder()
    {
        _mdctOverlap = Pinned.New<float>(2 * 9 * 32, out var a);
        _qmfState = Pinned.New<float>(15 * 2 * 32, out var b);
        _header = Pinned.New<byte>(HdrSize, out var c);
        _reservBuf = Pinned.New<byte>(MaxBitReservoirBytes, out var d);
        _maindata = Pinned.New<byte>(MaxBitReservoirBytes + MaxL3FramePayloadBytes + 64, out var e);
        _grInfo = Pinned.New<GrInfo>(4, out var f);
        _grbuf = Pinned.New<float>(2 * 576, out var g);
        _scf = Pinned.New<float>(40, out var h);
        _syn = Pinned.New<float>((18 + 15) * 2 * 32, out var i);
        _istPos = Pinned.New<byte>(2 * 39 + 8, out var j);
        _owners = [a, b, c, d, e, f, g, h, i, j];
    }

    /// mp3dec_init, and the memset(dec, 0) a lost sync does: the next frame
    /// starts from nothing.
    public void Reset()
    {
        new Span<float>(_mdctOverlap, 2 * 9 * 32).Clear();
        new Span<float>(_qmfState, 15 * 2 * 32).Clear();
        _reserv = _freeFormatBytes = 0;
        new Span<byte>(_header, HdrSize).Clear();
        new Span<byte>(_reservBuf, MaxBitReservoirBytes).Clear();
    }

    // ------------------------------------------------------------ the header

    public static bool HdrIsMono(byte* h) => (h[3] & 0xC0) == 0xC0;
    private static bool HdrIsMsStereo(byte* h) => (h[3] & 0xE0) == 0x60;
    private static bool HdrIsFreeFormat(byte* h) => (h[2] & 0xF0) == 0;
    private static bool HdrIsCrc(byte* h) => (h[1] & 1) == 0;
    private static bool HdrTestPadding(byte* h) => (h[2] & 0x2) != 0;
    public static bool HdrTestMpeg1(byte* h) => (h[1] & 0x8) != 0;
    private static bool HdrTestNotMpeg25(byte* h) => (h[1] & 0x10) != 0;
    private static bool HdrTestIStereo(byte* h) => (h[3] & 0x10) != 0;
    private static bool HdrTestMsStereo(byte* h) => (h[3] & 0x20) != 0;
    private static int HdrGetStereoMode(byte* h) => (h[3] >> 6) & 3;
    private static int HdrGetStereoModeExt(byte* h) => (h[3] >> 4) & 3;
    public static int HdrGetLayer(byte* h) => (h[1] >> 1) & 3;
    private static int HdrGetBitrate(byte* h) => h[2] >> 4;
    private static int HdrGetSampleRate(byte* h) => (h[2] >> 2) & 3;
    private static int HdrGetMySampleRate(byte* h) => HdrGetSampleRate(h) + (((h[1] >> 3) & 1) + ((h[1] >> 4) & 1)) * 3;
    private static bool HdrIsFrame576(byte* h) => (h[1] & 14) == 2;
    private static bool HdrIsLayer1(byte* h) => (h[1] & 6) == 6;

    private static void BsInit(Bs* bs, byte* data, int bytes)
    {
        bs->Buf = data;
        bs->Pos = 0;
        bs->Limit = bytes * 8;
    }

    private static uint GetBits(Bs* bs, int n)
    {
        uint next, cache = 0, s = (uint)(bs->Pos & 7);
        int shl = n + (int)s;
        byte* p = bs->Buf + (bs->Pos >> 3);
        if ((bs->Pos += n) > bs->Limit)
            return 0;
        next = (uint)(*p++ & (255 >> (int)s));
        while ((shl -= 8) > 0)
        {
            cache |= next << shl;
            next = *p++;
        }
        return cache | (next >> -shl);
    }

    public static bool HdrValid(byte* h) =>
        h[0] == 0xff &&
        ((h[1] & 0xF0) == 0xf0 || (h[1] & 0xFE) == 0xe2) &&
        HdrGetLayer(h) != 0 &&
        HdrGetBitrate(h) != 15 &&
        HdrGetSampleRate(h) != 3;

    public static bool HdrCompare(byte* h1, byte* h2) =>
        HdrValid(h2) &&
        ((h1[1] ^ h2[1]) & 0xFE) == 0 &&
        ((h1[2] ^ h2[2]) & 0x0C) == 0 &&
        !(HdrIsFreeFormat(h1) ^ HdrIsFreeFormat(h2));

    private static readonly byte* HalfRate = Pinned.Of(new byte[]
    {
        0,4,8,12,16,20,24,28,32,40,48,56,64,72,80,  0,4,8,12,16,20,24,28,32,40,48,56,64,72,80,  0,16,24,28,32,40,48,56,64,72,80,88,96,112,128,
        0,16,20,24,28,32,40,48,56,64,80,96,112,128,160,  0,16,24,28,32,40,48,56,64,80,96,112,128,160,192,  0,16,32,48,64,80,96,112,128,144,160,176,192,208,224,
    });

    public static int HdrBitrateKbps(byte* h) =>
        2 * HalfRate[(HdrTestMpeg1(h) ? 1 : 0) * 45 + (HdrGetLayer(h) - 1) * 15 + HdrGetBitrate(h)];

    private static readonly int[] Hz = [44100, 48000, 32000];

    public static int HdrSampleRateHz(byte* h) =>
        Hz[HdrGetSampleRate(h)] >> (HdrTestMpeg1(h) ? 0 : 1) >> (HdrTestNotMpeg25(h) ? 0 : 1);

    public static int HdrFrameSamples(byte* h) => HdrIsLayer1(h) ? 384 : (1152 >> (HdrIsFrame576(h) ? 1 : 0));

    public static int HdrFrameBytes(byte* h, int freeFormatSize)
    {
        int frameBytes = HdrFrameSamples(h) * HdrBitrateKbps(h) * 125 / HdrSampleRateHz(h);
        if (HdrIsLayer1(h))
            frameBytes &= ~3;       // slot align
        return frameBytes != 0 ? frameBytes : freeFormatSize;
    }

    public static int HdrPadding(byte* h) => HdrTestPadding(h) ? (HdrIsLayer1(h) ? 4 : 1) : 0;

    // ------------------------------------------------------------ Layer III

    private static int L3ReadSideInfo(Bs* bs, GrInfo* gr, byte* hdr)
    {
        uint tables, scfsi = 0;
        int mainDataBegin, part23Sum = 0;
        int srIdx = HdrGetMySampleRate(hdr); srIdx -= srIdx != 0 ? 1 : 0;
        int grCount = HdrIsMono(hdr) ? 1 : 2;

        if (HdrTestMpeg1(hdr))
        {
            grCount *= 2;
            mainDataBegin = (int)GetBits(bs, 9);
            scfsi = GetBits(bs, 7 + grCount);
        }
        else
        {
            mainDataBegin = (int)(GetBits(bs, 8 + grCount) >> grCount);
        }

        do
        {
            if (HdrIsMono(hdr))
            {
                scfsi <<= 4;
            }
            gr->Part23Length = (ushort)GetBits(bs, 12);
            part23Sum += gr->Part23Length;
            gr->BigValues = (ushort)GetBits(bs, 9);
            if (gr->BigValues > 288)
            {
                return -1;
            }
            gr->GlobalGain = (byte)GetBits(bs, 8);
            gr->ScalefacCompress = (ushort)GetBits(bs, HdrTestMpeg1(hdr) ? 4 : 9);
            gr->Sfbtab = ScfLong + srIdx * 23;
            gr->NLongSfb = 22;
            gr->NShortSfb = 0;
            if (GetBits(bs, 1) != 0)
            {
                gr->BlockType = (byte)GetBits(bs, 2);
                if (gr->BlockType == 0)
                {
                    return -1;
                }
                gr->MixedBlockFlag = (byte)GetBits(bs, 1);
                gr->RegionCount[0] = 7;
                gr->RegionCount[1] = 255;
                if (gr->BlockType == ShortBlockType)
                {
                    scfsi &= 0x0F0F;
                    if (gr->MixedBlockFlag == 0)
                    {
                        gr->RegionCount[0] = 8;
                        gr->Sfbtab = ScfShort + srIdx * 40;
                        gr->NLongSfb = 0;
                        gr->NShortSfb = 39;
                    }
                    else
                    {
                        gr->Sfbtab = ScfMixed + srIdx * 40;
                        gr->NLongSfb = (byte)(HdrTestMpeg1(hdr) ? 8 : 6);
                        gr->NShortSfb = 30;
                    }
                }
                tables = GetBits(bs, 10);
                tables <<= 5;
                gr->SubblockGain[0] = (byte)GetBits(bs, 3);
                gr->SubblockGain[1] = (byte)GetBits(bs, 3);
                gr->SubblockGain[2] = (byte)GetBits(bs, 3);
            }
            else
            {
                gr->BlockType = 0;
                gr->MixedBlockFlag = 0;
                tables = GetBits(bs, 15);
                gr->RegionCount[0] = (byte)GetBits(bs, 4);
                gr->RegionCount[1] = (byte)GetBits(bs, 3);
                gr->RegionCount[2] = 255;
            }
            gr->TableSelect[0] = (byte)(tables >> 10);
            gr->TableSelect[1] = (byte)((tables >> 5) & 31);
            gr->TableSelect[2] = (byte)(tables & 31);
            gr->Preflag = (byte)(HdrTestMpeg1(hdr) ? GetBits(bs, 1) : (gr->ScalefacCompress >= 500 ? 1u : 0u));
            gr->ScalefacScale = (byte)GetBits(bs, 1);
            gr->Count1Table = (byte)GetBits(bs, 1);
            gr->Scfsi = (byte)((scfsi >> 12) & 15);
            scfsi <<= 4;
            gr++;
        } while (--grCount != 0);

        if (part23Sum + bs->Pos > bs->Limit + mainDataBegin * 8)
        {
            return -1;
        }

        return mainDataBegin;
    }

    private static void L3ReadScalefactors(byte* scf, byte* istPos, byte* scfSize, byte* scfCount, Bs* bitbuf, int scfsi)
    {
        int i, k;
        for (i = 0; i < 4 && scfCount[i] != 0; i++, scfsi *= 2)
        {
            int cnt = scfCount[i];
            if ((scfsi & 8) != 0)
            {
                Buffer.MemoryCopy(istPos, scf, cnt, cnt);
            }
            else
            {
                int bits = scfSize[i];
                if (bits == 0)
                {
                    new Span<byte>(scf, cnt).Clear();
                    new Span<byte>(istPos, cnt).Clear();
                }
                else
                {
                    int maxScf = (scfsi < 0) ? (1 << bits) - 1 : -1;
                    for (k = 0; k < cnt; k++)
                    {
                        int s = (int)GetBits(bitbuf, bits);
                        istPos[k] = (byte)(s == maxScf ? -1 : s);
                        scf[k] = (byte)s;
                    }
                }
            }
            istPos += cnt;
            scf += cnt;
        }
        scf[0] = scf[1] = scf[2] = 0;
    }

    private static float L3LdexpQ2(float y, int expQ2)
    {
        int e;
        do
        {
            e = Math.Min(30 * 4, expQ2);
            y *= ExpFrac[e & 3] * (1 << 30 >> (e >> 2));
        } while ((expQ2 -= e) > 0);
        return y;
    }

    private static void L3DecodeScalefactors(byte* hdr, byte* istPos, Bs* bs, GrInfo* gr, float* scf, int ch)
    {
        byte* scfPartition = ScfPartitions + 28 * ((gr->NShortSfb != 0 ? 1 : 0) + (gr->NLongSfb == 0 ? 1 : 0));
        byte* scfSize = stackalloc byte[4];
        byte* iscf = stackalloc byte[44];       // 40 in C, which the three zeros after the last band can run past
        int i, scfShift = gr->ScalefacScale + 1, gainExp, scfsi = gr->Scfsi;
        float gain;

        if (HdrTestMpeg1(hdr))
        {
            int part = ScfcDecode[gr->ScalefacCompress];
            scfSize[1] = scfSize[0] = (byte)(part >> 2);
            scfSize[3] = scfSize[2] = (byte)(part & 3);
        }
        else
        {
            int k, modprod, sfc, ist = HdrTestIStereo(hdr) && ch != 0 ? 1 : 0;
            sfc = gr->ScalefacCompress >> ist;
            for (k = ist * 3 * 4; sfc >= 0; sfc -= modprod, k += 4)
            {
                for (modprod = 1, i = 3; i >= 0; i--)
                {
                    scfSize[i] = (byte)(sfc / modprod % Mod[k + i]);
                    modprod *= Mod[k + i];
                }
            }
            scfPartition += k;
            scfsi = -16;
        }
        L3ReadScalefactors(iscf, istPos, scfSize, scfPartition, bs, scfsi);

        if (gr->NShortSfb != 0)
        {
            int sh = 3 - scfShift;
            for (i = 0; i < gr->NShortSfb; i += 3)
            {
                iscf[gr->NLongSfb + i + 0] += (byte)(gr->SubblockGain[0] << sh);
                iscf[gr->NLongSfb + i + 1] += (byte)(gr->SubblockGain[1] << sh);
                iscf[gr->NLongSfb + i + 2] += (byte)(gr->SubblockGain[2] << sh);
            }
        }
        else if (gr->Preflag != 0)
        {
            for (i = 0; i < 10; i++)
            {
                iscf[11 + i] += Preamp[i];
            }
        }

        gainExp = gr->GlobalGain + BitsDequantizerOut * 4 - 210 - (HdrIsMsStereo(hdr) ? 2 : 0);
        gain = L3LdexpQ2(1 << (MaxScfi / 4), MaxScfi - gainExp);
        for (i = 0; i < gr->NLongSfb + gr->NShortSfb; i++)
        {
            scf[i] = L3LdexpQ2(gain, iscf[i] << scfShift);
        }
    }

    private static float L3Pow43(int x)
    {
        float frac;
        int sign, mult = 256;

        if (x < 129)
        {
            return Pow43[16 + x];
        }

        if (x < 1024)
        {
            mult = 16;
            x <<= 3;
        }

        sign = 2 * x & 64;
        frac = (float)((x & 63) - sign) / ((x & ~63) + sign);
        return Pow43[16 + ((x + sign) >> 6)] * (1f + frac * ((4f / 3) + frac * (2f / 9))) * mult;
    }

    private static void L3Huffman(float* dst, Bs* bs, GrInfo* grInfo, float* scf, int layer3grLimit)
    {
        // PEEK_BITS(n) is bsCache >> (32 - n); FLUSH_BITS(n) is bsCache <<= n,
        // bsSh += n; CHECK_BITS refills the cache from bsNextPtr.
        float one = 0.0f;
        int ireg = 0, bigValCnt = grInfo->BigValues;
        byte* sfb = grInfo->Sfbtab;
        byte* bsNextPtr = bs->Buf + bs->Pos / 8;
        uint bsCache = (((bsNextPtr[0] * 256u + bsNextPtr[1]) * 256u + bsNextPtr[2]) * 256u + bsNextPtr[3]) << (bs->Pos & 7);
        int pairsToDecode, np, bsSh = (bs->Pos & 7) - 8;
        bsNextPtr += 4;

        while (bigValCnt > 0)
        {
            int tabNum = grInfo->TableSelect[ireg];
            int sfbCnt = grInfo->RegionCount[ireg++];
            short* codebook = Tabs + TabIndex[tabNum];
            int linbits = Linbits[tabNum];
            if (linbits != 0)
            {
                do
                {
                    np = *sfb++ / 2;
                    pairsToDecode = Math.Min(bigValCnt, np);
                    one = *scf++;
                    do
                    {
                        int j, w = 5;
                        int leaf = codebook[bsCache >> (32 - w)];
                        while (leaf < 0)
                        {
                            bsCache <<= w; bsSh += w;
                            w = leaf & 7;
                            leaf = codebook[(int)(bsCache >> (32 - w)) - (leaf >> 3)];
                        }
                        { int n = leaf >> 8; bsCache <<= n; bsSh += n; }

                        for (j = 0; j < 2; j++, dst++, leaf >>= 4)
                        {
                            int lsb = leaf & 0x0F;
                            if (lsb == 15)
                            {
                                lsb += (int)(bsCache >> (32 - linbits));
                                bsCache <<= linbits; bsSh += linbits;
                                while (bsSh >= 0) { bsCache |= (uint)*bsNextPtr++ << bsSh; bsSh -= 8; }
                                *dst = one * L3Pow43(lsb) * ((int)bsCache < 0 ? -1 : 1);
                            }
                            else
                            {
                                *dst = Pow43[16 + lsb - 16 * (int)(bsCache >> 31)] * one;
                            }
                            { int n = lsb != 0 ? 1 : 0; bsCache <<= n; bsSh += n; }
                        }
                        while (bsSh >= 0) { bsCache |= (uint)*bsNextPtr++ << bsSh; bsSh -= 8; }
                    } while (--pairsToDecode != 0);
                } while ((bigValCnt -= np) > 0 && --sfbCnt >= 0);
            }
            else
            {
                do
                {
                    np = *sfb++ / 2;
                    pairsToDecode = Math.Min(bigValCnt, np);
                    one = *scf++;
                    do
                    {
                        int j, w = 5;
                        int leaf = codebook[bsCache >> (32 - w)];
                        while (leaf < 0)
                        {
                            bsCache <<= w; bsSh += w;
                            w = leaf & 7;
                            leaf = codebook[(int)(bsCache >> (32 - w)) - (leaf >> 3)];
                        }
                        { int n = leaf >> 8; bsCache <<= n; bsSh += n; }

                        for (j = 0; j < 2; j++, dst++, leaf >>= 4)
                        {
                            int lsb = leaf & 0x0F;
                            *dst = Pow43[16 + lsb - 16 * (int)(bsCache >> 31)] * one;
                            { int n = lsb != 0 ? 1 : 0; bsCache <<= n; bsSh += n; }
                        }
                        while (bsSh >= 0) { bsCache |= (uint)*bsNextPtr++ << bsSh; bsSh -= 8; }
                    } while (--pairsToDecode != 0);
                } while ((bigValCnt -= np) > 0 && --sfbCnt >= 0);
            }
        }

        for (np = 1 - bigValCnt; ; dst += 4)
        {
            byte* codebookCount1 = grInfo->Count1Table != 0 ? Tab33 : Tab32;
            int leaf = codebookCount1[bsCache >> (32 - 4)];
            if ((leaf & 8) == 0)
            {
                leaf = codebookCount1[(leaf >> 3) + (int)(bsCache << 4 >> (32 - (leaf & 3)))];
            }
            { int n = leaf & 7; bsCache <<= n; bsSh += n; }
            if ((int)((bsNextPtr - bs->Buf) * 8 - 24 + bsSh) > layer3grLimit)
            {
                break;
            }
            if (--np == 0) { np = *sfb++ / 2; if (np == 0) break; one = *scf++; }
            if ((leaf & (128 >> 0)) != 0) { dst[0] = (int)bsCache < 0 ? -one : one; bsCache <<= 1; bsSh += 1; }
            if ((leaf & (128 >> 1)) != 0) { dst[1] = (int)bsCache < 0 ? -one : one; bsCache <<= 1; bsSh += 1; }
            if (--np == 0) { np = *sfb++ / 2; if (np == 0) break; one = *scf++; }
            if ((leaf & (128 >> 2)) != 0) { dst[2] = (int)bsCache < 0 ? -one : one; bsCache <<= 1; bsSh += 1; }
            if ((leaf & (128 >> 3)) != 0) { dst[3] = (int)bsCache < 0 ? -one : one; bsCache <<= 1; bsSh += 1; }
            while (bsSh >= 0) { bsCache |= (uint)*bsNextPtr++ << bsSh; bsSh -= 8; }
        }

        bs->Pos = layer3grLimit;
    }

    private static void L3MidsideStereo(float* left, int n)
    {
        int i = 0;
        float* right = left + 576;
        for (; i < n; i++)
        {
            float a = left[i];
            float b = right[i];
            left[i] = a + b;
            right[i] = a - b;
        }
    }

    private static void L3IntensityStereoBand(float* left, int n, float kl, float kr)
    {
        int i;
        for (i = 0; i < n; i++)
        {
            left[i + 576] = left[i] * kr;
            left[i] = left[i] * kl;
        }
    }

    private static void L3StereoTopBand(float* right, byte* sfb, int nbands, int* maxBand)
    {
        int i, k;

        maxBand[0] = maxBand[1] = maxBand[2] = -1;

        for (i = 0; i < nbands; i++)
        {
            for (k = 0; k < sfb[i]; k += 2)
            {
                if (right[k] != 0 || right[k + 1] != 0)
                {
                    maxBand[i % 3] = i;
                    break;
                }
            }
            right += sfb[i];
        }
    }

    private static void L3StereoProcess(float* left, byte* istPos, byte* sfb, byte* hdr, int* maxBand, int mpeg2Sh)
    {
        uint i, maxPos = HdrTestMpeg1(hdr) ? 7u : 64u;

        for (i = 0; sfb[i] != 0; i++)
        {
            uint ipos = istPos[i];
            if ((int)i > maxBand[i % 3] && ipos < maxPos)
            {
                float kl, kr, s = HdrTestMsStereo(hdr) ? 1.41421356f : 1;
                if (HdrTestMpeg1(hdr))
                {
                    kl = Pan[2 * ipos];
                    kr = Pan[2 * ipos + 1];
                }
                else
                {
                    kl = 1;
                    kr = L3LdexpQ2(1, (int)((ipos + 1) >> 1 << mpeg2Sh));
                    if ((ipos & 1) != 0)
                    {
                        kl = kr;
                        kr = 1;
                    }
                }
                L3IntensityStereoBand(left, sfb[i], kl * s, kr * s);
            }
            else if (HdrTestMsStereo(hdr))
            {
                L3MidsideStereo(left, sfb[i]);
            }
            left += sfb[i];
        }
    }

    private static void L3IntensityStereo(float* left, byte* istPos, GrInfo* gr, byte* hdr)
    {
        int* maxBand = stackalloc int[3];
        int nSfb = gr->NLongSfb + gr->NShortSfb;
        int i, maxBlocks = gr->NShortSfb != 0 ? 3 : 1;

        L3StereoTopBand(left + 576, gr->Sfbtab, nSfb, maxBand);
        if (gr->NLongSfb != 0)
        {
            maxBand[0] = maxBand[1] = maxBand[2] = Math.Max(Math.Max(maxBand[0], maxBand[1]), maxBand[2]);
        }
        for (i = 0; i < maxBlocks; i++)
        {
            int defaultPos = HdrTestMpeg1(hdr) ? 3 : 0;
            int itop = nSfb - maxBlocks + i;
            int prev = itop - maxBlocks;
            istPos[itop] = (byte)(maxBand[i] >= prev ? defaultPos : istPos[prev]);
        }
        L3StereoProcess(left, istPos, gr->Sfbtab, hdr, maxBand, gr[1].ScalefacCompress & 1);
    }

    private static void L3Reorder(float* grbuf, float* scratch, byte* sfb)
    {
        int i, len;
        float* src = grbuf, dst = scratch;

        for (; 0 != (len = *sfb); sfb += 3, src += 2 * len)
        {
            for (i = 0; i < len; i++, src++)
            {
                *dst++ = src[0 * len];
                *dst++ = src[1 * len];
                *dst++ = src[2 * len];
            }
        }
        Buffer.MemoryCopy(scratch, grbuf, (dst - scratch) * sizeof(float), (dst - scratch) * sizeof(float));
    }

    private static void L3Antialias(float* grbuf, int nbands)
    {
        for (; nbands > 0; nbands--, grbuf += 18)
        {
            int i = 0;
            for (; i < 8; i++)
            {
                float u = grbuf[18 + i];
                float d = grbuf[17 - i];
                grbuf[18 + i] = u * Aa[0 * 8 + i] - d * Aa[1 * 8 + i];
                grbuf[17 - i] = u * Aa[1 * 8 + i] + d * Aa[0 * 8 + i];
            }
        }
    }

    private static void L3Dct3_9(float* y)
    {
        float s0, s1, s2, s3, s4, s5, s6, s7, s8, t0, t2, t4;

        s0 = y[0]; s2 = y[2]; s4 = y[4]; s6 = y[6]; s8 = y[8];
        t0 = s0 + s6 * 0.5f;
        s0 -= s6;
        t4 = (s4 + s2) * 0.93969262f;
        t2 = (s8 + s2) * 0.76604444f;
        s6 = (s4 - s8) * 0.17364818f;
        s4 += s8 - s2;

        s2 = s0 - s4 * 0.5f;
        y[4] = s4 + s0;
        s8 = t0 - t2 + s6;
        s0 = t0 - t4 + t2;
        s4 = t0 + t4 - s6;

        s1 = y[1]; s3 = y[3]; s5 = y[5]; s7 = y[7];

        s3 *= 0.86602540f;
        t0 = (s5 + s1) * 0.98480775f;
        t4 = (s5 - s7) * 0.34202014f;
        t2 = (s1 + s7) * 0.64278761f;
        s1 = (s1 - s5 - s7) * 0.86602540f;

        s5 = t0 - s3 - t2;
        s7 = t4 - s3 - t0;
        s3 = t4 + s3 - t2;

        y[0] = s4 - s7;
        y[1] = s2 + s1;
        y[2] = s0 - s3;
        y[3] = s8 + s5;
        y[5] = s8 - s5;
        y[6] = s0 + s3;
        y[7] = s2 - s1;
        y[8] = s4 + s7;
    }

    private static void L3Imdct36(float* grbuf, float* overlap, float* window, int nbands)
    {
        int i, j;
        float* co = stackalloc float[9];
        float* si = stackalloc float[9];

        for (j = 0; j < nbands; j++, grbuf += 18, overlap += 9)
        {
            co[0] = -grbuf[0];
            si[0] = grbuf[17];
            for (i = 0; i < 4; i++)
            {
                si[8 - 2 * i] = grbuf[4 * i + 1] - grbuf[4 * i + 2];
                co[1 + 2 * i] = grbuf[4 * i + 1] + grbuf[4 * i + 2];
                si[7 - 2 * i] = grbuf[4 * i + 4] - grbuf[4 * i + 3];
                co[2 + 2 * i] = -(grbuf[4 * i + 3] + grbuf[4 * i + 4]);
            }
            L3Dct3_9(co);
            L3Dct3_9(si);

            si[1] = -si[1];
            si[3] = -si[3];
            si[5] = -si[5];
            si[7] = -si[7];

            for (i = 0; i < 9; i++)
            {
                float ovl = overlap[i];
                float sum = co[i] * Twid9[9 + i] + si[i] * Twid9[0 + i];
                overlap[i] = co[i] * Twid9[0 + i] - si[i] * Twid9[9 + i];
                grbuf[i] = ovl * window[0 + i] - sum * window[9 + i];
                grbuf[17 - i] = ovl * window[9 + i] + sum * window[0 + i];
            }
        }
    }

    private static void L3Idct3(float x0, float x1, float x2, float* dst)
    {
        float m1 = x1 * 0.86602540f;
        float a1 = x0 - x2 * 0.5f;
        dst[1] = x0 + x2;
        dst[0] = a1 + m1;
        dst[2] = a1 - m1;
    }

    private static void L3Imdct12(float* x, float* dst, float* overlap)
    {
        float* co = stackalloc float[3];
        float* si = stackalloc float[3];
        int i;

        L3Idct3(-x[0], x[6] + x[3], x[12] + x[9], co);
        L3Idct3(x[15], x[12] - x[9], x[6] - x[3], si);
        si[1] = -si[1];

        for (i = 0; i < 3; i++)
        {
            float ovl = overlap[i];
            float sum = co[i] * Twid3[3 + i] + si[i] * Twid3[0 + i];
            overlap[i] = co[i] * Twid3[0 + i] - si[i] * Twid3[3 + i];
            dst[i] = ovl * Twid3[2 - i] - sum * Twid3[5 - i];
            dst[5 - i] = ovl * Twid3[5 - i] + sum * Twid3[2 - i];
        }
    }

    private static void L3ImdctShort(float* grbuf, float* overlap, int nbands)
    {
        float* tmp = stackalloc float[18];
        for (; nbands > 0; nbands--, overlap += 9, grbuf += 18)
        {
            Buffer.MemoryCopy(grbuf, tmp, 18 * sizeof(float), 18 * sizeof(float));
            Buffer.MemoryCopy(overlap, grbuf, 6 * sizeof(float), 6 * sizeof(float));
            L3Imdct12(tmp, grbuf + 6, overlap + 6);
            L3Imdct12(tmp + 1, grbuf + 12, overlap + 6);
            L3Imdct12(tmp + 2, overlap, overlap + 6);
        }
    }

    private static void L3ChangeSign(float* grbuf)
    {
        int b, i;
        for (b = 0, grbuf += 18; b < 32; b += 2, grbuf += 36)
            for (i = 1; i < 18; i += 2)
                grbuf[i] = -grbuf[i];
    }

    private static void L3ImdctGr(float* grbuf, float* overlap, uint blockType, uint nLongBands)
    {
        if (nLongBands != 0)
        {
            L3Imdct36(grbuf, overlap, MdctWindow, (int)nLongBands);
            grbuf += 18 * nLongBands;
            overlap += 9 * nLongBands;
        }
        if (blockType == ShortBlockType)
            L3ImdctShort(grbuf, overlap, 32 - (int)nLongBands);
        else
            L3Imdct36(grbuf, overlap, MdctWindow + (blockType == StopBlockType ? 18 : 0), 32 - (int)nLongBands);
    }

    private void L3SaveReservoir()
    {
        int pos = (int)((uint)(_bs.Pos + 7) / 8u);
        int remains = (int)((uint)_bs.Limit / 8u) - pos;
        if (remains > MaxBitReservoirBytes)
        {
            pos += remains - MaxBitReservoirBytes;
            remains = MaxBitReservoirBytes;
        }
        if (remains > 0)
        {
            Buffer.MemoryCopy(_maindata + pos, _reservBuf, MaxBitReservoirBytes, remains);
        }
        _reserv = remains;
    }

    private bool L3RestoreReservoir(Bs* bs, int mainDataBegin)
    {
        int frameBytes = (bs->Limit - bs->Pos) / 8;
        int bytesHave = Math.Min(_reserv, mainDataBegin);
        Buffer.MemoryCopy(_reservBuf + Math.Max(0, _reserv - mainDataBegin), _maindata, bytesHave, bytesHave);
        Buffer.MemoryCopy(bs->Buf + bs->Pos / 8, _maindata + bytesHave, frameBytes, frameBytes);
        fixed (Bs* s = &_bs) BsInit(s, _maindata, bytesHave + frameBytes);
        return _reserv >= mainDataBegin;
    }

    private void L3Decode(GrInfo* grInfo, int nch)
    {
        int ch;

        fixed (Bs* bs = &_bs)
        {
            for (ch = 0; ch < nch; ch++)
            {
                int layer3grLimit = bs->Pos + grInfo[ch].Part23Length;
                L3DecodeScalefactors(_header, _istPos + 39 * ch, bs, grInfo + ch, _scf, ch);
                L3Huffman(_grbuf + 576 * ch, bs, grInfo + ch, _scf, layer3grLimit);
            }
        }

        if (HdrTestIStereo(_header))
        {
            L3IntensityStereo(_grbuf, _istPos + 39, grInfo, _header);
        }
        else if (HdrIsMsStereo(_header))
        {
            L3MidsideStereo(_grbuf, 576);
        }

        for (ch = 0; ch < nch; ch++, grInfo++)
        {
            int aaBands = 31;
            int nLongBands = (grInfo->MixedBlockFlag != 0 ? 2 : 0) << (HdrGetMySampleRate(_header) == 2 ? 1 : 0);

            if (grInfo->NShortSfb != 0)
            {
                aaBands = nLongBands - 1;
                L3Reorder(_grbuf + 576 * ch + nLongBands * 18, _syn, grInfo->Sfbtab + grInfo->NLongSfb);
            }

            L3Antialias(_grbuf + 576 * ch, aaBands);
            L3ImdctGr(_grbuf + 576 * ch, _mdctOverlap + 9 * 32 * ch, grInfo->BlockType, (uint)nLongBands);
            L3ChangeSign(_grbuf + 576 * ch);
        }
    }

    // ------------------------------------------------------------ synthesis

    private static void Mp3dDctII(float* grbuf, int n)
    {
        int i, k = 0;
        float* t = stackalloc float[4 * 8];
        for (; k < n; k++)
        {
            float* x, y = grbuf + k;

            for (x = t, i = 0; i < 8; i++, x++)
            {
                float x0 = y[i * 18];
                float x1 = y[(15 - i) * 18];
                float x2 = y[(16 + i) * 18];
                float x3 = y[(31 - i) * 18];
                float t0 = x0 + x3;
                float t1 = x1 + x2;
                float t2 = (x1 - x2) * Sec[3 * i + 0];
                float t3 = (x0 - x3) * Sec[3 * i + 1];
                x[0] = t0 + t1;
                x[8] = (t0 - t1) * Sec[3 * i + 2];
                x[16] = t3 + t2;
                x[24] = (t3 - t2) * Sec[3 * i + 2];
            }
            for (x = t, i = 0; i < 4; i++, x += 8)
            {
                float x0 = x[0], x1 = x[1], x2 = x[2], x3 = x[3], x4 = x[4], x5 = x[5], x6 = x[6], x7 = x[7], xt;
                xt = x0 - x7; x0 += x7;
                x7 = x1 - x6; x1 += x6;
                x6 = x2 - x5; x2 += x5;
                x5 = x3 - x4; x3 += x4;
                x4 = x0 - x3; x0 += x3;
                x3 = x1 - x2; x1 += x2;
                x[0] = x0 + x1;
                x[4] = (x0 - x1) * 0.70710677f;
                x5 = x5 + x6;
                x6 = (x6 + x7) * 0.70710677f;
                x7 = x7 + xt;
                x3 = (x3 + x4) * 0.70710677f;
                x5 -= x7 * 0.198912367f;    // rotate by PI/8
                x7 += x5 * 0.382683432f;
                x5 -= x7 * 0.198912367f;
                x0 = xt - x6; xt += x6;
                x[1] = (xt + x7) * 0.50979561f;
                x[2] = (x4 + x3) * 0.54119611f;
                x[3] = (x0 - x5) * 0.60134488f;
                x[5] = (x0 + x5) * 0.89997619f;
                x[6] = (x4 - x3) * 1.30656302f;
                x[7] = (xt - x7) * 2.56291556f;
            }
            for (i = 0; i < 7; i++, y += 4 * 18)
            {
                y[0 * 18] = t[0 * 8 + i];
                y[1 * 18] = t[2 * 8 + i] + t[3 * 8 + i] + t[3 * 8 + i + 1];
                y[2 * 18] = t[1 * 8 + i] + t[1 * 8 + i + 1];
                y[3 * 18] = t[2 * 8 + i + 1] + t[3 * 8 + i] + t[3 * 8 + i + 1];
            }
            y[0 * 18] = t[0 * 8 + 7];
            y[1 * 18] = t[2 * 8 + 7] + t[3 * 8 + 7];
            y[2 * 18] = t[1 * 8 + 7];
            y[3 * 18] = t[3 * 8 + 7];
        }
    }

    private static float Mp3dScalePcm(float sample) => sample * (1f / 32768f);

    private static void Mp3dSynthPair(float* pcm, int nch, float* z)
    {
        float a;
        a = (z[14 * 64] - z[0]) * 29;
        a += (z[1 * 64] + z[13 * 64]) * 213;
        a += (z[12 * 64] - z[2 * 64]) * 459;
        a += (z[3 * 64] + z[11 * 64]) * 2037;
        a += (z[10 * 64] - z[4 * 64]) * 5153;
        a += (z[5 * 64] + z[9 * 64]) * 6574;
        a += (z[8 * 64] - z[6 * 64]) * 37489;
        a += z[7 * 64] * 75038;
        pcm[0] = Mp3dScalePcm(a);

        z += 2;
        a = z[14 * 64] * 104;
        a += z[12 * 64] * 1567;
        a += z[10 * 64] * 9727;
        a += z[8 * 64] * 64019;
        a += z[6 * 64] * -9975;
        a += z[4 * 64] * -45;
        a += z[2 * 64] * 146;
        a += z[0 * 64] * -5;
        pcm[16 * nch] = Mp3dScalePcm(a);
    }

    private static void Mp3dSynth(float* xl, float* dstl, int nch, float* lins)
    {
        int i;
        float* xr = xl + 576 * (nch - 1);
        float* dstr = dstl + (nch - 1);
        float* zlin = lins + 15 * 64;
        float* w = Win;
        float* a = stackalloc float[4];
        float* b = stackalloc float[4];

        zlin[4 * 15] = xl[18 * 16];
        zlin[4 * 15 + 1] = xr[18 * 16];
        zlin[4 * 15 + 2] = xl[0];
        zlin[4 * 15 + 3] = xr[0];

        zlin[4 * 31] = xl[1 + 18 * 16];
        zlin[4 * 31 + 1] = xr[1 + 18 * 16];
        zlin[4 * 31 + 2] = xl[1];
        zlin[4 * 31 + 3] = xr[1];

        Mp3dSynthPair(dstr, nch, lins + 4 * 15 + 1);
        Mp3dSynthPair(dstr + 32 * nch, nch, lins + 4 * 15 + 64 + 1);
        Mp3dSynthPair(dstl, nch, lins + 4 * 15);
        Mp3dSynthPair(dstl + 32 * nch, nch, lins + 4 * 15 + 64);

        for (i = 14; i >= 0; i--)
        {
            zlin[4 * i] = xl[18 * (31 - i)];
            zlin[4 * i + 1] = xr[18 * (31 - i)];
            zlin[4 * i + 2] = xl[1 + 18 * (31 - i)];
            zlin[4 * i + 3] = xr[1 + 18 * (31 - i)];
            zlin[4 * (i + 16)] = xl[1 + 18 * (1 + i)];
            zlin[4 * (i + 16) + 1] = xr[1 + 18 * (1 + i)];
            zlin[4 * (i - 16) + 2] = xl[18 * (1 + i)];
            zlin[4 * (i - 16) + 3] = xr[18 * (1 + i)];

            // S0(0) S2(1) S1(2) S2(3) S1(4) S2(5) S1(6) S2(7)
            for (int k = 0; k < 8; k++)
            {
                float w0 = *w++;
                float w1 = *w++;
                float* vz = &zlin[4 * i - k * 64];
                float* vy = &zlin[4 * i - (15 - k) * 64];
                if (k == 0)
                    for (int j = 0; j < 4; j++) { b[j] = vz[j] * w1 + vy[j] * w0; a[j] = vz[j] * w0 - vy[j] * w1; }
                else if ((k & 1) != 0)
                    for (int j = 0; j < 4; j++) { b[j] += vz[j] * w1 + vy[j] * w0; a[j] += vy[j] * w1 - vz[j] * w0; }
                else
                    for (int j = 0; j < 4; j++) { b[j] += vz[j] * w1 + vy[j] * w0; a[j] += vz[j] * w0 - vy[j] * w1; }
            }

            dstr[(15 - i) * nch] = Mp3dScalePcm(a[1]);
            dstr[(17 + i) * nch] = Mp3dScalePcm(b[1]);
            dstl[(15 - i) * nch] = Mp3dScalePcm(a[0]);
            dstl[(17 + i) * nch] = Mp3dScalePcm(b[0]);
            dstr[(47 - i) * nch] = Mp3dScalePcm(a[3]);
            dstr[(49 + i) * nch] = Mp3dScalePcm(b[3]);
            dstl[(47 - i) * nch] = Mp3dScalePcm(a[2]);
            dstl[(49 + i) * nch] = Mp3dScalePcm(b[2]);
        }
    }

    private static void Mp3dSynthGranule(float* qmfState, float* grbuf, int nbands, int nch, float* pcm, float* lins)
    {
        int i;
        for (i = 0; i < nch; i++)
        {
            Mp3dDctII(grbuf + 576 * i, nbands);
        }

        Buffer.MemoryCopy(qmfState, lins, 15 * 64 * sizeof(float), 15 * 64 * sizeof(float));

        for (i = 0; i < nbands; i += 2)
        {
            Mp3dSynth(grbuf + i, pcm + 32 * nch * i, nch, lins + i * 64);
        }
        if (nch == 1)
        {
            for (i = 0; i < 15 * 64; i += 2)
            {
                qmfState[i] = lins[nbands * 64 + i];
            }
        }
        else
        {
            Buffer.MemoryCopy(lins + nbands * 64, qmfState, 15 * 64 * sizeof(float), 15 * 64 * sizeof(float));
        }
    }

    // ------------------------------------------------------------ framing

    private static bool Mp3dMatchFrame(byte* hdr, int mp3Bytes, int frameBytes)
    {
        int i, nmatch;
        for (i = 0, nmatch = 0; nmatch < MaxFrameSyncMatches; nmatch++)
        {
            i += HdrFrameBytes(hdr + i, frameBytes) + HdrPadding(hdr + i);
            if (i + HdrSize > mp3Bytes)
                return nmatch > 0;
            if (!HdrCompare(hdr, hdr + i))
                return false;
        }
        return true;
    }

    /// The first frame at or after `mp3` that the frames after it agree with.
    /// Returns its offset, or mp3Bytes (and frameBytes 0) for none.
    public static int Mp3dFindFrame(byte* mp3, int mp3Bytes, ref int freeFormatBytes, out int frameBytesOut)
    {
        int i, k;
        for (i = 0; i < mp3Bytes - HdrSize; i++, mp3++)
        {
            if (HdrValid(mp3))
            {
                int frameBytes = HdrFrameBytes(mp3, freeFormatBytes);
                int frameAndPadding = frameBytes + HdrPadding(mp3);

                for (k = HdrSize; frameBytes == 0 && k < MaxFreeFormatFrameSize && i + 2 * k < mp3Bytes - HdrSize; k++)
                {
                    if (HdrCompare(mp3, mp3 + k))
                    {
                        int fb = k - HdrPadding(mp3);
                        int nextfb = fb + HdrPadding(mp3 + k);
                        if (i + k + nextfb + HdrSize > mp3Bytes || !HdrCompare(mp3, mp3 + k + nextfb))
                            continue;
                        frameAndPadding = k;
                        frameBytes = fb;
                        freeFormatBytes = fb;
                    }
                }
                if ((frameBytes != 0 && i + frameAndPadding <= mp3Bytes &&
                    Mp3dMatchFrame(mp3, mp3Bytes - i, frameBytes)) ||
                    (i == 0 && frameAndPadding == mp3Bytes))
                {
                    frameBytesOut = frameAndPadding;
                    return i;
                }
                freeFormatBytes = 0;
            }
        }
        frameBytesOut = 0;
        return mp3Bytes;
    }

    /// mp3dec_decode_frame: the next frame from `mp3`, into `pcm` (room for
    /// MaxSamplesPerFrame floats, interleaved), or with `pcm` null only the
    /// frame found and described. Returns the samples per channel decoded:
    /// 0 when no frame was found (info.FrameBytes says how much to skip), or
    /// when one was but could not be decoded (its reservoir is from before a
    /// seek, or it is not Layer III).
    public int DecodeFrame(byte* mp3, int mp3Bytes, float* pcm, out FrameInfo info)
    {
        info = default;
        int i = 0, igr, frameSize = 0;
        bool success = true;
        byte* hdr;
        Bs bsFrame;

        if (mp3Bytes > 4 && _header[0] == 0xff && HdrCompare(_header, mp3))
        {
            frameSize = HdrFrameBytes(mp3, _freeFormatBytes) + HdrPadding(mp3);
            if (frameSize != mp3Bytes && (frameSize + HdrSize > mp3Bytes || !HdrCompare(mp3, mp3 + frameSize)))
            {
                frameSize = 0;
            }
        }
        if (frameSize == 0)
        {
            Reset();
            i = Mp3dFindFrame(mp3, mp3Bytes, ref _freeFormatBytes, out frameSize);
            if (frameSize == 0 || i + frameSize > mp3Bytes)
            {
                info.FrameBytes = i;
                return 0;
            }
        }

        hdr = mp3 + i;
        Buffer.MemoryCopy(hdr, _header, HdrSize, HdrSize);
        info.FrameBytes = i + frameSize;
        info.FrameOffset = i;
        info.Channels = HdrIsMono(hdr) ? 1 : 2;
        info.Hz = HdrSampleRateHz(hdr);
        info.Layer = 4 - HdrGetLayer(hdr);
        info.BitrateKbps = HdrBitrateKbps(hdr);

        if (pcm == null)
        {
            return HdrFrameSamples(hdr);
        }

        BsInit(&bsFrame, hdr + HdrSize, frameSize - HdrSize);
        if (HdrIsCrc(hdr))
        {
            GetBits(&bsFrame, 16);
        }

        if (info.Layer != 3)
        {
            return 0;       // Layers I and II are not ported
        }

        int mainDataBegin = L3ReadSideInfo(&bsFrame, _grInfo, hdr);
        if (mainDataBegin < 0 || bsFrame.Pos > bsFrame.Limit)
        {
            Reset();
            return 0;
        }
        success = L3RestoreReservoir(&bsFrame, mainDataBegin);
        if (success)
        {
            for (igr = 0; igr < (HdrTestMpeg1(hdr) ? 2 : 1); igr++, pcm += 576 * info.Channels)
            {
                new Span<float>(_grbuf, 576 * 2).Clear();
                L3Decode(_grInfo + igr * info.Channels, info.Channels);
                Mp3dSynthGranule(_qmfState, _grbuf, 18, info.Channels, pcm, _syn);
            }
        }
        L3SaveReservoir();
        return success ? HdrFrameSamples(_header) : 0;
    }
}
