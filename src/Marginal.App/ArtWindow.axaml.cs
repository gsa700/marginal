using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Marginal.Core;

namespace Marginal.App;

/// The art picker for one album. Closes with the changes for Pending (an
/// image file to save, and the front embedded in each file if asked), or null.
/// Asks the Cover Art Archive (and MusicBrainz for the album's editions) only
/// when it opens or the editions box is ticked; never writes anything.
public sealed partial class ArtWindow : Window
{
    private readonly Album _album;
    private readonly HttpClient _http = CoverArtArchive.Client();
    private readonly MusicBrainzClient _mb = new();
    private readonly CancellationTokenSource _closing = new();
    private List<ArtRow> _rows = [];

    /// Full images asked for, by address: your release's front and back start
    /// as soon as they are known, so a click on them is usually instant.
    private readonly Dictionary<string, Task<(byte[]? Image, string? Error)>> _full = [];
    private int _loading;
    private byte[]? _picked;
    private string _pickedFrom = "";
    private byte[]? _made;
    private int _making;

    /// For the previewer only.
    public ArtWindow() : this(AlbumReader.Read(Path.GetTempPath(), []), ArtSlot.Front) { }

    /// Its split and every option, kept (Keep).
    public void Remember(Settings settings)
    {
        Keep.Column(settings, this, Columns, 2, "art.selected");
        Keep.Tick(settings, this, OtherEditions, "art.otherEditions");
        Keep.Tick(settings, this, AsPrinted, "art.asPrinted");
        Keep.Tick(settings, this, CropSquare, "art.cropSquare");
        Keep.Tick(settings, this, PadSquare, "art.padSquare");
        Keep.Choice(settings, this, SizeBox, "art.size");
        Keep.Choice(settings, this, QualityBox, "art.quality");
        Keep.Tick(settings, this, StripBox, "art.strip");
        Keep.Tick(settings, this, SaveBox, "art.save");
        Keep.Tick(settings, this, EmbedBox, "art.embed");
    }

    public ArtWindow(Album album, ArtSlot slot)
    {
        _album = album;
        InitializeComponent();
        TextSize.Follow(this);   // before the frame: the frame stays its own size
        DrawnFrame.Attach(this, Surface, TopBar, LeftButtons, RightButtons, owned: true);
        AlbumLine.Text = $"{album.Artist} — {album.Title}";
        FrontSlot.Content = $"Front  {(album.Cover is { } c ? $"have {c.Size}" : album.Tracks.Any(t => t.EmbeddedCover() is not null) ? "embedded only" : "missing")}";
        BackSlot.Content = $"Back  {(album.Back is { } b ? $"have {b.Size}" : "missing")}";
        FrontSlot.IsChecked = slot == ArtSlot.Front;
        BackSlot.IsChecked = slot == ArtSlot.Back;
        AddHandler(DragDrop.DropEvent, OnDrop);
        Opened += async (_, _) => { AddLocal(); await LoadCandidates(); };
        Closed += (_, _) => { _closing.Cancel(); _http.Dispose(); _mb.Dispose(); };
        ShowSlot();
    }

    private ArtSlot Slot => BackSlot.IsChecked == true ? ArtSlot.Back : ArtSlot.Front;

    // ---- candidates --------------------------------------------------------------

