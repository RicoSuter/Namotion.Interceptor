using NuGet.Versioning;
using Xunit;

using Namotion.NuGet.Plugins.Configuration;

namespace Namotion.NuGet.Plugins.Tests;

public class NuGetVersionCompatibilityTests
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
        var result = NuGetVersionCompatibility.IsCompatible(requiredVersion, availableVersion);

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
        var result = NuGetVersionCompatibility.IsCompatible(requiredVersion, availableVersion);

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
        var result = NuGetVersionCompatibility.IsCompatible(requiredVersion, availableVersion);

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
        var conflicts = NuGetVersionCompatibility.FindConflicts(
            pluginDependencies, hostResolver, "TestPlugin");

        // Assert
        Assert.Equal(2, conflicts.Count);
        Assert.Contains(conflicts, c => c.PackageName == "LibA");
        Assert.Contains(conflicts, c => c.PackageName == "LibB");
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
        var conflicts = NuGetVersionCompatibility.FindConflicts(
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
        var conflicts = NuGetVersionCompatibility.FindConflicts(
            pluginDependencies, hostResolver, "TestPlugin");

        // Assert
        Assert.Empty(conflicts);
    }

    [Theory]
    [InlineData("0.0.0", "0.0.0", true)]   // zero versions
    [InlineData("0.1.0", "0.1.0", true)]   // pre-1.0 exact
    [InlineData("0.1.0", "0.2.0", true)]   // pre-1.0 minor higher
    [InlineData("0.2.0", "0.1.0", false)]  // pre-1.0 minor lower
    public void WhenPreReleaseVersions_ThenValidatesCorrectly(
        string required, string available, bool expected)
    {
        // Act
        var result = NuGetVersionCompatibility.IsCompatible(
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
        var result = NuGetVersionCompatibility.IsCompatible(required, available);

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
        var conflicts = NuGetVersionCompatibility.FindConflicts(
            new Dictionary<string, NuGetVersion>(), resolver, "Plugin");

        // Assert
        Assert.Empty(conflicts);
    }
}
