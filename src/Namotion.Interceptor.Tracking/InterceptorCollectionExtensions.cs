using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Recorder;

namespace Namotion.Interceptor.Tracking;

public static class InterceptorCollectionExtensions
{
    public static IObservable<PropertyChangedContext> GetPropertyChangedObservable(this IInterceptorSubjectContext context)
    {
        return context.GetService<PropertyChangedObservable>();
    }
    
    public static IInterceptorSubjectContext WithFullPropertyTracking(this IInterceptorSubjectContext context)
    {
        return context
            .WithEqualityCheck()
            .WithContextInheritance()
            .WithDerivedPropertyChangeDetection();
    }

    public static IInterceptorSubjectContext WithEqualityCheck(this IInterceptorSubjectContext context)
    {
        return context
            .WithInterceptor(() => new PropertyValueEqualityCheckHandler());
    }

    public static IInterceptorSubjectContext WithDerivedPropertyChangeDetection(this IInterceptorSubjectContext context)
    {
        context
            .WithInterceptor(() => new DerivedPropertyChangeHandler())
            .TryAddService(context.GetService<DerivedPropertyChangeHandler>, _ => true);

        return context
            .WithLifecycle()
            .WithPropertyChangedObservable();
    }

    public static IInterceptorSubjectContext WithReadPropertyRecorder(this IInterceptorSubjectContext context)
    {
        return context
            .WithInterceptor(() => new ReadPropertyRecorder());
    }

    /// <summary>
    /// Registers the property changed observable which can be retrieved using subject.GetPropertyChangedObservable().
    /// </summary>
    /// <param name="context">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorSubjectContext WithPropertyChangedObservable(this IInterceptorSubjectContext context)
    {
        return context
            .WithInterceptor(() => new PropertyChangedObservable());
    }

    /// <summary>
    /// Adds automatic context assignment and <see cref="WithLifecycle"/>.
    /// </summary>
    /// <param name="context">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorSubjectContext WithContextInheritance(this IInterceptorSubjectContext context)
    {
        context
            .TryAddService(() => new ContextInheritanceHandler(), _ => true);

        return context
            .WithLifecycle();
    }

    /// <summary>
    /// Adds support for <see cref="ILifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="context">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorSubjectContext WithLifecycle(this IInterceptorSubjectContext context)
    {
        return context
            .WithInterceptor(() => new LifecycleInterceptor(context));
    }
    
    /// <summary>
    /// Automatically assigns the parents to the interceptable data.
    /// </summary>
    /// <param name="context">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorSubjectContext WithParents(this IInterceptorSubjectContext context)
    {
        return context
            .WithInterceptor(() => new ParentTrackingHandler())
            .WithLifecycle();
    }
}