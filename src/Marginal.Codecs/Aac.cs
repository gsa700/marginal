// COPIED from AlbumWall (src/AlbumWall.Codecs/Aac.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — AAC-LC decoded in C#: the codec core.
//
// PORTED FROM FAAD2 (github.com/knik0/faad2, libfaad at 521d105), GPL v2 or
// later, as AlbumWall is GPL v3: his call on 2026-10-22, the top three formats
// get native C# decoders so that mpv can go. Code from FAAD2 is copyright (c)
// Nero AG, www.nero.com. Its LC path only, function by function, the C names
// kept in PascalCase: syntax.c (the bitstream), huffman.c, specrec.c (inverse
// quantization), pns.c, ms.c, is.c, pulse.c, tns.c, filtbank.c and mdct.c, in
// the floating-point build. Left out: SBR and PS (HE-AAC), MAIN prediction,
// LTP, SSR, the error-resilient and DRM profiles, DRC, and coupling channels:
// none is in an iTunes AAC-LC file, and a stream that needs one is refused. The
// FFT is a plain radix-2 one in place of FAAD2's FFTPACK, the same transform
// with the same sign. The tables are in AacTables.cs, generated from FAAD2's.
//
// Unlike FAAD2, the first frame is put out, not dropped: FFmpeg puts it out,
// and the container's priming (Mp4Track) is counted from there, so the same
// samples are cut either way.

namespace Marginal.Codecs;

internal sealed class AacDecoder
{
    public const int FrameLength = 1024;

    private const int OnlyLongSequence = 0, LongStartSequence = 1, EightShortSequence = 2, LongStopSequence = 3;
    private const int ZeroHcb = 0, FirstPairHcb = 5, EscHcb = 11, NoiseHcb = 13, IntensityHcb2 = 14, IntensityHcb = 15;
    private const int IdSce = 0, IdCpe = 1, IdCce = 2, IdLfe = 3, IdDse = 4, IdPce = 5, IdFil = 6, IdEnd = 7;
    private const int MaxSfb = 51, TnsMaxOrder = 20;

    public int SampleRate { get; }
    public int Channels { get; }
    private readonly int _sfIndex;

    // Per channel, from frame to frame: the second half of the last
    // transform, its window's shape, and PNS's random generator.
    private readonly float[][] _overlap;
    private readonly int[] _windowShapePrev;
    private uint _r1 = 0x2bb431ea, _r2 = 0x206155b7;

    private readonly Ics _ics1 = new(), _ics2 = new();
    private readonly short[] _specData1 = new short[1024], _specData2 = new short[1024];
    private readonly float[] _specCoef1 = new float[1024], _specCoef2 = new float[1024];
    private readonly float[][] _timeOut;
    private readonly float[] _transfBuf = new float[2 * 1024];
    private readonly Mdct _mdct2048 = new(2048), _mdct256 = new(256);

    private static readonly int[] SampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
    private static readonly byte[] NumSwb1024Window = [41, 41, 47, 49, 49, 51, 47, 47, 43, 43, 43, 40];
    private static readonly byte[] NumSwb128Window = [12, 12, 12, 14, 14, 14, 15, 15, 15, 15, 15, 15];
    private static readonly ushort[][] SwbOffset1024Window =
    [
        AacTables.SwbOffset1024_96, AacTables.SwbOffset1024_96, AacTables.SwbOffset1024_64, AacTables.SwbOffset1024_48,
        AacTables.SwbOffset1024_48, AacTables.SwbOffset1024_32, AacTables.SwbOffset1024_24, AacTables.SwbOffset1024_24,
        AacTables.SwbOffset1024_16, AacTables.SwbOffset1024_16, AacTables.SwbOffset1024_16, AacTables.SwbOffset1024_8,
    ];
    private static readonly ushort[][] SwbOffset128Window =
    [
        AacTables.SwbOffset128_96, AacTables.SwbOffset128_96, AacTables.SwbOffset128_64, AacTables.SwbOffset128_48,
        AacTables.SwbOffset128_48, AacTables.SwbOffset128_48, AacTables.SwbOffset128_24, AacTables.SwbOffset128_24,
        AacTables.SwbOffset128_16, AacTables.SwbOffset128_16, AacTables.SwbOffset128_16, AacTables.SwbOffset128_8,
    ];

    private AacDecoder(int sfIndex, int channels)
    {
        _sfIndex = sfIndex;
        SampleRate = SampleRates[sfIndex];
        Channels = channels;
        _overlap = new float[channels][];
        _timeOut = new float[channels][];
        for (var c = 0; c < channels; c++)
        {
            _overlap[c] = new float[1024];
            _timeOut[c] = new float[1024];
        }
        _windowShapePrev = new int[channels];
    }

    /// From an AudioSpecificConfig (ISO 14496-3 1.6.2.1), as an MP4's esds
    /// carries it. Only AAC-LC, mono or stereo, 1024-sample frames.
    public static AacDecoder FromConfig(ReadOnlySpan<byte> asc)
    {
        var ld = new BitReader(asc.ToArray(), 0, asc.Length);
        int objectType = (int)ld.GetBits(5);
        if (objectType == 31) objectType = 32 + (int)ld.GetBits(6);
        int sfIndex = (int)ld.GetBits(4);
        if (sfIndex == 15) throw new UnsupportedAudioException("An explicit AAC sample rate is not supported.");
        int channelConfiguration = (int)ld.GetBits(4);
        if (objectType is 5 or 29) throw new UnsupportedAudioException("HE-AAC (SBR) is not supported.");
        if (objectType != 2) throw new UnsupportedAudioException($"AAC object type {objectType} is not supported, only LC (2).");
        if (sfIndex >= 12) throw new FlacDecodeException("AAC sample rate index out of range.");
        // GASpecificConfig
        if (ld.Get1Bit() != 0) throw new UnsupportedAudioException("960-sample AAC frames are not supported.");
        if (ld.Get1Bit() != 0) ld.GetBits(14);         // dependsOnCoreCoder: coreCoderDelay
        ld.Get1Bit();                                    // extensionFlag, 0 for LC
        if (channelConfiguration is not (1 or 2))
            throw new UnsupportedAudioException($"AAC channel configuration {channelConfiguration} is not supported.");
        // A backward-compatible SBR signal after the config (sync word 0x2b7):
        // an HE-AAC stream that would play at twice the rate.
        if (ld.BitsLeft >= 16 && ld.ShowBits(11) == 0x2b7)
        {
            ld.GetBits(11);
            var ext = (int)ld.GetBits(5);
            if (ext == 5 && ld.Get1Bit() != 0) throw new UnsupportedAudioException("HE-AAC (SBR) is not supported.");
        }
        return new AacDecoder(sfIndex, channelConfiguration);
    }

    /// Forgets the past (the overlap and window shapes), for a seek: the
    /// frame after the next one decoded is then exact.
    public void Reset()
    {
        foreach (var o in _overlap) Array.Clear(o);
        Array.Clear(_windowShapePrev);
    }

    /// One raw_data_block (an MP4 sample) into `pcm`: 1024 frames,
    /// interleaved, as floats from -1 to 1.
    public void DecodeFrame(byte[] data, int offset, int length, Span<float> pcm)
    {
        // As ALAC: the spectral and Huffman code is a port, and one check it
        // misses on a damaged frame must fail that track as a decode error, not
        // escape as IndexOutOfRange (security review 2026-10-26).
        try
        {
            Decode(data, offset, length, pcm);
        }
        catch (IndexOutOfRangeException)
        {
            throw new FlacDecodeException("A damaged AAC frame: it reads past its end.");
        }
    }

