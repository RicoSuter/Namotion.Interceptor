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
    /// <para>
    /// One instance per type. A second call for the same type throws rather than silently dropping its
    /// <paramref name="configure"/> and <paramref name="contextResolver"/>,
    /// and if the caller already registered <typeparamref name="T"/> themselves, neither the context
    /// nor <paramref name="configure"/> is applied.
    /// </para>
    /// <para>
    /// <paramref name="configure"/> always runs before the attach this method performs, and the attach
    /// is what makes a hosting enabled context start the subject. When that attach is the first one,
    /// which is the case for a <typeparamref name="T"/> that has no context constructor and for one
    /// that takes a context and ignores it, the subject is fully configured before anything can start
    /// it and the assignments are neither intercepted nor tracked. When a generated context constructor
    /// has already attached the subject, that attach cannot be reordered from here, so the assignments
    /// are intercepted and tracked and they race the start it appended, exactly as they do for a hand
    /// written <c>new MySubject(context) { Name = "x" }</c>. The three shapes are set out side by side
    /// in docs/hosting.md.
    /// </para>
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
        GuardDuplicateRegistration<T>(services);

        var contextFactory = TryCreateContextFactory<T>();

        services.TryAddSingleton<T>(serviceProvider =>
        {
            var context = contextResolver is not null
                ? contextResolver(serviceProvider)
                : serviceProvider.GetService<IInterceptorSubjectContext>();

            // The constructor branch exists only because ActivatorUtilities throws when no constructor
            // can consume the extra argument. It confers no attachment advantage: the generated
            // constructor is "C(IInterceptorSubjectContext context) : this()", so the attach happens
            // after the parameterless constructor body either way.
            //
            // The decision is the factory itself rather than a reflection query, so the code that
            // decides a context taking constructor is usable is the code that then uses it. A
            // reflection query answers the looser question of whether any constructor mentions the
            // type, which can be true of one ActivatorUtilities cannot call.
            var instance = context is not null && contextFactory is not null
                ? (T)contextFactory(serviceProvider, [context])
                : ActivatorUtilities.CreateInstance<T>(serviceProvider);

            // Ordered ahead of the attach below, which is the only ordering this factory controls. For
            // the generated constructor the subject is already attached and this changes nothing, but
            // for the documented "MySubject(IInterceptorSubjectContext? context = null)" shape, which
            // takes the context and never uses it, the attach below is the first one and running
            // configure after it would start the subject half configured. The handler's start delay is
            // not a synchronisation, so this ordering is what keeps a handler from starting the subject
            // half configured.
            configure?.Invoke(instance);

            // Unconditional and idempotent, so the constructor shape that takes the context and ignores
            // it is attached rather than left out.
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
    /// Throws when the same subject type is registered twice. The activation is the marker: it is
    /// registered once per call, so finding one already there means this is a second call. Silently
    /// keeping the first registration would drop this call's configure and context resolver, which
    /// reads as a working registration and is not one.
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
    /// The factory for a constructor that takes the context, or null when the type has none that
    /// <see cref="ActivatorUtilities"/> can call with one. There is no Try form of
    /// <see cref="ActivatorUtilities.CreateFactory(Type, Type[])"/>, so the probe is a catch, but it
    /// runs once per registration and it caches the constructor selection.
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
