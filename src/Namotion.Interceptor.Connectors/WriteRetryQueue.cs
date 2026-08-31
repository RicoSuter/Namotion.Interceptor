using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors.Diagnostics;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Manages a write retry queue with ring buffer semantics for buffering writes during disconnection.
/// When the queue is full, oldest writes are dropped to make room for new ones.
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
    private bool _retired;
    private int _activeWriteCount;

    // Throttle flush-failure warnings to avoid log spam during extended disconnections
    private long _lastFlushWarningTimestamp;
    private bool _hasFlushWarnings;

    private readonly record struct FlushSettlement(bool IsRetired, int DroppedCount, int PendingWriteCount);

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

        int droppedCount;
        var rejected = false;
        lock (_lock)
        {
            if (_retired)
            {
                rejected = true;
                droppedCount = 0;
            }
            else
            {
                // Add all new items
                var span = changes.Span;
                for (var i = 0; i < span.Length; i++)
                {
                    _pendingWrites.Add(span[i]);
                }

                // Ring buffer: Drop the oldest if over capacity
                droppedCount = TrimToCapacity();

                Volatile.Write(ref _count, _pendingWrites.Count);
            }
        }

        if (rejected)
        {
            _metrics.AddDropped(changes.Length);
            return;
        }

        if (droppedCount > 0)
        {
            _metrics.AddDropped(droppedCount);
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
        if (!await TryEnterFlushAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            return await FlushCoreAsync(source, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseFlush();
        }
    }

    /// <summary>
    /// Registers and writes a current batch after flushing all older retry writes.
    /// </summary>
    public ValueTask WriteAsync(
        ISubjectSource source,
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        var rejected = false;
        lock (_lock)
        {
            if (_retired)
            {
                rejected = true;
            }
            else
            {
                _activeWriteCount += changes.Length;
            }
        }

        if (rejected)
        {
            _metrics.AddDropped(changes.Length);
            return ValueTask.CompletedTask;
        }

        return WriteCoreAsync(source, changes, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> TryEnterFlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _flushSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
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
    }

    private void ReleaseFlush()
    {
        try
        {
            _flushSemaphore.Release();
        }
        catch
        {
            // The queue may already be disposed.
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> FlushCoreAsync(ISubjectSource source, CancellationToken cancellationToken)
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
                _activeWriteCount += count;
                Volatile.Write(ref _count, _pendingWrites.Count);
            }

            var memory = new ReadOnlyMemory<SubjectPropertyChange>(_scratchBuffer, 0, count);
            var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken).ConfigureAwait(false);
            if (result.Error is not null)
            {
                // FailedChanges is complete (see WriteChangesInBatchesAsync), so every failed
                // item is restored before ring capacity is applied to the combined queue.
                var settlement = SettleFlushedChanges(result.FailedChanges.AsSpan(), count);
                if (settlement.IsRetired)
                {
                    Array.Clear(_scratchBuffer, 0, count);
                    return false;
                }

                var now = Environment.TickCount64;
                if (now - _lastFlushWarningTimestamp >= 5000)
                {
                    _lastFlushWarningTimestamp = now;
                    _logger.LogWarning(result.Error,
                        "Failed to flush queued writes to source, re-queuing failed items ({QueueSize} writes queued).",
                        settlement.PendingWriteCount);
                }

                _hasFlushWarnings = true;

                _metrics.AddDropped(settlement.DroppedCount);
                Array.Clear(_scratchBuffer, 0, count);
                return false;
            }

            var successfulSettlement = SettleFlushedChanges(ReadOnlySpan<SubjectPropertyChange>.Empty, count);
            if (successfulSettlement.IsRetired)
            {
                Array.Clear(_scratchBuffer, 0, count);
                return true;
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

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask WriteCoreAsync(
        ISubjectSource source,
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        if (!await TryEnterFlushAsync(cancellationToken).ConfigureAwait(false))
        {
            RequeueUnattemptedCurrentWrite(changes.Span);
            return;
        }

        try
        {
            if (!await FlushCoreAsync(source, cancellationToken).ConfigureAwait(false))
            {
                RequeueUnattemptedCurrentWrite(changes.Span);
                return;
            }

            lock (_lock)
            {
                if (_retired)
                {
                    return;
                }
            }

            var result = await source.WriteChangesInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
            var droppedCount = SettleCurrentWrite(result.FailedChanges.AsSpan(), changes.Length);
            if (droppedCount > 0)
            {
                _metrics.AddDropped(droppedCount);
            }
        }
        finally
        {
            ReleaseFlush();
        }
    }

    private void RequeueUnattemptedCurrentWrite(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        int droppedCount;
        lock (_lock)
        {
            if (_retired)
            {
                return;
            }

            _activeWriteCount -= changes.Length;
            _pendingWrites.AddRange(changes);
            droppedCount = TrimToCapacity();
            Volatile.Write(ref _count, _pendingWrites.Count);
        }

        if (droppedCount > 0)
        {
            _metrics.AddDropped(droppedCount);
        }
    }

    private int SettleCurrentWrite(ReadOnlySpan<SubjectPropertyChange> failedChanges, int attemptedCount)
    {
        lock (_lock)
        {
            if (_retired)
            {
                return 0;
            }

            _activeWriteCount -= attemptedCount;
            _pendingWrites.InsertRange(0, failedChanges);
            var droppedCount = TrimToCapacity();
            Volatile.Write(ref _count, _pendingWrites.Count);
            return droppedCount;
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
            stranded = _pendingWrites.Count + _activeWriteCount;
            _pendingWrites.Clear();
            _activeWriteCount = 0;
            Volatile.Write(ref _count, 0);
        }

        _metrics.AddDropped(stranded);
    }

    private FlushSettlement SettleFlushedChanges(ReadOnlySpan<SubjectPropertyChange> failedChanges, int attemptedCount)
    {
        lock (_lock)
        {
            if (_retired)
            {
                return new FlushSettlement(IsRetired: true, DroppedCount: 0, PendingWriteCount: 0);
            }

            _activeWriteCount -= attemptedCount;
            _pendingWrites.InsertRange(0, failedChanges);
            var droppedCount = TrimToCapacity();
            var pendingWriteCount = _pendingWrites.Count;
            Volatile.Write(ref _count, pendingWriteCount);
            return new FlushSettlement(
                IsRetired: false,
                DroppedCount: droppedCount,
                PendingWriteCount: pendingWriteCount);
        }
    }

    private int TrimToCapacity()
    {
        var droppedCount = _pendingWrites.Count - _maxQueueSize;
        if (droppedCount > 0)
        {
            _pendingWrites.RemoveRange(0, droppedCount);
        }

        return droppedCount;
    }

    /// <summary>
    /// Disposes the write retry queue and releases resources.
    /// </summary>
    public void Dispose()
    {
        Retire();
        _flushSemaphore.Dispose();
    }
}
