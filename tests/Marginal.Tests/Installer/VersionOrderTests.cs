using Marginal.Core;

namespace Marginal.Tests;

public class VersionOrderTests
{
    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("v0.2.0", "0.1.9", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.1.0", "0.2.0", false)]
    [InlineData("1.0.0", "1.0.0-beta1", true)]      // the release outranks its pre-releases
    [InlineData("1.0.0-beta2", "1.0.0-beta10", false)]   // numbers, not text
    [InlineData("1.0.0-beta10", "1.0.0-beta2", true)]
    [InlineData("1.0", "1.0.0", false)]              // the same version
    [InlineData("1.0.0+abc", "1.0.0", false)]        // build metadata ignored
    [InlineData("garbage", "0.1.0", false)]          // unparseable is never "newer"
    [InlineData("0.2.0", "", false)]
    public void IsNewer(string candidate, string current, bool expected) =>
        Assert.Equal(expected, VersionOrder.IsNewer(candidate, current));
}
