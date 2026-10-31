// COPIED from AlbumWall (src/AlbumWall.Playback/AudioQueueOutput.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — sound out on the Mac, through an AudioQueue.
//
// Why an AudioQueue and not an output AudioUnit: a unit's render callback runs
// on CoreAudio's real-time thread and wants a few milliseconds of samples by
// return, every few milliseconds. This is .NET, where a garbage collection can
// stop every managed thread for longer than that, and a late callback is a
// click. A queue asks for a whole buffer at a time from a thread of its own
// and plays what it already has meanwhile, so four buffers of 4096 frames
// (about 0.37 s at 44.1 kHz) ride out any pause the app's collector takes.
//
// The queue plays the file's own sample rate and lets CoreAudio convert for
// the device, which is what mpv does on the Mac too: the device stays shared
// with every other app ("Desktop audio: be a good neighbour", docs/roadmap.md).
//
// AudioToolbox through P/Invoke, nothing bundled.

using System.Runtime.InteropServices;

namespace Marginal.Playback;

internal sealed unsafe class AudioQueueOutput : IOutput
{
    public const int FramesPerBuffer = 4096;
    private const int Buffers = 4;

    public int SampleRate { get; }
    public int Channels { get; }

    private readonly IOutput.Source _source;
    private IntPtr _queue;
    private GCHandle _self;
    private int _idle;              // buffers handed back and not refilled
    private volatile bool _ended;   // the source has said it has no more
    private volatile bool _stopping;
    private long _lastPlayed;

    public AudioQueueOutput(int sampleRate, int channels, IOutput.Source source)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _source = source;

        var format = new AudioStreamBasicDescription
        {
            SampleRate = sampleRate,
            FormatId = 0x6C70636D,                      // 'lpcm'
            FormatFlags = 1 | 8,                        // float, packed
            BytesPerPacket = (uint)(4 * channels),
            FramesPerPacket = 1,
            BytesPerFrame = (uint)(4 * channels),
            ChannelsPerFrame = (uint)channels,
            BitsPerChannel = 32,
        };
        _self = GCHandle.Alloc(this);
        delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> callback = &OnBufferDone;
        Check(AudioQueueNewOutput(&format, (IntPtr)callback, GCHandle.ToIntPtr(_self),
                                  IntPtr.Zero, IntPtr.Zero, 0, out _queue), "AudioQueueNewOutput");

        // Filled here, before anything plays, so that Start has sound to give
        // at once and a paused restore holds the first samples ready.
        for (var i = 0; i < Buffers; i++)
        {
            Check(AudioQueueAllocateBuffer(_queue, (uint)(FramesPerBuffer * 4 * channels), out var buffer), "AudioQueueAllocateBuffer");
            Fill(buffer);
        }
    }

    public void Start() { if (!_stopping) Check(AudioQueueStart(_queue, IntPtr.Zero), "AudioQueueStart"); }
    public void Pause() => AudioQueuePause(_queue);

    /// The queue's own volume, applied as it plays rather than to samples
    /// already waiting in its buffers, so the slider is heard at once. Linear.
    public void SetVolume(float gain) => AudioQueueSetParameter(_queue, 1 /* kAudioQueueParam_Volume */, gain);

    /// Frames the queue has played since it was made: its own timeline, which
    /// stands still while paused. Read from the queue, not counted here, so it
    /// is what has reached the device rather than what has been handed over.
    public long Played
    {
        get
        {
            AudioTimeStamp t;
            byte discontinuity;
            if (AudioQueueGetCurrentTime(_queue, IntPtr.Zero, &t, &discontinuity) == 0 && (t.Flags & 1) != 0 && t.SampleTime >= 0)
                _lastPlayed = (long)t.SampleTime;
            return _lastPlayed;
        }
    }

    /// True once the source has ended and every buffer has played out.
    public bool Drained => _ended && Volatile.Read(ref _idle) == Buffers;

    [UnmanagedCallersOnly]
    private static void OnBufferDone(IntPtr self, IntPtr queue, IntPtr buffer)
    {
        try
        {
            if (GCHandle.FromIntPtr(self).Target is AudioQueueOutput output) output.Fill(buffer);
        }
        catch (Exception ex)
        {
            // Nothing may be thrown back into AudioToolbox.
            Console.WriteLine($"[engine] output: {ex.Message}");
        }
    }

    private void Fill(IntPtr buffer)
    {
        var b = (AudioQueueBuffer*)buffer;
        var frames = 0;
        if (!_ended && !_stopping)
        {
            var span = new Span<float>((void*)b->AudioData, FramesPerBuffer * Channels);
            frames = _source(span);
            if (frames < FramesPerBuffer) _ended = true;
        }
        if (frames == 0) { Interlocked.Increment(ref _idle); return; }
        b->AudioDataByteSize = (uint)(frames * 4 * Channels);
        if (AudioQueueEnqueueBuffer(_queue, buffer, 0, IntPtr.Zero) != 0) Interlocked.Increment(ref _idle);
    }

    public void Dispose()
    {
        _stopping = true;
        if (_queue != IntPtr.Zero)
        {
            // Immediately: what was waiting is thrown away, and no callback
            // comes after this returns.
            AudioQueueDispose(_queue, 1);
            _queue = IntPtr.Zero;
        }
        if (_self.IsAllocated) _self.Free();
    }

    private static void Check(int status, string call)
    {
        if (status != 0) throw new InvalidOperationException($"{call}: OSStatus {status}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId, FormatFlags, BytesPerPacket, FramesPerPacket, BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioQueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public IntPtr AudioData;
        public uint AudioDataByteSize;
        public IntPtr UserData;
        public uint PacketDescriptionCapacity;
        public IntPtr PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioTimeStamp
    {
        public double SampleTime;
        public ulong HostTime;
        public double RateScalar;
        public ulong WordClockTime;
        public SmpteTime Smpte;
        public uint Flags;          // 1: SampleTime is valid
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmpteTime
    {
        public short Subframes, SubframeDivisor;
        public uint Counter, Type, Flags;
        public short Hours, Minutes, Seconds, Frames;
    }

    private const string Toolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    [DllImport(Toolbox)] private static extern int AudioQueueNewOutput(AudioStreamBasicDescription* format, IntPtr callback, IntPtr userData, IntPtr runLoop, IntPtr runLoopMode, uint flags, out IntPtr queue);
    [DllImport(Toolbox)] private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint bytes, out IntPtr buffer);
    [DllImport(Toolbox)] private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint packetDescriptions, IntPtr descriptions);
    [DllImport(Toolbox)] private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);
    [DllImport(Toolbox)] private static extern int AudioQueuePause(IntPtr queue);
    [DllImport(Toolbox)] private static extern int AudioQueueDispose(IntPtr queue, byte immediate);
    [DllImport(Toolbox)] private static extern int AudioQueueSetParameter(IntPtr queue, uint parameter, float value);
    [DllImport(Toolbox)] private static extern int AudioQueueGetCurrentTime(IntPtr queue, IntPtr timeline, AudioTimeStamp* time, byte* discontinuity);
}
