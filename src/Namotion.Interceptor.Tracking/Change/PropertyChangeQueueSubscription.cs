using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// A subscription to receive property changes from a PropertyChangeQueue.
/// Each subscription maintains its own isolated queue.
/// Thread-safe for concurrent Enqueue calls from multiple threads.
/// TryDequeue should only be called from a single consumer thread per subscription.
/// </summary>
public sealed class PropertyChangeQueueSubscription : IDisposable
{
    private readonly PropertyChangeQueue _changeQueue;
    private readonly ConcurrentQueue<SubjectPropertyChange> _queue = new();
    private readonly ManualResetEventSlim _signal = new(false); // non-counting signal
    private volatile bool _completed;

    public PropertyChangeQueueSubscription(PropertyChangeQueue queue)
    {
        _changeQueue = queue;
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
    /// <param name="ct">Cancellation token to abort the wait.</param>
    /// <returns>True if an item was dequeued; false if the subscription is completed or cancelled.</returns>
    public bool TryDequeue(out SubjectPropertyChange item, CancellationToken ct = default)
    {
        while (true)
        {
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

            // Reset the signal and re-check via TryDequeue to avoid lost wake-ups
            _signal.Reset();
            if (_queue.TryDequeue(out item))
            {
                return true;
            }

            // Still empty after reset: wait for a producer to Set()
            try
            {
                _signal.Wait(ct);
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
        if (_completed)
        {
            return;
        }

        _completed = true;

        // Wake any waiting TryDequeue
        _signal.Set();

        _changeQueue.Unsubscribe(this);
        _signal.Dispose();
    }
}