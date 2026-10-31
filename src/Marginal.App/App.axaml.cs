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
            // `marginal <folder>` opens that folder (spec §6); other arguments are ignored.
            window.OpenAtStart(desktop.Args?.FirstOrDefault(a => !a.StartsWith('-')));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
