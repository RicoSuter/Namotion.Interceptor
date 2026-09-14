using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// One managed thing: a subject implementing <see cref="IHostedService"/>, or a factory attachment.
/// Owns a serialized transition chain, so transitions for one target never interleave while
/// transitions for unrelated targets run concurrently.
/// </summary>
internal sealed class HostedServiceTarget
{
    private readonly object _sync = new();

    /// <summary>
    /// Pairs the ownership exchange with the handler's record of it, which are one fact: a release
    /// landing between an install and its record nulls the owner, finds no record to retire, and leaves
    /// one that no later release can ever match.
    /// </summary>
    /// <remarks>
    /// Its own lock rather than <c>_sync</c>. A context detach releases every attachment target it
    /// enumerates, including ones whose chain lock a concurrent attach is holding, so releasing under
    /// the chain lock deadlocks that pair. Nothing held here ever takes another lock, and the take
    /// enters it while already holding <c>_sync</c>, so the one order is chain lock then this.
    /// </remarks>
    private readonly object _ownershipSync = new();

    /// <summary>Which transition owns the target right now, or none.</summary>
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
    /// The instance and the phase as one value, because they are one fact about the target. Held in a
    /// single reference field so every transition is one write and the pair comes out of one load, which
    /// is what leaves <see cref="State"/> no order to get wrong between the two of them.
    /// </summary>
    private sealed record TargetSnapshot(IHostedService? Instance, TransitionPhase Phase);

    // Every snapshot that holds no instance is one of these three, so a settled target allocates
    // nothing and only recording an instance allocates at all.
    private static readonly TargetSnapshot SettledSnapshot = new(null, TransitionPhase.None);
    private static readonly TargetSnapshot StartingSnapshot = new(null, TransitionPhase.Starting);
    private static readonly TargetSnapshot StoppingSnapshot = new(null, TransitionPhase.Stopping);

    private Task _tail = Task.CompletedTask;

    // Written by transition bodies only, which the chain serializes, and read from anywhere the handle
    // is polled. Volatile because a poll on an unrelated thread has no happens before edge to the body
    // that wrote it.
    private TargetSnapshot _snapshot = SettledSnapshot;

    private Exception? _fault;
    private HostedServiceHandler? _owner;
    private IHostedService? _lastFactoryInstance;
    private bool _detached;

    public HostedServiceTarget(Func<IHostedService>? factory, IHostedService? subject)
    {
        Factory = factory;
        Subject = subject;
    }

    /// <summary>The factory for an attachment, or null when this target is a subject.</summary>
    public Func<IHostedService>? Factory { get; }

    /// <summary>The subject when this target is a subject, or null when it is an attachment.</summary>
    public IHostedService? Subject { get; }

    /// <summary>True when the handler created the current instance, so it owns its disposal.</summary>
    public bool IsHandlerOwnedInstance => Factory is not null;

    /// <summary>Test seam, awaited at the top of every transition body. Null in production.</summary>
    internal Func<Task>? TransitionGate { get; set; }

    /// <summary>Test seam, invoked inside the chain lock between the take and the append. Null in production.</summary>
    internal Action? ChainLockGate { get; set; }

    public IHostedService? Current => Volatile.Read(ref _snapshot).Instance;

    public Exception? Fault => Volatile.Read(ref _fault);

    public HostedServiceHandler? Owner => Volatile.Read(ref _owner);

    public void SetFault(Exception? fault) => Volatile.Write(ref _fault, fault);

    /// <summary>Enters the start window, with nothing recorded yet.</summary>
    /// <remarks>
    /// One write, and each of the four transitions below has to stay one write. A reader takes the
    /// instance and the phase in a single load, so a transition split back into two writes puts a state
    /// that is neither the one before nor the one after between them, and nothing in the suite would
    /// catch it: what makes this safe is the shape of the write rather than a test.
    /// </remarks>
    public void BeginStart() => Volatile.Write(ref _snapshot, StartingSnapshot);

