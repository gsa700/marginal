// COPIED from AlbumWall (src/AlbumWall.Codecs/WavTrack.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — one WAV file open for playing (his call, 2026-10-22, with mpv
// going: "WAV, ALAC").
//
// There is no codec: the samples are stored as they are, so this is only the
// RIFF chunks around them. `fmt ` says what they are: whole numbers of 8, 16,
// 24 or 32 bits (8 unsigned, as the format has it, the rest signed), or 32- or
// 64-bit floats, plainly or in WAVE_FORMAT_EXTENSIBLE's dress. `data` is the
// samples; a size of 0 or 0xFFFFFFFF (a recorder that never came back to fill
// it in) means to the end of the file. Seeks are a multiply.
//
// Tags: an `id3 ` chunk ahead of the samples is read for ReplayGain. One after
// them is not: waiting for the end of the file before the first sample would
// undo playing a server's track as it arrives, and WAV with ReplayGain is rare.
//
// The bytes arrive as they do for the other formats (TrackBytes).

namespace Marginal.Codecs;

public sealed class WavTrack : IAudioTrack
{
    public string Name { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }
    public ReplayGain Gain { get; }
    public long TotalFrames { get; }
    public TimeSpan Duration => TimeSpan.FromSeconds((double)TotalFrames / SampleRate);

    private enum Kind { Unsigned8, Signed, Float }

    private readonly TrackBytes _bytes;
    private readonly byte[] _data;
    private readonly Kind _kind;
    private readonly int _bytesPerSample;       // as stored: 24-bit is 3, float64 is 8
    private readonly int _blockAlign;           // one frame, every channel
    private readonly long _start;               // the first sample's byte
    private long _frame;                        // the next frame to hand out
    private long _seekTo = -1;
    private volatile bool _interrupted;

    private const ushort Pcm = 1, IeeeFloat = 3, Extensible = 0xFFFE;

    private WavTrack(string name, TrackBytes bytes)
    {
        Name = name;
        _bytes = bytes;
        _data = bytes.Data;

        _bytes.WaitFor(12);
        if (_bytes.Available < 12 || !Is(0, "RIFF") || !Is(8, "WAVE"))
            throw new UnsupportedAudioException(Is(0, "RF64") ? "RF64, not RIFF WAV." : "Not a RIFF WAVE file.");

        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        ushort format = 0, channels = 0, bits = 0, align = 0;
        uint rate = 0;
        var haveFormat = false;
        long at = 12;
        while (true)
        {
            _bytes.WaitFor((int)Math.Min(_data.Length, at + 8));
            if (at + 8 > _bytes.Available) throw new FlacDecodeException("No samples in this WAV.");
            var id = System.Text.Encoding.ASCII.GetString(_data, (int)at, 4);
            long size = BitConverter.ToUInt32(_data, (int)at + 4);
            var body = at + 8;

            if (id == "data")
            {
                if (!haveFormat) throw new FlacDecodeException("WAV samples before their format.");
                if (size == 0 || size == uint.MaxValue || body + size > _data.Length) size = _data.Length - body;
                _start = body;
                _blockAlign = align;
                TotalFrames = size / align;
                break;
            }

            _bytes.WaitFor((int)Math.Min(_data.Length, body + size));
            if (id == "fmt " && size >= 16)
            {
                format = BitConverter.ToUInt16(_data, (int)body);
                channels = BitConverter.ToUInt16(_data, (int)body + 2);
                rate = BitConverter.ToUInt32(_data, (int)body + 4);
                align = BitConverter.ToUInt16(_data, (int)body + 12);
                bits = BitConverter.ToUInt16(_data, (int)body + 14);
                // EXTENSIBLE carries the real format in the first two bytes of
                // its sub-format GUID, which is PCM's or float's base GUID.
                if (format == Extensible && size >= 40) format = BitConverter.ToUInt16(_data, (int)body + 24);
                haveFormat = true;
            }
            else if (id is "id3 " or "ID3 ")
            {
                var end = (int)Math.Min(_bytes.Available, body + size);
                if (Id3.SizeAt(_data, (int)body, end) > 0) Id3.Measure(_data, (int)body, end, tags);
            }
            at = body + size + (size & 1);      // chunks are padded to an even length
        }

        if (channels is 0 or > 8 || rate == 0 || align == 0)
            throw new FlacDecodeException("A WAV format that makes no sense.");
        _bytesPerSample = align / channels;
        (_kind, BitsPerSample) = (format, bits, _bytesPerSample) switch
        {
            (Pcm, 8, 1) => (Kind.Unsigned8, 8),
            (Pcm, > 8 and <= 32, 2 or 3 or 4) => (Kind.Signed, (int)bits),
            (IeeeFloat, 32, 4) or (IeeeFloat, 64, 8) => (Kind.Float, 0),
            _ => throw new UnsupportedAudioException($"WAV format {format}, {bits}-bit: not plain samples."),
        };
        SampleRate = (int)rate;
        Channels = channels;
        Gain = ReplayGain.From(tags);
    }

