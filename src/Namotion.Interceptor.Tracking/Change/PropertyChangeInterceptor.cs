using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Single write interceptor that delivers property changes through two channels: an Rx observable
/// (see <see cref="InterceptorSubjectContextExtensions.GetPropertyChangeObservable"/>) and a
/// high-performance pull queue (see <see cref="InterceptorSubjectContextExtensions.CreatePropertyChangeQueueSubscription"/>).
/// Replaces the former PropertyChangeObservable and PropertyChangeQueue.
/// </summary>
[RunsAfter(typeof(SubjectTransactionInterceptor))]
public sealed class PropertyChangeInterceptor : IObservable<SubjectPropertyChange>, IWriteInterceptor, IDisposable
{
    private readonly Lock _modificationLock = new();

    // The single hot-path field. Null means neither channel has consumers (idle).
    private volatile DispatchState? _state;

    // Observable channel state, guarded by _modificationLock. Lazily created on first subscribe.
    private Subject<SubjectPropertyChange>? _subject;
    private ISubject<SubjectPropertyChange>? _syncSubject;
    private int _observableConsumerCount;

    private bool _disposed;

    // White-box test hook: true when neither channel has consumers. The idle write fast path
    // additionally requires the process-wide subscription count to be zero.
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

    // Called under _modificationLock. Publishes null when both channels are empty.
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
            PublishState(CopyOnWriteArray.Add(current, subscription), ActiveSyncSubject);
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

            PublishState(CopyOnWriteArray.RemoveAt(current, index), ActiveSyncSubject);
        }
    }

    public IDisposable Subscribe(IObserver<SubjectPropertyChange> observer)
    {
        // Validate before publishing state: Rx would throw AFTER the consumer count was
        // published, with no handle to dispose, leaving the gate permanently open.
        ArgumentNullException.ThrowIfNull(observer);

        ISubject<SubjectPropertyChange> syncSubject;
        lock (_modificationLock)
        {
            // Intentionally NOT gated on _disposed. Interceptor disposal tears down the queue channel
            // only; the observable channel is unaffected (contract: existing observers keep receiving
            // and new observers may still subscribe after Dispose). Only CreateQueueSubscription throws.

            if (_subject is null)
            {
                _subject = new Subject<SubjectPropertyChange>();
                _syncSubject = Subject.Synchronize(_subject);
            }

            _observableConsumerCount++;
            syncSubject = _syncSubject!;
            if (_observableConsumerCount == 1)
            {
                // Only the 0 to 1 transition changes the state; further consumers share the
                // already-published sync subject (mirrors RemoveObservableConsumer's 1 to 0).
                PublishState(_state?.QueueSubscriptions ?? [], ActiveSyncSubject);
            }
        }

        // Joining outside the lock: a write may OnNext before this observer joins (treated as
        // committed before install). A write already past the idle gate may be missed entirely
        // when this is the first consumer (documented channel caveat).
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
        // RESOLUTION is post-commit (see ResolveListeners), so an install racing this
        // write is never missed.
        if (_state is null && PropertyChangeSubscriptions.ReadSubscriptionCount() == 0)
        {
            next(ref context);
            DispatchLateListeners(ref context);
            return;
        }

        var oldValue = context.CurrentValue;
        next(ref context);

        if (!context.IsWritten)
        {
            return; // vetoed by an inner interceptor: nothing was stored, publish nothing
        }

        // Resolved during unwind; the innermost aggregated instance resolves first and marks
        // the shared write context so outer instances skip.
        var listeners = ResolveListeners(ref context);

        // Channel state is re-read post-commit so a subscription installed while this write was
        // in flight is still delivered; a full fence already ran post-commit on this thread
        // (in ResolveListeners here or in an inner aggregated instance).
        var state = _state;
        var subscriptions = state?.QueueSubscriptions ?? [];
        var syncSubject = state?.SyncSubject;

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
        if (!context.IsWritten)
        {
            return; // vetoed: nothing was stored
        }

        var listeners = ResolveListeners(ref context);
        if (listeners is null)
        {
            return;
        }

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

    // Post-commit listener resolution, shared by both entry paths. The dedup flag is per-write
    // context (by-ref on the writing thread), so reading it needs no fence; the count read must
    // stay BEHIND the fence (Dekker read side pairing with subscription install).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PropertyChangeSubscription[]? ResolveListeners<TProperty>(ref PropertyWriteContext<TProperty> context)
    {
        if (context.ArePropertyObserversNotified)
        {
            return null;
        }

        Interlocked.MemoryBarrier();
        if (PropertyChangeSubscriptions.ReadSubscriptionCount() == 0)
        {
            return null;
        }

        var listeners = TryGetListeners(context.Property);
        if (listeners is not null)
        {
            context.ArePropertyObserversNotified = true;
        }

        return listeners;
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
            PublishState([], ActiveSyncSubject); // preserve observable channel, drop queue
        }

        foreach (var subscription in toComplete)
        {
            subscription.Complete();
        }
    }
}