    private void Decode(byte[] data, int offset, int length, Span<float> pcm)
    {
        var ld = new BitReader(data, offset, length);
        var channels = 0;
        int id;
        while ((id = (int)ld.GetBits(3)) != IdEnd)
        {
            switch (id)
            {
                case IdSce:
                case IdLfe:
                    if (channels + 1 > Channels) throw new FlacDecodeException("More AAC channels than configured.");
                    SingleLfeChannelElement(ld, channels);
                    channels += 1;
                    break;
                case IdCpe:
                    if (channels + 2 > Channels) throw new FlacDecodeException("More AAC channels than configured.");
                    ChannelPairElement(ld, channels);
                    channels += 2;
                    break;
                case IdCce:
                    throw new UnsupportedAudioException("AAC coupling channels are not supported.");
                case IdDse:
                    DataStreamElement(ld);
                    break;
                case IdPce:
                    ProgramConfigElement(ld);
                    break;
                case IdFil:
                    FillElement(ld);
                    break;
            }
            if (ld.Error) throw new FlacDecodeException("An AAC frame ends early.");
        }
        if (channels != Channels) throw new FlacDecodeException("An AAC frame without all its channels.");

        const float scale = 1f / 32768;     // FAAD2's floats are on the 16-bit scale
        for (var c = 0; c < Channels; c++)
        {
            var t = _timeOut[c];
            for (var i = 0; i < FrameLength; i++) pcm[i * Channels + c] = t[i] * scale;
        }
    }

    // ------------------------------------------------------------ elements

    private void SingleLfeChannelElement(BitReader ld, int channel)
    {
        ld.GetBits(4);                      // element_instance_tag
        var ics = _ics1;
        ics.Reset();
        Array.Clear(_specData1);
        IndividualChannelStream(ld, ics, commonWindow: false, _specData1);
        if (ics.IsUsed) throw new FlacDecodeException("Intensity stereo in a single channel.");
        ReconstructSingleChannel(ics, channel, _specData1);
    }

    private void ChannelPairElement(BitReader ld, int channel)
    {
        Array.Clear(_specData1);
        Array.Clear(_specData2);
        var ics1 = _ics1;
        var ics2 = _ics2;
        ics1.Reset();
        ics2.Reset();
        ld.GetBits(4);                      // element_instance_tag
        var commonWindow = ld.Get1Bit() != 0;
        if (commonWindow)
        {
            IcsInfo(ld, ics1);
            ics1.MsMaskPresent = (int)ld.GetBits(2);
            if (ics1.MsMaskPresent == 3) throw new FlacDecodeException("Bad AAC M/S mask.");
            if (ics1.MsMaskPresent == 1)
                for (var g = 0; g < ics1.NumWindowGroups; g++)
                    for (var sfb = 0; sfb < ics1.MaxSfb; sfb++)
                        ics1.MsUsed[g, sfb] = (byte)ld.Get1Bit();
            ics2.CopyFrom(ics1);
        }
        else
        {
            ics1.MsMaskPresent = 0;
        }
        IndividualChannelStream(ld, ics1, commonWindow, _specData1);
        IndividualChannelStream(ld, ics2, commonWindow, _specData2);
        ReconstructChannelPair(ics1, ics2, channel, _specData1, _specData2);
    }

    private static void DataStreamElement(BitReader ld)
    {
        ld.GetBits(4);                      // element_instance_tag
        var byteAligned = ld.Get1Bit() != 0;
        var count = (int)ld.GetBits(8);
        if (count == 255) count += (int)ld.GetBits(8);
        if (byteAligned) ld.ByteAlign();
        ld.Skip(count * 8);
    }

    /// Read and passed over (14496-4 5.6.4.1.2.1.3: a PCE in an access unit is ignored).
    private static void ProgramConfigElement(BitReader ld)
    {
        ld.GetBits(4 + 2 + 4);              // tag, object type, sf index
        int front = (int)ld.GetBits(4), side = (int)ld.GetBits(4), back = (int)ld.GetBits(4);
        int lfe = (int)ld.GetBits(2), assoc = (int)ld.GetBits(3), cc = (int)ld.GetBits(4);
        if (ld.Get1Bit() != 0) ld.GetBits(4);
        if (ld.Get1Bit() != 0) ld.GetBits(4);
        if (ld.Get1Bit() != 0) ld.GetBits(3);
        ld.Skip((front + side + back) * 5 + lfe * 4 + assoc * 4 + cc * 5);
        ld.ByteAlign();
        ld.Skip((int)ld.GetBits(8) * 8);   // comment
    }

    /// Passed over whole: DRC is not applied, and SBR data here is refused.
    private static void FillElement(BitReader ld)
    {
        var count = (int)ld.GetBits(4);
        if (count == 15) count += (int)ld.GetBits(8) - 1;
        if (count > 0 && ld.ShowBits(4) is 13 or 14) throw new UnsupportedAudioException("HE-AAC (SBR) is not supported.");
        ld.Skip(count * 8);
    }

    // ------------------------------------------------------------ the channel stream

    private void IcsInfo(BitReader ld, Ics ics)
    {
        if (ld.Get1Bit() != 0) throw new FlacDecodeException("AAC ics_reserved_bit set.");
        ics.WindowSequence = (int)ld.GetBits(2);
        ics.WindowShape = ld.Get1Bit();
        if (ics.WindowSequence == EightShortSequence)
        {
            ics.MaxSfb = (int)ld.GetBits(4);
            ics.ScaleFactorGrouping = (int)ld.GetBits(7);
        }
        else
        {
            ics.MaxSfb = (int)ld.GetBits(6);
        }
        WindowGroupingInfo(ics);
        if (ics.MaxSfb > ics.NumSwb) throw new FlacDecodeException("AAC max_sfb out of range.");
        if (ics.WindowSequence != EightShortSequence && ld.Get1Bit() != 0)
            throw new UnsupportedAudioException("AAC prediction (MAIN or LTP) is not supported.");
    }

    private void IndividualChannelStream(BitReader ld, Ics ics, bool commonWindow, short[] specData)
    {
        // side_info
        ics.GlobalGain = (int)ld.GetBits(8);
        if (!commonWindow) IcsInfo(ld, ics);
        SectionData(ld, ics);
        DecodeScaleFactors(ld, ics);
        if ((ics.PulseDataPresent = ld.Get1Bit() != 0)) PulseData(ld, ics);
        if ((ics.TnsDataPresent = ld.Get1Bit() != 0)) TnsData(ld, ics);
        if (ld.Get1Bit() != 0) throw new UnsupportedAudioException("AAC gain control (SSR) is not supported.");

        SpectralData(ld, ics, specData);
        if (ics.PulseDataPresent)
        {
            if (ics.WindowSequence == EightShortSequence) throw new FlacDecodeException("AAC pulse data in short blocks.");
            PulseDecode(ics, specData);
        }
    }

