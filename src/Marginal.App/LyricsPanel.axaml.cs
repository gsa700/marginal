using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Marginal.Core;
using Marginal.Playback;

namespace Marginal.App;

/// The Lyrics tab. Shows one album's tracks with what lyrics each has, edits
/// them, and asks LRCLIB; whatever is used is handed to the main window as
/// changes for the album's Pending (Changes), and Write is asked of it
/// (WriteAsked). Nothing is written here.
public sealed partial class LyricsPanel : UserControl
{
    private Album? _album;
    private TrackFile? _track;
    private List<LyricsRow> _rows = [];
    private readonly Dictionary<string, IReadOnlyList<LyricsFit>> _results = [];
    private LrcLibClient? _lrclib;
    private CancellationTokenSource? _busy;
    private string _loaded = "";
    private bool _settingText;

    // Playing the track: AlbumWall's engine (Hymie), one track at a time.
    private Player? _player;
    private string? _playing;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _movingSlider;
    private int _tapLine = -1;
    private Lrc.Timeline? _timeline;
    private string? _timelineText;

    /// The lines stamped since Tap to time was turned on, newest last, and the
    /// time each had before (null: none), so Back a line can undo them in turn.
    private readonly Stack<(int Line, TimeSpan? Before)> _tapped = new();

    /// Changes to add to the album's Pending.
    public event Action<IReadOnlyList<Change>>? Changes;

    /// Write was pressed on this tab.
    public event Action? WriteAsked;

    public LyricsPanel()
    {
        InitializeComponent();
        SyncedView.IsChecked = true;
        _clock.Tick += (_, _) => Tick();
        // Space stamps a line while Tap to time is on, before the editor sees
        // it; its release is swallowed too, or a focused button (Play) takes
        // it as a click and pauses (2026-10-31).
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        if (!Player.IsAvailable)
        {
            PlayButton.IsEnabled = false;
            TapButton.IsEnabled = false;
            NowLine.Text = "no audio output on this machine, so tracks cannot be played here";
        }
        DetachedFromVisualTree += (_, _) => StopPlaying();
    }

    /// The album to show, or null. Searches already made for it are kept.
    public void Show(Album? album)
    {
        var same = album?.Folder == _album?.Folder;
        _album = album;
        if (!same) { _results.Clear(); _busy?.Cancel(); Busy(null); }
        var selected = _track?.Path;
        _rows = album?.Tracks.Select(t => new LyricsRow(t)).ToList() ?? [];
        foreach (var row in _rows)
            if (_results.TryGetValue(row.Track.Path, out var fits)) row.Found = FoundText(row.Track, fits);
        TrackList.ItemsSource = _rows;
        TrackCounts.Text = album is null ? "" : $"{_rows.Count(r => r.Track.Lyrics == LyricsKind.Synced)} synced · {_rows.Count(r => r.Track.Lyrics == LyricsKind.Plain)} plain · {_rows.Count(r => r.Track.Lyrics == LyricsKind.None)} none";
        FindAllButton.IsEnabled = album is not null;
        TrackList.SelectedItem = _rows.FirstOrDefault(r => r.Track.Path == selected) ?? _rows.FirstOrDefault();
        if (_rows.Count == 0) ShowTrack(null);
    }

    /// How many changes the album has waiting, for the bar under the editor.
    public void ShowPending(int changes, int files, bool canWrite)
    {
        PendingBar.IsVisible = changes > 0;
        PendingText.Text = $"Pending for this album: {changes} change{(changes == 1 ? "" : "s")} in {files} file{(files == 1 ? "" : "s")}. Review them on Tags & art.";
        WriteButton.Content = $"Write {files} file{(files == 1 ? "" : "s")}";
        WriteButton.IsEnabled = canWrite;
    }

    private void OnWrite(object? sender, RoutedEventArgs e) => WriteAsked?.Invoke();

    // ---- one track -------------------------------------------------------------------

    private void OnTrackSelected(object? sender, SelectionChangedEventArgs e) => ShowTrack((TrackList.SelectedItem as LyricsRow)?.Track);

