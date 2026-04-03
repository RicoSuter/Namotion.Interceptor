using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Plugins.Models;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Plugins;

[InterceptorSubject]
public partial class PluginManager : IConfigurable, ITitleProvider, IIconProvider
{
    public string? Title => "Plugins";

    public string? IconName => "Extension";

    [Derived]
    public string? IconColor =>
        LoadedPlugins.Values.Any(p => p.Status == ServiceStatus.Error) ? "Warning" : "Success";

    public PluginManager(PluginLoaderService pluginLoaderService)
    {
        Plugins = [];
        Feeds = [];
        HostPackages = [];
        LoadedPlugins = new Dictionary<string, PluginInfo>();

        // Read already-loaded results from service
        var result = pluginLoaderService.LoadResult;
        if (result != null)
        {
            var plugins = new Dictionary<string, PluginInfo>();

            foreach (var group in result.LoadedPlugins)
            {
                plugins[group.PackageName] = new PluginInfo(this)
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
                    Status = ServiceStatus.Running
                };
            }

            foreach (var failure in result.Failures)
            {
                plugins[failure.PackageName] = new PluginInfo(this)
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
