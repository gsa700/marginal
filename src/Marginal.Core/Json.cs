// Marginal — reading web answers that may not have the shape they should.
//
// MusicBrainz, the Cover Art Archive and LRCLIB are trusted to be themselves,
// but an answer can still be wrong: a maintenance page served with 200, a
// number with a fraction, an object where a list belongs. JsonElement throws on
// all of those (TryGetProperty on a non-object, GetInt32 on 1.5 or 3e9,
// EnumerateArray on an object), and one of them reaching a click handler took
// the whole app down (security review 2026-10-31, A2). Everything that maps an
// answer goes through these, which answer "not there" instead of throwing.

using System.Text.Json;

namespace Marginal.Core;

public static class Json
{
    /// The most bytes read from MusicBrainz or LRCLIB: a 100-release browse
    /// with media is well under 1 MB.
    public const long MaxAnswerBytes = 8 * 1024 * 1024;

    /// The property, or default when `e` is not an object or lacks it.
    public static JsonElement Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static bool Has(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out _);

    /// The items of an array property; none when it is missing or not an array.
    public static IEnumerable<JsonElement> Items(JsonElement e, string name) => Items(Get(e, name));

    public static IEnumerable<JsonElement> Items(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];

    public static string? Str(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static int? Int(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var n) ? n : null;

    public static long? Long(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) ? n : null;

    public static double? Double(JsonElement e, string name) =>
        Get(e, name) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;

    public static bool True(JsonElement e, string name) => Get(e, name).ValueKind == JsonValueKind.True;

    /// The answer parsed, or null when it is not JSON at all.
    public static JsonDocument? TryParse(byte[] body)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { return null; }
    }
}
