using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Recorder;

namespace Namotion.Interceptor.Tracking;

public static class InterceptorCollectionExtensions
{
    public static IObservable<PropertyChangedContext> GetPropertyChangedObservable(this IInterceptorCollection collection)
    {
        return collection.GetService<PropertyChangedObservable>();
    }
    
    public static IInterceptorCollection WithFullPropertyTracking(this IInterceptorCollection collection)
    {
        return collection
            .WithEqualityCheck()
            .WithInterceptorInheritance()
            .WithDerivedPropertyChangeDetection();
    }

    public static IInterceptorCollection WithEqualityCheck(this IInterceptorCollection collection)
    {
        return collection
            .WithInterceptor(() => new PropertyValueEqualityCheckHandler());
    }

    public static IInterceptorCollection WithDerivedPropertyChangeDetection(this IInterceptorCollection collection)
    {
        collection
            .WithInterceptor(() => new DerivedPropertyChangeHandler())
            .TryAddService(collection.GetService<DerivedPropertyChangeHandler>, _ => true);

        return collection
            .WithProxyLifecycle()
            .WithPropertyChangedObservable();
    }

    public static IInterceptorCollection WithReadPropertyRecorder(this IInterceptorCollection collection)
    {
        return collection
            .WithInterceptor(() => new ReadPropertyRecorder());
    }

    /// <summary>
    /// Registers the property changed observable which can be retrieved using interceptable.GetPropertyChangedObservable().
    /// </summary>
    /// <param name="collection">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorCollection WithPropertyChangedObservable(this IInterceptorCollection collection)
    {
        return collection
            .WithInterceptor(() => new PropertyChangedObservable());
    }

    /// <summary>
    /// Adds automatic context assignment and <see cref="WithProxyLifecycle"/>.
    /// </summary>
    /// <param name="collection">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorCollection WithInterceptorInheritance(this IInterceptorCollection collection)
    {
        collection
            .TryAddService(() => new InterceptorInheritanceHandler(), _ => true);

        return collection
            .WithProxyLifecycle();
    }

    /// <summary>
    /// Adds support for <see cref="ILifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="collection">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorCollection WithProxyLifecycle(this IInterceptorCollection collection)
    {
        return collection
            .WithInterceptor(() => new LifecycleInterceptor(collection));
    }
    
    /// <summary>
    /// Automatically assigns the parents to the interceptable data.
    /// </summary>
    /// <param name="collection">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorCollection WithParents(this IInterceptorCollection collection)
    {
        return collection
            .WithInterceptor(() => new ParentTrackingHandler())
            .WithProxyLifecycle();
    }
}