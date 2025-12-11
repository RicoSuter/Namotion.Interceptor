using HomeBlaze.Services.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

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
        services.AddSingleton<TypeProvider>();
        services.AddSingleton<SubjectTypeRegistry>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<SubjectPathResolver>();
        services.AddSingleton<RootManager>();
        services.AddHostedService(sp => sp.GetRequiredService<RootManager>());

        // Create and register the context
        var context = SubjectContextFactory.Create(services);
        services.AddSingleton(context);

        return services;
    }
}