    private void WindowGroupingInfo(Ics ics)
    {
        var sf = _sfIndex;
        switch (ics.WindowSequence)
        {
            case OnlyLongSequence:
            case LongStartSequence:
            case LongStopSequence:
                ics.NumWindows = 1;
                ics.NumWindowGroups = 1;
                ics.WindowGroupLength[0] = 1;
                ics.NumSwb = NumSwb1024Window[sf];
                if (ics.MaxSfb > ics.NumSwb) throw new FlacDecodeException("AAC max_sfb out of range.");
                for (var i = 0; i < ics.NumSwb; i++)
                {
                    ics.SectSfbOffset[0, i] = SwbOffset1024Window[sf][i];
                    ics.SwbOffset[i] = SwbOffset1024Window[sf][i];
                }
                ics.SectSfbOffset[0, ics.NumSwb] = FrameLength;
                ics.SwbOffset[ics.NumSwb] = FrameLength;
                ics.SwbOffsetMax = FrameLength;
                return;
            case EightShortSequence:
                ics.NumWindows = 8;
                ics.NumWindowGroups = 1;
                ics.WindowGroupLength[0] = 1;
                ics.NumSwb = NumSwb128Window[sf];
                if (ics.MaxSfb > ics.NumSwb) throw new FlacDecodeException("AAC max_sfb out of range.");
                for (var i = 0; i < ics.NumSwb; i++) ics.SwbOffset[i] = SwbOffset128Window[sf][i];
                ics.SwbOffset[ics.NumSwb] = FrameLength / 8;
                ics.SwbOffsetMax = FrameLength / 8;
                for (var i = 0; i < ics.NumWindows - 1; i++)
                {
                    if ((ics.ScaleFactorGrouping & (1 << (6 - i))) == 0)
                    {
                        ics.NumWindowGroups += 1;
                        ics.WindowGroupLength[ics.NumWindowGroups - 1] = 1;
                    }
                    else
                    {
                        ics.WindowGroupLength[ics.NumWindowGroups - 1] += 1;
                    }
                }
                for (var g = 0; g < ics.NumWindowGroups; g++)
                {
                    int sectSfb = 0, offset = 0;
                    for (var i = 0; i < ics.NumSwb; i++)
                    {
                        var width = i + 1 == ics.NumSwb
                            ? FrameLength / 8 - SwbOffset128Window[sf][i]
                            : SwbOffset128Window[sf][i + 1] - SwbOffset128Window[sf][i];
                        width *= ics.WindowGroupLength[g];
                        ics.SectSfbOffset[g, sectSfb++] = offset;
                        offset += width;
                    }
                    ics.SectSfbOffset[g, sectSfb] = offset;
                }
                return;
            default:
                throw new FlacDecodeException("Bad AAC window sequence.");
        }
    }

    private static void SectionData(BitReader ld, Ics ics)
    {
        int sectBits, sectLim;
        if (ics.WindowSequence == EightShortSequence) { sectBits = 3; sectLim = 8 * 15; }
        else { sectBits = 5; sectLim = MaxSfb; }
        var sectEscVal = (1 << sectBits) - 1;

        for (var g = 0; g < ics.NumWindowGroups; g++)
        {
            int k = 0, i = 0;
            while (k < ics.MaxSfb)
            {
                if (ld.Error) throw new FlacDecodeException("AAC section data ends early.");
                if (i >= sectLim) throw new FlacDecodeException("Too many AAC sections.");
                var cb = (int)ld.GetBits(4);
                if (cb == 12) throw new FlacDecodeException("AAC codebook 12.");
                ics.SectCb[g, i] = (byte)cb;
                if (cb == NoiseHcb) ics.NoiseUsed = true;
                if (cb is IntensityHcb2 or IntensityHcb) ics.IsUsed = true;

                var sectLen = 0;
                var sectLenIncr = (int)ld.GetBits(sectBits);
                while (sectLenIncr == sectEscVal)
                {
                    sectLen += sectLenIncr;
                    if (sectLen > sectLim) throw new FlacDecodeException("AAC section too long.");
                    sectLenIncr = (int)ld.GetBits(sectBits);
                }
                sectLen += sectLenIncr;
                ics.SectStart[g, i] = (byte)k;
                ics.SectEnd[g, i] = (byte)(k + sectLen);
                if (sectLen > sectLim || k + sectLen > sectLim) throw new FlacDecodeException("AAC section too long.");
                for (var sfb = k; sfb < k + sectLen; sfb++) ics.SfbCb[g, sfb] = (byte)cb;
                k += sectLen;
                i++;
            }
            ics.NumSec[g] = i;
            if (k != ics.MaxSfb) throw new FlacDecodeException("AAC sections do not add up.");
        }
    }

    private static void DecodeScaleFactors(BitReader ld, Ics ics)
    {
        int scaleFactor = ics.GlobalGain;
        int isPosition = 0;
        var noisePcmFlag = true;
        int noiseEnergy = ics.GlobalGain - 90;

        for (var g = 0; g < ics.NumWindowGroups; g++)
        {
            for (var sfb = 0; sfb < ics.MaxSfb; sfb++)
            {
                switch (ics.SfbCb[g, sfb])
                {
                    case ZeroHcb:
                        ics.ScaleFactors[g, sfb] = 0;
                        break;
                    case IntensityHcb:
                    case IntensityHcb2:
                        isPosition += HuffmanScaleFactor(ld) - 60;
                        ics.ScaleFactors[g, sfb] = (short)isPosition;
                        break;
                    case NoiseHcb:
                        int t;
                        if (noisePcmFlag)
                        {
                            noisePcmFlag = false;
                            t = (int)ld.GetBits(9) - 256;
                        }
                        else
                        {
                            t = HuffmanScaleFactor(ld) - 60;
                        }
                        noiseEnergy += t;
                        ics.ScaleFactors[g, sfb] = (short)noiseEnergy;
                        break;
                    default:
                        scaleFactor += HuffmanScaleFactor(ld) - 60;
                        if (scaleFactor < 0 || scaleFactor > 255) throw new FlacDecodeException("AAC scalefactor out of range.");
                        ics.ScaleFactors[g, sfb] = (short)scaleFactor;
                        break;
                }
            }
        }
    }

    private static void PulseData(BitReader ld, Ics ics)
    {
        ics.NumberPulse = (int)ld.GetBits(2);
        ics.PulseStartSfb = (int)ld.GetBits(6);
        if (ics.PulseStartSfb > ics.NumSwb) throw new FlacDecodeException("AAC pulse_start_sfb out of range.");
        for (var i = 0; i < ics.NumberPulse + 1; i++)
        {
            ics.PulseOffset[i] = (int)ld.GetBits(5);
            ics.PulseAmp[i] = (int)ld.GetBits(4);
        }
    }

    private static void PulseDecode(Ics ics, short[] specData)
    {
        var k = Math.Min(ics.SwbOffset[ics.PulseStartSfb], ics.SwbOffsetMax);
        for (var i = 0; i <= ics.NumberPulse; i++)
        {
            k += ics.PulseOffset[i];
            if (k >= FrameLength) throw new FlacDecodeException("AAC pulse out of range.");
            if (specData[k] > 0) specData[k] += (short)ics.PulseAmp[i];
            else specData[k] -= (short)ics.PulseAmp[i];
        }
    }

    private static void TnsData(BitReader ld, Ics ics)
    {
        int nFiltBits = 2, lengthBits = 6, orderBits = 5;
        if (ics.WindowSequence == EightShortSequence) { nFiltBits = 1; lengthBits = 4; orderBits = 3; }

        for (var w = 0; w < ics.NumWindows; w++)
        {
            var startCoefBits = 3;
            ics.TnsNFilt[w] = (int)ld.GetBits(nFiltBits);
            if (ics.TnsNFilt[w] != 0)
            {
                ics.TnsCoefRes[w] = ld.Get1Bit();
                if (ics.TnsCoefRes[w] != 0) startCoefBits = 4;
            }
            for (var filt = 0; filt < ics.TnsNFilt[w]; filt++)
            {
                ics.TnsLength[w, filt] = (int)ld.GetBits(lengthBits);
                ics.TnsOrder[w, filt] = (int)ld.GetBits(orderBits);
                if (ics.TnsOrder[w, filt] != 0)
                {
                    ics.TnsDirection[w, filt] = ld.Get1Bit();
                    ics.TnsCoefCompress[w, filt] = ld.Get1Bit();
                    var coefBits = startCoefBits - ics.TnsCoefCompress[w, filt];
                    for (var i = 0; i < ics.TnsOrder[w, filt]; i++)
                        ics.TnsCoef[w, filt, i] = (byte)ld.GetBits(coefBits);
                }
            }
        }
    }

