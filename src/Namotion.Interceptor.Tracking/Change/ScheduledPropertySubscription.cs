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
    // Hazardous interleavings, each with the field, the primitive that orders it, and what that buys.
    //
    // Enqueue against enqueue: only the zero to one transition of _wip schedules, so concurrent writers
    // cannot both start a drain and lose the in-subscription serialization.
    //
    // Enqueue against a settling drain: the settling Interlocked.Add and the enqueue Interlocked.Increment
    // are RMWs on _wip and therefore totally ordered. Either the settle observes the new work and
    // reschedules, or it returns zero and the increment returns one and schedules. Exactly one successor
    // either way, so accepted work is neither stranded nor drained twice at once.
    //
    // Dispose against enqueue: a writer already past its _state check can still enqueue and still schedule
    // after Dispose returns. Such a change lands in the queue the transition detached, which PendingCount no
    // longer reads and which becomes garbage with the last reference a racing writer or drain still holds.
    // The settle's _state check alone stops the reschedule, and the _wip left behind gates nothing. The drain's in-loop _state re-check adds nothing to
    // that and is only a harmless early exit: the transition drops the queue, so a drain running after it
    // dequeues nothing, settles with processed == 0 and is suppressed by the settle's check anyway. Neither
    // check stops the delivery of a change already queued: the drain reads _observer with no ordering against
    // the CAS, so a change dequeued in the window between the CAS and TransitionOutOfLive's
    // Volatile.Write(ref _observer, null) becoming visible is still delivered, and that null guard in Deliver
    // is the only thing that closes the window.
    //
    // Dispose against a mid-flight delivery: Deliver reads _observer into a local before use and ScheduleDrain
    // reads _scheduler the same way, so a transition that nulls them cannot null-reference work already
    // running. _onError is read into a local for a different reason: ReportError null-checks it, so a read
    // taken too late drops the report rather than faulting.
    //
    // Fault against dispose: both go through the Interlocked.CompareExchange on _state out of Live, so
    // exactly one performs the release and the loser does nothing. The release runs through the upstream
    // subscription's own one-shot Dispose and never touches the process-wide count itself, which is what
    // makes a double decrement unreachable. That count gates a process-wide idle write fast path, so
    // reaching zero with live subscriptions elsewhere would silently stop per-property delivery host-wide.
    //
    // A failing ScheduleDrain against the counter: a Schedule call that throws leaves _wip positive too, which
    // is harmless because the catch faults the subscription and nothing gates on the counter afterwards. A
    // Schedule call that succeeds and whose work item never runs leaves the same positive counter with the
    // subscription still Live and no further ScheduleDrain, which is unrecoverable and undetectable from
    // here, and is why a caller must dispose its subscriptions before the scheduler they run on.
    //
    // A drain exit against the next drain entry: the settling Interlocked.Add, the next writer's
    // Interlocked.Increment on the same field and the schedule that follows form a happens-before chain, so
    // a delivery observes state written by the previous delivery even when the two land on different
    // scheduler threads. This is what lets an observer of one subscription keep state without synchronizing.

    private const int Live = 0;
    private const int Disposed = 1;
    private const int Faulted = 2;

    /// <summary>
    /// Deliveries per scheduler work item before the drain hands off to a fresh one. Without a budget the
    /// drain would hold its scheduler thread for as long as a writer outruns the observer, which starves
    /// sibling subscriptions and unrelated pool work. It caps the handoff cost while a backlog exists rather
    /// than limiting the rate: when the observer keeps up, the counter settles to zero constantly and every
    /// new write schedules again.
    /// </summary>
    internal const int MaxBatch = 1024;

    // Cached and static so no closure or delegate is allocated per Schedule call.
    private static readonly Func<IScheduler, ScheduledPropertySubscription, IDisposable> DrainAction =
        static (_, subscription) =>
        {
            subscription.Drain();
            return Disposable.Empty;
        };

    // Nulled rather than cleared on the way out of Live: Clear installs a fresh segment instead of freeing
    // anything, so a disposed handle parked in a DI container would keep holding what the live one held.
    private ConcurrentQueue<SubjectPropertyChange>? _queue = new();

    private IPropertyChangeObserver? _observer;
    private Action<Exception>? _onError;
    private IDisposable? _upstream;
    private IScheduler? _scheduler;

    private int _state;
    private int _wip;

    private ScheduledPropertySubscription(IPropertyChangeObserver observer, IScheduler scheduler, Action<Exception>? onError)
    {
        _observer = observer;
        _scheduler = scheduler;
        _onError = onError;
    }

    /// <summary>
    /// Changes accepted but not yet dequeued, excluding one currently being delivered, and exact only when
    /// read from a quiescent state, for the same reason <see cref="PropertyChangeQueueSubscription.Count"/>
    /// is. The queue is unbounded, so this is how a consumer on a hot property observes a growing backlog
    /// instead of discovering it through memory pressure. It reports the current backlog only: the queue
    /// keeps the largest segment it ever grew to, so the subscription goes on holding its peak backlog after
    /// this reads zero again, and only <see cref="Dispose"/> gives that memory back. A faulted subscription
    /// reads the same zero a healthy idle one does, so use <see cref="IsFaulted"/> to tell those apart.
    /// </summary>
    public int PendingCount => Volatile.Read(ref _queue)?.Count ?? 0;

    /// <summary>
    /// Whether a scheduler failure killed the subscription, which stops delivery and acceptance for good and
    /// cannot be undone. False after <see cref="Dispose"/>, which is a deliberate stop rather than a failure.
    /// With the default null <c>onError</c> this is the only way to detect such a subscription. It does not
    /// cover the other half of the scheduler-failure space, a <c>Schedule</c> call that succeeds and whose
    /// work item never runs, which stays live and simply goes quiet.
    /// </summary>
    public bool IsFaulted => Volatile.Read(ref _state) == Faulted;

    internal int WorkInProgressForTests => Volatile.Read(ref _wip);

    internal bool IsObserverReleasedForTests => Volatile.Read(ref _observer) is null;

    internal static ScheduledPropertySubscription Create(
        PropertyReference property,
        IPropertyChangeObserver observer,
        IScheduler scheduler,
        Action<Exception>? onError)
    {
        var subscription = new ScheduledPropertySubscription(observer, scheduler, onError);

        // Installs the upstream and can start delivering before the publication below.
        var upstream = property.SubscribeInline(new Forwarder(subscription));

        // Creator-side Dekker half: an interlocked publication, not a release store, because only an RMW
        // orders this store against the state load below. It pairs with TransitionOutOfLive's
        // CAS-then-Exchange, and without the StoreLoad ordering both halves can miss each other and strand
        // the upstream forever. Mirrors the barriers in PropertyChangeSubscription.Create. A release store
        // followed by an acquire load leaves StoreLoad free, so the orphan is reachable on x86-64 and only
        // accidentally excluded on arm64, where the stlr/ldar pair happens to close it, which is why local
        // testing does not surface it.
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

        // A transition out of Live drops the queue, and a writer already past the state check races it:
        // reading null here skips the enqueue and the counter increment, and a stale non-null read lands the
        // change in a detached queue that nothing drains and nothing keeps alive.
        var queue = Volatile.Read(ref _queue);
        if (queue is null)
        {
            return;
        }

        // Enqueue before the increment: TryDequeue then cannot report empty while the counter is positive,
        // because ConcurrentQueue spins on a reserved-but-unpublished slot instead. Reversed, a drain can find
        // nothing and exit with processed == 0, so the settling Interlocked.Add(ref _wip, 0) returns the
        // counter unchanged and positive, _state is still Live, and the drain reschedules itself having made
        // no progress: a self-sustaining reschedule storm occupying a scheduler thread until the writer's
        // enqueue lands, not merely a wasted work item.
        queue.Enqueue(change);
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

                // Read per iteration rather than hoisted, so a drain that outlives the transition sees the
                // dropped queue and stops instead of holding it for the rest of the batch.
                var queue = Volatile.Read(ref _queue);
                if (queue is null || !queue.TryDequeue(out var change))
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
        // A transition out of Live releases the scheduler too, and both callers can race one, so a null here
        // means the subscription is already gone and there is nothing left to drain for.
        var scheduler = Volatile.Read(ref _scheduler);
        if (scheduler is null)
        {
            return;
        }

        // Captured before the try rather than read inside the catch: the fault transition there nulls
        // _onError itself, and a racing Dispose does the same, so a read taken in the catch would swallow the
        // scheduler fault silently.
        var onError = Volatile.Read(ref _onError);

        try
        {
            // Scheduling happens inside the write, so without suppression the observer would inherit the
            // writer's ambient AsyncLocal state, including SubjectTransaction.CurrentTransaction, and a whole
            // batch would run under whichever writer enqueued first. This only governs the work item: Rx does
            // not save or restore context per work item on a scheduler that owns its thread, so such a thread
            // keeps the AsyncLocal values it was born with and nothing here can strip them.
            if (ExecutionContext.IsFlowSuppressed())
            {
                // Not a guard against a throw, since a nested SuppressFlow is legal and leaves the outer scope
                // intact. It saves the ExecutionContext clone, and is reachable because a writer can write
                // from inside its own suppressed-flow scope.
                scheduler.Schedule(this, DrainAction);
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                {
                    scheduler.Schedule(this, DrainAction);
                }
            }
        }
        catch (Exception exception)
        {
            // Transition first, so IsFaulted already reads true inside the handler being told about the
            // fault. Reported first, a handler that reacts by disposing, which is the natural reaction, wins
            // the CAS and pins IsFaulted false for good, leaving a supervisor polling it none the wiser.
            try
            {
                TransitionOutOfLive(Faulted);
            }
            catch
            {
                // Sealed like every other escape here: this runs inside a scheduler work item on the settle
                // path, where an escape is unhandled and terminates the process, and on the writer thread
                // otherwise, where it would reach the setter this channel exists to isolate. The report below
                // still goes out.
            }

            ReportError(onError, exception);
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

        // The handler is captured before the observer runs rather than inside the catch, for the same reason
        // ScheduleDrain captures it: a stop-on-first-failure observer disposes its own subscription and then
        // throws, which nulls the field itself, and a read taken afterwards would drop the exception it
        // deliberately rethrew.
        var onError = Volatile.Read(ref _onError);

        try
        {
            observer.OnChange(in change);
        }
        catch (Exception exception)
        {
            ReportError(onError, exception);
        }
    }

    private static void ReportError(Action<Exception>? onError, Exception exception)
    {
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

    private void TransitionOutOfLive(int target)
    {
        if (Interlocked.CompareExchange(ref _state, target, Live) != Live)
        {
            return;
        }

        Volatile.Write(ref _observer, null);
        Volatile.Write(ref _onError, null);

        // Dropped for the same reason as the observer, and it matters more: an EventLoopScheduler owns a
        // thread, so a disposed handle still holding one keeps that thread reachable.
        Volatile.Write(ref _scheduler, null);

        // Queued changes each pin a subject and its boxed values, and the queue holds on to the largest
        // segment its backlog ever needed, so the whole queue goes rather than being cleared. Dropped before
        // the release below, which is the only statement here that can throw.
        Volatile.Write(ref _queue, null);

        // Releasing through the upstream's own one-shot Dispose is what makes the process-wide gate
        // decrement unreachable twice when a fault races a disposal.
        Interlocked.Exchange(ref _upstream, null)?.Dispose();
    }

    /// <summary>
    /// Stops delivery, releases the upstream subscription and the reference to the scheduler, so a parked
    /// handle does not keep an <c>EventLoopScheduler</c> thread reachable, and drops the queue with whatever
    /// was still in it, so those changes stop pinning their subjects and the segment its peak backlog grew to
    /// goes with them. A delivery already running can finish after this returns, so an observer that touches
    /// state the caller owns and disposes must tolerate a call arriving late, and a drain work item already
    /// queued on the scheduler is not cancelled either; it runs and finds nothing left to deliver.
    /// </summary>
    public void Dispose() => TransitionOutOfLive(Disposed);

    private sealed class Forwarder(ScheduledPropertySubscription owner) : IPropertyChangeObserver
    {
        public void OnChange(in SubjectPropertyChange change) => owner.Enqueue(in change);
    }
}
