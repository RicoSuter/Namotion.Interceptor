namespace Namotion.Interceptor.Tracking.Lifecycle;

public static class LifecycleInterceptorExtensions
{
    // Must match LifecycleInterceptor.ReferenceCountKey (private implementation detail)
    private const string ReferenceCountKey = "Namotion.Interceptor.Tracking.ReferenceCount";
    private const string AttachedPropertiesKey = "Namotion.Interceptor.Tracking.AttachedProperties";

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
        if (subject.Data.TryGetValue((null, ReferenceCountKey), out var count))
        {
            return (int)(count ?? 0);
        }
        return 0;
    }

    /// <summary>
    /// Increments the reference count and returns the new value.
    /// </summary>
    internal static int IncrementReferenceCount(this IInterceptorSubject subject)
    {
        return (int)(subject.Data.AddOrUpdate((null, ReferenceCountKey), 1, (_, count) => (int)(count ?? 0) + 1) ?? 1);
    }

    /// <summary>
    /// Decrements the reference count and returns the new value.
    /// </summary>
    internal static int DecrementReferenceCount(this IInterceptorSubject subject)
    {
        return (int)(subject.Data.AddOrUpdate((null, ReferenceCountKey), 0, (_, count) => Math.Max(0, (int)(count ?? 0) - 1)) ?? 0);
    }

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
            handler.AttachProperty(change);
        }

        if (subject is IPropertyLifecycleHandler lifecycleHandler)
        {
            lifecycleHandler.AttachProperty(change);
        }
    }

    private static HashSet<string> GetOrCreateAttachedPropertiesSet(IInterceptorSubject subject)
    {
        var key = (null as string, AttachedPropertiesKey);
        if (subject.Data.TryGetValue(key, out var existing) && existing is HashSet<string> set)
        {
            return set;
        }

        var newSet = new HashSet<string>();
        subject.Data.TryAdd(key, newSet);
        return (HashSet<string>)subject.Data.GetOrAdd(key, newSet)!;
    }
    
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
            handler.DetachProperty(change);
        }

        if (subject is IPropertyLifecycleHandler lifecycleHandler)
        {
            lifecycleHandler.DetachProperty(change);
        }
    }
}