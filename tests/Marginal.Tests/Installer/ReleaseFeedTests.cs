using System.Text.Json;
using Marginal.Core;

namespace Marginal.Tests;

/// The Edge channel's pick from GitHub's /releases list (ReleaseFeed).
public class ReleaseFeedTests
{
    private static string? Pick(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ReleaseFeed.Newest(doc.RootElement)?.GetProperty("tag_name").GetString();
    }

    [Fact]
    public void Edge_takes_the_newest_version_prerelease_or_not()
    {
        Assert.Equal("v0.2.11", Pick("""
            [{"tag_name":"v0.2.10","prerelease":false,"draft":false},
             {"tag_name":"v0.2.11","prerelease":true,"draft":false},
             {"tag_name":"v0.2.9","prerelease":false,"draft":false}]
            """));
    }

    [Fact]
    public void Edge_never_takes_a_draft_or_a_tag_that_is_not_a_version()
    {
        Assert.Equal("v0.2.10", Pick("""
            [{"tag_name":"v0.2.12","prerelease":true,"draft":true},
             {"tag_name":"nightly","prerelease":true,"draft":false},
             {"tag_name":"v0.2.10","prerelease":false,"draft":false}]
            """));
    }

    [Fact]
    public void Numbers_compare_as_numbers()
    {
        Assert.Equal("v0.2.10", Pick("""[{"tag_name":"v0.2.9"},{"tag_name":"v0.2.10"}]"""));
    }

    [Fact]
    public void Only_vXYZ_tags_are_app_releases()
    {
        Assert.Equal("v0.2.11", Pick("""
            [{"tag_name":"libmpv-0.41.0-4","prerelease":true},{"tag_name":"v0.2.11","prerelease":true}]
            """));
        Assert.False(ReleaseFeed.IsAppTag("v0.2.12-beta1"));
        Assert.False(ReleaseFeed.IsAppTag("0.2.12"));
        Assert.True(ReleaseFeed.IsAppTag("v0.2.12"));
    }

    [Fact]
    public void Nothing_listed_is_null()
    {
        Assert.Null(Pick("[]"));
        Assert.Null(Pick("""{"message":"Not Found"}"""));
    }
}
