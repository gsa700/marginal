// COPIED from AlbumWall (src/AlbumWall.Codecs/Mp4Track.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — one AAC or ALAC track in an MP4 (.m4a) open for playing: around
// the codec cores (Aac.cs, Alac.cs), the container and what a track needs.
//
// The MP4 is read for what playing needs and nothing else: the first audio
// track's sample table (where each frame is and how long), its decoder
// configuration (the esds for AAC, the 'alac' box's magic cookie for ALAC),
// its edit list, and the iTunes tags that carry the gapless information and
// ReplayGain. An AAC frame is 1024 samples; an ALAC frame is as long as its
// cookie says, 4096 as a rule, the last one shorter.
//
// GAPLESS, AS FFMPEG DOES IT (libavformat/mov.c, n9.0.2, which is what mpv
// plays AAC through), so a record plays the same on either engine. An encoder
// starts with priming samples and ends with padding:
//   * the edit list, when there is one, says where the music starts in the
//     media (its media time: so many samples are dropped) and how long it is;
//   * iTunes' iTunSMPB tag says the same in its own words, priming, padding
//     and length, and its priming is what is dropped when it is there;
//   * its padding counts only when nothing else has cut the length down: with
//     an edit list that already ends at the music, it is not cut twice.
// With neither, nothing is dropped. The length is the track's own, the
// shorter of its header's and its sample table's.
//
// SEEKS ARE EXACT. An AAC frame depends on the one before it only through the
// overlap of the transform, so decoding starts one frame before the one that
// holds the sample, from nothing, and from there the samples are a straight
// decode's (but for PNS noise, which no iTunes file uses: random by design).
// An ALAC frame depends on nothing before it: decoding starts at the frame
// that holds the sample.
//
// The bytes arrive as for FLAC and MP3 (TrackBytes). The sample table is in
// the moov box, which an MP4 may keep at its end: then the whole file is
// waited for before the first sample, as it has to be.

using System.Text;

namespace Marginal.Codecs;

