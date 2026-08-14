using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    /// One instance per type; a second call throws. <paramref name="configure"/> runs before the attach
    /// this method performs, so a subject whose first attach is that one is fully configured before
    /// anything can start it. A generated context constructor has already attached the subject, so there
    /// its assignments race the start, as they do for <c>new MySubject(context) { Name = "x" }</c>. The
    /// three constructor shapes are compared in docs/hosting.md.
    /// </remarks>
    /// <typeparam name="T">The subject type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback applied to the instance after construction.</param>
    /// <param name="contextResolver">
    /// Optional resolver for the context this method attaches the subject to. When the resolver itself
    /// is null that context is taken from dependency injection instead.
    /// <para>
    /// A resolver that returns null makes this method attach nothing, which is not the same as the
    /// subject ending up with no context. What decides that is the constructor
    /// <see cref="ActivatorUtilities"/> picks: a generated <c>T(IInterceptorSubjectContext)</c>
    /// constructor attaches the context dependency injection supplies, so the subject is attached
    /// anyway, while a constructor that takes the context and ignores it, or takes none at all, leaves
    /// it unattached. To keep every shape away from a context, do not register one.
    /// </para></param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSubject<T>(
        this IServiceCollection services,
        Action<T>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext?>? contextResolver = null)
        where T : class, IInterceptorSubject
    {
        GuardDuplicateRegistration<T>(services);

        var contextFactory = TryCreateContextFactory<T>();

        services.TryAddSingleton<T>(serviceProvider =>
        {
            var context = contextResolver is not null
                ? contextResolver(serviceProvider)
                : serviceProvider.GetService<IInterceptorSubjectContext>();

            // The factory is the decision, not a reflection query: reflection answers the looser question
            // of whether a constructor mentions the type, which can be true of one that cannot be called
            // with it.
            var instance = context is not null && contextFactory is not null
                ? (T)contextFactory(serviceProvider, [context])
                : ActivatorUtilities.CreateInstance<T>(serviceProvider);

            // Before the attach, the only ordering this factory controls: for a shape whose first attach
            // is the one below, configuring after it would let a handler start the subject half
            // configured. The start delay is a mitigation, not a synchronisation.
            configure?.Invoke(instance);

            // Also for the shape that takes the context and ignores it, which is otherwise unattached.
            if (context is not null)
            {
                instance.Context.AddFallbackContext(context);
            }

            return instance;
        });

        services.AddHostedService<SubjectActivation<T>>();

        return services;
    }

    /// <summary>
    /// Throws on a second registration of the same type. Keyed on the activation rather than on
    /// <typeparamref name="T"/>, so a caller who registered the type themselves is not caught. Keeping
    /// the first silently would drop this call's configure and resolver and still read as registered.
    /// </summary>
    private static void GuardDuplicateRegistration<T>(IServiceCollection services)
        where T : class, IInterceptorSubject
    {
        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType == typeof(SubjectActivation<T>)))
        {
            throw new InvalidOperationException(
                $"{typeof(T).Name} is already registered with AddSubject. Register it once. To run " +
                "several instances of one subject type, construct them and attach them to the object " +
                "graph rather than registering each one.");
        }
    }

    /// <summary>
    /// The factory for a constructor taking the context, or null when there is none. A catch because
    /// there is no Try form; it runs once per registration.
    /// </summary>
    private static ObjectFactory? TryCreateContextFactory<T>()
    {
        try
        {
            return ActivatorUtilities.CreateFactory(typeof(T), [typeof(IInterceptorSubjectContext)]);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
