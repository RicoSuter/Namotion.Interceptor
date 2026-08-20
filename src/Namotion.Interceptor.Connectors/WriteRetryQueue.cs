using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Manages a write retry queue with ring buffer semantics for buffering writes during disconnection.
/// When the queue is full, oldest writes are dropped to make room for new ones. A queue of size 0
/// is the disabled configuration: everything handed to it is counted as dropped and discarded.
/// </summary>
internal sealed class WriteRetryQueue : IDisposable
{
    private readonly List<SubjectPropertyChange> _pendingWrites = [];
    private readonly SemaphoreSlim _flushSemaphore = new(1, 1);
    private readonly Lock _lock = new();

    // Reusable buffer to avoid allocation on each flush (capped at 1024 items, loops for larger queues)
    private const int MaxBatchSize = 1024;
    private SubjectPropertyChange[] _scratchBuffer = new SubjectPropertyChange[64];

    private readonly ILogger _logger;
    private readonly QueueMetrics _metrics;
    private readonly int _maxQueueSize;
    private int _count;

    // Only ever read and written under _lock, so a producer cannot pass the check while a retire
    // clears the list behind it.
    private bool _retired;

    // Throttle flush-failure warnings to avoid log spam during extended disconnections
    private long _lastFlushWarningTimestamp;
    private bool _hasFlushWarnings;

    /// <summary>
    /// Gets a value indicating whether the write queue is empty.
    /// </summary>
    public bool IsEmpty => Volatile.Read(ref _count) == 0;

    /// <summary>
    /// Gets the number of pending writes in the queue.
    /// </summary>
    public int PendingWriteCount => Volatile.Read(ref _count);

