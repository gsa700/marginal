// FROM Deadwax (Deadwax.App/Install/XdgUserDirs.cs, 704ed87, 2026-10-31), renamed to Marginal throughout.
// Dates and findings in the comments are Deadwax's history; a fix to the
// install or update logic goes to both (and AlbumWall's, from which Deadwax's came).
//
namespace Marginal.App.Install;

/// <summary>
/// Reads a directory out of <c>~/.config/user-dirs.dirs</c>, the freedesktop file that says where a
/// user's Desktop, Downloads and so on actually are.
///
/// This exists because <c>~/Desktop</c> is an assumption, not a fact. The directory is localised —
/// <c>~/Escritorio</c>, <c>~/Bureau</c>, <c>~/デスクトップ</c> — and a user can point it anywhere or
/// switch it off entirely. Writing a launcher to a hardcoded <c>~/Desktop</c> on such a machine
/// creates a stray folder holding a file nobody will ever see.
///
/// Pure parsing, given the file's contents: the format is quoted, <c>$HOME</c>-relative and
/// comment-bearing, which is three ways to get it subtly wrong on a machine that isn't in front of
/// you. The v0.7.0-beta symlink bug came from trusting a BCL call's behaviour on Linux without
/// checking it, so this one is tested instead.
/// </summary>
public static class XdgUserDirs
{
    /// <summary>Key naming the desktop directory.</summary>
    public const string DesktopKey = "XDG_DESKTOP_DIR";

    /// <summary>
    /// Resolve <paramref name="key"/> from the contents of <c>user-dirs.dirs</c>. Returns null when
    /// the key is absent, empty, or set to the home directory itself.
    /// </summary>
    /// <param name="home">Value to substitute for <c>$HOME</c>.</param>
    /// <remarks>
    /// A key set to <c>"$HOME/"</c> means "this user has no such directory" by convention — the
    /// desktop is disabled rather than being the home directory. Writing a launcher into $HOME on
    /// that machine would scatter a file into the top of someone's home directory, so it returns null
    /// and the caller skips the shortcut.
    /// </remarks>
    public static string? Resolve(string? contents, string key, string home)
    {
        if (string.IsNullOrEmpty(contents)) return null;

        string? found = null;
        foreach (var raw in contents.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0 || line[..eq].Trim() != key) continue;

            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            found = value;   // last assignment wins, matching how the shell would read the file
        }

        if (string.IsNullOrWhiteSpace(found)) return null;

        var path = found.StartsWith("$HOME", StringComparison.Ordinal)
            ? home + found["$HOME".Length..]
            : found;

        path = path.TrimEnd('/');
        if (path.Length == 0) return null;

        // "$HOME/" collapses to the home directory, which is the convention for "no such directory".
        return string.Equals(path.TrimEnd('/'), home.TrimEnd('/'), StringComparison.Ordinal) ? null : path;
    }
}
