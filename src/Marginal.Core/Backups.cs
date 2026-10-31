// Marginal — keeping the backup folder in check.
//
// A backup is a whole original file (TagWriter), so they add up: two test
// writes of one album each came to 364 MB (2026-10-31). Kept for a set time,
// then pruned; their size is always on screen, and they can be emptied.
// Emptying gives up Undo for every write in them, so the window asks first.

namespace Marginal.Core;

/// One write's backup folder: when, how big, how many files, undone or not.
public sealed record BackupBatch(string Folder, DateTime Time, long Bytes, int Files, bool Undone);

public static class Backups
{
    /// How long a write's originals are kept before pruning.
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    public static IReadOnlyList<BackupBatch> List(string? root = null)
    {
        root ??= TagWriter.DefaultBackupRoot;
        if (!Directory.Exists(root)) return [];
        var batches = new List<BackupBatch>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var time = DateTime.TryParseExact(Path.GetFileName(dir), "yyyyMMdd-HHmmss-fff", null,
                System.Globalization.DateTimeStyles.None, out var t) ? t : Directory.GetCreationTime(dir);
            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".tsv", StringComparison.Ordinal)).ToList();
            var bytes = files.Sum(f => new FileInfo(f).Length);
            var undone = File.Exists(Path.Combine(dir, "manifest.undone.tsv"));
            batches.Add(new BackupBatch(dir, time, bytes, files.Count, undone));
        }
        return batches.OrderByDescending(b => b.Time).ToList();
    }

    public static long Size(string? root = null) => List(root).Sum(b => b.Bytes);

    /// Deletes the backups of writes older than `keep`. Returns how many
    /// writes and bytes went.
    public static (int Writes, long Bytes) Prune(TimeSpan keep, string? root = null, DateTime? now = null)
    {
        var cutoff = (now ?? DateTime.Now) - keep;
        var old = List(root).Where(b => b.Time < cutoff).ToList();
        return Delete(old);
    }

    /// Deletes every backup: no write before now can be undone after this.
    public static (int Writes, long Bytes) Empty(string? root = null) => Delete(List(root));

    private static (int, long) Delete(IReadOnlyList<BackupBatch> batches)
    {
        var writes = 0;
        var bytes = 0L;
        foreach (var b in batches)
        {
            try
            {
                Directory.Delete(b.Folder, recursive: true);
                writes++;
                bytes += b.Bytes;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return (writes, bytes);
    }
}
