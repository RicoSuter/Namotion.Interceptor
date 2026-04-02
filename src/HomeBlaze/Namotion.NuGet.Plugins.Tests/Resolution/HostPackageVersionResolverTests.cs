using global::NuGet.Versioning;
using Xunit;

using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Loading;
using Namotion.NuGet.Plugins.Repository;
using Namotion.NuGet.Plugins.Resolution;

namespace Namotion.NuGet.Plugins.Tests.Resolution;

public class HostPackageVersionResolverTests
{
    [Fact]
    public void WhenSinglePluginNeedsPackage_ThenUsesItsRange()
    {
        // Arrange
        var requirements = new Dictionary<string, List<(string PluginName, VersionRange Range)>>
        {
            ["SharedLib"] = [("PluginA", VersionRange.Parse("[1.2.0, 2.0.0)"))]
        };

        // Act
        var result = HostPackageVersionResolver.ResolveVersions(requirements);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.ResolvedRanges["SharedLib"].Satisfies(NuGetVersion.Parse("1.2.0")));
        Assert.True(result.ResolvedRanges["SharedLib"].Satisfies(NuGetVersion.Parse("1.9.0")));
        Assert.False(result.ResolvedRanges["SharedLib"].Satisfies(NuGetVersion.Parse("2.0.0")));
    }

    [Fact]
    public void WhenMultiplePluginsHaveCompatibleRanges_ThenResolvesCommonSubset()
    {
        // Arrange
        var requirements = new Dictionary<string, List<(string PluginName, VersionRange Range)>>
        {
            ["SharedLib"] =
            [
                ("PluginA", VersionRange.Parse("[1.2.0, 2.0.0)")),
                ("PluginB", VersionRange.Parse("[1.3.0, )"))
            ]
        };

        // Act
        var result = HostPackageVersionResolver.ResolveVersions(requirements);

        // Assert
        Assert.True(result.Success);
        Assert.False(result.ResolvedRanges["SharedLib"].Satisfies(NuGetVersion.Parse("1.2.0")));
        Assert.True(result.ResolvedRanges["SharedLib"].Satisfies(NuGetVersion.Parse("1.3.0")));
        Assert.True(result.ResolvedRanges["SharedLib"].Satisfies(NuGetVersion.Parse("1.9.0")));
    }

    [Fact]
    public void WhenPluginsHaveIncompatibleRanges_ThenReturnsConflict()
    {
        // Arrange
        var requirements = new Dictionary<string, List<(string PluginName, VersionRange Range)>>
        {
            ["SharedLib"] =
            [
                ("PluginA", VersionRange.Parse("[1.0.0, 2.0.0)")),
                ("PluginB", VersionRange.Parse("[3.0.0, )"))
            ]
        };

        // Act
        var result = HostPackageVersionResolver.ResolveVersions(requirements);

        // Assert
        Assert.False(result.Success);
        Assert.Single(result.Conflicts);
        Assert.Equal("SharedLib", result.Conflicts[0].AssemblyName);
    }

    [Fact]
    public void WhenMultiplePackagesWithMixedCompatibility_ThenReportsAllConflicts()
    {
        // Arrange
        var requirements = new Dictionary<string, List<(string PluginName, VersionRange Range)>>
        {
            ["LibA"] =
            [
                ("P1", VersionRange.Parse("[1.0.0, 2.0.0)")),
                ("P2", VersionRange.Parse("[1.5.0, 2.0.0)"))
            ],
            ["LibB"] =
            [
                ("P1", VersionRange.Parse("[1.0.0, 2.0.0)")),
                ("P2", VersionRange.Parse("[3.0.0, )"))
            ]
        };

        // Act
        var result = HostPackageVersionResolver.ResolveVersions(requirements);

        // Assert
        Assert.False(result.Success);
        Assert.Single(result.Conflicts); // Only LibB conflicts
        Assert.True(result.ResolvedRanges.ContainsKey("LibA")); // LibA resolved fine
    }
}
