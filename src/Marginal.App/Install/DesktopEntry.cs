// FROM Deadwax (Deadwax.App/Install/DesktopEntry.cs, 704ed87, 2026-10-31), renamed to Marginal throughout.
// Dates and findings in the comments are Deadwax's history; a fix to the
// install or update logic goes to both (and AlbumWall's, from which Deadwax's came).
//
using System.Text;

namespace Marginal.App.Install;

/// <summary>
/// Builds a freedesktop.org <c>.desktop</c> entry — the file that puts the app in a Linux or
/// Raspberry Pi application menu. Pure string work so the quoting rules can be tested without a
/// Linux box, which matters because the failure mode is a menu entry that silently does nothing
/// rather than an error anyone sees.
///
/// Ported from LP-100A Monitor.
/// </summary>
public static class DesktopEntry
{
    /// <summary>Filename used under <c>~/.local/share/applications</c>.</summary>
    public const string FileName = "marginal.desktop";

    /// <summary>
    /// Window class Avalonia reports on X11, so the running window associates with this entry
    /// (correct icon in the dock/taskbar rather than a generic placeholder). Matches the assembly
    /// name, which is what Avalonia uses.
    /// </summary>
    public const string WindowClass = "Marginal";

    /// <summary>
    /// Quote a path for the <c>Exec</c> key. Always quoted rather than only when it looks
    /// necessary — the spec's reserved set is wide, and a path is not the place to be clever.
    /// </summary>
    /// <remarks>
    /// Inside double quotes the spec requires a backslash before <c>"</c>, <c>`</c>, <c>$</c> and
    /// <c>\</c> itself. A literal percent must be doubled, because the launcher expands <c>%f</c>,
    /// <c>%U</c> and friends before running anything.
    /// </remarks>
    public static string QuoteExec(string path)
    {
        // A line break would start a key of its own (Exec=, run at login by the launcher): the
        // path comes from HOME or XDG_DATA_HOME, but no entry is written for one that says so
        // (security review 2026-10-24, L17).
        if (path.Any(char.IsControl))
            throw new InstallBlockedException("The install folder's path holds a control character, so no menu entry can be written for it.");
        var sb = new StringBuilder(path.Length + 8);
        sb.Append('"');
        foreach (var c in path)
        {
            switch (c)
            {
                // Twice over: the file's string unescaping turns \\ into \ before the Exec
                // rules read it, and those want \\ for a backslash (spec, "The Exec key").
                case '\\': sb.Append("\\\\\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '`': sb.Append("\\`"); break;
                case '$': sb.Append("\\$"); break;
                case '%': sb.Append("%%"); break;   // field-code escape, not a quoting rule
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Build the file's contents.</summary>
    /// <param name="name">Menu label.</param>
    /// <param name="execPath">Absolute path of the executable; quoted for you.</param>
    /// <param name="iconPath">
    /// Absolute path of an icon file, or a bare theme name. Omitted entirely when null, which is
    /// better than pointing at a file that isn't there.
    /// </param>
    /// <param name="comment">Tooltip / one-line description.</param>
    /// <param name="categories">
    /// Menu categories. Defaults to <c>AudioVideo;Audio;DiscBurning;</c>, as tools/install.sh
    /// writes them.
    /// </param>
    public static string Build(
        string name,
        string execPath,
        string? iconPath = null,
        string? comment = null,
        IEnumerable<string>? categories = null)
    {
        var cats = categories is null ? ["AudioVideo", "Audio"] : categories.ToArray();

        var sb = new StringBuilder();
        sb.Append("[Desktop Entry]\n");
        sb.Append("Type=Application\n");
        sb.Append("Version=1.5\n");            // Desktop Entry Specification version, not the app's
        sb.Append($"Name={Sanitize(name)}\n");
        if (!string.IsNullOrWhiteSpace(comment)) sb.Append($"Comment={Sanitize(comment)}\n");
        sb.Append($"Exec={QuoteExec(execPath)}\n");
        if (!string.IsNullOrWhiteSpace(iconPath)) sb.Append($"Icon={Sanitize(iconPath)}\n");
        sb.Append("Terminal=false\n");
        sb.Append($"Categories={string.Join(";", cats)};\n");
        sb.Append($"StartupWMClass={WindowClass}\n");
        return sb.ToString();
    }

    /// <summary>
    /// Values are single-line: a stray newline would start a bogus key and quietly corrupt the
    /// rest of the file.
    /// </summary>
    private static string Sanitize(string value) =>
        value.Replace("\r", " ").Replace("\n", " ").Trim();
}
