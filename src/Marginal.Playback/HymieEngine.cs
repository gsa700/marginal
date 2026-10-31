// COPIED from AlbumWall (src/AlbumWall.Playback/HymieEngine.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — Hymie, its own audio engine: FLAC, MP3 and AAC, on the Mac,
// Linux and Windows (roadmap, "Hymie, our own audio engine: mpv out, in
// steps", steps 1 to 3b, started 2026-10-21). Called FlacEngine until it
// played more than FLAC, then OwnEngine, then Hymie (his call, 2026-10-22:
// Get Smart's robot, who does exactly what he is told and nothing more, as a
// bit-perfect engine should). The decoders are AlbumWall.Codecs'; the output
// is the platform's (Output.cs); everything here is plain .NET.
//
// One continuous stream of samples. The album's tracks are decoded one after
// another into the same output, so nothing happens at a track boundary: the
// last sample of one track is followed by the first of the next, which is
// gapless by construction rather than by prefetching and splicing. Where each
// track begins on the output's timeline is written down as it is decoded (a
// Mark), and the track playing NOW is read from how far the output has
// actually played, so the app's idea of the current track follows the
// speakers, not the decoder, which runs about 0.4 s ahead.
//
// Only when two tracks differ in sample rate or channels does the stream
// break: the first is let play out and a new output is made for the second.
// A CD rip never does that.
//
// Levels and gain were made to match mpv's (Codecs' ReplayGain, Volume
// below), so that a record sounded the same on either engine while there were
// two; mpv went on 2026-10-22, and the loudness it set stays.

using Marginal.Codecs;

namespace Marginal.Playback;

internal sealed class HymieEngine : IDisposable
{
    private readonly Lock _lock = new();
    private List<string> _queue = [];           // where each track is: a file, or a server's address
    private List<string> _names = [];           // what each is called in the log: never an address

    // The output, and the decoding that feeds it (on the output's thread,
    // under _lock). _generation tells a callback from an output already
    // thrown away that it has nothing more to say.
    private IOutput? _out;
    private int _outChannels;
    private int _generation;
    private (int Index, IAudioTrack Track, long Frame)? _pending;    // a seek or jump the output has yet to restart for
    private volatile IAudioTrack? _decoding;      // read outside the lock too, to wait for its bytes there
    private int _decodingIndex = -1;
    private float _decodingGain = 1f;
    private long _handed;                       // frames given to _out so far
    private readonly List<Mark> _marks = [];
    private IAudioTrack? _afterBreak;             // the next track, when its format needs a new output

    private Task<IAudioTrack?>? _prefetch;
    private int _prefetchIndex = -1;

    private volatile int _index = -1;
    private volatile bool _playing;
    private TimeSpan _duration;
    private GainMode _gain;
    private int _volume = 100;

    private readonly Thread _watch;
    private volatile bool _running = true;

    /// Where a track starts on the output's timeline: frame At of the output is
    /// frame TrackFrame of track Index (not 0 after a seek).
    private readonly record struct Mark(long At, int Index, long TrackFrame, IAudioTrack Track);

    public HymieEngine(GainMode gain)
    {
        _gain = gain;
        Array.Fill(_levelBlock, -1);
        _watch = new Thread(Watch) { IsBackground = true, Name = "engine watch" };
        _watch.Start();
    }

    public bool IsPlaying => _playing;
    public int Index => _index;
    public int Count
    {
        get
        {
            if (!_lock.TryEnter(UiWait)) return _lastCount;
            try { return _lastCount = _queue.Count; }
            finally { _lock.Exit(); }
        }
    }

    // THE UI NEVER WAITS ON THE AUDIO THREAD. Position, Levels and Count are read by the
    // window's timers (four and thirty times a second), and the audio thread holds _lock
    // while it decodes, which at a track change includes opening the next file
    // (NextTrack: _prefetch.Result, or TryOpen). A file on a sleeping NAS or a slow
    // Navidrome reply held the lock for seconds, and the window froze with it (interface
    // pass, 2026-10-27). These three ask for the lock for a few milliseconds at most and
    // otherwise answer with what they last read: the position a quarter second stale, a
    // needle a frame late, never a frozen window.
    private static readonly TimeSpan UiWait = TimeSpan.FromMilliseconds(5);
    private TimeSpan _lastPosition;
    private (double Left, double Right) _lastLevels = (double.NegativeInfinity, double.NegativeInfinity);
    private int _lastCount;

