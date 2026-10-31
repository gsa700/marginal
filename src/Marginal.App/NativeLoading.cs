// Marginal — where the system's native libraries are looked for.
//
// A [DllImport("libpulse-simple.so.0")] by a bare name is looked for in the
// program's own folder first. A copy run from where it was unpacked, beside
// whatever else is there (Downloads), would load a library of that name
// planted beside it: the review's Native AOT probe ran a planted
// libpulse-simple.so.0 (security review 2026-10-31, C2). Avalonia's X11,
// Wayland, OpenGL and Windows imports (libX11.so.6, libEGL.so.1, dwmapi.dll...)
// work the same way. The release zips now unpack into a folder of their own;
// this is the second half:
//
//   * The libraries Marginal ships beside itself (Skia, HarfBuzz, ANGLE) are
//     loaded as before, from the program's folder.
//   * Every other library these assemblies ask for is looked for in the
//     system's places first (dlopen's own search on Linux, System32 on
//     Windows), so a system library always wins over a file beside the program.
//   * Only a library the system does not have at all falls back to the
//     default search, as before: nothing that worked stops working.
//
// The Mac needs none of this: the hardened runtime's library validation
// refuses any library not signed by Apple or by Dave's Developer ID.

using System.Reflection;
using System.Runtime.InteropServices;

namespace Marginal.App;

public static class NativeLoading
{
    /// Libraries that travel beside the program (InstallLayout.NativeLibraries).
    private static readonly string[] Beside = ["libSkiaSharp", "libHarfBuzzSharp", "av_libglesv2", "libAvaloniaNative"];

    /// The assemblies whose native imports are steered: ours, and Avalonia's
    /// platform layers. One that is not part of this build is passed over.
    private static readonly string[] Assemblies =
    [
        "Marginal.Playback", "Avalonia.Base", "Avalonia.X11", "Avalonia.Wayland", "Avalonia.FreeDesktop",
        "Avalonia.OpenGL", "Avalonia.Win32", "Avalonia.Skia", "Avalonia.Controls",
    ];

    public static void Install()
    {
        if (OperatingSystem.IsMacOS()) return;
        foreach (var name in Assemblies)
        {
            try { NativeLibrary.SetDllImportResolver(Assembly.Load(name), Resolve); }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or InvalidOperationException or ArgumentException) { }
        }
    }

    /// A handle from the system's own places, or zero for the default search.
    public static IntPtr Resolve(string library, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (IsBeside(library)) return IntPtr.Zero;
        return NativeLibrary.TryLoad(library, assembly, DllImportSearchPath.System32, out var handle) ? handle : IntPtr.Zero;
    }

    public static bool IsBeside(string library)
    {
        var name = Path.GetFileName(library);
        return Beside.Any(b => name.StartsWith(b, StringComparison.OrdinalIgnoreCase));
    }
}
