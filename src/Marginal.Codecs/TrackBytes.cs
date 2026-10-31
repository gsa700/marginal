// COPIED from AlbumWall (src/AlbumWall.Codecs/TrackBytes.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — the bytes of one FLAC file, for FlacTrack, as they arrive: from
// the disk or from a server (roadmap, "Hymie, our own audio engine": "the
// stream is .NET's HttpClient (format=raw, as now)", 2026-10-22).
//
// A track is played as mpv plays one: from the first frames, while the rest is
// still coming. The whole file still ends up in memory, so a seek or a replay
// never goes back to the disk or the server; what arrives is written into one
// array the size the file is, and the decoder waits for the bytes it needs and
// never looks past what has arrived. A local file was read whole before the
// first sample until 2026-10-22, which made a jump to a track not opened ahead
// slower than the same jump on a server's (his ear, the same day).
//
// THE ADDRESS CARRIES THE SIGN-IN. It is used here to fetch and for nothing
// else: never logged, never kept beyond the request. What is logged is the
// track's name (its library path), which the engine is given beside it.

using System.Net.Http;

namespace Marginal.Codecs;

public sealed class TrackBytes
{
    private static readonly TimeSpan Answer = TimeSpan.FromSeconds(10);    // for the server to start sending
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(30);     // with nothing more arriving
    private static readonly TimeSpan Whole = TimeSpan.FromMinutes(2);      // for all of a track with no length

    /// The most a server's track may be (a 24-bit/192 kHz FLAC of an hour is
    /// a third of this): a length is made into one array, so a claimed one is
    /// held to this before it is (security review 2026-10-26, finding 5).
    public const int MaxServerBytes = 1 << 30;

    private static readonly HttpClient Http = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
    {
        Timeout = Timeout.InfiniteTimeSpan,     // a track takes as long as it takes; Stall is the limit
    };

    /// The whole file, as long as it will be; only Available of it is there yet.
    public byte[] Data { get; }
    public int Length => Data.Length;

    private readonly object _gate = new();
    private volatile int _available;
    private volatile bool _done;                // all of it is here, or no more is coming

    private TrackBytes(byte[] data, int available)
    {
        Data = data;
        _available = available;
        _done = available == data.Length;
    }

    /// How much has arrived, from the start.
    public int Available => _available;

    /// True once nothing more will arrive: all of it, or a failure.
    public bool Done => _done;

    /// From where the track is: a file, or a server's address (http or
    /// https), which is fetched and not kept.
    public static TrackBytes Open(string name, string source) =>
        AudioTracks.IsAddress(source) ? FromServer(source, name) : FromFile(source);

    /// Opens the file and returns at once, with the rest read in behind.
    public static TrackBytes FromFile(string path)
    {
        // No buffer of its own: Fill reads in large pieces anyway.
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);
        try
        {
            if (file.Length > int.MaxValue) throw new IOException("the file is too large to play");
            var bytes = new TrackBytes(new byte[file.Length], 0);
            _ = Task.Run(() => bytes.Fill(file, null, path));
            return bytes;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// Asks the server for the file and returns once it has started sending,
    /// with the rest coming in behind. `name` is what a failure is logged as.
    public static TrackBytes FromServer(string address, string name)
    {
        using var answer = new CancellationTokenSource(Answer);
        var response = Http.Send(new HttpRequestMessage(HttpMethod.Get, address), HttpCompletionOption.ResponseHeadersRead, answer.Token);
        try
        {
            response.EnsureSuccessStatusCode();
            // Subsonic says no with a 200 and a message, not with a status.
            if (response.Content.Headers.ContentType?.MediaType is { } type && (type.Contains("xml") || type.Contains("json")))
                throw new IOException("the server answered with an error, not the file");

            var body = response.Content.ReadAsStream(answer.Token);
            if (response.Content.Headers.ContentLength is { } claimed && claimed > MaxServerBytes)
                throw new IOException($"the server offered {claimed:N0} bytes, more than a track can be");
            if (response.Content.Headers.ContentLength is not { } length)
            {
                // No length to make the array from: all of it first, then play.
                // This is the caller's thread (the app's, at a click or at
                // launch), so it is held to the same stall as Fill, to a time
                // for the whole of it, and to a size: unbounded, one reply that
                // trickled froze the app, at every launch once the session
                // named it, and an endless one took 3.4 GB.
                var data = ReadAll(body);
                response.Dispose();
                return new TrackBytes(data, data.Length);
            }

            var bytes = new TrackBytes(new byte[length], 0);
            _ = Task.Run(() => bytes.Fill(body, response, name));
            return bytes;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// A reply with no length, all of it, within Stall per read, Whole in all
    /// and MaxServerBytes in size.
    private static byte[] ReadAll(Stream body)
    {
        using var whole = new CancellationTokenSource(Whole);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(whole.Token);
        using var all = new MemoryStream();
        var buffer = new byte[256 * 1024];
        try
        {
            while (true)
            {
                stall.CancelAfter(Stall);
                var n = body.ReadAsync(buffer, stall.Token).AsTask().GetAwaiter().GetResult();
                if (n == 0) return all.ToArray();
                if (all.Length + n > MaxServerBytes) throw new IOException("the server sent more than a track can be");
                all.Write(buffer, 0, n);
            }
        }
        catch (OperationCanceledException)
        {
            throw new IOException(whole.IsCancellationRequested
                ? $"the server took more than {Whole.TotalMinutes:0} minutes for the track"
                : $"nothing from the server for {Stall.TotalSeconds:0} s");
        }
        finally { body.Dispose(); }
    }

    /// `response` is the server's, or null for a file.
    private async Task Fill(Stream body, HttpResponseMessage? response, string name)
    {
        var from = response is null ? "the file" : "the server";
        string? failure = null;
        try
        {
            using var stall = new CancellationTokenSource();
            var at = 0;
            while (at < Data.Length)
            {
                stall.CancelAfter(Stall);
                var n = await body.ReadAsync(Data.AsMemory(at, Math.Min(256 * 1024, Data.Length - at)), stall.Token);
                if (n == 0) break;
                at += n;
                lock (_gate)
                {
                    _available = at;
                    Monitor.PulseAll(_gate);
                }
            }
            if (at < Data.Length) failure = $"{from} gave {at:N0} of {Data.Length:N0} bytes";
        }
        catch (OperationCanceledException) { failure = $"nothing from {from} for {Stall.TotalSeconds:0} s"; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException) { failure = ex.Message; }
        finally
        {
            body.Dispose();
            response?.Dispose();
            lock (_gate)
            {
                _done = true;
                Monitor.PulseAll(_gate);
            }
        }
        if (failure is not null) Console.WriteLine($"[engine] {System.IO.Path.GetFileName(name)}: reading stopped: {failure}");
    }

    /// Blocks until the first `end` bytes are here. False if they never will
    /// be, or `giveUp` says to stop waiting (it is asked every 50 ms).
    public bool WaitFor(int end, Func<bool>? giveUp = null)
    {
        end = Math.Min(end, Data.Length);
        if (_available >= end) return true;
        lock (_gate)
        {
            while (_available < end && !_done)
            {
                if (giveUp?.Invoke() == true) return false;
                Monitor.Wait(_gate, 50);
            }
            return _available >= end;
        }
    }
}
