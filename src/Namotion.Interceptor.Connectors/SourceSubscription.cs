using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// One subscriber to the source event stream, with its own queue and its own drain.
/// </summary>
/// <remarks>
/// Per-subscriber queues mean a slow handler delays only itself. They also remove the need for
/// sequence stamping: this queue is created empty, so it cannot contain events enqueued before the
/// subscription existed. Pair it with <see cref="Sources"/>, captured atomically with the
/// subscription, to observe every change exactly once.
/// </remarks>
public sealed class SourceSubscription : IDisposable
{
    private readonly ConcurrentQueue<SourceEvent> _queue = new();
    private readonly Action<SourceEvent> _handler;
    private readonly Action<SourceSubscription> _onDisposed;
    private readonly ILogger? _logger;

    private int _draining;
    private volatile bool _disposed;

    internal SourceSubscription(
        Action<SourceEvent> handler,
        ImmutableArray<ISubjectSource> sources,
        Action<SourceSubscription> onDisposed,
        ILogger? logger)
    {
        _handler = handler;
        Sources = sources;
        _onDisposed = onDisposed;
        _logger = logger;
    }

    /// <summary>
    /// The sources registered at the moment this subscription was created, captured atomically with
    /// it. Reading SourceMonitor.Sources separately after subscribing is not race-free: a source
    /// registering between the two calls appears in both, and a naive consumer double-counts it.
    /// </summary>
    public ImmutableArray<ISubjectSource> Sources { get; }

    internal void Enqueue(in SourceEvent sourceEvent)
    {
        if (_disposed)
        {
            return;
        }

        _queue.Enqueue(sourceEvent);

        // Single-flight: one drain at a time, and it exits when the queue is empty, so an idle
        // subscription owns no task.
        if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
        {
            _ = Task.Run(Drain);
        }
    }

    private void Drain()
    {
        do
        {
            while (_queue.TryDequeue(out var sourceEvent))
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    _handler(sourceEvent);
                }
                catch (Exception exception)
                {
                    _logger?.LogError(exception, "A source event handler threw and was ignored.");
                }
            }

            Volatile.Write(ref _draining, 0);
        }
        while (!_queue.IsEmpty && Interlocked.CompareExchange(ref _draining, 1, 0) == 0);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _onDisposed(this);
    }
}