    public event EventHandler<TrackChangedEventArgs>? TrackChanged;
    public event EventHandler? StateChanged;
    public event EventHandler? Finished;
    public event EventHandler? DurationChanged;
    public event EventHandler<TimeSpan>? Seeked;

    public GainMode Gain
    {
        get => _gain;
        set
        {
            _gain = value;
            // Heard from the next samples decoded, which is a buffer's length
            // (0.4 s) from now: what is already waiting keeps the gain it had.
            lock (_lock)
                if (_decoding is not null) _decodingGain = _decoding.Gain.Multiplier(value);
        }
    }

    /// 0-100 on mpv's curve, the cube of the fraction (mpv's audio_get_gain),
    /// so the slider kept the feel it had when mpv played. Set on the
    /// output's own volume, which is heard at once.
    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            lock (_lock) _out?.SetVolume(Cube(_volume));
        }
    }

    private static float Cube(int volume) => MathF.Pow(volume / 100f, 3);

    public TimeSpan Position
    {
        get
        {
            if (!_lock.TryEnter(UiWait)) return _lastPosition;
            try { return _lastPosition = PositionLocked(); }
            finally { _lock.Exit(); }
        }
    }

    private TimeSpan PositionLocked()
    {
        // Where it was asked to go, until the output is there.
        if (_pending is { } p) return TimeSpan.FromSeconds((double)p.Frame / p.Track.SampleRate);
        if (_out is null || Current(_out.Played) is not { } m) return TimeSpan.Zero;
        return TimeSpan.FromSeconds((double)(_out.Played - m.At + m.TrackFrame) / _out.SampleRate);
    }

    public TimeSpan Duration => _duration;

    /// The mark for what the output is playing now.
    private Mark? Current(long played)
    {
        Mark? found = null;
        foreach (var m in _marks)
        {
            if (m.At > played) break;
            found = m;
        }
        return found;
    }

    public void Play(IReadOnlyList<string> paths, int start, TimeSpan? at, bool paused) => Play(paths, paths, start, at, paused);

    /// `sources` are where the tracks are, `names` what they are called in
    /// the log, one for one: the same list for files, the library paths for a
    /// server's tracks, whose addresses carry the sign-in.
    public void Play(IReadOnlyList<string> sources, IReadOnlyList<string> names, int start, TimeSpan? at, bool paused)
    {
        if (sources.Count == 0) return;
        var paths = sources;
        Discard();
        lock (_lock)
        {
            _names = [.. names];
            _queue = [.. paths];
            _prefetch = null;
            _prefetchIndex = -1;
        }
        _index = -1;

        var index = Math.Clamp(start, 0, paths.Count - 1);
        var track = OpenFrom(ref index);
        if (track is null)
        {
            Console.WriteLine("[engine] nothing in the queue would open");
            return;
        }

        long frame = 0;
        if (at is { TotalSeconds: > 0.5 } a && index == Math.Clamp(start, 0, paths.Count - 1))
        {
            frame = (long)(a.TotalSeconds * track.SampleRate);
            track.Seek(frame);
        }
        Console.WriteLine($"[engine] queued {paths.Count}, from {index}{(frame > 0 ? $" at {at!.Value:m\\:ss}" : "")}"
                        + $"{(paused ? ", held" : "")}: {System.IO.Path.GetFileName(track.Name)} "
                        + $"({track.SampleRate} Hz, {(track.BitsPerSample > 0 ? $"{track.BitsPerSample}-bit" : "lossy")}, {track.Channels} ch, "
                        + $"gain x{track.Gain.Multiplier(_gain):0.###})");
        if (Begin(index, track, frame, play: !paused)) SetState(!paused);
    }

    public void PlayIndex(int index) => JumpTo(index, play: true);

    /// At the end of the queue, nothing: mpv's `playlist-next weak`.
    public void Next()
    {
        if (_index >= 0 && _index + 1 < Count) JumpTo(_index + 1, _playing);
    }

    /// Past three seconds in, back to the start of the track; before that, the
    /// track before, and on the first track nothing (mpv's `playlist-prev weak`).
    public void Previous()
    {
        if (Position > TimeSpan.FromSeconds(3)) { Seek(TimeSpan.Zero); return; }
        if (_index > 0) JumpTo(_index - 1, _playing);
    }

    /// The track playing on meanwhile: opening the next one is not silence.
    private void JumpTo(int index, bool play)
    {
        if (index < 0 || index >= Count) return;
        var track = OpenFrom(ref index);
        if (track is null) return;
        if (!Restart(index, track, 0, play))
        {
            Discard();
            if (!Begin(index, track, 0, play)) return;
        }
        SetState(play);
    }

    public void SetPaused(bool paused)
    {
        lock (_lock)
        {
            if (paused) _out?.Pause();
            else _out?.Start();
        }
        SetState(!paused);
    }

    public void Stop()
    {
        var was = _index;
        Discard();
        SetState(false);
        if (was >= 0) Finished?.Invoke(this, EventArgs.Empty);
        _index = -1;
    }

    public void Seek(TimeSpan to)
    {
        int index;
        IAudioTrack track;
        lock (_lock)
        {
            // A seek on a seek not yet done is on the same track.
            if (_pending is { } p) (index, track) = (p.Index, p.Track);
            else if (_out is not null && Current(_out.Played) is { } m) (index, track) = (m.Index, m.Track);
            else return;
        }

        var frame = Math.Clamp((long)(to.TotalSeconds * track.SampleRate), 0, Math.Max(0, track.TotalFrames - 1));
        if (!Restart(index, track, frame, _playing))
        {
            Discard();          // before the track's cursor moves under the decoding
            track.Seek(frame);
            Begin(index, track, frame, _playing);
        }
        Seeked?.Invoke(this, to);
    }

    // ---------------------------------------------------------------- output

    /// The output kept, where it can be (IOutput.Restart): what it holds for
    /// the old place is thrown away, and `track` is decoded from `frame` once
    /// the output's own thread is ready for it, so its cursor never moves
    /// under the decoding. False when it cannot be kept: a different format,
    /// or an output that cannot.
    private bool Restart(int index, IAudioTrack track, long frame, bool play)
    {
        IOutput? output;
        lock (_lock)
        {
            output = _out;
            if (output is null || output.SampleRate != track.SampleRate || _outChannels != track.Channels) return false;
            _pending = (index, track, frame);
        }
        // A wait for a server's bytes lets go, so that the output gets to it.
        _decoding?.Interrupt();
        if (!output.Restart(at => Restarted(output, at)))
        {
            lock (_lock) _pending = null;
            return false;
        }
        if (play) output.Start();
        else output.Pause();
        return true;
    }

    /// On the output's thread, with nothing old left in it: the decoding
    /// starts again where it was asked to, at frame `at` of the timeline.
    private void Restarted(IOutput output, long at)
    {
        lock (_lock)
        {
            if (_out != output || _pending is not { } p) return;
            _pending = null;
            p.Track.Seek(p.Frame);
            _decoding = p.Track;
            _decodingIndex = p.Index;
            _decodingGain = p.Track.Gain.Multiplier(_gain);
            _handed = at;
            _marks.Clear();
            _marks.Add(new Mark(at, p.Index, p.Frame, p.Track));
            _afterBreak = null;
            Array.Fill(_levelBlock, -1);
            _levelAt = at;
            _levelFrom = at;
            _levelL = _levelR = 0;
            _peakL = _peakR = 0;
            Prefetch(p.Index + 1);
        }
    }

    /// A new output, starting with `track` at `frame`. False, with nothing
    /// playing, when the system would not make one for its format: said so,
    /// never thrown, as it is called from the watch thread too, where a throw
    /// ended the app (finding 4).
    private bool Begin(int index, IAudioTrack track, long frame, bool play)
    {
        Discard();
        int generation;
        lock (_lock)
        {
            generation = _generation;
            _outChannels = track.Channels;
            _decoding = track;
            _decodingIndex = index;
            _decodingGain = track.Gain.Multiplier(_gain);
            _handed = 0;
            _marks.Clear();
            _marks.Add(new Mark(0, index, frame, track));
            _afterBreak = null;
            Array.Fill(_levelBlock, -1);
            _levelAt = 0;
            _levelFrom = 0;
            _levelL = _levelR = 0;
            _peakL = _peakR = 0;
            Prefetch(index + 1);
        }

        IOutput output;
        try { output = Output.Open(track.SampleRate, track.Channels, into => Source(into, generation)); }
        catch (Exception ex)
        {
            Console.WriteLine($"[engine] no output for {track.SampleRate} Hz, {track.Channels} ch: {ex.Message}");
            Discard();
            SetState(false);
            return false;
        }
        output.SetVolume(Cube(_volume));
        lock (_lock)
        {
            if (generation != _generation) { output.Dispose(); return true; }
            _out = output;
        }
        if (play) output.Start();
        return true;
    }

    /// The output thrown away, immediately. Outside the lock: disposing waits
    /// for a callback in progress, which may itself be waiting for the lock.
    private void Discard()
    {
        // First, and without the lock: the output's thread may hold it while
        // waiting for a server's bytes, and is let go of here.
        _decoding?.Interrupt();
        IOutput? old;
        lock (_lock)
        {
            old = _out;
            _out = null;
            _generation++;
            _decoding = null;
            _pending = null;
        }
        old?.Dispose();
    }

    /// The output asks for what plays next.
    private int Source(Span<float> into, int generation)
    {
        // A server's track may still be arriving: what this asks for is
        // waited for here, before the lock, so that nothing else waits with it.
        // Only the output's thread reads a track, so this is safe unlocked.
        if (_decoding is { } track)
        {
            try { track.Prepare(into.Length / track.Channels); }
            catch (Exception ex) { Console.WriteLine($"[engine] {System.IO.Path.GetFileName(track.Name)}: {ex.Message}"); }
        }

        lock (_lock)
        {
            if (generation != _generation || _decoding is null) return 0;
            var channels = _decoding.Channels;
            var want = into.Length / channels;
            var done = 0;
            while (done < want && _decoding is not null)
            {
                int n;
                try { n = _decoding.Read(into[(done * channels)..(want * channels)]); }
                catch (Exception ex)
                {
                    // mpv goes on to the next track after a broken one; so does
                    // this, whatever the decoder threw (findings 3 and 4).
                    Console.WriteLine($"[engine] {System.IO.Path.GetFileName(_decoding.Name)}: {ex.Message}");
                    n = 0;
                }
                var chunk = into.Slice(done * channels, n * channels);
                Measure(chunk, channels);
                if (_decodingGain != 1f)
                    foreach (ref var s in chunk) s *= _decodingGain;
                done += n;
                _handed += n;
                // Short because the output is being thrown away is not the end
                // of the track.
                if (done < want && (_decoding.Interrupted || !NextTrack())) break;
            }
            return done;
        }
    }

    /// On to the next track in the same stream. False at the end of the queue,
    /// or when the next track needs an output of its own (a different rate or
    /// channel count): then this one plays out first and Watch makes it.
    private bool NextTrack()
    {
        var index = _decodingIndex + 1;
        IAudioTrack? next = null;
        while (index < _queue.Count && next is null)
        {
            next = index == _prefetchIndex && _prefetch is not null ? _prefetch.Result : TryOpen(_names[index], _queue[index]);
            if (next is null) index++;
        }
        _decoding = null;
        if (next is null) return false;

        var current = _marks[^1].Track;
        if (next.SampleRate != current.SampleRate || next.Channels != current.Channels)
        {
            _afterBreak = next;
            _decodingIndex = index;
            return false;
        }
        _decoding = next;
        _decodingIndex = index;
        _decodingGain = next.Gain.Multiplier(_gain);
        _marks.Add(new Mark(_handed, index, 0, next));
        Prefetch(index + 1);
        return true;
    }

    /// Opens the track after the one now being decoded, on another thread, so
    /// that it is in memory before the boundary arrives. Under _lock.
    private void Prefetch(int index)
    {
        if (index >= _queue.Count || index == _prefetchIndex) return;
        var (name, source) = (_names[index], _queue[index]);
        _prefetchIndex = index;
        _prefetch = Task.Run(() => TryOpen(name, source));
    }

    /// The first track from `index` on that opens, with `index` moved to it;
    /// one that is broken, or that these decoders do not play, is passed over.
    private IAudioTrack? OpenFrom(ref int index)
    {
        for (; index < Count; index++)
        {
            Task<IAudioTrack?>? ready;
            string name, source;
            lock (_lock)
            {
                ready = index == _prefetchIndex ? _prefetch : null;
                (name, source) = (_names[index], _queue[index]);
            }
            var track = ready?.Result;
            // Not the one being decoded now (the last track's, opened ahead,
            // stays the prefetch): its cursor is the output's. A fresh copy.
            if (track is not null && track == _decoding) { track = null; ready = null; }
            track ??= TryOpen(name, source);
            if (track is null) continue;
            // A track that was half played before is opened at its start.
            if (ready is not null) track.Seek(0);
            return track;
        }
        return null;
    }

    /// Logged by name only: a server's source carries the sign-in.
    private static IAudioTrack? TryOpen(string name, string source)
    {
        if (!AudioTracks.CanDecode(name))
        {
            Console.WriteLine($"[engine] cannot play {System.IO.Path.GetFileName(name)}: not a format AlbumWall plays");
            return null;
        }
        try { return AudioTracks.Open(name, source); }
        catch (UnsupportedAudioException ex)
        {
            Console.WriteLine($"[engine] cannot play {System.IO.Path.GetFileName(name)}: {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException
                                      or OperationCanceledException or FlacDecodeException)
        {
            var why = ex is OperationCanceledException ? "the server did not answer in time" : ex.Message;
            Console.WriteLine($"[engine] cannot play {System.IO.Path.GetFileName(name)}: {why}");
            return null;
        }
        catch (Exception ex)
        {
            // Anything else a file or a server can make a decoder throw is a
            // broken track too, passed over like one (security review
            // 2026-10-26, findings 3 and 4). Before, an IndexOutOfRange from a
            // 144-byte .m4a escaped: from the prefetch it stalled the output,
            // from Next it reached the app's thread.
            Console.WriteLine($"[engine] cannot play {System.IO.Path.GetFileName(name)}: the file is broken ({ex.GetType().Name})");
            return null;
        }
    }

    // ---------------------------------------------------------------- levels

    // RMS and peak per block of 1024 frames of the output's timeline, measured
    // BEFORE the gain as mpv's astats is (it sits ahead of the volume), and kept
    // for a while so the meters can read the block that is sounding now, not the
    // one being decoded. 512 blocks is 12 s at 44.1 kHz, far more than is
    // buffered. Each block keeps the gain it was decoded with too, so a meter
    // can be asked for the level after ReplayGain without measuring twice (the
    // setting, 2026-10-27: "before or after", one choice for every meter).
    private const int LevelShift = 10;
    private const int LevelBlocks = 512;
    private readonly long[] _levelBlock = new long[LevelBlocks];
    private readonly float[] _levelLeft = new float[LevelBlocks];
    private readonly float[] _levelRight = new float[LevelBlocks];
    private readonly float[] _peakLeft = new float[LevelBlocks];
    private readonly float[] _peakRight = new float[LevelBlocks];
    private readonly float[] _levelGain = new float[LevelBlocks];
    private long _levelAt;          // the next frame to measure
    private long _levelFrom;        // where the timeline last started over
    private double _levelL, _levelR;
    private float _peakL, _peakR;

    // THE SAMPLES THEMSELVES, for the 31-band analyzer: the last 2^18 frames
    // (6 s at 44.1 kHz, 1.4 s at 192 kHz, either well past what is buffered),
    // mixed to mono, by their place on the same timeline. Written as they are
    // measured, read only while the analyzer is on screen (Spectrum), so a
    // window without one pays for a store, not for a transform.
    private const int RingShift = 18;
    private readonly float[] _ring = new float[1 << RingShift];

    private void Measure(ReadOnlySpan<float> chunk, int channels)
    {
        var right = channels > 1 ? 1 : 0;
        const int ringMask = (1 << RingShift) - 1;
        for (var i = 0; i < chunk.Length; i += channels)
        {
            float l = chunk[i], r = chunk[i + right];
            _ring[(int)(_levelAt & ringMask)] = (l + r) * 0.5f;
            _levelL += (double)l * l;
            _levelR += (double)r * r;
            _peakL = Math.Max(_peakL, Math.Abs(l));
            _peakR = Math.Max(_peakR, Math.Abs(r));
            if ((++_levelAt & ((1 << LevelShift) - 1)) != 0) continue;
            var block = (_levelAt >> LevelShift) - 1;
            var slot = (int)(block % LevelBlocks);
            _levelLeft[slot] = (float)(_levelL / (1 << LevelShift));
            _levelRight[slot] = (float)(_levelR / (1 << LevelShift));
            _peakLeft[slot] = _peakL;
            _peakRight[slot] = _peakR;
            _levelGain[slot] = _decodingGain;
            _levelBlock[slot] = block;
            _levelL = _levelR = 0;
            _peakL = _peakR = 0;
        }
    }

    /// The RMS levels of the block sounding now, in dBFS, before the gain.
    public (double Left, double Right) Levels()
    {
        var m = Meter(afterGain: false);
        return (m.RmsLeft, m.RmsRight);
    }

    private MeterLevels _lastMeter = MeterLevels.Silent;

    /// RMS and peak of the block sounding now, in dBFS: as the record was
    /// mastered, or with its ReplayGain applied (what reaches the volume).
    public MeterLevels Meter(bool afterGain)
    {
        if (!_lock.TryEnter(UiWait)) return _lastMeter;
        try
        {
            if (_out is null) return _lastMeter = MeterLevels.Silent;
            var block = _out.Played >> LevelShift;
            var slot = (int)(block % LevelBlocks);
            if (_levelBlock[slot] != block) return _lastMeter = MeterLevels.Silent;
            var g = afterGain ? _levelGain[slot] : 1f;
            return _lastMeter = new MeterLevels(
                Db(_levelLeft[slot] * g * g), Db(_levelRight[slot] * g * g),
                Db(_peakLeft[slot] * _peakLeft[slot] * g * g), Db(_peakRight[slot] * _peakRight[slot] * g * g));
        }
        finally { _lock.Exit(); }

        static double Db(float meanSquare) => meanSquare > 0 ? 10 * Math.Log10(meanSquare) : double.NegativeInfinity;
    }

    private readonly float[] _window = new float[Spectrum.Size];

    /// The 31 third-octave bands of what is sounding now, in dB (a full-scale sine
    /// in a band reads 0), into `bands`. False, with nothing written, when there
    /// is nothing to measure or the audio thread is busy: the caller keeps what
    /// it showed last.
    public bool Bands(Span<float> bands, bool afterGain)
    {
        int rate;
        float g;
        if (!_lock.TryEnter(UiWait)) return false;
        try
        {
            if (_out is null) return false;
            var end = _out.Played;
            var block = end >> LevelShift;
            var slot = (int)(block % LevelBlocks);
            // The block sounding now must have been measured, and with it the
            // samples before it: a seek or a new output starts the timeline over.
            if (_levelBlock[slot] != block || end - Spectrum.Size < _levelFrom) return false;
            rate = _out.SampleRate;
            g = afterGain ? _levelGain[slot] : 1f;
            const int ringMask = (1 << RingShift) - 1;
            for (var i = 0; i < Spectrum.Size; i++)
                _window[i] = _ring[(int)((end - Spectrum.Size + i) & ringMask)];
        }
        finally { _lock.Exit(); }

        Spectrum.ThirdOctaves(_window, rate, g, bands);
        return true;
    }

    // ---------------------------------------------------------------- watching

    /// Follows the output: which track is sounding, and when the end of the
    /// queue (or a change of format) has played out.
    private void Watch()
    {
        while (_running)
        {
            Thread.Sleep(40);
            Mark? now = null;
            bool drained = false;
            IAudioTrack? afterBreak = null;
            int breakIndex = -1;
            lock (_lock)
            {
                if (_out is null) continue;
                now = Current(_out.Played);
                drained = _out.Drained;
                if (drained && _afterBreak is not null)
                {
                    afterBreak = _afterBreak;
                    breakIndex = _decodingIndex;
                    _afterBreak = null;
                }
                // What has played is let go of, all but the track sounding now.
                if (now is { } n) _marks.RemoveAll(m => m.At < n.At);
            }

            if (now is { } mark && mark.Index != _index)
            {
                _index = mark.Index;
                _duration = mark.Track.Duration;
                lock (_lock)
                    if (mark.Index < _queue.Count)
                        Console.WriteLine($"[engine] playing {mark.Index}  {System.IO.Path.GetFileName(_names[mark.Index])}");
                TrackChanged?.Invoke(this, new TrackChangedEventArgs(mark.Index));
                DurationChanged?.Invoke(this, EventArgs.Empty);
            }

            if (afterBreak is not null)
            {
                Console.WriteLine($"[engine] new output for {breakIndex}: {afterBreak.SampleRate} Hz, {afterBreak.Channels} ch");
                if (!Begin(breakIndex, afterBreak, 0, _playing))
                {
                    Finished?.Invoke(this, EventArgs.Empty);
                    _index = -1;
                }
            }
            else if (drained)
            {
                // The record has played out. Finished goes while Index still
                // names the last track, as the app expects (it asks whether the
                // player was on one); then it is on none.
                Discard();
                SetState(false);
                Finished?.Invoke(this, EventArgs.Empty);
                _index = -1;
            }
        }
    }

    private void SetState(bool playing)
    {
        _playing = playing;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _running = false;
        Discard();
        _watch.Join(500);
    }
}