    private static void SpectralData(BitReader ld, Ics ics, short[] specData)
    {
        const int nshort = FrameLength / 8;
        var groups = 0;
        for (var g = 0; g < ics.NumWindowGroups; g++)
        {
            var p = groups * nshort;
            for (var i = 0; i < ics.NumSec[g]; i++)
            {
                int sectCb = ics.SectCb[g, i];
                var inc = sectCb >= FirstPairHcb ? 2 : 4;
                int from = ics.SectSfbOffset[g, ics.SectStart[g, i]], to = ics.SectSfbOffset[g, ics.SectEnd[g, i]];
                switch (sectCb)
                {
                    case ZeroHcb:
                    case NoiseHcb:
                    case IntensityHcb:
                    case IntensityHcb2:
                        p += to - from;
                        break;
                    default:
                        for (var k = from; k < to; k += inc)
                        {
                            if (p + inc > specData.Length) throw new FlacDecodeException("AAC spectral data out of range.");
                            HuffmanSpectralData(sectCb, ld, specData.AsSpan(p, inc));
                            p += inc;
                        }
                        break;
                }
            }
            groups += ics.WindowGroupLength[g];
        }
    }

    // ------------------------------------------------------------ Huffman

    private static int HuffmanScaleFactor(BitReader ld)
    {
        var t = AacTables.HcbSf;
        var offset = 0;
        while (t[offset * 2 + 1] != 0)
        {
            var b = ld.Get1Bit();
            offset += t[offset * 2 + b];
        }
        return t[offset * 2];
    }

    private static readonly int[] HcbN = [0, 5, 5, 0, 5, 0, 5, 0, 5, 0, 6, 5];
    private static readonly byte[]?[] HcbTable = [null, AacTables.Hcb1_1, AacTables.Hcb2_1, null, AacTables.Hcb4_1, null, AacTables.Hcb6_1, null, AacTables.Hcb8_1, null, AacTables.Hcb10_1, AacTables.Hcb11_1];
    private static readonly sbyte[]?[] Hcb2QuadTable = [null, AacTables.Hcb1_2, AacTables.Hcb2_2, null, AacTables.Hcb4_2, null, null, null, null, null, null, null];
    private static readonly sbyte[]?[] Hcb2PairTable = [null, null, null, null, null, null, AacTables.Hcb6_2, null, AacTables.Hcb8_2, null, AacTables.Hcb10_2, AacTables.Hcb11_2];
    private static readonly sbyte[]?[] HcbBinTable = [null, null, null, null, null, AacTables.Hcb5, null, AacTables.Hcb7, null, AacTables.Hcb9, null, null];

    private static void HuffmanSignBits(BitReader ld, Span<short> sp, int len)
    {
        for (var i = 0; i < len; i++)
            if (sp[i] != 0 && ld.Get1Bit() != 0)
                sp[i] = (short)-sp[i];
    }

    private static void HuffmanGetEscape(BitReader ld, ref short sp)
    {
        int x = sp;
        bool neg;
        if (x < 0) { if (x != -16) return; neg = true; }
        else { if (x != 16) return; neg = false; }
        int i;
        for (i = 4; i < 16; i++)
            if (ld.Get1Bit() == 0) break;
        if (i >= 16) throw new FlacDecodeException("Bad AAC escape.");
        var off = (int)ld.GetBits(i);
        var j = off | (1 << i);
        sp = (short)(neg ? -j : j);
    }

    private static void Huffman2StepQuad(int cb, BitReader ld, Span<short> sp)
    {
        var root = HcbTable[cb]!;
        var rootBits = HcbN[cb];
        var table = Hcb2QuadTable[cb]!;
        var cw = (int)ld.ShowBits(rootBits);
        int offset = root[cw * 2], extraBits = root[cw * 2 + 1];
        if (extraBits != 0)
        {
            ld.FlushBits(rootBits);
            offset += (int)ld.ShowBits(extraBits);
            ld.FlushBits((byte)table[offset * 5] - rootBits);
        }
        else
        {
            ld.FlushBits((byte)table[offset * 5]);
        }
        sp[0] = table[offset * 5 + 1];
        sp[1] = table[offset * 5 + 2];
        sp[2] = table[offset * 5 + 3];
        sp[3] = table[offset * 5 + 4];
    }

    private static void Huffman2StepPair(int cb, BitReader ld, Span<short> sp)
    {
        var root = HcbTable[cb]!;
        var rootBits = HcbN[cb];
        var table = Hcb2PairTable[cb]!;
        var cw = (int)ld.ShowBits(rootBits);
        int offset = root[cw * 2], extraBits = root[cw * 2 + 1];
        if (extraBits != 0)
        {
            ld.FlushBits(rootBits);
            offset += (int)ld.ShowBits(extraBits);
            ld.FlushBits((byte)table[offset * 3] - rootBits);
        }
        else
        {
            ld.FlushBits((byte)table[offset * 3]);
        }
        sp[0] = table[offset * 3 + 1];
        sp[1] = table[offset * 3 + 2];
    }

    private static void HuffmanBinaryQuad(BitReader ld, Span<short> sp)
    {
        var table = AacTables.Hcb3;
        var offset = 0;
        while (table[offset * 5] == 0)
        {
            var b = ld.Get1Bit();
            offset += table[offset * 5 + 1 + b];
        }
        sp[0] = table[offset * 5 + 1];
        sp[1] = table[offset * 5 + 2];
        sp[2] = table[offset * 5 + 3];
        sp[3] = table[offset * 5 + 4];
    }

    private static void HuffmanBinaryPair(int cb, BitReader ld, Span<short> sp)
    {
        var table = HcbBinTable[cb]!;
        var offset = 0;
        while (table[offset * 3] == 0)
        {
            var b = ld.Get1Bit();
            offset += table[offset * 3 + 1 + b];
        }
        sp[0] = table[offset * 3 + 1];
        sp[1] = table[offset * 3 + 2];
    }

    private static void HuffmanSpectralData(int cb, BitReader ld, Span<short> sp)
    {
        switch (cb)
        {
            case 1:
            case 2:
                Huffman2StepQuad(cb, ld, sp);
                return;
            case 3:
                HuffmanBinaryQuad(ld, sp);
                HuffmanSignBits(ld, sp, 4);
                return;
            case 4:
                Huffman2StepQuad(cb, ld, sp);
                HuffmanSignBits(ld, sp, 4);
                return;
            case 5:
                HuffmanBinaryPair(cb, ld, sp);
                return;
            case 6:
                Huffman2StepPair(cb, ld, sp);
                return;
            case 7:
            case 9:
                HuffmanBinaryPair(cb, ld, sp);
                HuffmanSignBits(ld, sp, 2);
                return;
            case 8:
            case 10:
                Huffman2StepPair(cb, ld, sp);
                HuffmanSignBits(ld, sp, 2);
                return;
            case 11:
                Huffman2StepPair(11, ld, sp);
                HuffmanSignBits(ld, sp, 2);
                HuffmanGetEscape(ld, ref sp[0]);
                HuffmanGetEscape(ld, ref sp[1]);
                return;
            default:
                throw new FlacDecodeException($"AAC codebook {cb} does not exist.");
        }
    }

    // ------------------------------------------------------------ reconstruction

    private static readonly float[] IqTable = MakeIqTable();
    private static float[] MakeIqTable()
    {
        var t = new float[8192];
        for (var i = 0; i < t.Length; i++) t[i] = (float)Math.Pow(i, 4.0 / 3.0);
        return t;
    }

