// COPIED from AlbumWall (src/AlbumWall.Codecs/FlacTrack.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — one FLAC file open for playing.
//
// The whole file is held in memory: 20-40 MB for a CD track, gone again when
// the track is. That makes the decoder's input a plain array, which is what
// Deadwax's decoder takes (Flac.cs), and it means a track boundary never waits
// on the disk or the network: the next track is opened ahead of time
// (HymieEngine).
//
// The file arrives while it plays, from the disk or a server (TrackBytes),
// so every frame waits for its own bytes before it is
// decoded, and a seek is only noted when asked and done at the next Read, on
// the output's thread, where waiting for the bytes it lands in holds up
// nothing but the sound.

namespace Marginal.Codecs;

public sealed class FlacTrack : IAudioTrack
{
    /// The track's name: its file, or its library path for a server's track.
    /// Never the address it is fetched from, which carries the sign-in.
    public string Name { get; }
    public FlacStreamInfo Info { get; }
    public ReplayGain Gain { get; }

    public int SampleRate => Info.SampleRate;
    public int Channels => Info.Channels;
    public int BitsPerSample => Info.BitsPerSample;
    public long TotalFrames => Info.TotalSamples;

    private readonly TrackBytes _bytes;
    private readonly byte[] _data;
    private readonly int _framesStart;
    private readonly int _frameBytes;   // the most one frame can take
    private readonly float _scale;

    private int _at;                    // the next frame to decode
    private long[][] _channels;         // the last frame decoded
    private int _frameLength;           // its length in samples
    private int _frameOffset;           // how much of it has been handed out
    private long _seekTo = -1;          // a seek asked for and not yet done
    private volatile bool _interrupted;

    private FlacTrack(string name, TrackBytes bytes)
    {
        Name = name;
        _bytes = bytes;
        _data = bytes.Data;
        Info = Metadata(bytes, out _framesStart, out var tags);
        Gain = ReplayGain.From(tags);
        if (Info.Channels > 8) throw new FlacDecodeException("More channels than FLAC has.");
        _scale = 1f / (1L << (Info.BitsPerSample - 1));
        _channels = new long[Info.Channels][];
        for (var c = 0; c < Info.Channels; c++) _channels[c] = new long[Math.Max(Info.MaxBlockSize, 1)];
        // STREAMINFO says, or else the most a frame could be: every sample
        // stored verbatim, a bit to spare for each, and the header.
        _frameBytes = Info.MaxFrameSize > 0
            ? Info.MaxFrameSize
            : Math.Max(Info.MaxBlockSize, 1) * Info.Channels * (Info.BitsPerSample / 8 + 1) + 64;
        _at = _framesStart;
    }

    public static FlacTrack Open(string path) => new(path, TrackBytes.FromFile(path));

    /// A track by name, from where it is: a file, or a server's address
    /// (http or https), which is fetched and not kept.
    public static FlacTrack Open(string name, string source) => new(name, TrackBytes.Open(name, source));

    /// The metadata, as soon as enough of the file has arrived to hold it: a
    /// picture in it can make that a few megabytes.
    private static FlacStreamInfo Metadata(TrackBytes bytes, out int framesStart, out Dictionary<string, string> tags)
    {
        var want = 64 * 1024;
        while (true)
        {
            bytes.WaitFor(want);
            var have = bytes.Available;
            var data = have == bytes.Length ? bytes.Data : bytes.Data.AsSpan(0, have).ToArray();
            try { return FlacDecoder.ReadMetadata(data, out framesStart, out tags); }
            catch (FlacDecodeException) when (have < bytes.Length && !bytes.Done)
            {
                want = Math.Max(have * 2, have + 64 * 1024);
            }
        }
    }

    /// Stops a wait for bytes that have not arrived, so that the output can be
    /// thrown away; the next Seek undoes it.
    public void Interrupt() => _interrupted = true;
    public bool Interrupted => _interrupted;

    /// Outside the engine's lock, before a Read of `frames`: the seek asked
    /// for is done, and the bytes those frames will come from waited for, so
    /// the Read itself does not wait while the engine is held.
    public void Prepare(int frames)
    {
        if (_seekTo >= 0) SeekNow();
        if (_bytes.Done) return;
        var left = frames - (_frameLength - _frameOffset);
        if (left <= 0) return;
        var count = left / Math.Max(Info.MaxBlockSize, 1) + 2;
        _bytes.WaitFor((int)Math.Min(_data.Length, _at + (long)count * _frameBytes), () => _interrupted);
    }

    public TimeSpan Duration => Info.TotalSamples > 0 && Info.SampleRate > 0
        ? TimeSpan.FromSeconds((double)Info.TotalSamples / Info.SampleRate)
        : TimeSpan.Zero;

    /// The next samples, interleaved, as floats from -1 to 1, into `into`
    /// (whose length is a whole number of frames). Returns the frames written;
    /// fewer than asked for only at the end of the file.
    public int Read(Span<float> into)
    {
        if (_seekTo >= 0) SeekNow();
        var channels = Info.Channels;
        var want = into.Length / channels;
        var done = 0;
        while (done < want)
        {
            if (_frameOffset >= _frameLength)
            {
                if (_at >= _data.Length || !DecodeNext()) break;
                continue;
            }
            var n = Math.Min(want - done, _frameLength - _frameOffset);
            for (var i = 0; i < n; i++)
            {
                var o = (done + i) * channels;
                for (var c = 0; c < channels; c++) into[o + c] = _channels[c][_frameOffset + i] * _scale;
            }
            _frameOffset += n;
            done += n;
        }
        return done;
    }