    private void ShowTrack(TrackFile? track)
    {
        if (track?.Path != _playing) StopPlaying();
        _track = track;
        Editor.IsEnabled = track is not null;
        if (track is null)
        {
            TrackTitle.Text = "";
            TrackLine.Text = "";
            SetEditor("");
            ResultList.ItemsSource = null;
            FoundNote.Text = "";
            return;
        }
        TrackTitle.Text = track.Get(FieldMap.Title) ?? track.FileName;
        var n = track.Get(FieldMap.Track)?.Split('/')[0] ?? "";
        var kind = track.Lyrics switch
        {
            LyricsKind.Synced => File.Exists(track.LrcPath) ? "synced lyrics (.lrc beside it)" : "synced lyrics in the tag",
            LyricsKind.Plain => "plain lyrics, no times",
            _ => "no lyrics",
        };
        TrackLine.Text = string.Join(" · ", new[] { n, track.Duration.ToString(@"m\:ss"), kind }.Where(s => s.Length > 0));
        _loaded = Current(track);
        ShowView();
        ShowResults();
    }

    /// The track's lyrics as they are: the .lrc beside it when there is one
    /// (it is where times are kept), else the tag.
    private static string Current(TrackFile track)
    {
        try
        {
            if (File.Exists(track.LrcPath)) return Lrc.Tidy(File.ReadAllText(track.LrcPath));
        }
        catch (IOException) { }
        return Lrc.Tidy(track.Get(FieldMap.Lyrics));
    }

    private void OnView(object? sender, RoutedEventArgs e)
    {
        var synced = ReferenceEquals(sender, SyncedView);
        // What was typed is kept: a switch to Plain shows it without times.
        if (!synced && SyncedView.IsChecked == false) { PlainView.IsChecked = true; return; }
        _loaded = Editor.Text ?? _loaded;
        SyncedView.IsChecked = synced;
        PlainView.IsChecked = !synced;
        ShowView();
    }

    // ---- playing and timing --------------------------------------------------------------

