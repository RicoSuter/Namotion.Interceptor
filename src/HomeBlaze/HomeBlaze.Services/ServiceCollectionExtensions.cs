using HomeBlaze.Services.Clients;
using HomeBlaze.Services.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
        services.AddSingleton<SubjectTypeCatalog>();
        services.AddSingleton<ConfigurableSubjectSerializer>();
        services.AddSingleton<RootManager>();
        // The root accessor is deferred so constructing the resolver does not pull in RootManager,
        // which depends on the resolver.
        services.AddSingleton(sp => new SubjectPathResolver(() => sp.GetRequiredService<RootManager>().Root));
        services.AddSingleton<DeveloperModeService>();
        services.AddSingleton(serviceProvider => new ClientConnectionService(
            serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System,
            serviceProvider.GetService<ILogger<ClientConnectionService>>() ?? NullLogger<ClientConnectionService>.Instance));
        services.AddScoped<ITimeZoneDisplay, TimeZoneDisplayService>();
        // It subscribes before any hosted service starts and unsubscribes after all have stopped. Registered
        // ahead of the HomeBlaze services so it also covers their lifecycle callbacks, but not those of
        // services registered earlier, such as by AddServiceDefaults.
        services.AddHostedService(serviceProvider => new FirstChanceExceptionLogger(
            serviceProvider.GetService<ILogger<FirstChanceExceptionLogger>>() ?? NullLogger<FirstChanceExceptionLogger>.Instance,
            serviceProvider.GetService<TimeProvider>()));
        services.AddHostedService(sp => sp.GetRequiredService<RootManager>());

        return services;
    }

    /// <summary>
    /// Adds <see cref="IHttpClientFactory"/> whose clients give up connecting after 10 seconds, so that a device that is
    /// not reachable fails fast. Requests that are connected have no limit besides the one of the client.
    /// </summary>
    public static IServiceCollection AddHomeBlazeHttpClients(this IServiceCollection services)
    {
        services.AddHttpClient();
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler((handler, _) =>
        {
            // Only the timeout is set, so the primary handler and its pooling stay those of the factory.
            if (handler is SocketsHttpHandler socketsHandler)
            {
                socketsHandler.ConnectTimeout = TimeSpan.FromSeconds(10);
            }
        }));
        return services;
    }
}
