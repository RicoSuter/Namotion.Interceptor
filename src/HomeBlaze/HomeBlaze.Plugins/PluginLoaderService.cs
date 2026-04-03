using Microsoft.Extensions.Logging;
using Namotion.NuGet.Plugins;
using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Loading;

namespace HomeBlaze.Plugins;

/// <summary>
/// Core bootstrap service that loads NuGet plugins before the subject system starts.
/// </summary>
public class PluginLoaderService : IDisposable
{
    private readonly NuGetPluginLoader? _loader;
    private readonly PluginConfiguration? _config;
    private readonly ILogger<PluginLoaderService> _logger;

    public PluginLoaderService(
        string? pluginConfigPath,
        ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<PluginLoaderService>();

        if (pluginConfigPath != null && File.Exists(pluginConfigPath))
        {
            _config = PluginConfiguration.LoadFrom(pluginConfigPath);
            var options = _config.ToLoaderOptions(HostDependencyResolver.FromDepsJson());
            _loader = new NuGetPluginLoader(options, loggerFactory.CreateLogger<NuGetPluginLoader>());
        }
    }

    public NuGetPluginLoader? Loader => _loader;

    public NuGetPluginLoadResult? LoadResult { get; private set; }

    public async Task<NuGetPluginLoadResult?> LoadPluginsAsync(CancellationToken cancellationToken)
    {
        if (_loader == null || _config == null)
        {
            _logger.LogInformation("No plugin configuration found. Skipping plugin loading.");
            return null;
        }

        _logger.LogInformation("Loading {Count} plugins...", _config.Plugins.Count);
        var result = await _loader.LoadPluginsAsync(_config.Plugins, cancellationToken);
        LoadResult = result;

        if (result.Failures.Count > 0)
        {
            foreach (var failure in result.Failures)
            {
                _logger.LogError("Plugin '{Plugin}' failed to load: {Reason}",
                    failure.PackageName, failure.Reason);
            }
        }

        _logger.LogInformation("Plugin loading complete: {Loaded} loaded, {Failed} failed.",
            result.LoadedPlugins.Count, result.Failures.Count);

        return result;
    }

    public void Dispose()
    {
        _loader?.Dispose();
    }
}
