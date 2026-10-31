// Marginal — the one place that WRITES to a music file (spec §2, rules 4-7).
//
// AlbumWall's TagWriter pattern, with a backup of everything but the audio:
//
//   1. Copy the file to a working copy beside it (same folder, same volume).
//   2. Make the changes to the copy. The original is never opened for writing.
//   3. Read the copy back. It must hold what was asked, every other frame as
//      before, the same pictures, and the same audio bytes (AudioRange: the
//      part of the file no tag covers). Anything else and the copy is deleted
//      and the file left as it was.
//   4. Back up what the write may change: the original's bytes BEFORE its
//      audio and AFTER it (tags, pictures, padding, an MP4's boxes), with a
//      hash of the whole original and of its audio. Since the audio is proven
//      the same, that is all it takes to rebuild the original exactly.
//   5. Swap by one rename in the folder: the copy replaces the original
//      (rename(2) on macOS and Linux, ReplaceFile on Windows). A power cut
//      leaves the old file or the new one, never half of one. (It was two
//      renames by way of "<name>.marginal-old" until the 2026-10-31 review,
//      B3 and B4: a planted .marginal-old blocked the write, and a failure
//      between the two left the original hidden.)
//
// Only plain files are written: a link, a pipe or a device is refused
// (SafeFiles.WriteProblem). The manifest Undo reads is written after every
// file, so a crash part-way still leaves Undo for what was done, and it is
// checked when read: every path one Write could have produced, every backup
// file inside its batch folder (review B6, I2).
//
// Undo rebuilds: old front + the file's current audio + old back, into a
// working copy that must hash exactly like the original did, then the same
// swap. It refuses a file whose audio has changed since (a re-rip): there is
// nothing to go back to. Backups used to be the whole original moved aside:
// two test writes of a FLAC album came to 364 MB; this way an album is a few
// hundred KB at most, art included (Dave's idea, 2026-10-31), and they can be
// kept far longer.
//
// THE MODIFIED TIME MOVES, on purpose: backups, sync jobs, Navidrome and
// AlbumWall's index all learn of the change that way.
//
// Never on a network share (spec §9.6): a rename there is not always atomic.

using System.Security.Cryptography;

namespace Marginal.Core;

/// What one file's backup holds: where the original's front and back bytes
/// are kept, and the hashes Undo checks against.
public sealed record BackupEntry(string Original, string Head, string Tail, string Sha256, string AudioSha256);

/// An image file a write saved (cover.jpg, back.jpg): the old one's copy, if
/// there was one, and the hash of what was written, so Undo removes a new
/// image only while it is still exactly what Marginal wrote.
public sealed record ImageEntry(string Path, string? Old, string Sha256);

/// A write: when, and each file's backup. One write can span many albums (a
/// library-wide clean-up), and one Undo takes it all back.
public sealed record WriteBatch(string BackupFolder, DateTime Time, IReadOnlyList<BackupEntry> Files, IReadOnlyList<ImageEntry> Images)
{
    public IReadOnlyList<string> Folders => Files.Select(f => f.Original).Concat(Images.Select(i => i.Path))
        .Select(AlbumFolderOf).Distinct().ToList();

    public int Count => Files.Count + Images.Count;

    /// The album a file belongs to: its folder, or the one above booklet/.
    public static string AlbumFolderOf(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        return Path.GetFileName(dir).Equals("booklet", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(dir)! : dir;
    }
}

public sealed record WriteResult(WriteBatch? Batch, IReadOnlyList<(string File, string Problem)> Problems)
{
    public int Written => Batch?.Count ?? 0;
}

public static class TagWriter
{
    /// Working copies start with this, so the library scan never lists one.
    public const string TempPrefix = ".marginal-";

    private const string Manifest = "manifest.tsv";

    /// Where backups live (spec §9.1): the app's own data folder, never the
    /// library. On Linux that is the XDG state folder (~/.local/state), NOT
    /// ~/.local/share/marginal: the program is installed there, and every
    /// uninstall deleted the backups with it (security review 2026-10-31, C1).
    public static string DefaultBackupRoot => BackupRootFor(OperatingSystem.IsLinux());

