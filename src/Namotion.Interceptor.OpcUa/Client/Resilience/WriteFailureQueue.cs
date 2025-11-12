using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.OpcUa.Client.Resilience;

/// <summary>
/// Manages a write queue with ring buffer semantics for buffering writes during disconnection.
/// When the queue is full, oldest writes are dropped to make room for new ones.
/// </summary>
internal sealed class WriteFailureQueue
{
    private readonly ConcurrentQueue<SubjectPropertyChange> _pendingWrites = new();

    private readonly ILogger _logger;
    private readonly int _maxQueueSize;

    private int _droppedWriteCount; // Thread-safe via Interlocked

    /// <summary>
    /// Gets a value indicating whether the write queue is empty.
    /// </summary>
    public bool IsEmpty => _pendingWrites.IsEmpty;

    /// <summary>
    /// Gets the number of pending writes in the queue.
    /// </summary>
    public int PendingWriteCount => _pendingWrites.Count;

    /// <summary>
    /// Gets the number of dropped writes due to queue capacity limits.
    /// </summary>
    public int DroppedWriteCount => Interlocked.CompareExchange(ref _droppedWriteCount, 0, 0);

    public WriteFailureQueue(int maxQueueSize, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxQueueSize);
        ArgumentNullException.ThrowIfNull(logger);

        _maxQueueSize = maxQueueSize;
        _logger = logger;
    }

    /// <summary>
    /// Enqueues multiple write operations. If the queue reaches capacity,
    /// oldest writes are dropped to make room (ring buffer semantics).
    /// Thread-safety: EnqueueBatch can be called concurrently with DequeueAll. This is safe because:
    /// - ConcurrentQueue itself is fully thread-safe for concurrent Enqueue/TryDequeue operations
    /// - DequeueAll is always called inside _writeFlushSemaphore (OpcUaSubjectClientSource.cs:281)
    /// - Ring buffer enforcement (lines 66-76) is best-effort; slight variance in queue size
    ///   under concurrent access is acceptable and won't cause data corruption
    /// </summary>
    public void EnqueueBatch(IReadOnlyList<SubjectPropertyChange> changes)
    {
        if (_maxQueueSize is 0)
        {
            _logger.LogWarning("Write buffering is disabled. Dropping {Count} writes", changes.Count);
            return;
        }

        // Add all new items first
        foreach (var change in changes)
        {
            _pendingWrites.Enqueue(change);
        }

        // Enforce size limit by removing oldest items (ring buffer semantics)
        // Note: Count might be slightly stale if DequeueAll is running concurrently, but this is
        // acceptable - the queue will converge to the correct size on the next EnqueueBatch call
        while (_pendingWrites.Count > _maxQueueSize)
        {
            if (_pendingWrites.TryDequeue(out _))
            {
                Interlocked.Increment(ref _droppedWriteCount);
            }
            else
            {
                break; // Queue empty (shouldn't happen, but defensive)
            }
        }

        var dropped = Interlocked.CompareExchange(ref _droppedWriteCount, 0, 0);
        if (dropped > 0)
        {
            _logger.LogWarning(
                "Write queue at capacity, dropped {Count} oldest writes (queue size: {QueueSize})",
                dropped,
                _maxQueueSize);
        }
    }

    /// <summary>
    /// Dequeues all pending writes and returns them as a list.
    /// Resets the dropped write counter.
    /// </summary>
    public List<SubjectPropertyChange> DequeueAll()
    {
        var pendingWrites = new List<SubjectPropertyChange>(_pendingWrites.Count);
        while (_pendingWrites.TryDequeue(out var change))
        {
            pendingWrites.Add(change);
        }

        if (pendingWrites.Count > 0)
        {
            // Reset dropped counter after successful flush
            Interlocked.Exchange(ref _droppedWriteCount, 0);
        }

        return pendingWrites;
    }
}
