// FROM Deadwax (Deadwax.Drive/ToolPath.cs, 704ed87, 2026-10-31), renamed to Marginal throughout.
// Dates and findings in the comments are Deadwax's history; a fix to the
// install or update logic goes to both (and AlbumWall's, from which Deadwax's came).
//
namespace Marginal.Core;

/// A system tool by its full path, found on PATH as a shell would, never in
/// the current folder or the program's own: .NET, given a bare name, looks
/// in both before PATH, so a file of that name left there (an unpacked
/// archive in Downloads) ran instead (security review 2026-10-24, L16).
/// Only absolute PATH entries count; an empty or relative one ("." among
/// them) is the current folder in disguise.
public static class ToolPath
{
    /// The tool's full path, or null when no PATH entry has it.
    public static string? Find(string name)
    {
        var file = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name + ".exe" : name;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0 || !Path.IsPathFullyQualified(dir)) continue;
            var p = Path.Combine(dir, file);
            if (!File.Exists(p)) continue;
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(p) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0) continue;
            return p;
        }
        return null;
    }
}
