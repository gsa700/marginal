// COPIED from AlbumWall (src/AlbumWall.Playback/Output.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — where Hymie's samples go: one output per platform
// (roadmap, "Hymie, our own audio engine", step 2). HymieEngine asks only
// this of it.

namespace Marginal.Playback;

internal interface IOutput : IDisposable
{
    /// Fills the span (interleaved floats, a whole number of frames) with what
    /// plays next. Returns the frames written: fewer than asked for means the
    /// end, and zero means nothing more will come.
    delegate int Source(Span<float> into);

    int SampleRate { get; }

    void Start();
    void Pause();

    /// Linear, 0-1.
    void SetVolume(float gain);

    /// Frames played since the output was made: what has reached the device,
    /// not what has been handed over. Stands still while paused.
    long Played { get; }

    /// True once the source has ended and everything handed over has played.
    bool Drained { get; }

    /// Throws away what has been handed over and not played, keeping the
    /// device open, for a seek or a jump: cheaper than a new output where
    /// opening one is slow (WASAPI, 360 ms on Mobilenode). `onReset` is called
    /// on the output's own thread once nothing old is left to play, with the
    /// frame of the timeline the next samples will be, before the source is
    /// asked for them; it may not come, if the output is disposed first.
    /// Playing or paused stays as it was. False when this output cannot,
    /// and then it is made anew.
    bool Restart(Action<long> onReset) => false;
}

internal static class Output
{
    /// Whether Hymie has an output on this machine.
    public static bool IsAvailable => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
                                      || (OperatingSystem.IsLinux() && PulseOutput.IsAvailable);

    public static IOutput Open(int sampleRate, int channels, IOutput.Source source) =>
        OperatingSystem.IsMacOS() ? new AudioQueueOutput(sampleRate, channels, source)
        : OperatingSystem.IsWindows() ? new WasapiOutput(sampleRate, channels, source)
        : new PulseOutput(sampleRate, channels, source);
}
