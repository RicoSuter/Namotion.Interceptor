using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Hosting;

/// <summary>
/// One managed thing: either a subject that implements <see cref="IHostedService"/>, or a factory
/// attachment. Owns a serialized transition chain so start, stop and dispose for this target never
/// interleave, while transitions for unrelated targets run concurrently.
/// </summary>
internal sealed class HostedServiceTarget
{
    private readonly object _sync = new();

    private Task _tail = Task.CompletedTask;
    private IHostedService? _current;
    private Exception? _fault;
    private HostedServiceHandler? _owner;

    public HostedServiceTarget(Func<IHostedService>? factory, IHostedService? subject)
    {
        Factory = factory;
        Subject = subject;
    }

    /// <summary>The factory for an attachment, or null when this target is a subject.</summary>
    public Func<IHostedService>? Factory { get; }

    /// <summary>The subject when this target is a subject, or null when it is an attachment.</summary>
    public IHostedService? Subject { get; }

    /// <summary>True when the handler created the current instance and must therefore dispose it.</summary>
    public bool IsHandlerOwnedInstance => Factory is not null;

    /// <summary>
    /// Test seam, awaited at the top of every transition body. Null in production. Lets a test hold a
    /// transition so ordering and race assertions do not depend on timing.
    /// </summary>
    internal Func<Task>? TransitionGate { get; set; }

    public IHostedService? Current => Volatile.Read(ref _current);

    public Exception? Fault => Volatile.Read(ref _fault);

    public HostedServiceHandler? Owner => Volatile.Read(ref _owner);

    public void SetCurrent(IHostedService? instance) => Volatile.Write(ref _current, instance);

    public void SetFault(Exception? fault) => Volatile.Write(ref _fault, fault);

    /// <summary>
    /// Takes ownership for the given handler. Finding this handler already installed counts as
    /// success; only losing to a different handler returns false. <paramref name="ownershipTaken"/>
    /// tells the two successes apart, which matters to a caller that may have to undo its own take
    /// but must leave an earlier one alone.
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
    /// Appends a transition to this target's chain and returns a task that completes when it has run.
    /// Appending never blocks and never runs the body, so callers may append while holding a lock.
    /// </summary>
    public Task AppendAsync(Func<CancellationToken, Task> body, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            return AppendCore(body, cancellationToken);
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
    /// The three steps have to be one critical section, and the liveness read has to be the one inside
    /// it. A context detach clears liveness before it appends its stops, and it appends each stop under
    /// this same lock, so the two orders are the only ones left: this call first, and the detach's stop
    /// lands behind a start that then finds the subject dead and no-ops; or the detach's stop first, and
    /// this call reads the cleared liveness and appends nothing. Splitting them lets a start land behind
    /// an attachment stop that is waiting for the subject's own stop, which is waiting for the caller
    /// that is awaiting this start, and that cycle never resolves.
    /// </remarks>
    public Task? TryTakeOwnershipAndAppendAsync(
        HostedServiceHandler handler,
        IInterceptorSubject subject,
        Func<CancellationToken, Task> body,
        CancellationToken cancellationToken,
        out bool ownershipTaken)
    {
        ownershipTaken = false;

        lock (_sync)
        {
            if (!handler.IsLive(subject))
            {
                return null;
            }

            if (!TryTakeOwnership(handler, out ownershipTaken))
            {
                return null;
            }

            return AppendCore(body, cancellationToken);
        }
    }

    private Task AppendCore(Func<CancellationToken, Task> body, CancellationToken cancellationToken)
    {
        // The lock the callers hold is required: "_tail = _tail.ContinueWith(...)" is a
        // read-modify-write, and two racing appenders lose an assignment and run both transitions
        // concurrently. TaskScheduler.Default is required: ContinueWith otherwise captures
        // TaskScheduler.Current, which can be a scheduler the appending task is itself occupying.
        _tail = _tail
            .ContinueWith(
                _ => RunAsync(body, cancellationToken),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default)
            .Unwrap();

        return _tail;
    }

    private async Task RunAsync(Func<CancellationToken, Task> body, CancellationToken cancellationToken)
    {
        // Bodies never throw. A faulted tail would raise UnobservedTaskException for every dropped
        // fire and forget transition and would be retained until the target transitions again.
        try
        {
            if (TransitionGate is { } gate)
            {
                await gate().ConfigureAwait(false);
            }

            await body(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Handled by the body itself, which records into Fault and logs. This catch only
            // guarantees the chain stays unfaulted.
        }
    }
}
