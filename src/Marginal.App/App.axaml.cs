using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Marginal.App;

public sealed class App : Application
{
    /// The product's name wherever a person reads it.
    public const string DisplayName = "Marginal";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (OperatingSystem.IsMacOS()) Name = DisplayName;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // On native Wayland Avalonia draws the title bars: only the buttons
        // GNOME asks for, never its full-screen one (AlbumWall's DrawnCaptions).
        DrawnCaptions.FollowTheDesktop();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            // The last line of defence: an error that escapes a click or an
            // answer from the web is shown, not allowed to close the app with
            // Pending in it (a 200 maintenance page did that; security review
            // 2026-10-31, A2). Each handler still catches what it expects.
            Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Console.Error.WriteLine($"[error] {e.Exception}");
                window.ShowError(e.Exception.Message);
                e.Handled = true;
            };
            // `marginal <folder> [--from albumwall]` opens that folder (spec §6);
            // other arguments are ignored.
            var args = desktop.Args ?? [];
            var from = Array.IndexOf(args, "--from") is >= 0 and var i && i + 1 < args.Length ? args[i + 1] : null;
            window.OpenAtStart(args.FirstOrDefault(a => !a.StartsWith('-') && a != from), from);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
