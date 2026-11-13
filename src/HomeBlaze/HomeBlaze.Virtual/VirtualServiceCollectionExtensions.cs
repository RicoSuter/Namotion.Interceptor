using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HomeBlaze.Virtual;

/// <summary>Registers the services of virtual subjects.</summary>
public static class VirtualServiceCollectionExtensions
{
    /// <summary>Adds the awaiter that virtual subjects use for properties added at run time.</summary>
    public static IServiceCollection AddHomeBlazeVirtual(this IServiceCollection services)
    {
        services.TryAddSingleton<VirtualPropertyAwaiter>();
        return services;
    }
}