    private static readonly float[] Pow2SfTab = MakePow2SfTab();
    private static float[] MakePow2SfTab()
    {
        var t = new float[64];
        for (var i = 0; i < t.Length; i++) t[i] = (float)Math.Pow(2, i - 25);
        return t;
    }

    private static readonly float[] Pow2Table = [1.0f, 1.1892071150027210667174999705605f, 1.4142135623730950488016887242097f, 1.6817928305074290860622509524664f];

    private static float Iquant(short q)
    {
        if (q < 0)
        {
            if (-q < IqTable.Length) return -IqTable[-q];
            throw new FlacDecodeException("AAC quantized value out of range.");
        }
        if (q < IqTable.Length) return IqTable[q];
        throw new FlacDecodeException("AAC quantized value out of range.");
    }

    private static bool IsIntensityBand(Ics ics, int g, int sfb) => ics.SfbCb[g, sfb] is IntensityHcb or IntensityHcb2;
    private static int IsIntensity(Ics ics, int g, int sfb) => ics.SfbCb[g, sfb] switch { IntensityHcb => 1, IntensityHcb2 => -1, _ => 0 };
    private static bool IsNoise(Ics ics, int g, int sfb) => ics.SfbCb[g, sfb] == NoiseHcb;
    private static int InvertIntensity(Ics ics, int g, int sfb) => ics.MsMaskPresent == 1 ? 1 - 2 * ics.MsUsed[g, sfb] : 1;

    /// quant_to_spec: dequantisation and scaling, and for short blocks the deinterleaving.
    private static void QuantToSpec(Ics ics, short[] quantData, float[] specData)
    {
        int k = 0, gindex = 0;
        if (ics.NumSwb == 0) Array.Clear(specData);
        for (var g = 0; g < ics.NumWindowGroups; g++)
        {
            int j = 0, gincrease = 0;
            int winInc = ics.SwbOffset[ics.NumSwb];
            for (var sfb = 0; sfb < ics.NumSwb; sfb++)
            {
                var wa = gindex + j;
                int scaleFactor = ics.ScaleFactors[g, sfb];
                var width = ics.SwbOffset[sfb + 1] - ics.SwbOffset[sfb];
                if (width + 3 >= 1024) throw new FlacDecodeException("AAC band too wide.");
                if (IsIntensityBand(ics, g, sfb) || IsNoise(ics, g, sfb)) scaleFactor = 0;
                int exp = scaleFactor >> 2, frac = scaleFactor & 3;
                var scf = Pow2SfTab[exp] * Pow2Table[frac];
                for (var win = 0; win < ics.WindowGroupLength[g]; win++)
                {
                    for (var bin = 0; bin < width; bin += 4)
                    {
                        var wb = wa + bin;
                        specData[wb + 0] = Iquant(quantData[k + 0]) * scf;
                        specData[wb + 1] = Iquant(quantData[k + 1]) * scf;
                        specData[wb + 2] = Iquant(quantData[k + 2]) * scf;
                        specData[wb + 3] = Iquant(quantData[k + 3]) * scf;
                        gincrease += 4;
                        k += 4;
                    }
                    wa += winInc;
                }
                j += width;
            }
            gindex += gincrease;
        }
    }

    private void ReconstructSingleChannel(Ics ics, int channel, short[] specData)
    {
        QuantToSpec(ics, specData, _specCoef1);
        PnsDecode(ics, null, _specCoef1, null, channelPair: false);
        TnsDecodeFrame(ics, _specCoef1);
        IFilterBank(ics.WindowSequence, ics.WindowShape, _windowShapePrev[channel], _specCoef1, _timeOut[channel], _overlap[channel]);
        _windowShapePrev[channel] = ics.WindowShape;
    }

    private void ReconstructChannelPair(Ics ics1, Ics ics2, int channel, short[] specData1, short[] specData2)
    {
        QuantToSpec(ics1, specData1, _specCoef1);
        QuantToSpec(ics2, specData2, _specCoef2);
        PnsDecode(ics1, ics2, _specCoef1, _specCoef2, channelPair: ics1.MsMaskPresent != 0);
        MsDecode(ics1, ics2, _specCoef1, _specCoef2);
        IsDecode(ics1, ics2, _specCoef1, _specCoef2);
        TnsDecodeFrame(ics1, _specCoef1);
        TnsDecodeFrame(ics2, _specCoef2);
        IFilterBank(ics1.WindowSequence, ics1.WindowShape, _windowShapePrev[channel], _specCoef1, _timeOut[channel], _overlap[channel]);
        IFilterBank(ics2.WindowSequence, ics2.WindowShape, _windowShapePrev[channel + 1], _specCoef2, _timeOut[channel + 1], _overlap[channel + 1]);
        _windowShapePrev[channel] = ics1.WindowShape;
        _windowShapePrev[channel + 1] = ics2.WindowShape;
    }

    private static void MsDecode(Ics ics, Ics icsr, float[] lSpec, float[] rSpec)
    {
        const int nshort = FrameLength / 8;
        if (ics.MsMaskPresent < 1) return;
        var group = 0;
        for (var g = 0; g < ics.NumWindowGroups; g++)
        {
            for (var b = 0; b < ics.WindowGroupLength[g]; b++)
            {
                for (var sfb = 0; sfb < ics.MaxSfb; sfb++)
                {
                    if ((ics.MsUsed[g, sfb] != 0 || ics.MsMaskPresent == 2) && !IsIntensityBand(icsr, g, sfb) && !IsNoise(ics, g, sfb))
                    {
                        for (int i = ics.SwbOffset[sfb]; i < Math.Min(ics.SwbOffset[sfb + 1], ics.SwbOffsetMax); i++)
                        {
                            var k = group * nshort + i;
                            var tmp = lSpec[k] - rSpec[k];
                            lSpec[k] = lSpec[k] + rSpec[k];
                            rSpec[k] = tmp;
                        }
                    }
                }
                group++;
            }
        }
    }

    private static void IsDecode(Ics ics, Ics icsr, float[] lSpec, float[] rSpec)
    {
        const int nshort = FrameLength / 8;
        var group = 0;
        for (var g = 0; g < icsr.NumWindowGroups; g++)
        {
            for (var b = 0; b < icsr.WindowGroupLength[g]; b++)
            {
                for (var sfb = 0; sfb < icsr.MaxSfb; sfb++)
                {
                    if (!IsIntensityBand(icsr, g, sfb)) continue;
                    int scaleFactor = Math.Min(Math.Max((int)icsr.ScaleFactors[g, sfb], -120), 120);
                    var scale = (float)Math.Pow(0.5, 0.25 * scaleFactor);
                    for (int i = icsr.SwbOffset[sfb]; i < Math.Min(icsr.SwbOffset[sfb + 1], ics.SwbOffsetMax); i++)
                    {
                        var k = group * nshort + i;
                        rSpec[k] = lSpec[k] * scale;
                        if (IsIntensity(icsr, g, sfb) != InvertIntensity(ics, g, sfb)) rSpec[k] = -rSpec[k];
                    }
                }
                group++;
            }
        }
    }

    /// ne_rng: two polycounters, XORed (common.c).
    private static uint NeRng(ref uint r1, ref uint r2)
    {
        uint t1, t2, t3, t4;
        t3 = t1 = r1; t4 = t2 = r2;
        t1 &= 0xF5; t2 >>= 25;
        t1 = (uint)(System.Numerics.BitOperations.PopCount(t1) & 1); t2 &= 0x63;
        t1 <<= 31; t2 = (uint)(System.Numerics.BitOperations.PopCount(t2) & 1);
        return (r1 = (t3 >> 1) | t1) ^ (r2 = (t4 + t4) | t2);
    }

