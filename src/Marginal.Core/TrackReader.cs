// Marginal — what one music file holds, read and never written.
//
// Everything comes from the RAW frames: the ID3v2 frames, the Vorbis comments,
// the MP4 atoms, each under the key it is really stored as. The friendly
// fields (FieldMap) are looked up in those same frames, so the Fields view and
// the Raw view can never disagree about a file (spec §2, rule 8).
//
// TagLib# opens a file for reading only; it asks for write access only when
// saved, and nothing here saves.

using System.Text.RegularExpressions;

namespace Marginal.Core;

public enum AudioFormat { Flac, Mp3, M4a }

public enum LyricsKind { None, Plain, Synced }

/// One frame or comment as stored: `Key` is what the format calls it.
/// `Junk` marks what a clean-up would offer to remove (store and player
/// leftovers, not music metadata).
public sealed record RawFrame(string Key, string Value, bool Junk = false);

public sealed record EmbeddedPicture(string Kind, int? Width, int? Height, string Mime, long Bytes)
{
    public string Size => Width is { } w && Height is { } h ? $"{w}×{h}" : "unknown size";
}

public sealed record TrackFile(
    string Path,
    AudioFormat Format,
    TimeSpan Duration,
    int Bitrate,
    string TagVersions,
    bool HasId3v1,
    bool HasApe,
    IReadOnlyList<RawFrame> Raw,
    IReadOnlyList<EmbeddedPicture> Pictures,
    LyricsKind Lyrics,
    string? Problem)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    /// The .lrc beside the track, where synced lyrics are often kept.
    public string LrcPath => System.IO.Path.ChangeExtension(Path, ".lrc");

    /// The file's cover: the front picture if one is typed so, else the first
    /// that reads as an image — AlbumWall's rule (ImageSize.Cover), since many
    /// files type their only cover "Other".
    public EmbeddedPicture? EmbeddedCover() =>
        Pictures.FirstOrDefault(p => p.Kind == "front" && p.Width is not null)
        ?? Pictures.FirstOrDefault(p => p.Width is not null);

    /// The value of a field as stored, or null when the file has none.
    public string? Get(Field field)
    {
        var tagFormat = Format switch { AudioFormat.Flac => TagFormat.Flac, AudioFormat.Mp3 => TagFormat.Id3v2, _ => TagFormat.Mp4 };
        string? Find(string key) => Raw.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

        var value = Find(field.KeyIn(tagFormat));
        if (value is null && field.Id3v23 is not null && tagFormat == TagFormat.Id3v2) value = Find(field.Id3v23);
        // Older FLAC taggers wrote lyrics as UNSYNCEDLYRICS (AlbumWall reads both).
        if (value is null && field == FieldMap.Lyrics && tagFormat == TagFormat.Flac) value = Find("UNSYNCEDLYRICS");
        // FLAC keeps the totals in a field of their own; show them as "6/9", as ID3 and MP4 do.
        if (value is not null && tagFormat == TagFormat.Flac)
        {
            var total = field == FieldMap.Track ? Find("TRACKTOTAL") ?? Find("TOTALTRACKS")
                      : field == FieldMap.Disc ? Find("DISCTOTAL") ?? Find("TOTALDISCS") : null;
            if (total is not null && !value.Contains('/')) value = $"{value}/{total}";
        }
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

public static partial class TrackReader
{
    public static readonly string[] Extensions = [".flac", ".mp3", ".m4a"];

    public static bool IsMusic(string path) =>
        Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
        // macOS AppleDouble files on shares and USB disks: "._01 Track.mp3".
        && !System.IO.Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal)
        // TagWriter's own working copy, should one ever be left behind.
        && !System.IO.Path.GetFileName(path).StartsWith(TagWriter.TempPrefix, StringComparison.Ordinal);

    public static AudioFormat? FormatOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".flac" => AudioFormat.Flac,
        ".mp3" => AudioFormat.Mp3,
        ".m4a" => AudioFormat.M4a,
        _ => null,
    };

    /// Lines that start with an LRC time code: "[01:12.40]".
    // [ \t]*, not \s*: \s also matches the line ends, and on a lyric of blank
    // lines that made every match attempt run on to the end (quadratic: 10 s
    // for a 600 KB tag; security review 2026-10-31, A4).
    [GeneratedRegex(@"^[ \t]*\[\d{1,3}:\d{2}([.:]\d{1,3})?\]", RegexOptions.Multiline)]
    private static partial Regex LrcLine();

    public static LyricsKind KindOf(string? lyrics) =>
        string.IsNullOrWhiteSpace(lyrics) ? LyricsKind.None
        : LrcLine().IsMatch(lyrics) ? LyricsKind.Synced
        : LyricsKind.Plain;

    /// TagLib#'s reader for a file, after the checks that keep a crafted file
    /// from taking the app down: an MP4 nested too deep is refused
    /// (Mp4Nesting). Every TagLib# open goes through here.
    public static TagLib.File Open(string path)
    {
        if (!SafeFiles.HasContent(path))
            throw new TagLib.CorruptFileException("not a plain file, or empty");
        if (FormatOf(path) == AudioFormat.M4a && !Mp4Nesting.IsSafe(path))
            throw new TagLib.CorruptFileException("boxes nested deeper than any real file");
        return TagLib.File.Create(path);
    }

    public static TrackFile Read(string path)
    {
        var format = FormatOf(path) ?? AudioFormat.Mp3;
        try
        {
            using var file = Open(path);
            var raw = new List<RawFrame>();
            var versions = new List<string>();

            switch (file.GetTag(TagLib.TagTypes.Id3v2) as TagLib.Id3v2.Tag)
            {
                case { } id3:
                    versions.Add($"v2.{id3.Version}");
                    raw.AddRange(Id3Frames(id3));
                    break;
            }
            if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            {
                versions.Add("Vorbis");
                foreach (var name in xiph)
                {
                    if (name.Equals("METADATA_BLOCK_PICTURE", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var value in xiph.GetField(name))
                        raw.Add(new RawFrame(name, value, IsJunkName(name)));
                }
            }
            if (file.GetTag(TagLib.TagTypes.Apple) is TagLib.Mpeg4.AppleTag apple)
            {
                versions.Add("MP4");
                raw.AddRange(AppleAtoms(apple));
            }

            var types = file.TagTypesOnDisk;
            var hasV1 = types.HasFlag(TagLib.TagTypes.Id3v1);
            var hasApe = types.HasFlag(TagLib.TagTypes.Ape);
            if (hasV1) versions.Add("v1");
            if (hasApe) versions.Add("APE");

            var pictures = file.Tag.Pictures.Select(Picture).ToList();
            var lyrics = file.Tag.Lyrics;
            var kind = KindOf(lyrics);
            // A synced .lrc beside the track counts too (spec §3.2).
            if (kind != LyricsKind.Synced && System.IO.File.Exists(System.IO.Path.ChangeExtension(path, ".lrc")))
                kind = LyricsKind.Synced;

            var p = file.Properties;
            return new TrackFile(path, format, p?.Duration ?? TimeSpan.Zero, p?.AudioBitrate ?? 0,
                string.Join(" + ", versions), hasV1, hasApe, raw, pictures, kind, null);
        }
        catch (Exception ex)
        {
            return new TrackFile(path, format, TimeSpan.Zero, 0, "", false, false, [], [], LyricsKind.None, ex.Message);
        }
    }

    private static EmbeddedPicture Picture(TagLib.IPicture p)
    {
        var data = p.Data?.Data ?? [];
        var size = ImageSize.Read(ImageSize.Repair(data));
        var kind = p.Type switch
        {
            TagLib.PictureType.FrontCover => "front",
            TagLib.PictureType.BackCover => "back",
            TagLib.PictureType.NotAPicture => "not a picture",
            _ => p.Type.ToString().ToLowerInvariant(),
        };
        return new EmbeddedPicture(kind, size?.Width, size?.Height, p.MimeType ?? "", data.LongLength);
    }

    /// Store and player leftovers: not music metadata, and what "Clean junk
    /// tags" offers to remove. Kept short and certain; anything unknown stays.
    private static bool IsJunkName(string key) =>
        key.StartsWith("PRIV", StringComparison.OrdinalIgnoreCase)
        || key.Contains("iTunNORM", StringComparison.OrdinalIgnoreCase)
        || key.Contains("iTunSMPB", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Amazon", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<RawFrame> Id3Frames(TagLib.Id3v2.Tag id3) => id3.GetFrames().Select(Id3Raw);

    /// One ID3v2 frame as the Raw view shows it. TagWriter matches frames to
    /// remove by this, so what is removed is exactly what was shown.
    public static RawFrame Id3Raw(TagLib.Id3v2.Frame frame)
    {
        var id = frame.FrameId.ToString();
        switch (frame)
        {
            case TagLib.Id3v2.UserTextInformationFrame txxx:
                var key = $"TXXX:{txxx.Description}";
                return new RawFrame(key, string.Join("; ", txxx.Text), IsJunkName(key));
            case TagLib.Id3v2.TextInformationFrame text:
                return new RawFrame(id, string.Join("; ", text.Text));
            case TagLib.Id3v2.UnsynchronisedLyricsFrame uslt:
                return new RawFrame(id, uslt.Text ?? "");
            case TagLib.Id3v2.CommentsFrame comm:
                var c = comm.Text ?? "";
                return new RawFrame(string.IsNullOrEmpty(comm.Description) ? id : $"{id}:{comm.Description}", c,
                    IsJunkName(comm.Description ?? "") || c.Contains("Amazon.com", StringComparison.OrdinalIgnoreCase));
            case TagLib.Id3v2.AttachmentFrame pic:
                var pp = Picture(pic);
                return new RawFrame(id, $"{pp.Kind} · {pp.Size} · {ShortMime(pp.Mime)} · {Bytes(pp.Bytes)}");
            case TagLib.Id3v2.PrivateFrame priv:
                return new RawFrame(id, $"{priv.Owner} ({priv.PrivateData?.Count ?? 0} bytes)", Junk: true);
            case TagLib.Id3v2.UniqueFileIdentifierFrame ufid:
                return new RawFrame($"UFID:{ufid.Owner}", ufid.Identifier is { } d ? d.ToString(TagLib.StringType.Latin1) : "");
            default:
                return new RawFrame(id, frame.ToString() ?? "");
        }
    }

    private static IEnumerable<RawFrame> AppleAtoms(TagLib.Mpeg4.AppleTag apple)
    {
        foreach (var box in apple)
        {
            var type = box.BoxType.ToString(TagLib.StringType.Latin1);
            if (type == "----")
            {
                // Freeform: "----:<mean>:<name>", the value in its data child.
                string? mean = null, name = null, value = null;
                foreach (var child in box.Children)
                {
                    var ct = child.BoxType.ToString(TagLib.StringType.Latin1);
                    if (child is TagLib.Mpeg4.AppleAdditionalInfoBox info)
                    {
                        if (ct == "mean") mean = info.Text;
                        else if (ct == "name") name = info.Text;
                    }
                    else if (child is TagLib.Mpeg4.AppleDataBox data) value = data.Text;
                }
                var key = $"----:{mean}:{name}";
                yield return new RawFrame(key, value ?? "", IsJunkName(key));
                continue;
            }
            foreach (var child in box.Children)
            {
                if (child is not TagLib.Mpeg4.AppleDataBox data) continue;
                yield return new RawFrame(type, type switch
                {
                    "trkn" or "disk" => NumberPair(data.Data),
                    "covr" => CoverAtom(data),
                    _ => data.Text ?? $"({data.Data.Count} bytes)",
                });
            }
        }
    }

    /// trkn and disk: two reserved bytes, number and total as 16-bit big-endian.
    private static string NumberPair(TagLib.ByteVector d)
    {
        if (d.Count < 6) return "";
        var n = (d[2] << 8) | d[3];
        var total = (d[4] << 8) | d[5];
        return total > 0 ? $"{n}/{total}" : n.ToString();
    }

    private static string CoverAtom(TagLib.Mpeg4.AppleDataBox data)
    {
        var size = ImageSize.Read(data.Data.Data);
        var kind = data.Flags == 14 ? "png" : "jpg";
        return $"front · {(size is { } s ? $"{s.Width}×{s.Height}" : "unknown size")} · {kind} · {Bytes(data.Data.Count)}";
    }

    private static string ShortMime(string mime) => mime switch
    {
        "image/jpeg" or "image/jpg" => "jpg",
        "image/png" => "png",
        "" => "?",
        _ => mime,
    };

    public static string Bytes(long n) => n switch
    {
        < 1024 => $"{n} B",
        < 1024 * 1024 => $"{n / 1024.0:0.#} KB",
        _ => $"{n / 1048576.0:0.#} MB",
    };
}
