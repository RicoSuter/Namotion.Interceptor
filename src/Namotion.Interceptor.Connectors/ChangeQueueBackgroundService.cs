using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// A hosted service that consumes property changes through a <see cref="ChangeQueueProcessor"/>. The processor,
/// and with it the change subscription, is created in <see cref="StartAsync"/>, so a change made after the host
/// start returns is delivered even though the execution may run later. <see cref="RequestRestart"/> replaces the
/// processor while the service runs. The service disposes the processors it created on every exit path,
/// including a restart.
/// </summary>
/// <remarks>
/// A further <see cref="StartAsync"/> is supported only once the previous <see cref="BackgroundService.ExecuteTask"/>
/// has completed. A <see cref="StopAsync"/> that returned because its cancellation token fired first does not
/// guarantee that, and a restart requested after such a start may be lost.
/// </remarks>
public abstract class ChangeQueueBackgroundService : BackgroundService
{
    private ChangeQueueProcessor? _startProcessor;
    private TaskCompletionSource? _restartWake;
    private int _restartRequested;

    /// <summary>
    /// Creates the processor that subscribes to property changes. Called from <see cref="StartAsync"/> on the
    /// host start path and from the execution on a restart, so it must not block or perform I/O; an exception
    /// fails the start or, during execution, is passed to <see cref="GetRetryDelay"/>. A processor from an
    /// earlier start that no execution took is disposed before this is called.
    /// </summary>
    protected abstract ChangeQueueProcessor CreateProcessor();

    /// <summary>
    /// Consumes the given processor until <paramref name="stoppingToken"/> is cancelled, which a stop and
    /// <see cref="RequestRestart"/> both do; an <see cref="OperationCanceledException"/> thrown after that
    /// cancellation counts as a return. The default drains the processor with
    /// <see cref="ChangeQueueProcessor.ProcessAsync"/> until the token is cancelled. Override it to set up state
    /// before draining, tear it down after, or run work alongside it. The processor is disposed when this returns.
    /// Returning before the cancellation leaves the service idle, without a processor, until a restart is
    /// requested or it stops. The service owns the processor; implementations must not dispose it.
    /// </summary>
    protected virtual Task ProcessAsync(ChangeQueueProcessor processor, CancellationToken stoppingToken) =>
        processor.ProcessAsync(stoppingToken);

    /// <summary>
    /// Returns the delay before retrying a failed processing session or processor creation during execution,
    /// or null to propagate the failure. Called after the failed session's processor is disposed.
    /// Initial creation failures always fail <see cref="StartAsync"/>. Expected stop and restart cancellation
    /// does not call this method. A stop or restart interrupts the delay.
    /// </summary>
    /// <returns>A nonnegative delay supported by <see cref="Task.Delay(TimeSpan, CancellationToken)"/>, or null.</returns>
    protected virtual TimeSpan? GetRetryDelay(Exception exception) => null;

    /// <summary>
    /// Cancels the token given to <see cref="ProcessAsync"/>, disposes its processor and runs
    /// <see cref="ProcessAsync"/> again with a new processor from <see cref="CreateProcessor"/>; a
    /// <see cref="ProcessAsync"/> that has already returned is run again the same way. Requests made before the
    /// restart begins coalesce into one, and a request made while the service is stopped is served by the next
    /// start. Failures during the restart use <see cref="GetRetryDelay"/>.
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
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                retryDelay = GetRetryDelay(exception);
                if (retryDelay is null)
                {
                    throw;
                }
            }
            processor = null;

            await WaitForRestartAsync(wake.Task, retryDelay, stoppingToken).ConfigureAwait(false);
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

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

        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero, nameof(retryDelay));
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
