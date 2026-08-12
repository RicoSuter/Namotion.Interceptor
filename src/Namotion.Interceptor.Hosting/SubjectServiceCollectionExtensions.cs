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
    /// <para>
    /// Registration is idempotent, which has a sharp edge worth knowing: a second call for the same
    /// type silently drops its <paramref name="configure"/> and <paramref name="contextResolver"/>,
    /// and if the caller already registered <typeparamref name="T"/> themselves, neither the context
    /// nor <paramref name="configure"/> is applied.
    /// </para>
    /// <para>
    /// Where <paramref name="configure"/> runs relative to the context attach depends on the
    /// constructor shape of <typeparamref name="T"/>, and the attach is what makes a hosting enabled
    /// context start the subject. When <typeparamref name="T"/> has no constructor taking an
    /// <see cref="IInterceptorSubjectContext"/>, this method performs the attach itself and runs
    /// <paramref name="configure"/> before it, so the subject is fully configured before anything can
    /// start it. Its assignments are then not intercepted and not tracked, because the subject has no
    /// context yet.
    /// </para>
    /// <para>
    /// When <typeparamref name="T"/> does take a context, <paramref name="configure"/> still runs
    /// before the attach this method performs, but a generated context constructor has already
    /// attached the subject and that attach cannot be reordered from here. For that shape the
    /// assignments are intercepted and tracked, and they race the start the attach appended exactly as
    /// they do for a hand written <c>new MySubject(context) { Name = "x" }</c>. A constructor that
    /// takes the context and ignores it, which is the documented
    /// <c>MySubject(IInterceptorSubjectContext? context = null)</c> shape, attaches nothing, so there
    /// the attach below is the first one and <paramref name="configure"/> precedes it. Its assignments
    /// are then not intercepted and not tracked either, so that shape behaves exactly like the one with
    /// no context parameter at all.
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
            if (context is not null && contextFactory is not null)
            {
                var attachedInstance = (T)contextFactory(serviceProvider, [context]);

                // Ordered ahead of the attach below, which is the only ordering this factory controls.
                // For the generated constructor the subject is already attached and this changes
                // nothing, but for the documented "MySubject(IInterceptorSubjectContext? context = null)"
                // shape, which takes the context and never uses it, the attach below is the first one
                // and running configure after it would start the subject half configured.
                configure?.Invoke(attachedInstance);

                // Unconditional and idempotent. Applying it only when there is no context constructor
                // would leave that same shape unattached.
                attachedInstance.Context.AddFallbackContext(context);

                return attachedInstance;
            }

            var instance = ActivatorUtilities.CreateInstance<T>(serviceProvider);

            // Ordered ahead of the attach, which is the only ordering the factory controls: nothing
            // has seen this subject yet, so running configure first is what keeps a handler from
            // starting it half configured. The handler's start delay is not a synchronisation.
            configure?.Invoke(instance);

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
