// Marginal — synced lyrics in the LRC form: "[01:12.40] a line", one time per
// line (or several, for a line sung more than once), with optional header tags
// like [ar:Artist] and [offset:+250]. Read leniently, written plainly.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Marginal.Core;

public sealed record LrcLine(TimeSpan Time, string Text);

public static partial class Lrc
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex Stamp();

    [GeneratedRegex(@"^\s*\[(ar|ti|al|by|au|length|offset|re|ve|#):[^\]]*\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Header();

    [GeneratedRegex(@"^\s*\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex Offset();

    /// The timed lines, in time order. [offset:] is applied (positive means
    /// the lyrics come sooner). Lines with no time are left out.
    public static IReadOnlyList<LrcLine> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var offset = Offset().Match(text) is { Success: true } o ? TimeSpan.FromMilliseconds(int.Parse(o.Groups[1].Value, CultureInfo.InvariantCulture)) : TimeSpan.Zero;
        var lines = new List<LrcLine>();
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (Header().IsMatch(raw)) continue;
            var stamps = Stamp().Matches(raw);
            if (stamps.Count == 0) continue;
            var words = raw[(stamps[^1].Index + stamps[^1].Length)..].Trim();
            foreach (Match m in stamps)
            {
                var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var fraction = m.Groups[3].Success ? m.Groups[3].Value : "0";
                var ms = int.Parse(fraction.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
                var time = new TimeSpan(0, 0, minutes, seconds, ms) - offset;
                lines.Add(new LrcLine(time < TimeSpan.Zero ? TimeSpan.Zero : time, words));
            }
        }
        return lines.OrderBy(l => l.Time).ToList();
    }

    /// "[mm:ss.xx] text" lines, hundredths as most players write them.
    public static string Format(IEnumerable<LrcLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines)
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"[{(int)l.Time.TotalMinutes:00}:{l.Time.Seconds:00}.{l.Time.Milliseconds / 10:00}] {l.Text}").TrimEnd()).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    /// The words alone: synced lyrics with their times taken out, header tags
    /// dropped. Plain text comes back as it was, tidied.
    public static string Plain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (TrackReader.KindOf(text) != LyricsKind.Synced) return Tidy(text);
        return Tidy(string.Join('\n', text.ReplaceLineEndings("\n").Split('\n')
            .Where(l => !Header().IsMatch(l))
            .Select(l => Stamp().Replace(l, "").Trim())));
    }

    /// One kind of line ending, no trailing space or blank lines at the ends
    /// (AlbumWall's TagWriter.Tidy: what a lyric is compared and kept as).
    public static string Tidy(string? text) =>
        string.Join('\n', (text ?? "").ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd())).Trim();
}
