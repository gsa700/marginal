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

    public ArtWindow(Album album, ArtSlot slot)
    {
        _album = album;
        InitializeComponent();
        AlbumLine.Text = $"{album.Artist} — {album.Title}";
        FrontSlot.Content = $"Front  {(album.Cover is { } c ? $"have {c.Size}" : album.Tracks.Any(t => t.EmbeddedCover() is not null) ? "embedded only" : "missing")}";
        BackSlot.Content = $"Back  {(album.Back is { } b ? $"have {b.Size}" : "missing")}";
        FrontSlot.IsChecked = slot == ArtSlot.Front;
        BackSlot.IsChecked = slot == ArtSlot.Back;
        AddHandler(DragDrop.DropEvent, OnDrop);
        Opened += async (_, _) => await LoadCandidates();
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
        _rows = [];
        ShowSlot();
        if (release is null || !WebLimits.IsMbid(release))
        {
            NoneText.Text = "These files carry no MusicBrainz release, so the archive cannot be asked. "
                            + "Look up on MusicBrainz first, or use From a file… (or drop one here).";
            NoneText.IsVisible = true;
            Status("no MusicBrainz release in the tags", Tones.WarnText);
            return;
        }
        Status("asking the Cover Art Archive…", Tones.Dim);
        try
        {
            // The release group, for other editions: from the tags, or asked of
            // MusicBrainz alongside the archive.
            var others = OtherEditions.IsChecked == true;
            var groupTask = group is not null || !others ? Task.FromResult(group) : GroupOf(release);
            var error = await CoverArtArchive.CandidatesAsync(_http, _mb, release, groupTask, others,
                found => Dispatcher.UIThread.Post(() => Arrived(ticket, found)), _closing.Token);
            if (ticket != _loading) return;
            var releases = _rows.Select(r => r.Candidate.ReleaseId).Distinct().Count();
            Status(error is not null && _rows.Count == 0 ? error
                   : $"{_rows.Count} image{(_rows.Count == 1 ? "" : "s")} from {releases} release{(releases == 1 ? "" : "s")}" + (error is not null ? $" · {error}" : ""),
                error is not null ? Tones.WarnText : Tones.OkText);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is MusicBrainzException or HttpRequestException)
        {
            Status(ex.Message, Tones.BadText);
        }
    }

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
        // Yours first, then the editions in the order they answered.
        _rows = _rows.OrderByDescending(r => r.Candidate.Ours).ToList();
        var selected = CandidateList.SelectedItem;
        ShowSlot(keepPick: true);
        if (selected is not null) CandidateList.SelectedItem = selected;
        Status($"{_rows.Count} image{(_rows.Count == 1 ? "" : "s")} so far…", Tones.Dim);
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
                    using var stream = new MemoryStream(bytes);
                    var bitmap = new Bitmap(stream);
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
        Pick(bytes, row.Candidate.Ours ? "this pressing" : $"other edition: {row.Candidate.Edition}");
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
            var info = new FileInfo(path);
            if (info.Length > WebLimits.MaxImageBytes) { Status("that file is too large to be a cover", Tones.WarnText); return; }
            var bytes = File.ReadAllBytes(path);
            if (!ArtImage.LooksLikeAnImage(bytes)) { Status("not a JPEG or PNG", Tones.WarnText); return; }
            CandidateList.SelectedItem = null;
            PickView.IsVisible = true;
            PickHint.IsVisible = false;
            Pick(bytes, $"your file: {Path.GetFileName(path)}");
        }
        catch (IOException ex) { Status(ex.Message, Tones.BadText); }
    }

    private void Pick(byte[] bytes, string from)
    {
        _picked = bytes;
        _pickedFrom = from;
        PreviewWait.Text = "";
        using (var stream = new MemoryStream(bytes))
            try { Preview.Source = new Bitmap(stream); } catch (Exception) { PreviewWait.Text = "cannot show this image"; }
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
        if (SaveBox.IsChecked == true) list.Add(Pending.SaveImage(_album, Slot == ArtSlot.Front ? "cover.jpg" : "back.jpg", made));
        if (Slot == ArtSlot.Front && EmbedBox.IsChecked == true) list.AddRange(Pending.EmbedFront(_album, made));
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

public sealed class ArtRow(ArtCandidate candidate) : INotifyPropertyChanged
{
    public ArtCandidate Candidate { get; } = candidate;
    public string Edition => Candidate.Edition;
    public string What => Candidate.Slot == ArtSlot.Booklet ? $"booklet page {Candidate.Page}" : Candidate.Slot.ToString().ToLowerInvariant();
    public string Source => Candidate.Ours ? "this pressing" : "other edition";
    public IBrush SourceInk => Candidate.Ours ? Tones.OkText : Tones.WarnText;
    public IBrush SourceFill => Candidate.Ours ? Tones.OkFill : Tones.WarnFill;

    public Bitmap? Thumbnail
    {
        get;
        set { field = value; PropertyChanged?.Invoke(this, new(nameof(Thumbnail))); Placeholder = ""; }
    }

    public string Placeholder
    {
        get;
        set { field = value; PropertyChanged?.Invoke(this, new(nameof(Placeholder))); }
    } = "loading…";

    public event PropertyChangedEventHandler? PropertyChanged;
}
