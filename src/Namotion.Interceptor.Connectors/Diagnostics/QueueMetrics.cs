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
    private sealed record Snapshot(long Accumulated, Registration? Active, int? Capacity);

    private readonly string _name;

    private Snapshot _snapshot = new(0, null, null);

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
    /// Points this instance at a newly created buffer and returns the handle that owns the
    /// registration. Only one registration may be live at a time. Disposing the handle releases only
    /// that registration, and disposing it more than once has no further effect.
    /// </summary>
    /// <remarks>
    /// Neither delegate may throw (a throwing delegate is treated as reporting zero) and neither may
    /// take a lock owned by this library, because a diagnostics read can happen while a monitor holds
    /// its own lock.
    /// Lifetime-long providers may intentionally leave the returned handle undisposed when their
    /// lifetime matches this instance.
    /// </remarks>
    /// <param name="depth">Reads the buffer's current item count.</param>
    /// <param name="dropped">
    /// Reads the buffer's own drop counter, or <c>null</c> for a buffer that has none and reports
    /// through <see cref="AddDropped"/> instead. Must be non-decreasing: both <see cref="Reset"/> and
    /// the fold on handover rely on that to keep <see cref="TotalDropped"/> from decreasing.
    /// </param>
    /// <param name="capacity">The buffer's bound, or <c>null</c> if it is unbounded.</param>
    /// <exception cref="InvalidOperationException">
    /// A registration is already live. Dispose its registration handle first.
    /// </exception>
    public IDisposable Register(Func<int> depth, Func<long>? dropped, int? capacity)
    {
        ArgumentNullException.ThrowIfNull(depth);

        var registration = new Registration(this, depth, dropped, capacity);
        SpinWait spin = default;
        while (true)
        {
            var current = Volatile.Read(ref _snapshot);
            if (current.Active is not null)
            {
                throw new InvalidOperationException(
                    $"A registration is already live on the '{_name}' queue metrics. Dispose its registration handle before registering again.");
            }

            var updated = new Snapshot(current.Accumulated, registration, registration.Capacity);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, updated, current), current))
            {
                return registration;
            }

            spin.SpinOnce();
        }
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
        Swap<object?>(null, static (current, _) => current with { Accumulated = -SafeInvokeDropped(current.Active?.Dropped) });

    internal int Depth => SafeInvokeDepth(Volatile.Read(ref _snapshot).Active?.Depth);

    internal int? Capacity => Volatile.Read(ref _snapshot).Capacity;

    internal long TotalDropped
    {
        get
        {
            var snapshot = Volatile.Read(ref _snapshot);

            // Reset stores a negative Accumulated that the same provider adds back on the next read,
            // so clamp: a provider that throws reports 0 and would surface the sum as negative.
            return Math.Max(0, snapshot.Accumulated + SafeInvokeDropped(snapshot.Active?.Dropped));
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
        private QueueMetrics? _owner;

        internal Registration(QueueMetrics owner, Func<int> depth, Func<long>? dropped, int? capacity)
        {
            _owner = owner;
            Depth = depth;
            Dropped = dropped;
            Capacity = capacity;
        }

        internal Func<int> Depth { get; }

        internal Func<long>? Dropped { get; }

        internal int? Capacity { get; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(this);
    }

    private void Release(Registration registration)
    {
        SpinWait spin = default;
        while (true)
        {
            var current = Volatile.Read(ref _snapshot);
            if (!ReferenceEquals(current.Active, registration))
            {
                return;
            }

            var updated = new Snapshot(
                current.Accumulated + SafeInvokeDropped(registration.Dropped),
                Active: null,
                current.Capacity);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, updated, current), current))
            {
                return;
            }

            spin.SpinOnce();
        }
    }

    private void Swap<TState>(TState state, Func<Snapshot, TState, Snapshot> update)
    {
        // Reference equality, not Snapshot's record-generated value equality: a registration/release
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
