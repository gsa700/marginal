using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Marginal.Core;

namespace Marginal.App;

public sealed partial class MainWindow : Window
{
    private readonly List<AlbumRow> _albums = [];
    private readonly Settings _settings = Settings.Load();
    private PrefsWindow? _prefs;

    /// What the last update check found, for the Preferences window.
    internal UpdateInfo? LatestUpdate { get; set; }

    /// The last update could not be put in place, or did not start.
    internal bool LastUpdateFailed { get; private set; }
    private CancellationTokenSource? _scan;
    private Album? _album;
    private TrackFile? _track;
    private bool _onShare;

    /// Pending changes, by album folder: switching albums keeps them.
    private readonly Dictionary<string, List<Change>> _pending = [];

    private List<Change> PendingHere => _album is null ? [] : _pending.GetValueOrDefault(_album.Folder) ?? [];

    public MainWindow()
    {
        InitializeComponent();
        Lyrics.Changes += changes => AddPending(changes);
        Lyrics.WriteAsked += () => OnWrite(this, new RoutedEventArgs());
        Closed += (_, _) => Lyrics.Shutdown();
        Opened += async (_, _) =>
        {
            // Installing and uninstalling, before anything else (InstallFlow,
            // Deadwax's): true when this run ends here.
            if (await InstallFlow.BeforeStartAsync(this)) return;
            _ = CheckForUpdateAsync();
        };
        UpdateUndo();
        UpdateBackups();
        // Originals older than the keeping time go, quietly, at each start.
        _ = Task.Run(() => Backups.Prune(Backups.Keep)).ContinueWith(_ => Dispatcher.UIThread.Post(UpdateBackups));
    }

    /// `marginal <folder>`: open it straight away (spec §6), the album itself
    /// selected when the folder is one. `from` names the app that asked
    /// ("albumwall"), shown beside the tabs.
    /// Something went wrong that nothing else caught (App's last line of
    /// defence): said on the status line; Pending is kept.
    public void ShowError(string message) => ScanText.Text = $"something went wrong: {message}";

    public void OpenAtStart(string? folder, string? from = null)
    {
        if (string.Equals(from, "albumwall", StringComparison.OrdinalIgnoreCase))
        {
            FromText.Text = "opened from AlbumWall";
            FromChip.IsVisible = true;
        }
        if (folder is not null && Directory.Exists(folder)) Scan(Path.GetFullPath(folder));
    }

