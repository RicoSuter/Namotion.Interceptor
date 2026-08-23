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
/// Every publication marks the thread through <see cref="CallbackReentrancyGuard"/>, which is what
/// lets the structural write protocol detect a callback that writes a structural property. The
/// guard calls compile out in Release, and the JIT removes the then-empty finally blocks.
/// </remarks>
internal sealed class LifecycleNotifier(IInterceptorSubjectContext context)
{
    public event Action<SubjectLifecycleChange>? SubjectAttached;

    public event Action<SubjectLifecycleChange>? SubjectDetaching;

    public void RaiseSubjectAttached(SubjectLifecycleChange change)
    {
        CallbackReentrancyGuard.EnterCallback();
        try
        {
            SubjectAttached?.Invoke(change);
        }
        finally
        {
            CallbackReentrancyGuard.ExitCallback();
        }
    }

    public void RaiseSubjectDetaching(SubjectLifecycleChange change)
    {
        CallbackReentrancyGuard.EnterCallback();
        try
        {
            SubjectDetaching?.Invoke(change);
        }
        finally
        {
            CallbackReentrancyGuard.ExitCallback();
        }
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
        CallbackReentrancyGuard.EnterCallback();
        try
        {
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
        finally
        {
            CallbackReentrancyGuard.ExitCallback();
        }
    }

    public void InvokeRemovedLifecycleHandlers(IInterceptorSubject subject, SubjectLifecycleChange change)
    {
        CallbackReentrancyGuard.EnterCallback();
        try
        {
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
        finally
        {
            CallbackReentrancyGuard.ExitCallback();
        }
    }

    public void RefreshCollectionProperty(PropertyReference property, object? value)
    {
        CallbackReentrancyGuard.EnterCallback();
        try
        {
            var handlers = context.GetServices<IPropertyLifecycleHandler>();
            for (var index = 0; index < handlers.Length; index++)
            {
                handlers[index].RefreshCollectionProperty(property, value);
            }
        }
        finally
        {
            CallbackReentrancyGuard.ExitCallback();
        }
    }
}
