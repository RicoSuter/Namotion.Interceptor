using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// A hosted service that consumes property changes through a <see cref="ChangeQueueProcessor"/>. The change
/// subscription is created in <see cref="StartAsync"/>, so a change made after the host start returns is delivered
/// even if the execution runs later. A restart or a retry after a fault runs <see cref="ProcessAsync"/> again with a
/// new processor on the same subscription, which delivers the changes made in between except those a later write
/// superseded. The subscription is released while the service is idle, after each fault from the third consecutive
/// one on (the next run subscribes again), and on stop, where undelivered changes are dropped.
/// </summary>
/// <remarks>
/// Calling <see cref="StartAsync"/> while the previous <see cref="BackgroundService.ExecuteTask"/> is still running,
/// as it can be after a <see cref="StopAsync"/> ended by its cancellation token, is unsupported: a restart requested
/// afterwards may be lost, and the extra subscription and processor may stay unused until stop or dispose.
/// </remarks>
public abstract class ChangeQueueBackgroundService : BackgroundService
{
    // Bounds what accumulates in the subscription while runs keep faulting.
    private const int FaultsBeforeRelease = 3;

    private readonly ILogger _logger;
    private ChangeQueueProcessor? _startProcessor;
    private TaskCompletionSource? _restartWake;
    private int _restartRequested;

    /// <summary>
    /// Initializes the service with the logger that receives each fault it retries.
    /// </summary>
    protected ChangeQueueBackgroundService(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// The context whose property changes the service subscribes to.
    /// </summary>
    protected abstract IInterceptorSubjectContext Context { get; }

    /// <summary>
    /// Creates the processor that consumes <paramref name="subscription"/>, which the service owns, using the
    /// <see cref="ChangeQueueProcessor"/> constructor that takes a subscription; a processor on any other
    /// subscription is rejected. Called from <see cref="StartAsync"/> and, after the previous processor is disposed,
    /// on each restart or retry, so it must not block or perform I/O. An exception fails the start or, during
    /// execution, is logged and retried after <see cref="GetRetryDelay"/>.
    /// </summary>
    protected abstract ChangeQueueProcessor CreateProcessor(PropertyChangeQueueSubscription subscription);

    /// <summary>
    /// Consumes <paramref name="processor"/> until <paramref name="cancellationToken"/> is cancelled by a stop or
    /// <see cref="RequestRestart"/>; an <see cref="OperationCanceledException"/> after that cancellation counts as a
    /// return. Any other exception is logged and retried after <see cref="GetRetryDelay"/>. Returning before the
    /// cancellation leaves the service idle, without a processor or a subscription, until a restart is requested or
    /// it stops. The service owns the processor and disposes it when this returns; implementations must not dispose
    /// it. The default drains it with <see cref="ChangeQueueProcessor.ProcessAsync"/>.
    /// </summary>
    protected virtual Task ProcessAsync(ChangeQueueProcessor processor, CancellationToken cancellationToken) =>
        processor.ProcessAsync(cancellationToken);

    /// <summary>
    /// Returns the delay before a new processor is created after <see cref="ProcessAsync"/> or
    /// <see cref="CreateProcessor"/> throws during execution, five seconds by default. Called after the failed
    /// processor is disposed; a restart or stop ends the delay early.
    /// </summary>
    /// <returns>
    /// A nonnegative delay supported by <see cref="Task.Delay(TimeSpan, CancellationToken)"/>. A negative delay
    /// faults the execution.
    /// </returns>
    protected virtual TimeSpan GetRetryDelay(Exception exception) => TimeSpan.FromSeconds(5);

    /// <summary>
    /// Cancels the token given to <see cref="ProcessAsync"/>, or wakes the service if it has returned, and runs
    /// <see cref="ProcessAsync"/> again with a new processor. Requests made before the restart begins coalesce into
    /// one. A request made while the service is stopped has no further effect, since the next start creates a new
    /// processor anyway. A failure during the restart is logged and retried after <see cref="GetRetryDelay"/>.
    /// </summary>
    protected void RequestRestart()
    {
        // Full fence before the wake is read, paired with ExecuteAsync's publish then flag read: a racing request is
        // seen by one of the two sides.
        Interlocked.Exchange(ref _restartRequested, 1);
        Volatile.Read(ref _restartWake)?.TrySetResult();
    }

    /// <inheritdoc />
    public sealed override Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _restartRequested, 0);

        // Before the next is created, so a failed creation leaves no subscription.
        ReleaseStart();

