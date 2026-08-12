using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// A per-property subscription whose deliveries run on a scheduler instead of on the writing thread,
/// serialized within the subscription, with observer and scheduler exceptions reported rather than
/// propagated into the write. Disposal is mandatory.
/// </summary>
public sealed class ScheduledPropertySubscription : IDisposable
{
    private const int Live = 0;
    private const int Disposed = 1;
    private const int Faulted = 2;

    /// <summary>
    /// Deliveries per scheduler work item before the drain hands off to a fresh one. Without a budget the
    /// drain would hold its scheduler thread for as long as a writer outruns the observer, which starves
    /// sibling subscriptions and unrelated pool work. 1024 costs one work item per 1024 changes.
    /// </summary>
    internal const int MaxBatch = 1024;

    // Re-entrancy accounting is test-only: two interlocked operations per delivery is not a cost the
    // production path should pay for an assertion.
    internal static bool EnableReentrancyInstrumentation;

    // Cached and static so no closure or delegate is allocated per Schedule call.
    private static readonly Func<IScheduler, ScheduledPropertySubscription, IDisposable> DrainAction =
        static (_, subscription) =>
        {
            subscription.Drain();
            return Disposable.Empty;
        };

    private readonly ConcurrentQueue<SubjectPropertyChange> _queue = new();
    private readonly IScheduler _scheduler;

    private IPropertyChangeObserver? _observer;
    private Action<Exception>? _onError;
    private IDisposable? _upstream;

    private int _state;
    private int _wip;
    private int _inDeliver;
    private int _reentrancyCount;

    private ScheduledPropertySubscription(IPropertyChangeObserver observer, IScheduler scheduler, Action<Exception>? onError)
    {
        _observer = observer;
        _scheduler = scheduler;
        _onError = onError;
    }

    /// <summary>
    /// Changes accepted but not yet dequeued, excluding one currently being delivered. Exact only when read
    /// from a quiescent state, for the same reason <see cref="PropertyChangeQueueSubscription.Count"/> is.
    /// The queue is unbounded, so this is how a consumer on a hot property observes a growing backlog
    /// instead of discovering it through memory pressure. A writer already past its state check can still
    /// enqueue after <see cref="Dispose"/> cleared the queue, and such a change is never dequeued, so this
    /// is not guaranteed to reach zero once the subscription is disposed.
    /// </summary>
    public int PendingCount => _queue.Count;

    internal int WorkInProgressForTests => Volatile.Read(ref _wip);

    internal int ReentrancyCountForTests => Volatile.Read(ref _reentrancyCount);

    internal static ScheduledPropertySubscription Create(
        PropertyReference property,
        IPropertyChangeObserver observer,
        IScheduler scheduler,
        Action<Exception>? onError)
    {
        var subscription = new ScheduledPropertySubscription(observer, scheduler, onError);

        // Installs the upstream and can start delivering before the publication below.
        var upstream = property.Subscribe(new Forwarder(subscription));

        // Creator-side Dekker half: an interlocked publication, not a release store, because only an RMW
        // orders this store against the state load below. It pairs with TransitionOutOfLive's
        // CAS-then-Exchange, and without the StoreLoad ordering both halves can miss each other and strand
        // the upstream forever. Mirrors the barriers in PropertyChangeSubscription.Create.
        Interlocked.Exchange(ref subscription._upstream, upstream);

        // A change arriving during Subscribe can fault the subscription through a throwing scheduler, and
        // that transition saw a null upstream. Releasing here is what stops it leaking.
        if (Volatile.Read(ref subscription._state) != Live)
        {
            Interlocked.Exchange(ref subscription._upstream, null)?.Dispose();
        }

        return subscription;
    }

    private void Enqueue(in SubjectPropertyChange change)
    {
        if (Volatile.Read(ref _state) != Live)
        {
            return;
        }

        // Enqueue before the increment: TryDequeue then cannot report empty while the counter is positive,
        // because ConcurrentQueue spins on a reserved-but-unpublished slot instead. Reversing these is a
        // liveness bug, not a correctness one, and it shows up as drains that find nothing.
        _queue.Enqueue(change);
        if (Interlocked.Increment(ref _wip) == 1)
        {
            ScheduleDrain();
        }
    }

