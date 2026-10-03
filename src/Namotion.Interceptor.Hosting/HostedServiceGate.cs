namespace Namotion.Interceptor.Hosting;

internal enum HostedServiceGateState
{
    NotStarted,
    Running,
    Draining
}

/// <summary>
/// Startup and shutdown gate for hosted service transitions. The state only ever moves forward:
/// NotStarted to Running to Draining, or NotStarted straight to Draining when a host is stopped
/// without having started. Draining is final, so it also covers a drain that has returned.
/// </summary>
internal sealed class HostedServiceGate
{
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _draining = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HostedServiceGateState _state = HostedServiceGateState.NotStarted;

    // An interlocked read rather than a volatile one: the checks that write and then re-read the gate
    // need a full fence between the two, which an acquire load does not give.
    public HostedServiceGateState State => Interlocked.CompareExchange(ref _state, default, default);

    /// <summary>Whether the drain has begun.</summary>
    public bool IsDraining => State == HostedServiceGateState.Draining;

    /// <summary>
    /// Advances NotStarted to Running. A one way ratchet: calling this during shutdown must not
    /// reopen the gate, or a detach arriving mid drain would let queued starts run again.
    /// </summary>
    public void EnsureStarted()
    {
        if (Interlocked.CompareExchange(ref _state, HostedServiceGateState.Running, HostedServiceGateState.NotStarted)
            == HostedServiceGateState.NotStarted)
        {
            _opened.TrySetResult();
        }
    }

    public void BeginDraining()
    {
        Interlocked.Exchange(ref _state, HostedServiceGateState.Draining);

        // Releases anything parked on a gate that was never opened, so a host that aborts startup
        // does not leave transitions and their awaiters hanging forever.
        _opened.TrySetResult();
        _draining.TrySetResult();
    }

    /// <summary>
    /// Completes once the gate has left <see cref="HostedServiceGateState.NotStarted"/>. Callers must
    /// then read <see cref="State"/> and decide what to do; the wait itself carries no verdict.
    /// </summary>
    // Untokened, here and below: BeginDraining completes both unconditionally, so nothing parked on
    // either outlives the start of shutdown, and both are awaited inside transition bodies, where a
    // caller's token must never abort a start already creating an instance.
    public Task WaitForOpenAsync() => _opened.Task;

    /// <summary>
    /// Completes once the drain has begun. Lets a transition parked on something a caller controls,
    /// which is a startup scope and nothing else, be released by shutdown instead of holding the
    /// drain's barrier for the whole shutdown deadline. Continuations run asynchronously, so what was
    /// parked cannot resume on the thread that begins the drain.
    /// </summary>
    public Task WaitForDrainingAsync() => _draining.Task;
}
