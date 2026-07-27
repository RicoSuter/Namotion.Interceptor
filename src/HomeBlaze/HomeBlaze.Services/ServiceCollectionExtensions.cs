using Microsoft.Extensions.DependencyInjection;
using HomeBlaze.Components.Abstractions.TimeZones;

namespace HomeBlaze.Services;

/// <summary>
/// Extension methods for registering HomeBlaze.Services in dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds HomeBlaze backend services to the service collection.
    /// This includes root management, serialization, type registry, and context factory.
    /// </summary>
    public static IServiceCollection AddHomeBlazeServices(this IServiceCollection services)
    {
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);
        var context = SubjectContextFactory.Create(services);

        services.AddSingleton(typeProvider);
        services.AddSingleton(typeRegistry);
        services.AddSingleton(context);

        services.AddSingleton<SubjectFactory>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<RootManager>();
        services.AddSingleton<SubjectPathResolver>();
        services.AddSingleton<DeveloperModeService>();
        services.AddScoped<ITimeZoneDisplay, TimeZoneDisplayService>();
        services.AddHostedService(sp =>
        {
            // SubjectPathResolver registers itself as a context service in its constructor, and
            // subjects attached by RootManager (history stores) need it the moment their hosted
            // service starts. Force the singleton before RootManager loads the graph; otherwise the
            // resolver would first be constructed by whichever Blazor component injects it, which
            // happens after startup.
            sp.GetRequiredService<SubjectPathResolver>();
            return sp.GetRequiredService<RootManager>();
        });

        return services;
    }
}
