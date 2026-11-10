using OpenShelf.Update;
using Xunit;

namespace OpenShelf.Update.Tests;

public class PrereleaseTests
{
    [Theory]
    [InlineData("0.1.0-beta.1", true)]
    [InlineData("0.1.0-beta.1+abc123", true)]
    [InlineData("0.0.0-dev", true)]
    [InlineData("0.1.0", false)]
    [InlineData("0.1.0+sha-with-dash", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_version_with_a_suffix_follows_prereleases(string? version, bool expected) =>
        Assert.Equal(expected, VelopackBackend.IsPrerelease(version));
}
