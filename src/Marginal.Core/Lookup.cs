// Marginal — a MusicBrainz lookup, from search to the changes it proposes
// (spec §3.3). One screen: find, pick a release, choose what to take. The
// result is a list of changes for Pending; nothing here writes.
//
// MusicBrainz scores every release of an album 100 for a plain artist and
// title search (Facelift: 28 releases, the first 25 all 100, 2026-10-31), so
// the order is Marginal's own: the release the files are already tagged with,
// then a matching track count, then CD and digital ahead of vinyl and
// cassette (what files are usually ripped or bought from), then the score.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Marginal.Core;

public sealed record ReleaseCandidate(
    string Id, string Title, string Artist, string Date, string Country, string Formats,
    int Tracks, string Label, string CatalogNumber, string Barcode, int Score, bool Tagged)
{
    /// "CD · 1990 · Columbia · US", the second line of the list.
    public string Line => string.Join(" · ", new[] { Formats, Date, Label, Country }.Where(s => s.Length > 0));
}

public sealed record MbTrack(int Disc, int DiscCount, int Number, int TrackCount, string Title, string Artist, int? LengthMs);

public sealed record MbRelease(
    string Id, string Title, string Artist, string Date, string FirstReleaseDate, string ReleaseGroupId,
    string Label, string CatalogNumber, string Barcode, string Country, string Formats,
    IReadOnlyList<MbTrack> Tracks, bool ArtFront, bool ArtBack);

/// One of the album's files beside the MusicBrainz track it was paired with.
public sealed record Pair(TrackFile? File, MbTrack? Track, string Fit, Tone Tone);

/// A line of the "what to take" checklist: its changes, and whether it is
/// ticked to begin with.
public sealed record TakeItem(string Label, string Now, string Theirs, IReadOnlyList<Change> Changes, bool On)
{
    public ChangeKind? Kind => Changes.Count == 0 ? null
        : Changes.All(c => c.Kind == ChangeKind.Add) ? ChangeKind.Add
        : Changes.All(c => c.Kind == ChangeKind.Remove) ? ChangeKind.Remove : ChangeKind.Change;
}

