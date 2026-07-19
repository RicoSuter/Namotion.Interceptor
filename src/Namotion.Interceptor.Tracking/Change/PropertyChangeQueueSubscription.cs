using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// A subscription to receive property changes from a PropertyChangeInterceptor.
/// Each subscription maintains its own isolated queue.
/// Thread-safe for concurrent Enqueue calls from multiple threads.
/// TryDequeue should only be called from a single consumer thread per subscription.
/// </summary>
public sealed class PropertyChangeQueueSubscription : IDisposable
{
    // Cleared on disposal (doubles as the one-shot dispose flag) so a retained handle does not
    // pin the interceptor and its other consumers.
    private PropertyChangeInterceptor? _interceptor;

    private readonly ConcurrentQueue<SubjectPropertyChange> _queue = new();
    private readonly ManualResetEventSlim _signal = new(false); // non-counting signal
    private volatile bool _completed;

    internal PropertyChangeQueueSubscription(PropertyChangeInterceptor interceptor)
    {
        _interceptor = interceptor;
    }

    /// <summary>
    /// Enqueues a property change. Thread-safe and can be called concurrently from multiple threads.
    /// </summary>
    /// <param name="item">The property change to enqueue.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Enqueue(in SubjectPropertyChange item)
    {
        if (_completed)
        {
            return;
        }

        _queue.Enqueue(item); // copy happens here into the queue
        _signal.Set(); // wake consumer (idempotent if already set)
    }

    /// <summary>
    /// Attempts to dequeue a property change, waiting if the queue is empty.
    /// Should only be called from a single consumer thread per subscription (not thread-safe for concurrent TryDequeue calls).
    /// </summary>
    /// <param name="item">The dequeued property change if available.</param>
    /// <param name="cancellationToken">Cancellation token to abort the wait.</param>
    /// <returns>True if an item was dequeued; false when cancellation is requested, or when the
    /// subscription is completed and its queue is empty.</returns>
    public bool TryDequeue(out SubjectPropertyChange item, CancellationToken cancellationToken)
    {
        while (true)
        {
            // Check cancellation before dequeuing so that kill/shutdown signals
            // are observed even when the queue is continuously fed by producers.
            if (cancellationToken.IsCancellationRequested)
            {
                item = default!;
                return false;
            }

            // Fast path: dequeue if available
            if (_queue.TryDequeue(out item))
            {
                return true;
            }

            if (_completed)
            {
                item = default!;
                return false;
            }

            // Reset the signal and re-check via TryDequeue to avoid lost wake-ups; also re-check
            // completion so a Complete()/Dispose() Set() that raced the Reset is not lost.
            _signal.Reset();
            if (_queue.TryDequeue(out item))
            {
                return true;
            }

            if (_completed)
            {
                item = default!;
                return false;
            }

            // Still empty after reset: wait for a producer to Set()
            try
            {
                _signal.Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                item = default!;
                return false;
            }
            // loop and try dequeuing again
        }
    }

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _interceptor, null);
        if (owner is null)
        {
            return; // one-shot
        }

        _completed = true;

        // Wake any waiting TryDequeue
        _signal.Set();

        owner.RemoveQueueSubscription(this);

        // Deliberately not disposing _signal: a concurrent producer may still call _signal.Set() after its _completed check (enqueue-vs-dispose fix).
    }
}
