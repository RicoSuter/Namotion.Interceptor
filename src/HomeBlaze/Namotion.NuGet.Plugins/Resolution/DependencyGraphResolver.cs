using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;
using Namotion.NuGet.Plugins.Configuration;

namespace Namotion.NuGet.Plugins.Resolution;

/// <summary>
/// Resolves the full transitive dependency tree for a package.
/// </summary>
public class DependencyGraphResolver
{
    private readonly IDependencyInfoProvider _provider;
    private readonly ILogger _logger;

    internal DependencyGraphResolver(IDependencyInfoProvider provider, ILogger? logger = null)
    {
        _provider = provider;
        _logger = logger ?? NullLogger.Instance;
    }

    public DependencyGraphResolver(
        IReadOnlyList<NuGetFeed> feeds,
        ILogger? logger = null)
        : this(new NuGetDependencyInfoProvider(feeds, logger), logger)
    {
    }

    public async Task<DependencyNode> ResolveAsync(
        string packageName, string? version, CancellationToken cancellationToken)
    {
        var visited = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        var resolvedVersion = version != null ? NuGetVersion.Parse(version) : null;

        if (resolvedVersion == null)
        {
            resolvedVersion = await _provider.ResolveVersionAsync(
                packageName, VersionRange.All, cancellationToken)
                ?? throw new PackageNotFoundException(packageName, null);
        }

        var root = new DependencyNode(packageName, resolvedVersion);
        visited[packageName] = resolvedVersion;

        await ResolveRecursiveAsync(root, visited, cancellationToken);
        return root;
    }

    /// <summary>
    /// Flattens the dependency tree into a dictionary of package name -> highest resolved version.
    /// </summary>
    public Dictionary<string, NuGetVersion> FlattenDependencies(DependencyNode root)
    {
        var result = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        FlattenRecursive(root, result);
        return result;
    }

    private static void FlattenRecursive(DependencyNode node, Dictionary<string, NuGetVersion> result)
    {
        if (!result.TryGetValue(node.PackageName, out var existing) || node.Version > existing)
        {
            result[node.PackageName] = node.Version;
        }

        foreach (var dependency in node.Dependencies)
        {
            FlattenRecursive(dependency, result);
        }
    }

    private async Task ResolveRecursiveAsync(
        DependencyNode node,
        Dictionary<string, NuGetVersion> visited,
        CancellationToken cancellationToken)
    {
        var dependencies = await _provider.GetDependenciesAsync(
            node.PackageName, node.Version.ToNormalizedString(), cancellationToken);

        foreach (var (dependencyId, versionRange) in dependencies)
        {
            // Cycle detection
            if (visited.ContainsKey(dependencyId))
                continue;

            var resolvedVersion = await _provider.ResolveVersionAsync(
                dependencyId, versionRange, cancellationToken);
            if (resolvedVersion == null)
            {
                _logger.LogWarning("Could not resolve dependency {PackageId} {VersionRange}.",
                    dependencyId, versionRange);
                continue;
            }

            var dependencyNode = new DependencyNode(dependencyId, resolvedVersion);
            node.Dependencies.Add(dependencyNode);
            visited[dependencyId] = resolvedVersion;

            await ResolveRecursiveAsync(dependencyNode, visited, cancellationToken);
        }
    }
}
