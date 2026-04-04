using NuGet.Versioning;
using Xunit;

using Namotion.NuGet.Plugins.Configuration;

namespace Namotion.NuGet.Plugins.Tests.Configuration;

public class HostDependencyResolverTests
{
    [Fact]
    public void WhenCreatedFromDepsJsonFile_ThenContainsPackageDependencies()
    {
        // Arrange
        var depsJsonPath = Path.Combine(AppContext.BaseDirectory, "TestData", "test.deps.json");

        // Act
        var resolver = HostDependencyResolver.FromDepsJson(depsJsonPath);

        // Assert
        Assert.True(resolver.Contains("Microsoft.Extensions.Logging.Abstractions"));
        Assert.True(resolver.Contains("Newtonsoft.Json"));
        Assert.False(resolver.Contains("NonExistent.Package"));
    }

    [Fact]
    public void WhenCreatedFromDepsJsonFile_ThenVersionsAreParsedCorrectly()
    {
        // Arrange
        var depsJsonPath = Path.Combine(AppContext.BaseDirectory, "TestData", "test.deps.json");

        // Act
        var resolver = HostDependencyResolver.FromDepsJson(depsJsonPath);

        // Assert
        Assert.Equal(NuGetVersion.Parse("9.0.0"), resolver.GetVersion("Microsoft.Extensions.Logging.Abstractions"));
        Assert.Equal(NuGetVersion.Parse("13.0.3"), resolver.GetVersion("Newtonsoft.Json"));
    }

    [Fact]
    public void WhenCreatedFromDepsJsonFile_ThenProjectEntriesAreIncluded()
    {
        // Arrange
        var depsJsonPath = Path.Combine(AppContext.BaseDirectory, "TestData", "test.deps.json");

        // Act
        var resolver = HostDependencyResolver.FromDepsJson(depsJsonPath);

        // Assert
        Assert.True(resolver.Contains("TestApp"));
    }

    [Fact]
    public void WhenCreatedFromTuples_ThenContainsDependencies()
    {
        // Act
        var resolver = HostDependencyResolver.FromAssemblies(
            ("MyLib", new Version(1, 2, 3)),
            ("OtherLib", new Version(3, 0, 0)));

        // Assert
        Assert.True(resolver.Contains("MyLib"));
        Assert.Equal(NuGetVersion.Parse("1.2.3"), resolver.GetVersion("MyLib"));
        Assert.True(resolver.Contains("OtherLib"));
    }

    [Fact]
    public void WhenCreatedFromAssemblyObjects_ThenExtractsNameAndVersion()
    {
        // Arrange
        var assemblies = new[] { typeof(object).Assembly };

        // Act
        var resolver = HostDependencyResolver.FromAssemblies(assemblies);

        // Assert
        Assert.True(resolver.Dependencies.Count > 0);
    }

    [Fact]
    public void WhenPackageNotFound_ThenGetVersionReturnsNull()
    {
        // Arrange
        var resolver = HostDependencyResolver.FromAssemblies(
            ("MyLib", new Version(1, 0, 0)));

        // Act
        var version = resolver.GetVersion("NonExistent");

        // Assert
        Assert.Null(version);
    }

    [Fact]
    public void WhenAutoDetecting_ThenUsesEntryAssemblyDepsJson()
    {
        // Act -- the parameterless overload should find the running app's deps.json
        var resolver = HostDependencyResolver.FromDepsJson();

        // Assert -- the test runner itself has dependencies
        Assert.True(resolver.Dependencies.Count > 0);
    }
}
