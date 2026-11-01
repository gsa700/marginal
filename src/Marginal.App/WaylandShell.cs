// COPIED from Deadwax (src/Deadwax.App/WaylandShell.cs, itself from AlbumWall), 2026-10-31;
// keep the three in step. Names changed only.
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Avalonia.Controls;

namespace Marginal.App;

/// <summary>
/// Tells the Wayland compositor which application a window belongs to, the Wayland counterpart of
/// <see cref="WindowsShell.ClaimIdentity"/>.
///
/// On Wayland the compositor does not take an icon from the window. It matches the toplevel's
/// app_id to a .desktop file and shows that file's icon in the dash, the panel and Alt+Tab. X11
/// matched on WM_CLASS, which Avalonia sets, so the icon came for free under XWayland. Avalonia
/// 12.1.2's Wayland backend never sends xdg_toplevel.set_app_id at all (WAYLAND_DEBUG shows only
/// set_title), so after the move to native Wayland (2b3f43a) GNOME showed a generic icon. Mutter
/// has no xdg-toplevel-icon either, so the app_id is the only way in.
///
/// THIS REACHES INTO AVALONIA'S PRIVATE API: Window.PlatformImpl -> WindowImpl._surfaceProxy
/// (WXdgTopLevelProxy) -> ProxyTarget (WXdgTopLevel) -> _xdgTopLevel (NWayland's XdgToplevel),
/// whose generated binding has SetAppId even though Avalonia never calls it. Every step is checked
/// and any failure is logged and ignored: the worst case is the generic icon we already had. When
/// Avalonia sets an app_id itself, delete this.
/// </summary>
internal static class WaylandShell
{
    /// The .desktop file's name without the extension, which is what GNOME matches on.
    public const string AppId = "marginal";   // InstallService and tools/install.sh write marginal.desktop

    // Each member reached below, kept by name so a trimmed or Native AOT build
    // still has it; a member Avalonia renames is still caught by the checks.
    // The analyzer cannot follow GetType() on objects of private types, hence
    // the suppression: every lookup is checked and a miss only costs the icon.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(TopLevel))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, "Avalonia.Wayland.WindowImpl", "Avalonia.Wayland")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, "Avalonia.Wayland.Server.Persistent.WXdgTopLevelProxy", "Avalonia.Wayland")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties, "Avalonia.Wayland.Server.Persistent.WSurfaceProxy", "Avalonia.Wayland")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, "Avalonia.Wayland.Server.Persistent.WXdgTopLevel", "Avalonia.Wayland")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, "NWayland.Protocols.XdgShell.XdgToplevel", "NWayland")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Private Avalonia members, kept by the DynamicDependency attributes; every lookup is checked.")]
    public static void ClaimIdentity(Window window)
    {
        if (!Program.NativeWayland) return;
        try
        {
            const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var impl = typeof(TopLevel).GetProperty("PlatformImpl", Any)?.GetValue(window)
                       ?? throw new InvalidOperationException("no PlatformImpl");
            var proxy = impl.GetType().GetField("_surfaceProxy", Any)?.GetValue(impl)
                        ?? throw new InvalidOperationException($"no _surfaceProxy on {impl.GetType().Name}");
            // ProxyTarget is declared twice (the proxy and its base, one hiding the
            // other), so a lookup by name alone is ambiguous: try each and keep the
            // one whose target carries the toplevel.
            var toplevel = proxy.GetType().GetProperties(Any)
                               .Where(p => p.Name == "ProxyTarget" && p.GetIndexParameters().Length == 0)
                               .Select(p => p.GetValue(proxy))
                               .Select(t => t?.GetType().GetField("_xdgTopLevel", Any)?.GetValue(t))
                               .FirstOrDefault(t => t is not null)
                           ?? throw new InvalidOperationException($"no ProxyTarget with an _xdgTopLevel on {proxy.GetType().Name}");
            var setAppId = toplevel.GetType().GetMethod("SetAppId", [typeof(string)])
                           ?? throw new InvalidOperationException($"no SetAppId on {toplevel.GetType().Name}");

            setAppId.Invoke(toplevel, [AppId]);
            Console.WriteLine($"[marginal] wayland app_id -> {AppId}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[marginal] wayland app_id NOT set ({e.GetBaseException().Message}); the icon will be generic");
        }
    }
}
