namespace Namotion.Interceptor.Tracking.Lifecycle;

public static class LifecycleInterceptorExtensions
{
    // Must match LifecycleInterceptor.ReferenceCountKey (private implementation detail)
    private const string ReferenceCountKey = "Namotion.Interceptor.Tracking.ReferenceCount";

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
    
    public static void DetachSubjectProperty(this IInterceptorSubject subject, PropertyReference property)
    {            
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