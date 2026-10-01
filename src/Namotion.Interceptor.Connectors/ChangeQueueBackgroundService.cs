using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// A hosted service that consumes property changes through a <see cref="ChangeQueueProcessor"/>. The change
/// subscription is created in <see cref="StartAsync"/>, so a change made after the host start returns is
/// delivered even though the execution may run later, and it outlives the processors: <see cref="RequestRestart"/>
/// and a retry after a fault run <see cref="ProcessAsync"/> again with a new processor from
/// <see cref="CreateProcessor"/> on the same subscription, so changes made in between are delivered by that
/// processor, which skips a change a later write superseded as at any start. The subscription is released while
/// the service is idle, after each fault from the third consecutive one on (the next run subscribes again), and on
/// stop, where undelivered changes are dropped. The service disposes the processors it
/// created on every exit path, including a restart.
/// </summary>
/// <remarks>
/// A further <see cref="StartAsync"/> is supported only once the previous <see cref="BackgroundService.ExecuteTask"/>
/// has completed. A <see cref="StopAsync"/> that returned because its cancellation token fired first does not
/// guarantee that, and a restart requested after such a start may be lost.
/// </remarks>
public abstract class ChangeQueueBackgroundService : BackgroundService
{
    // Bounds what accumulates during a persistent fault: the subscription is released once this many runs
    // have faulted in a row and is created again for the next run.
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
    /// Creates the processor that consumes <paramref name="subscription"/>, which the service owns and keeps
    /// across restarts and retries; build it with the <see cref="ChangeQueueProcessor"/> constructor that takes a
    /// subscription. A processor on any other subscription is rejected. Called from <see cref="StartAsync"/> on
    /// the host start path and from the execution on a restart or retry, so it must not block or perform I/O; an
    /// exception fails the start or, during execution, is logged and retried after <see cref="GetRetryDelay"/>.
    /// The previous processor is disposed before this is called.
    /// </summary>
    protected abstract ChangeQueueProcessor CreateProcessor(PropertyChangeQueueSubscription subscription);

    /// <summary>
    /// Consumes the given processor until <paramref name="stoppingToken"/> is cancelled, which a stop and
    /// <see cref="RequestRestart"/> both do; an <see cref="OperationCanceledException"/> thrown after that
    /// cancellation counts as a return. Any other exception is logged and retried after <see cref="GetRetryDelay"/>.
    /// The default drains the processor with <see cref="ChangeQueueProcessor.ProcessAsync"/> until the token is
    /// cancelled. Override it to set up state before draining, tear it down after, or run work alongside it. The
    /// processor is disposed when this returns. Returning before the cancellation leaves the service idle, without
    /// a processor or a subscription, until a restart is requested or it stops. The service owns the processor;
    /// implementations must not dispose it.
    /// </summary>
    protected virtual Task ProcessAsync(ChangeQueueProcessor processor, CancellationToken stoppingToken) =>
        processor.ProcessAsync(stoppingToken);

    /// <summary>
    /// Returns the delay before a new processor is created after <see cref="ProcessAsync"/> or
    /// <see cref="CreateProcessor"/> throws during execution, five seconds by default. Called after the failed
    /// processor is disposed; the delay is waited unless a restart or stop comes first.
    /// </summary>
    /// <returns>
    /// A nonnegative delay supported by <see cref="Task.Delay(TimeSpan, CancellationToken)"/>. A negative delay
    /// faults the execution.
    /// </returns>
    protected virtual TimeSpan GetRetryDelay(Exception exception) => TimeSpan.FromSeconds(5);

    /// <summary>
    /// Cancels the token given to <see cref="ProcessAsync"/>, disposes its processor and runs
    /// <see cref="ProcessAsync"/> again with a new processor from <see cref="CreateProcessor"/>; a
    /// <see cref="ProcessAsync"/> that has already returned is run again the same way. Requests made before the
    /// restart begins coalesce into one, and a request made while the service is stopped is served by the next
    /// start. A failure during the restart is logged and retried after <see cref="GetRetryDelay"/>.
    /// </summary>
    protected void RequestRestart()
    {
        // Full fence before the wake is read, mirroring the publish order in ExecuteAsync: a request that races
        // the publish is then either seen by the flag check there or completes the published wake here.
        Interlocked.Exchange(ref _restartRequested, 1);
        Volatile.Read(ref _restartWake)?.TrySetResult();
    }

    /// <inheritdoc />
    public sealed override Task StartAsync(CancellationToken cancellationToken)
    {
        // A request made while stopped is served by this fresh start.
        Interlocked.Exchange(ref _restartRequested, 0);

        // Released before the next is created, so two subscriptions never overlap and a failed creation leaves none.
        ReleaseStart();

        // Not in ExecuteAsync: since .NET 10 it may run after StartAsync returns, and changes made in between
        // would never reach the subscription. Every other writer stores null, so this overwrites nothing, and the
        // dispatch in base.StartAsync orders it before the execution takes it.
        var subscription = Context.CreatePropertyChangeQueueSubscription();
        try
        {
            Volatile.Write(ref _startProcessor, CreateProcessorOn(subscription));
        }
        catch
        {
            subscription.Dispose();
            throw;
        }

        return base.StartAsync(cancellationToken);
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
                    // Idle until a restart: nothing would drain the subscription, so nothing may accumulate in it.
                    Release(ref subscription);
                }

                await WaitForRestartAsync(wake.Task, retryDelay, stoppingToken).ConfigureAwait(false);

                // Consumed before the processor is created, so a request made from here on restarts once more.
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
            // Nothing would drain the service's subscription, so it would grow without bound.
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
            // The session was ended by a restart or stop; an implementation may report that by throwing.
        }
    }

    private static async Task WaitForRestartAsync(Task wake, TimeSpan? retryDelay, CancellationToken stoppingToken)
    {
        if (retryDelay is not { } delay)
        {
            // A normal return stays idle until explicitly restarted; nothing would consume a new processor.
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
            // A stop can outrun the dispatch of the execution, and a graph detach stops without disposing.
            ReleaseStart();
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        // A start not followed by a stop leaves the processor here if the execution has not taken it yet.
        base.Dispose();
        ReleaseStart();
    }

    // The processor left by a start that no execution took, together with the subscription it was built on.
    private void ReleaseStart()
    {
        if (Interlocked.Exchange(ref _startProcessor, null) is { } processor)
        {
            processor.Dispose();
            processor.Subscription.Dispose();
        }
    }
}
