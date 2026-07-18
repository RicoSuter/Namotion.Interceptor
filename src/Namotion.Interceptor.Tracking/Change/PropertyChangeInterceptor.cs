using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Single write interceptor that delivers property changes through two facets: an Rx observable
/// (see <see cref="InterceptorSubjectContextExtensions.GetPropertyChangeObservable"/>) and a
/// high-performance pull queue (see <see cref="InterceptorSubjectContextExtensions.CreatePropertyChangeQueueSubscription"/>).
/// Replaces the former PropertyChangeObservable and PropertyChangeQueue.
/// </summary>
[RunsAfter(typeof(SubjectTransactionInterceptor))]
public sealed class PropertyChangeInterceptor : IObservable<SubjectPropertyChange>, IWriteInterceptor, IDisposable
{
    private readonly Lock _modificationLock = new();

    // The single hot-path field. Null means neither facet has consumers (idle).
    private volatile DispatchState? _state;

    // Observable facet state, guarded by _modificationLock. Lazily created on first subscribe.
    private Subject<SubjectPropertyChange>? _subject;
    private ISubject<SubjectPropertyChange>? _syncSubject;
    private int _observableConsumerCount;

    private bool _disposed;

    // White-box test hook: true when neither facet has consumers (the idle fast path).
    // Used by the gate-closure and idle tests instead of a no-op "must not throw" assertion.
    internal bool IsIdle => _state is null;

    // Only surface the sync subject while at least one observable consumer is live; the field is
    // never cleared, so publishing it unconditionally would resurrect an observer-less subject and
    // permanently defeat the idle gate after transient observable use followed by queue churn.
    private ISubject<SubjectPropertyChange>? ActiveSyncSubject => _observableConsumerCount > 0 ? _syncSubject : null;

    private sealed class DispatchState
    {
        public required PropertyChangeQueueSubscription[] QueueSubscriptions { get; init; } // never null
        public required ISubject<SubjectPropertyChange>? SyncSubject { get; init; }          // null = no observers
    }

    // Called under _modificationLock. Publishes null when both facets are empty.
    private void PublishState(PropertyChangeQueueSubscription[] queueSubscriptions, ISubject<SubjectPropertyChange>? syncSubject)
    {
        _state = queueSubscriptions.Length == 0 && syncSubject is null
            ? null
            : new DispatchState { QueueSubscriptions = queueSubscriptions, SyncSubject = syncSubject };
    }

    internal PropertyChangeQueueSubscription CreateQueueSubscription()
    {
        lock (_modificationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var subscription = new PropertyChangeQueueSubscription(this);
            var current = _state?.QueueSubscriptions ?? [];
            var updated = new PropertyChangeQueueSubscription[current.Length + 1];
            Array.Copy(current, updated, current.Length);
            updated[current.Length] = subscription;
            PublishState(updated, ActiveSyncSubject);
            return subscription;
        }
    }

    internal void RemoveQueueSubscription(PropertyChangeQueueSubscription subscription)
    {
        lock (_modificationLock)
        {
            var current = _state?.QueueSubscriptions ?? [];
            var index = Array.IndexOf(current, subscription);
            if (index < 0)
            {
                return;
            }

            var updated = new PropertyChangeQueueSubscription[current.Length - 1];
            Array.Copy(current, 0, updated, 0, index);
            Array.Copy(current, index + 1, updated, index, current.Length - index - 1);
            PublishState(updated, ActiveSyncSubject);
        }
    }

    public IDisposable Subscribe(IObserver<SubjectPropertyChange> observer)
    {
        ISubject<SubjectPropertyChange> syncSubject;
        lock (_modificationLock)
        {
            // Intentionally NOT gated on _disposed. Interceptor disposal tears down the queue facet
            // only; the observable facet is unaffected (spec: existing observers keep receiving and
            // new observers may still subscribe after Dispose). Only CreateQueueSubscription throws.

            if (_subject is null)
            {
                _subject = new Subject<SubjectPropertyChange>();
                _syncSubject = Subject.Synchronize(_subject);
            }

            _observableConsumerCount++;
            syncSubject = _syncSubject!;
            PublishState(_state?.QueueSubscriptions ?? [], ActiveSyncSubject);
        }

        // Benign race: a concurrent write may OnNext into syncSubject before this observer joins here; no observer expects changes from before its Subscribe returns.
        var inner = syncSubject.Subscribe(observer);
        return new ObservableSubscription(this, inner);
    }

