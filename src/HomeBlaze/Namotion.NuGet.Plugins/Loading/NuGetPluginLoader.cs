using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
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
    private readonly List<NuGetPluginGroup> _loadedPlugins = [];
    private readonly PackageExtractor _extractor;
    private readonly INuGetPackageRepository _repository;
    private readonly ConcurrentDictionary<string, string> _additionalHostAssemblyPaths = new();
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

        // Hook default context resolving for additional host packages
        AssemblyLoadContext.Default.Resolving += OnDefaultContextResolving;
    }

    /// <summary>
    /// Gets a snapshot of all currently loaded plugin groups.
    /// </summary>
    public IReadOnlyList<NuGetPluginGroup> LoadedPlugins
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
    /// Gets all types assignable to <typeparamref name="T"/> from all loaded plugin groups.
    /// </summary>
    public IEnumerable<Type> GetTypes<T>()
    {
        return LoadedPlugins.SelectMany(plugin => plugin.GetTypes<T>());
    }

    /// <summary>
    /// Gets all types matching the predicate from all loaded plugin groups.
    /// </summary>
    public IEnumerable<Type> GetTypes(Func<Type, bool> predicate)
    {
        return LoadedPlugins.SelectMany(plugin => plugin.GetTypes(predicate));
    }

    /// <summary>
    /// Resolves, downloads, validates, and loads the specified plugins and their transitive dependencies.
    /// </summary>
    public async Task<NuGetPluginLoadResult> LoadPluginsAsync(
        IEnumerable<NuGetPluginRequest> plugins,
        CancellationToken cancellationToken)
    {
        var pluginList = plugins.ToList();
        var failures = new List<NuGetPluginFailure>();
        var loadedGroups = new List<NuGetPluginGroup>();

        var hostResolver = _options.HostDependencies ?? new HostDependencyResolver([]);
        var classifier = new DependencyClassifier(
            hostResolver,
            _options.HostPackagePatterns,
            pluginList.Select(plugin => plugin.PackageName));

        // Phase 1 & 2: Resolve and classify each plugin's dependencies
        var pluginGraphs = new List<(NuGetPluginRequest Request, Dictionary<string, NuGetVersion> FlatDependencies, Dictionary<string, DependencyClassification> Classifications)>();

        var graphResolver = new DependencyGraphResolver(_options.Feeds, _logger);

        foreach (var request in pluginList)
        {
            try
            {
                DependencyNode graph;
                if (request.Path != null)
                {
                    // File-based plugin: extract version from .nupkg if not specified
                    var version = request.Version
                        ?? PackageExtractor.GetVersionFromPackage(request.Path)
                        ?? "0.0.0";
                    graph = new DependencyNode(request.PackageName,
                        NuGetVersion.Parse(version));
                }
                else
                {
                    graph = await graphResolver.ResolveAsync(
                        request.PackageName, request.Version, cancellationToken);
                }

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

        // Phase 3b: Validate additional host package version compatibility across plugins
        var additionalHostRequirements = new Dictionary<string, List<(string PluginName, global::NuGet.Versioning.VersionRange Range)>>(StringComparer.OrdinalIgnoreCase);
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
                        (request.PackageName, new global::NuGet.Versioning.VersionRange(version)));
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

                    if (request.Path != null && packageName == request.PackageName)
                    {
                        // Load from file
                        using var fileStream = File.OpenRead(request.Path);
                        _extractor.ExtractAndGetAssemblyPaths(packageName, versionString, fileStream);
                    }
                    else
                    {
                        var (_, stream) = await _repository.DownloadPackageAsync(
                            packageName, versionString, cancellationToken);
                        using (stream)
                        {
                            _extractor.ExtractAndGetAssemblyPaths(packageName, versionString, stream);
                        }
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

        // Phase 5: Load additional host packages into default context
        foreach (var (request, flatDependencies, classifications) in pluginGraphs)
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
                        _additionalHostAssemblyPaths[assemblyName] = path;
                    }
                }
            }
        }

        // Phase 6: Load plugin groups
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
                            privateAssemblyPaths[assemblyName] = path;
                        }
                    }
                }

                var loadContext = new PluginAssemblyLoadContext(
                    request.PackageName, hostAssemblyNames, privateAssemblyPaths);

                var assemblies = new List<Assembly>();
                foreach (var (assemblyName, path) in privateAssemblyPaths)
                {
                    try
                    {
                        var assembly = loadContext.LoadFromAssemblyPath(path);
                        assemblies.Add(assembly);
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

                var group = new NuGetPluginGroup(request.PackageName, pluginVersion, assemblies, loadContext);
                loadedGroups.Add(group);
                lock (_lock)
                {
                    _loadedPlugins.Add(group);
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

        return new NuGetPluginLoadResult(loadedGroups, failures);
    }

    /// <summary>
    /// Unloads a plugin group by package name. Returns true if the plugin was found and unloaded.
    /// </summary>
    public bool UnloadPlugin(string packageName)
    {
        lock (_lock)
        {
            var group = _loadedPlugins.FirstOrDefault(plugin =>
                plugin.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase));

            if (group != null)
            {
                group.Dispose();
                _loadedPlugins.Remove(group);
                return true;
            }
            return false;
        }
    }

    private Assembly? OnDefaultContextResolving(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (assemblyName.Name != null &&
            _additionalHostAssemblyPaths.TryGetValue(assemblyName.Name, out var path))
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