    /// <summary>Records the started instance and leaves the start window together.</summary>
    /// <remarks>Stays one write, for the reason on <see cref="BeginStart"/>.</remarks>
    public void CompleteStart(IHostedService instance)
        => Volatile.Write(ref _snapshot, new TargetSnapshot(instance, TransitionPhase.None));

    /// <summary>
    /// Leaves the start window for a start that recorded nothing, and leaves a start that recorded its
    /// instance alone, because the write that recorded it already left the window.
    /// </summary>
    /// <remarks>
    /// Stays one write, for the reason on <see cref="BeginStart"/>. The condition is load bearing as
    /// the start body is written: <see cref="CompleteStart"/> replaces the snapshot with the one
    /// holding the instance, and this runs afterwards in the same body, so settling unconditionally
    /// would null an instance that has just started. Contrast <see cref="EndStop"/>, whose condition
    /// guards against a later edit rather than against the body as it stands.
    /// </remarks>
    public void EndStart() => LeavePhase(TransitionPhase.Starting);

    /// <summary>
    /// Takes the instance out of <see cref="Current"/> and enters the stop window together, so the
    /// target never holds no instance while raising no phase.
    /// </summary>
    /// <remarks>Stays one write, for the reason on <see cref="BeginStart"/>.</remarks>
    public void BeginStop() => Volatile.Write(ref _snapshot, StoppingSnapshot);

    /// <summary>
    /// Leaves the stop window, and leaves the snapshot alone for a stop body that never entered it.
    /// </summary>
    /// <remarks>
    /// Stays one write, for the reason on <see cref="BeginStart"/>. The condition changes nothing for
    /// the stop body as it stands: the one exit above the phase change is the return taken because the
    /// target held no instance, the chain runs no other body meanwhile, so an unconditional settle
    /// there would write the value already in the field. It is conditional so that this method is
    /// correct on its own rather than on what precedes it, because a guard added above the instance
    /// read later would otherwise make it a settle over a live instance and nothing would say so.
    /// Contrast <see cref="EndStart"/>, whose condition is load bearing as the start body stands.
    /// </remarks>
    public void EndStop() => LeavePhase(TransitionPhase.Stopping);

    /// <summary>
    /// Settles the snapshot while <paramref name="phase"/> is still the one in flight. A plain read
    /// modify write rather than a compare and exchange: the chain serializes the bodies for one target,
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
    /// What the target is doing right now, derived from the one snapshot the transitions write rather
    /// than stored, so <see cref="HostedServiceAttachmentState.Running"/> and a non null
    /// <see cref="Current"/> can never disagree.
    /// </summary>
    /// <remarks>
    /// Two steps of the precedence are not arbitrary. <see cref="HostedServiceAttachmentState.Removed"/>
    /// outranks <see cref="HostedServiceAttachmentState.Faulted"/> because the mark is terminal for the
    /// attachment while the fault is not: every start appended after it is refused, and the one start
    /// that can still run, one queued ahead of the mark, clears the fault on its way past. Ranked the
    /// other way a target nothing can attach to again would read as the recoverable one of the two.
    /// <see cref="HostedServiceAttachmentState.Stopping"/> outranks
    /// <see cref="HostedServiceAttachmentState.Removed"/> because an explicit detach marks detached and
    /// then appends its stop, so both hold while that stop runs and "still shutting down" is the more
    /// urgent of the two; it settles into <see cref="HostedServiceAttachmentState.Removed"/>.
    /// </remarks>
    public HostedServiceAttachmentState State
    {
        get
        {
            // The instance and the phase come out of this one load, which is what the property rests
            // on: they are written together, so no transition can land between them. This is not the
            // only load the property takes, and the other two below are not covered by that argument.
            var snapshot = Volatile.Read(ref _snapshot);

            return snapshot.Instance is not null ? HostedServiceAttachmentState.Running
                : snapshot.Phase is TransitionPhase.Starting ? HostedServiceAttachmentState.Starting
                : snapshot.Phase is TransitionPhase.Stopping ? HostedServiceAttachmentState.Stopping
                // The two loads below run only on a settled snapshot, so neither can contradict a
                // transition in flight: between them they choose among Removed, Faulted and Stopped,
                // and a reading that is one moment old is one of those three rather than a phase.
                //
                // Read outside _sync, unlike everywhere else: a poll must not queue behind an append,
                // and the mark is one way, so the worst a racing read can do is report the state one
                // moment older.
                : Volatile.Read(ref _detached) ? HostedServiceAttachmentState.Removed
                : Fault is not null ? HostedServiceAttachmentState.Faulted
                : HostedServiceAttachmentState.Stopped;
        }
    }

