namespace Namotion.Interceptor.Connectors.Diagnostics;

/// <summary>
/// Write side of one buffer's diagnostics. Owned by the connector for its whole lifetime, while the
/// buffer it describes may be created and destroyed many times.
/// </summary>
/// <remarks>
/// All state lives in a single immutable snapshot swapped with <see cref="Interlocked"/>, so a
/// reader sees the accumulated count and the live provider that belongs with it. Splitting them into
/// separate fields cannot be lock-free, monotonic and free of double counting at the same time.
/// </remarks>
public sealed class QueueMetrics
{
    private sealed record Snapshot(long Accumulated, Func<int>? Depth, Func<long>? Dropped, int? Capacity);

    private readonly string _name;

    private Snapshot _snapshot = new(0, null, null, null);

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueMetrics"/> class.
    /// </summary>
    /// <param name="name">
    /// Which buffer this instance describes, for example <c>OutboundChanges</c>, used in the
    /// registration failure message. The instances exposed by <see cref="ConnectorMetrics"/> and
    /// <see cref="SourceMetrics"/> are already named; pass one only when constructing directly.
    /// </param>
    public QueueMetrics(string name = "queue")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _name = name;
    }

    /// <summary>
    /// Points this instance at a newly created buffer. A live registration must be released with
    /// <see cref="Deregister"/> before another can be made. Use
    /// <see cref="BeginRegister"/> for a registration that ends with the buffer it describes.
    /// </summary>
    /// <remarks>
    /// Neither delegate may throw (a throwing delegate is treated as reporting zero) and neither may
    /// take a lock owned by this library, because a diagnostics read can happen while a monitor holds
    /// its own lock.
    /// </remarks>
    /// <param name="depth">Reads the buffer's current item count.</param>
    /// <param name="dropped">
    /// Reads the buffer's own drop counter, or <c>null</c> for a buffer that has none and reports
    /// through <see cref="AddDropped"/> instead. Must be non-decreasing: both <see cref="Reset"/> and
    /// the fold on handover rely on that to keep <see cref="TotalDropped"/> from decreasing.
    /// </param>
    /// <param name="capacity">The buffer's bound, or <c>null</c> if it is unbounded.</param>
    /// <exception cref="InvalidOperationException">
    /// A registration is already live. Call <see cref="Deregister"/> first.
    /// </exception>
    public void Register(Func<int> depth, Func<long>? dropped, int? capacity)
    {
        ArgumentNullException.ThrowIfNull(depth);

        if (Volatile.Read(ref _snapshot).Depth is not null)
        {
            throw new InvalidOperationException(
                $"A registration is already live on the '{_name}' queue metrics. Call Deregister before registering again.");
        }

        Swap((Depth: depth, Dropped: dropped, Capacity: capacity), static (current, state) =>
            new Snapshot(current.Accumulated + SafeInvokeDropped(current.Dropped), state.Depth, state.Dropped, state.Capacity));
    }

    /// <summary>
    /// Registers as <see cref="Register"/> does and returns a handle that releases the registration
    /// when it is disposed. Disposing the handle more than once has no further effect.
    /// </summary>
    /// <remarks>
    /// Declare the handle after the buffer it points at, so that the reverse order of disposal
    /// releases the registration while the buffer can still answer its counters.
    /// </remarks>
    /// <param name="depth">Reads the buffer's current item count.</param>
    /// <param name="dropped">
    /// Reads the buffer's own drop counter, or <c>null</c> for a buffer that has none and reports
    /// through <see cref="AddDropped"/> instead. Must be non-decreasing.
    /// </param>
    /// <param name="capacity">The buffer's bound, or <c>null</c> if it is unbounded.</param>
    /// <returns>The handle that releases this registration.</returns>
    /// <exception cref="InvalidOperationException">
    /// A registration is already live. Nothing is registered and there is no handle to release.
    /// </exception>
    public IDisposable BeginRegister(Func<int> depth, Func<long>? dropped, int? capacity)
    {
        Register(depth, dropped, capacity);
        return new Registration(this);
    }

    /// <summary>
    /// Folds the live drop count into the accumulator and clears the providers.
    /// </summary>
    /// <remarks>
    /// <see cref="BeginRegister"/> is the preferred form, whose handle does this at the end of the
    /// buffer's scope. Call this one for a registration made with <see cref="Register"/>, whose release
    /// is not tied to a scope, such as one that spans the connector's lifetime.
    /// <para>
    /// The buffer must have stopped producing before this runs: any drop that lands between the fold
    /// and the compare-exchange is lost. A concurrent reader can still be holding a provider that has
    /// just been cleared, which is safe only because <see cref="ChangeQueueProcessor"/> keeps its
    /// queue and drop count alive through <see cref="ChangeQueueProcessor.Dispose"/>.
    /// </para>
    /// </remarks>
    public void Deregister()
    {
        Swap<object?>(null, static (current, _) => new Snapshot(
            current.Accumulated + SafeInvokeDropped(current.Dropped),
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

        Swap(count, static (current, addedCount) => current with { Accumulated = current.Accumulated + addedCount });
    }

    internal void Reset() =>
        Swap<object?>(null, static (current, _) => current with { Accumulated = -SafeInvokeDropped(current.Dropped) });

    internal int Depth => SafeInvokeDepth(Volatile.Read(ref _snapshot).Depth);

    internal int? Capacity => Volatile.Read(ref _snapshot).Capacity;

    internal long TotalDropped
    {
        get
        {
            var snapshot = Volatile.Read(ref _snapshot);

            // Reset stores a negative Accumulated that the same provider adds back on the next read,
            // so clamp: a provider that throws reports 0 and would surface the sum as negative.
            return Math.Max(0, snapshot.Accumulated + SafeInvokeDropped(snapshot.Dropped));
        }
    }

    private static int SafeInvokeDepth(Func<int>? depth)
    {
        if (depth is null)
        {
            return 0;
        }

        try
        {
            return depth();
        }
        catch
        {
            return 0;
        }
    }

    private static long SafeInvokeDropped(Func<long>? dropped)
    {
        if (dropped is null)
        {
            return 0;
        }

        try
        {
            return dropped();
        }
        catch
        {
            return 0;
        }
    }

    private sealed class Registration : IDisposable
    {
        private QueueMetrics? _metrics;

        public Registration(QueueMetrics metrics)
        {
            _metrics = metrics;
        }

        // Exchanged rather than flagged, so a second disposal cannot release the registration a
        // later Register has since made.
        public void Dispose() => Interlocked.Exchange(ref _metrics, null)?.Deregister();
    }

    private void Swap<TState>(TState state, Func<Snapshot, TState, Snapshot> update)
    {
        // Reference equality, not Snapshot's record-generated value equality: a Register/Deregister
        // cycle can produce a value-equal instance, so == would read a failed exchange as success.
        SpinWait spin = default;
        while (true)
        {
            var current = Volatile.Read(ref _snapshot);
            var updated = update(current, state);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, updated, current), current))
            {
                return;
            }

            spin.SpinOnce();
        }
    }
}
