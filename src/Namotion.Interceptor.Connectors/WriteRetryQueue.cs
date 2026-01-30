using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Manages a write retry queue with ring buffer semantics for buffering writes during disconnection.
/// When the queue is full, oldest writes are dropped to make room for new ones.
/// </summary>
public sealed class WriteRetryQueue : IDisposable
{
    private readonly List<SubjectPropertyChange> _pendingWrites = [];
    private readonly SemaphoreSlim _flushSemaphore = new(1, 1);
    private readonly Lock _lock = new();

    // Reusable buffer to avoid allocation on each flush (capped at 1024 items, loops for larger queues)
    private const int MaxBatchSize = 1024;
    private SubjectPropertyChange[] _scratchBuffer = new SubjectPropertyChange[64];

    private readonly ILogger _logger;
    private readonly int _maxQueueSize;
    private int _count;

    /// <summary>
    /// Gets a value indicating whether the write queue is empty.
    /// </summary>
    public bool IsEmpty => Volatile.Read(ref _count) == 0;

    /// <summary>
    /// Gets the number of pending writes in the queue.
    /// </summary>
    public int PendingWriteCount => Volatile.Read(ref _count);

    public WriteRetryQueue(int maxQueueSize, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxQueueSize);
        ArgumentNullException.ThrowIfNull(logger);

        _maxQueueSize = maxQueueSize;
        _logger = logger;
    }

    /// <summary>
    /// Enqueues writes for retry. Ring buffer: oldest dropped when full.
    /// Thread-safe via lock to ensure atomic enqueue + drop operations.
    /// </summary>
    public void Enqueue(ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        if (_maxQueueSize is 0)
        {
            _logger.LogWarning("Write buffering is disabled. Dropping {Count} writes.", changes.Length);
            return;
        }

        int droppedCount;
        lock (_lock)
        {
            // Add all new items
            var span = changes.Span;
            for (var i = 0; i < span.Length; i++)
            {
                _pendingWrites.Add(span[i]);
            }

            // Ring buffer: Drop the oldest if over capacity
            droppedCount = _pendingWrites.Count - _maxQueueSize;
            if (droppedCount > 0)
            {
                _pendingWrites.RemoveRange(0, droppedCount);
            }

            Volatile.Write(ref _count, _pendingWrites.Count);
        }

        if (droppedCount > 0)
        {
            _logger.LogWarning(
                "Write queue at capacity, dropped {Count} oldest writes (queue size: {QueueSize}).",
                droppedCount,
                _maxQueueSize);
        }
    }

    /// <summary>
    /// Flushes pending writes from the queue to the source.
    /// Returns true if flush succeeded (or queue was empty), false if flush failed.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
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
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error acquiring flush semaphore");
            return false;
        }

        try
        {
            if (IsEmpty)
            {
                return true;
            }

            // Ensure buffer is large enough (grow up to MaxBatchSize, then loop)
            if (_scratchBuffer.Length < MaxBatchSize)
            {
                var newSize = Math.Min(_scratchBuffer.Length * 2, MaxBatchSize);
                _scratchBuffer = new SubjectPropertyChange[newSize];
            }

            // Process in batches up to MaxBatchSize, looping until queue is empty
            while (true)
            {
                // Dequeue up to buffer size
                int count;
                lock (_lock)
                {
                    count = Math.Min(_scratchBuffer.Length, _pendingWrites.Count);
                    if (count == 0)
                    {
                        break;
                    }

                    for (var i = 0; i < count; i++)
                    {
                        _scratchBuffer[i] = _pendingWrites[i];
                    }
                    _pendingWrites.RemoveRange(0, count);
                    Volatile.Write(ref _count, _pendingWrites.Count);
                }

                var memory = new ReadOnlyMemory<SubjectPropertyChange>(_scratchBuffer, 0, count);
                var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken).ConfigureAwait(false);
                if (result.Error is not null)
                {
                    _logger.LogWarning(result.Error, "Failed to flush {Count} queued writes to source, re-queuing failed items.", count);
                    RequeueChanges(result.FailedChanges);
                    Array.Clear(_scratchBuffer, 0, count);
                    return false;
                }

                Array.Clear(_scratchBuffer, 0, count);
            }

            return true;
        }
        finally
        {
            try { _flushSemaphore.Release(); } catch { /* might be disposed already */ }
        }
    }

    private void RequeueChanges(ImmutableArray<SubjectPropertyChange> changes)
    {
        lock (_lock)
        {
            _pendingWrites.InsertRange(0, changes);
            Volatile.Write(ref _count, _pendingWrites.Count);
        }
    }

    /// <summary>
    /// Disposes the write retry queue and releases resources.
    /// </summary>
    public void Dispose()
    {
        _flushSemaphore.Dispose();
    }
}