    /// False at the end: what follows the last frame is not another frame (an
    /// ID3v1 tag left by a tagger, or padding). Also false when the bytes of
    /// the next frame will not arrive, or the wait for them is interrupted.
    private bool DecodeNext()
    {
        _bytes.WaitFor((int)Math.Min(_data.Length, (long)_at + _frameBytes), () => _interrupted);
        if (_interrupted) return false;
        var end = Readable;
        if (_at + 16 > end && end < _data.Length) return false;     // the header has not all arrived, and will not
        if (!FlacDecoder.TryReadHeader(_data, _at, Info, out _, out _)) { _at = _data.Length; return false; }
        // Every frame must be what STREAMINFO says the stream is: its channel
        // count (the header's channel code, read before the frame is decoded)
        // and no longer than its largest block. Here a mismatch gave wrong audio
        // rather than a read out of range (DecodeFrame grows its buffers, Read
        // takes Info.Channels), but it is a broken file all the same, refused as
        // Deadwax refuses it (security review 2026-10-26, L14; Deadwax L2).
        var code = _data[_at + 3] >> 4;
        if ((code < 8 ? code + 1 : 2) != Info.Channels)
            throw new FlacDecodeException($"The frame at byte {_at} has a different number of channels from STREAMINFO.");
        var at = _at;
        _at += FlacDecoder.DecodeFrame(_data, _at, end, Info, ref _channels, out _frameLength);
        if (Info.MaxBlockSize > 0 && _frameLength > Info.MaxBlockSize)
            throw new FlacDecodeException($"The frame at byte {at} is longer than STREAMINFO's largest block.");
        _frameOffset = 0;
        return true;
    }

    /// How far the bytes can be read: all of them, or what has arrived.
    private int Readable => _bytes.Available;

    /// Puts the cursor on sample `to`, exactly, at the next Read: the frame
    /// holding it is found by its header, without decoding what comes before
    /// (a binary search over the bytes, each guess checked by the header that
    /// follows it), then decoded, and what precedes the sample is skipped.
    public void Seek(long to)
    {
        _interrupted = false;
        _frameLength = _frameOffset = 0;
        _at = _framesStart;
        _seekTo = to > 0 ? to : -1;
    }

    private void SeekNow()
    {
        var to = _seekTo;
        _seekTo = -1;
        _frameLength = _frameOffset = 0;
        _at = _framesStart;
        if (to <= 0) return;

        var total = Info.TotalSamples;
        if (!_bytes.Done && total > 0)
        {
            // A server's track still arriving: wait for about where the sample
            // is (the bytes are roughly even over the track) and a little
            // more, then search what is here. Short of it, the decoding below
            // walks on to it, waiting as it goes.
            var guess = _framesStart + (long)((double)to / total * (_data.Length - _framesStart));
            _bytes.WaitFor((int)Math.Min(_data.Length, guess + 512 * 1024), () => _interrupted);
        }

        var end = Readable;
        long lo = _framesStart, hi = end;
        var from = _framesStart;
        var fromSample = 0L;
        for (var tries = 0; tries < 48 && hi - lo > 16 && total > 0; tries++)
        {
            var mid = (int)((lo + hi) / 2);
            if (FindFrame(mid, end) is not var (at, first, size)) { hi = mid; continue; }
            if (first > to) { hi = mid; continue; }
            from = at;
            fromSample = first;
            if (first + size > to) break;       // this frame holds it
            lo = at + 1;
        }

        _at = from;
        var skip = to - fromSample;
        while (skip > 0 && DecodeNext())
        {
            if (skip < _frameLength) { _frameOffset = (int)skip; return; }
            skip -= _frameLength;
        }
    }

    /// The first frame header at or after `from` that the next header agrees
    /// with (its first sample is this one's first plus its size), so that a
    /// sync pattern inside a frame's audio is not taken for a frame.
    /// Only the first `end` bytes are looked at: past them, a server's track
    /// may not have arrived yet.
    private (int At, long First, int Size)? FindFrame(int from, int end)
    {
        var limit = Info.MaxFrameSize > 0 ? Info.MaxFrameSize + 64 : 1 << 20;
        var stop = end == _data.Length ? end - 1 : end - 16;   // a header is never read half arrived
        for (var i = from; i < stop; i++)
        {
            if (_data[i] != 0xFF || !FlacDecoder.TryReadHeader(_data, i, Info, out var first, out var size)) continue;
            if (Info.TotalSamples > 0 && first + size >= Info.TotalSamples) return (i, first, size);
            var within = (int)Math.Min(stop, (long)i + limit);
            for (var j = i + 2; j < within; j++)
                if (_data[j] == 0xFF && FlacDecoder.TryReadHeader(_data, j, Info, out var next, out _) && next == first + size)
                    return (i, first, size);
        }
        return null;
    }
}
