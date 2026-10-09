using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// One managed thing: a subject implementing <see cref="IHostedService"/>, or a factory attachment.
/// Owns a serialized transition queue, so transitions for one slot never interleave while
/// transitions for unrelated slots run concurrently.
/// </summary>
internal sealed class HostedServiceSlot
{
    /// <summary>
    /// Guards the queue, the detach mark and the ownership exchange together with the handler's drain
    /// tracking of it. A leaf lock: nothing held under it blocks or takes another lock, so a caller may
    /// hold the lifecycle lock on the way in. Keep it that way, because a context detach takes it for
    /// every slot it enumerates while holding the lifecycle lock.
    /// </summary>
    private readonly Lock _queueLock = new();

    /// <summary>Which transition owns the slot right now, or none.</summary>
    private enum TransitionPhase
    {
        /// <summary>No transition is in flight.</summary>
        None,

        /// <summary>A start body is creating and starting an instance.</summary>
        Starting,

        /// <summary>A stop body owns the instance it took, until that instance is stopped and disposed.</summary>
        Stopping
    }

    /// <summary>
    /// The instance and the phase as one value, because they are one fact about the slot. Held in a
    /// single reference field so every transition is one write and the pair comes out of one load, which
    /// is what leaves <see cref="GetState"/> no order to get wrong between the two of them.
    /// </summary>
    private sealed record SlotSnapshot(IHostedService? Instance, TransitionPhase Phase);

    // Every snapshot that holds no instance is one of these three, so a settled slot allocates
    // nothing and only recording an instance allocates at all.
    private static readonly SlotSnapshot SettledSnapshot = new(null, TransitionPhase.None);
    private static readonly SlotSnapshot StartingSnapshot = new(null, TransitionPhase.Starting);
    private static readonly SlotSnapshot StoppingSnapshot = new(null, TransitionPhase.Stopping);

    private Task _tail = Task.CompletedTask;

    // Written by transition bodies only, which the queue serializes, and read from anywhere the handle
    // is polled. Volatile because a poll on an unrelated thread has no happens before edge to the body
    // that wrote it.
    private SlotSnapshot _snapshot = SettledSnapshot;

    private Exception? _fault;
    private Exception? _startFault;
    private HostedServiceHandler? _owner;
    private IHostedService? _lastFactoryInstance;
    private bool _detached;

    /// <summary>
    /// For a subject slot, the completion of the stop that ends the current ownership, which the
    /// stops of the subject's attachments await. Created by whichever comes first, the owner's own
    /// stop enqueue or one of its attachment stops asking to wait for it, and set by every subject stop
    /// body enqueued while it is current. Guarded by <see cref="_queueLock"/>; reset on every install,
    /// so a stop from an earlier ownership cannot release the attachments of a later one.
    /// </summary>
    private TaskCompletionSource? _stopSignal;

    /// <summary>The owner <see cref="_stopSignal"/> was created for, so no other handler's stops wait on it.</summary>
    private HostedServiceHandler? _stopSignalOwner;

    public HostedServiceSlot(Func<IHostedService>? factory, IHostedService? subject)
    {
        Factory = factory;
        Subject = subject;
    }

    /// <summary>The factory for an attachment, or null when this slot is a subject.</summary>
    public Func<IHostedService>? Factory { get; }

    /// <summary>The subject when this slot is a subject, or null when it is an attachment.</summary>
    public IHostedService? Subject { get; }

    /// <summary>True when the handler created the current instance, so it owns its disposal.</summary>
    public bool IsFactoryAttachment => Factory is not null;

    /// <summary>Test seam, awaited at the top of every transition body. Null in production.</summary>
    internal Func<Task>? TransitionTestHook { get; set; }

    /// <summary>Test seam, invoked inside the queue lock between the take and the enqueue. Null in production.</summary>
    internal Action? QueueLockTestHook { get; set; }

    public IHostedService? Current => Volatile.Read(ref _snapshot).Instance;

    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>
    /// The exception from the last start that failed, or null. Written by no later transition, so a
    /// caller waiting on a start reads that start's outcome even when an execution fault queued behind
    /// it has already been recorded in <see cref="Fault"/>.
    /// </summary>
    public Exception? StartFault => Volatile.Read(ref _startFault);

    public HostedServiceHandler? Owner => Volatile.Read(ref _owner);

    public void SetFault(Exception? fault) => Volatile.Write(ref _fault, fault);

