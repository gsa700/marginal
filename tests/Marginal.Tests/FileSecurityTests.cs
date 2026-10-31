// Security review 2026-10-31, part B (writes, Undo and paths) and A6/A7: a
// booby-trapped album folder or backup folder must not hang the scan, crash
// the app, write outside the album, or bring a private file into it. Every
// test works in its own temporary folder.

using System.Diagnostics;
using Marginal.Core;

namespace Marginal.Tests;

public sealed class FileSecurityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("marginal-fs-").FullName;
    private readonly string _album;
    private readonly string _backups;
    private readonly List<string> _locked = [];

    public FileSecurityTests()
    {
        _album = Path.Combine(_root, "lib", "Album");
        _backups = Path.Combine(_root, "backups");
        Directory.CreateDirectory(_album);
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fixture Band - Test Album");
        foreach (var f in Directory.GetFiles(fixture)) File.Copy(f, Path.Combine(_album, Path.GetFileName(f)));
    }

    public void Dispose()
    {
        // Only what a test locked is unlocked: walking the tree here would
        // follow the very link loops the tests plant.
        foreach (var d in _locked)
            try { File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch (Exception) { }
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    private string Track(string start) => Directory.GetFiles(_album).Single(f => Path.GetFileName(f).StartsWith(start));
    private static bool Unix => !OperatingSystem.IsWindows();

    // ---- B1 / A6: folder links ---------------------------------------------------

    [Fact]
    public void Scan_does_not_follow_folder_links_in_circles()
    {
        if (!Unix) return;
        Directory.CreateSymbolicLink(Path.Combine(_album, "a"), ".");
        Directory.CreateSymbolicLink(Path.Combine(_album, "b"), ".");
        Directory.CreateSymbolicLink(Path.Combine(_album, "up"), "..");
        var clock = Stopwatch.StartNew();
        var folders = Library.AlbumFolders(Path.Combine(_root, "lib")).ToList();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal([_album], folders);
    }

    [Fact]
    public void Scan_does_not_follow_a_link_out_of_the_library()
    {
        if (!Unix) return;
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.Copy(Track("01"), Path.Combine(outside, "x.flac"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "lib", "elsewhere"), outside);
        Assert.Equal([_album], Library.AlbumFolders(Path.Combine(_root, "lib")).ToList());
    }

    [Fact]
    public void Scan_can_be_cancelled()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => Library.AlbumFolders(_root, cancel.Token).ToList());
    }

    // ---- A7: unreadable folders and files -----------------------------------------

    [Fact]
    public void Unreadable_folder_is_passed_over()
    {
        if (!Unix) return;
        var locked = Path.Combine(_root, "lib", "Locked");
        Directory.CreateDirectory(locked);
        File.Copy(Track("01"), Path.Combine(locked, "x.flac"));
        File.SetUnixFileMode(locked, UnixFileMode.None);
        _locked.Add(locked);
        Assert.Equal([_album], Library.AlbumFolders(Path.Combine(_root, "lib")).ToList());
        Assert.Empty(AlbumReader.Read(locked).Tracks);
    }

    [Fact]
    public void Unreadable_cover_does_not_stop_the_album()
    {
        if (!Unix) return;
        File.SetUnixFileMode(Path.Combine(_album, "cover.jpg"), UnixFileMode.None);
        var album = AlbumReader.Read(_album);
        Assert.Equal(3, album.Tracks.Count);
        Assert.Null(album.Cover!.Width);
    }

    // ---- B2: side files that are not files ------------------------------------------

    [Fact]
    public void Cover_and_lrc_linked_to_a_device_are_not_read()
    {
        if (!Unix) return;
        File.Delete(Path.Combine(_album, "cover.jpg"));
        File.CreateSymbolicLink(Path.Combine(_album, "cover.jpg"), "/dev/zero");
        var lrc = Path.ChangeExtension(Track("01"), ".lrc");
        File.CreateSymbolicLink(lrc, "/dev/zero");
        var album = AlbumReader.Read(_album);
        Assert.Null(album.Cover!.Width);
        Assert.Null(SafeFiles.ReadText(lrc, SafeFiles.MaxLrcBytes));
        Assert.Null(SafeFiles.Read(Path.Combine(_album, "cover.jpg"), WebLimits.MaxImageBytes));
        var change = Pending.SaveImage(album, "cover.jpg", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Fixture Band - Test Album", "cover.jpg")));
        Assert.Equal("(not a file Marginal can read)", change.From);
    }

    [Fact]
    public void A_pipe_is_not_opened()
    {
        if (!Unix) return;
        var fifo = Path.Combine(_album, "cover.jpg");
        File.Delete(fifo);
        Assert.Equal(0, Process.Start("/usr/bin/mkfifo", fifo)!.WaitForExitAsync().ContinueWith(_ => 0).Result);
        var task = Task.Run(() => (SafeFiles.Read(fifo, 1000), AlbumReader.Read(_album)));
        Assert.True(task.Wait(TimeSpan.FromSeconds(5)), "reading a pipe waited");
        Assert.Null(task.Result.Item1);
    }

    [Fact]
    public void A_music_file_that_is_a_pipe_is_unreadable_not_waited_on()
    {
        if (!Unix) return;
        var fifo = Path.Combine(_album, "04 Pipe.flac");
        Process.Start("/usr/bin/mkfifo", fifo)!.WaitForExit();
        var task = Task.Run(() => TrackReader.Read(fifo));
        Assert.True(task.Wait(TimeSpan.FromSeconds(5)), "reading a pipe waited");
        Assert.NotNull(task.Result.Problem);
    }

    [Fact]
    public void Lrc_over_the_limit_is_not_read()
    {
        var lrc = Path.ChangeExtension(Track("01"), ".lrc");
        File.WriteAllBytes(lrc, new byte[SafeFiles.MaxLrcBytes + 1]);
        Assert.Null(SafeFiles.ReadText(lrc, SafeFiles.MaxLrcBytes));
        File.WriteAllText(lrc, "[00:01.00] hello");
        Assert.Equal("[00:01.00] hello", SafeFiles.ReadText(lrc, SafeFiles.MaxLrcBytes));
    }

    // ---- B5: links are not written ---------------------------------------------------

    [Fact]
    public void A_linked_track_is_refused_and_its_target_untouched()
    {
        if (!Unix) return;
        var target = Path.Combine(_root, "private.flac");
        File.Move(Track("01"), target);
        File.CreateSymbolicLink(Path.Combine(_album, "01 First.flac"), target);
        var before = TagWriter.FileHash(target);
        var changes = Pending.SetField(AlbumReader.Read(_album), FieldMap.Label, "Vertigo");
        var result = TagWriter.Write(changes, _backups);
        Assert.Contains(result.Problems, p => p.File == "01 First.flac" && p.Problem.Contains("link"));
        Assert.Equal(before, TagWriter.FileHash(target));
        Assert.NotNull(new FileInfo(Path.Combine(_album, "01 First.flac")).LinkTarget);
    }

    [Fact]
    public void A_cover_linked_to_a_private_file_is_never_backed_up()
    {
        if (!Unix) return;
        var secret = Path.Combine(_root, "id_ed25519");
        File.WriteAllText(secret, "PRIVATE KEY");
        var cover = Path.Combine(_album, "cover.jpg");
        var image = File.ReadAllBytes(cover);
        File.Delete(cover);
        File.CreateSymbolicLink(cover, secret);
        var result = TagWriter.Write([Pending.SaveImage(AlbumReader.Read(_album), "cover.jpg", image)], _backups);
        Assert.Single(result.Problems);
        Assert.Null(result.Batch);
        Assert.False(Directory.Exists(_backups) && Directory.EnumerateFiles(_backups, "*", SearchOption.AllDirectories)
            .Any(f => File.ReadAllText(f).Contains("PRIVATE KEY")));
        Assert.Equal("PRIVATE KEY", File.ReadAllText(secret));
    }

    // ---- B3 / B4: the swap -------------------------------------------------------------

    [Fact]
    public void A_planted_marginal_old_does_not_block_a_write()
    {
        var track = Track("02");
        File.WriteAllText(track + ".marginal-old", "planted");
        var result = TagWriter.Write(Pending.SetField(AlbumReader.Read(_album), FieldMap.Label, "Vertigo"), _backups);
        Assert.Empty(result.Problems);
        Assert.Equal("Vertigo", TrackReader.Read(track).Get(FieldMap.Label));
        Assert.Equal("planted", File.ReadAllText(track + ".marginal-old"));
    }

    // ---- B6 / B7 / I1 / I2: the manifest -------------------------------------------------

    private WriteBatch WriteLabel()
    {
        var result = TagWriter.Write(Pending.SetField(AlbumReader.Read(_album), FieldMap.Label, "Vertigo"), _backups);
        Assert.Empty(result.Problems);
        return result.Batch!;
    }

    private string ManifestOf(WriteBatch b) => Path.Combine(b.BackupFolder, "manifest.tsv");

    [Fact]
    public void Manifest_is_on_disk_for_every_file_written()
    {
        var batch = WriteLabel();
        var lines = File.ReadAllLines(ManifestOf(batch));
        Assert.Equal(3, lines.Length);
        Assert.False(File.Exists(ManifestOf(batch) + ".new"));
    }

    [Theory]
    [InlineData("relative/01.flac")]
    [InlineData("/tmp/../etc/01.flac")]
    [InlineData("/etc/passwd")]
    public void Manifest_naming_a_path_write_could_not_produce_is_not_used(string target)
    {
        var batch = WriteLabel();
        var lines = File.ReadAllLines(ManifestOf(batch));
        var p = lines[0].Split('\t');
        p[0] = target;
        lines[0] = string.Join('\t', p);
        File.WriteAllLines(ManifestOf(batch), lines);
        Assert.Null(TagWriter.Latest(_backups));
    }

    [Fact]
    public void Manifest_backup_outside_its_batch_is_not_used()
    {
        var batch = WriteLabel();
        var outside = Path.Combine(_root, "attacker.head");
        File.WriteAllText(outside, "x");
        var lines = File.ReadAllLines(ManifestOf(batch));
        var p = lines[0].Split('\t');
        p[1] = outside;
        lines[0] = string.Join('\t', p);
        File.WriteAllLines(ManifestOf(batch), lines);
        Assert.Null(TagWriter.Latest(_backups));
    }

    [Fact]
    public void Image_entry_must_be_a_side_file()
    {
        var batch = WriteLabel();
        var hash = new string('A', 64);
        File.AppendAllLines(ManifestOf(batch), [$"IMG\t{Path.Combine(_root, "victim.txt")}\t\t{hash}"]);
        Assert.Null(TagWriter.Latest(_backups));
    }

    [Fact]
    public void Undo_that_fails_everywhere_keeps_its_undo()
    {
        var batch = WriteLabel();
        var moved = _album + "-away";
        Directory.Move(_album, moved);
        var problems = TagWriter.Undo(batch);
        Assert.Equal(3, problems.Count);
        Directory.Move(moved, _album);
        var again = TagWriter.Latest(_backups);
        Assert.NotNull(again);
        Assert.Empty(TagWriter.Undo(again!));
        Assert.Null(TagWriter.Latest(_backups));
        Assert.All(AlbumReader.Read(_album).Tracks, t => Assert.NotEqual("Vertigo", t.Get(FieldMap.Label)));
    }

    [Fact]
    public void Undo_that_fails_for_one_keeps_only_that_one()
    {
        var batch = WriteLabel();
        var two = Track("02");
        var away = two + ".away";
        File.Move(two, away);
        Assert.Single(TagWriter.Undo(batch));
        File.Move(away, two);
        var again = TagWriter.Latest(_backups)!;
        Assert.Single(again.Files);
        Assert.Equal(two, again.Files[0].Original);
        Assert.Empty(TagWriter.Undo(again));
    }

    [Fact]
    public void Backup_listing_does_not_follow_links_and_skips_unreadable()
    {
        if (!Unix) return;
        var batch = WriteLabel();
        Directory.CreateSymbolicLink(Path.Combine(batch.BackupFolder, "loop"), ".");
        Directory.CreateSymbolicLink(Path.Combine(batch.BackupFolder, "loop2"), ".");
        var clock = Stopwatch.StartNew();
        Assert.Single(Backups.List(_backups));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        var locked = Path.Combine(_backups, "29991231-235959-999");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "manifest.tsv"), "x");
        File.SetUnixFileMode(Path.Combine(locked, "manifest.tsv"), UnixFileMode.None);
        Assert.Equal(batch.BackupFolder, TagWriter.Latest(_backups)!.BackupFolder);
    }

    // ---- B8: the share check sees through links ------------------------------------------

    [Fact]
    public void Real_path_follows_links()
    {
        if (!Unix) return;
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, _album);
        Assert.Equal(SafeFiles.RealPath(_album), SafeFiles.RealPath(Path.Combine(link)));
        Assert.Equal(Path.Combine(SafeFiles.RealPath(_album)!, "cover.jpg"), SafeFiles.RealPath(Path.Combine(link, "cover.jpg")));
        Assert.True(SafeFiles.IsWithin(Path.Combine(link, "cover.jpg"), _album));
    }

    [Fact]
    public void Real_path_of_a_loop_is_null()
    {
        if (!Unix) return;
        var a = Path.Combine(_root, "la");
        var b = Path.Combine(_root, "lb");
        File.CreateSymbolicLink(a, b);
        File.CreateSymbolicLink(b, a);
        Assert.Null(SafeFiles.RealPath(Path.Combine(a, "x")));
    }
}
