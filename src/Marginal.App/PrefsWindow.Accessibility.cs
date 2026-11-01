// Marginal — the Accessibility page of Preferences: text size, the one thing
// here a person chooses (AlbumWall's page, less its motion choice: nothing in
// Marginal moves).

namespace Marginal.App;

public sealed partial class PrefsWindow
{
    private void WireAccessibility()
    {
        TextSizeChoice.ItemsSource = TextSize.Choices.Select(p => $"{p}%").ToList();
        TextSizeChoice.SelectedIndex = Array.IndexOf(TextSize.Choices, TextSize.Percent);
        TextSizeChoice.SelectionChanged += (_, _) =>
        {
            if (TextSizeChoice.SelectedIndex < 0) return;
            var percent = TextSize.Choices[TextSizeChoice.SelectedIndex];
            _settings.TextSize = percent == 100 ? null : percent;
            _settings.Save();
            TextSize.Choose(percent);
        };
    }
}
