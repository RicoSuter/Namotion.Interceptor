using HomeBlaze.Components.Controls;
using HomeBlaze.Host.Services;
using HomeBlaze.Host.TimeZones;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor.Services;
using Namotion.Interceptor.Blazor;

namespace HomeBlaze.Host;

/// <summary>
/// Extension methods for registering HomeBlaze.Host services in dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds HomeBlaze host UI services to the service collection.
    /// This includes MudBlazor and cascades to all underlying services.
    /// </summary>
    public static IServiceCollection AddHomeBlazeHost(this IServiceCollection services)
    {
        // Include Host.Services (which includes Services)
        services.AddHomeBlazeHostServices();

        // MudBlazor services
        services.AddMudServices();

        // Every component renders again when a property that its render read changes.
        services.AddBlazorSubjectTracking();

        services.AddHttpContextAccessor();
        services.AddScoped<TimeZoneInterop>();
        services.AddScoped<PropertyWriter>();

        // TryAdd, so that a test host can register its own clock first.
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }
}
