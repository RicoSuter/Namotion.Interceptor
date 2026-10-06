using Namotion.Interceptor.Tracking;

namespace HomeBlaze.Services;

/// <summary>
/// Completes once the root subject has loaded and every startup deferral is released, so the subject tree
/// has settled: queued hosted service starts ran, storages scanned their files, plugin providers finished
/// their initial load and placeholders of plugin types were upgraded.
/// </summary>
/// <remarks>
/// Registered on the subject context before the root graph is built (see <see cref="SubjectContextFactory"/>),
/// so every <see cref="IStartupCompletion"/> deferral taken while the tree starts counts towards it. Once
/// completed it never reopens. It faults with the load exception, or is cancelled, when the root fails to
/// load, so waiters do not hang on a tree that will never appear.
/// </remarks>
public sealed class StartupGate : IStartupCompletion
{
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Starts with the root load's hold, so the gate cannot complete before the subject tree exists.
    private int _deferrals = 1;
    private int _isRootLoadReported;

    /// <summary>
    /// Completes once startup has settled. Faults or is cancelled when the root fails to load.
    /// </summary>
    public Task Completed => _completed.Task;

    /// <inheritdoc />
    public IDisposable Defer()
    {
        // Hosted service attaches keep deferring after startup; they need no counted handle then.
        if (_completed.Task.IsCompleted)
        {
            return NoOpDeferral.Instance;
        }

        Interlocked.Increment(ref _deferrals);
        return new Deferral(this);
    }

    /// <summary>
    /// Releases the hold the gate is created with. Called once the root graph is attached. Idempotent.
    /// </summary>
    public void CompleteRootLoad()
    {
        if (Interlocked.Exchange(ref _isRootLoadReported, 1) == 0)
        {
            Release();
        }
    }

    /// <summary>
    /// Faults the gate with <paramref name="exception"/>, or cancels it for an <see cref="OperationCanceledException"/>,
    /// because the root failed to load. Has no effect once the root load was reported.
    /// </summary>
    public void FailRootLoad(Exception exception)
    {
        if (Interlocked.Exchange(ref _isRootLoadReported, 1) == 0)
        {
            if (exception is OperationCanceledException canceledException)
            {
                _completed.TrySetCanceled(canceledException.CancellationToken);
            }
            else
            {
                _completed.TrySetException(exception);
            }
        }
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _deferrals) == 0)
        {
            _completed.TrySetResult();
        }
    }

    private sealed class Deferral(StartupGate gate) : IDisposable
    {
        private int _isDisposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
