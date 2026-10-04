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

    // Reused across overflows; created on the first one, since most queues never overflow.
    private Dictionary<PropertyReference, (SubjectPropertyChange Change, int LastIndex)>? _collapseScratch;

    private readonly ILogger _logger;
    private readonly QueueMetrics _metrics;
    private readonly int _maxQueueSize;
    private int _count;

    // Includes pending, current and in-flight writes. Moving a write between those states does not
    // change the total; only admission, confirmation, capacity eviction, drain or retirement does.
    // A negative value is the retired sentinel.
    private int _ownedWriteCount;

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
    /// Enqueues writes for retry. When the queue exceeds its capacity, writes are first collapsed to one per
    /// property (see <see cref="Collapse"/>) and then the oldest are dropped, so a property written
    /// repeatedly cannot evict other properties' writes and every surviving property keeps its latest
    /// commit. This operation is thread-safe.
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
            if (_ownedWriteCount < 0)
            {
                droppedCount = -1;
            }
            else
            {
                _pendingWrites.AddRange(changes.Span);
                droppedCount = TrimToCapacity();
                _ownedWriteCount += changes.Length - droppedCount;
                Volatile.Write(ref _count, _pendingWrites.Count);
            }
        }

        if (droppedCount < 0)
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
    /// Collapses two changes to one property into one that keeps the old value of the older change and the
    /// new value and origin of the newer one. Which one is newer is decided by
    /// <see cref="SubjectPropertyChange.Revision"/>, not by capture order: changes are enqueued after their
    /// commit and outside the subject lock, so under concurrent writers arrival order is a race order. The
    /// survivor carries the newer revision only when both changes carry one.
    /// </summary>
    internal static SubjectPropertyChange Collapse(SubjectPropertyChange kept, SubjectPropertyChange change) =>
        change.Revision == 0 || kept.Revision == 0
            // One of them orders against nothing, so capture order decides and the survivor carries
            // no revision either. Same rule as the flush-path collapse: keeping a revision here would
            // let the survivor be ranked against the property marker and dropped, on a comparison
            // against a value it was not ordered by.
            ? kept.MergeWithNewer(change).WithoutRevision()
            : change.Revision < kept.Revision
                ? change.MergeWithNewer(kept)
                : kept.MergeWithNewer(change);

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
            if (IsEmpty)
            {
                return true;
            }

            return await FlushCoreAsync(source, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _flushSemaphore.Release();
        }
    }

    /// <summary>
    /// Flushes older pending writes, then attempts the supplied writes while retaining exact ownership
    /// until they are confirmed, parked for retry, or counted as dropped.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask WriteAsync(
        ISubjectSource source,
        ReadOnlyMemory<SubjectPropertyChange> changes,
        CancellationToken cancellationToken)
    {
        var rejected = false;
        lock (_lock)
        {
            if (_ownedWriteCount < 0)
            {
                rejected = true;
            }
            else
            {
                _ownedWriteCount += changes.Length;
            }
        }

        if (rejected)
        {
            _metrics.AddDropped(changes.Length);
            return;
        }

        if (!await TryEnterFlushAsync(cancellationToken).ConfigureAwait(false))
        {
            _metrics.AddDropped(SettleWrite(changes.Span, changes.Length, append: true).Dropped);
            return;
        }

        try
        {
            if (!await FlushCoreAsync(source, cancellationToken).ConfigureAwait(false))
            {
                _metrics.AddDropped(SettleWrite(changes.Span, changes.Length, append: true).Dropped);
                return;
            }

            lock (_lock)
            {
                if (_ownedWriteCount < 0)
                {
                    return;
                }
            }

            var result = await source.WriteChangesInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
            var settlement = SettleWrite(result.FailedChanges.AsSpan(), changes.Length, append: false);
            _metrics.AddDropped(settlement.Dropped);
            if (result.Error is not null && !settlement.Retired)
            {
                _logger.LogWarning(result.Error,
                    "Failed to write {Count} changes to source; {PendingCount} writes are queued for retry and {DroppedCount} were dropped.",
                    result.FailedChanges.Length,
                    settlement.PendingCount,
                    settlement.Dropped);
            }
        }
        finally
        {
            _flushSemaphore.Release();
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> TryEnterFlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _flushSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Error acquiring flush semaphore");
            }

            return false;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> FlushCoreAsync(ISubjectSource source, CancellationToken cancellationToken)
    {
        var totalFlushed = 0;
        while (true)
        {
            int count;
            lock (_lock)
            {
                if (_pendingWrites.Count == 0)
                {
                    break;
                }

                if (_scratchBuffer.Length < MaxBatchSize)
                {
                    var newSize = Math.Min(_scratchBuffer.Length * 2, MaxBatchSize);
                    _scratchBuffer = new SubjectPropertyChange[newSize];
                }

                count = Math.Min(_scratchBuffer.Length, _pendingWrites.Count);
                _pendingWrites.CopyTo(0, _scratchBuffer, 0, count);
                _pendingWrites.RemoveRange(0, count);
                Volatile.Write(ref _count, _pendingWrites.Count);
            }

            var memory = new ReadOnlyMemory<SubjectPropertyChange>(_scratchBuffer, 0, count);
            var result = await source.WriteChangesInBatchesAsync(memory, cancellationToken).ConfigureAwait(false);
            Array.Clear(_scratchBuffer, 0, count);
            if (result.Error is not null)
            {
                // FailedChanges is complete (see WriteChangesInBatchesAsync), so every failed
                // item is restored before ring capacity is applied to the combined queue.
                var settlement = SettleWrite(result.FailedChanges.AsSpan(), count, append: false);
                if (settlement.Retired)
                {
                    return false;
                }

                var now = Environment.TickCount64;
                if (now - _lastFlushWarningTimestamp >= 5000)
                {
                    _lastFlushWarningTimestamp = now;
                    _logger.LogWarning(result.Error,
                        "Failed to flush queued writes to source, re-queuing failed items ({QueueSize} writes queued).",
                        settlement.PendingCount);
                }

                _hasFlushWarnings = true;
                _metrics.AddDropped(settlement.Dropped);
                return false;
            }

            if (SettleWrite([], count, append: false).Retired)
            {
                return true;
            }

            totalFlushed += count;
        }

        if (_hasFlushWarnings)
        {
            _logger.LogWarning("Successfully flushed {Count} queued writes after retry.", totalFlushed);
            _hasFlushWarnings = false;
            _lastFlushWarningTimestamp = 0;
        }

        return true;
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
            _ownedWriteCount -= changes.Length;
            Volatile.Write(ref _count, 0);
            return changes;
        }
    }

    /// <summary>
    /// Retires the queue, rejects future writes, and counts every write still owned by the queue as dropped.
    /// Calling this method more than once has no additional effect.
    /// </summary>
    public void Retire()
    {
        int stranded;
        lock (_lock)
        {
            if (_ownedWriteCount < 0)
            {
                return;
            }

            stranded = _ownedWriteCount;
            _pendingWrites.Clear();
            _ownedWriteCount = -1;
            Volatile.Write(ref _count, 0);
        }

        _metrics.AddDropped(stranded);
    }

    private (bool Retired, int Dropped, int PendingCount) SettleWrite(
        ReadOnlySpan<SubjectPropertyChange> pendingChanges,
        int attemptedCount,
        bool append)
    {
        lock (_lock)
        {
            if (_ownedWriteCount < 0)
            {
                return (true, 0, 0);
            }

            _pendingWrites.InsertRange(append ? _pendingWrites.Count : 0, pendingChanges);

            var droppedCount = TrimToCapacity();
            var pendingWriteCount = _pendingWrites.Count;
            _ownedWriteCount -= attemptedCount - pendingChanges.Length + droppedCount;
            Volatile.Write(ref _count, pendingWriteCount);
            return (false, droppedCount, pendingWriteCount);
        }
    }

    // Under _lock. Collapses only once the queue has overflowed, so a queue within its capacity costs
    // nothing beyond the append, and the capacity bounds the collapse.
    private int TrimToCapacity()
    {
        if (_pendingWrites.Count <= _maxQueueSize)
        {
            return 0;
        }

        CollapsePendingPerProperty();

        var droppedCount = Math.Max(0, _pendingWrites.Count - _maxQueueSize);
        if (droppedCount > 0)
        {
            _pendingWrites.RemoveRange(0, droppedCount);
        }

        return droppedCount;
    }

    // Each property's survivor takes the slot of its latest change, so the eviction that follows drops
    // the properties written longest ago rather than the one written most, and every survivor carries
    // its property's latest commit.
    private void CollapsePendingPerProperty()
    {
        var pending = _pendingWrites;
        var survivors = _collapseScratch ??= new Dictionary<PropertyReference, (SubjectPropertyChange Change, int LastIndex)>(PropertyReference.Comparer);
        for (var index = 0; index < pending.Count; index++)
        {
            var change = pending[index];
            survivors[change.Property] = survivors.TryGetValue(change.Property, out var survivor)
                ? (Collapse(survivor.Change, change), index)
                : (change, index);
        }

        var collapsedCount = pending.Count - survivors.Count;
        if (collapsedCount > 0)
        {
            var write = 0;
            for (var index = 0; index < pending.Count; index++)
            {
                var survivor = survivors[pending[index].Property];
                if (survivor.LastIndex == index)
                {
                    pending[write++] = survivor.Change;
                }
            }

            pending.RemoveRange(write, pending.Count - write);

            // An absorbed write leaves the owned count too: its value travels on in the survivor, so it
            // is neither pending nor dropped.
            _ownedWriteCount -= collapsedCount;
        }

        survivors.Clear();
    }

    /// <summary>
    /// Disposes the queue by retiring it and accounting for every unconfirmed write.
    /// </summary>
    public void Dispose() => Retire();
}
