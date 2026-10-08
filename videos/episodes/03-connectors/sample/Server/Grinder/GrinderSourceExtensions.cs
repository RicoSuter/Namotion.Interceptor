using Coffee;

namespace Connectors.Server.Grinder;

public static class GrinderSourceExtensions
{
    #region AddGrinderSource
    public static IServiceCollection AddGrinderSource(
        this IServiceCollection services, Func<IServiceProvider, BeanHopper> hopperSelector)
    {
        var key = Guid.NewGuid().ToString();
        services.AddKeyedSingleton(key, (serviceProvider, _) => new GrinderSource(
            hopperSelector(serviceProvider),
            serviceProvider.GetRequiredService<IGrinderDevice>(),
            serviceProvider.GetRequiredService<ILogger<GrinderSource>>()));
        services.AddSingleton<IHostedService>(serviceProvider =>
            serviceProvider.GetRequiredKeyedService<GrinderSource>(key));
        return services;
    }
    #endregion
}
