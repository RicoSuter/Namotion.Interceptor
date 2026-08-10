using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    // Reused across flushes: a refused write fails every tick, so a per-flush dictionary would allocate
    // for as long as the outage lasts. Only touched by the dequeue below, under _lock.
    private readonly Dictionary<PropertyReference, int> _collapseIndices = new(PropertyReference.Comparer);

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
                    var dequeuedCount = Math.Min(_scratchBuffer.Length, _pendingWrites.Count);
                    if (dequeuedCount == 0)
                    {
                        break;
                    }

                    for (var i = 0; i < dequeuedCount; i++)
                    {
                        _scratchBuffer[i] = _pendingWrites[i];
                    }
                    _pendingWrites.RemoveRange(0, dequeuedCount);

                    // The collapse can push a tail back onto the queue, so the count is published after it.
                    count = CollapsePerProperty(dequeuedCount);
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

    /// <summary>
    /// Compacts the dequeued batch in <see cref="_scratchBuffer"/> to one change per property, in place,
    /// and returns the compacted length. Each survivor stays at its first occurrence's position. A batch
    /// never carries a property twice: where two changes for one property cannot be merged, the batch is
    /// cut before the second one and everything from there is pushed back to the head of the queue, so
    /// the flush loop ships the pair in separate rounds.
    /// </summary>
    /// <remarks>
    /// Collapsing at dequeue rather than on the requeued span is what bounds the queue: a failed flush
    /// requeues its batch and the caller then appends the same tick's own changes, so only the dequeue
    /// sees both producers. Without it a property the source keeps refusing costs entries per flush tick
    /// rather than a fixed number.
    /// <para>
    /// Two changes merge only when both carry a revision. A change built outside a terminal write carries
    /// none, orders against nothing, and passes the delivery filter's supersession check unconditionally,
    /// so merging one away could let a later reconcile restore an older parked write over a newer local
    /// one. Nothing in this queue may lose its revision, which is also why the survivor is assembled by
    /// revision rather than by position, and why an unmergeable pair is separated in time instead.
    /// </para>
    /// </remarks>
    private int CollapsePerProperty(int count)
    {
        if (count < 2)
        {
            return count;
        }

        _collapseIndices.Clear();

        var kept = 0;
        for (var i = 0; i < count; i++)
        {
            // By reference: a by-value copy of this struct is a large block move that the JIT emits a
            // bulk write barrier for, because the struct carries object fields.
            ref readonly var change = ref _scratchBuffer[i];

            // Single lookup per change: the ref is only read and written before the next add.
            ref var survivorIndex = ref CollectionsMarshal.GetValueRefOrAddDefault(_collapseIndices, change.Property, out var propertyAlreadySeen);
            if (propertyAlreadySeen)
            {
                ref var survivor = ref _scratchBuffer[survivorIndex];
                if (survivor.Revision == 0 || change.Revision == 0)
                {
                    // Unmergeable, and one write must never carry a property twice: split across two of
                    // the source's batches, a failure of the batch holding the older change requeues it
                    // alone and settles the source on it for good. The rest goes back to the queue, whose
                    // lock the caller already holds, and the flush loop ships it in the next round. That
                    // round still ships something: seeing this property again means an earlier change was
                    // kept, so kept is at least 1 here.
                    _pendingWrites.InsertRange(0, _scratchBuffer.AsSpan(i, count - i));
                    break;
                }

                // Queue order is chronological, but changes are enqueued after their commit and
                // outside the subject lock, so the revision decides which new value is the current
                // state. Both changes are writes to one property and therefore comparable.
                survivor = change.Revision > survivor.Revision
                    ? survivor.MergeWithNewer(change)
                    : change.MergeWithNewer(survivor);
                continue;
            }

            survivorIndex = kept;
            if (kept != i)
            {
                // Guarded because nothing collapsing is the common case, and a self-assignment here is
                // still the full block move with its write barriers.
                _scratchBuffer[kept] = change;
            }

            kept++;
        }

        // Cleared on the way out as well: every key holds its subject alive, and a source that goes quiet
        // after a wide collapse would otherwise pin them until the next batch of two or more.
        _collapseIndices.Clear();

        if (kept < count)
        {
            // The slots past the compacted prefix still reference the merged and pushed-back changes'
            // subjects and boxed values, and the flush only clears the prefix it hands to the source.
            Array.Clear(_scratchBuffer, kept, count - kept);
        }

        return kept;
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
