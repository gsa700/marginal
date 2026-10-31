using System.Text.Json;
using Marginal.Core;

namespace Marginal.Tests;

/// The lookup against two real MusicBrainz answers saved on 2026-10-31
/// (Fixtures/mb): a search for Facelift, and the release Control's Deadwax rip
/// is tagged with. No network.
public class LookupTests
{
    private static readonly string Mb = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mb");
    private static JsonElement Json(string name) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Mb, name))).RootElement;
    private static readonly MbRelease Facelift = Lookup.Release(Json("release-facelift.json"));
    private const string FaceliftId = "c63ad92b-19d7-41d9-83e6-607202ceba07";

    /// Twelve FLAC tracks shaped like the rip: titles, numbers and lengths
    /// from the release itself, unless changed by `tweak`.
    private static Album Rip(string? taggedRelease = null, Func<int, MbTrack, (string Title, double Seconds)>? tweak = null)
    {
        var files = Facelift.Tracks.Select((t, i) =>
        {
            var (title, seconds) = tweak?.Invoke(i, t) ?? (t.Title, t.LengthMs!.Value / 1000.0);
            var raw = new List<RawFrame>
            {
                new("TITLE", title), new("ARTIST", "Alice in Chains"), new("ALBUM", "Facelift"),
                new("ALBUMARTIST", "Alice in Chains"), new("TRACKNUMBER", t.Number.ToString()), new("TRACKTOTAL", "12"),
                new("DATE", "1990-08-28"),
            };
            if (taggedRelease is not null) raw.Add(new("MUSICBRAINZ_ALBUMID", taggedRelease));
            return new TrackFile($"/rip/{t.Number:D2} {t.Title}.flac", AudioFormat.Flac, TimeSpan.FromSeconds(seconds), 900,
                "Vorbis", false, false, raw, [], LyricsKind.None, null);
        });
        return AlbumReader.Read("/rip", files);
    }

    [Theory]
    [InlineData("https://musicbrainz.org/release/c63ad92b-19d7-41d9-83e6-607202ceba07", FaceliftId)]
    [InlineData("C63AD92B-19D7-41D9-83E6-607202CEBA07", FaceliftId)]
    [InlineData("https://musicbrainz.org/release-group/1a8b3e6c-0000-4000-8000-000000000000", null)]
    [InlineData("https://musicbrainz.org/artist/4bd95eea-b9f6-4d70-a36c-cfea77431553", null)]
    [InlineData("Facelift", null)]
    public void Release_id_from_a_link_or_an_id(string text, string? id) => Assert.Equal(id, Lookup.ReleaseIdFrom(text));

    [Fact]
    public void The_query_is_artist_and_album() =>
        Assert.Equal("artist:\"Alice in Chains\" AND release:\"Facelift\"", Lookup.Query(Rip()));

    [Fact]
    public void Reads_the_release()
    {
        Assert.Equal("Facelift", Facelift.Title);
        Assert.Equal("Alice in Chains", Facelift.Artist);
        Assert.Equal(12, Facelift.Tracks.Count);
        Assert.Equal(("We Die Young", 152400), (Facelift.Tracks[0].Title, Facelift.Tracks[0].LengthMs!.Value));
        Assert.Equal(("Columbia", "CK 46075", "074644607529"), (Facelift.Label, Facelift.CatalogNumber, Facelift.Barcode));
        Assert.Equal("1990-08-28", Facelift.FirstReleaseDate);
        Assert.True(Facelift.ArtFront && Facelift.ArtBack);
    }

    [Fact]
    public void Every_search_result_scores_100_so_Marginal_orders_them()
    {
        var search = Json("search-facelift.json");
        var candidates = Lookup.Candidates(search, Rip());
        Assert.True(candidates.Take(8).All(c => c.Score == 100));
        // CD and digital first, cassette and vinyl after.
        var firstOther = candidates.Select((c, i) => (c, i)).First(x => !x.c.Formats.Contains("CD") && !x.c.Formats.Contains("Digital")).i;
        Assert.All(candidates.Take(firstOther), c => Assert.True(c.Formats.Contains("CD") || c.Formats.Contains("Digital")));
    }

    [Fact]
    public void The_release_the_files_carry_comes_first()
    {
        var candidates = Lookup.Candidates(Json("search-facelift.json"), Rip(taggedRelease: FaceliftId));
        if (candidates.Any(c => c.Id == FaceliftId))   // when the search returned it at all
            Assert.True(candidates[0] is { Id: FaceliftId, Tagged: true });
    }

    [Fact]
    public void Pairs_every_file_exactly_when_titles_and_lengths_agree()
    {
        var pairs = Lookup.Pair(Rip(), Facelift);
        Assert.Equal(12, pairs.Count);
        Assert.All(pairs, p => Assert.Equal("exact", p.Fit));
        Assert.Equal(100, Lookup.FitPercent(pairs));
    }

    [Fact]
    public void Flags_title_case_and_length()
    {
        var pairs = Lookup.Pair(Rip(tweak: (i, t) => i switch
        {
            1 => (t.Title.ToUpperInvariant(), t.LengthMs!.Value / 1000.0),
            2 => (t.Title, t.LengthMs!.Value / 1000.0 + 9),
            _ => (t.Title, t.LengthMs!.Value / 1000.0),
        }), Facelift);
        Assert.Equal(("title case differs", Tone.Fixable), (pairs[1].Fit, pairs[1].Tone));
        Assert.Equal(("length +9 s", Tone.Missing), (pairs[2].Fit, pairs[2].Tone));
        Assert.True(Lookup.FitPercent(pairs) < 100);
    }

    [Fact]
    public void Take_proposes_what_differs_and_leaves_label_unticked()
    {
        var album = Rip();
        var items = Lookup.Take(album, Facelift, Lookup.Pair(album, Facelift), firstRelease: true);
        var label = items.Single(i => i.Label == "Label");
        Assert.False(label.On);
        Assert.Equal(ChangeKind.Add, label.Kind);
        var titles = items.Single(i => i.Label == "Track titles");
        Assert.Empty(titles.Changes);                       // the rip's titles are the release's
        var catalog = items.Single(i => i.Label == "Catalog no.");
        Assert.True(catalog.On);
        Assert.All(catalog.Changes, c => Assert.Equal("CK 46075", c.To));
        var id = items.Single(i => i.Label == "MusicBrainz release");
        Assert.All(id.Changes, c => Assert.Equal(FaceliftId, c.To));
        Assert.Empty(items.Single(i => i.Label == "Album").Changes);
    }

    [Fact]
    public void A_lookup_never_removes()
    {
        var album = Rip();
        var items = Lookup.Take(album, Facelift, Lookup.Pair(album, Facelift), firstRelease: false);
        Assert.DoesNotContain(items.SelectMany(i => i.Changes), c => c.Kind == ChangeKind.Remove);
    }
}
