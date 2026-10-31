// Marginal — an album: a folder of music files, read together, and what is
// wrong with it.
//
// The checks are the ones the main window shows (spec §3.1), each with a tone:
// Missing (red), Fixable (orange), Note (grey). An album's status is the worst
// of its checks. Nothing here changes a file; a check only says what a fix
// would be about.

namespace Marginal.Core;

public enum Tone { Clean, Note, Fixable, Missing }

/// A finding. `Track` is the file it is about, or null for the album.
public sealed record Check(Tone Tone, string Text, string? Track = null);

/// One field across the album's files: each distinct value with how many
/// files hold it, and how many hold none.
public sealed record FieldSummary(Field Field, IReadOnlyList<(string Value, int Count)> Values, int Missing)
{
    public bool Differs => Values.Count > 1 || (Values.Count == 1 && Missing > 0);
    public string? Common => Values.Count > 0 ? Values[0].Value : null;
}

/// A picture beside the tracks: cover.jpg, back.jpg.
public sealed record FolderImage(string Name, int? Width, int? Height, long Bytes)
{
    public string Size => Width is { } w && Height is { } h ? $"{w}×{h}" : "unknown size";
    public bool Square => Width is { } w && Height is { } h && AlbumReader.NearlySquare(w, h);
}

public sealed class Album
{
    public required string Folder { get; init; }
    public required IReadOnlyList<TrackFile> Tracks { get; init; }
    public required IReadOnlyList<FieldSummary> Fields { get; init; }
    public required IReadOnlyList<Check> Checks { get; init; }
    public FolderImage? Cover { get; init; }
    public FolderImage? Back { get; init; }

    public Tone Status => Checks.Count == 0 ? Tone.Clean : Checks.Max(c => c.Tone);

    public string Title => Field(FieldMap.Album) ?? System.IO.Path.GetFileName(Folder);
    public string Artist => Field(FieldMap.AlbumArtist) ?? Field(FieldMap.Artist) ?? "";
    public string Year => Field(FieldMap.Date) is { Length: >= 4 } d ? d[..4] : "";
    public TimeSpan Length => Tracks.Aggregate(TimeSpan.Zero, (t, f) => t + f.Duration);

    /// "FLAC", or "MP3 + M4A" for a mixed folder.
    public string Formats => string.Join(" + ", Tracks.Select(t => t.Format.ToString().ToUpperInvariant()).Distinct());

    private string? Field(Field f) => Fields.First(s => s.Field == f).Common;

    /// A short line for the library list: "clean", "3 fixable", "no art".
    public string Summary
    {
        get
        {
            if (Checks.Count == 0) return "clean";
            var missing = Checks.Count(c => c.Tone == Tone.Missing);
            var fixable = Checks.Count(c => c.Tone == Tone.Fixable);
            if (missing == 1 && fixable == 0) return Checks.First(c => c.Tone == Tone.Missing).Text;
            if (missing > 0) return $"{missing} missing";
            if (fixable == 1) return Checks.First(c => c.Tone == Tone.Fixable).Text;
            return fixable > 0 ? $"{fixable} fixable" : "notes";
        }
    }
}

public static class AlbumReader
{
    /// Fields that should be the same on every track of an album.
    private static readonly Field[] AlbumWide =
        [FieldMap.Album, FieldMap.AlbumArtist, FieldMap.Date, FieldMap.Genre, FieldMap.Label];

    /// Below this a cover looks soft on a large screen (AlbumWall's wall).
    public const int SmallArt = 500;

    /// Square within 1%: a 500×497 scan is square to anyone looking at it,
    /// and flagging it filled the "fixable" list on the first real library
    /// (Control's ~/Music, 2026-10-31).
    public static bool NearlySquare(int w, int h) => Math.Abs(w - h) * 100 <= Math.Max(w, h);

    public static Album Read(string folder) =>
        Read(folder, Directory.EnumerateFiles(folder).Where(TrackReader.IsMusic).Select(TrackReader.Read));

    public static Album Read(string folder, IEnumerable<TrackFile> files)
    {
        var tracks = files
            .OrderBy(t => DiscNumber(t)).ThenBy(t => TrackNumber(t)).ThenBy(t => t.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fields = FieldMap.All.Select(f =>
        {
            var values = tracks.Select(t => t.Get(f)).ToList();
            var groups = values.Where(v => v is not null).GroupBy(v => v!)
                .Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).ToList();
            return new FieldSummary(f, groups, values.Count(v => v is null));
        }).ToList();

        var cover = Image(folder, "cover.jpg") ?? Image(folder, "folder.jpg");
        var back = Image(folder, "back.jpg");
        return new Album
        {
            Folder = folder, Tracks = tracks, Fields = fields, Cover = cover, Back = back,
            Checks = Checks(tracks, fields, cover).ToList(),
        };
    }