    private static void GenRandVector(float[] spec, int at, int scaleFactor, int size, ref uint r1, ref uint r2)
    {
        var energy = 0.0f;
        scaleFactor = Math.Min(Math.Max(scaleFactor, -120), 120);
        for (var i = 0; i < size; i++)
        {
            var tmp = (float)(int)NeRng(ref r1, ref r2);
            spec[at + i] = tmp;
            energy += tmp * tmp;
        }
        if (energy > 0)
        {
            var scale = 1.0f / (float)Math.Sqrt(energy);
            scale *= (float)Math.Pow(2.0, 0.25 * scaleFactor);
            for (var i = 0; i < size; i++) spec[at + i] *= scale;
        }
    }

    private void PnsDecode(Ics icsLeft, Ics? icsRight, float[] specLeft, float[]? specRight, bool channelPair)
    {
        const int nshort = FrameLength / 8;
        var group = 0;
        for (var g = 0; g < icsLeft.NumWindowGroups; g++)
        {
            for (var b = 0; b < icsLeft.WindowGroupLength[g]; b++)
            {
                var @base = group * nshort;
                for (var sfb = 0; sfb < icsLeft.MaxSfb; sfb++)
                {
                    uint r1Dep = 0, r2Dep = 0;
                    if (IsNoise(icsLeft, g, sfb))
                    {
                        var begin = Math.Min(@base + icsLeft.SwbOffset[sfb], FrameLength);
                        var end = Math.Min(@base + icsLeft.SwbOffset[sfb + 1], FrameLength);
                        r1Dep = _r1;
                        r2Dep = _r2;
                        GenRandVector(specLeft, begin, icsLeft.ScaleFactors[g, sfb], end - begin, ref _r1, ref _r2);
                    }
                    if (icsRight is not null && specRight is not null && IsNoise(icsRight, g, sfb))
                    {
                        var begin = Math.Min(@base + icsRight.SwbOffset[sfb], FrameLength);
                        var end = Math.Min(@base + icsRight.SwbOffset[sfb + 1], FrameLength);
                        if (channelPair && IsNoise(icsLeft, g, sfb)
                            && ((icsLeft.MsMaskPresent == 1 && icsLeft.MsUsed[g, sfb] != 0) || icsLeft.MsMaskPresent == 2))
                            GenRandVector(specRight, begin, icsRight.ScaleFactors[g, sfb], end - begin, ref r1Dep, ref r2Dep);
                        else
                            GenRandVector(specRight, begin, icsRight.ScaleFactors[g, sfb], end - begin, ref _r1, ref _r2);
                    }
                }
                group++;
            }
        }
    }

    // ------------------------------------------------------------ TNS

    private static readonly byte[][] TnsSbfMax =
    [
        [31, 9], [31, 9], [34, 10], [40, 14], [42, 14], [51, 14], [46, 14], [46, 14], [42, 14], [42, 14], [42, 14], [39, 14], [39, 14],
    ];
    private static readonly float[][] AllTnsCoefs = [AacTables.TnsCoef0_3, AacTables.TnsCoef0_4, AacTables.TnsCoef1_3, AacTables.TnsCoef1_4];

    private void TnsDecodeFrame(Ics ics, float[] spec)
    {
        const int nshort = FrameLength / 8;
        if (!ics.TnsDataPresent) return;
        Span<float> lpc = stackalloc float[TnsMaxOrder + 1];
        var maxTns = TnsSbfMax[_sfIndex][ics.WindowSequence == EightShortSequence ? 1 : 0];
        for (var w = 0; w < ics.NumWindows; w++)
        {
            int bottom = ics.NumSwb;
            for (var f = 0; f < ics.TnsNFilt[w]; f++)
            {
                var top = bottom;
                bottom = Math.Max(top - ics.TnsLength[w, f], 0);
                var tnsOrder = Math.Min(ics.TnsOrder[w, f], TnsMaxOrder);
                if (tnsOrder == 0) continue;
                TnsDecodeCoef(tnsOrder, ics.TnsCoefRes[w] + 3, ics.TnsCoefCompress[w, f], ics, w, f, lpc);

                int start = Math.Min(bottom, maxTns);
                start = Math.Min(start, ics.MaxSfb);
                start = Math.Min(ics.SwbOffset[start], ics.SwbOffsetMax);
                int end = Math.Min(top, maxTns);
                end = Math.Min(end, ics.MaxSfb);
                end = Math.Min(ics.SwbOffset[end], ics.SwbOffsetMax);
                var size = end - start;
                if (size <= 0) continue;
                int inc;
                if (ics.TnsDirection[w, f] != 0) { inc = -1; start = end - 1; }
                else inc = 1;
                TnsArFilter(spec, w * nshort + start, size, inc, lpc, tnsOrder);
            }
        }
    }

    private static void TnsDecodeCoef(int order, int coefResBits, int coefCompress, Ics ics, int w, int f, Span<float> a)
    {
        Span<float> tmp2 = stackalloc float[TnsMaxOrder + 1];
        Span<float> b = stackalloc float[TnsMaxOrder + 1];
        var tableIndex = 2 * (coefCompress != 0 ? 1 : 0) + (coefResBits != 3 ? 1 : 0);
        var tnsCoef = AllTnsCoefs[tableIndex];
        for (var i = 0; i < order; i++) tmp2[i] = tnsCoef[ics.TnsCoef[w, f, i]];
        a[0] = 1.0f;
        for (var m = 1; m <= order; m++)
        {
            a[m] = tmp2[m - 1];
            for (var i = 1; i < m; i++) b[i] = a[i] + a[m] * a[m - i];
            for (var i = 1; i < m; i++) a[i] = b[i];
        }
    }

    private static void TnsArFilter(float[] spectrum, int at, int size, int inc, Span<float> lpc, int order)
    {
        Span<float> state = stackalloc float[2 * TnsMaxOrder];
        state.Clear();
        var stateIndex = 0;
        for (var i = 0; i < size; i++)
        {
            var y = 0.0f;
            for (var j = 0; j < order; j++) y += state[stateIndex + j] * lpc[j + 1];
            y = spectrum[at] - y;
            stateIndex--;
            if (stateIndex < 0) stateIndex = order - 1;
            state[stateIndex] = state[stateIndex + order] = y;
            spectrum[at] = y;
            at += inc;
        }
    }

    // ------------------------------------------------------------ filterbank

    private static readonly float[][] LongWindow = [AacTables.SineLong1024, AacTables.KbdLong1024];
    private static readonly float[][] ShortWindow = [AacTables.SineShort128, AacTables.KbdShort128];