    /// <summary>Records a failed start, as the slot's fault and as the start's own outcome.</summary>
    public void SetStartFault(Exception fault)
    {
        Volatile.Write(ref _startFault, fault);
        Volatile.Write(ref _fault, fault);
    }

    /// <summary>Clears the fault and the start outcome together, which only a start past its guards does.</summary>
    public void ClearFault()
    {
        Volatile.Write(ref _startFault, null);
        Volatile.Write(ref _fault, null);
    }

    /// <summary>Enters the start window, with nothing recorded yet.</summary>
    /// <remarks>
    /// This and each transition below stay one write, which no test can pin: split into two, a reader
    /// sees a state between them that is neither the one before nor the one after. See
    /// docs/design/hosting-service-ownership.md#the-state-a-consumer-polls.
    /// </remarks>
    public void BeginStart() => Volatile.Write(ref _snapshot, StartingSnapshot);

    /// <summary>Records the started instance and leaves the start window together.</summary>
    /// <remarks>Stays one write, for the reason on <see cref="BeginStart"/>.</remarks>
    public void RecordStarted(IHostedService instance)
        => Volatile.Write(ref _snapshot, new SlotSnapshot(instance, TransitionPhase.None));

    /// <summary>
    /// Leaves the start window for a start that recorded nothing. The condition is load bearing: this
    /// runs after <see cref="RecordStarted"/>, so settling unconditionally would null a started instance.
    /// </summary>
    public void AbandonStart() => LeavePhase(TransitionPhase.Starting);

    /// <summary>
    /// Takes the instance out of <see cref="Current"/> and enters the stop window together, so the
    /// slot never holds no instance while raising no phase.
    /// </summary>
    /// <remarks>Stays one write, for the reason on <see cref="BeginStart"/>.</remarks>
    public void BeginStop() => Volatile.Write(ref _snapshot, StoppingSnapshot);

    /// <summary>
    /// Leaves the stop window. The condition decides nothing today and is kept so a guard added above
    /// the stop's instance read cannot turn this into a settle over a live instance.
    /// </summary>
    public void EndStop() => LeavePhase(TransitionPhase.Stopping);

    /// <summary>
    /// Settles the snapshot while <paramref name="phase"/> is still the one in flight. A plain read
    /// modify write rather than a compare and exchange: the queue serializes the bodies for one slot,
    /// so nothing else writes the snapshot while this runs.
    /// </summary>
    private void LeavePhase(TransitionPhase phase)
    {
        if (Volatile.Read(ref _snapshot).Phase == phase)
        {
            Volatile.Write(ref _snapshot, SettledSnapshot);
        }
    }

    /// <summary>
    /// The state and the instance it was derived from, out of one snapshot load, so a caller cannot read
    /// a state that disagrees with the instance it then acts on. The precedence is deliberate:
    /// docs/design/hosting-service-ownership.md#the-state-a-consumer-polls.
    /// </summary>
    public HostedServiceAttachmentState GetState(out IHostedService? current)
    {
        // The instance and the phase come out of this one load, so no transition can land between them.
        var snapshot = Volatile.Read(ref _snapshot);
        current = snapshot.Instance;

        return snapshot.Instance is not null ? HostedServiceAttachmentState.Running
            : snapshot.Phase is TransitionPhase.Starting ? HostedServiceAttachmentState.Starting
            : snapshot.Phase is TransitionPhase.Stopping ? HostedServiceAttachmentState.Stopping
            // Only on a settled snapshot, so neither load below can contradict a transition in flight.
            // The mark is read outside _queueLock so a poll never queues behind an enqueue; it is one
            // way, so the worst a racing read does is report the state one moment older.
            : Volatile.Read(ref _detached) ? HostedServiceAttachmentState.Removed
            : Fault is not null ? HostedServiceAttachmentState.Faulted
            : HostedServiceAttachmentState.Stopped;
    }

    /// <summary>
    /// Permanently refuses every start enqueued after this, and none already queued. Under the queue
    /// lock, so a detach that marks here before enqueuing its stop leaves an enqueued start either
    /// refused or ordered ahead of that stop.
    /// </summary>
    public void MarkDetached()
    {
        lock (_queueLock)
        {
            _detached = true;
        }
    }

