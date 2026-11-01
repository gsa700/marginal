using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Marginal.App;

/// Preferences: a sidebar of pages (AlbumWall's, 2026-11-01). The update
/// logic is Deadwax's Updates page (one button, two jobs: check, then "Update
/// to X and restart"), on Marginal's main window.
public sealed partial class PrefsWindow : Window
{
    /// The sidebar's lines, in order (PrefsWindow.axaml).
    public enum Page { Accessibility, Updates, Keys, About }

    private readonly MainWindow _main;
    private readonly Settings _settings;
    private readonly Control[] _pages;
    private bool _updating;
    private string? _updateNote;

    /// For the previewer only.
    public PrefsWindow() : this(new MainWindow(), new Settings()) { }

    public PrefsWindow(MainWindow main, Settings settings)
    {
        _main = main;
        _settings = settings;
        InitializeComponent();
        TextSize.Follow(this);   // before the frame: the frame stays its own size
        if (settings.PrefsWidth is { } w) Width = Math.Max(w * TextSize.Factor, MinWidth);
        if (settings.PrefsHeight is { } h) Height = Math.Max(h * TextSize.Factor, MinHeight);
        DrawnFrame.Wrap(this);
        _pages = [PageAccessibility, PageUpdates, PageKeys, PageAbout];
        Pages.SelectionChanged += (_, _) => ShowPage();
        Pages.SelectedIndex = 0;
        ShowPage();

        VersionText.Text = $"Version {UpdateService.CurrentVersion}";
        FillDiagnostics();
        try { AppIcon.Source = new Bitmap(AssetLoader.Open(new Uri("avares://Marginal/Assets/icon-256.png"))); } catch (Exception) { }
        AutoCheckBox.IsChecked = settings.CheckForUpdates;
        EdgeBox.IsChecked = settings.EdgeChannel;
        FillUpdate();
        WireAccessibility();
        FillKeys();
        // Escape closes it, as any panel of settings (Accessibility, 2026-11-01).
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) { Close(); e.Handled = true; } };
        // Where it was last time, kept on a screen (WindowPlacement); a first
        // run opens over the main window.
        Opened += (_, _) => WindowPlacement.Restore(this, _settings.PrefsX, _settings.PrefsY);
        Closing += (_, _) =>
        {
            if (WindowPlacement.Current(this) is { } p) (_settings.PrefsX, _settings.PrefsY) = (p.X, p.Y);
            if (WindowState == WindowState.Normal)
                (_settings.PrefsWidth, _settings.PrefsHeight) = (Math.Round(Bounds.Width / TextSize.Factor), Math.Round(Bounds.Height / TextSize.Factor));
            _settings.Save();
        };
    }

    /// Opens on a page: the gear on the first, F1 on Keys.
    public void Select(Page page) => Pages.SelectedIndex = (int)page;

    private void ShowPage()
    {
        for (var i = 0; i < _pages.Length; i++) _pages[i].IsVisible = i == Pages.SelectedIndex;
    }

    /// Where things stand, from whatever is known: the launch-time check may
    /// have answered already, and the last update may have failed to apply.
    internal void FillUpdate()
    {
        if (_updating) return;
        var have = UpdateService.CurrentVersion;
        var info = _main.LatestUpdate;
        UpdateButton.IsVisible = UpdateService.CanUpdate;
        UpdateButton.IsEnabled = true;
        UpdateButton.Content = info is { UpdateAvailable: true, AssetUrl: not null }
            ? $"Update to {info.LatestTag.TrimStart('v', 'V')} and restart"
            : "Check for updates";
        UpdateText.Text = _updateNote
            ?? (!UpdateService.CanUpdate ? (UpdateService.MacBlocker is { } why
                    ? $"This is version {have}. {why}"
                    : "This is a development build, so updates are not offered.")
              : _main.LastUpdateFailed ? $"The last update could not be put in place, or did not start, so this is still {have}."
              : info is null ? $"This is version {have}."
              : info.Error is { } error ? error
              : info.NothingPublished ? $"No release has been published yet. This is version {have}."
              : !info.UpdateAvailable ? $"This is the latest version, {have}."
              : info.AssetUrl is null ? $"{info.LatestTag} is out, but it has no build for this kind of computer."
              : $"Version {info.LatestTag.TrimStart('v', 'V')}{(info.IsPrerelease ? " (Edge)" : "")} is available. This is {have}. Downloads, checks the SHA-256, then restarts.");
    }

    private async void OnUpdate(object? sender, RoutedEventArgs e)
    {
        if (_updating || !UpdateService.CanUpdate) return;
        _updateNote = null;

        if (_main.LatestUpdate is not { UpdateAvailable: true, AssetUrl: not null })
        {
            UpdateButton.IsEnabled = false;
            UpdateText.Text = "Looking…";
            _main.LatestUpdate = await UpdateService.CheckAsync(_settings.EdgeChannel);
            _main.ShowUpdateDot(_main.LatestUpdate.UpdateAvailable);
            FillUpdate();
            return;
        }

        var info = _main.LatestUpdate;
        _updating = true;
        UpdateButton.IsEnabled = false;
        UpdateBar.IsVisible = true;
        try
        {
            var progress = new Progress<double>(f =>
            {
                UpdateFill.Width = f * UpdateBar.Bounds.Width;
                UpdateText.Text = $"Downloading {info.LatestTag.TrimStart('v', 'V')}… {f:P0}";
            });
            await _main.ApplyUpdateAsync(info, progress);
            UpdateText.Text = "Checked. Restarting into the new version…";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[update] failed: {ex.Message}");
            _updating = false;
            UpdateBar.IsVisible = false;
            UpdateFill.Width = 0;
            _updateNote = ex.Message;
            FillUpdate();
        }
    }

    private void OnAutoCheck(object? sender, RoutedEventArgs e)
    {
        _settings.CheckForUpdates = AutoCheckBox.IsChecked == true;
        _settings.Save();
    }

    private void OnEdge(object? sender, RoutedEventArgs e)
    {
        _settings.EdgeChannel = EdgeBox.IsChecked == true;
        _settings.Save();
        _main.LatestUpdate = null;   // what was found belongs to the other channel
        _main.ShowUpdateDot(_main.LastUpdateFailed);
        _updateNote = null;
        FillUpdate();
    }

    private void OnRepo(object? sender, RoutedEventArgs e) => Open(UpdateService.ProjectUrl);
    private void OnAlbumWall(object? sender, RoutedEventArgs e) => Open("https://github.com/gsa700/albumwall");
    private void OnDeadwax(object? sender, RoutedEventArgs e) => Open("https://github.com/gsa700/deadwax");

    private void Open(string url) => _ = Launcher.LaunchUriAsync(new Uri(url));

    // DIAGNOSTICS (Diagnostics.cs): a report on the clipboard, and the log's folder.
    private void FillDiagnostics() =>
        DiagnosticsStatus.Text =
            "If something goes wrong, Copy diagnostics puts a report on the clipboard, ready to paste into "
          + "a GitHub issue or a message: the version, this system, the last crash if there was one, and "
          + "the end of the log. It names folders on this computer, so look it over before you send it."
          + (Diagnostics.LastCrash is { } when
                ? $" {App.DisplayName} last closed unexpectedly on {when:dddd d MMMM 'at' HH:mm}."
                : "");

    private async void OnCopyDiagnostics(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is not { } clipboard) throw new InvalidOperationException("there is no clipboard here");
            await clipboard.SetTextAsync(Diagnostics.Report());
            DiagnosticsResult.Text = "Copied. Paste it wherever you are reporting the problem.";
        }
        catch (Exception ex)
        {
            DiagnosticsResult.Text = $"Could not copy it: {ex.Message}";
        }
    }

    private void OnShowLog(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var folder = new DirectoryInfo(Path.GetDirectoryName(LogFile.Path)!);
            _ = Launcher.LaunchDirectoryInfoAsync(folder);
            DiagnosticsResult.Text = $"The log is {LogFile.Path}";
        }
        catch (Exception ex)
        {
            DiagnosticsResult.Text = $"Could not open the folder: {ex.Message}";
        }
    }
}
