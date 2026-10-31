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
/// drops one raw frame by its key.
public enum ChangeOp { SetField, RemoveTag, RemoveFrame }

public sealed record Change(
    string Path,
    ChangeOp Op,
    ChangeKind Kind,
    string Key,
    string? From,
    string? To,
    Field? Field = null)
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

    /// Adds `changes` to `list`, replacing any earlier change to the same file
    /// and key: the last edit of a field is the one that counts. A change back
    /// to what the file holds drops out.
    public static List<Change> Merge(IEnumerable<Change> list, IEnumerable<Change> changes)
    {
        var result = list.ToList();
        foreach (var c in changes)
        {
            var i = result.FindIndex(e => e.Path == c.Path && e.Key == c.Key && e.Op == c.Op);
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
