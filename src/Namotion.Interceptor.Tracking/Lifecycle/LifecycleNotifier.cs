using System.Diagnostics.CodeAnalysis;
using Namotion.Interceptor.Tracking.Change;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// The lifecycle's outbound notification surface: the two subject events and the ordered handler
/// fan-out, for one context.
/// </summary>
/// <remarks>
/// Graph descent runs inline. Other handlers and events are queued in their declared order and
/// delivered at the outer gate boundary. Nested writes update ownership before returning, while
/// their notifications join the queue. Callback failures do not roll back committed ownership.
/// </remarks>
internal sealed class LifecycleNotifier(IInterceptorSubjectContext context, OwnershipGraph graph, ILifecycleHandler descentHandler)
{
    private enum NotificationKind { Attached, Detaching, LifecycleHandler, Refresh, AttachProperty, DetachProperty, RecalculateDerived, ReleaseClaim }
    private readonly record struct Notification(NotificationKind Kind, SubjectLifecycleChange Change, object? Value = null);
    private readonly List<PropertyChangeInterceptor.Publication> _propertyChanges = [];
    private readonly List<Notification> _notifications = [];
    private bool _draining;

    // Handler lists resolved once per service snapshot instead of once per notification. A queued
    // notification asks the same question the one before it asked, and answering it goes through a
    // generic virtual dispatch and a dictionary probe, which a structural write pays once per
    // property of every subject it moves. Only the built-in context can be cached against, so a
    // foreign implementation keeps resolving per call. Like the queues themselves, these fields are
    // only ever touched under the topology gate.
    private readonly InterceptorSubjectContext? _cacheableContext = context as InterceptorSubjectContext;
    private object? _lifecycleHandlerSnapshot;
    private ImmutableArray<ILifecycleHandler> _lifecycleHandlers;
    private object? _propertyHandlerSnapshot;
    private ImmutableArray<IPropertyLifecycleHandler> _propertyHandlers;

    public event Action<SubjectLifecycleChange>? SubjectAttached;
    public event Action<SubjectLifecycleChange>? SubjectDetaching;

    public void RaiseSubjectAttached(SubjectLifecycleChange change) => _notifications.Add(new(NotificationKind.Attached, change));
    public void RaiseSubjectDetaching(SubjectLifecycleChange change) => _notifications.Add(new(NotificationKind.Detaching, change));

    public void InvokeAddedLifecycleHandlers(IInterceptorSubject subject, SubjectLifecycleChange change)
    {
        try
        {
            QueueLifecycleHandlers(change);
        }
        finally
        {
            QueueSubjectHandler(subject, in change);
        }
    }

    public void InvokeRemovedLifecycleHandlers(IInterceptorSubject subject, SubjectLifecycleChange change)
    {
        QueueSubjectHandler(subject, in change);
        QueueLifecycleHandlers(change);
    }

    // Out of line so the notification it builds does not force a wide, zero-initialized frame onto
    // the two methods above, which run for every subject entering or leaving the graph whether or
    // not the subject handles its own lifecycle.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void QueueSubjectHandler(IInterceptorSubject subject, in SubjectLifecycleChange change)
    {
        if (subject is ILifecycleHandler handler)
        {
            _notifications.Add(new(NotificationKind.LifecycleHandler, change, Value: handler));
        }
    }

    // Snapshot identity is an exact invalidation token: a registration publishes a new snapshot, so
    // a cached list is reused only while the answer cannot have changed. Reading the token before
    // resolving can pair an older token with a newer list, which costs one redundant resolve on the
    // next call and never returns a stale list.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ImmutableArray<ILifecycleHandler> GetLifecycleHandlers()
    {
        var cacheableContext = _cacheableContext;
        return cacheableContext is not null && ReferenceEquals(cacheableContext.ServiceSnapshot, _lifecycleHandlerSnapshot)
            ? _lifecycleHandlers
            : ResolveLifecycleHandlers();
    }

