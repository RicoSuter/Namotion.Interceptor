using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// A hosted service that consumes property changes through a <see cref="ChangeQueueProcessor"/>. The processor,
/// and with it the change subscription, is created in <see cref="StartAsync"/>, so a change made after the host
/// start returns is delivered even though the execution may run later. <see cref="RequestRestart"/> replaces the
/// processor while the service runs. The service disposes the processors it created on every exit path,
/// including a restart.
/// </summary>
public abstract class ChangeQueueBackgroundService : BackgroundService
{
    private ChangeQueueProcessor? _startProcessor;
    private TaskCompletionSource? _restartWake;
    private int _restartRequested;

    /// <summary>
    /// Creates the processor that subscribes to property changes. Called from <see cref="StartAsync"/> on the
    /// host start path and from the execution on a restart, so it must not block or perform I/O; an exception
    /// fails the start or, on a restart, faults the execution.
    /// </summary>
    protected abstract ChangeQueueProcessor CreateProcessor();

    /// <summary>
    /// Consumes the given processor until <paramref name="stoppingToken"/> is cancelled, which a stop and
    /// <see cref="RequestRestart"/> both do; an <see cref="OperationCanceledException"/> thrown after that
    /// cancellation counts as a return. The processor is disposed when this returns. Returning before the
    /// cancellation leaves the service idle, without a processor, until a restart is requested or it stops. An
    /// implementation that restarts processing itself may dispose the processor earlier and use processors from
    /// <see cref="CreateProcessor"/>, which it then owns.
    /// </summary>
    protected abstract Task ProcessAsync(ChangeQueueProcessor processor, CancellationToken stoppingToken);

    /// <summary>
    /// Cancels the token given to <see cref="ProcessAsync"/>, disposes its processor and runs
    /// <see cref="ProcessAsync"/> again with a new processor from <see cref="CreateProcessor"/>; a
    /// <see cref="ProcessAsync"/> that has already returned is run again the same way. Requests made before the
    /// restart begins coalesce into one, and a request made while the service is stopped is served by the next
    /// start. An exception from <see cref="CreateProcessor"/> on the restart faults the execution.
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
        Volatile.Write(ref _restartRequested, 0);

        // Not in ExecuteAsync: since .NET 10 it may run after StartAsync returns, and changes made in between
        // would never reach the processor.
        Interlocked.Exchange(ref _startProcessor, CreateProcessor())?.Dispose();
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processor = Interlocked.Exchange(ref _startProcessor, null);
        while (processor is not null)
        {
            var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stopRegistration = stoppingToken.UnsafeRegister(
                static state => ((TaskCompletionSource)state!).TrySetResult(), wake);

            // Full fence before the flag is read, see RequestRestart.
            Interlocked.Exchange(ref _restartWake, wake);
            if (Volatile.Read(ref _restartRequested) != 0)
            {
                wake.TrySetResult();
            }

            using (processor)
            using (var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                var processing = ProcessAsync(processor, session.Token);
                if (await Task.WhenAny(processing, wake.Task).ConfigureAwait(false) == wake.Task)
                {
                    await session.CancelAsync().ConfigureAwait(false);
                }

                try
                {
                    await processing.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (session.IsCancellationRequested)
                {
                    // The session was ended by a restart or stop; an implementation may report that by throwing.
                }
            }

            // Idles here after a ProcessAsync that returned on its own, such as a disabled store.
            await wake.Task.ConfigureAwait(false);
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            // Consumed before the processor is created, so a request made from here on restarts once more.
            Interlocked.Exchange(ref _restartRequested, 0);
            processor = CreateProcessor();
        }
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
        // A start cancelled before its execution ran leaves the processor here.
        base.Dispose();
        Interlocked.Exchange(ref _startProcessor, null)?.Dispose();
    }
}