    private static IEnumerable<Check> Checks(List<TrackFile> tracks, List<FieldSummary> fields, FolderImage? cover)
    {
        foreach (var t in tracks.Where(t => t.Problem is not null))
            yield return new Check(Tone.Missing, "unreadable", t.FileName);

        var readable = tracks.Where(t => t.Problem is null).ToList();
        var summary = fields.ToDictionary(f => f.Field);

        // Album artist missing on some tracks but not all is a gap; on none of
        // them is a gap for every track.
        foreach (var t in readable.Where(t => t.Get(FieldMap.AlbumArtist) is null))
            yield return new Check(Tone.Missing, "no album artist", t.FileName);

        foreach (var f in AlbumWide.Where(f => f != FieldMap.AlbumArtist))
        {
            var s = summary[f];
            if (s.Values.Count <= 1) continue;
            foreach (var t in readable.Where(t => t.Get(f) is { } v && v != s.Common))
                yield return new Check(Tone.Fixable, $"{f.Name.ToLowerInvariant()} differs", t.FileName);
        }

        foreach (var t in readable.Where(t => t.HasId3v1))
            yield return new Check(Tone.Fixable, "ID3v1 leftover", t.FileName);
        foreach (var t in readable.Where(t => t.HasApe))
            yield return new Check(Tone.Fixable, "APE leftover", t.FileName);

        var embedded = readable.Select(t => t.EmbeddedCover()).OfType<EmbeddedPicture>().ToList();
        if (cover is null && embedded.Count == 0)
        {
            yield return new Check(Tone.Missing, "no art");
            yield break;
        }
        if (cover is not null)
        {
            if (cover.Width is < SmallArt) yield return new Check(Tone.Fixable, $"art {cover.Width} px");
            if (cover.Width is not null && !cover.Square) yield return new Check(Tone.Fixable, "art not square");
            if (embedded.Any(p => p.Width != cover.Width || p.Height != cover.Height))
                yield return new Check(Tone.Fixable, "embedded art differs from cover.jpg");
        }
        else if (embedded.Count > 0 && embedded.Max(p => p.Width ?? 0) is > 0 and < SmallArt and var w)
            yield return new Check(Tone.Fixable, $"art {w} px");

        if (readable.Any(t => t.Junk().Any()))
            yield return new Check(Tone.Note, "junk frames");
    }

    private static IEnumerable<RawFrame> Junk(this TrackFile t) => t.Raw.Where(r => r.Junk);

    private static FolderImage? Image(string folder, string name)
    {
        var path = System.IO.Path.Combine(folder, name);
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            // The frame header can sit behind an EXIF thumbnail: hand over plenty.
            var head = new byte[Math.Min(stream.Length, 256 * 1024)];
            stream.ReadExactly(head);
            var size = ImageSize.Read(head);
            return new FolderImage(name, size?.Width, size?.Height, stream.Length);
        }
        catch (IOException)
        {
            return new FolderImage(name, null, null, 0);
        }
    }

    private static int TrackNumber(TrackFile t) => LeadingNumber(t.Get(FieldMap.Track));
    private static int DiscNumber(TrackFile t) => LeadingNumber(t.Get(FieldMap.Disc));

    private static int LeadingNumber(string? s)
    {
        if (s is null) return int.MaxValue;
        var n = 0;
        foreach (var c in s) { if (c is < '0' or > '9') break; n = n * 10 + (c - '0'); }
        return n;
    }
}

public static class Library
{
    /// True for a folder on SMB, NFS, AFP, WebDAV or SSHFS: read-only to
    /// Marginal (spec §9.6), since a rename there is not always atomic.
    public static bool IsOnNetworkShare(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows() && full.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            var drive = DriveInfo.GetDrives()
                .Where(d => full.StartsWith(d.RootDirectory.FullName, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .MaxBy(d => d.RootDirectory.FullName.Length);
            if (drive is null) return false;
            if (drive.DriveType == DriveType.Network) return true;
            var format = drive.DriveFormat.ToLowerInvariant();
            return format is "smbfs" or "nfs" or "nfs4" or "afpfs" or "webdav" or "cifs" or "smb3" or "fuse.sshfs" or "sshfs";
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// Every folder under `root` (and `root` itself) that holds music files,
    /// in path order. Folders only: albums are read one at a time after.
    public static IEnumerable<string> AlbumFolders(string root)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        var folders = new[] { root }.Concat(Directory.EnumerateDirectories(root, "*", options));
        return folders
            .Where(d => Directory.EnumerateFiles(d).Any(TrackReader.IsMusic))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
    }
}
