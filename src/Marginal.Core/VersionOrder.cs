// FROM Deadwax (Deadwax.Core/VersionOrder.cs, 704ed87, 2026-10-31), renamed to Marginal throughout.
// Dates and findings in the comments are Deadwax's history; a fix to the
// install or update logic goes to both (and AlbumWall's, from which Deadwax's came).
//
// Ported unchanged from AlbumWall (AlbumWall.Domain/VersionOrder.cs), itself from
// FlexPad.Core, so every program in the family orders release versions alike.
// Covered by VersionOrderTests here.
using System.Globalization;
using System.Text.RegularExpressions;

namespace Marginal.Core;

/// <summary>
/// Orders release versions the way semantic versioning says to, including pre-release suffixes.
///
/// The updater used to compare with <see cref="Version"/> after truncating at the first dash, which
/// was fine while every release was <c>X.Y.Z-beta</c> — the numbers always moved. It stops being fine
/// the moment the numbers hold still and the suffix carries the difference: <c>1.0.0-beta1</c>,
/// <c>1.0.0-beta2</c> and <c>1.0.0</c> all truncate to <c>1.0.0</c>, so nobody on beta1 would ever be
/// offered beta2 or the final release. The app would tell them they were current, indefinitely.
///
/// Two rules do most of the work, and both are easy to get wrong:
///   - A pre-release ranks **below** the same version without one: 1.0.0-beta1 &lt; 1.0.0.
///   - Trailing digits compare as numbers, not text. Plain lexical ordering puts <c>beta10</c> before
///     <c>beta2</c>, which is the classic way a beta series breaks on its tenth build.
/// </summary>
public static class VersionOrder
{
    /// <summary>Splits an identifier into its alphabetic prefix and trailing digits: "beta10" → ("beta", 10).</summary>
    private static readonly Regex Ident = new(@"^(?<alpha>[A-Za-z-]*)(?<num>\d*)$", RegexOptions.Compiled);

    /// <summary>
    /// Compare two version strings. Leading <c>v</c> is ignored and build metadata after <c>+</c> is
    /// dropped, so a release tag and the version baked into an assembly compare directly. Returns null
    /// if either side has no parseable numeric core — callers should treat that as "don't know", never
    /// as "not newer".
    /// </summary>
    public static int? Compare(string? a, string? b)
    {
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);
        if (coreA is null || coreB is null) return null;

        for (var i = 0; i < Math.Max(coreA.Length, coreB.Length); i++)
        {
            var x = i < coreA.Length ? coreA[i] : 0;   // "1.0" and "1.0.0" are the same version
            var y = i < coreB.Length ? coreB[i] : 0;
            if (x != y) return x.CompareTo(y);
        }

        // Same numbers: a pre-release loses to the plain release.
        if (preA.Length == 0 && preB.Length == 0) return 0;
        if (preA.Length == 0) return 1;
        if (preB.Length == 0) return -1;

        for (var i = 0; i < Math.Min(preA.Length, preB.Length); i++)
        {
            var cmp = CompareIdentifier(preA[i], preB[i]);
            if (cmp != 0) return cmp;
        }

        // All shared identifiers equal: the longer suffix is the later one (beta < beta.1).
        return preA.Length.CompareTo(preB.Length);
    }

    /// <summary>True when <paramref name="candidate"/> is strictly newer than <paramref name="current"/>.</summary>
    /// <remarks>
    /// Unparseable on either side is false, not true: the cost of missing an update is a user who
    /// updates later, and the cost of a wrong "newer" is every user being offered a downgrade.
    /// </remarks>
    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;

    private static int CompareIdentifier(string a, string b)
    {
        var ma = Ident.Match(a);
        var mb = Ident.Match(b);

        // Anything that isn't <letters><digits> is compared as plain text — rare, and better than
        // inventing an order for it.
        if (!ma.Success || !mb.Success) return string.CompareOrdinal(a, b);

        var alpha = string.CompareOrdinal(ma.Groups["alpha"].Value, mb.Groups["alpha"].Value);
        if (alpha != 0) return alpha;

        var na = ma.Groups["num"].Value;
        var nb = mb.Groups["num"].Value;
        if (na.Length == 0 && nb.Length == 0) return 0;
        if (na.Length == 0) return -1;   // "beta" precedes "beta1"
        if (nb.Length == 0) return 1;

        // Compared as numbers, so beta2 precedes beta10 rather than following it.
        return long.TryParse(na, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ia)
               && long.TryParse(nb, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ib)
            ? ia.CompareTo(ib)
            : string.CompareOrdinal(na, nb);
    }

    /// <summary>Split "v1.0.0-beta1+abc" into numeric core [1,0,0] and pre-release identifiers ["beta1"].</summary>
    private static (long[]? Core, string[] Pre) Split(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return (null, []);

        var s = version.Trim().TrimStart('v', 'V');

        var plus = s.IndexOf('+');            // build metadata is ignored for ordering
        if (plus >= 0) s = s[..plus];

        var dash = s.IndexOf('-');
        var corePart = dash >= 0 ? s[..dash] : s;
        var prePart = dash >= 0 ? s[(dash + 1)..] : "";

        var fields = corePart.Split('.');
        var core = new long[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            if (!long.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out core[i]))
                return (null, []);
        }
        if (core.Length == 0) return (null, []);

        return (core, prePart.Length == 0 ? [] : prePart.Split('.'));
    }
}
