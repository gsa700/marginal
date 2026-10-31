// Marginal — the Pending list: every change waits here until Write (spec §2,
// rule 1). A change is one file and one thing about it, shown as
// `file · key · from → to` and as a kind: + add, ~ change, − remove.
//
// Building a change never touches a file; only TagWriter does that, and only
// with the whole list for a file at once.

namespace Marginal.Core;

public enum ChangeKind { Add, Change, Remove }

/// What a change does. SetField writes `To` into `Field` (null `To` removes
/// the field); RemoveTag drops a whole leftover tag (ID3v1, APE); RemoveFrame
/// drops one raw frame by its key; WriteImage saves `Data` as an image file
/// beside the tracks (cover.jpg, back.jpg); EmbedFront puts `Data` in a file
/// as its front cover, in place of the one it has.
public enum ChangeOp { SetField, RemoveTag, RemoveFrame, WriteImage, EmbedFront, WriteText }

public sealed record Change(
    string Path,
    ChangeOp Op,
    ChangeKind Kind,
    string Key,
    string? From,
    string? To,
    Field? Field = null,
    byte[]? Data = null)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

public static class Pending
{
    /// Sets `field` to `value` on every track of the album that does not
    /// already hold exactly that; an empty value removes the field.
    public static IReadOnlyList<Change> SetField(Album album, Field field, string? value) =>
        SetField(album, field, _ => value);

    /// Each track's own value (a lookup's titles and track numbers); null or
    /// empty removes the field from that track.
    public static IReadOnlyList<Change> SetField(Album album, Field field, Func<TrackFile, string?> valueOf)
    {
        var changes = new List<Change>();
        foreach (var t in album.Tracks.Where(t => t.Problem is null))
        {
            var value = valueOf(t);
            value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            var now = t.Get(field);
            if (now == value) continue;
            var kind = now is null ? ChangeKind.Add : value is null ? ChangeKind.Remove : ChangeKind.Change;
            changes.Add(new Change(t.Path, ChangeOp.SetField, kind, KeyOf(field, t), now, value, field));
        }
        return changes;
    }

    /// The leftovers "Clean junk tags" offers to remove: ID3v1 and APE tags
    /// beside an ID3v2 one, and frames and atoms TrackReader marks as junk
    /// (store and player leftovers). Music metadata is never on this list.
    public static IReadOnlyList<Change> CleanJunk(Album album)
    {
        var changes = new List<Change>();
        // A freeform atom that is removed takes its whole box: one change per key.
        foreach (var t in album.Tracks.Where(t => t.Problem is null))
        {
            if (t.HasId3v1) changes.Add(new Change(t.Path, ChangeOp.RemoveTag, ChangeKind.Remove, "ID3v1", "present", null));
            if (t.HasApe) changes.Add(new Change(t.Path, ChangeOp.RemoveTag, ChangeKind.Remove, "APE", "present", null));
            var junk = t.Raw.Where(r => r.Junk);
            if (t.Format == AudioFormat.M4a) junk = junk.DistinctBy(r => r.Key);
            foreach (var r in junk)
                changes.Add(new Change(t.Path, ChangeOp.RemoveFrame, ChangeKind.Remove, r.Key, r.Value, null));
        }
        return changes;
    }

    /// Dates in two forms with one year, made one (Tidy.AlbumDate). Nothing
    /// on an album whose years really differ.
    public static IReadOnlyList<Change> TidyDates(Album album)
    {
        var readable = album.Tracks.Where(t => t.Problem is null).ToList();
        if (Tidy.AlbumDate(readable.Select(t => t.Get(FieldMap.Date))) is not { } date) return [];
        return SetField(album, FieldMap.Date, t => t.Get(FieldMap.Date) is null ? null : date)
            .Where(c => c.To is not null).ToList();
    }

    /// Old numeric genres given their names ("17" is Rock).
    public static IReadOnlyList<Change> NameGenres(Album album) =>
        SetField(album, FieldMap.Genre, t => Tidy.GenreName(t.Get(FieldMap.Genre)) ?? t.Get(FieldMap.Genre));