public sealed class Mp4Track : IAudioTrack
{
    public string Name { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample => _alac?.BitDepth ?? 0;  // AAC is lossy: no depth of its own
    public ReplayGain Gain { get; }
    public long TotalFrames => Math.Max(0, _end - _skip);
    public TimeSpan Duration => TimeSpan.FromSeconds((double)TotalFrames / SampleRate);

    /// What the gapless information said, for checking: priming dropped, and
    /// where the music ends, in decoded samples.
    public long Skip => _skip;
    public long End => _end;

    private readonly TrackBytes _bytes;
    private readonly byte[] _data;
    private readonly AacDecoder? _aac;          // one of the two
    private readonly AlacDecoder? _alac;
    private readonly int _frameLength;          // samples in a whole frame
    private readonly int _preroll;              // frames decoded before a seek's own
    private readonly long[] _offsets;           // each frame's place in the file
    private readonly int[] _sizes;
    private readonly long _skip;                // decoded samples dropped at the start
    private readonly long _end;                 // the decoded sample the music ends before

    private readonly float[] _pcm;
    private int _frame;                         // the next frame to decode
    private long _pcmAt;                        // the decoded sample _pcm starts at
    private int _pcmFrames, _pcmOffset;
    private long _from;                         // the first decoded sample to hand out
    private long _seekTo = -1;
    private volatile bool _interrupted;

    private Mp4Track(string name, TrackBytes bytes)
    {
        Name = name;
        _bytes = bytes;
        _data = bytes.Data;

        var moov = FindMoov();
        var movie = Parse(moov.Start, moov.End);
        if ((movie.Asc is null && movie.AlacCookie is null) || movie.Sizes is null || movie.ChunkOffsets is null)
            throw new UnsupportedAudioException("No AAC or ALAC track in this MP4 (another codec).");
        if (movie.AlacCookie is not null)
        {
            _alac = AlacDecoder.FromCookie(movie.AlacCookie);
            SampleRate = _alac.SampleRate;
            Channels = _alac.Channels;
            _frameLength = _alac.FrameLength;
        }
        else
        {
            _aac = AacDecoder.FromConfig(movie.Asc);
            SampleRate = _aac.SampleRate;
            Channels = _aac.Channels;
            _frameLength = AacDecoder.FrameLength;
            _preroll = 1;
        }
        _pcm = new float[_frameLength * Channels];
        (_offsets, _sizes) = SampleTable(movie);

        // The first frame decoded now and forgotten: what shows only inside the
        // frames (SBR in a fill element, a coupling channel) is refused here,
        // where another engine can take the track, not played as silence.
        if (_offsets.Length > 0)
        {
            _bytes.WaitFor((int)Math.Min(_data.Length, _offsets[0] + _sizes[0]));
            try { DecodeFrame((int)_offsets[0], _sizes[0]); }
            catch (FlacDecodeException) { }     // damaged, not unsupported: played as it comes
            _aac?.Reset();
        }

        // The length and the cuts, in decoded samples (FFmpeg's st->duration,
        // skip_samples and first_discard_sample).
        long ToSamples(long t) => movie.TrackTimescale == SampleRate ? t : Rescale(t, SampleRate, movie.TrackTimescale);
        long decoded = (long)_offsets.Length * _frameLength;
        long sttsTotal = movie.SttsTotal;
        long duration = movie.MdhdDuration > 0 ? Math.Min(movie.MdhdDuration, sttsTotal > 0 ? sttsTotal : long.MaxValue) : sttsTotal;
        long origin = 0, skip = 0;
        if (movie.EditMediaTime >= 0)
        {
            origin = movie.EditMediaTime;
            skip = movie.EditMediaTime;
            if (movie.EditsDuration > 0 && movie.MovieTimescale > 0)
                duration = Math.Min(duration, Rescale(movie.EditsDuration, movie.TrackTimescale, movie.MovieTimescale));
        }
        long firstDiscard = -1;     // in the timeline that starts at `origin`
        if (movie.Smpb is var (priming, remainder, samples))
        {
            if (priming > 0 && priming < 16384) skip = priming;
            if (remainder > 0 && duration > remainder && duration > samples) firstDiscard = duration - remainder;
        }
        if (firstDiscard < 0) firstDiscard = duration;

        _skip = ToSamples(skip);
        _end = Math.Min(decoded, ToSamples(origin + firstDiscard));
        _from = _skip;

        Gain = ReplayGain.From(movie.Tags);
    }

    /// a * b / c, rounded to nearest with halves away from zero, exactly:
    /// FFmpeg's av_rescale, which a double would round differently at times.
    private static long Rescale(long a, long b, long c) =>
        c <= 0 ? a : (long)(((Int128)a * b + (a < 0 ? -(c / 2) : c / 2)) / c);

    public static Mp4Track Open(string path) => new(path, TrackBytes.FromFile(path));
    public static Mp4Track Open(string name, string source) => new(name, TrackBytes.Open(name, source));

    // ------------------------------------------------------------ boxes

    /// The moov box, wherever it is among the top-level boxes: before the
    /// media (all of it is waited for only up to there) or after it.
    private (long Start, long End) FindMoov()
    {
        long at = 0;
        while (true)
        {
            _bytes.WaitFor((int)Math.Min(_data.Length, at + 16));
            if (at + 8 > _data.Length) throw new FlacDecodeException("No moov box in this MP4.");
            var (size, type, header) = BoxHeader(at, _data.Length);
            if (size < header) throw new FlacDecodeException("A broken MP4 box.");
            if (type == "moov")
            {
                _bytes.WaitFor((int)Math.Min(_data.Length, at + size));
                return (at + header, Math.Min(_data.Length, at + size));
            }
            at += size;
        }
    }

    private (long Size, string Type, int Header) BoxHeader(long at, long end)
    {
        long size = Be32(at);
        var type = Encoding.ASCII.GetString(_data, (int)at + 4, 4);
        var header = 8;
        if (size == 1) { size = (long)Be64(at + 8); header = 16; }
        else if (size == 0) size = end - at;
        return (size, type, header);
    }

    /// A full box's entry count (after its version and flags), when that many
    /// entries of `entry` bytes fit in the box; a broken file otherwise.
    private int Count(long body, long bodyEnd, int entry)
    {
        var count = Be32(body + 4);
        if (8 + (ulong)count * (ulong)entry > (ulong)(bodyEnd - body)) throw new FlacDecodeException("A broken MP4 sample table.");
        return (int)count;
    }

    private uint Be32(long i) => (uint)(_data[i] << 24 | _data[i + 1] << 16 | _data[i + 2] << 8 | _data[i + 3]);
    private ulong Be64(long i) => (ulong)Be32(i) << 32 | Be32(i + 4);
    private int Be16(long i) => _data[i] << 8 | _data[i + 1];

    private sealed class Movie
    {
        public long MovieTimescale, TrackTimescale, MdhdDuration, SttsTotal;
        public long EditMediaTime = -1, EditsDuration;
        public byte[]? Asc;
        public byte[]? AlacCookie;
        public int[]? Sizes;
        public int FixedSize;
        public int SampleCount;
        public long[]? ChunkOffsets;
        public (int FirstChunk, int SamplesPerChunk)[]? Stsc;
        public (int Priming, int Remainder, long Samples)? Smpb;
        public readonly Dictionary<string, string> Tags = new(StringComparer.Ordinal);
        public bool HaveAudio;
    }

    private Movie Parse(long start, long end)
    {
        var movie = new Movie();
        Walk(start, end, movie, null);
        return movie;
    }

    /// How deep boxes are followed. A real file goes six or seven deep
    /// (moov/udta/meta/ilst/©nam/data); a crafted 24 KB one nested 3,000 deep
    /// overflowed the stack, which no catch can stop (Marginal security review
    /// 2026-10-31, A1). Deeper boxes are ignored.
    private const int MaxDepth = 16;

    /// The boxes inside [start, end), those that matter, recursively. `trak`
    /// collects one track's boxes, kept only if it is the first audio track.
    private void Walk(long start, long end, Movie movie, Movie? trak, int depth = 0)
    {
        if (depth > MaxDepth) return;
        var at = start;
        while (at + 8 <= end)
        {
            var (size, type, header) = BoxHeader(at, end);
            if (size < header || at + size > end) break;
            long body = at + header, bodyEnd = at + size;
            switch (type)
            {
                case "mvhd":
                {
                    var version = _data[body];
                    movie.MovieTimescale = Be32(body + (version == 1 ? 20 : 12));
                    break;
                }
                case "trak":
                {
                    if (movie.HaveAudio) break;
                    var t = new Movie { MovieTimescale = movie.MovieTimescale };
                    Walk(body, bodyEnd, t, t, depth + 1);
                    if (t.HaveAudio && (t.Asc is not null || t.AlacCookie is not null))
                    {
                        movie.HaveAudio = true;
                        movie.TrackTimescale = t.TrackTimescale;
                        movie.MdhdDuration = t.MdhdDuration;
                        movie.SttsTotal = t.SttsTotal;
                        movie.EditMediaTime = t.EditMediaTime;
                        movie.EditsDuration = t.EditsDuration;
                        movie.Asc = t.Asc;
                        movie.AlacCookie = t.AlacCookie;
                        movie.Sizes = t.Sizes;
                        movie.FixedSize = t.FixedSize;
                        movie.SampleCount = t.SampleCount;
                        movie.ChunkOffsets = t.ChunkOffsets;
                        movie.Stsc = t.Stsc;
                    }
                    break;
                }
                case "mdia":
                case "minf":
                case "stbl":
                case "edts":
                case "udta":
                case "ilst":
                    Walk(body, bodyEnd, movie, trak, depth + 1);
                    break;
                case "meta":
                    // A full box in MP4 (version and flags first), not in QuickTime.
                    Walk(Be32(body) == 0 ? body + 4 : body, bodyEnd, movie, trak, depth + 1);
                    break;
                case "mdhd" when trak is not null:
                {
                    var version = _data[body];
                    trak.TrackTimescale = Be32(body + (version == 1 ? 20 : 12));
                    trak.MdhdDuration = version == 1 ? (long)Be64(body + 24) : Be32(body + 16);
                    break;
                }
                case "hdlr" when trak is not null:
                    if (Encoding.ASCII.GetString(_data, (int)body + 8, 4) == "soun") trak.HaveAudio = true;
                    break;
                case "elst" when trak is not null:
                {
                    var version = _data[body];
                    var count = Be32(body + 4);
                    var e = body + 8;
                    var found = false;
                    for (var i = 0; i < count && e + (version == 1 ? 20 : 12) <= bodyEnd; i++)
                    {
                        long segment, mediaTime;
                        if (version == 1) { segment = (long)Be64(e); mediaTime = (long)Be64(e + 8); e += 20; }
                        else { segment = Be32(e); mediaTime = (int)Be32(e + 4); e += 12; }
                        trak.EditsDuration += segment;
                        if (!found && mediaTime != -1) { trak.EditMediaTime = mediaTime; found = true; }
                    }
                    break;
                }
                case "stsd" when trak is not null:
                    SampleDescriptions(body + 8, bodyEnd, trak);
                    break;
                case "stts" when trak is not null:
                {
                    var count = Be32(body + 4);
                    long total = 0;
                    for (long i = 0, e = body + 8; i < count && e + 8 <= bodyEnd; i++, e += 8) total += (long)Be32(e) * Be32(e + 4);
                    trak.SttsTotal = total;
                    break;
                }
                case "stsz" when trak is not null:
                {
                    // Every count in the sample tables is held to the bytes
                    // there are before anything is made from it (security
                    // review 2026-10-26, finding 3): a 148-byte file claiming
                    // 2^31 samples asked for 8 GB, and stco and stsc read on
                    // past their box and the file.
                    var fixedSize = Be32(body + 4);
                    var sampleCount = Be32(body + 8);
                    if (fixedSize > int.MaxValue || sampleCount > (ulong)_data.Length
                        || (fixedSize == 0 && 12 + 4 * (ulong)sampleCount > (ulong)(bodyEnd - body))
                        || (ulong)sampleCount * fixedSize > (ulong)_data.Length)
                        throw new FlacDecodeException("A broken MP4 sample size table.");
                    trak.FixedSize = (int)fixedSize;
                    trak.SampleCount = (int)sampleCount;
                    if (trak.FixedSize == 0)
                    {
                        var sizes = new int[trak.SampleCount];
                        for (var i = 0; i < sizes.Length; i++) sizes[i] = (int)Be32(body + 12 + i * 4);
                        trak.Sizes = sizes;
                    }
                    else
                    {
                        trak.Sizes = [];
                    }
                    break;
                }
                case "stsc" when trak is not null:
                {
                    var count = Count(body, bodyEnd, 12);
                    var stsc = new (int, int)[count];
                    for (var i = 0; i < count; i++) stsc[i] = ((int)Be32(body + 8 + i * 12), (int)Be32(body + 12 + i * 12));
                    trak.Stsc = stsc;
                    break;
                }
                case "stco" when trak is not null:
                case "co64" when trak is not null:
                {
                    var count = Count(body, bodyEnd, type == "stco" ? 4 : 8);
                    var offsets = new long[count];
                    for (var i = 0; i < count; i++) offsets[i] = type == "stco" ? Be32(body + 8 + i * 4) : (long)Be64(body + 8 + i * 8);
                    trak.ChunkOffsets = offsets;
                    break;
                }
                case "----":
                    Freeform(body, bodyEnd, movie);
                    break;
            }
            at += size;
        }
    }

    /// The first audio sample description: an mp4a's esds, for its
    /// AudioSpecificConfig, or an alac's magic cookie.
    private void SampleDescriptions(long start, long end, Movie trak)
    {
        var at = start;
        while (at + 8 <= end)
        {
            var (size, type, header) = BoxHeader(at, end);
            if (size < header || at + size > end) break;
            if (type is "mp4a" or "alac")
            {
                var body = at + header;
                var version = Be16(body + 8);           // QuickTime sound description version
                var children = body + 28 + version switch { 1 => 16, 2 => 36, _ => 0 };
                if (type == "mp4a") Esds(children, at + size, trak);
                else AlacCookie(children, at + size, trak);
                return;
            }
            at += size;
        }
    }

    /// The ALAC magic cookie (its ALACSpecificConfig, and a channel layout
    /// after it when there is one): in an 'alac' full box after the sample
    /// entry, or, as older encoders wrote it, in one inside a 'wave' box
    /// (ALACMagicCookieDescription.txt).
    private void AlacCookie(long start, long end, Movie trak)
    {
        var at = start;
        while (at + 8 <= end)
        {
            var (size, type, header) = BoxHeader(at, end);
            if (size < header || at + size > end) break;
            if (type == "wave") { AlacCookie(at + header, at + size, trak); return; }
            if (type == "alac")
            {
                var body = at + header + 4;             // version and flags
                if (body < at + size) trak.AlacCookie = _data.AsSpan((int)body, (int)(at + size - body)).ToArray();
                return;
            }
            at += size;
        }
    }

    private void Esds(long start, long end, Movie trak)
    {
        var at = start;
        while (at + 8 <= end)
        {
            var (size, type, header) = BoxHeader(at, end);
            if (size < header || at + size > end) break;
            if (type == "wave") { Esds(at + header, at + size, trak); return; }
            if (type == "esds")
            {
                var d = at + header + 4;                // version and flags
                var e = at + size;
                if (Descriptor(ref d, e) != 0x03) return;
                d += 2;                                 // ES_ID
                var flags = _data[d++];
                if ((flags & 0x80) != 0) d += 2;
                if ((flags & 0x40) != 0) d += 1 + _data[d];
                if ((flags & 0x20) != 0) d += 2;
                if (Descriptor(ref d, e) != 0x04) return;
                var oti = _data[d];
                if (oti is not (0x40 or 0x66 or 0x67 or 0x68)) return;      // MPEG-4 audio, MPEG-2 AAC
                d += 13;
                var tag = Descriptor(ref d, e, out var length);
                if (tag != 0x05 || d + length > e) return;
                trak.Asc = _data.AsSpan((int)d, length).ToArray();
                return;
            }
            at += size;
        }
    }

    private int Descriptor(ref long d, long end) => Descriptor(ref d, end, out _);

    /// An MPEG-4 descriptor's tag and length (up to four bytes of seven bits).
    private int Descriptor(ref long d, long end, out int length)
    {
        length = 0;
        if (d >= end) return -1;
        int tag = _data[d++];
        for (var i = 0; i < 4 && d < end; i++)
        {
            int b = _data[d++];
            length = length << 7 | (b & 0x7F);
            if ((b & 0x80) == 0) break;
        }
        return tag;
    }

    /// A freeform iTunes tag (mean, name, data), as FFmpeg's mov_read_custom
    /// reads one: iTunSMPB for gapless, the rest kept by name (ReplayGain).
    private void Freeform(long start, long end, Movie movie)
    {
        string? mean = null, key = null, value = null;
        var at = start;
        for (var i = 0; i < 3 && end - at > 12; i++)
        {
            var len = Be32(at);
            var tag = Encoding.ASCII.GetString(_data, (int)at + 4, 4);
            var p = at + 12;                            // length, tag, version and flags
            if (len < 12 || at + len > end) break;
            var n = (int)len - 12;
            if (tag == "data" && n > 4) { p += 4; n -= 4; }
            else if (tag is not ("mean" or "name")) break;
            var text = Encoding.UTF8.GetString(_data, (int)p, n);
            if (tag == "mean") mean = text;
            else if (tag == "name") key = text;
            else value = text;
            at += len;
        }
        if (mean is null || key is null || value is null) return;
        if (key == "iTunSMPB")
        {
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4
                && int.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var priming)
                && int.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out var remainder)
                && long.TryParse(parts[3], System.Globalization.NumberStyles.HexNumber, null, out var samples))
                movie.Smpb = (priming, remainder, samples);
        }
        if (key != "cdec") movie.Tags.TryAdd(key.ToUpperInvariant(), value);
    }

    /// Where every frame is, from the chunk offsets and the sample-to-chunk
    /// runs.
    private static (long[] Offsets, int[] Sizes) SampleTable(Movie m)
    {
        var count = m.FixedSize != 0 ? m.SampleCount : m.Sizes!.Length;
        var offsets = new long[count];
        var sizes = new int[count];
        var stsc = m.Stsc ?? [(1, count)];
        var chunks = m.ChunkOffsets!;
        var sample = 0;
        for (var r = 0; r < stsc.Length && sample < count; r++)
        {
            var firstChunk = stsc[r].FirstChunk - 1;
            var lastChunk = r + 1 < stsc.Length ? stsc[r + 1].FirstChunk - 1 : chunks.Length;
            for (var c = firstChunk; c < lastChunk && c < chunks.Length && sample < count; c++)
            {
                var at = chunks[c];
                for (var s = 0; s < stsc[r].SamplesPerChunk && sample < count; s++)
                {
                    var size = m.FixedSize != 0 ? m.FixedSize : m.Sizes![sample];
                    offsets[sample] = at;
                    sizes[sample] = size;
                    at += size;
                    sample++;
                }
            }
        }
        return (offsets[..sample], sizes[..sample]);
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
        if (_bytes.Done || _frame >= _offsets.Length) return;
        var last = Math.Min(_offsets.Length - 1, _frame + frames / _frameLength + 1);
        _bytes.WaitFor((int)Math.Min(_data.Length, _offsets[last] + _sizes[last]), () => _interrupted);
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
                if (_pcmAt + _pcmFrames >= _end && _pcmFrames > 0) break;
                if (!DecodeNext()) break;
                continue;
            }
            var s = _pcmAt + _pcmOffset;
            if (s >= _end) { _pcmOffset = _pcmFrames; break; }
            if (s < _from)
            {
                _pcmOffset += (int)Math.Min(_from - s, _pcmFrames - _pcmOffset);
                continue;
            }
            var n = (int)Math.Min(Math.Min(want - done, _pcmFrames - _pcmOffset), _end - s);
            _pcm.AsSpan(_pcmOffset * channels, n * channels).CopyTo(into[(done * channels)..]);
            _pcmOffset += n;
            done += n;
        }
        return done;
    }

    /// The next frame into _pcm. False at the end, or when its bytes will not
    /// arrive, or the wait for them is interrupted.
    private bool DecodeNext()
    {
        if (_frame >= _offsets.Length) return false;
        long at = _offsets[_frame];
        var size = _sizes[_frame];
        if (!_bytes.WaitFor((int)Math.Min(_data.Length, at + size), () => _interrupted) || _interrupted) return false;
        if (at + size > _data.Length) return false;
        int frames;
        try
        {
            frames = DecodeFrame((int)at, size);
        }
        catch (IOException)
        {
            // A damaged frame still takes its place in time.
            Array.Clear(_pcm);
            frames = _frameLength;
        }
        _pcmAt = (long)_frame * _frameLength;
        _pcmFrames = frames;
        _pcmOffset = 0;
        _frame++;
        return true;
    }

    /// One frame into _pcm, by whichever codec the track is: its length in
    /// samples (an ALAC track's last frame is short).
    private int DecodeFrame(int at, int size)
    {
        if (_alac is not null) return _alac.DecodeFrame(_data, at, size, _pcm);
        _aac!.DecodeFrame(_data, at, size, _pcm);
        return AacDecoder.FrameLength;
    }

    /// The seek asked for, done: for AAC, decoding from one frame before the
    /// one that holds the sample, the overlap from before forgotten; for ALAC,
    /// from that frame itself.
    private void SeekNow()
    {
        var to = _seekTo;
        _seekTo = -1;
        var target = to + _skip;
        var frame = (int)Math.Min(_offsets.Length, target / _frameLength);
        var from = Math.Max(0, frame - _preroll);
        _aac?.Reset();
        _frame = from;
        _pcmAt = (long)from * _frameLength;
        _pcmFrames = _pcmOffset = 0;
        _from = Math.Max(_skip, target);
    }
}
