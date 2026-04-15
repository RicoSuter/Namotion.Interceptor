using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Plugins;

public static class PluginsServiceCollectionExtensions
{
    /// <summary>
    /// Registers plugin loading services. When <paramref name="pluginConfigPath"/> is null
    /// or the file does not exist, no plugins are loaded (clean no-op).
    /// </summary>
    public static IServiceCollection AddHomeBlazePlugins(this IServiceCollection services, string? pluginConfigPath = null)
    {
        PluginConfiguration? config = null;
        if (pluginConfigPath != null && File.Exists(pluginConfigPath))
        {
            config = PluginConfiguration.LoadFrom(pluginConfigPath, AppContext.BaseDirectory);
        }

        services.AddSingleton(serviceProvider =>
            new PluginLoader(
                config,
                serviceProvider.GetRequiredService<ILoggerFactory>()));

        return services;
    }
}
