// COPIED from Deadwax (src/Deadwax.Metadata/AppVersion.cs, 2026-10-31). Namespace changed only.
//
using System.Reflection;

namespace Marginal.Core;

/// The running program's own version (the app, or the CLI), without the
/// source-revision suffix: what the .log's first line says, and what Deadwax
/// tells MusicBrainz and the Cover Art Archive it is. Both were constants,
/// "0.1", that nothing ever bumped (the log fixed 2026-10-10, the MusicBrainz
/// client and the CLI 2026-10-11).
public static class AppVersion
{
    public static string Current
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
            var v = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }
}
