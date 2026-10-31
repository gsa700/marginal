// COPIED from AlbumWall (src/AlbumWall.Codecs/AudioTrack.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — one track open for playing, whatever its format: what Hymie,
// the engine (HymieEngine), asks of a decoder, and nothing more.
//
// Each format is a small codec core on the shared parts of this project: the
// bytes as they arrive (TrackBytes), the tags (ReplayGain), seeking to an
// exact sample. FLAC first; MP3 and AAC next, his call 2026-10-22 ("replace
// the top 3 decoders with native C# versions"), so that mpv can go; then WAV
// and ALAC, the same day, and mpv went. What these do not decode, nothing in
// AlbumWall plays.

namespace Marginal.Codecs;

public interface IAudioTrack
{
    /// The track's name: its file, or its library path for a server's track.
    /// Never the address it is fetched from, which carries the sign-in.
    string Name { get; }

    int SampleRate { get; }
    int Channels { get; }
    int BitsPerSample { get; }

    /// Frames from the first to the last, after anything the format says to
    /// drop (an encoder's padding): what is played, gapless.
    long TotalFrames { get; }

    TimeSpan Duration { get; }
    ReplayGain Gain { get; }

    /// The next frames, interleaved, as floats from -1 to 1, into `into`
    /// (whose length is a whole number of frames). Returns the frames written;
    /// fewer than asked for only at the end of the track.
    int Read(Span<float> into);

    /// Puts the cursor on frame `frame`, exactly, at the next Read.
    void Seek(long frame);

    /// Outside the engine's lock, before a Read of `frames`: whatever waiting
    /// that Read would do (for bytes still arriving, a seek) done here.
    void Prepare(int frames);

    /// Stops a wait for bytes that have not arrived; the next Seek undoes it.
    void Interrupt();
    bool Interrupted { get; }
}

/// A file these decoders do not play, though nothing is wrong with it (HE-AAC
/// in an .m4a, MP2 named .mp3, a WAV of compressed samples). Passed over like
/// a damaged one, but said so in words that do not call it damaged.
public sealed class UnsupportedAudioException(string message) : IOException(message);

public static class AudioTracks
{
    /// Whether a track by this name (its file or library path) has a decoder
    /// here.
    public static bool CanDecode(string name) =>
        name.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);

    /// The most a track may claim, and the fewest: what an output is made for.
    public const int MaxSampleRate = 768_000, MaxChannels = 8;

    /// A track by name, from where it is: a file, or a server's address
    /// (http or https), which is fetched and not kept. The decoder is chosen
    /// by the name's extension, as the scanner chose the file by it.
    ///
    /// Its rate and channels are held to what an output can be made for, here
    /// for every format (security review 2026-10-26, finding 4): a WAV header
    /// claiming 3,221,225,472 Hz reached an output as -1,073,741,824 Hz and
    /// took the whole app down from the engine's watch thread.
    public static IAudioTrack Open(string name, string source)
    {
        IAudioTrack track = name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? Mp3Track.Open(name, source)
            : name.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ? Mp4Track.Open(name, source)
            : name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? WavTrack.Open(name, source)
            : FlacTrack.Open(name, source);
        if (track.SampleRate is < 1 or > MaxSampleRate || track.Channels is < 1 or > MaxChannels)
            throw new FlacDecodeException($"{track.SampleRate} Hz, {track.Channels} channels: no output plays that.");
        return track;
    }

    public static bool IsAddress(string source) =>
        source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
