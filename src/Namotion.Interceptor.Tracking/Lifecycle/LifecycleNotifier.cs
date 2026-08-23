namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// The lifecycle's outbound notification surface: the two subject events and the ordered handler
/// fan-out, for one context.
/// </summary>
/// <remarks>
/// Separated from <see cref="LifecycleInterceptor"/> so the classes that publish transitions depend
/// on what they publish through rather than on the interceptor as a whole. Handing them the
/// interceptor would make the dependency circular and would put the write protocol and both attach
/// entry points within reach of code running while the topology lock is held; what those classes are
/// allowed to do is then a property of a constructor signature rather than of three class bodies.
///
/// Every publication marks the thread through <see cref="CallbackReentrancyGuard.EnterScope"/>,
/// which is what lets the structural write protocol reject a callback that writes a structural
/// property. The guard is live in every build, so the exception regions the scopes produce are
/// deliberate, not leftovers.
/// </remarks>
internal sealed class LifecycleNotifier(IInterceptorSubjectContext context)
{
    public event Action<SubjectLifecycleChange>? SubjectAttached;

    public event Action<SubjectLifecycleChange>? SubjectDetaching;

    public void RaiseSubjectAttached(SubjectLifecycleChange change)
    {
        using var scope = CallbackReentrancyGuard.EnterScope();
        SubjectAttached?.Invoke(change);
    }

    public void RaiseSubjectDetaching(SubjectLifecycleChange change)
    {
        using var scope = CallbackReentrancyGuard.EnterScope();
        SubjectDetaching?.Invoke(change);
    }

    /// <summary>Publishes an edge removal that did not release the subject.</summary>
    public void PublishEdgeRemoved(IInterceptorSubject subject, PropertyReference property, object? index, int referenceCount)
    {
        InvokeRemovedLifecycleHandlers(subject, new SubjectLifecycleChange
        {
            Subject = subject,
            Property = property,
            Index = index,
            ReferenceCount = referenceCount,
            IsPropertyReferenceRemoved = true
        });
    }

    public void InvokeAddedLifecycleHandlers(IInterceptorSubject subject, SubjectLifecycleChange change)
    {
        using var scope = CallbackReentrancyGuard.EnterScope();
        var handlers = context.GetServices<ILifecycleHandler>();
        for (var index = 0; index < handlers.Length; index++)
        {
            handlers[index].HandleLifecycleChange(change);
        }

        if (subject is ILifecycleHandler subjectHandler)
        {
            subjectHandler.HandleLifecycleChange(change);
        }
    }

    public void InvokeRemovedLifecycleHandlers(IInterceptorSubject subject, SubjectLifecycleChange change)
    {
        using var scope = CallbackReentrancyGuard.EnterScope();
        if (subject is ILifecycleHandler subjectHandler)
        {
            subjectHandler.HandleLifecycleChange(change);
        }

        var handlers = context.GetServices<ILifecycleHandler>();
        for (var index = 0; index < handlers.Length; index++)
        {
            handlers[index].HandleLifecycleChange(change);
        }
    }

    public void RefreshCollectionProperty(PropertyReference property, object? value)
    {
        using var scope = CallbackReentrancyGuard.EnterScope();
        var handlers = context.GetServices<IPropertyLifecycleHandler>();
        for (var index = 0; index < handlers.Length; index++)
        {
            handlers[index].RefreshCollectionProperty(property, value);
        }
    }
}
