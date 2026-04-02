using global::NuGet.Versioning;
using Xunit;

using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Loading;
using Namotion.NuGet.Plugins.Repository;
using Namotion.NuGet.Plugins.Resolution;

namespace Namotion.NuGet.Plugins.Tests;

public class VersionCompatibilityEdgeCaseTests
{
    [Theory]
    [InlineData("0.0.0", "0.0.0", true)]   // zero versions
    [InlineData("0.1.0", "0.1.0", true)]   // pre-1.0 exact
    [InlineData("0.1.0", "0.2.0", true)]   // pre-1.0 minor higher
    [InlineData("0.2.0", "0.1.0", false)]  // pre-1.0 minor lower
    public void WhenPreReleaseVersions_ThenValidatesCorrectly(
        string required, string available, bool expected)
    {
        // Act
        var result = VersionCompatibility.IsCompatible(
            NuGetVersion.Parse(required), NuGetVersion.Parse(available));

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenHostResolverIsCaseInsensitive_ThenFindsPackages()
    {
        // Arrange
        var resolver = HostDependencyResolver.FromAssemblies(
            ("Microsoft.Extensions.Logging", new Version(9, 0, 0)));

        // Act & Assert
        Assert.True(resolver.Contains("microsoft.extensions.logging"));
        Assert.True(resolver.Contains("MICROSOFT.EXTENSIONS.LOGGING"));
    }

    [Fact]
    public void WhenPrereleaseSuffix_ThenMajorMinorStillChecked()
    {
        // Arrange -- NuGet prerelease versions
        var required = NuGetVersion.Parse("1.0.0-beta1");
        var available = NuGetVersion.Parse("1.0.0");

        // Act
        var result = VersionCompatibility.IsCompatible(required, available);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void WhenFindConflictsWithEmptyDeps_ThenReturnsEmpty()
    {
        // Arrange
        var resolver = HostDependencyResolver.FromAssemblies(
            ("Lib", new Version(1, 0, 0)));

        // Act
        var conflicts = VersionCompatibility.FindConflicts(
            new Dictionary<string, NuGetVersion>(), resolver, "Plugin");

        // Assert
        Assert.Empty(conflicts);
    }
}
