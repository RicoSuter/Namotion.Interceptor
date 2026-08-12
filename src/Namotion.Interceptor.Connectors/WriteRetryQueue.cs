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

    // Reused across flushes so a persistent outage does not allocate one per tick. Only used under _lock.
    private readonly Dictionary<PropertyReference, int> _collapseIndices = new(PropertyReference.Comparer);

    // Writes the source refused for the lifetime of its current connection. Held out of _pendingWrites
    // rather than skipped inside it, so an idle tick's flush still short-circuits on IsEmpty and
    // PendingWriteCount still falls back to zero. One entry per property, so a property written
    // repeatedly while refused costs one slot rather than one per write. Only used under _lock.
    //
    // Deliberately not trimmed to _maxQueueSize: dropping a held write loses one a reconnect would have
    // delivered, since the refusal is only permanent for this connection. One entry per property is what
    // bounds it instead, by the model's property count rather than by the write rate. Released writes
    // rejoin _pendingWrites and are subject to its bound again from there.
    private readonly Dictionary<PropertyReference, SubjectPropertyChange> _refusedWrites = new(PropertyReference.Comparer);
    private int _refusedCount;

    // Bumped whenever the connection the refusals are scoped to is replaced. A write reads it before it
    // is issued and hands it back with the answer, because releasing only what is held at the moment of
    // the bump cannot reach a write still in flight: its answer arrives afterwards and would be held
    // against a connection that no longer exists, for the whole life of the one that replaced it.
    private int _connectionGeneration;

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

    /// <summary>
    /// Gets the number of writes held back because the source refuses them until it reconnects.
    /// </summary>
    public int RefusedWriteCount => Volatile.Read(ref _refusedCount);

    /// <summary>
    /// Gets the generation identifying the connection writes are currently being issued over. Read it
    /// before issuing a write and hand it back to <see cref="EnqueueFailures"/> with that write's answer.
    /// </summary>
    public int ConnectionGeneration => Volatile.Read(ref _connectionGeneration);

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
            var span = changes.Span;

            // Add all new items
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
    /// Queues a failed write's changes for retry, except those the source named as refused until it
    /// reconnects: those are held back instead, since re-sending them on this connection cannot succeed
    /// and starves everything behind them. Returns true when nothing was left to retry.
    /// </summary>
    /// <remarks>
    /// <see cref="WriteResult.RefusedUntilReconnect"/> is a subset of
    /// <see cref="WriteResult.FailedChanges"/>, which is complete, so every change lands in one of the
    /// two. A change failing without being named refused is queued even if the same property is held
    /// back from an earlier answer: the source has just answered differently about it.
    /// <para>
    /// <paramref name="connectionGeneration"/> is the value <see cref="ConnectionGeneration"/> held when
    /// the write was issued. A refusal is only permanent for the connection that gave it, so one answered
    /// over a connection since replaced is retried rather than held.
    /// </para>
    /// </remarks>
    public bool EnqueueFailures(in WriteResult result, int connectionGeneration)
    {
        var failedChanges = result.FailedChanges;
        if (failedChanges.IsDefaultOrEmpty)
        {
            return true;
        }

        if (_maxQueueSize is 0)
        {
            // Buffering off: report and drop, which holding writes back instead would silently undo.
            _metrics.AddDropped(failedChanges.Length);
            _logger.LogWarning("Write buffering is disabled. Dropping {Count} writes.", failedChanges.Length);
            return true;
        }

        var refusedChanges = result.RefusedUntilReconnect;
        if (refusedChanges.IsDefaultOrEmpty)
        {
            RequeueChanges(failedChanges.AsSpan());
            return false;
        }

        var nothingLeftToRetry = false;
        int droppedCount;
        lock (_lock)
        {
            if (connectionGeneration != _connectionGeneration)
            {
                // Answered over a connection since replaced. The refusals were scoped to that connection,
                // and the release that came with the replacement ran before this answer existed, so
                // holding them now would strand these writes until the connection is replaced again.
                droppedCount = RequeueLocked(failedChanges.AsSpan());
            }
            else
            {
                // This answer's refused properties, not the held-back set: a property held back from an
                // earlier answer that fails for another reason now has to be retried like any other failure.
                var refusedProperties = new HashSet<PropertyReference>(refusedChanges.Length, PropertyReference.Comparer);
                List<SubjectPropertyChange>? retryableChanges = null;

                foreach (var change in refusedChanges)
                {
                    refusedProperties.Add(change.Property);
                    HoldRefusedWrite(change);
                }

                Volatile.Write(ref _refusedCount, _refusedWrites.Count);

                foreach (var change in failedChanges)
                {
                    if (!refusedProperties.Contains(change.Property))
                    {
                        (retryableChanges ??= new List<SubjectPropertyChange>(failedChanges.Length)).Add(change);
                    }
                }

                nothingLeftToRetry = retryableChanges is null;
                droppedCount = nothingLeftToRetry
                    ? 0
                    : RequeueLocked(CollectionsMarshal.AsSpan(retryableChanges));
            }
        }

        _metrics.AddDropped(droppedCount);
        return nothingLeftToRetry;
    }

    /// <summary>
    /// Puts every held-back write back in line for retry. Call it whenever the connection the source
    /// talks over is replaced: the refusals were scoped to the previous one, and the new one can hold
    /// different permissions, a different address space and different access levels.
    /// </summary>
    public void RetryRefusedWrites()
    {
        int droppedCount;
        lock (_lock)
        {
            // Before the empty check, not after: a write issued over the replaced connection and still in
            // flight is exactly the case where nothing is held yet, and it is the one this has to reach.
            _connectionGeneration++;

            if (_refusedWrites.Count == 0)
            {
                return;
            }

            ReleaseRefusedWrites();

            // Released refusals go to the head, so a queue already at capacity drops them first. That is
            // the ring buffer's own rule rather than an exception to it: they are the oldest writes here,
            // and holding them past the bound would be the one path that grows the queue without limit.
            droppedCount = _pendingWrites.Count - _maxQueueSize;
            if (droppedCount > 0)
            {
                _pendingWrites.RemoveRange(0, droppedCount);
            }

            Volatile.Write(ref _count, _pendingWrites.Count);
        }

        _metrics.AddDropped(droppedCount);
    }

    /// <summary>
    /// Records a change the source refuses until it reconnects, merging it with one already held for
    /// the same property. Caller holds <see cref="_lock"/>.
    /// </summary>
    private void HoldRefusedWrite(SubjectPropertyChange change)
    {
        ref var held = ref CollectionsMarshal.GetValueRefOrAddDefault(_refusedWrites, change.Property, out var propertyAlreadyHeld);
        if (!propertyAlreadyHeld)
        {
            held = change;
            return;
        }

        // Same rule as the collapses either side of this queue: the revision decides which new value is
        // the current state, and a change carrying none orders against nothing, so the merged survivor
        // carries none either rather than being ranked against a value it was not ordered by.
        held = held.Revision == 0 || change.Revision == 0
            ? held.MergeWithNewer(change).WithoutRevision()
            : held.MergeByRevision(change);
    }

    /// <summary>
    /// Moves every held-back write to the head of the pending queue. Caller holds <see cref="_lock"/>
    /// and publishes <see cref="_count"/> afterwards.
    /// </summary>
    private void ReleaseRefusedWrites()
    {
        if (_refusedWrites.Count == 0)
        {
            return;
        }

        // At the head: these are older than anything queued since. Where that matters, the dequeue
        // collapse ranks a pair for one property by revision rather than by position anyway.
        _pendingWrites.InsertRange(0, _refusedWrites.Values);
        _refusedWrites.Clear();
        Volatile.Write(ref _refusedCount, 0);
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

                    count = CollapsePerProperty(dequeuedCount);
                    Volatile.Write(ref _count, _pendingWrites.Count);
                }

                var memory = new ReadOnlyMemory<SubjectPropertyChange>(_scratchBuffer, 0, count);

                // Read before the write is issued, since the connection can be replaced while it is in
                // flight. Reading it late would be the unsafe direction: it would hold a refusal the
                // replacement had already released against the connection that replaced it.
                var connectionGeneration = ConnectionGeneration;
                var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken).ConfigureAwait(false);
                if (result.Error is not null)
                {
                    // FailedChanges is complete (see WriteChangesInBatchesAsync), so every dequeued item
                    // is accounted for rather than lost track of: what the source refuses for this
                    // connection is held back and the rest is queued again. Queueing again is still
                    // subject to the bound, so a batch that was dequeued while the pump kept appending
                    // can lose its oldest to the ring buffer, the same way a direct enqueue would.
                    var nothingLeftToRetry = EnqueueFailures(in result, connectionGeneration);
                    Array.Clear(_scratchBuffer, 0, count);

                    if (!nothingLeftToRetry)
                    {
                        var now = Environment.TickCount64;
                        if (now - _lastFlushWarningTimestamp >= 5000)
                        {
                            var queueSize = PendingWriteCount;
                            _lastFlushWarningTimestamp = now;
                            _logger.LogWarning(result.Error,
                                "Failed to flush queued writes to source, re-queuing failed items ({QueueSize} writes queued).",
                                queueSize);
                        }

                        _hasFlushWarnings = true;
                        return false;
                    }

                    // Only refusals failed, and they are held back rather than queued. Reporting failure
                    // here would have the caller divert the tick's own changes into the queue
                    // unattempted, so every later write would reach the source one flush window late.
                    totalFlushed += count - result.FailedChanges.Length;
                    continue;
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
            // Held-back writes are still local intent, and the reconcile is what decides their fate on
            // the new connection. Left here they would be stranded for the lifetime of the source.
            ReleaseRefusedWrites();

            var changes = _pendingWrites.ToArray();
            _pendingWrites.Clear();
            Volatile.Write(ref _count, 0);
            return changes;
        }
    }

    /// <summary>
    /// Compacts the dequeued batch in <see cref="_scratchBuffer"/> to one change per property, in place,
    /// and returns the compacted length. Each survivor stays at its first occurrence's position.
    /// </summary>
    /// <remarks>
    /// Collapsing at dequeue rather than on the requeued span is what bounds the queue: a failed flush
    /// requeues its batch and the caller then appends the same tick's changes, so only the dequeue sees both.
    /// <para>
    /// Two changes for one property always merge, ranked by revision. A change carrying no revision cannot
    /// reach this queue: every change published to a source comes from a write terminal, which stamps one.
    /// The collapses that manufacture a revisionless survivor do so only from a revisionless input, so none
    /// can produce one here either. A revisionless change arriving anyway means a write interceptor or a
    /// source is not honouring its contract, an invariant the publish sites assert rather than this
    /// queue defending against.
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
            // By reference: this struct is more than a dozen words wide, and every change is read here.
            ref readonly var change = ref _scratchBuffer[i];

            // Single lookup per change: the ref is only read and written before the next add.
            ref var survivorIndex = ref CollectionsMarshal.GetValueRefOrAddDefault(_collapseIndices, change.Property, out var propertyAlreadySeen);
            if (propertyAlreadySeen)
            {
                ref var survivor = ref _scratchBuffer[survivorIndex];

                // Queue order is chronological, but changes are enqueued after their commit and outside
                // the subject lock, so the revision decides which new value is the current state.
                survivor = survivor.MergeByRevision(change);
                continue;
            }

            survivorIndex = kept;
            if (kept != i)
            {
                // Guarded because nothing collapsing is the common case, and a self-assignment still copies.
                _scratchBuffer[kept] = change;
            }

            kept++;
        }

        // Cleared on the way out too: every key holds its subject alive until the next collapse otherwise.
        _collapseIndices.Clear();

        if (kept < count)
        {
            // The slots past the compacted prefix still reference the merged-away changes'
            // subjects and boxed values, and the flush only clears the prefix it hands to the source.
            Array.Clear(_scratchBuffer, kept, count - kept);
        }

        return kept;
    }

    private void RequeueChanges(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        int droppedCount;
        lock (_lock)
        {
            droppedCount = RequeueLocked(changes);
        }

        _metrics.AddDropped(droppedCount);
    }

    /// <summary>
    /// Puts changes back at the head of the queue, applies the ring bound and returns how many writes
    /// that dropped. Caller holds <see cref="_lock"/> and reports the count to the metrics outside it.
    /// </summary>
    private int RequeueLocked(ReadOnlySpan<SubjectPropertyChange> changes)
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

    /// <summary>
    /// Disposes the write retry queue and releases resources.
    /// </summary>
    public void Dispose()
    {
        _flushSemaphore.Dispose();
    }
}