    private void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (_track is null || !Player.IsAvailable) return;
        _player ??= new Player(GainMode.Off);
        if (_playing != _track.Path)
        {
            _playing = _track.Path;
            _player.Play([_track.Path]);
            _clock.Start();
        }
        else _player.TogglePause();
        Tick();
    }

    /// The engine closed for good (the window is going).
    public void Shutdown()
    {
        StopPlaying();
        _player?.Dispose();
        _player = null;
    }

    private void StopPlaying()
    {
        _player?.Stop();
        _playing = null;
        _clock.Stop();
        SetTap(false);
        PlayGlyph.Text = "▶";
        PositionText.Text = "0:00.0";
        NowLine.Text = Player.IsAvailable ? "" : NowLine.Text;
        _movingSlider = true;
        PositionSlider.Value = 0;
        _movingSlider = false;
    }

    private void Tick()
    {
        if (_player is null || _playing is null) return;
        var position = _player.Position;
        var duration = _player.Duration;
        PlayGlyph.Text = _player.IsPlaying ? "⏸" : "▶";
        PositionText.Text = Clock(position);
        DurationText.Text = Clock(duration);
        _movingSlider = true;
        PositionSlider.Maximum = Math.Max(1, duration.TotalSeconds);
        PositionSlider.Value = Math.Min(position.TotalSeconds, PositionSlider.Maximum);
        _movingSlider = false;
        // The line being sung, from the editor as it is now (read again only
        // when the text has changed).
        if (SyncedView.IsChecked == true)
        {
            var text = Editor.Text ?? "";
            if (_timeline is null || !ReferenceEquals(_timelineText, text)) { _timeline = Lrc.TimelineOf(text); _timelineText = text; }
            var lines = _timeline.Lines;
            var i = _timeline.LineAt(position);
            NowLine.Text = TapButton.IsChecked == true
                ? (_tapLine >= 0 && _tapLine < lines.Length ? $"Space stamps: {Lrc.Words(lines[_tapLine])}" : "every line is timed")
                : i >= 0 ? $"now: {Lrc.Words(lines[i])}" : "";
        }
    }

    private void OnSeek(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_movingSlider || _player is null || _playing is null) return;
        _player.Seek(TimeSpan.FromSeconds(e.NewValue));
    }

    /// Tap to time: the editor is held still, and each Space puts the playing
    /// position on the next line that takes a time, from the line the cursor
    /// is on (or the first).
    private void OnTap(object? sender, RoutedEventArgs e) => SetTap(TapButton.IsChecked == true);

    private void SetTap(bool on)
    {
        TapButton.IsChecked = on;
        _tapped.Clear();
        BackLineButton.IsEnabled = false;
        if (on)
        {
            if (SyncedView.IsChecked != true) { SyncedView.IsChecked = true; PlainView.IsChecked = false; }
            // From the first line with no time yet (or the first line when all
            // have one), not from the cursor: after a paste the cursor is at
            // the end, and only the last line would have been stamped
            // (2026-10-31). The cursor goes there, to show where Space starts.
            var text = Editor.Text ?? "";
            var lines = Lrc.Lines(text);
            var first = Lrc.NextTimeable(text, -1);
            var untimed = first;
            while (untimed >= 0 && Lrc.TimeOf(lines[untimed]) is not null) untimed = Lrc.NextTimeable(text, untimed);
            _tapLine = untimed >= 0 ? untimed : first;
            if (_tapLine >= 0) SetEdited(text, caretOnLine: _tapLine);
            if (_playing is null) OnPlay(this, new RoutedEventArgs());
            Editor.IsReadOnly = true;
            // Off the buttons, so nothing else answers Space.
            Editor.Focus();
            EditorNote.Text = "Tap to time: Space as each line begins · Backspace steps back a line · click Tap to time to stop";
        }
        else
        {
            _tapLine = -1;
            Editor.IsReadOnly = false;
            if (_track is not null) EditorNote.Text = "LRC: one [mm:ss.xx] time per line";
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (TapButton.IsChecked != true || _player is null || _playing is null) return;
        if (e.Key == Key.Back) { e.Handled = true; BackLine(); return; }
        if (e.Key != Key.Space) return;
        e.Handled = true;
        if (_tapLine < 0) return;
        _tapped.Push((_tapLine, Lrc.TimeOf(Lrc.Lines(Editor.Text)[_tapLine])));
        BackLineButton.IsEnabled = true;
        // The stamp is written as the file will be read: a lyric with an
        // [offset:] has it taken off every time when played.
        var text = Lrc.SetTime(Editor.Text, _tapLine, _player.Position + Lrc.Offset(Editor.Text));
        var stamped = _tapLine;
        _tapLine = Lrc.NextTimeable(text, _tapLine);
        SetEdited(text, caretOnLine: _tapLine >= 0 ? _tapLine : stamped);
        if (_tapLine < 0) SetTap(false);
    }

    private void OnBackLine(object? sender, RoutedEventArgs e)
    {
        BackLine();
        Editor.Focus();
    }

    /// Undoes the last stamp: the line gets back what it had, Space will stamp
    /// it next, and playback goes 3 s before where it was stamped, to hear it
    /// coming again (a one-line slip had no way back, 2026-10-31).
    private void BackLine()
    {
        if (_tapped.Count == 0 || _player is null) return;
        var (line, before) = _tapped.Pop();
        var text = Editor.Text ?? "";
        var stampedAt = Lrc.TimeOf(Lrc.Lines(text)[line]);
        text = before is { } b ? Lrc.SetTime(text, line, b) : Lrc.ClearTime(text, line);
        _tapLine = line;
        SetEdited(text, caretOnLine: line);
        if (stampedAt - Lrc.Offset(text) is { } t) _player.Seek(t > TimeSpan.FromSeconds(3) ? t - TimeSpan.FromSeconds(3) : TimeSpan.Zero);
        BackLineButton.IsEnabled = _tapped.Count > 0;
        Tick();
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Back && TapButton.IsChecked == true) e.Handled = true;
    }

    private void OnNudgeBack(object? sender, RoutedEventArgs e) => Nudge(-0.1);
    private void OnNudgeOn(object? sender, RoutedEventArgs e) => Nudge(+0.1);

    /// The line the cursor is on (or the one just stamped) moved a tenth of a second.
    private void Nudge(double seconds)
    {
        var line = TapButton.IsChecked == true && _tapLine > 0 ? _tapLine - 1 : CaretLine();
        SetEdited(Lrc.Nudge(Editor.Text, line, TimeSpan.FromSeconds(seconds)), caretOnLine: line);
    }

    private void OnShift(object? sender, RoutedEventArgs e)
    {
        var by = (double)(ShiftBy.Value ?? 0);
        ShiftButton.Flyout?.Hide();
        if (by == 0) return;
        SetEdited(Lrc.ShiftAll(Editor.Text, TimeSpan.FromSeconds(by)), caretOnLine: CaretLine());
    }

    /// New editor text from a timing action: counted as an edit, the cursor
    /// put on a line so the next nudge knows where it is.
    private void SetEdited(string text, int caretOnLine)
    {
        Editor.Text = text;
        var lines = Lrc.Lines(text);
        var caret = 0;
        for (var i = 0; i < Math.Min(caretOnLine, lines.Length); i++) caret += lines[i].Length + 1;
        Editor.CaretIndex = Math.Min(caret, text.Length);
        UseButton.IsEnabled = Lrc.Tidy(text) != Lrc.Tidy(_loaded);
    }

    private int CaretLine()
    {
        var text = Editor.Text ?? "";
        var caret = Math.Min(Editor.CaretIndex, text.Length);
        return text[..caret].Count(c => c == '\n');
    }

    private static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");

    private void ShowView()
    {
        var synced = SyncedView.IsChecked == true;
        var hasTimes = TrackReader.KindOf(_loaded) == LyricsKind.Synced;
        SetEditor(synced ? _loaded : Lrc.Plain(_loaded));
        Editor.IsReadOnly = !synced && hasTimes;
        EditorNote.Text = !synced && hasTimes ? "the words alone; edit in Synced to keep the times"
            : synced && !hasTimes && _loaded.Length > 0 ? "no times yet: lines without [mm:ss.xx] are plain lyrics"
            : synced ? "LRC: one [mm:ss.xx] time per line" : "";
    }

    private void SetEditor(string text)
    {
        _settingText = true;
        Editor.Text = text;
        _settingText = false;
        UseButton.IsEnabled = false;
    }

    private void OnEdited(object? sender, TextChangedEventArgs e)
    {
        if (_settingText || _track is null) return;
        UseButton.IsEnabled = Lrc.Tidy(Editor.Text) != Lrc.Tidy(SyncedView.IsChecked == true ? _loaded : Lrc.Plain(_loaded));
    }

    private void OnUseText(object? sender, RoutedEventArgs e)
    {
        if (_track is null) return;
        Use(_track, Editor.Text ?? "");
    }

    private void OnRevert(object? sender, RoutedEventArgs e)
    {
        if (_track is null) return;
        _loaded = Current(_track);
        ShowView();
    }

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        if (_track is null) return;
        Use(_track, "");
        SetEditor("");
    }

    private void Use(TrackFile track, string text)
    {
        var changes = Pending.SetLyrics(track, text, ToTag.IsChecked == true, ToLrc.IsChecked == true, TimesInTag.IsChecked == true);
        if (changes.Count > 0) Changes?.Invoke(changes);
        UseButton.IsEnabled = false;
    }

    // ---- LRCLIB ------------------------------------------------------------------------

    private void ShowResults()
    {
        if (_track is null) return;
        if (_results.TryGetValue(_track.Path, out var fits))
        {
            ResultList.ItemsSource = fits.Select(f => new ResultRow(f)).ToList();
            FoundNote.Text = fits.Count == 0 ? "LRCLIB has nothing for this track." : "";
        }
        else
        {
            ResultList.ItemsSource = null;
            FoundNote.Text = "Not asked yet.";
        }
    }

    private void OnUseResult(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ResultRow row } || _track is null) return;
        var text = row.Fit.Result.Synced ?? row.Fit.Result.Plain ?? "";
        _loaded = Lrc.Tidy(text);
        SyncedView.IsChecked = true;
        PlainView.IsChecked = false;
        ShowView();
        Use(_track, text);
    }

    private void OnPreviewResult(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ResultRow row }) return;
        _loaded = Lrc.Tidy(row.Fit.Result.Synced ?? row.Fit.Result.Plain);
        SyncedView.IsChecked = true;
        PlainView.IsChecked = false;
        ShowView();
        UseButton.IsEnabled = true;
        EditorNote.Text = "a preview: nothing is used until Use for this track";
    }

    private async void OnFindOne(object? sender, RoutedEventArgs e)
    {
        if (_track is not { } track || _album is null) return;
        var cancel = Restart();
        Busy($"Asking LRCLIB about \"{track.Get(FieldMap.Title) ?? track.FileName}\"…");
        try
        {
            await Find(track, cancel.Token, automatic: false);
        }
        catch (OperationCanceledException) { }
        catch (LrcLibException ex) { FoundNote.Text = $"{ex.Message}. Try again later."; }
        catch (Exception ex) { FoundNote.Text = $"LRCLIB: {ex.Message}"; }
        finally { if (!cancel.IsCancellationRequested) Busy(null); }
    }

    /// Every track without synced lyrics is asked about; a fit within 2 s with
    /// the same artist and title goes to Pending, the rest wait to be looked
    /// at (spec §3.2).
    private async void OnFindAll(object? sender, RoutedEventArgs e)
    {
        if (_album is null) return;
        var cancel = Restart();
        var asked = 0;
        var queued = 0;
        var tracks = _album.Tracks.Where(t => t.Lyrics != LyricsKind.Synced && t.Problem is null).ToList();
        try
        {
            foreach (var track in tracks)
            {
                asked++;
                Busy($"Asking LRCLIB: {asked} of {tracks.Count} tracks, {queued} found so far…");
                if (await Find(track, cancel.Token, automatic: true)) queued++;
            }
            Busy(null);
            FoundNote.Text = tracks.Count == 0 ? "Every track already has synced lyrics."
                : $"{queued} of {tracks.Count} tracks found and added to Pending; the rest are marked to look at.";
        }
        catch (OperationCanceledException) { }
        catch (LrcLibException ex)
        {
            Busy(null);
            FoundNote.Text = $"{ex.Message} after {asked - 1} of {tracks.Count} tracks. Try again later.";
        }
        catch (Exception ex)
        {
            Busy(null);
            FoundNote.Text = $"LRCLIB: {ex.Message} after {asked - 1} of {tracks.Count} tracks.";
        }
    }

    /// Asks LRCLIB about one track: its exact match first, then a search.
    /// With `automatic`, a fit that may be used unasked goes to Pending.
    /// True when something went to Pending.
    private async Task<bool> Find(TrackFile track, CancellationToken ct, bool automatic)
    {
        _lrclib ??= new LrcLibClient();
        var artist = track.Get(FieldMap.Artist) ?? _album?.Artist ?? "";
        var title = track.Get(FieldMap.Title) ?? Path.GetFileNameWithoutExtension(track.FileName);
        var album = track.Get(FieldMap.Album) ?? _album?.Title ?? "";
        var results = new List<LrcLibResult>();
        if (await _lrclib.GetAsync(artist, title, album, track.Duration, ct) is { } exact) results.Add(exact);
        if (!results.Any(r => LrcLibClient.Fit(r, artist, title, track.Duration).Automatic))
            results.AddRange((await _lrclib.SearchAsync(artist, title, ct)).Where(r => results.All(x => x.Id != r.Id)));
        var fits = LrcLibClient.Rank(results, artist, title, track.Duration).Take(6).ToList();
        _results[track.Path] = fits;
        var row = _rows.FirstOrDefault(r => r.Track.Path == track.Path);
        if (row is not null) row.Found = FoundText(track, fits);
        if (_track?.Path == track.Path) ShowResults();

        // Used unasked only when it fits and is an improvement: lyrics where
        // there were none, or times where there were only words.
        if (!automatic || fits.FirstOrDefault() is not { Automatic: true } best) return false;
        var better = track.Lyrics == LyricsKind.None ? best.Result.HasSynced || best.Result.HasPlain : best.Result.HasSynced;
        if (!better) return false;
        Use(track, best.Result.Synced ?? best.Result.Plain ?? "");
        return true;
    }

    private static (string Text, Tone Tone) FoundText(TrackFile track, IReadOnlyList<LyricsFit> fits) => fits.FirstOrDefault() switch
    {
        null => ("none found", Tone.Note),
        { Automatic: true, Result.HasSynced: true } => ("synced found", Tone.Clean),
        { Automatic: true } => ("plain found", Tone.Clean),
        var f => ($"check: {f.Text.Split(" · ")[0].Replace("length ", "")}", Tone.Fixable),
    };

    private CancellationTokenSource Restart()
    {
        _busy?.Cancel();
        return _busy = new CancellationTokenSource();
    }

    private void Busy(string? text)
    {
        BusyBar.IsVisible = text is not null;
        if (text is not null) BusyText.Text = text;
        FindAllButton.IsEnabled = text is null && _album is not null;
    }
}

