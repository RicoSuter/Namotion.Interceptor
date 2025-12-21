using HomeBlaze.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddScoped<ISubjectMethodInvoker, SubjectMethodInvoker>();
        services.AddHostedService(sp => sp.GetRequiredService<RootManager>());

        return services;
    }
}
