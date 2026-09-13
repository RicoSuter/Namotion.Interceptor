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

    private Task _tail = Task.CompletedTask;
    private IHostedService? _current;
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

    public IHostedService? Current => Volatile.Read(ref _current);

    public Exception? Fault => Volatile.Read(ref _fault);

    public HostedServiceHandler? Owner => Volatile.Read(ref _owner);

    public void SetCurrent(IHostedService? instance) => Volatile.Write(ref _current, instance);

    public void SetFault(Exception? fault) => Volatile.Write(ref _fault, fault);

    /// <summary>
    /// Permanently refuses further starts. Under the chain lock, so a detach that marks here before
    /// appending its stop leaves an appended start either refused or ordered ahead of that stop.
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