public static partial class Lookup
{
    /// The query from the tags: artist, album, and the number of tracks.
    public static string Query(Album album)
    {
        var parts = new List<string>();
        if (album.Artist.Length > 0) parts.Add($"artist:\"{Escape(album.Artist)}\"");
        parts.Add($"release:\"{Escape(album.Title)}\"");
        return string.Join(" AND ", parts);
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Mbid();

    /// A release ID from a pasted link ("https://musicbrainz.org/release/…")
    /// or a bare ID; null when there is none.
    public static string? ReleaseIdFrom(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Contains("musicbrainz.org", StringComparison.OrdinalIgnoreCase) && !text.Contains("/release/", StringComparison.OrdinalIgnoreCase))
            return null;   // a link to an artist or a release group, not a release
        var m = Mbid().Match(text);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    public static IReadOnlyList<ReleaseCandidate> Candidates(JsonElement search, Album album)
    {
        var tagged = album.Fields.First(f => f.Field == FieldMap.MusicBrainzRelease).Common;
        var list = search.TryGetProperty("releases", out var releases)
            ? releases.EnumerateArray().Select(r => Candidate(r, tagged)).ToList()
            : [];
        var n = album.Tracks.Count;
        return list
            .OrderByDescending(c => c.Tagged)
            .ThenBy(c => c.Tracks == n ? 0 : 1)
            .ThenBy(c => FormatRank(c.Formats))
            .ThenByDescending(c => c.Score)
            .ThenBy(c => c.Date.Length == 0 ? "9999" : c.Date, StringComparer.Ordinal)
            .ToList();
    }

    private static int FormatRank(string formats) =>
        formats.Contains("CD", StringComparison.OrdinalIgnoreCase) || formats.Contains("Digital", StringComparison.OrdinalIgnoreCase) ? 0
        : formats.Length == 0 ? 1 : 2;

    public static ReleaseCandidate Candidate(JsonElement r, string? taggedId = null)
    {
        var id = Str(r, "id");
        var media = r.TryGetProperty("media", out var m) ? m.EnumerateArray().ToList() : [];
        var formats = string.Join(" + ", media.Select(x => Str(x, "format")).Where(f => f.Length > 0)
            .GroupBy(f => f).Select(g => g.Count() > 1 ? $"{g.Count()}×{g.Key}" : g.Key));
        var tracks = r.TryGetProperty("track-count", out var tc) && tc.ValueKind == JsonValueKind.Number ? tc.GetInt32()
            : media.Sum(x => x.TryGetProperty("track-count", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0);
        var (label, cat) = LabelOf(r);
        return new ReleaseCandidate(id, Str(r, "title"), Credit(r), Str(r, "date"), Str(r, "country"), formats,
            tracks, label, cat, Str(r, "barcode"), r.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0,
            string.Equals(id, taggedId, StringComparison.OrdinalIgnoreCase));
    }

    public static MbRelease Release(JsonElement r)
    {
        var tracks = new List<MbTrack>();
        var media = r.TryGetProperty("media", out var m) ? m.EnumerateArray().ToList() : [];
        foreach (var medium in media)
        {
            var disc = medium.TryGetProperty("position", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 1;
            var list = medium.TryGetProperty("tracks", out var t) ? t.EnumerateArray().ToList() : [];
            foreach (var track in list)
            {
                var number = track.TryGetProperty("position", out var tp) && tp.ValueKind == JsonValueKind.Number ? tp.GetInt32() : tracks.Count + 1;
                int? length = track.TryGetProperty("length", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null;
                var artist = track.TryGetProperty("artist-credit", out _) ? Credit(track) : Credit(r);
                tracks.Add(new MbTrack(disc, media.Count, number, list.Count, Str(track, "title"), artist, length));
            }
        }
        var group = r.TryGetProperty("release-group", out var g) ? g : default;
        var (label, cat) = LabelOf(r);
        var art = r.TryGetProperty("cover-art-archive", out var caa) ? caa : default;
        return new MbRelease(Str(r, "id"), Str(r, "title"), Credit(r), Str(r, "date"),
            group.ValueKind == JsonValueKind.Object ? Str(group, "first-release-date") : "",
            group.ValueKind == JsonValueKind.Object ? Str(group, "id") : "",
            label, cat, Str(r, "barcode"), Str(r, "country"),
            string.Join(" + ", media.Select(x => Str(x, "format")).Where(f => f.Length > 0).Distinct()),
            tracks,
            art.ValueKind == JsonValueKind.Object && art.TryGetProperty("front", out var fr) && fr.ValueKind == JsonValueKind.True,
            art.ValueKind == JsonValueKind.Object && art.TryGetProperty("back", out var bk) && bk.ValueKind == JsonValueKind.True);
    }

    // ---- pairing ---------------------------------------------------------------

    /// Each file with its track: by disc and track number from the tags, or in
    /// order when the files carry no numbers. Lengths within 2 s are a fit,
    /// within 5 s a doubt, more a misfit (Deadwax allows 3 s against a TOC).
    public static IReadOnlyList<Pair> Pair(Album album, MbRelease release)
    {
        var pairs = new List<Pair>();
        var used = new HashSet<MbTrack>();
        var numbered = album.Tracks.All(t => Number(t.Get(FieldMap.Track)) > 0);
        for (var i = 0; i < album.Tracks.Count; i++)
        {
            var file = album.Tracks[i];
            MbTrack? track;
            if (numbered)
            {
                var disc = Math.Max(1, Number(file.Get(FieldMap.Disc)));
                var n = Number(file.Get(FieldMap.Track));
                track = release.Tracks.FirstOrDefault(t => t.Disc == disc && t.Number == n);
            }
            else track = i < release.Tracks.Count ? release.Tracks[i] : null;

            if (track is null || !used.Add(track)) { pairs.Add(new Pair(file, null, "no track", Tone.Missing)); continue; }
            pairs.Add(Judge(file, track));
        }
        foreach (var t in release.Tracks.Where(t => !used.Contains(t)))
            pairs.Add(new Pair(null, t, "no file", Tone.Note));
        return pairs;
    }

    private static Pair Judge(TrackFile file, MbTrack track)
    {
        if (track.LengthMs is not { } ms) return new Pair(file, track, "no length", Tone.Note);
        var diff = (int)Math.Round(file.Duration.TotalSeconds - ms / 1000.0);
        var title = file.Get(FieldMap.Title) ?? "";
        var titleFit = title == track.Title ? null
            : string.Equals(Plain(title), Plain(track.Title), StringComparison.OrdinalIgnoreCase) ? "title case differs" : "title differs";
        var abs = Math.Abs(diff);
        var lengthTone = abs <= 2 ? Tone.Clean : abs <= 5 ? Tone.Fixable : Tone.Missing;
        var lengthText = diff == 0 ? "exact" : $"{diff:+0;−0} s";
        if (titleFit is not null && lengthTone == Tone.Clean) return new Pair(file, track, titleFit, Tone.Fixable);
        return new Pair(file, track, lengthTone == Tone.Clean ? lengthText : $"length {lengthText}", lengthTone);
    }

    /// How well a release fits: the share of files paired within 2 s.
    public static int FitPercent(IReadOnlyList<Pair> pairs)
    {
        var files = pairs.Count(p => p.File is not null);
        if (files == 0) return 0;
        var good = pairs.Count(p => p is { File: not null, Track: not null } && p.Tone != Tone.Missing && !p.Fit.StartsWith("length"));
        var extra = pairs.Count(p => p.File is null);
        return (int)Math.Round(100.0 * good / (files + extra));
    }

    // ---- what to take ------------------------------------------------------------

    /// The checklist (spec §3.3). Label is unticked (a reissue's label is not
    /// the album's); `firstRelease` picks the year: the album's first release
    /// (true) or this pressing.
    public static IReadOnlyList<TakeItem> Take(Album album, MbRelease release, IReadOnlyList<Pair> pairs, bool firstRelease)
    {
        var paired = pairs.Where(p => p is { File: not null, Track: not null }).ToDictionary(p => p.File!.Path, p => p.Track!);
        MbTrack? T(TrackFile f) => paired.GetValueOrDefault(f.Path);
        var date = firstRelease && release.FirstReleaseDate.Length > 0 ? release.FirstReleaseDate : release.Date;

        var items = new List<TakeItem>
        {
            Item(album, "Track titles", FieldMap.Title, f => T(f)?.Title, true, "per track"),
            Item(album, "Track artists", FieldMap.Artist, f => T(f)?.Artist, true, "per track"),
            Item(album, "Track numbers", FieldMap.Track, f => T(f) is { } t ? $"{t.Number}/{t.TrackCount}" : null, true, "per track"),
            Item(album, "Disc", FieldMap.Disc, f => T(f) is { } t ? $"{t.Disc}/{t.DiscCount}" : null, true),
            Item(album, "Album", FieldMap.Album, _ => release.Title, true),
            Item(album, "Album artist", FieldMap.AlbumArtist, _ => release.Artist, true),
            Item(album, firstRelease ? "Year (first release)" : "Year (this pressing)", FieldMap.Date, _ => Blank(date), true),
            Item(album, "Original date", FieldMap.OriginalDate, _ => Blank(release.FirstReleaseDate), true),
            Item(album, "Label", FieldMap.Label, _ => Blank(release.Label), false),
            Item(album, "Catalog no.", FieldMap.CatalogNumber, _ => Blank(release.CatalogNumber), true),
            Item(album, "Barcode", FieldMap.Barcode, _ => Blank(release.Barcode), true),
            Item(album, "MusicBrainz release", FieldMap.MusicBrainzRelease, _ => release.Id, true),
            Item(album, "MusicBrainz release group", FieldMap.MusicBrainzReleaseGroup, _ => Blank(release.ReleaseGroupId), true),
        };
        // An item with nothing to change is still listed, as "same".
        return items;
    }

    private static TakeItem Item(Album album, string label, Field field, Func<TrackFile, string?> theirs, bool on, string? perTrack = null)
    {
        // Theirs only where MusicBrainz has a value: never a removal from a lookup.
        var changes = Pending.SetField(album, field, f => theirs(f) ?? f.Get(field))
            .Where(c => c.To is not null).ToList();
        string Summary(IEnumerable<string?> values)
        {
            var distinct = values.Select(v => v ?? "(none)").Distinct().ToList();
            return distinct.Count == 1 ? distinct[0] : $"{distinct.Count} values";
        }
        var now = perTrack ?? Summary(album.Tracks.Select(f => f.Get(field)));
        var theirsText = perTrack is not null ? (changes.Count == 0 ? "same" : $"{changes.Count} of {album.Tracks.Count} differ")
            : Summary(album.Tracks.Select(f => theirs(f) ?? f.Get(field)));
        return new TakeItem(label, now, theirsText, changes, on && changes.Count > 0);
    }

    // ---- small helpers -------------------------------------------------------------

    private static string? Blank(string s) => s.Length == 0 ? null : s;

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// An artist credit as written: names and join phrases ("A feat. B").
    private static string Credit(JsonElement e)
    {
        if (!e.TryGetProperty("artist-credit", out var credit) || credit.ValueKind != JsonValueKind.Array) return "";
        return string.Concat(credit.EnumerateArray().Select(c => Str(c, "name") + Str(c, "joinphrase")));
    }

    private static (string Label, string Catalog) LabelOf(JsonElement r)
    {
        if (!r.TryGetProperty("label-info", out var info) || info.ValueKind != JsonValueKind.Array) return ("", "");
        var first = info.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) return ("", "");
        var label = first.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.Object ? Str(l, "name") : "";
        return (label, Str(first, "catalog-number"));
    }

    private static int Number(string? s)
    {
        if (s is null) return 0;
        var n = 0;
        foreach (var c in s) { if (c is < '0' or > '9') break; n = n * 10 + (c - '0'); }
        return n;
    }

    private static string Plain(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());
}
