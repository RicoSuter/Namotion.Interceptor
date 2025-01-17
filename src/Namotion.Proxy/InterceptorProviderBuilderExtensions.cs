using Microsoft.Extensions.DependencyInjection;
using Namotion.Interception.Lifecycle;
using Namotion.Interception.Lifecycle.Abstractions;
using Namotion.Interception.Lifecycle.Handlers;
using Namotion.Proxy.Registry;
using Namotion.Proxy.Registry.Abstractions;
using Namotion.Proxy.Validation;

namespace Namotion.Proxy;

public static class InterceptorProviderBuilderExtensions
{
    public static IInterceptorProviderBuilder WithFullPropertyTracking(this IInterceptorProviderBuilder builder)
    {
        return builder
            .WithEqualityCheck()
            .WithAutomaticContextAssignment()
            .WithDerivedPropertyChangeDetection();
    }

    public static IInterceptorProviderBuilder WithEqualityCheck(this IInterceptorProviderBuilder builder)
    {
        return builder
            .TryAddInterceptor(_ => new PropertyValueEqualityCheckHandler());
    }

    public static IInterceptorProviderBuilder WithDerivedPropertyChangeDetection(this IInterceptorProviderBuilder builder)
    {
        return builder
            .TryAddInterceptor(_ => new DerivedPropertyChangeHandler())
            .TryAddSingleton<ILifecycleHandler, DerivedPropertyChangeHandler>(sp => 
                sp.GetRequiredService<DerivedPropertyChangeHandler>())
            .WithPropertyChangedObservable();
    }

    public static IInterceptorProviderBuilder WithReadPropertyRecorder(this IInterceptorProviderBuilder builder)
    {
        return builder
            .TryAddInterceptor(_ => new ReadPropertyRecorder());
    }

    /// <summary>
    /// Registers support for <see cref="IProxyPropertyValidator"/> handlers.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorProviderBuilder WithPropertyValidation(this IInterceptorProviderBuilder builder)
    {
        return builder
            .TryAddInterceptor(sp => new ValidationInterceptor(sp.GetServices<IProxyPropertyValidator>()));
    }

    /// <summary>
    /// Adds support for data annotations on the interceptable properties and <see cref="WithPropertyValidation"/>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorProviderBuilder WithDataAnnotationValidation(this IInterceptorProviderBuilder builder)
    {
        builder
            .WithPropertyValidation()
            .TryAddSingleton<IProxyPropertyValidator, DataAnnotationsValidator>(_ => new DataAnnotationsValidator());

        return builder;
    }

    /// <summary>
    /// Registers the property changed observable which can be retrieved using interceptable.GetPropertyChangedObservable().
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorProviderBuilder WithPropertyChangedObservable(this IInterceptorProviderBuilder builder)
    {
        return builder
            .TryAddInterceptor(_ => new PropertyChangedObservable());
    }

    /// <summary>
    /// Adds automatic context assignment and <see cref="WithProxyLifecycle"/>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorProviderBuilder WithAutomaticContextAssignment(this IInterceptorProviderBuilder builder)
    {
        return builder
            .TryAddSingleton<ILifecycleHandler, AssignInterceptorsHandler>(_ => new AssignInterceptorsHandler())
            .WithProxyLifecycle();
    }

    /// <summary>
    /// Adds support for <see cref="ILifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorProviderBuilder WithProxyLifecycle(this IInterceptorProviderBuilder builder)
    {
        return builder
            .TryAddInterceptor(sp => new LifecycleInterceptor(sp.GetServices<ILifecycleHandler>()));
    }

    /// <summary>
    /// Adds support for <see cref="ILifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorProviderBuilder WithRegistry(this IInterceptorProviderBuilder builder)
    {
        return builder
            .TryAddSingleton<IProxyRegistry, ProxyRegistry>(_ => new ProxyRegistry())
            .TryAddSingleton<ILifecycleHandler, ProxyRegistry>(sp => (ProxyRegistry)sp.GetRequiredService<IProxyRegistry>())
            .WithAutomaticContextAssignment();
    }

    /// <summary>
    /// Automatically assigns the parents to the interceptable data.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorProviderBuilder WithParents(this IInterceptorProviderBuilder builder)
    {
        return builder
            .TryAddSingleton<ILifecycleHandler, ParentTrackingHandler>(_ => new ParentTrackingHandler())
            .WithProxyLifecycle();
    }
}