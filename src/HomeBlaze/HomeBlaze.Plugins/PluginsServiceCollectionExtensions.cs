using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Plugins;

public static class PluginsServiceCollectionExtensions
{
    public static IServiceCollection AddHomeBlazePlugins(this IServiceCollection services, string? pluginConfigPath = null)
    {
        services.AddSingleton(serviceProvider =>
            new PluginLoaderService(
                pluginConfigPath,
                serviceProvider.GetRequiredService<ILoggerFactory>()));

        return services;
    }
}
