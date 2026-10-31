// Marginal — Keep: what is kept between runs besides the windows themselves (Dave,
// 2026-10-31: "remember everything, even the art panel dimensions"): every
// pane's width, the options that are ticks and choices, the folder and tab.
// Each is read back when its window opens and written when it closes, under a
// short key in settings.json; a key never saved leaves the XAML's default.

using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Marginal.App;

internal static class Keep
{
    /// A pane's width (a grid column), within its MinWidth and MaxWidth.
    public static void Column(Settings settings, Window window, Grid grid, int index, string key)
    {
        var column = grid.ColumnDefinitions[index];
        if (settings.Panes.TryGetValue(key, out var width) && double.IsFinite(width))
            column.Width = new GridLength(Math.Clamp(width, column.MinWidth, column.MaxWidth));
        // The width it was set to (a splitter sets it in pixels): a pane on a
        // tab not showing reads as 0 wide, and was not kept.
        window.Closing += (_, _) =>
        {
            var now = column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth;
            if (now > 0) settings.Panes[key] = Math.Round(now);
        };
    }

    /// A tick box or one of a group of radio buttons.
    public static void Tick(Settings settings, Window window, ToggleButton box, string key)
    {
        if (settings.Ticks.TryGetValue(key, out var on)) box.IsChecked = on;
        window.Closing += (_, _) => settings.Ticks[key] = box.IsChecked == true;
    }

    /// A drop-down's choice.
    public static void Choice(Settings settings, Window window, ComboBox box, string key)
    {
        if (settings.Choices.TryGetValue(key, out var index) && index >= 0 && index < box.ItemCount) box.SelectedIndex = index;
        window.Closing += (_, _) => settings.Choices[key] = box.SelectedIndex;
    }
}
