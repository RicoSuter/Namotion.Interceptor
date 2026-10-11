namespace HomeBlaze.AI.Agents;

/// <summary>
/// Runs background turns when <see cref="AgentTriggerPolicy"/> says they are due. Requests may arrive from any
/// thread; runs never overlap, and requests during a run coalesce into one follow-up run.
/// </summary>
public sealed class AgentTriggerScheduler
{
    // Task.Delay rejects delays above about 49.7 days; the 1-day cap keeps long waits within that and re-checked.
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromDays(1);

    private readonly TimeProvider _timeProvider;
    private readonly Func<string, CancellationToken, Task> _runBackground;
    private readonly Lock _lock = new();
    private readonly AgentTriggerPolicy _policy;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="timeProvider">The clock.</param>
    /// <param name="pollInterval">The poll interval, null for none.</param>
    /// <param name="minRunInterval">The minimum time between the starts of two automatic runs.</param>
    /// <param name="runBackground">Runs one background turn; must handle its own failures and report them.</param>
    public AgentTriggerScheduler(
        TimeProvider timeProvider,
        TimeSpan? pollInterval,
        TimeSpan minRunInterval,
        Func<string, CancellationToken, Task> runBackground)
    {
        _timeProvider = timeProvider;
        _runBackground = runBackground;
        _policy = new AgentTriggerPolicy(pollInterval, minRunInterval, timeProvider.GetUtcNow());
    }

    /// <summary>Why automatic runs are paused, or null.</summary>
    public string? PauseReason
    {
        get
        {
            lock (_lock)
            {
                return _policy.PauseReason;
            }
        }
    }

    /// <summary>Applies new intervals; see <see cref="AgentTriggerPolicy.Reconfigure"/>.</summary>
    public void Reconfigure(TimeSpan? pollInterval, TimeSpan minRunInterval)
    {
        lock (_lock)
        {
            _policy.Reconfigure(pollInterval, minRunInterval, _timeProvider.GetUtcNow());
            Wake();
        }
    }

    /// <summary>Requests an automatic run; a pending request keeps its first reason.</summary>
    public void RequestRun(string reason)
    {
        lock (_lock)
        {
            _policy.RequestRun(reason);
            Wake();
        }
    }

    /// <summary>Requests a run that ignores cooldown, backoff and pauses.</summary>
    public void RequestManualRun()
    {
        lock (_lock)
        {
            _policy.RequestManualRun();
            Wake();
        }
    }

    /// <summary>Reports a successful turn, which resets backoff and lifts every pause.</summary>
    public void ReportSuccess()
    {
        lock (_lock)
        {
            _policy.OnRunSucceeded();
            // A chat turn may report outside a run and unblock a pending request the loop is not waiting for.
            Wake();
        }
    }

    /// <summary>Reports a failed turn, which applies backoff or a pause.</summary>
    public void ReportFailure(AgentTurnException exception)
    {
        lock (_lock)
        {
            _policy.OnRunFailed(_timeProvider.GetUtcNow(), exception.Kind, exception.RetryAfter, exception.Message);
            Wake();
        }
    }

    /// <summary>
    /// Runs until cancelled. Throws <see cref="OperationCanceledException"/> on cancellation.
    /// </summary>
    /// <remarks>An exception thrown by the run callback ends the loop and propagates from the returned task.</remarks>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? reason = null;
            DateTimeOffset? wakeAt;
            Task wakeSignal;
            lock (_lock)
            {
                var now = _timeProvider.GetUtcNow();
                if (_policy.TryTakeDueRun(now, out var dueReason))
                {
                    reason = dueReason;
                }

                wakeAt = _policy.GetNextWakeTime(now);

                // Captured under the lock together with the policy state, so a request after this point completes
                // this very task; a completed signal only means state changed before it was read here.
                if (_wake.Task.IsCompleted)
                {
                    _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                wakeSignal = _wake.Task;
            }

            if (reason is not null)
            {
                await _runBackground(reason, cancellationToken);
                continue;
            }

            await WaitAsync(wakeSignal, wakeAt, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task WaitAsync(Task wakeSignal, DateTimeOffset? wakeAt, CancellationToken cancellationToken)
    {
        if (wakeAt is null)
        {
            await wakeSignal.WaitAsync(cancellationToken);
            return;
        }

        var delay = wakeAt.Value - _timeProvider.GetUtcNow();
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        if (delay > MaximumDelay)
        {
            delay = MaximumDelay;
        }

        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(delay, _timeProvider, delayCancellation.Token);
        await Task.WhenAny(wakeSignal, delayTask).WaitAsync(cancellationToken);
        await delayCancellation.CancelAsync();
    }

    // Callers hold the lock; completing under it is safe because continuations run asynchronously.
    private void Wake() => _wake.TrySetResult();
}
