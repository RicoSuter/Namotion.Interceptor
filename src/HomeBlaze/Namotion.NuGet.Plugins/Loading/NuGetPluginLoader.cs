using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;
using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Repository;
using Namotion.NuGet.Plugins.Resolution;

namespace Namotion.NuGet.Plugins.Loading;

/// <summary>
/// Main entry point for loading NuGet packages as plugins.
/// </summary>
public class NuGetPluginLoader : IDisposable
{
    private readonly NuGetPluginLoaderOptions _options;
    private readonly ILogger<NuGetPluginLoader> _logger;
    private readonly List<NuGetPlugin> _loadedPlugins = [];
    private readonly PackageExtractor _extractor;
    private readonly INuGetPackageRepository _repository;
    private readonly SharedHostAssemblyRegistry _hostAssemblyRegistry;
    private readonly Lock _lock = new();

    private bool _disposed;

    private static readonly Lazy<HashSet<string>> TrustedPlatformAssemblyNames = new(() =>
    {
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (tpa == null) return new(StringComparer.OrdinalIgnoreCase);
        return tpa.Split(Path.PathSeparator)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
    });

    public NuGetPluginLoader(
        NuGetPluginLoaderOptions options,
        ILogger<NuGetPluginLoader>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<NuGetPluginLoader>.Instance;

        var cacheDirectory = options.CacheDirectory
            ?? Path.Combine(Path.GetTempPath(), "Namotion.NuGet.Plugins", Guid.NewGuid().ToString("N"));
        _extractor = new PackageExtractor(cacheDirectory);

        var repositories = options.Feeds
            .Select(feed => (INuGetPackageRepository)new NuGetPackageRepository(feed, options.IncludePrerelease, _logger))
            .ToList();
        _repository = new CompositeNuGetPackageRepository(repositories);
        _hostAssemblyRegistry = new SharedHostAssemblyRegistry();
    }

    /// <summary>
    /// Gets a snapshot of all currently loaded plugins.
    /// </summary>
    public IReadOnlyList<NuGetPlugin> LoadedPlugins
    {
        get
        {
            lock (_lock)
            {
                return _loadedPlugins.ToList().AsReadOnly();
            }
        }
    }

    /// <summary>
    /// Gets all types assignable to <typeparamref name="T"/> from all loaded plugins.
    /// </summary>
    public IEnumerable<Type> GetTypes<T>()
    {
        return LoadedPlugins.SelectMany(plugin => plugin.GetTypes<T>());
    }

    /// <summary>
    /// Gets all types matching the predicate from all loaded plugins.
    /// </summary>
    public IEnumerable<Type> GetTypes(Func<Type, bool> predicate)
    {
        return LoadedPlugins.SelectMany(plugin => plugin.GetTypes(predicate));
    }

    /// <summary>
    /// Resolves, downloads, validates, and loads the specified plugins and their transitive dependencies.
    /// </summary>
    public async Task<NuGetPluginLoadResult> LoadPluginsAsync(
        IEnumerable<NuGetPluginReference> plugins,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var pluginList = plugins.ToList();
        var failures = new List<NuGetPluginFailure>();

        var hostResolver = _options.HostDependencies ?? new HostDependencyResolver([]);
        var classifier = new NuGetDependencyClassifier(
            hostResolver,
            _options.IsHostPackage,
            pluginList.Select(plugin => plugin.PackageName));

        var graphResolver = new DependencyGraphResolver(
            _options.Feeds, hostResolver, _options.IsHostPackage, _options.IncludePrerelease, _logger);

        var pluginGraphs = await ResolvePluginGraphsAsync(
            pluginList, classifier, graphResolver, failures, cancellationToken);

        var hostFailures = ValidateHostCompatibility(pluginGraphs, hostResolver);
        failures.AddRange(hostFailures);
        pluginGraphs.RemoveAll(graph => hostFailures.Any(failure => failure.PackageName == graph.Request.PackageName));

        await DownloadPackagesAsync(pluginGraphs, failures, hostResolver, cancellationToken);

        var pluginManifests = DiscoverHostSharedPackages(
            pluginGraphs, failures, pluginList, hostResolver);

        LoadHostPackages(pluginGraphs, hostResolver);

        var loadedPlugins = LoadPluginAssemblies(
            pluginGraphs, failures, pluginManifests);

        return new NuGetPluginLoadResult(loadedPlugins, failures);
    }

