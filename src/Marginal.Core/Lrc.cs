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

    // Up to seven digits (under three hours) and [ \t], not \s: an offset of
    // 99999999999 overflowed int.Parse and took the app down on Play, and \s
    // across line ends is quadratic on blank lines (security review
    // 2026-10-31, A3 and A4). A longer number is no offset.
    [GeneratedRegex(@"^[ \t]*\[offset:[ \t]*([+-]?\d{1,7})[ \t]*\]", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex OffsetTag();

    /// The [offset:] of a lyric, in milliseconds as written (positive means
    /// the lyrics come sooner), or zero.
    public static TimeSpan Offset(string? text) =>
        text is not null && OffsetTag().Match(text) is { Success: true } o
        && int.TryParse(o.Groups[1].ValueSpan, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms)
            ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;

    /// The timed lines, in time order. [offset:] is applied. Lines with no
    /// time are left out.
    public static IReadOnlyList<LrcLine> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var offset = Offset(text);
        var lines = new List<LrcLine>();
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (Header().IsMatch(raw)) continue;
            var stamps = Stamp().Matches(raw);
            if (stamps.Count == 0) continue;
            var words = raw[(stamps[^1].Index + stamps[^1].Length)..].Trim();
            foreach (Match m in stamps)
            {
                var time = StampTime(m) - offset;
                lines.Add(new LrcLine(time < TimeSpan.Zero ? TimeSpan.Zero : time, words));
            }
        }
        return lines.OrderBy(l => l.Time).ToList();
    }

    /// A stamp's time as written. The pattern allows at most 999:99.999, so
    /// nothing here can overflow.
    private static TimeSpan StampTime(Match m)
    {
        var minutes = int.Parse(m.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
        var seconds = int.Parse(m.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
        var fraction = m.Groups[3].Success ? m.Groups[3].Value : "0";
        var ms = int.Parse(fraction.PadRight(3, '0').AsSpan(0, 3), CultureInfo.InvariantCulture);
        return new TimeSpan(0, 0, minutes, seconds, ms);
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

    // ---- timing by hand (Tap to time, nudges, shift) --------------------------
    //
    // These work on the editor's text line by line: a line keeps its words and
    // gets (or loses, or moves) the one time at its start. Header tags and
    // blank lines are never timed.

    /// The lines of `text`, as the editor shows them.
    public static string[] Lines(string? text) => (text ?? "").ReplaceLineEndings("\n").Split('\n');

    /// Whether line `i` is one that takes a time: words, not a header or blank.
    public static bool Timeable(string[] lines, int i) =>
        i >= 0 && i < lines.Length && Words(lines[i]).Length > 0 && !Header().IsMatch(lines[i]);

    /// The words of a line, any times at its start taken off.
    public static string Words(string line) =>
        StartStamps().Match(line) is { Success: true } m ? line[m.Length..].Trim() : line.Trim();

    [GeneratedRegex(@"^\s*(\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]\s*)+")]
    private static partial Regex StartStamps();

    /// The time of a line as written (its earliest stamp), if it has one.
    /// [offset:] is not applied: this is what the editor shows and changes.
    public static TimeSpan? TimeOf(string line)
    {
        if (Header().IsMatch(line)) return null;
        TimeSpan? first = null;
        foreach (Match m in Stamp().Matches(line))
            if (StampTime(m) is var t && (first is null || t < first)) first = t;
        return first;
    }

    /// Every line's time as written (null for none), and the [offset:]: read
    /// once per edit, so the 50 ms clock is not re-reading the whole lyric
    /// (it was slower than the clock at 50,000 lines; review A10).
    public sealed record Timeline(string[] Lines, TimeSpan?[] Times, TimeSpan Offset)
    {
        /// The line being sung at a playing position: the last timed line at
        /// or before it, [offset:] applied, or -1 before the first.
        public int LineAt(TimeSpan position)
        {
            var found = -1;
            for (var i = 0; i < Times.Length; i++)
                if (Times[i] is { } t && t - Offset <= position && (found < 0 || t >= Times[found]!.Value)) found = i;
            return found;
        }
    }

    public static Timeline TimelineOf(string? text)
    {
        var lines = Lines(text);
        return new Timeline(lines, lines.Select(TimeOf).ToArray(), Offset(text));
    }

    /// Line `i` with `time` at its start, in place of any it had.
    public static string SetTime(string? text, int i, TimeSpan time)
    {
        var lines = Lines(text);
        if (!Timeable(lines, i)) return text ?? "";
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        lines[i] = Format([new LrcLine(time, Words(lines[i]))]);
        return string.Join('\n', lines);
    }

    /// Line `i` with its time taken off: the words alone again.
    public static string ClearTime(string? text, int i)
    {
        var lines = Lines(text);
        if (i < 0 || i >= lines.Length || TimeOf(lines[i]) is null) return text ?? "";
        lines[i] = Words(lines[i]);
        return string.Join('\n', lines);
    }

    /// Line `i`'s time moved by `delta` (a line without one is left alone).
    public static string Nudge(string? text, int i, TimeSpan delta)
    {
        var lines = Lines(text);
        return i >= 0 && i < lines.Length && TimeOf(lines[i]) is { } t ? SetTime(text, i, t + delta) : text ?? "";
    }

    /// Every timed line moved by `delta`: for lyrics that run early or late
    /// throughout (another edition's intro, a different master).
    public static string ShiftAll(string? text, TimeSpan delta)
    {
        // One pass over the lines (it was a whole-text rewrite per line:
        // 3 s for 10,000 lines; review A10).
        var lines = Lines(text);
        var changed = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!Timeable(lines, i) || TimeOf(lines[i]) is not { } t) continue;
            var moved = t + delta;
            lines[i] = Format([new LrcLine(moved < TimeSpan.Zero ? TimeSpan.Zero : moved, Words(lines[i]))]);
            changed = true;
        }
        return changed ? string.Join('\n', lines) : text ?? "";
    }

    /// The line being sung at `position`, [offset:] applied, or -1.
    public static int LineAt(string? text, TimeSpan position) => TimelineOf(text).LineAt(position);

    /// The next line after `i` that takes a time, or -1.
    public static int NextTimeable(string? text, int i)
    {
        var lines = Lines(text);
        for (var j = Math.Max(0, i + 1); j < lines.Length; j++)
            if (Timeable(lines, j)) return j;
        return -1;
    }

    /// One kind of line ending, no trailing space or blank lines at the ends
    /// (AlbumWall's TagWriter.Tidy: what a lyric is compared and kept as).
    public static string Tidy(string? text) =>
        string.Join('\n', (text ?? "").ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd())).Trim();
}