    private async Task LoadCandidates()
    {
        var ticket = ++_loading;
        var release = _album.Fields.First(f => f.Field == FieldMap.MusicBrainzRelease).Common;
        var group = _album.Fields.First(f => f.Field == FieldMap.MusicBrainzReleaseGroup).Common;
        _rows = _rows.Where(r => r.Local is not null).ToList();
        ShowSlot();
        // No release in the tags: MusicBrainz is searched for the album, as
        // Look up does, and the best-ranked release is asked about (Dave,
        // 2026-10-31: the picker was all but useless without the IDs).
        _searched = false;
        if (release is null || !WebLimits.IsMbid(release))
        {
            Busy($"No MusicBrainz release in the tags: searching MusicBrainz for {_album.Artist} — {_album.Title}…");
            Status("searching MusicBrainz…", Tones.Dim);
            ReleaseCandidate? best;
            try
            {
                using var search = await _mb.SearchReleasesAsync(Lookup.Query(_album), 25, _closing.Token);
                best = search is null ? null : Lookup.Candidates(search.RootElement, _album).FirstOrDefault();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Busy(null);
                Status($"MusicBrainz: {ex.Message}", Tones.BadText);
                return;
            }
            if (ticket != _loading) return;
            if (best is null || !WebLimits.IsMbid(best.Id))
            {
                Busy(null);
                NoneText.Text = "These files carry no MusicBrainz release, and a search for this album found none. "
                                + "Look up on MusicBrainz to find it by hand (a pasted link works), or use From a file… (or drop one here).";
                NoneText.IsVisible = true;
                Status("MusicBrainz found no release for this album", Tones.WarnText);
                return;
            }
            release = best.Id;
            group = null;
            _searched = true;
            _found = best;
            var clear = Lookup.IsClearMatch(best, _album);
            NoneText.Text = clear
                ? $"Found by search: {best.Title} by {best.Artist}, {best.Line}, {best.Tracks} tracks, the same as these files."
                : $"Found by search, not a sure match: {best.Title} by {best.Artist}, {best.Line}, {best.Tracks} tracks for {_album.Tracks.Count} files. "
                  + "Check the images, or Look up on MusicBrainz to choose the release.";
            NoneText.IsVisible = true;
            TakeIdsBox.Content = $"Also take its MusicBrainz IDs ({best.Title}, {best.Line})";
            TakeIdsBox.IsVisible = clear;
        }
        Status("asking the Cover Art Archive…", Tones.Dim);
        Busy(OtherEditions.IsChecked == true
            ? "Asking the Cover Art Archive: your release first, then other editions of the album…"
            : "Asking the Cover Art Archive for your release's art…");
        try
        {
            // The release group, for other editions: from the tags, or asked of
            // MusicBrainz alongside the archive.
            var others = OtherEditions.IsChecked == true;
            // A release found by search needs its group for the IDs to take, even
            // when other editions are not asked about.
            var groupTask = group is not null || (!others && !_searched) ? Task.FromResult(group) : GroupOf(release);
            var error = await CoverArtArchive.CandidatesAsync(_http, _mb, release, groupTask, others,
                found => Dispatcher.UIThread.Post(() => Arrived(ticket, found)), _closing.Token,
                ownLabel: _searched ? "best match by search" : "your release");
            if (_searched) _foundGroup = await groupTask;
            if (ticket != _loading) return;
            Busy(null);
            var releases = _rows.Where(r => r.Local is null).Select(r => r.Candidate.ReleaseId).Distinct().Count();
            Status(error is not null && _rows.Count == 0 ? error
                   : $"{_rows.Count} image{(_rows.Count == 1 ? "" : "s")} from {releases} release{(releases == 1 ? "" : "s")}" + (error is not null ? $" · {error}" : ""),
                error is not null ? Tones.WarnText : Tones.OkText);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Busy(null);
            Status(ex.Message, Tones.BadText);
        }
    }

    /// The banner over the candidates while the archive is asked: where the
    /// eye already is (the corner status line alone took a minute to find,
    /// Dave, 2026-10-31). Null hides it.
    private void Busy(string? text)
    {
        BusyBar.IsVisible = text is not null;
        if (text is not null) BusyText.Text = text;
    }

    /// The art the album already has, shown before the archive answers (Dave's
    /// idea, 2026-10-31): cover.jpg and back.jpg beside the tracks, and the
    /// cover embedded in the files. No download, and the starting point for
    /// cropping, resizing or embedding what is already there.
    private void AddLocal()
    {
        var local = new List<ArtRow>();
        void Add(ArtSlot slot, string where, string name, byte[]? bytes)
        {
            if (bytes is null || !ArtImage.LooksLikeAnImage(bytes)) return;
            var row = new ArtRow(new ArtCandidate("", name, Ours: true, slot, "", $"local:{where}:{name}", 0), bytes, where);
            if (Display.Decode(bytes, 340) is { } thumbnail) row.Thumbnail = thumbnail;
            else row.Placeholder = "no preview";
            local.Add(row);
        }
        // Plain files only, no larger than a cover may be (SafeFiles; review B2).
        byte[]? Read(string name) => SafeFiles.Read(Path.Combine(_album.Folder, name), WebLimits.MaxImageBytes);
        if (_album.Cover is { } cover) Add(ArtSlot.Front, "in the folder", cover.Name, Read(cover.Name));
        if (_album.Back is { } back) Add(ArtSlot.Back, "in the folder", back.Name, Read(back.Name));
        if (_album.Tracks.FirstOrDefault(t => t.EmbeddedCover() is not null) is { } track)
            Add(ArtSlot.Front, "in the files", $"embedded in {track.FileName}", EmbeddedBytes(track.Path));
        _rows = local.Concat(_rows).ToList();
        ShowSlot();
    }

