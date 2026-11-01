// Marginal — what the main window's lists show, one record per row, built
// from Core's Album and TrackFile. Read-only rows: a new album or a new
// selection makes new rows rather than changing old ones.

using Avalonia.Media;
using Marginal.Core;

namespace Marginal.App;

/// The status colours (App.axaml), for rows that pick theirs from data.
/// Always shown with a word: colour never carries the meaning alone.
public static class Tones
{
    public static readonly IBrush OkText = Brush("#1E6B34"), OkFill = Brush("#E6F4EA");
    public static readonly IBrush WarnText = Brush("#8A4B00"), WarnFill = Brush("#FFF1DC");
    public static readonly IBrush BadText = Brush("#A4231A"), BadFill = Brush("#FDECEA");
    public static readonly IBrush NoteText = Brush("#4A535D"), NoteFill = Brush("#EEF0F3");
    public static readonly IBrush Text = Brush("#1B1F24"), Dim = Brush("#59636E");
    public static readonly IBrush Accent = Brush("#0B5CBF"), Selected = Brush("#F2F7FD"), Bar = Brush("#FBFBFC");

    public static (IBrush Text, IBrush Fill) Of(Tone tone) => tone switch
    {
        Tone.Clean => (OkText, OkFill),
        Tone.Fixable => (WarnText, WarnFill),
        Tone.Missing => (BadText, BadFill),
        _ => (NoteText, NoteFill),
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();
}

public sealed class AlbumRow(Album Album) : System.ComponentModel.INotifyPropertyChanged
{
    public Album Album { get; } = Album;
    public string Folder => Album.Folder;
    /// The folder's name; "Disc 1" and the like with the set's folder before
    /// it, or every disc of a split set reads the same (59 in Dave's lossy
    /// library, 2026-10-31).
    public string FolderName
    {
        get
        {
            var name = System.IO.Path.GetFileName(Album.Folder.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^\(?(disc|disk|cd|disco)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                && !System.Text.RegularExpressions.Regex.IsMatch(name, @"\b(disc|disk|cd|disco)\s*\d+\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return name;
            var parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Album.Folder.TrimEnd(System.IO.Path.DirectorySeparatorChar)));
            return string.IsNullOrEmpty(parent) ? name : $"{parent} / {name}";
        }
    }

    /// Set when another album has the same title and artist: the folder
    /// name is then the only way to tell them apart in the list.
    public bool ShowFolder
    {
        get;
        set { if (field == value) return; field = value; PropertyChanged?.Invoke(this, new(nameof(ShowFolder))); PropertyChanged?.Invoke(this, new(nameof(Spoken))); }
    }

    /// How many changes wait in this album's Pending, shown in the list.
    public int PendingCount
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new(nameof(PendingCount)));
            PropertyChanged?.Invoke(this, new(nameof(PendingText)));
            PropertyChanged?.Invoke(this, new(nameof(HasPending)));
            PropertyChanged?.Invoke(this, new(nameof(Spoken)));
        }
    }

    public string PendingText => $"● {PendingCount} pending";
    public bool HasPending => PendingCount > 0;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Title => Album.Title;
    public string ArtistYear => Album.Year.Length > 0 ? $"{Album.Artist} · {Album.Year}" : Album.Artist;
    public string Formats => Album.Formats;
    public string Summary => Album.Summary;
    public IBrush SummaryText => Tones.Of(Album.Status).Text;
    public IBrush SummaryFill => Tones.Of(Album.Status).Fill;

    /// The row as a screen reader says it (Accessibility, 2026-11-01): the
    /// list item is what has focus, and it has no text of its own.
    public string Spoken => Speech.Join(Title, Album.Artist.Length > 0 ? $"by {Album.Artist}" : "", Album.Year,
        Formats, Summary, ShowFolder ? $"folder {FolderName}" : "", HasPending ? $"{PendingCount} pending" : "");
}

/// Screen-reader text (Accessibility, 2026-11-01): the parts that are there,
/// joined with commas, so a reader pauses between them.
public static class Speech
{
    public static string Join(params string?[] parts) =>
        string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
}

public sealed record FieldRow(string Label, string Value, string Note, IBrush NoteText, IBrush NoteFill, string Key, IBrush ValueText)
{
    public Field? Field { get; init; }
    public bool HasNote => Note.Length > 0;
    public string Spoken => Speech.Join(Label, Value == "—" ? "empty" : Value, Note);

    /// The row as it will be after Write: the pending value in its kind's
    /// colour, with the kind and count as the note.
    public static FieldRow From(FieldSummary s, Album album, IReadOnlyList<Change> pending)
    {
        var row = From(s, album);
        var mine = pending.Where(c => c.Field == s.Field).ToList();
        if (mine.Count == 0) return row with { Field = s.Field };
        var kind = mine.All(c => c.Kind == ChangeKind.Remove) ? ChangeKind.Remove
                 : mine.All(c => c.Kind == ChangeKind.Add) ? ChangeKind.Add : ChangeKind.Change;
        var (ink, fill) = PendingRow.Colours(kind);
        var value = mine[0].To ?? "—";
        var sign = kind switch { ChangeKind.Add => "+", ChangeKind.Remove => "−", _ => "~" };
        return row with
        {
            Field = s.Field,
            Value = value,
            ValueText = ink,
            Note = $"{sign} {mine.Count} pending",
            NoteText = ink,
            NoteFill = fill,
        };
    }