        // Not in ExecuteAsync: since .NET 10 it may run after StartAsync returns and would miss changes made in
        // between.
        var subscription = Context.CreatePropertyChangeQueueSubscription();
        ChangeQueueProcessor processor;
        try
        {
            processor = CreateProcessorOn(subscription);
        }
        catch
        {
            subscription.Dispose();
            throw;
        }

        // Must precede base.StartAsync, whose dispatch orders it before the execution takes it.
        Volatile.Write(ref _startProcessor, processor);
        try
        {
            return base.StartAsync(cancellationToken);
        }
        catch
        {
            ReleaseStart();
            throw;
        }
    }

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processor = Interlocked.Exchange(ref _startProcessor, null);
        if (processor is null)
        {
            return;
        }

        var subscription = processor.Subscription;
        var consecutiveFaults = 0;
        try
        {
            while (true)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    processor?.Dispose();
                    return;
                }

                var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var stopRegistration = stoppingToken.UnsafeRegister(
                    static state => ((TaskCompletionSource)state!).TrySetResult(), wake);

                // Full fence before the flag is read, see RequestRestart.
                Interlocked.Exchange(ref _restartWake, wake);
                if (Volatile.Read(ref _restartRequested) != 0)
                {
                    wake.TrySetResult();
                }

                TimeSpan? retryDelay = null;
                try
                {
                    subscription ??= Context.CreatePropertyChangeQueueSubscription();
                    processor ??= CreateProcessorOn(subscription);
                    using (processor)
                    {
                        await ProcessSessionAsync(processor, wake.Task, stoppingToken).ConfigureAwait(false);
                    }

                    consecutiveFaults = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogError(exception, "Change queue processing faulted while stopping.");
                        return;
                    }

                    var delay = GetRetryDelay(exception);
                    if (delay < TimeSpan.Zero)
                    {
                        throw new InvalidOperationException(
                            $"{nameof(GetRetryDelay)} returned a negative delay ({delay}) for the fault in the inner exception.",
                            exception);
                    }

                    _logger.LogError(exception, "Change queue processing faulted; retrying in {RetryDelay}.", delay);
                    retryDelay = delay;
                    if (++consecutiveFaults >= FaultsBeforeRelease)
                    {
                        Release(ref subscription);
                    }
                }
                processor = null;

                if (retryDelay is null && !wake.Task.IsCompleted)
                {
                    // Idle: nothing drains the subscription, so it must not accumulate changes.
                    Release(ref subscription);
                }

                await WaitForRestartAsync(wake.Task, retryDelay, stoppingToken).ConfigureAwait(false);

                // Cleared before the next processor is created, so a later request restarts again.
                Interlocked.Exchange(ref _restartRequested, 0);
            }
        }
        finally
        {
            subscription?.Dispose();
        }
    }

    private ChangeQueueProcessor CreateProcessorOn(PropertyChangeQueueSubscription subscription)
    {
        var processor = CreateProcessor(subscription);
        if (!ReferenceEquals(processor.Subscription, subscription))
        {
            // Nothing would drain the service's subscription.
            processor.Dispose();
            throw new InvalidOperationException(
                $"{nameof(CreateProcessor)} must build the processor on the subscription it is given.");
        }

        return processor;
    }

    private static void Release(ref PropertyChangeQueueSubscription? subscription)
    {
        subscription?.Dispose();
        subscription = null;
    }

    private async Task ProcessSessionAsync(ChangeQueueProcessor processor, Task wake, CancellationToken stoppingToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            var processing = ProcessAsync(processor, session.Token);
            if (await Task.WhenAny(processing, wake).ConfigureAwait(false) == wake)
            {
                await session.CancelAsync().ConfigureAwait(false);
            }
            await processing.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            // Ended by a restart or stop.
        }
    }

    private static async Task WaitForRestartAsync(Task wake, TimeSpan? retryDelay, CancellationToken stoppingToken)
    {
        if (retryDelay is not { } delay)
        {
            await wake.ConfigureAwait(false);
            return;
        }

        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await Task.WhenAny(wake, Task.Delay(delay, delayCancellation.Token)).ConfigureAwait(false);
        await delayCancellation.CancelAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public sealed override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The execution may not have taken the processor yet, and a stop is not always followed by a dispose.
            ReleaseStart();
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();
        ReleaseStart();
    }

    // Releases the processor a start left that no execution took, with its subscription.
    private void ReleaseStart()
    {
        if (Interlocked.Exchange(ref _startProcessor, null) is { } processor)
        {
            processor.Dispose();
            processor.Subscription.Dispose();
        }
    }
}