    private void RemoveObservableConsumer()
    {
        lock (_modificationLock)
        {
            if (_observableConsumerCount > 0 && --_observableConsumerCount == 0)
            {
                PublishState(_state?.QueueSubscriptions ?? [], null);
            }
        }
    }

    private sealed class ObservableSubscription(PropertyChangeInterceptor interceptor, IDisposable inner) : IDisposable
    {
        private PropertyChangeInterceptor? _interceptor = interceptor;
        private readonly IDisposable _inner = inner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _interceptor, null) is not { } owner)
            {
                return; // one-shot
            }

            _inner.Dispose();
            owner.RemoveObservableConsumer();
        }
    }

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        // Pre-commit gate decides only whether the old value must be captured; listener
        // RESOLUTION is post-commit, so an install racing this write is never missed
        // (spec: Post-commit listener resolution / the Dekker pair).
        var state = _state;
        var mayHaveListeners = PropertyChangeSubscriptions.ReadSubscriptionCount() != 0;
        if (state is null && !mayHaveListeners)
        {
            next(ref context);
            DispatchLateListeners(ref context);
            return;
        }

        var subscriptions = state?.QueueSubscriptions ?? [];
        var syncSubject = state?.SyncSubject;

        var oldValue = context.CurrentValue;
        next(ref context);

        // Post-commit listener resolution: full fence, count re-read, then the Data lookup.
        // Published during unwind; the innermost aggregated instance resolves first, outer
        // instances observe the publish (same thread, by-ref context) and skip.
        PropertyChangeSubscription[]? listeners = null;
        Interlocked.MemoryBarrier();
        if (PropertyChangeSubscriptions.ReadSubscriptionCount() != 0 && !context.ArePropertyListenersPublished)
        {
            listeners = TryGetListeners(context.Property);
            if (listeners is not null)
            {
                context.ArePropertyListenersPublished = true;
            }
        }

        if (syncSubject is null && subscriptions.Length == 0 && listeners is null)
        {
            return;
        }

        var change = SubjectPropertyChange.Create(
            context.Property,
            context.Origin,
            context.WriteTimestampForPublishing,
            SubjectChangeContext.Current.ReceivedTimestamp,
            oldValue,
            context.GetFinalValue());

        for (var i = 0; i < subscriptions.Length; i++)
        {
            subscriptions[i].Enqueue(in change);
        }

        if (syncSubject is not null)
        {
            syncSubject.OnNext(change);
        }

        if (listeners is not null)
        {
            PropertyChangeSubscription.Dispatch(listeners, in change);
        }
    }

    /// <summary>
    /// Idle-entry path: the pre-commit gate saw no consumers, so no old value was captured. A
    /// listener installed while the write was in flight is still delivered, with the final value
    /// as both old and new (documented caveat). Non-inlined so the idle fast path stays small.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DispatchLateListeners<TProperty>(ref PropertyWriteContext<TProperty> context)
    {
        Interlocked.MemoryBarrier();
        if (PropertyChangeSubscriptions.ReadSubscriptionCount() == 0 || context.ArePropertyListenersPublished)
        {
            return;
        }

        var listeners = TryGetListeners(context.Property);
        if (listeners is null)
        {
            return;
        }

        context.ArePropertyListenersPublished = true;

        var finalValue = context.GetFinalValue();
        var change = SubjectPropertyChange.Create(
            context.Property,
            context.Origin,
            context.WriteTimestampForPublishing,
            SubjectChangeContext.Current.ReceivedTimestamp,
            finalValue,
            finalValue);

        PropertyChangeSubscription.Dispatch(listeners, in change);
    }

    private static PropertyChangeSubscription[]? TryGetListeners(PropertyReference property)
    {
        return property.Subject.Data.TryGetValue((property.Name, PropertyChangeSubscription.ListenersKey), out var value)
            ? value as PropertyChangeSubscription[]
            : null;
    }

    public void Dispose()
    {
        PropertyChangeQueueSubscription[] toComplete;
        lock (_modificationLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            toComplete = _state?.QueueSubscriptions ?? [];
            PublishState([], ActiveSyncSubject); // preserve observable facet, drop queue
        }

        foreach (var subscription in toComplete)
        {
            subscription.Complete();
        }
    }
}
