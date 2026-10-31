// COPIED from AlbumWall (src/AlbumWall.Playback/PulseOutput.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — sound out on Linux, through PulseAudio's simple API (roadmap,
// "Hymie, our own audio engine", step 2a, 2026-10-22).
//
// libpulse-simple, which PipeWire serves as well as PulseAudio does, so this is
// a stream like any other app's: the device stays shared ("Desktop audio: be a
// good neighbour", docs/roadmap.md), at the file's own sample rate, which
// PipeWire follows where its allowed rates include it, as it does for mpv.
//
// The simple API is a blocking write and little else: no pause, no volume. So
// one thread of our own does every call, writing 1024 frames at a time into a
// server-side buffer of 0.2 s, which rides out a pause of the app's collector
// as the Mac's queue does. What it lacks is made here:
//
//   * Pause: what the server still holds is thrown away at once (a flush), and
//     kept here, to be written first when play resumes. So a pause is heard
//     within a write, and across it nothing is lost; under a millisecond is
//     heard twice, as near as the server's latency can be read.
//   * Volume: a multiply as the samples are written, so a change is heard
//     after what is already buffered, 0.2 s, not at once as on the Mac.
//   * Played: written minus the server's latency, asked after every write and
//     run on by the clock in between.
//
// Nothing bundled: libpulse-simple.so.0 is on every desktop that has sound.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Marginal.Playback;

internal sealed unsafe class PulseOutput : IOutput
{
    private const int Chunk = 1024;             // frames per write
    private const double Buffered = 0.2;        // seconds the server holds

    public int SampleRate { get; }
    public int Channels { get; }

    private readonly IOutput.Source _source;
    private readonly object _gate = new();      // Monitor: the writer waits on it while paused
    private Thread? _thread;
    private volatile bool _wantPlaying;
    private volatile bool _stopping;
    private volatile bool _ended;               // the source has said it has no more
    private volatile bool _drained;
    private volatile float _volume = 1f;

    // On the writer thread only.
    private bool _live;                         // samples are with the server
    private long _written;                      // frames with the server, net of flushes
    private readonly float[] _history;          // the last second written, before the volume
    private float[] _pending = [];              // flushed at a pause, written first after it
    private int _pendingAt;

    // The timeline, under _gate: Played was _base at _baseAt, and runs on
    // from there while _running, never past _written.
    private long _base, _limit;
    private long _baseAt;
    private bool _running;

