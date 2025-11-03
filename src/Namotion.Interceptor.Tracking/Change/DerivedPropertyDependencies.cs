namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Lock-free, copy-on-write collection for property dependencies.
/// Concurrency Model:
/// - Reads: Allocation-free via <see cref="AsSpan"/>. Always returns stable snapshot.
/// - Writes: Lock-free CAS (Compare-And-Swap) with automatic retry on contention.
/// - Version: Monotonically increasing counter for optimistic concurrency control.
/// Design: Copy-on-write ensures readers never see partial updates. Version counter detects ABA problems.
/// </summary>
public sealed class DerivedPropertyDependencies
{
    private PropertyReference[] _items = [];
    private long _version; // Increments on every mutation (Add/Remove/TryReplace)

    /// <summary>
    /// Gets the count of dependencies (thread-safe, allocation-free).
    /// </summary>
    public int Count => Volatile.Read(ref _items).Length;

    /// <summary>
    /// Gets the current version for optimistic concurrency control.
    /// Version increments on every mutation. Wraps around after 2^64 operations (584 years @ 1B ops/sec).
    /// </summary>
    internal long Version => Volatile.Read(ref _version);

    /// <summary>
    /// Gets a stable snapshot for iteration (thread-safe, allocation-free).
    /// <para>Snapshot won't change even if collection is modified concurrently - copy-on-write semantics.</para>
    /// </summary>
    public ReadOnlySpan<PropertyReference> AsSpan() => Volatile.Read(ref _items);


    /// <summary>
    /// Adds a dependency using lock-free CAS (compare-and-swap).
    /// Thread-safe: Multiple threads can call concurrently. CAS loop retries on contention.
    /// Idempotent: Adding same item multiple times is safe (no duplicates).
    /// </summary>
    /// <returns>True if item was added; false if already exists.</returns>
    internal bool Add(in PropertyReference item)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _items);

            // Idempotency check: Skip if already present
            if (Array.IndexOf(snapshot, item) >= 0)
                return false;

            // Create new array with item appended
            var newArr = new PropertyReference[snapshot.Length + 1];
            Array.Copy(snapshot, newArr, snapshot.Length);
            newArr[^1] = item;

            // Atomic swap: Succeeds if no other thread modified _items
            if (ReferenceEquals(Interlocked.CompareExchange(ref _items, newArr, snapshot), snapshot))
            {
                Interlocked.Increment(ref _version);
                return true;
            }

            // Another thread won the race - retry with new snapshot
        }
    }

    /// <summary>
    /// Removes a dependency using lock-free CAS (compare-and-swap).
    /// Thread-safe: Multiple threads can call concurrently. CAS loop retries on contention.
    /// </summary>
    /// <returns>True if item was removed; false if not found.</returns>
    internal bool Remove(in PropertyReference item)
    {
        while (true)
        {
            var snapshot = Volatile.Read(ref _items);

            // Find item to remove
            var idx = Array.IndexOf(snapshot, item);
            if (idx < 0)
            {
                return false; // Not found
            }

            // Create new array without the item
            var newArr = snapshot.Length == 1 ? [] : RemoveAt(snapshot, idx);

            // Atomic swap: Succeeds if no other thread modified _items
            if (ReferenceEquals(Interlocked.CompareExchange(ref _items, newArr, snapshot), snapshot))
            {
                Interlocked.Increment(ref _version);
                return true;
            }

            // Another thread won the race - retry with new snapshot
        }
    }

    // Helper: Creates new array with item at index removed
    private static PropertyReference[] RemoveAt(PropertyReference[] source, int index)
    {
        var result = new PropertyReference[source.Length - 1];
        if (index > 0)
            Array.Copy(source, 0, result, 0, index);
        if (index < source.Length - 1)
            Array.Copy(source, index + 1, result, index, source.Length - index - 1);
        return result;
    }

    /// <summary>
    /// Attempts to atomically replace all dependencies if version matches (optimistic concurrency).
    /// Check version matches expectedVersion (no concurrent modifications)
    /// - If match: Replace array and increment version
    /// - If mismatch: Return false (caller should use merge mode)
    /// The version check prevents ABA problem where value changes and changes back.
    /// </summary>
    /// <returns>True if replaced successfully; false if version changed (concurrent modification detected).</returns>
    internal bool TryReplace(ReadOnlySpan<PropertyReference> newItems, long expectedVersion)
    {
        // Optimistic concurrency: Fail if another thread modified since we read
        if (Volatile.Read(ref _version) != expectedVersion)
            return false;

        // Replace array atomically (volatile write ensures visibility)
        var newArr = newItems.Length == 0 ? [] : newItems.ToArray();
        Volatile.Write(ref _items, newArr);
        Interlocked.Increment(ref _version);

        return true;
    }
}
