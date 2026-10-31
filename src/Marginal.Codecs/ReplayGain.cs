// COPIED from AlbumWall (src/AlbumWall.Codecs/ReplayGain.cs, 4219eb0, 2026-10-31): the same
// code in both, namespace changed only. A fix in one goes to the other.
//
// AlbumWall — ReplayGain from a track's tags, for every format: the four
// tags are the same words in FLAC's Vorbis comments, ID3's TXXX frames and
// MP4's freeform atoms.

namespace Marginal.Codecs;

/// ReplayGain from a file's tags, read the way mpv read it, so that a record
/// kept the level it had when mpv played it (until 2026-10-22). mpv played FLAC
/// through FFmpeg's demuxer (`demuxer=lavf`), where FFmpeg parses the four
/// tags (libavformat/replaygain.c) and mpv takes them from there
/// (demux_lavf.c, export_replaygain): a gain counts only with a peak above
/// zero beside it, an album without its own gain takes the track's, and with
/// neither there is no ReplayGain at all.
public readonly record struct ReplayGain(bool Present, float TrackGain, float TrackPeak, float AlbumGain, float AlbumPeak)
{
    public static ReplayGain From(IReadOnlyDictionary<string, string> tags) => FromRaw(Raw(tags));

    /// The four values FFmpeg keeps (ff_replaygain_export), in hundred-
    /// thousandths, a missing gain as int.MinValue and a missing peak as 0.
    public static (int TrackGain, int TrackPeak, int AlbumGain, int AlbumPeak) Raw(IReadOnlyDictionary<string, string> tags) =>
        (Parse(tags, "REPLAYGAIN_TRACK_GAIN", int.MinValue), Parse(tags, "REPLAYGAIN_TRACK_PEAK", 0),
         Parse(tags, "REPLAYGAIN_ALBUM_GAIN", int.MinValue), Parse(tags, "REPLAYGAIN_ALBUM_PEAK", 0));

    /// Whether FFmpeg would keep these at all: a gain of either kind.
    public static bool Any((int TrackGain, int TrackPeak, int AlbumGain, int AlbumPeak) raw) =>
        raw.TrackGain != int.MinValue || raw.AlbumGain != int.MinValue;

    /// From the four values as FFmpeg keeps them, wherever they came from
    /// (tags, or the LAME tag of an MP3), by mpv's rule.
    public static ReplayGain FromRaw((int TrackGain, int TrackPeak, int AlbumGain, int AlbumPeak) raw)
    {
        var (tg, tp, ag, ap) = raw;
        var track = tg != int.MinValue && tp != 0;
        var album = ag != int.MinValue && ap != 0;
        if (!track && !album) return default;

        float trackGain = 0, trackPeak = 1, albumGain = 0, albumPeak = 1;
        if (track) { trackGain = albumGain = tg / 100000f; trackPeak = albumPeak = tp / 100000f; }
        if (album) { albumGain = ag / 100000f; albumPeak = ap / 100000f; }
        return new ReplayGain(true, trackGain, trackPeak, albumGain, albumPeak);
    }

    /// The multiplier, by album or by track: mpv's compute_replaygain,
    /// clipping prevention on (its default), no pre-amp, no fallback gain.
    public float Multiplier(bool album)
    {
        if (!Present) return 1f;
        var (gain, peak) = album ? (AlbumGain, AlbumPeak) : (TrackGain, TrackPeak);
        return Math.Min(MathF.Pow(10f, gain / 20f), 1f / peak);
    }

    /// FFmpeg's parse_value: a whole number, then up to five decimals, in
    /// hundred-thousandths ("-7.53 dB" is -753000; what follows is ignored).
    private static int Parse(IReadOnlyDictionary<string, string> tags, string key, int missing)
    {
        if (!tags.TryGetValue(key, out var text)) return missing;
        var s = text.AsSpan().TrimStart(" \t");
        var negative = s.Length > 0 && s[0] == '-';
        var i = s.Length > 0 && (s[0] == '-' || s[0] == '+') ? 1 : 0;
        long whole = 0;
        for (; i < s.Length && char.IsAsciiDigit(s[i]); i++) whole = Math.Min(whole * 10 + (s[i] - '0'), int.MaxValue);
        long fraction = 0;
        if (i < s.Length && s[i] == '.')
            for (int scale = 10000, j = i + 1; scale > 0 && j < s.Length && char.IsAsciiDigit(s[j]); j++, scale /= 10)
                fraction += scale * (s[j] - '0');
        if (whole > (int.MaxValue - fraction) / 100000) return missing;
        var value = whole * 100000 + fraction;
        return (int)(negative ? -value : value);
    }
}
