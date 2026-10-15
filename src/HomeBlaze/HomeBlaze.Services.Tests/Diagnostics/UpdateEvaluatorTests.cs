using HomeBlaze.Services.Diagnostics;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class UpdateEvaluatorTests
{
    private static readonly Uri ReleaseUrl = new("https://github.com/owner/repo/releases/tag/v0.6.0");

    [Theory]
    [InlineData("0.6.0", "0.5.0", true)]
    [InlineData("0.5.0", "0.5.0", false)]
    [InlineData("0.5.0", "0.6.0-beta.1", false)]
    [InlineData("0.6.0", "0.6.0-beta.1", true)]
    [InlineData("1.0.0.1", "1.0.0", true)]
    [InlineData("not a version", "0.5.0", false)]
    [InlineData("0.6.0+abc", "0.6.0", false)]
    [InlineData("0.4.0", "0.5.0", false)]
    public void WhenVersionsAreCompared_ThenSemanticVersionOrderingApplies(string candidate, string current, bool expected)
    {
        // Act
        var isNewer = UpdateEvaluator.IsNewer(candidate, current);

        // Assert
        Assert.Equal(expected, isNewer);
    }

    [Fact]
    public void WhenANewerReleaseExists_ThenAnUpdateIsAvailable()
    {
        // Arrange
        var release = new LatestRelease("0.6.0", new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), ReleaseUrl);

        // Act
        var updateAvailable = UpdateEvaluator.IsUpdateAvailable("0.5.0", release);

        // Assert
        Assert.True(updateAvailable);
    }

    [Fact]
    public void WhenNoReleaseExists_ThenNoUpdateIsAvailable()
    {
        // Act
        var updateAvailable = UpdateEvaluator.IsUpdateAvailable("0.5.0", release: null);

        // Assert
        Assert.False(updateAvailable);
    }

    [Fact]
    public void WhenTheRunningVersionDoesNotParse_ThenUpdateAvailabilityIsUnknown()
    {
        // Arrange
        var release = new LatestRelease("0.6.0", null, ReleaseUrl);

        // Act
        var updateAvailable = UpdateEvaluator.IsUpdateAvailable("not a version", release);

        // Assert
        Assert.Null(updateAvailable);
    }
}