    // Metrics is required rather than optional, so no construction site can drop writes uncounted.
    public WriteRetryQueue(int maxQueueSize, ILogger logger, QueueMetrics metrics)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxQueueSize);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(metrics);

        _maxQueueSize = maxQueueSize;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>
    /// Enqueues writes for retry. Ring buffer: oldest dropped when full.
    /// Thread-safe via lock to ensure atomic enqueue + drop operations.
    /// </summary>
    public void Enqueue(ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        if (_maxQueueSize is 0)
        {
            _metrics.AddDropped(changes.Length);
            _logger.LogWarning("Write buffering is disabled. Dropping {Count} writes.", changes.Length);
            return;
        }

        bool retired;
        int droppedCount;
        lock (_lock)
        {
            // Inside the lock rather than ahead of it: a check that lost the race to Retire would park
            // this batch in a list nobody reads again, which is the silent loss the latch exists to stop.
            retired = _retired;
            if (retired)
            {
                // Exclusive with the ring trim below, so the batch is counted exactly once.
                droppedCount = changes.Length;
            }
            else
            {
                var span = changes.Span;
                for (var i = 0; i < span.Length; i++)
                {
                    _pendingWrites.Add(span[i]);
                }

                droppedCount = TrimToCapacity();
            }
        }

        if (droppedCount <= 0)
        {
            return;
        }

        // Reported outside the lock, so an arbitrary logger implementation cannot stall the write path
        // or take a second lock while this one is held.
        _metrics.AddDropped(droppedCount);
        if (retired)
        {
            _logger.LogWarning(
                "{Count} writes settled after the source stopped and are discarded.",
                droppedCount);
        }
        else
        {
            _logger.LogWarning(
                "Write queue at capacity, dropped {Count} oldest writes (queue size: {QueueSize}).",
                droppedCount,
                _maxQueueSize);
        }
    }

    /// <summary>
    /// Retires the queue: counts and logs whatever is still pending, and makes every later enqueue
    /// count instead of park. Idempotent.
    /// </summary>
    /// <remarks>
    /// Meant to be called when the run ends rather than only from <see cref="Dispose"/>, because a
    /// source detached from the graph is stopped and never disposed, and a stopped source never gets
    /// another attempt to flush. The latch is what keeps a write that settles afterwards, such as an
    /// abandoned teardown flush, from parking into a queue nobody will read. No producer adds past it,
    /// so the pending list stays empty and <see cref="DrainForLocalReapply"/> returns nothing.
    /// </remarks>
    public void Retire()
    {
        int stranded;
        lock (_lock)
        {
            if (_retired)
            {
                return;
            }

            _retired = true;
            stranded = _pendingWrites.Count;
            _pendingWrites.Clear();
            Volatile.Write(ref _count, 0);
        }

        if (stranded > 0)
        {
            _metrics.AddDropped(stranded);
            _logger.LogWarning(
                "{Count} queued writes were never delivered before the source stopped and are discarded.",
                stranded);
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
            var totalFlushed = 0;
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
                    var now = Environment.TickCount64;
                    if (now - _lastFlushWarningTimestamp >= 5000)
                    {
                        var queueSize = count + PendingWriteCount;
                        _lastFlushWarningTimestamp = now;
                        _logger.LogWarning(result.Error,
                            "Failed to flush queued writes to source, re-queuing failed items ({QueueSize} writes queued).",
                            queueSize);
                    }

                    _hasFlushWarnings = true;

                    // FailedChanges is complete (see WriteChangesInBatchesAsync), so every failed
                    // item is restored before ring capacity is applied to the combined queue.
                    var droppedCount = RequeueChanges(result.FailedChanges.AsSpan());
                    _metrics.AddDropped(droppedCount);
                    Array.Clear(_scratchBuffer, 0, count);
                    return false;
                }

                totalFlushed += count;
                Array.Clear(_scratchBuffer, 0, count);
            }

            if (_hasFlushWarnings)
            {
                _logger.LogWarning("Successfully flushed {Count} queued writes after retry.", totalFlushed);
                _hasFlushWarnings = false;
                _lastFlushWarningTimestamp = 0;
            }

            return true;
        }
        finally
        {
            try { _flushSemaphore.Release(); } catch { /* might be disposed already */ }
        }
    }

    /// <summary>
    /// Drains all pending writes from the queue for local re-application with optimistic concurrency.
    /// Used on reconnection: instead of flushing stale changes to the server, the caller compares
    /// each change's old value with the current (post-reconnection) value and re-applies locally if non-conflicting.
    /// </summary>
    public SubjectPropertyChange[] DrainForLocalReapply()
    {
        lock (_lock)
        {
            var changes = _pendingWrites.ToArray();
            _pendingWrites.Clear();
            Volatile.Write(ref _count, 0);
            return changes;
        }
    }

    private int RequeueChanges(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        lock (_lock)
        {
            if (_retired)
            {
                // Retired while this batch was in flight, so no later attempt will pick it up. Report
                // the whole batch as dropped instead of restoring it into a queue with no reader.
                return changes.Length;
            }

            _pendingWrites.InsertRange(0, changes);

            // The failed in-flight changes are older than anything enqueued while the write was in
            // progress. Ring semantics therefore evict from the front after restoring the batch.
            return TrimToCapacity();
        }
    }

    // Ring semantics: evict from the front once over capacity. Callers must hold the lock.
    private int TrimToCapacity()
    {
        var droppedCount = _pendingWrites.Count - _maxQueueSize;
        if (droppedCount > 0)
        {
            _pendingWrites.RemoveRange(0, droppedCount);
        }

        Volatile.Write(ref _count, _pendingWrites.Count);
        return droppedCount;
    }

    /// <summary>
    /// Disposes the write retry queue and releases resources.
    /// </summary>
    /// <remarks>
    /// Retires first, so a queue disposed without its run ever reaching <see cref="Retire"/> still
    /// reports what it throws away. The two stay separate in the other direction: a retire happens
    /// while the source is still unwinding and must leave the flush semaphore usable.
    /// </remarks>
    public void Dispose()
    {
        Retire();
        _flushSemaphore.Dispose();
    }
}