    public static string BackupRootFor(bool linux)
    {
        if (!linux)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify), "marginal", "backups");
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrEmpty(state) || !Path.IsPathRooted(state))
            state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return Path.Combine(state, "marginal", "backups");
    }

    /// Where Linux backups were kept before 0.1.0's review: inside the
    /// install folder. Moved once, at start (Backups.MoveFromInstallFolder).
    public static string OldLinuxBackupRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify), "marginal", "backups");

    public static WriteResult Write(IReadOnlyList<Change> changes, string? backupRoot = null)
    {
        backupRoot ??= DefaultBackupRoot;
        var problems = new List<(string, string)>();
        var done = new List<BackupEntry>();
        var images = new List<ImageEntry>();
        if (changes.Count == 0) return new WriteResult(null, problems);

        // Never on a network share (spec §9.6): those files are refused, the rest written.
        var onShare = changes.Select(c => Path.GetDirectoryName(c.Path)!).Distinct().Where(Library.IsOnNetworkShare).ToHashSet();
        foreach (var f in onShare) problems.Add((f, "on a network share: Marginal writes only to local libraries"));
        changes = changes.Where(c => !onShare.Contains(Path.GetDirectoryName(c.Path)!)).ToList();
        if (changes.Count == 0) return new WriteResult(null, problems);

        var time = DateTime.Now;
        var batchFolder = Path.Combine(backupRoot, time.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(batchFolder);

        var n = 0;
        foreach (var c in changes.Where(c => c.Op is ChangeOp.WriteImage or ChangeOp.WriteText))
        {
            n++;
            try
            {
                images.Add(WriteImage(c, batchFolder, $"{n:D3} {Path.GetFileName(c.Path)}"));
                SaveManifest(batchFolder, done, images);
            }
            catch (Exception ex) { problems.Add((Path.GetFileName(c.Path), ex.Message)); }
        }

        foreach (var group in changes.Where(c => c.Op is not (ChangeOp.WriteImage or ChangeOp.WriteText)).GroupBy(c => c.Path))
        {
            n++;
            var path = group.Key;
            var temp = TempBeside(path);
            try
            {
                if (SafeFiles.WriteProblem(path) is { } refused) throw new IOException(refused);
                var before = TrackReader.Read(path);
                if (before.Problem is not null) throw new IOException(before.Problem);

                System.IO.File.Copy(path, temp);
                using (var file = TrackReader.Open(temp))
                {
                    foreach (var c in group) Apply(file, before, c);
                    file.Save();
                }

                if (Verify(path, temp, before, group.ToList()) is { } problem)
                    throw new IOException(problem);

                // The backup is on disk before the original is touched.
                var entry = SaveBackup(path, batchFolder, $"{n:D3} {Path.GetFileName(path)}");
                Swap(path, temp);
                done.Add(entry);
                SaveManifest(batchFolder, done, images);
            }
            catch (Exception ex)
            {
                try { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); } catch (IOException) { }
                problems.Add((Path.GetFileName(path), ex.Message));
            }
        }

        if (done.Count == 0 && images.Count == 0)
        {
            try { Directory.Delete(batchFolder, recursive: true); } catch (IOException) { }
            return new WriteResult(null, problems);
        }
        return new WriteResult(new WriteBatch(batchFolder, time, done, images), problems);
    }

    /// The manifest for what is done so far, replaced whole by a rename.
    private static void SaveManifest(string batchFolder, IEnumerable<BackupEntry> files, IEnumerable<ImageEntry> images)
    {
        var temp = Path.Combine(batchFolder, Manifest + ".new");
        System.IO.File.WriteAllLines(temp,
            files.Select(d => string.Join('\t', d.Original, Path.GetFileName(d.Head), Path.GetFileName(d.Tail), d.Sha256, d.AudioSha256))
                .Concat(images.Select(i => string.Join('\t', "IMG", i.Path, i.Old is null ? "" : Path.GetFileName(i.Old), i.Sha256))));
        System.IO.File.Move(temp, Path.Combine(batchFolder, Manifest), overwrite: true);
    }

    /// The newest write not yet undone, for the Undo button. Older ones come
    /// after it, one Undo at a time.
    public static WriteBatch? Latest(string? backupRoot = null)
    {
        backupRoot ??= DefaultBackupRoot;
        if (!Directory.Exists(backupRoot)) return null;
        IEnumerable<string> dirs;
        try { dirs = Directory.GetDirectories(backupRoot).OrderByDescending(d => d, StringComparer.Ordinal).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        foreach (var dir in dirs)
        {
            try
            {
                if (ReadBatch(dir) is { } batch) return batch;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // unreadable: passed over (review I1)
        }
        return null;
    }

    /// A batch folder's manifest, checked line by line (review B6): a
    /// manifest is data from disk, and one that names anything a Write could
    /// not have produced is not used at all. Null when there is none to undo.
    private static WriteBatch? ReadBatch(string dir)
    {
        var manifest = Path.Combine(dir, Manifest);
        if (!System.IO.File.Exists(manifest) || SafeFiles.IsLink(dir) || SafeFiles.IsLink(manifest)) return null;
        var files = new List<BackupEntry>();
        var images = new List<ImageEntry>();
        foreach (var line in System.IO.File.ReadAllLines(manifest))
        {
            var p = line.Split('\t');
            if (p.Length == 5 && p[0] != "IMG"
                && IsTarget(p[0]) && TrackReader.IsMusic(p[0])
                && InBatch(dir, p[1]) && InBatch(dir, p[2]) && IsHash(p[3]) && IsHash(p[4]))
                files.Add(new BackupEntry(p[0], Path.Combine(dir, p[1]), Path.Combine(dir, p[2]), p[3], p[4]));
            else if (p.Length == 4 && p[0] == "IMG"
                     && IsTarget(p[1]) && IsSideFile(p[1])
                     && (p[2].Length == 0 || InBatch(dir, p[2])) && IsHash(p[3]))
                images.Add(new ImageEntry(p[1], p[2].Length == 0 ? null : Path.Combine(dir, p[2]), p[3]));
            else if (line.Length > 0)
                return null;
        }
        if (files.Count == 0 && images.Count == 0) return null;
        var time = DateTime.TryParseExact(Path.GetFileName(dir), "yyyyMMdd-HHmmss-fff", null,
            System.Globalization.DateTimeStyles.None, out var t) ? t : Directory.GetCreationTime(dir);
        return new WriteBatch(dir, time, files, images);
    }

    /// A full path with nothing to resolve (no "..", no relative part).
    private static bool IsTarget(string path) =>
        Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path
        && !Path.GetFileName(path).StartsWith(TempPrefix, StringComparison.Ordinal);

    /// What Write saves beside the tracks: cover.jpg, back.jpg, a .lrc, a booklet page.
    private static bool IsSideFile(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        return name is "cover.jpg" or "back.jpg" or "folder.jpg"
               || name.EndsWith(".lrc", StringComparison.Ordinal)
               || (name.EndsWith(".jpg", StringComparison.Ordinal)
                   && Path.GetFileName(Path.GetDirectoryName(path))?.Equals("booklet", StringComparison.OrdinalIgnoreCase) == true);
    }

    /// A backup file's name: a plain name, of a plain file in the batch folder.
    private static bool InBatch(string dir, string name) =>
        name.Length > 0 && Path.GetFileName(name) == name && name is not ("." or "..")
        && System.IO.File.Exists(Path.Combine(dir, name)) && !SafeFiles.IsLink(Path.Combine(dir, name));

    private static bool IsHash(string s) => s.Length == 64 && s.All(char.IsAsciiHexDigit);

    /// Rebuilds every original of `batch` in place: its old front and back
    /// around the file's audio as it is now, checked against the original's
    /// hash before it replaces anything.
    public static IReadOnlyList<(string File, string Problem)> Undo(WriteBatch batch)
    {
        var problems = new List<(string, string)>();
        var failedFiles = new List<BackupEntry>();
        var failedImages = new List<ImageEntry>();
        foreach (var entry in batch.Files)
        {
            var path = entry.Original;
            var temp = TempBeside(path);
            try
            {
                if (!System.IO.File.Exists(path)) throw new IOException("the file is gone");
                if (SafeFiles.WriteProblem(path) is { } refused) throw new IOException(refused);
                if (!System.IO.File.Exists(entry.Head) || !System.IO.File.Exists(entry.Tail)) throw new IOException("the backup is gone");
                if (AudioHash(path) is not { } audio || Convert.ToHexString(audio) != entry.AudioSha256)
                    throw new IOException("its audio has changed since the write, so there is nothing to go back to");

                var (start, end) = AudioRange(path);
                using (var output = System.IO.File.Create(temp))
                {
                    using (var head = System.IO.File.OpenRead(entry.Head)) head.CopyTo(output);
                    using (var input = System.IO.File.OpenRead(path))
                    {
                        input.Position = start;
                        CopyBytes(input, output, end - start);
                    }
                    using (var tail = System.IO.File.OpenRead(entry.Tail)) tail.CopyTo(output);
                }
                if (FileHash(temp) != entry.Sha256) throw new IOException("the rebuilt file does not match the original");
                Swap(path, temp);
            }
            catch (Exception ex)
            {
                try { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); } catch (IOException) { }
                problems.Add((Path.GetFileName(path), ex.Message));
                failedFiles.Add(entry);
            }
        }
        foreach (var image in batch.Images)
        {
            try
            {
                if (SafeFiles.WriteProblem(image.Path) is { } refused) throw new IOException(refused);
                if (image.Old is not null)
                {
                    if (!System.IO.File.Exists(image.Old)) throw new IOException("the backup is gone");
                    var temp = TempBeside(image.Path);
                    try
                    {
                        System.IO.File.Copy(image.Old, temp);
                        Place(temp, image.Path);
                    }
                    catch
                    {
                        try { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); } catch (IOException) { }
                        throw;
                    }
                }
                else if (System.IO.File.Exists(image.Path))
                {
                    // It was new: removed only while it is what Marginal wrote.
                    if (FileHash(image.Path) != image.Sha256) throw new IOException("it has changed since the write, so it is left as it is");
                    System.IO.File.Delete(image.Path);
                }
            }
            catch (Exception ex)
            {
                problems.Add((Path.GetFileName(image.Path), ex.Message));
                failedImages.Add(image);
            }
        }
        // Undone only when all of it was: what failed stays in the manifest,
        // so the next Undo tries it again (it was marked undone either way,
        // and an album briefly away lost its Undo; review B7).
        try
        {
            if (failedFiles.Count == 0 && failedImages.Count == 0)
                System.IO.File.Move(Path.Combine(batch.BackupFolder, Manifest), Path.Combine(batch.BackupFolder, "manifest.undone.tsv"), overwrite: true);
            else if (failedFiles.Count < batch.Files.Count || failedImages.Count < batch.Images.Count)
                SaveManifest(batch.BackupFolder, failedFiles, failedImages);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(("backup", $"could not be marked: {ex.Message}"));
        }
        return problems;
    }

    // ---- images beside the tracks ----------------------------------------------

    /// Saves an image file: the old one copied into the backup first (an
    /// image is small), the new one written beside it and swapped in.
    private static ImageEntry WriteImage(Change c, string batchFolder, string name)
    {
        if (c.Data is not { Length: > 0 } data) throw new IOException("nothing to write");
        if (c.Op == ChangeOp.WriteImage && !ArtImage.LooksLikeAnImage(data)) throw new IOException("not an image");
        // A link is refused (its target would be copied into the backup and
        // could come back as a plain file on Undo), as is a pipe (review B5).
        if (SafeFiles.WriteProblem(c.Path) is { } refused) throw new IOException(refused);
        Directory.CreateDirectory(Path.GetDirectoryName(c.Path)!);
        string? old = null;
        if (System.IO.File.Exists(c.Path))
        {
            old = Path.Combine(batchFolder, name + ".old");
            System.IO.File.Copy(c.Path, old);
        }
        var temp = TempBeside(c.Path);
        try
        {
            using (var f = System.IO.File.Create(temp)) { f.Write(data); f.Flush(flushToDisk: true); }
            Place(temp, c.Path);
        }
        catch
        {
            try { if (System.IO.File.Exists(temp)) System.IO.File.Delete(temp); } catch (IOException) { }
            throw;
        }
        return new ImageEntry(c.Path, old, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)));
    }

    // ---- backup and swap ------------------------------------------------------

    private static string TempBeside(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, TempPrefix + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(path));

    /// Saves the bytes before and after the audio, and the hashes.
    private static BackupEntry SaveBackup(string path, string folder, string name)
    {
        var (start, end) = AudioRange(path);
        if (start < 0 || end <= start) throw new IOException("the audio could not be found for the backup");
        var head = Path.Combine(folder, name + ".head");
        var tail = Path.Combine(folder, name + ".tail");
        using (var input = System.IO.File.OpenRead(path))
        {
            using (var h = System.IO.File.Create(head)) { CopyBytes(input, h, start); h.Flush(flushToDisk: true); }
            input.Position = end;
            using (var t = System.IO.File.Create(tail)) { input.CopyTo(t); t.Flush(flushToDisk: true); }
        }
        var audio = AudioHash(path) ?? throw new IOException("the audio could not be hashed for the backup");
        return new BackupEntry(path, head, tail, FileHash(path), Convert.ToHexString(audio));
    }

    /// `temp` takes `path`'s place in one step: rename(2), which replaces
    /// the old file atomically, on macOS and Linux; ReplaceFile on Windows.
    /// The backup is already on disk.
    private static void Swap(string path, string temp)
    {
        if (OperatingSystem.IsWindows()) System.IO.File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else System.IO.File.Move(temp, path, overwrite: true);
    }

    /// `temp` put at `path`: in place of the file there, or new.
    private static void Place(string temp, string path)
    {
        if (System.IO.File.Exists(path)) Swap(path, temp);
        else System.IO.File.Move(temp, path);
    }

    private static void CopyBytes(Stream from, Stream to, long count)
    {
        var buffer = new byte[1 << 16];
        while (count > 0)
        {
            var read = from.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read <= 0) throw new IOException("the file ended early");
            to.Write(buffer, 0, read);
            count -= read;
        }
    }

    public static string FileHash(string path)
    {
        using var stream = System.IO.File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // ---- making a change ----------------------------------------------------

    private static void Apply(TagLib.File file, TrackFile before, Change c)
    {
        switch (c.Op)
        {
            case ChangeOp.RemoveTag:
                file.RemoveTags(c.Key == "APE" ? TagLib.TagTypes.Ape : TagLib.TagTypes.Id3v1);
                return;
            case ChangeOp.RemoveFrame:
                RemoveFrame(file, before.Format, c);
                return;
            case ChangeOp.SetField:
                SetField(file, before.Format, c.Field!, c.To);
                return;
            case ChangeOp.EmbedFront:
                EmbedFront(file, c.Data!);
                return;
        }
    }

    /// The new front in place of the file's cover (the picture typed front,
    /// else the first that is an image: TrackFile.EmbeddedCover's rule); any
    /// other pictures (a back, a booklet page) stay as they were.
    private static void EmbedFront(TagLib.File file, byte[] image)
    {
        var pictures = file.Tag.Pictures.ToList();
        var cover = pictures.FindIndex(p => p.Type == TagLib.PictureType.FrontCover && ImageSize.Read(ImageSize.Repair(p.Data.Data)) is not null);
        if (cover < 0) cover = pictures.FindIndex(p => ImageSize.Read(ImageSize.Repair(p.Data.Data)) is not null);
        if (cover >= 0) pictures.RemoveAt(cover);
        pictures.Insert(0, new TagLib.Picture(new TagLib.ByteVector(image))
        {
            Type = TagLib.PictureType.FrontCover,
            MimeType = ArtImage.IsJpeg(image) ? "image/jpeg" : "image/png",
            Description = "",
        });
        file.Tag.Pictures = pictures.ToArray();
    }

    private static void SetField(TagLib.File file, AudioFormat format, Field field, string? value)
    {
        switch (format)
        {
            case AudioFormat.Flac:
            {
                var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, true);
                if (field == FieldMap.Disc || field == FieldMap.Track)
                {
                    var disc = field == FieldMap.Disc;
                    var (n, total) = SplitPair(value);
                    xiph.SetField(disc ? "DISCNUMBER" : "TRACKNUMBER", n is null ? [] : [n]);
                    xiph.SetField(disc ? "DISCTOTAL" : "TRACKTOTAL", total is null ? [] : [total]);
                    xiph.RemoveField(disc ? "TOTALDISCS" : "TOTALTRACKS");
                    return;
                }
                xiph.SetField(field.Flac, value is null ? [] : [value]);
                return;
            }
            case AudioFormat.Mp3:
            {
                var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, true);
                if (field == FieldMap.Lyrics)
                {
                    // USLT is a frame of its own, not a text frame: all of them
                    // go, and one is written (TagLib#'s own Lyrics property).
                    id3.RemoveFrames(Id("USLT"));
                    if (value is not null) id3.Lyrics = value;
                    return;
                }
                var key = field.Id3v2;
                if (key.StartsWith("TXXX:", StringComparison.Ordinal))
                {
                    var desc = key[5..];
                    if (TagLib.Id3v2.UserTextInformationFrame.Get(id3, desc, false) is { } old) id3.RemoveFrame(old);
                    if (value is not null) TagLib.Id3v2.UserTextInformationFrame.Get(id3, desc, true).Text = [value];
                    return;
                }
                // Dates: write the v2.4 frame and drop the v2.3 ones; TagLib#
                // renders it back as TYER (and TDAT) when it saves a v2.3 tag.
                if (field == FieldMap.Date) foreach (var id in new[] { "TYER", "TDAT", "TIME" }) id3.RemoveFrames(Id(id));
                if (field == FieldMap.OriginalDate) id3.RemoveFrames(Id("TORY"));
                id3.RemoveFrames(Id(key));
                if (value is not null) TagLib.Id3v2.TextInformationFrame.Get(id3, Id(key), true).Text = [value];
                return;
            }
            case AudioFormat.M4a:
            {
                var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagLib.TagTypes.Apple, true);
                if (field == FieldMap.Disc)
                {
                    var (n, total) = SplitPair(value);
                    apple.Disc = uint.TryParse(n, out var d) ? d : 0;
                    apple.DiscCount = uint.TryParse(total, out var dc) ? dc : 0;
                    return;
                }
                if (field == FieldMap.Track)
                {
                    var (n, total) = SplitPair(value);
                    apple.Track = uint.TryParse(n, out var t) ? t : 0;
                    apple.TrackCount = uint.TryParse(total, out var tc) ? tc : 0;
                    return;
                }
                if (field == FieldMap.Compilation)
                {
                    // cpil is an integer atom, not text.
                    if (value is null) apple.ClearData(TagLib.ByteVector.FromString("cpil", TagLib.StringType.Latin1));
                    else apple.IsCompilation = value == "1";
                    return;
                }
                const string itunes = "----:com.apple.iTunes:";
                if (field.Mp4.StartsWith(itunes, StringComparison.Ordinal))
                {
                    apple.SetDashBox("com.apple.iTunes", field.Mp4[itunes.Length..], value ?? "");
                    return;
                }
                var type = TagLib.ByteVector.FromString(field.Mp4, TagLib.StringType.Latin1);
                if (value is null) apple.ClearData(type);
                else apple.SetText(type, value);
                return;
            }
        }
    }

    private static void RemoveFrame(TagLib.File file, AudioFormat format, Change c)
    {
        if (format == AudioFormat.Mp3 && file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
        {
            // The frame the Raw view showed under this key with this value.
            var frame = id3.GetFrames().FirstOrDefault(f => TrackReader.Id3Raw(f) is var r && r.Key == c.Key && r.Value == c.From);
            if (frame is not null) id3.RemoveFrame(frame);
        }
        else if (format == AudioFormat.Flac && file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
        {
            var rest = xiph.GetField(c.Key).Where(v => v != c.From).ToArray();
            xiph.SetField(c.Key, rest);
        }
        else if (format == AudioFormat.M4a && file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
        {
            // "----:<mean>:<name>": a freeform atom, dropped whole; else a plain one.
            var parts = c.Key.Split(':', 3);
            if (parts is ["----", var mean, var name]) apple.SetDashBox(mean, name, "");
            else apple.ClearData(TagLib.ByteVector.FromString(c.Key, TagLib.StringType.Latin1));
        }
    }

    private static TagLib.ByteVector Id(string id) => TagLib.ByteVector.FromString(id, TagLib.StringType.Latin1);

    private static (string? N, string? Total) SplitPair(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, null);
        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        return (parts[0].Length > 0 ? parts[0] : null, parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
    }

    // ---- checking the copy --------------------------------------------------

    /// Null when the copy holds exactly what was asked and nothing else
    /// changed; otherwise what is wrong, in words.
    private static string? Verify(string original, string copy, TrackFile before, List<Change> changes)
    {
        var after = TrackReader.Read(copy);
        if (after.Problem is not null) return $"the written copy does not read: {after.Problem}";

        foreach (var c in changes.Where(c => c.Op == ChangeOp.SetField))
        {
            // Lyrics are compared as kept: line endings may come back as the format stores them.
            var same = c.Field == FieldMap.Lyrics ? Lrc.Tidy(after.Get(c.Field)) == Lrc.Tidy(c.To) : after.Get(c.Field!) == c.To;
            if (!same) return $"{c.Field!.Name} reads back as \"{after.Get(c.Field!)}\", not \"{c.To}\"";
        }
        if (changes.Any(c => c is { Op: ChangeOp.RemoveTag, Key: "ID3v1" }) && after.HasId3v1) return "the ID3v1 tag is still there";
        if (changes.Any(c => c is { Op: ChangeOp.RemoveTag, Key: "APE" }) && after.HasApe) return "the APE tag is still there";
        foreach (var c in changes.Where(c => c.Op == ChangeOp.RemoveFrame))
            if (after.Raw.Any(r => r.Key == c.Key && (before.Format == AudioFormat.M4a || r.Value == c.From)))
                return $"{c.Key} is still there";

        // Every frame not touched must be as it was.
        var touched = changes.SelectMany(Touched).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = changes.Where(c => c.Op == ChangeOp.RemoveFrame).Select(c => (c.Key, c.From)).ToList();
        string Line(RawFrame r) => $"{r.Key}\u0001{r.Value}";
        var was = before.Raw.Where(r => !touched.Contains(r.Key)).Where(r => !removed.Remove((r.Key, r.Value))).Select(Line).Order().ToList();
        var now = after.Raw.Where(r => !touched.Contains(r.Key)).Select(Line).Order().ToList();
        if (!was.SequenceEqual(now))
        {
            var lost = was.Except(now).FirstOrDefault()?.Replace('\u0001', '=');
            var gained = now.Except(was).FirstOrDefault()?.Replace('\u0001', '=');
            return lost is not null ? $"another frame changed: {lost}" : $"a frame appeared: {gained}";
        }

        var embed = changes.FirstOrDefault(c => c.Op == ChangeOp.EmbedFront);
        if (embed is not null)
        {
            // The new front is there, whole; every other picture is as it was.
            if (after.EmbeddedCover() is not { } front || front.Bytes != embed.Data!.Length) return "the new front cover did not go in";
            var keptBefore = before.Pictures.Where(p => p != before.EmbeddedCover()).Select(p => p.Bytes).Order().ToList();
            var keptAfter = after.Pictures.Where(p => p != front).Select(p => p.Bytes).Order().ToList();
            if (!keptBefore.SequenceEqual(keptAfter)) return "another embedded picture changed";
        }
        else if (before.Pictures.Count != after.Pictures.Count
            || before.Pictures.Zip(after.Pictures).Any(p => p.First.Bytes != p.Second.Bytes))
            return "the embedded pictures changed";

        var a = AudioHash(original);
        var b = AudioHash(copy);
        if (a is null || b is null) return "the audio could not be compared";
        if (!a.AsSpan().SequenceEqual(b)) return "the audio changed";
        return null;
    }

    /// Keys a change may alter besides its own: a date's v2.3 frames, a
    /// disc's total.
    private static IEnumerable<string> Touched(Change c)
    {
        yield return c.Key;
        if (c.Field == FieldMap.Date) { yield return "TYER"; yield return "TDAT"; yield return "TIME"; yield return "TDRC"; }
        if (c.Field == FieldMap.OriginalDate) { yield return "TORY"; yield return "TDOR"; }
        if (c.Field == FieldMap.Disc) { yield return "DISCNUMBER"; yield return "DISCTOTAL"; yield return "TOTALDISCS"; yield return "disk"; }
        if (c.Field == FieldMap.Track) { yield return "TRACKNUMBER"; yield return "TRACKTOTAL"; yield return "TOTALTRACKS"; yield return "trkn"; }
        if (c.Op == ChangeOp.EmbedFront) { yield return "APIC"; yield return "covr"; }
        if (c.Field == FieldMap.Lyrics) { yield return "USLT"; yield return "LYRICS"; yield return "UNSYNCEDLYRICS"; yield return "©lyr"; }
    }

    /// SHA-256 of the audio: the bytes no tag covers. FLAC: everything after
    /// the metadata blocks. MP4: the media data box ("mdat"). MP3: TagLib#'s
    /// invariant positions, which leave out ID3v2 at the front and ID3v1/APE
    /// at the end. (TagLib# gives a FLAC's as the whole file, metadata and
    /// all, so a tag edit looked like an audio change: caught by these tests,
    /// 2026-10-31, before any real file was written.)
    public static byte[]? AudioHash(string path)
    {
        try
        {
            var (start, end) = AudioRange(path);
            if (start < 0 || end <= start) return null;
            using var stream = System.IO.File.OpenRead(path);
            stream.Position = start;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 16];
            var left = end - start;
            while (left > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read <= 0) break;
                sha.AppendData(buffer, 0, read);
                left -= read;
            }
            return left == 0 ? sha.GetHashAndReset() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// Where a file's audio is: [start, end) in bytes, or (-1, -1).
    public static (long Start, long End) AudioRange(string path)
    {
        switch (TrackReader.FormatOf(path))
        {
            case AudioFormat.Flac:
            {
                using var s = System.IO.File.OpenRead(path);
                var head = new byte[4];
                s.ReadExactly(head);
                // An ID3v2 tag in front of "fLaC" is not allowed but is found; skip it.
                if (head[0] == 'I' && head[1] == 'D' && head[2] == '3')
                {
                    var id3 = new byte[6];
                    s.ReadExactly(id3);
                    var size = (id3[2] << 21) | (id3[3] << 14) | (id3[4] << 7) | id3[5];
                    s.Position = 10 + size;
                    s.ReadExactly(head);
                }
                if (head[0] != 'f' || head[1] != 'L' || head[2] != 'a' || head[3] != 'C') return (-1, -1);
                var block = new byte[4];
                while (true)
                {
                    s.ReadExactly(block);
                    var length = (block[1] << 16) | (block[2] << 8) | block[3];
                    s.Position += length;
                    if ((block[0] & 0x80) != 0) break;            // the last metadata block
                }
                var start = s.Position;
                var end = s.Length;
                // An ID3v1 tag at the end (not allowed in FLAC, sometimes there).
                if (end >= 128)
                {
                    var tail = new byte[3];
                    s.Position = end - 128;
                    s.ReadExactly(tail);
                    if (tail[0] == 'T' && tail[1] == 'A' && tail[2] == 'G') end -= 128;
                }
                return (start, end);
            }
            case AudioFormat.M4a:
            {
                using var s = System.IO.File.OpenRead(path);
                var header = new byte[16];
                while (s.Position + 8 <= s.Length)
                {
                    var at = s.Position;
                    s.ReadExactly(header, 0, 8);
                    long size = ((long)header[0] << 24) | ((long)header[1] << 16) | ((long)header[2] << 8) | header[3];
                    var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
                    var headerSize = 8L;
                    if (size == 1)
                    {
                        s.ReadExactly(header, 8, 8);
                        size = 0;
                        for (var i = 8; i < 16; i++) size = (size << 8) | header[i];
                        headerSize = 16;
                    }
                    else if (size == 0) size = s.Length - at;
                    if (size < headerSize) return (-1, -1);
                    if (type == "mdat") return (at + headerSize, at + size);
                    s.Position = at + size;
                }
                return (-1, -1);
            }
            default:
            {
                using var file = TrackReader.Open(path);
                return (file.InvariantStartPosition, file.InvariantEndPosition);
            }
        }
    }
}
