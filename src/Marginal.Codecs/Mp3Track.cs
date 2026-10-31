// COPIED from AlbumWall (src/AlbumWall.Codecs/Mp3Track.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — one MP3 open for playing: around the codec core (Mp3.cs), what
// a track needs.
//
// GAPLESS, AS FFMPEG DOES IT (libavformat/mp3dec.c, n9.0.2, which is what mpv
// plays MP3 through), so a record plays the same on either engine. An encoder
// pads both ends of the music, and LAME writes how much in the Info tag, a
// frame of its own before the first audio frame: `start_pad` at the front and
// `end_pad` at the end, read only when the tag says LAME, Lavf or Lavc. The
// first start_pad + 529 decoded samples are dropped (529 is the decoder's own
// delay), and the track is then frames x samples-per-frame - start_pad -
// end_pad samples long. No tag, no trimming, as there. The Info frame itself
// is not audio and is not decoded.
//
// SEEKS ARE EXACT. The frame that holds the sample is found by its position
// in an index of frame starts (headers only, the file being in memory), and
// decoding starts ten frames before it with nothing kept from before: the bit
// reservoir refills from those frames' bytes and the filters settle, so from
// the frame asked for on, the samples are the ones a straight decode gives.
//
// REPLAYGAIN as FFmpeg keeps it: the ID3 TXXX tags if they have a gain, else
// the LAME tag's (only when its CRC is right).
//
// The bytes arrive as they do for FLAC (TrackBytes): from the disk or a
// server, every frame waiting for its own, and a seek done at the next Read.

namespace Marginal.Codecs;

public sealed unsafe class Mp3Track : IAudioTrack
{
    private const int Lookahead = 4100;         // two of the largest frames and a header: what framing looks at
    private const int Preroll = 10;             // frames decoded and dropped before a seek's