    /// <summary>
    /// Resolves each plugin's full dependency graph and classifies every dependency.
    /// </summary>
    private async Task<List<ResolvedPluginGraph>> ResolvePluginGraphsAsync(
        List<NuGetPluginReference> pluginList,
        NuGetDependencyClassifier classifier,
        DependencyGraphResolver graphResolver,
        List<NuGetPluginFailure> failures,
        CancellationToken cancellationToken)
    {
        var pluginGraphs = new List<ResolvedPluginGraph>();

        foreach (var request in pluginList)
        {
            try
            {
                var graph = await graphResolver.ResolveAsync(
                    request.PackageName, request.Version, cancellationToken);

                var flatDependencies = graphResolver.FlattenDependencies(graph);
                var classifications = classifier.ClassifyAll(flatDependencies);
                pluginGraphs.Add(new ResolvedPluginGraph(request, flatDependencies, classifications));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Failed to resolve dependencies for plugin '{Plugin}'.", request.PackageName);
                failures.Add(new NuGetPluginFailure(request.PackageName,
                    $"Dependency resolution failed: {exception.Message}", Exception: exception));
            }
        }

        return pluginGraphs;
    }

    /// <summary>
    /// Validates that host dependencies are version-compatible across all plugins.
    /// Returns a list of failures for plugins that have host version conflicts,
    /// rather than aborting all plugins.
    /// </summary>
    private static List<NuGetPluginFailure> ValidateHostCompatibility(
        List<ResolvedPluginGraph> pluginGraphs,
        HostDependencyResolver hostResolver)
    {
        var failures = new List<NuGetPluginFailure>();

        // Check each plugin's host dependencies against the host's deps.json versions
        foreach (var entry in pluginGraphs)
        {
            var hostDependencies = entry.FlatDependencies
                .Where(kvp => entry.Classifications.TryGetValue(kvp.Key, out var classification) && classification == NuGetDependencyClassification.Host)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            var conflicts = NuGetVersionCompatibility.FindConflicts(hostDependencies, hostResolver, entry.Request.PackageName);
            if (conflicts.Count > 0)
            {
                failures.Add(new NuGetPluginFailure(
                    entry.Request.PackageName,
                    $"Host version conflicts: {string.Join(", ", conflicts.Select(conflict => $"{conflict.PackageName} requires {conflict.RequiredVersion} but host has {conflict.HostVersion}"))}",
                    Conflicts: conflicts));
            }
        }

        // Validate external host package version compatibility across plugins
        var additionalHostRequirements = new Dictionary<string, List<(string PluginName, VersionRange Range)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in pluginGraphs)
        {
            // Skip plugins already failed above
            if (failures.Any(failure => failure.PackageName == entry.Request.PackageName))
            {
                continue;
            }

            foreach (var (packageName, version) in entry.FlatDependencies)
            {
                if (IsExternalHostPackage(packageName, entry.Classifications, hostResolver))
                {
                    if (!additionalHostRequirements.ContainsKey(packageName))
                    {
                        additionalHostRequirements[packageName] = [];
                    }

                    additionalHostRequirements[packageName].Add(
                        (entry.Request.PackageName, new VersionRange(version)));
                }
            }
        }

