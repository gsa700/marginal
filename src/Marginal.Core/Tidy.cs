// Marginal — what a library ripped or bought over twenty years carries
// (a survey of Dave's lossy library, 2026-10-31: 1,101 albums, 15,529 tracks):
//
//   * dates in two forms on one album: iTunes Match wrote full timestamps
//     ("2005-08-23T07:00:00Z") beside plain years ("2005"), 71 albums;
//   * genres as old ID3v1 numbers ("17", "(13)"), 1,230 tracks;
//   * best-ofs whose tracks keep each song's original year, which is right
//     for a compilation and was flagged as a fault (74 albums).
//
// What a fix would change is worked out here; Pending turns it into changes.

using System.Text.RegularExpressions;

namespace Marginal.Core;

public static partial class Tidy
{
    /// The year at the start of a date, or null.
    public static string? Year(string? date) =>
        date is { Length: >= 4 } && date[..4].All(char.IsAsciiDigit) ? date[..4] : null;

    /// A date without the time iTunes adds: "2005-08-23T07:00:00Z" is "2005-08-23".
    public static string Day(string date) => date.Split('T', 2)[0].Trim();

    /// What every track's date should read when the album's dates share one
    /// year but are written differently: the day where all agree on it, else
    /// the year. Null when the years differ (that is a real difference) or
    /// there is nothing to tidy.
    public static string? AlbumDate(IEnumerable<string?> dates)
    {
        var all = dates.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d!).ToList();
        if (all.Count == 0 || all.Distinct().Count() < 2) return null;
        if (all.Select(Year).Distinct().Count() != 1 || Year(all[0]) is null) return null;
        var days = all.Select(Day).Distinct().ToList();
        return days.Count == 1 ? days[0] : Year(all[0]);
    }

    [GeneratedRegex(@"^\s*\(?(\d{1,3})\)?\s*(.*)$")]
    private static partial Regex NumericGenre();

    /// The genre's name for an old numeric one ("17", "(17)", "(17)Rock"), or
    /// null when it is not numeric or the number is not a known genre.
    public static string? GenreName(string? genre)
    {
        if (genre is null || NumericGenre().Match(genre) is not { Success: true } m) return null;
        if (m.Groups[2].Value.Length > 0 && !m.Groups[2].Value.All(char.IsAsciiDigit)) return m.Groups[2].Value.Trim();
        return byte.TryParse(m.Groups[1].Value, out var n) && TagLib.Genres.IndexToAudio(n) is { Length: > 0 } name ? name : null;
    }

    [GeneratedRegex(@"\b(greatest hits|best of|the best|very best|collection|essential|anthology|gold|definitive|ultimate|platinum|hits|singles|retrospective|20th century masters|millennium collection|classic masters|compilation)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompilationTitle();

    /// Whether an album looks like a compilation: its tracks' years spread
    /// over eight years or more, a best-of title, several artists, or
    /// Various Artists. A guess, offered to be confirmed, never acted on.
    public static bool LooksLikeCompilation(IReadOnlyList<TrackFile> tracks, string? album, string? albumArtist)
    {
        if (tracks.Count < 3) return false;
        if (albumArtist?.Contains("Various", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (tracks.Select(t => t.Get(FieldMap.Artist)).Distinct().Count() >= 3) return true;
        var years = tracks.Select(t => Year(t.Get(FieldMap.Date))).OfType<string>().Select(int.Parse).ToList();
        var spread = years.Count > 1 && years.Max() - years.Min() >= 8;
        return spread || (album is not null && CompilationTitle().IsMatch(album) && years.Distinct().Count() > 1);
    }
}
