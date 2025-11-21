using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Manages a write retry queue with ring buffer semantics for buffering writes during disconnection.
/// When the queue is full, oldest writes are dropped to make room for new ones.
/// </summary>
internal sealed class WriteRetryQueue
{
    private readonly ConcurrentQueue<SubjectPropertyChange> _pendingWrites = new();
    private readonly SemaphoreSlim _flushSemaphore = new(1, 1);

    // Reusable buffer to avoid allocation on each flush
    private SubjectPropertyChange[] _scratchBuffer = new SubjectPropertyChange[64];
    private int _scratchBufferCount;

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
    public void EnqueueBatch(ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        if (_maxQueueSize is 0)
        {
            _logger.LogWarning("Write buffering is disabled. Dropping {Count} writes", changes.Length);
            return;
        }

        // Add all new items first
        var span = changes.Span;
        for (var i = 0; i < span.Length; i++)
        {
            _pendingWrites.Enqueue(span[i]);
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
    /// Flushes pending writes from the queue to the connector.
    /// Returns true if flush succeeded (or queue was empty), false if flush failed.
    /// </summary>
    public async ValueTask<bool> FlushAsync(ISubjectConnector connector, CancellationToken cancellationToken)
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
            _scratchBufferCount = 0;
            var pendingCount = _pendingWrites.Count;

            // Ensure buffer is large enough
            if (_scratchBuffer.Length < pendingCount)
            {
                _scratchBuffer = new SubjectPropertyChange[Math.Max(pendingCount, _scratchBuffer.Length * 2)];
            }

            while (_pendingWrites.TryDequeue(out var change))
            {
                _scratchBuffer[_scratchBufferCount++] = change;
            }

            var count = _scratchBufferCount;
            if (count == 0)
            {
                return true;
            }

            // Reset dropped counter after successful dequeue
            Interlocked.Exchange(ref _droppedWriteCount, 0);

            // Do inline batching to track exactly which items succeed
            var batchSize = connector.WriteBatchSize;
            var writtenCount = 0;

            // Use ReadOnlyMemory over the reusable buffer for zero-allocation slicing
            var memory = new ReadOnlyMemory<SubjectPropertyChange>(_scratchBuffer, 0, count);
            try
            {
                if (batchSize <= 0)
                {
                    // No batching - send all at once
                    await connector.WriteToSourceAsync(memory, cancellationToken).ConfigureAwait(false);
                    return true;
                }

                for (var i = 0; i < count; i += batchSize)
                {
                    var currentBatchSize = Math.Min(batchSize, count - i);
                    var batch = memory.Slice(i, currentBatchSize);

                    await connector.WriteToSourceAsync(batch, cancellationToken).ConfigureAwait(false);
                    writtenCount += currentBatchSize;
                }

                return true;
            }
            catch (Exception e)
            {
                var remainingCount = count - writtenCount;
                _logger.LogWarning(e,
                    "Failed to flush queued writes to connector. {WrittenCount} succeeded, {RemainingCount} re-queuing.",
                    writtenCount, remainingCount);

                // Re-queue only items that weren't successfully written
                if (remainingCount > 0)
                {
                    var remaining = memory.Slice(writtenCount, remainingCount);
                    EnqueueBatch(remaining);
                }

                return false;
            }
        }
        finally
        {
            // Clear buffer to allow GC of SubjectPropertyChange objects
            if (_scratchBufferCount > 0)
            {
                Array.Clear(_scratchBuffer, 0, _scratchBufferCount);
            }
            try { _flushSemaphore.Release(); } catch { /* might be disposed already */ }
        }
    }
}