    // ---- the library -------------------------------------------------------

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open a folder of albums",
            AllowMultiple = false,
        });
        if (picked is [var folder] && folder.TryGetLocalPath() is { } path) Scan(path);
    }

    /// Reads every album under `root`, one at a time, off the UI thread; each
    /// joins the list as soon as it is read.
    private void Scan(string root)
    {
        _scan?.Cancel();
        var cancel = _scan = new CancellationTokenSource();
        _albums.Clear();
        ShowAlbum(null);
        RootText.Text = root;
        ToolTip.SetTip(RootText, root);
        LibraryHint.IsVisible = false;
        Refilter();

        _ = Task.Run(() =>
        {
            try { ScanFolders(root, cancel); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Said, not lost with the discarded task (review A7).
                Dispatcher.UIThread.Post(() => ScanText.Text = $"the scan stopped: {ex.Message}");
            }
        }, cancel.Token);
    }

    private void ScanFolders(string root, CancellationTokenSource cancel)
    {
        {
            var folders = Library.AlbumFolders(root, cancel.Token).ToList();
            var done = 0;
            foreach (var folder in folders)
            {
                if (cancel.IsCancellationRequested) return;
                var album = AlbumReader.Read(folder);
                done++;
                var n = done;
                Dispatcher.UIThread.Post(() =>
                {
                    if (cancel.IsCancellationRequested) return;
                    var row = new AlbumRow(album);
                    _albums.Add(row);
                    ScanText.Text = n < folders.Count ? $"reading {n} of {folders.Count}" : "";
                    Refilter();
                    // Opened on an album (from AlbumWall, say): that album, at once.
                    if (string.Equals(album.Folder.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal)
                        && AlbumList.SelectedItem is null)
                        AlbumList.SelectedItem = row;
                });
            }
            if (folders.Count == 0)
                Dispatcher.UIThread.Post(() => { ScanText.Text = "no music files under this folder"; Refilter(); });
        }
    }

    private void OnFilterChanged(object? sender, TextChangedEventArgs e) => Refilter();

    /// The chips: All, one format, or Needs work. One at a time.
    private void OnChip(object? sender, RoutedEventArgs e)
    {
        foreach (var chip in Chips.Children.OfType<ToggleButton>())
            chip.IsChecked = ReferenceEquals(chip, sender);
        Refilter();
    }

    private void Refilter()
    {
        var text = FilterBox.Text?.Trim() ?? "";
        IEnumerable<AlbumRow> rows = _albums;
        if (text.Length > 0)
            rows = rows.Where(r => r.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                                   || r.Album.Artist.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (ChipFlac.IsChecked == true) rows = rows.Where(r => r.Album.Tracks.Any(t => t.Format == AudioFormat.Flac));
        if (ChipMp3.IsChecked == true) rows = rows.Where(r => r.Album.Tracks.Any(t => t.Format == AudioFormat.Mp3));
        if (ChipM4a.IsChecked == true) rows = rows.Where(r => r.Album.Tracks.Any(t => t.Format == AudioFormat.M4a));
        if (ChipWork.IsChecked == true) rows = rows.Where(r => r.Album.Status is Tone.Fixable or Tone.Missing);

        // Same title and artist twice: show each one's folder name.
        var twins = _albums.GroupBy(r => (r.Title.ToLowerInvariant(), r.Album.Artist.ToLowerInvariant()))
            .Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
        foreach (var r in _albums)
        {
            r.ShowFolder = twins.Contains(r);
            r.PendingCount = _pending.TryGetValue(r.Album.Folder, out var p) ? p.Count : 0;
        }
        var pendingAlbums = _albums.Count(r => r.HasPending);
        ChipPending.IsVisible = pendingAlbums > 0;
        if (pendingAlbums == 0 && ChipPending.IsChecked == true) { ChipPending.IsChecked = false; ChipAll.IsChecked = true; }
        if (ChipPending.IsChecked == true) rows = rows.Where(r => r.HasPending);
        ShowLibraryPending();
        CleanButton.IsEnabled = _albums.Count > 0;

        var selected = AlbumList.SelectedItem;
        var list = rows.ToList();
        AlbumList.ItemsSource = list;
        if (selected is AlbumRow s && list.Contains(s)) AlbumList.SelectedItem = s;

        AlbumCount.Text = _albums.Count == 1 ? "1 album" : $"{_albums.Count} albums";
        int Count(AudioFormat f) => _albums.Count(a => a.Album.Tracks.Any(t => t.Format == f));
        ChipFlac.Content = $"FLAC {Count(AudioFormat.Flac)}";
        ChipMp3.Content = $"MP3 {Count(AudioFormat.Mp3)}";
        ChipM4a.Content = $"M4A {Count(AudioFormat.M4a)}";
        var clean = _albums.Count(a => a.Album.Status is Tone.Clean or Tone.Note);
        var fixable = _albums.Count(a => a.Album.Status == Tone.Fixable);
        var missing = _albums.Count(a => a.Album.Status == Tone.Missing);
        CleanCount.Text = _albums.Count > 0 ? $"● {clean} clean" : "";
        FixableCount.Text = _albums.Count > 0 ? $"● {fixable} fixable" : "";
        MissingCount.Text = _albums.Count > 0 ? $"● {missing} missing" : "";
    }

    private void OnAlbumSelected(object? sender, SelectionChangedEventArgs e) =>
        ShowAlbum((AlbumList.SelectedItem as AlbumRow)?.Album);

    // ---- the album ---------------------------------------------------------

    private void ShowAlbum(Album? album)
    {
        _album = album;
        EmptyAlbum.IsVisible = album is null;
        AlbumView.IsVisible = album is not null;
        ArtHint.IsVisible = album is null;
        ArtGrid.IsVisible = album is not null;
        LookupButton.IsEnabled = album is not null;
        if (album is null) Lyrics.Show(null);
        ArtButton.IsEnabled = album is not null;
        if (album is null)
        {
            ShowTrack(null);
            return;
        }

        AlbumTitle.Text = album.Title;
        var length = album.Length.TotalHours >= 1 ? album.Length.ToString(@"h\:mm\:ss") : album.Length.ToString(@"m\:ss");
        AlbumLine.Text = string.Join(" · ", new[] { album.Artist, album.Year, $"{album.Tracks.Count} tracks", length }.Where(s => s.Length > 0));

        AlbumFolder.Text = album.Folder;
        ToolTip.SetTip(AlbumFolder, album.Folder);
        AlbumChips.Children.Clear();
        foreach (var f in album.Tracks.GroupBy(t => t.Format))
        {
            var kbps = f.Where(t => t.Bitrate > 0).Select(t => t.Bitrate).DefaultIfEmpty().Max();
            AlbumChips.Children.Add(Chip(f.Key == AudioFormat.Flac || kbps == 0 ? $"{f.Key.ToString().ToUpperInvariant()} ×{f.Count()}"
                                                       : $"{f.Key.ToString().ToUpperInvariant()} · {kbps} kbps", Tone.Note));
        }
        foreach (var v in album.Tracks.GroupBy(t => t.TagVersions).Where(g => g.Key.Length > 0))
            AlbumChips.Children.Add(Chip($"{v.Key} ×{v.Count()}", Tone.Note));
        var missing = album.Checks.Count(c => c.Tone == Tone.Missing);
        var fixable = album.Checks.Count(c => c.Tone == Tone.Fixable);
        if (missing > 0) AlbumChips.Children.Add(Chip($"{missing} missing", Tone.Missing));
        if (fixable > 0) AlbumChips.Children.Add(Chip($"{fixable} fixable", Tone.Fixable));
        if (missing + fixable == 0) AlbumChips.Children.Add(Chip("clean", Tone.Clean));

        Lyrics.Show(album);
        ValuesHeader.Text = $"VALUE ACROSS {album.Tracks.Count} FILES";
        _onShare = Library.IsOnNetworkShare(album.Folder);
        LookupButton.IsEnabled = true;
        ArtButton.IsEnabled = true;
        EditBar.IsVisible = false;
        ResultBox.IsVisible = false;
        ShowFields();
        ShowPending();

        var rows = album.Tracks.Select(t => new TrackRow(t, album.Checks.Where(c => c.Track == t.FileName).ToList())).ToList();
        TrackList.ItemsSource = rows;

        AlbumChecks.Children.Clear();
        foreach (var c in album.Checks.Where(c => c.Track is null))
            AlbumChecks.Children.Add(Chip(c.Text, c.Tone));

        ShowFolderArt(album);
        TrackList.SelectedIndex = rows.Count > 0 ? 0 : -1;
    }

    private void OnTrackSelected(object? sender, SelectionChangedEventArgs e) =>
        ShowTrack((TrackList.SelectedItem as TrackRow)?.Track);

    private void OnFieldsView(object? sender, RoutedEventArgs e) => SetRawView(false);
    private void OnRawView(object? sender, RoutedEventArgs e) => SetRawView(true);

    private void SetRawView(bool raw)
    {
        FieldsButton.IsChecked = !raw;
        RawButton.IsChecked = raw;
        FieldsCard.IsVisible = !raw;
        RawCard.IsVisible = raw;
    }

    private void ShowTrack(TrackFile? track)
    {
        _track = track;
        RawList.ItemsSource = track?.Raw.Select(RawRow.From).ToList();
        RawFileName.Text = track?.FileName ?? "";
        FileName.Text = track?.FileName ?? "";
        if (track is null)
        {
            FileFacts.ItemsSource = null;
            EmbeddedImage.Source = null;
            return;
        }

        var facts = new List<RawRow>();
        void Fact(string k, string v) => facts.Add(new RawRow(k, v, Tones.Text, null));
        if (track.Problem is not null) Fact("problem", track.Problem);
        Fact("format", track.Format.ToString().ToUpperInvariant() + (track.Bitrate > 0 ? $" · {track.Bitrate} kbps" : ""));
        Fact("length", track.Duration.ToString(@"m\:ss\.f"));
        Fact("tags", track.TagVersions.Length > 0 ? track.TagVersions : "none");
        Fact("frames", $"{track.Raw.Count}" + (track.Raw.Count(r => r.Junk) is > 0 and var j ? $", {j} junk" : ""));
        foreach (var p in track.Pictures) Fact("picture", $"{p.Kind} · {p.Size} · {TrackReader.Bytes(p.Bytes)}");
        Fact("lyrics", track.Lyrics.ToString().ToLowerInvariant());
        FileFacts.ItemsSource = facts;

        ShowEmbeddedArt(track);
    }

    // ---- updates (Deadwax's, on this window) -----------------------------------

    /// A few seconds after launch. Only a published copy is offered anything;
    /// a development build is not. All this ever does is light the dot on the
    /// gear (AlbumWall's rule): nothing is fetched until Preferences asks.
    private async Task CheckForUpdateAsync()
    {
        LastUpdateFailed = UpdateService.ConsumeUpdateFailed();
        if (LastUpdateFailed) ShowUpdateDot(true);
        if (!UpdateService.CanUpdate || !_settings.CheckForUpdates) return;
        await Task.Delay(TimeSpan.FromSeconds(7));
        LatestUpdate = await UpdateService.CheckAsync(_settings.EdgeChannel);
        ShowUpdateDot(LatestUpdate.UpdateAvailable || LastUpdateFailed);
    }

    internal void ShowUpdateDot(bool show)
    {
        UpdateDot.IsVisible = show;
        _prefs?.FillUpdate();
    }

    /// Fetches the known newer release, checks it, and restarts into it.
    /// Pending changes would be lost, so it refuses while there are any.
    internal async Task ApplyUpdateAsync(UpdateInfo info, IProgress<double> progress)
    {
        if (_pending.Count > 0)
            throw new InvalidOperationException("Write or discard the pending changes first; the update restarts Marginal.");
        var staged = await UpdateService.DownloadAndStageAsync(info, progress);
        UpdateService.ApplyAndRestart(staged);
        Close();
    }

    private void OnPrefs(object? sender, RoutedEventArgs e)
    {
        if (_prefs is { } open) { open.Activate(); return; }
        _prefs = new PrefsWindow(this, _settings);
        _prefs.Closed += (_, _) => _prefs = null;
        _prefs.Show(this);
    }

    // ---- the mode tabs -------------------------------------------------------

    /// Tags & art or Lyrics: the Lyrics panel takes the album and art columns.
    private void OnTab(object? sender, RoutedEventArgs e)
    {
        var lyrics = ReferenceEquals(sender, LyricsTab);
        TagsTab.Classes.Set("on", !lyrics);
        LyricsTab.Classes.Set("on", lyrics);
        Lyrics.IsVisible = lyrics;
        AlbumPane.IsVisible = !lyrics;
        ArtPane.IsVisible = !lyrics;
        ArtSplitter.IsVisible = !lyrics;
    }

    // ---- editing and Pending -----------------------------------------------

    private void ShowFields()
    {
        if (_album is not { } album) return;
        var pending = PendingHere;
        FieldList.SelectionChanged -= OnFieldSelected;
        FieldList.ItemsSource = FieldMap.AlbumTable
            .Select(f => FieldRow.From(album.Fields.First(s => s.Field == f), album, pending)).ToList();
        FieldList.SelectedItem = null;
        FieldList.SelectionChanged += OnFieldSelected;
        CleanAlbumItem.IsEnabled = Pending.CleanJunk(album).Any(c => !pending.Contains(c));
    }

    private void OnFieldSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (FieldList.SelectedItem is not FieldRow { Field: { } field } || _album is not { } album) return;
        EditBar.Tag = field;
        EditLabel.Text = field.Name;
        var pending = PendingHere.FirstOrDefault(c => c.Field == field);
        EditBox.Text = pending is not null ? pending.To ?? "" : album.Fields.First(f => f.Field == field).Common ?? "";
        EditApply.Content = $"Set on all {album.Tracks.Count} files";
        EditBar.IsVisible = true;
        EditBox.Focus();
        EditBox.SelectAll();
    }

    private void OnEditKey(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter) { OnEditApply(sender, e); e.Handled = true; }
        else if (e.Key == Avalonia.Input.Key.Escape) { OnEditCancel(sender, e); e.Handled = true; }
    }

    private void OnEditApply(object? sender, RoutedEventArgs e) => Edit(EditBox.Text);
    private void OnEditRemove(object? sender, RoutedEventArgs e) => Edit(null);

    private void OnEditCancel(object? sender, RoutedEventArgs e)
    {
        EditBar.IsVisible = false;
        FieldList.SelectedItem = null;
    }

    private void Edit(string? value)
    {
        if (EditBar.Tag is not Field field || _album is not { } album) return;
        AddPending(Pending.SetField(album, field, value), field);
        EditBar.IsVisible = false;
    }

    /// The lookup sheet; what is ticked there joins this album's Pending.
    private async void OnLookup(object? sender, RoutedEventArgs e)
    {
        if (_album is not { } album) return;
        var changes = await new LookupWindow(album).ShowDialog<List<Change>?>(this);
        if (changes is { Count: > 0 } && _album?.Folder == album.Folder) AddPending(changes);
    }

    /// The art picker; the image it makes joins this album's Pending. It opens
    /// on the back when the album has a cover but no back.
    private async void OnFindArt(object? sender, RoutedEventArgs e)
    {
        if (_album is not { } album) return;
        var slot = album.Cover is not null && album.Back is null ? ArtSlot.Back : ArtSlot.Front;
        var changes = await new ArtWindow(album, slot).ShowDialog<List<Change>?>(this);
        if (changes is { Count: > 0 } && _album?.Folder == album.Folder) AddPending(changes);
    }

    private void OnCleanJunk(object? sender, RoutedEventArgs e)
    {
        if (_album is { } album) AddPending(Pending.CleanJunk(album));
    }

    /// Merges into this album's Pending. For a field, a value back to what the
    /// files hold drops its earlier changes rather than adding new ones.
    private void AddPending(IReadOnlyList<Change> changes, Field? field = null)
    {
        if (_album is not { } album) return;
        var list = PendingHere;
        if (field is not null) list = list.Where(c => c.Field != field).ToList();
        list = Core.Pending.Merge(list, changes);
        if (list.Count == 0) _pending.Remove(album.Folder);
        else _pending[album.Folder] = list;
        ResultBox.IsVisible = false;
        ShowFields();
        ShowPending();
        UpdatePendingMarks();
    }

    private void ShowPending()
    {
        var list = PendingHere;
        PendingCard.IsVisible = list.Count > 0;
        var files = list.Select(c => c.Path).Distinct().Count();
        Lyrics.ShowPending(list.Count, files, !_onShare);
        if (list.Count == 0) return;
        PendingTitle.Text = $"Pending: {list.Count} change{(list.Count == 1 ? "" : "s")} in {files} file{(files == 1 ? "" : "s")}";
        int Count(ChangeKind k) => list.Count(c => c.Kind == k);
        PendingAdds.Text = Count(ChangeKind.Add) is > 0 and var a ? $"+{a} add" : "";
        PendingChanges.Text = Count(ChangeKind.Change) is > 0 and var c ? $"~{c} change" : "";
        PendingRemoves.Text = Count(ChangeKind.Remove) is > 0 and var r ? $"−{r} remove" : "";
        PendingNote.Text = _onShare ? "· on a network share: read only (spec §9.6)" : "· nothing written yet";
        PendingNote.Foreground = _onShare ? Tones.BadText : Tones.Dim;
        PendingList.ItemsSource = list.Select(ch => new PendingRow(ch)).ToList();
        WriteButton.Content = $"Write {files} file{(files == 1 ? "" : "s")}";
        WriteButton.IsEnabled = !_onShare;
    }

    private void OnDiscard(object? sender, RoutedEventArgs e)
    {
        if (_album is null) return;
        _pending.Remove(_album.Folder);
        ShowFields();
        ShowPending();
        UpdatePendingMarks();
    }

    private async void OnWrite(object? sender, RoutedEventArgs e)
    {
        if (_album is not { } album || PendingHere is not { Count: > 0 } changes || _onShare) return;
        WriteButton.IsEnabled = false;
        WriteButton.Content = "Writing…";
        var (result, reread) = await Task.Run(() =>
        {
            var r = TagWriter.Write(changes);
            return (r, AlbumReader.Read(album.Folder));
        });

        _pending.Remove(album.Folder);
        ReplaceAlbum(reread);
        ShowResult(result.Written > 0
                ? $"Wrote {result.Written} file{(result.Written == 1 ? "" : "s")}. The originals are kept; Undo puts them back."
                : "Nothing was written.",
            result.Problems.Select(p => $"{p.File}: {p.Problem} (left as it was)"),
            result.Problems.Count == 0 ? Tone.Clean : Tone.Missing);
        UpdateUndo();
        UpdateBackups();
    }

    private async void OnUndo(object? sender, RoutedEventArgs e)
    {
        if (TagWriter.Latest() is not { } batch) return;
        UndoButton.IsEnabled = false;
        var (problems, reread) = await Task.Run(() =>
        {
            var p = TagWriter.Undo(batch);
            return (p, batch.Folders.Where(Directory.Exists).Select(AlbumReader.Read).ToList());
        });
        foreach (var a in reread) ReplaceAlbum(a);
        var undid = $"Undid the write of {batch.Time:HH:mm:ss}: {batch.Count - problems.Count} of {batch.Count} files back as they were.";
        ShowResult(undid, problems.Select(p => $"{p.File}: {p.Problem}"), problems.Count == 0 ? Tone.Clean : Tone.Missing);
        ScanText.Text = undid;
        UpdateUndo();
        UpdateBackups();
    }

    private void UpdateUndo()
    {
        var latest = TagWriter.Latest();
        UndoButton.IsEnabled = latest is not null;
        ToolTip.SetTip(UndoButton, latest is null ? null
            : $"Undo the write of {latest.Time:yyyy-MM-dd HH:mm:ss}: {latest.Count} file(s) in {latest.Folders.Count} album(s)");
    }

    // ---- across albums --------------------------------------------------------

    /// Every album's junk into its own Pending; then only those albums are
    /// listed (the Pending chip), each to be looked at before Write all.
    private void OnCleanLibrary(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _albums)
        {
            var changes = Pending.CleanJunk(row.Album);
            if (changes.Count == 0) continue;
            var list = Core.Pending.Merge(_pending.GetValueOrDefault(row.Album.Folder) ?? [], changes);
            if (list.Count > 0) _pending[row.Album.Folder] = list;
        }
        if (_pending.Count == 0)
        {
            ScanText.Text = "no junk tags in this library";
            return;
        }
        foreach (var chip in Chips.Children.OfType<ToggleButton>()) chip.IsChecked = false;
        ChipPending.IsChecked = true;
        Refilter();
        if (_album is not null) { ShowFields(); ShowPending(); }
    }

    private void UpdatePendingMarks()
    {
        foreach (var r in _albums) r.PendingCount = _pending.TryGetValue(r.Album.Folder, out var p) ? p.Count : 0;
        ChipPending.IsVisible = _pending.Count > 0;
        ShowLibraryPending();
    }

    /// The bar shows when more than the album on screen has changes waiting.
    private void ShowLibraryPending()
    {
        var albums = _pending.Count;
        var changes = _pending.Values.Sum(l => l.Count);
        var files = _pending.Values.SelectMany(l => l).Select(c => c.Path).Distinct().Count();
        LibraryPendingBar.IsVisible = albums > 1 || (albums == 1 && _album?.Folder != _pending.Keys.First());
        LibraryPendingText.Text = $"Pending in {albums} album{(albums == 1 ? "" : "s")}: {changes} change{(changes == 1 ? "" : "s")} in {files} file{(files == 1 ? "" : "s")}. Pick an album to see its list.";
        WriteAllButton.Content = $"Write all {files} files";
    }

    private async void OnWriteAll(object? sender, RoutedEventArgs e)
    {
        var all = _pending.Values.SelectMany(l => l).ToList();
        if (all.Count == 0) return;
        WriteAllButton.IsEnabled = false;
        WriteAllButton.Content = "Writing…";
        var folders = _pending.Keys.ToList();
        var (result, reread) = await Task.Run(() =>
        {
            var r = TagWriter.Write(all);
            return (r, folders.Where(Directory.Exists).Select(AlbumReader.Read).ToList());
        });
        _pending.Clear();
        foreach (var a in reread) ReplaceAlbum(a);
        WriteAllButton.IsEnabled = true;
        var refused = result.Problems.Count;
        ScanText.Text = $"wrote {result.Written} files in {result.Batch?.Folders.Count ?? 0} albums"
                        + (refused > 0 ? $"; {refused} refused, left as they were" : "") + ". Undo takes it all back.";
        if (_album is not null)
            ShowResult($"Wrote {result.Written} files in {result.Batch?.Folders.Count ?? 0} albums. Undo takes it all back.",
                result.Problems.Select(p => $"{p.File}: {p.Problem} (left as it was)"), refused == 0 ? Tone.Clean : Tone.Missing);
        UpdatePendingMarks();
        UpdateUndo();
        UpdateBackups();
    }

    private void OnDiscardAll(object? sender, RoutedEventArgs e)
    {
        _pending.Clear();
        Refilter();
        if (_album is not null) { ShowFields(); ShowPending(); }
    }

    // ---- backups -------------------------------------------------------------

    private void UpdateBackups()
    {
        var list = Backups.List();
        var bytes = list.Sum(b => b.Bytes);
        BackupText.Text = bytes == 0
            ? "backups: every write can be undone for a year"
            : $"backups: {TrackReader.Bytes(bytes)} from {list.Count} write{(list.Count == 1 ? "" : "s")}, kept a year";
        EmptyBackupsButton.IsVisible = bytes > 0;
        EmptyQuestion.Text = $"Delete the backups of {list.Count} write{(list.Count == 1 ? "" : "s")} ({TrackReader.Bytes(bytes)})? "
                             + "Those writes can no longer be undone. Your music is not touched.";
    }

    private void OnEmptyBackups(object? sender, RoutedEventArgs e)
    {
        var (writes, bytes) = Backups.Empty();
        EmptyBackupsButton.Flyout?.Hide();
        ScanText.Text = $"emptied backups: {TrackReader.Bytes(bytes)} from {writes} write{(writes == 1 ? "" : "s")}";
        UpdateUndo();
        UpdateBackups();
    }

    /// The album as read again after a write: its row is replaced and, if it
    /// is the one on screen, shown again.
    private void ReplaceAlbum(Album album)
    {
        var i = _albums.FindIndex(r => r.Album.Folder == album.Folder);
        var row = new AlbumRow(album);
        if (i >= 0) _albums[i] = row;
        var showing = _album?.Folder == album.Folder;
        Refilter();
        if (showing) AlbumList.SelectedItem = row;
    }

    private void ShowResult(string title, IEnumerable<string> lines, Tone tone)
    {
        var (ink, fill) = Tones.Of(tone);
        ResultBox.IsVisible = true;
        ResultBox.Background = fill;
        ResultBox.BorderBrush = ink;
        ResultTitle.Text = title;
        ResultTitle.Foreground = ink;
        var text = string.Join("\n", lines);
        ResultText.Text = text;
        ResultText.IsVisible = text.Length > 0;
        ResultText.Foreground = ink;
    }

    // ---- art ---------------------------------------------------------------

    private void ShowFolderArt(Album album)
    {
        var cover = album.Cover;
        // No cover.jpg but art inside the files (the usual iTunes or store
        // library): the embedded cover takes the main spot, the selected
        // track's, set by ShowEmbeddedArt (Dave's question, 2026-10-31).
        var firstEmbedded = album.Tracks.Select(t => t.EmbeddedCover()).OfType<EmbeddedPicture>().FirstOrDefault();
        var embeddedIsMain = cover is null && firstEmbedded is not null;
        CoverName.Text = cover?.Name ?? (embeddedIsMain ? "Embedded (no cover.jpg)" : "cover.jpg");
        CoverImage.Source = cover is null ? null : LoadBitmap(Path.Combine(album.Folder, cover.Name));
        CoverNone.Text = cover is null && !embeddedIsMain ? "no cover.jpg in the folder" : "";
        CoverInfo.Text = cover is not null ? $"{cover.Size} · {TrackReader.Bytes(cover.Bytes)}"
                       : embeddedIsMain ? $"{firstEmbedded!.Size} · {TrackReader.Bytes(firstEmbedded.Bytes)}" : "";
        var (text, tone) = (cover, firstEmbedded) switch
        {
            (null, null) => ("missing", Tone.Missing),
            (null, { Width: < AlbumReader.SmallArt } e) => ($"{e.Width} px", Tone.Fixable),
            (null, _) => ("no cover.jpg", Tone.Note),
            ({ Width: < AlbumReader.SmallArt } c, _) => ($"{c.Width} px", Tone.Fixable),
            ({ Square: false }, _) => ("not square", Tone.Fixable),
            _ => ("good", Tone.Clean),
        };
        Pill(CoverPill, CoverStatus, text, tone);

        var back = album.Back;
        BackImage.Source = back is null ? null : LoadBitmap(Path.Combine(album.Folder, back.Name));
        BackFrame.IsVisible = BackImage.Source is not null;
        BackInfo.Text = back is null ? "not in the folder" : $"{back.Size} · {TrackReader.Bytes(back.Bytes)}";
        Pill(BackPill, BackStatus, back is null ? "none (optional)" : "good", back is null ? Tone.Note : Tone.Clean);

        // Embedded, for the whole album: how many files carry a cover and how
        // it compares with cover.jpg; gone when none does. The picture is the
        // selected track's.
        var covers = album.Tracks.Select(t => t.EmbeddedCover()).ToList();
        var carried = covers.OfType<EmbeddedPicture>().ToList();
        EmbeddedRow.IsVisible = carried.Count > 0 && !embeddedIsMain;
        var sizes = carried.Select(c => c.Size).Distinct().ToList();
        var differs = cover is not null && carried.Any(c => c.Width != cover.Width || c.Height != cover.Height);
        EmbeddedInfo.Text = $"in {carried.Count} of {covers.Count} files · {string.Join(", ", sizes)}" + (differs ? "\ndiffers from cover.jpg" : "");
        EmbeddedInfo.Foreground = differs ? Tones.WarnText : Tones.Text;
    }

    /// The selected track's embedded cover, read again from the file: the
    /// scan keeps sizes, not pictures.
    private void ShowEmbeddedArt(TrackFile track)
    {
        EmbeddedImage.Source = null;
        if (_album?.Cover is null) CoverImage.Source = null;
        if (track.EmbeddedCover() is null) return;

        var path = track.Path;
        _ = Task.Run(() =>
        {
            try
            {
                using var file = TrackReader.Open(path);
                var picture = file.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover && ImageSize.Read(p.Data.Data) is not null)
                              ?? file.Tag.Pictures.FirstOrDefault(p => ImageSize.Read(ImageSize.Repair(p.Data.Data)) is not null);
                if (picture is null) return;
                var bytes = ImageSize.Repair(picture.Data.Data);
                // Decoded here, off the UI thread, and only within the limit.
                if (Display.Decode(bytes, 1600) is not { } bitmap) return;   // not decodable: the sizes still show
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_track, track)) return;
                    EmbeddedImage.Source = bitmap;
                    if (_album?.Cover is null) CoverImage.Source = bitmap;
                });
            }
            catch (Exception) { /* unreadable now: the facts already say what was read */ }
        });
    }

    private static Bitmap? LoadBitmap(string path) => Display.Decode(path, 1600);

    private static void Pill(Border pill, TextBlock label, string text, Tone tone)
    {
        var (t, f) = Tones.Of(tone);
        pill.Background = f;
        label.Text = text;
        label.Foreground = t;
    }

    private static Border Chip(string text, Tone tone)
    {
        var (t, f) = Tones.Of(tone);
        return new Border
        {
            Classes = { "pill" },
            Background = f,
            Margin = new Avalonia.Thickness(6, 0, 0, 4),
            Padding = new Avalonia.Thickness(8, 2),
            Child = new TextBlock { Text = text, Foreground = t, Classes = { "mono" } },
        };
    }
}
