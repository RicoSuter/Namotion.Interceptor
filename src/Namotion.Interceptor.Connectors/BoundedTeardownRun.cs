using System.Runtime.ExceptionServices;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Runs a cancellable core task and guarantees the caller is released within a fixed bound once the core
/// begins finalizing, whatever the core or its handlers do afterwards.
/// </summary>
/// <remarks>
/// Deliberately knows nothing about what the core does. The two properties it exists to hold are both
/// about time:
/// <list type="bullet">
/// <item>The bound arms when finalization <em>begins</em>, which the core reports through
/// <see cref="MarkFinalizationStarted()"/>, not only when the caller stops. A core that starts finalizing
/// because it faulted would otherwise wait on a blocking handler with no deadline at all.</item>
/// <item>Work abandoned at the bound is never waited on again, but it is still observed, so an abandoned
/// task cannot surface later as an unobserved exception and its token sources are disposed once it
/// actually settles.</item>
/// </list>
/// One run per instance: the tokens and the finalization mark are this run's state.
/// </remarks>
internal sealed class BoundedTeardownRun
{
    private readonly CancellationTokenSource _processingTokenSource = new();
    private readonly CancellationTokenSource _teardownTokenSource = new();
    private readonly TaskCompletionSource<ExceptionDispatchInfo?> _finalizationStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _teardownBound;

    public BoundedTeardownRun(TimeSpan teardownBound)
    {
        _teardownBound = teardownBound;
    }

    /// <summary>Cancelled first when the run is stopping; the core's steady-state work observes this.</summary>
    public CancellationToken ProcessingToken => _processingTokenSource.Token;

    /// <summary>Cancelled only at the bound; the core's final flush and handoff observe this.</summary>
    public CancellationToken TeardownToken => _teardownTokenSource.Token;

    /// <summary>Reports that the core is entering ordinary finalization. First report wins.</summary>
    public void MarkFinalizationStarted() => _finalizationStarted.TrySetResult(null);

    /// <summary>
    /// Reports that a fault pushed the core into finalization. The fault is rethrown to the runner's
    /// caller if the bound expires, so it is not lost when the core never gets to complete.
    /// </summary>
    public void MarkFinalizationStarted(Exception fault) =>
        _finalizationStarted.TrySetResult(ExceptionDispatchInfo.Capture(fault));

    /// <summary>
    /// Runs the core until it completes, or until <paramref name="stopSignal"/> or a reported
    /// finalization start opens the teardown window and the bound expires.
    /// </summary>
    /// <param name="core">The work to run. Reads this run's tokens and reports its finalization start.</param>
    /// <param name="stopSignal">
    /// Completes when the caller asks to stop. A task rather than a token, because observing a token's
    /// wait handle can throw and the caller has to establish that before the run.
    /// </param>
    /// <returns>
    /// Whether the core was abandoned at the bound, and the fault that pushed it into finalization when
    /// one was reported. The caller settles ownership of whatever an abandoned core still holds, then
    /// rethrows the fault; a core that completes in time propagates its exception from the await instead.
    /// </returns>
    public async Task<BoundedTeardownOutcome> RunAsync(Func<Task> core, Task stopSignal)
    {
        var coreTask = Task.Run(core, CancellationToken.None);

        var completedTask = await Task.WhenAny(coreTask, _finalizationStarted.Task, stopSignal).ConfigureAwait(false);
        if (completedTask == coreTask)
        {
            // Nothing was abandoned, so the sources are dead and can be released inline.
            try { await coreTask.ConfigureAwait(false); }
            finally
            {
                _processingTokenSource.Dispose();
                _teardownTokenSource.Dispose();
            }

            return BoundedTeardownOutcome.Completed;
        }

        var processingCancellationTask = _processingTokenSource.CancelAsync();
        var teardownCancellationTask = Task.CompletedTask;
        using var boundCancellation = new CancellationTokenSource();
        var boundExpiry = Task.Delay(_teardownBound, boundCancellation.Token);
        try
        {
            if (await Task.WhenAny(coreTask, boundExpiry).ConfigureAwait(false) == coreTask)
            {
                await boundCancellation.CancelAsync().ConfigureAwait(false);
                await coreTask.ConfigureAwait(false);
                return BoundedTeardownOutcome.Completed;
            }

            teardownCancellationTask = _teardownTokenSource.CancelAsync();

            // The fault surfaces only when finalization began on its own: a caller-requested stop has
            // no fault to raise, and abandonment at the bound is its documented outcome, not an error.
            return new BoundedTeardownOutcome(
                abandonedAtBound: true,
                fault: completedTask == _finalizationStarted.Task
                    ? await _finalizationStarted.Task.ConfigureAwait(false)
                    : null);
        }
        finally
        {
            ObserveInBackground(coreTask, processingCancellationTask, teardownCancellationTask,
                _processingTokenSource, _teardownTokenSource);
        }
    }

    // Nothing awaits this: the point of the bound is that the caller has already been released.
    private static void ObserveInBackground(
        Task coreTask,
        Task processingCancellationTask,
        Task teardownCancellationTask,
        CancellationTokenSource processingTokenSource,
        CancellationTokenSource teardownTokenSource)
    {
        _ = Task.WhenAll(coreTask, processingCancellationTask, teardownCancellationTask).ContinueWith(
            task =>
            {
                _ = task.Exception;
                processingTokenSource.Dispose();
                teardownTokenSource.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

/// <summary>How a <see cref="BoundedTeardownRun"/> ended, for the caller to settle and rethrow.</summary>
internal readonly struct BoundedTeardownOutcome
{
    public static readonly BoundedTeardownOutcome Completed = default;

    public BoundedTeardownOutcome(bool abandonedAtBound, ExceptionDispatchInfo? fault)
    {
        AbandonedAtBound = abandonedAtBound;
        Fault = fault;
    }

    /// <summary>The core was still finalizing at the bound and will never be waited on again.</summary>
    public bool AbandonedAtBound { get; }

    /// <summary>The fault that pushed the core into finalization, when it was abandoned before completing.</summary>
    public ExceptionDispatchInfo? Fault { get; }
}
