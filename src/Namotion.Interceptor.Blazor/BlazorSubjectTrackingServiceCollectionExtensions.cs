using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Namotion.Interceptor.Blazor;

public static class BlazorSubjectTrackingServiceCollectionExtensions
{
    /// <summary>
    /// Makes every component record the intercepted property reads of its own render and render again when one of
    /// them changes. Requires a context with property change tracking and the read property recorder.
    /// </summary>
    /// <remarks>
    /// Registers an <see cref="IComponentActivator"/>: it replaces one registered before this call and is replaced by one
    /// registered after it.
    /// </remarks>
    public static IServiceCollection AddBlazorSubjectTracking(this IServiceCollection services, Action<BlazorSubjectTrackingOptions>? configure = null)
    {
        services.AddOptions<BlazorSubjectTrackingOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddScoped<ComponentTrackingDispatcher>();
        services.AddTransient<IComponentActivator, ComponentTrackingActivator>();
        return services;
    }
}
