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
    /// listed twice in one collection counts two. Returns 0 for an unattached subject and for an
    /// anchored root that no edge points at, so this is not an ownership predicate: use
    /// <see cref="InterceptorSubjectExtensions.TryGetContext"/> for that.
    /// </summary>
    /// <exception cref="InvalidOperationException">The subject is attached to a context that has
    /// no <see cref="LifecycleInterceptor"/>, which cannot answer the question rather than
    /// answering it with zero.</exception>
    public static int GetReferenceCount(this IInterceptorSubject subject)
    {
        var context = subject.TryGetContext();
        if (context is null)
        {
            // Zero is the answer here rather than a stand-in for one: no edge can point at an
            // unattached subject, because an attached parent would have pulled it into the context.
            return 0;
        }

        return context.TryGetLifecycleInterceptor()?.GetReferenceCount(subject)
            ?? throw MissingLifecycle(subject, "reference counting");
    }

    private static InvalidOperationException MissingLifecycle(IInterceptorSubject subject, string feature)
    {
        return new InvalidOperationException(
            $"LifecycleInterceptor not configured for the context of '{subject.GetType().Name}'. " +
            $"Call WithLifecycle() on the context to enable {feature}.");
    }

    /// <summary>
    /// Runs the property attach callbacks for one property. The subject must be attached: every
    /// caller runs inside an attach descent or a property admission, where the attachment is
    /// already established.
    /// </summary>
    public static void AttachSubjectProperty(this IInterceptorSubject subject, PropertyReference property)
    {
        using var scope = CallbackReentrancyGuard.EnterPropertyCallbackScope();
        var change = new SubjectPropertyLifecycleChange(subject, property);

        foreach (var handler in subject.GetContext().GetServices<IPropertyLifecycleHandler>())
        {
            handler.AttachProperty(change);
        }

        if (subject is IPropertyLifecycleHandler lifecycleHandler)
        {
            lifecycleHandler.AttachProperty(change);
        }
    }

    /// <summary>
    /// Runs the property detach callbacks for one property. The subject must be attached: the
    /// release descent runs these before the claim is released.
    /// </summary>
    public static void DetachSubjectProperty(this IInterceptorSubject subject, PropertyReference property)
    {
        using var scope = CallbackReentrancyGuard.EnterPropertyCallbackScope();
        var change = new SubjectPropertyLifecycleChange(subject, property);

        foreach (var handler in subject.GetContext().GetServices<IPropertyLifecycleHandler>())
        {
            handler.DetachProperty(change);
        }

        if (subject is IPropertyLifecycleHandler lifecycleHandler)
        {
            lifecycleHandler.DetachProperty(change);
        }
    }
}
