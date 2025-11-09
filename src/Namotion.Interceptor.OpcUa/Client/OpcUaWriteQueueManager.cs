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
    private readonly object _lock = new();
    private int _droppedWriteCount;

    public int PendingWriteCount => _pendingWrites.Count;

    public int DroppedWriteCount
    {
        get { lock (_lock) return _droppedWriteCount; }
    }

    public bool IsEmpty => _pendingWrites.IsEmpty;

    public OpcUaWriteQueueManager(int maxQueueSize, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxQueueSize);
        ArgumentNullException.ThrowIfNull(logger);

        _maxQueueSize = maxQueueSize;
        _logger = logger;
    }

    /// <summary>
    /// Enqueues a single write operation. If the queue is at capacity,
    /// the oldest write is dropped (ring buffer semantics).
    /// </summary>
    public void Enqueue(SubjectPropertyChange change)
    {
        if (_maxQueueSize is 0)
        {
            _logger.LogWarning("Write buffering is disabled. Dropping write for {PropertyName}", change.Property.Name);
            return;
        }

        // Fix TOCTOU race - dequeue FIRST to maintain strict bound
        while (_pendingWrites.Count >= _maxQueueSize)
        {
            if (_pendingWrites.TryDequeue(out _))
            {
                lock (_lock) _droppedWriteCount++;
            }
            else
            {
                break; // Queue emptied by another thread (e.g., flush)
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
        if (_maxQueueSize is 0)
        {
            _logger.LogWarning("Write buffering is disabled. Dropping {Count} writes", changes.Count);
            return;
        }

        foreach (var change in changes)
        {
            Enqueue(change);
        }

        int dropped;
        lock (_lock) dropped = _droppedWriteCount;

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
        var pendingWrites = new List<SubjectPropertyChange>();

        while (_pendingWrites.TryDequeue(out var change))
        {
            pendingWrites.Add(change);
        }

        if (pendingWrites.Count > 0)
        {
            // Reset dropped counter after successful flush
            lock (_lock) _droppedWriteCount = 0;
        }

        return pendingWrites;
    }
}
