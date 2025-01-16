using Microsoft.Extensions.DependencyInjection;
using Namotion.Interception.Lifecycle;
using Namotion.Interception.Lifecycle.Abstractions;
using Namotion.Interception.Lifecycle.Handlers;
using Namotion.Proxy.Abstractions;
using Namotion.Proxy.Registry;
using Namotion.Proxy.Registry.Abstractions;
using Namotion.Proxy.Validation;

namespace Namotion.Proxy;

public static class InterceptorCollectionBuilderExtensions
{
    public static IInterceptorContextBuilder WithFullPropertyTracking(this IInterceptorContextBuilder builder)
    {
        return builder
            .WithEqualityCheck()
            .WithAutomaticContextAssignment()
            .WithDerivedPropertyChangeDetection();
    }

    public static IInterceptorContextBuilder WithEqualityCheck(this IInterceptorContextBuilder builder)
    {
        return builder
            .TryAddInterceptor((interceptors, sp) => new PropertyValueEqualityCheckHandler());
    }

    public static IInterceptorContextBuilder WithDerivedPropertyChangeDetection(this IInterceptorContextBuilder builder)
    {
        return builder
            .TryAddInterceptor((interceptors, sp) => new DerivedPropertyChangeHandler(interceptors))
            .TryAddSingleton<ILifecycleHandler, DerivedPropertyChangeHandler>((interceptors, sp) => 
                sp.GetRequiredService<DerivedPropertyChangeHandler>())
            .WithPropertyChangedObservable();
    }

    public static IInterceptorContextBuilder WithReadPropertyRecorder(this IInterceptorContextBuilder builder)
    {
        return builder
            .TryAddInterceptor((interceptors, sp) => new ReadPropertyRecorder(interceptors));
    }

    /// <summary>
    /// Registers support for <see cref="IProxyPropertyValidator"/> handlers.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorContextBuilder WithPropertyValidation(this IInterceptorContextBuilder builder)
    {
        return builder
            .TryAddInterceptor((interceptors, sp) => new ValidationInterceptor(sp.GetServices<IProxyPropertyValidator>()));
    }

    /// <summary>
    /// Adds support for data annotations on the interceptable properties and <see cref="WithPropertyValidation"/>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorContextBuilder WithDataAnnotationValidation(this IInterceptorContextBuilder builder)
    {
        builder
            .WithPropertyValidation()
            .TryAddSingleton<IProxyPropertyValidator, DataAnnotationsValidator>((interceptors, sp) => new DataAnnotationsValidator());

        return builder;
    }

    /// <summary>
    /// Registers the property changed observable which can be retrieved using interceptable.GetPropertyChangedObservable().
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorContextBuilder WithPropertyChangedObservable(this IInterceptorContextBuilder builder)
    {
        return builder
            .TryAddInterceptor((interceptors, sp) => new PropertyChangedObservable(interceptors));
    }

    /// <summary>
    /// Adds automatic context assignment and <see cref="WithProxyLifecycle"/>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorContextBuilder WithAutomaticContextAssignment(this IInterceptorContextBuilder builder)
    {
        return builder
            .TryAddSingleton<ILifecycleHandler, AssignInterceptorsHandler>((interceptors, sp) => new AssignInterceptorsHandler())
            .WithProxyLifecycle();
    }

    /// <summary>
    /// Adds support for <see cref="ILifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorContextBuilder WithProxyLifecycle(this IInterceptorContextBuilder builder)
    {
        return builder
            .TryAddInterceptor((interceptors, sp) => new LifecycleInterceptor(sp.GetServices<ILifecycleHandler>()));
    }

    /// <summary>
    /// Adds support for <see cref="ILifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorContextBuilder WithRegistry(this IInterceptorContextBuilder builder)
    {
        return builder
            .TryAddSingleton<IProxyRegistry, ProxyRegistry>((interceptors, sp) => new ProxyRegistry((IInterceptorContext)interceptors))
            .TryAddSingleton<ILifecycleHandler, ProxyRegistry>((interceptors, sp) => (ProxyRegistry)sp.GetRequiredService<IProxyRegistry>())
            .WithAutomaticContextAssignment();
    }

    /// <summary>
    /// Automatically assigns the parents to the interceptable data.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorContextBuilder WithParents(this IInterceptorContextBuilder builder)
    {
        return builder
            .TryAddSingleton<ILifecycleHandler, ParentTrackingHandler>((interceptors, sp) => new ParentTrackingHandler())
            .WithProxyLifecycle();
    }
}