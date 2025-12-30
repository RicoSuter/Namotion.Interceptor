using System.Runtime.CompilerServices;
using Namotion.Interceptor.Performance;

namespace Namotion.Interceptor.Tracking.Lifecycle;

public static class LifecycleInterceptorExtensions
{
    // Must match LifecycleInterceptor.ReferenceCountKey (private implementation detail)
    private const string ReferenceCountKey = "Namotion.Interceptor.Tracking.ReferenceCount";
    private const string AttachedPropertiesKey = "Namotion.Interceptor.Tracking.AttachedProperties";

    // Pool for ReferenceCounter objects to avoid boxing allocations
    private static readonly ObjectPool<ReferenceCounter> CounterPool = new(() => new ReferenceCounter());

    /// <summary>
    /// Pooled wrapper for reference count to avoid boxing int.
    /// </summary>
    private sealed class ReferenceCounter
    {
        public int Value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset() => Value = 0;
    }

    /// <summary>
    /// Gets the lifecycle interceptor from the context, if configured.
    /// </summary>
    public static LifecycleInterceptor? TryGetLifecycleInterceptor(this IInterceptorSubjectContext context)
    {
        return context.TryGetService<LifecycleInterceptor>();
    }

    /// <summary>
    /// Gets the current reference count (number of parent references) for the subject.
    /// Returns 0 if subject is not attached or lifecycle tracking is not enabled.
    /// </summary>
    public static int GetReferenceCount(this IInterceptorSubject subject)
    {
        return subject.Data.TryGetValue((null, ReferenceCountKey), out var value)
            && value is ReferenceCounter counter
            ? counter.Value
            : 0;
    }

    /// <summary>
    /// Increments the reference count and returns the new value.
    /// </summary>
    internal static int IncrementReferenceCount(this IInterceptorSubject subject)
    {
        var counter = (ReferenceCounter)subject.Data.GetOrAdd(
            (null, ReferenceCountKey),
            static _ => CounterPool.Rent())!;
        return Interlocked.Increment(ref counter.Value);
    }

    /// <summary>
    /// Decrements the reference count and returns the new value.
    /// </summary>
    internal static int DecrementReferenceCount(this IInterceptorSubject subject)
    {
        if (subject.Data.TryGetValue((null, ReferenceCountKey), out var value)
            && value is ReferenceCounter counter)
        {
            var newValue = Interlocked.Decrement(ref counter.Value);
            return Math.Max(0, newValue);
        }
        return 0;
    }

    /// <summary>
    /// Returns the pooled reference counter to the pool.
    /// Called when a subject is fully detached (when OnSubjectDetached fires).
    /// </summary>
    internal static void ReturnReferenceCounter(this IInterceptorSubject subject)
    {
        if (subject.Data.TryRemove((null, ReferenceCountKey), out var value) && value is ReferenceCounter counter)
        {
            counter.Reset();
            CounterPool.Return(counter);
        }
    }

    /// <summary>
    /// Attaches a property to the subject's lifecycle tracking.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="property">The property reference to attach.</param>
    /// <remarks>
    /// This method is not thread-safe by itself. It must be called within
    /// the LifecycleInterceptor's lock to ensure thread safety.
    /// </remarks>
    public static void AttachSubjectProperty(this IInterceptorSubject subject, PropertyReference property)
    {
        // Check if this property is already attached (idempotent)
        var attachedProperties = GetOrCreateAttachedPropertiesSet(subject);
        if (!attachedProperties.Add(property.Name))
        {
            return; // Already attached
        }

        var change = new SubjectPropertyLifecycleChange(subject, property);

        foreach (var handler in subject.Context.GetServices<IPropertyLifecycleHandler>())
        {
            handler.OnPropertyAttached(change);
        }

        if (subject is IPropertyLifecycleHandler lifecycleHandler)
        {
            lifecycleHandler.OnPropertyAttached(change);
        }
    }

    private static HashSet<string> GetOrCreateAttachedPropertiesSet(IInterceptorSubject subject)
    {
        var key = (null as string, AttachedPropertiesKey);
        return (HashSet<string>)subject.Data.GetOrAdd(key, static _ => new HashSet<string>())!;
    }
    
    /// <summary>
    /// Detaches a property from the subject's lifecycle tracking.
    /// </summary>
    /// <param name="subject">The subject.</param>
    /// <param name="property">The property reference to detach.</param>
    /// <remarks>
    /// This method is not thread-safe by itself. It must be called within
    /// the LifecycleInterceptor's lock to ensure thread safety.
    /// </remarks>
    public static void DetachSubjectProperty(this IInterceptorSubject subject, PropertyReference property)
    {
        // Remove from attached properties set
        var key = (null as string, AttachedPropertiesKey);
        if (subject.Data.TryGetValue(key, out var existing) && existing is HashSet<string> attachedSet)
        {
            attachedSet.Remove(property.Name);
        }

        var change = new SubjectPropertyLifecycleChange(subject, property);

        foreach (var handler in subject.Context.GetServices<IPropertyLifecycleHandler>())
        {
            handler.OnPropertyDetached(change);
        }

        if (subject is IPropertyLifecycleHandler lifecycleHandler)
        {
            lifecycleHandler.OnPropertyDetached(change);
        }
    }
}