    /// <inheritdoc cref="GetLifecycleHandlers"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ImmutableArray<IPropertyLifecycleHandler> GetPropertyHandlers()
    {
        var cacheableContext = _cacheableContext;
        return cacheableContext is not null && ReferenceEquals(cacheableContext.ServiceSnapshot, _propertyHandlerSnapshot)
            ? _propertyHandlers
            : ResolvePropertyHandlers();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ImmutableArray<ILifecycleHandler> ResolveLifecycleHandlers()
    {
        if (_cacheableContext is null)
        {
            return context.GetServices<ILifecycleHandler>();
        }

        var snapshot = _cacheableContext.ServiceSnapshot;
        _lifecycleHandlers = _cacheableContext.GetServices<ILifecycleHandler>();
        _lifecycleHandlerSnapshot = snapshot;
        return _lifecycleHandlers;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ImmutableArray<IPropertyLifecycleHandler> ResolvePropertyHandlers()
    {
        if (_cacheableContext is null)
        {
            return context.GetServices<IPropertyLifecycleHandler>();
        }

        var snapshot = _cacheableContext.ServiceSnapshot;
        _propertyHandlers = _cacheableContext.GetServices<IPropertyLifecycleHandler>();
        _propertyHandlerSnapshot = snapshot;
        return _propertyHandlers;
    }

    private void QueueLifecycleHandlers(SubjectLifecycleChange change)
    {
        ExceptionDispatchInfo? failure = null;
        foreach (var handler in GetLifecycleHandlers())
        {
            if (ReferenceEquals(handler, descentHandler))
            {
                try { handler.HandleLifecycleChange(change); }
                catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
            }
            else QueueHandler(handler, in change);
        }

        failure?.Throw();
    }

    /// <inheritdoc cref="QueueSubjectHandler"/>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void QueueHandler(ILifecycleHandler handler, in SubjectLifecycleChange change)
    {
        _notifications.Add(new(NotificationKind.LifecycleHandler, change, Value: handler));
    }

    public void QueuePropertyChange(in PropertyChangeInterceptor.Publication publication)
    {
        _propertyChanges.Add(publication);
    }
    public void RefreshCollectionProperty(PropertyReference property, object? value) => _notifications.Add(new(NotificationKind.Refresh, new SubjectLifecycleChange { Subject = property.Subject, Property = property, ReferenceCount = 0 }, value));
    public void QueueProperty(PropertyReference property, bool attach) => _notifications.Add(new(attach ? NotificationKind.AttachProperty : NotificationKind.DetachProperty, new SubjectLifecycleChange { Subject = property.Subject, Property = property, ReferenceCount = 0 }));
    public void QueueDerivedRecalculation(IInterceptorSubject subject) => _notifications.Add(new(NotificationKind.RecalculateDerived, new SubjectLifecycleChange { Subject = subject, ReferenceCount = 0 }));
    public void QueueRelease(IInterceptorSubject subject, SubjectOwnership ownership) => _notifications.Add(new(NotificationKind.ReleaseClaim, new SubjectLifecycleChange { Subject = subject, ReferenceCount = 0 }, Value: ownership));

    public void PublishEdgeRemoved(IInterceptorSubject subject, PropertyReference property, object? index, int referenceCount)
    {
        InvokeRemovedLifecycleHandlers(subject, new SubjectLifecycleChange
        {
            Subject = subject, Property = property, Index = index,
            ReferenceCount = referenceCount, IsPropertyReferenceRemoved = true
        });
    }

    public void Drain(Exception? operationFailure = null)
    {
        if (_draining || (_notifications.Count == 0 && _propertyChanges.Count == 0))
        {
            return;
        }

        _draining = true;
        List<Exception>? failures = null;
        try
        {
            DrainQueues(ref failures);
        }
        finally
        {
            _notifications.Clear();
            _propertyChanges.Clear();
            _draining = false;
        }

        if (failures is not null)
        {
            ThrowFailures(failures, operationFailure);
        }
    }

    private void DrainQueues(ref List<Exception>? failures)
    {
        var notificationIndex = 0;
        var propertyChangeIndex = 0;
        while (notificationIndex < _notifications.Count || propertyChangeIndex < _propertyChanges.Count)
        {
            // A self-writing seed queues its property change before discovering that value's
            // lifecycle transitions. Drain all pending maintenance before each observer group.
            if (notificationIndex == _notifications.Count)
            {
                try { _propertyChanges[propertyChangeIndex++].Dispatch(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
                continue;
            }

            Dispatch(_notifications[notificationIndex++], ref failures);
        }
    }

    private void Dispatch(in Notification notification, ref List<Exception>? failures)
    {
        switch (notification.Kind)
        {
            case NotificationKind.Attached:
                InvokeSubjectCallbacks(SubjectAttached, notification.Change, ref failures);
                break;
            case NotificationKind.Detaching:
                InvokeSubjectCallbacks(SubjectDetaching, notification.Change, ref failures);
                break;
            case NotificationKind.LifecycleHandler:
                try { ((ILifecycleHandler)notification.Value!).HandleLifecycleChange(notification.Change); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
                break;
            case NotificationKind.Refresh:
            case NotificationKind.AttachProperty:
            case NotificationKind.DetachProperty:
                InvokePropertyHandlers(in notification, ref failures);
                break;
            case NotificationKind.RecalculateDerived:
                RecalculateDerivedProperties(notification.Change.Subject, ref failures);
                break;
            case NotificationKind.ReleaseClaim:
                CompleteRelease(in notification);
                break;
        }
    }

    // Each subscriber is invoked on its own, so one failing subscriber does not keep the others
    // from seeing the change.
    private static void InvokeSubjectCallbacks(Action<SubjectLifecycleChange>? callbacks, SubjectLifecycleChange change, ref List<Exception>? failures)
    {
        if (callbacks is null)
        {
            return;
        }

        foreach (var callback in Delegate.EnumerateInvocationList(callbacks))
        {
            try { callback(change); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
    }

    private void InvokePropertyHandlers(in Notification notification, ref List<Exception>? failures)
    {
        foreach (var handler in GetPropertyHandlers())
        {
            InvokePropertyHandler(handler, in notification, ref failures);
        }

        if (notification.Kind != NotificationKind.Refresh && notification.Change.Subject is IPropertyLifecycleHandler subjectHandler)
        {
            InvokePropertyHandler(subjectHandler, in notification, ref failures);
        }
    }

    [DoesNotReturn]
    private static void ThrowFailures(List<Exception> failures, Exception? operationFailure)
    {
        if (operationFailure is not null)
        {
            failures.Insert(0, operationFailure);
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(failures);
    }

    // Out of line so the graph bookkeeping, which runs at most once per released subject, keeps its
    // dictionary probes out of the drain loop's instruction stream.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CompleteRelease(in Notification notification)
    {
        var subject = notification.Change.Subject;
        var ownership = (SubjectOwnership)notification.Value!;
        if (graph.IsCurrentRelease(subject, ownership))
        {
            graph.ReleaseClaim(subject);
            graph.ClearReleasing(subject, ownership);
        }
    }

    // Out of line for the same reason as CompleteRelease: it runs at most once per subject whose
    // provisional anchor an edge consumed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RecalculateDerivedProperties(IInterceptorSubject subject, ref List<Exception>? failures)
    {
        foreach (var entry in subject.Properties)
        {
            if (!entry.Value.IsDerived)
            {
                continue;
            }

            try { DerivedPropertyChangeHandler.RecalculateDerivedPropertyIfChanged(new PropertyReference(subject, entry.Key)); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
    }

    private static void InvokePropertyHandler(IPropertyLifecycleHandler handler, in Notification notification, ref List<Exception>? failures)
    {
        try
        {
            var property = notification.Change.Property.GetValueOrDefault();
            var change = new SubjectPropertyLifecycleChange(notification.Change.Subject, property);
            switch (notification.Kind)
            {
                case NotificationKind.AttachProperty: handler.AttachProperty(change); break;
                case NotificationKind.DetachProperty: handler.DetachProperty(change); break;
                default: handler.RefreshCollectionProperty(property, notification.Value); break;
            }
        }
        catch (Exception exception) { (failures ??= []).Add(exception); }
    }
}
