using System.Reactive.Subjects;
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

    // ----- Queue facet -----

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
            PublishState(updated, _syncSubject);
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
            PublishState(updated, _syncSubject);
        }
    }

    // ----- Observable facet -----

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
            PublishState(_state?.QueueSubscriptions ?? [], _syncSubject);
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

    // ----- Write path -----

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var state = _state;
        if (state is null)
        {
            next(ref context);
            return;
        }

        var subscriptions = state.QueueSubscriptions;
        var syncSubject = state.SyncSubject;

        var oldValue = context.CurrentValue;
        next(ref context);

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
            PublishState([], _syncSubject); // preserve observable facet, drop queue
        }

        foreach (var subscription in toComplete)
        {
            subscription.Complete();
        }
    }
}
