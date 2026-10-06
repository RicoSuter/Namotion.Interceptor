using System.Globalization;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Plugins.Models;
using HomeBlaze.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.NuGet.Plugins;
using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Loading;

namespace HomeBlaze.Plugins;

/// <summary>
/// Loads NuGet packages as plugins and adds their assemblies to the <see cref="TypeProvider"/>.
/// Adding a plugin takes effect immediately. Removing a plugin, changing its version or changing the
/// feeds, host packages, host identifier or cache directory takes effect after a restart.
/// </summary>
[InterceptorSubject]
public partial class NuGetPluginProvider : BackgroundService, IConfigurable, ITitleProvider, IIconProvider
{
    private readonly TypeProvider _typeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<NuGetPluginProvider> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    // Requested version per package of every load attempt, successful or failed.
    private readonly Dictionary<string, string?> _requestedVersions = new(StringComparer.OrdinalIgnoreCase);

    // Captured on the first reconcile; the loader is always created from these options so it matches _loaderSettings.
    private NuGetPluginLoaderOptions? _loaderOptions;
    private string? _loaderSettings;

    // Never disposed: disposing it unloads plugin assemblies that live subjects still use.
    private NuGetPluginLoader? _loader;

    public NuGetPluginProvider(TypeProvider typeProvider, ILoggerFactory loggerFactory)
    {
        _typeProvider = typeProvider;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<NuGetPluginProvider>();

        Plugins = [];
        Feeds = [];
        HostPackages = [];
        HostIdentifier = null;
        CacheDirectory = null;
        LoadedPlugins = new Dictionary<string, Plugin>(StringComparer.OrdinalIgnoreCase);
        IsRestartRequired = false;
    }

    public string? Title => "Plugins";

    public string? IconName => "Extension";

    [Derived]
    public string? IconColor =>
        IsRestartRequired || LoadedPlugins.Values.Any(plugin => plugin.Status == ServiceStatus.Error) ? "Warning" : "Success";

    [Configuration]
    public partial PluginEntry[] Plugins { get; set; }

    [Configuration]
    public partial PluginFeedEntry[] Feeds { get; set; }

    [Configuration]
    public partial string[] HostPackages { get; set; }

    [Configuration]
    public partial string? HostIdentifier { get; set; }

    /// <summary>
    /// Folder for downloaded packages. Relative paths resolve against the data directory; empty means <c>Plugins/Cache</c>.
    /// </summary>
    [Configuration]
    public partial string? CacheDirectory { get; set; }

    [State]
    public partial Dictionary<string, Plugin> LoadedPlugins { get; internal set; }

    /// <summary>
    /// Whether a configuration change only takes effect after a restart.
    /// </summary>
    [State]
    public partial bool IsRestartRequired { get; internal set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ReconcileAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Plugin loading failed.");
        }
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => ReconcileAsync(cancellationToken);

