using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Recorder;

namespace Namotion.Interceptor.Tracking;

public static class InterceptorSubjectContextExtensions
{
    /// <summary>
    /// Gets the property changed observable which is registered in the context.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The observable.</returns>
    public static IObservable<SubjectPropertyChange> GetPropertyChangedObservable(this IInterceptorSubjectContext context)
    {
        return context.GetService<PropertyChangedObservable>();
    }
    
    /// <summary>
    /// Registers full property tracking including equality checks, context inheritance, derived property change detection, and property changed observable.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The context.</returns>
    public static IInterceptorSubjectContext WithFullPropertyTracking(this IInterceptorSubjectContext context)
    {
        return context
            .WithEqualityCheck()
            .WithContextInheritance()
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangedObservable();
    }
    
    /// <summary>
    /// Registers an interceptor that checks if the new value is different from the current value and only calls inner interceptors when the property has changed.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The context.</returns>
    public static IInterceptorSubjectContext WithEqualityCheck(this IInterceptorSubjectContext context)
    {
        return context
            .WithInterceptor(() => new PropertyValueEqualityCheckHandler());
    }

    /// <summary>
    /// Registers the derived property change detection interceptor.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The context.</returns>
    public static IInterceptorSubjectContext WithDerivedPropertyChangeDetection(this IInterceptorSubjectContext context)
    {
        context
            .WithInterceptor(() => new DerivedPropertyChangeHandler())
            .TryAddService(context.GetService<DerivedPropertyChangeHandler>, _ => true);

        return context
            .WithLifecycle();
    }

    /// <summary>
    /// Registers the read property recorder used to record property read invocations.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The context.</returns>
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
    /// Adds support for <see cref="ISubjectLifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="context">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorSubjectContext WithLifecycle(this IInterceptorSubjectContext context)
    {
        return context
            .WithInterceptor(() => new LifecycleInterceptor());
    }
    
    /// <summary>
    /// Automatically assigns the parents to the interceptable data.
    /// </summary>
    /// <param name="context">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorSubjectContext WithParents(this IInterceptorSubjectContext context)
    {
        context
            .TryAddService(() => new ParentTrackingHandler(), _ => true);

        return context
            .WithLifecycle();
    }
}