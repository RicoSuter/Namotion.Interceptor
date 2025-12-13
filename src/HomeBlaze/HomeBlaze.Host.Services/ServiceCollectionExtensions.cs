using HomeBlaze.Services.Components;
using HomeBlaze.Host.Services.Navigation;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Host.Services;

/// <summary>
/// Extension methods for registering HomeBlaze.Host.Services in dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds HomeBlaze host services to the service collection.
    /// This includes component registry, navigation, and display services.
    /// Also calls AddHomeBlazeServices() to include base services.
    /// </summary>
    public static IServiceCollection AddHomeBlazeHostServices(this IServiceCollection services)
    {
        // Include base services
        services.AddHomeBlazeServices();

        // UI-specific services
        services.AddSingleton<SubjectComponentRegistry>();
        services.AddSingleton<RoutePathResolver>();
        services.AddSingleton<NavigationItemResolver>();
        services.AddSingleton<DeveloperModeService>();

        return services;
    }
}
