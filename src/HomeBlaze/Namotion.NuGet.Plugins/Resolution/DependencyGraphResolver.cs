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
    private readonly HostDependencyResolver? _hostDependencies;
    private readonly Func<string, bool>? _isHostPackage;

    internal DependencyGraphResolver(
        IDependencyInfoProvider provider,
        HostDependencyResolver? hostDependencies = null,
        Func<string, bool>? isHostPackage = null,
        ILogger? logger = null)
    {
        _provider = provider;
        _logger = logger ?? NullLogger.Instance;
        _hostDependencies = hostDependencies;
        _isHostPackage = isHostPackage;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DependencyGraphResolver"/> class using the specified NuGet feeds.
    /// </summary>
    /// <param name="feeds">The NuGet feeds to query for package metadata.</param>
    /// <param name="hostDependencies">Optional host dependency resolver for skipping already-provided packages.</param>
    /// <param name="isHostPackage">Optional predicate that determines whether a package should be treated as host-provided.</param>
    /// <param name="includePrerelease">Whether to include pre-release package versions when resolving dependencies.</param>
    /// <param name="logger">An optional logger for diagnostic output.</param>
    public DependencyGraphResolver(
        IReadOnlyList<NuGetFeed> feeds,
        HostDependencyResolver? hostDependencies = null,
        Func<string, bool>? isHostPackage = null,
        bool includePrerelease = false,
        ILogger? logger = null)
        : this(new NuGetDependencyInfoProvider(feeds, includePrerelease, logger), hostDependencies, isHostPackage, logger)
    {
    }

    /// <summary>
    /// Resolves the full transitive dependency tree for a package.
    /// </summary>
    /// <param name="packageName">The NuGet package identifier.</param>
    /// <param name="version">The specific version to resolve, or null for latest.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The root node of the resolved dependency tree.</returns>
    internal async Task<DependencyNode> ResolveAsync(
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
    internal Dictionary<string, NuGetVersion> FlattenDependencies(DependencyNode root)
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
            if (visited.TryGetValue(dependencyId, out var existingVersion))
            {
                // Already visited -- check if existing version satisfies the new range
                if (versionRange.Satisfies(existingVersion))
                    continue;

                // Existing version doesn't satisfy the new range -- re-resolve for the higher version
                var upgradedVersion = await _provider.ResolveVersionAsync(
                    dependencyId, versionRange, cancellationToken);
                if (upgradedVersion != null && upgradedVersion > existingVersion)
                {
                    visited[dependencyId] = upgradedVersion;
                    var upgradedNode = new DependencyNode(dependencyId, upgradedVersion);
                    node.Dependencies.Add(upgradedNode);
                    await ResolveRecursiveAsync(upgradedNode, visited, cancellationToken);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Cannot satisfy dependency '{dependencyId}' {versionRange}. " +
                        $"Existing resolved version {existingVersion} does not satisfy the range " +
                        $"and no compatible upgrade was found.");
                }
                continue;
            }

            // Skip dependencies already provided by the host
            if (_hostDependencies != null && _hostDependencies.Contains(dependencyId))
            {
                _logger.LogDebug("Skipping host dependency {PackageId} during resolution.", dependencyId);
                continue;
            }

            // Skip dependencies matching the host package predicate
            if (_isHostPackage?.Invoke(dependencyId) == true)
            {
                _logger.LogDebug("Skipping host package {PackageId} during resolution (matched IsHostPackage predicate).", dependencyId);
                continue;
            }

            var resolvedVersion = await _provider.ResolveVersionAsync(
                dependencyId, versionRange, cancellationToken);
            if (resolvedVersion == null)
            {
                throw new PackageNotFoundException(dependencyId, versionRange.ToNormalizedString());
            }

            var dependencyNode = new DependencyNode(dependencyId, resolvedVersion);
            node.Dependencies.Add(dependencyNode);
            visited[dependencyId] = resolvedVersion;

            await ResolveRecursiveAsync(dependencyNode, visited, cancellationToken);
        }
    }
}
