using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Namotion.Interceptor.Hosting;

/// <summary>Extension methods for registering subjects with dependency injection.</summary>
public static class SubjectServiceCollectionExtensions
{
    /// <summary>
    /// Registers the subject as a singleton, constructs it at host start and runs it: a subject that is
    /// a hosted service is started, an <see cref="ISubjectHostedServiceFactory"/> is activated with the
    /// application's service provider. Host start waits for those starts and fails on their faults.
    /// </summary>
    /// <remarks>
    /// Without <paramref name="contextResolver"/> the subject runs in a private context, with
    /// property tracking, lifecycle, hosting and whatever
    /// <see cref="IPrivateContextConfigurator.ConfigureContext"/> adds, ignores any context registered
    /// in dependency injection, and is stopped and detached from that context at host stop. With it, the
    /// subject joins the resolved context, and host start throws when that context has no hosting while
    /// the subject has something to run. One registration per type; use
    /// <see cref="AddKeyedSubject{T}"/> for several. If <typeparamref name="T"/> is already registered,
    /// neither <paramref name="configure"/> nor the context applies to that instance: the hosting
    /// context it is already in runs it. Otherwise, without a resolver, it runs in a private context
    /// when it is in no graph and host start throws when it is in a tracked graph; with one, host start
    /// throws when it is a hosted service or an <see cref="ISubjectHostedServiceFactory"/> and leaves a
    /// plain subject alone.
    /// </remarks>
    /// <typeparam name="T">The subject type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback applied to the instance after construction, before anything can start it.</param>
    /// <param name="contextResolver">Optional resolver for a shared context the subject joins.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSubject<T>(
        this IServiceCollection services,
        Action<T>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext>? contextResolver = null)
        where T : class, IInterceptorSubject
        => AddSubjectCore(services, serviceKey: null, configure, contextResolver);

    /// <summary>
    /// Registers the subject as a keyed singleton. Same modes as <see cref="AddSubject{T}"/>, each
    /// registration without a context resolver in a private context; one registration per type and key.
    /// A null <paramref name="serviceKey"/> registers it unkeyed, as <see cref="AddSubject{T}"/> does.
    /// </summary>
    /// <typeparam name="T">The subject type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="serviceKey">The service key.</param>
    /// <param name="configure">Optional callback applied to the instance after construction, before anything can start it.</param>
    /// <param name="contextResolver">Optional resolver for a shared context the subject joins.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="serviceKey"/> is <see cref="KeyedService.AnyKey"/>.</exception>
    public static IServiceCollection AddKeyedSubject<T>(
        this IServiceCollection services,
        object? serviceKey,
        Action<T>? configure = null,
        Func<IServiceProvider, IInterceptorSubjectContext>? contextResolver = null)
        where T : class, IInterceptorSubject
    {
        if (Equals(serviceKey, KeyedService.AnyKey))
        {
            throw new ArgumentException("A subject cannot be registered under KeyedService.AnyKey.", nameof(serviceKey));
        }

        return AddSubjectCore(services, serviceKey, configure, contextResolver);
    }

    private static IServiceCollection AddSubjectCore<T>(
        IServiceCollection services,
        object? serviceKey,
        Action<T>? configure,
        Func<IServiceProvider, IInterceptorSubjectContext>? contextResolver)
        where T : class, IInterceptorSubject
    {
        GuardDuplicateRegistration<T>(services, serviceKey);

        var registration = new SubjectRegistration<T>(serviceKey, configure, contextResolver, TryCreateContextFactory<T>());
        if (serviceKey is null)
        {
            services.AddSingleton(registration);
            services.TryAddSingleton<T>(registration.Create);
        }
        else
        {
            services.AddKeyedSingleton(serviceKey, registration);
            services.TryAddKeyedSingleton<T>(serviceKey, (serviceProvider, _) => registration.Create(serviceProvider));
        }

        // The activation is a keyed singleton of its own, keyed on the registration, so the registration
        // can hand it the private context host as soon as the instance is created. A factory registration
        // for the hosted service, not AddHostedService, which dedupes on implementation type and would
        // drop every registration of T after the first.
        services.AddKeyedSingleton(registration, (serviceProvider, _) => new SubjectActivation<T>(serviceProvider, registration));
        services.AddSingleton<IHostedService>(serviceProvider => serviceProvider.GetRequiredKeyedService<SubjectActivation<T>>(registration));
        return services;
    }

    /// <summary>
    /// Throws on a second registration of the same type and key. Keyed on the registration rather than
    /// on <typeparamref name="T"/>, so a caller who registered the type themselves is not caught.
    /// </summary>
    private static void GuardDuplicateRegistration<T>(IServiceCollection services, object? serviceKey)
        where T : class, IInterceptorSubject
    {
        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(SubjectRegistration<T>) &&
                descriptor.IsKeyedService == (serviceKey is not null) &&
                Equals(descriptor.ServiceKey, serviceKey)))
        {
            throw new InvalidOperationException(serviceKey is null
                ? $"{typeof(T).Name} is already registered with AddSubject. Use AddKeyedSubject to register several instances."
                : $"{typeof(T).Name} is already registered with AddKeyedSubject under the key '{serviceKey}'.");
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
