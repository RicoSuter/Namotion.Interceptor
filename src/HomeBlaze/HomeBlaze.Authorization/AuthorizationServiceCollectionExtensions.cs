using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Authorization;

/// <summary>
/// Registers the HomeBlaze authorization services.
/// </summary>
public static class AuthorizationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AuthorizationService"/> as a singleton over the registered <see cref="IInterceptorSubjectContext"/>.
    /// A second call has no effect.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="pathProvider">
    /// The path provider of the tools that show the graph to the principals, which rule paths are matched against,
    /// see <see cref="AuthorizationService(IInterceptorSubjectContext, PathProviderBase, ILogger{AuthorizationService})"/>.
    /// </param>
    public static IServiceCollection AddHomeBlazeAuthorization(this IServiceCollection services, PathProviderBase pathProvider)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);

        services.TryAddSingleton(serviceProvider => new AuthorizationService(
            serviceProvider.GetRequiredService<IInterceptorSubjectContext>(),
            pathProvider,
            serviceProvider.GetService<ILogger<AuthorizationService>>()));
        return services;
    }
}
