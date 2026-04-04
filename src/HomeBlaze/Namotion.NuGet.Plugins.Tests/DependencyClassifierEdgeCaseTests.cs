using NuGet.Versioning;
using Xunit;

using Namotion.NuGet.Plugins.Configuration;

namespace Namotion.NuGet.Plugins.Tests;

public class DependencyClassifierEdgeCaseTests
{
    [Fact]
    public void WhenPackageNameCaseDiffers_ThenStillClassifiedCorrectly()
    {
        // Arrange
        var hostResolver = HostDependencyResolver.FromAssemblies(
            ("Microsoft.Extensions.Logging", new Version(9, 0, 0)));
        var classifier = new DependencyClassifier(hostResolver, null, ["Plugin"]);

        // Act -- different casing
        var result = classifier.Classify("microsoft.extensions.logging");

        // Assert
        Assert.Equal(DependencyClassification.Host, result);
    }

    [Fact]
    public void WhenHostPatternCaseDiffers_ThenStillMatches()
    {
        // Arrange
        var hostResolver = HostDependencyResolver.FromAssemblies();
        var classifier = new DependencyClassifier(
            hostResolver,
            name => PackageNameMatcher.IsMatchAny(name, ["mycompany.*.abstractions"]),
            ["Plugin"]);

        // Act
        var result = classifier.Classify("MyCompany.Devices.Abstractions");

        // Assert
        Assert.Equal(DependencyClassification.Host, result);
    }

    [Fact]
    public void WhenNoHostResolverAndNoPatterns_ThenEverythingIsPrivateExceptPlugins()
    {
        // Arrange
        var hostResolver = HostDependencyResolver.FromAssemblies();
        var classifier = new DependencyClassifier(hostResolver, null, ["MyPlugin"]);

        // Act
        var pluginResult = classifier.Classify("MyPlugin");
        var depResult = classifier.Classify("SomeDep");

        // Assert
        Assert.Equal(DependencyClassification.Entry, pluginResult);
        Assert.Equal(DependencyClassification.Isolated, depResult);
    }

    [Fact]
    public void WhenClassifyAllWithEmptyDeps_ThenReturnsEmptyDictionary()
    {
        // Arrange
        var classifier = new DependencyClassifier(
            HostDependencyResolver.FromAssemblies(), null, []);

        // Act
        var result = classifier.ClassifyAll(new Dictionary<string, NuGetVersion>());

        // Assert
        Assert.Empty(result);
    }
}