    /// <summary>
    /// Records the instance the factory produced and reports whether it differs from the previous one.
    /// Unsynchronized because only start bodies call it and the queue serializes them. Never cleared:
    /// the comparison has to outlive the stop that disposed the instance it names, or the repeat it
    /// catches is exactly the case it would miss.
    /// </summary>
    public bool TryRecordFactoryInstance(IHostedService instance)
    {
        if (ReferenceEquals(_lastFactoryInstance, instance))
        {
            return false;
        }

        _lastFactoryInstance = instance;
        return true;
    }

    /// <summary>
    /// Takes ownership and tracks the slot for the handler's drain. Finding this handler already
    /// installed is also success; <paramref name="ownershipTaken"/> tells the two apart, which a caller
    /// that may undo its own take but must leave an earlier one alone needs.
    /// </summary>
    /// <remarks>
    /// Production takes through <see cref="TryTakeOwnershipAndEnqueueAsync"/> only; this entry point is
    /// for tests that drive the exchange on its own.
    /// </remarks>
    public bool TryTakeOwnership(HostedServiceHandler handler, IInterceptorSubject subject, out bool ownershipTaken)
    {
        lock (_queueLock)
        {
            return TryTakeOwnershipCore(handler, subject, out ownershipTaken);
        }
    }

