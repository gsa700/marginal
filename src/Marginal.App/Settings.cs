// Marginal — the few settings it keeps, in ~/.config/marginal/settings.json
// (Deadwax's place for its own). Updates only, so far.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marginal.App;

public sealed class Settings
{
    /// One small question to GitHub at launch: is there a newer release?
    public bool CheckForUpdates { get; set; } = true;

    /// Edge: every release as it comes out. Off is Stable.
    public bool EdgeChannel { get; set; }

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