    public static FieldRow From(FieldSummary s, Album album)
    {
        // The key as stored in this album's formats: "TPE2", or "ALBUMARTIST · TPE2" for a mixed folder.
        var formats = album.Tracks.Select(t => t.Format).Distinct()
            .Select(f => f switch { AudioFormat.Flac => TagFormat.Flac, AudioFormat.Mp3 => TagFormat.Id3v2, _ => TagFormat.Mp4 });
        // One key per line: a mixed folder's three, and ID3v2.3's date beside v2.4's.
        var keys = formats.Select(f => s.Field.KeyIn(f)).Distinct().ToList();
        if (s.Field.Id3v23 is not null && album.Tracks.Any(t => t.TagVersions.StartsWith("v2.3")))
            keys = keys.SelectMany(k => k == s.Field.Id3v2 ? new[] { $"{s.Field.Id3v23} (v2.3)", k } : [k]).ToList();
        var key = string.Join("\n", keys);

        var n = album.Tracks.Count;
        if (s.Values.Count == 0)
            return new FieldRow(s.Field.Name, "—", "", Tones.NoteText, Tones.NoteFill, key, Tones.Dim);
        if (!s.Differs)
            return new FieldRow(s.Field.Name, Short(s.Common!), "", Tones.NoteText, Tones.NoteFill, key, Tones.Text);

        // Values that differ: each with its count, most common first.
        var parts = s.Values.Select(v => $"{Short(v.Value)} ×{v.Count}").ToList();
        if (s.Missing > 0) parts.Add($"none ×{s.Missing}");
        if (FieldMap.VariesByTrack(s.Field))
            return new FieldRow(s.Field.Name, string.Join(", ", parts), "varies by track", Tones.NoteText, Tones.NoteFill, key, Tones.Text);
        var note = s.Missing > 0 && s.Values.Count == 1 ? $"missing on {s.Missing} of {n}" : $"{s.Values.Count + (s.Missing > 0 ? 1 : 0)} values";
        var tone = s.Missing > 0 && s.Values.Count == 1 ? Tone.Missing : Tone.Fixable;
        var (t, f) = Tones.Of(tone);
        return new FieldRow(s.Field.Name, string.Join(", ", parts), note, t, f, key, Tones.Text);
    }

    /// One line: lyrics and long comments are not for this table.
    private static string Short(string v)
    {
        var line = v.ReplaceLineEndings(" ⏎ ");
        return line.Length > 90 ? line[..90] + "…" : line;
    }
}

public sealed record TrackRow(TrackFile Track, IReadOnlyList<Check> Checks)
{
    public string Number => Track.Get(FieldMap.Track) is { } t ? t.Split('/')[0].PadLeft(2, '0') : "";
    public string Title => Track.Get(FieldMap.Title) ?? Track.FileName;
    public string Time => Track.Duration.TotalHours >= 1 ? Track.Duration.ToString(@"h\:mm\:ss") : Track.Duration.ToString(@"m\:ss");
    public string Tags => Track.TagVersions;
    public string Art => Track.EmbeddedCover() is { } c ? (c.Width is { } w ? $"{w} px" : "?") : "none";
    public IBrush ArtText => Track.EmbeddedCover() is { Width: < AlbumReader.SmallArt } ? Tones.WarnText : Tones.NoteText;
    public string Lyrics => Track.Lyrics.ToString().ToLowerInvariant();
    public IBrush LyricsText => Track.Lyrics == LyricsKind.Synced ? Tones.OkText : Tones.NoteText;
    public IBrush LyricsFill => Track.Lyrics == LyricsKind.Synced ? Tones.OkFill : Tones.NoteFill;

    private Check? Worst => Checks.OrderByDescending(c => c.Tone).FirstOrDefault();
    public string Check => Worst?.Text ?? "clean";
    public IBrush CheckText => Tones.Of(Worst?.Tone ?? Tone.Clean).Text;
    public IBrush CheckFill => Tones.Of(Worst?.Tone ?? Tone.Clean).Fill;

    public string Spoken => Speech.Join(Number.Length > 0 ? $"track {Number.TrimStart('0')}" : "", Title, Time,
        $"art {Art}", $"lyrics {Lyrics}", Checks.Count == 0 ? "clean" : string.Join(", ", Checks.Select(c => c.Text)));
}

public sealed record RawRow(string Key, string Value, IBrush ValueText, TextDecorationCollection? Decorations)
{
    public static RawRow From(RawFrame r) => new(r.Key, Short(r.Value),
        r.Junk ? Tones.BadText : Tones.Text, r.Junk ? TextDecorations.Strikethrough : null);

    private static string Short(string v)
    {
        var line = v.ReplaceLineEndings(" ⏎ ");
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}

public sealed record PendingRow(Change Change)
{
    public string Kind => Change.Kind switch { ChangeKind.Add => "+ add", ChangeKind.Remove => "− remove", _ => "~ change" };
    public string File => Change.FileName;
    public string Key => Change.Key;
    public string From => Change.From ?? "(none)";
    public string To => Change.To ?? "removed";
    public IBrush Ink => Colours(Change.Kind).Ink;
    public IBrush Fill => Colours(Change.Kind).Fill;
    public string Spoken => Change.Kind switch
    {
        ChangeKind.Add => Speech.Join($"add {Key}", To, $"in {File}"),
        ChangeKind.Remove => Speech.Join($"remove {Key}", $"was {From}", $"in {File}"),
        _ => Speech.Join($"change {Key}", $"from {From}", $"to {To}", $"in {File}"),
    };

    /// + add green, ~ change orange, − remove red (spec §2, rule 2).
    public static (IBrush Ink, IBrush Fill) Colours(ChangeKind kind) => kind switch
    {
        ChangeKind.Add => (Tones.OkText, Tones.OkFill),
        ChangeKind.Remove => (Tones.BadText, Tones.BadFill),
        _ => (Tones.WarnText, Tones.WarnFill),
    };
}
