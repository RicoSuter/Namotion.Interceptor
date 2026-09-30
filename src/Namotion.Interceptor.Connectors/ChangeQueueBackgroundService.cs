using Microsoft.Extensions.Hosting;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// A hosted service that consumes property changes through a <see cref="ChangeQueueProcessor"/>. The processor,
/// and with it the change subscription, is created in <see cref="StartAsync"/>, so a change made after the host
/// start returns is delivered even though the execution may run later. The service disposes the processor on
/// every exit path.
/// </summary>
public abstract class ChangeQueueBackgroundService : BackgroundService
{
    private ChangeQueueProcessor? _startProcessor;

    /// <summary>
    /// Creates the processor that subscribes to property changes. Called on the host start path on every
    /// <see cref="StartAsync"/>, so it must not block or perform I/O; an exception fails the start.
    /// </summary>
    protected abstract ChangeQueueProcessor CreateProcessor();

    /// <summary>
    /// Consumes the processor created by the start until <paramref name="stoppingToken"/> is cancelled. The
    /// processor is disposed when this returns. An implementation that restarts processing may dispose it
    /// earlier and use processors from <see cref="CreateProcessor"/>, which it then owns.
    /// </summary>
    protected abstract Task ProcessAsync(ChangeQueueProcessor processor, CancellationToken stoppingToken);

    /// <inheritdoc />
    public sealed override Task StartAsync(CancellationToken cancellationToken)
    {
        // Not in ExecuteAsync: since .NET 10 it may run after StartAsync returns, and changes made in between
        // would never reach the processor.
        Interlocked.Exchange(ref _startProcessor, CreateProcessor())?.Dispose();
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

        using (processor)
        {
            await ProcessAsync(processor, stoppingToken).ConfigureAwait(false);
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
