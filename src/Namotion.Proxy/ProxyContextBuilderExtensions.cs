using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;
using Namotion.Proxy.ChangeTracking;
using Namotion.Proxy.Lifecycle;
using Namotion.Proxy.Registry;
using Namotion.Proxy.Validation;

namespace Namotion.Proxy;

public static class ProxyContextBuilderExtensions
{
    public static IProxyContextBuilder WithFullPropertyTracking(this IProxyContextBuilder builder)
    {
        return builder
            .WithEqualityCheck()
            .WithAutomaticContextAssignment()
            .WithDerivedPropertyChangeDetection();
    }

    public static IProxyContextBuilder WithEqualityCheck(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(_ => new PropertyValueEqualityCheckHandler());
    }

    public static IProxyContextBuilder WithDerivedPropertyChangeDetection(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(context => new DerivedPropertyChangeDetectionHandler(context))
            .WithPropertyChangedObservable();
    }

    public static IProxyContextBuilder WithReadPropertyRecorder(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(context => new ReadPropertyRecorder(context));
    }

    /// <summary>
    /// Registers support for <see cref="IProxyPropertyValidator"/> handlers.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IProxyContextBuilder WithPropertyValidation(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(context => new ProxyValidationHandler(builder.GetLazyHandlers<IProxyPropertyValidator>(context)));
    }

    /// <summary>
    /// Adds support for data annotations on the interceptable properties and <see cref="WithPropertyValidation"/>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IProxyContextBuilder WithDataAnnotationValidation(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(_ => new DataAnnotationsValidator())
            .WithPropertyValidation();
    }

    /// <summary>
    /// Registers the property changed observable which can be retrieved using interceptable.GetPropertyChangedObservable().
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IProxyContextBuilder WithPropertyChangedObservable(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(context => new PropertyChangedObservable(context));
    }

    /// <summary>
    /// Adds automatic context assignment and <see cref="WithProxyLifecycle"/>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IProxyContextBuilder WithAutomaticContextAssignment(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(context => new AutomaticContextAssignmentHandler(context))
            .WithProxyLifecycle();
    }

    /// <summary>
    /// Adds support for <see cref="IProxyLifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IProxyContextBuilder WithProxyLifecycle(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(context => new ProxyLifecycleHandler(builder.GetLazyHandlers<IProxyLifecycleHandler>(context)));
    }

    /// <summary>
    /// Adds support for <see cref="IProxyLifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IProxyContextBuilder WithRegistry(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(context => new ProxyRegistry(context))
            .WithAutomaticContextAssignment();
    }

    /// <summary>
    /// Automatically assigns the parents to the interceptable data.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IProxyContextBuilder WithParents(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(_ => new ParentsHandler())
            .WithProxyLifecycle();
    }
}