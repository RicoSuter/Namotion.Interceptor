using global::NuGet.Versioning;
using Xunit;

using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Loading;
using Namotion.NuGet.Plugins.Repository;
using Namotion.NuGet.Plugins.Resolution;

namespace Namotion.NuGet.Plugins.Tests;

public class DependencyClassifierEdgeCaseTests
{
    [Fact]
    public void WhenPackageNameCaseDiffers_ThenStillClassifiedCorrectly()
    {
        // Arrange
        var hostResolver = HostDependencyResolver.FromAssemblies(
            ("Microsoft.Extensions.Logging", new Version(9, 0, 0)));
        var classifier = new DependencyClassifier(hostResolver, [], ["Plugin"]);

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
            hostResolver, ["mycompany.*.abstractions"], ["Plugin"]);

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
        var classifier = new DependencyClassifier(hostResolver, [], ["MyPlugin"]);

        // Act
        var pluginResult = classifier.Classify("MyPlugin");
        var depResult = classifier.Classify("SomeDep");

        // Assert
        Assert.Equal(DependencyClassification.Plugin, pluginResult);
        Assert.Equal(DependencyClassification.PluginPrivate, depResult);
    }

    [Fact]
    public void WhenClassifyAllWithEmptyDeps_ThenReturnsEmptyDictionary()
    {
        // Arrange
        var classifier = new DependencyClassifier(
            HostDependencyResolver.FromAssemblies(), [], []);

        // Act
        var result = classifier.ClassifyAll(new Dictionary<string, NuGetVersion>());

        // Assert
        Assert.Empty(result);
    }
}
