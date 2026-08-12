namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// Write side of one buffer's diagnostics. Owned by the connector for its whole lifetime, while the
/// buffer it describes may be created and destroyed many times.
/// </summary>
/// <remarks>
/// All state lives in a single immutable snapshot swapped with <see cref="Interlocked"/>, so a
/// reader sees the accumulated count and the live provider that belongs with it. Holding them in
/// separate fields cannot be lock-free, monotonic and free of double counting at the same time:
/// reading the accumulator before the provider lets the total decrease across a handover, and
/// reading them the other way round counts the same drops twice.
/// </remarks>
public sealed class QueueMetrics
{
    private sealed record Snapshot(long Accumulated, Func<int>? Depth, Func<long>? Dropped, int? Capacity);

    private Snapshot _snapshot = new(0, null, null, null);

    /// <summary>
    /// Points this instance at a newly created buffer.
    /// </summary>
    /// <param name="depth">Reads the buffer's current item count.</param>
    /// <param name="dropped">
    /// Reads the buffer's own drop counter, or <c>null</c> for a buffer that has none and reports
    /// through <see cref="AddDropped"/> instead. Passing <c>() =&gt; 0</c> instead of <c>null</c>
    /// would work but invites a later implementer to add a counter that then double counts.
    /// </param>
    /// <param name="capacity">The buffer's bound, or <c>null</c> if it is unbounded.</param>
    public void Register(Func<int> depth, Func<long>? dropped, int? capacity)
    {
        ArgumentNullException.ThrowIfNull(depth);

        Swap(current => current with { Depth = depth, Dropped = dropped, Capacity = capacity });
    }

    /// <summary>
    /// Folds the live drop count into the accumulator and clears the providers. Must run before the
    /// buffer is disposed.
    /// </summary>
    /// <remarks>
    /// Clearing the providers first narrows the race with a concurrent reader rather than closing
    /// it: a reader can hold a non-null provider and be preempted. That is safe only because
    /// <see cref="ChangeQueueProcessor"/> keeps its queue and drop count alive through
    /// <see cref="ChangeQueueProcessor.Dispose"/>.
    /// </remarks>
    public void Deregister()
    {
        Swap(current => new Snapshot(
            current.Accumulated + (current.Dropped?.Invoke() ?? 0),
            Depth: null,
            Dropped: null,
            current.Capacity));
    }

    /// <summary>
    /// Records drops for a buffer that has no counter of its own.
    /// </summary>
    public void AddDropped(long count)
    {
        if (count <= 0)
        {
            return;
        }

        Swap(current => current with { Accumulated = current.Accumulated + count });
    }

    internal void Reset() => Swap(current => current with { Accumulated = -(current.Dropped?.Invoke() ?? 0) });

    internal int Depth => _snapshot.Depth?.Invoke() ?? 0;

    internal int? Capacity => _snapshot.Capacity;

    internal long TotalDropped
    {
        get
        {
            var snapshot = _snapshot;
            return snapshot.Accumulated + (snapshot.Dropped?.Invoke() ?? 0);
        }
    }

    private void Swap(Func<Snapshot, Snapshot> update)
    {
        // Compare-exchange rather than a blind exchange: every caller here is a read-modify-write and
        // drops arrive off the pump thread, so an exchange would lose increments.
        //
        // The success check below must use reference equality, not Snapshot's record-generated value
        // equality: Interlocked.CompareExchange itself always compares by reference at the hardware
        // level, but a concurrent Register/Deregister cycle can produce a new Snapshot instance that
        // is value-equal to the one this loop already read. A value-equality check on the returned
        // "previous" snapshot would then read a genuine CAS failure as success and silently drop this
        // update.
        SpinWait spin = default;
        while (true)
        {
            var current = Volatile.Read(ref _snapshot);
            var updated = update(current);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, updated, current), current))
            {
                return;
            }

            spin.SpinOnce();
        }
    }
}