public sealed class LyricsRow(TrackFile track) : INotifyPropertyChanged
{
    public TrackFile Track { get; } = track;
    public string Number => Track.Get(FieldMap.Track) is { } t ? t.Split('/')[0].PadLeft(2, '0') : "";
    public string Title => Track.Get(FieldMap.Title) ?? Track.FileName;
    public string Now => Track.Lyrics.ToString().ToLowerInvariant();
    public IBrush NowInk => Track.Lyrics == LyricsKind.Synced ? Tones.OkText : Tones.NoteText;
    public IBrush NowFill => Track.Lyrics == LyricsKind.Synced ? Tones.OkFill : Tones.NoteFill;

    /// What a search found, or null before one.
    public (string Text, Tone Tone)? Found
    {
        get;
        set
        {
            field = value;
            foreach (var p in new[] { nameof(Found), nameof(HasFound), nameof(FoundText), nameof(FoundInk), nameof(FoundFill) })
                PropertyChanged?.Invoke(this, new(p));
        }
    }

    public bool HasFound => Found is not null;
    public string FoundText => Found?.Text ?? "";
    public IBrush FoundInk => Tones.Of(Found?.Tone ?? Tone.Note).Text;
    public IBrush FoundFill => Tones.Of(Found?.Tone ?? Tone.Note).Fill;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ResultRow(LyricsFit Fit)
{
    public string Title => $"{(Fit.Result.HasSynced ? "Synced" : Fit.Result.Instrumental ? "Instrumental" : "Plain")} · {TimeSpan.FromSeconds(Fit.Result.Duration):m\\:ss}";
    public string Detail => $"{Fit.Text}\n{Fit.Result.Artist} — {Fit.Result.Track}\nalbum {(Fit.Result.Album.Length > 0 ? Fit.Result.Album : "unknown")}";
    public string Tag => Fit.Automatic ? "fits" : Fit.Names && Math.Abs(Fit.Seconds) <= 5 ? "close" : "check";
    public IBrush TagInk => Fit.Automatic ? Tones.OkText : Tones.WarnText;
    public IBrush TagFill => Fit.Automatic ? Tones.OkFill : Tones.WarnFill;
    public IBrush Fill => Fit.Automatic ? Tones.OkFill : Tones.Bar;
    public IBrush Edge => Fit.Automatic ? Tones.OkText : Tones.NoteFill;
}
