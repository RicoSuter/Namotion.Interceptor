using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// A hosted service that consumes property changes through a <see cref="ChangeQueueProcessor"/>. The processor,
/// and with it the change subscription, is created in <see cref="StartAsync"/>, so a change made after the host
/// start returns is delivered even though the execution may run later. <see cref="RequestRestart"/> replaces the
/// processor while the service runs, and a fault is logged and retried with a new processor after
/// <see cref="GetRetryDelay"/>. The service disposes the processors it created on every exit path, including a
/// restart.
/// </summary>
/// <remarks>
/// A further <see cref="StartAsync"/> is supported only once the previous <see cref="BackgroundService.ExecuteTask"/>
/// has completed. A <see cref="StopAsync"/> that returned because its cancellation token fired first does not
/// guarantee that, and a restart requested after such a start may be lost.
/// </remarks>
public abstract class ChangeQueueBackgroundService : BackgroundService
{
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
    /// Creates the processor that subscribes to property changes. Called from <see cref="StartAsync"/> on the
    /// host start path and from the execution on a restart or retry, so it must not block or perform I/O; an
    /// exception fails the start or, during execution, is logged and retried after <see cref="GetRetryDelay"/>.
    /// A processor from an earlier start that no execution took is disposed before this is called.
    /// </summary>
    protected abstract ChangeQueueProcessor CreateProcessor();

    /// <summary>
    /// Consumes the given processor until <paramref name="stoppingToken"/> is cancelled, which a stop and
    /// <see cref="RequestRestart"/> both do; an <see cref="OperationCanceledException"/> thrown after that
    /// cancellation counts as a return. Any other exception is logged and retried after <see cref="GetRetryDelay"/>.
    /// The default drains the processor with <see cref="ChangeQueueProcessor.ProcessAsync"/> until the token is
    /// cancelled. Override it to set up state before draining, tear it down after, or run work alongside it. The
    /// processor is disposed when this returns. Returning before the cancellation leaves the service idle, without
    /// a processor, until a restart is requested or it stops. The service owns the processor; implementations must
    /// not dispose it.
    /// </summary>
    protected virtual Task ProcessAsync(ChangeQueueProcessor processor, CancellationToken stoppingToken) =>
        processor.ProcessAsync(stoppingToken);

    /// <summary>
    /// Returns the delay before a new processor is created after <see cref="ProcessAsync"/> or a restart's
    /// <see cref="CreateProcessor"/> throws, five seconds by default. Called after the failed processor is
    /// disposed; the delay is waited unless a restart or stop comes first.
    /// </summary>
    /// <returns>A nonnegative delay supported by <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</returns>
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

        // Disposed before the next is created, so two subscriptions never overlap and a failed creation leaves none.
        Interlocked.Exchange(ref _startProcessor, null)?.Dispose();

        // Not in ExecuteAsync: since .NET 10 it may run after StartAsync returns, and changes made in between
        // would never reach the processor. Every other writer stores null, so this overwrites nothing, and the
        // dispatch in base.StartAsync orders it before the execution takes it.
        Volatile.Write(ref _startProcessor, CreateProcessor());
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
                processor ??= CreateProcessor();
                using (processor)
                {
                    await ProcessSessionAsync(processor, wake.Task, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                var delay = GetRetryDelay(exception);
                if (delay < TimeSpan.Zero)
                {
                    throw new InvalidOperationException(
                        $"{nameof(GetRetryDelay)} returned a negative delay ({delay}) for the fault in the inner exception.",
                        exception);
                }

                _logger.LogError(exception, "Change queue processing faulted; retrying in {RetryDelay}.", delay);
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                retryDelay = delay;
            }
            processor = null;

            await WaitForRestartAsync(wake.Task, retryDelay, stoppingToken).ConfigureAwait(false);

            // Consumed before the processor is created, so a request made from here on restarts once more.
            Interlocked.Exchange(ref _restartRequested, 0);
        }
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
            // A normal return, such as a disabled store, stays idle until explicitly restarted.
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
            Interlocked.Exchange(ref _startProcessor, null)?.Dispose();
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        // A start not followed by a stop leaves the processor here if the execution has not taken it yet.
        base.Dispose();
        Interlocked.Exchange(ref _startProcessor, null)?.Dispose();
    }
}