    private void Drain()
    {
        var processed = 0;
        try
        {
            // A count hint only. Item visibility comes from the queue, and the settling Add below is what
            // makes a stale read safe.
            var pending = Volatile.Read(ref _wip);
            while (processed < MaxBatch)
            {
                if (processed >= pending)
                {
                    // Work accepted while this batch ran would otherwise wait for a whole new work item
                    // with the budget untouched, so the snapshot is refreshed before giving up.
                    pending = Volatile.Read(ref _wip);
                    if (processed >= pending)
                    {
                        break;
                    }
                }

                if (Volatile.Read(ref _state) != Live)
                {
                    return;
                }

                if (!_queue.TryDequeue(out var change))
                {
                    break;
                }

                processed++; // counts the dequeue, not the delivery, so an escape leaves the counter consistent
                Deliver(in change);
            }
        }
        finally
        {
            if (Interlocked.Add(ref _wip, -processed) != 0 && Volatile.Read(ref _state) == Live)
            {
                ScheduleDrain();
            }
        }
    }

    private void ScheduleDrain()
    {
        try
        {
            // Scheduling happens inside the write, so without suppression the observer would inherit the
            // writer's ambient AsyncLocal state, including SubjectTransaction.CurrentTransaction, and a whole
            // batch would run under whichever writer enqueued first.
            if (ExecutionContext.IsFlowSuppressed())
            {
                _scheduler.Schedule(this, DrainAction);
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                {
                    _scheduler.Schedule(this, DrainAction);
                }
            }
        }
        catch (Exception exception)
        {
            ReportError(exception);
            TransitionOutOfLive(Faulted);
        }
    }

    private void Deliver(in SubjectPropertyChange change)
    {
        // Read into a local so a disposal racing this delivery cannot null-reference it, matching
        // PropertyChangeSubscription.Dispatch.
        var observer = Volatile.Read(ref _observer);
        if (observer is null)
        {
            return;
        }

        var instrumented = EnableReentrancyInstrumentation;
        if (instrumented && Interlocked.Increment(ref _inDeliver) != 1)
        {
            Interlocked.Increment(ref _reentrancyCount);
        }

        try
        {
            observer.OnChange(in change);
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
        finally
        {
            if (instrumented)
            {
                Interlocked.Decrement(ref _inDeliver);
            }
        }
    }

    private void ReportError(Exception exception)
    {
        var onError = Volatile.Read(ref _onError);
        if (onError is null)
        {
            return;
        }

        try
        {
            onError(exception);
        }
        catch
        {
            // The handler added to observe failures must not become one. An escape here would leave a
            // scheduler work item, which on the thread pool is unhandled and terminates the process.
        }
    }

    private bool TransitionOutOfLive(int target)
    {
        if (Interlocked.CompareExchange(ref _state, target, Live) != Live)
        {
            return false;
        }

        Volatile.Write(ref _observer, null);
        Volatile.Write(ref _onError, null);

        // Releasing through the upstream's own one-shot Dispose is what makes the process-wide gate
        // decrement unreachable twice when a fault races a disposal.
        Interlocked.Exchange(ref _upstream, null)?.Dispose();

        // Queued changes each pin a subject and its boxed values, and these handles get parked in DI
        // containers, so they are released rather than retained.
        _queue.Clear();
        return true;
    }

    /// <summary>
    /// Stops delivery, releases the upstream subscription and drops the queued changes. A change enqueued
    /// by a writer that had already passed its state check can land in the queue after it was cleared, and
    /// nothing dequeues it afterwards, so <see cref="PendingCount"/> is not guaranteed to reach zero and
    /// such a change keeps pinning its subject. The number of them is bounded by the writers running
    /// concurrently with the disposal.
    /// </summary>
    public void Dispose() => TransitionOutOfLive(Disposed);

    private sealed class Forwarder(ScheduledPropertySubscription owner) : IPropertyChangeObserver
    {
        public void OnChange(in SubjectPropertyChange change) => owner.Enqueue(in change);
    }
}
