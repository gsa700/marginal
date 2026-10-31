// Marginal — the fields it shows, and what each is called inside a file.
//
// One row per field a person edits, with its key in each format, so the
// Album fields table can say what is really stored ("Stored as": TPE2,
// ALBUMARTIST, aART). The names follow MusicBrainz Picard's mapping, so what
// Marginal writes reads the same in Picard, Navidrome, AlbumWall and most
// players (spec §4). To be checked against Picard's published table when the
// readers and writers are built on it.
//
// Freeform MP4 keys are iTunes "----" atoms: "----:com.apple.iTunes:<NAME>".

namespace Marginal.Core;

public enum TagFormat { Flac, Id3v2, Mp4 }

/// A field, with its key in each format. Id3v23 is set only where ID3v2.3
/// differs from v2.4 (the dates).
public sealed record Field(string Name, string Flac, string Id3v2, string Mp4, string? Id3v23 = null)
{
    public string KeyIn(TagFormat format, bool id3v23 = false) => format switch
    {
        TagFormat.Flac => Flac,
        TagFormat.Id3v2 => id3v23 && Id3v23 is not null ? Id3v23 : Id3v2,
        TagFormat.Mp4 => Mp4,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}

public static class FieldMap
{
    private const string Itunes = "----:com.apple.iTunes:";

    public static readonly Field Title = new("Title", "TITLE", "TIT2", "©nam");
    public static readonly Field Artist = new("Artist", "ARTIST", "TPE1", "©ART");
    public static readonly Field Album = new("Album", "ALBUM", "TALB", "©alb");
    public static readonly Field AlbumArtist = new("Album artist", "ALBUMARTIST", "TPE2", "aART");
    public static readonly Field Track = new("Track", "TRACKNUMBER", "TRCK", "trkn");
    public static readonly Field Disc = new("Disc", "DISCNUMBER", "TPOS", "disk");
    public static readonly Field Date = new("Date", "DATE", "TDRC", "©day", Id3v23: "TYER");
    public static readonly Field OriginalDate = new("Original date", "ORIGINALDATE", "TDOR", Itunes + "ORIGINALDATE", Id3v23: "TORY");
    public static readonly Field Genre = new("Genre", "GENRE", "TCON", "©gen");
    public static readonly Field Label = new("Label", "LABEL", "TPUB", Itunes + "LABEL");
    public static readonly Field CatalogNumber = new("Catalog no.", "CATALOGNUMBER", "TXXX:CATALOGNUMBER", Itunes + "CATALOGNUMBER");
    public static readonly Field Barcode = new("Barcode", "BARCODE", "TXXX:BARCODE", Itunes + "BARCODE");
    public static readonly Field MusicBrainzRelease = new("MusicBrainz release", "MUSICBRAINZ_ALBUMID", "TXXX:MusicBrainz Album Id", Itunes + "MusicBrainz Album Id");
    public static readonly Field Lyrics = new("Lyrics", "LYRICS", "USLT", "©lyr");

    /// In the order the Album fields table shows them.
    public static readonly IReadOnlyList<Field> All =
    [
        Title, Artist, Album, AlbumArtist, Track, Disc, Date, OriginalDate,
        Genre, Label, CatalogNumber, Barcode, MusicBrainzRelease, Lyrics,
    ];
}
