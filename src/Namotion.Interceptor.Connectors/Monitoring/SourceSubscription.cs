using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Connectors.Monitoring;

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
                    // Deliberately leaves _draining set rather than resetting it to 0. That is
                    // harmless, not a leak: Enqueue checks _disposed BEFORE it ever looks at
                    // _draining, so once disposed no further Enqueue call can reach the
                    // CompareExchange that would otherwise need _draining back at 0 to schedule a
                    // new Drain. The flag simply goes dead along with the rest of the subscription.
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

    /// <summary>
    /// Marks the subscription disposed and removes it from the monitor. Does not block on draining.
    /// </summary>
    /// <remarks>
    /// Disposal is asynchronous with respect to delivery, in both directions:
    /// <list type="bullet">
    /// <item>A handler invocation already in progress when Dispose is called is not interrupted; it
    /// runs to completion on its own, even after Dispose has returned.</item>
    /// <item>Any event still sitting in the queue, dequeued by the drain loop after Dispose has set
    /// the disposed flag, is dropped rather than delivered. Delivery is therefore best-effort at the
    /// disposal boundary: an event enqueued shortly before Dispose is not guaranteed to be handled.</item>
    /// </list>
    /// </remarks>
    public void Dispose()
    {
        _disposed = true;
        _onDisposed(this);
    }
}
