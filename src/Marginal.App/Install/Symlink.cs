// FROM Deadwax (Deadwax.App/Install/Symlink.cs, 704ed87, 2026-10-31), renamed to Marginal throughout.
// Dates and findings in the comments are Deadwax's history; a fix to the
// install or update logic goes to both (and AlbumWall's, from which Deadwax's came).
//
namespace Marginal.App.Install;

/// <summary>
/// Symlink helpers, ported with the installer from AlbumWall, where they guard its
/// <c>~/.local/bin/albumwall</c> link. Marginal makes no such link; it uses <see cref="ResolveTarget"/>
/// to see a dangling link when deleting files on uninstall.
///
/// <see cref="File.ResolveLinkTarget(string,bool)"/> is the obvious way to ask "is there a link here,
/// and where does it point?", but it <em>throws</em> <see cref="FileNotFoundException"/> when nothing
/// exists at the path at all — which is exactly the ordinary first-install case. That exception
/// derives from <see cref="IOException"/>, so a caller that wraps the whole create-if-needed sequence
/// in <c>catch (IOException)</c> silently turns "no link yet" into "give up", and the link is then
/// never created on that launch or any later one. Asking where a link points is a question that
/// deserves an answer rather than an exception, so <see cref="ResolveTarget"/> makes the probe total:
/// a missing path is null, not a failure.
/// </summary>
public static class Symlink
{
    /// <summary>
    /// Where the symlink at <paramref name="path"/> points, or null when <paramref name="path"/> is
    /// not a symlink — including when nothing exists there at all.
    /// </summary>
    /// <remarks>
    /// Reports the target recorded on the link without following it any further, so a dangling link
    /// still names its target. That matters wherever a link has to be found and removed:
    /// <see cref="File.Exists(string)"/> is not a reliable way to spot one, because whether it
    /// follows a dangling link differs by runtime and platform — measured true on .NET 10 /
    /// linux-arm64, and long assumed false elsewhere in this codebase. Ask both, depend on neither.
    /// </remarks>
    public static string? ResolveTarget(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: false)?.FullName;
        }
        catch (FileNotFoundException) { return null; }        // nothing at the path
        catch (DirectoryNotFoundException) { return null; }   // not even its parent directory
    }

    /// <summary>
    /// Point the symlink at <paramref name="path"/> at <paramref name="target"/>, creating any
    /// missing parent directory and replacing whatever is already there.
    /// </summary>
    /// <param name="target">
    /// Give this as an absolute path. <see cref="ResolveTarget"/> reports absolute paths, so a
    /// relative one never compares equal to what is already on disk and the link would be pointlessly
    /// recreated on every launch.
    /// </param>
    /// <returns>
    /// True if the link was created or replaced; false if it already pointed at
    /// <paramref name="target"/> and was left untouched.
    /// </returns>
    /// <remarks>
    /// An already-correct link is deliberately left alone rather than recreated: this runs on every
    /// launch, and deleting and remaking a good link each time would open a pointless window in which
    /// the terminal command does not exist.
    ///
    /// Throws rather than swallowing — creating a symlink needs Developer Mode or elevation on
    /// Windows, and callers for whom the link is merely a convenience should catch that themselves
    /// around this call alone, not around the probe.
    /// </remarks>
    public static bool Ensure(string path, string target)
    {
        if (InstallLayout.SamePath(ResolveTarget(path), target)) return false;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Clear the way for the new link: a stale link aimed elsewhere, or a regular file someone
        // put there. Both questions are asked because neither alone covers both cases on every
        // runtime — the link probe is blind to a regular file, and File.Exists cannot be trusted
        // either way on a dangling link.
        if (File.Exists(path) || ResolveTarget(path) is not null) File.Delete(path);

        File.CreateSymbolicLink(path, target);
        return true;
    }
}
