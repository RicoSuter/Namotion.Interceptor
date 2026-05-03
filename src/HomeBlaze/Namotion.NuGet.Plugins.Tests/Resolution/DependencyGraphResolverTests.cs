using NuGet.Versioning;
using Xunit;
using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Resolution;

namespace Namotion.NuGet.Plugins.Tests.Resolution;

public class DependencyGraphResolverTests
{
    [Fact]
    public async Task WhenPackageHasNoDependencies_ThenReturnsRootOnly()
    {
        // Arrange
        var resolver = CreateResolver(new Dictionary<string, (string Name, string Version, (string Id, string VersionRange)[] Deps)>
        {
            ["PackageA/1.0.0"] = ("PackageA", "1.0.0", Array.Empty<(string, string)>())
        });

        // Act
        var graph = await resolver.ResolveAsync("PackageA", "1.0.0", CancellationToken.None);

        // Assert
        Assert.Equal("PackageA", graph.PackageName);
        Assert.Equal(NuGetVersion.Parse("1.0.0"), graph.Version);
        Assert.Empty(graph.Dependencies);
    }

    [Fact]
    public async Task WhenPackageHasDirectDependencies_ThenResolvesAll()
    {
        // Arrange
        var resolver = CreateResolver(new Dictionary<string, (string Name, string Version, (string Id, string VersionRange)[] Deps)>
        {
            ["PackageA/1.0.0"] = ("PackageA", "1.0.0", [("LibB", "[1.0.0, )")]),
            ["LibB/1.2.0"] = ("LibB", "1.2.0", Array.Empty<(string, string)>())
        });

        // Act
        var graph = await resolver.ResolveAsync("PackageA", "1.0.0", CancellationToken.None);

        // Assert
        Assert.Single(graph.Dependencies);
        Assert.Equal("LibB", graph.Dependencies[0].PackageName);
    }

    [Fact]
    public async Task WhenPackageHasTransitiveDependencies_ThenResolvesFullTree()
    {
        // Arrange
        var resolver = CreateResolver(new Dictionary<string, (string Name, string Version, (string Id, string VersionRange)[] Deps)>
        {
            ["A/1.0.0"] = new("A", "1.0.0", [("B", "[1.0.0, )")]),
            ["B/1.0.0"] = new("B", "1.0.0", [("C", "[2.0.0, )")]),
            ["C/2.0.0"] = new("C", "2.0.0", [])
        });

        // Act
        var graph = await resolver.ResolveAsync("A", "1.0.0", CancellationToken.None);

        // Assert
        Assert.Equal("B", graph.Dependencies[0].PackageName);
        Assert.Equal("C", graph.Dependencies[0].Dependencies[0].PackageName);
        Assert.Equal(NuGetVersion.Parse("2.0.0"), graph.Dependencies[0].Dependencies[0].Version);
    }

    [Fact]
    public async Task WhenCircularDependencyExists_ThenDoesNotInfiniteLoop()
    {
        // Arrange
        var resolver = CreateResolver(new Dictionary<string, (string Name, string Version, (string Id, string VersionRange)[] Deps)>
        {
            ["A/1.0.0"] = new("A", "1.0.0", [("B", "[1.0.0, )")]),
            ["B/1.0.0"] = new("B", "1.0.0", [("A", "[1.0.0, )")])
        });

        // Act
        var graph = await resolver.ResolveAsync("A", "1.0.0", CancellationToken.None);

        // Assert -- should complete without stack overflow
        Assert.Single(graph.Dependencies);
        Assert.Empty(graph.Dependencies[0].Dependencies); // cycle broken
    }

    [Fact]
    public async Task WhenDiamondDependencyExists_ThenResolvesHigherVersion()
    {
        // Arrange -- A depends on B and C, both depend on D but different versions
        var resolver = CreateResolver(new Dictionary<string, (string Name, string Version, (string Id, string VersionRange)[] Deps)>
        {
            ["A/1.0.0"] = new("A", "1.0.0", [("B", "[1.0.0, )"), ("C", "[1.0.0, )")]),
            ["B/1.0.0"] = new("B", "1.0.0", [("D", "[1.0.0, )")]),
            ["C/1.0.0"] = new("C", "1.0.0", [("D", "[1.2.0, )")]),
            ["D/1.0.0"] = new("D", "1.0.0", []),
            ["D/1.2.0"] = new("D", "1.2.0", [])
        });

        // Act
        var graph = await resolver.ResolveAsync("A", "1.0.0", CancellationToken.None);

        // Assert -- D should appear, and the flattened dependencies should use 1.2.0
        var allDeps = resolver.FlattenDependencies(graph);
        Assert.True(allDeps.ContainsKey("D"));
        Assert.True(allDeps["D"] >= NuGetVersion.Parse("1.2.0"));
    }