        if (additionalHostRequirements.Count > 0)
        {
            var hostVersionResult = HostPackageVersionResolver.ResolveVersions(additionalHostRequirements);
            if (!hostVersionResult.Success)
            {
                // Determine which plugins contributed to each conflict and fail them
                var conflictingPluginNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var conflict in hostVersionResult.Conflicts)
                {
                    // Find all plugins that require this conflicting package
                    foreach (var (pluginName, _) in conflict.PluginRanges)
                    {
                        conflictingPluginNames.Add(pluginName);
                    }
                }

                foreach (var pluginName in conflictingPluginNames)
                {
                    // Only add if not already failed
                    if (!failures.Any(failure => failure.PackageName.Equals(pluginName, StringComparison.OrdinalIgnoreCase)))
                    {
                        var pluginConflicts = hostVersionResult.Conflicts
                            .Where(conflict =>
                                conflict.PluginRanges.Any(range => range.PluginName.Equals(pluginName, StringComparison.OrdinalIgnoreCase)))
                            .ToList();

                        var conflictDescriptions = pluginConflicts.Select(conflict =>
                            $"{conflict.PackageName}: incompatible ranges from {string.Join(", ", conflict.PluginRanges.Select(r => $"'{r.PluginName}' ({r.VersionRange})"))}");

                        failures.Add(new NuGetPluginFailure(
                            pluginName,
                            $"Cross-plugin host version conflicts: {string.Join("; ", conflictDescriptions)}",
                            Conflicts: pluginConflicts));
                    }
                }
            }
        }

        return failures;
    }

    /// <summary>
    /// Downloads all required packages that are not already cached. Host packages already
    /// present in the host resolver are skipped.
    /// </summary>
    private async Task DownloadPackagesAsync(
        List<ResolvedPluginGraph> pluginGraphs,
        List<NuGetPluginFailure> failures,
        HostDependencyResolver hostResolver,
        CancellationToken cancellationToken)
    {
        foreach (var entry in pluginGraphs)
        {
            try
            {
                foreach (var (packageName, version) in entry.FlatDependencies)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var classification = entry.Classifications[packageName];
                    if (classification == NuGetDependencyClassification.Host &&
                        hostResolver.Contains(packageName))
                    {
                        // Already in host, skip download
                        continue;
                    }

                    var versionString = version.ToNormalizedString();
                    if (_extractor.GetCachedPackagePath(packageName, versionString) != null)
                    {
                        continue; // Already cached
                    }

                    await using var download = await _repository.DownloadPackageAsync(
                        packageName, versionString, cancellationToken);

                    _extractor.ExtractAndGetAssemblyPaths(packageName, versionString, download.Stream);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Failed to download packages for plugin '{Plugin}'.", entry.Request.PackageName);
                failures.Add(new NuGetPluginFailure(entry.Request.PackageName,
                    $"Download failed: {exception.Message}", Exception: exception));
            }
        }
    }

    /// <summary>
    /// Discovers host-shared packages from plugin manifests (plugin.json) and assembly attributes,
    /// then re-classifies dependencies accordingly. Returns the collected plugin manifests.
    /// </summary>
    private Dictionary<string, JsonElement?> DiscoverHostSharedPackages(
        List<ResolvedPluginGraph> pluginGraphs,
        List<NuGetPluginFailure> failures,
        List<NuGetPluginReference> pluginList,
        HostDependencyResolver hostResolver)
    {
        var discoveredHostShared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pluginManifests = new Dictionary<string, JsonElement?>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in pluginGraphs)
        {
            if (failures.Any(failure => failure.PackageName == entry.Request.PackageName))
            {
                continue;
            }

            // Read plugin.json from the plugin's own extracted package
            var pluginVersionString = entry.FlatDependencies[entry.Request.PackageName].ToNormalizedString();
            var pluginPath = _extractor.GetCachedPackagePath(entry.Request.PackageName, pluginVersionString);
            if (pluginPath != null)
            {
                var manifest = PluginManifestReader.Read(pluginPath);
                pluginManifests[entry.Request.PackageName] = manifest;

                foreach (var hostDep in PluginManifestReader.GetHostDependencies(manifest))
                {
                    discoveredHostShared.Add(hostDep);
                }
            }

            // Scan dependency DLLs for HostShared attribute
            foreach (var (packageName, version) in entry.FlatDependencies)
            {
                if (packageName == entry.Request.PackageName)
                {
                    continue;
                }

                var depPath = _extractor.GetCachedPackagePath(packageName, version.ToNormalizedString());
                if (depPath != null && HostSharedAttributeScanner.IsAnyAssemblyHostShared(depPath))
                {
                    discoveredHostShared.Add(packageName);
                }
            }
        }

        // Re-classify with discovered host-shared packages
        if (discoveredHostShared.Count > 0)
        {
            var updatedClassifier = new NuGetDependencyClassifier(
                hostResolver, _options.IsHostPackage,
                pluginList.Select(plugin => plugin.PackageName), discoveredHostShared);

            for (var i = 0; i < pluginGraphs.Count; i++)
            {
                var entry = pluginGraphs[i];
                var newClassifications = updatedClassifier.ClassifyAll(entry.FlatDependencies);
                pluginGraphs[i] = entry with { Classifications = newClassifications };
            }
        }

        return pluginManifests;
    }

    /// <summary>
    /// Loads external host packages (not already in the host resolver) into the default
    /// assembly load context so they are available to all plugins.
    /// </summary>
    private void LoadHostPackages(
        List<ResolvedPluginGraph> pluginGraphs,
        HostDependencyResolver hostResolver)
    {
        // First pass: collect the highest version per external host package across all plugins
        var bestVersions = new Dictionary<string, (NuGetVersion Version, string VersionString)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in pluginGraphs)
        {
            foreach (var (packageName, version) in entry.FlatDependencies)
            {
                if (IsExternalHostPackage(packageName, entry.Classifications, hostResolver))
                {
                    if (!bestVersions.TryGetValue(packageName, out var existing) || version > existing.Version)
                    {
                        bestVersions[packageName] = (version, version.ToNormalizedString());
                    }
                }
            }
        }

        // Second pass: load assemblies from the highest-versioned package
        foreach (var (packageName, (_, versionString)) in bestVersions)
        {
            var cachedPath = _extractor.GetCachedPackagePath(packageName, versionString);
            if (cachedPath == null) continue;

            var paths = _extractor.GetAssemblyPaths(cachedPath);
            foreach (var path in paths)
            {
                var assemblyName = Path.GetFileNameWithoutExtension(path);
                _hostAssemblyRegistry.AddAssemblyPath(assemblyName, path);
            }
        }
    }

    /// <summary>
    /// Creates isolated assembly load contexts and loads plugin assemblies into them.
    /// Returns the list of successfully loaded plugins.
    /// </summary>
    private List<NuGetPlugin> LoadPluginAssemblies(
        List<ResolvedPluginGraph> pluginGraphs,
        List<NuGetPluginFailure> failures,
        Dictionary<string, JsonElement?> pluginManifests)
    {
        var loadedPlugins = new List<NuGetPlugin>();

        foreach (var entry in pluginGraphs)
        {
            if (failures.Any(failure => failure.PackageName == entry.Request.PackageName))
            {
                continue; // Skip failed plugins
            }

            PluginAssemblyLoadContext? loadContext = null;
            try
            {
                var hostAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var privateAssemblyPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var (packageName, version) in entry.FlatDependencies)
                {
                    var classification = entry.Classifications[packageName];
                    var versionString = version.ToNormalizedString();
                    var cachedPath = _extractor.GetCachedPackagePath(packageName, versionString);

                    if (classification == NuGetDependencyClassification.Host)
                    {
                        // Collect host assembly names so the ALC falls back
                        if (cachedPath != null)
                        {
                            var paths = _extractor.GetAssemblyPaths(cachedPath);
                            foreach (var path in paths)
                            {
                                hostAssemblyNames.Add(Path.GetFileNameWithoutExtension(path));
                            }
                        }
                        else
                        {
                            // Already in host - use the assembly name directly
                            hostAssemblyNames.Add(packageName);
                        }
                    }
                    else if (cachedPath != null)
                    {
                        // Plugin or plugin-private: map assembly names to paths
                        var paths = _extractor.GetAssemblyPaths(cachedPath);
                        foreach (var path in paths)
                        {
                            var assemblyName = Path.GetFileNameWithoutExtension(path);
                            if (IsAssemblyInDefaultContext(assemblyName))
                            {
                                // Already available in default context (e.g., framework-provided assembly
                                // like Microsoft.Extensions.* from the shared framework)
                                hostAssemblyNames.Add(assemblyName);
                                entry.Classifications[packageName] = NuGetDependencyClassification.Host;
                                _logger.LogDebug("Assembly '{Assembly}' from plugin '{Plugin}' already in default context, reclassifying as host.",
                                    assemblyName, entry.Request.PackageName);
                            }
                            else
                            {
                                privateAssemblyPaths[assemblyName] = path;
                            }
                        }
                    }
                }

                loadContext = new PluginAssemblyLoadContext(
                    entry.Request.PackageName, hostAssemblyNames, privateAssemblyPaths);

                _logger.LogDebug("Plugin '{Plugin}': {HostCount} host assemblies, {PrivateCount} private assemblies.",
                    entry.Request.PackageName, hostAssemblyNames.Count, privateAssemblyPaths.Count);

                var assemblies = new List<Assembly>();
                foreach (var (assemblyName, path) in privateAssemblyPaths)
                {
                    try
                    {
                        var assembly = loadContext.LoadFromAssemblyPath(path);
                        assemblies.Add(assembly);
                        _logger.LogDebug("Plugin '{Plugin}': Loaded assembly '{Assembly}' from '{Path}'.",
                            entry.Request.PackageName, assemblyName, path);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "Failed to load assembly '{Assembly}' for plugin '{Plugin}'.",
                            assemblyName, entry.Request.PackageName);
                    }
                }

                var pluginVersion = entry.FlatDependencies.TryGetValue(entry.Request.PackageName, out var resolvedVersion)
                    ? resolvedVersion.ToNormalizedString()
                    : entry.Request.Version ?? "0.0.0";

                // Read nuspec and manifest
                var pluginCachedPath = _extractor.GetCachedPackagePath(entry.Request.PackageName, pluginVersion);
                NuGetPackageMetadata metadata = new();
                XDocument? nuspec = null;
                JsonElement? manifest = null;
                IReadOnlyList<NuGetPluginDependency> dependencyInfos = [];

                if (pluginCachedPath != null)
                {
                    var (nuspecMetadata, nuspecDoc) = NuspecReader.ReadFromExtractedPackage(pluginCachedPath);
                    metadata = nuspecMetadata;
                    nuspec = nuspecDoc;
                    pluginManifests.TryGetValue(entry.Request.PackageName, out manifest);

                    dependencyInfos = entry.FlatDependencies
                        .Select(kvp => new NuGetPluginDependency
                        {
                            PackageName = kvp.Key,
                            Version = kvp.Value.ToNormalizedString(),
                            Classification = entry.Classifications[kvp.Key],
                        })
                        .ToList();
                }

                var loadedPlugin = new NuGetPlugin(
                    entry.Request.PackageName, pluginVersion, assemblies, loadContext,
                    metadata, nuspec, manifest, dependencyInfos);
                loadedPlugins.Add(loadedPlugin);
                lock (_lock)
                {
                    _loadedPlugins.Add(loadedPlugin);
                }
                loadContext = null; // success -- ownership transferred to NuGetPlugin

                _logger.LogInformation("Plugin '{Plugin}' v{Version} loaded with {Count} assemblies.",
                    entry.Request.PackageName, pluginVersion, assemblies.Count);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                loadContext?.Unload();
                _logger.LogError(exception, "Failed to load plugin '{Plugin}'.", entry.Request.PackageName);
                failures.Add(new NuGetPluginFailure(entry.Request.PackageName,
                    $"Assembly load failed: {exception.Message}", Exception: exception));
            }
        }

        return loadedPlugins;
    }

    /// <summary>
    /// Unloads the specified plugin. Returns true if the plugin was found and unloaded.
    /// </summary>
    public bool UnloadPlugin(NuGetPlugin plugin)
    {
        lock (_lock)
        {
            if (_loadedPlugins.Remove(plugin))
            {
                plugin.Dispose();
                return true;
            }
            return false;
        }
    }

    private static bool IsExternalHostPackage(
        string packageName,
        Dictionary<string, NuGetDependencyClassification> classifications,
        HostDependencyResolver hostResolver)
    {
        return classifications.TryGetValue(packageName, out var classification)
            && classification == NuGetDependencyClassification.Host
            && !hostResolver.Contains(packageName);
    }

    private static bool IsAssemblyInDefaultContext(string assemblyName)
        => TrustedPlatformAssemblyNames.Value.Contains(assemblyName);

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            _hostAssemblyRegistry.Dispose();

            lock (_lock)
            {
                foreach (var plugin in _loadedPlugins)
                {
                    plugin.Dispose();
                }
                _loadedPlugins.Clear();
            }
        }
    }

    /// <summary>
    /// Holds the resolved dependency graph and classifications for a single plugin request.
    /// </summary>
    private record ResolvedPluginGraph(
        NuGetPluginReference Request,
        Dictionary<string, NuGetVersion> FlatDependencies,
        Dictionary<string, NuGetDependencyClassification> Classifications);
}
