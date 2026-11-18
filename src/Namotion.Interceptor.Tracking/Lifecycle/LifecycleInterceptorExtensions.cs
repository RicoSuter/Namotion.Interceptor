namespace Namotion.Interceptor.Tracking.Lifecycle;

public static class LifecycleInterceptorExtensions
{
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
        return SubjectLifecycleAttachments.GetReferenceCount(subject);
    }

    /// <summary>
    /// Increments the reference count and returns the new value.
    /// </summary>
    internal static int IncrementReferenceCount(this IInterceptorSubject subject)
    {
        return SubjectLifecycleAttachments.IncrementReferenceCount(subject);
    }

    /// <summary>
    /// Decrements the reference count and returns the new value.
    /// </summary>
    internal static int DecrementReferenceCount(this IInterceptorSubject subject)
    {
        return SubjectLifecycleAttachments.DecrementReferenceCount(subject);
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