    /// The iTunes Store buyer's Apple ID and name taken out of every file
    /// (TrackReader.IsPurchaseInfo); the rest of the purchase data stays.
    public static IReadOnlyList<Change> RemovePurchaseInfo(Album album) =>
        album.Tracks.Where(t => t.Problem is null)
            .SelectMany(t => t.Raw.Where(r => TrackReader.IsPurchaseInfo(r.Key)).DistinctBy(r => r.Key)
                .Select(r => new Change(t.Path, ChangeOp.RemoveFrame, ChangeKind.Remove, r.Key, r.Value, null)))
            .ToList();

    /// What an ID3v1 removal says when it only goes along with a file's other
    /// changes: told apart from one asked for (Clean up), which stays.
    public const string Id3v1Along = "present · goes with this file's other changes";

    /// Marginal writes no ID3v1 (Dave, 2026-10-31: nothing has read it since
    /// ID3v2 came out in 1998, and TagLib# kept writing it back): an MP3
    /// with one that is changed loses it, said in Pending as its own line.
    /// Run over an album's Pending after every change to it, so the line
    /// comes and goes with the file's other changes.
    public static List<Change> DropId3v1(Album album, IReadOnlyList<Change> list)
    {
        var withV1 = album.Tracks.Where(t => t.Problem is null && t.Format == AudioFormat.Mp3 && t.HasId3v1).Select(t => t.Path).ToHashSet();
        bool IsV1(Change c) => c is { Op: ChangeOp.RemoveTag, Key: "ID3v1" };
        var touched = list.Where(c => !IsV1(c) && withV1.Contains(c.Path)).Select(c => c.Path).ToHashSet();
        var result = list.Where(c => !(IsV1(c) && c.From == Id3v1Along && !touched.Contains(c.Path))).ToList();
        foreach (var path in touched.Where(p => !result.Any(c => IsV1(c) && c.Path == p)))
            result.Add(new Change(path, ChangeOp.RemoveTag, ChangeKind.Remove, "ID3v1", Id3v1Along, null));
        return result;
    }

    /// Marks every track as part of a compilation.
    public static IReadOnlyList<Change> MarkCompilation(Album album) => SetField(album, FieldMap.Compilation, "1");

    /// Saves `image` beside the album's tracks as `name` (cover.jpg, back.jpg).
    public static Change SaveImage(Album album, string name, byte[] image)
    {
        var path = System.IO.Path.Combine(album.Folder, name);
        // Read as a plain file up to the limit (SafeFiles; review B2).
        var now = System.IO.File.Exists(path)
            ? SafeFiles.Read(path, WebLimits.MaxImageBytes) is { } old ? Describe(old) : "(not a file Marginal can read)"
            : null;
        return new Change(path, ChangeOp.WriteImage, now is null ? ChangeKind.Add : ChangeKind.Change, name, now, Describe(image), Data: image);
    }

    /// Embeds `image` as the front cover of every track, in place of the one
    /// each has (or as the first, when it has none).
    public static IReadOnlyList<Change> EmbedFront(Album album, byte[] image)
    {
        var to = Describe(image);
        return album.Tracks.Where(t => t.Problem is null).Select(t =>
        {
            var cover = t.EmbeddedCover();
            var key = t.Format switch { AudioFormat.Flac => "PICTURE", AudioFormat.Mp3 => "APIC", _ => "covr" };
            var from = cover is null ? null : $"{cover.Size} · {TrackReader.Bytes(cover.Bytes)}";
            return new Change(t.Path, ChangeOp.EmbedFront, cover is null ? ChangeKind.Add : ChangeKind.Change, key, from, to, Data: image);
        }).ToList();
    }