    private bool Is(int at, string id) =>
        at + 4 <= _data.Length && _data[at] == id[0] && _data[at + 1] == id[1] && _data[at + 2] == id[2] && _data[at + 3] == id[3];

    public static WavTrack Open(string path) => new(path, TrackBytes.FromFile(path));
    public static WavTrack Open(string name, string source) => new(name, TrackBytes.Open(name, source));

    public void Interrupt() => _interrupted = true;
    public bool Interrupted => _interrupted;

    public void Seek(long frame)
    {
        _interrupted = false;
        _seekTo = Math.Clamp(frame, 0, TotalFrames);
    }

    public void Prepare(int frames)
    {
        if (_seekTo >= 0) { _frame = _seekTo; _seekTo = -1; }
        _bytes.WaitFor((int)Math.Min(_data.Length, _start + (_frame + frames) * _blockAlign), () => _interrupted);
    }

    public int Read(Span<float> into)
    {
        if (_seekTo >= 0) { _frame = _seekTo; _seekTo = -1; }
        var want = (int)Math.Min(into.Length / Channels, TotalFrames - _frame);
        if (want <= 0) return 0;
        _bytes.WaitFor((int)Math.Min(_data.Length, _start + (_frame + want) * _blockAlign), () => _interrupted);
        // What has arrived, in whole frames: short only if the rest never will.
        want = (int)Math.Min(want, (_bytes.Available - _start) / _blockAlign - _frame);
        if (want <= 0) return 0;

        var src = _data.AsSpan((int)(_start + _frame * _blockAlign), want * _blockAlign);
        var n = want * Channels;
        switch (_kind, _bytesPerSample)
        {
            case (Kind.Unsigned8, _):
                for (var i = 0; i < n; i++) into[i] = (src[i] - 128) / 128f;
                break;
            case (Kind.Signed, 2):
                for (var i = 0; i < n; i++) into[i] = (short)(src[2 * i] | src[2 * i + 1] << 8) / 32768f;
                break;
            case (Kind.Signed, 3):
                for (var i = 0; i < n; i++)
                    into[i] = (src[3 * i] << 8 | src[3 * i + 1] << 16 | src[3 * i + 2] << 24) / 2147483648f;
                break;
            case (Kind.Signed, 4):
                for (var i = 0; i < n; i++) into[i] = BitConverter.ToInt32(src[(4 * i)..]) / 2147483648f;
                break;
            case (Kind.Float, 4):
                for (var i = 0; i < n; i++) into[i] = BitConverter.ToSingle(src[(4 * i)..]);
                break;
            case (Kind.Float, 8):
                for (var i = 0; i < n; i++) into[i] = (float)BitConverter.ToDouble(src[(8 * i)..]);
                break;
        }
        _frame += want;
        return want;
    }
}
