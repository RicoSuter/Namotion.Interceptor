using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// Extension methods for registering hosted subjects with dependency injection.
/// </summary>
public static class HostedSubjectServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IHostedService"/> as a singleton and hosted service.
    /// </summary>
    /// <typeparam name="T">The hosted service type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure the instance after creation.</param>
    /// <param name="contextResolver">
    /// Optional resolver for the <see cref="IInterceptorSubjectContext"/>.
    /// If null, attempts to resolve from DI; if not registered in DI, no context is used.
    /// If provided, uses the resolver's return value (which may be null for explicitly no context).
    /// The context is only passed to the constructor if the subject has a constructor that accepts it.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddHostedSubject<T>(
        this IServiceCollection services,
        Action<T>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        where T : class, IHostedService
    {
        services.TryAddSingleton<T>(serviceProvider =>
        {
            var context = contextResolver != null
                ? contextResolver(serviceProvider)
                : serviceProvider.GetService<IInterceptorSubjectContext>();

            T instance;
            if (context != null && HasContextConstructor<T>())
            {
                instance = ActivatorUtilities.CreateInstance<T>(serviceProvider, context);
            }
            else
            {
                instance = ActivatorUtilities.CreateInstance<T>(serviceProvider);
            }

            configure?.Invoke(instance);
            return instance;
        });

        services.AddHostedService<T>(serviceProvider => serviceProvider.GetRequiredService<T>());

        return services;
    }

    private static bool HasContextConstructor<T>()
    {
        return typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Any(constructor => constructor.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(IInterceptorSubjectContext)));
    }
}
