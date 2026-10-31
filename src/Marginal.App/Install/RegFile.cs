// FROM Deadwax (Deadwax.App/Install/RegFile.cs, 704ed87, 2026-10-31), renamed to Marginal throughout.
// Dates and findings in the comments are Deadwax's history; a fix to the
// install or update logic goes to both (and AlbumWall's, from which Deadwax's came).
//
using System.Globalization;
using System.Text;

namespace Marginal.App.Install;

/// <summary>One value inside a <c>.reg</c> file, already formatted as its right-hand side.</summary>
public readonly record struct RegValue(string Name, string Literal);

/// <summary>
/// Builds a Windows <c>.reg</c> file — the input to <c>reg import</c>. Pure string work so the
/// escaping rules can be tested without touching the registry, which matters because the failure
/// mode is a malformed line that <c>reg import</c> rejects wholesale: one bad value loses the entire
/// key, not just itself.
///
/// This exists so registration is a **single** <c>reg.exe</c> invocation rather than one per value.
/// Eleven separate spawns is eleven things that can individually not happen, and the app cannot tell
/// a spawn that silently didn't take from one that did — see the installed-apps entry that was
/// written and then went missing (BACKLOG, 2026-07-30). One import is also cheap enough to repeat on
/// every launch, which is what makes a lost entry self-heal.
///
/// The Linux analogue is <see cref="DesktopEntry"/>; same reasoning, same reason it's tested.
/// </summary>
public static class RegFile
{
    /// <summary>Required first line. The "5.00" is the file format, not a Windows version.</summary>
    public const string Header = "Windows Registry Editor Version 5.00";

    /// <summary>A <c>REG_SZ</c> value.</summary>
    public static RegValue Sz(string name, string data) => new(name, "\"" + Escape(data) + "\"");

    /// <summary>
    /// A <c>REG_DWORD</c> value. Always eight lower-case hex digits — <c>reg import</c> is strict
    /// about the width, and a short one is rejected rather than zero-padded.
    /// </summary>
    public static RegValue Dword(string name, long data) =>
        new(name, "dword:" + ((uint)data).ToString("x8", CultureInfo.InvariantCulture));

    /// <summary>
    /// Build the file contents for one key. <paramref name="keyPath"/> must use the full hive name
    /// (<c>HKEY_CURRENT_USER\…</c>), not the <c>HKCU</c> abbreviation that the <c>reg.exe</c> command
    /// line accepts — the file format only understands the long form.
    /// </summary>
    /// <remarks>
    /// CRLF throughout, and a trailing blank line: <c>reg import</c> is unbothered by the former but
    /// tooling that reads these files expects the DOS convention, and the latter is what regedit's own
    /// exports end with.
    /// </remarks>
    public static string Build(string keyPath, IEnumerable<RegValue> values)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append("\r\n\r\n");
        sb.Append('[').Append(keyPath).Append("]\r\n");
        foreach (var v in values)
            sb.Append('"').Append(Escape(v.Name)).Append("\"=").Append(v.Literal).Append("\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Escape a string for the inside of a quoted <c>.reg</c> value: backslash and double quote take
    /// a backslash. Newlines are flattened to spaces — a value spans exactly one line, and a stray
    /// newline would make the rest of it read as a malformed line and take the import down with it.
    /// </summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r':
                case '\n': sb.Append(' '); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
