// Marginal — the keyboard (Accessibility, 2026-11-01).
//
// Everything the toolbar does can be done from the keyboard: ⌘ on the Mac,
// Ctrl elsewhere, each named in its button's tooltip.
//
//   O  open folder        F  the library's filter     ⇧F  find art
//   L  look up            S  Write this album          Z  Undo (not in a text box,
//   1  Tags & art         2  Lyrics                    ,  Preferences   which undoes text)
//   F1 (and ⌘? on the Mac): the Keys page of Preferences.
//
// THE LISTS MUST AGREE: every key here is listed on that page (PrefsWindow.Keys.cs).
//
// The field list is walked with the arrow keys; Enter (or F2) edits the field,
// Escape goes back to the list. A click on a field still goes straight to its
// text box, as before.

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Marginal.App;

public sealed partial class MainWindow
{
    private static readonly KeyModifiers Command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// The last thing to choose a field was the mouse: its text box takes focus.
    private bool _fieldByPointer;

    /// "⌘O" on the Mac, "Ctrl+O" elsewhere.
    internal static string Shortcut(string key, bool shift = false) =>
        OperatingSystem.IsMacOS() ? $"{(shift ? "⇧" : "")}⌘{key}" : $"Ctrl+{(shift ? "Shift+" : "")}{key}";

    private void SetUpKeys()
    {
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
        FieldList.AddHandler(PointerPressedEvent, (_, _) => _fieldByPointer = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        FieldList.AddHandler(KeyDownEvent, (_, _) => _fieldByPointer = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Tunnelling: the list takes Enter for itself before a bubbling handler sees it.
        FieldList.AddHandler(KeyDownEvent, OnFieldKey, RoutingStrategies.Tunnel);

        Tip(OpenButton, "Open a folder of albums", "O");
        Tip(LookupButton, "Look up this album on MusicBrainz", "L");
        Tip(ArtButton, "Find cover art", "F", shift: true);
        Tip(UndoButton, "Undo the last write", "Z");
        Tip(WriteButton, "Write this album's pending changes", "S");
        Tip(TagsTab, "Tags & art", "1");
        Tip(LyricsTab, "Lyrics", "2");
        Tip(GearButton, "Preferences: updates and about", ",");
        Tip(FilterBox, "Filter by artist or album", "F");
    }

    private static void Tip(Control control, string what, string key, bool shift = false)
    {
        var keys = Shortcut(key, shift);
        ToolTip.SetTip(control, $"{what} ({keys})");
        AutomationProperties.SetAcceleratorKey(control, keys);
    }

    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        // F1, and ⌘? on the Mac: the Keys page of Preferences.
        if ((e.Key == Key.F1 && mods == KeyModifiers.None)
            || (OperatingSystem.IsMacOS() && e.Key == Key.OemQuestion && mods == (Command | KeyModifiers.Shift)))
        {
            ShowPrefs(PrefsWindow.Page.Keys);
            e.Handled = true;
            return;
        }
        if (!mods.HasFlag(Command) || mods.HasFlag(KeyModifiers.Alt)) return;
        var shift = mods.HasFlag(KeyModifiers.Shift);
        var args = new RoutedEventArgs();
        switch (e.Key)
        {
            case Key.O when !shift:
                OnOpenFolder(this, args);
                break;
            case Key.F when shift:
                if (!ArtButton.IsEnabled) return;
                OnFindArt(this, args);
                break;
            case Key.F:
                FilterBox.Focus(NavigationMethod.Tab);
                FilterBox.SelectAll();
                break;
            case Key.L when !shift:
                if (!LookupButton.IsEnabled) return;
                OnLookup(this, args);
                break;
            case Key.S when !shift:
                if (PendingHere.Count == 0 || !WriteButton.IsEnabled) return;
                OnWrite(this, args);
                break;
            // In a text box the keys undo its typing, as everywhere else.
            case Key.Z when !shift:
                if (FocusManager?.GetFocusedElement() is TextBox || !UndoButton.IsEnabled) return;
                OnUndo(this, args);
                break;
            case Key.D1 or Key.NumPad1 when !shift:
                OnTab(TagsTab, args);
                break;
            case Key.D2 or Key.NumPad2 when !shift:
                OnTab(LyricsTab, args);
                break;
            case Key.OemComma when !shift:
                OnPrefs(this, args);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// Enter or F2 on a field: into its text box.
    private void OnFieldKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.F2) || !EditBar.IsVisible) return;
        EditBox.Focus(NavigationMethod.Tab);
        EditBox.SelectAll();
        e.Handled = true;
    }

    /// After an edit is set or cancelled, back to that field's row, so the
    /// arrow keys carry on from where they were. Posted: setting a value
    /// rebuilds the list first.
    private void BackToFields(Marginal.Core.Field? field) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        var i = (FieldList.ItemsSource as IEnumerable<FieldRow>)?.ToList().FindIndex(r => r.Field == field) ?? -1;
        if (i >= 0 && FieldList.ContainerFromIndex(i) is Control row) row.Focus(NavigationMethod.Tab);
        else FieldList.Focus(NavigationMethod.Tab);
    });
}
