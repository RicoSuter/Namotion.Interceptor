using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Plugins.Models;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Plugins;

[InterceptorSubject]
public partial class PluginManager : IConfigurable, ITitleProvider
{
    public string? Title => "Plugins";

    public PluginManager(PluginLoaderService pluginLoaderService)
    {
        Plugins = [];
        Feeds = [];
        HostPackages = [];
        LoadedPlugins = new Dictionary<string, PluginInfo>();

        // Read already-loaded results from service
        if (pluginLoaderService.Loader != null)
        {
            LoadedPlugins = pluginLoaderService.Loader.LoadedPlugins
                .ToDictionary(
                    group => group.PackageName,
                    group => new PluginInfo(this)
                    {
                        Name = group.PackageName,
                        Version = group.PackageVersion,
                        Assemblies = group.Assemblies
                            .Select(a =>
                            {
                                var name = a.GetName();
                                var version = name.Version?.ToString(3) ?? "?";
                                return $"{name.Name} v{version}";
                            })
                            .ToArray(),
                        Status = "Loaded"
                    });
        }
    }

    [Configuration]
    public partial PluginConfigEntry[] Plugins { get; set; }

    [Configuration]
    public partial PluginFeedEntry[] Feeds { get; set; }

    [Configuration]
    public partial string[] HostPackages { get; set; }

    [State]
    public partial Dictionary<string, PluginInfo> LoadedPlugins { get; internal set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [Operation(Title = "Add Plugin")]
    public void AddPlugin(string packageName, string version)
    {
        if (Plugins.Any(plugin => plugin.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Plugin '{packageName}' is already configured.");
        }

        Plugins = [.. Plugins, new PluginConfigEntry { PackageName = packageName, Version = version }];
    }

    internal void RemovePlugin(string packageName)
    {
        Plugins = Plugins
            .Where(plugin => !plugin.PackageName.Equals(packageName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
