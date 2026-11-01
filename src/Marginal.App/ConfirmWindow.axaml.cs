// FROM Deadwax (Deadwax.App/ConfirmWindow.axaml.cs, 704ed87, 2026-10-31), renamed to Marginal throughout.
// Dates and findings in the comments are Deadwax's history; a fix to the
// install or update logic goes to both (and AlbumWall's, from which Deadwax's came).
//
using Avalonia.Controls;

namespace Marginal.App;

/// <summary>
/// Minimal modal yes/no dialog, ported from the station tools. Use via
/// <c>await new ConfirmWindow(...).ShowDialog&lt;bool&gt;(owner)</c>.
/// </summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
        DrawnFrame.Wrap(this);   // off the Mac: drawn title bar, Close only (Marginal, 2026-10-31)
    }

    /// <param name="affirmative">
    /// Label for the button that returns true. Name the action ("Install", "Remove") rather than
    /// saying "OK" — the button text is what a person actually reads before committing to
    /// something that changes their machine.
    /// </param>
    /// <param name="negative">
    /// Label for the button that returns false, or null for a one-button message where there is
    /// nothing to decline — the outcome has already happened and is only being reported.
    /// </param>
    /// <param name="detail">Secondary text spelling out the consequences. Hidden when null.</param>
    /// <param name="option">A checkbox that rides along with the answer. Hidden when null.</param>
    /// <param name="negativeIsDefault">
    /// Enter and Esc both answer with the negative button, for an action a stray key
    /// should not take (stopping a rip).
    /// </param>
    public ConfirmWindow(string title, string message,
        string affirmative = "Continue", string? negative = "Cancel",
        string? detail = null, string? option = null, bool negativeIsDefault = false) : this()
    {
        if (negativeIsDefault && negative is not null)
        {
            NegativeButton.IsDefault = true;
            NegativeButton.IsCancel = true;
            Opened += (_, _) => NegativeButton.Focus();
        }

        Title = title;
        MessageText.Text = message;
        AffirmativeButton.Content = affirmative;

        if (negative is null) NegativeButton.IsVisible = false;
        else NegativeButton.Content = negative;

        if (!string.IsNullOrWhiteSpace(detail))
        {
            DetailText.Text = detail;
            DetailText.IsVisible = true;
        }

        if (!string.IsNullOrWhiteSpace(option))
        {
            OptionBox.Content = option;
            OptionBox.IsVisible = true;
        }

        AffirmativeButton.Click += (_, _) => Close(true);
        NegativeButton.Click += (_, _) => Close(false);
    }

    /// <summary>Whether the optional checkbox was ticked when the dialog closed.</summary>
    public bool OptionChecked => OptionBox.IsChecked == true;
}
