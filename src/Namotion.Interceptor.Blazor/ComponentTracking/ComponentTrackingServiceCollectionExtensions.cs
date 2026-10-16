using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Namotion.Interceptor.Blazor.ComponentTracking;

public static class ComponentTrackingServiceCollectionExtensions
{
    /// <summary>
    /// Makes every component record the intercepted property reads of its own render and render again when one of
    /// them changes. Requires a context with property change tracking and the read property recorder.
    /// </summary>
    public static IServiceCollection AddComponentTracking(this IServiceCollection services, Action<ComponentTrackingOptions>? configure = null)
    {
        services.AddOptions<ComponentTrackingOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddScoped<ComponentTrackingDispatcher>();
        services.AddScoped<IComponentActivator, ComponentTrackingActivator>();
        return services;
    }
}