    /// <summary>
    /// Permanently refuses every start appended after this, and none already queued. Under the chain
    /// lock, so a detach that marks here before appending its stop leaves an appended start either
    /// refused or ordered ahead of that stop.
    /// </summary>
    public void MarkDetached()
    {
        lock (_sync)
        {
            _detached = true;
        }
    }

    /// <summary>
    /// Records the instance the factory produced and reports whether it differs from the previous one.
    /// Unsynchronized because only start bodies call it and the chain serializes them. Never cleared:
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
    /// Takes ownership and records the target on the handler. Finding this handler already installed is
    /// also success; <paramref name="ownershipTaken"/> tells the two apart, which a caller that may undo
    /// its own take but must leave an earlier one alone needs.
    /// </summary>
    /// <remarks>
    /// The record is written on the install only, so membership can be gained through nothing but a take
    /// that has an undo behind it.
    /// </remarks>
    public bool TryTakeOwnership(HostedServiceHandler handler, IInterceptorSubject subject, out bool ownershipTaken)
    {
        lock (_ownershipSync)
        {
            var previous = Interlocked.CompareExchange(ref _owner, handler, null);
            ownershipTaken = previous is null;

            if (ownershipTaken)
            {
                handler.RecordOwnership(this, subject);
            }

            return ownershipTaken || ReferenceEquals(previous, handler);
        }
    }

