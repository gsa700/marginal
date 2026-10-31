// Marginal — the few settings it keeps, in ~/.config/marginal/settings.json
// (Deadwax's place for its own): updates, and where the windows were.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marginal.App;

public sealed class Settings
{
    /// One small question to GitHub at launch: is there a newer release?
    public bool CheckForUpdates { get; set; } = true;

    /// Edge: every release as it comes out. Off is Stable.
    public bool EdgeChannel { get; set; }

    // Where the windows were (Deadwax's): the main window's size, corner and
    // whether it was maximized; the Lookup and Art windows' sizes; where
    // Preferences was. Null until first saved: the XAML sizes then. Native
    // Wayland does not let a window place itself, so no corner is kept there.
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public double? LookupWidth { get; set; }
    public double? LookupHeight { get; set; }
    public double? ArtWidth { get; set; }
    public double? ArtHeight { get; set; }
    public int? PrefsX { get; set; }
    public int? PrefsY { get; set; }

    // Everything else that is kept (Keep): pane widths, ticks, choices,
    // and the library folder, tab and view last open.
    public Dictionary<string, double> Panes { get; set; } = [];
    public Dictionary<string, bool> Ticks { get; set; } = [];
    public Dictionary<string, int> Choices { get; set; } = [];
    public string? Folder { get; set; }
    public bool LyricsTab { get; set; }
    public bool RawView { get; set; }

    internal static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "marginal", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.Settings) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJson.Default.Settings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// The settings file's reader and writer, made at build time rather than by
/// reflection at run time, which a Native AOT build cannot do.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