    private void IFilterBank(int windowSequence, int windowShape, int windowShapePrev, float[] freqIn, float[] timeOut, float[] overlap)
    {
        const int nlong = FrameLength, nshort = FrameLength / 8, trans = nshort / 2, nflatLs = (nlong - nshort) / 2;
        var transfBuf = _transfBuf;
        Array.Clear(transfBuf);
        var windowLong = LongWindow[windowShape];
        var windowLongPrev = LongWindow[windowShapePrev];
        var windowShort = ShortWindow[windowShape];
        var windowShortPrev = ShortWindow[windowShapePrev];

        switch (windowSequence)
        {
            case OnlyLongSequence:
                _mdct2048.Imdct(freqIn, 0, transfBuf, 0);
                for (var i = 0; i < nlong; i++) timeOut[i] = overlap[i] + transfBuf[i] * windowLongPrev[i];
                for (var i = 0; i < nlong; i++) overlap[i] = transfBuf[nlong + i] * windowLong[nlong - 1 - i];
                break;
            case LongStartSequence:
                _mdct2048.Imdct(freqIn, 0, transfBuf, 0);
                for (var i = 0; i < nlong; i++) timeOut[i] = overlap[i] + transfBuf[i] * windowLongPrev[i];
                for (var i = 0; i < nflatLs; i++) overlap[i] = transfBuf[nlong + i];
                for (var i = 0; i < nshort; i++) overlap[nflatLs + i] = transfBuf[nlong + nflatLs + i] * windowShort[nshort - i - 1];
                for (var i = 0; i < nflatLs; i++) overlap[nflatLs + nshort + i] = 0;
                break;
            case EightShortSequence:
                for (var w = 0; w < 8; w++) _mdct256.Imdct(freqIn, w * nshort, transfBuf, 2 * nshort * w);
                for (var i = 0; i < nflatLs; i++) timeOut[i] = overlap[i];
                for (var i = 0; i < nshort; i++)
                {
                    timeOut[nflatLs + i] = overlap[nflatLs + i] + transfBuf[nshort * 0 + i] * windowShortPrev[i];
                    timeOut[nflatLs + 1 * nshort + i] = overlap[nflatLs + nshort * 1 + i] + transfBuf[nshort * 1 + i] * windowShort[nshort - 1 - i] + transfBuf[nshort * 2 + i] * windowShort[i];
                    timeOut[nflatLs + 2 * nshort + i] = overlap[nflatLs + nshort * 2 + i] + transfBuf[nshort * 3 + i] * windowShort[nshort - 1 - i] + transfBuf[nshort * 4 + i] * windowShort[i];
                    timeOut[nflatLs + 3 * nshort + i] = overlap[nflatLs + nshort * 3 + i] + transfBuf[nshort * 5 + i] * windowShort[nshort - 1 - i] + transfBuf[nshort * 6 + i] * windowShort[i];
                    if (i < trans)
                        timeOut[nflatLs + 4 * nshort + i] = overlap[nflatLs + nshort * 4 + i] + transfBuf[nshort * 7 + i] * windowShort[nshort - 1 - i] + transfBuf[nshort * 8 + i] * windowShort[i];
                }
                for (var i = 0; i < nshort; i++)
                {
                    if (i >= trans)
                        overlap[nflatLs + 4 * nshort + i - nlong] = transfBuf[nshort * 7 + i] * windowShort[nshort - 1 - i] + transfBuf[nshort * 8 + i] * windowShort[i];
                    overlap[nflatLs + 5 * nshort + i - nlong] = transfBuf[nshort * 9 + i] * windowShort[nshort - 1 - i] + transfBuf[nshort * 10 + i] * windowShort[i];
                    overlap[nflatLs + 6 * nshort + i - nlong] = transfBuf[nshort * 11 + i] * windowShort[nshort - 1 - i] + transfBuf[nshort * 12 + i] * windowShort[i];
                    overlap[nflatLs + 7 * nshort + i - nlong] = transfBuf[nshort * 13 + i] * windowShort[nshort - 1 - i] + transfBuf[nshort * 14 + i] * windowShort[i];
                    overlap[nflatLs + 8 * nshort + i - nlong] = transfBuf[nshort * 15 + i] * windowShort[nshort - 1 - i];
                }
                for (var i = 0; i < nflatLs; i++) overlap[nflatLs + nshort + i] = 0;
                break;
            case LongStopSequence:
                _mdct2048.Imdct(freqIn, 0, transfBuf, 0);
                for (var i = 0; i < nflatLs; i++) timeOut[i] = overlap[i];
                for (var i = 0; i < nshort; i++) timeOut[nflatLs + i] = overlap[nflatLs + i] + transfBuf[nflatLs + i] * windowShortPrev[i];
                for (var i = 0; i < nflatLs; i++) timeOut[nflatLs + nshort + i] = overlap[nflatLs + nshort + i] + transfBuf[nflatLs + nshort + i];
                for (var i = 0; i < nlong; i++) overlap[i] = transfBuf[nlong + i] * windowLong[nlong - 1 - i];
                break;
        }
    }

    // ------------------------------------------------------------ state

    /// ic_stream: one channel's side information for one frame.
    private sealed class Ics
    {
        public int MaxSfb, NumSwb, NumWindowGroups, NumWindows, WindowSequence, WindowShape, ScaleFactorGrouping;
        public readonly int[] WindowGroupLength = new int[8];
        public readonly int[] SwbOffset = new int[52];
        public int SwbOffsetMax;
        public readonly int[,] SectSfbOffset = new int[8, 15 * 8 + 1];
        public readonly byte[,] SectCb = new byte[8, 15 * 8];
        public readonly byte[,] SectStart = new byte[8, 15 * 8];
        public readonly byte[,] SectEnd = new byte[8, 15 * 8];
        public readonly byte[,] SfbCb = new byte[8, 8 * 15];
        public readonly int[] NumSec = new int[8];
        public int GlobalGain;
        public readonly short[,] ScaleFactors = new short[8, 51];
        public int MsMaskPresent;
        public readonly byte[,] MsUsed = new byte[8, 51];
        public bool NoiseUsed, IsUsed, PulseDataPresent, TnsDataPresent;
        public int NumberPulse, PulseStartSfb;
        public readonly int[] PulseOffset = new int[4], PulseAmp = new int[4];
        public readonly int[] TnsNFilt = new int[8], TnsCoefRes = new int[8];
        public readonly int[,] TnsLength = new int[8, 4], TnsOrder = new int[8, 4], TnsDirection = new int[8, 4], TnsCoefCompress = new int[8, 4];
        public readonly byte[,,] TnsCoef = new byte[8, 4, 32];

        /// element sce = {0}: a fresh one for every element.
        public void Reset()
        {
            MaxSfb = NumSwb = NumWindowGroups = NumWindows = WindowSequence = WindowShape = ScaleFactorGrouping = 0;
            Array.Clear(WindowGroupLength); Array.Clear(SwbOffset); SwbOffsetMax = 0;
            Array.Clear(SectSfbOffset); Array.Clear(SectCb); Array.Clear(SectStart); Array.Clear(SectEnd);
            Array.Clear(SfbCb); Array.Clear(NumSec); GlobalGain = 0; Array.Clear(ScaleFactors);
            MsMaskPresent = 0; Array.Clear(MsUsed);
            NoiseUsed = IsUsed = PulseDataPresent = TnsDataPresent = false;
            NumberPulse = PulseStartSfb = 0; Array.Clear(PulseOffset); Array.Clear(PulseAmp);
            Array.Clear(TnsNFilt); Array.Clear(TnsCoefRes); Array.Clear(TnsLength); Array.Clear(TnsOrder);
            Array.Clear(TnsDirection); Array.Clear(TnsCoefCompress); Array.Clear(TnsCoef);
        }