    /// <summary>Releases an ownership this handler installed and retires its record.</summary>
    /// <remarks>
    /// The record is retired only when the exchange matched, because the record and <c>_owner</c> are
    /// one fact: a release that matched nothing released nothing.
    /// </remarks>
    public void ReleaseOwnership(HostedServiceHandler handler)
    {
        lock (_ownershipSync)
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _owner, null, handler), handler))
            {
                handler.ForgetOwnership(this);
            }
        }
    }

    /// <summary>
    /// Appends a transition counted against <paramref name="handler"/>, so its drain waits for it, and
    /// returns a task completing when it has run. Appending never blocks and never runs the body, so
    /// callers may append while holding a lock.
    /// </summary>
    public Task AppendAsync(HostedServiceHandler handler, Func<Task> body)
    {
        lock (_sync)
        {
            return AppendCore(body, handler);
        }
    }

    /// <summary>Appends a transition no drain waits for.</summary>
    /// <remarks>
    /// Test only. Every production append is attributed, or shutdown returns while the transition is
    /// still about to touch a service provider the host is disposing, and the body keeps the appending
    /// flow's ambient startup scope, which <see cref="RunAsync"/> clears only for an attributed one.
    /// </remarks>
    public Task AppendAsync(Func<Task> body)
    {
        lock (_sync)
        {
            return AppendCore(body, handler: null);
        }
    }

    /// <summary>
    /// Appends a transition only while <paramref name="handler"/> still owns the target, both decided
    /// under one acquisition of the chain lock. Returns null when ownership has moved on, in which case
    /// nothing was appended.
    /// </summary>
    /// <remarks>
    /// The drain snapshots what it owns while holding nothing and appends afterwards, so deciding
    /// outside this lock lets one host's drain stop and dispose an instance a second host started.
    /// </remarks>
    public Task? AppendIfOwnedAsync(HostedServiceHandler handler, Func<Task> body)
    {
        lock (_sync)
        {
            return ReferenceEquals(Owner, handler) ? AppendCore(body, handler) : null;
        }
    }

    /// <summary>
    /// Confirms the subject is still live for <paramref name="handler"/>, takes ownership and appends
    /// the transition, all under one acquisition of the chain lock. Returns null when the subject is
    /// no longer live or another handler owns the target, in which case nothing was appended and no
    /// ownership was taken. <paramref name="ownershipTaken"/> is as on <see cref="TryTakeOwnership"/>.
    /// </summary>
    /// <remarks>
    /// The three steps must stay under one acquisition. Splitting them deadlocks a start against the
    /// stops a context detach appends under this same lock, and leaves a start able to outlive an
    /// explicit detach. Worked through in
    /// docs/design/hosting-service-ownership.md#the-read-inside-the-chain-lock.
    /// </remarks>
    public Task? TryTakeOwnershipAndAppendAsync(
        HostedServiceHandler handler,
        IInterceptorSubject subject,
        Func<Task> body,
        out bool ownershipTaken)
    {
        ownershipTaken = false;

        lock (_sync)
        {
            if (_detached || !handler.IsLive(subject))
            {
                return null;
            }

            handler.LivenessReadGate?.Invoke();

            if (!TryTakeOwnership(handler, subject, out ownershipTaken))
            {
                return null;
            }

            if (!handler.IsLive(subject))
            {
                // A detach clears liveness, then reads Owner and releases, the last two outside this
                // lock, so a take landing after that release is one it never saw and never releases.
                //
                // Undone inside the lock, not after it: ReleaseOwnership matches on the handler rather
                // than on the take, so outside the lock the undo destroys an ownership a concurrent
                // re-attach installed. Only for an ownership this call installed: an earlier take's is
                // the detach's to release, having read Owner as non-null.
                if (ownershipTaken)
                {
                    ReleaseOwnership(handler);
                    ownershipTaken = false;
                }

                return null;
            }

            ChainLockGate?.Invoke();

            return AppendCore(body, handler);
        }
    }

    private Task AppendCore(Func<Task> body, HostedServiceHandler? handler)
    {
        // Before the append, never after: on an already completed tail the continuation runs and
        // decrements before the next statement here executes, which takes the count negative. Nothing
        // between here and the append may throw, because a leaked increment never comes back.
        handler?.EnterTransition();

        // The lock the callers hold is required: "_tail = _tail.ContinueWith(...)" is a
        // read-modify-write, and two racing appenders lose an assignment and run both transitions
        // concurrently. TaskScheduler.Default is required: ContinueWith otherwise captures
        // TaskScheduler.Current, which can be a scheduler the appending task is itself occupying.
        _tail = _tail
            .ContinueWith(
                _ => RunAsync(body, handler),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default)
            .Unwrap();

        return _tail;
    }

    private async Task RunAsync(Func<Task> body, HostedServiceHandler? handler)
    {
        // A body inherits the execution context of the flow that appended it, startup scope included.
        // A stop body does not wait for that scope, so an attach it makes would be captured by a scope
        // belonging to a caller it has nothing to do with and park until that caller closes it.
        handler?.ClearAmbientStartupScope();

        // Bodies never throw. A faulted tail would raise UnobservedTaskException for every dropped
        // fire and forget transition and would be retained until the target transitions again.
        try
        {
            if (TransitionGate is { } gate)
            {
                await gate().ConfigureAwait(false);
            }

            await body().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Handled by the body itself, which records into Fault and logs.
        }
        finally
        {
            // In the finally rather than after the catch, which covers the body alone: the gate above
            // is inside the same count.
            handler?.LeaveTransition();
        }
    }
}
