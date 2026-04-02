using global::NuGet.Versioning;
using Xunit;

using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Loading;
using Namotion.NuGet.Plugins.Repository;
using Namotion.NuGet.Plugins.Resolution;

namespace Namotion.NuGet.Plugins.Tests;

public class VersionCompatibilityTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", true)]   // exact match
    [InlineData("1.0.0", "1.1.0", true)]   // host has higher minor
    [InlineData("1.0.0", "1.0.5", true)]   // host has higher patch
    [InlineData("1.2.0", "1.3.0", true)]   // host has higher minor
    public void WhenHostVersionIsCompatible_ThenReturnsTrue(
        string required, string available, bool expected)
    {
        // Arrange
        var requiredVersion = NuGetVersion.Parse(required);
        var availableVersion = NuGetVersion.Parse(available);

        // Act
        var result = VersionCompatibility.IsCompatible(requiredVersion, availableVersion);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("2.0.0", "1.0.0")]   // different major
    [InlineData("1.0.0", "2.0.0")]   // different major (host higher)
    [InlineData("1.4.0", "1.3.0")]   // plugin wants higher minor
    public void WhenHostVersionIsIncompatible_ThenReturnsFalse(string required, string available)
    {
        // Arrange
        var requiredVersion = NuGetVersion.Parse(required);
        var availableVersion = NuGetVersion.Parse(available);

        // Act
        var result = VersionCompatibility.IsCompatible(requiredVersion, availableVersion);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.1", "1.0.0", true)]  // patch difference ignored
    public void WhenPatchVersionsDiffer_ThenAlwaysCompatible(
        string required, string available, bool expected)
    {
        // Arrange
        var requiredVersion = NuGetVersion.Parse(required);
        var availableVersion = NuGetVersion.Parse(available);

        // Act
        var result = VersionCompatibility.IsCompatible(requiredVersion, availableVersion);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenValidatingMultipleConflicts_ThenReturnsAllConflicts()
    {
        // Arrange
        var hostResolver = HostDependencyResolver.FromAssemblies(
            ("LibA", new Version(1, 0, 0)),
            ("LibB", new Version(2, 0, 0)));

        var pluginDependencies = new Dictionary<string, NuGetVersion>
        {
            ["LibA"] = NuGetVersion.Parse("2.0.0"),  // major mismatch
            ["LibB"] = NuGetVersion.Parse("2.1.0"),  // host minor too low
        };

        // Act
        var conflicts = VersionCompatibility.FindConflicts(
            pluginDependencies, hostResolver, "TestPlugin");

        // Assert
        Assert.Equal(2, conflicts.Count);
        Assert.Contains(conflicts, c => c.AssemblyName == "LibA");
        Assert.Contains(conflicts, c => c.AssemblyName == "LibB");
    }

    [Fact]
    public void WhenAllDependenciesCompatible_ThenReturnsEmptyList()
    {
        // Arrange
        var hostResolver = HostDependencyResolver.FromAssemblies(
            ("LibA", new Version(1, 3, 0)),
            ("LibB", new Version(2, 0, 0)));

        var pluginDependencies = new Dictionary<string, NuGetVersion>
        {
            ["LibA"] = NuGetVersion.Parse("1.2.0"),
            ["LibB"] = NuGetVersion.Parse("2.0.0"),
        };

        // Act
        var conflicts = VersionCompatibility.FindConflicts(
            pluginDependencies, hostResolver, "TestPlugin");

        // Assert
        Assert.Empty(conflicts);
    }

    [Fact]
    public void WhenDependencyNotInHost_ThenNotAConflict()
    {
        // Arrange
        var hostResolver = HostDependencyResolver.FromAssemblies(
            ("LibA", new Version(1, 0, 0)));

        var pluginDependencies = new Dictionary<string, NuGetVersion>
        {
            ["LibA"] = NuGetVersion.Parse("1.0.0"),
            ["LibNotInHost"] = NuGetVersion.Parse("5.0.0"),
        };

        // Act
        var conflicts = VersionCompatibility.FindConflicts(
            pluginDependencies, hostResolver, "TestPlugin");

        // Assert
        Assert.Empty(conflicts);
    }
}
