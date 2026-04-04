using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Plugins;

public static class PluginsServiceCollectionExtensions
{
    public static IServiceCollection AddHomeBlazePlugins(this IServiceCollection services, string? pluginConfigPath = null)
    {
        PluginConfiguration? config = null;
        if (pluginConfigPath != null && File.Exists(pluginConfigPath))
        {
            config = PluginConfiguration.LoadFrom(pluginConfigPath);
        }

        services.AddSingleton(serviceProvider =>
            new PluginLoader(
                config,
                serviceProvider.GetRequiredService<ILoggerFactory>()));

        return services;
    }
}