    private static byte[]? EmbeddedBytes(string path)
    {
        try
        {
            using var file = TrackReader.Open(path);
            var picture = file.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover && ImageSize.Read(ImageSize.Repair(p.Data.Data)) is not null)
                          ?? file.Tag.Pictures.FirstOrDefault(p => ImageSize.Read(ImageSize.Repair(p.Data.Data)) is not null);
            return picture is null ? null : ImageSize.Repair(picture.Data.Data);
        }
        catch (Exception) { return null; }
    }

    /// The release a search found, when the files carry none, and its group.
    private bool _searched;
    private ReleaseCandidate? _found;
    private string? _foundGroup;

    private async Task<string?> GroupOf(string release)
    {
        using var doc = await _mb.GetReleaseAsync(release, _closing.Token);
        return doc is not null && Lookup.Release(doc.RootElement).ReleaseGroupId is { Length: > 0 } g ? g : null;
    }

    /// One release's images, in as they come: listed at once, thumbnails
    /// started, and for your release the front and back fetched in full.
    private void Arrived(int ticket, IReadOnlyList<ArtCandidate> found)
    {
        if (ticket != _loading || found.Count == 0) return;
        var rows = found.Select(c => new ArtRow(c)).ToList();
        _rows.AddRange(rows);
        // The album's own art, then your release's, then the editions in the order they answered.
        _rows = _rows.OrderByDescending(r => r.Local is not null).ThenByDescending(r => r.Candidate.Ours).ToList();
        var selected = CandidateList.SelectedItem;
        ShowSlot(keepPick: true);
        if (selected is not null) CandidateList.SelectedItem = selected;
        Status($"{_rows.Count} image{(_rows.Count == 1 ? "" : "s")} so far…", Tones.Dim);
        var editions = _rows.Where(r => r.Local is null && !r.Candidate.Ours).Select(r => r.Candidate.ReleaseId).Distinct().Count();
        if (BusyBar.IsVisible)
            Busy(editions == 0 ? (_searched ? "The best match's art is in. Asking about other editions of the album…" : "Your release's art is in. Asking about other editions of the album…")
                               : $"Found art from {editions} other edition{(editions == 1 ? "" : "s")} so far. Still asking…");
        _ = LoadThumbnails(rows);
        foreach (var c in found.Where(c => c.Ours && c.Slot != ArtSlot.Booklet)) Full(c.Image);
    }

    /// Thumbnails, four at a time, each shown as it arrives.
    private async Task LoadThumbnails(IReadOnlyList<ArtRow> rows)
    {
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(rows.Select(async row =>
        {
            try
            {
                await gate.WaitAsync(_closing.Token);
                try
                {
                    var (bytes, _) = await CoverArtArchive.ImageAsync(_http, row.Candidate.Thumbnail, _closing.Token);
                    if (bytes is null) { row.Placeholder = "no preview"; return; }
                    var bitmap = Display.Decode(bytes, 340);
                    if (bitmap is null) { row.Placeholder = "no preview"; return; }
                    await Dispatcher.UIThread.InvokeAsync(() => row.Thumbnail = bitmap);
                }
                finally { gate.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { row.Placeholder = "no preview"; }
        }));
    }

    private Task<(byte[]? Image, string? Error)> Full(string url)
    {
        if (!_full.TryGetValue(url, out var task)) _full[url] = task = CoverArtArchive.ImageAsync(_http, url, _closing.Token);
        return task;
    }

    private async void OnOtherEditions(object? sender, RoutedEventArgs e) => await LoadCandidates();

    private void OnSlot(object? sender, RoutedEventArgs e)
    {
        FrontSlot.IsChecked = ReferenceEquals(sender, FrontSlot);
        BackSlot.IsChecked = ReferenceEquals(sender, BackSlot);
        _picked = null;
        _made = null;
        ShowSlot();
    }

    private void ShowSlot(bool keepPick = false)
    {
        var rows = _rows.Where(r => r.Candidate.Slot == Slot).ToList();
        CandidateList.ItemsSource = rows;
        CandidatesLabel.Text = $"{(Slot == ArtSlot.Front ? "FRONT COVERS" : "BACK COVERS")} · COVER ART ARCHIVE · {rows.Count}";
        SaveBox.Content = Slot == ArtSlot.Front ? "cover.jpg in the album folder" : "back.jpg in the album folder";
        EmbedBox.IsVisible = Slot == ArtSlot.Front;
        EmbedBox.Content = $"Embed in each file as the front cover";
        // Embedding is on when the files already carry a cover: it is then what
        // players show, and a new cover.jpg alone would leave the old one there.
        if (!keepPick) EmbedBox.IsChecked = Slot == ArtSlot.Front && _album.Tracks.Any(t => t.EmbeddedCover() is not null);
        PickView.IsVisible = _picked is not null;
        PickHint.IsVisible = _picked is null;
        Recount();
    }

    // ---- picking -----------------------------------------------------------------

    private async void OnCandidateSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (CandidateList.SelectedItem is not ArtRow row) return;
        PickView.IsVisible = true;
        PickHint.IsVisible = false;
        Preview.Source = row.Thumbnail;
        PreviewWait.Text = "";
        PickInfo.Text = "";
        ShapeNote.Text = "";
        _made = null;
        Recount();
        if (row.Local is { } local)
        {
            Pick(local, $"{row.Where}: {row.Candidate.Edition}");
            return;
        }
        // The thumbnail shows at once; the full image follows (about 1 MB from
        // archive.org, a few seconds) unless it was fetched ahead.
        var full = Full(row.Candidate.Image);
        if (!full.IsCompleted) PickInfo.Text = "fetching the full image…";
        var (bytes, error) = await full;
        if (!ReferenceEquals(CandidateList.SelectedItem, row)) return;
        if (bytes is null)
        {
            PreviewWait.Text = error is null ? "the archive has no such image now" : $"the archive failed: {error}";
            return;
        }
        Pick(bytes, row.Candidate.Ours ? (row.Candidate.Edition.StartsWith("best match") ? "best match by search" : "this pressing") : $"other edition: {row.Candidate.Edition}");
    }

    private async void OnFromFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "An image for the cover",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JPEG or PNG") { Patterns = ["*.jpg", "*.jpeg", "*.png"] }],
        });
        if (files is [var file] && file.TryGetLocalPath() is { } path) PickFile(path);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is { } items && items.FirstOrDefault()?.TryGetLocalPath() is { } path) PickFile(path);
    }

    private void PickFile(string path)
    {
        try
        {
            // Read as a plain file up to the limit: a link's own size said
            // nothing about what it points at (review B2).
            if (!SafeFiles.HasContent(path)) { Status("not a file that can be read", Tones.WarnText); return; }
            var bytes = SafeFiles.Read(path, WebLimits.MaxImageBytes) ?? ReadPicked(path);
            if (bytes is null) { Status("that file is too large to be a cover", Tones.WarnText); return; }
            if (!ArtImage.LooksLikeAnImage(bytes)) { Status("not a JPEG or PNG", Tones.WarnText); return; }
            CandidateList.SelectedItem = null;
            PickView.IsVisible = true;
            PickHint.IsVisible = false;
            Pick(bytes, $"your file: {Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status(ex.Message, Tones.BadText); }
    }

    /// A file picked by hand from anywhere (a link to one included), read up
    /// to the limit; null when larger.
    private static byte[]? ReadPicked(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[WebLimits.MaxImageBytes + 1];
        var n = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return n > WebLimits.MaxImageBytes ? null : buffer[..n];
    }

    private void Pick(byte[] bytes, string from)
    {
        _picked = bytes;
        _pickedFrom = from;
        PreviewWait.Text = "";
        Preview.Source = Display.Decode(bytes, 1600);
        if (Preview.Source is null) PreviewWait.Text = "cannot show this image, or it is too large";
        var size = ImageSize.Read(bytes);
        PickInfo.Text = $"{(size is { } s ? $"{s.Width}×{s.Height}" : "unknown size")} · {(ArtImage.IsJpeg(bytes) ? "JPEG" : "PNG")} · {TrackReader.Bytes(bytes.Length)}";
        if (size is { } sz)
        {
            var ratio = (double)sz.Width / sz.Height;
            var square = AlbumReader.NearlySquare(sz.Width, sz.Height);
            ShapeNote.Text = square ? "square" : $"{ratio:0.00} : 1{(Slot == ArtSlot.Back ? " (CD inlay)" : "")}";
            ShapeNote.Foreground = square || Slot == ArtSlot.Back ? Tones.Dim : Tones.WarnText;
        }
        _ = Make();
    }

    private void OnOption(object? sender, RoutedEventArgs e) => _ = Make();

    /// The image as it would be saved, made off the UI thread whenever an
    /// option changes; the latest request wins.
    private async Task Make()
    {
        if (_picked is not { } picked) { Recount(); return; }
        var ticket = ++_making;
        var shape = CropSquare.IsChecked == true ? ArtShape.CropSquare : PadSquare.IsChecked == true ? ArtShape.PadSquare : ArtShape.AsPrinted;
        int? max = SizeBox.SelectedIndex switch { 1 => 1500, 2 => 1200, 3 => 1000, _ => null };
        int? quality = QualityBox.SelectedIndex switch { 1 => 92, 2 => 85, _ => null };
        var strip = StripBox.IsChecked == true;
        var made = await Task.Run(() => ArtImage.Process(picked, shape, max, quality, strip));
        if (ticket != _making) return;
        _made = made;
        if (made is null) Status("this image cannot be read or is too large to decode", Tones.BadText);
        Recount();
    }

    private IReadOnlyList<Change> Changes()
    {
        if (_made is not { } made) return [];
        var list = new List<Change>();
        var name = Slot == ArtSlot.Front ? "cover.jpg" : "back.jpg";
        var existing = Path.Combine(_album.Folder, name);
        var same = SafeFiles.Read(existing, WebLimits.MaxImageBytes) is { } now && now.AsSpan().SequenceEqual(made);
        if (SaveBox.IsChecked == true && !same) list.Add(Pending.SaveImage(_album, name, made));
        if (Slot == ArtSlot.Front && EmbedBox.IsChecked == true) list.AddRange(Pending.EmbedFront(_album, made));
        // The IDs of the release a search surely found, with the art.
        if (list.Count > 0 && TakeIdsBox.IsVisible && TakeIdsBox.IsChecked == true && _found is { } found)
        {
            list.AddRange(Pending.SetField(_album, FieldMap.MusicBrainzRelease, found.Id));
            if (_foundGroup is { } g) list.AddRange(Pending.SetField(_album, FieldMap.MusicBrainzReleaseGroup, g));
        }
        return list;
    }

    private void Recount()
    {
        var changes = Changes();
        AddButton.IsEnabled = changes.Count > 0;
        AddButton.Content = changes.Count == 0 ? "Add to Pending" : $"Add {changes.Count} change{(changes.Count == 1 ? "" : "s")} to Pending";
        if (_made is null) { SummaryBox.IsVisible = false; return; }
        var lines = new List<string>();
        var image = changes.FirstOrDefault(c => c.Op == ChangeOp.WriteImage);
        if (image is not null) lines.Add($"{(image.Kind == ChangeKind.Add ? "+ add" : "~ change")} {image.Key} · {image.To}");
        var embeds = changes.Count(c => c.Op == ChangeOp.EmbedFront);
        if (embeds > 0) lines.Add($"~ embed in {embeds} file{(embeds == 1 ? "" : "s")} · {Pending.Describe(_made)}");
        lines.Add(_pickedFrom);
        SummaryText.Text = string.Join("\n", lines);
        var kind = image?.Kind == ChangeKind.Add && embeds == 0 ? ChangeKind.Add : ChangeKind.Change;
        var (ink, fill) = PendingRow.Colours(kind);
        SummaryBox.IsVisible = changes.Count > 0;
        SummaryBox.Background = fill;
        SummaryBox.BorderBrush = ink;
        SummaryText.Foreground = ink;
    }

    private void OnAdd(object? sender, RoutedEventArgs e) => Close(Changes().ToList());
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void Status(string text, IBrush ink)
    {
        StatusText.Text = text;
        StatusDot.Foreground = ink;
    }
}

public sealed class ArtRow(ArtCandidate candidate, byte[]? local = null, string? where = null) : INotifyPropertyChanged
{
    public ArtCandidate Candidate { get; } = candidate;

    /// The bytes of art the album already has ("in the folder", "in the
    /// files"); null for the archive's, which are downloaded when picked.
    public byte[]? Local { get; } = local;
    public string? Where { get; } = where;

    public string Edition => Candidate.Edition;
    public string What => Local is { } b ? Pending.Describe(b)
        : Candidate.Slot == ArtSlot.Booklet ? $"booklet page {Candidate.Page}" : Candidate.Slot.ToString().ToLowerInvariant();
    public string Source => Where ?? (Candidate.Ours ? (Candidate.Edition.StartsWith("best match") ? "best match" : "this pressing") : "other edition");
    public IBrush SourceInk => Local is not null ? Tones.NoteText : Candidate.Ours ? Tones.OkText : Tones.WarnText;
    public IBrush SourceFill => Local is not null ? Tones.NoteFill : Candidate.Ours ? Tones.OkFill : Tones.WarnFill;

    public Bitmap? Thumbnail
    {
        get;
        set { field = value; PropertyChanged?.Invoke(this, new(nameof(Thumbnail))); Placeholder = ""; }
    }

    public string Placeholder
    {
        get;
        set { field = value; PropertyChanged?.Invoke(this, new(nameof(Placeholder))); PropertyChanged?.Invoke(this, new(nameof(Spoken))); }
    } = "loading…";

    public string Spoken => Speech.Join(Edition, What, Source, Placeholder);

    public event PropertyChangedEventHandler? PropertyChanged;
}
