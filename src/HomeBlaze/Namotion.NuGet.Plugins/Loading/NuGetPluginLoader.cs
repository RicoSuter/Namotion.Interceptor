using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly ConcurrentDictionary<string, string> _externalHostAssemblyPaths = new();
    private readonly object _lock = new();
    private bool _disposed;

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
            .Select(feed => (INuGetPackageRepository)new NuGetPackageRepository(feed, _logger))
            .ToList();
        _repository = new CompositeNuGetPackageRepository(repositories);

        // Hook default context resolving for external host packages
        AssemblyLoadContext.Default.Resolving += OnDefaultContextResolving;
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
        var pluginList = plugins.ToList();
        var failures = new List<NuGetPluginFailure>();
        var loadedPlugins = new List<NuGetPlugin>();

        var hostResolver = _options.HostDependencies ?? new HostDependencyResolver([]);
        var classifier = new DependencyClassifier(
            hostResolver,
            _options.HostPackages,
            pluginList.Select(plugin => plugin.PackageName));

        // Phase 1 & 2: Resolve and classify each plugin's dependencies
        var pluginGraphs = new List<(NuGetPluginReference Request, Dictionary<string, NuGetVersion> FlatDependencies, Dictionary<string, DependencyClassification> Classifications)>();

        var graphResolver = new DependencyGraphResolver(
            _options.Feeds, hostResolver, _options.HostPackages, _logger);

        foreach (var request in pluginList)
        {
            try
            {
                var graph = await graphResolver.ResolveAsync(
                    request.PackageName, request.Version, cancellationToken);

                var flatDependencies = graphResolver.FlattenDependencies(graph);
                var classifications = classifier.ClassifyAll(flatDependencies);
                pluginGraphs.Add((request, flatDependencies, classifications));
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to resolve dependencies for plugin '{Plugin}'.", request.PackageName);
                failures.Add(new NuGetPluginFailure(request.PackageName,
                    $"Dependency resolution failed: {exception.Message}", exception: exception));
            }
        }

        // Phase 3: Validate host dependency compatibility
        var allHostConflicts = new List<NuGetPluginConflict>();
        foreach (var (request, flatDependencies, classifications) in pluginGraphs)
        {
            var hostDependencies = flatDependencies
                .Where(kvp => classifications.TryGetValue(kvp.Key, out var classification) && classification == DependencyClassification.Host)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            var conflicts = VersionCompatibility.FindConflicts(hostDependencies, hostResolver, request.PackageName);
            allHostConflicts.AddRange(conflicts);
        }

        // Phase 3b: Validate external host package version compatibility across plugins
        var additionalHostRequirements = new Dictionary<string, List<(string PluginName, VersionRange Range)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (request, flatDependencies, classifications) in pluginGraphs)
        {
            foreach (var (packageName, version) in flatDependencies)
            {
                if (classifications[packageName] == DependencyClassification.Host &&
                    !hostResolver.Contains(packageName))
                {
                    if (!additionalHostRequirements.ContainsKey(packageName))
                    {
                        additionalHostRequirements[packageName] = [];
                    }

                    additionalHostRequirements[packageName].Add(
                        (request.PackageName, new VersionRange(version)));
                }
            }
        }

        if (additionalHostRequirements.Count > 0)
        {
            var hostVersionResult = HostPackageVersionResolver.ResolveVersions(additionalHostRequirements);
            if (!hostVersionResult.Success)
            {
                allHostConflicts.AddRange(hostVersionResult.Conflicts);
            }
        }

        if (allHostConflicts.Count > 0)
        {
            throw new NuGetPluginVersionConflictException(allHostConflicts);
        }

        // Phase 4: Download packages
        foreach (var (request, flatDependencies, classifications) in pluginGraphs)
        {
            try
            {
                foreach (var (packageName, version) in flatDependencies)
                {
                    var classification = classifications[packageName];
                    if (classification == DependencyClassification.Host &&
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

                    var (_, stream) = await _repository.DownloadPackageAsync(
                        packageName, versionString, cancellationToken);
                   
                    await using (stream)
                    {
                        _extractor.ExtractAndGetAssemblyPaths(packageName, versionString, stream);
                    }
                }
            }
            catch (Exception exception)
            {
                var isHostPackageFailure = flatDependencies.Any(kvp =>
                    classifications[kvp.Key] == DependencyClassification.Host &&
                    !hostResolver.Contains(kvp.Key));

                if (isHostPackageFailure)
                {
                    throw; // Host package failures fail everything
                }

                _logger.LogError(exception, "Failed to download packages for plugin '{Plugin}'.", request.PackageName);
                failures.Add(new NuGetPluginFailure(request.PackageName,
                    $"Download failed: {exception.Message}", exception: exception));
            }
        }

        // Phase 4b: Discover host-shared packages from plugin.json and assembly attributes
        var discoveredHostShared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pluginManifests = new Dictionary<string, JsonElement?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (request, flatDependencies, _) in pluginGraphs)
        {
            if (failures.Any(failure => failure.PackageName == request.PackageName))
            {
                continue;
            }

            // Read plugin.json from the plugin's own extracted package
            var pluginVersionString = flatDependencies[request.PackageName].ToNormalizedString();
            var pluginPath = _extractor.GetCachedPackagePath(request.PackageName, pluginVersionString);
            if (pluginPath != null)
            {
                var manifest = PluginManifestReader.Read(pluginPath);
                pluginManifests[request.PackageName] = manifest;

                foreach (var hostDep in PluginManifestReader.GetHostDependencies(manifest))
                {
                    discoveredHostShared.Add(hostDep);
                }
            }

            // Scan dependency DLLs for HostShared attribute
            foreach (var (packageName, version) in flatDependencies)
            {
                if (packageName == request.PackageName)
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
            var updatedClassifier = new DependencyClassifier(
                hostResolver, _options.HostPackages,
                pluginList.Select(plugin => plugin.PackageName), discoveredHostShared);

            for (var i = 0; i < pluginGraphs.Count; i++)
            {
                var (request, flatDeps, _) = pluginGraphs[i];
                var newClassifications = updatedClassifier.ClassifyAll(flatDeps);
                pluginGraphs[i] = (request, flatDeps, newClassifications);
            }
        }

        // Phase 5: Load external host packages into default context
        foreach (var (_, flatDependencies, classifications) in pluginGraphs)
        {
            foreach (var (packageName, version) in flatDependencies)
            {
                if (classifications[packageName] == DependencyClassification.Host &&
                    !hostResolver.Contains(packageName))
                {
                    var versionString = version.ToNormalizedString();
                    var paths = _extractor.GetAssemblyPaths(
                        Path.Combine(_extractor.GetCachedPackagePath(packageName, versionString)!));

                    foreach (var path in paths)
                    {
                        var assemblyName = Path.GetFileNameWithoutExtension(path);
                        _externalHostAssemblyPaths[assemblyName] = path;
                    }
                }
            }
        }

        // Phase 6: Load plugins
        foreach (var (request, flatDependencies, classifications) in pluginGraphs)
        {
            if (failures.Any(failure => failure.PackageName == request.PackageName))
            {
                continue; // Skip failed plugins
            }

            try
            {
                var hostAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var privateAssemblyPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var (packageName, version) in flatDependencies)
                {
                    var classification = classifications[packageName];
                    var versionString = version.ToNormalizedString();
                    var cachedPath = _extractor.GetCachedPackagePath(packageName, versionString);

                    if (classification == DependencyClassification.Host)
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
                                // Already available in default context (e.g., framework assembly)
                                hostAssemblyNames.Add(assemblyName);
                                _logger.LogDebug("Assembly '{Assembly}' from plugin '{Plugin}' already in default context, treating as host.",
                                    assemblyName, request.PackageName);
                            }
                            else
                            {
                                privateAssemblyPaths[assemblyName] = path;
                            }
                        }
                    }
                }

                var loadContext = new PluginAssemblyLoadContext(
                    request.PackageName, hostAssemblyNames, privateAssemblyPaths);

                _logger.LogDebug("Plugin '{Plugin}': {HostCount} host assemblies, {PrivateCount} private assemblies.",
                    request.PackageName, hostAssemblyNames.Count, privateAssemblyPaths.Count);

                var assemblies = new List<Assembly>();
                foreach (var (assemblyName, path) in privateAssemblyPaths)
                {
                    try
                    {
                        var assembly = loadContext.LoadFromAssemblyPath(path);
                        assemblies.Add(assembly);
                        _logger.LogDebug("Plugin '{Plugin}': Loaded assembly '{Assembly}' from '{Path}'.",
                            request.PackageName, assemblyName, path);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "Failed to load assembly '{Assembly}' for plugin '{Plugin}'.",
                            assemblyName, request.PackageName);
                    }
                }

                var pluginVersion = flatDependencies.TryGetValue(request.PackageName, out var resolvedVersion)
                    ? resolvedVersion.ToNormalizedString()
                    : request.Version ?? "0.0.0";

                // Read nuspec and manifest
                var pluginCachedPath = _extractor.GetCachedPackagePath(request.PackageName, pluginVersion);
                NuGetPackageMetadata metadata = new();
                XDocument? nuspec = null;
                JsonElement? manifest = null;
                IReadOnlyList<NuGetPluginDependencyInfo> dependencyInfos = [];

                if (pluginCachedPath != null)
                {
                    var (nuspecMetadata, nuspecDoc) = NuspecHelper.ReadFromExtractedPackage(pluginCachedPath);
                    metadata = nuspecMetadata;
                    nuspec = nuspecDoc;
                    pluginManifests.TryGetValue(request.PackageName, out manifest);

                    dependencyInfos = flatDependencies
                        .Select(kvp => new NuGetPluginDependencyInfo
                        {
                            PackageName = kvp.Key,
                            Version = kvp.Value.ToNormalizedString(),
                            Classification = classifications[kvp.Key],
                        })
                        .ToList();
                }

                var loadedPlugin = new NuGetPlugin(
                    request.PackageName, pluginVersion, assemblies, loadContext,
                    metadata, nuspec, manifest, dependencyInfos);
                loadedPlugins.Add(loadedPlugin);
                lock (_lock)
                {
                    _loadedPlugins.Add(loadedPlugin);
                }

                _logger.LogInformation("Plugin '{Plugin}' v{Version} loaded with {Count} assemblies.",
                    request.PackageName, pluginVersion, assemblies.Count);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to load plugin '{Plugin}'.", request.PackageName);
                failures.Add(new NuGetPluginFailure(request.PackageName,
                    $"Assembly load failed: {exception.Message}", exception: exception));
            }
        }

        return new NuGetPluginLoadResult(loadedPlugins, failures);
    }

    /// <summary>
    /// Unloads a plugin by package name. Returns true if the plugin was found and unloaded.
    /// </summary>
    public bool UnloadPlugin(string packageName)
    {
        lock (_lock)
        {
            var loadedPlugin = _loadedPlugins.FirstOrDefault(plugin =>
                plugin.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase));

            if (loadedPlugin != null)
            {
                loadedPlugin.Dispose();
                _loadedPlugins.Remove(loadedPlugin);
                return true;
            }
            return false;
        }
    }

    private static bool IsAssemblyInDefaultContext(string assemblyName)
    {
        return AssemblyLoadContext.Default.Assemblies
            .Any(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
    }

    private Assembly? OnDefaultContextResolving(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (assemblyName.Name != null &&
            _externalHostAssemblyPaths.TryGetValue(assemblyName.Name, out var path))
        {
            return context.LoadFromAssemblyPath(path);
        }
        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            AssemblyLoadContext.Default.Resolving -= OnDefaultContextResolving;
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
}
