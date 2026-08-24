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
    /// Gets the number of active incoming structural edge occurrences of the subject. A subject
    /// listed twice in one collection counts two. Returns 0 for an unattached subject, for an
    /// anchored root that no edge points at, and for a context using another lifecycle
    /// implementation, so this is not an ownership predicate: use
    /// <see cref="InterceptorSubjectExtensions.TryGetContext"/> for that.
    /// </summary>
    /// <exception cref="InvalidOperationException">The subject's context resolves more than one
    /// built-in lifecycle, which happens when two contexts that each configure Tracking are
    /// composed. A subject belongs to exactly one context's graph, so that configuration has no
    /// answer.</exception>
    public static int GetReferenceCount(this IInterceptorSubject subject)
    {
        return subject.TryGetContext()?.TryGetLifecycleInterceptor()?.GetReferenceCount(subject) ?? 0;
    }

    public static void AttachSubjectProperty(this IInterceptorSubject subject, PropertyReference property)
    {
        using var scope = CallbackReentrancyGuard.EnterPropertyCallbackScope();
        var change = new SubjectPropertyLifecycleChange(subject, property);

        foreach (var handler in subject.TryGetContext()?.GetServices<IPropertyLifecycleHandler>() ?? [])
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
        using var scope = CallbackReentrancyGuard.EnterPropertyCallbackScope();
        var change = new SubjectPropertyLifecycleChange(subject, property);

        foreach (var handler in subject.TryGetContext()?.GetServices<IPropertyLifecycleHandler>() ?? [])
        {
            handler.DetachProperty(change);
        }

        if (subject is IPropertyLifecycleHandler lifecycleHandler)
        {
            lifecycleHandler.DetachProperty(change);
        }
    }
}
