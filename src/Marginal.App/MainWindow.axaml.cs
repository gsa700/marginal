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
    private CancellationTokenSource? _scan;
    private Album? _album;
    private TrackFile? _track;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// `marginal <folder>`: open it straight away (spec §6).
    public void OpenAtStart(string? folder)
    {
        if (folder is not null && Directory.Exists(folder)) Scan(folder);
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
            var folders = Library.AlbumFolders(root).ToList();
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
                    _albums.Add(new AlbumRow(album));
                    ScanText.Text = n < folders.Count ? $"reading {n} of {folders.Count}" : "";
                    Refilter();
                });
            }
            if (folders.Count == 0)
                Dispatcher.UIThread.Post(() => { ScanText.Text = "no music files under this folder"; Refilter(); });
        }, cancel.Token);
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
        foreach (var r in _albums) r.ShowFolder = twins.Contains(r);

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

        ValuesHeader.Text = $"VALUE ACROSS {album.Tracks.Count} FILES";
        FieldList.ItemsSource = FieldMap.AlbumTable
            .Select(f => FieldRow.From(album.Fields.First(s => s.Field == f), album)).ToList();

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

    // ---- art ---------------------------------------------------------------

    private void ShowFolderArt(Album album)
    {
        var cover = album.Cover;
        // No cover.jpg but art inside the files (the usual iTunes or store
        // library): the embedded cover takes the main spot, the selected
        // track's, set by ShowEmbeddedArt (his question, 2026-10-31).
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
                using var file = TagLib.File.Create(path);
                var picture = file.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover && ImageSize.Read(p.Data.Data) is not null)
                              ?? file.Tag.Pictures.FirstOrDefault(p => ImageSize.Read(ImageSize.Repair(p.Data.Data)) is not null);
                if (picture is null) return;
                var bytes = ImageSize.Repair(picture.Data.Data);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!ReferenceEquals(_track, track)) return;
                    using var stream = new MemoryStream(bytes);
                    Bitmap bitmap;
                    try { bitmap = new Bitmap(stream); } catch (Exception) { return; /* not decodable: the sizes still show */ }
                    EmbeddedImage.Source = bitmap;
                    if (_album?.Cover is null) CoverImage.Source = bitmap;
                });
            }
            catch (Exception) { /* unreadable now: the facts already say what was read */ }
        });
    }

    private static Bitmap? LoadBitmap(string path)
    {
        try { return new Bitmap(path); }
        catch (Exception) { return null; }
    }

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