    public string Name { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample => 0;              // lossy: no depth of its own
    public ReplayGain Gain { get; }

    /// What plays: the Info tag's count, trimmed, or else an estimate from
    /// the bitrate, which the end of the data corrects.
    public long TotalFrames => _total >= 0 ? _total : _estimate;
    public TimeSpan Duration => TimeSpan.FromSeconds((double)TotalFrames / SampleRate);

    /// The encoder's padding, as the LAME tag says (0, 0 without one).
    public int StartPad { get; }
    public int EndPad { get; }

    private readonly TrackBytes _bytes;
    private readonly byte[] _data;
    private readonly int _start;                // the first audio frame
    private int _end = -1;                      // where the audio ends (before ID3v1 or APE), once the tail is here
    private readonly int _spf;                  // samples per frame
    private readonly long _skip;                // stream samples dropped at the start
    private readonly long _total = -1;          // samples played, when the Info tag says
    private readonly long _estimate;

    private readonly Mp3Decoder _decoder = new();
    private readonly float[] _pcm = new float[Mp3Decoder.MaxSamplesPerFrame];
    private int _pcmFrames, _pcmOffset;         // decoded in _pcm, and handed out
    private long _pcmAt;                        // the stream sample _pcm starts at
    private int _at;                            // the next byte to decode
    private long _streamAt;                     // the stream sample the next frame decoded starts at
    private long _from;                         // the first stream sample to hand out (the skip, or a seek's)
    private readonly List<int> _frames = [];    // where frame n starts, as far as known
    private int _scanFreeFormat;
    private long _seekTo = -1;
    private volatile bool _interrupted;

    private Mp3Track(string name, TrackBytes bytes)
    {
        Name = name;
        _bytes = bytes;
        _data = bytes.Data;

        // The ID3v2 tags in front, however many: their sizes first, then them.
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = 0;
        while (true)
        {
            _bytes.WaitFor(at + 10);
            var size = Id3.SizeAt(_data, at, _bytes.Available);
            if (size == 0) break;
            _bytes.WaitFor(at + size);
            at += Id3.Measure(_data, at, Math.Min(_bytes.Available, at + size), tags);
        }

        // The first frame, which may be the Info tag's.
        _bytes.WaitFor(at + 2 * Lookahead);
        var available = _bytes.Available;
        int first, firstBytes;
        fixed (byte* p = _data)
        {
            var free = 0;
            first = at + Mp3Decoder.Mp3dFindFrame(p + at, available - at, ref free, out firstBytes);
            if (firstBytes == 0) throw new FlacDecodeException("No MP3 frames.");
            var h = p + first;
            if (Mp3Decoder.HdrGetLayer(h) != 1) throw new UnsupportedAudioException("MPEG audio Layer I or II, not Layer III.");
            SampleRate = Mp3Decoder.HdrSampleRateHz(h);
            Channels = Mp3Decoder.HdrIsMono(h) ? 1 : 2;
            _spf = Mp3Decoder.HdrTestMpeg1(h) ? 1152 : 576;
            _estimate = (long)((double)(_data.Length - first) / Math.Max(1, firstBytes) * _spf);

            var info = InfoTag(p, first, firstBytes, h);
            _start = info.Skip ? first + firstBytes : first;
            StartPad = info.StartPad;
            EndPad = info.EndPad;
            if (info.Lame)
            {
                _skip = info.StartPad + 528 + 1;
                if (info.Frames > 0) _total = Math.Max(0, info.Frames * (long)_spf - info.StartPad - info.EndPad);
            }
            if (info.Frames > 0) _estimate = info.Frames * (long)_spf - info.StartPad - info.EndPad;

            var raw = ReplayGain.Raw(tags);
            Gain = ReplayGain.FromRaw(ReplayGain.Any(raw) ? raw : info.Gain);
        }

        _at = _start;
        _from = _skip;
        _frames.Add(_start);
    }

    public static Mp3Track Open(string path) => new(path, TrackBytes.FromFile(path));
    public static Mp3Track Open(string name, string source) => new(name, TrackBytes.Open(name, source));

    // ------------------------------------------------------------ the Info tag

    private readonly record struct Info(bool Skip, bool Lame, long Frames, int StartPad, int EndPad,
                                        (int, int, int, int) Gain);

    /// The Xing/Info tag (and LAME's inside it) or the VBRI tag, read in the
    /// order FFmpeg reads them (mp3_parse_vbr_tags, mp3_parse_info_tag).
    private Info InfoTag(byte* p, int frame, int frameBytes, byte* h)
    {
        var none = (int.MinValue, 0, int.MinValue, 0);
        bool lsf = !Mp3Decoder.HdrTestMpeg1(h), mono = Mp3Decoder.HdrIsMono(h);
        var end = frame + frameBytes;
        var r = frame + 4 + (lsf ? (mono ? 9 : 17) : (mono ? 17 : 32));
        uint Be32(int i) => i + 4 <= end ? (uint)(p[i] << 24 | p[i + 1] << 16 | p[i + 2] << 8 | p[i + 3]) : 0;
        int Be16(int i) => i + 2 <= end ? p[i] << 8 | p[i + 1] : 0;

        long frames = 0;
        uint headerFileSize = 0;
        bool lame = false;
        int startPad = 0, endPad = 0;
        var gain = none;
        var tag = Be32(r);
        if (tag is 0x58696E67 or 0x496E666F)        // "Xing", "Info"
        {
            r += 4;
            var flags = Be32(r); r += 4;
            if ((flags & 1) != 0) { frames = Be32(r); r += 4; }
            if ((flags & 2) != 0) { headerFileSize = Be32(r); r += 4; }
            var fileSize = (long)_data.Length - (frame + 4);
            if (fileSize > 0 && headerFileSize > 0)
            {
                long min = Math.Min(fileSize, headerFileSize), delta = Math.Max(fileSize, headerFileSize) - min;
                if (fileSize > headerFileSize && delta > min >> 4) frames = 0;      // a concatenated file
            }
            if ((flags & 4) != 0) r += 100;           // the seek table, which exact seeking does not need
            if ((flags & 8) != 0) r += 4;             // VBR quality
            var version = r + 9 <= end ? Be32(r) : 0;
            r += 9;
            r += 2;                                   // tag revision and VBR method, lowpass
            var peak = ((long)Be32(r) * 100000 + (1 << 22)) >> 23; r += 4;   // av_rescale, to nearest
            var rg = Be16(r); r += 2;
            var ag = Be16(r); r += 2;
            int trackGain = int.MinValue, albumGain = int.MinValue;
            if (rg >> 13 == 1) { trackGain = (rg & 0x1FF) * 10000; if ((rg & 0x200) != 0) trackGain = -trackGain; }
            if (ag >> 13 == 2) { albumGain = (ag & 0x1FF) * 10000; if ((ag & 0x200) != 0) albumGain = -albumGain; }
            r += 2;                                   // encoding flags, bitrate
            var delays = r + 3 <= end ? p[r] << 16 | p[r + 1] << 8 | p[r + 2] : 0; r += 3;
            if (version is 0x4C414D45 or 0x4C617666 or 0x4C617663)   // "LAME", "Lavf", "Lavc"
            {
                lame = true;
                startPad = delays >> 12;
                endPad = delays & 4095;
            }
            r += 1 + 1 + 2 + 4 + 2;                   // misc, MP3 gain, preset, music length, music CRC
            if (r + 2 <= end && Be16(r) == Crc16(p + frame, r - frame))
                gain = (trackGain, (int)peak, albumGain, 0);
        }
        else if (Be32(frame + 4 + 32) == 0x56425249 && Be16(frame + 4 + 36) == 1)  // "VBRI", version 1
        {
            headerFileSize = Be32(frame + 4 + 42);
            frames = Be32(frame + 4 + 46);
        }
        // Without a count or a size, the frame was audio after all.
        var skip = frames != 0 || headerFileSize != 0;
        return new Info(skip, lame, frames, startPad, endPad, gain);
    }

    /// CRC-16 with the polynomial 0xA001 (FFmpeg's ff_crcA001_update): the
    /// LAME tag's check over the 190 bytes before it.
    private static int Crc16(byte* p, int length)
    {
        var crc = 0;
        for (var i = 0; i < length; i++)
        {
            crc ^= p[i];
            for (var b = 0; b < 8; b++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
        }
        return crc;
    }

    // ------------------------------------------------------------ playing

    public void Interrupt() => _interrupted = true;
    public bool Interrupted => _interrupted;

    public void Seek(long frame)
    {
        _interrupted = false;
        _seekTo = Math.Max(0, frame);
    }

    public void Prepare(int frames)
    {
        if (_seekTo >= 0) SeekNow();
        if (_bytes.Done) return;
        var ahead = ((long)frames + _spf - 1) / _spf + 2;
        _bytes.WaitFor((int)Math.Min(_data.Length, _at + ahead * 1441 + Lookahead), () => _interrupted);
    }

    public int Read(Span<float> into)
    {
        if (_seekTo >= 0) SeekNow();
        var channels = Channels;
        var want = into.Length / channels;
        var done = 0;
        while (done < want)
        {
            if (_pcmOffset >= _pcmFrames)
            {
                if (_total >= 0 && _streamAt - _skip >= _total) break;
                if (!DecodeNext()) break;
                continue;
            }
            var s = _pcmAt + _pcmOffset;
            if (s < _from)
            {
                _pcmOffset += (int)Math.Min(_from - s, _pcmFrames - _pcmOffset);
                continue;
            }
            long n = Math.Min(want - done, _pcmFrames - _pcmOffset);
            if (_total >= 0) n = Math.Min(n, _total - (s - _skip));
            if (n <= 0) { _pcmOffset = _pcmFrames; _streamAt = _skip + _total; break; }
            _pcm.AsSpan(_pcmOffset * channels, (int)n * channels).CopyTo(into[(done * channels)..]);
            _pcmOffset += (int)n;
            done += (int)n;
        }
        return done;
    }

    /// The next frame into _pcm. False at the end, or when the bytes for it
    /// will not arrive, or the wait for them is interrupted.
    private bool DecodeNext()
    {
        while (true)
        {
            _bytes.WaitFor((int)Math.Min(_data.Length, (long)_at + Lookahead), () => _interrupted);
            if (_interrupted) return false;
            var limit = Limit();
            if (_at >= limit) return false;

            int samples;
            Mp3Decoder.FrameInfo info;
            fixed (byte* p = _data)
            fixed (float* pcm = _pcm)
                samples = _decoder.DecodeFrame(p + _at, limit - _at, pcm, out info);

            if (info.Channels == 0)
            {
                // No frame here: what is skipped is not audio.
                if (info.FrameBytes <= 0) return false;
                _at += info.FrameBytes;
                continue;
            }

            var index = (int)(_streamAt / _spf);
            if (index == _frames.Count) _frames.Add(_at + info.FrameOffset);
            _at += info.FrameBytes;
            _pcmAt = _streamAt;
            _pcmOffset = 0;
            _pcmFrames = _spf;
            // A frame that could not be decoded (its reservoir is from before
            // a seek, or the file is damaged) still takes its place in time.
            if (samples == 0 || info.Channels != Channels) Array.Clear(_pcm);
            _streamAt += _spf;
            return true;
        }
    }

    /// How far the bytes can be read: up to the tags at the end once those
    /// are known, and to what has arrived until then.
    private int Limit()
    {
        if (_end >= 0) return Math.Min(_end, _bytes.Available);
        if (_at + Lookahead + 512 < _data.Length) return _bytes.Available;
        // Near the end: the tail is needed to know where the audio stops.
        if (!_bytes.WaitFor(_data.Length, () => _interrupted)) return _bytes.Available;
        _end = AudioEnd();
        return Math.Min(_end, _bytes.Available);
    }

    /// The end of the audio: before an ID3v1 tag, and an APEv2 or Lyrics3v2
    /// tag before that.
    private int AudioEnd()
    {
        var end = _data.Length;
        bool At(int i, string s)
        {
            if (i < 0 || i + s.Length > _data.Length) return false;
            for (var k = 0; k < s.Length; k++) if (_data[i + k] != s[k]) return false;
            return true;
        }
        if (end >= 128 && At(end - 128, "TAG")) end -= 128;
        if (At(end - 32, "APETAGEX"))
        {
            var size = BitConverter.ToInt32(_data, end - 32 + 12);
            var flags = BitConverter.ToUInt32(_data, end - 32 + 20);
            end -= size + ((flags & 0x80000000) != 0 ? 32 : 0);
        }
        else if (At(end - 9, "LYRICS200") && int.TryParse(System.Text.Encoding.ASCII.GetString(_data, end - 15, 6), out var lyrics))
        {
            end -= lyrics + 15;
        }
        return Math.Clamp(end, _start, _data.Length);
    }

    /// The seek asked for, done: the frame that holds it found in the index,
    /// and decoding started Preroll frames before it from nothing.
    private void SeekNow()
    {
        var to = _seekTo;
        _seekTo = -1;
        var target = to + _skip;
        var frame = (int)(target / _spf);
        var from = Math.Max(0, frame - Preroll);
        if (!IndexTo(from)) from = Math.Max(0, _frames.Count - 1);   // past the end, or not arrived: as far as known

        _decoder.Reset();
        _at = _frames[from];
        _streamAt = (long)from * _spf;
        _pcmFrames = _pcmOffset = 0;
        _from = Math.Max(_skip, target);
    }

    /// The index grown to frame `n`, by headers alone. False if the frames
    /// end, or their bytes will not arrive, before it.
    private bool IndexTo(int n)
    {
        while (_frames.Count <= n)
        {
            var at = _frames[^1];
            _bytes.WaitFor((int)Math.Min(_data.Length, (long)at + Lookahead), () => _interrupted);
            if (_interrupted) return false;
            var limit = Limit();
            if (at + 4 > limit) return false;
            fixed (byte* p = _data)
            {
                var h = p + at;
                int size;
                if (Mp3Decoder.HdrValid(h))
                {
                    size = Mp3Decoder.HdrFrameBytes(h, _scanFreeFormat) + Mp3Decoder.HdrPadding(h);
                }
                else
                {
                    // Not a frame where one should be: find the next, as the decoder would.
                    var skip = Mp3Decoder.Mp3dFindFrame(h, limit - at, ref _scanFreeFormat, out _);
                    if (skip >= limit - at) return false;
                    _frames[^1] = at + skip;
                    continue;
                }
                if (size <= 4 || at + size >= limit) return false;
                _frames.Add(at + size);
            }
        }
        return true;
    }
}
