using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Marginal.Core;

namespace Marginal.App;

/// The MusicBrainz lookup for one album. Closes with the ticked changes, or
/// null when cancelled. Asks MusicBrainz only on Search and when a release is
/// picked, one request a second (MusicBrainzClient).
public sealed partial class LookupWindow : Window
{
    private readonly Album _album;
    private readonly MusicBrainzClient _mb = new();
    private readonly Dictionary<string, MbRelease> _releases = [];
    private CancellationTokenSource? _busy;
    private MbRelease? _release;
    private IReadOnlyList<Pair> _pairs = [];

    /// Whose checklist is on screen: ticks carry over a year change, never
    /// to another release (2026-10-31: a release with nothing to change left
    /// every box unticked for the next one picked).
    private string? _takeFor;

    /// For the previewer only.
    public LookupWindow() : this(AlbumReader.Read(Path.GetTempPath(), [])) { }

    public LookupWindow(Album album)
    {
        _album = album;
        InitializeComponent();
        AlbumLine.Text = $"{album.Artist} — {album.Title} · {album.Tracks.Count} files · {Length(album.Length)}";
        // A release ID already in the files is the first thing to look at.
        var tagged = album.Fields.First(f => f.Field == FieldMap.MusicBrainzRelease).Common;
        QueryBox.Text = Lookup.Query(album);
        Opened += async (_, _) => await Search(QueryBox.Text, tagged);
        Closed += (_, _) => { _busy?.Cancel(); _mb.Dispose(); };
    }

    private async void OnSearch(object? sender, RoutedEventArgs e) => await Search(QueryBox.Text, null);

