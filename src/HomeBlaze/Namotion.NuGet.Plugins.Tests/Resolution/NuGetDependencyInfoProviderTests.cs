using NuGet.Frameworks;
using NuGet.Packaging;
using Xunit;
using Namotion.NuGet.Plugins.Resolution;

namespace Namotion.NuGet.Plugins.Tests.Resolution;

public class NuGetDependencyInfoProviderTests
{
    private static readonly NuGetFramework Net10 = NuGetFramework.Parse("net10.0");

    [Fact]
    public void WhenPackageOnlyTargetsNewerFramework_ThenFrameworkIsNotSupported()
    {
        // Arrange
        PackageDependencyGroup[] dependencyGroups = [new(NuGetFramework.Parse("net11.0"), [])];

        // Act
        var isSupported = NuGetDependencyInfoProvider.SupportsFramework(dependencyGroups, Net10);

        // Assert
        Assert.False(isSupported);
    }

    [Fact]
    public void WhenPackageTargetsCompatibleFramework_ThenFrameworkIsSupported()
    {
        // Arrange
        PackageDependencyGroup[] dependencyGroups =
        [
            new(NuGetFramework.Parse("net11.0"), []),
            new(NuGetFramework.Parse("netstandard2.0"), []),
        ];

        // Act
        var isSupported = NuGetDependencyInfoProvider.SupportsFramework(dependencyGroups, Net10);

        // Assert
        Assert.True(isSupported);
    }

    [Fact]
    public void WhenPackageHasFrameworkIndependentGroup_ThenFrameworkIsSupported()
    {
        // Arrange
        PackageDependencyGroup[] dependencyGroups = [new(NuGetFramework.AnyFramework, [])];

        // Act
        var isSupported = NuGetDependencyInfoProvider.SupportsFramework(dependencyGroups, Net10);

        // Assert
        Assert.True(isSupported);
    }

    [Fact]
    public void WhenPackageHasNoDependencyGroups_ThenFrameworkIsSupported()
    {
        // Act
        var isSupported = NuGetDependencyInfoProvider.SupportsFramework([], Net10);

        // Assert
        Assert.True(isSupported);
    }
}
