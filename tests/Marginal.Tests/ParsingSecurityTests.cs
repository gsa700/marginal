// Security review 2026-10-31, part A (parsing of untrusted input): each test is
// a crafted file or web answer that crashed, hung or ballooned 0.1.0's
// release candidate, and must now be turned away or read as "nothing".

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Marginal.Core;

namespace Marginal.Tests;

public sealed class ParsingSecurityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("marginal-sec-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fixture Band - Test Album", name);

    // ---- A1: MP4 boxes nested deeper than any real file -----------------------

    private static byte[] Box(string type, byte[] body)
    {
        var b = new byte[8 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        Encoding.ASCII.GetBytes(type, b.AsSpan(4));
        body.CopyTo(b, 8);
        return b;
    }

    /// `depth` boxes of `type`, each inside the last, under `outer`.
    private string Deep(string name, string type, int depth, params string[] outer)
    {
        var inner = Box(type, []);
        for (var i = 1; i < depth; i++) inner = Box(type, inner);
        foreach (var o in outer.Reverse())
            inner = o == "meta" ? Box("meta", [0, 0, 0, 0, .. inner]) : Box(o, inner);
        var file = Path.Combine(_dir, name);
        File.WriteAllBytes(file, [.. Box("ftyp", Encoding.ASCII.GetBytes("M4A \0\0\0\0M4A mp42isom")), .. inner]);
        return file;
    }

    [Fact]
    public void Deep_moov_is_refused_before_TagLib_sees_it()
    {
        var file = Deep("deep.m4a", "moov", 3000);
        Assert.False(Mp4Nesting.IsSafe(file));
        Assert.NotNull(TrackReader.Read(file).Problem);
    }

    [Fact]
    public void Deep_ilst_under_meta_is_refused_too()
    {
        var file = Deep("deep-ilst.m4a", "ilst", 3000, "moov", "udta", "meta");
        Assert.False(Mp4Nesting.IsSafe(file));
    }

    [Fact]
    public void Deep_boxes_with_bytes_after_them_are_still_counted()
    {
        var inner = Box("trak", []);
        for (var i = 1; i < 3000; i++) inner = Box("trak", [.. inner, 0x00]);
        var file = Path.Combine(_dir, "trailing.m4a");
        File.WriteAllBytes(file, Box("moov", inner));
        Assert.False(Mp4Nesting.IsSafe(file));
    }

    [Fact]
    public void Real_m4a_passes() => Assert.True(Mp4Nesting.IsSafe(Fixture("03 Third.m4a")));

    [Fact]
    public void Hymie_does_not_follow_boxes_without_end()
    {
        // 20,000 deep overflowed the stack in Mp4Track.Walk; now the deep part
        // is ignored and the file is simply one without audio.
        var file = Deep("deep-play.m4a", "ilst", 20000, "moov", "udta", "meta");
        var e = Record.Exception(() => Marginal.Codecs.Mp4Track.Open(file));
        Assert.IsNotType<InsufficientExecutionStackException>(e);
    }

    // ---- A3, A4, A10: lyrics -------------------------------------------------

    private const string Huge = "[offset:99999999999]\n[00:01.00] first line\n[00:05.00] second line";

    [Fact]
    public void Offset_too_large_is_no_offset()
    {
        Assert.Equal(TimeSpan.Zero, Lrc.Offset(Huge));
        Assert.Equal(2, Lrc.Parse(Huge).Count);
        Assert.Equal(2, Lrc.LineAt(Huge, TimeSpan.FromSeconds(6)));   // line 0 is the header
        Assert.Contains("[00:03.00] first line", Lrc.ShiftAll(Huge, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Offset_is_applied_when_playing()
    {
        const string text = "[offset:+2000]\n[00:03.00] first\n[00:10.00] second";
        Assert.Equal(TimeSpan.FromSeconds(2), Lrc.Offset(text));
        Assert.Equal(1, Lrc.LineAt(text, TimeSpan.FromSeconds(1.5)));   // 3 s - 2 s; line 0 is the header
        Assert.Equal(-1, Lrc.LineAt(text, TimeSpan.FromSeconds(0.5)));
        Assert.Equal(TimeSpan.FromSeconds(3), Lrc.TimeOf("[00:03.00] first"));   // as written
    }

    [Fact]
    public void Blank_lines_are_read_in_linear_time()
    {
        var text = "a" + new string('\n', 300_000) + "b";
        var clock = Stopwatch.StartNew();
        Assert.Equal(LyricsKind.Plain, TrackReader.KindOf(text));
        Assert.Equal(TimeSpan.Zero, Lrc.Offset(text));
        Assert.True(clock.ElapsedMilliseconds < 1000, $"{clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Long_lyrics_shift_and_play_quickly()
    {
        var text = string.Join('\n', Enumerable.Range(0, 20_000).Select(i => $"[{i / 60 % 100:00}:{i % 60:00}.00] line {i}"));
        var clock = Stopwatch.StartNew();
        var shifted = Lrc.ShiftAll(text, TimeSpan.FromSeconds(1));
        Assert.True(clock.ElapsedMilliseconds < 2000, $"ShiftAll {clock.ElapsedMilliseconds} ms");
        Assert.StartsWith("[00:01.00] line 0", shifted);
        var timeline = Lrc.TimelineOf(text);
        clock.Restart();
        for (var i = 0; i < 20; i++) timeline.LineAt(TimeSpan.FromMinutes(10));
        Assert.True(clock.ElapsedMilliseconds < 500, $"LineAt {clock.ElapsedMilliseconds} ms for 20");
    }

    // ---- A2, A8: web answers of the wrong shape ----------------------------------

    private sealed class Answer(HttpStatusCode code, string body, string type = "application/json") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, type) });
    }

    private sealed class Endless : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Zeros()) });

        private sealed class Zeros : Stream
        {
            public override int Read(byte[] buffer, int offset, int count) { Array.Clear(buffer, offset, count); return count; }
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }
    }

    private const string Html = "<html><body>Down for maintenance</body></html>";

    [Fact]
    public async Task LrcLib_maintenance_page_is_LRCLIB_failing()
    {
        using var lrc = new LrcLibClient(new Answer(HttpStatusCode.OK, Html, "text/html"));
        await Assert.ThrowsAsync<LrcLibException>(() => lrc.SearchAsync("a", "b"));
        await Assert.ThrowsAsync<LrcLibException>(() => lrc.GetAsync("a", "b", "c", TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public async Task LrcLib_odd_values_are_read_as_none()
    {
        const string body = """[{"id":1.5,"trackName":"T","artistName":"A","duration":-1e300,"syncedLyrics":"[00:01.00] x"}, 5, "x", {"id":3e30}]""";
        using var lrc = new LrcLibClient(new Answer(HttpStatusCode.OK, body));
        var results = await lrc.SearchAsync("A", "T");
        Assert.Equal(2, results.Count);
        Assert.Equal(0, results[0].Id);
        Assert.Equal(0, results[0].Duration);
        var fit = LrcLibClient.Fit(results[0], "A", "T", TimeSpan.FromMinutes(3));
        Assert.False(fit.Automatic);
        Assert.Equal("length unknown", fit.Text);
        Assert.Empty(await new LrcLibClient(new Answer(HttpStatusCode.OK, "[1,2]")).SearchAsync("A", "T"));
        Assert.Null(await new LrcLibClient(new Answer(HttpStatusCode.OK, "[1,2]")).GetAsync("A", "T", "", TimeSpan.Zero));
    }

    [Fact]
    public async Task LrcLib_endless_answer_is_cut_off()
    {
        using var lrc = new LrcLibClient(new Endless());
        await Assert.ThrowsAsync<LrcLibException>(() => lrc.SearchAsync("a", "b"));
    }

    [Fact]
    public async Task MusicBrainz_maintenance_page_is_MusicBrainz_failing()
    {
        using var mb = new MusicBrainzClient(new Answer(HttpStatusCode.OK, Html, "text/html"));
        await Assert.ThrowsAsync<MusicBrainzException>(() => mb.SearchReleasesAsync("x"));
        await Assert.ThrowsAsync<MusicBrainzException>(() => mb.GetReleaseAsync("b84ee12a-09ef-421b-82de-0441a926375b"));
    }

    [Fact]
    public async Task MusicBrainz_endless_answer_is_cut_off()
    {
        using var mb = new MusicBrainzClient(new Endless());
        var e = await Record.ExceptionAsync(() => mb.SearchReleasesAsync("x"));
        Assert.True(e is MusicBrainzException or HttpRequestException, e?.ToString());
    }

    private static Album FixtureAlbum() => AlbumReader.Read(Path.GetDirectoryName(Fixture("01 First.flac"))!);

    [Fact]
    public void MusicBrainz_odd_shapes_map_to_nothing()
    {
        using var search = JsonDocument.Parse("""
            {"releases":[{"id":"x","media":{"format":"CD"},"track-count":1.5,"score":3e9,"label-info":"none","artist-credit":7},
                         5, "str", {"media":[1,{"track-count":"9"}]}]}
            """);
        var list = Lookup.Candidates(search.RootElement, FixtureAlbum());
        Assert.Equal(2, list.Count);
        Assert.Equal(0, list.First(c => c.Id == "x").Score);
        using var notObject = JsonDocument.Parse("""{"releases":{"a":1}}""");
        Assert.Empty(Lookup.Candidates(notObject.RootElement, FixtureAlbum()));
        using var release = JsonDocument.Parse("""
            {"id":"r","media":[{"position":2.5,"tracks":{"a":1}},{"tracks":[1,{"position":1e20,"length":-5,"artist-credit":"x"}]}],
             "release-group":[],"cover-art-archive":"yes"}
            """);
        var r = Lookup.Release(release.RootElement);
        Assert.Single(r.Tracks);
        Assert.Null(r.Tracks[0].LengthMs);
        Assert.False(r.ArtFront);
    }

    [Fact]
    public void Archive_odd_shapes_map_to_nothing()
    {
        using var index = JsonDocument.Parse("""
            {"images":[{"types":[1,"Front"],"image":"https://archive.org/a.jpg","thumbnails":[]},
                       {"types":"Back","image":5}, 7]}
            """);
        Assert.Single(CoverArtArchive.Parse(index.RootElement, "r", "e", true));
        using var browse = JsonDocument.Parse("""
            {"releases":[{"id":"b84ee12a-09ef-421b-82de-0441a926375b","cover-art-archive":{"count":3e9},"media":"CD"},
                         {"id":"c","cover-art-archive":[]}, 4]}
            """);
        Assert.Single(CoverArtArchive.Editions(browse.RootElement, null));
    }

    // ---- A9, A11: archive downloads --------------------------------------------

    private sealed class Redirect(string to) : HttpMessageHandler
    {
        public int Asked;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked++;
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri(to);
            return Task.FromResult(r);
        }
    }

    [Fact]
    public async Task Archive_redirect_elsewhere_is_not_followed()
    {
        var handler = new Redirect("https://example.com/x.jpg");
        using var http = new HttpClient(handler);
        var (image, error) = await CoverArtArchive.ImageAsync(http, "https://coverartarchive.org/release/x/front");
        Assert.Null(image);
        Assert.Equal("sent elsewhere", error);
        Assert.Equal(1, handler.Asked);
    }

    private sealed class Drip : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Slow()) });

        private sealed class Slow : Stream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                await Task.Delay(200, ct);
                buffer.Span[0] = 0xFF;
                return 1;
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task Archive_download_has_a_deadline()
    {
        var before = CoverArtArchive.RequestLimit;
        CoverArtArchive.RequestLimit = TimeSpan.FromSeconds(1);
        try
        {
            using var http = new HttpClient(new Drip());
            var clock = Stopwatch.StartNew();
            var (image, error) = await CoverArtArchive.ImageAsync(http, "https://coverartarchive.org/release/x/front");
            Assert.Null(image);
            Assert.Equal("timed out", error);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
        }
        finally { CoverArtArchive.RequestLimit = before; }
    }
}