    [Fact]
    public async Task WhenFlatteningDependencies_ThenReturnsDedupedMap()
    {
        // Arrange
        var resolver = CreateResolver(new Dictionary<string, (string Name, string Version, (string Id, string VersionRange)[] Deps)>
        {
            ["A/1.0.0"] = new("A", "1.0.0", [("B", "[1.0.0, )"), ("C", "[2.0.0, )")]),
            ["B/1.0.0"] = new("B", "1.0.0", []),
            ["C/2.0.0"] = new("C", "2.0.0", [])
        });

        // Act
        var graph = await resolver.ResolveAsync("A", "1.0.0", CancellationToken.None);
        var flat = resolver.FlattenDependencies(graph);

        // Assert -- includes root + deps
        Assert.Equal(3, flat.Count);
        Assert.True(flat.ContainsKey("A"));
        Assert.True(flat.ContainsKey("B"));
        Assert.True(flat.ContainsKey("C"));
    }

    [Fact]
    public async Task WhenDiamondDependencyVisitedLowerFirst_ThenReResolvesToHigherVersion()
    {
        // Arrange -- A→B→D([1.0.0,1.1.0)), A→C→D(>=1.2.0)
        // B is listed first, so D 1.0.0 is visited first
        var resolver = CreateResolver(new Dictionary<string, (string Name, string Version, (string Id, string VersionRange)[] Deps)>
        {
            ["A/1.0.0"] = new("A", "1.0.0", [("B", "[1.0.0, )"), ("C", "[1.0.0, )")]),
            ["B/1.0.0"] = new("B", "1.0.0", [("D", "[1.0.0, 1.1.0)")]),
            ["C/1.0.0"] = new("C", "1.0.0", [("D", "[1.2.0, )")]),
            ["D/1.0.0"] = new("D", "1.0.0", []),
            ["D/1.2.0"] = new("D", "1.2.0", [])
        });

        // Act
        var graph = await resolver.ResolveAsync("A", "1.0.0", CancellationToken.None);
        var flat = resolver.FlattenDependencies(graph);

        // Assert -- D should be 1.2.0 (the higher version satisfying C's range)
        Assert.Equal(NuGetVersion.Parse("1.2.0"), flat["D"]);
    }

    [Fact]
    public async Task WhenDependencyCannotBeResolved_ThenThrows()
    {
        // Arrange -- A depends on B, B is not in any feed
        var resolver = CreateResolver(new Dictionary<string, (string Name, string Version, (string Id, string VersionRange)[] Deps)>
        {
            ["A/1.0.0"] = new("A", "1.0.0", [("B", "[1.0.0, )")])
            // B is intentionally missing
        });

        // Act & Assert
        await Assert.ThrowsAsync<PackageNotFoundException>(
            () => resolver.ResolveAsync("A", "1.0.0", CancellationToken.None));
    }

    [Fact]
    public async Task WhenHostDependencyExists_ThenSkippedDuringResolution()
    {
        // Arrange -- A depends on B (host) and C (not host)
        var hostResolver = HostDependencyResolver.FromAssemblies(
            ("B", new Version(1, 0, 0)));
        var provider = new FakeDependencyInfoProvider(new Dictionary<string, (string Name, string Version, (string Id, string VersionRange)[] Deps)>
        {
            ["A/1.0.0"] = new("A", "1.0.0", [("B", "[1.0.0, )"), ("C", "[1.0.0, )")]),
            ["C/1.0.0"] = new("C", "1.0.0", [])
        });
        var resolver = new DependencyGraphResolver(provider, hostResolver);

        // Act
        var graph = await resolver.ResolveAsync("A", "1.0.0", CancellationToken.None);
        var flat = resolver.FlattenDependencies(graph);

        // Assert -- B should be skipped (host dep), C should be present
        Assert.False(flat.ContainsKey("B"));
        Assert.True(flat.ContainsKey("C"));
    }

    [Fact]
    public async Task WhenDeepTransitiveDiamond_ThenHighestVersionWins()
    {
        // Arrange -- A->B->D(1.0), A->C->E->D(2.0)
        var resolver = CreateResolver(new Dictionary<string, (string Name, string Version, (string Id, string VersionRange)[] Deps)>
        {
            ["A/1.0.0"] = new("A", "1.0.0", [("B", "[1.0.0, )"), ("C", "[1.0.0, )")]),
            ["B/1.0.0"] = new("B", "1.0.0", [("D", "[1.0.0, )")]),
            ["C/1.0.0"] = new("C", "1.0.0", [("E", "[1.0.0, )")]),
            ["E/1.0.0"] = new("E", "1.0.0", [("D", "[2.0.0, )")]),
            ["D/1.0.0"] = new("D", "1.0.0", []),
            ["D/2.0.0"] = new("D", "2.0.0", [])
        });

        // Act
        var graph = await resolver.ResolveAsync("A", "1.0.0", CancellationToken.None);
        var flat = resolver.FlattenDependencies(graph);

        // Assert
        Assert.Equal(NuGetVersion.Parse("2.0.0"), flat["D"]);
    }

    private static DependencyGraphResolver CreateResolver(
        Dictionary<string, (string Name, string Version, (string Id, string VersionRange)[] Deps)> packages)
    {
        var provider = new FakeDependencyInfoProvider(packages);
        return new DependencyGraphResolver(provider);
    }
}
