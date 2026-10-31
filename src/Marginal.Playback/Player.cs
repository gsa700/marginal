// COPIED from AlbumWall (src/AlbumWall.Playback/Player.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — playback, as the app sees it.
//
// Hymie (HymieEngine) plays everything: FLAC, MP3, AAC, ALAC and WAV, local
// or from a server, on the Mac, Linux and Windows (roadmap, "Hymie, our own
// audio engine: mpv out, in steps"). Until 2026-10-22 this was the face of two
// engines, mpv and Hymie, chosen for each queue by a Preferences switch; then
// mpv went, his call ("I think it's worth doing now ... the sooner the
// better"), and this is the face of one.
//
// The requirements it is held to, from the spec:
//
//   * GAPLESS. Non-negotiable.
//   * NATIVE SAMPLE RATE, no unnecessary resampling.
//   * REPLAYGAIN from tags: album gain for album playback, track gain for
//     shuffle, never silently applied without being visible.

namespace Marginal.Playback;

public enum GainMode { Off, Track, Album }

internal static class GainModes
{
    /// What Hymie multiplies a track's samples by, in a mode.
    public static float Multiplier(this Codecs.ReplayGain gain, GainMode mode) =>
        mode == GainMode.Off ? 1f : gain.Multiplier(album: mode == GainMode.Album);
}

public sealed class TrackChangedEventArgs(int index) : EventArgs
{
    public int Index { get; } = index;
}

public sealed class Player : IDisposable
{
    private readonly HymieEngine _hymie;

    /// Whether playback is possible at all on this machine: whether Hymie has
    /// an output here (the Mac, Windows, and Linux with PulseAudio's client
    /// library, which PipeWire serves too).
    public static bool IsAvailable => Output.IsAvailable;

    public Player(GainMode gain = GainMode.Album)
    {
        _hymie = new HymieEngine(gain);
        _hymie.TrackChanged += (_, e) => TrackChanged?.Invoke(this, e);
        _hymie.StateChanged += (_, e) => StateChanged?.Invoke(this, e);
        _hymie.Finished += (_, e) => Finished?.Invoke(this, e);
        _hymie.DurationChanged += (_, e) => DurationChanged?.Invoke(this, e);
        _hymie.Seeked += (_, e) => Seeked?.Invoke(this, e);
    }

    public event EventHandler<TrackChangedEventArgs>? TrackChanged;
    public event EventHandler? StateChanged;

    /// The queue has played out, or was stopped while on a track. Index still
    /// names that track while this goes out.
    public event EventHandler? Finished;

    /// The track's length has become known, or changed.
    public event EventHandler? DurationChanged;

    /// Playback jumped, by anyone's hand: the app's bar, the desktop's, or
    /// Previous restarting a track. Carries where it was asked to go.
    public event EventHandler<TimeSpan>? Seeked;

    public bool IsPlaying => _hymie.IsPlaying;
    public int Index => _hymie.Index;
    public int Count => _hymie.Count;
    public TimeSpan Position => _hymie.Position;
    public TimeSpan Duration => _hymie.Duration;

    /// The levels of the block sounding now, in dB, for the meters: measured
    /// where the output is playing, so nothing needs holding back.
    public (double Left, double Right) Levels() => _hymie.Levels();

    /// RMS and peak of the block sounding now, before ReplayGain or after it.
    public MeterLevels Meter(bool afterGain) => _hymie.Meter(afterGain);

    /// The 31-band analyzer's reading of what is sounding now (Spectrum); false
    /// when there is none to take, and the caller keeps what it had.
    public bool Bands(Span<float> bands, bool afterGain) => _hymie.Bands(bands, afterGain);

    /// ReplayGain. Album gain preserves the relative loudness the record was
    /// mastered with, which is what album playback wants; track gain flattens
    /// that out, which is what shuffle wants.
    public GainMode Gain
    {
        get => _hymie.Gain;
        set => _hymie.Gain = value;
    }

    /// The app's own volume, 0-100, independent of the system mixer: it
    /// attenuates only this app's stream. NOT the same lever as ReplayGain.
    public int Volume
    {
        get => _hymie.Volume;
        set => _hymie.Volume = value;
    }

    /// Plays `paths` from `start`; `at` and `paused` pick up where the last run
    /// left off. `names` are the tracks' own paths, one for each, when `paths`
    /// are not: a server's tracks are played from addresses that carry the
    /// sign-in, and named by their library paths.
    public void Play(IEnumerable<string> paths, int start = 0, TimeSpan? at = null, bool paused = false,
                     IReadOnlyList<string>? names = null)
    {
        var list = paths.ToList();
        if (list.Count == 0) return;
        _hymie.Play(list, names is not null && names.Count == list.Count ? names : list, start, at, paused);
    }

    public void PlayIndex(int index) => _hymie.PlayIndex(index);
    public void Next() => _hymie.Next();
    public void Previous() => _hymie.Previous();
    public void TogglePause() => SetPaused(IsPlaying);
    public void SetPaused(bool paused) => _hymie.SetPaused(paused);
    public void Stop() => _hymie.Stop();
    public void Seek(TimeSpan to) => _hymie.Seek(to);

    public void Dispose() => _hymie.Dispose();
}
