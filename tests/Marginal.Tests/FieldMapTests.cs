using Marginal.Core;

namespace Marginal.Tests;

public class FieldMapTests
{
    [Fact]
    public void Every_field_has_one_name_and_one_key_per_format()
    {
        Assert.Equal(FieldMap.All.Count, FieldMap.All.Select(f => f.Name).Distinct().Count());
        foreach (var format in Enum.GetValues<TagFormat>())
            Assert.Equal(FieldMap.All.Count, FieldMap.All.Select(f => f.KeyIn(format)).Distinct().Count());
    }

    [Theory]
    [InlineData(TagFormat.Flac, "ALBUMARTIST")]
    [InlineData(TagFormat.Id3v2, "TPE2")]
    [InlineData(TagFormat.Mp4, "aART")]
    public void Album_artist_is_stored_as_the_spec_says(TagFormat format, string key) =>
        Assert.Equal(key, FieldMap.AlbumArtist.KeyIn(format));

    [Fact]
    public void Dates_differ_between_id3_versions()
    {
        Assert.Equal("TDRC", FieldMap.Date.KeyIn(TagFormat.Id3v2));
        Assert.Equal("TYER", FieldMap.Date.KeyIn(TagFormat.Id3v2, id3v23: true));
        Assert.Equal("TORY", FieldMap.OriginalDate.KeyIn(TagFormat.Id3v2, id3v23: true));
        // Fields without a v2.3 key are the same in both.
        Assert.Equal("TPE2", FieldMap.AlbumArtist.KeyIn(TagFormat.Id3v2, id3v23: true));
    }
}
