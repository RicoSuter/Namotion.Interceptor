using HomeBlaze.Services.Diagnostics;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class UpdateEvaluatorTests
{
    private static readonly Uri ReleaseUrl = new("https://github.com/owner/repo/releases/tag/v0.6.0");
    private static readonly Uri CompareUrl = new("https://github.com/owner/repo/compare/abc...master");

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
    public void WhenAReleaseRunsAndANewerReleaseExists_ThenAnUpdateIsAvailable()
    {
        // Arrange
        var release = new LatestRelease("0.6.0", new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), ReleaseUrl);

        // Act
        var state = UpdateEvaluator.Evaluate(InstallationChannel.Release, "0.5.0", release, comparison: null);

        // Assert
        Assert.True(state.UpdateAvailable);
        Assert.Equal("0.6.0", state.LatestVersion);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), state.LatestReleaseDate);
        Assert.Equal(ReleaseUrl, state.LatestReleaseUrl);
        Assert.Null(state.CommitsBehind);
        Assert.Null(state.ChangesUrl);
    }

    [Fact]
    public void WhenAReleaseRunsAndNoReleaseExists_ThenNoUpdateIsAvailable()
    {
        // Act
        var state = UpdateEvaluator.Evaluate(InstallationChannel.Release, "0.5.0", release: null, comparison: null);

        // Assert
        Assert.False(state.UpdateAvailable);
        Assert.Null(state.LatestVersion);
    }

    [Fact]
    public void WhenAReleaseRunsAndTheRunningVersionDoesNotParse_ThenUpdateAvailabilityIsUnknown()
    {
        // Arrange
        var release = new LatestRelease("0.6.0", null, ReleaseUrl);

        // Act
        var state = UpdateEvaluator.Evaluate(InstallationChannel.Release, "edge", release, comparison: null);

        // Assert
        Assert.Null(state.UpdateAvailable);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(12, true)]
    public void WhenAnEdgeBuildRuns_ThenTheBranchComparisonDecides(int commitsBehind, bool expected)
    {
        // Arrange
        var release = new LatestRelease("0.6.0", null, ReleaseUrl);
        var comparison = new BranchComparison(commitsBehind, CompareUrl);

        // Act
        var state = UpdateEvaluator.Evaluate(InstallationChannel.Edge, "edge", release, comparison);

        // Assert
        Assert.Equal(expected, state.UpdateAvailable);
        Assert.Equal(commitsBehind, state.CommitsBehind);
        Assert.Equal(CompareUrl, state.ChangesUrl);
        Assert.Equal("0.6.0", state.LatestVersion);
    }

    [Fact]
    public void WhenAnEdgeBuildRunsAndNoComparisonExists_ThenUpdateAvailabilityIsUnknown()
    {
        // Arrange
        var release = new LatestRelease("0.6.0", null, ReleaseUrl);

        // Act
        var state = UpdateEvaluator.Evaluate(InstallationChannel.Edge, "edge", release, comparison: null);

        // Assert
        Assert.Null(state.UpdateAvailable);
    }

    [Fact]
    public void WhenADevelopmentBuildRuns_ThenUpdateAvailabilityIsUnknown()
    {
        // Act
        var state = UpdateEvaluator.Evaluate(InstallationChannel.Development, "1.0.0", release: null, comparison: null);

        // Assert
        Assert.Null(state.UpdateAvailable);
    }
}
