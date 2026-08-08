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
    /// When <typeparamref name="T"/> does take a context, the attach can already have happened inside
    /// the constructor and cannot be reordered from here, so <paramref name="configure"/> necessarily
    /// runs against an attached subject. Its assignments are intercepted and tracked, and they race
    /// the start the attach appended exactly as they do for a hand written
    /// <c>new MySubject(context) { Name = "x" }</c>.
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
        services.TryAddSingleton<T>(serviceProvider =>
        {
            var context = contextResolver is not null
                ? contextResolver(serviceProvider)
                : serviceProvider.GetService<IInterceptorSubjectContext>();

            // The constructor branch exists only because ActivatorUtilities throws when no constructor
            // can consume the extra argument. It confers no attachment advantage: the generated
            // constructor is "C(IInterceptorSubjectContext context) : this()", so the attach happens
            // after the parameterless constructor body either way.
            if (context is not null && HasContextConstructor<T>())
            {
                var attachedInstance = ActivatorUtilities.CreateInstance<T>(serviceProvider, context);

                // Unconditional and idempotent. Applying it only when there is no context constructor
                // would leave the documented "MySubject(IInterceptorSubjectContext? context = null)"
                // shape unattached, because that constructor takes the context and never uses it.
                attachedInstance.Context.AddFallbackContext(context);

                // The generated constructor attached the subject before this factory ever saw it, so
                // the start that attach appended cannot be ordered behind configure from here.
                configure?.Invoke(attachedInstance);
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

    private static bool HasContextConstructor<T>()
    {
        return typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Any(constructor => constructor.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(IInterceptorSubjectContext)));
    }
}
