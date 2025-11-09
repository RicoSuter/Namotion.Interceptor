using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Change;
using System.Collections.Concurrent;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Manages a write queue with ring buffer semantics for buffering writes during disconnection.
/// When the queue is full, oldest writes are dropped to make room for new ones.
/// </summary>
internal sealed class OpcUaWriteQueueManager
{
    private readonly ConcurrentQueue<SubjectPropertyChange> _pendingWrites = new();
    private readonly int _maxQueueSize;
    private readonly ILogger _logger;
    private int _droppedWriteCount = 0; // Access via Interlocked operations only

    /// <summary>
    /// Gets the number of write operations currently queued.
    /// </summary>
    public int PendingWriteCount => _pendingWrites.Count;

    /// <summary>
    /// Gets the number of writes that were dropped due to ring buffer overflow.
    /// This counter is reset when the queue is successfully flushed.
    /// </summary>
    public int DroppedWriteCount => Interlocked.CompareExchange(ref _droppedWriteCount, 0, 0);

    /// <summary>
    /// Gets whether the queue is empty.
    /// </summary>
    public bool IsEmpty => _pendingWrites.IsEmpty;

    public OpcUaWriteQueueManager(int maxQueueSize, ILogger logger)
    {
        if (maxQueueSize < 0)
        {
            throw new ArgumentException("Max queue size must be non-negative", nameof(maxQueueSize));
        }

        _maxQueueSize = maxQueueSize;
        _logger = logger;
    }

    /// <summary>
    /// Enqueues a single write operation. If the queue is at capacity,
    /// the oldest write is dropped (ring buffer semantics).
    /// </summary>
    public void Enqueue(SubjectPropertyChange change)
    {
        if (_maxQueueSize == 0)
        {
            _logger.LogWarning("Write buffering is disabled. Dropping write for {PropertyName}.",
                change.Property.Name);
            return;
        }

        // Fix TOCTOU race - dequeue FIRST to maintain strict bound
        while (_pendingWrites.Count >= _maxQueueSize)
        {
            if (_pendingWrites.TryDequeue(out _))
            {
                Interlocked.Increment(ref _droppedWriteCount);
            }
            else
            {
                // Queue was emptied by another thread (e.g., flush)
                break;
            }
        }

        _pendingWrites.Enqueue(change);
    }

    /// <summary>
    /// Enqueues multiple write operations. If the queue reaches capacity,
    /// oldest writes are dropped to make room (ring buffer semantics).
    /// </summary>
    public void EnqueueBatch(IReadOnlyList<SubjectPropertyChange> changes)
    {
        if (_maxQueueSize == 0)
        {
            _logger.LogWarning("Write buffering is disabled. Dropping {Count} writes.", changes.Count);
            return;
        }

        foreach (var change in changes)
        {
            Enqueue(change);
        }

        var dropped = Interlocked.CompareExchange(ref _droppedWriteCount, 0, 0);
        if (dropped > 0)
        {
            _logger.LogWarning(
                "Write queue at capacity, dropped {Count} oldest writes (queue size: {QueueSize}).",
                dropped, _maxQueueSize);
        }
    }

    /// <summary>
    /// Dequeues all pending writes and returns them as a list.
    /// Resets the dropped write counter.
    /// </summary>
    public List<SubjectPropertyChange> DequeueAll()
    {
        var pendingWrites = new List<SubjectPropertyChange>();

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

    /// <summary>
    /// Clears all pending writes without returning them.
    /// </summary>
    public void Clear()
    {
        _pendingWrites.Clear();
        Interlocked.Exchange(ref _droppedWriteCount, 0);
    }
}