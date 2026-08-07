using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Connectors.Monitoring;

/// <summary>
/// One subscriber to the source event stream, with its own queue and its own drain.
/// </summary>
/// <remarks>
/// Per-subscriber queues mean a slow handler delays only itself, and remove the need for sequence
/// stamping: a queue is created empty, so it cannot hold events enqueued before the subscription
/// existed. Pair with <see cref="Sources"/> (captured atomically with the subscription) to observe
/// every change exactly once.
/// </remarks>
public sealed class SourceSubscription : IDisposable
{
    private readonly ConcurrentQueue<SourceEvent> _queue = new();
    private readonly Action<SourceEvent> _handler;
    private readonly Action<SourceSubscription> _onDisposed;
    private readonly SourceMonitor _monitor;
    private readonly Action _drain;

    private int _draining;
    private volatile bool _disposed;

    internal SourceSubscription(
        Action<SourceEvent> handler,
        ImmutableArray<ISubjectSource> sources,
        Action<SourceSubscription> onDisposed,
        SourceMonitor monitor)
    {
        _handler = handler;
        Sources = sources;
        _onDisposed = onDisposed;
        // The monitor, not a resolved ILogger: subscribing typically happens before the host is
        // built, which is when the ILoggerFactory reaches the context, so a logger captured here
        // would be null for this subscription's lifetime and every handler exception below would be
        // swallowed silently.
        _monitor = monitor;
        // Cached rather than a Task.Run(Drain) method-group conversion at each call site: that
        // allocates a fresh Action delegate on every wakeup, and Drain is only ever run this way.
        _drain = Drain;
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
            _ = Task.Run(_drain);
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
                    // Leaves _draining set rather than resetting it: harmless, since Enqueue checks
                    // _disposed before _draining, so no further Enqueue can reach the CompareExchange
                    // that would need _draining back at 0. The flag just goes dead with the subscription.
                    return;
                }

                try
                {
                    _handler(sourceEvent);
                }
                catch (Exception exception)
                {
                    _monitor.ResolveLogger()?.LogError(exception, "A source event handler threw and was ignored.");
                }
            }

        }
        while (TryReacquireForPendingEvents());
    }

    /// <summary>
    /// Releases the single-flight flag and takes it straight back if the queue turned out not to be
    /// empty. Returns true when this thread must keep draining.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="Drain"/> so the handoff can be driven directly by a test: the
    /// window it closes is a few nanoseconds wide inside a running drain, so no dynamic test can
    /// reach it in place.
    /// <para>
    /// Must be Interlocked.Exchange, not Volatile.Write: Volatile.Write is a release only, so the
    /// !_queue.IsEmpty read right after it can be satisfied before this write is globally visible
    /// (StoreLoad reordering). A concurrent Enqueue landing in that window sees _draining still 1
    /// and declines to schedule a new drain, while this thread's own reordered read observes an
    /// empty queue and exits - the event is then stranded, since nothing is scheduled to look at it.
    /// Interlocked.Exchange is a full fence, making the two misses mutually exclusive. Do not
    /// "simplify" this back to Volatile.Write: the reordering reproduces roughly once in 500 million
    /// aligned attempts, far beyond any test's reach, so a companion test pins the literal API used
    /// on this line instead.
    /// </para>
    /// </remarks>
    internal bool TryReacquireForPendingEvents()
    {
        Interlocked.Exchange(ref _draining, 0);
        return !_queue.IsEmpty && Interlocked.CompareExchange(ref _draining, 1, 0) == 0;
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
