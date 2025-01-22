using Namotion.Interceptor.Tracking.Abstractions;
using Namotion.Interceptor.Tracking.Handlers;

namespace Namotion.Interceptor.Tracking;

public static class InterceptorCollectionExtensions
{
    public static IInterceptorCollection WithFullPropertyTracking(this IInterceptorCollection collection)
    {
        return collection
            .WithEqualityCheck()
            .WithInterceptorInheritance()
            .WithDerivedPropertyChangeDetection();
    }

    public static IInterceptorCollection WithEqualityCheck(this IInterceptorCollection builder)
    {
        return builder
            .WithInterceptor(() => new PropertyValueEqualityCheckHandler());
    }

    public static IInterceptorCollection WithDerivedPropertyChangeDetection(this IInterceptorCollection builder)
    {
        builder
            .WithInterceptor(() => new DerivedPropertyChangeHandler())
            .TryAddService<ILifecycleHandler, DerivedPropertyChangeHandler>(builder.GetService<DerivedPropertyChangeHandler>);

        return builder
            .WithProxyLifecycle()
            .WithPropertyChangedObservable();
    }

    public static IInterceptorCollection WithReadPropertyRecorder(this IInterceptorCollection builder)
    {
        return builder
            .WithInterceptor(() => new ReadPropertyRecorder());
    }

    /// <summary>
    /// Registers the property changed observable which can be retrieved using interceptable.GetPropertyChangedObservable().
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorCollection WithPropertyChangedObservable(this IInterceptorCollection builder)
    {
        return builder
            .WithInterceptor(() => new PropertyChangedObservable());
    }

    /// <summary>
    /// Adds automatic context assignment and <see cref="WithProxyLifecycle"/>.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorCollection WithInterceptorInheritance(this IInterceptorCollection builder)
    {
        builder
            .TryAddService<ILifecycleHandler, InterceptorInheritanceHandler>(() => new InterceptorInheritanceHandler());

        return builder
            .WithProxyLifecycle();
    }

    /// <summary>
    /// Adds support for <see cref="ILifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorCollection WithProxyLifecycle(this IInterceptorCollection builder)
    {
        return builder
            .WithInterceptor(() => new LifecycleInterceptor(builder.GetServices<ILifecycleHandler>()));
    }
    
    /// <summary>
    /// Automatically assigns the parents to the interceptable data.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorCollection WithParents(this IInterceptorCollection builder)
    {
        return builder
            .WithInterceptor(() => new ParentTrackingHandler())
            .WithProxyLifecycle();
    }
}