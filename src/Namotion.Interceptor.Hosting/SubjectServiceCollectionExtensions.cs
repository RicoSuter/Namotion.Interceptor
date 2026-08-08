using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// Extension methods for registering subjects with dependency injection.
/// </summary>
public static class SubjectServiceCollectionExtensions
{
    /// <summary>
    /// Registers the subject as a singleton and constructs it at host start, attaching it to the
    /// context. When the subject is an <see cref="Microsoft.Extensions.Hosting.IHostedService"/> and
    /// the context has hosting enabled, the context starts it.
    /// </summary>
    /// <remarks>
    /// Registration is idempotent, which has a sharp edge worth knowing: a second call for the same
    /// type silently drops its <paramref name="configure"/> and <paramref name="contextResolver"/>,
    /// and if the caller already registered <typeparamref name="T"/> themselves, neither the context
    /// nor <paramref name="configure"/> is applied.
    /// </remarks>
    /// <typeparam name="T">The subject type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback applied to the instance after construction.</param>
    /// <param name="contextResolver">
    /// Optional resolver for the context. When null, the context is resolved from DI; when it returns
    /// null, the subject is registered without a context.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSubject<T>(
        this IServiceCollection services,
        Action<T>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        where T : class, IInterceptorSubject
    {
        services.TryAddSingleton<T>(serviceProvider =>
        {
            var context = contextResolver is not null
                ? contextResolver(serviceProvider)
                : serviceProvider.GetService<IInterceptorSubjectContext>();

            // The constructor branch exists only because ActivatorUtilities throws when no constructor
            // can consume the extra argument. It confers no attachment advantage: the generated
            // constructor is "C(IInterceptorSubjectContext context) : this()", so the attach happens
            // after the parameterless constructor body either way.
            var instance = context is not null && HasContextConstructor<T>()
                ? ActivatorUtilities.CreateInstance<T>(serviceProvider, context)
                : ActivatorUtilities.CreateInstance<T>(serviceProvider);

            if (context is not null)
            {
                // Unconditional and idempotent. Applying it only when there is no context constructor
                // would leave the documented "MySubject(IInterceptorSubjectContext? context = null)"
                // shape unattached, because that constructor takes the context and never uses it.
                instance.Context.AddFallbackContext(context);
            }

            configure?.Invoke(instance);
            return instance;
        });

        services.AddHostedService<SubjectActivation<T>>();

        return services;
    }

    private static bool HasContextConstructor<T>()
    {
        return typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Any(constructor => constructor.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(IInterceptorSubjectContext)));
    }
}