    /// <summary>
    /// Releases an ownership this handler installed and untracks the slot for the drain. Under the queue
    /// lock, so a take's liveness read and its exchange are one step against this release:
    /// docs/design/hosting-service-ownership.md#ownership.
    /// </summary>
    public void ReleaseOwnership(HostedServiceHandler handler)
    {
        lock (_queueLock)
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _owner, null, handler), handler))
            {
                handler.UntrackForDrain(this);
            }
        }
    }

    /// <summary>
    /// The exchange and the drain tracking under one acquisition, which the callers hold: a release
    /// landing between them nulls the owner, finds nothing to untrack, and leaves a drain entry nothing
    /// can ever match.
    /// </summary>
    private bool TryTakeOwnershipCore(HostedServiceHandler handler, IInterceptorSubject subject, out bool ownershipTaken)
    {
        var previous = Interlocked.CompareExchange(ref _owner, handler, null);
        ownershipTaken = previous is null;

        if (ownershipTaken)
        {
            handler.TrackForDrain(this, subject);

            // A new ownership ends with a stop of its own. The previous signal stays with the stop
            // that captured it at its enqueue, which is queued ahead of the start this install enqueues.
            _stopSignal = null;
            _stopSignalOwner = null;
        }

        return ownershipTaken || ReferenceEquals(previous, handler);
    }

    /// <summary>
    /// Enqueues a stop for a subject slot while <paramref name="handler"/> still owns it, as on
    /// <see cref="EnqueueIfOwnedAsync"/>, and hands the body the signal it must set when it has run,
    /// chosen under the same lock acquisition as the enqueue so an attachment stop asking for it in
    /// between is handed the signal this stop will set. A refused enqueue leaves the signal alone.
    /// </summary>
    public Task? EnqueueSubjectStopIfOwnedAsync(HostedServiceHandler handler, Func<TaskCompletionSource, Func<Task>> createBody)
    {
        lock (_queueLock)
        {
            return ReferenceEquals(Owner, handler) ? EnqueueCore(createBody(GetOrCreateStopSignal(handler)), handler) : null;
        }
    }

    /// <summary>
    /// The stop of this subject that an attachment stop enqueued by <paramref name="handler"/> has to
    /// wait for, or null when there is none: the stop <paramref name="handler"/> has enqueued for the
    /// current ownership and that has not finished, or, while it owns the slot and has enqueued none,
    /// the one it is going to enqueue. Null once that stop has finished and when the ownership is
    /// another handler's, so the wait is never on a stop this handler is not going to enqueue.
    /// </summary>
    /// <remarks>
    /// The signal is created here only for the owner, because every ownership ends with a stop enqueued
    /// by its owner ahead of the release, which is what guarantees a signal created here is set:
    /// docs/design/hosting-service-ownership.md#the-subject-stop-signal.
    /// </remarks>
    public Task? GetStopToAwait(HostedServiceHandler handler)
    {
        lock (_queueLock)
        {
            if (_stopSignal is { } signal)
            {
                return ReferenceEquals(_stopSignalOwner, handler) && !signal.Task.IsCompleted ? signal.Task : null;
            }

            return ReferenceEquals(_owner, handler) ? GetOrCreateStopSignal(handler).Task : null;
        }
    }

    /// <summary>
    /// The current ownership's signal, created on first use. Every caller is the owner: the stop
    /// enqueue and the wait both check ownership under the queue lock.
    /// </summary>
    private TaskCompletionSource GetOrCreateStopSignal(HostedServiceHandler handler)
    {
        if (_stopSignal is null)
        {
            _stopSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopSignalOwner = handler;
        }

        return _stopSignal;
    }

    /// <summary>
    /// Enqueues a transition counted against <paramref name="handler"/>, so its drain waits for it, and
    /// returns a task completing when it has run. Enqueuing never blocks and never runs the body, so
    /// callers may enqueue while holding a lock.
    /// </summary>
    public Task EnqueueAsync(HostedServiceHandler handler, Func<Task> body)
    {
        lock (_queueLock)
        {
            return EnqueueCore(body, handler);
        }
    }

    /// <summary>
    /// Enqueues a transition only while <paramref name="handler"/> still owns the slot, both decided
    /// under one acquisition of the queue lock. Returns null when ownership has moved on, in which case
    /// nothing was enqueued. Why the decision is inside the lock:
    /// docs/design/hosting-service-ownership.md#why-the-ownership-decision-is-inside-the-queue-lock.
    /// </summary>
    public Task? EnqueueIfOwnedAsync(HostedServiceHandler handler, Func<Task> body)
    {
        lock (_queueLock)
        {
            return ReferenceEquals(Owner, handler) ? EnqueueCore(body, handler) : null;
        }
    }

    /// <summary>
    /// Confirms the subject is still live for <paramref name="handler"/>, takes ownership and enqueues
    /// the transition, all under one acquisition of the queue lock. Returns null when the subject is
    /// no longer live or another handler owns the slot, in which case nothing was enqueued and no
    /// ownership was taken. <paramref name="ownershipTaken"/> is as on <see cref="TryTakeOwnership"/>.
    /// </summary>
    /// <remarks>
    /// The three steps must stay under one acquisition:
    /// docs/design/hosting-service-ownership.md#the-read-inside-the-queue-lock.
    /// </remarks>
    public Task? TryTakeOwnershipAndEnqueueAsync(
        HostedServiceHandler handler,
        IInterceptorSubject subject,
        Func<Task> body,
        out bool ownershipTaken)
    {
        ownershipTaken = false;

        lock (_queueLock)
        {
            if (_detached || !handler.IsLive(subject))
            {
                return null;
            }

            if (!TryTakeOwnershipCore(handler, subject, out ownershipTaken))
            {
                return null;
            }

            QueueLockTestHook?.Invoke();

            return EnqueueCore(body, handler);
        }
    }

    private Task EnqueueCore(Func<Task> body, HostedServiceHandler handler)
    {
        // Before the enqueue, never after: on an already completed tail the continuation runs and
        // decrements before the next statement here executes, which takes the count negative. Nothing
        // between here and the enqueue may throw, because a leaked increment never comes back.
        handler.EnterTransition();

        // The lock the callers hold is required: "_tail = _tail.ContinueWith(...)" is a
        // read-modify-write, and two racing enqueuers lose an assignment and run both transitions
        // concurrently. TaskScheduler.Default is required: ContinueWith otherwise captures
        // TaskScheduler.Current, which can be a scheduler the enqueuing task is itself occupying.
        _tail = _tail
            .ContinueWith(
                _ => RunAsync(body, handler),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default)
            .Unwrap();

        return _tail;
    }

    private async Task RunAsync(Func<Task> body, HostedServiceHandler handler)
    {
        // A body inherits the execution context of the flow that enqueued it, start deferral included.
        // A stop body does not wait for that deferral, so an attach it makes would be captured by a
        // deferral belonging to a caller it has nothing to do with and park until that caller closes it.
        handler.ClearAmbientStartDeferral();

        // Bodies never throw. A faulted tail would raise UnobservedTaskException for every dropped
        // fire and forget transition and would be retained until the slot transitions again.
        try
        {
            if (TransitionTestHook is { } hook)
            {
                await hook().ConfigureAwait(false);
            }

            await body().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Handled by the body itself, which records into Fault and logs.
        }
        finally
        {
            // In the finally rather than after the catch, which covers the body alone: the test hook
            // above is inside the same count.
            handler.LeaveTransition();
        }
    }
}
