using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

/// <summary>
/// Manages a write retry queue with ring buffer semantics for buffering writes during disconnection.
/// When the queue is full, oldest writes are dropped to make room for new ones.
/// </summary>
internal sealed class WriteRetryQueue
{
    private readonly ConcurrentQueue<SubjectPropertyChange> _pendingWrites = new();
    private readonly SemaphoreSlim _flushSemaphore = new(1, 1);
    private readonly List<SubjectPropertyChange> _scratchBuffer = [];

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

    public WriteRetryQueue(int maxQueueSize, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxQueueSize);
        ArgumentNullException.ThrowIfNull(logger);

        _maxQueueSize = maxQueueSize;
        _logger = logger;
    }

    /// <summary>
    /// Enqueues writes. Ring buffer: oldest dropped when full.
    /// Thread-safe: DequeueAllTo called inside semaphore, ConcurrentQueue handles concurrent access.
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

        // Ring buffer: Drop the oldest if over capacity.
        // Note: Under high contention, Count may be slightly stale causing temporary overshoot,
        // but this is self-correcting as excess items are drained. This is acceptable for a lossy buffer.
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
    /// Flushes pending writes from the queue to the source.
    /// Returns true if flush succeeded (or queue was empty), false if flush failed.
    /// </summary>
    public async ValueTask<bool> FlushAsync(ISubjectSource source, CancellationToken cancellationToken)
    {
        if (IsEmpty)
        {
            return true;
        }

        try
        {
            await _flushSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }

        try
        {
            if (IsEmpty)
            {
                return true;
            }

            // Dequeue all pending writes into scratch buffer
            _scratchBuffer.Clear();
            _scratchBuffer.EnsureCapacity(_pendingWrites.Count); // Hint to reduce resizes
            while (_pendingWrites.TryDequeue(out var change))
            {
                _scratchBuffer.Add(change);
            }

            var count = _scratchBuffer.Count;
            if (count == 0)
            {
                return true;
            }

            // Reset dropped counter after successful dequeue
            Interlocked.Exchange(ref _droppedWriteCount, 0);

            try
            {
                var result = await source.WriteToSourceAsync(_scratchBuffer, cancellationToken).ConfigureAwait(false);
                if (result.FailedChanges.Count > 0)
                {
                    _logger.LogInformation("Re-queuing {Count} changes with transient errors from flush.", result.FailedChanges.Count);
                    EnqueueBatch(result.FailedChanges);
                }
                return true;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to flush {Count} queued writes to source, re-queuing.", count);
                EnqueueBatch(_scratchBuffer);
                return false;
            }
        }
        finally
        {
            _scratchBuffer.Clear();
            try { _flushSemaphore.Release(); } catch { /* might be disposed already */ }
        }
    }
}