    /// Lyrics for one track, where asked (spec §3.2): the file's lyrics tag
    /// (with the time codes only if `timesInTag`: some players show them as
    /// text) and, for synced lyrics, a .lrc beside the track. Empty `text`
    /// removes the lyrics from the tag; an existing .lrc is then left alone.
    public static IReadOnlyList<Change> SetLyrics(TrackFile track, string? text, bool tag, bool lrc, bool timesInTag)
    {
        var changes = new List<Change>();
        var synced = TrackReader.KindOf(text) == LyricsKind.Synced;
        if (tag)
        {
            var value = Lrc.Tidy(synced && !timesInTag ? Lrc.Plain(text) : text);
            var now = track.Get(FieldMap.Lyrics);
            if (Lrc.Tidy(now) != value)
            {
                var key = track.Format switch { AudioFormat.Flac => "LYRICS", AudioFormat.Mp3 => "USLT", _ => "©lyr" };
                var to = value.Length == 0 ? null : value;
                changes.Add(new Change(track.Path, ChangeOp.SetField,
                    now is null ? ChangeKind.Add : to is null ? ChangeKind.Remove : ChangeKind.Change,
                    key, LyricsSummary(now), to, FieldMap.Lyrics));
            }
        }
        if (lrc && synced)
        {
            var body = Lrc.Tidy(text) + "\n";
            var path = track.LrcPath;
            var now = System.IO.File.Exists(path) ? SafeFiles.ReadText(path, SafeFiles.MaxLrcBytes) ?? "(not a file Marginal can read)" : null;
            if (Lrc.Tidy(now) != Lrc.Tidy(body))
                changes.Add(new Change(path, ChangeOp.WriteText, now is null ? ChangeKind.Add : ChangeKind.Change,
                    ".lrc", LyricsSummary(now), LyricsSummary(body), Data: System.Text.Encoding.UTF8.GetBytes(body)));
        }
        return changes;
    }

    /// "synced · 42 lines" or "plain · 31 lines", for Pending's from and to.
    public static string? LyricsSummary(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var kind = TrackReader.KindOf(text) == LyricsKind.Synced ? "synced" : "plain";
        var lines = Lrc.Tidy(text).Split('\n').Count(l => l.Trim().Length > 0);
        return $"{kind} · {lines} line{(lines == 1 ? "" : "s")}";
    }

    /// "1200×1200 · 312 KB".
    public static string Describe(byte[] image) =>
        (ImageSize.Read(image) is { } s ? $"{s.Width}×{s.Height}" : "unknown size") + $" · {TrackReader.Bytes(image.LongLength)}";

    /// Adds `changes` to `list`, replacing any earlier change to the same file
    /// and key: the last edit of a field is the one that counts. A change back
    /// to what the file holds drops out.
    public static List<Change> Merge(IEnumerable<Change> list, IEnumerable<Change> changes)
    {
        var result = list.ToList();
        foreach (var c in changes)
        {
            // A frame removal is that frame: its key AND value. Several frames
            // share a key (four or five Windows Media PRIV frames in one MP3),
            // and matching by key alone kept one removal of them per run
            // (Dave's lossy library, 2026-10-31).
            var i = result.FindIndex(e => e.Path == c.Path && e.Key == c.Key && e.Op == c.Op
                                          && (c.Op != ChangeOp.RemoveFrame || e.From == c.From));
            if (i >= 0)
            {
                var first = result[i];
                result.RemoveAt(i);
                // Kind and From stay those of the file as it is on disk.
                if (first.From == c.To) continue;
                var kind = first.From is null ? ChangeKind.Add : c.To is null ? ChangeKind.Remove : ChangeKind.Change;
                result.Insert(i, c with { From = first.From, Kind = kind });
            }
            else result.Add(c);
        }
        return result;
    }

    private static string KeyOf(Field field, TrackFile t) => t.Format switch
    {
        AudioFormat.Flac => field.Flac,
        AudioFormat.M4a => field.Mp4,
        _ => t.TagVersions.StartsWith("v2.3") && field.Id3v23 is not null ? field.Id3v23 : field.Id3v2,
    };
}
