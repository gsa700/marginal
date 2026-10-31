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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            // `marginal <folder> [--from albumwall]` opens that folder (spec §6);
            // other arguments are ignored.
            var args = desktop.Args ?? [];
            var from = Array.IndexOf(args, "--from") is >= 0 and var i && i + 1 < args.Length ? args[i + 1] : null;
            window.OpenAtStart(args.FirstOrDefault(a => !a.StartsWith('-') && a != from), from);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
