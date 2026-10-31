// Marginal — what an album folder may hold that is not what it seems.
//
// A downloaded album folder can carry symbolic links (zip keeps them) and
// named pipes (tar keeps them). In 0.1.0's release candidate a cover.jpg that
// was a link to /dev/zero filled memory in a second, a .lrc that was a pipe
// froze the window, two folder links pointing back at their parent made the
// scan run forever, and a link to a private file could be copied into a backup
// and from there into the album as a plain file (security review 2026-10-31,
// B1, B2, B5, A6). The rules here:
//
//   * The scan never follows a folder link.
//   * A side file (cover, back, .lrc) is read only when it is a plain file, or
//     a link to one in the same album folder, and only up to a limit.
//   * A file written to is a plain file; a link is refused (Write would
//     replace the link with a file, and back up what it points at).
//
// Real paths are worked out component by component (RealPath), since .NET
// has no realpath and Path.GetFullPath does not look at links.

namespace Marginal.Core;

public static class SafeFiles
{
    /// The largest .lrc read: a long song's synced lyrics are a few KB.
    public const long MaxLrcBytes = 1024 * 1024;

    /// How deep below the chosen folder the scan goes: Artist/Album/CD1 is 3.
    public const int MaxScanDepth = 16;

    /// The path with every link along it followed, or null when a link is
    /// broken or loops.
    public static string? RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        for (var hops = 0; hops < 40; hops++)
        {
            var root = Path.GetPathRoot(full)!;
            var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Where(p => p.Length > 0).ToArray();
            var current = root;
            var relinked = false;
            for (var i = 0; i < parts.Length; i++)
            {
                var next = Path.Combine(current, parts[i]);
                string? target;
                try { target = Info(next).LinkTarget; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { target = null; }
                if (target is null) { current = next; continue; }
                var resolved = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(current, target));
                full = Path.Combine([resolved, .. parts[(i + 1)..]]);
                relinked = true;
                break;
            }
            if (!relinked) return current;
        }
        return null;
    }

    /// True when `path` is inside `folder` (or is it), both as real paths.
    public static bool IsWithin(string path, string folder)
    {
        var real = RealPath(path);
        var realFolder = RealPath(folder);
        if (real is null || realFolder is null) return false;
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(real, realFolder, comparison)
               || real.StartsWith(Path.TrimEndingDirectorySeparator(realFolder) + Path.DirectorySeparatorChar, comparison);
    }

    public static bool IsLink(string path)
    {
        try { return Info(path).LinkTarget is not null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static FileSystemInfo Info(string path) => Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);

    /// A file Marginal may read whole: there, a plain file or a link to one
    /// in its own folder, and not empty (a pipe or a device reads as empty
    /// here; opening a pipe would wait for a writer that never comes).
    public static bool IsPlainFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            if (info.LinkTarget is not null)
            {
                if (!IsWithin(path, Path.GetDirectoryName(Path.GetFullPath(path))!)) return false;
                if (info.ResolveLinkTarget(returnFinalTarget: true) is not FileInfo { Exists: true } final) return false;
                info = final;
            }
            return info.Length > 0 && (info.Attributes & (FileAttributes.Directory | FileAttributes.Device)) == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// A file with something in it, wherever a link takes it: what a music
    /// file must be before TagLib# opens it (a pipe would hold the scan, a
    /// link to /dev/zero reads without end).
    public static bool HasContent(string path)
    {
        try
        {
            FileSystemInfo info = new FileInfo(path);
            if (info.LinkTarget is not null) info = info.ResolveLinkTarget(returnFinalTarget: true) ?? info;
            return info is FileInfo { Exists: true, Length: > 0 } f && (f.Attributes & FileAttributes.Device) == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// The bytes of a plain file no larger than `max`, or null.
    public static byte[]? Read(string path, long max)
    {
        if (!IsPlainFile(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[(int)Math.Min(max + 1, Math.Max(stream.Length + 1, 1))];
            var n = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return n > max ? null : buffer[..n];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// The text of a plain file no larger than `max`, or null.
    public static string? ReadText(string path, long max) =>
        Read(path, max) is { } bytes ? new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true).ReadToEnd() : null;

    /// Why Marginal may not write `path`, or null when it may: absent, or a
    /// plain file. A link is refused, wherever it points: writing replaces
    /// the link with a file of its own, leaving its target as it was, and a
    /// backup of it would copy whatever it points at (review B5). A pipe or a
    /// device (which reads as empty) is refused too.
    public static string? WriteProblem(string path)
    {
        if (IsLink(path)) return "is a link to another file, and Marginal changes only plain files";
        if (File.Exists(path) && !IsPlainFile(path)) return "is not a plain file with something in it";
        return null;
    }

    /// The folders under `root` (and `root`), never through a folder link,
    /// no deeper than MaxScanDepth, skipping any that cannot be read.
    public static IEnumerable<string> Folders(string root, CancellationToken ct = default)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        var work = new Stack<(string Path, int Depth)>();
        work.Push((root, 0));
        while (work.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = work.Pop();
            yield return dir;
            if (depth >= MaxScanDepth) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir, "*", options); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var sub in subs.OrderByDescending(s => s, StringComparer.OrdinalIgnoreCase))
                if (!IsLink(sub)) work.Push((sub, depth + 1));
        }
    }
}
