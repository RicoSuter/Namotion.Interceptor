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

    /// <summary>
    /// Test seam, invoked inside the chain lock between the take and the append. Null in production.
    /// Holds the critical section open where a split would put its gap, which is the only way an
    /// appender racing that gap is reachable from a test.
    /// </summary>
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
    /// Takes ownership. Finding this handler already installed is also success;
    /// <paramref name="ownershipTaken"/> tells the two apart, which a caller that may undo its own take
    /// but must leave an earlier one alone needs.
    /// </summary>
    public bool TryTakeOwnership(HostedServiceHandler handler, out bool ownershipTaken)
    {
        var previous = Interlocked.CompareExchange(ref _owner, handler, null);
        ownershipTaken = previous is null;
        return ownershipTaken || ReferenceEquals(previous, handler);
    }

    public void ReleaseOwnership(HostedServiceHandler handler)
        => Interlocked.CompareExchange(ref _owner, null, handler);

    /// <summary>
    /// Appends a transition and returns a task completing when it has run. Appending never blocks and
    /// never runs the body, so callers may append while holding a lock.
    /// </summary>
    public Task AppendAsync(Func<Task> body)
    {
        lock (_sync)
        {
            return AppendCore(body);
        }
    }

    /// <summary>
    /// Confirms the subject is still live for <paramref name="handler"/>, takes ownership and appends
    /// the transition, all under one acquisition of the chain lock. Returns null when the subject is
    /// no longer live or another handler owns the target, in which case nothing was appended and no
    /// ownership was taken. <paramref name="ownershipTaken"/> distinguishes an owner this call
    /// installed from one it found already installed for the same handler, which the caller must not
    /// undo: that one belongs to an earlier attach whose instance may still be running.
    /// </summary>
    /// <remarks>
    /// One critical section, because a context detach clears liveness before appending its stops under
    /// this same lock. That leaves two orders and no third: this call first, and the detach's stop lands
    /// behind a start that finds the subject dead; or the stop first, and this call appends nothing.
    /// Split them and a start lands behind an attachment stop waiting for the subject's stop, which
    /// waits for the caller awaiting this start.
    /// <para>
    /// The detached read is the same argument for an explicit detach, which clears no liveness: without
    /// it the start runs and creates an instance no later detach can reach, because the attachment it
    /// would be enumerated from is gone.
    /// </para>
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

            if (!TryTakeOwnership(handler, out ownershipTaken))
            {
                return null;
            }

            if (!handler.IsLive(subject))
            {
                // A detach clears liveness, then reads Owner and releases, and it does the last two
                // outside this lock. A take landing after that release is one the detach never saw and
                // never releases, so the target stays owned and rooted here until shutdown.
                //
                // Undone inside the lock, not after it: ReleaseOwnership matches on the handler, not on
                // the take, so outside the lock a re-attach can install a fresh ownership that the undo
                // then destroys. No install can interleave in here, because every install takes it.
                //
                // Only an ownership this call installed. An earlier take's is the detach's to release,
                // having read Owner as non-null.
                if (ownershipTaken)
                {
                    ReleaseOwnership(handler);
                    ownershipTaken = false;
                }

                return null;
            }

            ChainLockGate?.Invoke();

            return AppendCore(body);
        }
    }

    private Task AppendCore(Func<Task> body)
    {
        // The lock the callers hold is required: "_tail = _tail.ContinueWith(...)" is a
        // read-modify-write, and two racing appenders lose an assignment and run both transitions
        // concurrently. TaskScheduler.Default is required: ContinueWith otherwise captures
        // TaskScheduler.Current, which can be a scheduler the appending task is itself occupying.
        _tail = _tail
            .ContinueWith(
                _ => RunAsync(body),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default)
            .Unwrap();

        return _tail;
    }

    private async Task RunAsync(Func<Task> body)
    {
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
            // Handled by the body itself, which records into Fault and logs. This catch only
            // guarantees the chain stays unfaulted.
        }
    }
}