        /// memcpy(ics2, ics1, sizeof(ic_stream)), for a common window.
        public void CopyFrom(Ics o)
        {
            MaxSfb = o.MaxSfb; NumSwb = o.NumSwb; NumWindowGroups = o.NumWindowGroups; NumWindows = o.NumWindows;
            WindowSequence = o.WindowSequence; WindowShape = o.WindowShape; ScaleFactorGrouping = o.ScaleFactorGrouping;
            Array.Copy(o.WindowGroupLength, WindowGroupLength, 8); Array.Copy(o.SwbOffset, SwbOffset, 52); SwbOffsetMax = o.SwbOffsetMax;
            Array.Copy(o.SectSfbOffset, SectSfbOffset, SectSfbOffset.Length);
            Array.Copy(o.SectCb, SectCb, SectCb.Length); Array.Copy(o.SectStart, SectStart, SectStart.Length);
            Array.Copy(o.SectEnd, SectEnd, SectEnd.Length); Array.Copy(o.SfbCb, SfbCb, SfbCb.Length);
            Array.Copy(o.NumSec, NumSec, 8); GlobalGain = o.GlobalGain; Array.Copy(o.ScaleFactors, ScaleFactors, ScaleFactors.Length);
            MsMaskPresent = o.MsMaskPresent; Array.Copy(o.MsUsed, MsUsed, MsUsed.Length);
            NoiseUsed = o.NoiseUsed; IsUsed = o.IsUsed; PulseDataPresent = o.PulseDataPresent; TnsDataPresent = o.TnsDataPresent;
            NumberPulse = o.NumberPulse; PulseStartSfb = o.PulseStartSfb;
            Array.Copy(o.PulseOffset, PulseOffset, 4); Array.Copy(o.PulseAmp, PulseAmp, 4);
            Array.Copy(o.TnsNFilt, TnsNFilt, 8); Array.Copy(o.TnsCoefRes, TnsCoefRes, 8);
            Array.Copy(o.TnsLength, TnsLength, TnsLength.Length); Array.Copy(o.TnsOrder, TnsOrder, TnsOrder.Length);
            Array.Copy(o.TnsDirection, TnsDirection, TnsDirection.Length); Array.Copy(o.TnsCoefCompress, TnsCoefCompress, TnsCoefCompress.Length);
            Array.Copy(o.TnsCoef, TnsCoef, TnsCoef.Length);
        }
    }

    /// MSB-first bits from a byte range, as FAAD2's bitfile reads them:
    /// past the end it gives zeros and says so.
    private sealed class BitReader(byte[] data, int start, int length)
    {
        private int _pos;                   // bits from start
        private readonly int _limit = length * 8;
        public bool Error { get; private set; }
        public int BitsLeft => _limit - _pos;

        public uint ShowBits(int n)
        {
            uint r = 0;
            for (var i = 0; i < n; i++)
            {
                var p = _pos + i;
                var bit = p < _limit ? (data[start + (p >> 3)] >> (7 - (p & 7))) & 1 : 0;
                r = (r << 1) | (uint)bit;
            }
            return r;
        }

        public void FlushBits(int n)
        {
            _pos += n;
            if (_pos > _limit) Error = true;
        }

        public uint GetBits(int n)
        {
            if (n == 0) return 0;
            var r = ShowBits(n);
            FlushBits(n);
            return r;
        }

        public int Get1Bit()
        {
            var r = _pos < _limit ? (data[start + (_pos >> 3)] >> (7 - (_pos & 7))) & 1 : 0;
            FlushBits(1);
            return r;
        }

        public void Skip(int n) => FlushBits(n);
        public void ByteAlign() { var r = _pos & 7; if (r != 0) FlushBits(8 - r); }
    }

    /// The IMDCT (mdct.c, floating point): pre-twiddle, a complex FFT of N/4
    /// points, post-twiddle, reorder.
    private sealed class Mdct
    {
        private readonly int _n;
        private readonly float[] _sincos;   // FAAD2's float table: sqrt(2/N)·(cos, sin)(2π(k + 1/8)/N)
        private readonly float[] _re, _im;
        private readonly Fft _fft;

        public Mdct(int n)
        {
            _n = n;
            _sincos = n == 2048 ? AacTables.MdctTab2048 : AacTables.MdctTab256;
            _re = new float[n / 4];
            _im = new float[n / 4];
            _fft = new Fft(n / 4);
        }

        public void Imdct(float[] xIn, int inAt, float[] xOut, int outAt)
        {
            int n = _n, n2 = n >> 1, n4 = n >> 2, n8 = n >> 3;
            var sc = _sincos;
            var re = _re;
            var im = _im;
            for (var k = 0; k < n4; k++)
            {
                // ComplexMult(&IM(Z1[k]), &RE(Z1[k]), X_in[2k], X_in[N2-1-2k], RE(sincos[k]), IM(sincos[k]))
                float x1 = xIn[inAt + 2 * k], x2 = xIn[inAt + n2 - 1 - 2 * k], c1 = sc[2 * k], c2 = sc[2 * k + 1];
                im[k] = x1 * c1 + x2 * c2;
                re[k] = x2 * c1 - x1 * c2;
            }
            _fft.Backward(re, im);
            for (var k = 0; k < n4; k++)
            {
                // ComplexMult(&IM(Z1[k]), &RE(Z1[k]), IM(x), RE(x), RE(sincos[k]), IM(sincos[k]))
                float x1 = im[k], x2 = re[k], c1 = sc[2 * k], c2 = sc[2 * k + 1];
                im[k] = x1 * c1 + x2 * c2;
                re[k] = x2 * c1 - x1 * c2;
            }
            for (var k = 0; k < n8; k += 2)
            {
                var o = xOut;
                var b = outAt;
                o[b + 2 * k] = im[n8 + k];
                o[b + 2 + 2 * k] = im[n8 + 1 + k];
                o[b + 1 + 2 * k] = -re[n8 - 1 - k];
                o[b + 3 + 2 * k] = -re[n8 - 2 - k];
                o[b + n4 + 2 * k] = re[k];
                o[b + n4 + 2 + 2 * k] = re[1 + k];
                o[b + n4 + 1 + 2 * k] = -im[n4 - 1 - k];
                o[b + n4 + 3 + 2 * k] = -im[n4 - 2 - k];
                o[b + n2 + 2 * k] = re[n8 + k];
                o[b + n2 + 2 + 2 * k] = re[n8 + 1 + k];
                o[b + n2 + 1 + 2 * k] = -im[n8 - 1 - k];
                o[b + n2 + 3 + 2 * k] = -im[n8 - 2 - k];
                o[b + n2 + n4 + 2 * k] = -im[k];
                o[b + n2 + n4 + 2 + 2 * k] = -im[1 + k];
                o[b + n2 + n4 + 1 + 2 * k] = re[n4 - 1 - k];
                o[b + n2 + n4 + 3 + 2 * k] = re[n4 - 2 - k];
            }
        }
    }

    /// An unnormalised complex FFT of a power-of-two size, with FFTPACK's
    /// backward sign (cfftb: exp(+i 2 pi j k / n)).
    private sealed class Fft
    {
        private readonly int _n;
        private readonly int[] _reverse;
        private readonly float[] _cos, _sin;

        public Fft(int n)
        {
            _n = n;
            var bits = System.Numerics.BitOperations.Log2((uint)n);
            _reverse = new int[n];
            for (var i = 0; i < n; i++)
            {
                int r = 0, x = i;
                for (var b = 0; b < bits; b++) { r = (r << 1) | (x & 1); x >>= 1; }
                _reverse[i] = r;
            }
            _cos = new float[n / 2];
            _sin = new float[n / 2];
            for (var i = 0; i < n / 2; i++)
            {
                _cos[i] = (float)Math.Cos(2 * Math.PI * i / n);
                _sin[i] = (float)Math.Sin(2 * Math.PI * i / n);
            }
        }

        public void Backward(float[] re, float[] im)
        {
            var n = _n;
            for (var i = 0; i < n; i++)
            {
                var j = _reverse[i];
                if (j > i)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }
            for (var size = 2; size <= n; size <<= 1)
            {
                var half = size >> 1;
                var step = n / size;
                for (var start = 0; start < n; start += size)
                {
                    for (var k = 0; k < half; k++)
                    {
                        float wr = _cos[k * step], wi = _sin[k * step];
                        int a = start + k, b = a + half;
                        var tr = re[b] * wr - im[b] * wi;
                        var ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }
        }
    }
}