    [Operation(Title = "Add Plugin")]
    public async Task AddPluginAsync(string packageName, string version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (Plugins.Any(plugin => plugin.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Plugin '{packageName}' is already configured.");
        }

        Plugins = [.. Plugins, new PluginEntry { PackageName = packageName, Version = version }];
        await ReconcileAsync(cancellationToken);
    }

    [Operation(Title = "Retry Failed Plugins")]
    public Task RetryAsync(CancellationToken cancellationToken) => ReconcileAsync(cancellationToken, retryFailed: true);

    internal Task RemovePluginAsync(string packageName)
    {
        Plugins = Plugins
            .Where(plugin => !plugin.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return ReconcileAsync(CancellationToken.None);
    }

    /// <summary>
    /// Brings the loaded plugins in line with the configuration: loads new entries (and failed ones when
    /// <paramref name="retryFailed"/> is set) and marks changes that need a restart.
    /// </summary>
    internal async Task ReconcileAsync(CancellationToken cancellationToken, bool retryFailed = false)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            var dataDirectory = ((IInterceptorSubject)this).Context.TryGetService<IDataDirectoryProvider>()?.DataDirectory;
            var options = CreateLoaderOptions(dataDirectory);
            var settings = GetLoaderSettings(options);

            if (_loaderOptions is null)
            {
                _loaderOptions = options;
                _loaderSettings = settings;
            }
            else if (settings != _loaderSettings)
            {
                IsRestartRequired = true;
            }

            var plugins = new Dictionary<string, Plugin>(LoadedPlugins, StringComparer.OrdinalIgnoreCase);
            var configured = Plugins
                .GroupBy(entry => entry.PackageName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var (packageName, plugin) in plugins.ToList())
            {
                var isRemoved = !configured.TryGetValue(packageName, out var entry);
                if (plugin.Status == ServiceStatus.Error)
                {
                    // None of a failed plugin's types are registered, so dropping or changing it needs no restart.
                    if (isRemoved)
                    {
                        plugins.Remove(packageName);
                    }

                    continue;
                }

                if (isRemoved)
                {
                    MarkRestartRequired(plugin, "Removed. Takes effect after a restart.");
                }
                else if (!IsRequestedVersion(entry!))
                {
                    MarkRestartRequired(plugin, $"Version {entry!.Version} takes effect after a restart.");
                }
            }

            var entriesToLoad = configured.Values
                .Where(entry =>
                    !plugins.TryGetValue(entry.PackageName, out var plugin) ||
                    plugin.Status == ServiceStatus.Error && (retryFailed || !IsRequestedVersion(entry)))
                .ToList();

            if (entriesToLoad.Count > 0)
            {
                await LoadAsync(entriesToLoad, plugins, cancellationToken);
            }

            LoadedPlugins = plugins;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    internal NuGetPluginLoaderOptions CreateLoaderOptions(string? dataDirectory)
    {
        var feeds = new List<NuGetFeed>(Feeds.Length);
        foreach (var feed in Feeds)
        {
            if (string.IsNullOrWhiteSpace(feed.Url))
            {
                _logger.LogWarning("Plugin feed '{Feed}' has no URL and is ignored.", feed.Name);
                continue;
            }

            feeds.Add(new NuGetFeed(feed.Name, NuGetPluginPaths.ResolveFeedUrl(feed.Url, dataDirectory), feed.ApiKey));
        }

        var hostPackages = HostPackages;
        return new NuGetPluginLoaderOptions
        {
            Feeds = feeds.Count > 0 ? feeds : [NuGetFeed.NuGetOrg],
            IsHostPackage = hostPackages.Length > 0
                ? name => NuGetPackageNameMatcher.IsMatchAny(name, hostPackages)
                : null,
            CacheDirectory = NuGetPluginPaths.ResolveCacheDirectory(CacheDirectory, dataDirectory),
            HostIdentifier = HostIdentifier,
        };
    }

    private async Task LoadAsync(List<PluginEntry> entries, Dictionary<string, Plugin> plugins, CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            _requestedVersions[entry.PackageName] = entry.Version;
        }

        _logger.LogInformation("Loading {Count} plugins...", entries.Count);

        NuGetPluginLoadResult result;
        try
        {
            if (_loader is null)
            {
                _loaderOptions!.HostDependencies ??= HostDependencyResolver.FromDepsJson();
                _loader = new NuGetPluginLoader(_loaderOptions, _loggerFactory.CreateLogger<NuGetPluginLoader>());
            }

            result = await _loader.LoadPluginsAsync(
                entries.Select(entry => new NuGetPluginReference(entry.PackageName, entry.Version)),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Plugin loading failed.");
            foreach (var entry in entries)
            {
                plugins[entry.PackageName] = CreateFailedPlugin(entry.PackageName, exception.Message);
            }

            return;
        }

        foreach (var loadedPlugin in result.LoadedPlugins)
        {
            plugins[loadedPlugin.PackageName] = RegisterTypes(loadedPlugin);
        }

        foreach (var failure in result.Failures)
        {
            _logger.LogError("Plugin '{Plugin}' failed to load: {Reason}", failure.PackageName, failure.Reason);
            plugins[failure.PackageName] = CreateFailedPlugin(failure.PackageName, failure.Reason);
        }

        _logger.LogInformation("Plugin loading complete: {Loaded} loaded, {Failed} failed.",
            result.LoadedPlugins.Count, result.Failures.Count);
    }

    private Plugin RegisterTypes(NuGetPlugin loadedPlugin)
    {
        IReadOnlyList<Type> skippedTypes;
        try
        {
            skippedTypes = _typeProvider.AddAssemblies(loadedPlugin.Assemblies);
        }
        catch (AggregateException exception)
        {
            // Only TypesChanged handlers failed; the types themselves are registered.
            _logger.LogWarning(exception, "Types of plugin '{Plugin}' were added, but a type change handler failed.", loadedPlugin.PackageName);
            var plugin = CreateLoadedPlugin(loadedPlugin);
            plugin.StatusMessage = exception.Message;
            return plugin;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Types of plugin '{Plugin}' could not be added.", loadedPlugin.PackageName);
            var plugin = CreateLoadedPlugin(loadedPlugin);
            plugin.Status = ServiceStatus.Error;
            plugin.StatusMessage = exception.Message;
            return plugin;
        }

        var loaded = CreateLoadedPlugin(loadedPlugin);
        if (skippedTypes.Count > 0)
        {
            foreach (var skippedType in skippedTypes)
            {
                var registeredAssembly = _typeProvider.Types
                    .FirstOrDefault(type => type.FullName == skippedType.FullName)?.Assembly.GetName().Name;
                _logger.LogWarning(
                    "Type {Type} from {Assembly} was skipped because {RegisteredAssembly} already provides a type with that name.",
                    skippedType.FullName, skippedType.Assembly.GetName().Name, registeredAssembly);
            }

            loaded.StatusMessage =
                $"Skipped {skippedTypes.Count} types already provided by other assemblies: {string.Join(", ", skippedTypes.Select(type => type.FullName))}";
        }

        return loaded;
    }

    private bool IsRequestedVersion(PluginEntry entry)
    {
        return string.Equals(_requestedVersions.GetValueOrDefault(entry.PackageName), entry.Version, StringComparison.OrdinalIgnoreCase);
    }

    private string GetLoaderSettings(NuGetPluginLoaderOptions options)
    {
        // Null and empty are distinct settings for the loader, so null gets its own marker.
        const string nullMarker = "\u0001";
        return string.Join('\0', [
            options.Feeds.Count.ToString(CultureInfo.InvariantCulture),
            .. options.Feeds.SelectMany(feed => new[] { feed.Name, feed.Url, feed.ApiKey ?? nullMarker }),
            HostPackages.Length.ToString(CultureInfo.InvariantCulture),
            .. HostPackages,
            options.HostIdentifier ?? nullMarker,
            options.CacheDirectory ?? nullMarker
        ]);
    }

    private void MarkRestartRequired(Plugin plugin, string message)
    {
        plugin.StatusMessage = message;
        IsRestartRequired = true;
    }

    private Plugin CreateLoadedPlugin(NuGetPlugin plugin)
    {
        return new Plugin(this)
        {
            Name = plugin.PackageName,
            Version = plugin.PackageVersion,
            Description = plugin.Metadata.Description,
            Authors = plugin.Metadata.Authors,
            IconUrl = plugin.Metadata.IconUrl,
            Tags = plugin.Metadata.Tags.ToArray(),
            HostDependencies = plugin.Dependencies
                .Where(dependency => dependency.Classification == NuGetDependencyClassification.Host)
                .Select(dependency => $"{dependency.PackageName} v{dependency.Version}")
                .ToArray(),
            PrivateDependencies = plugin.Dependencies
                .Where(dependency => dependency.Classification == NuGetDependencyClassification.Isolated)
                .Select(dependency => $"{dependency.PackageName} v{dependency.Version}")
                .ToArray(),
            Assemblies = plugin.Assemblies
                .Select(assembly =>
                {
                    var name = assembly.GetName();
                    return $"{name.Name} v{name.Version?.ToString(3) ?? "?"}";
                })
                .ToArray(),
            Status = ServiceStatus.Running
        };
    }

    private Plugin CreateFailedPlugin(string packageName, string reason)
    {
        return new Plugin(this)
        {
            Name = packageName,
            Version = "",
            Assemblies = [],
            Status = ServiceStatus.Error,
            StatusMessage = reason
        };
    }
}
