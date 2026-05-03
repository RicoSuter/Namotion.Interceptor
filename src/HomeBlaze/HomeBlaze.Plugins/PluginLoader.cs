using Microsoft.Extensions.Logging;
using Namotion.NuGet.Plugins;
using Namotion.NuGet.Plugins.Configuration;
using Namotion.NuGet.Plugins.Loading;

namespace HomeBlaze.Plugins;

/// <summary>
/// Core bootstrap service that loads NuGet plugins before the subject system starts.
/// </summary>
public class PluginLoader : IDisposable
{
    private NuGetPluginLoader? _loader;
    private readonly PluginConfiguration? _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PluginLoader> _logger;
    private int _disposed;

    public PluginLoader(
        PluginConfiguration? config,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PluginLoader>();
    }

    public NuGetPluginLoader? Loader => _loader;

    public NuGetPluginLoadResult? LoadResult { get; private set; }

    public async Task<NuGetPluginLoadResult?> LoadPluginsAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (_config == null)
        {
            _logger.LogInformation("No plugin configuration found. Skipping plugin loading.");
            return null;
        }

        var options = _config.ToLoaderOptions(HostDependencyResolver.FromDepsJson());
        _loader = new NuGetPluginLoader(options, _loggerFactory.CreateLogger<NuGetPluginLoader>());

        _logger.LogInformation("Loading {Count} plugins...", _config.PluginReferences.Count);
        var result = await _loader.LoadPluginsAsync(_config.PluginReferences, cancellationToken);
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
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _loader?.Dispose();
            _loader = null;
        }
    }
}
