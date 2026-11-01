// Marginal — the Keys page of Preferences: what cannot be found by looking
// (AlbumWall's Keys and search page, PrefsWindow.Help.cs there).
//
// THE LISTS MUST AGREE: the keys are handled in MainWindow.Keys.cs (and the
// lyrics player's in LyricsPanel.OnKey). A key added there and not here is a
// key nobody knows about; one listed here that does nothing is worse.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Marginal.App;

public sealed partial class PrefsWindow
{
    private void FillKeys()
    {
        static string K(string key, bool shift = false) => MainWindow.Shortcut(key, shift);

        Heading("The main window", first: true);
        Rows(
            (K("O"), "Open a folder of albums."),
            (K("F"), "Go to the library's filter."),
            (K("L"), "Look up the album on MusicBrainz."),
            (K("F", shift: true), "Find cover art for the album."),
            (K("S"), "Write the album's pending changes."),
            (K("Z"), "Undo the last write. In a text box, undo the typing instead."),
            ($"{K("1")}   {K("2")}", "Tags & art, Lyrics."),
            (K(","), "Preferences."),
            (OperatingSystem.IsMacOS() ? "F1   or   ⌘?" : "F1", "This page."));
        Note("Tab and Shift+Tab move from control to control, and the arrow keys move through a list. "
           + "In the album's fields, Enter or F2 edits the one picked, Enter sets it on every file and "
           + "Esc goes back to the list. The dividers between the panes take the arrow keys too.");

        Heading("Timing lyrics");
        Rows(
            ("Space", "With Tap to time on: stamp the line, as the track plays."),
            ("⌫", "With Tap to time on: back a line, its stamp taken off."));

        Heading("Windows and dialogs");
        Rows(
            ("Esc", "Close Find art, Look up or Preferences; in a question, the answer that changes nothing."),
            ("Enter", "In the MusicBrainz search box, search."));
    }

    private void Heading(string text, bool first = false)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = 14, FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, first ? 0 : 14, 0, 5)
        };
        Avalonia.Automation.AutomationProperties.SetHeadingLevel(block, 2);
        KeysBody.Children.Add(block);
    }

    private void Note(string text) =>
        KeysBody.Children.Add(new TextBlock
        {
            Text = text, Classes = { "about" }, Margin = new Thickness(0, 6, 0, 3)
        });

    /// One key and what it does; a row whose keys are null is left out.
    private void Rows(params (string? Keys, string Does)[] rows)
    {
        foreach (var (keys, does) in rows)
        {
            if (keys is null) continue;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), MinHeight = 22 };
            row.Children.Add(new TextBlock
            {
                Text = keys, FontSize = 13, Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Top
            });
            var what = new TextBlock { Text = does, FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap, Classes = { "soft" } };
            Grid.SetColumn(what, 1);
            row.Children.Add(what);
            Avalonia.Automation.AutomationProperties.SetName(row, $"{keys}: {does}");
            KeysBody.Children.Add(row);
        }
    }
}
