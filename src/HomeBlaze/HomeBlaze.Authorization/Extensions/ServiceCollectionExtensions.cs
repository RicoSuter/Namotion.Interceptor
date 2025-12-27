using HomeBlaze.Abstractions.Services;
using HomeBlaze.Authorization.Blazor;
using HomeBlaze.Authorization.Configuration;
using HomeBlaze.Authorization.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Authorization.Extensions;

/// <summary>
/// Extension methods for adding HomeBlaze authorization services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds HomeBlaze authorization services including Identity, cookie auth, and role hierarchy.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">SQLite connection string. Defaults to "Data Source=identity.db".</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHomeBlazeAuthorization(
        this IServiceCollection services,
        string connectionString = "Data Source=identity.db")
    {
        return services.AddHomeBlazeAuthorization(connectionString, _ => { });
    }

    /// <summary>
    /// Adds HomeBlaze authorization services with custom options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">SQLite connection string.</param>
    /// <param name="configureOptions">Action to configure authorization options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHomeBlazeAuthorization(
        this IServiceCollection services,
        string connectionString,
        Action<AuthorizationOptions> configureOptions)
    {
        // Register authorization options
        var options = new AuthorizationOptions();
        configureOptions(options);
        services.AddSingleton(options);

        // Add Identity services (includes DbContext, RoleExpander, ClaimsTransformation, Seeding)
        services.AddHomeBlazeIdentity(connectionString);

        // Register authorization resolver as singleton (uses thread-safe caching, no scoped dependencies)
        services.AddSingleton<IAuthorizationResolver, AuthorizationResolver>();

        // Register authorization data provider for JSON persistence
        services.AddSingleton<IAdditionalPropertiesSerializer, AuthorizationDataProvider>();

        // Register circuit handler for Blazor authorization context
        services.AddScoped<CircuitHandler, AuthorizationCircuitHandler>();

        // Register revalidating authentication state provider for Blazor Server
        // This periodically checks if the user is still valid (not deleted, not locked out, roles unchanged)
        services.AddScoped<AuthenticationStateProvider, HomeBlazeAuthenticationStateProvider>();

        // Register AuthorizedMethodInvoker as the implementation of ISubjectMethodInvoker
        // This wraps the base SubjectMethodInvoker with authorization checks
        // Note: HomeBlaze.Services must register SubjectMethodInvoker first, then this overrides it
        services.AddScoped<ISubjectMethodInvoker>(sp =>
        {
            var baseInvoker = new HomeBlaze.Services.SubjectMethodInvoker(sp);
            var resolver = sp.GetRequiredService<IAuthorizationResolver>();
            return new AuthorizedMethodInvoker(baseInvoker, resolver);
        });

        // Register a hosted service that initializes authorization on the subject context
        // This runs after the service provider is built and adds the authorization interceptor
        services.AddHostedService<AuthorizationContextInitializer>();

        return services;
    }
}