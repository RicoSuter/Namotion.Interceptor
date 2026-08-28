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
    /// <remarks>
    /// Always takes the flush gate, including on an empty queue. The queue reads empty from the moment a
    /// concurrent flush moves a batch into the scratch buffer, well before that batch reaches the peer,
    /// so a lock-free empty return would let a caller that goes on to send its own newer batch race the
    /// older one to the peer. On the steady-state path, where nothing is flushing, that costs one
    /// uncontended acquisition per batch and never suspends.
    /// </remarks>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<bool> FlushAsync(ISubjectSource source, CancellationToken cancellationToken)
    {
        switch (await TryEnterFlushGateAsync(cancellationToken).ConfigureAwait(false))
        {
            case FlushGateEntry.Cancelled:
                // Something may still be queued, so the caller must not send past it.
                return false;

            case FlushGateEntry.Disposed:
                // Nothing is pending and nothing ever will be, so there is nothing for a caller to be
                // ordered behind. Reported as success so it still attempts its own write, which fails
                // cleanly against a disposed transport rather than being parked into a dead queue.
                return true;
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
                if (cancellationToken.IsCancellationRequested)
                {
                    // Checked per batch, not only when entering: the loop drains until the queue is
                    // empty, and an abandoned flush would otherwise keep sending onto a connection the
                    // source has already replaced. Whatever is still queued stays queued, and reporting
                    // failure keeps the caller from sending its own batch ahead of it.
                    return false;
                }

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
            ExitFlushGate();
        }
    }

    /// <summary>
    /// Drains all pending writes from the queue for local re-application with optimistic concurrency.
    /// Used on reconnection: instead of flushing stale changes to the server, the caller compares
    /// each change's old value with the current (post-reconnection) value and re-applies locally if non-conflicting.
    /// </summary>
    /// <remarks>
    /// Takes the same flush gate as <see cref="FlushAsync"/> so the two cannot interleave: without it,
    /// this drain can run while a flush is holding a batch in its scratch buffer, and if that flush then
    /// fails, its requeue puts those stale values back at the front of the queue after the reconcile has
    /// already judged them, moving a property backwards. That is also why it does not skip an empty
    /// queue: empty here does not mean nothing is in flight.
    /// </remarks>
    public async Task<SubjectPropertyChange[]> DrainForLocalReapplyAsync(CancellationToken cancellationToken)
    {
        // Both failure modes yield the same empty result the caller already handles: there is nothing to
        // reapply either way.
        if (await TryEnterFlushGateAsync(cancellationToken).ConfigureAwait(false) != FlushGateEntry.Acquired)
        {
            return [];
        }

        try
        {
            lock (_lock)
            {
                var changes = _pendingWrites.ToArray();
                _pendingWrites.Clear();
                Volatile.Write(ref _count, 0);
                return changes;
            }
        }
        finally
        {
            ExitFlushGate();
        }
    }

    /// <summary>What happened when a caller tried to take the flush gate.</summary>
    private enum FlushGateEntry
    {
        /// <summary>Taken, and the caller must release it with <see cref="ExitFlushGate"/>.</summary>
        Acquired,

        /// <summary>Not taken because the caller's token fired. Work may still be pending.</summary>
        Cancelled,

        /// <summary>Not taken because the queue is disposed. Nothing is pending and nothing will be.</summary>
        Disposed,
    }

    /// <summary>
    /// Takes the gate that serializes every operation which moves entries out of the queue.
    /// </summary>
    /// <remarks>
    /// Reported rather than thrown: the queue is disposed with its source, so a flush or a reconcile
    /// still in flight races that disposal, and both callers have a "did not run" result to return.
    /// The two failure modes are kept apart because they do not mean the same thing to a caller that
    /// goes on to write.
    /// </remarks>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<FlushGateEntry> TryEnterFlushGateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _flushSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return FlushGateEntry.Acquired;
        }
        catch (OperationCanceledException)
        {
            return FlushGateEntry.Cancelled;
        }
        catch (ObjectDisposedException)
        {
            return FlushGateEntry.Disposed;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Error acquiring the write retry queue's flush gate.");
            return FlushGateEntry.Disposed;
        }
    }

    private void ExitFlushGate()
    {
        try { _flushSemaphore.Release(); } catch { /* might be disposed already */ }
    }

    private int RequeueChanges(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        lock (_lock)
        {
            _pendingWrites.InsertRange(0, changes);

            // The failed in-flight changes are older than anything enqueued while the write was in
            // progress. Ring semantics therefore evict from the front after restoring the batch.
            var droppedCount = _pendingWrites.Count - _maxQueueSize;
            if (droppedCount > 0)
            {
                _pendingWrites.RemoveRange(0, droppedCount);
            }

            Volatile.Write(ref _count, _pendingWrites.Count);
            return droppedCount;
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
