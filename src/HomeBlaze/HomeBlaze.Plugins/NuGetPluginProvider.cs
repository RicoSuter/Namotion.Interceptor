using System.Globalization;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Plugins.Models;
using HomeBlaze.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.NuGet.Plugins;
using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Loading;
using NuGet.Versioning;

namespace HomeBlaze.Plugins;

/// <summary>
/// Loads NuGet packages as plugins and adds their assemblies to the <see cref="TypeProvider"/>.
/// Adding a plugin, and removing or changing a plugin that failed to load, takes effect immediately.
/// Changing the feeds, host packages, host identifier or cache directory while no plugin has loaded also takes
/// effect immediately and loads the failed plugins again.
/// Removing a loaded plugin, changing its version or changing the feeds, host packages, host identifier or
/// cache directory after a plugin has loaded takes effect after a restart.
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

    // Status message of each loaded plugin at load time, shown again once a pending restart change is reverted.
    private readonly Dictionary<string, string?> _loadStatusMessages = new(StringComparer.OrdinalIgnoreCase);

    // Replaced on every reconcile until a plugin has loaded; the loader is created from these options.
    private NuGetPluginLoaderOptions? _loaderOptions;
    private string? _loaderSettings;

    // Disposed only while none of its plugins is loaded: disposing it unloads plugin assemblies that live subjects use.
    private NuGetPluginLoader? _loader;

    // Cancelled by StopAsync and replaced by StartAsync when already cancelled, so a restart does not leave
    // the instance stuck cancelled. Operations link against this rather than the token BackgroundService hands
    // ExecuteAsync because ExecuteAsync may not have run yet when they are called (StartAsync dispatches it via
    // Task.Run). A replaced source is never disposed: a caller already reading its .Token may still be using it.
    private CancellationTokenSource _stoppingCts = new();

    // Exposed for tests that seed a loaded plugin. Only change these under _loadLock: tests seed them
    // before any reconcile runs, and ReconcileAsync/LoadAsync read and write them while holding the lock.
    internal Dictionary<string, string?> RequestedVersions => _requestedVersions;

    internal Dictionary<string, string?> LoadStatusMessages => _loadStatusMessages;

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

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return ReconcileAndLogAsync(stoppingToken);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // A prior StopAsync left this cancelled; swap it before base.StartAsync schedules ExecuteAsync again.
        if (Volatile.Read(ref _stoppingCts).IsCancellationRequested)
        {
            Volatile.Write(ref _stoppingCts, new CancellationTokenSource());
        }

        // Taken before base.StartAsync while the hosted service start still defers startup, so startup cannot
        // complete in between, and before the reconcile adds types, so storages defer their upgrade in time.
        var startupDeferral = ((IInterceptorSubject)this).Context.DeferStartupCompletion();
        try
        {
            await base.StartAsync(cancellationToken);
        }
        catch
        {
            startupDeferral.Dispose();
            throw;
        }

        // Bound to this start's ExecuteAsync, so an earlier start's ExecuteAsync ending late cannot release it.
        StartupGate.ReleaseWhenCompleted(startupDeferral, ExecuteTask, _logger);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancelled directly, not only via the token passed to ExecuteAsync: ExecuteAsync may not have been
        // scheduled yet (base.StartAsync dispatches it via Task.Run), so that token might never exist yet.
        Volatile.Read(ref _stoppingCts).Cancel();
        return base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Starts reconciling the plugins with the configuration in the background and returns without waiting for it.
    /// </summary>
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        // Callers await this while processing file changes, which a package download must not hold up.
        var stoppingToken = StoppingToken;
        _ = Task.Run(() => ReconcileAndLogAsync(stoppingToken), CancellationToken.None);
        return Task.CompletedTask;
    }

    [Operation(Title = "Add Plugin")]
    public async Task AddPluginAsync(string packageName, string version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        // Linked so a download already running when the host stops is cancelled rather than outliving it.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, StoppingToken);
        await ReconcileAsync(linkedCts.Token, updatePlugins: plugins =>
        {
            if (plugins.Any(plugin => plugin.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Plugin '{packageName}' is already configured.");
            }

            return [.. plugins, new PluginEntry { PackageName = packageName, Version = version }];
        });
    }

    [Operation(Title = "Retry Failed Plugins")]
    public async Task RetryAsync(CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, StoppingToken);
        await ReconcileAsync(linkedCts.Token, retryFailed: true);
    }

    internal Task RemovePluginAsync(string packageName)
    {
        // No caller-supplied token to link: the stopping token is the only cancellation source here.
        return ReconcileAsync(StoppingToken, updatePlugins: plugins => plugins
            .Where(plugin => !plugin.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase))
            .ToArray());
    }

    private CancellationToken StoppingToken => Volatile.Read(ref _stoppingCts).Token;

    /// <summary>
    /// Brings the loaded plugins in line with the configuration: loads new entries (and failed ones when
    /// <paramref name="retryFailed"/> is set) and marks changes that need a restart.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for and loading packages.</param>
    /// <param name="retryFailed">Whether plugins that failed to load are loaded again.</param>
    /// <param name="updatePlugins">Replaces <see cref="Plugins"/> under the load lock before reconciling.</param>
    internal async Task ReconcileAsync(
        CancellationToken cancellationToken,
        bool retryFailed = false,
        Func<PluginEntry[], PluginEntry[]>? updatePlugins = null)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (updatePlugins is not null)
            {
                Plugins = updatePlugins(Plugins);
            }

            var dataDirectory = ((IInterceptorSubject)this).Context.TryGetService<IDataDirectoryProvider>()?.DataDirectory;
            var options = CreateLoaderOptions(dataDirectory);
            var settings = GetLoaderSettings(options);

            var isRestartRequired = false;
            if (_loader is null || _loader.LoadedPlugins.Count == 0)
            {
                if (_loaderSettings is not null && settings != _loaderSettings)
                {
                    // The new settings are the likely fix for plugins that failed with the old ones.
                    retryFailed = true;
                    _loader?.Dispose();
                    _loader = null;
                }

                _loaderOptions = options;
                _loaderSettings = settings;
            }
            else
            {
                isRestartRequired = settings != _loaderSettings;
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

                var restartMessage =
                    isRemoved ? "Removed. Takes effect after a restart." :
                    !IsRequestedVersion(entry!) ? $"Version {entry!.Version} takes effect after a restart." :
                    null;

                plugin.StatusMessage = restartMessage ?? _loadStatusMessages.GetValueOrDefault(packageName);
                isRestartRequired |= restartMessage is not null;
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
            IsRestartRequired = isRestartRequired;
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
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Plugin loading failed.");
            foreach (var entry in entries)
            {
                SetFailedPlugin(plugins, entry.PackageName, exception.Message);
            }

            return;
        }

        foreach (var loadedPlugin in result.LoadedPlugins)
        {
            var plugin = RegisterTypes(loadedPlugin);
            plugins[loadedPlugin.PackageName] = plugin;
            if (plugin.Status == ServiceStatus.Running)
            {
                _loadStatusMessages[loadedPlugin.PackageName] = plugin.StatusMessage;
            }
            else
            {
                _loadStatusMessages.Remove(loadedPlugin.PackageName);
            }
        }

        foreach (var failure in result.Failures)
        {
            _logger.LogError("Plugin '{Plugin}' failed to load: {Reason}", failure.PackageName, failure.Reason);
            SetFailedPlugin(plugins, failure.PackageName, failure.Reason);
        }

        _logger.LogInformation("Plugin loading complete: {Loaded} loaded, {Failed} failed.",
            result.LoadedPlugins.Count, result.Failures.Count);
    }

    private Plugin RegisterTypes(NuGetPlugin loadedPlugin)
    {
        IReadOnlyList<Type> skippedTypes;
        try
        {
            // TypesChanged handlers run here under _loadLock, so they must not call back into this provider's reconcile.
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

            // None of its types are registered, so unloading lets a retry load it again cleanly.
            _loader!.UnloadPlugin(loadedPlugin);
            return plugin;
        }

        var loaded = CreateLoadedPlugin(loadedPlugin);
        if (skippedTypes.Count > 0)
        {
            foreach (var skippedType in skippedTypes)
            {
                var registeredAssembly = skippedType.FullName is not null
                    && _typeProvider.TryGetType(skippedType.FullName, out var registeredType)
                        ? registeredType.Assembly.GetName().Name
                        : null;
                _logger.LogWarning(
                    "Type {Type} from {Assembly} was skipped because {RegisteredAssembly} already provides a type with that name.",
                    skippedType.FullName, skippedType.Assembly.GetName().Name, registeredAssembly);
            }

            loaded.StatusMessage =
                $"Skipped {skippedTypes.Count} types already provided by other assemblies: {string.Join(", ", skippedTypes.Select(type => type.FullName))}";
        }

        return loaded;
    }

    private async Task ReconcileAndLogAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReconcileAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // Nobody observes this task, so errors are logged here; once stopping they are expected and dropped.
            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(exception, "Plugin loading failed.");
            }
        }
    }

    private bool IsRequestedVersion(PluginEntry entry)
    {
        return IsSameVersion(_requestedVersions.GetValueOrDefault(entry.PackageName), entry.Version);
    }

    private static bool IsSameVersion(string? version, string? otherVersion)
    {
        return NuGetVersion.TryParse(version, out var parsedVersion) && NuGetVersion.TryParse(otherVersion, out var parsedOtherVersion)
            ? parsedVersion == parsedOtherVersion
            : string.Equals(version, otherVersion, StringComparison.OrdinalIgnoreCase);
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

    private void SetFailedPlugin(Dictionary<string, Plugin> plugins, string packageName, string reason)
    {
        plugins[packageName] = CreateFailedPlugin(packageName, reason);
        _loadStatusMessages.Remove(packageName);
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
