using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Recorder;

namespace Namotion.Interceptor.Tracking;

public static class InterceptorSubjectContextExtensions
{
    // Static dictionary to store context-specific data since the interface doesn't have a Data property
    private static readonly ConcurrentDictionary<IInterceptorSubjectContext, ConcurrentDictionary<string, object>> _contextData = new();

    /// <summary>
    /// Gets or adds data to the context.
    /// </summary>
    public static TValue GetOrAddData<TValue>(this IInterceptorSubjectContext context, string key, Func<TValue> valueFactory) where TValue : class
    {
        var data = _contextData.GetOrAdd(context, _ => new ConcurrentDictionary<string, object>());
        return (TValue)data.GetOrAdd(key, _ => valueFactory()!);
    }

    /// <summary>
    /// Tries to get data from the context.
    /// </summary>
    public static bool TryGetData<TValue>(this IInterceptorSubjectContext context, string key, out TValue? value) where TValue : class
    {
        if (_contextData.TryGetValue(context, out var data) && data.TryGetValue(key, out var obj))
        {
            value = (TValue)obj;
            return true;
        }

        value = default;
        return false;
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
            .WithDerivedPropertyChangeDetection()
            .WithPropertyChangeObservable()
            .WithPropertyChangeQueue()
            .WithContextInheritance();
    }
    
    /// <summary>
    /// Registers an interceptor that checks if the new value is different from the current value and only calls inner interceptors when the property has changed.
    /// Uses EqualityComparer.Default for value types or strings and does nothing for reference types.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The context.</returns>
    public static IInterceptorSubjectContext WithEqualityCheck(this IInterceptorSubjectContext context)
    {
        return context
            .WithService(() => new PropertyValueEqualityCheckHandler());
    }

    /// <summary>
    /// Registers the derived property change detection interceptor.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <returns>The context.</returns>
    public static IInterceptorSubjectContext WithDerivedPropertyChangeDetection(this IInterceptorSubjectContext context)
    {
        context // must be before lifecycle!
            .WithService(() => new DerivedPropertyChangeHandler());

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
            .WithService(() => new ReadPropertyRecorder());
    }

    /// <summary>
    /// Registers the property changed observable which can be retrieved using subject.GetPropertyChangeObservable().
    /// </summary>
    /// <param name="context">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorSubjectContext WithPropertyChangeObservable(this IInterceptorSubjectContext context)
    {
        return context
            .WithService(() => new PropertyChangeObservable());
    }

    /// <summary>
    /// Gets the property changed observable which is registered in the context.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="scheduler">The scheduler to run the callbacks on (defaults to Scheduler.Default).
    /// Use ImmediateScheduler.Instance for zero-allocation synchronous delivery.</param>
    /// <returns>The observable.</returns>
    public static IObservable<SubjectPropertyChange> GetPropertyChangeObservable(this IInterceptorSubjectContext context, IScheduler? scheduler = null)
    {
        var observable = context
            .GetService<PropertyChangeObservable>()
            .Publish()
            .RefCount(); // single upstream subscription (shared)

        if (scheduler == ImmediateScheduler.Instance)
        {
            // Skip ObserveOn for ImmediateScheduler - it's synchronous and ObserveOn adds allocation overhead
            return observable;
        }

        return observable.ObserveOn(scheduler ?? Scheduler.Default);
    }

    /// <summary>
    /// Registers the property changed observable which can be retrieved using subject.GetPropertyChangeObservable().
    /// </summary>
    /// <param name="context">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorSubjectContext WithPropertyChangeQueue(this IInterceptorSubjectContext context)
    {
        return context
            .WithService(() => new PropertyChangeQueue());
    }

    /// <summary>
    /// Gets the property changed observable which is registered in the context.
    /// </summary>
    /// <param name="context">The context.</param>
    /// <param name="scheduler">The scheduler to run the callbacks on (defaults to Scheduler.Default).</param>
    /// <returns>The observable.</returns>
    public static PropertyChangeQueueSubscription CreatePropertyChangeQueueSubscription(this IInterceptorSubjectContext context, IScheduler? scheduler = null)
    {
        return context
            .GetService<PropertyChangeQueue>()
            .Subscribe();
    }

    /// <summary>
    /// Adds automatic context assignment and <see cref="WithLifecycle"/>.
    /// </summary>
    /// <param name="context">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorSubjectContext WithContextInheritance(this IInterceptorSubjectContext context)
    {
        context
            .WithLifecycle()
            .WithService(() => new ContextInheritanceHandler());

        return context;
    }

    /// <summary>
    /// Adds support for <see cref="ILifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="context">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorSubjectContext WithLifecycle(this IInterceptorSubjectContext context)
    {
        return context
            .WithService(() => new LifecycleInterceptor());
    }
    
    /// <summary>
    /// Automatically assigns the parents to the interceptable data.
    /// </summary>
    /// <param name="context">The collection.</param>
    /// <returns>The collection.</returns>
    public static IInterceptorSubjectContext WithParents(this IInterceptorSubjectContext context)
    {
        context
            .WithService(() => new ParentTrackingHandler());

        return context
            .WithLifecycle();
    }
}