using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Plugins.Models;
using Namotion.Interceptor.Attributes;
using Namotion.NuGet.Plugins;

namespace HomeBlaze.Plugins;

[InterceptorSubject]
public partial class PluginManager : IConfigurable, ITitleProvider, IIconProvider
{
    public string? Title => "Plugins";

    public string? IconName => "Extension";

    [Derived]
    public string? IconColor =>
        LoadedPlugins.Values.Any(p => p.Status == ServiceStatus.Error) ? "Warning" : "Success";

    public PluginManager(PluginLoader pluginLoader)
    {
        Plugins = [];
        Feeds = [];
        HostPackages = [];
        HostIdentifier = null;
        LoadedPlugins = new Dictionary<string, Plugin>();

        // Read already-loaded results from service
        var result = pluginLoader.LoadResult;
        if (result != null)
        {
            var plugins = new Dictionary<string, Plugin>();

            foreach (var plugin in result.LoadedPlugins)
            {
                plugins[plugin.PackageName] = new Plugin(this)
                {
                    Name = plugin.PackageName,
                    Version = plugin.PackageVersion,
                    Description = plugin.Metadata.Description,
                    Authors = plugin.Metadata.Authors,
                    IconUrl = plugin.Metadata.IconUrl,
                    Tags = plugin.Metadata.Tags.ToArray(),
                    HostDependencies = plugin.Dependencies
                        .Where(d => d.Classification == NuGetDependencyClassification.Host)
                        .Select(d => $"{d.PackageName} v{d.Version}")
                        .ToArray(),
                    PrivateDependencies = plugin.Dependencies
                        .Where(d => d.Classification == NuGetDependencyClassification.Isolated)
                        .Select(d => $"{d.PackageName} v{d.Version}")
                        .ToArray(),
                    Assemblies = plugin.Assemblies
                        .Select(a =>
                        {
                            var name = a.GetName();
                            var version = name.Version?.ToString(3) ?? "?";
                            return $"{name.Name} v{version}";
                        })
                        .ToArray(),
                    Status = ServiceStatus.Running
                };
            }

            foreach (var failure in result.Failures)
            {
                plugins[failure.PackageName] = new Plugin(this)
                {
                    Name = failure.PackageName,
                    Version = "",
                    Assemblies = [],
                    Status = ServiceStatus.Error,
                    StatusMessage = failure.Reason
                };
            }

            LoadedPlugins = plugins;
        }
    }

    [Configuration]
    public partial PluginEntry[] Plugins { get; set; }

    [Configuration]
    public partial PluginFeedEntry[] Feeds { get; set; }

    [Configuration]
    public partial string[] HostPackages { get; set; }

    [Configuration]
    public partial string? HostIdentifier { get; set; }

    [State]
    public partial Dictionary<string, Plugin> LoadedPlugins { get; internal set; }

    // Plugin configuration changes (feeds, packages, host packages) take effect
    // after a restart. Runtime hot-reload of plugins is not supported.
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [Operation(Title = "Add Plugin")]
    public void AddPlugin(string packageName, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (Plugins.Any(plugin => plugin.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Plugin '{packageName}' is already configured.");
        }

        Plugins = [.. Plugins, new PluginEntry { PackageName = packageName, Version = version }];
    }

    // TODO: Implement runtime plugin unloading. This requires:
    // 1. Call PluginLoader.Loader.UnloadPlugin() to unload the plugin assemblies
    // 2. Remove or convert live subject instances from the plugin (e.g., serialize to JSON subjects to preserve state)
    internal void RemovePlugin(string packageName)
    {
        Plugins = Plugins
            .Where(plugin => !plugin.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
