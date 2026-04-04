using NuGet.Versioning;
using Xunit;

using Namotion.NuGet.Plugins.Configuration;

namespace Namotion.NuGet.Plugins.Tests;

public class DependencyClassifierTests
{
    [Fact]
    public void WhenDependencyIsInHostDeps_ThenClassifiedAsHost()
    {
        // Arrange
        var hostResolver = HostDependencyResolver.FromAssemblies(
            ("Microsoft.Extensions.Logging", new Version(9, 0, 0)));
        var classifier = new DependencyClassifier(hostResolver, null, ["PluginA"]);

        // Act
        var classification = classifier.Classify("Microsoft.Extensions.Logging");

        // Assert
        Assert.Equal(DependencyClassification.Host, classification);
    }

    [Fact]
    public void WhenDependencyMatchesHostPattern_ThenClassifiedAsHost()
    {
        // Arrange
        var hostResolver = HostDependencyResolver.FromAssemblies();
        var classifier = new DependencyClassifier(
            hostResolver,
            name => PackageNameMatcher.IsMatchAny(name, ["MyCompany.*.Abstractions"]),
            ["PluginA"]);

        // Act
        var classification = classifier.Classify("MyCompany.Devices.Abstractions");

        // Assert
        Assert.Equal(DependencyClassification.Host, classification);
    }

    [Fact]
    public void WhenDependencyIsConfiguredPlugin_ThenClassifiedAsPlugin()
    {
        // Arrange
        var hostResolver = HostDependencyResolver.FromAssemblies();
        var classifier = new DependencyClassifier(hostResolver, null, ["MyCompany.Device1.HomeBlaze"]);

        // Act
        var classification = classifier.Classify("MyCompany.Device1.HomeBlaze");

        // Assert
        Assert.Equal(DependencyClassification.Entry, classification);
    }

    [Fact]
    public void WhenDependencyIsNeitherHostNorPlugin_ThenClassifiedAsPrivate()
    {
        // Arrange
        var hostResolver = HostDependencyResolver.FromAssemblies();
        var classifier = new DependencyClassifier(hostResolver, null, ["PluginA"]);

        // Act
        var classification = classifier.Classify("Newtonsoft.Json");

        // Assert
        Assert.Equal(DependencyClassification.Isolated, classification);
    }

    [Fact]
    public void WhenClassifyingPluginDeps_ThenPluginTakesPriorityOverHostPattern()
    {
        // Arrange -- plugin name also matches a host pattern
        var hostResolver = HostDependencyResolver.FromAssemblies();
        var classifier = new DependencyClassifier(
            hostResolver,
            name => PackageNameMatcher.IsMatchAny(name, ["MyCompany.*"]),
            ["MyCompany.Device1"]);

        // Act
        var classification = classifier.Classify("MyCompany.Device1");

        // Assert -- configured plugin always wins
        Assert.Equal(DependencyClassification.Entry, classification);
    }

    [Fact]
    public void WhenPackageIsInDiscoveredHostShared_ThenClassifiedAsHost()
    {
        // Arrange
        var hostResolver = HostDependencyResolver.FromAssemblies(
            ("HostLib", new Version(1, 0, 0)));
        var discoveredHostShared = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MyCompany.Abstractions" };
        var classifier = new DependencyClassifier(hostResolver, null, ["PluginA"], discoveredHostShared);

        // Act
        var result = classifier.Classify("MyCompany.Abstractions");

        // Assert
        Assert.Equal(DependencyClassification.Host, result);
    }

    [Fact]
    public void WhenClassifyingEntireGraph_ThenReturnsCorrectGrouping()
    {
        // Arrange
        var hostResolver = HostDependencyResolver.FromAssemblies(
            ("Microsoft.Extensions.Logging", new Version(9, 0, 0)));
        var classifier = new DependencyClassifier(
            hostResolver,
            name => PackageNameMatcher.IsMatchAny(name, ["MyCompany.*.Abstractions"]),
            ["MyCompany.Device1.HomeBlaze"]);

        var dependencies = new Dictionary<string, NuGetVersion>
        {
            ["MyCompany.Device1.HomeBlaze"] = NuGetVersion.Parse("1.0.0"),
            ["MyCompany.Device1"] = NuGetVersion.Parse("1.0.0"),
            ["MyCompany.Shared.Abstractions"] = NuGetVersion.Parse("1.0.0"),
            ["Microsoft.Extensions.Logging"] = NuGetVersion.Parse("9.0.0"),
            ["Newtonsoft.Json"] = NuGetVersion.Parse("13.0.3"),
        };

        // Act
        var result = classifier.ClassifyAll(dependencies);

        // Assert
        Assert.Equal(DependencyClassification.Entry, result["MyCompany.Device1.HomeBlaze"]);
        Assert.Equal(DependencyClassification.Isolated, result["MyCompany.Device1"]);
        Assert.Equal(DependencyClassification.Host, result["MyCompany.Shared.Abstractions"]);
        Assert.Equal(DependencyClassification.Host, result["Microsoft.Extensions.Logging"]);
        Assert.Equal(DependencyClassification.Isolated, result["Newtonsoft.Json"]);
    }

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
