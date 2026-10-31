// COPIED from AlbumWall (src/AlbumWall.Playback/WasapiOutput.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — sound out on Windows, through WASAPI in shared mode (roadmap,
// "Hymie, our own audio engine", step 2c, 2026-10-22).
//
// Shared mode, so the device stays shared ("Desktop audio: be a good
// neighbour", docs/roadmap.md): a stream at the file's own sample rate, which
// Windows converts to the device's mix format (AUTOCONVERTPCM), as it does for
// mpv's own WASAPI output.
//
// COM straight from .NET, with no library and no interop assembly: the few
// interfaces needed are called through their vtables. One thread of our own
// does every COM call, as PulseOutput's writer does every Pulse call, so
// nothing here depends on the apartment the app's threads are in. It wakes
// when the device has room (event-driven) and keeps 0.2 s buffered, which
// rides out a pause of the app's collector as the Mac's queue does.
//
// What WASAPI gives that the simple Pulse API does not:
//
//   * Pause: IAudioClient::Stop keeps what is buffered, and Start plays on
//     from it, so nothing is flushed and replayed. Heard when the writer next
//     looks, which it is woken to do at once.
//   * Volume: the stream's own (IAudioStreamVolume), applied as Windows mixes,
//     so a change is heard within a device period (10 ms), not after the
//     buffer. Not the session's volume, which Windows would remember in its
//     mixer as AlbumWall's level from then on.
//   * Played: the device's own position (IAudioClock), read after every fill
//     and run on by the clock in between. It stands still while stopped.
//
// Nothing bundled: the Core Audio API is part of Windows.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Marginal.Playback;

internal sealed unsafe class WasapiOutput : IOutput
{
    private const long Buffered = 2_000_000;    // 0.2 s, in the 100 ns units WASAPI counts in

    public int SampleRate { get; }
    public int Channels { get; }

    private readonly IOutput.Source _source;
    private readonly object _gate = new();      // Monitor: the writer waits on it while paused
    private readonly IntPtr _wake;              // set to have the writer look now, not at the next period
    private Thread? _thread;
    private volatile bool _wantPlaying;
    private volatile bool _stopping;
    private volatile bool _ended;               // the source has said it has no more
    private volatile bool _drained;
    private volatile float _volume = 1f;

    // On the writer thread only.
    private long _written;                      // frames handed to the device
    private long _offset;                       // the frame of the timeline the device's position 0 is, since a Restart
    private bool _started;                      // the client is running
    private float _volumeSet = -1f;             // what the stream was last told

    // Under _gate.
    private bool _open;                         // the writer has a stream and is serving it
    private Action<long>? _restart;             // a Restart asked for and not yet done

    // The timeline, under _gate: Played was _base at _baseAt, and runs on
    // from there while _running, never past _limit.
    private long _base, _limit;
    private long _baseAt;
    private bool _running;

