// FROM Deadwax (Deadwax.Core/ReleaseFeed.cs, 704ed87, 2026-10-31), renamed to Marginal throughout.
// Dates and findings in the comments are Deadwax's history; a fix to the
// install or update logic goes to both (and AlbumWall's, from which Deadwax's came).
//
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Marginal.Core;

/// The two release channels (spec: release model, 2026-10-10). Every build is
/// published as a GitHub pre-release: that is Edge. A build that has run on Edge
/// without trouble is promoted, the same binary with its pre-release flag
/// cleared: that is Stable. GitHub's /releases/latest never returns a
/// pre-release, so Stable reads that; Edge reads the release list and takes the
/// newest version in it, pre-release or not.
public static partial class ReleaseFeed
{
    /// Only tags shaped exactly vX.Y.Z (what tools/release.sh makes) are app
    /// releases, as in AlbumWall, whose repository also holds libmpv-* engine
    /// pre-releases that must never be offered.
    [GeneratedRegex(@"^v\d+\.\d+\.\d+$")]
    private static partial Regex AppTag();

    public static bool IsAppTag(string? tag) => tag is not null && AppTag().IsMatch(tag);

    /// The newest release in a /releases list by version (VersionOrder), never a
    /// draft; null when the list holds none.
    public static JsonElement? Newest(JsonElement releases)
    {
        if (releases.ValueKind != JsonValueKind.Array) return null;
        JsonElement? best = null;
        string? bestTag = null;
        foreach (var r in releases.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            if (!r.TryGetProperty("tag_name", out var t) || t.GetString() is not { } tag || !IsAppTag(tag)) continue;
            if (bestTag is null || VersionOrder.IsNewer(tag, bestTag)) { best = r; bestTag = tag; }
        }
        return best;
    }
}