    private async void OnQueryKey(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter) return;
        e.Handled = true;
        await Search(QueryBox.Text, null);
    }

    /// A pasted link or ID goes straight to that release; anything else is a
    /// search. `tagged`: also fetch the release the files carry, if the search
    /// does not bring it.
    private async Task Search(string? text, string? tagged)
    {
        var cancel = Restart();
        Status("searching MusicBrainz…", Tones.Dim);
        Busy(Lookup.ReleaseIdFrom(text) is not null && !(text ?? "").Contains("reid:", StringComparison.OrdinalIgnoreCase)
            ? "Reading that release from MusicBrainz…" : "Searching MusicBrainz…");
        try
        {
            var candidates = new List<ReleaseCandidate>();
            // A link or a bare ID, not a search that happens to hold one ("reid:…").
            if (Lookup.ReleaseIdFrom(text) is { } id && !(text ?? "").Contains("reid:", StringComparison.OrdinalIgnoreCase))
            {
                if (await Fetch(id, cancel.Token) is { } r) candidates.Add(AsCandidate(r, tagged: false));
            }
            else
            {
                using var search = await _mb.SearchReleasesAsync(text ?? "", 25, cancel.Token);
                if (search is not null) candidates.AddRange(Lookup.Candidates(search.RootElement, _album));
                if (tagged is not null && candidates.All(c => c.Id != tagged) && await Fetch(tagged, cancel.Token) is { } r)
                    candidates.Insert(0, AsCandidate(r, tagged: true));
            }
            if (cancel.IsCancellationRequested) return;
            Busy(null);
            var rows = candidates.Select(c => new CandidateRow(c, _album.Tracks.Count)).ToList();
            ReleaseList.ItemsSource = rows;
            ReleasesLabel.Text = $"RELEASES · {rows.Count}";
            Status(rows.Count == 0 ? "nothing found: try fewer words, or paste a link" : $"found {rows.Count} release{(rows.Count == 1 ? "" : "s")}",
                rows.Count == 0 ? Tones.WarnText : Tones.OkText);
            if (rows.Count > 0) ReleaseList.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Busy(null);
            Status($"MusicBrainz: {ex.Message}", Tones.BadText);
        }
    }

    /// The banner over the release list while MusicBrainz is asked: where the
    /// eye already is, not only the corner status line. Null hides it.
    private void Busy(string? text)
    {
        BusyBar.IsVisible = text is not null;
        if (text is not null) BusyText.Text = text;
    }

    private ReleaseCandidate AsCandidate(MbRelease r, bool tagged) =>
        new(r.Id, r.Title, r.Artist, r.Date, r.Country, r.Formats, r.Tracks.Count, r.Label, r.CatalogNumber, r.Barcode, 100,
            tagged || string.Equals(r.Id, _album.Fields.First(f => f.Field == FieldMap.MusicBrainzRelease).Common, StringComparison.OrdinalIgnoreCase));

    private async Task<MbRelease?> Fetch(string id, CancellationToken ct)
    {
        if (_releases.TryGetValue(id, out var known)) return known;
        using var doc = await _mb.GetReleaseAsync(id, ct);
        if (doc is null) return null;
        var release = Lookup.Release(doc.RootElement);
        _releases[id] = release;
        return release;
    }

    private async void OnReleaseSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ReleaseList.SelectedItem is not CandidateRow row) return;
        var cancel = Restart();
        Status($"reading the release…", Tones.Dim);
        try
        {
            var release = await Fetch(row.Candidate.Id, cancel.Token);
            if (cancel.IsCancellationRequested) return;
            if (release is null) { Status("MusicBrainz no longer has that release", Tones.WarnText); return; }
            _release = release;
            _pairs = Lookup.Pair(_album, release);
            row.Fit = Lookup.FitPercent(_pairs);
            ShowRelease();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status($"MusicBrainz: {ex.Message}", Tones.BadText);
        }
    }

    private void ShowRelease()
    {
        if (_release is not { } r) return;
        PickHint.IsVisible = false;
        ReleaseView.IsVisible = true;
        ReleaseTitle.Text = string.Join(" · ", new[] { r.Title, r.Formats, r.Date.Length >= 4 ? r.Date[..4] : "" }.Where(s => s.Length > 0));
        ReleaseCodes.Text = string.Join(" · ", new[] { r.Label, r.CatalogNumber, r.Barcode, r.Country, $"{r.Tracks.Count} tracks" }.Where(s => s.Length > 0));
        ReleaseArt.Text = r.ArtFront ? (r.ArtBack ? "art: front + back" : "art: front only") : "art: none";
        ReleaseArt.Foreground = r.ArtFront ? (r.ArtBack ? Tones.OkText : Tones.WarnText) : Tones.BadText;
        PairList.ItemsSource = _pairs.Select(p => new PairRow(p)).ToList();

        var year = r.FirstReleaseDate.Length > 0 ? r.FirstReleaseDate : r.Date;
        FirstReleaseRadio.Content = $"The album's first release: {(year.Length > 0 ? year : "unknown")}";
        PressingRadio.Content = $"This pressing: {(r.Date.Length > 0 ? r.Date : "unknown")}";
        ShowTake();
    }

    private void OnYearChanged(object? sender, RoutedEventArgs e) => ShowTake();

    /// The checklist, keeping what was ticked or unticked across a year change.
    private void ShowTake()
    {
        if (_release is null) return;
        var before = _takeFor == _release.Id ? (TakeList.ItemsSource as IEnumerable<TakeRow>)?.ToDictionary(t => t.Index, t => t.On) : null;
        _takeFor = _release.Id;
        var items = Lookup.Take(_album, _release, _pairs, FirstReleaseRadio.IsChecked == true);
        // A release that fits badly starts with nothing ticked: the 2-CD
        // "×2: Facelift / Alice in Chains" fits a Facelift rip at 50%, and its
        // year is the compilation's (2026-10-31). Ticking by hand still works.
        var trusted = Lookup.FitPercent(_pairs) >= GoodFit;
        var rows = items.Select((item, i) => new TakeRow(item, i,
            before is not null && before.TryGetValue(i, out var on) ? on && item.Changes.Count > 0 : item.On && trusted)).ToList();
        foreach (var row in rows) row.PropertyChanged += (_, _) => Count();
        TakeList.ItemsSource = rows;
        Count();
    }

    /// Below this fit nothing starts ticked.
    private const int GoodFit = 90;

    private void Count()
    {
        var rows = (TakeList.ItemsSource as IEnumerable<TakeRow>)?.ToList() ?? [];
        var chosen = rows.Where(r => r.On).SelectMany(r => r.Item.Changes).ToList();
        int K(ChangeKind k) => chosen.Count(c => c.Kind == k);
        TakeSummary.Text = $"+{K(ChangeKind.Add)} add  ~{K(ChangeKind.Change)} change  {rows.Count(r => !r.HasChanges)} same";
        AddButton.Content = chosen.Count == 0 ? "Add to Pending" : $"Add {chosen.Count} change{(chosen.Count == 1 ? "" : "s")} to Pending";
        AddButton.IsEnabled = chosen.Count > 0;
        var paired = _pairs.Count(p => p is { File: not null, Track: not null });
        var fit = Lookup.FitPercent(_pairs);
        if (fit < GoodFit && chosen.Count == 0)
            Status($"{_release?.Title} fits {fit}%: nothing ticked; tick by hand only if you are sure", Tones.WarnText);
        else
            Status($"{_release?.Title} selected · {paired} of {_album.Tracks.Count} files paired · {chosen.Count} changes chosen",
                fit < GoodFit ? Tones.WarnText : Tones.OkText);
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        var rows = (TakeList.ItemsSource as IEnumerable<TakeRow>)?.ToList() ?? [];
        Close(rows.Where(r => r.On).SelectMany(r => r.Item.Changes).ToList());
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private CancellationTokenSource Restart()
    {
        _busy?.Cancel();
        return _busy = new CancellationTokenSource();
    }

    private void Status(string text, IBrush ink)
    {
        StatusText.Text = text;
        StatusDot.Foreground = ink;
    }

    private static string Length(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}

public sealed class CandidateRow(ReleaseCandidate candidate, int files) : INotifyPropertyChanged
{
    public ReleaseCandidate Candidate { get; } = candidate;
    public string Title => Candidate.Title;
    public string Line => Candidate.Line;
    public string Codes => string.Join(" · ", new[] { Candidate.CatalogNumber, Candidate.Barcode }.Where(s => s.Length > 0)) is { Length: > 0 } c ? c : "no catalog number";
    public string TracksText => $"{files} of {Candidate.Tracks} tracks";
    public IBrush TracksInk => Candidate.Tracks == files ? Tones.OkText : Tones.WarnText;
    public IBrush TracksFill => Candidate.Tracks == files ? Tones.OkFill : Tones.WarnFill;

    /// The fit once the release has been read: blank until then.
    public int? Fit
    {
        get;
        set { field = value; PropertyChanged?.Invoke(this, new(nameof(FitText))); PropertyChanged?.Invoke(this, new(nameof(FitInk))); }
    }

    public string FitText => Fit is { } f ? $"{f}%" : "";
    public IBrush FitInk => Fit switch { >= 90 => Tones.OkText, >= 60 => Tones.WarnText, _ => Tones.BadText };

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record PairRow(Pair Pair)
{
    public string File => Pair.File?.FileName ?? "—";
    public string Track => Pair.Track is { } t ? (t.DiscCount > 1 ? $"{t.Disc}.{t.Number} " : "") + t.Title : "—";
    public string Yours => Pair.File is { } f ? f.Duration.ToString(@"m\:ss") : "";
    public string Theirs => Pair.Track?.LengthMs is { } ms ? TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss") : "";
    public string Fit => Pair.Fit;
    public IBrush Ink => Tones.Of(Pair.Tone).Text;
    public IBrush Fill => Tones.Of(Pair.Tone).Fill;
}

public sealed class TakeRow(TakeItem item, int index, bool on) : INotifyPropertyChanged
{
    public TakeItem Item { get; } = item;
    public int Index { get; } = index;
    public string Label => Item.Label;
    public string Now => Item.Now;
    public string Theirs => Item.Theirs;
    public bool HasChanges => Item.Changes.Count > 0;
    public string KindText => Item.Kind switch { ChangeKind.Add => "+ add", ChangeKind.Change => "~ change", ChangeKind.Remove => "− remove", _ => "same" };
    public IBrush Ink => Item.Kind is { } k ? PendingRow.Colours(k).Ink : Tones.NoteText;
    public IBrush Fill => Item.Kind is { } k ? PendingRow.Colours(k).Fill : Tones.NoteFill;
    public IBrush Edge => On && Item.Kind is { } k ? PendingRow.Colours(k).Ink : Avalonia.Media.Brushes.Transparent;

    public bool On
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new(nameof(On)));
            PropertyChanged?.Invoke(this, new(nameof(Edge)));
        }
    } = on;

    public event PropertyChangedEventHandler? PropertyChanged;
}
