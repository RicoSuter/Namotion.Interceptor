using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Single write interceptor that delivers property changes through three channels: an Rx observable
/// (see <see cref="InterceptorSubjectContextExtensions.GetPropertyChangeObservable"/>), a
/// high-performance pull queue (see <see cref="InterceptorSubjectContextExtensions.CreatePropertyChangeQueueSubscription"/>),
/// and synchronous per-property subscriptions (see <see cref="PropertyChangeSubscriptionExtensions"/>).
/// </summary>
[RunsAfter(typeof(SubjectTransactionInterceptor))]
public sealed class PropertyChangeInterceptor : IObservable<SubjectPropertyChange>, IWriteInterceptor
{
    private readonly Lock _modificationLock = new();

    // The single hot-path field. Null means neither channel has consumers (idle).
    private volatile DispatchState? _state;

    // Observable channel state, guarded by _modificationLock. Lazily created on first subscribe.
    private Subject<SubjectPropertyChange>? _subject;
    private ISubject<SubjectPropertyChange>? _syncSubject;
    private int _observableConsumerCount;

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

    // Called under _modificationLock.
    private void PublishState(PropertyChangeQueueSubscription[] queueSubscriptions, ISubject<SubjectPropertyChange>? syncSubject)
    {
        _state = queueSubscriptions.Length == 0 && syncSubject is null
            ? null
            : new DispatchState { QueueSubscriptions = queueSubscriptions, SyncSubject = syncSubject };
    }

    internal PropertyChangeQueueSubscription CreateQueueSubscription()
    {
        PropertyChangeQueueSubscription subscription;
        lock (_modificationLock)
        {
            subscription = new PropertyChangeQueueSubscription(this);
            var current = _state?.QueueSubscriptions ?? [];
            PublishState(CopyOnWriteArray.Add(current, subscription), ActiveSyncSubject);
        }

        // Subscriber-side Dekker half: the state publish must be globally visible before the
        // caller's post-create property reads (a lock exit alone is only a release). Pairs with
        // the write path's post-commit fence and state re-read.
        Interlocked.MemoryBarrier();
        return subscription;
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
        // Validate first: a later throw would leave the consumer count published with no handle to dispose.
        ArgumentNullException.ThrowIfNull(observer);

        IDisposable inner;
        lock (_modificationLock)
        {
            if (_subject is null)
            {
                _subject = new Subject<SubjectPropertyChange>();
                _syncSubject = Subject.Synchronize(_subject);
            }

            // Join BEFORE publishing state: the join's CAS-install precedes the volatile _state
            // publish in program order, so a writer that observes the state also observes the
            // join; joining after the publish reopens the missed-write window. Safe under the
            // lock only because this subject is never completed, errored, or disposed (Subscribe
            // is then a pure CAS); the Synchronize wrapper gates OnNext only.
            inner = _subject.Subscribe(observer);

            _observableConsumerCount++;
            if (_observableConsumerCount == 1)
            {
                // Mirrors RemoveObservableConsumer's 1 to 0; later consumers share the published subject.
                PublishState(_state?.QueueSubscriptions ?? [], ActiveSyncSubject);
            }
        }

        // Subscriber-side Dekker half (see CreateQueueSubscription).
        Interlocked.MemoryBarrier();
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
            DispatchLateConsumers(ref context);
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

        // Re-read post-commit so a subscription installed mid-write is delivered; a full fence
        // already ran on this thread (ResolveListeners here or in an inner aggregated instance).
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
    /// Idle-entry path: the gate saw no consumers, so no old value was captured. A listener or
    /// channel subscription installed while the write was in flight is still delivered, with the
    /// final value as both old and new (documented caveat). Non-inlined to keep the fast path small.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void DispatchLateConsumers<TProperty>(ref PropertyWriteContext<TProperty> context)
    {
        if (!context.IsWritten)
        {
            return; // vetoed: nothing was stored
        }

        var listeners = ResolveListeners(ref context);

        // Channel state re-read behind the fence (ResolveListeners fenced on this thread, or an
        // inner aggregated instance did): pairs with the fence after the channel install.
        var state = _state;

        if (state is null && listeners is null)
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

        if (state is not null)
        {
            var subscriptions = state.QueueSubscriptions;
            for (var i = 0; i < subscriptions.Length; i++)
            {
                subscriptions[i].Enqueue(in change);
            }

            state.SyncSubject?.OnNext(change);
        }

        if (listeners is not null)
        {
            PropertyChangeSubscription.Dispatch(listeners, in change);
        }
    }

    // Post-commit listener resolution, shared by both entry paths and performed once per write:
    // the innermost aggregated instance resolves (whether or not listeners exist) and marks the
    // per-write context so outer instances skip. A listener installed after this resolution is
    // not owed the write (it committed before the install; the post-subscribe read observes it).
    // The flag needs no fence (by-ref context on the writing thread) and the instance that sets
    // it always executes the fence, which the channel state re-reads rely on; the count read
    // must stay BEHIND the fence (Dekker read side pairing with subscription install).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PropertyChangeSubscription[]? ResolveListeners<TProperty>(ref PropertyWriteContext<TProperty> context)
    {
        if (context.ArePropertyObserversResolved)
        {
            return null;
        }

        context.ArePropertyObserversResolved = true;

        Interlocked.MemoryBarrier();
        if (PropertyChangeSubscriptions.ReadSubscriptionCount() == 0)
        {
            return null;
        }

        return TryGetListeners(context.Property);
    }

    private static PropertyChangeSubscription[]? TryGetListeners(PropertyReference property)
    {
        return property.Subject.Data.TryGetValue((property.Name, PropertyChangeSubscription.ListenersKey), out var value)
            ? value as PropertyChangeSubscription[]
            : null;
    }
}