    public PulseOutput(int sampleRate, int channels, IOutput.Source source)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _source = source;
        _history = new float[sampleRate * channels];
    }

    /// Whether the client library is here to be loaded.
    public static bool IsAvailable => _available.Value;
    private static readonly Lazy<bool> _available = new(() =>
    {
        if (!NativeLibrary.TryLoad(Simple, out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    });

    /// The server is connected to on the first Start, not before, so a record
    /// restored paused holds no stream open.
    public void Start()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _wantPlaying = true;
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "engine output" };
                _thread.Start();
            }
            Monitor.PulseAll(_gate);
        }
    }

    /// Heard within a write (23 ms at 44.1 kHz), when the writer next looks.
    /// The clock stops now, so the position stands still from the moment asked.
    public void Pause()
    {
        lock (_gate)
        {
            _base = Played;
            _running = false;
            _wantPlaying = false;
        }
    }

    public void SetVolume(float gain) => _volume = gain;

    public long Played
    {
        get
        {
            lock (_gate)
            {
                if (!_running) return _base;
                var ran = (long)(Stopwatch.GetElapsedTime(_baseAt).TotalSeconds * SampleRate);
                return Math.Min(_base + ran, _limit);
            }
        }
    }

    public bool Drained => _drained;

    private void Run()
    {
        var spec = new SampleSpec { Format = 5 /* PA_SAMPLE_FLOAT32LE */, Rate = (uint)SampleRate, Channels = (byte)Channels };
        var bytesPerFrame = (uint)(4 * Channels);
        var attr = new BufferAttr
        {
            MaxLength = uint.MaxValue,
            TLength = (uint)(Buffered * SampleRate) * bytesPerFrame,
            PreBuf = uint.MaxValue,             // as much as TLength: nothing plays until it is full
            MinReq = Chunk * bytesPerFrame,
            FragSize = uint.MaxValue,
        };
        int error;
        var s = pa_simple_new(IntPtr.Zero, "AlbumWall", 1 /* PA_STREAM_PLAYBACK */, IntPtr.Zero, "Music", &spec, IntPtr.Zero, &attr, &error);
        if (s == IntPtr.Zero)
        {
            Console.WriteLine($"[engine] output: cannot open a PulseAudio stream: {Error(error)}");
            _ended = _drained = true;
            return;
        }

        var buf = new float[Chunk * Channels];
        var loud = new float[Chunk * Channels];
        try
        {
            while (!_stopping)
            {
                if (!_wantPlaying || _drained)
                {
                    if (_live) Hold(s);
                    lock (_gate)
                        while ((!_wantPlaying || _drained) && !_stopping) Monitor.Wait(_gate);
                    continue;
                }

                int frames;
                if (_pendingAt < _pending.Length)
                {
                    var n = Math.Min(buf.Length, _pending.Length - _pendingAt);
                    _pending.AsSpan(_pendingAt, n).CopyTo(buf);
                    _pendingAt += n;
                    frames = n / Channels;
                }
                else if (!_ended)
                {
                    frames = _source(buf);
                    if (frames < Chunk) _ended = true;
                }
                else
                {
                    // Everything is written: let it play out.
                    if (pa_simple_drain(s, &error) < 0) Console.WriteLine($"[engine] output: drain: {Error(error)}");
                    lock (_gate) { _base = _limit = _written; _running = false; }
                    _live = false;
                    _drained = true;
                    continue;
                }
                if (frames == 0) continue;

                Remember(buf.AsSpan(0, frames * Channels));
                var gain = _volume;
                for (var i = 0; i < frames * Channels; i++) loud[i] = buf[i] * gain;
                fixed (float* p = loud)
                    if (pa_simple_write(s, p, (nuint)(frames * bytesPerFrame), &error) < 0)
                    {
                        Console.WriteLine($"[engine] output: write: {Error(error)}");
                        _ended = _drained = true;
                        break;
                    }
                _written += frames;
                _live = true;
                Clock(s);
            }
        }
        catch (Exception ex)
        {
            // The engine's thread, not the app's: say so and stop, no more.
            Console.WriteLine($"[engine] output: {ex.Message}");
            _ended = _drained = true;
        }
        finally
        {
            pa_simple_flush(s, &error);
            pa_simple_free(s);
        }
    }

    /// Where the speakers are: what has been written, less what the server
    /// says it still has to play.
    private void Clock(IntPtr s)
    {
        int error;
        var latency = pa_simple_get_latency(s, &error);
        if (latency == ulong.MaxValue) return;
        var behind = Frames(latency);
        lock (_gate)
        {
            if (!_wantPlaying) return;      // a pause has stopped the clock
            _base = Math.Clamp(_written - behind, Math.Min(_base, _written), _written);
            _baseAt = Stopwatch.GetTimestamp();
            _limit = _written;
            _running = true;
        }
    }

    /// A pause: what the server has not played yet is thrown away and kept,
    /// ahead of anything still kept from an earlier pause.
    private void Hold(IntPtr s)
    {
        // The latency counts what has already gone on to the device as well,
        // which a flush cannot call back (it played out after the pause, 32 ms
        // of it, heard twice when this kept the whole latency): so only what
        // the flush took away is kept, the latency before it less the latency
        // after it.
        int error;
        var before = pa_simple_get_latency(s, &error);
        pa_simple_flush(s, &error);
        var after = pa_simple_get_latency(s, &error);
        var unplayed = before == ulong.MaxValue ? 0 : Frames(before) - (after == ulong.MaxValue ? 0 : Frames(after));
        unplayed = Math.Clamp(unplayed, 0, Math.Min(_written, _history.Length / Channels));

        var rest = _pending.Length - _pendingAt;
        var kept = new float[unplayed * Channels + rest];
        var cap = _history.Length;
        var from = (int)((_written - unplayed) * Channels % cap);
        for (var i = 0; i < unplayed * Channels; i++) kept[i] = _history[(from + i) % cap];
        _pending.AsSpan(_pendingAt, rest).CopyTo(kept.AsSpan((int)(unplayed * Channels)));
        _pending = kept;
        _pendingAt = 0;

        _written -= unplayed;
        _live = false;
        lock (_gate) { _base = _limit = _written; _running = false; }
    }

    private long Frames(ulong microseconds) => (long)(microseconds * (ulong)SampleRate / 1_000_000);

    /// The last second written, for Hold.
    private void Remember(ReadOnlySpan<float> samples)
    {
        var cap = _history.Length;
        var at = (int)(_written * Channels % cap);
        for (var i = 0; i < samples.Length; i++) _history[(at + i) % cap] = samples[i];
    }

    /// Immediately: what was waiting is thrown away, and the source is not
    /// asked for more after this returns.
    public void Dispose()
    {
        Thread? thread;
        lock (_gate)
        {
            _stopping = true;
            thread = _thread;
            Monitor.PulseAll(_gate);
        }
        if (thread is not null && !thread.Join(2000))
            Console.WriteLine("[engine] output: the writer did not stop within 2 s");
    }

    private static string Error(int code) => Marshal.PtrToStringUTF8(pa_strerror(code)) ?? $"error {code}";

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferAttr
    {
        public uint MaxLength, TLength, PreBuf, MinReq, FragSize;
    }

    private const string Simple = "libpulse-simple.so.0";
    private const string Pulse = "libpulse.so.0";

    [DllImport(Simple)] private static extern IntPtr pa_simple_new(IntPtr server, string name, int dir, IntPtr dev, string streamName, SampleSpec* spec, IntPtr map, BufferAttr* attr, int* error);
    [DllImport(Simple)] private static extern int pa_simple_write(IntPtr s, void* data, nuint bytes, int* error);
    [DllImport(Simple)] private static extern int pa_simple_drain(IntPtr s, int* error);
    [DllImport(Simple)] private static extern int pa_simple_flush(IntPtr s, int* error);
    [DllImport(Simple)] private static extern ulong pa_simple_get_latency(IntPtr s, int* error);
    [DllImport(Simple)] private static extern void pa_simple_free(IntPtr s);
    [DllImport(Pulse)] private static extern IntPtr pa_strerror(int error);
}