    public WasapiOutput(int sampleRate, int channels, IOutput.Source source)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _source = source;
        _wake = CreateEventW(IntPtr.Zero, 0, 0, IntPtr.Zero);
    }

    /// The device is opened on the first Start, not before, so a record
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

    /// The clock stops now, so the position stands still from the moment
    /// asked; the writer is woken to stop the device.
    public void Pause()
    {
        lock (_gate)
        {
            _base = Played;
            _running = false;
            _wantPlaying = false;
        }
        SetEvent(_wake);
    }

    /// Linear, 0-1, as the stream's volume.
    public void SetVolume(float gain)
    {
        _volume = gain;
        if (!_stopping) SetEvent(_wake);
    }

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

    /// Keeps the stream: Initialize is the slow part of opening one (360 ms
    /// on Mobilenode, whatever the format, so the driver's own), and a seek
    /// paid it every time. The clock stops here, at what has played; the
    /// writer stops the client, resets it, and plays on from there.
    public bool Restart(Action<long> onReset)
    {
        lock (_gate)
        {
            if (_stopping || !_open) return false;
            _restart = onReset;                 // a second before the first is done: the second wins
            _base = Played;
            _running = false;
            Monitor.PulseAll(_gate);
        }
        SetEvent(_wake);
        return true;
    }

    private void Run()
    {
        var com = CoInitializeEx(IntPtr.Zero, 0 /* COINIT_MULTITHREADED */);
        IntPtr enumerator = 0, device = 0, client = 0, render = 0, clock = 0, volume = 0, ready = 0;
        try
        {
            var clsid = ClsidDeviceEnumerator;
            var iid = IidDeviceEnumerator;
            Check(CoCreateInstance(&clsid, 0, 0x17 /* CLSCTX_ALL */, &iid, &enumerator), "creating the device enumerator");
            Check(((delegate* unmanaged<IntPtr, int, int, IntPtr*, int>)Slot(enumerator, 4))(enumerator, 0 /* eRender */, 1 /* eMultimedia */, &device),
                  "finding the default output device");
            iid = IidAudioClient;
            Check(((delegate* unmanaged<IntPtr, Guid*, uint, IntPtr, IntPtr*, int>)Slot(device, 3))(device, &iid, 0x17, 0, &client), "opening the device");

            var format = new WaveFormatExtensible
            {
                FormatTag = 0xFFFE,                     // WAVE_FORMAT_EXTENSIBLE
                Channels = (ushort)Channels,
                SamplesPerSec = (uint)SampleRate,
                AvgBytesPerSec = (uint)(SampleRate * 4 * Channels),
                BlockAlign = (ushort)(4 * Channels),
                BitsPerSample = 32,
                Size = 22,
                ValidBitsPerSample = 32,
                ChannelMask = Mask(Channels),
                SubFormat = SubtypeFloat,
            };
            const uint flags = 0x00040000      // AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                             | 0x80000000      // AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM: Windows converts to the mix format
                             | 0x08000000;     // AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY: and resamples well
            Check(((delegate* unmanaged<IntPtr, int, uint, long, long, WaveFormatExtensible*, Guid*, int>)Slot(client, 3))
                      (client, 0 /* AUDCLNT_SHAREMODE_SHARED */, flags, Buffered, 0, &format, null), "starting a stream");

            ready = CreateEventW(IntPtr.Zero, 0, 0, IntPtr.Zero);
            Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Slot(client, 13))(client, ready), "SetEventHandle");
            uint size;
            Check(((delegate* unmanaged<IntPtr, uint*, int>)Slot(client, 4))(client, &size), "GetBufferSize");
            render = Service(client, IidRenderClient);
            clock = Service(client, IidAudioClock);
            volume = Service(client, IidStreamVolume);
            ulong frequency;
            Check(((delegate* unmanaged<IntPtr, ulong*, int>)Slot(clock, 3))(clock, &frequency), "GetFrequency");

            var handles = stackalloc IntPtr[] { ready, _wake };
            lock (_gate) _open = true;
            while (!_stopping)
            {
                Action<long>? restart;
                long at;
                lock (_gate)
                {
                    restart = _restart;
                    _restart = null;
                    at = _base;
                }
                if (restart is not null)
                {
                    // Whatever the source gave meanwhile is thrown away with
                    // the rest: it was for the old place.
                    if (_started) { ClientCall(client, 11, "Stop"); _started = false; }
                    ClientCall(client, 12, "Reset");
                    _written = _offset = at;
                    _ended = _drained = false;
                    restart(at);
                    lock (_gate) { _base = _limit = at; _running = false; }
                    continue;
                }

                if (!_wantPlaying || _drained)
                {
                    // What is buffered stays, to play on from.
                    if (_started) { ClientCall(client, 11, "Stop"); _started = false; }
                    lock (_gate)
                        while ((!_wantPlaying || _drained) && _restart is null && !_stopping) Monitor.Wait(_gate);
                    continue;
                }

                ApplyVolume(volume);
                if (!_started)
                {
                    // Full before it starts, so the first period has sound.
                    Fill(client, render, size);
                    ClientCall(client, 10, "Start");
                    _started = true;
                }

                WaitForMultipleObjects(2, handles, 0, 100);
                if (_stopping || !_wantPlaying) continue;

                var padding = Fill(client, render, size);
                if (_ended && padding == 0)
                {
                    // Everything handed over has played.
                    ClientCall(client, 11, "Stop");
                    _started = false;
                    lock (_gate) { _base = _limit = _written; _running = false; }
                    _drained = true;
                    continue;
                }
                Clock(clock, frequency);
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
            lock (_gate) _open = false;
            if (_started) ((delegate* unmanaged<IntPtr, int>)Slot(client, 11))(client);
            Release(volume);
            Release(clock);
            Release(render);
            Release(client);
            Release(device);
            Release(enumerator);
            if (ready != 0) CloseHandle(ready);
            if (com >= 0) CoUninitialize();
        }
    }

    /// Hands the device as much as it has room for, while the source has
    /// more. Returns what the device still holds unplayed.
    private uint Fill(IntPtr client, IntPtr render, uint size)
    {
        uint padding;
        Check(((delegate* unmanaged<IntPtr, uint*, int>)Slot(client, 6))(client, &padding), "GetCurrentPadding");
        var room = (int)(size - padding);
        if (room <= 0 || _ended) return padding;

        byte* data;
        Check(((delegate* unmanaged<IntPtr, uint, byte**, int>)Slot(render, 3))(render, (uint)room, &data), "GetBuffer");
        var frames = 0;
        try { frames = _source(new Span<float>(data, room * Channels)); }
        finally { Check(((delegate* unmanaged<IntPtr, uint, uint, int>)Slot(render, 4))(render, (uint)frames, 0), "ReleaseBuffer"); }
        if (frames < room) _ended = true;
        _written += frames;
        return padding + (uint)frames;
    }

    /// Where the speakers are: the device's position, in the stream's frames.
    private void Clock(IntPtr clock, ulong frequency)
    {
        ulong position, qpc;
        if (((delegate* unmanaged<IntPtr, ulong*, ulong*, int>)Slot(clock, 4))(clock, &position, &qpc) < 0 || frequency == 0) return;
        var at = _offset + (long)((double)position * SampleRate / frequency);
        lock (_gate)
        {
            if (!_wantPlaying) return;      // a pause has stopped the clock
            _base = Math.Clamp(at, Math.Min(_base, _written), _written);
            // When the device was there (QPC, in 100 ns units), not when asked.
            _baseAt = (long)((double)qpc * Stopwatch.Frequency / 10_000_000);
            _limit = _written;
            _running = true;
        }
    }

    private void ApplyVolume(IntPtr volume)
    {
        var gain = _volume;
        if (gain == _volumeSet) return;
        var all = stackalloc float[Channels];
        for (var i = 0; i < Channels; i++) all[i] = gain;
        Check(((delegate* unmanaged<IntPtr, uint, float*, int>)Slot(volume, 6))(volume, (uint)Channels, all), "SetAllVolumes");
        _volumeSet = gain;
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
        SetEvent(_wake);
        if (thread is not null && !thread.Join(2000))
        {
            // Still running: it may yet wait on the event, so it is left open.
            Console.WriteLine("[engine] output: the writer did not stop within 2 s");
            return;
        }
        CloseHandle(_wake);
    }

    /// Speaker positions for 1 to 8 channels in FLAC's order, which is
    /// Windows' order too.
    private static uint Mask(int channels) => channels switch
    {
        1 => 0x4,           // center
        2 => 0x3,           // left, right
        3 => 0x7,           // + center
        4 => 0x33,          // left, right, back left, back right
        5 => 0x37,          // + center
        6 => 0x3F,          // 5.1
        7 => 0x70F,         // 6.1
        8 => 0x63F,         // 7.1
        _ => 0,
    };

    private static void* Slot(IntPtr com, int index) => (*(void***)com)[index];

    private static void ClientCall(IntPtr client, int slot, string what) =>
        Check(((delegate* unmanaged<IntPtr, int>)Slot(client, slot))(client), what);

    private static IntPtr Service(IntPtr client, Guid iid)
    {
        IntPtr service;
        Check(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Slot(client, 14))(client, &iid, &service), "GetService");
        return service;
    }

    private static void Release(IntPtr com)
    {
        if (com != 0) ((delegate* unmanaged<IntPtr, uint>)Slot(com, 2))(com);
    }

    private static void Check(int hr, string what)
    {
        if (hr >= 0) return;
        var why = (uint)hr switch
        {
            0x80070490 => "no output device",                   // E_NOTFOUND
            0x88890004 => "the device went away",               // AUDCLNT_E_DEVICE_INVALIDATED
            0x88890008 => "the device will not take this format",
            0x8889000A => "the device is in use exclusively",
            _ => $"HRESULT 0x{hr:X8}",
        };
        throw new InvalidOperationException($"{what}: {why}");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatExtensible
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
        public ushort ValidBitsPerSample;
        public uint ChannelMask;
        public Guid SubFormat;
    }

    private static readonly Guid ClsidDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IidDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid IidAudioClock = new("CD63314F-3FBA-4A1B-812C-EF96358728E7");
    private static readonly Guid IidStreamVolume = new("93014887-242D-4068-8A15-CF5E93B90FE3");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");   // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint model);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, IntPtr* obj);
    [DllImport("kernel32.dll")] private static extern IntPtr CreateEventW(IntPtr attributes, int manualReset, int initialState, IntPtr name);
    [DllImport("kernel32.dll")] private static extern int SetEvent(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern int CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint WaitForMultipleObjects(uint count, IntPtr* handles, int waitAll, uint milliseconds);
}
